using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Postern.Core.Apps;

namespace Postern.App.Views;

/// <summary>One request in the approval queue, and the answer the broker is waiting on.</summary>
internal sealed class PendingApproval(ApprovalRequest request)
{
    public ApprovalRequest Request { get; } = request;
    public DateTimeOffset ArrivedAt { get; } = DateTimeOffset.Now;
    public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// "Allow this app?" The security decision of the whole product, so it's deliberately slow to say yes:
/// <list type="bullet">
/// <item>Allow is disabled for the first 2 seconds of every request, so a stray Enter or click aimed at another window
/// can't approve, and a double-click on one request can't approve the next one as it slides into place.</item>
/// <item>Deny is the default and Escape; closing the window denies everything in it; the broker's timeout is Deny.</item>
/// <item>Shows the exe path and publisher as Windows reports them, not anything the app says about itself (except its name).</item>
/// </list>
/// Requests queue in arrival order in this one window (a full stack starting up asks several at once), and there's
/// no "Allow all": each one is read and approved on its own.
/// If Windows won't let it take focus (foreground lock), it flashes in the taskbar instead.
/// </summary>
internal sealed class ApprovalWindow : Window
{
    static readonly TimeSpan ArmDelay = TimeSpan.FromSeconds(2);

    readonly List<PendingApproval> _queue = [];
    readonly Button _allow;
    readonly Button _denyAll;
    readonly Border _current = new();
    readonly StackPanel _waiting = new() { Spacing = 4 };
    readonly Control _waitingSection;
    readonly DispatcherTimer _arm;
    DateTimeOffset _armsAt;

    public ApprovalWindow()
    {
        Title = "Postern: allow an app?";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Background;
        Icon = TrayArt.WindowIcon(attention: true);

        _allow = Ui.Button("Allow", () => Decide(true), primary: true);
        _allow.IsEnabled = false;
        var deny = Ui.Button("Deny", () => Decide(false));
        deny.IsDefault = true;
        deny.IsCancel = true;
        _denyAll = Ui.Button("Deny all", DenyAll);
        DockPanel.SetDock(_denyAll, Dock.Left);

        _waitingSection = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new Separator { Background = Ui.Border, Margin = new Thickness(0, 4) },
                new ScrollViewer { MaxHeight = 150, Content = _waiting },
            },
        };

        Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(22, 20),
            Children =
            {
                _current,
                new DockPanel
                {
                    Children =
                    {
                        _denyAll,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 10,
                            Children = { deny, _allow },
                        },
                    },
                },
                _waitingSection,
            },
        };

        _arm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _arm.Tick += (_, _) => Arm();
        Closing += (_, _) => IsDone = true;
        Closed += (_, _) =>
        {
            _arm.Stop();
            foreach (var item in _queue)
                item.Answer.TrySetResult(false);
            _queue.Clear();
        };
    }

    /// <summary>Closing or closed: new requests need a new window.</summary>
    public bool IsDone { get; private set; }

    public void Enqueue(PendingApproval item)
    {
        _queue.Add(item);
        if (_queue.Count == 1)
            ShowCurrent();
        else
            RefreshQueue();
        if (IsVisible)
            FlashUnlessForeground();
    }

    /// <summary>The broker stopped waiting for this one (timeout, or the app asked again).</summary>
    public void Withdraw(PendingApproval item)
    {
        var index = _queue.IndexOf(item);
        if (index < 0)
            return;
        _queue.RemoveAt(index);
        item.Answer.TrySetResult(false);
        if (index == 0)
            Advance();
        else
            RefreshQueue();
    }

    void Decide(bool allowed)
    {
        if (_queue.Count == 0 || (allowed && !_allow.IsEnabled))
            return;
        var item = _queue[0];
        _queue.RemoveAt(0);
        item.Answer.TrySetResult(allowed);
        Advance();
    }

    void DenyAll()
    {
        foreach (var item in _queue)
            item.Answer.TrySetResult(false);
        _queue.Clear();
        Advance();
    }

    void Advance()
    {
        if (_queue.Count == 0)
        {
            IsDone = true;
            Close();
            return;
        }
        ShowCurrent();
    }

    void ShowCurrent()
    {
        _current.Child = Describe(_queue[0].Request);
        _armsAt = DateTimeOffset.UtcNow + ArmDelay;
        _allow.IsEnabled = false;
        Arm();
        _arm.Start();
        RefreshQueue();
    }

    void RefreshQueue()
    {
        Title = _queue.Count > 1 ? $"Postern: allow an app? (1 of {_queue.Count})" : "Postern: allow an app?";
        _denyAll.IsVisible = _queue.Count > 1;
        _waitingSection.IsVisible = _queue.Count > 1;
        _waiting.Children.Clear();
        if (_queue.Count <= 1)
            return;

        _waiting.Children.Add(Ui.Text($"{_queue.Count} requests, in the order they arrived", Ui.Muted, 11.5));
        for (var i = 0; i < _queue.Count; i++)
        {
            var item = _queue[i];
            var line = $"{(i == 0 ? "▶" : " ")} {i + 1,2}. {item.ArrivedAt:HH:mm:ss}  {item.Request.Name}  ({Path.GetFileName(item.Request.ExePath)}, pid {item.Request.ProcessId})";
            _waiting.Children.Add(Ui.Code(line, i == 0 ? Ui.Foreground : Ui.Muted));
        }
    }

    static Control Describe(ApprovalRequest request)
    {
        var signed = request.SignatureTrusted
            ? Ui.Text(request.Signer ?? "Signed", Ui.Ok)
            : Ui.Text("Unsigned. Windows can't vouch for who made this program; check the path is one you expect.", Ui.Warning);

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(Ui.Heading($"“{request.Name}” wants to call services through Postern"));
        if (request.PreviousScopes is not null)
            body.Children.Add(Ui.Text("It already has access. Approving replaces that access (and its token) with the list below.", Ui.Warning));
        body.Children.Add(Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Field("Program", Ui.Code(request.ExePath)),
                Ui.Field("Publisher", signed),
                Ui.Field("Process", Ui.Code($"pid {request.ProcessId}")),
            },
        }));
        body.Children.Add(Ui.Field("It asks for", ScopeList(request)));
        body.Children.Add(Ui.Text("Postern adds your stored credential to each allowed request. The app never sees the secret. " +
                                  "You can revoke this at any time from the tray (Apps…).", Ui.Muted, 12));
        return body;
    }

    static Control ScopeList(ApprovalRequest request)
    {
        var previous = request.PreviousScopes?.Select(s => s.ToString()).ToHashSet() ?? [];
        var list = new StackPanel { Spacing = 4 };
        foreach (var scope in request.Scopes)
        {
            var isNew = request.PreviousScopes is not null && !previous.Contains(scope.ToString());
            list.Children.Add(Ui.Code((isNew ? "+ " : "  ") + scope, isNew ? Ui.Warning : Ui.Foreground));
        }
        if (request.PreviousScopes is not null)
            foreach (var gone in request.PreviousScopes.Where(s => !request.Scopes.Any(n => n.ToString() == s.ToString())))
                list.Children.Add(Ui.Code("- " + gone, Ui.Muted));
        return Ui.Card(list);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Activate();
        FlashUnlessForeground();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Re-arm the delay on any key while disarmed, so holding Enter through the prompt doesn't approve it.
        if (!_allow.IsEnabled)
            _armsAt = DateTimeOffset.UtcNow + ArmDelay;
        base.OnKeyDown(e);
    }

    void Arm()
    {
        var left = _armsAt - DateTimeOffset.UtcNow;
        if (left > TimeSpan.Zero)
        {
            _allow.Content = $"Allow ({Math.Ceiling(left.TotalSeconds):F0})";
            return;
        }
        _arm.Stop();
        _allow.Content = "Allow";
        _allow.IsEnabled = true;
    }

    void FlashUnlessForeground()
    {
        if (TryGetPlatformHandle()?.Handle is { } hwnd && GetForegroundWindow() != hwnd)
            Flash(hwnd);
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FlashWindowEx(ref FlashInfo info);

    static void Flash(IntPtr hwnd)
    {
        const uint flashAll = 3, untilForeground = 12;
        var info = new FlashInfo { cbSize = (uint)Marshal.SizeOf<FlashInfo>(), hwnd = hwnd, dwFlags = flashAll | untilForeground };
        FlashWindowEx(ref info);
    }
}
