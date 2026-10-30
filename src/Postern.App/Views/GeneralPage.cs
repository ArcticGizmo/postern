using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Postern.App.Services;
using Postern.Broker;

namespace Postern.App.Views;

/// <summary>How Postern runs on this machine: start at login, and where it keeps its files.</summary>
internal sealed class GeneralPage : UserControl
{
    readonly CheckBox _autostart = new() { Content = "Start Postern when I sign in to Windows", Foreground = Ui.Foreground };
    readonly TextBlock _autostartError = Ui.Text("", Ui.Warning, 12);
    bool _syncing;

    public GeneralPage()
    {
        var dataDir = Program.Args.DataDirectory;
        _autostart.IsChecked = Autostart.IsEnabled();
        _autostart.IsCheckedChanged += (_, _) => ApplyAutostart();
        _autostartError.IsVisible = false;

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                MaxWidth = 720,
                HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    Ui.Heading("General"),
                    Ui.Card(new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            _autostart,
                            Ui.Text("Apps that call through Postern fail until it's running, so most people want this on. " +
                                    "It starts under a supervisor that restarts Postern if it crashes.", Ui.Muted, 12),
                            _autostartError,
                        },
                    }),
                    Ui.Card(new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            Folder("Data folder", dataDir, "Approvals, the audit log, providers.json and settings.json."),
                            Folder("Logs", Path.Combine(dataDir, "logs"), "One file a day. Request headers are never logged."),
                        },
                    }),
                    Ui.Card(new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            Ui.Field("Version", Ui.Code($"{BrokerRuntime.Version}{Profile.DisplaySuffix}")),
                            Ui.Field("Pipe", Ui.Code($@"\\.\pipe\{Program.Args.PipeName}")),
                        },
                    }),
                },
            },
        };
    }

    void ApplyAutostart()
    {
        if (_syncing)
            return;
        try
        {
            Autostart.Apply(_autostart.IsChecked == true, Program.Args);
            _autostartError.IsVisible = false;
        }
        catch (Exception ex)
        {
            _autostartError.Text = "Couldn't change it: " + ex.Message;
            _autostartError.IsVisible = true;
        }
        // Show what the Run key actually says, without that write looping back into Apply.
        _syncing = true;
        _autostart.IsChecked = Autostart.IsEnabled();
        _syncing = false;
    }

    static Control Folder(string label, string path, string note)
    {
        var open = Ui.Button("Open", () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }));
        open.IsEnabled = Directory.Exists(path);
        DockPanel.SetDock(open, Dock.Right);
        return new DockPanel
        {
            Children =
            {
                open,
                Ui.Field(label, new StackPanel { Spacing = 2, Children = { Ui.Code(path), Ui.Text(note, Ui.Muted, 12) } }),
            },
        };
    }
}
