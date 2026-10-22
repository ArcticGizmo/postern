using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Postern.Broker.Credentials;
using Postern.Broker.Http;
using Postern.Broker.Identity;
using Postern.Broker.Pipe;
using Postern.Broker.Storage;
using Postern.Core.Apps;
using Postern.Core.Audit;
using Postern.Core.Providers;
using Postern.Core.RateLimiting;
using Yarp.ReverseProxy.Forwarder;

namespace Postern.Broker;

/// <summary>Swap-ins for tests. Anything left null gets the real (Windows, SQLite) implementation.</summary>
public sealed class BrokerOverrides
{
    public ICredentialStore? Credentials { get; init; }
    public IRegistrationStore? Registrations { get; init; }
    public IAuditLog? Audit { get; init; }
    public TimeProvider? Time { get; init; }
    public Action<ILoggingBuilder>? Logging { get; init; }
}

/// <summary>
/// The broker as an in-process service: build it, start it, and the tray (or a test) talks to its parts directly.
/// There is no admin channel over the pipe; approving, revoking and editing credentials happen in-process only.
/// </summary>
public sealed class BrokerRuntime : IAsyncDisposable
{
    public static string Version { get; } =
        typeof(BrokerRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    readonly WebApplication _app;
    readonly SqliteAuditLog? _ownedAudit;
    bool _started;

    public BrokerOptions Options { get; }
    public Registrations Registrations { get; }
    public IRegistrationStore RegistrationStore { get; }
    public IAuditLog Audit { get; }
    public RateLimits RateLimits { get; }
    public ICredentialStore Credentials { get; }
    public Connections Connections { get; }
    public IReadOnlyList<ProviderDefinition> Providers => Connections.All;
    public TimeProvider Time { get; }

    BrokerRuntime(BrokerOptions options, IApprovalPrompt prompt, BrokerOverrides overrides)
    {
        Options = options;
        Time = overrides.Time ?? TimeProvider.System;
        Credentials = overrides.Credentials ?? Profile.CredentialStore();
        Connections = new Connections(options.Providers, options.DataDirectory, Credentials, options.ProvidersLoadError);
        RateLimits = new RateLimits(Time);

        PosternDatabase? database = null;
        if (overrides.Registrations is null || overrides.Audit is null)
        {
            database = new PosternDatabase(Path.Combine(options.DataDirectory, "postern.db"));
            database.Migrate();
        }
        RegistrationStore = overrides.Registrations ?? new SqliteRegistrationStore(database!);
        Audit = overrides.Audit ?? (_ownedAudit = new SqliteAuditLog(database!, Time));
        Registrations = new Registrations(RegistrationStore, prompt, Time, options.ApprovalTimeout);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = "Postern" });
        builder.Logging.ClearProviders();
        overrides.Logging?.Invoke(builder.Logging);
        builder.WebHost.UseNamedPipes(o =>
        {
            o.CurrentUserOnly = false; // it would replace our descriptor; see PipeServerFactory
            o.CreateNamedPipeServerStream = PipeServerFactory.Create(PipeServerFactory.CurrentUserOnly());
        });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.ListenNamedPipe(options.PipeName);
        });
        builder.Services.AddHttpForwarder();
        builder.Services.AddSingleton<CallerResolver>();
        builder.Services.AddSingleton<BrokerMiddleware>();
        builder.Services.AddSingleton(Connections);
        builder.Services.AddSingleton(RegistrationStore);
        builder.Services.AddSingleton(Credentials);
        builder.Services.AddSingleton(RateLimits);
        builder.Services.AddSingleton(Audit);
        builder.Services.AddSingleton(Time);
        builder.Services.AddSingleton<ProxyEndpoint>();

        _app = builder.Build();
        _app.UseMiddleware<BrokerMiddleware>();
        ControlEndpoints.Map(_app, this);
        _app.Map("/{provider}/{**rest}", (HttpContext context, string provider, ProxyEndpoint proxy) => proxy.Handle(context, provider));
    }

    public static BrokerRuntime Create(BrokerOptions options, IApprovalPrompt prompt, BrokerOverrides? overrides = null) =>
        new(options, prompt, overrides ?? new BrokerOverrides());

    public ILogger Logger => _app.Logger;

    /// <summary>Starts listening. Throws <see cref="PipeInUseException"/> if the name is already taken (fail closed).</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _app.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (Find<PipeInUseException>(ex) is { } inUse)
        {
            throw inUse;
        }
        _started = true;
        if (Options.PublishDiscovery)
            Discovery.Publish(Options.PipeName);
        _app.Logger.LogInformation("Postern {Version} listening on \\\\.\\pipe\\{Pipe} (pid {Pid}); connections: {Providers}",
            Version, Options.PipeName, Environment.ProcessId, string.Join(", ", Providers.Select(p => $"{p.Name}={p.BaseUrl}")));
        if (Connections.LoadError is { } error)
            _app.Logger.LogError("providers.json wasn't used, so only the built-in connections are available: {Error}", error);
    }

    public async Task StopAsync()
    {
        if (!_started)
            return;
        _started = false;
        if (Options.PublishDiscovery)
            Discovery.Withdraw(Options.PipeName);
        await _app.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _app.DisposeAsync();
        if (_ownedAudit is not null)
            await _ownedAudit.DisposeAsync();
    }

    static T? Find<T>(Exception? ex) where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is T match)
                return match;
            if (ex is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions)
                    if (Find<T>(inner) is { } found)
                        return found;
        }
        return null;
    }
}
