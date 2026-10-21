using System.Net.Http;

namespace Postern.Client;

/// <summary>
/// Adds the app token and rides out a Postern restart: if the pipe can't be connected, it retries a few times with
/// backoff. Only connect failures are retried, so a request is never sent twice. A failed server check is never retried.
/// </summary>
public sealed class PosternHandler(PosternClient client, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    static readonly TimeSpan[] Backoff = [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(PosternClient.TokenHeader) && client.Token is { } token)
            request.Headers.TryAddWithoutValidation(PosternClient.TokenHeader, token);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (IsConnectFailure(ex) && attempt < client.Options.ConnectAttempts)
            {
                await Task.Delay(Backoff[Math.Min(attempt - 1, Backoff.Length - 1)], cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.InnerException is PosternVerificationException verification)
            {
                throw verification;
            }
            catch (HttpRequestException ex) when (IsConnectFailure(ex))
            {
                throw new PosternUnavailableException($"Postern isn't answering on \\\\.\\pipe\\{client.Options.PipeName}.", ex);
            }
        }
    }

    static bool IsConnectFailure(HttpRequestException ex) =>
        ex.HttpRequestError == HttpRequestError.ConnectionError && ex.InnerException is not PosternVerificationException;
}
