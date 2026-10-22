using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Postern.App.Services;
using Velopack;

namespace Postern.App.Views;

/// <summary>
/// Tray → Check for updates. Checks when it opens, then offers the update with what it will do to running apps
/// spelled out, since Postern sits in the request path of all of them.
/// </summary>
internal sealed class UpdateWindow : Window
{
    readonly Updater _updater;
    readonly Action _shutDownForUpdate;
    readonly bool _checkOnOpen;
    readonly StackPanel _body = new() { Spacing = 14, Margin = new Thickness(22, 20) };

    public UpdateWindow(Updater updater, Action shutDownForUpdate, bool checkOnOpen = true)
    {
        _updater = updater;
        _shutDownForUpdate = shutDownForUpdate;
        _checkOnOpen = checkOnOpen;
        Title = "Postern: updates";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Background;
        Icon = TrayArt.WindowIcon();
        Content = _body;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!_checkOnOpen)
            return;
        if (!_updater.IsInstalled)
        {
            ShowNotInstalled();
            return;
        }

        ShowChecking();
        var result = await _updater.CheckAsync();
        if (result.Error is { } error)
            ShowError("Postern couldn't check for updates.", error);
        else if (result.Update is { } update)
            ShowAvailable(update.TargetFullRelease.Version.ToString(), update);
        else
            ShowUpToDate();
    }

    public void ShowChecking() => Show("Checking for updates…", [Ui.Text($"You have Postern {_updater.CurrentVersion}.", Ui.Muted)]);

    public void ShowUpToDate() => Show("Postern is up to date", [Ui.Text($"{_updater.CurrentVersion} is the latest release.", Ui.Muted)], CloseButton());

    public void ShowNotInstalled() => Show("This copy can't update itself",
    [
        Ui.Text("It's running from a build folder, not an installed copy:", Ui.Muted),
        Ui.Code(Environment.ProcessPath ?? "?"),
        Ui.Text($"Install a release from {Updater.RepoUrl}/releases to get updates here.", Ui.Muted),
    ], CloseButton());

    public void ShowError(string heading, string detail) => Show(heading, [Ui.Text(detail, Ui.Warning)], CloseButton());

    /// <summary><paramref name="update"/> is null only for a snapshot render, which can't apply anything.</summary>
    public void ShowAvailable(string version, UpdateInfo? update)
    {
        var apply = Ui.Button("Update and restart", () => Apply(version, update!), primary: true);
        apply.IsEnabled = update is not null;
        var buttons = new List<Button>();
        if (Updater.LocalFeed is null)
            buttons.Add(Ui.Button("Release notes", () => OpenReleaseNotes(version)));
        buttons.Add(Ui.Button("Later", Close));
        buttons.Add(apply);

        Show($"Postern {version} is available",
        [
            Ui.Text($"You have {_updater.CurrentVersion}.", Ui.Muted),
            Ui.Card(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Ui.Text("Updating restarts Postern:"),
                    Ui.Text("• Apps lose their connection for a few seconds and reconnect on their own.", Ui.Muted, 12.5),
                    Ui.Text("• Approval requests that are still waiting are denied; those apps ask again.", Ui.Muted, 12.5),
                    Ui.Text("• Approvals, credentials and the audit log are kept.", Ui.Muted, 12.5),
                },
            }),
            Updater.LocalFeed is { } feed
                ? Ui.Text($"Updating from the local folder in {Updater.FeedVariable}: {feed}", Ui.Warning, 12)
                : Ui.Text($"From {Updater.RepoUrl}/releases", Ui.Muted, 12),
        ], [.. buttons]);
    }

    public void ShowDownloading(string version, int percent) =>
        Show($"Downloading Postern {version}…", [Ui.Text($"{percent}%. Postern restarts when it's ready.", Ui.Muted)]);

    async void Apply(string version, UpdateInfo update)
    {
        ShowDownloading(version, 0);
        try
        {
            await _updater.PrepareAsync(update, percent => Dispatcher.UIThread.Post(() => ShowDownloading(version, percent)));
        }
        catch (Exception ex)
        {
            ShowError("The update couldn't be prepared.", ex.Message);
            return;
        }
        _shutDownForUpdate();
    }

    static void OpenReleaseNotes(string version) =>
        Process.Start(new ProcessStartInfo($"{Updater.RepoUrl}/releases/tag/v{version}") { UseShellExecute = true });

    Button CloseButton() => Ui.Button("Close", Close, primary: true);

    void Show(string heading, Control[] lines, params Button[] buttons)
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.Heading(heading));
        foreach (var line in lines)
            _body.Children.Add(line);
        if (buttons.Length == 0)
            return;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        foreach (var button in buttons)
            row.Children.Add(button);
        _body.Children.Add(row);
    }
}
