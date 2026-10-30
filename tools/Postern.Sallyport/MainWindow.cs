using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Postern.Client;

namespace Postern.Sallyport;

/// <summary>
/// Scenarios on the left, the selected one's request, response and verdict in the middle, what fake-upstream received
/// on the right, and the soak strip along the bottom. Everything goes through Postern.Client, as any app's would.
/// </summary>
internal sealed class MainWindow : Window
{
    const int StripCells = 60;

    readonly Settings _settings;
    readonly PosternClient _postern;
    readonly Upstream _upstream;
    readonly Runner _runner;
    readonly Soak _soak;
    readonly IReadOnlyList<Scenario> _scenarios = [];
    readonly IReadOnlyList<string> _scopes = [];
    readonly Dictionary<string, RunResult> _results = [];
    readonly Dictionary<string, (Button Item, Ellipse Mark)> _items = [];
    readonly CancellationTokenSource _closing = new();

    readonly TextBlock _pipePill, _tokenPill, _upstreamPill;
    readonly Button _register, _runAll, _runOne, _pause, _crash, _start;
    readonly TextBlock _note = Ui.Text("", Ui.Muted);
    readonly TextBlock _method = new() { FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Accent, FontWeight = FontWeight.SemiBold };
    readonly SelectableTextBlock _path = Ui.Code("", 12.5);
    readonly WrapPanel _expect = new() { Orientation = Orientation.Horizontal };
    readonly SelectableTextBlock _request = Ui.Code(), _response = Ui.Code(), _seen = Ui.Code();
    readonly TextBlock _outcome = new() { FontFamily = Ui.Mono, FontSize = 11.5 };
    readonly StackPanel _checks = new() { Spacing = 4 };
    readonly UniformGrid _strip = new() { Columns = StripCells, Rows = 1, Height = 20 };
    readonly StackPanel _events = new() { Spacing = 1 };

    string? _selected;
    PosternStatus? _status;
    RegistrationState _state = RegistrationState.Unavailable;
    bool _asking, _declined, _running, _userPaused, _soakStarted;
    SoakKind? _lastKind;

    public MainWindow(Settings settings) : this(settings, watch: true)
    {
    }

    /// <param name="watch">False for snapshots: nothing is polled or sent, and <see cref="Preview"/> fills it in.</param>
    internal MainWindow(Settings settings, bool watch)
    {
        _settings = settings;
        _postern = Program.CreateClient(settings);
        _upstream = new Upstream(settings.Upstream);
        _runner = new Runner(_postern, _upstream);
        _soak = new Soak(_postern);
        _soak.Ticked += tick => Dispatcher.UIThread.Post(() => OnTick(tick));

        Title = $"Sallyport → {(settings.Pipe == Target.SandboxPipe ? "Postern (Sandbox)" : settings.Pipe)}";
        Width = 1280;
        Height = 820;
        MinWidth = 900;
        MinHeight = 600;
        Background = Ui.Background;
        Icon = Ui.Icon();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        string? loadProblem = null;
        try
        {
            _scenarios = Scenarios.Load();
            _scopes = Registration.ScopesFor(_scenarios, _soak);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            loadProblem = $"Scenarios can't be read from {Scenarios.Folder}: {ex.Message}";
        }

        var pipePill = Ui.Pill(out _pipePill);
        var tokenPill = Ui.Pill(out _tokenPill);
        var upstreamPill = Ui.Pill(out _upstreamPill);
        _register = Ui.Button("Ask Postern", () => _ = Register(manual: true));
        _runAll = Ui.Button("Run all", () => _ = RunAll(), Ui.Accent);
        _runOne = Ui.Button("Run", () => _ = RunSelected(), Ui.Accent);
        _pause = Ui.Button("Pause soak", TogglePause);
        _crash = Ui.Button("Crash Postern", Crash);
        _crash.Foreground = Ui.Danger;
        _start = Ui.Button("Start Postern", () => Log(Target.Start(_settings.Pipe), Ui.Muted));
        ToolTip.SetTip(_crash, "Kills the sandbox Postern's process. Under its supervisor it's back in about 2 s.");
        ToolTip.SetTip(_start, "Starts Postern --sandbox --supervise from where it last ran.");
        ToolTip.SetTip(_pause, "The soak pauses by itself while scenarios run. To see a clean quit, use Quit Postern in its tray menu.");
        _register.IsVisible = false;

        var top = new DockPanel { Margin = new Thickness(12, 9) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _register, _runAll, _pause, _crash, _start } };
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { pipePill, tokenPill, upstreamPill } });

        var problem = settings.Problem ?? loadProblem;
        var banner = new Border
        {
            Background = Ui.Brush("#3A1D1E"),
            BorderBrush = Ui.Danger,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8),
            IsVisible = problem is not null,
            Child = Ui.Text(problem ?? "", Ui.Foreground),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*,330") };
        var list = List();
        var middle = Middle();
        var right = Right();
        Grid.SetColumn(list, 0);
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(list);
        grid.Children.Add(middle);
        grid.Children.Add(right);

        Content = new DockPanel
        {
            Children =
            {
                Ui.Top(new Border { Background = Ui.Panel, BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = top }),
                Ui.Top(banner),
                Ui.Bottom(SoakPanel()),
                grid,
            },
        };

        if (problem is not null)
        {
            foreach (var button in new[] { _register, _runAll, _runOne, _pause, _crash, _start })
                button.IsEnabled = false;
            Ui.SetPill(_pipePill, settings.Pipe, Ui.Danger);
            return;
        }

        Select(_scenarios.FirstOrDefault()?.Id);
        if (watch)
            Opened += (_, _) => _ = Watch(_closing.Token);
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _runner.Dispose();
            _upstream.Dispose();
            _postern.Dispose();
        };
    }

    Control List()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };
        foreach (var group in _scenarios.GroupBy(s => s.Group))
        {
            var header = Ui.Label(group.Key);
            header.Margin = new Thickness(14, 12, 12, 4);
            stack.Children.Add(header);
            foreach (var scenario in group)
            {
                var mark = new Ellipse { Width = 10, Height = 10, Stroke = Ui.Border, StrokeThickness = 1.5, Fill = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
                var item = new Button
                {
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Children = { mark, new TextBlock { Text = scenario.Name, FontSize = 13 } } },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = Ui.Sunken,
                    Foreground = Ui.Foreground,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = Brushes.Transparent,
                    CornerRadius = new CornerRadius(0),
                    Padding = new Thickness(11, 6),
                };
                item.Click += (_, _) => Select(scenario.Id);
                item.DoubleTapped += (_, _) => _ = RunSelected();
                _items[scenario.Id] = (item, mark);
                stack.Children.Add(item);
            }
        }
        return new Border
        {
            Background = Ui.Sunken,
            BorderBrush = Ui.Border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
    }

    Control Middle()
    {
        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_runOne, Dock.Right);
        line.Children.Add(_runOne);
        line.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new Border { Background = Ui.Brush("#1D2A40"), CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 2), Child = _method }, _path },
        });

        var expectLabel = Ui.Label("Expect");
        expectLabel.Margin = new Thickness(0, 4, 10, 0);
        DockPanel.SetDock(expectLabel, Dock.Left);
        var expectRow = new DockPanel { Children = { expectLabel, _expect } };
        var panes = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 10 };
        var request = Ui.Pane("Request as Sallyport sent it", _request, maxHeight: 190);
        var response = Ui.Pane("Response", _response, _outcome);
        Grid.SetRow(response, 1);
        panes.Children.Add(request);
        panes.Children.Add(response);

        return new DockPanel
        {
            Margin = new Thickness(16, 14),
            Children =
            {
                Ui.Top(new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 12), Children = { _note, line, expectRow } }),
                panes,
            },
        };
    }

    Control Right()
    {
        var seen = Ui.Pane("What fake-upstream received", _seen);
        var checks = Ui.Pane("Verdict", _checks, maxHeight: 260);
        DockPanel.SetDock(checks, Dock.Bottom);
        checks.Margin = new Thickness(0, 10, 0, 0);
        return new Border
        {
            Background = Ui.Sunken,
            BorderBrush = Ui.Border,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(12, 14),
            Child = new DockPanel { Children = { checks, seen } },
        };
    }

    Control SoakPanel()
    {
        for (var i = 0; i < StripCells; i++)
            _strip.Children.Add(new Border { Background = Ui.Panel, Margin = new Thickness(1, 0), CornerRadius = new CornerRadius(2) });

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var (label, brush) in new[] { ("200", Ui.Ok), ("waited for the pipe, then ok", Ui.Warning), ("Postern unavailable", Ui.Danger), ("refused by Postern", Ui.Sand) })
            legend.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = brush }, Ui.Text(label, Ui.Muted, 11.5) },
            });
        var head = new DockPanel();
        DockPanel.SetDock(legend, Dock.Right);
        head.Children.Add(legend);
        head.Children.Add(Ui.Text($"Soak · GET {_soak.Describe} every second", Ui.Foreground, 12.5));

        return new Border
        {
            Background = Ui.Panel,
            BorderBrush = Ui.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 10),
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { head, _strip, new ScrollViewer { MaxHeight = 96, Content = _events } },
            },
        };
    }

    void Select(string? id)
    {
        _selected = id;
        foreach (var (key, (item, _)) in _items)
        {
            item.BorderBrush = key == id ? Ui.Accent : Brushes.Transparent;
            item.Background = key == id ? Ui.Panel : Ui.Sunken;
        }
        if (_scenarios.FirstOrDefault(s => s.Id == id) is not { } scenario)
            return;

        _note.Text = scenario.Note;
        _method.Text = scenario.Request.Method.ToUpperInvariant();
        _path.Text = $"{scenario.Request.Connection}/{scenario.Request.Path}";
        _expect.Children.Clear();
        foreach (var text in Expected(scenario))
        {
            var pill = Ui.Pill(out var label);
            pill.Margin = new Thickness(0, 0, 6, 4);
            label.Text = text;
            label.Foreground = Ui.Muted;
            _expect.Children.Add(pill);
        }
        Show(_results.GetValueOrDefault(scenario.Id));
    }

    void Show(RunResult? result)
    {
        _checks.Children.Clear();
        if (result is null)
        {
            _request.Text = _response.Text = _seen.Text = "";
            _outcome.Text = "";
            _checks.Children.Add(Ui.Text("Not run yet. Run, or double-click it in the list.", Ui.Muted, 12));
            return;
        }
        _request.Text = result.RequestText;
        _response.Text = result.ResponseText;
        _seen.Text = result.SeenText;
        _outcome.Text = result.Outcome;
        _outcome.Foreground = result.Passed ? Ui.Ok : Ui.Danger;
        foreach (var check in result.Checks)
            _checks.Children.Add(new TextBlock
            {
                Text = $"{(check.Passed ? "✓" : "✗")} {check.What}{(check.Detail is null ? "" : $"  ({check.Detail})")}",
                Foreground = check.Passed ? Ui.Ok : Ui.Danger,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        _checks.Children.Add(Ui.Text($"{result.Elapsed.TotalMilliseconds:N0} ms", Ui.Muted, 11));
    }

    static IEnumerable<string> Expected(Scenario scenario)
    {
        var e = scenario.Expect;
        if (scenario.Flow == "hang")
            yield return $"gives up after {scenario.Request.TimeoutSeconds} s";
        if (e.Status is { } status)
            yield return scenario.Flow == "hang" ? $"{scenario.Meanwhile} → {status} meanwhile" : $"{status}";
        if (scenario.Flow == "conditional")
            yield return "costs nothing";
        if (e.PosternError is { } code)
            yield return code == "none" ? "no Postern-Error" : code;
        if (e.Upstream is { } upstream)
            yield return upstream == "reached" ? "reaches upstream" : "upstream sees nothing";
        foreach (var (name, prefix) in e.UpstreamHeaders ?? new())
            yield return $"{name}: {prefix}…";
        foreach (var name in e.UpstreamAbsent ?? [])
            yield return $"no {name} upstream";
        if (e.UpstreamBodyMatches)
            yield return "body arrives byte for byte";
        foreach (var name in e.Headers ?? [])
            yield return name;
        foreach (var (name, value) in e.HeaderEquals ?? new())
            yield return $"{name}: {value}";
        if (e.BodyMatchesSha)
            yield return "body intact";
        if (e.MinBytes is { } min)
            yield return $"≥ {min:N0} bytes";
        if (e.MaxBytes == 0)
            yield return "no body";
        if (e.JsonArrayLength is { } length)
            yield return $"{length} items";
        if (e.BodyContains is { } fragment)
            yield return $"contains {fragment}";
        if (e.StreamSpreadMs is { } spread)
            yield return $"streamed (spread ≥ {spread} ms)";
    }

    async Task RunSelected()
    {
        if (_scenarios.FirstOrDefault(s => s.Id == _selected) is { } scenario && !_running)
            await Run([scenario]);
    }

    async Task RunAll()
    {
        if (!_running)
            await Run(_scenarios);
    }

    async Task Run(IReadOnlyList<Scenario> scenarios)
    {
        _running = true;
        UpdateButtons();
        try
        {
            foreach (var scenario in scenarios)
                Mark(scenario.Id, null);
            foreach (var scenario in scenarios)
            {
                Mark(scenario.Id, null, running: true);
                var result = await _runner.RunAsync(scenario, _closing.Token);
                _results[scenario.Id] = result;
                Mark(scenario.Id, result.Passed);
                if (scenario.Id == _selected)
                    Show(result);
            }
            if (scenarios.Count > 1)
            {
                var failed = scenarios.Count(s => _results.TryGetValue(s.Id, out var r) && !r.Passed);
                Log(failed == 0 ? $"run all: all {scenarios.Count} matched" : $"run all: {failed} of {scenarios.Count} didn't match", failed == 0 ? Ui.Ok : Ui.Danger);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _running = false;
            UpdateButtons();
        }
    }

    void Mark(string id, bool? passed, bool running = false)
    {
        var mark = _items[id].Mark;
        mark.Fill = passed switch { true => Ui.Ok, false => Ui.Danger, null => Brushes.Transparent };
        mark.Stroke = running ? Ui.Accent : passed switch { true => Ui.Ok, false => Ui.Danger, null => Ui.Border };
        mark.StrokeDashArray = running ? [2, 2] : null;
    }

    void TogglePause()
    {
        _userPaused = !_userPaused;
        _pause.Content = _userPaused ? "Resume soak" : "Pause soak";
        UpdateButtons();
    }

    void UpdateButtons()
    {
        _soak.Paused = _userPaused || _running;
        var approved = _state == RegistrationState.Approved;
        _runAll.IsEnabled = _runOne.IsEnabled = approved && !_running;
        _crash.IsEnabled = _status is not null && _settings.Pipe == Target.SandboxPipe;
        _start.IsEnabled = _status is null && _settings.Pipe == Target.SandboxPipe;
    }

    void Crash()
    {
        if (_status is not { } status)
            return;
        Log($"crash: {Target.Crash(_settings.Pipe, status.ProcessId)}", Ui.Danger);
    }

    /// <summary>Every two seconds: is Postern there, which pid, where Sallyport stands with it, and is fake-upstream up.</summary>
    async Task Watch(CancellationToken stop)
    {
        Log($"looking for Postern on \\\\.\\pipe\\{_settings.Pipe} and fake-upstream on {_settings.Upstream}", Ui.Muted);
        int? lastPid = null;
        while (!stop.IsCancellationRequested)
        {
            var (state, status) = await Registration.CheckAsync(_postern, _scopes, stop);
            _status = status;
            if (status is null)
                Ui.SetPill(_pipePill, $"{_settings.Pipe} · not running", Ui.Danger);
            else
                Ui.SetPill(_pipePill, $"{_settings.Pipe} · pid {status.ProcessId}", Ui.Ok);
            if (status is not null && lastPid is { } previous && previous != status.ProcessId)
                Log($"Postern is back as pid {status.ProcessId} (was {previous})", Ui.Ok);
            if (status is not null)
                lastPid = status.ProcessId;

            if (!_asking)
                SetState(state);
            if (state == RegistrationState.NeedsApproval && !_asking && !_declined)
                _ = Register(manual: false);

            var upstreamUp = await _upstream.IsRunningAsync(stop);
            Ui.SetPill(_upstreamPill, upstreamUp ? "fake-upstream" : "fake-upstream not running", upstreamUp ? Ui.Ok : Ui.Danger);

            UpdateButtons();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    void SetState(RegistrationState state)
    {
        _state = state;
        var (text, colour) = state switch
        {
            RegistrationState.Approved => ($"approved · {_scopes.Count} scopes", Ui.Ok),
            RegistrationState.Revoked => ("revoked in Postern", Ui.Danger),
            RegistrationState.Denied => ("not approved", Ui.Danger),
            RegistrationState.NeedsApproval => ("not approved yet", Ui.Warning),
            _ => ("waiting for Postern", Ui.Muted),
        };
        Ui.SetPill(_tokenPill, text, colour);
        _register.IsVisible = state is RegistrationState.Revoked or RegistrationState.Denied;
        if (state == RegistrationState.Approved && !_soakStarted)
        {
            _soakStarted = true;
            Log($"soak: GET {_soak.Describe} every second", Ui.Muted);
            _ = RunSoak();
        }
    }

    async Task Register(bool manual)
    {
        if (_asking)
            return;
        _asking = true;
        _declined = false;
        Ui.SetPill(_tokenPill, "approve Sallyport in Postern", Ui.Warning);
        _register.IsVisible = false;
        Log(manual ? "asking Postern again" : $"asking Postern for {_scopes.Count} scopes: approve it in the Postern window", Ui.Warning);
        var (state, detail) = await Registration.RequestAsync(_postern, _scopes, _closing.Token);
        _asking = false;
        _declined = state == RegistrationState.Denied;
        Log($"registration: {detail}", state == RegistrationState.Approved ? Ui.Ok : Ui.Danger);
        SetState(state);
        UpdateButtons();
    }

    async Task RunSoak()
    {
        try
        {
            await _soak.RunAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    void OnTick(SoakTick tick)
    {
        var cell = (Border)_strip.Children[0];
        _strip.Children.RemoveAt(0);
        cell.Background = tick.Kind switch
        {
            SoakKind.Ok => Ui.Ok,
            SoakKind.Retried => Ui.Warning,
            SoakKind.Down => Ui.Danger,
            _ => Ui.Sand,
        };
        ToolTip.SetTip(cell, $"{tick.At:HH:mm:ss}  {tick.Kind}: {tick.Detail} ({tick.Ms} ms)");
        _strip.Children.Add(cell);

        if (tick.Kind != _lastKind)
        {
            var (text, colour) = tick.Kind switch
            {
                SoakKind.Ok => ($"ok again ({tick.Detail}, {tick.Ms} ms)", Ui.Ok),
                SoakKind.Retried => ($"answered after waiting {tick.Ms:N0} ms for the pipe", Ui.Warning),
                SoakKind.Down => ($"Postern unavailable: {tick.Detail}", Ui.Danger),
                _ => ($"refused by Postern: {tick.Detail}", Ui.Sand),
            };
            if (!(tick.Kind == SoakKind.Ok && _lastKind is null))
                Log(text, colour);
            _lastKind = tick.Kind;
        }
    }

    /// <summary>For snapshots: shows these results, soak ticks and log lines as if they'd happened.</summary>
    internal void Preview(PosternStatus status, IEnumerable<RunResult> results, IEnumerable<SoakTick> ticks, IEnumerable<(string Text, bool Bad)> log, string select)
    {
        _status = status;
        Ui.SetPill(_pipePill, $"{_settings.Pipe} · pid {status.ProcessId}", Ui.Ok);
        Ui.SetPill(_upstreamPill, "fake-upstream", Ui.Ok);
        _soakStarted = true;
        SetState(RegistrationState.Approved);
        foreach (var result in results)
        {
            _results[result.Scenario.Id] = result;
            Mark(result.Scenario.Id, result.Passed);
        }
        foreach (var tick in ticks)
            OnTick(tick);
        foreach (var (text, bad) in log)
            Log(text, bad ? Ui.Danger : Ui.Muted);
        UpdateButtons();
        Select(select);
    }

    internal IReadOnlyList<Scenario> LoadedScenarios => _scenarios;

    void Log(string text, IBrush colour)
    {
        _events.Children.Insert(0, new SelectableTextBlock
        {
            Text = $"{DateTime.Now:HH:mm:ss}  {text}",
            FontFamily = Ui.Mono,
            FontSize = 11.5,
            Foreground = colour,
            TextWrapping = TextWrapping.Wrap,
        });
        while (_events.Children.Count > 40)
            _events.Children.RemoveAt(_events.Children.Count - 1);
    }
}
