using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Postern.App.Services;
using Postern.App.Views;
using Postern.Broker;
using Postern.Broker.Pipe;
using Postern.Core.Apps;

namespace Postern.App;

public partial class App : Application
{
    readonly AvaloniaApprovalPrompt _prompt = new();
    readonly Updater _updater = new();
    ILogger _log = null!;
    BrokerRuntime? _broker;
    IClassicDesktopStyleApplicationLifetime? _desktop;
    TrayIcon? _tray;
    NativeMenuItem? _updateItem;
    MainWindow? _main;
    int _pendingPrompts;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-resident: no main window, so don't exit when the last window closes.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _desktop = desktop;
            _log = Program.Logs.CreateLogger("Postern.App");

            _broker = Program.CreateBroker(Program.WithDevAutoApprove(_prompt, _log));
            var connections = _broker.Connections;
            _prompt.DescribeConnection = name => connections.Find(name) is { } c ? $"{c.TemplateName} · {c.BaseUrl.Authority}" : null;
            _broker.Registrations.Changed += () => Dispatcher.UIThread.Post(UpdateTray);
            _prompt.PendingChanged += count =>
            {
                _pendingPrompts = count;
                UpdateTray();
            };
            _updater.AvailableChanged += UpdateTray;

            SetupTray(desktop);
            desktop.ShutdownRequested += (_, _) => StopBroker();
            _ = StartBroker(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    async Task StartBroker(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await _broker!.StartAsync();
            UpdateTray();
            _updater.Start();
            if (_broker.Credentials.Read(Core.Providers.ProviderDefinition.GitHub.CredentialTarget) is null)
                Open(MainWindow.ConnectionsKey); // first run: nothing works until there's a credential
            ShowWhatsNewAfterUpdate();
        }
        catch (PipeInUseException ex)
        {
            _log.LogError("{Message}", ex.Message);
            await ShowFatal("Postern is already running", ex.Message);
            Environment.ExitCode = Program.ExitPipeInUse;
            desktop.Shutdown(Program.ExitPipeInUse);
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Broker failed to start");
            await ShowFatal("Postern couldn't start", ex.Message);
            Environment.ExitCode = Program.ExitBrokerFailed;
            desktop.Shutdown(Program.ExitBrokerFailed);
        }
    }

    // The version is recorded on every start, not just after an update, so a fresh install or a downgrade shows
    // nothing. If it can't be recorded, nothing is shown either: it would otherwise come back on every start.
    void ShowWhatsNewAfterUpdate()
    {
        var dataDir = Program.Args.DataDirectory;
        var settings = AppSettings.Load(dataDir);
        var lastSeen = settings.LastSeenVersion;
        if (lastSeen == BrokerRuntime.Version)
            return;
        settings.LastSeenVersion = BrokerRuntime.Version;
        try
        {
            settings.Save(dataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Couldn't record the version in settings.json");
            return;
        }

        if (lastSeen is null || !settings.ShowChangelogAfterUpdate || ChangelogMarkdown.LoadEmbedded() is not { } markdown)
            return;
        var sections = Core.Changelog.ChangelogParser.UnseenSince(markdown, lastSeen, BrokerRuntime.Version);
        if (sections.Count == 0)
            return;
        var window = new WhatsNewWindow(lastSeen, sections, () =>
        {
            try
            {
                var latest = AppSettings.Load(dataDir);
                latest.ShowChangelogAfterUpdate = false;
                latest.Save(dataDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Couldn't save the changelog preference");
            }
        });
        window.Show();
        window.Activate();
    }

    void StopBroker()
    {
        try
        {
            _broker?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Broker didn't stop cleanly");
        }
    }

    void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var header = new NativeMenuItem($"Postern{Profile.DisplaySuffix} {BrokerRuntime.Version}{PipeSuffix}") { IsEnabled = false };

        var open = new NativeMenuItem("Open Postern…");
        open.Click += (_, _) => Open(MainWindow.AppsKey);

        // Only there when the background check has found something; checking by hand is on the Updates page.
        _updateItem = new NativeMenuItem("Update…") { IsVisible = false };
        _updateItem.Click += (_, _) => Open(MainWindow.UpdatesKey);

        var quit = new NativeMenuItem("Quit Postern");
        quit.Click += (_, _) => desktop.Shutdown(0);

        var menu = new NativeMenu
        {
            header,
            new NativeMenuItemSeparator(),
            open,
            _updateItem,
            new NativeMenuItemSeparator(),
            quit,
        };

        _tray = new TrayIcon { Icon = TrayArt.WindowIcon(), Menu = menu, IsVisible = true, ToolTipText = "Postern: starting" };
        _tray.Clicked += (_, _) => Open(MainWindow.AppsKey);
        TrayIcon.SetIcons(this, [_tray]);
    }

    static string PipeSuffix => Program.Args.PipeName == Profile.PipeName ? "" : $" ({Program.Args.PipeName})";

    void UpdateTray()
    {
        if (_tray is null || _broker is null)
            return;
        var approved = _broker.Registrations.All().Count(r => r.Status == RegistrationStatus.Approved);
        var update = _updater.Available?.TargetFullRelease.Version.ToString();
        _tray.Icon = TrayArt.WindowIcon(attention: _pendingPrompts > 0);
        _tray.ToolTipText = (_pendingPrompts > 0
            ? $"Postern{Profile.DisplaySuffix}{PipeSuffix}: {_pendingPrompts} app(s) waiting for approval"
            : $"Postern{Profile.DisplaySuffix}{PipeSuffix}: {approved} app(s) allowed") + (update is null ? "" : $"\nUpdate {update} available");
        if (_updateItem is not null)
        {
            _updateItem.Header = $"Update to {update}…";
            _updateItem.IsVisible = update is not null;
        }
    }

    void Open(string page)
    {
        if (_main is { } existing)
        {
            existing.NavigateTo(page);
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new MainWindow(_broker!, _updater, () => _desktop?.Shutdown(Program.ExitUpdating), page);
        _main = window;
        window.Closed += (_, _) => _main = null;
        window.Show();
        window.Activate();
    }

    static Task ShowFatal(string title, string message)
    {
        var closed = new TaskCompletionSource();
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            Background = Ui.Background,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children = { Ui.Heading(title), Ui.Text(message, Ui.Muted) },
            },
        };
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        return closed.Task;
    }
}
