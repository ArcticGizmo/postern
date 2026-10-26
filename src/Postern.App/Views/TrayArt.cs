using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Postern.App.Views;

/// <summary>
/// The icon, from the assets tools/gen-icons.ps1 renders from postern.svg. Amber while an app is waiting for approval.
/// <para>
/// Postern (Dev) is pink, like Perch (Dev) and Shrike (Dev), so it can't be mistaken for the installed copy when both
/// sit in the tray. Amber has to mean the same in both, so while an approval waits only the tooltip tells them apart.
/// </para>
/// <para>
/// Window and tray icons are loaded from .ico files, not bitmaps: Avalonia then hands Windows the frame drawn for the
/// tray's size at the current display scale, where a single bitmap would be downscaled into a blur.
/// </para>
/// </summary>
internal static class TrayArt
{
    static WindowIcon? _normal;
    static WindowIcon? _attention;
    static Bitmap? _logo;

    public static WindowIcon WindowIcon(bool attention = false) => attention
        ? _attention ??= new WindowIcon(Open("postern-attention.ico"))
        : _normal ??= new WindowIcon(Open(Broker.Profile.IsDev ? "postern-dev.ico" : "postern.ico"));

    /// <summary>The normal icon as an image, for the main window's nav header.</summary>
    public static Bitmap Logo() => _logo ??= new Bitmap(Open(Broker.Profile.IsDev ? "icon-dev.png" : "icon.png"));

    static Stream Open(string asset) => AssetLoader.Open(new Uri($"avares://Postern/Assets/{asset}"));
}
