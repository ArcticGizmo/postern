using System.Collections.Concurrent;

namespace Postern.Core.RateLimiting;

public sealed record RateLimitSnapshot(
    string Provider,
    string Resource,
    long? Limit,
    long? Remaining,
    long? Used,
    DateTimeOffset? ResetsAt,
    DateTimeOffset ObservedAt);

/// <summary>
/// Latest rate-limit state per provider and resource bucket (GitHub: core, graphql, search, ...). Postern sees all
/// traffic, so this is the whole machine's budget, not one app's view of it.
/// </summary>
public sealed class RateLimits(TimeProvider? time = null)
{
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly ConcurrentDictionary<(string Provider, string Resource), RateLimitSnapshot> _latest = new();

    /// <summary>Reads GitHub-style x-ratelimit-* headers. Returns null when the response carries none.</summary>
    public RateLimitSnapshot? Observe(string provider, Func<string, string?> header)
    {
        var remaining = Number(header("x-ratelimit-remaining"));
        if (remaining is null)
            return null;

        var reset = Number(header("x-ratelimit-reset"));
        var snapshot = new RateLimitSnapshot(
            provider,
            header("x-ratelimit-resource") ?? "default",
            Number(header("x-ratelimit-limit")),
            remaining,
            Number(header("x-ratelimit-used")),
            reset is null ? null : DateTimeOffset.FromUnixTimeSeconds(reset.Value),
            _time.GetUtcNow());
        _latest[(provider, snapshot.Resource)] = snapshot;
        return snapshot;
    }

    public IReadOnlyList<RateLimitSnapshot> All() => [.. _latest.Values.OrderBy(s => s.Provider).ThenBy(s => s.Resource)];

    static long? Number(string? text) => long.TryParse(text, out var value) ? value : null;
}
