using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Postern.App.Views;

/// <summary>
/// The changelog embedded in Postern.exe, drawn as text. Only the markdown <c>CHANGELOG.md</c> uses is handled:
/// <c>##</c> and <c>###</c> headings, bullets, quotes, <c>---</c> rules and inline emphasis, code and links.
/// </summary>
internal static partial class ChangelogMarkdown
{
    /// <summary>Null if the build didn't embed it.</summary>
    public static string? LoadEmbedded()
    {
        using var stream = typeof(ChangelogMarkdown).Assembly.GetManifestResourceStream("Postern.CHANGELOG.md");
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static void Render(StackPanel page, IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
                page.Children.Add(new TextBlock
                {
                    Text = StripInline(line[3..]),
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Ui.Foreground,
                    Margin = new Thickness(0, 10, 0, 2),
                });
            else if (line.StartsWith("### ", StringComparison.Ordinal))
                page.Children.Add(new TextBlock
                {
                    Text = StripInline(line[4..]),
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Ui.Foreground,
                    Margin = new Thickness(0, 6, 0, 0),
                });
            else if (line.StartsWith("# ", StringComparison.Ordinal))
                continue; // the page already has a heading
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                page.Children.Add(Ui.Text("•  " + StripInline(line[2..])));
            else if (line == "---")
                page.Children.Add(new Border { Height = 1, Background = Ui.Border, Margin = new Thickness(0, 8) });
            else if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                var quote = Ui.Text(StripInline(line[2..]), Ui.Muted);
                quote.FontStyle = FontStyle.Italic;
                quote.Margin = new Thickness(12, 0, 0, 0);
                page.Children.Add(quote);
            }
            else if (line.Trim().Length > 0)
                page.Children.Add(Ui.Text(StripInline(line), Ui.Muted));
        }
    }

    public static string StripInline(string text)
    {
        var parts = text.Split('`');
        // Odd parts are inside `code`, where a scope's * is literal and mustn't be read as emphasis.
        for (var i = 0; i < parts.Length; i += 2)
            parts[i] = Link().Replace(Italic().Replace(Bold().Replace(parts[i], "$1"), "$1"), "$1");
        return string.Concat(parts);
    }

    [GeneratedRegex(@"(?:\*\*|__)(.*?)(?:\*\*|__)")]
    private static partial Regex Bold();

    // Not inside a word, so snake_case survives.
    [GeneratedRegex(@"(?<![\w*])[*_](?![\s*_])(.*?)(?<![\s*_])[*_](?![\w*])")]
    private static partial Regex Italic();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();
}
