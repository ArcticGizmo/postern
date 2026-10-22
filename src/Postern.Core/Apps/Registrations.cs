using System.Collections.Concurrent;
using Postern.Core.Scopes;

namespace Postern.Core.Apps;

public sealed class RegistrationException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Handed to the registering app: poll with these to collect the token once the user approves.</summary>
public sealed record RegistrationTicket(string Id, string Claim);

public enum ClaimStatus { Issued, Pending, Denied, AlreadyClaimed, NotFound, CallerMismatch }

public sealed record ClaimResult(ClaimStatus Status, string? Token, string Detail);

/// <summary>
/// The registration lifecycle: an app asks (Begin), the user decides (via <see cref="IApprovalPrompt"/>, or the
/// timeout denies), and the app claims its token once (Claim). Only the process that registered can claim, and the
/// token is generated at claim time and never stored, only its hash.
/// <para>
/// Several registrations can wait at once (a full stack starting up), and the prompt shows them in arrival order.
/// The timeout is long enough to work through that queue. An exe can only have <see cref="MaxPendingPerExe"/>
/// waiting, so one misbehaving caller can't bury the user in prompts.
/// </para>
/// </summary>
public sealed class Registrations
{
    public static readonly TimeSpan DefaultApprovalTimeout = TimeSpan.FromMinutes(5);
    public const int MaxPendingPerExe = 10;
    const int MaxScopes = 64;

    readonly IRegistrationStore _store;
    readonly IApprovalPrompt _prompt;
    readonly TimeProvider _time;
    readonly TimeSpan _approvalTimeout;
    readonly ConcurrentDictionary<string, TaskCompletionSource> _decisions = new();
    readonly ConcurrentDictionary<string, CancellationTokenSource> _asking = new();
    readonly Lock _gate = new();

    public Registrations(IRegistrationStore store, IApprovalPrompt prompt, TimeProvider? time = null, TimeSpan? approvalTimeout = null)
    {
        _store = store;
        _prompt = prompt;
        _time = time ?? TimeProvider.System;
        _approvalTimeout = approvalTimeout ?? DefaultApprovalTimeout;
    }

    /// <summary>Raised after any status change (for the Apps page and the tray badge).</summary>
    public event Action? Changed;

    public IReadOnlyList<AppRegistration> All() => _store.All();

    public RegistrationTicket Begin(string name, IReadOnlyList<string> scopeTexts, CallerInfo caller)
    {
        name = name.Trim();
        if (name.Length is 0 or > 64 || name.Any(char.IsControl))
            throw new RegistrationException(400, "bad-name", "Name must be 1–64 printable characters.");
        if (caller.ExePath is null)
            throw new RegistrationException(403, "caller-unidentified", "Postern couldn't identify the registering process.");
        if (scopeTexts.Count is 0 or > MaxScopes)
            throw new RegistrationException(400, "bad-scopes", $"Ask for 1–{MaxScopes} scopes.");

        var scopes = new List<Scope>();
        foreach (var text in scopeTexts)
        {
            if (!Scope.TryParse(text, out var scope, out var error))
                throw new RegistrationException(400, "bad-scope", error);
            scopes.Add(scope);
        }

        var claim = Secrets.NewClaim();
        AppRegistration registration;
        AppRegistration? previous;
        AppRegistration? replaced;
        CancellationTokenSource asking;
        lock (_gate)
        {
            var all = _store.All();
            var waiting = all.Where(r => r.Status == RegistrationStatus.Pending && SameExe(r.ExePath, caller.ExePath)).ToList();
            // The same app asking again (it restarted, or gave up waiting) replaces its request rather than queueing a second.
            replaced = waiting.FirstOrDefault(r => SameApp(r, name, caller.ExePath));
            if (replaced is null && waiting.Count >= MaxPendingPerExe)
                throw new RegistrationException(429, "registration-pending", $"This program already has {waiting.Count} registrations waiting for approval.");
            if (replaced is not null)
                _store.Save(replaced with { Status = RegistrationStatus.Superseded, DecidedAt = _time.GetUtcNow() });

            previous = all.LastOrDefault(r => r.Status == RegistrationStatus.Approved && SameApp(r, name, caller.ExePath));
            registration = new AppRegistration
            {
                Id = Secrets.NewId(),
                Name = name,
                ExePath = caller.ExePath,
                Signer = caller.SignatureTrusted ? caller.Signer : null,
                Scopes = scopes,
                Status = RegistrationStatus.Pending,
                CreatedAt = _time.GetUtcNow(),
                ClaimHash = Secrets.Hash(claim),
            };
            _store.Save(registration);
            _decisions[registration.Id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            asking = new CancellationTokenSource(_approvalTimeout, _time);
            _asking[registration.Id] = asking;
        }

        if (replaced is not null)
            Withdraw(replaced.Id);
        Changed?.Invoke();
        var request = new ApprovalRequest(registration.Id, name, caller.ExePath, caller.ProcessId, caller.SignatureTrusted,
            caller.Signer, scopes, previous?.Scopes);
        _ = AskAsync(request, asking);
        return new RegistrationTicket(registration.Id, claim);
    }

    async Task AskAsync(ApprovalRequest request, CancellationTokenSource asking)
    {
        bool approved;
        try
        {
            approved = await _prompt.AskAsync(request, asking.Token);
        }
        catch
        {
            approved = false; // a prompt that fails is a no
        }
        finally
        {
            _asking.TryRemove(request.RegistrationId, out _);
            asking.Dispose();
        }
        Decide(request.RegistrationId, approved);
    }

    /// <summary>Takes a no-longer-pending registration out of the prompt and wakes anyone waiting to claim it.</summary>
    void Withdraw(string id)
    {
        if (_asking.TryGetValue(id, out var asking))
        {
            try
            {
                asking.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // its prompt already finished
            }
        }
        if (_decisions.TryRemove(id, out var decided))
            decided.TrySetResult();
    }

    /// <summary>Records the user's answer. Ignored unless the registration is still pending.</summary>
    public void Decide(string id, bool approve)
    {
        lock (_gate)
        {
            var registration = _store.Find(id);
            if (registration is not { Status: RegistrationStatus.Pending })
                return;

            var now = _time.GetUtcNow();
            if (approve)
            {
                foreach (var older in _store.All().Where(r => r.Status == RegistrationStatus.Approved && SameApp(r, registration.Name, registration.ExePath)))
                    _store.Save(older with { Status = RegistrationStatus.Superseded, DecidedAt = now });
            }
            _store.Save(registration with { Status = approve ? RegistrationStatus.Approved : RegistrationStatus.Denied, DecidedAt = now });
        }

        if (_decisions.TryRemove(id, out var decided))
            decided.TrySetResult();
        Changed?.Invoke();
    }

    /// <summary>Waits up to <paramref name="wait"/> for a decision, then issues the token once.</summary>
    public async Task<ClaimResult> ClaimAsync(string id, string claim, CallerInfo caller, TimeSpan wait, CancellationToken cancellationToken)
    {
        var registration = _store.Find(id);
        if (registration is null || !Secrets.HashEquals(claim, registration.ClaimHash))
            return new(ClaimStatus.NotFound, null, "No such registration.");
        if (!caller.Matches(registration))
            return new(ClaimStatus.CallerMismatch, null, "Only the process that registered can claim the token.");

        if (registration.Status == RegistrationStatus.Pending && _decisions.TryGetValue(id, out var decided))
        {
            try
            {
                await decided.Task.WaitAsync(wait, _time, cancellationToken);
            }
            catch (TimeoutException)
            {
                return new(ClaimStatus.Pending, null, "Waiting for approval in Postern.");
            }
        }

        lock (_gate)
        {
            registration = _store.Find(id)!;
            switch (registration.Status)
            {
                case RegistrationStatus.Approved when registration.TokenHash is null:
                    var token = Secrets.NewToken();
                    _store.Save(registration with { TokenHash = Secrets.Hash(token) });
                    return new(ClaimStatus.Issued, token, "Approved.");
                case RegistrationStatus.Approved:
                    return new(ClaimStatus.AlreadyClaimed, null, "The token was already issued. Register again if it was lost.");
                case RegistrationStatus.Pending:
                    return new(ClaimStatus.Pending, null, "Waiting for approval in Postern.");
                default:
                    return new(ClaimStatus.Denied, null, $"Registration {registration.Status.ToString().ToLowerInvariant()}.");
            }
        }
    }

    public void Revoke(string id)
    {
        lock (_gate)
        {
            var registration = _store.Find(id);
            if (registration is not { Status: RegistrationStatus.Approved })
                return;
            _store.Save(registration with { Status = RegistrationStatus.Revoked, DecidedAt = _time.GetUtcNow() });
        }
        Changed?.Invoke();
    }

    static bool SameExe(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    static bool SameApp(AppRegistration r, string name, string exePath) =>
        string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) && SameExe(r.ExePath, exePath);
}
