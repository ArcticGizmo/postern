using System.Diagnostics;
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
    ILogger _log = null!;
    BrokerRuntime? _broker;
    TrayIcon? _tray;
    AppsWindow? _apps;
    CredentialsWindow? _credentials;
    AuditWindow? _audit;
    int _pendingPrompts;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-resident: no main window, so don't exit when the last window closes.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _log = Program.Logs.CreateLogger("Postern.App");

            _broker = Program.CreateBroker(Program.WithDevAutoApprove(_prompt, _log));
            _broker.Registrations.Changed += () => Dispatcher.UIThread.Post(UpdateTray);
            _prompt.PendingChanged += count =>
            {
                _pendingPrompts = count;
                UpdateTray();
            };

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
            if (_broker.Credentials.Read(Core.Providers.ProviderDefinition.GitHub.CredentialTarget) is null)
                OpenCredentials(); // first run: nothing works until there's a credential
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
        var header = new NativeMenuItem($"Postern {BrokerRuntime.Version}{PipeSuffix}") { IsEnabled = false };

        var apps = new NativeMenuItem("Apps…");
        apps.Click += (_, _) => OpenApps();
        var credentials = new NativeMenuItem("Credentials…");
        credentials.Click += (_, _) => OpenCredentials();
        var audit = new NativeMenuItem("Audit log…");
        audit.Click += (_, _) => OpenAudit();

        var autostart = new NativeMenuItem("Start at login")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = Autostart.IsEnabled(),
        };
        autostart.Click += (_, _) =>
        {
            Autostart.Apply(!Autostart.IsEnabled(), Program.Args);
            autostart.IsChecked = Autostart.IsEnabled();
        };

        var folder = new NativeMenuItem("Open data folder");
        folder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Program.Args.DataDirectory}\"") { UseShellExecute = true });

        var quit = new NativeMenuItem("Quit Postern");
        quit.Click += (_, _) => desktop.Shutdown(0);

        var menu = new NativeMenu
        {
            header,
            new NativeMenuItemSeparator(),
            apps,
            credentials,
            audit,
            new NativeMenuItemSeparator(),
            autostart,
            folder,
            new NativeMenuItemSeparator(),
            quit,
        };

        _tray = new TrayIcon { Icon = TrayArt.WindowIcon(), Menu = menu, IsVisible = true, ToolTipText = "Postern: starting" };
        _tray.Clicked += (_, _) => OpenApps();
        TrayIcon.SetIcons(this, [_tray]);
    }

    static string PipeSuffix => Program.Args.PipeName == BrokerOptions.DefaultPipeName ? "" : $" ({Program.Args.PipeName})";

    void UpdateTray()
    {
        if (_tray is null || _broker is null)
            return;
        var approved = _broker.Registrations.All().Count(r => r.Status == RegistrationStatus.Approved);
        _tray.Icon = TrayArt.WindowIcon(attention: _pendingPrompts > 0);
        _tray.ToolTipText = _pendingPrompts > 0
            ? $"Postern{PipeSuffix}: {_pendingPrompts} app(s) waiting for approval"
            : $"Postern{PipeSuffix}: {approved} app(s) allowed";
    }

    void OpenApps() => Open(ref _apps, () => new AppsWindow(_broker!));
    void OpenCredentials() => Open(ref _credentials, () => new CredentialsWindow(_broker!));
    void OpenAudit() => Open(ref _audit, () => new AuditWindow(_broker!));

    static void Open<T>(ref T? slot, Func<T> create) where T : Window
    {
        if (slot is { } existing)
        {
            existing.Activate();
            return;
        }
        var window = create();
        slot = window;
        var captured = window;
        window.Closed += (_, _) => ClearSlot(captured);
        window.Show();
        window.Activate();
    }

    // Open() can't capture a ref parameter in the Closed handler, so windows clear themselves through this.
    static void ClearSlot(Window window)
    {
        if (Current is not App app)
            return;
        if (ReferenceEquals(app._apps, window)) app._apps = null;
        if (ReferenceEquals(app._credentials, window)) app._credentials = null;
        if (ReferenceEquals(app._audit, window)) app._audit = null;
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
