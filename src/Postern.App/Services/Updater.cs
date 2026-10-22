using Avalonia.Threading;
using Microsoft.Win32;
using Postern.Broker;
using Postern.Broker.Credentials;
using Velopack;
using Velopack.Sources;

namespace Postern.App.Services;

/// <summary>
/// Updates from GitHub Releases through Velopack. Postern is the security-critical component, so an update is only
/// ever applied when the user asks for it: the background check just relabels the tray item. Applying restarts
/// Postern (and its supervisor), which clients ride out with their connect retries.
/// </summary>
internal sealed class Updater
{
    public const string RepoUrl = "https://github.com/ArcticGizmo/postern";

    /// <summary>A local folder of `vpk pack` output to update from instead of GitHub, for testing an update end to end.</summary>
    public const string FeedVariable = "POSTERN_UPDATE_FEED";

    static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    // Anonymous GitHub API calls are limited per IP, and a corporate NAT shares one IP between everybody.
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    readonly UpdateManager? _manager;
    readonly DispatcherTimer _timer = new() { Interval = CheckInterval };
    bool _busy;

    public Updater()
    {
        try
        {
            var manager = CreateManager();
            _manager = manager.IsInstalled ? manager : null;
        }
        catch
        {
            _manager = null; // a dev build has no install to update
        }
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    /// <summary>False for a dev build run from bin: there's nothing for Velopack to replace.</summary>
    public bool IsInstalled => _manager is not null;

    public string CurrentVersion => _manager?.CurrentVersion?.ToString() ?? BrokerRuntime.Version;

    /// <summary>The newer release the last check found, if any.</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Available"/> changes.</summary>
    public event Action? AvailableChanged;

    public static string? LocalFeed =>
        Environment.GetEnvironmentVariable(FeedVariable) is { Length: > 0 } dir && Directory.Exists(dir) ? Path.GetFullPath(dir) : null;

    static UpdateManager CreateManager() =>
        LocalFeed is { } feed ? new UpdateManager(feed) : new UpdateManager(new GithubSource(RepoUrl, null, false));

    public void Start()
    {
        if (_manager is null)
            return;
        DispatcherTimer.RunOnce(() => _ = CheckAsync(), FirstCheckDelay);
        _timer.Start();
    }

    public sealed record CheckResult(UpdateInfo? Update, string? Error);

    /// <summary>Asks the feed. Never throws: the error comes back for the caller to show, or not.</summary>
    public async Task<CheckResult> CheckAsync()
    {
        if (_manager is null)
            return new(null, "This copy of Postern isn't installed.");
        if (_busy)
            return new(Available, null);
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update?.TargetFullRelease.Version.ToString() != Available?.TargetFullRelease.Version.ToString())
            {
                Available = update;
                AvailableChanged?.Invoke();
            }
            return new(update, null);
        }
        catch (Exception ex)
        {
            return new(null, ex.Message);
        }
    }

    /// <summary>
    /// Downloads the update, then starts Velopack's updater, which waits for this process to exit before it replaces
    /// the files. The caller must shut down with <see cref="Program.ExitUpdating"/> straight after.
    /// <paramref name="progress"/> is called off the UI thread.
    /// </summary>
    public async Task PrepareAsync(UpdateInfo update, Action<int> progress)
    {
        _busy = true;
        try
        {
            await _manager!.DownloadUpdatesAsync(update, progress);
            _manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: false, restart: true, RestartArguments());
        }
        finally
        {
            _busy = false;
        }
    }

    // Program.Args.Raw is the child's command line, which never has --supervise. Put it back when this process was
    // supervised, or the restarted Postern would run without its watchdog.
    static string[] RestartArguments() =>
        Environment.GetEnvironmentVariable(Program.SupervisedVariable) == "1" ? ["--supervise", .. Program.Args.Raw] : [.. Program.Args.Raw];

    /// <summary>
    /// Velopack's uninstall hook: drop what Postern registered outside its install folder, and the credentials it
    /// held. Leaving a PAT in Credential Manager for a broker that's gone would be a secret with no owner. The data
    /// folder (approvals, audit log) stays; without the credentials it can't call anything.
    /// </summary>
    public static void CleanUpBeforeUninstall()
    {
        // Each step on its own: an uninstall must never fail on cleanup, and one failure mustn't skip the rest.
        Try(() => new WindowsCredentialStore().DeleteAll("postern:"));
        Try(Autostart.Remove);
        Try(() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\Postern", throwOnMissingSubKey: false));
    }

    static void Try(Action step)
    {
        try
        {
            step();
        }
        catch
        {
            // best effort, see CleanUpBeforeUninstall
        }
    }
}
