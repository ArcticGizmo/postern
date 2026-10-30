using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Svg;

// Generates every raster icon asset from the single source-of-truth SVG (postern.svg).
//
//   src/Postern.App/Assets/postern.ico            multi-resolution ICO — the .exe ApplicationIcon, vpk --icon, and
//                                                 the installed copy's window and tray icon
//   src/Postern.App/Assets/postern-dev.ico        the same in Postern (Dev)'s pink
//   src/Postern.App/Assets/postern-attention.ico  the same in amber, while an approval waits
//   src/Postern.App/Assets/postern-sandbox.ico    the same in Postern (Sandbox)'s sand
//   src/Postern.App/Assets/icon.png, icon-dev.png, icon-sandbox.png
//                                                 256x256 PNG — the in-app logo
//   tools/Postern.Sallyport/Assets/sallyport.ico  the same in Sallyport's blue
//   landing-icon.png                              512x512 PNG — the README header logo
//
// Re-run after editing postern.svg:  tools/gen-icons.ps1

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string svgPath = Path.Combine(repoRoot, "postern.svg");
if (!File.Exists(svgPath))
{
    Console.Error.WriteLine($"Source SVG not found: {svgPath}");
    return 1;
}

Console.WriteLine($"Source: {svgPath}");

// Every colour variant is rendered from the vector at each size, never recoloured or rescaled from another raster, so
// each frame gets its own anti-aliasing. Pink matches Perch (Dev) and Shrike (Dev); amber matches the approval prompt.
var brand = SvgDocument.Open(svgPath);
var dev = Filled(SvgDocument.Open(svgPath), Color.FromArgb(0xFF, 0x4F, 0xA3));
var attention = Filled(SvgDocument.Open(svgPath), Color.FromArgb(0xF5, 0xA5, 0x24));
// Sand for Postern (Sandbox), paler than the amber so the two can't be confused at 16px; blue for Sallyport, which is
// an app talking to Postern rather than a Postern.
var sandbox = Filled(SvgDocument.Open(svgPath), Color.FromArgb(0xC8, 0xB2, 0x7A));
var sallyport = Filled(SvgDocument.Open(svgPath), Color.FromArgb(0x7A, 0xA7, 0xF5));

// The tile sits inside a margin of a few units that isn't a whole pixel at any icon size, so every edge of it would land
// mid-pixel and blur, which shows worst at 16px in the tray. Cropping the viewBox to the drawn content puts the tile's
// edges on the frame's edges at every size.
var content = ContentBox(brand);
Console.WriteLine($"Content: {content.X:0.00},{content.Y:0.00} {content.Width:0.00}x{content.Height:0.00} of a {brand.ViewBox.Width}x{brand.ViewBox.Height} viewBox");
foreach (var doc in new[] { brand, dev, attention, sandbox, sallyport })
    CropTo(doc, content);

// The tray asks Avalonia for 16px times the display scale and the taskbar for 24px times it, and Avalonia takes the
// nearest frame, which the shell then resizes without smoothing. A frame at each common scale (100, 125, 150, 175, 200,
// 250%) means nothing is resized.
int[] icoSizes = [16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 60, 64, 72, 96, 128, 256];

string assetsDir = Path.Combine(repoRoot, "src", "Postern.App", "Assets");
Directory.CreateDirectory(assetsDir);
WriteIco(brand, Path.Combine(assetsDir, "postern.ico"), icoSizes);
WriteIco(dev, Path.Combine(assetsDir, "postern-dev.ico"), icoSizes);
WriteIco(attention, Path.Combine(assetsDir, "postern-attention.ico"), icoSizes);
WriteIco(sandbox, Path.Combine(assetsDir, "postern-sandbox.ico"), icoSizes);
WritePng(brand, Path.Combine(assetsDir, "icon.png"), 256);
WritePng(dev, Path.Combine(assetsDir, "icon-dev.png"), 256);
WritePng(sandbox, Path.Combine(assetsDir, "icon-sandbox.png"), 256);

string sallyportAssets = Path.Combine(repoRoot, "tools", "Postern.Sallyport", "Assets");
Directory.CreateDirectory(sallyportAssets);
WriteIco(sallyport, Path.Combine(sallyportAssets, "sallyport.ico"), icoSizes);
WritePng(brand, Path.Combine(repoRoot, "landing-icon.png"), 512);

Console.WriteLine("Done.");
return 0;

static SvgDocument Filled(SvgDocument doc, Color colour)
{
    foreach (var element in doc.Descendants().OfType<SvgVisualElement>())
        if (element.Fill is SvgColourServer)
            element.Fill = new SvgColourServer(colour);
    return doc;
}

// SvgDocument.Bounds includes curves' control points, so it overstates the art. A high-resolution render's opaque area
// is the real extent, to within 1/8 of a unit.
static RectangleF ContentBox(SvgDocument doc)
{
    const int probe = 2048;
    using var bmp = Render(doc, probe);
    var locked = bmp.LockBits(new Rectangle(0, 0, probe, probe), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var pixels = new byte[locked.Stride * probe];
    Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length);
    bmp.UnlockBits(locked);

    int left = probe, top = probe, right = -1, bottom = -1;
    for (int y = 0; y < probe; y++)
        for (int x = 0; x < probe; x++)
            if (pixels[y * locked.Stride + x * 4 + 3] > 0)
            {
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }

    var unit = doc.ViewBox.Width / probe;
    return new RectangleF(left * unit, top * unit, (right + 1 - left) * unit, (bottom + 1 - top) * unit);
}

// The content isn't quite square (under 2% taller than wide). Stretching it that little to fill the square frame is
// invisible, where padding one axis would bring a blurred edge back.
static void CropTo(SvgDocument doc, RectangleF content)
{
    doc.ViewBox = new SvgViewBox(content.X, content.Y, content.Width, content.Height);
    doc.AspectRatio = new SvgAspectRatio(SvgPreserveAspectRatio.none);
}

// postern.svg declares its size in mm. Setting the document's width/height in pixels makes the library map the viewBox
// straight onto the pixel canvas; otherwise the content renders at the wrong scale.
static Bitmap Render(SvgDocument doc, int size)
{
    doc.Width = new SvgUnit(SvgUnitType.Pixel, size);
    doc.Height = new SvgUnit(SvgUnitType.Pixel, size);
    return doc.Draw();
}

static void WritePng(SvgDocument doc, string path, int size)
{
    using var bmp = Render(doc, size);
    bmp.Save(path, ImageFormat.Png);
    Console.WriteLine($"  {Path.GetFileName(path)}  {size}x{size}");
}

// Small frames are uncompressed 32bpp BMP/DIB, not PNG: Win32 icon loading and the .NET <ApplicationIcon> PE embedding
// mishandle PNG-compressed small frames, and they render low-res in the taskbar and Explorer. 256 has to be PNG to keep
// the file a sane size.
static void WriteIco(SvgDocument doc, string path, int[] sizes)
{
    var frames = new List<byte[]>();
    foreach (var size in sizes)
    {
        using var bmp = Render(doc, size);
        if (size >= 256)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            frames.Add(ms.ToArray());
        }
        else
        {
            frames.Add(BuildBmpFrame(bmp, size));
        }
    }

    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);

    // ICONDIR header
    w.Write((ushort)0); // reserved
    w.Write((ushort)1); // type = icon
    w.Write((ushort)sizes.Length);

    int offset = 6 + sizes.Length * 16; // ICONDIRENTRY is 16 bytes; image data follows the directory
    for (int i = 0; i < sizes.Length; i++)
    {
        int size = sizes[i];
        w.Write((byte)(size >= 256 ? 0 : size)); // width  (0 = 256)
        w.Write((byte)(size >= 256 ? 0 : size)); // height (0 = 256)
        w.Write((byte)0); // palette count
        w.Write((byte)0); // reserved
        w.Write((ushort)1); // colour planes
        w.Write((ushort)32); // bits per pixel
        w.Write(frames[i].Length);
        w.Write(offset);
        offset += frames[i].Length;
    }

    foreach (var frame in frames)
        w.Write(frame);

    Console.WriteLine($"  {Path.GetFileName(path)}  [{string.Join(", ", sizes)}]  (BMP < 256, PNG 256)");
}

// An ICO's DIB frame declares double height because the colour rows are followed by a 1bpp AND mask. The mask is left
// all zero: transparency comes from the alpha channel.
static byte[] BuildBmpFrame(Bitmap bmp, int size)
{
    var locked = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var stride = locked.Stride;
    var pixels = new byte[stride * size];
    Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length);
    bmp.UnlockBits(locked);

    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40);          // biSize
    w.Write(size);        // biWidth
    w.Write(size * 2);    // biHeight (colour rows + mask rows)
    w.Write((ushort)1);   // biPlanes
    w.Write((ushort)32);  // biBitCount
    w.Write(0);           // biCompression = BI_RGB
    w.Write(0);           // biSizeImage
    w.Write(0); w.Write(0); w.Write(0); w.Write(0);   // x/y pels-per-metre, clrUsed, clrImportant

    for (int y = size - 1; y >= 0; y--)   // bottom-up; Format32bppArgb rows are already BGRA
        w.Write(pixels, y * stride, size * 4);

    var maskRow = new byte[((size + 31) / 32) * 4];   // padded to 4 bytes
    for (int y = 0; y < size; y++)
        w.Write(maskRow, 0, maskRow.Length);

    return ms.ToArray();
}

static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "postern.svg")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(start, "..", "..", "..", "..", ".."));
}
