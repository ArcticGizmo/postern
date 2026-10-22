using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Postern.Broker.Pipe;
using Postern.Client;
using Postern.Core.Apps;
using Postern.Tests.Support;

namespace Postern.Tests.Broker;

/// <summary>The fail-closed table from docs/spike-plan.md, end to end: real pipe, real client, fake upstream.</summary>
public class ProxyTests : IAsyncLifetime
{
    BrokerHarness _h = null!;

    public async Task InitializeAsync() => _h = await BrokerHarness.StartAsync();
    public async Task DisposeAsync() => await _h.DisposeAsync();

    static async Task<string?> Code(HttpResponseMessage r) => (await r.GetPosternErrorAsync())?.Code;

    [Fact]
    public async Task RegisteredAppCallsThroughWithInjectedAuth()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        using var http = client.CreateHttpClient("fake");
        using var response = await http.GetAsync("echo?x=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/echo?x=1", _h.Upstream.Last.PathAndQuery);
        Assert.Equal("Bearer " + BrokerHarness.Secret, _h.Upstream.Last.Headers["Authorization"]);
        Assert.False(_h.Upstream.Last.Headers.ContainsKey(PosternClient.TokenHeader));
        Assert.StartsWith("Postern/", _h.Upstream.Last.Headers["User-Agent"]);
    }

    [Fact]
    public async Task ApprovalPromptShowsWhatWasAsked()
    {
        using var client = await _h.RegisteredClient("GET fake/echo", "POST fake/graphql");
        var asked = Assert.Single(_h.Prompt.Asked);
        Assert.Equal("test-app", asked.Name);
        Assert.Equal(Environment.ProcessPath, asked.ExePath, ignoreCase: true);
        Assert.Equal((uint)Environment.ProcessId, asked.ProcessId);
        Assert.Equal(["GET fake/echo", "POST fake/graphql"], asked.Scopes.Select(s => s.ToString()));
    }

    [Fact]
    public async Task SynchronousSendGoesThroughTheSameChecks()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        using var http = client.CreateHttpClient("fake");
        using var ok = http.Send(new HttpRequestMessage(HttpMethod.Get, "echo"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode); // token attached on the sync path too
        Assert.Throws<HttpRequestException>(() => http.Send(new HttpRequestMessage(HttpMethod.Get, "https://codeload.example/x")));
    }

    [Fact]
    public async Task NoTokenNeverReachesUpstream()
    {
        using var client = _h.Client();
        using var http = client.CreateHttpClient("fake");
        using var response = await http.GetAsync("echo");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("no-token", await Code(response));
        Assert.Empty(_h.Upstream.Requests);
    }

    [Fact]
    public async Task OutOfScopeNeverReachesUpstream()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        using var http = client.CreateHttpClient("fake");
        using var response = await http.PostAsync("echo", new StringContent("x"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("scope-denied", await Code(response));
        Assert.Empty(_h.Upstream.Requests);
    }

    [Fact]
    public async Task ClientCannotOverrideAuthOrLeakLocalHeaders()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        using var http = client.CreateHttpClient("fake");
        using var request = new HttpRequestMessage(HttpMethod.Get, "echo");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer client-supplied");
        request.Headers.TryAddWithoutValidation("Cookie", "session=1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "10.1.1.1");
        request.Headers.TryAddWithoutValidation("Postern-Debug", "x");
        request.Headers.TryAddWithoutValidation("X-Kept", "yes");
        using var response = await http.SendAsync(request);

        var seen = _h.Upstream.Last.Headers;
        Assert.Equal("Bearer " + BrokerHarness.Secret, seen["Authorization"]);
        Assert.False(seen.ContainsKey("Cookie"));
        Assert.False(seen.ContainsKey("X-Forwarded-For"));
        Assert.False(seen.ContainsKey("Postern-Debug"));
        Assert.False(seen.ContainsKey("traceparent"));
        Assert.Equal("yes", seen["X-Kept"]);
    }

    [Fact]
    public async Task MissingCredentialFailsClosed()
    {
        using var client = await _h.RegisteredClient("GET nocred/echo");
        using var http = client.CreateHttpClient("nocred");
        using var response = await http.GetAsync("echo");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("credential-missing", await Code(response));
        Assert.Empty(_h.Upstream.Requests);
    }

    [Fact]
    public async Task UnreadableCredentialStoreFailsClosed()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        using var http = client.CreateHttpClient("fake");
        _h.Credentials.FailReads = true;
        using var response = await http.GetAsync("echo");
        Assert.Equal("credential-store-unavailable", await Code(response));
        Assert.Empty(_h.Upstream.Requests);
    }

    [Fact]
    public async Task NonAsciiCredentialIsRefusedNotSent()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        _h.Credentials.Write("postern:fake:default", "﻿secret-with-bom");
        using var http = client.CreateHttpClient("fake");
        using var response = await http.GetAsync("echo");
        Assert.Equal("credential-invalid", await Code(response));
        Assert.Empty(_h.Upstream.Requests);
    }

    [Fact]
    public async Task UnreachableUpstreamIsAPosternError()
    {
        using var client = await _h.RegisteredClient("GET dead/x");
        using var http = client.CreateHttpClient("dead");
        using var response = await http.GetAsync("x");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.StartsWith("upstream-", await Code(response));
    }

    [Fact]
    public async Task UnknownProvider()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        using var http = client.CreateHttpClient("nope");
        using var response = await http.GetAsync("echo");
        Assert.Equal("unknown-provider", await Code(response));
    }

    [Fact]
    public async Task RedirectIsPassedBackNotFollowed()
    {
        using var client = await _h.RegisteredClient("GET fake/redirect");
        using var http = client.CreateHttpClient("fake");
        using var response = await http.GetAsync("redirect");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("elsewhere.example", response.Headers.Location!.Host);
        Assert.Single(_h.Upstream.Requests);
    }

    [Fact]
    public async Task ConditionalRequestPassesThrough()
    {
        using var client = await _h.RegisteredClient("GET fake/etag");
        using var http = client.CreateHttpClient("fake");
        using var first = await http.GetAsync("etag");
        using var request = new HttpRequestMessage(HttpMethod.Get, "etag");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var second = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal("\"v1\"", _h.Upstream.Last.Headers["If-None-Match"]);
    }

    [Fact]
    public async Task RevokeTakesEffectOnTheNextRequest()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        using var http = client.CreateHttpClient("fake");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("echo")).StatusCode);

        _h.Broker.Registrations.Revoke(_h.Registrations.All().Single().Id);

        using var response = await http.GetAsync("echo");
        Assert.Equal("app-revoked", await Code(response));
        Assert.Single(_h.Upstream.Requests);
    }

    [Fact]
    public async Task ReRegisteringSupersedesTheOldToken()
    {
        using var old = await _h.RegisteredClient("GET fake/echo");
        using var replacement = await _h.RegisteredClient("GET fake/echo");
        using var oldHttp = old.CreateHttpClient("fake");
        using var newHttp = replacement.CreateHttpClient("fake");

        var error = await (await oldHttp.GetAsync("echo")).GetPosternErrorAsync();
        Assert.Equal("token-superseded", error!.Code);
        Assert.True(error.NeedsRegistration);
        Assert.Equal(HttpStatusCode.OK, (await newHttp.GetAsync("echo")).StatusCode);
    }

    [Fact]
    public async Task TokenOnlyWorksFromTheRegisteredExe()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        var registration = _h.Registrations.All().Single();
        _h.Registrations.Save(registration with { ExePath = @"C:\Somewhere\else.exe" });

        using var http = client.CreateHttpClient("fake");
        using var response = await http.GetAsync("echo");
        Assert.Equal("caller-mismatch", await Code(response));
    }

    [Fact]
    public async Task DeniedRegistrationGetsNoToken()
    {
        _h.Prompt.Decide = _ => false;
        using var client = _h.Client();
        var result = await client.RegisterAsync(["GET fake/echo"], TimeSpan.FromSeconds(10));
        Assert.Equal(RegistrationOutcome.Denied, result.Outcome);
        Assert.False(client.HasToken);
    }

    [Fact]
    public async Task BadScopeIsRejectedAtRegistration()
    {
        using var client = _h.Client();
        var result = await client.RegisterAsync(["GET fake/pull*"], TimeSpan.FromSeconds(10));
        Assert.Equal(RegistrationOutcome.Denied, result.Outcome);
        Assert.Contains("bad-scope", result.Detail);
        Assert.Empty(_h.Prompt.Asked);
    }

    [Fact]
    public async Task AmbiguousPathIsRejected()
    {
        using var client = await _h.RegisteredClient("GET fake/**");
        using var http = client.CreateHttpClient("fake");
        using var response = await http.GetAsync("a%2Fb/echo");
        Assert.Equal("bad-path", await Code(response));
        Assert.Empty(_h.Upstream.Requests);
    }

    [Theory]
    [InlineData("http://elsewhere.example/landing")]
    [InlineData("https://codeload.example/o/r/tar.gz/main")]
    [InlineData("https://postern/fake/echo")]
    public async Task ClientRefusesRequestsNotForPostern(string url)
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        using var http = new HttpClient(client.CreateHandler());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url));
        Assert.Contains("only carries requests to http://postern/", ex.Message);
        Assert.Empty(_h.Upstream.Requests);
    }

    [Fact]
    public async Task BrokerRefusesRequestsForAnotherHost()
    {
        // A client without PosternHandler's check (Node, a hand-rolled one): the broker's Host check is the backstop.
        using var client = await _h.RegisteredClient("GET fake/echo");
        var options = new PosternOptions { AppName = "raw", PipeName = _h.Pipe, ExpectedServerExe = Environment.ProcessPath };
        using var http = new HttpClient(Postern.Client.PipeConnector.CreateHandler(options));
        using var response = await http.GetAsync("http://elsewhere.example/landing");
        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
        Assert.Equal("misdirected-host", await Code(response));
    }

    [Fact]
    public async Task RateLimitsAreSurfacedAndScopedToTheApp()
    {
        using var client = await _h.RegisteredClient("GET fake/ratelimit");
        using var http = client.CreateHttpClient("fake");
        using var response = await http.GetAsync("ratelimit");
        Assert.Equal("4321", response.Headers.GetValues("Postern-RateLimit-Remaining").Single());
        Assert.Equal("core", response.Headers.GetValues("Postern-RateLimit-Resource").Single());

        using var control = new HttpClient(client.CreateHandler()) { BaseAddress = new Uri("http://postern/") };
        var limits = await control.GetFromJsonAsync<JsonElement>("_postern/ratelimits");
        Assert.Equal("fake", limits[0].GetProperty("provider").GetString());
    }

    [Fact]
    public async Task StatusShowsTheApp()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        var status = await client.GetStatusAsync();
        Assert.Equal("test-app", status.App!.Name);
        Assert.Equal("approved", status.App.Status);
        Assert.Contains("fake", status.Providers);
    }

    [Fact]
    public async Task EveryDecisionIsAudited()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        using var http = client.CreateHttpClient("fake");
        await http.GetAsync("echo?secret=1");
        await http.PostAsync("echo", new StringContent("x"));

        var entries = _h.Audit.Recent(10);
        var allowed = entries.Single(e => e.Outcome == "allowed");
        Assert.Equal("/echo", allowed.Path);
        Assert.Equal("?secret=1".Length, allowed.QueryLength);
        Assert.Equal("test-app", allowed.AppName);
        Assert.NotNull(allowed.UpstreamMs);
        Assert.Contains(entries, e => e.Outcome == "scope-denied" && e.Method == "POST");
        Assert.Contains(entries, e => e.Outcome == "token-issued");
    }

    [Fact]
    public async Task ClientSurvivesABrokerRestart()
    {
        using var client = await _h.RegisteredClient("GET fake/echo");
        using var http = client.CreateHttpClient("fake");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("echo")).StatusCode);

        var restart = _h.RestartAsync(TimeSpan.FromMilliseconds(600));
        await Task.Delay(100);
        using var during = await http.GetAsync("echo"); // first attempts hit a dead pipe; the handler retries
        await restart;
        Assert.Equal(HttpStatusCode.OK, during.StatusCode);
    }

    [Fact]
    public async Task ClientCanWaitForPosternToStart()
    {
        using var client = _h.Client();
        var restart = _h.RestartAsync(TimeSpan.FromSeconds(1.5));
        var down = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (client.IsAvailable && DateTimeOffset.UtcNow < down)
            await Task.Delay(20);
        Assert.False(client.IsAvailable);

        Assert.True(await client.WaitUntilAvailableAsync(TimeSpan.FromSeconds(10)));
        await restart;
        Assert.Equal(_h.Pipe, (await client.GetStatusAsync()).Pipe);
    }

    [Fact]
    public async Task WaitingForPosternGivesUpAtTheTimeout()
    {
        using var client = new PosternClient(new PosternOptions
        {
            AppName = "test-app",
            PipeName = "postern-test-nobody-" + Guid.NewGuid().ToString("N")[..8],
            TokenStore = new InMemoryTokenStore(),
        });
        Assert.False(await client.WaitUntilAvailableAsync(TimeSpan.FromMilliseconds(600)));
    }

    [Fact]
    public async Task ClientRefusesAnUnexpectedServer()
    {
        using var client = _h.Client(expectedServerExe: @"C:\Program Files\Postern\Postern.exe");
        using var http = client.CreateHttpClient("fake");
        await Assert.ThrowsAsync<PosternVerificationException>(() => http.GetAsync("echo"));
        Assert.Empty(_h.Upstream.Requests);
    }

    [Fact]
    public async Task SecondBrokerOnTheSamePipeRefusesToStart()
    {
        await using var second = Postern.Broker.BrokerRuntime.Create(
            new Postern.Broker.BrokerOptions { PipeName = _h.Pipe, PublishDiscovery = false }, new DenyAllPrompt(),
            new Postern.Broker.BrokerOverrides { Credentials = _h.Credentials, Registrations = new InMemoryRegistrationStore(), Audit = new Postern.Core.Audit.InMemoryAuditLog() });
        await Assert.ThrowsAsync<PipeInUseException>(() => second.StartAsync());
    }

    [Fact]
    public void RemoteClientsAreRejected()
    {
        // \\localhost\pipe goes through SMB loopback: refused by PIPE_REJECT_REMOTE_CLIENTS (S1).
        using var stream = new NamedPipeClientStream("localhost", _h.Pipe, PipeDirection.InOut);
        var ex = Record.Exception(() => stream.Connect(3000));
        Assert.True(ex is UnauthorizedAccessException or TimeoutException, $"expected refusal, got {ex?.GetType().Name ?? "a connection"}");
    }
}
