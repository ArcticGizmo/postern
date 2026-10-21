using System.Diagnostics;
using System.Globalization;
using System.Net;
using Spike.Shared;
using Yarp.ReverseProxy.Forwarder;

namespace Spike.Broker;

/// <summary>Request pipeline for /{provider}/{**rest}: identify caller → find provider → read secret → forward with auth.</summary>
internal sealed class Forwarding(BrokerOptions options, IHttpForwarder forwarder, RateLimits rateLimits, ILogger<Forwarding> log)
{
    // One upstream client for every provider. No redirects (never carry auth to another host), no cookies, no
    // decompression (bodies go back untouched), and no trace headers leaking to the provider.
    readonly HttpMessageInvoker _upstream = new(new SocketsHttpHandler
    {
        UseProxy = true,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        EnableMultipleHttp2Connections = true,
        ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
    });

    readonly ForwarderRequestConfig _requestConfig = new() { ActivityTimeout = TimeSpan.FromSeconds(100) };

    public async Task Handle(HttpContext context, string provider)
    {
        var started = Stopwatch.GetTimestamp();

        if (!options.Providers.TryGetValue(provider, out var target))
        {
            await PosternErrors.Write(context, 404, "unknown-provider", $"No provider named '{provider}'.");
            return;
        }

        Caller caller;
        try
        {
            caller = CallerIdentity.Get(context);
        }
        catch (Exception ex)
        {
            await PosternErrors.Write(context, 403, "caller-unidentified", ex.Message);
            return;
        }

        string? secret;
        try
        {
            secret = CredentialStore.Read(target.CredentialTarget);
        }
        catch (Exception ex)
        {
            await PosternErrors.Write(context, 503, "credential-store-unavailable", ex.Message);
            return;
        }

        if (string.IsNullOrEmpty(secret))
        {
            await PosternErrors.Write(context, 503, "credential-missing",
                $"No credential for '{target.Name}'. Run: Spike.Broker.exe set-credential {target.Name}");
            return;
        }

        context.Request.Path.StartsWithSegments("/" + provider, out var upstreamPath);
        var transformer = new InjectingTransformer(target, secret, upstreamPath, rateLimits);
        var error = await forwarder.SendAsync(context, target.BaseUrl, _upstream, _requestConfig, transformer);

        if (error != ForwarderError.None && !context.Response.HasStarted)
            await PosternErrors.Write(context, 502, "upstream-" + error.ToString().ToLowerInvariant(),
                context.Features.Get<IForwarderErrorFeature>()?.Exception?.Message ?? error.ToString());

        // The spike's stand-in for the audit log. Never log headers: the injected Authorization lives there.
        log.LogInformation("{Method} {Provider}{Path} -> {Status} in {Ms:F0} ms (caller pid {Pid} {Exe}, signature {Signature})",
            context.Request.Method, provider, upstreamPath, context.Response.StatusCode,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, caller.ProcessId, caller.ExePath, caller.SignatureStatus);
    }

    sealed class InjectingTransformer(Provider provider, string secret, PathString upstreamPath, RateLimits rateLimits) : HttpTransformer
    {
        // Anything that would let the client influence auth, or tell the provider about the local machine.
        static readonly string[] Stripped =
        [
            "Authorization", "Proxy-Authorization", "Cookie",
            "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Prefix", "X-Real-IP",
        ];

        long _sentAt;

        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            foreach (var name in Stripped)
                proxyRequest.Headers.Remove(name);
            foreach (var name in proxyRequest.Headers.Select(h => h.Key).Where(IsPosternHeader).ToList())
                proxyRequest.Headers.Remove(name);

            proxyRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
            if (proxyRequest.Headers.UserAgent.Count == 0)
                proxyRequest.Headers.TryAddWithoutValidation("User-Agent", "Postern-Spike/0.1"); // GitHub rejects requests without one

            proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(destinationPrefix, upstreamPath, httpContext.Request.QueryString);
            proxyRequest.Headers.Host = null;
            _sentAt = Stopwatch.GetTimestamp();
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
        {
            var result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            var headers = httpContext.Response.Headers;
            headers["Postern-Upstream-Ms"] = Stopwatch.GetElapsedTime(_sentAt).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);

            if (proxyResponse is not null && rateLimits.Observe(provider.Name, proxyResponse) is { } limit)
            {
                headers["Postern-RateLimit-Resource"] = limit.Resource;
                headers["Postern-RateLimit-Remaining"] = limit.Remaining?.ToString(CultureInfo.InvariantCulture);
                headers["Postern-RateLimit-Limit"] = limit.Limit?.ToString(CultureInfo.InvariantCulture);
                headers["Postern-RateLimit-Reset"] = limit.ResetsAt?.ToString("O");
            }
            return result;
        }

        static bool IsPosternHeader(string name) => name.StartsWith("Postern-", StringComparison.OrdinalIgnoreCase);
    }
}
