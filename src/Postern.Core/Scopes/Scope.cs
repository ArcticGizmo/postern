using System.Text.RegularExpressions;

namespace Postern.Core.Scopes;

/// <summary>
/// One permission: <c>METHOD provider/path-glob</c>, for example <c>GET github/repos/*/*/pulls/**</c>.
/// <list type="bullet">
/// <item><c>*</c> matches exactly one path segment.</item>
/// <item><c>**</c> matches zero or more segments.</item>
/// <item>Anything else is a literal segment, compared case-insensitively (GitHub owner/repo names are).</item>
/// <item>The method may be <c>*</c> (any). A <c>GET</c> scope also allows <c>HEAD</c>.</item>
/// <item>The query string is never part of the match.</item>
/// </list>
/// Partial wildcards inside a segment (<c>pull*</c>) aren't supported, on purpose: they're where globs get clever.
/// </summary>
public sealed partial record Scope(string Method, string Provider, IReadOnlyList<string> Pattern)
{
    /// <summary>Keeps backtracking bounded: matching is O(n^k) in the number of ** segments.</summary>
    const int MaxDoubleStars = 3;

    public override string ToString() => $"{Method} {Provider}/{string.Join('/', Pattern)}";

    public static Scope Parse(string text) =>
        TryParse(text, out var scope, out var error) ? scope : throw new FormatException(error);

    public static bool TryParse(string text, out Scope scope, out string error)
    {
        scope = null!;
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            error = $"'{text}': expected 'METHOD provider/path'";
            return false;
        }

        var method = parts[0].ToUpperInvariant();
        if (method != "*" && !MethodPattern().IsMatch(method))
        {
            error = $"'{text}': '{parts[0]}' isn't an HTTP method";
            return false;
        }

        var segments = parts[1].Split('/');
        var provider = segments[0];
        if (!ProviderName.IsValid(provider))
        {
            error = $"'{text}': '{provider}' isn't a provider name (lowercase letters, digits, '-')";
            return false;
        }

        var pattern = segments[1..];
        if (pattern.Count(s => s == "**") > MaxDoubleStars)
        {
            error = $"'{text}': at most {MaxDoubleStars} '**' per scope";
            return false;
        }
        foreach (var segment in pattern)
        {
            if (segment.Length == 0)
            {
                error = $"'{text}': empty path segment";
                return false;
            }
            if (segment is not ("*" or "**") && (segment.Contains('*') || segment is "." or ".."))
            {
                error = $"'{text}': '{segment}' — wildcards must be a whole segment (* or **)";
                return false;
            }
        }

        scope = new Scope(method, provider, pattern);
        error = "";
        return true;
    }

    /// <summary>True if this scope allows <paramref name="method"/> on <paramref name="provider"/> + <paramref name="path"/>.</summary>
    public bool Allows(string method, string provider, RequestPath path)
    {
        if (!string.Equals(provider, Provider, StringComparison.Ordinal))
            return false;
        if (!MethodMatches(method))
            return false;
        return Match(Pattern, 0, path.Segments, 0, new HashSet<(int, int)>());
    }

    bool MethodMatches(string method)
    {
        if (Method == "*")
            return true;
        if (string.Equals(method, Method, StringComparison.OrdinalIgnoreCase))
            return true;
        return Method == "GET" && string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
    }

    // failed remembers (pattern index, path index) pairs already known not to match, so ** backtracking is O(P×S).
    static bool Match(IReadOnlyList<string> pattern, int p, IReadOnlyList<string> path, int s, HashSet<(int, int)> failed)
    {
        while (p < pattern.Count)
        {
            if (pattern[p] == "**")
            {
                // Collapse consecutive ** and try every possible split.
                while (p < pattern.Count && pattern[p] == "**")
                    p++;
                if (p == pattern.Count)
                    return true;
                for (var i = s; i <= path.Count; i++)
                {
                    if (failed.Contains((p, i)))
                        continue;
                    if (Match(pattern, p, path, i, failed))
                        return true;
                    failed.Add((p, i));
                }
                return false;
            }

            if (s == path.Count)
                return false;
            if (pattern[p] != "*" && !string.Equals(pattern[p], path[s], StringComparison.OrdinalIgnoreCase))
                return false;
            p++;
            s++;
        }
        return s == path.Count;
    }

    [GeneratedRegex("^[A-Z]{3,10}$")]
    private static partial Regex MethodPattern();
}
