using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Postern.App.Views;

/// <summary>
/// The icon, drawn at runtime: a small arched gate with a keyhole. Teal normally, amber while an app is waiting for
/// approval. A placeholder until there's a designed icon (the icon-prompt skill can produce one).
/// <para>
/// Postern (Dev) gets a violet gate on a purple tile, so it can't be mistaken for the installed copy when both sit in
/// the tray. The tile keeps it recognisable while the gate is amber, since amber has to mean the same in both.
/// </para>
/// </summary>
internal static class TrayArt
{
    static readonly Color Gate = Color.Parse(Broker.Profile.IsDev ? "#A78BFA" : "#4FB8A8");
    static readonly Color Attention = Color.Parse("#F5A524");
    static readonly Color Tile = Color.Parse(Broker.Profile.IsDev ? "#2A2140" : "#15181B");

    static WindowIcon? _normal;
    static WindowIcon? _attention;
    static Bitmap? _logo;

    public static WindowIcon WindowIcon(bool attention = false) =>
        attention ? _attention ??= new WindowIcon(Draw(Attention)) : _normal ??= new WindowIcon(Draw(Gate));

    /// <summary>The normal icon as an image, for the main window's nav header.</summary>
    public static Bitmap Logo() => _logo ??= Draw(Gate);

    static Bitmap Draw(Color gate)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        var dark = new SolidColorBrush(Tile);
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawRectangle(dark, null, new RoundedRect(new Rect(2, 2, 60, 60), 14));
            context.DrawGeometry(new SolidColorBrush(gate), null, StreamGeometry.Parse("M16,56 L16,30 A16,16 0 0 1 48,30 L48,56 Z"));
            context.DrawEllipse(dark, null, new Point(32, 35), 5, 5);
            context.DrawRectangle(dark, null, new Rect(30, 37, 4, 11));
        }
        return bitmap;
    }
}
