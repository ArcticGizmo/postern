using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Postern.Shadow;

/// <summary>
/// One GitHub question, asked the same way through gh and through Postern. Answers are compared by summary (the
/// facts an app would act on, like which PRs are open), not by raw body: the two transports send different Accept
/// headers and tokens, so incidental fields such as permissions and URLs legitimately differ.
/// </summary>
internal sealed partial record Probe(string Label, HttpMethod Method, string Path, string? Body, Func<JsonElement, string> Summary)
{
    public const string Kinds = "user | review-requests | my-prs | pulls:OWNER/REPO | runs:OWNER/REPO";

    /// <summary>The narrowest scope that allows this probe: its exact path, without the query.</summary>
    public string ScopeFor(string provider) => $"{Method.Method} {provider}/{Path.Split('?')[0]}";

    public static Probe Parse(string text)
    {
        var colon = text.IndexOf(':');
        var kind = colon < 0 ? text : text[..colon];
        var repo = colon < 0 ? null : text[(colon + 1)..];
        if (kind is "pulls" or "runs" && (repo is null || !RepoPattern().IsMatch(repo)))
            throw new ArgumentException($"'{text}': {kind} needs a repo, e.g. {kind}:octo-org/octo-repo");

        return kind switch
        {
            "user" => new("user", HttpMethod.Get, "user", null, r => "login " + Fingerprint(r.GetProperty("login").GetString() ?? "?")),
            "review-requests" => Search("review-requests", "is:pr is:open review-requested:@me archived:false"),
            "my-prs" => Search("my-prs", "is:pr is:open author:@me archived:false"),
            "pulls" => new($"pulls {repo}", HttpMethod.Get, $"repos/{repo}/pulls?state=open&per_page=50", null, Pulls),
            "runs" => new($"runs {repo}", HttpMethod.Get, $"repos/{repo}/actions/runs?per_page=10", null, Runs),
            _ => throw new ArgumentException($"'{text}': unknown probe. Kinds: {Kinds}"),
        };
    }

    /// <summary>The comparable summary of a successful response body. Never throws: a surprise is itself a result.</summary>
    public string Summarise(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            // GraphQL reports errors with a 200, so they'd otherwise look like an empty answer.
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
                return "graphql error: " + (errors[0].TryGetProperty("message", out var m) ? m.GetString() : "?");
            return Summary(root);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return $"unreadable response ({ex.GetType().Name})";
        }
    }

    static Probe Search(string label, string query)
    {
        var body = JsonSerializer.Serialize(new
        {
            query = "query($q: String!) { search(query: $q, type: ISSUE, first: 50) { issueCount nodes { ... on PullRequest { number repository { nameWithOwner } } } } }",
            variables = new { q = query },
        });
        return new(label, HttpMethod.Post, "graphql", body, root =>
        {
            var search = root.GetProperty("data").GetProperty("search");
            var prs = search.GetProperty("nodes").EnumerateArray()
                .Where(n => n.TryGetProperty("number", out _))
                .Select(n => $"{n.GetProperty("repository").GetProperty("nameWithOwner").GetString()}#{n.GetProperty("number").GetInt32()}")
                .Order(StringComparer.Ordinal);
            return $"{search.GetProperty("issueCount").GetInt32()} [{string.Join(", ", prs)}]";
        });
    }

    static string Pulls(JsonElement root)
    {
        var prs = root.EnumerateArray()
            .OrderBy(p => p.GetProperty("number").GetInt32())
            .Select(p => $"#{p.GetProperty("number").GetInt32()}@{p.GetProperty("head").GetProperty("sha").GetString()?[..7]}");
        return $"{root.GetArrayLength()} open [{string.Join(", ", prs)}]";
    }

    static string Runs(JsonElement root)
    {
        var runs = root.GetProperty("workflow_runs").EnumerateArray()
            .Select(r => $"{r.GetProperty("id").GetInt64()} {r.GetProperty("status").GetString()}/{(r.GetProperty("conclusion").ValueKind == JsonValueKind.Null ? "-" : r.GetProperty("conclusion").GetString())}");
        return $"total {root.GetProperty("total_count").GetInt32()}, latest [{string.Join(", ", runs)}]";
    }

    // Logs are meant to be pasted back, and the login is the one thing in them that identifies a person.
    static string Fingerprint(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..10];

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoPattern();
}
