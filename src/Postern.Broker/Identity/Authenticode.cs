using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Postern.Broker.Identity;

internal sealed record SignatureInfo(bool Trusted, string Status, string? Subject);

/// <summary>
/// Embedded Authenticode check through WinVerifyTrust, offline (cache-only URL retrieval, no revocation fetch) so the
/// request path never waits on the network. Catalog-signed files (most of System32) report as unsigned here.
/// </summary>
internal static class Authenticode
{
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    const uint UiNone = 2;
    const uint RevokeNone = 0;
    const uint ChoiceFile = 1;
    const uint CacheOnlyUrlRetrieval = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WintrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

    public static SignatureInfo Verify(string path)
    {
        var fileInfo = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        int hr;
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = UiNone,
                fdwRevocationChecks = RevokeNone,
                dwUnionChoice = ChoiceFile,
                pFile = pFile,
                dwProvFlags = CacheOnlyUrlRetrieval,
            };
            var action = GenericVerifyV2;
            hr = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WintrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
        }

        return new(hr == 0, Describe(hr), hr == 0 ? Subject(path) : null);
    }

    static string? Subject(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // no X509CertificateLoader equivalent for "the cert embedded in this PE"
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch
        {
            return null;
        }
    }

    static string Describe(int hr) => unchecked((uint)hr) switch
    {
        0 => "valid",
        0x800B0100 => "unsigned",
        0x800B0109 => "untrusted-root",
        0x800B0111 => "explicitly-distrusted",
        0x800B0004 => "subject-not-trusted",
        0x80096010 => "bad-digest",
        0x800B0101 => "expired",
        var other => $"0x{other:X8}",
    };
}
