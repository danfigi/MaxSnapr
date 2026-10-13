using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MaxSnapr.Editor.Raster;

public enum EdgeSides { None = 0, Top = 1, Right = 2, Bottom = 4, Left = 8, All = 15 }

/// <summary>Pixel operations on frozen Bgra32, 96 DPI images. Every method returns a new frozen image.</summary>
public static class RasterOps
{
    public sealed class Pixels
    {
        public int W, H;
        public byte[] Data = Array.Empty<byte>();
        public int Stride => W * 4;
        public int Index(int x, int y) => y * Stride + x * 4;
    }

    public static Pixels Read(BitmapSource src)
    {
        BitmapSource b = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var p = new Pixels { W = b.PixelWidth, H = b.PixelHeight };
        p.Data = new byte[p.Stride * p.H];
        b.CopyPixels(p.Data, p.Stride, 0);
        return p;
    }

    public static BitmapSource Write(Pixels p)
    {
        var b = BitmapSource.Create(p.W, p.H, 96, 96, PixelFormats.Bgra32, null, p.Data, p.Stride);
        b.Freeze();
        return b;
    }

    public static BitmapSource Normalize(BitmapSource src) => Write(Read(src));

    public static BitmapSource Render(int w, int h, Action<DrawingContext> draw, BitmapScalingMode mode = BitmapScalingMode.HighQuality)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, mode);
        using (var dc = dv.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return Normalize(rtb);
    }

    public static BitmapSource Blank(int w, int h, Color c) => Render(w, h, dc => dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(0, 0, w, h)));

    // ---- geometry ------------------------------------------------------------------------------

    public static BitmapSource Crop(BitmapSource src, Int32Rect r)
    {
        r = Capture.ScreenSnapshot.Intersect(r, new Int32Rect(0, 0, src.PixelWidth, src.PixelHeight));
        return r.Width <= 0 || r.Height <= 0 ? src : Normalize(new CroppedBitmap(src, r));
    }

    /// <summary>Removes the band [from, to) and joins the remaining halves.</summary>
    public static BitmapSource CutOut(BitmapSource src, int from, int to, bool vertical)
    {
        var p = Read(src);
        from = Math.Clamp(from, 0, vertical ? p.W : p.H);
        to = Math.Clamp(to, from, vertical ? p.W : p.H);
        int cut = to - from;
        if (cut <= 0) return src;
        var o = new Pixels { W = vertical ? p.W - cut : p.W, H = vertical ? p.H : p.H - cut };
        if (o.W <= 0 || o.H <= 0) return src;
        o.Data = new byte[o.Stride * o.H];
        for (int y = 0; y < o.H; y++)
        {
            if (vertical)
            {
                Buffer.BlockCopy(p.Data, p.Index(0, y), o.Data, o.Index(0, y), from * 4);
                Buffer.BlockCopy(p.Data, p.Index(to, y), o.Data, o.Index(from, y), (p.W - to) * 4);
            }
            else
            {
                int sy = y < from ? y : y + cut;
                Buffer.BlockCopy(p.Data, p.Index(0, sy), o.Data, o.Index(0, y), p.Stride);
            }
        }
        return Write(o);
    }

    public static BitmapSource Transform(BitmapSource src, Transform t)
    {
        var tb = new TransformedBitmap(src, t);
        return Normalize(tb);
    }

    public static BitmapSource Rotate(BitmapSource src, int degrees) => Transform(src, new RotateTransform(degrees));
    public static BitmapSource Flip(BitmapSource src, bool horizontal) => Transform(src, new ScaleTransform(horizontal ? -1 : 1, horizontal ? 1 : -1));

    public static BitmapSource RotateArbitrary(BitmapSource src, double degrees, Color background)
    {
        double rad = degrees * Math.PI / 180;
        double cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
        int w = (int)Math.Ceiling(src.PixelWidth * cos + src.PixelHeight * sin);
        int h = (int)Math.Ceiling(src.PixelWidth * sin + src.PixelHeight * cos);
        return Render(w, h, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, w, h));
            dc.PushTransform(new RotateTransform(degrees, w / 2.0, h / 2.0));
            dc.DrawImage(src, new Rect((w - src.PixelWidth) / 2.0, (h - src.PixelHeight) / 2.0, src.PixelWidth, src.PixelHeight));
            dc.Pop();
        });
    }

    public static BitmapSource Resize(BitmapSource src, int w, int h, bool smooth = true)
        => Render(w, h, dc => dc.DrawImage(src, new Rect(0, 0, w, h)), smooth ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);

    public static BitmapSource Expand(BitmapSource src, int left, int top, int right, int bottom, Color fill)
    {
        int w = src.PixelWidth + left + right, h = src.PixelHeight + top + bottom;
        if (w <= 0 || h <= 0) return src;
        return Render(w, h, dc =>
        {
            if (fill.A > 0) dc.DrawRectangle(new SolidColorBrush(fill), null, new Rect(0, 0, w, h));
            dc.DrawImage(src, new Rect(left, top, src.PixelWidth, src.PixelHeight));
        }, BitmapScalingMode.NearestNeighbor);
    }

    // ---- effects -------------------------------------------------------------------------------

    public static BitmapSource Border(BitmapSource src, int width, Color color) => Expand(src, width, width, width, width, color);

    public static BitmapSource Shadow(BitmapSource src, int size, int offset, Color color)
    {
        int pad = size + Math.Abs(offset);
        int w = src.PixelWidth + pad * 2, h = src.PixelHeight + pad * 2;
        // Alpha of the source, blurred, tinted, then the image on top.
        var sp = Read(src);
        var mask = new Pixels { W = w, H = h, Data = new byte[w * h * 4] };
        for (int y = 0; y < sp.H; y++)
            for (int x = 0; x < sp.W; x++)
            {
                int a = sp.Data[sp.Index(x, y) + 3];
                int mi = mask.Index(x + pad + offset, y + pad + offset);
                mask.Data[mi] = color.B; mask.Data[mi + 1] = color.G; mask.Data[mi + 2] = color.R;
                mask.Data[mi + 3] = (byte)(a * color.A / 255);
            }
        BoxBlur(mask, Math.Max(1, size / 2), 3, alphaOnly: true);
        var shadow = Write(mask);
        return Render(w, h, dc =>
        {
            dc.DrawImage(shadow, new Rect(0, 0, w, h));
            dc.DrawImage(src, new Rect(pad, pad, src.PixelWidth, src.PixelHeight));
        }, BitmapScalingMode.NearestNeighbor);
    }

    /// <summary>Torn/jagged/wave edges on the selected sides, made transparent outside the edge line.</summary>
    public static BitmapSource Edge(BitmapSource src, EdgeSides sides, int depth, string style, int seed = 7)
    {
        var p = Read(src);
        var rnd = new Random(seed);
        int[] Profile(int length)
        {
            var prof = new int[length];
            int tooth = Math.Max(4, depth);
            double phase = rnd.NextDouble() * Math.PI * 2;
            int cur = rnd.Next(depth + 1);
            for (int i = 0; i < length; i++)
            {
                prof[i] = style switch
                {
                    "saw" => (int)(depth * (i % tooth) / (double)tooth),
                    "jag" => (i / Math.Max(2, tooth / 2)) % 2 == 0 ? depth : depth / 3 + rnd.Next(0, Math.Max(1, depth / 4)),
                    "wave" => (int)(depth * (0.5 + 0.5 * Math.Sin(phase + i * Math.PI * 2 / (tooth * 4.0)))),
                    _ => cur = Math.Clamp(cur + rnd.Next(-2, 3), 0, depth), // torn
                };
            }
            return prof;
        }
        var top = (sides & EdgeSides.Top) != 0 ? Profile(p.W) : null;
        var bottom = (sides & EdgeSides.Bottom) != 0 ? Profile(p.W) : null;
        var left = (sides & EdgeSides.Left) != 0 ? Profile(p.H) : null;
        var right = (sides & EdgeSides.Right) != 0 ? Profile(p.H) : null;
        for (int y = 0; y < p.H; y++)
            for (int x = 0; x < p.W; x++)
            {
                bool cut = (top != null && y < top[x]) || (bottom != null && y >= p.H - bottom[x])
                    || (left != null && x < left[y]) || (right != null && x >= p.W - right[y]);
                if (cut) p.Data[p.Index(x, y) + 3] = 0;
            }
        return Write(p);
    }

    public static BitmapSource Grayscale(BitmapSource src) => PerPixel(src, (ref byte b, ref byte g, ref byte r) =>
    {
        byte l = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
        r = g = b = l;
    });

    public static BitmapSource Invert(BitmapSource src) => PerPixel(src, (ref byte b, ref byte g, ref byte r) =>
    {
        r = (byte)(255 - r); g = (byte)(255 - g); b = (byte)(255 - b);
    });

    public static BitmapSource Sepia(BitmapSource src) => PerPixel(src, (ref byte b, ref byte g, ref byte r) =>
    {
        double rr = r * .393 + g * .769 + b * .189, gg = r * .349 + g * .686 + b * .168, bb = r * .272 + g * .534 + b * .131;
        r = Clamp(rr); g = Clamp(gg); b = Clamp(bb);
    });

    /// <summary>brightness/contrast/saturation in -100..100.</summary>
    public static BitmapSource Adjust(BitmapSource src, double brightness, double contrast, double saturation)
    {
        double c = (100 + contrast) / 100.0; c *= c;
        double s = 1 + saturation / 100.0;
        double br = brightness * 2.55;
        return PerPixel(src, (ref byte b, ref byte g, ref byte r) =>
        {
            double R = r + br, G = g + br, B = b + br;
            R = (R / 255 - 0.5) * c * 255 + 127.5; G = (G / 255 - 0.5) * c * 255 + 127.5; B = (B / 255 - 0.5) * c * 255 + 127.5;
            double l = 0.299 * R + 0.587 * G + 0.114 * B;
            R = l + (R - l) * s; G = l + (G - l) * s; B = l + (B - l) * s;
            r = Clamp(R); g = Clamp(G); b = Clamp(B);
        });
    }

    public static BitmapSource Posterize(BitmapSource src, int levels)
    {
        double step = 255.0 / Math.Max(1, levels - 1);
        return PerPixel(src, (ref byte b, ref byte g, ref byte r) =>
        {
            r = Clamp(Math.Round(r / step) * step); g = Clamp(Math.Round(g / step) * step); b = Clamp(Math.Round(b / step) * step);
        });
    }

    public static BitmapSource Obscure(BitmapSource region, bool pixelate, double intensity)
    {
        var p = Read(region);
        if (pixelate)
        {
            int block = Math.Max(2, (int)(2 + intensity / 100.0 * 28));
            for (int by = 0; by < p.H; by += block)
                for (int bx = 0; bx < p.W; bx += block)
                {
                    long sb = 0, sg = 0, sr = 0, n = 0;
                    for (int y = by; y < Math.Min(p.H, by + block); y++)
                        for (int x = bx; x < Math.Min(p.W, bx + block); x++)
                        { int i = p.Index(x, y); sb += p.Data[i]; sg += p.Data[i + 1]; sr += p.Data[i + 2]; n++; }
                    byte B = (byte)(sb / n), G = (byte)(sg / n), R = (byte)(sr / n);
                    for (int y = by; y < Math.Min(p.H, by + block); y++)
                        for (int x = bx; x < Math.Min(p.W, bx + block); x++)
                        { int i = p.Index(x, y); p.Data[i] = B; p.Data[i + 1] = G; p.Data[i + 2] = R; p.Data[i + 3] = 255; }
                }
        }
        else
        {
            BoxBlur(p, Math.Max(1, (int)(1 + intensity / 100.0 * 18)), 3, alphaOnly: false);
            for (int i = 3; i < p.Data.Length; i += 4) p.Data[i] = 255;
        }
        return Write(p);
    }

    public static void BoxBlur(Pixels p, int radius, int passes, bool alphaOnly)
    {
        var tmp = new byte[p.Data.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            BlurLine(p.Data, tmp, p.W, p.H, radius, horizontal: true, alphaOnly);
            BlurLine(tmp, p.Data, p.W, p.H, radius, horizontal: false, alphaOnly);
        }
    }

    static void BlurLine(byte[] src, byte[] dst, int w, int h, int r, bool horizontal, bool alphaOnly)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        int first = alphaOnly ? 3 : 0;
        for (int l = 0; l < lines; l++)
        {
            for (int c = first; c < 4; c++)
            {
                int sum = 0;
                int Idx(int i) { i = Math.Clamp(i, 0, len - 1); return (horizontal ? (l * w + i) : (i * w + l)) * 4 + c; }
                for (int i = -r; i <= r; i++) sum += src[Idx(i)];
                for (int i = 0; i < len; i++)
                {
                    dst[(horizontal ? (l * w + i) : (i * w + l)) * 4 + c] = (byte)(sum / (2 * r + 1));
                    sum += src[Idx(i + r + 1)] - src[Idx(i - r)];
                }
            }
            if (alphaOnly)
                for (int i = 0; i < len; i++)
                {
                    int k = (horizontal ? (l * w + i) : (i * w + l)) * 4;
                    dst[k] = src[k]; dst[k + 1] = src[k + 1]; dst[k + 2] = src[k + 2];
                }
        }
    }

    public static BitmapSource FloodFill(BitmapSource src, int sx, int sy, Color color, double tolerance)
    {
        var p = Read(src);
        if (sx < 0 || sy < 0 || sx >= p.W || sy >= p.H) return src;
        int t = (int)(tolerance * 2.55);
        int si = p.Index(sx, sy);
        byte b0 = p.Data[si], g0 = p.Data[si + 1], r0 = p.Data[si + 2], a0 = p.Data[si + 3];
        var seen = new bool[p.W * p.H];
        var stack = new Stack<(int, int)>();
        stack.Push((sx, sy));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (x < 0 || y < 0 || x >= p.W || y >= p.H || seen[y * p.W + x]) continue;
            int i = p.Index(x, y);
            if (Math.Abs(p.Data[i] - b0) > t || Math.Abs(p.Data[i + 1] - g0) > t || Math.Abs(p.Data[i + 2] - r0) > t || Math.Abs(p.Data[i + 3] - a0) > t) continue;
            seen[y * p.W + x] = true;
            p.Data[i] = color.B; p.Data[i + 1] = color.G; p.Data[i + 2] = color.R; p.Data[i + 3] = color.A;
            stack.Push((x + 1, y)); stack.Push((x - 1, y)); stack.Push((x, y + 1)); stack.Push((x, y - 1));
        }
        return Write(p);
    }

    /// <summary>Selects pixels connected to (sx,sy) with similar colour: magic wand mask bounds.</summary>
    public static Int32Rect MagicWandBounds(BitmapSource src, int sx, int sy, double tolerance, out bool[] mask)
    {
        var p = Read(src);
        mask = new bool[p.W * p.H];
        if (sx < 0 || sy < 0 || sx >= p.W || sy >= p.H) return Int32Rect.Empty;
        int t = (int)(tolerance * 2.55);
        int si = p.Index(sx, sy);
        byte b0 = p.Data[si], g0 = p.Data[si + 1], r0 = p.Data[si + 2];
        int minX = sx, minY = sy, maxX = sx, maxY = sy;
        var stack = new Stack<(int, int)>();
        stack.Push((sx, sy));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (x < 0 || y < 0 || x >= p.W || y >= p.H || mask[y * p.W + x]) continue;
            int i = p.Index(x, y);
            if (Math.Abs(p.Data[i] - b0) > t || Math.Abs(p.Data[i + 1] - g0) > t || Math.Abs(p.Data[i + 2] - r0) > t) continue;
            mask[y * p.W + x] = true;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            stack.Push((x + 1, y)); stack.Push((x - 1, y)); stack.Push((x, y + 1)); stack.Push((x, y - 1));
        }
        return new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Applies a coverage mask (rendered geometry) to the image: covered pixels become <paramref name="color"/>.</summary>
    public static BitmapSource PaintGeometry(BitmapSource src, Geometry g, Pen? pen, Brush? brush, Color color)
    {
        var maskImg = Render(src.PixelWidth, src.PixelHeight, dc => dc.DrawGeometry(brush, pen, g), BitmapScalingMode.NearestNeighbor);
        var m = Read(maskImg);
        var p = Read(src);
        for (int i = 0; i < p.Data.Length; i += 4)
        {
            int a = m.Data[i + 3];
            if (a == 0) continue;
            p.Data[i] = (byte)((p.Data[i] * (255 - a) + color.B * a) / 255);
            p.Data[i + 1] = (byte)((p.Data[i + 1] * (255 - a) + color.G * a) / 255);
            p.Data[i + 2] = (byte)((p.Data[i + 2] * (255 - a) + color.R * a) / 255);
            p.Data[i + 3] = (byte)((p.Data[i + 3] * (255 - a) + color.A * a) / 255);
        }
        return Write(p);
    }

    /// <summary>Fills a rectangle by blending the colours just outside its edges (simple content-aware fill).</summary>
    public static BitmapSource FillFromSurroundings(BitmapSource src, Int32Rect r)
    {
        var p = Read(src);
        r = Capture.ScreenSnapshot.Intersect(r, new Int32Rect(0, 0, p.W, p.H));
        if (r.Width <= 0 || r.Height <= 0) return src;
        int x0 = Math.Max(0, r.X - 1), x1 = Math.Min(p.W - 1, r.X + r.Width);
        int y0 = Math.Max(0, r.Y - 1), y1 = Math.Min(p.H - 1, r.Y + r.Height);
        for (int y = r.Y; y < r.Y + r.Height; y++)
            for (int x = r.X; x < r.X + r.Width; x++)
            {
                double fx = (x - x0) / (double)Math.Max(1, x1 - x0), fy = (y - y0) / (double)Math.Max(1, y1 - y0);
                int i = p.Index(x, y), L = p.Index(x0, y), R = p.Index(x1, y), T = p.Index(x, y0), B = p.Index(x, y1);
                for (int c = 0; c < 4; c++)
                {
                    double h = p.Data[L + c] * (1 - fx) + p.Data[R + c] * fx;
                    double v = p.Data[T + c] * (1 - fy) + p.Data[B + c] * fy;
                    p.Data[i + c] = Clamp((h + v) / 2);
                }
            }
        return Write(p);
    }

    public static Color AverageColor(BitmapSource src)
    {
        var p = Read(src);
        long b = 0, g = 0, r = 0; int n = Math.Max(1, p.W * p.H);
        for (int i = 0; i < p.Data.Length; i += 4) { b += p.Data[i]; g += p.Data[i + 1]; r += p.Data[i + 2]; }
        return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    public static Color PixelAt(BitmapSource src, int x, int y)
    {
        if (x < 0 || y < 0 || x >= src.PixelWidth || y >= src.PixelHeight) return Colors.Transparent;
        var buf = new byte[4];
        new FormatConvertedBitmap(new CroppedBitmap(src, new Int32Rect(x, y, 1, 1)), PixelFormats.Bgra32, null, 0).CopyPixels(buf, 4, 0);
        return Color.FromArgb(buf[3], buf[2], buf[1], buf[0]);
    }

    delegate void PixelFn(ref byte b, ref byte g, ref byte r);

    static BitmapSource PerPixel(BitmapSource src, PixelFn fn)
    {
        var p = Read(src);
        for (int i = 0; i < p.Data.Length; i += 4) fn(ref p.Data[i], ref p.Data[i + 1], ref p.Data[i + 2]);
        return Write(p);
    }

    static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
