using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Spike.Shared;

/// <summary>What we could learn about the process at the other end of a pipe. Any field may be null if Windows refused.</summary>
internal sealed record ProcessIdentity(uint ProcessId, string? ExePath, SecurityIdentifier? User, uint? SessionId, string? Error);

/// <summary>Win32 calls for identifying the process at the other end of a named pipe.</summary>
internal static class Native
{
    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint TokenQuery = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    public static uint ClientProcessId(SafePipeHandle pipe) =>
        GetNamedPipeClientProcessId(pipe, out var pid) ? pid : throw new Win32Exception(Marshal.GetLastPInvokeError());

    public static uint ServerProcessId(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var pid) ? pid : throw new Win32Exception(Marshal.GetLastPInvokeError());

    public static ProcessIdentity Describe(uint pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid)
            return new(pid, null, null, null, $"OpenProcess failed (Win32 error {Marshal.GetLastPInvokeError()})");

        var buffer = new StringBuilder(1024);
        var size = (uint)buffer.Capacity;
        var exe = QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;

        SecurityIdentifier? user = null;
        if (OpenProcessToken(process, TokenQuery, out var token))
        {
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                user = identity.User;
        }

        uint? session = ProcessIdToSessionId(pid, out var s) ? s : null;
        var error = exe is null || user is null ? $"partial identity (Win32 error {Marshal.GetLastPInvokeError()})" : null;
        return new(pid, exe, user, session, error);
    }
}
