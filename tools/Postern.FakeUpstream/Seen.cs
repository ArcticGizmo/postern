using System.Security.Cryptography;

namespace Postern.FakeUpstream;

/// <summary>One request as it arrived from Postern: what a test bed checks Postern did (or didn't) send.</summary>
public sealed class SeenRequest
{
    public required DateTimeOffset At { get; init; }
    public required string? Id { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string Query { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required long BodyBytes { get; init; }
    public required string? BodySha256 { get; init; }
    public string? Fault { get; set; }
    public int? Status { get; set; }
}

/// <summary>The last few hundred requests, newest first. Bodies are hashed, not kept.</summary>
public sealed class SeenLog
{
    public const string IdHeader = "X-Testbed-Id";
    const int Capacity = 300;

    readonly LinkedList<SeenRequest> _entries = new();

    public async Task<SeenRequest> Record(HttpRequest request)
    {
        request.EnableBuffering();
        long bytes = 0;
        string? sha = null;
        if (request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                bytes += read;
            }
            sha = bytes > 0 ? Convert.ToHexStringLower(hash.GetHashAndReset()) : null;
            request.Body.Position = 0;
        }

        var entry = new SeenRequest
        {
            At = DateTimeOffset.Now,
            Id = request.Headers[IdHeader].FirstOrDefault(),
            Method = request.Method,
            Path = request.Path.Value ?? "/",
            Query = request.QueryString.Value ?? "",
            Headers = request.Headers.ToDictionary(h => h.Key, h => Masking.IsCredential(h.Key) ? Masking.Mask(h.Value) : h.Value.ToString()),
            BodyBytes = bytes,
            BodySha256 = sha,
        };
        lock (_entries)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > Capacity)
                _entries.RemoveLast();
        }
        return entry;
    }

    public IReadOnlyList<SeenRequest> Recent(string? id, int count)
    {
        lock (_entries)
            return [.. _entries.Where(e => id is null || e.Id == id).Take(Math.Clamp(count, 1, Capacity))];
    }

    public void Clear()
    {
        lock (_entries)
            _entries.Clear();
    }
}

public static class Masking
{
    static readonly string[] CredentialHeaders = ["Authorization", "x-api-key", "X-Octopus-ApiKey", "DD-API-KEY", "DD-APPLICATION-KEY"];

    public static bool IsCredential(string header) => CredentialHeaders.Contains(header, StringComparer.OrdinalIgnoreCase);

    /// <summary>Enough to see which credential arrived (sandbox values all start the same way), not enough to reuse.</summary>
    public static string Mask(string? value) => value switch
    {
        null or "" => "<none>",
        { Length: > 12 } => value[..10] + "…",
        _ => value,
    };
}
