using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Postern.Client;

namespace Postern.Sallyport;

public sealed record Check(string What, bool Passed, string? Detail = null);

/// <summary>One scenario's run: what was sent, what came back, what fake-upstream saw, and each expectation's verdict.</summary>
public sealed record RunResult(
    Scenario Scenario, string RequestText, string Outcome, string ResponseText, string SeenText, IReadOnlyList<Check> Checks, TimeSpan Elapsed)
{
    public bool Passed => Checks.Count > 0 && Checks.All(c => c.Passed);
}

/// <summary>
/// Runs scenarios through Postern exactly as an app would: a <see cref="PosternClient"/> HttpClient over the pipe.
/// Each request carries an X-Testbed-Id, so fake-upstream can say afterwards whether (and how) it arrived.
/// </summary>
public sealed class Runner(PosternClient postern, Upstream upstream) : IDisposable
{
    const int PreviewChars = 3000;
    const string ShaHeader = "X-Fake-Body-Sha256";

    // No token store with anything in it: what an app that never registered sends.
    readonly PosternClient _anonymous = new(new PosternOptions
    {
        AppName = postern.Options.AppName + "-anonymous",
        PipeName = postern.Options.PipeName,
        TokenStore = new InMemoryTokenStore(),
    });

    public async Task<RunResult> RunAsync(Scenario scenario, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var checks = new List<Check>();
        try
        {
            if (scenario.Knob is { } knob)
            {
                if (!await upstream.IsRunningAsync(cancellationToken))
                    return Failed(scenario, clock, $"fake-upstream isn't answering on {upstream.BaseUrl}, so the {knob.Fault} fault can't be set.");
                await upstream.SetKnobAsync(knob, cancellationToken);
            }

            return scenario.Flow switch
            {
                "conditional" => await Conditional(scenario, checks, clock, cancellationToken),
                "hang" => await Hang(scenario, checks, clock, cancellationToken),
                _ => await Single(scenario, checks, clock, cancellationToken),
            };
        }
        finally
        {
            if (scenario.Knob is not null)
                await upstream.ClearKnobsAsync(CancellationToken.None);
        }
    }

    async Task<RunResult> Single(Scenario scenario, List<Check> checks, Stopwatch clock, CancellationToken cancellationToken)
    {
        var exchange = await Send(scenario.Request, cancellationToken: cancellationToken);
        var seen = await upstream.SeenAsync(exchange.Id, cancellationToken);
        Evaluate(scenario.Expect, exchange, seen, checks);
        return new RunResult(scenario, exchange.RequestText, exchange.Outcome, exchange.ResponseText, Describe(seen), checks, clock.Elapsed);
    }

    async Task<RunResult> Conditional(Scenario scenario, List<Check> checks, Stopwatch clock, CancellationToken cancellationToken)
    {
        var first = await Send(scenario.Request, cancellationToken: cancellationToken);
        checks.Add(new Check("first request 200 with an ETag", first.Status == 200 && first.Header("ETag") is not null, first.Outcome));
        var etag = first.Header("ETag") ?? "\"none\"";

        var second = await Send(scenario.Request, new() { ["If-None-Match"] = etag }, cancellationToken);
        var seen = await upstream.SeenAsync(second.Id, cancellationToken);
        Evaluate(scenario.Expect, second, seen, checks);
        var before = first.Header("Postern-RateLimit-Remaining");
        var after = second.Header("Postern-RateLimit-Remaining");
        checks.Add(new Check("the 304 cost nothing", before is not null && before == after, $"remaining {before ?? "?"} → {after ?? "?"}"));
        return new RunResult(scenario, second.RequestText, second.Outcome, second.ResponseText, Describe(seen), checks, clock.Elapsed);
    }

    async Task<RunResult> Hang(Scenario scenario, List<Check> checks, Stopwatch clock, CancellationToken cancellationToken)
    {
        var hanging = Send(scenario.Request, cancellationToken: cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        var meanwhile = scenario.Meanwhile is { } other ? await Send(other, cancellationToken: cancellationToken) : null;
        var held = await hanging;
        var seen = await upstream.SeenAsync(held.Id, cancellationToken);

        checks.Add(new Check($"gave up after {scenario.Request.TimeoutSeconds} s", held.TimedOut, held.Outcome));
        if (meanwhile is not null)
            checks.Add(new Check($"{scenario.Meanwhile} got through meanwhile ({scenario.Expect.Status})", meanwhile.Status == scenario.Expect.Status, meanwhile.Outcome));
        checks.Add(new Check("upstream was holding it", seen is { Count: > 0 }, seen is null ? "fake-upstream isn't answering" : null));
        return new RunResult(scenario, held.RequestText, held.Outcome,
            held.ResponseText + (meanwhile is null ? "" : $"\n\nmeanwhile: {scenario.Meanwhile} → {meanwhile.Outcome}"), Describe(seen), checks, clock.Elapsed);
    }

    static RunResult Failed(Scenario scenario, Stopwatch clock, string why) =>
        new(scenario, "", "not sent", "", why, [new Check("ready to run", false, why)], clock.Elapsed);

    static void Evaluate(Expectation e, Exchange r, IReadOnlyList<SeenRequest>? seen, List<Check> checks)
    {
        if (e.Status is { } status)
            checks.Add(new Check($"status {status}", r.Status == status, r.Outcome));

        if (e.PosternError is { } code)
        {
            var passed = code switch
            {
                "none" => r.PosternError is null,
                _ when code.EndsWith('*') => r.PosternError?.StartsWith(code[..^1], StringComparison.Ordinal) == true,
                _ => r.PosternError == code,
            };
            checks.Add(new Check(code == "none" ? "no Postern-Error (the answer is the service's)" : $"Postern-Error {code}", passed, r.PosternError ?? "none"));
        }

        var first = seen?.FirstOrDefault();
        if (e.Upstream is { } upstreamExpected)
        {
            var passed = seen is not null && (upstreamExpected == "reached" ? seen.Count > 0 : seen.Count == 0);
            var detail = seen is null ? "fake-upstream isn't answering" : seen.Count == 0 ? "it saw nothing" : $"it saw {seen.Count} request(s)";
            checks.Add(new Check(upstreamExpected == "reached" ? "reached fake-upstream" : "never reached fake-upstream", passed, detail));
        }
        foreach (var (name, prefix) in e.UpstreamHeaders ?? new())
        {
            var value = first?.Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            checks.Add(new Check($"upstream got {name}: {prefix}…", value?.StartsWith(prefix, StringComparison.Ordinal) == true, value ?? (first is null ? "nothing arrived" : "not sent")));
        }
        foreach (var name in e.UpstreamAbsent ?? [])
        {
            var sent = first?.Headers.Keys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;
            checks.Add(new Check($"upstream didn't get {name}", first is not null && !sent, first is null ? "nothing arrived" : sent ? "it was sent" : null));
        }
        if (e.UpstreamBodyMatches)
            checks.Add(new Check("upstream got the body byte for byte", first is not null && first.BodySha256 == r.SentSha,
                $"sent {Short(r.SentSha)}, received {Short(first?.BodySha256)}"));

        foreach (var name in e.Headers ?? [])
            checks.Add(new Check($"response has {name}", r.Header(name) is not null));
        foreach (var (name, value) in e.HeaderEquals ?? new())
            checks.Add(new Check($"{name}: {value}", r.Header(name) == value, r.Header(name) ?? "missing"));

        if (e.BodyMatchesSha)
        {
            var expected = r.Header(ShaHeader);
            checks.Add(new Check("body arrived intact (SHA-256)", expected is not null && expected == r.BodySha, $"expected {Short(expected)}, got {Short(r.BodySha)}"));
        }
        if (e.BodyContains is { } fragment)
            checks.Add(new Check($"body contains {fragment}", r.BodyText?.Contains(fragment, StringComparison.Ordinal) == true));
        if (e.MinBytes is { } min)
            checks.Add(new Check($"at least {min:N0} bytes", r.BodyBytes >= min, $"{r.BodyBytes:N0} bytes"));
        if (e.MaxBytes is { } max)
            checks.Add(new Check(max == 0 ? "no body" : $"at most {max:N0} bytes", r.BodyBytes <= max, $"{r.BodyBytes:N0} bytes"));
        if (e.JsonArrayLength is { } length)
            checks.Add(new Check($"{length} items", ArrayLength(r.BodyText) == length, $"{ArrayLength(r.BodyText)?.ToString() ?? "not a JSON array"}"));
        if (e.StreamSpreadMs is { } spread)
        {
            var arrived = r.EventArrivals;
            var actual = arrived.Count > 1 ? (arrived[^1] - arrived[0]).TotalMilliseconds : 0;
            checks.Add(new Check($"events arrived as sent (spread ≥ {spread} ms)", actual >= spread,
                $"{arrived.Count} events over {actual:F0} ms; first after {(arrived.Count > 0 ? arrived[0].TotalMilliseconds : 0):F0} ms"));
        }
    }

    static int? ArrayLength(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string Short(string? sha) => sha is null ? "nothing" : sha[..Math.Min(12, sha.Length)] + "…";

    static string Describe(IReadOnlyList<SeenRequest>? seen)
    {
        if (seen is null)
            return "fake-upstream isn't answering, so there's no record of what it received.";
        if (seen.Count == 0)
            return "Nothing. Postern refused it before it left the machine.";
        var text = new StringBuilder();
        foreach (var r in seen)
        {
            text.AppendLine($"{r.Method} {r.Path}{r.Query}");
            foreach (var (name, value) in r.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
                text.AppendLine($"{name}: {value}");
            text.AppendLine(r.BodyBytes > 0 ? $"\nbody: {r.BodyBytes:N0} bytes, sha256 {Short(r.BodySha256)}" : "\nno body");
            if (r.Fault is not null)
                text.AppendLine(r.Fault);
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    async Task<Exchange> Send(ScenarioRequest spec, Dictionary<string, string>? extraHeaders = null, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        using var http = (spec.OmitToken ? _anonymous : postern).CreateHttpClient(spec.Connection);
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(new HttpMethod(spec.Method.ToUpperInvariant()), spec.Path);
        foreach (var (name, value) in spec.Headers.Concat(extraHeaders ?? new()))
            request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.TryAddWithoutValidation(Upstream.IdHeader, id);

        byte[]? body = null;
        if (Content(spec) is { } content)
        {
            body = await content.ReadAsByteArrayAsync(cancellationToken);
            request.Content = new ByteArrayContent(body);
            foreach (var header in content.Headers)
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        var exchange = new Exchange(id, RequestText(request, spec, body), body is null ? null : Convert.ToHexStringLower(SHA256.HashData(body)));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(spec.TimeoutSeconds));
        var started = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            exchange.Status = (int)response.StatusCode;
            exchange.Headers = response.Headers.Concat(response.Content.Headers)
                .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            exchange.PosternError = exchange.Header(PosternError.Header);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                await ReadEvents(stream, exchange, started, timeout.Token);
            else
                await ReadBody(stream, exchange, timeout.Token);
            exchange.Outcome = $"{exchange.Status} {response.ReasonPhrase} in {started.ElapsedMilliseconds:N0} ms";
            exchange.ResponseText = ResponseText(response, exchange);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            exchange.TimedOut = true;
            exchange.Outcome = $"timed out after {spec.TimeoutSeconds} s";
            exchange.ResponseText = exchange.Outcome;
        }
        catch (PosternUnavailableException ex)
        {
            exchange.Outcome = "Postern unavailable";
            exchange.ResponseText = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            exchange.Outcome = "failed";
            exchange.ResponseText = ex.Message;
        }
        return exchange;
    }

    static HttpContent? Content(ScenarioRequest spec)
    {
        if (spec.Json is { } json)
            return new StringContent(json.GetRawText(), Encoding.UTF8, "application/json");
        if (spec.Form is { } form)
            return new FormUrlEncodedContent(form);
        if (spec.Multipart is { } parts)
        {
            var multipart = new MultipartFormDataContent("sallyport-" + Guid.NewGuid().ToString("N")[..8]);
            foreach (var part in parts)
            {
                if (part.Bytes is { } size)
                {
                    var file = new ByteArrayContent([.. Enumerable.Range(0, size).Select(i => (byte)(i % 251))]);
                    file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    multipart.Add(file, part.Name, part.Filename ?? part.Name);
                }
                else
                {
                    multipart.Add(new StringContent(part.Text ?? ""), part.Name);
                }
            }
            return multipart;
        }
        return null;
    }

    static async Task ReadBody(Stream stream, Exchange exchange, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        exchange.BodyBytes = bytes.Length;
        exchange.BodySha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (exchange.Header("Content-Type") is { } type && (type.Contains("json") || type.StartsWith("text/")))
            exchange.BodyText = Encoding.UTF8.GetString(bytes);
    }

    static async Task ReadEvents(Stream stream, Exchange exchange, Stopwatch started, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                exchange.EventArrivals.Add(started.Elapsed);
                text.AppendLine($"{line,-34} +{started.ElapsedMilliseconds:N0} ms");
            }
        }
        exchange.BodyText = text.ToString();
        exchange.BodyBytes = Encoding.UTF8.GetByteCount(exchange.BodyText);
    }

    static string RequestText(HttpRequestMessage request, ScenarioRequest spec, byte[]? body)
    {
        var text = new StringBuilder($"{request.Method} /{spec.Connection}/{spec.Path} HTTP/1.1\n");
        text.AppendLine(spec.OmitToken ? $"{PosternClient.TokenHeader}: (none)" : $"{PosternClient.TokenHeader}: (attached)");
        foreach (var header in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
            text.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        if (body is not null)
            text.Append('\n').Append(spec.Multipart is null ? Preview(Encoding.UTF8.GetString(body), body.Length) : $"<{body.Length:N0} bytes of multipart>");
        return text.ToString().TrimEnd();
    }

    static string ResponseText(HttpResponseMessage response, Exchange exchange)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in exchange.Headers.OrderBy(h => h.Key.StartsWith("Postern-", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            text.AppendLine($"{name}: {value}");
        text.Append('\n');
        text.Append(exchange.BodyText is { } body ? Preview(body, exchange.BodyBytes)
            : exchange.BodyBytes == 0 ? "(empty)"
            : $"<{exchange.BodyBytes:N0} bytes, sha256 {Short(exchange.BodySha)}>");
        return text.ToString();
    }

    static string Preview(string text, long bytes) => text.Length <= PreviewChars ? text : text[..PreviewChars] + $"\n… ({bytes:N0} bytes in all)";

    public void Dispose() => _anonymous.Dispose();

    sealed class Exchange(string id, string requestText, string? sentSha)
    {
        public string Id => id;
        public string RequestText => requestText;
        public string? SentSha => sentSha;
        public int? Status { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? PosternError { get; set; }
        public long BodyBytes { get; set; }
        public string? BodySha { get; set; }
        public string? BodyText { get; set; }
        public List<TimeSpan> EventArrivals { get; } = [];
        public bool TimedOut { get; set; }
        public string Outcome { get; set; } = "";
        public string ResponseText { get; set; } = "";

        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }
}
