using System.Text;
using Postern.Core.Providers;

namespace Postern.Tests.Core;

public class ProviderTests
{
    static Dictionary<string, string> Values(params (string Name, string Value)[] values) => values.ToDictionary(v => v.Name, v => v.Value);

    static ProviderDefinition Template(string id, params (string Name, string Value)[] parameters) =>
        ProviderTemplates.Find(id)!.Connect(id, Values(parameters));

    static string Basic(string text) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void EveryTemplateMakesAValidConnection()
    {
        foreach (var template in ProviderTemplates.All)
        {
            var parameters = template.Parameters.ToDictionary(p => p.Name, p => p.Default ?? "example");
            var connection = template.Connect("x", parameters);
            connection.Validate();
            Assert.Equal(Uri.UriSchemeHttps, connection.BaseUrl.Scheme);
            Assert.Equal(template.Id, connection.Template);
        }
        Assert.Equal(ProviderTemplates.All.Count, ProviderTemplates.All.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void TemplatesInjectWhatEachServiceExpects()
    {
        Assert.Equal([new Injected(false, "Authorization", "Bearer t")], Template("github").Apply(Values(("token", "t"))));
        Assert.Equal([new Injected(false, "Authorization", Basic("me@example.com:t"))],
            Template("jira", ("site", "contoso")).Apply(Values(("email", "me@example.com"), ("token", "t"))));
        Assert.Equal([new Injected(false, "Authorization", Basic(":pat"))], Template("azure-devops", ("organization", "contoso")).Apply(Values(("token", "pat"))));
        Assert.Equal([new Injected(false, "Authorization", "k")], Template("linear").Apply(Values(("key", "k"))));
        Assert.Equal(
            [new Injected(false, "x-api-key", "k"), new Injected(false, "anthropic-version", "2023-06-01")],
            Template("anthropic").Apply(Values(("key", "k"))));
        Assert.Equal(
            [new Injected(false, "DD-API-KEY", "a"), new Injected(false, "DD-APPLICATION-KEY", "b")],
            Template("datadog").Apply(Values(("apikey", "a"), ("appkey", "b"))));
    }

    [Fact]
    public void UrlParametersFillInAndStayOneName()
    {
        Assert.Equal("https://contoso.atlassian.net/", Template("jira", ("site", "contoso")).BaseUrl.ToString());
        Assert.Equal("https://gitlab.com/api/v4", Template("gitlab").BaseUrl.ToString());
        Assert.Equal("https://git.example.com:8443/api/v4", Template("gitlab", ("host", "git.example.com:8443")).BaseUrl.ToString());

        var jira = ProviderTemplates.Find("jira")!;
        Assert.Throws<ArgumentException>(() => jira.ResolveUrl());
        Assert.Throws<ArgumentException>(() => jira.ResolveUrl(Values(("site", "evil.example/x?"))));
        Assert.Throws<ArgumentException>(() => jira.ResolveUrl(Values(("site", "a b"))));
        Assert.Throws<ArgumentException>(() => jira.ResolveUrl(Values(("site", "user@evil.example"))));
    }

    [Fact]
    public void ShorthandStylesStillWork()
    {
        var octopus = new ProviderDefinition("octopus", new Uri("https://octo.example.com"), AuthStyle.Header, "X-Octopus-ApiKey");
        octopus.Validate();
        Assert.Equal([new Injected(false, "X-Octopus-ApiKey", "k")], octopus.Apply(Values(("secret", "k"))));
        var basic = new ProviderDefinition("jira", new Uri("https://x.atlassian.net"), AuthStyle.Basic);
        Assert.Equal([new Injected(false, "Authorization", Basic("me:t"))], basic.Apply(Values(("secret", "me:t"))));
        Assert.Throws<ArgumentException>(() => new ProviderDefinition("o", new Uri("https://o.example.com"), AuthStyle.Header).Validate());
    }

    public static TheoryData<string, CredentialField[], Injection[], string> Invalid => new()
    {
        { "https://x.example.com", [new("token", "T")], [Injection.ToHeader("Authorization", "Bearer {tokn}")], "isn't one of its fields" },
        { "https://x.example.com", [new("token", "T"), new("other", "O")], [Injection.ToHeader("Authorization", "{token}")], "never injected" },
        { "https://x.example.com", [new("token", "T")], [Injection.ToHeader("Host", "{token}")], "can't set" },
        { "https://x.example.com", [new("token", "T")], [Injection.ToHeader("Content-Type", "{token}")], "can't set" },
        { "https://x.example.com", [new("token", "T")], [Injection.ToHeader("Postern-App-Token", "{token}")], "can't set" },
        { "https://x.example.com", [new("token", "T")], [Injection.ToHeader("Bad Header", "{token}")], "valid header name" },
        { "https://x.example.com", [new("token", "T")], [Injection.ToQuery("a&b", "{token}")], "valid query parameter" },
        { "https://x.example.com", [new("u", "U"), new("p", "P")], [Injection.AsBasic("{u}", "{p}"), Injection.ToHeader("authorization", "x")], "same header twice" },
        { "https://x.example.com", [new("token", "T")], [new Injection { Header = "A", Query = "b", Value = "{token}" }], "exactly one" },
        { "https://x.example.com", [new("token", "T")], [Injection.ToHeader("X-Key", "{token}"), Injection.ToHeader("X-Note", "naïve")], "non-ASCII" },
        { "http://x.example.com", [new("token", "T")], [Injection.ToHeader("X-Key", "{token}")], "https" },
        { "https://me:pw@x.example.com", [new("token", "T")], [Injection.ToHeader("X-Key", "{token}")], "can't carry a user" },
        { "https://x.example.com/?key=1", [new("token", "T")], [Injection.ToHeader("X-Key", "{token}")], "can't carry a user" },
        { "https://x.example.com", [new("Token", "T")], [Injection.ToHeader("X-Key", "{token}")], "field name" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void ValidationExplainsWhatsWrong(string url, CredentialField[] fields, Injection[] inject, string expected)
    {
        var error = Assert.Throws<ArgumentException>(() => new ProviderDefinition("x", new Uri(url), fields, inject).Validate());
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void CredentialsAreBoundToTheOriginTheyWereEnteredFor()
    {
        var jira = Template("jira", ("site", "contoso"));
        var stored = jira.EncodeCredential(Values(("email", "me@example.com"), ("token", "t")));
        Assert.Equal("t", jira.DecodeCredential(stored, out _)!["token"]);

        var moved = Template("jira", ("site", "elsewhere"));
        Assert.Null(moved.DecodeCredential(stored, out var problem));
        Assert.Contains("https://contoso.atlassian.net", problem);
        Assert.Contains("https://elsewhere.atlassian.net", problem);
    }

    [Fact]
    public void ABareSecretIsOnlyTrustedForApiGitHub()
    {
        Assert.Equal("pat", ProviderDefinition.GitHub.DecodeCredential("pat", out _)!["token"]);
        Assert.Null(Template("github-enterprise", ("host", "github.example.com")).DecodeCredential("pat", out var problem));
        Assert.Contains("before Postern recorded", problem);
        Assert.Null(Template("datadog").DecodeCredential("not-json", out _));
    }

    [Fact]
    public void ChangedFieldsMeanEnteringItAgain()
    {
        var one = new ProviderDefinition("x", new Uri("https://x.example.com"), [new("key", "Key")], [Injection.ToHeader("X-Key", "{key}")]);
        var two = new ProviderDefinition("x", new Uri("https://x.example.com"), [new("key", "Key"), new("app", "App")],
            [Injection.ToHeader("X-Key", "{key}"), Injection.ToHeader("X-App", "{app}")]);
        Assert.Null(two.DecodeCredential(one.EncodeCredential(Values(("key", "k"))), out var problem));
        Assert.Contains("App", problem);
        Assert.Throws<ArgumentException>(() => two.EncodeCredential(Values(("key", "k"))));
        Assert.Throws<ArgumentException>(() => one.EncodeCredential(Values(("key", "bad\nvalue"))));
    }
}
