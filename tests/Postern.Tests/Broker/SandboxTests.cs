using System.Net;
using Postern.Broker;
using Postern.Broker.Credentials;
using Postern.Client;
using Postern.Tests.Support;

namespace Postern.Tests.Broker;

/// <summary>Postern (Sandbox)'s fence: loopback only, made-up credentials only.</summary>
public class SandboxTests : IAsyncLifetime
{
    BrokerHarness _h = null!;

    public async Task InitializeAsync() => _h = await BrokerHarness.StartAsync(loopbackOnly: true);
    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task LoopbackConnectionWorksAsUsual()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        using var http = client.CreateHttpClient("fake");
        using var response = await http.GetAsync("echo");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer " + BrokerHarness.Secret, _h.Upstream.Last.Headers["Authorization"]);
    }

    [Fact]
    public async Task ConnectionOffTheMachineIsRefused()
    {
        using var client = await _h.RegisteredClient("GET outside/user");
        using var http = client.CreateHttpClient("outside");
        using var response = await http.GetAsync("user");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.GetPosternErrorAsync();
        Assert.Equal("sandbox-host", error?.Code);
        Assert.Contains("https://outside.example", error!.Detail);
        Assert.Equal("sandbox-host", _h.Audit.Recent(1)[0].Outcome);
    }

    [Fact]
    public void SavingAConnectionOffTheMachineIsRefused()
    {
        var before = _h.Broker.Connections.All.Select(c => c.Name).ToList();
        var json = $$"""
            { "providers": [
                { "name": "github", "template": "github", "baseUrl": "{{_h.Upstream.BaseUrl}}github" },
                { "name": "jira", "template": "jira", "baseUrl": "https://contoso.atlassian.net" }
            ] }
            """;

        var error = Assert.Throws<ArgumentException>(() => _h.Broker.Connections.Save(json));
        Assert.Contains("'jira' points at https://contoso.atlassian.net", error.Message);
        Assert.Equal(before, _h.Broker.Connections.All.Select(c => c.Name));
    }

    [Fact]
    public void DroppingTheGitHubOverrideBringsBackTheRealOneSoItIsRefused()
    {
        var json = $$"""{ "providers": [ { "name": "fake", "baseUrl": "{{_h.Upstream.BaseUrl}}" } ] }""";
        var error = Assert.Throws<ArgumentException>(() => _h.Broker.Connections.Save(json));
        Assert.Contains("'github' points at https://api.github.com", error.Message);
    }

    [Fact]
    public void LoopbackConnectionsSave()
    {
        _h.Broker.Connections.Save($$"""
            { "providers": [
                { "name": "github", "template": "github", "baseUrl": "{{_h.Upstream.BaseUrl}}github" },
                { "name": "local", "baseUrl": "http://localhost:5599/x" }
            ] }
            """);
        Assert.NotNull(_h.Broker.Connections.Find("local"));
    }

    public static TheoryData<string, string> RealShapes => new()
    {
        { "ghp_" + new string('a', 36), "GitHub token" },
        { "github_pat_" + new string('B', 40), "GitHub token" },
        { "glpat-" + new string('c', 20), "GitLab token" },
        { "xoxb-1234-5678-" + new string('d', 20), "Slack token" },
        { "sk-ant-api03-" + new string('e', 40), "Anthropic key" },
        { "sk-proj-" + new string('f', 40), "OpenAI key" },
        { "ATATT3x" + new string('g', 40), "Atlassian API token" },
        { "API-" + new string('H', 26), "Octopus API key" },
    };

    [Theory]
    [MemberData(nameof(RealShapes))]
    public void RealLookingCredentialIsRefused(string secret, string kind)
    {
        var inner = new InMemoryCredentialStore();
        var store = new SandboxCredentialStore(inner);
        var stored = """{"origin":"http://127.0.0.1:5599","values":{"token":"__"}}""".Replace("__", secret);

        var error = Assert.Throws<ArgumentException>(() => store.Write("postern:github:default", stored));
        Assert.Contains($"a real {kind}", error.Message);
        Assert.Null(inner.Read("postern:github:default"));
    }

    [Theory]
    [InlineData("sandbox-not-a-secret-github-token")]
    [InlineData("sk-test")]
    [InlineData("ghp_short")]
    [InlineData("task-123456789012345678901234567890")]
    [InlineData("whatever")]
    public void MadeUpCredentialIsStored(string secret)
    {
        var store = new SandboxCredentialStore(new InMemoryCredentialStore());
        store.Write("postern:github:default", secret);
        Assert.Equal(secret, store.Read("postern:github:default"));
    }

    [Fact]
    public void SeedGivesEveryConnectionButOneAFakeCredentialOnLoopback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "postern-tests-" + Guid.NewGuid().ToString("N")[..8]);
        var credentials = new SandboxCredentialStore(new InMemoryCredentialStore());
        try
        {
            Assert.True(Sandbox.Seed(dir, credentials));

            var connections = BrokerOptions.LoadProviders(dir);
            Assert.Equal(Sandbox.SeedConnections.Select(e => e.Name).Order(), connections.Select(c => c.Name).Order());
            foreach (var connection in connections)
            {
                Assert.True(connection.BaseUrl.IsLoopback, connection.Name);
                var stored = credentials.Read(connection.CredentialTarget);
                if (connection.Name == Sandbox.EmptyConnection)
                {
                    Assert.Null(stored);
                    continue;
                }
                var values = connection.DecodeCredential(stored!, out var problem);
                Assert.True(values is not null, $"{connection.Name}: {problem}");
                Assert.All(values!.Values, v => Assert.StartsWith("sandbox-not-a-secret-", v));
            }

            Assert.False(Sandbox.Seed(dir, credentials)); // already a sandbox: left as it is
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
