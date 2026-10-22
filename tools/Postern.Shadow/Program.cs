using System.Diagnostics;
using System.Text;
using Postern.Client;
using Postern.Shadow;

// postern-shadow: a stand-in app that asks gh and Postern the same GitHub questions at the same moment and logs
// whether the answers agree. Run several copies (different names, different exe paths) to look like a stack
// starting up: scripts/shadow-stack.ps1 does that.
//
//   postern-shadow --name shadow-api --probe user --probe review-requests [--every 60] [--once]
//
// It registers on first run (approve it in Postern) and asks once: if you deny it, or don't answer in time, it
// exits rather than prompting again every round. A revoked token is logged each round, not re-registered.
// Log: %LOCALAPPDATA%\Postern.Shadow\logs\<name>.log, one line per probe per round:
//   review-requests: same (gh 812 ms, postern 301 ms) 2 [org/repo#12, org/repo#40]

const int ExitOk = 0, ExitUsage = 2, ExitPostern = 3, ExitNoGh = 4;

var rest = args.ToList();
string? Take(string option)
{
    var i = rest.IndexOf(option);
    if (i < 0 || i + 1 >= rest.Count)
        return null;
    var value = rest[i + 1];
    rest.RemoveRange(i, 2);
    return value;
}

var name = Take("--name");
var provider = Take("--provider") ?? "github";
var every = TimeSpan.FromSeconds(int.TryParse(Take("--every"), out var seconds) && seconds >= 10 ? seconds : 60);
var once = rest.Remove("--once");
var probeTexts = new List<string>();
while (Take("--probe") is { } text)
    probeTexts.Add(text);

List<Probe> probes;
try
{
    probes = [.. probeTexts.Select(Probe.Parse)];
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return ExitUsage;
}
if (name is null || probes.Count == 0 || rest.Count > 0)
{
    Console.Error.WriteLine($"""
        postern-shadow --name NAME --probe KIND [--probe ...] [--every SECONDS (min 10, default 60)] [--once]
                       [--provider NAME (default github; a test Postern can point another name at a fake)]
        Probes: {Probe.Kinds}
        The pipe is %POSTERN_PIPE%, else "postern-dev" for a Debug build and "postern" for Release.
        """);
    return ExitUsage;
}

var log = new ShadowLog(name);
if (GhCli.Find() is not { } gh)
{
    log.Write("gh.exe isn't on PATH; nothing to compare against.");
    return ExitNoGh;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

using var postern = new PosternClient(new PosternOptions { AppName = name, PipeName = DefaultPipe() });
using var github = postern.CreateHttpClient(provider);
github.DefaultRequestHeaders.UserAgent.ParseAdd("postern-shadow/0.1");
string[] scopes = [.. probes.Select(p => p.ScopeFor(provider)).Distinct()];

log.Write($"started: pid {Environment.ProcessId}, {Environment.ProcessPath}");
log.Write($"probes every {every.TotalSeconds:F0} s: {string.Join(", ", probes.Select(p => p.Label))}; gh is {gh.Exe}");

while (!stop.IsCancellationRequested)
{
    switch (await EnsureRegistered())
    {
        case false:
            return ExitPostern;
        case true:
            foreach (var probe in probes)
                await Compare(probe);
            break;
    }
    if (once)
        break;
    try
    {
        await Task.Delay(every, stop.Token);
    }
    catch (OperationCanceledException)
    {
    }
}
log.Write("stopped");
return ExitOk;

// True: ready. Null: Postern isn't up, try next round. False: the user said no (or didn't answer), so stop.
async Task<bool?> EnsureRegistered()
{
    if (postern.HasToken)
        return true;
    if (!postern.IsAvailable)
    {
        log.Write($"Postern isn't running; trying again next round. {PosternClient.NotRunningHint}");
        return null;
    }
    log.Write($"registering for {string.Join(", ", scopes)}: approve it in Postern.");
    try
    {
        var result = await postern.RegisterAsync(scopes, cancellationToken: stop.Token);
        log.Write($"registration {result.Outcome.ToString().ToLowerInvariant()}: {result.Detail}");
        return result.Outcome == RegistrationOutcome.Approved;
    }
    catch (PosternUnavailableException ex)
    {
        log.Write($"registration failed: {ex.Message}");
        return null;
    }
}

async Task Compare(Probe probe)
{
    var viaGh = Timed(() => gh.AskAsync(probe, stop.Token));
    var viaPostern = Timed(() => AskPostern(probe));
    var (ghSummary, ghMs) = await viaGh;
    var (posternSummary, posternMs) = await viaPostern;
    if (stop.IsCancellationRequested)
        return;
    var same = ghSummary == posternSummary;
    var detail = same ? " " + ghSummary : $"\n  gh:      {ghSummary}\n  postern: {posternSummary}";
    log.Write($"{probe.Label}: {(same ? "same" : "DIFFERENT")} (gh {ghMs} ms, postern {posternMs} ms){detail}");
}

async Task<string> AskPostern(Probe probe)
{
    try
    {
        using var request = new HttpRequestMessage(probe.Method, probe.Path);
        if (probe.Body is not null)
            request.Content = new StringContent(probe.Body, Encoding.UTF8, "application/json");
        using var response = await github.SendAsync(request, stop.Token);
        if (await response.GetPosternErrorAsync(stop.Token) is { } error)
        {
            if (error.NeedsRegistration)
                postern.ForgetToken(); // registers again next round
            return $"postern refused: {error.Code}";
        }
        return response.IsSuccessStatusCode
            ? probe.Summarise(await response.Content.ReadAsStringAsync(stop.Token))
            : $"HTTP {(int)response.StatusCode}";
    }
    catch (PosternUnavailableException ex)
    {
        return "postern unavailable: " + ex.Message;
    }
    catch (HttpRequestException ex)
    {
        return "postern failed: " + ex.Message;
    }
    catch (OperationCanceledException)
    {
        return "stopped";
    }
}

// The same rule as Postern.Broker.Profile (this tool references only the client): a Debug build talks to the dev
// Postern, unless POSTERN_PIPE says otherwise or POSTERN_DEV forces the profile.
static string DefaultPipe()
{
    if (Environment.GetEnvironmentVariable("POSTERN_PIPE") is { Length: > 0 } pipe)
        return pipe;
    var dev = Environment.GetEnvironmentVariable("POSTERN_DEV");
#if DEBUG
    var isDev = string.IsNullOrEmpty(dev) || !(dev == "0" || dev.Equals("false", StringComparison.OrdinalIgnoreCase));
#else
    var isDev = !string.IsNullOrEmpty(dev) && !(dev == "0" || dev.Equals("false", StringComparison.OrdinalIgnoreCase));
#endif
    return isDev ? "postern-dev" : PosternOptions.DefaultPipeName;
}

static async Task<(string Summary, long Ms)> Timed(Func<Task<string>> ask)
{
    var clock = Stopwatch.StartNew();
    var summary = await ask();
    return (summary, clock.ElapsedMilliseconds);
}
