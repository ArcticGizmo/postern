using System.Net;
using System.Security.Cryptography;
using System.Text;

// fake-upstream [port]   (default 5599; loopback only)
//
//   GET  /echo                       → JSON of the method, path and headers Postern sent (Authorization masked)
//   GET  /repos/{o}/{r}/pulls        → a PR list whose ETag changes every 20 s, 304 on If-None-Match; rate-limit headers
//   POST /graphql                    → {"data":{"viewer":{"login":"fake"}}}
//   GET  /redirect                   → 302 to another host
//   GET  /user                       → {"login":"fake"}

var port = args.Length > 0 ? int.Parse(args[0]) : 5599;
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
var app = builder.Build();
var remaining = 5000;

app.Use(async (context, next) =>
{
    var budget = context.Response.StatusCode == 304 ? remaining : Interlocked.Decrement(ref remaining);
    context.Response.Headers["x-ratelimit-limit"] = "5000";
    context.Response.Headers["x-ratelimit-remaining"] = budget.ToString();
    context.Response.Headers["x-ratelimit-used"] = (5000 - budget).ToString();
    context.Response.Headers["x-ratelimit-reset"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString();
    context.Response.Headers["x-ratelimit-resource"] = context.Request.Path.StartsWithSegments("/graphql") ? "graphql" : "core";
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {context.Request.Method} {context.Request.Path}{context.Request.QueryString} auth={Mask(context.Request.Headers.Authorization)}");
    await next();
});

app.MapGet("/echo", (HttpContext c) => Results.Json(new
{
    method = c.Request.Method,
    path = c.Request.Path.Value,
    query = c.Request.QueryString.Value,
    headers = c.Request.Headers.ToDictionary(h => h.Key, h => h.Key == "Authorization" ? Mask(h.Value) : h.Value.ToString()),
}));

app.MapGet("/user", () => Results.Json(new { login = "fake" }));

app.MapPost("/graphql", () => Results.Json(new { data = new { viewer = new { login = "fake" } } }));

app.MapGet("/redirect", () => Results.Redirect("https://elsewhere.example/landing"));

// Like GitHub: archives redirect to a pre-signed URL on another host.
app.MapGet("/repos/{owner}/{repo}/tarball", (string owner, string repo) => Results.Redirect($"https://codeload.example/{owner}/{repo}/tar.gz/main?token=presigned"));

app.MapGet("/repos/{owner}/{repo}/pulls", (HttpContext c, string owner, string repo) =>
{
    var generation = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 20;
    var etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}/{repo}/{generation}")))[..16] + "\"";
    c.Response.Headers.ETag = etag;
    if (c.Request.Headers.IfNoneMatch == etag)
    {
        Interlocked.Increment(ref remaining); // a 304 is free, like GitHub
        return Results.StatusCode(304);
    }
    return Results.Text($$"""[{"number":{{generation % 1000}},"title":"Generation {{generation}}","state":"open"}]""", "application/json");
});

Console.WriteLine($"fake-upstream on http://127.0.0.1:{port}");
app.Run();

static string Mask(string? value) => value switch
{
    null or "" => "<none>",
    { Length: > 12 } => value[..10] + "…",
    _ => value,
};
