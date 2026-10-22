using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Postern.Client;

// poller-spike [--provider github] [--repo owner/name] [--interval SECONDS] [--count N] [--pipe NAME]
//
// What Pylon's GitHub adapter would do, minus the events: poll a repo's PR list with If-None-Match, count 200s vs
// 304s, watch the rate-limit headers Postern adds, and keep going through Postern restarts. Prints one line per poll,
// then a summary. --count 0 polls until Ctrl+C.

var provider = Option("--provider") ?? "github";
var repo = Option("--repo") ?? "cli/cli";
var interval = TimeSpan.FromSeconds(double.Parse(Option("--interval") ?? "30"));
var count = int.Parse(Option("--count") ?? "0");
var pipe = Option("--pipe");

using var postern = new PosternClient(new PosternOptions
{
    AppName = "poller-spike",
    PipeName = pipe ?? new PosternOptions { AppName = "x" }.PipeName,
});

if (!postern.HasToken && !await Register())
    return 3;

using var http = postern.CreateHttpClient(provider);
EntityTagHeaderValue? etag = null;
int changed = 0, notModified = 0, failures = 0;
string? firstRemaining = null, lastRemaining = null;
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

for (var poll = 1; count == 0 || poll <= count; poll++)
{
    var sw = Stopwatch.StartNew();
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{repo}/pulls?per_page=20");
        if (etag is not null)
            request.Headers.IfNoneMatch.Add(etag);
        using var response = await http.SendAsync(request, stop.Token);
        var remaining = response.Headers.TryGetValues("Postern-RateLimit-Remaining", out var r) ? r.First() : "?";
        firstRemaining ??= remaining;
        lastRemaining = remaining;

        if (await response.GetPosternErrorAsync() is { } error)
        {
            failures++;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} #{poll} POSTERN {error.Status} {error.Code}: {error.Detail}");
            if (error.Code == PosternError.AppRevoked)
            {
                Console.WriteLine("Access was revoked in Postern. A real consumer stops here and tells the user; it doesn't re-register on its own.");
                break;
            }
            if (error.NeedsRegistration && !await Register())
                break;
        }
        else if (response.StatusCode == HttpStatusCode.NotModified)
        {
            notModified++;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} #{poll} 304 not modified  ({sw.ElapsedMilliseconds} ms, remaining {remaining})");
        }
        else if (response.IsSuccessStatusCode)
        {
            changed++;
            etag = response.Headers.ETag;
            var body = await response.Content.ReadAsStringAsync(stop.Token);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} #{poll} 200 changed       ({sw.ElapsedMilliseconds} ms, remaining {remaining}, {body.Length} bytes, ETag {etag})");
        }
        else
        {
            failures++;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} #{poll} provider said {(int)response.StatusCode}");
        }
    }
    catch (PosternUnavailableException ex)
    {
        failures++;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} #{poll} Postern unavailable after retries: {ex.Message}");
    }
    catch (OperationCanceledException)
    {
        break;
    }

    if (count == 0 || poll < count)
    {
        try
        {
            await Task.Delay(interval, stop.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

Console.WriteLine($"summary: {changed} changed (200), {notModified} not modified (304), {failures} failures; rate limit remaining {firstRemaining} -> {lastRemaining}");
return failures == 0 ? 0 : 1;

async Task<bool> Register()
{
    postern.ForgetToken();
    Console.WriteLine("Registering 'poller-spike' with Postern; approve it in the Postern window...");
    var result = await postern.RegisterAsync([$"GET {provider}/repos/*/*/pulls"]);
    Console.WriteLine($"  {result.Outcome}: {result.Detail}");
    return result.Outcome == RegistrationOutcome.Approved;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
