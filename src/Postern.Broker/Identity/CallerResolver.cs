using System.Collections.Concurrent;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Postern.Core.Apps;
using Postern.Interop;

namespace Postern.Broker.Identity;

/// <summary>
/// Who is on the other end of this pipe connection. Resolved once per connection (keep-alive reuses it) and cached
/// in the connection's items. WinVerifyTrust is the slow part (~15 ms in S1), so signature results are also cached
/// across connections by path + size + last-write time.
/// </summary>
internal sealed class CallerResolver
{
    readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), SignatureInfo> _signatures = new();

    public CallerInfo Resolve(HttpContext context)
    {
        var items = context.Features.Get<IConnectionItemsFeature>()?.Items;
        if (items is not null && items.TryGetValue(typeof(CallerInfo), out var cached))
            return (CallerInfo)cached!;

        var pipe = context.Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe
            ?? throw new InvalidOperationException("Not a named-pipe connection.");
        var identity = PipeProcess.Describe(PipeProcess.ClientProcessId(pipe.SafePipeHandle));
        if (identity.ExePath is null)
            throw new InvalidOperationException($"Couldn't identify pid {identity.ProcessId}: {identity.Error}");

        var signature = Signature(identity.ExePath);
        var caller = new CallerInfo(identity.ProcessId, identity.ExePath, signature.Trusted, signature.Subject);
        if (items is not null)
            items[typeof(CallerInfo)] = caller;
        return caller;
    }

    SignatureInfo Signature(string path)
    {
        var file = new FileInfo(path);
        var key = (path.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc);
        return _signatures.GetOrAdd(key, _ => Authenticode.Verify(path));
    }
}
