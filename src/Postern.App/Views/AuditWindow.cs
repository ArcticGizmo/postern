using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Postern.Broker;
using Postern.Core.Audit;
using Postern.Core.RateLimiting;

namespace Postern.App.Views;

/// <summary>The last few hundred requests (allowed and denied) and the latest rate-limit state per provider bucket.</summary>
internal sealed class AuditWindow : Window
{
    readonly BrokerRuntime _broker;
    readonly StackPanel _limits = new() { Spacing = 2 };
    readonly StackPanel _rows = new() { Spacing = 1 };

    public AuditWindow(BrokerRuntime broker)
    {
        _broker = broker;
        Title = "Postern: audit log";
        Width = 1080;
        Height = 640;
        Background = Ui.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = TrayArt.WindowIcon();

        var refresh = Ui.Button("Refresh", Refresh);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        header.Children.Add(Ui.Heading("Audit log"));

        var top = new StackPanel { Spacing = 8, Children = { header, Ui.Field("Rate limits (latest seen)", _limits) } };
        DockPanel.SetDock(top, Dock.Top);
        Content = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                top,
                new ScrollViewer { Margin = new Thickness(0, 12, 0, 0), Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
            },
        };
        Refresh();
    }

    void Refresh()
    {
        _limits.Children.Clear();
        var limits = _broker.RateLimits.All();
        if (limits.Count == 0)
            _limits.Children.Add(Ui.Text("Nothing seen yet.", Ui.Muted, 12));
        foreach (var limit in limits)
            _limits.Children.Add(Ui.Code(Describe(limit)));

        _rows.Children.Clear();
        _rows.Children.Add(Ui.Code($"{"time",-8}  {"app",-16} {"pid",6}  {"method",-6} {"request",-52} {"status",6}  {"outcome",-22} {"ms",7}", Ui.Muted));
        foreach (var entry in _broker.Audit.Recent(300))
            _rows.Children.Add(Ui.Code(Line(entry), entry.Outcome == "allowed" ? Ui.Foreground : Ui.Warning));
    }

    static string Describe(RateLimitSnapshot s) =>
        $"{s.Provider}/{s.Resource}: {s.Remaining}/{s.Limit} left, resets {s.ResetsAt?.ToLocalTime():HH:mm}, seen {s.ObservedAt.ToLocalTime():HH:mm:ss}";

    static string Line(AuditEntry e)
    {
        var request = $"{e.Provider}{e.Path}{(e.QueryLength > 0 ? "?…" : "")}";
        return $"{e.At.ToLocalTime():HH:mm:ss}  {Fit(e.AppName ?? Path.GetFileName(e.ExePath) ?? "?", 16),-16} {e.ProcessId,6}  {e.Method,-6} {Fit(request, 52),-52} {e.Status,6}  {Fit(e.Outcome, 22),-22} {e.UpstreamMs,7:F0}";
    }

    static string Fit(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";
}
