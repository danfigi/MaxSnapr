using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaxSnapr.Services;

namespace MaxSnapr.Video;

/// <summary>
/// Live camera picture drawn exactly where the recorder composites it. It is excluded from capture, so it
/// never appears twice in the video. Drag it to move it, use the handle or the wheel to resize it and
/// right-click for shape, background and more; the recording follows every change.
/// </summary>
public sealed class WebcamPreviewWindow : Window
{
    readonly Grid _root = new();
    readonly Border _pic = new() { Cursor = Cursors.SizeAll };
    readonly Border _ring = new() { BorderBrush = Brushes.White, BorderThickness = new Thickness(3), IsHitTestVisible = false };
    readonly Border _grip = new()
    {
        Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Brushes.White, Cursor = Cursors.SizeNWSE,
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7C, 0xF6)), BorderThickness = new Thickness(3),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Hidden,
        ToolTip = "Arrastre para cambiar el tamaño",
    };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    readonly Int32Rect _region;
    WebcamShape _shape;
    float _aspect = 16f / 9f;
    bool _fullScreen;
    Int32Rect _rect;
    enum DragMode { None, Move, Resize }
    DragMode _drag;
    Native.NativeMethods.POINT _dragStart;
    Int32Rect _dragRect;

    public WebcamSource? Source { get; set; }
    /// <summary>Raised while dragging (final = false) and when the gesture ends (final = true), in physical screen pixels.</summary>
    public event Action<Int32Rect, bool>? Edited;
    public Func<ContextMenu>? MenuFactory { get; set; }

    public WebcamPreviewWindow(Int32Rect region)
    {
        _region = region;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        _pic.ToolTip = "Arrastre para mover · rueda o tirador para el tamaño · clic derecho: forma, fondo y más";
        _root.Children.Add(_pic);
        _root.Children.Add(_ring);
        _root.Children.Add(_grip);
        Content = _root;
        SourceInitialized += (_, _) =>
        {
            CaptureExclusion.Apply(this);
            // Never steal focus from the setup bar or the recorded app.
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int ex = Native.NativeMethods.GetWindowLong(h, Native.NativeMethods.GWL_EXSTYLE);
            Native.NativeMethods.SetWindowLong(h, Native.NativeMethods.GWL_EXSTYLE, ex | 0x08000000 /*WS_EX_NOACTIVATE*/ | Native.NativeMethods.WS_EX_TOOLWINDOW);
        };
        SizeChanged += (_, _) => ApplyShape();
        MouseEnter += (_, _) => _grip.Visibility = _fullScreen ? Visibility.Hidden : Visibility.Visible;
        MouseLeave += (_, _) => { if (_drag == DragMode.None) _grip.Visibility = Visibility.Hidden; };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_fullScreen) return;
            _drag = ReferenceEquals(e.OriginalSource, _grip) ? DragMode.Resize : DragMode.Move;
            Native.NativeMethods.GetCursorPos(out _dragStart);
            _dragRect = _rect;
            CaptureMouse();
            e.Handled = true;
        };
        PreviewMouseMove += (_, _) =>
        {
            if (_drag == DragMode.None) return;
            Native.NativeMethods.GetCursorPos(out var p);
            int dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
            var r = _drag == DragMode.Move
                ? new Int32Rect(_dragRect.X + dx, _dragRect.Y + dy, _dragRect.Width, _dragRect.Height)
                : Scaled(_dragRect, Math.Max((_dragRect.Width + dx) / (double)_dragRect.Width, (_dragRect.Height + dy) / (double)_dragRect.Height), anchorTopLeft: true);
            Apply(r, false);
        };
        PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (_drag == DragMode.None) return;
            _drag = DragMode.None;
            ReleaseMouseCapture();
            Edited?.Invoke(_rect, true);
        };
        MouseWheel += (_, e) =>
        {
            if (_fullScreen) return;
            Apply(Scaled(_rect, e.Delta > 0 ? 1.08 : 1 / 1.08, anchorTopLeft: false), true);
        };
        MouseRightButtonUp += (_, e) =>
        {
            var m = MenuFactory?.Invoke();
            if (m == null) return;
            m.PlacementTarget = this;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            m.IsOpen = true;
            e.Handled = true;
        };
        _timer.Tick += (_, _) => Source?.Use(bmp =>
        {
            _aspect = bmp.Width / (float)bmp.Height;
            SetPicture(ToBitmapSource(bmp));
        });
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Keeps the aspect ratio, a sensible minimum and the picture inside the recorded area.</summary>
    Int32Rect Scaled(Int32Rect r, double k, bool anchorTopLeft)
    {
        double maxH = Math.Min(_region.Width, _region.Height) * 0.95;
        double h = Math.Clamp(r.Height * k, 48, maxH);
        double w = r.Width * h / r.Height;
        if (w > _region.Width) { h *= _region.Width / w; w = _region.Width; }
        double x = anchorTopLeft ? r.X : r.X + r.Width / 2.0 - w / 2;
        double y = anchorTopLeft ? r.Y : r.Y + r.Height / 2.0 - h / 2;
        return new Int32Rect((int)x, (int)y, (int)w, (int)h);
    }

    void Apply(Int32Rect r, bool final)
    {
        r.X = Math.Clamp(r.X, _region.X, _region.X + _region.Width - r.Width);
        r.Y = Math.Clamp(r.Y, _region.Y, _region.Y + _region.Height - r.Height);
        _rect = r;
        CaptureExclusion.PlacePhysical(this, r);
        ApplyShape();
        Edited?.Invoke(r, final);
    }

    /// <summary>Synthetic picture for the self-test (no real camera is opened).</summary>
    public void ShowStill(BitmapSource still)
    {
        _aspect = still.PixelWidth / (float)still.PixelHeight;
        SetPicture(still);
    }

    /// <summary>Current position and size in physical screen pixels.</summary>
    public Int32Rect PhysicalRect => _rect;

    // An ImageBrush crops around the centre, like the recorder does, whatever the camera's aspect ratio.
    void SetPicture(ImageSource src) => _pic.Background = new ImageBrush(src) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };

    public void Place(VideoSettings s, bool fullScreen)
    {
        _fullScreen = fullScreen;
        _shape = fullScreen ? WebcamShape.Rectangle : s.WebcamShape;
        Int32Rect r;
        if (fullScreen) r = _region;
        else
        {
            var rf = VideoRecorder.WebcamLayout(_region.Width, _region.Height, s.WebcamSize, s.WebcamShape, s.WebcamCorner, _aspect, s.WebcamX, s.WebcamY);
            r = new Int32Rect(_region.X + (int)rf.X, _region.Y + (int)rf.Y, Math.Max(8, (int)rf.Width), Math.Max(8, (int)rf.Height));
        }
        if (!IsVisible) Show();
        _rect = r;
        CaptureExclusion.PlacePhysical(this, r);
        ApplyShape();
    }

    /// <summary>Moves the picture as a drag would (used by the self-test).</summary>
    internal void SimulateEdit(Int32Rect r) => Apply(r, true);

    void ApplyShape()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double radius = _shape switch { WebcamShape.Circle => Math.Min(w, h) / 2, WebcamShape.Rounded => h * 0.12, _ => 0 };
        _pic.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
        _ring.CornerRadius = new CornerRadius(radius);
        // On a circle the handle sits on the rim, at 45 degrees.
        double inset = _shape == WebcamShape.Circle ? radius - radius / Math.Sqrt(2) - 9 : -2;
        _grip.Margin = new Thickness(0, 0, Math.Max(0, inset), Math.Max(0, inset));
    }

    static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try { return BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgr32, null, bd.Scan0, bd.Stride * bmp.Height, bd.Stride); }
        finally { bmp.UnlockBits(bd); }
    }
}

/// <summary>Camera menu shared by the setup bar and the preview's right-click: device, background, shape, size and position.</summary>
public static class CameraMenus
{
    public static ContextMenu Build(RecordingSession session, VideoSettings s, Action changed)
    {
        var m = new ContextMenu();
        void Relayout() { App.Settings.Save(); session.RelayoutPreview(); changed(); }

        var devices = new MenuItem { Header = "Cámara" };
        devices.Items.Add(new MenuItem { Header = "Buscando cámaras…", IsEnabled = false });
        _ = FillCamerasAsync(devices, session, s, changed);
        m.Items.Add(devices);
        var bg = new MenuItem { Header = "Fondo" };
        AddBackgroundItems(bg.Items, session, s, changed);
        m.Items.Add(bg);
        m.Items.Add(new Separator());
        var shape = new MenuItem { Header = "Forma" };
        foreach (var (v, name) in new[] { (WebcamShape.Circle, "Círculo"), (WebcamShape.Rounded, "Redondeada"), (WebcamShape.Rectangle, "Rectángulo") })
            shape.Items.Add(Check(name, s.WebcamShape == v, () => { s.WebcamShape = v; Relayout(); }));
        m.Items.Add(shape);
        var size = new MenuItem { Header = "Tamaño" };
        foreach (var (v, name) in new[] { (0.16, "Pequeña"), (0.22, "Mediana"), (0.30, "Grande"), (0.45, "Muy grande") })
            size.Items.Add(Check(name, Math.Abs(s.WebcamSize - v) < 0.01, () => { s.WebcamSize = v; Relayout(); }));
        m.Items.Add(size);
        var corner = new MenuItem { Header = "Posición" };
        foreach (var (v, name) in new[] { (WebcamCorner.BottomRight, "Abajo a la derecha"), (WebcamCorner.BottomLeft, "Abajo a la izquierda"), (WebcamCorner.TopRight, "Arriba a la derecha"), (WebcamCorner.TopLeft, "Arriba a la izquierda") })
            corner.Items.Add(Check(name, s.WebcamCorner == v, () => { s.WebcamCorner = v; Relayout(); }));
        corner.Items.Add(new MenuItem { Header = "Libre: arrastre la imagen", IsChecked = s.WebcamCorner == WebcamCorner.Custom, IsEnabled = false });
        m.Items.Add(corner);
        m.Items.Add(new Separator());
        var hide = new MenuItem { Header = "Desactivar la cámara" };
        hide.Click += async (_, _) => { await session.SetCameraAsync(false); changed(); };
        m.Items.Add(hide);
        return m;
    }

    /// <summary>None, two blurs, the built-in backgrounds and the user's own image.</summary>
    public static void AddBackgroundItems(ItemCollection items, RecordingSession session, VideoSettings s, Action changed)
    {
        void Set(string id) { session.SetBackground(id); changed(); }
        items.Add(Check("Sin fondo", s.WebcamBackground == CameraBackgrounds.None, () => Set(CameraBackgrounds.None)));
        items.Add(Check("Desenfoque suave", s.WebcamBackground == CameraBackgrounds.BlurLight, () => Set(CameraBackgrounds.BlurLight)));
        items.Add(Check("Desenfoque intenso", s.WebcamBackground == CameraBackgrounds.BlurStrong, () => Set(CameraBackgrounds.BlurStrong)));
        items.Add(new Separator());
        foreach (var (id, name) in CameraBackgrounds.Presets)
        {
            var item = Check(name, s.WebcamBackground == id, () => Set(id));
            item.Icon = new Image { Source = Swatch(id), Width = 32, Height = 18 };
            items.Add(item);
        }
        items.Add(new Separator());
        if (s.LastBackgroundImage != null && System.IO.File.Exists(s.LastBackgroundImage))
        {
            var id = "image:" + s.LastBackgroundImage;
            items.Add(Check("Imagen: " + System.IO.Path.GetFileName(s.LastBackgroundImage), s.WebcamBackground == id, () => Set(id)));
        }
        var pick = new MenuItem { Header = "Elegir una imagen…" };
        pick.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Fondo de la cámara", Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Todos los archivos|*.*" };
            if (dlg.ShowDialog() == true) Set("image:" + dlg.FileName);
        };
        items.Add(pick);
    }

    static readonly Dictionary<string, BitmapSource> Swatches = new();

    static BitmapSource Swatch(string id)
    {
        if (Swatches.TryGetValue(id, out var cached)) return cached;
        using var bmp = CameraBackgrounds.RenderPreset(id, 64, 36);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, 64, 36), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var src = BitmapSource.Create(64, 36, 96, 96, PixelFormats.Bgr32, null, bd.Scan0, bd.Stride * 36, bd.Stride);
        bmp.UnlockBits(bd);
        src.Freeze();
        return Swatches[id] = src;
    }

    static async Task FillCamerasAsync(MenuItem parent, RecordingSession session, VideoSettings s, Action changed)
    {
        var cams = await Devices.CamerasAsync();
        parent.Items.Clear();
        if (cams.Count == 0) { parent.Items.Add(new MenuItem { Header = "No se encontró ninguna cámara", IsEnabled = false }); return; }
        foreach (var c in cams)
        {
            bool current = s.WebcamId == c.Id || (s.WebcamId == null && c == cams[0]);
            parent.Items.Add(Check(c.Name, current, async () =>
            {
                s.WebcamId = c.Id;
                App.Settings.Save();
                var err = await session.RestartCameraAsync();
                if (err != null) App.Current.Notify("Cámara", err);
                changed();
            }));
        }
    }

    public static MenuItem Check(string header, bool on, Action a)
    {
        var i = new MenuItem { Header = header, IsChecked = on };
        i.Click += (_, _) => a();
        return i;
    }
}

/// <summary>
/// Bar shown after choosing the area and before recording: camera, microphone and system audio
/// switches with their devices and live levels, more options, Record and Cancel.
/// </summary>
public sealed class RecordingSetupWindow : Window
{
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x80));
    static readonly Brush OffFg = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6));

    readonly RecordingSession _session;
    readonly VideoSettings _s;
    readonly bool _monitors;
    readonly TaskCompletionSource<bool> _result = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly TextBlock _status = new() { Foreground = OffFg, FontSize = 12, Margin = new Thickness(8, 2, 8, 4), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 900 };
    readonly Pill _cam, _mic, _sys;
    readonly Button _bg;
    readonly TextBlock _bgLabel = new() { FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    LevelMonitor? _micMon, _sysMon;
    string? _error;
    bool _busy;

    /// <param name="monitors">Open the microphone and loopback level meters (off in the self-test).</param>
    public RecordingSetupWindow(RecordingSession session, Int32Rect region, VideoSettings s, bool monitors = true)
    {
        _session = session;
        _s = s;
        _monitors = monitors;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        var rec = new Button
        {
            Style = (Style)Application.Current.FindResource("FlatButton"), Height = 40, Padding = new Thickness(14, 0, 16, 0), Margin = new Thickness(2),
            Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), ToolTip = "Empezar a grabar (Intro)",
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = Brushes.White, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Grabar", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        rec.Click += (_, _) => Finish(true);
        row.Children.Add(rec);
        row.Children.Add(Separator());

        _cam = new Pill("\uE714", "\uE714", "Cámara", false, () => _ = ToggleCameraAsync(), () => CameraMenus.Build(_session, _s, Changed));
        _bg = new Button
        {
            Style = (Style)Application.Current.FindResource("FlatButton"), Height = 40, Padding = new Thickness(10, 0, 8, 0), Margin = new Thickness(2), Background = Brushes.Transparent,
            ToolTip = "Fondo de la cámara: desenfoque, fondos incluidos o una imagen suya",
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "\uE8B9", FontFamily = Icons, FontSize = 16, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                    _bgLabel,
                    new TextBlock { Text = "\uE70D", FontFamily = Icons, FontSize = 10, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        _bg.Click += (_, _) =>
        {
            var m = new ContextMenu { PlacementTarget = _bg, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            CameraMenus.AddBackgroundItems(m.Items, _session, _s, Changed);
            m.IsOpen = true;
        };
        _mic = new Pill("\uE720", "\uEC54", "Micrófono", true, () => { _s.Microphone = !_s.Microphone; Changed(); }, MicMenu);
        _sys = new Pill("\uE767", "\uE74F", "Audio del sistema", true, () => { _s.SystemAudio = !_s.SystemAudio; Changed(); }, null);
        row.Children.Add(_cam);
        row.Children.Add(_bg);
        row.Children.Add(_mic);
        row.Children.Add(_sys);
        row.Children.Add(Separator());
        row.Children.Add(IconButton("\uE713", "Más opciones de vídeo", b => OptionsMenu().IsOpen = true));
        row.Children.Add(IconButton("\uE711", "Cancelar (Esc)", _ => Finish(false)));

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x1E, 0x20, 0x24)), CornerRadius = new CornerRadius(12), Padding = new Thickness(4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x44, 0x4A)), BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { row, _status } },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border or StackPanel) DragMove(); };
        Loaded += (_, _) =>
        {
            CaptureExclusion.Apply(this);
            RecordingSession.PlaceNear(this, region);
            Activate();
        };
        _timer.Tick += (_, _) => Tick();
        Closed += (_, _) => { _timer.Stop(); StopMonitors(); _result.TrySetResult(false); };
        Changed();
        _timer.Start();
    }

    /// <summary>Shows the bar; true to record, false when cancelled.</summary>
    public Task<bool> RunAsync()
    {
        Show();
        return _result.Task;
    }

    void Finish(bool record)
    {
        if (_busy && record) return; // camera still starting
        StopMonitors();
        _result.TrySetResult(record);
        Close();
    }

    async Task ToggleCameraAsync()
    {
        if (_busy) return;
        _busy = true;
        _error = null;
        Changed();
        bool want = !_s.Webcam;
        _error = await _session.SetCameraAsync(want);
        _busy = false;
        Changed();
    }

    /// <summary>Error shown under the switches, e.g. when Windows blocks the camera.</summary>
    public void ShowError(string? message) { _error = message; Changed(); }

    /// <summary>Re-reads the settings after a change made elsewhere (for example from the camera's own menu).</summary>
    public void Refresh() => Changed();

    void Changed()
    {
        _cam.Set(_s.Webcam);
        _bg.Visibility = _s.Webcam ? Visibility.Visible : Visibility.Collapsed;
        _bgLabel.Text = CameraBackgrounds.Describe(_s.WebcamBackground);
        _mic.Set(_s.Microphone);
        _sys.Set(_s.SystemAudio);
        App.Settings.Save();
        if (_monitors)
        {
            if (_s.Microphone && _micMon == null) _micMon = LevelMonitor.Microphone(_s.MicrophoneId);
            if (!_s.Microphone) { _micMon?.Dispose(); _micMon = null; }
            if (_s.SystemAudio && _sysMon == null) _sysMon = LevelMonitor.SystemOutput();
            if (!_s.SystemAudio) { _sysMon?.Dispose(); _sysMon = null; }
        }
        var parts = new List<string>();
        if (_busy) parts.Add("Abriendo la cámara…");
        if (_error != null) parts.Add(_error);
        else
        {
            if (_s.Webcam) parts.Add("Arrastre la cámara para moverla; rueda o tirador para el tamaño");
            if (_s.Microphone) parts.Add("Micrófono: " + (MicName() ?? "predeterminado"));
            if (!_s.Microphone && !_s.SystemAudio) parts.Add("Sin sonido");
            parts.Add($"{_s.Fps} fps" + (_s.SaveAsGif ? " · GIF" : ""));
            parts.Add("Intro para grabar · Esc para cancelar");
        }
        _status.Text = string.Join("   ·   ", parts);
        _status.Foreground = _error != null ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9E, 0x7A)) : OffFg;
    }

    string? MicName() => _s.MicrophoneId == null
        ? Devices.DefaultMicrophoneName()
        : Devices.Microphones().FirstOrDefault(d => d.Id == _s.MicrophoneId)?.Name ?? Devices.DefaultMicrophoneName();

    void Tick()
    {
        _mic.SetLevel(_micMon?.Take() ?? 0);
        _sys.SetLevel(_sysMon?.Take() ?? 0);
    }

    void StopMonitors()
    {
        _micMon?.Dispose(); _micMon = null;
        _sysMon?.Dispose(); _sysMon = null;
    }

    // ------------------------------------------------------------------ menus

    ContextMenu MicMenu()
    {
        var m = new ContextMenu();
        var def = Devices.DefaultMicrophoneName();
        m.Items.Add(Check("Predeterminado de Windows" + (def != null ? $" ({def})" : ""), _s.MicrophoneId == null, () => SetMic(null)));
        var mics = Devices.Microphones();
        if (mics.Count > 0) m.Items.Add(new Separator());
        foreach (var d in mics) m.Items.Add(Check(d.Name, _s.MicrophoneId == d.Id, () => SetMic(d.Id)));
        if (mics.Count == 0) m.Items.Add(new MenuItem { Header = "No se encontró ningún micrófono", IsEnabled = false });
        return m;
    }

    void SetMic(string? id)
    {
        _s.MicrophoneId = id;
        _s.Microphone = true;
        _micMon?.Dispose(); _micMon = null;
        Changed();
    }

    ContextMenu OptionsMenu()
    {
        var m = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var fps = new MenuItem { Header = "Fotogramas por segundo" };
        foreach (var f in new[] { 15, 24, 30, 60 }) fps.Items.Add(Check($"{f} fps", _s.Fps == f, () => { _s.Fps = f; Changed(); }));
        m.Items.Add(fps);
        m.Items.Add(new Separator());
        m.Items.Add(Toggle("Mostrar el cursor", _s.Cursor, v => _s.Cursor = v));
        m.Items.Add(Toggle("Resaltar los clics", _s.HighlightClicks, v => _s.HighlightClicks = v));
        m.Items.Add(Toggle("Cuenta atrás de 3 segundos", _s.Countdown, v => _s.Countdown = v));
        m.Items.Add(Toggle("Guardar como GIF animado", _s.SaveAsGif, v => _s.SaveAsGif = v));
        m.Items.Add(Toggle("Abrir en el editor al terminar", _s.OpenInEditor, v => _s.OpenInEditor = v));
        return m;
    }

    MenuItem Toggle(string header, bool value, Action<bool> set)
    {
        var i = new MenuItem { Header = header, IsCheckable = true, IsChecked = value };
        i.Click += (_, _) => { set(i.IsChecked); Changed(); };
        return i;
    }

    static MenuItem Check(string header, bool on, Action a) => CameraMenus.Check(header, on, a);

    // ------------------------------------------------------------------ building blocks

    static FrameworkElement Separator() => new Border { Width = 1, Margin = new Thickness(6, 8, 6, 8), Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x44, 0x4A)) };

    static Button IconButton(string glyph, string tip, Action<Button> click)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("FlatButton"), Width = 40, Height = 40, Margin = new Thickness(1), Padding = new Thickness(0), ToolTip = tip,
            Content = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 16, Foreground = Brushes.White },
        };
        b.Click += (_, _) => click(b);
        return b;
    }

    /// <summary>On/off switch with icon, label, optional level meter and a device menu.</summary>
    sealed class Pill : Border
    {
        readonly string _onGlyph, _offGlyph;
        readonly TextBlock _icon = new() { FontFamily = Icons, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        readonly TextBlock _label = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        readonly Border _levelFill = new() { Height = 4, Width = 0, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(0x3C, 0xD0, 0x7A)) };
        readonly Border? _level;
        double _shown;
        bool _on;

        public Pill(string onGlyph, string offGlyph, string label, bool meter, Action toggle, Func<ContextMenu>? menu)
        {
            _onGlyph = onGlyph; _offGlyph = offGlyph;
            _label.Text = label;
            CornerRadius = new CornerRadius(8);
            Margin = new Thickness(2);
            var main = new Button { Style = (Style)Application.Current.FindResource("FlatButton"), Height = 40, Padding = new Thickness(10, 0, 10, 0), Background = Brushes.Transparent, ToolTip = $"Activar o desactivar: {label.ToLowerInvariant()}" };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(_icon);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(_label);
            if (meter)
            {
                _level = new Border { Height = 4, Width = 70, Margin = new Thickness(0, 4, 0, 0), CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x44, 0x4A)), Child = _levelFill, HorizontalAlignment = HorizontalAlignment.Left };
                text.Children.Add(_level);
            }
            content.Children.Add(text);
            main.Content = content;
            main.Click += (_, _) => toggle();
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(main);
            if (menu != null)
            {
                var chevron = new Button
                {
                    Style = (Style)Application.Current.FindResource("FlatButton"), Width = 26, Height = 40, Padding = new Thickness(0), Background = Brushes.Transparent, ToolTip = "Dispositivo y opciones",
                    Content = new TextBlock { Text = "\uE70D", FontFamily = Icons, FontSize = 10, Foreground = Brushes.White },
                };
                chevron.Click += (_, _) =>
                {
                    var m = menu();
                    m.PlacementTarget = chevron;
                    m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    m.IsOpen = true;
                };
                panel.Children.Add(chevron);
            }
            Child = panel;
        }

        public void Set(bool on)
        {
            _on = on;
            Background = on ? OnBrush : Brushes.Transparent;
            _icon.Text = on ? _onGlyph : _offGlyph;
            _icon.Foreground = on ? Brushes.White : OffFg;
            _label.Foreground = on ? Brushes.White : OffFg;
            if (_level != null) _level.Opacity = on ? 1 : 0.35;
            if (!on) SetLevel(0);
        }

        public void SetLevel(float peak)
        {
            if (_level == null) return;
            // Fast attack, slow release, with a perceptual curve so speech fills the bar.
            double target = _on ? Math.Sqrt(Math.Clamp(peak, 0, 1)) : 0;
            _shown = target > _shown ? target : _shown * 0.85;
            _levelFill.Width = _level.Width * _shown;
        }
    }
}
