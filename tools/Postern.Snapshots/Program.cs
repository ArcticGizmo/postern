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

    var updater = new Postern.App.Services.Updater();
    var broker = Broker();
    Save(Shell(broker, updater, MainWindow.AppsKey), "apps");
    Save(Shell(broker, updater, MainWindow.CredentialsKey), "credentials");
    Save(Shell(broker, updater, MainWindow.AuditKey), "audit");
    Save(Shell(broker, updater, MainWindow.GeneralKey), "general");
    Save(Shell(broker, updater, MainWindow.UpdatesKey, p => p.ShowAvailable("0.3.0", null)), "update-available");
    Save(Shell(broker, updater, MainWindow.UpdatesKey, p => p.ShowUpToDate()), "update-up-to-date");
    Save(Shell(broker, updater, MainWindow.UpdatesKey, p => p.ShowNotInstalled()), "update-not-installed");
    Save(Shell(broker, updater, MainWindow.UpdatesKey, p => p.ShowDownloading("0.3.0", 42)), "update-downloading");
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

static MainWindow Shell(BrokerRuntime broker, Postern.App.Services.Updater updater, string page, Action<UpdatesPage>? updates = null)
{
    var window = new MainWindow(broker, updater, () => { }, page);
    updates?.Invoke(window.Updates);
    return window;
}

static ApprovalWindow Approval(params ApprovalRequest[] requests)
{
    var window = new ApprovalWindow();
    foreach (var request in requests)
        window.Enqueue(new PendingApproval(request));
    return window;
}

static ApprovalRequest Request(IReadOnlyList<Scope>? previous, bool signed, string name = "perch",
    string exe = @"C:\Users\Example\AppData\Local\Perch\current\Perch.exe") => new(
    "a1b2c3-" + name,
    name,
    exe,
    (uint)(4242 + name.Length),
    signed,
    signed ? "CN=Example Publisher Ltd, O=Example Publisher Ltd, C=GB" : null,
    [Scope.Parse("GET github/repos/*/*/pulls/**"), Scope.Parse("POST github/graphql"), Scope.Parse("GET github/notifications")],
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

    var credentials = new InMemoryCredentialStore();
    credentials.Write(ProviderDefinition.GitHub.CredentialTarget, "not-a-real-token");

    var broker = BrokerRuntime.Create(
        new BrokerOptions
        {
            PipeName = "postern-snapshots",
            PublishDiscovery = false,
            Providers = [ProviderDefinition.GitHub, new ProviderDefinition("octopus", new Uri("https://octopus.example.com"), AuthStyle.Header, "X-Octopus-ApiKey")],
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
