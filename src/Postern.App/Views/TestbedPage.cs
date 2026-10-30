using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Postern.App.Services;
using Postern.Broker;

namespace Postern.App.Views;

/// <summary>
/// Dev and Sandbox builds only. In the sandbox: starts Sallyport and fake-upstream as their own processes, shows what
/// the sandbox holds, and resets it. In Dev, which holds real credentials, it only offers to start the sandbox.
/// </summary>
internal sealed class TestbedPage : UserControl
{
    readonly BrokerRuntime _broker;
    readonly Action _quit;
    readonly StackPanel _body = new() { Spacing = 12 };

    public TestbedPage(BrokerRuntime broker, Action quit)
    {
        _broker = broker;
        _quit = quit;
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                MaxWidth = 760,
                HorizontalAlignment = HorizontalAlignment.Left,
                Children = { Ui.Heading("Testbed"), _body },
            },
        };
    }

    public async void Refresh()
    {
        if (!Profile.IsSandbox)
        {
            _body.Children.Clear();
            ShowDev();
            return;
        }
        // Before clearing, so two refreshes close together can't both add their cards.
        var upstreamUp = await Testbed.UpstreamAnsweringAsync();
        _body.Children.Clear();
        _body.Children.Add(Banner());
        _body.Children.Add(SallyportCard());
        _body.Children.Add(UpstreamCard(upstreamUp));
        _body.Children.Add(SandboxCard());
    }

    void ShowDev()
    {
        var running = Testbed.SandboxRunning();
        var start = Ui.Button("Start Postern (Sandbox)", () =>
        {
            Testbed.StartSandbox();
            Later(Refresh);
        }, primary: true);
        start.IsEnabled = !running;
        _body.Children.Add(Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Text("The test bed runs against Postern (Sandbox): a separate Postern, in sand, that only sends requests to 127.0.0.1 " +
                        "and holds made-up credentials. This is Postern (Dev), which holds real ones, so Sallyport won't talk to it."),
                Ui.Text(running ? "Postern (Sandbox) is running. Open its Testbed page from its own tray icon." : "Postern (Sandbox) isn't running.",
                    running ? Ui.Ok : Ui.Muted, 12),
                Row(start),
            },
        }));
    }

    static Control Banner() => new Border
    {
        Background = Ui.Brush("#2A261C"),
        BorderBrush = Ui.Brush("#6E6143"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 10),
        Child = Ui.Text("This is the sandbox. It only sends requests to 127.0.0.1 or localhost, its credentials are made up, " +
                        "and it refuses anything that looks like a real token. Nothing here touches Postern or Postern (Dev).", Ui.Foreground, 12.5),
    };

    Control SallyportCard()
    {
        var exe = Testbed.SallyportExe;
        var pids = Testbed.Running(exe);
        var open = Ui.Button("Open Sallyport", () =>
        {
            Testbed.Open(exe!);
            Later(Refresh);
        }, primary: true);
        open.IsEnabled = exe is not null && pids.Count == 0;
        var close = Ui.Button("Close", () =>
        {
            Testbed.Stop(exe);
            Later(Refresh);
        });
        close.IsVisible = pids.Count > 0;

        return Card("Sallyport",
            exe is null ? ("not built", Ui.Warning) : pids.Count > 0 ? ($"running · pid {pids[0]}", Ui.Ok) : ("not running", Ui.Muted),
            exe is null
                ? "Build it first: dotnet build tools\\Postern.Sallyport"
                : "Sends a library of requests through this Postern as an ordinary app, checks each answer, and soaks while you crash and restart Postern. Its approval prompt is the real one.",
            open, close);
    }

    Control UpstreamCard(bool answering)
    {
        var exe = Testbed.FakeUpstreamExe;
        var start = Ui.Button("Start", () =>
        {
            Testbed.Open(exe!, minimised: true);
            Later(Refresh);
        });
        start.IsEnabled = exe is not null && !answering;
        var stop = Ui.Button("Stop", () =>
        {
            Testbed.Stop(exe);
            Later(Refresh);
        });
        stop.IsEnabled = answering && Testbed.Running(exe).Count > 0;

        return Card("fake-upstream",
            answering ? ($"answering on {Sandbox.UpstreamOrigin}", Ui.Ok) : exe is null ? ("not built", Ui.Warning) : ("not running", Ui.Muted),
            exe is null
                ? "Build it first: dotnet build tools\\Postern.FakeUpstream"
                : "Stands in for GitHub, Jira, Slack, Octopus and Anthropic. Its console window (minimised) logs every request.",
            start, stop);
    }

    Control SandboxCard()
    {
        var connections = _broker.Connections.All;
        var missing = connections.Where(c => _broker.Credentials.Read(c.CredentialTarget) is null).Select(c => c.Name).ToList();
        var reset = Ui.Button("Reset sandbox…", async () =>
        {
            var sure = await new ConfirmDialog("Reset the sandbox?",
                "Postern (Sandbox) restarts with fresh connections and fake credentials. Every approval and the audit log are deleted, " +
                "so Sallyport will ask to be approved again.", "Reset").ShowOver<bool?>(this);
            if (sure != true)
                return;
            Testbed.StartSandbox(reset: true);
            _quit();
        });
        reset.Foreground = Ui.Danger;

        return Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "This sandbox", FontSize = 14, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Ui.Foreground },
                Ui.Field("Pipe", Ui.Code($@"\\.\pipe\{Program.Args.PipeName}")),
                Ui.Field("Data folder", Ui.Code(Program.Args.DataDirectory)),
                Ui.Field("Connections", Ui.Code(string.Join(", ", connections.Select(c => c.Name)))),
                Ui.Field("Credentials", Ui.Code(missing.Count == 0
                    ? $"{connections.Count} made up"
                    : $"{connections.Count - missing.Count} made up; none for {string.Join(", ", missing)}")),
                Row(reset),
            },
        });
    }

    static Control Card(string title, (string Text, Avalonia.Media.IBrush Colour) state, string note, params Button[] buttons)
    {
        var heading = new DockPanel();
        var status = Ui.Code(state.Text, state.Colour);
        DockPanel.SetDock(status, Dock.Right);
        heading.Children.Add(status);
        heading.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Ui.Foreground });
        return Ui.Card(new StackPanel { Spacing = 10, Children = { heading, Ui.Text(note, Ui.Muted, 12), Row(buttons) } });
    }

    static Control Row(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in buttons)
            row.Children.Add(button);
        return row;
    }

    // Long enough for a started process to open its pipe or port, or a stopped one to let go of it.
    static void Later(Action action) =>
        Avalonia.Threading.DispatcherTimer.RunOnce(action, TimeSpan.FromSeconds(1.5));
}
