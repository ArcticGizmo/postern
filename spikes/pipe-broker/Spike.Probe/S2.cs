using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Spike.Shared;

namespace Spike.Probe;

/// <summary>S2 against a local fake upstream: injection, header hygiene, redirects, conditional requests, fail-closed.</summary>
internal static class S2
{
    const string Secret = "spike-secret-0123456789";

    public static async Task Run(Report report)
    {
        report.Section("S2 forwarding (fake upstream)");

        await using var upstream = await FakeUpstream.StartAsync();
        var run = Guid.NewGuid().ToString("N")[..6];
        var target = $"postern:spiketest-{run}:default";
        CredentialStore.Write(target, "postern", Secret);
        report.Info("fake upstream", $"{upstream.BaseUrl}; test credential written to {target}");

        try
        {
            using var broker = await BrokerProcess.StartAsync(BrokerProcess.NewPipeName(),
                "--provider", $"fake={upstream.BaseUrl}|{target}",
                "--provider", $"nocred={upstream.BaseUrl}|postern:spiketest-{run}-absent:default",
                "--provider", $"dead=http://127.0.0.1:9|{target}");
            using var http = PipeHttp.Create(broker.Pipe, PipeHttp.BrokerExe);

            await report.Run("auth injection", async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "fake/echo?x=1");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer client-supplied");
                request.Headers.TryAddWithoutValidation("Cookie", "session=abc");
                request.Headers.TryAddWithoutValidation("Postern-App-Token", "app-token");
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", "10.0.0.1");
                request.Headers.TryAddWithoutValidation("X-Custom", "kept");
                using var response = await http.SendAsync(request);
                var seen = upstream.Requests.Last();

                report.Check(response.StatusCode == HttpStatusCode.OK && seen.PathAndQuery == "/echo?x=1", "path and query rewritten", $"upstream saw {seen.Method} {seen.PathAndQuery}");
                report.Check(seen.Headers.GetValueOrDefault("Authorization") == "Bearer " + Secret, "injected auth replaces client auth", "client's Authorization dropped");
                var leaked = new[] { "Cookie", "Postern-App-Token", "X-Forwarded-For", "traceparent", "Forwarded" }.Where(seen.Headers.ContainsKey).ToList();
                report.Check(leaked.Count == 0, "sensitive/local headers stripped", leaked.Count == 0 ? "no Cookie, Postern-*, X-Forwarded-*, traceparent" : "leaked: " + string.Join(", ", leaked));
                report.Check(seen.Headers.GetValueOrDefault("X-Custom") == "kept", "ordinary headers pass through");
                report.Check(seen.Headers.GetValueOrDefault("Host") == new Uri(upstream.BaseUrl).Authority, "Host is the provider's", seen.Headers.GetValueOrDefault("Host") ?? "");
                report.Info("User-Agent added when the client sent none", seen.Headers.GetValueOrDefault("User-Agent") ?? "<none>");
            });

            await report.Run("missing credential", async () =>
            {
                var before = upstream.Requests.Count;
                using var response = await http.GetAsync("nocred/echo");
                report.Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && Code(response) == "credential-missing" && upstream.Requests.Count == before,
                    "missing credential fails closed", $"{(int)response.StatusCode} {Code(response)}, upstream calls +{upstream.Requests.Count - before}");
            });

            await report.Run("unknown provider", async () =>
            {
                using var response = await http.GetAsync("nope/echo");
                report.Check(response.StatusCode == HttpStatusCode.NotFound && Code(response) == "unknown-provider", "unknown provider", $"{(int)response.StatusCode} {Code(response)}");
            });

            await report.Run("upstream down", async () =>
            {
                using var response = await http.GetAsync("dead/echo");
                report.Check(response.StatusCode == HttpStatusCode.BadGateway && Code(response)?.StartsWith("upstream-", StringComparison.Ordinal) == true,
                    "unreachable upstream is a Postern error", $"{(int)response.StatusCode} {Code(response)}");
            });

            await report.Run("redirect not followed", async () =>
            {
                var before = upstream.Requests.Count;
                using var response = await http.GetAsync("fake/redirect");
                report.Check(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.Host == "elsewhere.example" && upstream.Requests.Count == before + 1,
                    "broker passes 302 back instead of following it", $"{(int)response.StatusCode} -> {response.Headers.Location}");
            });

            await report.Run("redirect trap", async () =>
            {
                // A careless client with auto-redirect on: the redirected request also goes down the pipe.
                using var careless = PipeHttp.Create(broker.Pipe, PipeHttp.BrokerExe, allowAutoRedirect: true);
                using var response = await careless.GetAsync("fake/redirect");
                report.Check(response.StatusCode == HttpStatusCode.MisdirectedRequest && Code(response) == "misdirected-host",
                    "auto-followed redirect is caught by the Host check", $"{(int)response.StatusCode} {Code(response)}");
            });

            await report.Run("conditional request", async () =>
            {
                using var first = await http.GetAsync("fake/etag");
                var etag = first.Headers.ETag;
                using var request = new HttpRequestMessage(HttpMethod.Get, "fake/etag");
                request.Headers.IfNoneMatch.Add(etag!);
                using var second = await http.SendAsync(request);
                report.Check(first.StatusCode == HttpStatusCode.OK && second.StatusCode == HttpStatusCode.NotModified && upstream.Requests.Last().Headers.ContainsKey("If-None-Match"),
                    "If-None-Match forwarded and 304 returned", $"{(int)first.StatusCode} ETag {etag}, then {(int)second.StatusCode}");
            });

            await report.Run("rate-limit headers", async () =>
            {
                using var response = await http.GetAsync("fake/ratelimit");
                var remaining = Header(response, "Postern-RateLimit-Remaining");
                var all = await http.GetFromJsonAsync<JsonElement>("_postern/ratelimits");
                report.Check(remaining == "4321" && Header(response, "Postern-RateLimit-Resource") == "core", "rate-limit state surfaced",
                    $"remaining {remaining}, resource {Header(response, "Postern-RateLimit-Resource")}; /_postern/ratelimits has {all.GetArrayLength()} bucket(s)");
                report.Info("upstream timing header", $"Postern-Upstream-Ms: {Header(response, "Postern-Upstream-Ms")}");
            });
        }
        finally
        {
            CredentialStore.Delete(target);
            report.Info("cleanup", $"deleted {target}");
        }
    }

    static string? Code(HttpResponseMessage response) => Header(response, "Postern-Error");

    internal static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
