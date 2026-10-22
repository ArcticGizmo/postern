using System.Text.Json;
using System.Text.Json.Serialization;
using Postern.Core.Providers;

namespace Postern.Broker;

public sealed class BrokerOptions
{
    public const string DefaultPipeName = "postern";

    public string PipeName { get; init; } = DefaultPipeName;

    /// <summary>Holds postern.db, providers.json and logs. Default %LOCALAPPDATA%\Postern.Data.</summary>
    public string DataDirectory { get; init; } = DefaultDataDirectory;

    public IReadOnlyList<ProviderDefinition> Providers { get; init; } = [ProviderDefinition.GitHub];

    /// <summary>Write HKCU\Software\Postern\Pipes\{pipe} so clients can verify the server exe. Off for in-process tests.</summary>
    public bool PublishDiscovery { get; init; } = true;

    public TimeSpan ApprovalTimeout { get; init; } = Postern.Core.Apps.Registrations.DefaultApprovalTimeout;

    // Not %LOCALAPPDATA%\Postern: that's the Velopack install root, which an uninstall deletes wholesale.
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Postern.Data");

    /// <summary>
    /// Built-in GitHub plus anything in providers.json (same name overrides). Example file:
    /// <code>{ "providers": [ { "name": "octopus", "baseUrl": "https://octo.example.com", "auth": "header", "authHeader": "X-Octopus-ApiKey" } ] }</code>
    /// </summary>
    public static IReadOnlyList<ProviderDefinition> LoadProviders(string dataDirectory)
    {
        var providers = new Dictionary<string, ProviderDefinition>(StringComparer.Ordinal) { ["github"] = ProviderDefinition.GitHub };
        var path = Path.Combine(dataDirectory, "providers.json");
        if (File.Exists(path))
        {
            var file = JsonSerializer.Deserialize<ProvidersFile>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"{path} is empty.");
            foreach (var p in file.Providers)
            {
                var definition = new ProviderDefinition(p.Name, new Uri(p.BaseUrl), p.Auth, p.AuthHeader);
                definition.Validate();
                providers[definition.Name] = definition;
            }
        }
        return [.. providers.Values.OrderBy(p => p.Name)];
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    sealed record ProvidersFile(List<ProviderEntry> Providers);

    sealed record ProviderEntry(string Name, string BaseUrl, AuthStyle Auth = AuthStyle.Bearer, string? AuthHeader = null);
}
