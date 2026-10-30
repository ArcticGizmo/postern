using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Postern.App.Views;

/// <summary>
/// A modal over the main window: heading, body, an error line, and buttons at the bottom right. Escape and the close
/// box cancel (the result is null). Editing happens here rather than inline, so the page behind never reflows.
/// </summary>
internal abstract class Dialog : Window
{
    readonly TextBlock _error = Ui.Text("", Ui.Warning, 12);

    protected Dialog(string title, double width)
    {
        Title = $"Postern: {title}";
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Background = Ui.Background;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = TrayArt.WindowIcon();
        _error.IsVisible = false;
    }

    protected void Build(string heading, Control body, params Button[] actions)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        foreach (var action in actions)
            row.Children.Add(action);
        Content = new StackPanel
        {
            Margin = new Thickness(22, 20),
            Spacing = 14,
            Children = { Ui.Heading(heading), body, _error, row },
        };
    }

    protected void Fail(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    protected Button CancelButton(string text = "Cancel")
    {
        var cancel = Ui.Button(text, () => Close(null));
        cancel.IsCancel = true;
        return cancel;
    }

    /// <summary>Shows it modally over the window <paramref name="anchor"/> is in.</summary>
    public Task<T?> ShowOver<T>(Control anchor) =>
        ShowDialog<T?>(TopLevel.GetTopLevel(anchor) as Window ?? throw new InvalidOperationException("The page isn't in a window."));
}

/// <summary>"Are you sure?" for the few things that can't be undone from the page.</summary>
internal sealed class ConfirmDialog : Dialog
{
    public ConfirmDialog(string heading, string message, string action) : base(heading, 440)
    {
        var confirm = Ui.Button(action, () => Close(true));
        confirm.Background = Ui.Danger;
        confirm.Foreground = Ui.Brush("#1F0B0B");
        Build(heading, Ui.Text(message, Ui.Muted), CancelButton(), confirm);
    }
}
