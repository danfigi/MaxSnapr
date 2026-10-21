using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WShapes = System.Windows.Shapes;
using MaxSnapr.Library;
using MaxSnapr.Native;
using MaxSnapr.Services;

namespace MaxSnapr.Video;

/// <summary>Video settings remembered between recordings.</summary>
public sealed class VideoSettings
{
    public int Fps { get; set; } = 30;
    public bool Cursor { get; set; } = true;
    public bool HighlightClicks { get; set; } = true;
    public bool Microphone { get; set; }
    public string? MicrophoneId { get; set; }
    public bool SystemAudio { get; set; } = true;
    public bool Webcam { get; set; }
    public string? WebcamId { get; set; }
    public WebcamShape WebcamShape { get; set; } = WebcamShape.Circle;
    public WebcamCorner WebcamCorner { get; set; } = WebcamCorner.BottomRight;
    /// <summary>Camera picture height as a fraction of the shorter side of the recording.</summary>
    public double WebcamSize { get; set; } = 0.22;
    /// <summary>Centre of the camera picture as fractions of the recorded area, when moved by hand.</summary>
    public double WebcamX { get; set; } = 0.85;
    public double WebcamY { get; set; } = 0.8;
    public string WebcamBackground { get; set; } = CameraBackgrounds.None;
    public string? LastBackgroundImage { get; set; }
    public bool Countdown { get; set; } = true;
    public bool SaveAsGif { get; set; }
    public int GifProfile { get; set; } = 1;
    public bool OpenInEditor { get; set; } = true;
}

internal static class CaptureExclusion
{
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    /// <summary>Hides our helper windows from screen capture (Windows 10 2004+); harmless where unsupported.</summary>
    public static void Apply(Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
    }

    public static void ClickThrough(Window w, bool on)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        int ex = NativeMethods.GetWindowLong(h, NativeMethods.GWL_EXSTYLE);
        ex = on ? ex | 0x20 | NativeMethods.WS_EX_TOOLWINDOW : (ex & ~0x20) | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(h, NativeMethods.GWL_EXSTYLE, ex);
    }

    public static void PlacePhysical(Window w, Int32Rect r)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }
}

/// <summary>One screen recording: countdown, frame, HUD, recorder, then the library entry.</summary>
public sealed class RecordingSession
{
    static RecordingSession? _active;
    public static bool IsRecording => _active != null;

    readonly Int32Rect _region;   // physical, screen coordinates
    readonly VideoSettings _s;
    VideoRecorder? _rec;
    RegionFrameWindow? _frame;
    HudWindow? _hud;
    DrawOnScreenWindow? _draw;
    HotkeyService? _keys;
    WebcamSource? _cam;
    WebcamPreviewWindow? _preview;
    RecordingSetupWindow? _setup;
    string? _pauseKey, _stopKey;
    string _path = "";
    readonly TaskCompletionSource _done = new();

    RecordingSession(Int32Rect region, VideoSettings s)
    {
        _region = new Int32Rect(region.X, region.Y, Math.Max(2, region.Width & ~1), Math.Max(2, region.Height & ~1));
        _s = s;
    }

    /// <param name="regionVirtual">Region relative to the virtual screen origin, as the capture overlay reports it.</param>
    public static Task StartAsync(Int32Rect regionVirtual, VideoSettings settings)
    {
        if (_active != null) return Task.CompletedTask;
        var vs = Capture.ScreenCapturer.VirtualScreen;
        var session = new RecordingSession(new Int32Rect(regionVirtual.X + vs.X, regionVirtual.Y + vs.Y, regionVirtual.Width, regionVirtual.Height), settings);
        _active = session;
        _ = session.RunAsync();
        return session._done.Task;
    }

    /// <summary>Session that only drives the setup bar and preview, for the self-test.</summary>
    internal static RecordingSession ForPreview(Int32Rect region, VideoSettings s) => new(region, s);

    internal RecordingSetupWindow CreateSetup(bool monitors) => _setup = new RecordingSetupWindow(this, _region, _s, monitors);

    internal void ShowTestPreview(System.Windows.Media.Imaging.BitmapSource still)
    {
        if (_preview == null)
        {
            _preview = new WebcamPreviewWindow(_region);
            _preview.Edited += OnPreviewEdited;
        }
        _preview.ShowStill(still);
        _preview.Place(_s, false);
    }

    internal void CloseTestPreview() => Cleanup();

    async Task RunAsync()
    {
        try
        {
            _frame = new RegionFrameWindow(_region);
            _frame.Show();
            var setup = CreateSetup(monitors: true);
            if (_s.Webcam)
            {
                _s.Webcam = false;
                _ = setup.Dispatcher.InvokeAsync(async () => setup.ShowError(await SetCameraAsync(true)));
            }
            bool go = await setup.RunAsync();
            _setup = null;
            if (!go)
            {
                Cleanup();
                _done.TrySetResult();
                return;
            }
            if (_s.Countdown) await CountdownAsync();
            await BeginRecorderAsync();
            Usage.Count("video");
            if (_s.Microphone) Usage.Count("video.mic");
            if (_s.SystemAudio) Usage.Count("video.system_audio");
            if (CameraOn) { Usage.Count("video.webcam"); if (_s.WebcamBackground != CameraBackgrounds.None) Usage.Count("video.background"); }
            _keys = new HotkeyService();
            // Other recorders often own Shift+F9/F10, so fall back to Ctrl+Shift.
            _pauseKey = RegisterFirst(TogglePause, new Hotkey { Shift = true, Key = (int)Key.F9 }, new Hotkey { Ctrl = true, Shift = true, Key = (int)Key.F9 });
            _stopKey = RegisterFirst(() => _ = StopAsync(false), new Hotkey { Shift = true, Key = (int)Key.F10 }, new Hotkey { Ctrl = true, Shift = true, Key = (int)Key.F10 });
            _keys.Register(new Hotkey { Ctrl = true, Shift = true, Key = (int)Key.F }, ToggleWebcamFullScreen);
            _hud = new HudWindow(this, _region);
            _hud.Show();
        }
        catch (Exception ex)
        {
            Log.Error("Iniciar grabación", ex);
            MessageBox.Show("No se pudo iniciar la grabación: " + ex.Message, "MaxSnapr", MessageBoxButton.OK, MessageBoxImage.Warning);
            Cleanup();
            _done.TrySetResult();
        }
    }

    string? RegisterFirst(Action action, params Hotkey[] options)
    {
        foreach (var h in options)
            if (_keys!.Register(h, action)) return h.ToString();
        return null;
    }

    public string PauseKeyText => _pauseKey ?? "sin atajo";
    public string StopKeyText => _stopKey ?? "sin atajo";

    // ------------------------------------------------------------------ camera

    public bool CameraOn => _s.Webcam && _cam != null;

    /// <summary>Opens or closes the camera (before or during recording). Returns an error message or null.</summary>
    public async Task<string?> SetCameraAsync(bool on)
    {
        if (!on)
        {
            _s.Webcam = false;
            if (_rec != null) { _rec.WebcamVisible = false; _rec.AttachWebcam(null); }
            _preview?.Close(); _preview = null;
            _cam?.Dispose(); _cam = null;
            App.Settings.Save();
            _hud?.Refresh();
            return null;
        }
        if (_cam == null)
        {
            var cams = await Devices.CamerasAsync();
            if (cams.Count == 0) return "No se encontró ninguna cámara.";
            var id = cams.Any(c => c.Id == _s.WebcamId) ? _s.WebcamId : cams[0].Id;
            try { _cam = await WebcamSource.StartAsync(id); }
            catch (UnauthorizedAccessException)
            {
                return "Windows no deja usar la cámara: active «Permitir que las aplicaciones de escritorio accedan a la cámara» en Configuración > Privacidad y seguridad > Cámara.";
            }
            catch (Exception ex)
            {
                Log.Error("Abrir la cámara", ex);
                return "No se pudo abrir la cámara (¿la está usando otra aplicación?): " + ex.Message;
            }
        }
        _s.Webcam = true;
        App.Settings.Save();
        _cam.Background = _s.WebcamBackground;
        if (_preview == null)
        {
            _preview = new WebcamPreviewWindow(_region) { MenuFactory = () => CameraMenus.Build(this, _s, () => _setup?.Refresh()) };
            _preview.Edited += OnPreviewEdited;
        }
        _preview.Source = _cam;
        RelayoutPreview();
        if (_rec != null) { _rec.AttachWebcam(_cam); _rec.WebcamVisible = true; }
        _hud?.Refresh();
        // A camera can open yet send nothing (privacy shutter, closed lid): say so instead of a blank picture.
        var cam = _cam;
        for (int i = 0; i < 30 && cam.FramesReceived == 0; i++) await Task.Delay(100);
        if (cam == _cam && cam.FramesReceived == 0)
            return "La cámara no envía imagen: compruebe su tapa de privacidad o elija otra cámara en ▾.";
        return null;
    }

    public async Task<string?> RestartCameraAsync()
    {
        _cam?.Dispose(); _cam = null;
        if (_preview != null) _preview.Source = null;
        return await SetCameraAsync(true);
    }

    public void RelayoutPreview()
    {
        _rec?.UpdateWebcamLayout(_s);
        _preview?.Place(_s, _rec?.WebcamFullScreen == true);
    }

    public void SetBackground(string id)
    {
        _s.WebcamBackground = id;
        if (id.StartsWith("image:")) _s.LastBackgroundImage = id[6..];
        if (_cam != null) _cam.Background = id;
        App.Settings.Save();
    }

    /// <summary>The camera picture was dragged or resized on screen: keep it there in the video too.</summary>
    void OnPreviewEdited(Int32Rect r, bool final)
    {
        double side = Math.Min(_region.Width, _region.Height);
        _s.WebcamCorner = WebcamCorner.Custom;
        _s.WebcamX = (r.X + r.Width / 2.0 - _region.X) / _region.Width;
        _s.WebcamY = (r.Y + r.Height / 2.0 - _region.Y) / _region.Height;
        _s.WebcamSize = r.Height / side;
        _rec?.UpdateWebcamLayout(_s);
        if (final) App.Settings.Save();
    }

    public bool MicMuted => _rec?.MicMuted == true;
    public bool HasMicrophone => _rec?.HasMicrophone == true;

    public void ToggleMic()
    {
        if (_rec == null || !_rec.HasMicrophone) return;
        _rec.MicMuted = !_rec.MicMuted;
        _hud?.Refresh();
    }

    public float TakeMicLevel() => _rec?.TakeMicLevel() ?? 0;

    /// <summary>Puts a floating bar just below the recorded area, or above / inside it when there is no room.</summary>
    internal static void PlaceNear(Window w, Int32Rect region)
    {
        var scale = VisualTreeHelper.GetDpi(w).DpiScaleX;
        int ww = (int)(w.ActualWidth * scale), hh = (int)(w.ActualHeight * scale);
        var vs = Capture.ScreenCapturer.VirtualScreen;
        int x = Math.Clamp(region.X + region.Width - ww, vs.X, Math.Max(vs.X, vs.X + vs.Width - ww));
        int y = region.Y + region.Height + 10;
        if (y + hh > vs.Y + vs.Height) y = region.Y - hh - 10;
        if (y < vs.Y) y = region.Y + 10;
        CaptureExclusion.PlacePhysical(w, new Int32Rect(x, y, ww, hh));
    }

    async Task BeginRecorderAsync()
    {
        var dir = Path.Combine(LibraryStore.Instance.Root, DateTime.Now.ToString("yyyy-MM"));
        _path = Path.Combine(dir, $"{Naming.Default("Vídeo", DateTime.Now)}.mp4");
        _rec = new VideoRecorder(new RecorderOptions
        {
            Region = _region, Fps = _s.Fps, CaptureCursor = _s.Cursor, HighlightClicks = _s.HighlightClicks,
            RecordMicrophone = _s.Microphone, MicrophoneId = _s.MicrophoneId, RecordSystemAudio = _s.SystemAudio,
            Webcam = CameraOn, WebcamId = _s.WebcamId, WebcamShape = _s.WebcamShape, WebcamCorner = _s.WebcamCorner,
            WebcamScale = _s.WebcamSize, WebcamX = _s.WebcamX, WebcamY = _s.WebcamY, SharedWebcam = _cam, OutputPath = _path,
        });
        _rec.Failed += (_, ex) => Application.Current.Dispatcher.BeginInvoke(() => { MessageBox.Show("La grabación falló: " + ex.Message, "MaxSnapr"); _ = StopAsync(true); });
        await _rec.StartAsync();
    }

    async Task CountdownAsync()
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
            Width = 150, Height = 150, ShowActivated = false,
        };
        var text = new TextBlock { FontSize = 72, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        w.Content = new Border { CornerRadius = new CornerRadius(75), Background = new SolidColorBrush(Color.FromArgb(0xD0, 0xE5, 0x48, 0x4D)), Child = text };
        CaptureExclusion.Apply(w);
        w.Show();
        var size = (int)(150 * VisualTreeHelper.GetDpi(w).DpiScaleX);
        CaptureExclusion.PlacePhysical(w, new Int32Rect(_region.X + (_region.Width - size) / 2, _region.Y + (_region.Height - size) / 2, size, size));
        for (int i = 3; i > 0; i--) { text.Text = i.ToString(); await Task.Delay(800); }
        w.Close();
    }

    public TimeSpan Elapsed => _rec?.Elapsed ?? TimeSpan.Zero;
    public bool Paused => _rec?.IsPaused == true;

    public void TogglePause()
    {
        if (_rec == null) return;
        if (_rec.IsPaused) _rec.Resume(); else _rec.Pause();
        _frame?.SetPaused(_rec.IsPaused);
        _hud?.Refresh();
    }

    public void ToggleDraw()
    {
        if (_draw == null)
        {
            _draw = new DrawOnScreenWindow(_region);
            _draw.Show();
        }
        else { _draw.Close(); _draw = null; }
        _hud?.Refresh();
    }

    public bool Drawing => _draw != null;

    /// <summary>Saves a still image of the recorded area to the library without interrupting the video.</summary>
    public void CaptureStill()
    {
        try
        {
            var img = Capture.ScreenCapturer.CaptureRect(_region);
            Library.LibraryStore.Instance.Add(new Editor.Model.EditorDocument(img), "Imagen durante la grabación", "Grabación");
            App.Current.Notify("Imagen capturada", "Guardada en la biblioteca");
        }
        catch (Exception ex) { Log.Error("Imagen durante la grabación", ex); }
    }

    public void ToggleWebcamFullScreen()
    {
        if (_rec == null || !CameraOn) return;
        _rec.ToggleWebcamFullScreen();
        RelayoutPreview();
    }

    public async Task RestartAsync()
    {
        if (_rec == null) return;
        await _rec.StopAsync(cancel: true);
        await BeginRecorderAsync();
        _hud?.Refresh();
    }

    public async Task StopAsync(bool cancel)
    {
        if (_rec == null) return;
        var rec = _rec;
        _rec = null;
        _hud?.ShowBusy();
        bool ok = await rec.StopAsync(cancel);
        Cleanup();
        try
        {
            if (ok && !cancel) await FinishAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Guardar grabación", ex);
            MessageBox.Show("No se pudo guardar la grabación: " + ex.Message, "MaxSnapr");
        }
        _done.TrySetResult();
    }

    async Task FinishAsync()
    {
        LibraryItem item;
        if (_s.SaveAsGif)
        {
            Usage.Count("video.gif");
            var profile = GifProfile.All[Math.Clamp(_s.GifProfile, 0, GifProfile.All.Length - 1)];
            var gif = Path.ChangeExtension(_path, ".gif");
            await VideoTools.ToGifAsync(_path, TimeSpan.Zero, TimeSpan.Zero, profile.Fps, profile.MaxWidth, gif);
            File.Delete(_path);
            item = await LibraryStore.Instance.AddMediaAsync(gif, "Grabación");
        }
        else item = await LibraryStore.Instance.AddMediaAsync(_path, "Grabación");
        if (_s.OpenInEditor) App.Current.ShowEditor().OpenItem(item);
        else App.Current.Notify("Grabación guardada", item.Name);
    }

    void Cleanup()
    {
        _keys?.Dispose(); _keys = null;
        _hud?.Close(); _hud = null;
        _draw?.Close(); _draw = null;
        _frame?.Close(); _frame = null;
        _setup?.Close(); _setup = null;
        _preview?.Close(); _preview = null;
        _cam?.Dispose(); _cam = null;
        if (_active == this) _active = null;
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>Dashed outline just outside the recorded area; click-through and excluded from capture.</summary>
    sealed class RegionFrameWindow : Window
    {
        readonly Border _border;
        const int FrameMargin = 4;
        readonly Int32Rect _r;

        public RegionFrameWindow(Int32Rect r)
        {
            _r = r;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
            _border = new Border { BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)) };
            Content = _border;
            SourceInitialized += (_, _) =>
            {
                CaptureExclusion.Apply(this);
                CaptureExclusion.ClickThrough(this, true);
                CaptureExclusion.PlacePhysical(this, new Int32Rect(r.X - FrameMargin, r.Y - FrameMargin, r.Width + FrameMargin * 2, r.Height + FrameMargin * 2));
            };
        }

        public void SetPaused(bool paused) => _border.BorderBrush = new SolidColorBrush(paused ? Color.FromRgb(0xF2, 0xB0, 0x1E) : Color.FromRgb(0xE5, 0x48, 0x4D));
    }

    /// <summary>Floating recording controls.</summary>
    sealed class HudWindow : Window
    {
        readonly RecordingSession _s;
        readonly TextBlock _time = new() { Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Width = 64, TextAlignment = TextAlignment.Center };
        readonly WShapes.Ellipse _dot = new() { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), Margin = new Thickness(10, 0, 4, 0) };
        readonly Button _pause, _draw, _camBtn, _micBtn, _camFull;
        readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };
        readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

        public HudWindow(RecordingSession s, Int32Rect region)
        {
            _s = s;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            _panel.Children.Add(_dot);
            _panel.Children.Add(_time);
            _pause = Btn("\uE769", $"Pausar / reanudar ({s.PauseKeyText})", s.TogglePause);
            _panel.Children.Add(_pause);
            _panel.Children.Add(Btn("\uE71A", $"Detener y guardar ({s.StopKeyText})", () => _ = s.StopAsync(false)));
            _panel.Children.Add(Btn("\uE72C", "Reiniciar", () => _ = s.RestartAsync()));
            _panel.Children.Add(Btn("\uE722", "Capturar una imagen de la zona", s.CaptureStill));
            _micBtn = Btn("\uE720", "Silenciar o activar el micrófono", s.ToggleMic);
            if (s.HasMicrophone) _panel.Children.Add(_micBtn);
            _camBtn = Btn("\uE714", "Mostrar u ocultar la cámara", () => _ = ToggleCam());
            _panel.Children.Add(_camBtn);
            _camFull = Btn("\uE740", "Cámara a pantalla completa (Ctrl+Mayús+F)", s.ToggleWebcamFullScreen);
            _panel.Children.Add(_camFull);
            _draw = Btn("\uE70F", "Dibujar en la pantalla", s.ToggleDraw);
            _panel.Children.Add(_draw);
            _panel.Children.Add(Btn("\uE711", "Cancelar sin guardar", () =>
            {
                if (MessageBox.Show("¿Descartar esta grabación?", "MaxSnapr", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) _ = s.StopAsync(true);
            }));
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x20, 0x24)), CornerRadius = new CornerRadius(10), Padding = new Thickness(4),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x44, 0x4A)), BorderThickness = new Thickness(1), Child = _panel,
            };
            MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBlock { Parent: Button }) DragMove(); };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
            Loaded += (_, _) =>
            {
                CaptureExclusion.Apply(this);
                PlaceNear(this, region);
            };
            Refresh();
        }

        async Task ToggleCam()
        {
            var err = await _s.SetCameraAsync(!_s.CameraOn);
            if (err != null) App.Current.Notify("Cámara", err);
        }

        Button Btn(string glyph, string tip, Action a)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Foreground = Brushes.White },
                ToolTip = tip, Width = 38, Height = 34, Margin = new Thickness(1), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Style = (Style)Application.Current.FindResource("FlatButton"), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            b.Click += (_, _) => a();
            return b;
        }

        public void Refresh()
        {
            var t = _s.Elapsed;
            _time.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
            ((TextBlock)_pause.Content).Text = _s.Paused ? "\uE768" : "\uE769";
            _dot.Opacity = _s.Paused ? 0.3 : (DateTime.Now.Millisecond < 500 ? 1 : 0.5);
            var on = new SolidColorBrush(Color.FromRgb(0x1F, 0x3E, 0x5C));
            _draw.Background = _s.Drawing ? on : Brushes.Transparent;
            _camBtn.Background = _s.CameraOn ? on : Brushes.Transparent;
            _camFull.Visibility = _s.CameraOn ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)_micBtn.Content).Text = _s.MicMuted ? "\uEC54" : "\uE720";
            _micBtn.Background = _s.MicMuted ? new SolidColorBrush(Color.FromRgb(0x5C, 0x24, 0x24)) : Brushes.Transparent;
            _micBtn.ToolTip = _s.MicMuted ? "Micrófono silenciado: clic para activarlo" : "Silenciar el micrófono";
        }

        public void ShowBusy()
        {
            _timer.Stop();
            _panel.Children.Clear();
            _panel.Children.Add(new TextBlock { Text = "Guardando…", Foreground = Brushes.White, Margin = new Thickness(14, 8, 14, 8) });
        }

        protected override void OnClosed(EventArgs e) { _timer.Stop(); base.OnClosed(e); }
    }
}

/// <summary>
/// Transparent drawing layer over the recorded area: arrows, boxes, numbered steps and ink that fade
/// out after a few seconds. It is captured on purpose so viewers see the marks.
/// </summary>
public sealed class DrawOnScreenWindow : Window
{
    enum Tool { Arrow, Rect, Step, Pen }
    readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
    Tool _tool = Tool.Arrow;
    Point _start;
    WShapes.Shape? _current;
    WShapes.Polyline? _ink;
    int _step = 1;
    Color _color = Color.FromRgb(0xE8, 0x3A, 0x3A);
    readonly Window _bar;

    public DrawOnScreenWindow(Int32Rect region)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
        Content = _canvas;
        Cursor = Cursors.Pen;
        SourceInitialized += (_, _) => CaptureExclusion.PlacePhysical(this, region);
        _canvas.MouseLeftButtonDown += Down;
        _canvas.MouseMove += Move;
        _canvas.MouseLeftButtonUp += Up;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // Tool palette, itself excluded from the recording.
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        void B(string glyph, string tip, Action a)
        {
            var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, Foreground = Brushes.White }, ToolTip = tip, Width = 34, Height = 32, Style = (Style)Application.Current.FindResource("FlatButton"), Padding = new Thickness(0) };
            b.Click += (_, _) => a();
            panel.Children.Add(b);
        }
        B("\uE72A", "Flecha", () => _tool = Tool.Arrow);
        B("\uE739", "Rectángulo", () => _tool = Tool.Rect);
        B("\uE8FD", "Paso numerado", () => _tool = Tool.Step);
        B("\uE76D", "Pluma", () => _tool = Tool.Pen);
        foreach (var c in new[] { "#E83A3A", "#FFD23F", "#2BB673", "#2E7CF6" })
        {
            var col = Views.Palette.C(c);
            var b = new Button { Width = 22, Height = 22, Margin = new Thickness(3, 5, 3, 5), Background = new SolidColorBrush(col), BorderThickness = new Thickness(0), Style = (Style)Application.Current.FindResource("FlatButton") };
            b.Click += (_, _) => _color = col;
            panel.Children.Add(b);
        }
        B("\uE74D", "Borrar todo", () => { _canvas.Children.Clear(); _step = 1; });
        _bar = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight,
            Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x20, 0x24)), CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Child = panel },
        };
        _bar.SourceInitialized += (_, _) => CaptureExclusion.Apply(_bar);
        Loaded += (_, _) => { _bar.Left = Left + 12; _bar.Top = Top + 12; _bar.Show(); };
        Closed += (_, _) => _bar.Close();
    }

    Brush Stroke => new SolidColorBrush(_color);

    void Down(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        _canvas.CaptureMouse();
        switch (_tool)
        {
            case Tool.Step:
                var g = new Grid { Width = 40, Height = 40 };
                g.Children.Add(new WShapes.Ellipse { Fill = Stroke, Stroke = Brushes.White, StrokeThickness = 2 });
                g.Children.Add(new TextBlock { Text = (_step++).ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                Canvas.SetLeft(g, _start.X - 20); Canvas.SetTop(g, _start.Y - 20);
                _canvas.Children.Add(g);
                FadeLater(g);
                break;
            case Tool.Pen:
                _ink = new WShapes.Polyline { Stroke = Stroke, StrokeThickness = 5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _ink.Points.Add(_start);
                _canvas.Children.Add(_ink);
                break;
            case Tool.Rect:
                _current = new WShapes.Rectangle { Stroke = Stroke, StrokeThickness = 5, RadiusX = 6, RadiusY = 6 };
                _canvas.Children.Add(_current);
                break;
            default:
                _current = new WShapes.Path { Stroke = Stroke, Fill = Stroke, StrokeThickness = 6, StrokeLineJoin = PenLineJoin.Round };
                _canvas.Children.Add(_current);
                break;
        }
    }

    void Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_canvas);
        if (_ink != null) { _ink.Points.Add(p); return; }
        if (_current is WShapes.Rectangle r)
        {
            var rect = new Rect(_start, p);
            Canvas.SetLeft(r, rect.X); Canvas.SetTop(r, rect.Y); r.Width = rect.Width; r.Height = rect.Height;
        }
        else if (_current is WShapes.Path path) path.Data = ArrowGeometry(_start, p);
    }

    void Up(object sender, MouseButtonEventArgs e)
    {
        _canvas.ReleaseMouseCapture();
        if (_ink != null) FadeLater(_ink);
        if (_current != null) FadeLater(_current);
        _ink = null;
        _current = null;
    }

    static Geometry ArrowGeometry(Point a, Point b)
    {
        var v = b - a;
        if (v.Length < 1) return Geometry.Empty;
        v.Normalize();
        var n = new Vector(-v.Y, v.X);
        var head = b - v * 22;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(a, false, false);
            c.LineTo(head, true, true);
            c.BeginFigure(b, true, true);
            c.LineTo(head + n * 12, true, true);
            c.LineTo(head - n * 12, true, true);
        }
        return g;
    }

    void FadeLater(UIElement el)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.FromSeconds(3) };
        anim.Completed += (_, _) => _canvas.Children.Remove(el);
        el.BeginAnimation(OpacityProperty, anim);
    }
}
