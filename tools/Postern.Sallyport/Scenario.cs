using System.Text.Json;
using System.Text.Json.Serialization;

namespace Postern.Sallyport;

/// <summary>
/// One request and what should happen to it. Scenarios live as JSON in scenarios/*.json next to the exe, one group per
/// file, so adding one doesn't need a rebuild, and a runner in another language can read the same files.
/// </summary>
public sealed record Scenario
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Group { get; set; } = "";
    public string? Note { get; init; }
    public required ScenarioRequest Request { get; init; }

    /// <summary>
    /// "conditional": send once, then again with the ETag, and expect the second to be free.
    /// "hang": the request is expected to time out, and another must get through while it hangs.
    /// </summary>
    public string? Flow { get; init; }

    /// <summary>Set on fake-upstream before the request and cleared after.</summary>
    public KnobSetting? Knob { get; init; }

    /// <summary>For flow "hang": what has to get through meanwhile.</summary>
    public ScenarioRequest? Meanwhile { get; init; }

    /// <summary>The scope Sallyport asks for to cover this request. Null means derived from the request; "" means none (it's meant to be refused).</summary>
    public string? Scope { get; init; }

    public required Expectation Expect { get; init; }

    public string? ScopeNeeded => Scope switch
    {
        "" => null,
        { } given => given,
        null => $"{Request.Method.ToUpperInvariant()} {Request.Connection}/{Request.Path.Split('?')[0]}",
    };
}

public sealed record ScenarioRequest
{
    public required string Method { get; init; }
    public required string Connection { get; init; }
    public required string Path { get; init; }
    public Dictionary<string, string> Headers { get; init; } = [];
    public JsonElement? Json { get; init; }
    public Dictionary<string, string>? Form { get; init; }
    public List<MultipartPart>? Multipart { get; init; }

    /// <summary>Send without the app token, as an app that hasn't registered would.</summary>
    public bool OmitToken { get; init; }

    public int TimeoutSeconds { get; init; } = 30;

    public override string ToString() => $"{Method.ToUpperInvariant()} {Connection}/{Path}";
}

public sealed record MultipartPart(string Name, string? Filename = null, string? Text = null, int? Bytes = null);

public sealed record KnobSetting(string Service, string Fault, int? Seconds = null, int? Count = null);

/// <summary>Every field is optional; only the ones set are checked.</summary>
public sealed record Expectation
{
    public int? Status { get; init; }

    /// <summary>The Postern-Error code, "none" for "Postern let it through", or a prefix ending in * ("upstream-*").</summary>
    public string? PosternError { get; init; }

    /// <summary>"reached" or "untouched": whether fake-upstream saw the request at all.</summary>
    public string? Upstream { get; init; }

    /// <summary>Headers fake-upstream must have received, each starting with the given (masked) value.</summary>
    public Dictionary<string, string>? UpstreamHeaders { get; init; }

    /// <summary>Headers fake-upstream must not have received.</summary>
    public List<string>? UpstreamAbsent { get; init; }

    /// <summary>The body fake-upstream received hashes the same as the one Sallyport sent.</summary>
    public bool UpstreamBodyMatches { get; init; }

    /// <summary>Response headers that must be present.</summary>
    public List<string>? Headers { get; init; }

    /// <summary>Response headers that must have exactly this value.</summary>
    public Dictionary<string, string>? HeaderEquals { get; init; }

    /// <summary>The response body hashes to its X-Fake-Body-Sha256.</summary>
    public bool BodyMatchesSha { get; init; }

    public string? BodyContains { get; init; }
    public long? MinBytes { get; init; }
    public long? MaxBytes { get; init; }
    public int? JsonArrayLength { get; init; }

    /// <summary>Server-sent events must arrive spread over at least this long, not in one lump.</summary>
    public int? StreamSpreadMs { get; init; }
}

public static class Scenarios
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "scenarios");

    /// <summary>Every scenarios/*.json, in file name order. Throws <see cref="InvalidDataException"/> naming the file that's wrong.</summary>
    public static IReadOnlyList<Scenario> Load(string? folder = null)
    {
        var all = new List<Scenario>();
        foreach (var file in Directory.EnumerateFiles(folder ?? Folder, "*.json").Order(StringComparer.Ordinal))
        {
            ScenarioFile? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<ScenarioFile>(File.ReadAllText(file), Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{Path.GetFileName(file)}: {ex.Message}", ex);
            }
            foreach (var scenario in parsed?.Scenarios ?? [])
            {
                scenario.Group = parsed!.Group;
                all.Add(scenario);
            }
        }
        if (all.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1) is { } repeated)
            throw new InvalidDataException($"Scenario id '{repeated.Key}' is used more than once.");
        return all;
    }

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    sealed record ScenarioFile(string Group, List<Scenario> Scenarios);
}
