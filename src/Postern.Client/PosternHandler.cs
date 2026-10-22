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
        // The pipe connection ignores the URI's host, so anything else (typically a redirect an SDK follows itself,
        // like Octokit's archive downloads to https://codeload.github.com) would be dialled into Postern. For https
        // that's a TLS handshake against plain HTTP, which fails with a baffling SSL error. Refuse it here, clearly,
        // and before the app token is attached.
        if (request.RequestUri is not { Scheme: "http", Host: "postern" } uri)
            throw new HttpRequestException(
                $"Postern only carries requests to http://postern/...; this one is for {request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "(no URI)"}. " +
                "If it's a redirect to a pre-signed download URL, fetch that URL directly, without Postern.");

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
                throw new PosternUnavailableException($"Postern isn't answering on \\\\.\\pipe\\{client.Options.PipeName}. {PosternClient.NotRunningHint}", ex);
            }
        }
    }

    // HttpClient.Send (sync) calls this, not SendAsync. DelegatingHandler's default would pass straight to the inner
    // handler and skip the host check, the token and the retry above, so route it through the same path.
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    static bool IsConnectFailure(HttpRequestException ex) =>
        ex.HttpRequestError == HttpRequestError.ConnectionError && ex.InnerException is not PosternVerificationException;
}
