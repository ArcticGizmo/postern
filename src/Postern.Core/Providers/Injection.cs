using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Postern.Core.Providers;

/// <summary>One value the user enters for a connection's credential, such as Jira's account email or its API token.</summary>
/// <param name="Secret">False for values that identify rather than authenticate (an email). Still never shown back.</param>
public sealed record CredentialField(string Name, string Label, bool Secret = true);

/// <summary><c>Authorization: Basic base64(username:password)</c>. With no password, the username template already is <c>user:password</c>.</summary>
public sealed record BasicCredential(string Username, string? Password = null);

/// <summary>
/// Where part of the credential goes on each request: a header, a query parameter, or Basic auth. Exactly one of
/// <see cref="Header"/>, <see cref="Query"/> and <see cref="Basic"/> is set. Values are templates over the
/// connection's fields (<c>Bearer {token}</c>); a value with no placeholders is a fixed header such as an API version.
/// </summary>
public sealed partial record Injection
{
    public string? Header { get; init; }
    public string? Query { get; init; }
    public string? Value { get; init; }
    public BasicCredential? Basic { get; init; }

    public static Injection ToHeader(string header, string value) => new() { Header = header, Value = value };
    public static Injection ToQuery(string parameter, string value) => new() { Query = parameter, Value = value };
    public static Injection AsBasic(string username, string? password) => new() { Basic = new BasicCredential(username, password) };

    /// <summary>The header this sets (Basic sets Authorization), or null for a query parameter.</summary>
    [JsonIgnore]
    public string? HeaderName => Basic is not null ? "Authorization" : Header;

    [JsonIgnore]
    public IEnumerable<string> Templates => Basic is { } basic
        ? basic.Password is null ? new[] { basic.Username } : new[] { basic.Username, basic.Password }
        : new[] { Value ?? "" };

    public static IEnumerable<string> Placeholders(string template) => PlaceholderPattern().Matches(template).Select(m => m.Groups[1].Value);

    public static string Expand(string template, IReadOnlyDictionary<string, string> values) =>
        PlaceholderPattern().Replace(template, m => values[m.Groups[1].Value]);

    [GeneratedRegex(@"\{([a-z][a-z0-9_]{0,31})\}")]
    private static partial Regex PlaceholderPattern();
}

/// <summary>A header or query parameter ready to add to the outgoing request.</summary>
public sealed record Injected(bool IsQuery, string Name, string Value);
