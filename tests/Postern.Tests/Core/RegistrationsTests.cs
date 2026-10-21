using Postern.Core.Apps;

namespace Postern.Tests.Core;

/// <summary>An approval prompt the test answers by hand.</summary>
internal sealed class ManualPrompt : IApprovalPrompt
{
    readonly Dictionary<string, TaskCompletionSource<bool>> _pending = [];
    readonly SemaphoreSlim _asked = new(0);

    public List<ApprovalRequest> Requests { get; } = [];

    public Task<bool> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => answer.TrySetCanceled());
        lock (_pending)
        {
            Requests.Add(request);
            _pending[request.RegistrationId] = answer;
        }
        _asked.Release();
        return answer.Task;
    }

    public async Task<ApprovalRequest> NextAsync()
    {
        Assert.True(await _asked.WaitAsync(TimeSpan.FromSeconds(5)), "no approval prompt was shown");
        lock (_pending) return Requests[^1];
    }

    public void Answer(string id, bool approve)
    {
        lock (_pending) _pending[id].TrySetResult(approve);
    }
}

public class RegistrationsTests
{
    const string Exe = @"C:\Apps\Perch\Perch.exe";
    static readonly CallerInfo Perch = new(100, Exe, false, null);
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    readonly InMemoryRegistrationStore _store = new();
    readonly ManualPrompt _prompt = new();

    Registrations Create(TimeSpan? timeout = null) => new(_store, _prompt, approvalTimeout: timeout);

    [Fact]
    public async Task ApproveThenClaimIssuesTokenOnce()
    {
        var registrations = Create();
        var ticket = registrations.Begin("perch", ["GET github/user"], Perch);
        var asked = await _prompt.NextAsync();
        Assert.Equal(Exe, asked.ExePath);
        Assert.Equal("GET github/user", Assert.Single(asked.Scopes).ToString());

        var claim = registrations.ClaimAsync(ticket.Id, ticket.Claim, Perch, Wait, default);
        _prompt.Answer(ticket.Id, true);
        var result = await claim;

        Assert.Equal(ClaimStatus.Issued, result.Status);
        Assert.StartsWith(Secrets.TokenPrefix, result.Token);
        Assert.Equal(Secrets.Hash(result.Token!), _store.Find(ticket.Id)!.TokenHash);

        var again = await registrations.ClaimAsync(ticket.Id, ticket.Claim, Perch, Wait, default);
        Assert.Equal(ClaimStatus.AlreadyClaimed, again.Status);
    }

    [Fact]
    public async Task DenyMeansNoToken()
    {
        var registrations = Create();
        var ticket = registrations.Begin("perch", ["GET github/user"], Perch);
        await _prompt.NextAsync();
        _prompt.Answer(ticket.Id, false);
        var result = await registrations.ClaimAsync(ticket.Id, ticket.Claim, Perch, Wait, default);
        Assert.Equal(ClaimStatus.Denied, result.Status);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task UnansweredPromptTimesOutAsDenied()
    {
        var registrations = Create(timeout: TimeSpan.FromMilliseconds(200));
        var ticket = registrations.Begin("perch", ["GET github/user"], Perch);
        var result = await registrations.ClaimAsync(ticket.Id, ticket.Claim, Perch, Wait, default);
        Assert.Equal(ClaimStatus.Denied, result.Status);
        Assert.Equal(RegistrationStatus.Denied, _store.Find(ticket.Id)!.Status);
    }

    [Fact]
    public async Task ClaimReturnsPendingWhenWaitElapses()
    {
        var registrations = Create();
        var ticket = registrations.Begin("perch", ["GET github/user"], Perch);
        var result = await registrations.ClaimAsync(ticket.Id, ticket.Claim, Perch, TimeSpan.FromMilliseconds(50), default);
        Assert.Equal(ClaimStatus.Pending, result.Status);
    }

    [Fact]
    public async Task OnlyTheRegisteringExeCanClaim()
    {
        var registrations = Create();
        var ticket = registrations.Begin("perch", ["GET github/user"], Perch);
        await _prompt.NextAsync();
        _prompt.Answer(ticket.Id, true);
        var thief = new CallerInfo(666, @"C:\Evil\x.exe", false, null);
        Assert.Equal(ClaimStatus.CallerMismatch, (await registrations.ClaimAsync(ticket.Id, ticket.Claim, thief, Wait, default)).Status);
        Assert.Equal(ClaimStatus.NotFound, (await registrations.ClaimAsync(ticket.Id, Secrets.NewClaim(), Perch, Wait, default)).Status);
        Assert.Equal(ClaimStatus.Issued, (await registrations.ClaimAsync(ticket.Id, ticket.Claim, Perch, Wait, default)).Status);
    }

    [Fact]
    public async Task OnePendingRegistrationPerExe()
    {
        var registrations = Create();
        registrations.Begin("perch", ["GET github/user"], Perch);
        var ex = Assert.Throws<RegistrationException>(() => registrations.Begin("perch-2", ["GET github/user"], Perch));
        Assert.Equal("registration-pending", ex.Code);
        await _prompt.NextAsync();
    }

    [Fact]
    public async Task ReapprovalSupersedesTheOldToken()
    {
        var registrations = Create();
        var first = registrations.Begin("perch", ["GET github/user"], Perch);
        await _prompt.NextAsync();
        _prompt.Answer(first.Id, true);
        await registrations.ClaimAsync(first.Id, first.Claim, Perch, Wait, default);

        var second = registrations.Begin("perch", ["GET github/user", "POST github/graphql"], Perch);
        var asked = await _prompt.NextAsync();
        Assert.NotNull(asked.PreviousScopes);
        _prompt.Answer(second.Id, true);
        await registrations.ClaimAsync(second.Id, second.Claim, Perch, Wait, default);

        Assert.Equal(RegistrationStatus.Superseded, _store.Find(first.Id)!.Status);
        Assert.Equal(RegistrationStatus.Approved, _store.Find(second.Id)!.Status);
    }

    [Fact]
    public async Task RevokeStopsAnApprovedApp()
    {
        var registrations = Create();
        var ticket = registrations.Begin("perch", ["GET github/user"], Perch);
        await _prompt.NextAsync();
        _prompt.Answer(ticket.Id, true);
        var token = (await registrations.ClaimAsync(ticket.Id, ticket.Claim, Perch, Wait, default)).Token;

        Assert.True(AccessPolicy.Evaluate(_store, token, Perch, "GET", "github", "/user").Allowed);
        registrations.Revoke(ticket.Id);
        Assert.Equal("app-revoked", AccessPolicy.Evaluate(_store, token, Perch, "GET", "github", "/user").Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\nname")]
    public void RejectsBadNames(string name) =>
        Assert.Equal("bad-name", Assert.Throws<RegistrationException>(() => Create().Begin(name, ["GET github/user"], Perch)).Code);

    [Fact]
    public void RejectsBadScopes() =>
        Assert.Equal("bad-scope", Assert.Throws<RegistrationException>(() => Create().Begin("perch", ["GET github/pull*"], Perch)).Code);

    [Fact]
    public void SignerRecordedOnlyWhenTrusted()
    {
        var registrations = Create();
        var ticket = registrations.Begin("perch", ["GET github/user"], new CallerInfo(1, Exe, false, "CN=Untrusted"));
        Assert.Null(_store.Find(ticket.Id)!.Signer);
    }
}
