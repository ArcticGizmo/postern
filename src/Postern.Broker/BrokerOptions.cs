using Postern.Core.Providers;

namespace Postern.Broker;

public sealed class BrokerOptions
{
    public const string DefaultPipeName = "postern";

    public string PipeName { get; init; } = DefaultPipeName;

    /// <summary>Holds postern.db, providers.json and logs. Default %LOCALAPPDATA%\Postern.Data.</summary>
    public string DataDirectory { get; init; } = DefaultDataDirectory;

    public IReadOnlyList<ProviderDefinition> Providers { get; init; } = [ProviderDefinition.GitHub];

    /// <summary>Set when providers.json couldn't be used, so <see cref="Providers"/> fell back to the built-ins; shown in the app.</summary>
    public string? ProvidersLoadError { get; init; }

    /// <summary>Write HKCU\Software\Postern\Pipes\{pipe} so clients can verify the server exe. Off for in-process tests.</summary>
    public bool PublishDiscovery { get; init; } = true;

    public TimeSpan ApprovalTimeout { get; init; } = Postern.Core.Apps.Registrations.DefaultApprovalTimeout;

    // Not %LOCALAPPDATA%\Postern: that's the Velopack install root, which an uninstall deletes wholesale.
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Postern.Data");

    /// <summary>Built-in GitHub plus the connections in providers.json. See <see cref="ProviderEntry"/> for the shapes.</summary>
    public static IReadOnlyList<ProviderDefinition> LoadProviders(string dataDirectory) =>
        ProvidersFile.Resolve(ProvidersFile.Read(dataDirectory));
}
