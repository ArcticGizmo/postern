using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Postern.App;
using Postern.App.Views;
using Postern.Broker;
using Postern.Broker.Credentials;
using Postern.Core.Apps;
using Postern.Core.Audit;
using Postern.Core.Changelog;
using Postern.Core.Providers;
using Postern.Core.Scopes;

// postern-snapshots [outputDir]   (default ./captures)
// Every name, path and number below is made up.

var output = Path.GetFullPath(args.FirstOrDefault() ?? "captures");
Directory.CreateDirectory(output);
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

Postern.App.Program.Args = AppArgs.Parse(["--data-dir", @"C:\Users\Example\AppData\Local\Postern"]); // displayed only, never created

using var session = HeadlessUnitTestSession.StartNew(typeof(SnapshotApp));
await session.Dispatch(() =>
{
    Save(Approval(Request(previous: null, signed: false)), "approval-new-unsigned");
    Save(Approval(Request(previous: [Scope.Parse("GET github/repos/*/*/pulls/**"), Scope.Parse("GET github/user")], signed: true)),
        "approval-replacement-signed");
    Save(Approval(
        Request(previous: null, signed: false, name: "web", exe: @"C:\Code\example-stack\web\bin\Debug\web.exe"),
        Request(previous: null, signed: false, name: "api", exe: @"C:\Code\example-stack\api\bin\Debug\api.exe"),
        Request(previous: null, signed: false, name: "api-jobs", exe: @"C:\Code\example-stack\api\bin\Debug\api.exe"),
        Request(previous: null, signed: false, name: "worker", exe: @"C:\Code\example-stack\worker\bin\Debug\worker.exe")), "approval-queue");
    Save(Approval(Request(previous: null, signed: true, name: "pylon", exe: @"C:\Tools\Pylon\pylon.exe",
        scopes: ["GET github/notifications", "GET github-work/repos/example-org/*/actions/runs", "GET jira/rest/api/3/search", "POST linear/graphql"])),
        "approval-connections");

    var updater = new Postern.App.Services.Updater();
    var broker = Broker();
    Save(Shell(broker, updater, MainWindow.AppsKey), "apps");
    Save(Shell(broker, updater, MainWindow.ConnectionsKey), "connections");
    Save(new AddConnectionDialog(broker, "jira"), "connections-add");
    Save(new CredentialDialog(broker, broker.Connections.Find("jira")!, replacing: true), "connections-credential");
    Save(new ProvidersJsonDialog(broker), "connections-json");
    Save(new ConfirmDialog("Remove octopus?", "Its credential is deleted too. Apps with scopes on octopus keep their approval, " +
        "but their requests fail until a connection with that name exists again.", "Remove"), "connections-remove");
    Save(Shell(broker, updater, MainWindow.AuditKey), "audit");
    Save(Shell(broker, updater, MainWindow.HowKey), "how-github");
    Save(Shell(broker, updater, MainWindow.HowKey, howFor: "jira"), "how-jira");
    Save(Shell(broker, updater, MainWindow.HowKey, howFor: "datadog"), "how-datadog");
    foreach (var connection in broker.Connections.All)
        foreach (var (title, code) in HowItWorksPage.CodeFor(connection, Postern.App.Program.Args.PipeName))
        {
            var extension = title switch { "Node" => "mjs", "PowerShell" => "ps1", _ => "cs" };
            var path = Path.Combine(output, $"{stamp}-example-{connection.Name}-{title.Replace(".", "").ToLowerInvariant()}.{extension}");
            File.WriteAllText(path, code);
        }
    Save(Shell(broker, updater, MainWindow.GeneralKey), "general");
    Save(Shell(broker, updater, MainWindow.UpdatesKey, p => p.ShowAvailable("0.3.0", null)), "update-available");
    Save(Shell(broker, updater, MainWindow.UpdatesKey, p => p.ShowUpToDate()), "update-up-to-date");
    Save(Shell(broker, updater, MainWindow.UpdatesKey, p => p.ShowNotInstalled()), "update-not-installed");
    Save(Shell(broker, updater, MainWindow.UpdatesKey, p => p.ShowDownloading("0.3.0", 42)), "update-downloading");
    Save(Shell(broker, updater, MainWindow.ChangelogKey), "changelog");
    var changelog = ChangelogMarkdown.LoadEmbedded() ?? throw new InvalidOperationException("CHANGELOG.md isn't embedded");
    Save(new WhatsNewWindow("0.0.0", ChangelogParser.UnseenSince(changelog, "0.0.0", "999.0.0"), () => { }), "whats-new");

    Save(Sallyport("json-object"), "sallyport");
    Save(Sallyport("streaming"), "sallyport-mismatch");
}, CancellationToken.None);

Console.WriteLine($"Wrote snapshots to {output}");
Environment.Exit(0); // the headless session's dispatcher thread doesn't wind down on its own
return;

void Save(Window window, string name)
{
    window.Show();
    Dispatcher.UIThread.RunJobs();
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"No frame for {name}");
    var path = Path.Combine(output, $"{stamp}-ui-{name}.png");
    frame.Save(path);
    Console.WriteLine(path);
    window.Close();
}

static MainWindow Shell(BrokerRuntime broker, Postern.App.Services.Updater updater, string page, Action<UpdatesPage>? updates = null,
    string? howFor = null)
{
    var window = new MainWindow(broker, updater, () => { }, page);
    updates?.Invoke(window.Updates);
    if (howFor is not null)
        window.How.Select(howFor);
    if (page == MainWindow.HowKey)
        window.Height = 1900; // the whole page in one frame, not just what fits on screen
    return window;
}

// Every scenario passed apart from streaming, which is made to fail here to show how a mismatch reads. The soak
// strip shows a crash drill: down, a retry, then back.
static Window Sallyport(string select)
{
    var settings = new Postern.Sallyport.Settings("postern-sandbox", new Uri("http://127.0.0.1:5599/"), "sallyport", false, null);
    var window = new Postern.Sallyport.MainWindow(settings, watch: false) { Height = 900 };
    var results = window.LoadedScenarios.Select(s => s.Id switch
    {
        "json-object" => new Postern.Sallyport.RunResult(s,
            "GET /github/user HTTP/1.1\nPostern-App-Token: (attached)\nX-Testbed-Id: 3f9c2a7d1b04",
            "200 OK in 6 ms",
            "Postern-RateLimit-Limit: 5000\nPostern-RateLimit-Remaining: 4987\nPostern-RateLimit-Resource: core\nPostern-Upstream-Ms: 3.1\n" +
            "Content-Type: application/json; charset=utf-8\n\n{\"login\":\"fake\",\"id\":1001,\"type\":\"User\"}",
            "GET /github/user\nAuthorization: Bearer san…\nHost: 127.0.0.1:5599\nUser-Agent: Postern/0.1.1\nX-Testbed-Id: 3f9c2a7d1b04\n\nno body",
            [
                new("status 200", true, "200 OK in 6 ms"), new("no Postern-Error (the answer is the service's)", true, "none"),
                new("reached fake-upstream", true, "it saw 1 request(s)"), new("upstream got Authorization: Bearer san…", true, "Bearer san…"),
                new("upstream didn't get Postern-App-Token", true), new("response has Postern-Upstream-Ms", true), new("response has Postern-RateLimit-Remaining", true),
            ], TimeSpan.FromMilliseconds(9)),
        "streaming" => new Postern.Sallyport.RunResult(s,
            "POST /anthropic/v1/messages HTTP/1.1\nPostern-App-Token: (attached)\nContent-Type: application/json; charset=utf-8\n\n{\"model\":\"sandbox\",\"stream\":true}",
            "200 OK in 4,102 ms",
            "Content-Type: text/event-stream\n\nevent: message_start              +4,102 ms\nevent: content_block_delta        +4,102 ms\n" +
            "event: content_block_delta        +4,103 ms\nevent: message_stop               +4,103 ms",
            "POST /v1/messages\nanthropic-version: 2023-06-01\nx-api-key: sandbox-no…\n\nbody: 112 bytes, sha256 9b1e04c2d7aa…",
            [new("status 200", true, "200 OK"), new("events arrived as sent (spread ≥ 500 ms)", false, "4 events over 1 ms; first after 4102 ms"),
             new("upstream got x-api-key: sandbox-no…", true, "sandbox-no…")],
            TimeSpan.FromMilliseconds(4110)),
        _ => new Postern.Sallyport.RunResult(s, "", "ok", "", "", [new("as expected", true)], TimeSpan.FromMilliseconds(5)),
    }).ToList();

    var at = DateTimeOffset.Now.AddSeconds(-60);
    var ticks = Enumerable.Range(0, 60).Select(i => new Postern.Sallyport.SoakTick(at.AddSeconds(i),
        i is 41 or 42 ? Postern.Sallyport.SoakKind.Down : i == 43 ? Postern.Sallyport.SoakKind.Retried : Postern.Sallyport.SoakKind.Ok, "200", 5));
    window.Preview(new Postern.Client.PosternStatus("0.1.1", "postern-sandbox", 41990, ["github", "jira"], null), results, ticks,
        [("soak: GET github/user every second", false), ("crash: Killed pid 41872.", true), ("Postern is back as pid 41990 (was 41872)", false)], select);
    return window;
}

static ApprovalWindow Approval(params ApprovalRequest[] requests)
{
    var window = new ApprovalWindow(name => name switch
    {
        "github" => "GitHub · api.github.com",
        "github-work" => "GitHub · api.github.com",
        "jira" => "Jira Cloud · example.atlassian.net",
        _ => null,
    });
    foreach (var request in requests)
        window.Enqueue(new PendingApproval(request));
    return window;
}

static ApprovalRequest Request(IReadOnlyList<Scope>? previous, bool signed, string name = "perch",
    string exe = @"C:\Users\Example\AppData\Local\Perch\current\Perch.exe", string[]? scopes = null) => new(
    "a1b2c3-" + name,
    name,
    exe,
    (uint)(4242 + name.Length),
    signed,
    signed ? "CN=Example Publisher Ltd, O=Example Publisher Ltd, C=GB" : null,
    [.. (scopes ?? ["GET github/repos/*/*/pulls/**", "POST github/graphql", "GET github/notifications"]).Select(Scope.Parse)],
    previous);

static BrokerRuntime Broker()
{
    var registrations = new InMemoryRegistrationStore();
    var now = DateTimeOffset.Now;
    registrations.Save(new AppRegistration
    {
        Id = "r1", Name = "perch", ExePath = @"C:\Users\Example\AppData\Local\Perch\current\Perch.exe",
        Scopes = [Scope.Parse("GET github/repos/*/*/pulls/**"), Scope.Parse("POST github/graphql")],
        Status = RegistrationStatus.Approved, CreatedAt = now.AddDays(-3), DecidedAt = now.AddDays(-3),
        ClaimHash = "x", TokenHash = "y",
    });
    registrations.Save(new AppRegistration
    {
        Id = "r2", Name = "pylon", ExePath = @"C:\Tools\Pylon\pylon.exe", Signer = "CN=Example Publisher Ltd",
        Scopes = [Scope.Parse("GET github/repos/org/*/actions/runs"), Scope.Parse("GET octopus/api/*/events")],
        Status = RegistrationStatus.Approved, CreatedAt = now.AddHours(-5), DecidedAt = now.AddHours(-5),
        ClaimHash = "x", TokenHash = "z",
    });

    var audit = new InMemoryAuditLog();
    var rows = new (string App, string Method, string Path, int Status, string Outcome, double? Ms)[]
    {
        ("perch", "POST", "/graphql", 200, "allowed", 212),
        ("perch", "GET", "/repos/example/perch/pulls", 304, "allowed", 98),
        ("pylon", "GET", "/repos/example/pylon/actions/runs", 200, "allowed", 340),
        ("perch", "DELETE", "/repos/example/perch", 403, "scope-denied", null),
        ("unknown.exe", "GET", "/user", 401, "no-token", null),
    };
    foreach (var (app, method, path, status, outcome, ms) in rows)
        audit.Write(new AuditEntry(now.AddMinutes(-rows.Length), app.EndsWith(".exe") ? null : "id", app.EndsWith(".exe") ? null : app, 4242,
            @"C:\Example\" + app, method, "github", path, path.Contains("pulls") ? 12 : 0, status, outcome, ms));

    // A throwaway data folder, so the JSON editor shows this example rather than the real providers.json.
    var dataDir = Path.Combine(Path.GetTempPath(), "postern-snapshots-" + Guid.NewGuid().ToString("N")[..8]);
    ProvidersFile.Write(dataDir, ProvidersFile.Serialize(
    [
        new ProviderEntry { Name = "github-work", Template = "github" },
        new ProviderEntry { Name = "jira", Template = "jira", BaseUrl = "https://example.atlassian.net" },
        new ProviderEntry { Name = "octopus", Template = "octopus", BaseUrl = "https://octopus.example.com" },
        new ProviderEntry { Name = "datadog", Template = "datadog", BaseUrl = "https://api.datadoghq.eu" },
    ]));
    var providers = BrokerOptions.LoadProviders(dataDir);
    ProviderDefinition Find(string name) => providers.First(p => p.Name == name);

    var credentials = new InMemoryCredentialStore();
    credentials.Write(ProviderDefinition.GitHub.CredentialTarget, "not-a-real-token");
    credentials.Write(Find("jira").CredentialTarget, Find("jira").EncodeCredential(new Dictionary<string, string> { ["email"] = "someone@example.com", ["token"] = "not-real" }));
    // Entered while github-work pointed at an enterprise server, so it's held back from api.github.com.
    credentials.Write(Find("github-work").CredentialTarget,
        ProviderTemplates.Find("github-enterprise")!.Connect("github-work", new Dictionary<string, string> { ["host"] = "github.example.com" })
            .EncodeCredential(new Dictionary<string, string> { ["token"] = "not-real" }));

    var broker = BrokerRuntime.Create(
        new BrokerOptions
        {
            PipeName = "postern-snapshots",
            PublishDiscovery = false,
            DataDirectory = dataDir,
            Providers = providers,
        },
        new DenyAllPrompt(),
        new BrokerOverrides { Credentials = credentials, Registrations = registrations, Audit = audit });
    broker.RateLimits.Observe("github", h => h switch
    {
        "x-ratelimit-remaining" => "4711", "x-ratelimit-limit" => "5000", "x-ratelimit-resource" => "core",
        "x-ratelimit-reset" => now.AddMinutes(40).ToUnixTimeSeconds().ToString(), _ => null,
    });
    return broker;
}

sealed class SnapshotApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<SnapshotApp>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
