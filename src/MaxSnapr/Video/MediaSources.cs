using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using MaxSnapr.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace MaxSnapr.Video;

public sealed record DeviceEntry(string Id, string Name);

public static class Devices
{
    public static List<DeviceEntry> Microphones()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => new DeviceEntry(d.ID, d.FriendlyName)).ToList();
        }
        catch (Exception ex) { Log.Error("Enumerar micrófonos", ex); return new(); }
    }

    /// <summary>The chosen microphone, or the Windows default input when none is chosen or it was unplugged.</summary>
    public static MMDevice Microphone(MMDeviceEnumerator en, string? id)
    {
        if (id != null)
        {
            try { var d = en.GetDevice(id); if (d.State == DeviceState.Active) return d; } catch { }
        }
        return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    }

    public static string? DefaultMicrophoneName()
    {
        try { using var en = new MMDeviceEnumerator(); return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).FriendlyName; }
        catch { return null; }
    }

    public static async Task<List<DeviceEntry>> CamerasAsync()
    {
        try
        {
            var list = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture);
            return list.Select(d => new DeviceEntry(d.Id, d.Name)).ToList();
        }
        catch (Exception ex) { Log.Error("Enumerar cámaras", ex); return new(); }
    }
}

/// <summary>Mixes microphone and system audio (WASAPI loopback) into 48 kHz stereo PCM16 chunks timed to the recording clock.</summary>
public sealed class AudioMixer : IDisposable
{
    public const uint SampleRate = 48000;
    public const uint Channels = 2;

    readonly List<IWaveIn> _captures = new();
    readonly MixingSampleProvider _mixer = new(WaveFormat.CreateIeeeFloatWaveFormat((int)SampleRate, (int)Channels)) { ReadFully = true };
    readonly Func<TimeSpan> _clock;
    readonly Func<bool> _paused;
    readonly BlockingCollection<(byte[] data, TimeSpan time, TimeSpan duration)> _chunks = new(400);
    Thread? _pump;
    volatile bool _stop;
    long _produced;

    VolumeSampleProvider? _micVolume;
    PeakMeter? _micMeter, _systemMeter;

    /// <param name="extra">Additional source mixed in as-is (the self-test uses a tone so no real audio is recorded).</param>
    public AudioMixer(bool mic, string? micId, bool system, Func<TimeSpan> clock, Func<bool> paused, ISampleProvider? extra = null)
    {
        _clock = clock;
        _paused = paused;
        using var en = new MMDeviceEnumerator();
        if (system) _systemMeter = Add(new WasapiLoopbackCapture(), null);
        if (mic)
        {
            _micMeter = Add(new WasapiCapture(Devices.Microphone(en, micId)) { ShareMode = AudioClientShareMode.Shared }, v => _micVolume = v);
        }
        if (extra != null) _mixer.AddMixerInput(Normalize(extra));
        if (_captures.Count == 0 && extra == null) throw new InvalidOperationException("Sin fuentes de audio");
    }

    public bool HasMicrophone => _micMeter != null;
    public bool MicMuted
    {
        get => _micVolume?.Volume == 0f;
        set { if (_micVolume != null) _micVolume.Volume = value ? 0f : 1f; }
    }
    /// <summary>Peak since the last call, 0..1 (the meter is read before muting, so a muted mic still shows activity).</summary>
    public float TakeMicLevel() => _micMeter?.Take() ?? 0;
    public float TakeSystemLevel() => _systemMeter?.Take() ?? 0;

    PeakMeter Add(WasapiCapture capture, Action<VolumeSampleProvider>? volume)
    {
        var buffer = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = true, BufferDuration = TimeSpan.FromSeconds(3) };
        capture.DataAvailable += (_, e) => { if (!_paused()) buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); };
        var meter = new PeakMeter(Normalize(buffer.ToSampleProvider()));
        ISampleProvider sp = meter;
        if (volume != null) { var v = new VolumeSampleProvider(meter); volume(v); sp = v; }
        _mixer.AddMixerInput(sp);
        _captures.Add(capture);
        return meter;
    }

    static ISampleProvider Normalize(ISampleProvider sp)
    {
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2) sp = new Downmix(sp);
        if (sp.WaveFormat.SampleRate != SampleRate) sp = new WdlResamplingSampleProvider(sp, (int)SampleRate);
        return sp;
    }

    public void Start()
    {
        foreach (var c in _captures) c.StartRecording();
        _pump = new Thread(Pump) { IsBackground = true, Name = "MaxSnapr audio" };
        _pump.Start();
    }

    void Pump()
    {
        var buf = new float[SampleRate * Channels]; // up to 1 s per read
        while (!_stop)
        {
            long target = (long)(_clock().TotalSeconds * SampleRate);
            long frames = Math.Min(target - _produced, SampleRate);
            if (frames < SampleRate / 50) { Thread.Sleep(10); continue; }
            int samples = (int)frames * (int)Channels;
            _mixer.Read(buf, 0, samples);
            var pcm = new byte[samples * 2];
            for (int i = 0; i < samples; i++)
            {
                short s = (short)(Math.Clamp(buf[i], -1f, 1f) * short.MaxValue);
                pcm[i * 2] = (byte)s;
                pcm[i * 2 + 1] = (byte)(s >> 8);
            }
            var time = TimeSpan.FromSeconds(_produced / (double)SampleRate);
            _produced += frames;
            _chunks.TryAdd((pcm, time, TimeSpan.FromSeconds(frames / (double)SampleRate)), 500);
        }
        _chunks.CompleteAdding();
    }

    /// <summary>Next chunk, or null at the end of the recording.</summary>
    public (byte[] data, TimeSpan time, TimeSpan duration)? Take(Func<bool> ended)
    {
        while (true)
        {
            if (_chunks.TryTake(out var c, 100)) return c;
            if (_chunks.IsCompleted || ended()) return null;
        }
    }

    public void Stop()
    {
        _stop = true;
        foreach (var c in _captures) { try { c.StopRecording(); } catch { } }
        _pump?.Join(1000);
    }

    public void Dispose()
    {
        Stop();
        foreach (var c in _captures) c.Dispose();
        _captures.Clear();
    }

    sealed class PeakMeter : ISampleProvider
    {
        readonly ISampleProvider _src;
        float _peak;
        public PeakMeter(ISampleProvider src) => _src = src;
        public WaveFormat WaveFormat => _src.WaveFormat;
        public float Take() { var p = _peak; _peak = 0; return p; }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = _src.Read(buffer, offset, count);
            float peak = _peak;
            for (int i = offset; i < offset + n; i++) { float a = Math.Abs(buffer[i]); if (a > peak) peak = a; }
            _peak = peak;
            return n;
        }
    }

    sealed class Downmix : ISampleProvider
    {
        readonly ISampleProvider _src;
        float[] _tmp = Array.Empty<float>();
        public Downmix(ISampleProvider src) { _src = src; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2); }
        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int ch = _src.WaveFormat.Channels;
            int frames = count / 2;
            if (_tmp.Length < frames * ch) _tmp = new float[frames * ch];
            int read = _src.Read(_tmp, 0, frames * ch) / ch;
            for (int i = 0; i < read; i++)
            {
                buffer[offset + i * 2] = _tmp[i * ch];
                buffer[offset + i * 2 + 1] = _tmp[i * ch + 1];
            }
            return read * 2;
        }
    }
}

/// <summary>Live input level of a microphone or of the system output, for meters before recording starts.</summary>
public sealed class LevelMonitor : IDisposable
{
    readonly WasapiCapture _capture;
    float _peak;

    LevelMonitor(WasapiCapture capture)
    {
        _capture = capture;
        var f = capture.WaveFormat;
        capture.DataAvailable += (_, e) =>
        {
            float peak = _peak;
            // Shared-mode WASAPI delivers 32-bit float, usually wrapped as WAVE_FORMAT_EXTENSIBLE.
            if (f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32))
                for (int i = 0; i + 4 <= e.BytesRecorded; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
            else if (f.BitsPerSample == 16)
                for (int i = 0; i + 2 <= e.BytesRecorded; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f));
            _peak = peak;
        };
        capture.StartRecording();
    }

    public static LevelMonitor? Microphone(string? id)
    {
        try { using var en = new MMDeviceEnumerator(); return new LevelMonitor(new WasapiCapture(Devices.Microphone(en, id)) { ShareMode = AudioClientShareMode.Shared }); }
        catch (Exception ex) { Log.Error("Nivel del micrófono", ex); return null; }
    }

    public static LevelMonitor? SystemOutput()
    {
        try { return new LevelMonitor(new WasapiLoopbackCapture()); }
        catch (Exception ex) { Log.Error("Nivel del audio del sistema", ex); return null; }
    }

    public float Take() { var p = _peak; _peak = 0; return p; }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
    }
}

/// <summary>Latest webcam frame as a GDI+ bitmap.</summary>
public sealed class WebcamSource : IDisposable
{
    MediaCapture? _capture;
    MediaFrameReader? _reader;
    Bitmap? _latest;
    readonly object _gate = new();
    float[]? _mask;
    int _segBusy;
    int _frames;
    /// <summary>Frames received so far (diagnostics).</summary>
    public int FramesReceived => Volatile.Read(ref _frames);
    bool _segFailed;
    Bitmap? _bgCache;
    string? _bgKey;

    /// <summary>Background effect id (see <see cref="CameraBackgrounds"/>), applied to every frame.</summary>
    public string Background { get; set; } = CameraBackgrounds.None;

    /// <summary>A "camera" that always shows the same picture, for the self-test.</summary>
    internal static WebcamSource FromStill(Bitmap picture) => new() { _latest = picture };

    public static async Task<WebcamSource> StartAsync(string? deviceId)
    {
        var w = new WebcamSource();
        // Exclusive control lets us pick the resolution; if another app owns the camera, share it as it is.
        bool exclusive = true;
        foreach (var mode in new[] { MediaCaptureSharingMode.ExclusiveControl, MediaCaptureSharingMode.SharedReadOnly })
        {
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = mode,
            };
            if (deviceId != null) settings.VideoDeviceId = deviceId;
            w._capture = new MediaCapture();
            try { await w._capture.InitializeAsync(settings); break; }
            catch (Exception) when (mode == MediaCaptureSharingMode.ExclusiveControl)
            {
                w._capture.Dispose();
                exclusive = false;
            }
        }
        var source = w._capture!.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            ?? throw new InvalidOperationException("La cámara no ofrece vídeo en color");
        if (exclusive)
        {
            static double Fps(MediaFrameFormat f) => f.FrameRate.Denominator == 0 ? 0 : f.FrameRate.Numerator / (double)f.FrameRate.Denominator;
            // Up to 720p is plenty for a picture-in-picture and keeps background effects cheap.
            var best = source.SupportedFormats
                .Where(f => f.VideoFormat.Width <= 1280 && f.VideoFormat.Height <= 720 && Fps(f) >= 24)
                .OrderByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
                .ThenByDescending(Fps)
                .FirstOrDefault();
            if (best != null)
            {
                try { await source.SetFormatAsync(best); }
                catch (Exception ex) { Log.Error("Resolución de la cámara", ex); }
            }
        }
        w._reader = await w._capture.CreateFrameReaderAsync(source, Windows.Media.MediaProperties.MediaEncodingSubtypes.Bgra8);
        w._reader.FrameArrived += w.OnFrame;
        await w._reader.StartAsync();
        return w;
    }

    bool _frameErrorLogged;

    void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // WinRT swallows exceptions from this handler, so log the first one.
        try { ProcessFrame(sender); }
        catch (Exception ex)
        {
            if (!_frameErrorLogged) Log.Error("Fotograma de la cámara", ex);
            _frameErrorLogged = true;
        }
    }

    void ProcessFrame(MediaFrameReader sender)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var sb = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (sb == null) return;
        using var bgra = Windows.Graphics.Imaging.SoftwareBitmap.Convert(sb, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore);
        var bytes = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyToBuffer(bytes.AsBuffer());
        var bmp = new Bitmap(bgra.PixelWidth, bgra.PixelHeight, PixelFormat.Format32bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        Marshal.Copy(bytes, 0, bd.Scan0, bytes.Length);
        bmp.UnlockBits(bd);
        // Mirror like a video call so movement feels natural.
        bmp.RotateFlip(RotateFlipType.RotateNoneFlipX);
        ApplyBackground(bmp);
        Interlocked.Increment(ref _frames);
        lock (_gate)
        {
            _latest?.Dispose();
            _latest = bmp;
        }
    }

    void ApplyBackground(Bitmap bmp)
    {
        var effect = Background;
        if (!CameraBackgrounds.NeedsSegmentation(effect) || _segFailed) return;
        var seg = PersonSegmenter.Instance;
        if (seg == null) { _segFailed = true; return; }
        // The model runs beside the camera thread; frames use the latest mask meanwhile.
        if (Interlocked.CompareExchange(ref _segBusy, 1, 0) == 0)
        {
            var small = BackgroundCompositor.Downscale(bmp);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _mask = seg.Run(small); }
                catch (Exception ex) { Log.Error("Segmentación de la cámara", ex); _segFailed = true; }
                finally { small.Dispose(); Volatile.Write(ref _segBusy, 0); }
            });
        }
        var mask = _mask;
        if (mask == null) return;
        if (effect is CameraBackgrounds.BlurLight or CameraBackgrounds.BlurStrong)
        {
            using var blurred = BackgroundCompositor.Blur(bmp, effect == CameraBackgrounds.BlurStrong);
            BackgroundCompositor.Compose(bmp, blurred, mask);
            return;
        }
        var key = $"{effect}|{bmp.Width}x{bmp.Height}";
        if (_bgKey != key)
        {
            _bgCache?.Dispose();
            _bgCache = effect.StartsWith("image:") ? CameraBackgrounds.LoadImage(effect[6..], bmp.Width, bmp.Height) : CameraBackgrounds.RenderPreset(effect, bmp.Width, bmp.Height);
            _bgKey = key;
        }
        if (_bgCache != null) BackgroundCompositor.Compose(bmp, _bgCache, mask);
    }

    public void Use(Action<Bitmap> draw)
    {
        lock (_gate) { if (_latest != null) draw(_latest); }
    }

    public void Dispose()
    {
        try { _reader?.StopAsync().AsTask().Wait(1000); } catch { }
        _reader?.Dispose();
        _capture?.Dispose();
        lock (_gate) { _latest?.Dispose(); _latest = null; }
        _bgCache?.Dispose(); _bgCache = null;
    }
}

/// <summary>Low-level mouse hook reporting left/right clicks in screen coordinates.</summary>
public sealed class MouseHook : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public int x, y, mouseData, flags, time; public IntPtr extra; }
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);

    readonly HookProc _proc;
    IntPtr _hook;
    public event Action<Point>? Click;
    public event Action<Point, bool>? ButtonDown; // point, isRight

    public MouseHook()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(14 /*WH_MOUSE_LL*/, _proc, GetModuleHandle(null), 0);
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is 0x0201 or 0x0204) // WM_LBUTTONDOWN / WM_RBUTTONDOWN
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var p = new Point(s.x, s.y);
                Click?.Invoke(p);
                ButtonDown?.Invoke(p, msg == 0x0204);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
