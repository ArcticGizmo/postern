using System.Net;
using Postern.Broker;
using Postern.Client;
using Postern.Tests.Support;

namespace Postern.Tests.Broker;

/// <summary>Connections edited while Postern runs: they apply at once, and a credential never follows a connection to another host.</summary>
public class ConnectionTests : IAsyncLifetime
{
    BrokerHarness _h = null!;

    public async Task InitializeAsync() => _h = await BrokerHarness.StartAsync();
    public async Task DisposeAsync() => await _h.DisposeAsync();

    static async Task<string?> Code(HttpResponseMessage r) => (await r.GetPosternErrorAsync())?.Code;

    string Json(string fakeUrl, string? extra = null) => $$"""
        { "providers": [
            { "name": "fake", "baseUrl": "{{fakeUrl}}" },
            { "name": "nocred", "baseUrl": "{{_h.Upstream.BaseUrl}}" },
            { "name": "dead", "baseUrl": "http://127.0.0.1:9" }{{(extra is null ? "" : ",\n" + extra)}}
        ] }
        """;

    [Fact]
    public async Task SavedCustomConnectionWorksWithoutRestart()
    {
        using var client = await _h.RegisteredClient("GET extra/echo");
        _h.Broker.Connections.Save(Json(_h.Upstream.BaseUrl.ToString(), $$"""
            { "name": "extra", "baseUrl": "{{_h.Upstream.BaseUrl}}",
              "fields": [ { "name": "key", "label": "API key" }, { "name": "app", "label": "App key" } ],
              "inject": [ { "header": "X-Api-Key", "value": "{key}" },
                          { "query": "app_key", "value": "{app}" },
                          { "header": "X-Version", "value": "7" } ] }
            """));
        _h.SetSecret("extra", ("key", "k-123"), ("app", "a-456"));

        using var http = client.CreateHttpClient("extra");
        using var request = new HttpRequestMessage(HttpMethod.Get, "echo?x=1&app_key=client-chosen");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "client-chosen");
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = _h.Upstream.Last;
        Assert.Equal("/echo?x=1&app_key=a-456", seen.PathAndQuery);
        Assert.Equal("k-123", seen.Headers["X-Api-Key"]);
        Assert.Equal("7", seen.Headers["X-Version"]);
        Assert.False(seen.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task CredentialIsNotSentToAHostItWasntEnteredFor()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        // Same upstream, but by another name: as far as the credential knows, another host.
        _h.Broker.Connections.Save(Json(_h.Upstream.BaseUrl.ToString().Replace("127.0.0.1", "localhost")));

        using var http = client.CreateHttpClient("fake");
        using var response = await http.GetAsync("echo");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("credential-invalid", await Code(response));
        Assert.Empty(_h.Upstream.Requests);

        _h.SetSecret("fake", "entered-again");
        using var retried = await http.GetAsync("echo");
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal("Bearer entered-again", _h.Upstream.Last.Headers["Authorization"]);
    }

    [Fact]
    public async Task RemovingAConnectionDeletesItsCredential()
    {
        using var client = await _h.RegisteredClient("GET dead/x");
        _h.Broker.Connections.Remove("dead");

        Assert.Null(_h.Credentials.Read("postern:dead:default"));
        using var http = client.CreateHttpClient("dead");
        using var response = await http.GetAsync("x");
        Assert.Equal("unknown-provider", await Code(response));
    }

    [Fact]
    public void InvalidJsonChangesNothing()
    {
        var before = _h.Broker.Connections.All.Select(c => c.Name).ToList();
        var error = Assert.Throws<ArgumentException>(() => _h.Broker.Connections.Save("""{ "providers": [ { "name": "x", "baseUrl": "http://example.com" } ] }"""));
        Assert.Contains("https", error.Message);
        Assert.Equal(before, _h.Broker.Connections.All.Select(c => c.Name));
        Assert.False(File.Exists(ProvidersFile.PathIn(_h.DataDirectory)));
    }

    [Fact]
    public void AddingFromATemplateKeepsWhatsThere()
    {
        _h.Broker.Connections.Save(Json(_h.Upstream.BaseUrl.ToString()));
        _h.Broker.Connections.Add(new ProviderEntry { Name = "work", Template = "github" });

        Assert.Equal(["dead", "fake", "github", "nocred", "work"], _h.Broker.Connections.All.Select(c => c.Name));
        Assert.Equal("https://api.github.com/", _h.Broker.Connections.Find("work")!.BaseUrl.ToString());
        Assert.Throws<ArgumentException>(() => _h.Broker.Connections.Add(new ProviderEntry { Name = "work", Template = "gitlab" }));
    }
}
