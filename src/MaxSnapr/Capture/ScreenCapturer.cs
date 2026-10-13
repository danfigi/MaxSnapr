using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxSnapr.Native;
using static MaxSnapr.Native.NativeMethods;

namespace MaxSnapr.Capture;

/// <summary>A monitor in physical (device) pixels.</summary>
public sealed record MonitorInfo(Int32Rect Bounds, Int32Rect WorkArea, double Scale, string Device, bool Primary);

/// <summary>A top-level window candidate for window capture, in physical pixels.</summary>
public sealed record WindowInfo(IntPtr Handle, Int32Rect Bounds, string Title, string ClassName, uint ProcessId);

/// <summary>Captured screen pixels plus the context needed to build a library entry.</summary>
public sealed class ScreenSnapshot
{
    public required BitmapSource Image { get; init; }
    /// <summary>Virtual-screen origin of <see cref="Image"/> in physical pixels.</summary>
    public required Int32Rect Bounds { get; init; }
    public required IReadOnlyList<MonitorInfo> Monitors { get; init; }
    public required IReadOnlyList<WindowInfo> Windows { get; init; }
    public BitmapSource? CursorImage { get; init; }
    public Point CursorPosition { get; init; }

    public BitmapSource Crop(Int32Rect r, bool includeCursor)
    {
        r = Intersect(r, new Int32Rect(0, 0, Image.PixelWidth, Image.PixelHeight));
        if (r.Width <= 0 || r.Height <= 0) return Image;
        BitmapSource cropped = new CroppedBitmap(Image, r);
        if (includeCursor && CursorImage != null)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(cropped, new Rect(0, 0, r.Width, r.Height));
                dc.DrawImage(CursorImage, new Rect(CursorPosition.X - r.X, CursorPosition.Y - r.Y, CursorImage.PixelWidth, CursorImage.PixelHeight));
            }
            var rtb = new RenderTargetBitmap(r.Width, r.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            cropped = rtb;
        }
        var result = new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
        var wb = new WriteableBitmap(result);
        wb.Freeze();
        return wb;
    }

    public static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X + a.Width, b.X + b.Width), y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return x2 <= x1 || y2 <= y1 ? new Int32Rect(0, 0, 0, 0) : new Int32Rect(x1, y1, x2 - x1, y2 - y1);
    }
}

public static class ScreenCapturer
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT __, IntPtr ___) =>
        {
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
            {
                double scale = GetDpiForMonitor(h, 0, out uint dx, out uint _dy) == 0 ? dx / 96.0 : 1.0;
                list.Add(new MonitorInfo(ToRect(mi.rcMonitor), ToRect(mi.rcWork), scale, mi.szDevice, (mi.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static Int32Rect VirtualScreen => new(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>Freezes the whole virtual desktop, window list and cursor at one moment.</summary>
    public static ScreenSnapshot Snapshot(IEnumerable<IntPtr>? exclude = null)
    {
        var vs = VirtualScreen;
        var windows = EnumerateWindows(exclude);
        var image = CaptureRect(vs);
        var (cursor, pos) = CaptureCursor();
        return new ScreenSnapshot
        {
            Image = image,
            Bounds = vs,
            Monitors = GetMonitors(),
            Windows = windows,
            CursorImage = cursor,
            CursorPosition = new Point(pos.X - vs.X, pos.Y - vs.Y),
        };
    }

    public static BitmapSource CaptureRect(Int32Rect r)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // GDI leaves alpha undefined; normalise to opaque Bgra32 at 96 DPI so 1 pixel == 1 editor unit.
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
            var stride = r.Width * 4;
            var pixels = new byte[stride * r.Height];
            conv.CopyPixels(pixels, stride, 0);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var result = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static (BitmapSource?, POINT) CaptureCursor()
    {
        var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING) return (null, default);
        IntPtr icon = CopyIcon(ci.hCursor);
        if (icon == IntPtr.Zero) return (null, default);
        try
        {
            GetIconInfo(icon, out var info);
            var src = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            var pos = ci.ptScreenPos;
            pos.X -= info.xHotspot;
            pos.Y -= info.yHotspot;
            return (src, pos);
        }
        catch { return (null, default); }
        finally { DestroyIcon(icon); }
    }

    /// <summary>Visible top-level windows in Z order (topmost first), virtual-screen relative.</summary>
    public static List<WindowInfo> EnumerateWindows(IEnumerable<IntPtr>? exclude = null)
    {
        var skip = new HashSet<IntPtr>(exclude ?? Array.Empty<IntPtr>());
        var vs = VirtualScreen;
        var list = new List<WindowInfo>();
        var sb = new StringBuilder(512);
        for (IntPtr h = GetWindow(GetDesktopWindow(), 5 /*GW_CHILD*/); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (skip.Contains(h) || !IsWindowVisible(h) || IsIconic(h)) continue;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(h, out r);
            if (r.Width < 8 || r.Height < 8) continue;
            sb.Clear(); GetClassName(h, sb, sb.Capacity); var cls = sb.ToString();
            sb.Clear(); GetWindowText(h, sb, sb.Capacity); var title = sb.ToString();
            if (cls is "Progman" or "WorkerW") continue;
            GetWindowThreadProcessId(h, out uint pid);
            list.Add(new WindowInfo(h, new Int32Rect(r.Left - vs.X, r.Top - vs.Y, r.Width, r.Height), title, cls, pid));
        }
        return list;
    }

    static Int32Rect ToRect(RECT r)
    {
        var vs = VirtualScreen;
        return new Int32Rect(r.Left - vs.X, r.Top - vs.Y, r.Width, r.Height);
    }
}
