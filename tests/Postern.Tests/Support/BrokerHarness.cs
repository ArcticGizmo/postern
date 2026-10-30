using Postern.Broker;
using Postern.Broker.Credentials;
using Postern.Client;
using Postern.Core.Apps;
using Postern.Core.Audit;
using Postern.Core.Providers;

namespace Postern.Tests.Support;

/// <summary>Answers approval prompts with a rule (default: approve everything) and records what was asked.</summary>
internal sealed class ScriptedPrompt : IApprovalPrompt
{
    public Func<ApprovalRequest, bool> Decide { get; set; } = _ => true;
    public List<ApprovalRequest> Asked { get; } = [];

    public Task<bool> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        lock (Asked) Asked.Add(request);
        return Task.FromResult(Decide(request));
    }
}

/// <summary>
/// A real broker on a throwaway pipe, forwarding to a local fake upstream, with in-memory stores. The test process is
/// both the broker and the client, so the "expected server exe" is this test host.
/// Providers: fake (has a credential), nocred (none), dead (nothing listening), outside (a credential, and not loopback).
/// </summary>
internal sealed class BrokerHarness : IAsyncDisposable
{
    public const string Secret = "fake-secret-123";

    public FakeUpstream Upstream { get; }
    public BrokerRuntime Broker { get; private set; }
    public ScriptedPrompt Prompt { get; }
    public InMemoryCredentialStore Credentials { get; }
    public InMemoryRegistrationStore Registrations { get; }
    public InMemoryAuditLog Audit { get; }
    public string Pipe { get; }

    readonly bool _loopbackOnly;

    BrokerHarness(FakeUpstream upstream, string pipe, ScriptedPrompt prompt, InMemoryCredentialStore credentials,
        InMemoryRegistrationStore registrations, InMemoryAuditLog audit, bool loopbackOnly)
    {
        Upstream = upstream;
        Pipe = pipe;
        Prompt = prompt;
        Credentials = credentials;
        Registrations = registrations;
        Audit = audit;
        _loopbackOnly = loopbackOnly;
        Broker = CreateBroker();
    }

    /// <summary>Its own, so tests that save connections never touch the real providers.json.</summary>
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "postern-tests-" + Guid.NewGuid().ToString("N"));

    /// <param name="loopbackOnly">The sandbox's fence (<see cref="BrokerOptions.LoopbackOnly"/>).</param>
    public static async Task<BrokerHarness> StartAsync(bool loopbackOnly = false)
    {
        var upstream = await FakeUpstream.StartAsync();
        var credentials = new InMemoryCredentialStore();
        var harness = new BrokerHarness(upstream, "postern-test-" + Guid.NewGuid().ToString("N")[..8], new ScriptedPrompt(),
            credentials, new InMemoryRegistrationStore(), new InMemoryAuditLog(), loopbackOnly);
        harness.SetSecret("fake", Secret);
        harness.SetSecret("dead", Secret);
        harness.SetSecret("outside", Secret);
        await harness.Broker.StartAsync();
        return harness;
    }

    /// <summary>Stores a credential the way the app does, bound to the connection's current origin.</summary>
    public void SetSecret(string connection, params (string Field, string Value)[] values)
    {
        var definition = Broker.Connections.Find(connection)!;
        Credentials.Write(definition.CredentialTarget, definition.EncodeCredential(values.ToDictionary(v => v.Field, v => v.Value)));
    }

    public void SetSecret(string connection, string secret) => SetSecret(connection, (Broker.Connections.Find(connection)!.Fields[0].Name, secret));

    BrokerRuntime CreateBroker() => BrokerRuntime.Create(
        new BrokerOptions
        {
            PipeName = Pipe,
            PublishDiscovery = false,
            ApprovalTimeout = TimeSpan.FromSeconds(5),
            DataDirectory = DataDirectory,
            LoopbackOnly = _loopbackOnly,
            Providers =
            [
                new ProviderDefinition("fake", Upstream.BaseUrl),
                new ProviderDefinition("nocred", Upstream.BaseUrl),
                new ProviderDefinition("dead", new Uri("http://127.0.0.1:9")),
                new ProviderDefinition("outside", new Uri("https://outside.example")),
            ],
        },
        Prompt,
        new BrokerOverrides { Credentials = Credentials, Registrations = Registrations, Audit = Audit });

    /// <summary>Stops the broker and starts a fresh one on the same pipe and stores, as a crash + restart would.</summary>
    public async Task RestartAsync(TimeSpan downtime)
    {
        await Broker.DisposeAsync();
        await Task.Delay(downtime);
        Broker = CreateBroker();
        await Broker.StartAsync();
    }

    public PosternClient Client(string appName = "test-app", string? expectedServerExe = null) => new(new PosternOptions
    {
        AppName = appName,
        PipeName = Pipe,
        ExpectedServerExe = expectedServerExe ?? Environment.ProcessPath,
        TokenStore = new InMemoryTokenStore(),
    });

    /// <summary>A registered, approved client with the given scopes.</summary>
    public async Task<PosternClient> RegisteredClient(params string[] scopes)
    {
        var client = Client();
        var result = await client.RegisterAsync(scopes, TimeSpan.FromSeconds(10));
        Assert.Equal(RegistrationOutcome.Approved, result.Outcome);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await Broker.DisposeAsync();
        await Upstream.DisposeAsync();
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
