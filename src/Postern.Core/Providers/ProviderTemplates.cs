using System.Text.RegularExpressions;

namespace Postern.Core.Providers;

/// <summary>A value that fills a <c>{placeholder}</c> in a template's base URL: a host, an organisation, a site.</summary>
public sealed record UrlParameter(string Name, string Label, string? Default = null, string? Example = null);

/// <summary>A known service: everything about a connection except its name, URL parameters and credential.</summary>
public sealed partial record ProviderTemplate(
    string Id,
    string DisplayName,
    string BaseUrl,
    IReadOnlyList<UrlParameter> Parameters,
    IReadOnlyList<CredentialField> Fields,
    IReadOnlyList<Injection> Inject,
    string Hint)
{
    /// <summary>
    /// The base URL with its parameters filled in. A parameter is one host-like token, so it can't add a path, a port
    /// trick or a second host. Throws <see cref="ArgumentException"/> when one is missing or isn't that shape.
    /// </summary>
    public Uri ResolveUrl(IReadOnlyDictionary<string, string>? values = null) => new(UrlPlaceholder().Replace(BaseUrl, m =>
    {
        var parameter = Parameters.First(p => p.Name == m.Groups[1].Value);
        var value = (values?.GetValueOrDefault(parameter.Name) is { Length: > 0 } given ? given : parameter.Default)?.Trim();
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"{parameter.Label} is required.");
        if (!ParameterValue().IsMatch(value))
            throw new ArgumentException($"{parameter.Label} '{value}' should be a single name like {parameter.Example ?? parameter.Default ?? "example"}, with no slashes or spaces.");
        return value;
    }));

    public ProviderDefinition Connect(string name, IReadOnlyDictionary<string, string>? parameters = null) =>
        new(name, ResolveUrl(parameters), Fields, Inject, Id);

    [GeneratedRegex(@"\{([a-z]+)\}")]
    private static partial Regex UrlPlaceholder();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?(:[0-9]{1,5})?$")]
    private static partial Regex ParameterValue();
}

/// <summary>The services Postern knows out of the box. Anything else is a custom connection written as JSON.</summary>
public static class ProviderTemplates
{
    static CredentialField Field(string name, string label, bool secret = true) => new(name, label, secret);
    static Injection Bearer(string field) => Injection.ToHeader("Authorization", $"Bearer {{{field}}}");
    static UrlParameter Host(string? fallback = null, string? example = null) => new("host", "Server host", fallback, example);

    public static IReadOnlyList<ProviderTemplate> All { get; } =
    [
        new("github", "GitHub", "https://api.github.com", [],
            [Field("token", "Personal access token")], [Bearer("token")],
            "A fine-grained personal access token: github.com → Settings → Developer settings → Fine-grained tokens."),
        new("github-enterprise", "GitHub Enterprise Server", "https://{host}/api/v3", [Host(example: "github.example.com")],
            [Field("token", "Personal access token")], [Bearer("token")],
            "A personal access token from your server's Settings → Developer settings."),
        new("gitlab", "GitLab", "https://{host}/api/v4", [Host("gitlab.com", "gitlab.example.com")],
            [Field("token", "Personal access token")], [Bearer("token")],
            "A personal, project or group access token. Leave the host as gitlab.com unless you're self-hosted."),
        new("bitbucket", "Bitbucket Cloud", "https://api.bitbucket.org/2.0", [],
            [Field("token", "Access token")], [Bearer("token")],
            "A repository, project or workspace access token."),
        new("azure-devops", "Azure DevOps", "https://dev.azure.com/{organization}", [new("organization", "Organization", Example: "contoso")],
            [Field("token", "Personal access token")], [Injection.AsBasic("", "{token}")],
            "A personal access token from User settings → Personal access tokens. Sent as Basic auth with an empty user."),
        new("jira", "Jira Cloud", "https://{site}.atlassian.net", [new("site", "Site", Example: "contoso (for contoso.atlassian.net)")],
            [Field("email", "Account email", secret: false), Field("token", "API token")], [Injection.AsBasic("{email}", "{token}")],
            "An API token from id.atlassian.com → Security. Scoped tokens only work through api.atlassian.com; add those as a custom connection."),
        new("confluence", "Confluence Cloud", "https://{site}.atlassian.net/wiki", [new("site", "Site", Example: "contoso (for contoso.atlassian.net)")],
            [Field("email", "Account email", secret: false), Field("token", "API token")], [Injection.AsBasic("{email}", "{token}")],
            "An API token from id.atlassian.com → Security."),
        new("octopus", "Octopus Deploy", "https://{host}", [Host(example: "octopus.example.com")],
            [Field("apikey", "API key")], [Injection.ToHeader("X-Octopus-ApiKey", "{apikey}")],
            "An API key from your profile → My API keys. Octopus keys can't be narrowed, so keep app scopes tight."),
        new("linear", "Linear", "https://api.linear.app", [],
            [Field("key", "Personal API key")], [Injection.ToHeader("Authorization", "{key}")],
            "A personal API key from Settings → Security & access. Linear takes it without a Bearer prefix."),
        new("sentry", "Sentry", "https://{host}/api/0", [Host("sentry.io", "sentry.example.com")],
            [Field("token", "Auth token")], [Bearer("token")],
            "A user auth token or an internal integration token."),
        new("slack", "Slack", "https://slack.com/api", [],
            [Field("token", "Bot or user token")], [Bearer("token")],
            "An xoxb- or xoxp- token from your Slack app's OAuth & Permissions page."),
        new("openai", "OpenAI", "https://api.openai.com/v1", [],
            [Field("key", "API key")], [Bearer("key")],
            "A project API key from platform.openai.com."),
        new("anthropic", "Anthropic", "https://api.anthropic.com", [],
            [Field("key", "API key")], [Injection.ToHeader("x-api-key", "{key}"), Injection.ToHeader("anthropic-version", "2023-06-01")],
            "An API key from console.anthropic.com. Postern also sends the anthropic-version header."),
        new("datadog", "Datadog", "https://api.{site}", [new("site", "Site", "datadoghq.com", "datadoghq.eu")],
            [Field("apikey", "API key"), Field("appkey", "Application key")],
            [Injection.ToHeader("DD-API-KEY", "{apikey}"), Injection.ToHeader("DD-APPLICATION-KEY", "{appkey}")],
            "An API key and an application key from Organization settings. Change the site for EU and other regions."),
    ];

    public static ProviderTemplate? Find(string? id) => id is null ? null : All.FirstOrDefault(t => t.Id == id);
}
