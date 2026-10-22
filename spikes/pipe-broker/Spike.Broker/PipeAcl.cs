using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.Win32.SafeHandles;

namespace Spike.Broker;

/// <summary>
/// The pipe's security. Kestrel's CurrentUserOnly would replace any PipeSecurity we give it (and ties access to the
/// elevation level), so it's off and we build the descriptor ourselves:
///   - allow the current user's SID read/write and create-instance
///   - deny NETWORK
/// Nobody else is listed, so other users get nothing, including the right to add an instance of their own.
///
/// The deny ACE alone does NOT stop \\localhost\pipe\... (S1 found it connects: loopback SMB reuses the caller's own
/// token, so there's no NETWORK SID to match). The real remote block is PIPE_REJECT_REMOTE_CLIENTS, which .NET's
/// NamedPipeServerStream doesn't expose, so the pipe is created with CreateNamedPipeW and wrapped.
/// </summary>
internal static class PipeAcl
{
    const uint PipeAccessDuplex = 0x00000003;
    const uint FileFlagFirstPipeInstance = 0x00080000;
    const uint FileFlagOverlapped = 0x40000000;
    const uint PipeRejectRemoteClients = 0x00000008;
    const uint PipeUnlimitedInstances = 255;

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateNamedPipeW")]
    static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes securityAttributes);

    public static PipeSecurity Build(bool allowRemote)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        security.SetOwner(user);
        if (!allowRemote)
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return security;
    }

    public static string Sddl(PipeSecurity security) =>
        security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);

    /// <summary>
    /// Stream factory for Kestrel. The first instance is created with FirstPipeInstance, so if anything (another Postern,
    /// or a squatter) already owns the name, creation fails and the broker refuses to start rather than sharing it.
    /// </summary>
    public static Func<CreateNamedPipeServerStreamContext, NamedPipeServerStream> Factory(PipeSecurity security, bool allowRemote)
    {
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var first = 1;
        return context =>
        {
            var openMode = PipeAccessDuplex | FileFlagOverlapped;
            if (Interlocked.Exchange(ref first, 0) == 1)
                openMode |= FileFlagFirstPipeInstance;
            var pipeMode = allowRemote ? 0u : PipeRejectRemoteClients; // byte type, byte read mode, blocking

            var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
            try
            {
                var attributes = new SecurityAttributes
                {
                    nLength = Marshal.SizeOf<SecurityAttributes>(),
                    lpSecurityDescriptor = pinned.AddrOfPinnedObject(),
                };
                var handle = CreateNamedPipe(@"\\.\pipe\" + context.NamedPipeEndPoint.PipeName, openMode, pipeMode,
                    PipeUnlimitedInstances, 0, 0, 0, ref attributes);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    // ERROR_ACCESS_DENIED here on the first instance means the name already exists.
                    throw new IOException($"CreateNamedPipe failed: {new Win32Exception(error).Message}", error);
                }
                return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
            }
            finally
            {
                pinned.Free();
            }
        };
    }
}
