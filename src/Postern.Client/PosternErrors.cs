using System.Net.Http.Json;
using System.Text.Json;

namespace Postern.Client;

/// <summary>An error raised by Postern itself (as opposed to the provider), from the Postern-Error header.</summary>
public sealed record PosternError(int Status, string Code, string Detail)
{
    public const string Header = "Postern-Error";

    // Codes worth branching on. The full list is in the broker (AccessPolicy, ProxyEndpoint, ControlEndpoints).
    public const string NoToken = "no-token";
    public const string UnknownToken = "unknown-token";
    public const string TokenSuperseded = "token-superseded";
    public const string AppRevoked = "app-revoked";
    public const string ScopeDenied = "scope-denied";
    public const string CredentialMissing = "credential-missing";

    /// <summary>True when the app should register again (its token is gone or no longer valid).</summary>
    public bool NeedsRegistration => Code is NoToken or UnknownToken or TokenSuperseded;
}

public static class PosternResponseExtensions
{
    /// <summary>The Postern error on this response, or null if Postern let it through (whatever the provider said).</summary>
    public static async Task<PosternError?> GetPosternErrorAsync(this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        if (!response.Headers.TryGetValues(PosternError.Header, out var values))
            return null;
        var code = values.First();
        var detail = code;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (body.TryGetProperty("detail", out var d) && d.GetString() is { } text)
                detail = text;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
        }
        return new PosternError((int)response.StatusCode, code, detail);
    }
}

/// <summary>Postern isn't reachable (not running, not Windows) or isn't who it should be.</summary>
public class PosternUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Something answered on the pipe, but it isn't the Postern this user runs. Never retried.</summary>
public sealed class PosternVerificationException(string message) : PosternUnavailableException(message);
