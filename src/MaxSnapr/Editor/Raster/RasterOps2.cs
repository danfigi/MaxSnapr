using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MaxSnapr.Editor.Raster;

public static class RasterOps2
{
    /// <summary>
    /// Perspective tilt: horizontal (-1..1) narrows the left or right side, vertical (-1..1) the top or bottom.
    /// Implemented as an inverse projective mapping with bilinear sampling.
    /// </summary>
    public static BitmapSource Perspective(BitmapSource src, double horizontal, double vertical)
    {
        var p = RasterOps.Read(src);
        int w = p.W, h = p.H;
        double hx = Math.Clamp(horizontal, -0.9, 0.9) * 0.35, vy = Math.Clamp(vertical, -0.9, 0.9) * 0.35;
        // Destination quad corners (TL, TR, BR, BL).
        var d = new[]
        {
            new Point(Math.Max(0, vy) * w, Math.Max(0, hx) * h),
            new Point(w - Math.Max(0, vy) * w, Math.Max(0, -hx) * h),
            new Point(w - Math.Max(0, -vy) * w, h - Math.Max(0, -hx) * h),
            new Point(Math.Max(0, -vy) * w, h - Math.Max(0, hx) * h),
        };
        var s = new[] { new Point(0, 0), new Point(w, 0), new Point(w, h), new Point(0, h) };
        var m = Homography(d, s); // maps destination → source
        var o = new RasterOps.Pixels { W = w, H = h, Data = new byte[p.Data.Length] };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double den = m[6] * x + m[7] * y + 1;
                double sx = (m[0] * x + m[1] * y + m[2]) / den, sy = (m[3] * x + m[4] * y + m[5]) / den;
                if (sx < 0 || sy < 0 || sx > w - 1 || sy > h - 1) continue;
                Sample(p, sx, sy, o.Data, o.Index(x, y));
            }
        return RasterOps.Write(o);
    }

    static void Sample(RasterOps.Pixels p, double x, double y, byte[] dst, int di)
    {
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, p.W - 1), y1 = Math.Min(y0 + 1, p.H - 1);
        double fx = x - x0, fy = y - y0;
        for (int c = 0; c < 4; c++)
        {
            double a = p.Data[p.Index(x0, y0) + c] * (1 - fx) + p.Data[p.Index(x1, y0) + c] * fx;
            double b = p.Data[p.Index(x0, y1) + c] * (1 - fx) + p.Data[p.Index(x1, y1) + c] * fx;
            dst[di + c] = (byte)Math.Clamp(a * (1 - fy) + b * fy, 0, 255);
        }
    }

    /// <summary>3×3 homography (h33 = 1) mapping the four points <paramref name="from"/> onto <paramref name="to"/>.</summary>
    static double[] Homography(Point[] from, Point[] to)
    {
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = from[i].X, y = from[i].Y, u = to[i].X, v = to[i].Y;
            double[] r1 = { x, y, 1, 0, 0, 0, -u * x, -u * y, u };
            double[] r2 = { 0, 0, 0, x, y, 1, -v * x, -v * y, v };
            for (int k = 0; k < 9; k++) { a[i * 2, k] = r1[k]; a[i * 2 + 1, k] = r2[k]; }
        }
        // Gauss-Jordan on the 8×8 system.
        for (int col = 0; col < 8; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 8; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            for (int k = 0; k < 9; k++) (a[col, k], a[piv, k]) = (a[piv, k], a[col, k]);
            double div = a[col, col];
            if (Math.Abs(div) < 1e-12) continue;
            for (int k = 0; k < 9; k++) a[col, k] /= div;
            for (int r = 0; r < 8; r++)
            {
                if (r == col) continue;
                double f = a[r, col];
                for (int k = 0; k < 9; k++) a[r, k] -= f * a[col, k];
            }
        }
        var h = new double[8];
        for (int i = 0; i < 8; i++) h[i] = a[i, 8];
        return h;
    }

    /// <summary>Folded page corner at the bottom right, with shading underneath.</summary>
    public static BitmapSource PageCurl(BitmapSource src, double size)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        double s = Math.Clamp(size, 0.05, 0.45) * Math.Min(w, h);
        return RasterOps.Render(w, h, dc =>
        {
            var corner = new StreamGeometry();
            using (var c = corner.Open())
            {
                c.BeginFigure(new Point(w - s, h), true, true);
                c.LineTo(new Point(w, h - s), true, true);
                c.LineTo(new Point(w, h), true, true);
            }
            var visible = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, w, h)), corner);
            dc.PushClip(visible);
            dc.DrawImage(src, new Rect(0, 0, w, h));
            dc.Pop();
            // Shadow cast by the fold onto the page.
            var shadow = new StreamGeometry();
            using (var c = shadow.Open())
            {
                c.BeginFigure(new Point(w - s * 1.15, h), true, true);
                c.LineTo(new Point(w, h - s * 1.15), true, true);
                c.LineTo(new Point(w, h - s), true, true);
                c.LineTo(new Point(w - s, h), true, true);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), null, shadow);
            // The back of the page.
            var flap = new StreamGeometry();
            using (var c = flap.Open())
            {
                c.BeginFigure(new Point(w - s, h), true, true);
                c.LineTo(new Point(w, h - s), true, true);
                c.LineTo(new Point(w - s * 0.92, h - s * 0.92), true, true);
            }
            var grad = new LinearGradientBrush(Color.FromRgb(0xF4, 0xF4, 0xF4), Color.FromRgb(0xC9, 0xCC, 0xD1), new Point(1, 1), new Point(0, 0));
            dc.DrawGeometry(grad, new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 1), flap);
        });
    }
}
