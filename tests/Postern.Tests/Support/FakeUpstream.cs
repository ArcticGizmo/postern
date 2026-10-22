using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Postern.Tests.Support;

internal sealed record RecordedRequest(string Method, string PathAndQuery, Dictionary<string, string> Headers);

/// <summary>A provider stand-in on loopback TCP that records exactly what the broker sent it.</summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    readonly WebApplication _app;

    public Uri BaseUrl { get; }
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
    public RecordedRequest Last => Requests.Last();

    FakeUpstream(WebApplication app, Uri baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<FakeUpstream> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        FakeUpstream? self = null;

        app.Use(async (context, next) =>
        {
            self!.Requests.Enqueue(new(
                context.Request.Method,
                context.Request.Path + context.Request.QueryString,
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            await next();
        });

        app.Map("/echo", () => "ok");
        app.MapGet("/redirect", () => Results.Redirect("http://elsewhere.example/landing"));
        app.MapGet("/etag", (HttpContext context) =>
        {
            context.Response.Headers.ETag = "\"v1\"";
            return context.Request.Headers.IfNoneMatch == "\"v1\"" ? Results.StatusCode(304) : Results.Text("body-v1");
        });
        app.MapGet("/ratelimit", (HttpContext context) =>
        {
            var headers = context.Response.Headers;
            headers["x-ratelimit-limit"] = "5000";
            headers["x-ratelimit-remaining"] = "4321";
            headers["x-ratelimit-used"] = "679";
            headers["x-ratelimit-reset"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString();
            headers["x-ratelimit-resource"] = "core";
            return "ok";
        });
        app.MapGet("/repos/{owner}/{repo}/pulls", () => Results.Text("[]", "application/json"));

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FakeUpstream(app, new Uri(address));
        return self;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
