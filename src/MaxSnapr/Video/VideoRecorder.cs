using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using MaxSnapr.Native;
using MaxSnapr.Services;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace MaxSnapr.Video;

public enum WebcamShape { Circle, Rounded, Rectangle }
public enum WebcamCorner { BottomRight, BottomLeft, TopRight, TopLeft, Custom }

public sealed class RecorderOptions
{
    /// <summary>Region in physical virtual-screen coordinates.</summary>
    public System.Windows.Int32Rect Region { get; set; }
    public int Fps { get; set; } = 30;
    public bool CaptureCursor { get; set; } = true;
    public bool HighlightClicks { get; set; } = true;
    public bool RecordMicrophone { get; set; }
    public string? MicrophoneId { get; set; }
    public bool RecordSystemAudio { get; set; } = true;
    public bool Webcam { get; set; }
    public string? WebcamId { get; set; }
    public WebcamShape WebcamShape { get; set; } = WebcamShape.Circle;
    public WebcamCorner WebcamCorner { get; set; } = WebcamCorner.BottomRight;
    public double WebcamScale { get; set; } = 0.22;
    /// <summary>Centre of the camera picture as fractions of the frame, used when the corner is Custom.</summary>
    public double WebcamX { get; set; } = 0.85;
    public double WebcamY { get; set; } = 0.8;
    public bool WebcamFullScreen { get; set; }
    public string OutputPath { get; set; } = "";
    /// <summary>Camera already opened by the caller (setup preview); the recorder uses it but does not dispose it.</summary>
    public WebcamSource? SharedWebcam { get; set; }
    /// <summary>Extra audio mixed into the track; the self-test uses a tone so no real sound is recorded.</summary>
    public NAudio.Wave.ISampleProvider? TestAudio { get; set; }
    /// <summary>Optional pixel source for tests; default is the GDI screen grabber.</summary>
    public Func<int, int, Bitmap, bool>? FrameSource { get; set; }
}

/// <summary>
/// Records screen (+cursor, click rings, webcam) and mixed microphone/system audio to MP4 (H.264/AAC)
/// through a MediaStreamSource fed in real time into the Windows MediaTranscoder.
/// </summary>
public sealed class VideoRecorder : IDisposable
{
    readonly RecorderOptions _o;
    readonly int _w, _h;
    readonly BlockingCollection<(byte[] data, TimeSpan time)> _frames = new(new ConcurrentQueue<(byte[], TimeSpan)>(), 90);
    readonly Stopwatch _clock = new();
    TimeSpan _pausedTotal;
    DateTime _pauseStarted;
    volatile bool _paused, _stopping, _cancelled;
    Thread? _captureThread;
    Task? _transcodeTask;
    AudioMixer? _audio;
    volatile WebcamSource? _webcam;
    bool _webcamOwned;
    readonly List<(System.Drawing.Point pos, long ms)> _clicks = new();
    MouseHook? _hook;
    long _framesCaptured;

    public event EventHandler<Exception>? Failed;

    public VideoRecorder(RecorderOptions o)
    {
        _o = o;
        // H.264 requires even dimensions.
        _w = Math.Max(2, o.Region.Width & ~1);
        _h = Math.Max(2, o.Region.Height & ~1);
    }

    public TimeSpan Elapsed => Clock();
    public bool IsPaused => _paused;
    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);
    public bool WebcamVisible { get; set; }
    public bool WebcamFullScreen => _o.WebcamFullScreen;
    public bool HasAudio => _audio != null;
    public bool HasMicrophone => _audio?.HasMicrophone == true;
    public bool MicMuted { get => _audio?.MicMuted == true; set { if (_audio != null) _audio.MicMuted = value; } }
    public float TakeMicLevel() => _audio?.TakeMicLevel() ?? 0;
    public float TakeSystemLevel() => _audio?.TakeSystemLevel() ?? 0;

    /// <summary>Uses a camera opened by the session (for example when it is switched on mid-recording).</summary>
    public void AttachWebcam(WebcamSource? cam)
    {
        if (_webcamOwned) _webcam?.Dispose();
        _webcamOwned = false;
        _webcam = cam;
    }

    TimeSpan Clock()
    {
        var t = _clock.Elapsed - _pausedTotal;
        if (_paused) t -= DateTime.UtcNow - _pauseStarted;
        return t < TimeSpan.Zero ? TimeSpan.Zero : t;
    }

    public async Task StartAsync()
    {
        bool hasAudio = _o.RecordMicrophone || _o.RecordSystemAudio || _o.TestAudio != null;
        if (hasAudio)
        {
            try { _audio = new AudioMixer(_o.RecordMicrophone, _o.MicrophoneId, _o.RecordSystemAudio, Clock, () => _paused, _o.TestAudio); }
            catch (Exception ex) { Log.Error("Audio no disponible; se graba sin sonido", ex); _audio = null; }
        }
        WebcamVisible = _o.Webcam;
        if (_o.SharedWebcam != null) _webcam = _o.SharedWebcam;
        else if (_o.Webcam)
        {
            try { _webcam = await WebcamSource.StartAsync(_o.WebcamId); _webcamOwned = true; }
            catch (Exception ex) { Log.Error("Cámara no disponible", ex); _webcam = null; }
        }
        if (_o.HighlightClicks && _o.FrameSource == null)
        {
            _hook = new MouseHook();
            _hook.Click += p => { lock (_clicks) _clicks.Add((p, _clock.ElapsedMilliseconds)); };
        }

        var videoProps = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_w, (uint)_h);
        var mss = _audio != null
            ? new MediaStreamSource(new VideoStreamDescriptor(videoProps), new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(AudioMixer.SampleRate, AudioMixer.Channels, 16)))
            : new MediaStreamSource(new VideoStreamDescriptor(videoProps));
        mss.BufferTime = TimeSpan.Zero;
        mss.CanSeek = false;
        mss.SampleRequested += OnSampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)_w;
        profile.Video.Height = (uint)_h;
        profile.Video.FrameRate.Numerator = (uint)_o.Fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Clamp(_w * _h * _o.Fps * 0.12, 1_000_000, 40_000_000);
        if (_audio == null) profile.Audio = null;
        else
        {
            profile.Audio = AudioEncodingProperties.CreateAac(AudioMixer.SampleRate, AudioMixer.Channels, 160000);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_o.OutputPath)!);
        var file = await (await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(_o.OutputPath))).CreateFileAsync(Path.GetFileName(_o.OutputPath), CreationCollisionOption.ReplaceExisting);
        var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
        var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(mss, stream, profile);
        if (!prep.CanTranscode) throw new InvalidOperationException("Windows no puede codificar el vídeo: " + prep.FailureReason);

        _clock.Start();
        _audio?.Start();
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "MaxSnapr video capture", Priority = ThreadPriority.AboveNormal };
        _captureThread.Start();
        _transcodeTask = Task.Run(async () =>
        {
            try { await prep.TranscodeAsync(); }
            finally { stream.Dispose(); }
        });
    }

    public void ToggleWebcamFullScreen() => _o.WebcamFullScreen = !_o.WebcamFullScreen;

    /// <summary>Applies a new camera position, size or shape from the next frame on.</summary>
    public void UpdateWebcamLayout(VideoSettings s)
    {
        _o.WebcamShape = s.WebcamShape;
        _o.WebcamCorner = s.WebcamCorner;
        _o.WebcamScale = s.WebcamSize;
        _o.WebcamX = s.WebcamX;
        _o.WebcamY = s.WebcamY;
    }

    public void Pause()
    {
        if (_paused) return;
        _pauseStarted = DateTime.UtcNow;
        _paused = true;
    }

    public void Resume()
    {
        if (!_paused) return;
        _pausedTotal += DateTime.UtcNow - _pauseStarted;
        _paused = false;
    }

    /// <summary>Stops and finalises the file. Returns false when nothing usable was written.</summary>
    public async Task<bool> StopAsync(bool cancel = false)
    {
        _cancelled = cancel;
        _stopping = true;
        _captureThread?.Join(3000);
        _frames.CompleteAdding();
        _audio?.Stop();
        try { if (_transcodeTask != null) await _transcodeTask; }
        catch (Exception ex) { Log.Error("Finalizar vídeo", ex); }
        Dispose();
        if (cancel) { TryDelete(_o.OutputPath); return false; }
        return File.Exists(_o.OutputPath) && new FileInfo(_o.OutputPath).Length > 0;
    }

    static void TryDelete(string p) { try { File.Delete(p); } catch { } }

    void CaptureLoop()
    {
        using var bmp = new Bitmap(_w, _h, PixelFormat.Format32bppArgb);
        var frameInterval = TimeSpan.FromSeconds(1.0 / _o.Fps);
        var next = TimeSpan.Zero;
        try
        {
            while (!_stopping)
            {
                if (_paused) { Thread.Sleep(10); continue; }
                var now = Clock();
                if (now < next) { Thread.Sleep(Math.Max(1, (int)(next - now).TotalMilliseconds)); continue; }
                Grab(bmp);
                var data = new byte[_w * _h * 4];
                var bd = bmp.LockBits(new Rectangle(0, 0, _w, _h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                // Uncompressed RGB media samples are bottom-up, so copy rows in reverse.
                int rowBytes = _w * 4;
                for (int y = 0; y < _h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + (_h - 1 - y) * bd.Stride, data, y * rowBytes, rowBytes);
                bmp.UnlockBits(bd);
                // Drop frames rather than stall when the encoder falls behind.
                if (_frames.TryAdd((data, now), 200)) Interlocked.Increment(ref _framesCaptured);
                next = now + frameInterval;
                if (next < Clock() - frameInterval) next = Clock();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Captura de vídeo", ex);
            Failed?.Invoke(this, ex);
        }
    }

    void Grab(Bitmap bmp)
    {
        using var g = Graphics.FromImage(bmp);
        var r = _o.Region;
        if (_o.FrameSource != null) _o.FrameSource(_w, _h, bmp);
        else
        {
            IntPtr hdc = g.GetHdc();
            IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
            NativeMethods.BitBlt(hdc, 0, 0, _w, _h, screen, r.X, r.Y, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            if (_o.CaptureCursor) DrawCursor(hdc, r);
            g.ReleaseHdc(hdc);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_o.HighlightClicks) DrawClicks(g, r);
        if (WebcamVisible) _webcam?.Use(cam => DrawWebcam(g, cam));
    }

    static void DrawCursor(IntPtr hdc, System.Windows.Int32Rect r)
    {
        var ci = new NativeMethods.CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref ci) || ci.flags != NativeMethods.CURSOR_SHOWING) return;
        if (!NativeMethods.GetIconInfo(ci.hCursor, out var info)) return;
        if (info.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmMask);
        if (info.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmColor);
        NativeMethods.DrawIconEx(hdc, ci.ptScreenPos.X - r.X - info.xHotspot, ci.ptScreenPos.Y - r.Y - info.yHotspot, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3 /*DI_NORMAL*/);
    }

    void DrawClicks(Graphics g, System.Windows.Int32Rect r)
    {
        long now = _clock.ElapsedMilliseconds;
        lock (_clicks)
        {
            _clicks.RemoveAll(c => now - c.ms > 500);
            foreach (var (pos, ms) in _clicks)
            {
                float t = (now - ms) / 500f;
                float radius = 10 + 26 * t;
                int alpha = (int)(200 * (1 - t));
                using var pen = new Pen(Color.FromArgb(alpha, 255, 196, 0), 4);
                using var fill = new SolidBrush(Color.FromArgb(alpha / 3, 255, 196, 0));
                float x = pos.X - r.X, y = pos.Y - r.Y;
                g.FillEllipse(fill, x - radius, y - radius, radius * 2, radius * 2);
                g.DrawEllipse(pen, x - radius, y - radius, radius * 2, radius * 2);
            }
        }
    }

    void DrawWebcam(Graphics g, Bitmap cam)
    {
        RectangleF dest;
        if (_o.WebcamFullScreen)
        {
            float s = Math.Max(_w / (float)cam.Width, _h / (float)cam.Height);
            dest = new RectangleF((_w - cam.Width * s) / 2, (_h - cam.Height * s) / 2, cam.Width * s, cam.Height * s);
            g.DrawImage(cam, dest);
            return;
        }
        dest = WebcamLayout(_w, _h, _o.WebcamScale, _o.WebcamShape, _o.WebcamCorner, cam.Width / (float)cam.Height, _o.WebcamX, _o.WebcamY);
        float x = dest.X, y = dest.Y, camW = dest.Width, camH = dest.Height, size = camH;
        using var path = new GraphicsPath();
        if (_o.WebcamShape == WebcamShape.Circle) path.AddEllipse(dest);
        else if (_o.WebcamShape == WebcamShape.Rounded) AddRounded(path, dest, size * 0.12f);
        else path.AddRectangle(dest);
        var state = g.Save();
        g.SetClip(path);
        // Centre-crop the camera image into the shape.
        float sc = Math.Max(camW / cam.Width, camH / cam.Height);
        g.DrawImage(cam, new RectangleF(x + (camW - cam.Width * sc) / 2, y + (camH - cam.Height * sc) / 2, cam.Width * sc, cam.Height * sc));
        g.Restore(state);
        using var border = new Pen(Color.White, 3);
        g.DrawPath(border, path);
    }

    /// <summary>Where the camera picture goes inside a w×h frame; shared with the on-screen preview.</summary>
    public static RectangleF WebcamLayout(int w, int h, double scale, WebcamShape shape, WebcamCorner corner, float aspect, double cx = 0.5, double cy = 0.5)
    {
        float size = Math.Min(w, h) * (float)Math.Clamp(scale, 0.05, 0.95);
        float camW = shape == WebcamShape.Circle ? size : size * aspect, camH = size;
        if (camW > w) { camH *= w / camW; camW = w; }
        if (corner == WebcamCorner.Custom)
            return new RectangleF(Math.Clamp((float)(cx * w) - camW / 2, 0, w - camW), Math.Clamp((float)(cy * h) - camH / 2, 0, h - camH), camW, camH);
        float m = Math.Max(8, Math.Min(w, h) * 0.025f);
        float x = corner is WebcamCorner.BottomRight or WebcamCorner.TopRight ? w - camW - m : m;
        float y = corner is WebcamCorner.BottomRight or WebcamCorner.BottomLeft ? h - camH - m : m;
        return new RectangleF(x, y, camW, camH);
    }

    public static void AddRounded(GraphicsPath p, RectangleF r, float rad)
    {
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
    }

    void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var req = args.Request;
        if (req.StreamDescriptor is VideoStreamDescriptor)
        {
            // Blocks the transcoder's pull until the next frame exists, so encoding follows real time.
            if (_frames.TryTake(out var f, Timeout.Infinite))
            {
                var sample = MediaStreamSample.CreateFromBuffer(f.data.AsBuffer(), f.time);
                sample.Duration = TimeSpan.FromSeconds(1.0 / _o.Fps);
                req.Sample = sample;
            }
            else req.Sample = null; // end of stream
        }
        else if (_audio != null)
        {
            var chunk = _audio.Take(() => _stopping && _frames.IsCompleted);
            if (chunk == null) { req.Sample = null; return; }
            var sample = MediaStreamSample.CreateFromBuffer(chunk.Value.data.AsBuffer(), chunk.Value.time);
            sample.Duration = chunk.Value.duration;
            req.Sample = sample;
        }
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
        if (_webcamOwned) _webcam?.Dispose();
        _webcam = null;
        _audio?.Dispose();
        _audio = null;
    }
}
