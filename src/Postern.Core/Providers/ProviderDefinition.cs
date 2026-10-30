using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Postern.Core.Scopes;

namespace Postern.Core.Providers;

/// <summary>The shorthand auth styles providers.json has always accepted; each expands to one <see cref="Injection"/>.</summary>
public enum AuthStyle
{
    /// <summary><c>Authorization: Bearer &lt;secret&gt;</c> (GitHub, Graph).</summary>
    Bearer,
    /// <summary>A named header carrying the raw secret (Octopus: <c>X-Octopus-ApiKey</c>).</summary>
    Header,
    /// <summary><c>Authorization: Basic base64(secret)</c>, where the secret is stored as <c>user:token</c> (Jira).</summary>
    Basic,
}

/// <summary>
/// A connection: a name apps route and scope by (<c>/github-work/...</c>, <c>GET github-work/**</c>), a base URL, the
/// fields its credential is made of, and where those go on each request. No per-endpoint wrappers, so every endpoint
/// works. Several connections can share a template (two GitHub accounts), each with its own credential.
/// </summary>
public sealed partial class ProviderDefinition
{
    const int MaxFields = 6;
    const int MaxInjections = 8;

    static readonly CredentialField[] SecretOnly = [new("secret", "Secret")];

    // Hop-by-hop, framing and forwarding headers: injecting these would break the request or lie about the client.
    static readonly string[] ForbiddenHeaders =
        ["Host", "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Proxy-Authorization", "Proxy-Connection", "Forwarded", "X-Real-IP"];

    public ProviderDefinition(string name, Uri baseUrl, AuthStyle auth = AuthStyle.Bearer, string? authHeader = null)
        : this(name, baseUrl, SecretOnly, [Shorthand(auth, authHeader)])
    {
    }

    public ProviderDefinition(string name, Uri baseUrl, IReadOnlyList<CredentialField> fields, IReadOnlyList<Injection> inject, string? template = null)
    {
        Name = name;
        BaseUrl = baseUrl;
        Fields = fields;
        Inject = inject;
        Template = template;
    }

    public string Name { get; }
    public Uri BaseUrl { get; }
    public IReadOnlyList<CredentialField> Fields { get; }
    public IReadOnlyList<Injection> Inject { get; }

    /// <summary>The <see cref="ProviderTemplates"/> id this came from, or null for a custom connection.</summary>
    public string? Template { get; }

    public string TemplateName => ProviderTemplates.Find(Template)?.DisplayName ?? "Custom";

    public string CredentialTarget => TargetFor(Name);

    public static string TargetFor(string name) => $"postern:{name}:default";

    public static ProviderDefinition GitHub { get; } = ProviderTemplates.Find("github")!.Connect("github");

    public static Injection Shorthand(AuthStyle auth, string? authHeader) => auth switch
    {
        AuthStyle.Bearer => Injection.ToHeader("Authorization", "Bearer {secret}"),
        AuthStyle.Header => Injection.ToHeader(authHeader ?? "", "{secret}"),
        AuthStyle.Basic => Injection.AsBasic("{secret}", null),
        _ => throw new ArgumentOutOfRangeException(nameof(auth)),
    };

    /// <summary>Throws <see cref="ArgumentException"/> naming the first thing wrong.</summary>
    public void Validate()
    {
        if (!ProviderName.IsValid(Name))
            throw Problem($"name '{Name}' is invalid (lowercase letters, digits, '-', starting with a letter)");
        if (!BaseUrl.IsAbsoluteUri)
            throw Problem("the base URL must be absolute");
        if (BaseUrl.Scheme != Uri.UriSchemeHttps && !(BaseUrl.Scheme == Uri.UriSchemeHttp && BaseUrl.IsLoopback))
            throw Problem("must use https (plain http is allowed only for loopback test upstreams)");
        if (BaseUrl.UserInfo.Length > 0 || BaseUrl.Query.Length > 0 || BaseUrl.Fragment.Length > 0 || BaseUrl.OriginalString.Contains('{'))
            throw Problem("the base URL can't carry a user, query, fragment or unfilled {placeholder}; credentials go in fields");

        if (Fields.Count is 0 or > MaxFields)
            throw Problem($"needs 1–{MaxFields} credential fields");
        foreach (var field in Fields)
        {
            if (!FieldName().IsMatch(field.Name))
                throw Problem($"field name '{field.Name}' is invalid (lowercase letters, digits, '_', starting with a letter)");
            if (string.IsNullOrWhiteSpace(field.Label))
                throw Problem($"field '{field.Name}' needs a label");
        }
        if (Fields.Select(f => f.Name).Distinct().Count() != Fields.Count)
            throw Problem("field names must be unique");

        if (Inject.Count is 0 or > MaxInjections)
            throw Problem($"needs 1–{MaxInjections} injection rules");
        var used = new HashSet<string>();
        foreach (var rule in Inject)
        {
            foreach (var template in rule.Templates)
                foreach (var placeholder in Injection.Placeholders(template))
                {
                    if (!Fields.Any(f => f.Name == placeholder))
                        throw Problem($"'{{{placeholder}}}' isn't one of its fields ({string.Join(", ", Fields.Select(f => f.Name))})");
                    used.Add(placeholder);
                }
            ValidateRule(rule);
        }
        if (Fields.FirstOrDefault(f => !used.Contains(f.Name)) is { } unused)
            throw Problem($"field '{unused.Name}' is never injected");

        var headers = Inject.Select(r => r.HeaderName).OfType<string>().ToList();
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Count)
            throw Problem("sets the same header twice (Basic sets Authorization)");
        var queries = Inject.Select(r => r.Query).OfType<string>().ToList();
        if (queries.Distinct(StringComparer.Ordinal).Count() != queries.Count)
            throw Problem("sets the same query parameter twice");
    }

    void ValidateRule(Injection rule)
    {
        var kinds = (rule.Header is null ? 0 : 1) + (rule.Query is null ? 0 : 1) + (rule.Basic is null ? 0 : 1);
        if (kinds != 1)
            throw Problem("each injection rule sets exactly one of 'header', 'query' or 'basic'");

        if (rule.Header is { } header)
        {
            if (!HeaderToken().IsMatch(header))
                throw Problem($"'{header}' isn't a valid header name");
            if (ForbiddenHeaders.Contains(header, StringComparer.OrdinalIgnoreCase)
                || header.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("Postern-", StringComparison.OrdinalIgnoreCase))
                throw Problem($"can't set the '{header}' header");
        }
        if (rule.Query is { } query && !QueryToken().IsMatch(query))
            throw Problem($"'{query}' isn't a valid query parameter name (letters, digits, '.', '_', '-', '~')");
        if (rule.Basic is null && string.IsNullOrEmpty(rule.Value))
            throw Problem($"the '{rule.Header ?? rule.Query}' rule needs a value");

        foreach (var template in rule.Templates)
        {
            var literal = Injection.Expand(template, Fields.ToDictionary(f => f.Name, _ => "x"));
            if (literal.Length > 0 && CheckSecret(literal) is { } problem)
                throw Problem($"a value template {problem}");
        }
    }

    ArgumentException Problem(string detail) => new($"Connection '{Name}': {detail}.");

    /// <summary>One line for the UI, such as "Authorization header" or "Basic auth (email, token)".</summary>
    public string AuthSummary => string.Join(", ", Inject.Select(rule => rule switch
    {
        { Basic: not null } => "Basic auth",
        { Query: { } q } => $"?{q}= query parameter",
        { Header: { } h } => $"{h} header",
        _ => "?",
    }));

    /// <summary>
    /// Secrets go into an HTTP header, so they must be printable ASCII. Catches the classic paste accidents: a BOM from
    /// a PowerShell pipe, a trailing newline, smart quotes. Returns null when fine, else what's wrong.
    /// </summary>
    public static string? CheckSecret(string secret)
    {
        if (secret.Length == 0)
            return "is empty";
        var bad = secret.FirstOrDefault(c => c is < (char)0x20 or > (char)0x7E);
        return bad == default ? null : $"contains a non-printable or non-ASCII character (U+{(int)bad:X4}) at position {secret.IndexOf(bad) + 1}";
    }

    /// <summary>Scheme, host and port: where a credential is allowed to go.</summary>
    public string Origin => BaseUrl.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    /// <summary>
    /// What's stored in Credential Manager: the field values plus the origin they were entered for. The proxy only
    /// sends a credential to that origin, so repointing a connection (in the app, or by editing providers.json while
    /// Postern isn't running) can't carry a secret to another host; it has to be entered again.
    /// </summary>
    public string EncodeCredential(IReadOnlyDictionary<string, string> values)
    {
        foreach (var field in Fields)
        {
            if (!values.TryGetValue(field.Name, out var value))
                throw new ArgumentException($"{field.Label} is missing.");
            if (CheckSecret(value) is { } problem)
                throw new ArgumentException($"{field.Label} {problem}.");
        }
        return JsonSerializer.Serialize(new StoredCredential(Origin, Fields.ToDictionary(f => f.Name, f => values[f.Name])));
    }

    // Before origins were recorded the only connection anyone had was github, with a bare token, entered for api.github.com.
    const string LegacyOrigin = "https://api.github.com";

    /// <summary>The field values in a stored credential, or null with what's wrong (entered for another host, or before the fields changed).</summary>
    public IReadOnlyDictionary<string, string>? DecodeCredential(string stored, out string? problem)
    {
        problem = null;
        StoredCredential? credential = null;
        if (stored.StartsWith('{'))
        {
            try
            {
                credential = JsonSerializer.Deserialize<StoredCredential>(stored);
            }
            catch (JsonException)
            {
            }
        }
        var legacy = credential is null;
        credential ??= Fields.Count == 1 ? new StoredCredential(LegacyOrigin, new() { [Fields[0].Name] = stored }) : null;

        if (credential?.Values is not { } values || credential.Origin is null)
        {
            problem = "isn't in a form Postern can read";
            return null;
        }
        if (!string.Equals(credential.Origin, Origin, StringComparison.OrdinalIgnoreCase))
        {
            problem = legacy
                ? "was stored before Postern recorded which host each credential is for"
                : $"was entered for {credential.Origin}, and this connection now points at {Origin}";
            return null;
        }

        foreach (var field in Fields)
        {
            if (!values.TryGetValue(field.Name, out var value))
            {
                problem = $"has no {field.Label}";
                return null;
            }
            if (CheckSecret(value) is { } bad)
            {
                problem = $"{field.Label} {bad}";
                return null;
            }
        }
        return values;
    }

    /// <summary>The headers and query parameters to add to a request, with the credential's values filled in.</summary>
    public IReadOnlyList<Injected> Apply(IReadOnlyDictionary<string, string> values) =>
    [
        .. Inject.Select(rule => rule switch
        {
            { Basic: { } basic } => new Injected(false, "Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                Injection.Expand(basic.Username, values) + (basic.Password is null ? "" : ":" + Injection.Expand(basic.Password, values))))),
            { Query: { } query } => new Injected(true, query, Injection.Expand(rule.Value!, values)),
            _ => new Injected(false, rule.Header!, Injection.Expand(rule.Value!, values)),
        }),
    ];

    sealed record StoredCredential(
        [property: JsonPropertyName("origin")] string? Origin,
        [property: JsonPropertyName("values")] Dictionary<string, string>? Values);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$")]
    private static partial Regex FieldName();

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,64}$")]
    private static partial Regex HeaderToken();

    [GeneratedRegex("^[A-Za-z0-9._~-]{1,64}$")]
    private static partial Regex QueryToken();
}
