using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Postern.Broker.Credentials;
using Postern.Core.Apps;
using Postern.Core.Audit;
using Postern.Core.Providers;
using Postern.Core.RateLimiting;
using Yarp.ReverseProxy.Forwarder;

namespace Postern.Broker.Http;

/// <summary>
/// /{provider}/{**path}: provider → policy (token, caller, scope) → secret → forward with auth injected.
/// Anything short of an explicit allow returns a Postern error and never reaches the provider.
/// </summary>
internal sealed class ProxyEndpoint(
    Connections connections,
    IRegistrationStore registrations,
    ICredentialStore credentials,
    RateLimits rateLimits,
    IAuditLog audit,
    IHttpForwarder forwarder,
    TimeProvider time,
    ILogger<ProxyEndpoint> log)
{
    public const string TokenHeader = "Postern-App-Token";

    // One upstream client for all providers. No redirects (never carry auth to another host), no cookies, no
    // decompression (bodies go back untouched), no trace headers leaking to the provider. System proxy honoured.
    readonly HttpMessageInvoker _upstream = new(new SocketsHttpHandler
    {
        UseProxy = true,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        EnableMultipleHttp2Connections = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5), // pick up DNS changes
        ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
    });

    readonly ForwarderRequestConfig _requestConfig = new() { ActivityTimeout = TimeSpan.FromSeconds(100) };

    public async Task Handle(HttpContext context, string provider)
    {
        var caller = BrokerMiddleware.Caller(context);
        context.Request.Path.StartsWithSegments("/" + provider, out var rest);
        var path = rest.HasValue ? rest.Value! : "/";
        var audited = new AuditDraft(caller, context.Request.Method, provider, path, context.Request.QueryString.Value?.Length ?? 0);

        if (connections.Find(provider) is not { } definition)
        {
            await Deny(context, audited, null, 404, "unknown-provider", $"No provider named '{provider}'.");
            return;
        }

        var decision = AccessPolicy.Evaluate(registrations, context.Request.Headers[TokenHeader], caller, context.Request.Method, provider, path);
        if (!decision.Allowed)
        {
            await Deny(context, audited, decision.App, decision.Status, decision.Code, decision.Detail);
            return;
        }

        string? stored;
        try
        {
            stored = credentials.Read(definition.CredentialTarget);
        }
        catch (Exception ex)
        {
            await Deny(context, audited, decision.App, 503, "credential-store-unavailable", ex.Message);
            return;
        }
        if (string.IsNullOrEmpty(stored))
        {
            await Deny(context, audited, decision.App, 503, "credential-missing", $"No credential for '{provider}'. Add one in Postern (click the tray icon → Connections).");
            return;
        }
        if (definition.DecodeCredential(stored, out var problem) is not { } values)
        {
            await Deny(context, audited, decision.App, 503, "credential-invalid", $"The stored credential for '{provider}' {problem}. Set it again in Postern.");
            return;
        }

        var transformer = new InjectingTransformer(definition.Apply(values), rest, provider, rateLimits);
        var error = await forwarder.SendAsync(context, definition.BaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/'), _upstream, _requestConfig, transformer);
        if (error != ForwarderError.None && !context.Response.HasStarted)
        {
            var detail = context.Features.Get<IForwarderErrorFeature>()?.Exception?.Message ?? error.ToString();
            await PosternErrors.Write(context, 502, "upstream-" + error.ToString().ToLowerInvariant(), detail);
        }

        Record(audited, decision.App, context.Response.StatusCode, error == ForwarderError.None ? "allowed" : "upstream-" + error.ToString().ToLowerInvariant(), transformer.UpstreamMs);
    }

    async Task Deny(HttpContext context, AuditDraft audited, AppRegistration? app, int status, string code, string detail)
    {
        await PosternErrors.Write(context, status, code, detail);
        Record(audited, app, status, code, null);
        log.LogInformation("Denied {Method} {Provider}{Path} for pid {Pid} ({Exe}): {Code}", audited.Method, audited.Provider, audited.Path, audited.Caller.ProcessId, audited.Caller.ExePath, code);
    }

    void Record(AuditDraft d, AppRegistration? app, int status, string outcome, double? upstreamMs) =>
        audit.Write(new AuditEntry(time.GetUtcNow(), app?.Id, app?.Name, d.Caller.ProcessId, d.Caller.ExePath, d.Method, d.Provider, d.Path, d.QueryLength, status, outcome, upstreamMs));

    sealed record AuditDraft(CallerInfo Caller, string Method, string Provider, string Path, int QueryLength);

    sealed class InjectingTransformer(IReadOnlyList<Injected> injected, PathString upstreamPath, string provider, RateLimits rateLimits) : HttpTransformer
    {
        // Anything that would let the client influence auth, or tell the provider about the local machine.
        static readonly string[] Stripped =
        [
            "Authorization", "Proxy-Authorization", "Cookie",
            "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Prefix", "X-Real-IP",
        ];

        long _sentAt;

        public double? UpstreamMs { get; private set; }

        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            foreach (var name in Stripped)
                proxyRequest.Headers.Remove(name);
            foreach (var name in proxyRequest.Headers.Select(h => h.Key).Where(k => k.StartsWith("Postern-", StringComparison.OrdinalIgnoreCase)).ToList())
                proxyRequest.Headers.Remove(name);

            foreach (var header in injected.Where(i => !i.IsQuery))
            {
                proxyRequest.Headers.Remove(header.Name); // a client-sent X-Octopus-ApiKey (say) must not survive either
                proxyRequest.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
            if (proxyRequest.Headers.UserAgent.Count == 0)
                proxyRequest.Headers.TryAddWithoutValidation("User-Agent", "Postern/" + BrokerRuntime.Version); // GitHub rejects requests without one

            proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(destinationPrefix, upstreamPath, Query(httpContext.Request.QueryString));
            proxyRequest.Headers.Host = null;
            _sentAt = Stopwatch.GetTimestamp();
        }

        // The client's query goes through untouched unless the connection injects into it; then a client-sent value
        // for an injected name is dropped, so the app can't choose the key.
        QueryString Query(QueryString client)
        {
            var parameters = injected.Where(i => i.IsQuery).ToList();
            if (parameters.Count == 0)
                return client;
            var kept = QueryHelpers.ParseQuery(client.Value)
                .Where(p => !parameters.Any(i => string.Equals(i.Name, p.Key, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(p => p.Value.Select(v => new KeyValuePair<string, string?>(p.Key, v)));
            return QueryString.Create(kept.Concat(parameters.Select(i => new KeyValuePair<string, string?>(i.Name, i.Value))));
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
        {
            var result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            UpstreamMs = Stopwatch.GetElapsedTime(_sentAt).TotalMilliseconds;
            var headers = httpContext.Response.Headers;
            headers["Postern-Upstream-Ms"] = UpstreamMs.Value.ToString("F1", CultureInfo.InvariantCulture);

            if (proxyResponse is not null
                && rateLimits.Observe(provider, name => proxyResponse.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null) is { } limit)
            {
                headers["Postern-RateLimit-Resource"] = limit.Resource;
                headers["Postern-RateLimit-Remaining"] = limit.Remaining?.ToString(CultureInfo.InvariantCulture);
                headers["Postern-RateLimit-Limit"] = limit.Limit?.ToString(CultureInfo.InvariantCulture);
                headers["Postern-RateLimit-Reset"] = limit.ResetsAt?.ToString("O");
            }
            return result;
        }
    }
}
