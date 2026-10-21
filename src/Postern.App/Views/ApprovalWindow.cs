using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Postern.Core.Apps;

namespace Postern.App.Views;

/// <summary>
/// "Allow this app?" The security decision of the whole product, so it's deliberately slow to say yes:
/// <list type="bullet">
/// <item>Allow is disabled for the first 2 seconds, so a stray Enter or click aimed at another window can't approve.</item>
/// <item>Deny is the default and Escape; closing the window is Deny; the broker's timeout is Deny.</item>
/// <item>Shows the exe path and publisher as Windows reports them, not anything the app says about itself (except its name).</item>
/// </list>
/// If Windows won't let it take focus (foreground lock), it flashes in the taskbar instead.
/// </summary>
internal sealed class ApprovalWindow : Window
{
    static readonly TimeSpan ArmDelay = TimeSpan.FromSeconds(2);

    readonly TaskCompletionSource<bool> _answer;
    readonly Button _allow;
    readonly DispatcherTimer _arm;
    DateTimeOffset _armsAt;

    public ApprovalWindow(ApprovalRequest request, TaskCompletionSource<bool> answer)
    {
        _answer = answer;
        Title = "Postern: allow an app?";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Background;
        Icon = TrayArt.WindowIcon(attention: true);

        _allow = Ui.Button("Allow", () => Finish(true), primary: true);
        _allow.IsEnabled = false;
        var deny = Ui.Button("Deny", () => Finish(false));
        deny.IsDefault = true;
        deny.IsCancel = true;

        var signed = request.SignatureTrusted
            ? Ui.Text(request.Signer ?? "Signed", Ui.Ok)
            : Ui.Text("Unsigned. Windows can't vouch for who made this program; check the path is one you expect.", Ui.Warning);

        var body = new StackPanel { Spacing = 14, Margin = new Thickness(22, 20) };
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
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { deny, _allow },
        });
        Content = body;

        _arm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _arm.Tick += (_, _) => Arm();
        Closed += (_, _) => Finish(false);
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
        _armsAt = DateTimeOffset.UtcNow + ArmDelay;
        _arm.Start();
        Arm();
        Activate();
        if (TryGetPlatformHandle()?.Handle is { } hwnd && GetForegroundWindow() != hwnd)
            Flash(hwnd);
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

    /// <summary>The broker gave up waiting.</summary>
    public void TimeOut() => Finish(false);

    void Finish(bool allowed)
    {
        if (!_answer.TrySetResult(allowed))
            return;
        _arm.Stop();
        Close();
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
