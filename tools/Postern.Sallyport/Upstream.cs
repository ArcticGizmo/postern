using System.Net.Http.Json;
using System.Text.Json;

namespace Postern.Sallyport;

/// <summary>A request as fake-upstream received it (from its /_seen).</summary>
public sealed record SeenRequest(
    string Method, string Path, string Query, Dictionary<string, string> Headers, long BodyBytes, string? BodySha256, string? Fault, int? Status);

/// <summary>
/// fake-upstream's controls, called directly on loopback (not through Postern): what it received, and the faults to
/// inject. These are the far side of every check, so a scenario can prove what did or didn't leave Postern.
/// </summary>
public sealed class Upstream(Uri baseUrl) : IDisposable
{
    public const string IdHeader = "X-Testbed-Id";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly HttpClient _http = new() { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(5) };

    public Uri BaseUrl => baseUrl;

    /// <summary>What arrived carrying this id, newest first; null when fake-upstream isn't answering.</summary>
    public async Task<IReadOnlyList<SeenRequest>?> SeenAsync(string id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<SeenRequest>>($"_seen?id={Uri.EscapeDataString(id)}", Json, cancellationToken) ?? [];
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("_knobs", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task SetKnobAsync(KnobSetting knob, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("_knobs", knob, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task ClearKnobsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await _http.DeleteAsync("_knobs", cancellationToken);
        }
        catch (HttpRequestException)
        {
        }
    }

    public void Dispose() => _http.Dispose();
}
