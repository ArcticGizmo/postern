using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Postern.Interop;

namespace Postern.Client;

/// <summary>Opens the pipe and checks, before a byte is sent, that the server is this user's Postern.</summary>
[SupportedOSPlatform("windows")]
internal static class PipeConnector
{
    public static bool PipeExists(string pipeName)
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\", pipeName).Any();
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static string? PublishedBrokerPath(string pipeName)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Postern\Pipes\{pipeName}");
        return key?.GetValue("BrokerPath") as string;
    }

    public static SocketsHttpHandler CreateHandler(PosternOptions options) => new()
    {
        // Never follow redirects: the ConnectCallback dials the pipe whatever the target host, so a followed redirect
        // would send the request (and the app token) to Postern addressed to some other host. Postern answers 421.
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectCallback = async (_, cancellationToken) =>
        {
            var stream = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await stream.ConnectAsync((int)options.ConnectTimeout.TotalMilliseconds, cancellationToken);
                Verify(stream, options);
                return stream;
            }
            catch (UnauthorizedAccessException)
            {
                // The pipe's ACL refused us: it belongs to another user (or this is a remote/network logon). Not a
                // restart in progress, so don't let the handler retry it.
                await stream.DisposeAsync();
                throw new PosternVerificationException($"The '{options.PipeName}' pipe refused this account: it belongs to another user's Postern.");
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        },
    };

    static void Verify(NamedPipeClientStream stream, PosternOptions options)
    {
        var expected = options.ExpectedServerExe ?? PublishedBrokerPath(options.PipeName)
            ?? throw new PosternVerificationException($"Postern hasn't published its location for pipe '{options.PipeName}'; is it running?");

        var server = PipeProcess.Describe(PipeProcess.ServerProcessId(stream.SafePipeHandle));
        var me = WindowsIdentity.GetCurrent().User;
        if (server.User is null || server.User != me)
            throw new PosternVerificationException($"The '{options.PipeName}' pipe is served by pid {server.ProcessId}, which isn't running as you. Not sending anything.");
        if (server.ExePath is null || !string.Equals(Path.GetFullPath(server.ExePath), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new PosternVerificationException($"The '{options.PipeName}' pipe is served by {server.ExePath ?? "an unknown exe"}, not Postern ({expected}). Not sending anything.");
    }
}
