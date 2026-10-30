using System.Text.Json;
using System.Text.Json.Serialization;
using Postern.Core.Providers;

namespace Postern.Broker;

/// <summary>
/// One connection as providers.json spells it. Three shapes:
/// <list type="bullet">
/// <item>From a template: <c>{ "name": "github-work", "template": "github" }</c>, plus <c>baseUrl</c> when the template's URL has a host or site in it.</item>
/// <item>Custom: <c>name</c>, <c>baseUrl</c>, <c>fields</c> and <c>inject</c> (a template's fields and inject can be overridden the same way).</item>
/// <item>The original shorthand: <c>name</c>, <c>baseUrl</c>, <c>auth</c> (bearer, header, basic) and <c>authHeader</c>.</item>
/// </list>
/// </summary>
public sealed record ProviderEntry
{
    public required string Name { get; init; }
    public string? Template { get; init; }
    public string? BaseUrl { get; init; }
    public List<CredentialField>? Fields { get; init; }
    public List<Injection>? Inject { get; init; }
    public AuthStyle? Auth { get; init; }
    public string? AuthHeader { get; init; }

    public ProviderDefinition Resolve()
    {
        try
        {
            if (Template is { } id)
            {
                var template = ProviderTemplates.Find(id) ?? throw new ArgumentException($"there's no template '{id}'");
                var url = BaseUrl is { } given ? new Uri(given) : template.ResolveUrl();
                return new ProviderDefinition(Name, url, Fields ?? [.. template.Fields], Inject ?? [.. template.Inject], id);
            }
            if (BaseUrl is null)
                throw new ArgumentException("needs a template or a baseUrl");
            if (Inject is not null)
                return new ProviderDefinition(Name, new Uri(BaseUrl), Fields ?? [new CredentialField("secret", "Secret")], Inject);
            return new ProviderDefinition(Name, new Uri(BaseUrl), Auth ?? AuthStyle.Bearer, AuthHeader);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            throw new ArgumentException(ex.Message.StartsWith("Connection '", StringComparison.Ordinal) ? ex.Message : $"Connection '{Name}': {ex.Message.TrimEnd('.')}.", ex);
        }
    }
}

/// <summary>providers.json in the data folder: the connections beyond the built-in <c>github</c>, which it can also override.</summary>
public static class ProvidersFile
{
    public const string FileName = "providers.json";

    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>The file's text, or an empty list to start from when there isn't one yet.</summary>
    public static string ReadText(string dataDirectory) =>
        File.Exists(PathIn(dataDirectory)) ? File.ReadAllText(PathIn(dataDirectory)) : Serialize([]);

    public static IReadOnlyList<ProviderEntry> Read(string dataDirectory) => Parse(ReadText(dataDirectory));

    /// <summary>Throws <see cref="ArgumentException"/> with a readable message when the JSON doesn't parse.</summary>
    public static IReadOnlyList<ProviderEntry> Parse(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<FileShape>(json, Options) ?? throw new ArgumentException("The file is empty.");
            return file.Providers ?? [];
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Not valid JSON: {ex.Message}", ex);
        }
    }

    public static string Serialize(IEnumerable<ProviderEntry> entries) => JsonSerializer.Serialize(new FileShape([.. entries]), Options);

    /// <summary>Built-in github plus every entry, validated. Same name as github overrides it; any other repeat is an error.</summary>
    public static IReadOnlyList<ProviderDefinition> Resolve(IEnumerable<ProviderEntry> entries)
    {
        var connections = new Dictionary<string, ProviderDefinition>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var definition = entry.Resolve();
            definition.Validate();
            if (!connections.TryAdd(definition.Name, definition))
                throw new ArgumentException($"Connection '{definition.Name}' is listed twice.");
        }
        connections.TryAdd("github", ProviderDefinition.GitHub);
        return [.. connections.Values.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>Writes through a temporary file, so a crash mid-write can't leave Postern with half a config.</summary>
    public static void Write(string dataDirectory, string json)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = PathIn(dataDirectory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    sealed record FileShape(List<ProviderEntry>? Providers);
}
