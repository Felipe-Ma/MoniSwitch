using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenShift.Native;

namespace ScreenShift.Views;

/// <summary>
/// The application icon, drawn in code: two overlapping monitor rectangles, one accent blue and
/// one green. One drawing routine feeds everything — the runtime tray icon, and the .ico the
/// probe generates for the executable — so the design cannot drift between the two.
/// </summary>
[SupportedOSPlatform("windows")]
public static class IconArt
{
    private static readonly Color Accent = Color.FromRgb(0x6E, 0x9B, 0xFF);
    private static readonly Color Green = Color.FromRgb(0x45, 0xD1, 0x9A);
    private static readonly Color Outline = Color.FromRgb(0x0E, 0x0E, 0x11);

    /// <summary>Renders the icon at the given pixel size, non-premultiplied BGRA.</summary>
    public static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            Draw(dc, size);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96d, 96d, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        // Icons want straight alpha; RenderTargetBitmap only produces premultiplied.
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0d);
        converted.Freeze();
        return converted;
    }

    /// <summary>
    /// Creates an HICON for the tray at the given size. The caller owns it and must pass it to
    /// DestroyIcon when the tray icon is removed.
    /// </summary>
    public static IntPtr CreateHIcon(int size)
    {
        var source = Render(size);

        var stride = size * 4;
        var pixels = new byte[stride * size];
        source.CopyPixels(pixels, stride, 0);

        // A 32-bpp colour bitmap carries the alpha channel; the mask is still required by
        // CreateIconIndirect but is ignored in favour of that alpha, so all-zero is fine.
        var maskStride = ((size + 15) / 16) * 2;
        var mask = new byte[maskStride * size];

        var hbmColor = ShellInterop.CreateBitmap(size, size, 1, 32, pixels);
        var hbmMask = ShellInterop.CreateBitmap(size, size, 1, 1, mask);

        try
        {
            var info = new ICONINFO
            {
                fIcon = 1,
                hbmColor = hbmColor,
                hbmMask = hbmMask,
            };

            return ShellInterop.CreateIconIndirect(ref info);
        }
        finally
        {
            ShellInterop.DeleteObject(hbmColor);
            ShellInterop.DeleteObject(hbmMask);
        }
    }

    private static void Draw(DrawingContext dc, double s)
    {
        var radius = s * 0.10;

        // Back monitor: accent blue, upper left.
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Accent),
            null,
            new Rect(s * 0.04, s * 0.12, s * 0.60, s * 0.46),
            radius,
            radius);

        // Front monitor: green, lower right, with a dark outline so the overlap stays legible
        // at 16 pixels.
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Green),
            new Pen(new SolidColorBrush(Outline), Math.Max(1d, s * 0.05)),
            new Rect(s * 0.34, s * 0.42, s * 0.60, s * 0.44),
            radius,
            radius);
    }
}
