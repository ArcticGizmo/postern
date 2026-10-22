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
/// Providers: fake (has a credential), nocred (none), dead (nothing listening).
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

    BrokerHarness(FakeUpstream upstream, string pipe, ScriptedPrompt prompt, InMemoryCredentialStore credentials,
        InMemoryRegistrationStore registrations, InMemoryAuditLog audit)
    {
        Upstream = upstream;
        Pipe = pipe;
        Prompt = prompt;
        Credentials = credentials;
        Registrations = registrations;
        Audit = audit;
        Broker = CreateBroker();
    }

    public static async Task<BrokerHarness> StartAsync()
    {
        var upstream = await FakeUpstream.StartAsync();
        var credentials = new InMemoryCredentialStore();
        credentials.Write("postern:fake:default", Secret);
        credentials.Write("postern:dead:default", Secret);
        var harness = new BrokerHarness(upstream, "postern-test-" + Guid.NewGuid().ToString("N")[..8], new ScriptedPrompt(),
            credentials, new InMemoryRegistrationStore(), new InMemoryAuditLog());
        await harness.Broker.StartAsync();
        return harness;
    }

    BrokerRuntime CreateBroker() => BrokerRuntime.Create(
        new BrokerOptions
        {
            PipeName = Pipe,
            PublishDiscovery = false,
            ApprovalTimeout = TimeSpan.FromSeconds(5),
            Providers =
            [
                new ProviderDefinition("fake", Upstream.BaseUrl),
                new ProviderDefinition("nocred", Upstream.BaseUrl),
                new ProviderDefinition("dead", new Uri("http://127.0.0.1:9")),
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
    }
}
