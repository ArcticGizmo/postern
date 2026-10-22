using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Postern.App.Services;

/// <summary>
/// Postern runs as the user's normal (filtered) token. Started elevated, everything it launched would inherit
/// admin rights, its files would be owned by Administrators, and it would hold the pipe name so a normal Postern
/// couldn't start. UAC isn't a security boundary, so this is about not doing damage by accident, not about defence.
/// </summary>
internal static class Elevation
{
    /// <summary>
    /// True only for a UAC-elevated token that has a normal counterpart: the user could have run Postern unelevated.
    /// A machine with UAC off (or the built-in Administrator) has no other token to run with, so that isn't refused.
    /// </summary>
    public static bool IsElevatedByUac()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var size = Marshal.SizeOf<int>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return GetTokenInformation(identity.Token, TokenElevationTypeClass, buffer, size, out _)
                   && Marshal.ReadInt32(buffer) == TokenElevationTypeFull;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public const string Message = "Postern was started as administrator. It has to run as you, unelevated: anything it launched would " +
                                  "inherit admin rights, and it would hold the pipe so a normal Postern couldn't start.\n\n" +
                                  "Start it again from a normal (non-admin) terminal or the Start menu.";

    public static void ShowRefusal() => MessageBoxW(IntPtr.Zero, Message, "Postern", MbIconWarning);

    const int TokenElevationTypeClass = 18;
    const int TokenElevationTypeFull = 2;
    const uint MbIconWarning = 0x30;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returned);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}
