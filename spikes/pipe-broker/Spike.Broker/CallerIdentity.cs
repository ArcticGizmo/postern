using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Connections.Features;
using Spike.Shared;

namespace Spike.Broker;

internal sealed record Caller(
    uint ProcessId,
    string? ExePath,
    string? UserSid,
    uint? SessionId,
    bool SignatureTrusted,
    string SignatureStatus,
    string? Signer,
    bool SignatureFromCache,
    double LookupMs,
    string? Error);

/// <summary>
/// Who is on the other end of this connection. Resolved once per pipe connection and cached in the connection's items,
/// because HTTP keep-alive reuses the connection. WinVerifyTrust is the expensive part (~15 ms), so signature results
/// are also cached across connections by exe path + size + last write time.
/// </summary>
internal static class CallerIdentity
{
    static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), SignatureInfo> Signatures = new();

    public static Caller Get(HttpContext context)
    {
        var items = context.Features.Get<IConnectionItemsFeature>()?.Items;
        if (items is not null && items.TryGetValue(typeof(Caller), out var cached))
            return (Caller)cached!;

        var pipe = context.Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe
            ?? throw new InvalidOperationException("Not a named-pipe connection.");

        var stopwatch = Stopwatch.StartNew();
        var pid = Native.ClientProcessId(pipe.SafePipeHandle);
        var identity = Native.Describe(pid);
        var (signature, fromCache) = identity.ExePath is null ? (new SignatureInfo(false, "no-exe-path", null), false) : Signature(identity.ExePath);
        var caller = new Caller(
            pid,
            identity.ExePath,
            identity.User?.Value,
            identity.SessionId,
            signature.Trusted,
            signature.Status,
            signature.Subject,
            fromCache,
            stopwatch.Elapsed.TotalMilliseconds,
            identity.Error);

        if (items is not null)
            items[typeof(Caller)] = caller;
        return caller;
    }

    static (SignatureInfo Info, bool FromCache) Signature(string path)
    {
        var file = new FileInfo(path);
        var key = (path.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc);
        if (Signatures.TryGetValue(key, out var hit))
            return (hit, true);
        var info = Authenticode.Verify(path);
        Signatures[key] = info;
        return (info, false);
    }
}
