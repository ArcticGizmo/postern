namespace Postern.Core.Audit;

/// <summary>
/// One request through Postern, allowed or not. The query string is deliberately not kept (it can carry search
/// terms or IDs); only its length, so "same path, different query" is still visible.
/// </summary>
public sealed record AuditEntry(
    DateTimeOffset At,
    string? AppId,
    string? AppName,
    uint ProcessId,
    string? ExePath,
    string Method,
    string Provider,
    string Path,
    int QueryLength,
    int Status,
    string Outcome,
    double? UpstreamMs);

public interface IAuditLog
{
    /// <summary>Must not block the request path.</summary>
    void Write(AuditEntry entry);

    IReadOnlyList<AuditEntry> Recent(int count);
}

public sealed class InMemoryAuditLog : IAuditLog
{
    readonly List<AuditEntry> _entries = [];

    public void Write(AuditEntry entry)
    {
        lock (_entries) _entries.Add(entry);
    }

    public IReadOnlyList<AuditEntry> Recent(int count)
    {
        lock (_entries) return [.. _entries.TakeLast(count).Reverse()];
    }
}
