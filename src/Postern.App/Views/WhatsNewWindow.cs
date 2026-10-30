using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Postern.Core.Changelog;

namespace Postern.App.Views;

/// <summary>
/// Shown once, on the first start after an update: the changelog sections between the old version and this one. Not
/// topmost, so it can never sit over an approval prompt.
/// </summary>
internal sealed class WhatsNewWindow : Window
{
    public WhatsNewWindow(string fromVersion, IReadOnlyList<ChangelogSection> sections, Action dontShowAgain)
    {
        Title = "What's new in Postern";
        Width = 560;
        Height = 600;
        MinWidth = 420;
        MinHeight = 320;
        Background = Ui.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = TrayArt.WindowIcon();

        var list = new StackPanel { Spacing = 6 };
        for (var i = 0; i < sections.Count; i++)
        {
            if (i > 0)
                list.Children.Add(new Border { Height = 1, Background = Ui.Border, Margin = new Thickness(0, 8) });
            ChangelogMarkdown.Render(list, sections[i].Block);
        }

        var close = Ui.Button("Close", Close, primary: true);
        close.IsDefault = true;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 14, 0, 0),
            Children =
            {
                Ui.Button("Don't show again", () =>
                {
                    dontShowAgain();
                    Close();
                }),
                close,
            },
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        ChangelogParser.TryParseVersion(fromVersion, out var from);
        var subhead = sections.Count == 1
            ? $"Updated from {from} to {sections[0].Display}."
            : $"Updated from {from} to {sections[0].Display}: {sections.Count} releases.";

        Content = new DockPanel
        {
            Margin = new Thickness(22, 20),
            Children =
            {
                buttons,
                Ui.Top(new StackPanel
                {
                    Spacing = 2,
                    Margin = new Thickness(0, 0, 0, 6),
                    Children = { Ui.Heading("What's new in Postern"), Ui.Text(subhead, Ui.Muted) },
                }),
                new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            },
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
