using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Postern.App.Services;
using Velopack;

namespace Postern.App.Views;

/// <summary>
/// Checks the first time it's shown, then offers the update with what it will do to running apps spelled out, since
/// Postern sits in the request path of all of them.
/// </summary>
internal sealed class UpdatesPage : UserControl
{
    readonly Updater _updater;
    readonly Action _shutDownForUpdate;
    readonly StackPanel _body = new() { Spacing = 14, Margin = new Thickness(18), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
    bool _checked;
    bool _busy;

    public UpdatesPage(Updater updater, Action shutDownForUpdate)
    {
        _updater = updater;
        _shutDownForUpdate = shutDownForUpdate;
        Content = new ScrollViewer { Content = _body };
        ShowCurrent();
    }

    /// <summary>Called each time the page is selected. Only the first visit asks the feed; the background check covers the rest.</summary>
    public void Shown()
    {
        if (_busy)
            return;
        if (!_updater.IsInstalled)
            ShowNotInstalled();
        else if (_updater.Available is { } update)
            ShowAvailable(update.TargetFullRelease.Version.ToString(), update);
        else if (!_checked)
            _ = Check();
    }

    /// <summary>The background check found (or lost) an update while the window was open.</summary>
    public void AvailableChanged()
    {
        if (!_busy && _updater.Available is { } update)
            ShowAvailable(update.TargetFullRelease.Version.ToString(), update);
    }

    async Task Check()
    {
        _busy = true;
        ShowChecking();
        var result = await _updater.CheckAsync();
        _busy = false;
        _checked = true;
        if (result.Error is { } error)
            ShowError("Postern couldn't check for updates.", error);
        else if (result.Update is { } update)
            ShowAvailable(update.TargetFullRelease.Version.ToString(), update);
        else
            ShowUpToDate();
    }

    void ShowCurrent() => Show("Updates", [Ui.Text($"You have Postern {_updater.CurrentVersion}.", Ui.Muted)], CheckButton("Check for updates"));

    public void ShowChecking() => Show("Checking for updates…", [Ui.Text($"You have Postern {_updater.CurrentVersion}.", Ui.Muted)]);

    public void ShowUpToDate() => Show("Postern is up to date", [Ui.Text($"{_updater.CurrentVersion} is the latest release.", Ui.Muted)], CheckButton("Check again"));

    public void ShowNotInstalled() => Show("This copy can't update itself",
    [
        Ui.Text("It's running from a build folder, not an installed copy:", Ui.Muted),
        Ui.Code(Environment.ProcessPath ?? "?"),
        Ui.Text($"Install a release from {Updater.RepoUrl}/releases to get updates here.", Ui.Muted),
    ]);

    public void ShowError(string heading, string detail) => Show(heading, [Ui.Text(detail, Ui.Warning)], CheckButton("Try again"));

    /// <summary><paramref name="update"/> is null only for a snapshot render, which can't apply anything.</summary>
    public void ShowAvailable(string version, UpdateInfo? update)
    {
        var apply = Ui.Button("Update and restart", () => Apply(version, update!), primary: true);
        apply.IsEnabled = update is not null;
        var buttons = new List<Button>();
        if (Updater.LocalFeed is null)
            buttons.Add(Ui.Button("Release notes", () => OpenReleaseNotes(version)));
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
        _busy = true;
        ShowDownloading(version, 0);
        try
        {
            await _updater.PrepareAsync(update, percent => Dispatcher.UIThread.Post(() => ShowDownloading(version, percent)));
        }
        catch (Exception ex)
        {
            _busy = false;
            ShowError("The update couldn't be prepared.", ex.Message);
            return;
        }
        _shutDownForUpdate();
    }

    static void OpenReleaseNotes(string version) =>
        Process.Start(new ProcessStartInfo($"{Updater.RepoUrl}/releases/tag/v{version}") { UseShellExecute = true });

    Button CheckButton(string text)
    {
        var button = Ui.Button(text, () => _ = Check(), primary: true);
        button.IsEnabled = _updater.IsInstalled;
        return button;
    }

    void Show(string heading, Control[] lines, params Button[] buttons)
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.Heading(heading));
        foreach (var line in lines)
            _body.Children.Add(line);
        if (buttons.Length == 0)
            return;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var button in buttons)
            row.Children.Add(button);
        _body.Children.Add(row);
    }
}
