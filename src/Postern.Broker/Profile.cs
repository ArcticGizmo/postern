using Postern.Broker.Credentials;

namespace Postern.Broker;

/// <summary>
/// Debug builds run as "Postern (Dev)", so a dev build can run next to an installed Postern without either one
/// touching the other: its own pipe, data folder, credentials and login entry. Without this, the single-instance
/// guard would make whichever started second exit quietly, and both would share one database.
/// <para>
/// <c>POSTERN_DEV</c> forces it either way: <c>0</c> or <c>false</c> runs a Debug build as the real Postern, and any
/// other value runs a Release build as Dev. postern-cli follows the same rule, so a Debug CLI talks to a Debug Postern.
/// </para>
/// </summary>
public static class Profile
{
    public const string DevPipeName = "postern-dev";
    public const string EnvironmentVariable = "POSTERN_DEV";

    public static bool IsDev { get; } = ComputeIsDev();

    public static string PipeName => IsDev ? DevPipeName : BrokerOptions.DefaultPipeName;

    public static string DataDirectory => IsDev
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Postern.Dev")
        : BrokerOptions.DefaultDataDirectory;

    /// <summary>"" or " (Dev)", for the tray and window titles.</summary>
    public static string DisplaySuffix => IsDev ? " (Dev)" : "";

    /// <summary>Dev credentials live under postern-dev:…, so a dev build can't change or delete the real ones (and an uninstall leaves dev's alone).</summary>
    public static ICredentialStore CredentialStore() =>
        IsDev ? new NamespacedCredentialStore(new WindowsCredentialStore(), "postern-dev") : new WindowsCredentialStore();

    /// <summary>The Credential Manager name a provider target is stored under in this profile.</summary>
    public static string StoredName(string target) => IsDev ? NamespacedCredentialStore.Map(target, "postern-dev") : target;

    static bool ComputeIsDev()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(value))
            return !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
