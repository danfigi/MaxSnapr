using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxSnapr.Editor.Raster;
using MaxSnapr.Native;

namespace MaxSnapr.Capture;

/// <summary>
/// Joins overlapping frames of a scrolling area. Each new frame is matched against the previous one
/// to find the vertical (or horizontal) shift; fixed headers and footers that do not scroll are
/// detected and kept only once.
/// </summary>
public sealed class Stitcher
{
    readonly List<(RasterOps.Pixels px, int offset)> _frames = new();
    int _w, _h;
    int _pos;
    int _top, _bottom; // static rows at top/bottom
    bool _staticKnown;
    public bool Horizontal { get; }

    public Stitcher(bool horizontal = false) { Horizontal = horizontal; }

    public int FrameCount => _frames.Count;
    public int Length => _frames.Count == 0 ? 0 : _pos + (Horizontal ? _w : _h);

    /// <summary>Adds a frame; returns the detected shift (0 when nothing moved, -1 when it could not be matched).</summary>
    public int Add(BitmapSource frame)
    {
        var px = RasterOps.Read(frame);
        if (Horizontal) px = Transpose(px);
        if (_frames.Count == 0)
        {
            _w = px.W; _h = px.H;
            _frames.Add((px, 0));
            return 0;
        }
        if (px.W != _w || px.H != _h) return -1;
        var prev = _frames[^1].px;
        if (!_staticKnown)
        {
            _top = StaticRows(prev, px, fromTop: true);
            _bottom = StaticRows(prev, px, fromTop: false);
            if (_top + _bottom >= _h - 16) { _top = 0; _bottom = 0; return 0; } // nothing moved
            _staticKnown = true;
        }
        int shift = FindShift(prev, px);
        if (shift <= 0) return shift;
        _pos += shift;
        _frames.Add((px, _pos));
        return shift;
    }

    static RasterOps.Pixels Transpose(RasterOps.Pixels p)
    {
        var t = new RasterOps.Pixels { W = p.H, H = p.W, Data = new byte[p.Data.Length] };
        for (int y = 0; y < p.H; y++)
            for (int x = 0; x < p.W; x++)
                Buffer.BlockCopy(p.Data, p.Index(x, y), t.Data, t.Index(y, x), 4);
        return t;
    }

    static int[] RowSignature(RasterOps.Pixels p, int y)
    {
        // 48 luminance samples across the row.
        var s = new int[48];
        for (int i = 0; i < s.Length; i++)
        {
            int x = (int)((i + 0.5) * p.W / s.Length);
            int k = p.Index(x, y);
            s[i] = (p.Data[k] * 29 + p.Data[k + 1] * 150 + p.Data[k + 2] * 77) >> 8;
        }
        return s;
    }

    static bool RowsEqual(RasterOps.Pixels a, RasterOps.Pixels b, int y)
    {
        int stride = a.Stride, off = y * stride;
        for (int i = 0; i < stride; i += 16) if (Math.Abs(a.Data[off + i] - b.Data[off + i]) > 3) return false;
        return true;
    }

    int StaticRows(RasterOps.Pixels a, RasterOps.Pixels b, bool fromTop)
    {
        int n = 0;
        for (int i = 0; i < _h; i++)
        {
            int y = fromTop ? i : _h - 1 - i;
            if (!RowsEqual(a, b, y)) break;
            n++;
        }
        return Math.Min(n, _h / 3);
    }

    int FindShift(RasterOps.Pixels prev, RasterOps.Pixels cur)
    {
        int y0 = _top, y1 = _h - _bottom;
        int band = y1 - y0;
        if (band < 20) return -1;
        var ps = Enumerable.Range(y0, band).Select(y => RowSignature(prev, y)).ToArray();
        var cs = Enumerable.Range(y0, band).Select(y => RowSignature(cur, y)).ToArray();
        long bestErr = long.MaxValue;
        int best = -1;
        for (int shift = 1; shift < band - 10; shift++)
        {
            // prev row (shift + i) must equal cur row i.
            int overlap = band - shift;
            long err = 0;
            int step = Math.Max(1, overlap / 120);
            int count = 0;
            for (int i = 0; i < overlap; i += step)
            {
                var a = ps[shift + i]; var b = cs[i];
                for (int k = 0; k < a.Length; k++) err += Math.Abs(a[k] - b[k]);
                count++;
            }
            err = err * 1000 / Math.Max(1, count);
            if (err < bestErr) { bestErr = err; best = shift; }
        }
        // Mean absolute difference per sample must be small for a confident match.
        return bestErr / 1000.0 / 48 < 6 ? best : -1;
    }

    public BitmapSource Compose()
    {
        if (_frames.Count == 0) throw new InvalidOperationException("Sin fotogramas");
        int total = _pos + _h;
        var outPx = new RasterOps.Pixels { W = _w, H = total, Data = new byte[_w * total * 4] };
        for (int f = 0; f < _frames.Count; f++)
        {
            var (px, offset) = _frames[f];
            // Header only from the first frame, footer only from the last.
            int from = f == 0 ? 0 : _top;
            int to = f == _frames.Count - 1 ? _h : _h - _bottom;
            for (int y = from; y < to; y++)
                Buffer.BlockCopy(px.Data, y * px.Stride, outPx.Data, (offset + y) * outPx.Stride, px.Stride);
        }
        // The footer of the last frame lands at its natural place; make sure the static header stays on top.
        if (Horizontal) outPx = Transpose(outPx);
        return RasterOps.Write(outPx);
    }
}

/// <summary>Automatic (wheel-driven) and manual (user-driven) scrolling capture.</summary>
public static class ScrollCapture
{
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public int type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy, mouseData, dwFlags, time; public IntPtr extra; public int pad1, pad2; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

    static void Wheel(int notches)
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { mouseData = -120 * notches, dwFlags = 0x0800 } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <param name="region">Physical, virtual-screen relative.</param>
    public static async Task<BitmapSource?> AutoAsync(Int32Rect region, CancellationToken ct)
    {
        var vs = ScreenCapturer.VirtualScreen;
        var screenRect = new Int32Rect(region.X + vs.X, region.Y + vs.Y, region.Width, region.Height);
        NativeMethods.GetCursorPos(out var original);
        var stitch = new Stitcher();
        using var stop = StopButton.Show(screenRect, "Desplazando… pulse para detener");
        try
        {
            // Focus the content under the region centre so it receives wheel events.
            int cx = screenRect.X + screenRect.Width / 2, cy = screenRect.Y + screenRect.Height / 2;
            SetCursorPos(cx, cy);
            await Task.Delay(150, ct);
            stitch.Add(ScreenCapturer.CaptureRect(screenRect));
            int stillCount = 0;
            for (int i = 0; i < 200 && !stop.Stopped && !ct.IsCancellationRequested; i++)
            {
                SetCursorPos(cx, cy);
                Wheel(3);
                await Task.Delay(320, ct);
                int shift = stitch.Add(ScreenCapturer.CaptureRect(screenRect));
                if (shift == 0) { if (++stillCount >= 2) break; } // reached the end
                else stillCount = 0;
                if (stitch.Length > 30000) break;
            }
        }
        catch (TaskCanceledException) { }
        finally { SetCursorPos(original.X, original.Y); }
        return stitch.FrameCount > 0 ? stitch.Compose() : null;
    }

    /// <summary>User scrolls (vertically or horizontally) until pressing Detener.</summary>
    public static async Task<BitmapSource?> ManualAsync(Int32Rect region, CancellationToken ct)
    {
        var vs = ScreenCapturer.VirtualScreen;
        var screenRect = new Int32Rect(region.X + vs.X, region.Y + vs.Y, region.Width, region.Height);
        Stitcher? stitch = null;
        Stitcher vertical = new(), horizontal = new(horizontal: true);
        using var stop = StopButton.Show(screenRect, "Desplácese despacio por el contenido · Detener");
        var first = ScreenCapturer.CaptureRect(screenRect);
        vertical.Add(first);
        horizontal.Add(first);
        while (!stop.Stopped && !ct.IsCancellationRequested)
        {
            await Task.Delay(180);
            var f = ScreenCapturer.CaptureRect(screenRect);
            if (stitch == null)
            {
                // The first real movement decides the direction.
                int v = vertical.Add(f), h = horizontal.Add(f);
                if (v > 0) stitch = vertical; else if (h > 0) stitch = horizontal;
            }
            else stitch.Add(f);
            if (stitch?.Length > 30000) break;
        }
        return (stitch ?? vertical).Compose();
    }

    /// <summary>Small floating stop button, excluded from capture.</summary>
    sealed class StopButton : IDisposable
    {
        readonly Window _w;
        public bool Stopped { get; private set; }

        StopButton(Int32Rect around, string text)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = text, Foreground = Brushes.White, Margin = new Thickness(12, 6, 12, 6) },
                Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += (_, _) => Stopped = true;
            _w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight, ShowActivated = false, Content = b,
            };
            _w.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Stopped = true; };
            _w.Loaded += (_, _) =>
            {
                Video.CaptureExclusion.Apply(_w);
                var s = VisualTreeHelper.GetDpi(_w).DpiScaleX;
                int w = (int)(_w.ActualWidth * s), h = (int)(_w.ActualHeight * s);
                int y = around.Y + around.Height + 8;
                var vs = ScreenCapturer.VirtualScreen;
                if (y + h > vs.Y + vs.Height) y = Math.Max(vs.Y, around.Y - h - 8);
                Video.CaptureExclusion.PlacePhysical(_w, new Int32Rect(around.X + (around.Width - w) / 2, y, w, h));
            };
            _w.Show();
        }

        public static StopButton Show(Int32Rect around, string text) => new(around, text);
        public void Dispose() => _w.Close();
    }
}
