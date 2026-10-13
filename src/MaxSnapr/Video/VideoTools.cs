using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace MaxSnapr.Video;

public sealed record VideoInfo(TimeSpan Duration, int Width, int Height);

public sealed record GifProfile(string Name, int MaxWidth, int Fps)
{
    public static readonly GifProfile[] All =
    {
        new("Pequeño (480 px, 10 fps)", 480, 10),
        new("Mediano (720 px, 12 fps)", 720, 12),
        new("Grande (1080 px, 15 fps)", 1080, 15),
    };
}

/// <summary>Video operations on top of Windows.Media.Editing (no external tools).</summary>
public static class VideoTools
{
    public static async Task<VideoInfo> GetInfoAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var p = await file.Properties.GetVideoPropertiesAsync();
        return new VideoInfo(p.Duration, (int)p.Width, (int)p.Height);
    }

    static async Task<MediaClip> ClipAsync(string path) => await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));

    public static async Task<BitmapSource> FrameAsync(string path, TimeSpan at, int maxWidth = 0)
    {
        var comp = new MediaComposition();
        comp.Clips.Add(await ClipAsync(path));
        return await FrameAsync(comp, at, maxWidth);
    }

    static async Task<BitmapSource> FrameAsync(MediaComposition comp, TimeSpan at, int maxWidth)
    {
        var clip = comp.Clips[0];
        var props = clip.GetVideoEncodingProperties();
        int w = (int)props.Width, h = (int)props.Height;
        if (maxWidth > 0 && w > maxWidth) { h = (int)Math.Round(h * maxWidth / (double)w); w = maxWidth; }
        if (at >= comp.Duration) at = comp.Duration - TimeSpan.FromMilliseconds(40);
        if (at < TimeSpan.Zero) at = TimeSpan.Zero;
        using var stream = await comp.GetThumbnailAsync(at, w & ~1, h & ~1, VideoFramePrecision.NearestFrame);
        var ms = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(ms);
        ms.Position = 0;
        var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    static async Task RenderAsync(MediaComposition comp, string outPath, IProgress<double>? progress, CancellationToken ct, (uint w, uint h)? size = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outPath));
        var file = await folder.CreateFileAsync(Path.GetFileName(outPath), CreationCollisionOption.ReplaceExisting);
        // Image clips carry no encoding properties, so callers pass the output size explicitly.
        var (w, h) = size ?? (comp.Clips[0].GetVideoEncodingProperties().Width, comp.Clips[0].GetVideoEncodingProperties().Height);
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = w & ~1u;
        profile.Video.Height = h & ~1u;
        profile.Video.Bitrate = (uint)Math.Clamp(w * h * 30 * 0.12, 1_000_000, 40_000_000);
        var op = comp.RenderToFileAsync(file, MediaTrimmingPreference.Precise, profile);
        op.Progress = (_, p) => progress?.Report(p / 100.0);
        using (ct.Register(() => op.Cancel()))
        {
            var result = await op;
            if (result != TranscodeFailureReason.None) throw new InvalidOperationException("No se pudo generar el vídeo: " + result);
        }
    }

    public static async Task TrimAsync(string path, TimeSpan start, TimeSpan end, string outPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var clip = await ClipAsync(path);
        clip.TrimTimeFromStart = start;
        clip.TrimTimeFromEnd = clip.OriginalDuration - end;
        var comp = new MediaComposition();
        comp.Clips.Add(clip);
        await RenderAsync(comp, outPath, progress, ct);
    }

    /// <summary>Removes [start, end) and joins the remaining parts.</summary>
    public static async Task CutAsync(string path, TimeSpan start, TimeSpan end, string outPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var comp = new MediaComposition();
        var a = await ClipAsync(path);
        if (start > TimeSpan.FromMilliseconds(50))
        {
            a.TrimTimeFromEnd = a.OriginalDuration - start;
            comp.Clips.Add(a);
        }
        var b = await ClipAsync(path);
        if (b.OriginalDuration - end > TimeSpan.FromMilliseconds(50))
        {
            b.TrimTimeFromStart = end;
            comp.Clips.Add(b);
        }
        if (comp.Clips.Count == 0) throw new InvalidOperationException("La selección abarca todo el vídeo.");
        await RenderAsync(comp, outPath, progress, ct);
    }

    public static async Task ConcatAsync(IEnumerable<string> paths, string outPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var comp = new MediaComposition();
        foreach (var p in paths) comp.Clips.Add(await ClipAsync(p));
        await RenderAsync(comp, outPath, progress, ct);
    }

    public static async Task ImagesToVideoAsync(IReadOnlyList<string> imagePaths, TimeSpan each, string outPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var comp = new MediaComposition();
        uint w = 0, h = 0;
        foreach (var p in imagePaths)
        {
            var img = Services.ExportService.LoadImage(p);
            w = Math.Max(w, (uint)img.PixelWidth);
            h = Math.Max(h, (uint)img.PixelHeight);
            comp.Clips.Add(await MediaClip.CreateFromImageFileAsync(await StorageFile.GetFileFromPathAsync(p), each));
        }
        double s = Math.Min(1, 1920.0 / Math.Max(w, h));
        await RenderAsync(comp, outPath, progress, ct, ((uint)(w * s), (uint)(h * s)));
    }

    /// <summary>Converts a time range of a video into an animated GIF using the built-in encoder.</summary>
    public static async Task ToGifAsync(string path, TimeSpan start, TimeSpan end, int fps, int maxWidth, string outPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var comp = new MediaComposition();
        comp.Clips.Add(await ClipAsync(path));
        if (end <= start) end = comp.Duration;
        var step = TimeSpan.FromSeconds(1.0 / Math.Max(1, fps));
        int count = Math.Max(1, (int)((end - start).TotalSeconds * fps));
        using var fs = File.Create(outPath);
        GifEncoder? gif = null;
        int delay = (int)Math.Round(100.0 / fps);
        try
        {
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var frame = await FrameAsync(comp, start + step * i, maxWidth);
                var px = Editor.Raster.RasterOps.Read(frame);
                gif ??= new GifEncoder(fs, px.W, px.H);
                gif.AddFrame(px.Data, delay);
                progress?.Report((i + 1) / (double)count);
            }
        }
        finally { gif?.Dispose(); }
    }

    /// <summary>Encodes in-memory frames (e.g. from images) as GIF.</summary>
    public static void FramesToGif(IReadOnlyList<BitmapSource> frames, int delayCs, string outPath)
    {
        if (frames.Count == 0) return;
        int w = frames[0].PixelWidth, h = frames[0].PixelHeight;
        using var fs = File.Create(outPath);
        using var gif = new GifEncoder(fs, w, h);
        foreach (var f in frames)
        {
            var img = f.PixelWidth == w && f.PixelHeight == h ? f : Editor.Raster.RasterOps.Resize(f, w, h);
            gif.AddFrame(Editor.Raster.RasterOps.Read(img).Data, delayCs);
        }
    }
}
