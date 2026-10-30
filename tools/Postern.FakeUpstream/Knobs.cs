namespace Postern.FakeUpstream;

/// <summary>A fault to inject into the next requests for a service ("*" for all of them).</summary>
public sealed record Knob(string Service, string Fault, int? Seconds = null, int? Count = null)
{
    static readonly string[] Faults = ["429", "500", "hang", "slow", "drop"];

    public string? Problem() =>
        !Faults.Contains(Fault) ? $"fault must be one of {string.Join(", ", Faults)}"
        : string.IsNullOrWhiteSpace(Service) ? "service is required (\"*\" for every service)"
        : Count is <= 0 ? "count must be at least 1 (leave it out to keep the fault until it's deleted)"
        : Seconds is < 0 or > 600 ? "seconds must be 0–600"
        : null;

    public string Describe() => Fault switch
    {
        "hang" => $"knob: hang {Seconds ?? 120} s",
        "slow" => $"knob: slow {Seconds ?? 3} s",
        _ => $"knob: {Fault}",
    };

    /// <summary>Applies the fault. True when it wrote the whole response, so the request goes no further.</summary>
    public async Task<bool> Apply(HttpContext context)
    {
        switch (Fault)
        {
            case "429":
                context.Response.StatusCode = 429;
                context.Response.Headers.RetryAfter = "30";
                await context.Response.WriteAsJsonAsync(new { message = "API rate limit exceeded (fake-upstream knob)" });
                return true;
            case "500":
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { message = "fake-upstream: knob 500" });
                return true;
            case "drop":
                context.Abort();
                return true;
            case "hang":
                await Delay(Seconds ?? 120, context.RequestAborted);
                return false;
            default:
                await Delay(Seconds ?? 3, context.RequestAborted);
                return false;
        }
    }

    static async Task Delay(int seconds, CancellationToken aborted)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), aborted);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public sealed class Knobs
{
    readonly List<(Knob Knob, int? Left)> _knobs = [];

    public void Add(Knob knob)
    {
        lock (_knobs)
        {
            _knobs.RemoveAll(k => k.Knob.Service == knob.Service);
            _knobs.Add((knob, knob.Count));
        }
    }

    public IReadOnlyList<object> All()
    {
        lock (_knobs)
            return [.. _knobs.Select(k => new { k.Knob.Service, k.Knob.Fault, k.Knob.Seconds, left = k.Left })];
    }

    public void Clear()
    {
        lock (_knobs)
            _knobs.Clear();
    }

    /// <summary>The fault for this request, if any, using up one of its count.</summary>
    public Knob? Take(string service)
    {
        lock (_knobs)
        {
            var i = _knobs.FindIndex(k => k.Knob.Service == service);
            if (i < 0)
                i = _knobs.FindIndex(k => k.Knob.Service == "*");
            if (i < 0)
                return null;
            var (knob, left) = _knobs[i];
            if (left is { } n)
            {
                if (n <= 1)
                    _knobs.RemoveAt(i);
                else
                    _knobs[i] = (knob, n - 1);
            }
            return knob;
        }
    }
}

/// <summary>GitHub-style rate-limit headers on every response. A 304 is free, as on GitHub; a 429 says none are left.</summary>
public sealed class RateLimit
{
    const int Limit = 5000;
    int _remaining = Limit;

    public void Stamp(HttpContext context)
    {
        var status = context.Response.StatusCode;
        int remaining;
        if (status == 429)
            remaining = 0;
        else if (status == 304)
            remaining = Volatile.Read(ref _remaining);
        else if ((remaining = Interlocked.Decrement(ref _remaining)) <= 0)
            Interlocked.Exchange(ref _remaining, remaining = Limit); // a new window
        var headers = context.Response.Headers;
        headers["x-ratelimit-limit"] = Limit.ToString();
        headers["x-ratelimit-remaining"] = remaining.ToString();
        headers["x-ratelimit-used"] = (Limit - remaining).ToString();
        headers["x-ratelimit-reset"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString();
        headers["x-ratelimit-resource"] = context.Request.Path.Value?.EndsWith("/graphql", StringComparison.Ordinal) == true ? "graphql" : "core";
    }
}
