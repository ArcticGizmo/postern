using Postern.Core.Scopes;

namespace Postern.Core.Providers;

public enum AuthStyle
{
    /// <summary><c>Authorization: Bearer &lt;secret&gt;</c> (GitHub, Graph).</summary>
    Bearer,
    /// <summary>A named header carrying the raw secret (Octopus: <c>X-Octopus-ApiKey</c>).</summary>
    Header,
    /// <summary><c>Authorization: Basic base64(secret)</c>, where the secret is stored as <c>user:token</c> (Jira).</summary>
    Basic,
}

/// <summary>A provider is a base URL plus an auth strategy. No per-endpoint wrappers, so every endpoint works.</summary>
public sealed record ProviderDefinition(string Name, Uri BaseUrl, AuthStyle Auth = AuthStyle.Bearer, string? AuthHeader = null)
{
    public string CredentialTarget => $"postern:{Name}:default";

    public static ProviderDefinition GitHub { get; } = new("github", new Uri("https://api.github.com"));

    public void Validate()
    {
        if (!ProviderName.IsValid(Name))
            throw new ArgumentException($"Provider name '{Name}' is invalid (lowercase letters, digits, '-').");
        if (BaseUrl.Scheme != Uri.UriSchemeHttps && !BaseUrl.IsLoopback)
            throw new ArgumentException($"Provider '{Name}' must use https (plain http is allowed only for loopback test upstreams).");
        if (Auth == AuthStyle.Header && string.IsNullOrWhiteSpace(AuthHeader))
            throw new ArgumentException($"Provider '{Name}' uses header auth but names no header.");
    }

    /// <summary>The header name and value to inject.</summary>
    public (string Name, string Value) AuthorizationFor(string secret) => Auth switch
    {
        AuthStyle.Bearer => ("Authorization", "Bearer " + secret),
        AuthStyle.Basic => ("Authorization", "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret))),
        AuthStyle.Header => (AuthHeader!, secret),
        _ => throw new InvalidOperationException(),
    };
}
