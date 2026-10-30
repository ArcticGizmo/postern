using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Postern.App.Views;

/// <summary>Palette and small control factories. Windows are built in code, like Shrike's, so there's no binding setup to get wrong.</summary>
internal static class Ui
{
    public static readonly IBrush Sunken = Brush("#101315");
    public static readonly IBrush Background = Brush("#15181B");
    public static readonly IBrush Panel = Brush("#1E2226");
    public static readonly IBrush Border = Brush("#2E343A");
    public static readonly IBrush Foreground = Brush("#E6E9EC");
    public static readonly IBrush Muted = Brush("#9AA4AE");
    public static readonly IBrush Accent = Brush("#4FB8A8");
    public static readonly IBrush Warning = Brush("#F5A524");
    public static readonly IBrush Danger = Brush("#EF5350");
    public static readonly IBrush Ok = Brush("#4ADE80");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 18,
        FontWeight = FontWeight.SemiBold,
        Foreground = Foreground,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Text(string text, IBrush? colour = null, double size = 13) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = colour ?? Foreground,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Code(string text, IBrush? colour = null) => new()
    {
        Text = text,
        FontFamily = Mono,
        FontSize = 12.5,
        Foreground = colour ?? Foreground,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>A muted label above a value.</summary>
    public static Control Field(string label, Control value) => new StackPanel
    {
        Spacing = 2,
        Children = { Text(label.ToUpperInvariant(), Muted, 10.5), value },
    };

    public static Button Button(string text, Action onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(14, 6),
        };
        if (primary)
        {
            button.Background = Accent;
            button.Foreground = Brush("#0B1F1C");
        }
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Border Card(Control child) => new()
    {
        Background = Panel,
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12),
        Child = child,
    };

    public static Control Top(Control control)
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
    }

    public static Window Window(string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        Background = Background,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Icon = TrayArt.WindowIcon(),
    };
}
