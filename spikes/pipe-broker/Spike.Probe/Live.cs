using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Spike.Probe;

/// <summary>
/// S2 against real GitHub, with the PAT stored by "Spike.Broker.exe set-credential github". The probe never sees the
/// PAT; it only talks to the broker. Your login is masked unless --show-identity is passed, so the output is safe to paste.
/// </summary>
internal static class Live
{
    public static async Task Run(Report report, bool showIdentity, string? repo)
    {
        report.Section("S2 live GitHub");
        using var broker = await BrokerProcess.StartAsync(BrokerProcess.NewPipeName());
        using var http = PipeHttp.Create(broker.Pipe, PipeHttp.BrokerExe);

        EntityTagHeaderValue? etag = null;
        string? usedAfterFirst = null;

        await report.Run("GET /user", async () =>
        {
            var sw = Stopwatch.StartNew();
            using var response = await http.GetAsync("github/user");
            var total = sw.Elapsed.TotalMilliseconds;
            if (S2.Header(response, "Postern-Error") is { } code)
            {
                report.Fail("GET /user", $"{(int)response.StatusCode} {code}: {await response.Content.ReadAsStringAsync()}");
                return;
            }
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var login = body.TryGetProperty("login", out var l) ? l.GetString() : null;
            etag = response.Headers.ETag;
            usedAfterFirst = S2.Header(response, "x-ratelimit-used");
            report.Check(response.StatusCode == HttpStatusCode.OK && login is not null, "GET github/user through the pipe",
                $"200, authenticated as {(showIdentity ? login : Mask(login))}; {Timing(response, total)}");
            report.Info("rate limit after 200", RateLine(response));
        });

        if (etag is null && usedAfterFirst is null)
        {
            report.Info("stopping", "GET /user didn't succeed, so the rest would only repeat the failure");
            return;
        }

        await report.Run("conditional 304", async () =>
        {
            if (etag is null)
            {
                report.Info("conditional 304", "skipped: no ETag from /user");
                return;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, "github/user");
            request.Headers.IfNoneMatch.Add(etag);
            using var response = await http.SendAsync(request);
            var used = S2.Header(response, "x-ratelimit-used");
            report.Check(response.StatusCode == HttpStatusCode.NotModified, "If-None-Match returns 304 through Postern", $"{(int)response.StatusCode}");
            report.Info("304 vs rate limit", $"x-ratelimit-used after 200: {usedAfterFirst ?? "?"}, after 304: {used ?? "(not sent on 304)"} (equal = the 304 was free)");
        });

        await report.Run("POST /graphql", async () =>
        {
            var sw = Stopwatch.StartNew();
            using var response = await http.PostAsync("github/graphql",
                new StringContent("""{"query":"{ viewer { login } rateLimit { cost remaining } }"}""", Encoding.UTF8, "application/json"));
            var total = sw.Elapsed.TotalMilliseconds;
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var ok = response.StatusCode == HttpStatusCode.OK && body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object;
            report.Check(ok, "POST github/graphql through the pipe", $"{(int)response.StatusCode}; {Timing(response, total)}");
            report.Info("rate limit after graphql", RateLine(response));
        });

        await report.Run("overhead", async () =>
        {
            var overhead = new List<double>();
            for (var i = 0; i < 10; i++)
            {
                var sw = Stopwatch.StartNew();
                using var response = await http.GetAsync("github/rate_limit"); // doesn't count against the limit
                var total = sw.Elapsed.TotalMilliseconds;
                if (double.TryParse(S2.Header(response, "Postern-Upstream-Ms"), System.Globalization.CultureInfo.InvariantCulture, out var upstream))
                    overhead.Add(total - upstream);
            }
            overhead.Sort();
            report.Info("Postern overhead (client total - upstream time), 10 calls to /rate_limit",
                overhead.Count == 0 ? "no timing header" : $"median {overhead[overhead.Count / 2]:F1} ms, max {overhead[^1]:F1} ms");
        });

        if (repo is not null)
        {
            await report.Run("redirect", async () =>
            {
                using var response = await http.GetAsync($"github/repos/{repo}/tarball");
                report.Check(response.StatusCode == HttpStatusCode.Found && response.Headers.Location?.Host != "api.github.com",
                    "tarball 302 passed back, not followed with auth", $"{(int)response.StatusCode} -> host {response.Headers.Location?.Host}");
            });
        }

        await report.Run("ratelimits", async () =>
            report.Info("/_postern/ratelimits", await http.GetStringAsync("_postern/ratelimits")));

        report.Info("broker log (the audit stand-in)", Environment.NewLine + string.Join(Environment.NewLine,
            broker.Output.Split('\n', StringSplitOptions.TrimEntries).Where(l => l.Contains(" -> ", StringComparison.Ordinal)).Select(l => "      " + l)));
    }

    static string Timing(HttpResponseMessage response, double totalMs) =>
        $"client {totalMs:F0} ms, upstream {S2.Header(response, "Postern-Upstream-Ms") ?? "?"} ms";

    static string RateLine(HttpResponseMessage response) =>
        $"{S2.Header(response, "Postern-RateLimit-Resource")}: {S2.Header(response, "Postern-RateLimit-Remaining")}/{S2.Header(response, "Postern-RateLimit-Limit")} remaining, resets {S2.Header(response, "Postern-RateLimit-Reset")}";

    static string Mask(string? login) => login switch
    {
        null => "<none>",
        { Length: <= 2 } => "**",
        _ => $"{login[0]}{new string('*', login.Length - 2)}{login[^1]}",
    };
}
