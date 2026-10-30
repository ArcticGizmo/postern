using System.Text.RegularExpressions;

namespace Postern.Core.Changelog;

/// <summary>
/// One <c>## </c> section of <c>CHANGELOG.md</c>: its lines (heading included, without the trailing blank lines and
/// <c>---</c> rule) and the version in its heading. <c>## [Unreleased]</c> has no version, so it's never "new".
/// </summary>
public sealed record ChangelogSection(string Heading, string Display, Version? Version, IReadOnlyList<string> Block);

/// <summary>
/// Splits the changelog the app embeds into versions, and picks the ones an update brought in. Headings look like
/// <c>## [v0.2.0] - 2026-11-02</c>.
/// </summary>
public static partial class ChangelogParser
{
    [GeneratedRegex(@"v?(\d+(?:\.\d+){1,3})")]
    private static partial Regex VersionPattern();

    /// <summary>Every section, in the file's order (newest first).</summary>
    public static IReadOnlyList<ChangelogSection> Parse(string markdown)
    {
        var sections = new List<ChangelogSection>();
        List<string>? block = null;
        var heading = "";

        void Flush()
        {
            if (block is null)
                return;
            var end = block.Count;
            while (end > 0 && (block[end - 1].Trim().Length == 0 || block[end - 1].Trim() == "---"))
                end--;
            var version = TryParseVersion(heading, out var display);
            sections.Add(new ChangelogSection(heading, display, version, block.GetRange(0, end)));
        }

        foreach (var raw in (markdown ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = line[3..].Trim();
                block = [line];
            }
            else
            {
                block?.Add(line);
            }
        }
        Flush();
        return sections;
    }

    /// <summary>
    /// The sections newer than <paramref name="lastSeen"/> and no newer than <paramref name="current"/>, newest
    /// first. Nothing when <paramref name="lastSeen"/> isn't a version: a fresh install has nothing to compare with.
    /// </summary>
    public static IReadOnlyList<ChangelogSection> UnseenSince(string markdown, string? lastSeen, string current)
    {
        if (TryParseVersion(lastSeen, out _) is not { } low)
            return [];
        var high = TryParseVersion(current, out _);
        return [.. Parse(markdown).Where(s => s.Version is { } v && v > low && (high is null || v <= high))];
    }

    /// <summary>The version in text like <c>[v0.2.0] - 2026-11-02</c> or <c>0.2.0</c>, shown as <c>v0.2.0</c>.</summary>
    public static Version? TryParseVersion(string? text, out string display)
    {
        display = text ?? "";
        var match = VersionPattern().Match(text ?? "");
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version))
            return null;
        display = "v" + match.Groups[1].Value;
        return version;
    }
}
