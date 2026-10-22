using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Postern.Broker.Credentials;

public interface ICredentialStore
{
    /// <summary>The secret, or null when none is stored. Throws if the store itself can't be read (fail closed).</summary>
    string? Read(string target);
    void Write(string target, string secret);
    bool Delete(string target);
}

/// <summary>Generic credentials in Windows Credential Manager, persisted for this user on this machine only (no roaming).</summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    const uint TypeGeneric = 1;
    const uint PersistLocalMachine = 2;
    const int ErrorNotFound = 1168;
    public const int MaxBlobBytes = 5 * 512;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);

    public string? Read(string target)
    {
        if (!CredRead(target, TypeGeneric, 0, out var ptr))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(ptr);
            return credential.CredentialBlobSize == 0
                ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Write(string target, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > MaxBlobBytes)
            throw new ArgumentException($"Secret is {bytes.Length} bytes; Credential Manager allows {MaxBlobBytes}.");

        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = TypeGeneric,
                TargetName = target,
                UserName = "postern",
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public bool Delete(string target) => CredDelete(target, TypeGeneric, 0);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);

    /// <summary>
    /// Deletes every generic credential whose name starts with <paramref name="prefix"/> (e.g. "postern:"), for
    /// uninstall. Returns the names deleted. Only Postern writes under its prefix, so nothing else is touched.
    /// </summary>
    public IReadOnlyList<string> DeleteAll(string prefix)
    {
        if (!CredEnumerate(prefix + "*", 0, out var count, out var list))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? [] : throw new Win32Exception(error);
        }

        var targets = new List<string>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var credential = Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                if (credential.Type == TypeGeneric && credential.TargetName.StartsWith(prefix, StringComparison.Ordinal))
                    targets.Add(credential.TargetName);
            }
        }
        finally
        {
            CredFree(list);
        }
        return [.. targets.Where(Delete)];
    }
}

/// <summary>
/// Stores <c>postern:{provider}:{account}</c> as <c>{ns}:{provider}:{account}</c>, so the dev profile has its own
/// credentials next to the installed Postern's.
/// </summary>
public sealed class NamespacedCredentialStore(ICredentialStore inner, string ns) : ICredentialStore
{
    const string Prefix = "postern:";

    public static string Map(string target, string ns) =>
        target.StartsWith(Prefix, StringComparison.Ordinal) ? ns + ":" + target[Prefix.Length..] : target;

    public string? Read(string target) => inner.Read(Map(target, ns));
    public void Write(string target, string secret) => inner.Write(Map(target, ns), secret);
    public bool Delete(string target) => inner.Delete(Map(target, ns));
}

/// <summary>For tests.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    readonly ConcurrentDictionary<string, string> _secrets = new();

    public bool FailReads { get; set; }

    public string? Read(string target) =>
        FailReads ? throw new Win32Exception(1312, "simulated store failure") : _secrets.GetValueOrDefault(target);

    public void Write(string target, string secret) => _secrets[target] = secret;
    public bool Delete(string target) => _secrets.TryRemove(target, out _);
}
