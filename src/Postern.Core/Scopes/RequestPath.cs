namespace Postern.Core.Scopes;

/// <summary>
/// The provider-relative path of a request, split into segments, after the checks that stop a path from meaning one
/// thing to the scope matcher and another to the provider. Build it from the decoded path (Kestrel's
/// <c>Request.Path</c>, which decodes everything except <c>%2F</c>).
/// </summary>
public sealed class RequestPath
{
    public IReadOnlyList<string> Segments { get; }

    RequestPath(IReadOnlyList<string> segments) => Segments = segments;

    public override string ToString() => "/" + string.Join('/', Segments);

    /// <summary>
    /// Rejects: empty segments (<c>a//b</c>), dot segments, an encoded slash (<c>%2F</c>) or a backslash inside a
    /// segment, and control characters. A trailing slash is allowed and ignored.
    /// </summary>
    public static bool TryParse(string decodedPath, out RequestPath path, out string error)
    {
        path = null!;
        var trimmed = decodedPath.StartsWith('/') ? decodedPath[1..] : decodedPath;
        if (trimmed.EndsWith('/'))
            trimmed = trimmed[..^1];

        var segments = trimmed.Length == 0 ? [] : trimmed.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                error = "empty path segment";
                return false;
            }
            if (segment is "." or "..")
            {
                error = "dot segment in path";
                return false;
            }
            if (segment.Contains("%2f", StringComparison.OrdinalIgnoreCase) || segment.Contains('\\'))
            {
                error = "encoded slash or backslash in a path segment";
                return false;
            }
            if (segment.Any(char.IsControl))
            {
                error = "control character in path";
                return false;
            }
        }

        path = new RequestPath(segments);
        error = "";
        return true;
    }
}
