using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MaxSnapr.Capture;
using MaxSnapr.Library;
using MaxSnapr.Services;
using MaxSnapr.Views;
using Forms = System.Windows.Forms;

namespace MaxSnapr;

public partial class App : Application
{
    // Isolated (MAXSNAPR_HOME) runs get their own instance so they can coexist with a real one.
    static readonly string InstanceSuffix = AppSettings.Home == null ? "" : "." + StableHash(AppSettings.Home.ToLowerInvariant());

    // string.GetHashCode is randomised per process, so use FNV-1a for a name shared between processes.
    static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }
    static readonly string MutexName = "MaxSnapr.SingleInstance" + InstanceSuffix;
    static readonly string ActivateEventName = "MaxSnapr.Activate" + InstanceSuffix;

    Mutex? _mutex;
    EventWaitHandle? _activate;
    Forms.NotifyIcon? _tray;
    HotkeyService? _hotkeys;

    public static AppSettings Settings { get; private set; } = new();
    public static new App Current => (App)Application.Current;
    public CaptureService Capture { get; private set; } = null!;
    public EditorWindow? Editor { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Build helper: writes the multi-size application icon used by the .exe.
        if (e.Args.Length >= 2 && e.Args[0] == "--write-icon")
        {
            AppIcon.WriteIco(e.Args[1]);
            Shutdown(0);
            return;
        }
        // Diagnostic: applies every camera background to a still image and reports the model's speed.
        if (e.Args.Length >= 3 && e.Args[0] == "--segment-test")
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[2], "segment.txt"), Video.BackgroundCompositor.SegmentTest(e.Args[1], e.Args[2]));
            Shutdown(0);
            return;
        }
        // Diagnostic: checks that cameras and microphones open, recording only sizes and levels.
        if (e.Args.Length >= 2 && e.Args[0] == "--probe-devices")
        {
            Dispatcher.BeginInvoke(async () =>
            {
                System.IO.File.WriteAllText(e.Args[1], await Video.DeviceProbe.RunAsync());
                Shutdown(0);
            });
            return;
        }
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Second launch: ask the running instance to show its capture window.
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Excepción no controlada", ex.Exception);
            MessageBox.Show(ex.Exception.Message, "MaxSnapr", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            Settings = AppSettings.Load();
            LibraryStore.Initialize(Settings.LibraryFolder);
            Capture = new CaptureService();
            var outDir = e.Args[1];
            Dispatcher.BeginInvoke(async () => Shutdown(await SelfTest.RunAsync(outDir)));
            return;
        }

        Settings = AppSettings.Load();
        ThemeService.Apply(Settings.Theme);
        LibraryStore.Initialize(Settings.LibraryFolder);
        Capture = new CaptureService();

        CreateTray();
        RegisterHotkeys();
        ListenForActivation();

        UpdateEdgeWidget();
        UpdateClipboardWatcher();
        bool trayOnly = e.Args.Contains("--tray");
        var file = e.Args.FirstOrDefault(a => !a.StartsWith("--"));
        if (file != null) ShowEditor().OpenFile(file);
        else if (!trayOnly) ShowEditor();
        if (Settings.ShowWelcome && !trayOnly && file == null) Dispatcher.BeginInvoke(() => new WelcomeWindow { Owner = Editor }.ShowDialog(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    void ListenForActivation()
    {
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var wait = _activate;
        Task.Run(() =>
        {
            while (wait.WaitOne())
                Dispatcher.BeginInvoke(() => ShowEditor());
        });
    }

    public void RegisterHotkeys()
    {
        // Test runs must not take system-wide keys away from the user's real tools.
        if (Environment.GetEnvironmentVariable("MAXSNAPR_NO_HOTKEYS") == "1") { CaptureHotkeyText = Settings.CaptureHotkey.ToString(); return; }
        _hotkeys ??= new HotkeyService();
        _hotkeys.UnregisterAll();
        bool main = _hotkeys.Register(Settings.CaptureHotkey, () => _ = Capture.RunAsync(Settings.Current));
        if (!main) _hotkeys.Register(Settings.FallbackCaptureHotkey, () => _ = Capture.RunAsync(Settings.Current));
        _hotkeys.Register(Settings.RepeatHotkey, () => _ = Capture.RepeatLastAsync());
        _hotkeys.Register(Settings.GrabTextHotkey, () => _ = Capture.RunAsync(new CapturePreset { Name = "Extraer texto", GrabText = true, OpenInEditor = false }));
        _hotkeys.Register(Settings.VideoHotkey, () => _ = Capture.RunAsync(new CapturePreset { Name = "Vídeo", RecordVideo = true }));
        _hotkeys.Register(Settings.StepCaptureHotkey, StartStepCapture);
        foreach (var p in Settings.Presets.Where(p => p.Hotkey != null && p.Hotkey.Key != 0))
        {
            var preset = p;
            _hotkeys.Register(p.Hotkey!, () => _ = Capture.RunAsync(preset));
        }
        CaptureHotkeyText = main ? Settings.CaptureHotkey.ToString() : Settings.FallbackCaptureHotkey.ToString();
        if (_tray != null) _tray.Text = $"MaxSnapr – {CaptureHotkeyText} para capturar";
    }

    public string CaptureHotkeyText { get; private set; } = "";

    void CreateTray()
    {
        _tray = new Forms.NotifyIcon { Icon = AppIcon.CreateIcon(), Visible = true, Text = "MaxSnapr" };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Capturar", null, (_, _) => _ = Capture.RunAsync(Settings.Current));
        menu.Items.Add("Capturar región", null, (_, _) => _ = Capture.RunAsync(new CapturePreset { Selection = CaptureSelectionMode.Region }));
        menu.Items.Add("Capturar ventana", null, (_, _) => _ = Capture.RunAsync(new CapturePreset { Selection = CaptureSelectionMode.Window }));
        menu.Items.Add("Capturar pantalla completa", null, (_, _) => _ = Capture.RunAsync(new CapturePreset { Selection = CaptureSelectionMode.FullScreen }));
        menu.Items.Add("Extraer texto de la pantalla", null, (_, _) => _ = Capture.RunAsync(new CapturePreset { GrabText = true, OpenInEditor = false }));
        menu.Items.Add("Grabar vídeo", null, (_, _) => _ = Capture.RunAsync(new CapturePreset { RecordVideo = true }));
        menu.Items.Add("Captura de pasos", null, (_, _) => StartStepCapture());
        menu.Items.Add("Recibir desde el móvil…", null, (_, _) => new MobileReceiveWindow().Show());
        var stopInterval = new Forms.ToolStripMenuItem("Detener las capturas programadas", null, (_, _) => Capture.StopInterval());
        menu.Items.Add(stopInterval);
        menu.Opening += (_, _) => stopInterval.Visible = Capture.IntervalRunning;
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Ventana de captura…", null, (_, _) => ShowCaptureWindow());
        menu.Items.Add("Abrir el editor", null, (_, _) => ShowEditor());
        menu.Items.Add("Biblioteca", null, (_, _) => ShowEditor().ShowLibrary());
        menu.Items.Add("Preferencias…", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowCaptureWindow(); };
    }

    public void Notify(string title, string text)
    {
        _tray?.ShowBalloonTip(2500, title, text, Forms.ToolTipIcon.Info);
    }

    CaptureWindow? _captureWindow;
    EdgeWidget? _edge;

    public void StartStepCapture() => StepCaptureSession.Start();

    ClipboardWatcher? _clipWatcher;

    /// <summary>Optionally adds every image copied in other applications to the library.</summary>
    public void UpdateClipboardWatcher()
    {
        if (Settings.WatchClipboard && _clipWatcher == null) _clipWatcher = new ClipboardWatcher();
        else if (!Settings.WatchClipboard && _clipWatcher != null) { _clipWatcher.Dispose(); _clipWatcher = null; }
    }

    public void UpdateEdgeWidget()
    {
        if (Settings.ShowEdgeWidget && _edge == null) { _edge = new EdgeWidget(); _edge.Show(); }
        else if (!Settings.ShowEdgeWidget && _edge != null) { _edge.Close(); _edge = null; }
    }

    public void ShowCaptureWindow()
    {
        if (_captureWindow == null || !_captureWindow.IsLoaded)
        {
            _captureWindow = new CaptureWindow();
            _captureWindow.Closed += (_, _) => _captureWindow = null;
        }
        _captureWindow.Show();
        _captureWindow.Activate();
    }

    public EditorWindow ShowEditor()
    {
        if (Editor == null)
        {
            Editor = new EditorWindow();
            Editor.Closed += (_, _) => Editor = null;
        }
        if (!Editor.IsVisible) Editor.Show();
        if (Editor.WindowState == WindowState.Minimized) Editor.WindowState = WindowState.Normal;
        Editor.Activate();
        return Editor;
    }

    public void ShowSettings(string? tab = null)
    {
        var w = new SettingsWindow(tab) { Owner = Editor?.IsVisible == true ? Editor : null };
        w.ShowDialog();
    }

    public void Quit()
    {
        Editor?.SaveCurrent();
        Settings.Save();
        LibraryStore.Instance.Save();
        _hotkeys?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Programmatic app icon (an aperture-like mark) so no binary asset is required.</summary>
public static class AppIcon
{
    // MaxSnapr mark: a warm rounded tile with viewfinder corners framing Max's paw print.
    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(System.Drawing.Color.Transparent);
        float s = size;

        float inset = s * 0.02f, r = s * 0.24f;
        var tile = new RectangleF(inset, inset, s - 2 * inset, s - 2 * inset);
        using (var path = RoundedRect(tile, r))
        using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(tile, System.Drawing.Color.FromArgb(0xFF, 0xB1, 0x3D), System.Drawing.Color.FromArgb(0xEE, 0x4B, 0x5E), 45f))
            g.FillPath(bg, path);

        // Small sizes drop the corners so the paw stays legible.
        bool corners = size >= 24;
        using var white = new SolidBrush(System.Drawing.Color.White);
        if (corners)
        {
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(235, 255, 255, 255), s * 0.07f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            float m = s * 0.19f, l = s * 0.15f;
            g.DrawLines(pen, new[] { new PointF(m, m + l), new PointF(m, m), new PointF(m + l, m) });
            g.DrawLines(pen, new[] { new PointF(s - m - l, m), new PointF(s - m, m), new PointF(s - m, m + l) });
            g.DrawLines(pen, new[] { new PointF(s - m, s - m - l), new PointF(s - m, s - m), new PointF(s - m - l, s - m) });
            g.DrawLines(pen, new[] { new PointF(m + l, s - m), new PointF(m, s - m), new PointF(m, s - m - l) });
        }

        // Paw: one main pad and four toes, scaled up when there are no corners.
        float k = corners ? 1f : 1.35f, cx = s * 0.5f, cy = s * (corners ? 0.53f : 0.54f);
        void Oval(float x, float y, float w, float h, float angle)
        {
            var state = g.Save();
            g.TranslateTransform(cx + x * s * k, cy + y * s * k);
            g.RotateTransform(angle);
            g.FillEllipse(white, -w * s * k / 2, -h * s * k / 2, w * s * k, h * s * k);
            g.Restore(state);
        }
        Oval(0, 0.085f, 0.27f, 0.21f, 0);
        Oval(-0.155f, -0.045f, 0.085f, 0.115f, -22);
        Oval(-0.058f, -0.115f, 0.085f, 0.12f, -6);
        Oval(0.058f, -0.115f, 0.085f, 0.12f, 6);
        Oval(0.155f, -0.045f, 0.085f, 0.115f, 22);
        return bmp;
    }

    static System.Drawing.Drawing2D.GraphicsPath RoundedRect(RectangleF b, float r)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        float d = r * 2;
        p.AddArc(b.X, b.Y, d, d, 180, 90);
        p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
        p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Icon CreateIcon(int size = 32)
    {
        using var bmp = Render(size);
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Writes an .ico with each size rendered natively (PNG-compressed entries).</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var images = sizes.Select(sz =>
        {
            using var bmp = Render(sz);
            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();
        using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(images[i].Length); w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }

    public static System.Windows.Media.ImageSource CreateImageSource()
    {
        using var bmp = Render(128);
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var src = new System.Windows.Media.Imaging.BitmapImage();
        src.BeginInit();
        src.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        src.StreamSource = ms;
        src.EndInit();
        src.Freeze();
        return src;
    }
}

public static class ThemeService
{
    public static string Current { get; private set; } = "Dark";

    public static void Apply(string theme)
    {
        var effective = theme;
        if (theme == "System")
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                effective = key?.GetValue("AppsUseLightTheme") is int v && v == 1 ? "Light" : "Dark";
            }
            catch { effective = "Dark"; }
        }
        Current = effective;
        var dicts = Application.Current.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary { Source = new Uri($"Themes/{effective}.xaml", UriKind.Relative) };
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static event EventHandler? ThemeChanged;
}
