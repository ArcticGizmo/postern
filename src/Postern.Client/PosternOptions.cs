namespace Postern.Client;

public sealed class PosternOptions
{
    public const string DefaultPipeName = "postern";

    /// <summary>Shown to the user in the approval prompt, and the key for the default token file. Keep it stable.</summary>
    public required string AppName { get; init; }

    /// <summary>Defaults to %POSTERN_PIPE% or "postern".</summary>
    public string PipeName { get; init; } = Environment.GetEnvironmentVariable("POSTERN_PIPE") is { Length: > 0 } pipe ? pipe : DefaultPipeName;

    /// <summary>
    /// The exe the pipe server must be running. Null (the default) reads it from where the running Postern published
    /// it (HKCU\Software\Postern\Pipes\{pipe}\BrokerPath). Set it to pin a specific install.
    /// </summary>
    public string? ExpectedServerExe { get; init; }

    /// <summary>Where the app token lives. Defaults to a DPAPI-protected file under %LOCALAPPDATA%\Postern.Client.</summary>
    public ITokenStore? TokenStore { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Connection attempts for one request while Postern restarts (connect failures only, never after sending).</summary>
    public int ConnectAttempts { get; init; } = 4;
}
