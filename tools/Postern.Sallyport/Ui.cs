using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace Postern.Sallyport;

/// <summary>Postern's palette (Sallyport doesn't reference the app, so it's repeated here) with Sallyport's blue as its accent.</summary>
internal static class Ui
{
    public static readonly IBrush Sunken = Brush("#101315");
    public static readonly IBrush Background = Brush("#15181B");
    public static readonly IBrush Panel = Brush("#1E2226");
    public static readonly IBrush Border = Brush("#2E343A");
    public static readonly IBrush Foreground = Brush("#E6E9EC");
    public static readonly IBrush Muted = Brush("#9AA4AE");
    public static readonly IBrush Accent = Brush("#7AA7F5");
    public static readonly IBrush Sand = Brush("#C8B27A");
    public static readonly IBrush Warning = Brush("#F5A524");
    public static readonly IBrush Danger = Brush("#EF5350");
    public static readonly IBrush Ok = Brush("#4ADE80");
    public static readonly IBrush OnAccent = Brush("#0B1A33");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static WindowIcon Icon() => new(AssetLoader.Open(new Uri("avares://sallyport/Assets/sallyport.ico")));

    public static TextBlock Text(string text, IBrush? colour = null, double size = 13) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = colour ?? Foreground,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Label(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 10.5,
        LetterSpacing = 0.6,
        Foreground = Muted,
    };

    public static SelectableTextBlock Code(string text = "", double size = 12) => new()
    {
        Text = text,
        FontFamily = Mono,
        FontSize = size,
        Foreground = Foreground,
        TextWrapping = TextWrapping.Wrap,
    };

    public static Button Button(string text, Action onClick, IBrush? fill = null)
    {
        var button = new Button
        {
            Content = text,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(12, 5),
        };
        if (fill is not null)
        {
            button.Background = fill;
            button.Foreground = OnAccent;
        }
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A rounded label whose colour carries the state.</summary>
    public static Border Pill(out TextBlock text)
    {
        text = new TextBlock { FontFamily = Mono, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        return new Border
        {
            Background = Panel,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = text,
        };
    }

    public static void SetPill(TextBlock pill, string text, IBrush colour)
    {
        pill.Text = "● " + text;
        pill.Foreground = colour;
    }

    /// <summary>A titled, scrolling box of monospace text.</summary>
    public static Border Pane(string title, Control body, TextBlock? right = null, double maxHeight = double.PositiveInfinity)
    {
        var header = new DockPanel { Margin = new Thickness(10, 6) };
        if (right is not null)
        {
            DockPanel.SetDock(right, Dock.Right);
            header.Children.Add(right);
        }
        header.Children.Add(Label(title));
        return new Border
        {
            Background = Sunken,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = new DockPanel
            {
                Children =
                {
                    Top(new Border { BorderBrush = Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = header }),
                    // Padding on the content, not the ScrollViewer, which leaves it out of the height it asks for.
                    new ScrollViewer { MaxHeight = maxHeight, Content = new Border { Padding = new Thickness(10, 8), Child = body } },
                },
            },
        };
    }

    public static Control Top(Control control)
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
    }

    public static Control Bottom(Control control)
    {
        DockPanel.SetDock(control, Dock.Bottom);
        return control;
    }
}
