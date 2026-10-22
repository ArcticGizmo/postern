using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Postern.Client;

public enum RegistrationOutcome { Approved, Denied, TimedOut }

public sealed record RegistrationResult(RegistrationOutcome Outcome, string Detail);

public sealed record PosternStatus(string Version, string Pipe, int ProcessId, string[] Providers, PosternAppStatus? App);

public sealed record PosternAppStatus(string Id, string Name, string Status, string[] Scopes);

/// <summary>
/// One app's connection to Postern.
/// <code>
/// var postern = new PosternClient(new PosternOptions { AppName = "perch" });
/// if (!postern.HasToken)
///     await postern.RegisterAsync(["GET github/repos/*/*/pulls/**", "POST github/graphql"]);
/// using var github = postern.CreateHttpClient("github");
/// var me = await github.GetStringAsync("user");
/// </code>
/// </summary>
public sealed class PosternClient : IDisposable
{
    public const string TokenHeader = "Postern-App-Token";

    /// <summary>
    /// What to tell the user when nothing is listening. Clients don't start Postern themselves: it would inherit the
    /// app's environment, elevation and lifetime (and die with the app's process tree), so it's started by the user
    /// or at login.
    /// </summary>
    public const string NotRunningHint = "Start Postern.exe, and turn on Start at login in its tray menu so it's up before your apps.";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly ITokenStore _tokens;
    readonly Lazy<HttpClient> _control;
    string? _token;

    public PosternClient(PosternOptions options)
    {
        Options = options;
        _tokens = options.TokenStore ?? (OperatingSystem.IsWindows() ? DpapiFileTokenStore.ForApp(TokenKey(options.AppName, options.PipeName)) : new InMemoryTokenStore());
        _token = _tokens.Load();
        _control = new Lazy<HttpClient>(() => new HttpClient(CreateHandler(), disposeHandler: true) { BaseAddress = new Uri("http://postern/_postern/") });
    }

    public PosternOptions Options { get; }

    /// <summary>Token file key: the app name, plus the pipe when it isn't the default (a test broker's token is useless elsewhere).</summary>
    public static string TokenKey(string appName, string pipeName) =>
        pipeName == PosternOptions.DefaultPipeName ? appName : $"{appName}.{pipeName}";

    public bool HasToken => _token is not null;

    internal string? Token => _token;

    /// <summary>Windows, and something is listening on the pipe (it is verified on connect, not here).</summary>
    public bool IsAvailable => OperatingSystem.IsWindows() && PipeConnector.PipeExists(Options.PipeName);

    /// <summary>
    /// For apps that can start before Postern does (a stack launched at login, a script in a startup folder): waits
    /// until the pipe exists. False if it still doesn't after <paramref name="timeout"/>.
    /// </summary>
    public async Task<bool> WaitUntilAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!IsAvailable)
        {
            if (DateTimeOffset.UtcNow >= deadline)
                return false;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        return true;
    }

    /// <summary>A handler for your own HttpClient (or an SDK's adapter). Requests must target http://postern/{provider}/...</summary>
    public HttpMessageHandler CreateHandler()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Postern is Windows-only.");
        return new PosternHandler(this, PipeConnector.CreateHandler(Options));
    }

    /// <summary>An HttpClient whose relative URIs are provider paths: <c>CreateHttpClient("github").GetAsync("user")</c>.</summary>
    public HttpClient CreateHttpClient(string provider) =>
        new(CreateHandler(), disposeHandler: true) { BaseAddress = new Uri($"http://postern/{provider}/") };

    /// <summary>
    /// Asks Postern for the given scopes. The user sees an approval prompt; this waits for the answer (or the timeout)
    /// and stores the token on approval. Re-registering replaces any existing token once approved.
    /// </summary>
    public async Task<RegistrationResult> RegisterAsync(IEnumerable<string> scopes, TimeSpan? approvalTimeout = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + (approvalTimeout ?? TimeSpan.FromMinutes(5.5));
        using var begin = await _control.Value.PostAsJsonAsync("register", new { name = Options.AppName, scopes = scopes.ToArray() }, Json, cancellationToken);
        if (await begin.GetPosternErrorAsync(cancellationToken) is { } beginError)
            return new(RegistrationOutcome.Denied, $"{beginError.Code}: {beginError.Detail}");
        var ticket = await begin.Content.ReadFromJsonAsync<Ticket>(Json, cancellationToken) ?? throw new InvalidDataException("Empty registration response.");

        while (DateTimeOffset.UtcNow < deadline)
        {
            var wait = (int)Math.Clamp((deadline - DateTimeOffset.UtcNow).TotalSeconds, 1, 30);
            using var claim = await _control.Value.PostAsJsonAsync($"register/{ticket.Id}/claim?wait={wait}", new { claim = ticket.Claim }, Json, cancellationToken);
            if (await claim.GetPosternErrorAsync(cancellationToken) is { } claimError)
                return new(RegistrationOutcome.Denied, $"{claimError.Code}: {claimError.Detail}");
            if (claim.StatusCode == HttpStatusCode.OK)
            {
                var issued = await claim.Content.ReadFromJsonAsync<Claimed>(Json, cancellationToken);
                if (issued?.Token is null)
                    throw new InvalidDataException("Approved but no token in the response.");
                _tokens.Save(issued.Token);
                _token = issued.Token;
                return new(RegistrationOutcome.Approved, issued.Detail);
            }
        }
        return new(RegistrationOutcome.TimedOut, "No answer in time. The prompt may still be open in Postern.");
    }

    public async Task<PosternStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        await _control.Value.GetFromJsonAsync<PosternStatus>("status", Json, cancellationToken) ?? throw new InvalidDataException("Empty status.");

    /// <summary>Drops the stored token (for example after <see cref="PosternError.NeedsRegistration"/>).</summary>
    public void ForgetToken()
    {
        _tokens.Clear();
        _token = null;
    }

    public void Dispose()
    {
        if (_control.IsValueCreated)
            _control.Value.Dispose();
    }

    sealed record Ticket(string Id, string Claim);

    sealed record Claimed(string Status, [property: JsonPropertyName("token")] string? Token, string Detail);
}
