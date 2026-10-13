using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MaxSnapr.Video;

/// <summary>Decodes an animated GIF into fully composited frames with their delays.</summary>
public static class GifPlayer
{
    public sealed record Frame(BitmapSource Image, int DelayMs);

    public static List<Frame> Load(string path, int maxFrames = 2000)
    {
        using var fs = File.OpenRead(path);
        var dec = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        int w = dec.Frames[0].PixelWidth, h = dec.Frames[0].PixelHeight;
        if (dec.Metadata is BitmapMetadata m)
        {
            w = Get(m, "/logscrdesc/Width", w);
            h = Get(m, "/logscrdesc/Height", h);
        }
        var result = new List<Frame>();
        var canvas = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        foreach (var f in dec.Frames)
        {
            if (result.Count >= maxFrames) break;
            var meta = f.Metadata as BitmapMetadata;
            int left = Get(meta, "/imgdesc/Left", 0), top = Get(meta, "/imgdesc/Top", 0);
            int delay = Get(meta, "/grctlext/Delay", 10) * 10;
            int disposal = Get(meta, "/grctlext/Disposal", 0);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawImage(f, new Rect(left, top, f.PixelWidth, f.PixelHeight));
            canvas.Render(dv);
            var snapshot = new WriteableBitmap(canvas);
            snapshot.Freeze();
            result.Add(new Frame(snapshot, delay <= 10 ? 100 : delay));
            if (disposal == 2)
            {
                // Restore to background: clear that rectangle.
                var copy = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                var clear = new DrawingVisual();
                using (var dc = clear.RenderOpen())
                {
                    dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, w, h)), new RectangleGeometry(new Rect(left, top, f.PixelWidth, f.PixelHeight))));
                    dc.DrawImage(snapshot, new Rect(0, 0, w, h));
                    dc.Pop();
                }
                copy.Render(clear);
                canvas = copy;
            }
        }
        return result;
    }

    public static TimeSpan TotalDuration(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var dec = new GifBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.None);
            int total = 0;
            foreach (var f in dec.Frames) { int d = Get(f.Metadata as BitmapMetadata, "/grctlext/Delay", 10) * 10; total += d <= 10 ? 100 : d; }
            return TimeSpan.FromMilliseconds(total);
        }
        catch { return TimeSpan.Zero; }
    }

    static int Get(BitmapMetadata? m, string query, int fallback)
    {
        try { return m?.GetQuery(query) is { } v ? Convert.ToInt32(v) : fallback; }
        catch { return fallback; }
    }
}
