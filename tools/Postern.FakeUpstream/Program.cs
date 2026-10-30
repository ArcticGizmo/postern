using System.Net;
using Postern.FakeUpstream;

// fake-upstream [port]   (default 5599, which Postern (Sandbox)'s connections point at; loopback only)
//
// Stand-ins for the services a sandbox Postern talks to, each under its own prefix, plus the original GitHub shapes
// at the root for smoke.ps1 and the consumer spikes:
//
//   /github/…  and  /…   user, echo, graphql, pulls (ETag, Link paging), issues (big unicode), labels (204), redirects
//   /jira/…               myself, attachments (multipart, needs X-Atlassian-Token)
//   /slack/…              chat.postMessage (form-encoded), auth.test
//   /octopus/…            projects, packages/{id}/raw (binary)
//   /anthropic/…          v1/messages, streamed as server-sent events when asked
//
// Each prefixed service checks the credential arrives the way that service expects it, so a 401 here means Postern
// injected the wrong thing. Generated bodies carry X-Fake-Body-Sha256 to check against.
//
// Test bed controls, never recorded themselves:
//   GET /_seen[?id=X&n=20]   the requests as they arrived, newest first (credentials masked). X is X-Testbed-Id.
//   DELETE /_seen
//   GET|POST|DELETE /_knobs  faults to inject: { "service": "github" | "*", "fault": "429|500|hang|slow|drop",
//                            "seconds": 120, "count": 3 }. Without a count it stays until deleted.

var port = args.Length > 0 ? int.Parse(args[0]) : 5599;
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Loopback, port);
    k.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
});
var app = builder.Build();
var seen = new SeenLog();
var knobs = new Knobs();
var rateLimit = new RateLimit();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/_seen") || context.Request.Path.StartsWithSegments("/_knobs"))
    {
        await next();
        return;
    }

    var entry = await seen.Record(context.Request);
    var service = Services.Of(context.Request.Path);
    var fault = knobs.Take(service);
    entry.Fault = fault?.Describe();
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {context.Request.Method} {context.Request.Path}{context.Request.QueryString} auth={Masking.Mask(context.Request.Headers.Authorization)}{(fault is null ? "" : $" [{entry.Fault}]")}");

    context.Response.OnStarting(() =>
    {
        rateLimit.Stamp(context);
        entry.Status = context.Response.StatusCode;
        return Task.CompletedTask;
    });

    if (fault is not null && await fault.Apply(context))
        return;
    if (Services.CheckCredential(service, context.Request) is { } missing)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { message = $"fake-upstream: {missing}" });
        return;
    }
    await next();
});

app.MapGet("/_seen", (string? id, int? n) => Results.Json(seen.Recent(id, n ?? 20)));
app.MapDelete("/_seen", () =>
{
    seen.Clear();
    return Results.NoContent();
});
app.MapGet("/_knobs", () => Results.Json(knobs.All()));
app.MapPost("/_knobs", (Knob knob) =>
{
    if (knob.Problem() is { } problem)
        return Results.BadRequest(new { message = problem });
    knobs.Add(knob);
    return Results.Json(knobs.All());
});
app.MapDelete("/_knobs", () =>
{
    knobs.Clear();
    return Results.NoContent();
});

GitHub.Map(app);
GitHub.Map(app.MapGroup("/github"));
Services.MapJira(app.MapGroup("/jira"));
Services.MapSlack(app.MapGroup("/slack"));
Services.MapOctopus(app.MapGroup("/octopus"));
Services.MapAnthropic(app.MapGroup("/anthropic"));

Console.WriteLine($"fake-upstream on http://127.0.0.1:{port}");
app.Run();
