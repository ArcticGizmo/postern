using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Postern.App.Views;

/// <summary>
/// The icon, drawn at runtime: a small arched gate with a keyhole. Teal normally, amber while an app is waiting for
/// approval. A placeholder until there's a designed icon (the icon-prompt skill can produce one).
/// </summary>
internal static class TrayArt
{
    static WindowIcon? _normal;
    static WindowIcon? _attention;

    public static WindowIcon WindowIcon(bool attention = false) =>
        attention ? _attention ??= Render(Color.Parse("#F5A524")) : _normal ??= Render(Color.Parse("#4FB8A8"));

    static WindowIcon Render(Color gate)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        var dark = new SolidColorBrush(Color.Parse("#15181B"));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawRectangle(dark, null, new RoundedRect(new Rect(2, 2, 60, 60), 14));
            context.DrawGeometry(new SolidColorBrush(gate), null, StreamGeometry.Parse("M16,56 L16,30 A16,16 0 0 1 48,30 L48,56 Z"));
            context.DrawEllipse(dark, null, new Point(32, 35), 5, 5);
            context.DrawRectangle(dark, null, new Rect(30, 37, 4, 11));
        }
        return new WindowIcon(bitmap);
    }
}
