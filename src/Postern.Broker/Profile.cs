using Postern.Broker.Credentials;

namespace Postern.Broker;

public enum ProfileKind
{
    Live,
    Dev,
    Sandbox,
}

/// <summary>
/// Debug builds run as "Postern (Dev)", so a dev build can run next to an installed Postern without either one
/// touching the other: its own pipe, data folder, credentials and login entry. Without this, the single-instance
/// guard would make whichever started second exit quietly, and both would share one database.
/// <para>
/// <c>POSTERN_DEV</c> forces it either way: <c>0</c> or <c>false</c> runs a Debug build as the real Postern, and any
/// other value runs a Release build as Dev. postern-cli follows the same rule, so a Debug CLI talks to a Debug Postern.
/// </para>
/// <para>
/// <c>POSTERN_SANDBOX</c> (or <c>Postern.exe --sandbox</c>, which sets it for everything Postern starts) runs either
/// build as "Postern (Sandbox)": separate again from Live and Dev, and only able to send requests to loopback with
/// made-up credentials (see <see cref="Sandbox"/>). It wins over <c>POSTERN_DEV</c>.
/// </para>
/// </summary>
public static class Profile
{
    public const string DevPipeName = "postern-dev";
    public const string SandboxPipeName = "postern-sandbox";
    public const string EnvironmentVariable = "POSTERN_DEV";
    public const string SandboxVariable = "POSTERN_SANDBOX";

    public static ProfileKind Kind { get; } = Compute();

    public static bool IsDev => Kind == ProfileKind.Dev;

    public static bool IsSandbox => Kind == ProfileKind.Sandbox;

    public static string PipeName => Kind switch
    {
        ProfileKind.Dev => DevPipeName,
        ProfileKind.Sandbox => SandboxPipeName,
        _ => BrokerOptions.DefaultPipeName,
    };

    public static string DataDirectory => Kind switch
    {
        ProfileKind.Dev => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Postern.Dev"),
        ProfileKind.Sandbox => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Postern.Sandbox"),
        _ => BrokerOptions.DefaultDataDirectory,
    };

    /// <summary>"", " (Dev)" or " (Sandbox)", for the tray and window titles.</summary>
    public static string DisplaySuffix => Kind switch
    {
        ProfileKind.Dev => " (Dev)",
        ProfileKind.Sandbox => " (Sandbox)",
        _ => "",
    };

    /// <summary>
    /// Dev and Sandbox credentials live under their own prefix, so neither can change or delete the real ones (and an
    /// uninstall leaves theirs alone). Null for Live, which uses postern:… as it is.
    /// </summary>
    public static string? CredentialNamespace => Kind switch
    {
        ProfileKind.Dev => "postern-dev",
        ProfileKind.Sandbox => "postern-sandbox",
        _ => null,
    };

    public static ICredentialStore CredentialStore() => CredentialNamespace switch
    {
        null => new WindowsCredentialStore(),
        var ns when IsSandbox => new SandboxCredentialStore(new NamespacedCredentialStore(new WindowsCredentialStore(), ns)),
        var ns => new NamespacedCredentialStore(new WindowsCredentialStore(), ns),
    };

    /// <summary>The Credential Manager name a provider target is stored under in this profile.</summary>
    public static string StoredName(string target) =>
        CredentialNamespace is { } ns ? NamespacedCredentialStore.Map(target, ns) : target;

    static ProfileKind Compute()
    {
        if (IsOn(Environment.GetEnvironmentVariable(SandboxVariable)) is true)
            return ProfileKind.Sandbox;
        if (IsOn(Environment.GetEnvironmentVariable(EnvironmentVariable)) is { } dev)
            return dev ? ProfileKind.Dev : ProfileKind.Live;
#if DEBUG
        return ProfileKind.Dev;
#else
        return ProfileKind.Live;
#endif
    }

    // Null when unset; false for 0 or false; true for anything else.
    static bool? IsOn(string? value) =>
        string.IsNullOrEmpty(value) ? null : !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
}
