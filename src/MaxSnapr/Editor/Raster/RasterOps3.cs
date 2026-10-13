using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MaxSnapr.Editor.Raster;

/// <summary>Filters and effects beyond the basics: bevel, fade, monochrome, blur, sharpen, vignette, hue/gamma, colour replacement, logo watermark.</summary>
public static class RasterOps3
{
    static byte C(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    public static BitmapSource Bevel(BitmapSource src, int size)
    {
        var p = RasterOps.Read(src);
        for (int y = 0; y < p.H; y++)
            for (int x = 0; x < p.W; x++)
            {
                int l = x, t = y, r = p.W - 1 - x, b = p.H - 1 - y;
                int d = Math.Min(Math.Min(l, t), Math.Min(r, b));
                if (d >= size) continue;
                // The nearest edge decides: top/left catch the light, bottom/right fall in shadow.
                bool light = d == l || d == t;
                double k = (1 - d / (double)size) * 0.55;
                int i = p.Index(x, y);
                for (int c = 0; c < 3; c++) p.Data[i + c] = light ? C(p.Data[i + c] + (255 - p.Data[i + c]) * k) : C(p.Data[i + c] * (1 - k));
            }
        return RasterOps.Write(p);
    }

    /// <summary>Fades the chosen sides to transparent over <paramref name="size"/> pixels.</summary>
    public static BitmapSource FadeEdges(BitmapSource src, EdgeSides sides, int size)
    {
        var p = RasterOps.Read(src);
        for (int y = 0; y < p.H; y++)
            for (int x = 0; x < p.W; x++)
            {
                double f = 1;
                if ((sides & EdgeSides.Left) != 0 && x < size) f = Math.Min(f, x / (double)size);
                if ((sides & EdgeSides.Right) != 0 && p.W - 1 - x < size) f = Math.Min(f, (p.W - 1 - x) / (double)size);
                if ((sides & EdgeSides.Top) != 0 && y < size) f = Math.Min(f, y / (double)size);
                if ((sides & EdgeSides.Bottom) != 0 && p.H - 1 - y < size) f = Math.Min(f, (p.H - 1 - y) / (double)size);
                if (f < 1) p.Data[p.Index(x, y) + 3] = C(p.Data[p.Index(x, y) + 3] * f);
            }
        return RasterOps.Write(p);
    }

    public static BitmapSource Monochrome(BitmapSource src, double thresholdPercent)
    {
        var p = RasterOps.Read(src);
        int t = (int)(thresholdPercent * 2.55);
        for (int i = 0; i < p.Data.Length; i += 4)
        {
            int l = (p.Data[i] * 29 + p.Data[i + 1] * 150 + p.Data[i + 2] * 77) >> 8;
            byte v = l >= t ? (byte)255 : (byte)0;
            p.Data[i] = p.Data[i + 1] = p.Data[i + 2] = v;
        }
        return RasterOps.Write(p);
    }

    public static BitmapSource Blur(BitmapSource src, int radius)
    {
        var p = RasterOps.Read(src);
        RasterOps.BoxBlur(p, Math.Max(1, radius), 3, alphaOnly: false);
        return RasterOps.Write(p);
    }

    /// <summary>Unsharp mask.</summary>
    public static BitmapSource Sharpen(BitmapSource src, double amount)
    {
        var orig = RasterOps.Read(src);
        var blur = RasterOps.Read(src);
        RasterOps.BoxBlur(blur, 1, 2, alphaOnly: false);
        for (int i = 0; i < orig.Data.Length; i += 4)
            for (int c = 0; c < 3; c++)
                orig.Data[i + c] = C(orig.Data[i + c] + (orig.Data[i + c] - blur.Data[i + c]) * amount);
        return RasterOps.Write(orig);
    }

    public static BitmapSource Vignette(BitmapSource src, double strength)
    {
        var p = RasterOps.Read(src);
        double cx = p.W / 2.0, cy = p.H / 2.0, maxD = Math.Sqrt(cx * cx + cy * cy);
        for (int y = 0; y < p.H; y++)
            for (int x = 0; x < p.W; x++)
            {
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxD;
                double k = 1 - Math.Clamp((d - 0.45) / 0.55, 0, 1) * strength;
                int i = p.Index(x, y);
                p.Data[i] = C(p.Data[i] * k); p.Data[i + 1] = C(p.Data[i + 1] * k); p.Data[i + 2] = C(p.Data[i + 2] * k);
            }
        return RasterOps.Write(p);
    }

    /// <summary>Hue rotation in degrees and gamma (1 = unchanged).</summary>
    public static BitmapSource HueGamma(BitmapSource src, double hueDegrees, double gamma)
    {
        var p = RasterOps.Read(src);
        var lut = new byte[256];
        for (int v = 0; v < 256; v++) lut[v] = C(255 * Math.Pow(v / 255.0, 1 / Math.Max(0.1, gamma)));
        double a = hueDegrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        // Rotation around the grey axis (standard hue-rotate matrix).
        double[] m =
        {
            0.213 + cos * 0.787 - sin * 0.213, 0.715 - cos * 0.715 - sin * 0.715, 0.072 - cos * 0.072 + sin * 0.928,
            0.213 - cos * 0.213 + sin * 0.143, 0.715 + cos * 0.285 + sin * 0.140, 0.072 - cos * 0.072 - sin * 0.283,
            0.213 - cos * 0.213 - sin * 0.787, 0.715 - cos * 0.715 + sin * 0.715, 0.072 + cos * 0.928 + sin * 0.072,
        };
        for (int i = 0; i < p.Data.Length; i += 4)
        {
            double r = p.Data[i + 2], g = p.Data[i + 1], b = p.Data[i];
            double nr = m[0] * r + m[1] * g + m[2] * b, ng = m[3] * r + m[4] * g + m[5] * b, nb = m[6] * r + m[7] * g + m[8] * b;
            p.Data[i + 2] = lut[C(nr)]; p.Data[i + 1] = lut[C(ng)]; p.Data[i] = lut[C(nb)];
        }
        return RasterOps.Write(p);
    }

    /// <summary>Replaces every pixel close to <paramref name="from"/> with <paramref name="to"/> (or swaps both colours).</summary>
    public static BitmapSource ReplaceColor(BitmapSource src, Color from, Color to, double tolerance, bool swap)
    {
        var p = RasterOps.Read(src);
        int t = (int)(tolerance * 2.55);
        bool Near(int i, Color c) => Math.Abs(p.Data[i + 2] - c.R) <= t && Math.Abs(p.Data[i + 1] - c.G) <= t && Math.Abs(p.Data[i] - c.B) <= t;
        for (int i = 0; i < p.Data.Length; i += 4)
        {
            Color? target = Near(i, from) ? to : swap && Near(i, to) ? from : null;
            if (target is Color c) { p.Data[i + 2] = c.R; p.Data[i + 1] = c.G; p.Data[i] = c.B; }
        }
        return RasterOps.Write(p);
    }

    /// <summary>Places a logo image at one of nine positions, scaled to a fraction of the image width.</summary>
    public static BitmapSource WatermarkImage(BitmapSource src, BitmapSource logo, string position, double opacity, double widthFraction)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        double lw = Math.Max(8, w * widthFraction), lh = lw * logo.PixelHeight / Math.Max(1, logo.PixelWidth);
        double m = Math.Min(w, h) * 0.03;
        double x = position.Contains('W') ? m : position.Contains('E') ? w - lw - m : (w - lw) / 2;
        double y = position.Contains('N') ? m : position.Contains('S') ? h - lh - m : (h - lh) / 2;
        return RasterOps.Render(w, h, dc =>
        {
            dc.DrawImage(src, new Rect(0, 0, w, h));
            dc.PushOpacity(opacity);
            dc.DrawImage(logo, new Rect(x, y, lw, lh));
            dc.Pop();
        });
    }

    /// <summary>Same pixels with new DPI metadata (affects print size and some editors).</summary>
    public static BitmapSource WithDpi(BitmapSource src, double dpi)
    {
        var p = RasterOps.Read(src);
        var b = BitmapSource.Create(p.W, p.H, dpi, dpi, PixelFormats.Bgra32, null, p.Data, p.Stride);
        b.Freeze();
        return b;
    }
}
