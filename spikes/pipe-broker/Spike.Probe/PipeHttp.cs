using System.IO.Pipes;
using System.Security.Principal;
using Spike.Shared;

namespace Spike.Probe;

internal sealed class ServerVerificationException(string message) : Exception(message);

/// <summary>
/// What a Postern client library would do: HttpClient whose every connection is the named pipe, after checking that
/// the process serving the pipe is the real broker (same user, expected exe) before sending anything.
/// </summary>
internal static class PipeHttp
{
    public static string BrokerExe => Path.Combine(AppContext.BaseDirectory, "Spike.Broker.exe");

    public static HttpClient Create(string pipe, string? expectedServerExe = null, bool allowAutoRedirect = false, int connectTimeoutMs = 2000)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                // Note: the host in the request URI is ignored here. Every connection dials the pipe.
                var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await stream.ConnectAsync(connectTimeoutMs, cancellationToken);
                    if (expectedServerExe is not null)
                        VerifyServer(stream, expectedServerExe);
                    return stream;
                }
                catch
                {
                    await stream.DisposeAsync();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://postern/"), Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>Throws unless the pipe's server process runs as us, from the expected exe. Fails closed if Windows won't say.</summary>
    public static ProcessIdentity VerifyServer(NamedPipeClientStream stream, string expectedExe)
    {
        var pid = Native.ServerProcessId(stream.SafePipeHandle);
        var server = Native.Describe(pid);
        var me = WindowsIdentity.GetCurrent().User;

        if (server.User is null || server.User != me)
            throw new ServerVerificationException($"pipe server pid {pid} runs as {server.User?.Value ?? "<unknown>"} not {me} ({server.Error})");
        if (server.ExePath is null || !string.Equals(Path.GetFullPath(server.ExePath), Path.GetFullPath(expectedExe), StringComparison.OrdinalIgnoreCase))
            throw new ServerVerificationException($"pipe server pid {pid} is {server.ExePath ?? "<unknown>"}, expected {expectedExe}");
        return server;
    }
}
