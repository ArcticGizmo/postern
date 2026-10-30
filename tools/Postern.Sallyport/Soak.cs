using System.Diagnostics;
using Postern.Client;

namespace Postern.Sallyport;

public enum SoakKind
{
    /// <summary>Answered quickly.</summary>
    Ok,
    /// <summary>Answered, but only after the client waited for the pipe: Postern was restarting.</summary>
    Retried,
    /// <summary>Postern wasn't there.</summary>
    Down,
    /// <summary>Postern answered with its own refusal (revoked, say).</summary>
    Refused,
}

public sealed record SoakTick(DateTimeOffset At, SoakKind Kind, string Detail, long Ms);

/// <summary>
/// The same small request once a second, to watch what an app sees while Postern is crashed, quit, restarted or
/// revoked. It pauses while scenarios run, so their rate-limit and fault checks aren't disturbed.
/// </summary>
public sealed class Soak(PosternClient postern, string connection = "github", string path = "user")
{
    // A pipe connect that succeeds first time takes a few ms; one that needed the client's retries takes 250 ms or more.
    static readonly TimeSpan RetriedAfter = TimeSpan.FromMilliseconds(220);

    public event Action<SoakTick>? Ticked;

    public bool Paused { get; set; }

    public string Describe => $"{connection}/{path}";

    public async Task RunAsync(CancellationToken stop)
    {
        using var http = postern.CreateHttpClient(connection);
        http.Timeout = TimeSpan.FromSeconds(10);
        while (!stop.IsCancellationRequested)
        {
            var next = Task.Delay(TimeSpan.FromSeconds(1), stop);
            if (!Paused)
                Ticked?.Invoke(await Once(http, stop));
            try
            {
                await next;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    async Task<SoakTick> Once(HttpClient http, CancellationToken stop)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync(path, stop);
            var ms = clock.ElapsedMilliseconds;
            if (response.Headers.TryGetValues(PosternError.Header, out var codes))
                return new(DateTimeOffset.Now, SoakKind.Refused, codes.First(), ms);
            var kind = clock.Elapsed >= RetriedAfter ? SoakKind.Retried : SoakKind.Ok;
            return new(DateTimeOffset.Now, kind, $"{(int)response.StatusCode}", ms);
        }
        catch (PosternUnavailableException ex)
        {
            return new(DateTimeOffset.Now, SoakKind.Down, ex.Message, clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !stop.IsCancellationRequested)
        {
            return new(DateTimeOffset.Now, SoakKind.Down, ex.Message, clock.ElapsedMilliseconds);
        }
    }
}
