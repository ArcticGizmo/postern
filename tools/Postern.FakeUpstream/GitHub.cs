using System.Security.Cryptography;
using System.Text;

namespace Postern.FakeUpstream;

/// <summary>GitHub's shapes, mapped at the root (for smoke.ps1 and the consumer spikes) and again under /github.</summary>
public static class GitHub
{
    static readonly string[] Titles =
    [
        "Retry the pipe on ERROR_PIPE_BUSY", "Sandbox profile", "Seed connections", "Loopback-only fence",
        "Refuse real-looking tokens", "fake-upstream knobs", "Sallyport window", "Soak strip", "Crash drill",
        "Testbed page", "Headless run-all", "Scenario files", "Docs",
    ];

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/echo", (HttpContext c) => Results.Json(Services.Echo(c)));

        routes.MapGet("/user", () => Results.Json(new { login = "fake", id = 1001, type = "User" }));

        routes.MapPost("/graphql", () => Results.Json(new { data = new { viewer = new { login = "fake" }, rateLimit = new { remaining = 4999, cost = 1 } } }));

        routes.MapGet("/redirect", () => Results.Redirect("https://elsewhere.example/landing"));

        // Like GitHub: archives redirect to a pre-signed URL on another host.
        routes.MapGet("/repos/{owner}/{repo}/tarball", (string owner, string repo) => Results.Redirect($"https://codeload.example/{owner}/{repo}/tar.gz/main?token=presigned"));

        // The first PR changes every 20 s, so its ETag does too; If-None-Match gets a 304 in between. With per_page,
        // the list is paged like GitHub's, with a Link header of absolute upstream URLs.
        routes.MapGet("/repos/{owner}/{repo}/pulls", (HttpContext c, string owner, string repo, int? per_page, int? page) =>
        {
            var generation = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 20;
            var all = Titles.Select((title, i) => new { number = 40 - i, title, state = "open" }).ToList();
            all.Insert(0, new { number = (int)(generation % 1000), title = $"Generation {generation}", state = "open" });

            var size = Math.Clamp(per_page ?? all.Count, 1, 100);
            var current = Math.Max(page ?? 1, 1);
            var last = (all.Count + size - 1) / size;
            var etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}/{repo}/{generation}/{size}/{current}")))[..16] + "\"";
            c.Response.Headers.ETag = etag;
            if (c.Request.Headers.IfNoneMatch == etag)
                return Results.StatusCode(304);

            if (per_page is not null)
            {
                var self = $"{c.Request.Scheme}://{c.Request.Host}{c.Request.Path}?per_page={size}&page=";
                var links = new List<string>();
                if (current < last)
                    links.Add($"<{self}{current + 1}>; rel=\"next\"");
                links.Add($"<{self}{last}>; rel=\"last\"");
                c.Response.Headers.Link = string.Join(", ", links);
            }
            if (per_page is null && page is null)
                return Results.Json(all.Take(1)); // the original single-PR shape the poller spike counts on
            return Results.Json(all.Skip((current - 1) * size).Take(size));
        });

        // A big body with emoji, right-to-left text and characters outside the BMP. ?kb= sets the size (default 5 MB).
        routes.MapGet("/repos/{owner}/{repo}/issues/{number:int}", (HttpContext c, int number, int? kb) =>
        {
            var body = Payloads.UnicodeIssue(number, Math.Clamp(kb ?? 5120, 1, 50 * 1024));
            return Payloads.Send(c, body, "application/json; charset=utf-8");
        });

        routes.MapDelete("/repos/{owner}/{repo}/labels/{name}", () => Results.NoContent());
    }
}
