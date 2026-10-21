using System.Collections.Concurrent;

namespace Spike.Broker;

internal sealed record RateLimitSnapshot(
    string Provider,
    string Resource,
    long? Limit,
    long? Remaining,
    long? Used,
    DateTimeOffset? ResetsAt,
    DateTimeOffset ObservedAt);

/// <summary>Latest rate-limit state per provider and resource bucket (GitHub: core, graphql, search, ...).</summary>
internal sealed class RateLimits
{
    readonly ConcurrentDictionary<(string Provider, string Resource), RateLimitSnapshot> _latest = new();

    public RateLimitSnapshot? Observe(string provider, HttpResponseMessage response)
    {
        var remaining = Number(response, "x-ratelimit-remaining");
        if (remaining is null)
            return null;

        var resource = Text(response, "x-ratelimit-resource") ?? "default";
        var reset = Number(response, "x-ratelimit-reset");
        var snapshot = new RateLimitSnapshot(
            provider,
            resource,
            Number(response, "x-ratelimit-limit"),
            remaining,
            Number(response, "x-ratelimit-used"),
            reset is null ? null : DateTimeOffset.FromUnixTimeSeconds(reset.Value),
            DateTimeOffset.UtcNow);
        _latest[(provider, resource)] = snapshot;
        return snapshot;
    }

    public IReadOnlyList<RateLimitSnapshot> All() => [.. _latest.Values.OrderBy(s => s.Provider).ThenBy(s => s.Resource)];

    static string? Text(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    static long? Number(HttpResponseMessage response, string name) =>
        long.TryParse(Text(response, name), out var value) ? value : null;
}
