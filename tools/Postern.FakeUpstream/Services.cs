using System.Security.Cryptography;
using System.Text.Json;

namespace Postern.FakeUpstream;

/// <summary>Jira, Slack, Octopus and Anthropic shapes, and what each expects of the credential Postern injects.</summary>
public static class Services
{
    static readonly string[] Prefixed = ["github", "jira", "slack", "octopus", "anthropic"];

    /// <summary>The service a path belongs to: its first segment when that's one of the prefixes, else "github" (the root).</summary>
    public static string Of(PathString path)
    {
        var first = path.Value?.TrimStart('/').Split('/', 2)[0] ?? "";
        return Prefixed.Contains(first) ? first : "github";
    }

    /// <summary>What's wrong with the credential on a prefixed request, or null. The root takes anything, as it always has.</summary>
    public static string? CheckCredential(string service, HttpRequest request)
    {
        if (!request.Path.StartsWithSegments("/" + service))
            return null;
        var authorization = request.Headers.Authorization.ToString();
        return service switch
        {
            "github" or "slack" when !authorization.StartsWith("Bearer ", StringComparison.Ordinal) => "expected Authorization: Bearer",
            "jira" when !authorization.StartsWith("Basic ", StringComparison.Ordinal) => "expected Authorization: Basic",
            "octopus" when string.IsNullOrEmpty(request.Headers["X-Octopus-ApiKey"]) => "expected X-Octopus-ApiKey",
            "anthropic" when string.IsNullOrEmpty(request.Headers["x-api-key"]) => "expected x-api-key",
            "anthropic" when string.IsNullOrEmpty(request.Headers["anthropic-version"]) => "expected anthropic-version",
            _ => null,
        };
    }

    public static object Echo(HttpContext c) => new
    {
        method = c.Request.Method,
        path = c.Request.Path.Value,
        query = c.Request.QueryString.Value,
        headers = c.Request.Headers.ToDictionary(h => h.Key, h => Masking.IsCredential(h.Key) ? Masking.Mask(h.Value) : h.Value.ToString()),
    };

    public static void MapJira(RouteGroupBuilder jira)
    {
        jira.MapGet("/echo", (HttpContext c) => Results.Json(Echo(c)));

        jira.MapGet("/rest/api/3/myself", () => Results.Json(new { accountId = "sandbox-0001", displayName = "Sandbox User", emailAddress = "sandbox@example.invalid" }));

        // Jira refuses uploads without the XSRF opt-out header, so a client that forgets it learns here.
        jira.MapPost("/rest/api/3/issue/{key}/attachments", async (HttpContext c, string key) =>
        {
            if (c.Request.Headers["X-Atlassian-Token"] != "no-check")
                return Results.Json(new { message = "XSRF check failed: send X-Atlassian-Token: no-check" }, statusCode: 403);
            if (!c.Request.HasFormContentType)
                return Results.Json(new { message = "expected multipart/form-data" }, statusCode: 415);
            var form = await c.Request.ReadFormAsync(c.RequestAborted);
            var files = new List<object>();
            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, c.RequestAborted));
                files.Add(new { id = (10000 + files.Count + 1).ToString(), issue = key, filename = file.FileName, size = file.Length, sha256 = sha });
            }
            return files.Count == 0 ? Results.Json(new { message = "no file part" }, statusCode: 400) : Results.Json(files);
        });
    }

    public static void MapSlack(RouteGroupBuilder slack)
    {
        slack.MapGet("/echo", (HttpContext c) => Results.Json(Echo(c)));

        slack.MapPost("/auth.test", () => Results.Json(new { ok = true, user = "sandbox", team = "Sandbox" }));

        // Slack's Web API answers 200 with ok:false for bad input, never a 4xx.
        slack.MapPost("/chat.postMessage", async (HttpContext c) =>
        {
            if (!c.Request.HasFormContentType || c.Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.Ordinal) != true)
                return Results.Json(new { ok = false, error = "invalid_form_data" });
            var form = await c.Request.ReadFormAsync(c.RequestAborted);
            var channel = form["channel"].ToString();
            if (channel.Length == 0)
                return Results.Json(new { ok = false, error = "channel_not_found" });
            var ts = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.000100";
            return Results.Json(new { ok = true, channel, ts, message = new { text = form["text"].ToString(), ts } });
        });
    }

    public static void MapOctopus(RouteGroupBuilder octopus)
    {
        octopus.MapGet("/echo", (HttpContext c) => Results.Json(Echo(c)));

        octopus.MapGet("/api/projects", () => Results.Json(new[] { new { Id = "Projects-1", Name = "Sandbox", Slug = "sandbox" } }));

        // ?kb= sets the size; the default is about 2.4 MB.
        octopus.MapGet("/api/packages/{id}/raw", (HttpContext c, string id, int? kb) =>
            Payloads.Send(c, Payloads.Binary(id, Math.Clamp(kb ?? 2458, 1, 50 * 1024)), "application/octet-stream"));
    }

    public static void MapAnthropic(RouteGroupBuilder anthropic)
    {
        anthropic.MapGet("/echo", (HttpContext c) => Results.Json(Echo(c)));

        // With "stream": true, four events a quarter of a second apart, each stamped with when it was written, so a
        // client can tell whether something between here and it held them back.
        anthropic.MapPost("/v1/messages", async (HttpContext c) =>
        {
            JsonElement request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<JsonElement>(c.Request.Body, cancellationToken: c.RequestAborted);
            }
            catch (JsonException)
            {
                c.Response.StatusCode = 400;
                await c.Response.WriteAsJsonAsync(new { type = "error", error = new { type = "invalid_request_error", message = "body must be JSON" } });
                return;
            }

            var stream = request.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
            if (!stream)
            {
                await c.Response.WriteAsJsonAsync(new
                {
                    id = "msg_sandbox",
                    type = "message",
                    role = "assistant",
                    content = new[] { new { type = "text", text = "pong" } },
                    stop_reason = "end_turn",
                });
                return;
            }

            c.Response.ContentType = "text/event-stream";
            c.Response.Headers.CacheControl = "no-cache";
            string[] events = ["message_start", "content_block_delta", "content_block_delta", "message_stop"];
            for (var i = 0; i < events.Length; i++)
            {
                if (i > 0)
                    await Task.Delay(250, c.RequestAborted);
                var data = JsonSerializer.Serialize(new { type = events[i], index = i, sent_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                await c.Response.WriteAsync($"event: {events[i]}\ndata: {data}\n\n", c.RequestAborted);
                await c.Response.Body.FlushAsync(c.RequestAborted);
            }
        });
    }
}
