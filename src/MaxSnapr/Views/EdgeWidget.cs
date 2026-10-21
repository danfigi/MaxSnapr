using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MaxSnapr.Capture;
using MaxSnapr.Native;
using MaxSnapr.Services;
using Microsoft.Win32;

namespace MaxSnapr.Views;

/// <summary>
/// Slim capture tab docked to the top edge of the primary screen; expands on hover.
/// A watchdog keeps it there whatever Windows does to it: show desktop, minimise all, screens being
/// connected or disconnected, scale changes, virtual desktops, sleep and lock, or another always-on-top
/// window covering it. It only steps aside for full-screen apps and while a capture hides it on purpose.
/// </summary>
public sealed class EdgeWidget : Window
{
    readonly Border _tab;
    readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(6, 4, 6, 6) };
    readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(1.5) };
    IntPtr _hwnd;

    /// <summary>How many times the watchdog had to bring the widget back, and why the last time (diagnostics and self-test).</summary>
    public int Repairs { get; private set; }
    public string? LastRepair { get; private set; }
    /// <summary>Why the last check did nothing on purpose (diagnostics and self-test).</summary>
    public string? LastSkip { get; private set; }

    public EdgeWidget()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        // No minimise box, so "minimise all" (Win+M) leaves it alone.
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        void B(string glyph, string tip, Action a)
        {
            var b = new Button { Style = (Style)Application.Current.FindResource("FlatButton"), Width = 40, Height = 36, Padding = new Thickness(0), ToolTip = tip,
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 17, Foreground = Brushes.White } };
            b.Click += (_, _) => { Collapse(); a(); };
            _buttons.Children.Add(b);
        }
        B("\uE722", "Capturar imagen", () => _ = App.Current.Capture.RunAsync(App.Settings.Current.AsImagePreset()));
        B("\uE714", "Grabar vídeo", () => _ = App.Current.Capture.RunAsync(new CapturePreset { RecordVideo = true }));
        B("\uE8D2", "Extraer texto", () => _ = App.Current.Capture.RunAsync(new CapturePreset { GrabText = true, OpenInEditor = false }));
        B("\uE8FD", "Captura de pasos", () => App.Current.StartStepCapture());
        B("\uE7F4", "Ventana de captura", () => App.Current.ShowCaptureWindow());
        B("\uE70F", "Abrir el editor", () => App.Current.ShowEditor());
        var grip = new Border { Width = 46, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0xC0, 255, 255, 255)), Margin = new Thickness(0, 4, 0, 3), HorizontalAlignment = HorizontalAlignment.Center };
        _tab = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x24, 0x27, 0x2B)), CornerRadius = new CornerRadius(0, 0, 10, 10),
            Child = new StackPanel { Children = { _buttons, grip } }, MinWidth = 90,
            // Drawn at its own size even if the window is bigger: Windows creates windows at a minimum of
            // about 136×39, and a stretched tab is what looked too big right after Windows started.
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        };
        Content = _tab;
        MouseEnter += (_, _) => { _buttons.Visibility = Visibility.Visible; Reposition(); };
        MouseLeave += (_, _) => Collapse();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Video.CaptureExclusion.Apply(this);
            // Tool window: not in Alt+Tab. No-activate: clicking it keeps the user's app in front,
            // so "capture the active window" still means their window, not ours.
            int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        };
        Loaded += (_, _) =>
        {
            Reposition();
            _watchdog.Start();
            SystemEvents.DisplaySettingsChanged += OnSystemChange;
            SystemEvents.UserPreferenceChanged += OnSystemChange;
            SystemEvents.SessionSwitch += OnSystemChange;
            SystemEvents.PowerModeChanged += OnSystemChange;
        };
        SizeChanged += (_, _) => Reposition();
        // The first layout pass can leave the window at Windows' minimum size; shrink it to the tab once drawn.
        ContentRendered += (_, _) => FitToContent();
        // Right after Windows starts, the window can be created at the system scale and only later receive
        // the monitor's: re-measure and re-place whenever the scale it is drawn with changes.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(FitToContent, DispatcherPriority.Loaded);
        _watchdog.Tick += (_, _) => EnsureVisible();
        Closed += (_, _) =>
        {
            _watchdog.Stop();
            // SystemEvents holds static references: unsubscribe or the closed window leaks.
            SystemEvents.DisplaySettingsChanged -= OnSystemChange;
            SystemEvents.UserPreferenceChanged -= OnSystemChange;
            SystemEvents.SessionSwitch -= OnSystemChange;
            SystemEvents.PowerModeChanged -= OnSystemChange;
        };
    }

    void OnSystemChange(object? sender, EventArgs e) =>
        // Screens and scale settle a moment after the event; check right away and again shortly after.
        Dispatcher.BeginInvoke(async () =>
        {
            EnsureVisible();
            await System.Threading.Tasks.Task.Delay(1500);
            EnsureVisible();
        });

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SYSCOMMAND = 0x0112, SC_MINIMIZE = 0xF020, SC_CLOSE = 0xF060;
        // Alt+F4 or "minimise" would make it vanish with no taskbar button to bring it back;
        // it is turned off from Preferences instead.
        if (msg == WM_SYSCOMMAND && ((wParam.ToInt64() & 0xFFF0) is SC_MINIMIZE or SC_CLOSE)) handled = true;
        return IntPtr.Zero;
    }

    void Collapse() { _buttons.Visibility = Visibility.Collapsed; }

    /// <summary>Shows the buttons as hovering does (used by the self-test).</summary>
    internal void Expand() { _buttons.Visibility = Visibility.Visible; Reposition(); }

    /// <summary>
    /// Where the widget belongs: centred on the top edge of the primary screen's work area, in physical pixels.
    /// The size is the content's, at the scale WPF actually draws this window with (which, just after
    /// Windows starts, can differ from the monitor's for a moment).
    /// </summary>
    internal Int32Rect ExpectedBounds()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var content = ContentSize();
        return Place(ScreenCapturer.GetMonitorsOnScreen(), content.Width * dpi.DpiScaleX, content.Height * dpi.DpiScaleY);
    }

    /// <summary>
    /// Centred on the top edge of the primary screen's work area. Monitors must be in screen coordinates:
    /// with a screen to the left of the main one, virtual-desktop coordinates would push the widget off screen.
    /// </summary>
    internal static Int32Rect Place(System.Collections.Generic.IReadOnlyList<MonitorInfo> monitors, double width, double height)
    {
        MonitorInfo? primary = null;
        foreach (var m in monitors) if (m.Primary) primary = m;
        primary ??= monitors.Count > 0 ? monitors[0] : new MonitorInfo(new Int32Rect(0, 0, 1920, 1080), new Int32Rect(0, 0, 1920, 1040), 1, "", true);
        var wa = primary.WorkArea;
        int w = Math.Max(1, (int)Math.Round(width)), h = Math.Max(1, (int)Math.Round(height));
        return new Int32Rect(wa.X + (wa.Width - w) / 2, wa.Y, w, h);
    }

    /// <summary>What the placement is computed from (diagnostics).</summary>
    internal string Describe()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in ScreenCapturer.GetMonitorsOnScreen())
            sb.AppendLine($"pantalla {m.Device} principal={m.Primary} monitor={m.Bounds} trabajo={m.WorkArea} escala={m.Scale:0.##}");
        var dpi = VisualTreeHelper.GetDpi(this);
        NativeMethods.GetWindowRect(_hwnd, out var r);
        sb.AppendLine($"escala WPF={dpi.DpiScaleX:0.##} contenido={ContentSize()} actual={ActualWidth}x{ActualHeight}");
        sb.AppendLine($"esperado={ExpectedBounds()} real=({r.Left},{r.Top}) {r.Width}x{r.Height}");
        return sb.ToString();
    }

    /// <summary>Size the tab needs, in device-independent units, whatever size the window currently has.</summary>
    Size ContentSize()
    {
        // Measure a stand-in copy of the layout state: measuring _tab itself with infinite space would
        // replace the window's own layout result, and collapsed buttons would count as zero.
        if (!_tab.IsMeasureValid) _tab.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var d = _tab.DesiredSize;
        return d.Width > 0 && d.Height > 0 ? d : new Size(ActualWidth, ActualHeight);
    }

    void Reposition()
    {
        if (_hwnd == IntPtr.Zero || ActualWidth <= 0) return;
        var e = ExpectedBounds();
        // Move only: the size is WPF's (SizeToContent). Forcing a size computed with another scale is what left
        // the widget slightly too big after Windows started. This also re-asserts always-on-top.
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, e.X, e.Y, 0, 0, 0x1 /*SWP_NOSIZE*/ | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>Makes the window exactly as big as its content again and puts it back in place.</summary>
    void FitToContent()
    {
        // An outside resize (or a scale change) switches WPF to a fixed size; restore content sizing.
        SizeToContent = SizeToContent.Manual;
        var c = ContentSize();
        Width = c.Width;
        Height = c.Height;
        SizeToContent = SizeToContent.WidthAndHeight;
        UpdateLayout();
        Reposition();
    }

    /// <summary>True when the window's physical size does not match its content at the current scale.</summary>
    bool WrongSize(NativeMethods.RECT r)
    {
        var e = ExpectedBounds();
        // Only what is on screen counts. When WPF drops content sizing but the size is still right, put it
        // back quietly so the next outside resize is undone by WPF itself.
        bool wrong = Math.Abs(r.Width - e.Width) > 2 || Math.Abs(r.Height - e.Height) > 2;
        if (!wrong && SizeToContent != SizeToContent.WidthAndHeight) SizeToContent = SizeToContent.WidthAndHeight;
        return wrong;
    }

    /// <summary>Puts the widget back if anything has hidden, moved, covered or minimised it. Returns what was fixed, or null.</summary>
    internal string? EnsureVisible()
    {
        LastSkip = _hwnd == IntPtr.Zero ? "sin ventana" : !App.Settings.ShowEdgeWidget ? "desactivado"
            // Captures and recordings hide our windows on purpose; full-screen apps and presentations own the screen.
            : CaptureService.HidingWindows ? "captura en curso" : Video.RecordingSession.IsRecording ? "grabando"
            : ForegroundIsFullScreen() ? "pantalla completa" : null;
        if (LastSkip != null) return null;

        string? reason = null;
        if (!IsVisible) { Show(); reason = "oculto"; }
        if (NativeMethods.IsIconic(_hwnd)) { ShowWindow(_hwnd, 4 /*SW_SHOWNOACTIVATE*/); reason = "minimizado"; }
        if (!IsOnCurrentVirtualDesktop(_hwnd)) { MoveToCurrentVirtualDesktop(_hwnd); reason = "en otro escritorio virtual"; }
        if (Cloaked(_hwnd))
        {
            // Our own cloak can be lifted; the shell's (other virtual desktop) was handled just above.
            int off = 0;
            DwmSetWindowAttribute(_hwnd, 13 /*DWMWA_CLOAK*/, ref off, sizeof(int));
            ShowWindow(_hwnd, 4);
            reason ??= "ocultado por Windows";
        }
        if ((NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE) & 0x8 /*WS_EX_TOPMOST*/) == 0) reason ??= "sin «siempre visible»";

        if (_buttons.IsVisible && !IsMouseOver) Collapse();
        var expected = ExpectedBounds();
        bool resize = false;
        if (NativeMethods.GetWindowRect(_hwnd, out var r))
        {
            if (WrongSize(r))
            {
                var c = ContentSize();
                reason ??= $"tamaño incorrecto: {r.Width}×{r.Height} en lugar de {expected.Width}×{expected.Height} (contenido {c.Width:0.#}×{c.Height:0.#}, escala {VisualTreeHelper.GetDpi(this).DpiScaleX:0.##})";
                resize = true;
            }
            else if (Math.Abs(r.Left - expected.X) > 2 || Math.Abs(r.Top - expected.Y) > 2) reason ??= "fuera de su sitio";
            else if (Covered(r)) reason ??= "tapado por otra ventana";
        }
        if (reason == null) return null;

        if (resize) FitToContent(); else Reposition();
        Repairs++;
        LastRepair = reason;
        Log.Info($"Widget del borde restaurado ({reason})");
        return reason;
    }

    /// <summary>True when the point under the grip belongs to another window.</summary>
    bool Covered(NativeMethods.RECT r)
    {
        var p = new NativeMethods.POINT { X = (r.Left + r.Right) / 2, Y = r.Bottom - 3 };
        var top = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(p), 2 /*GA_ROOT*/);
        if (top == IntPtr.Zero || top == _hwnd) return false;
        GetWindowThreadProcessId(top, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false; // our own menus and tooltips do not count
        // Another MaxSnapr's widget in the same spot (an isolated test copy next to the real app) is not covering it.
        var cls = new System.Text.StringBuilder(128);
        NativeMethods.GetClassName(top, cls, cls.Capacity);
        try { if (cls.ToString().StartsWith("HwndWrapper[") && System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.Equals("MaxSnapr", StringComparison.OrdinalIgnoreCase)) return false; }
        catch (ArgumentException) { }
        return true;
    }

    static bool Cloaked(IntPtr h) => NativeMethods.DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out int c, sizeof(int)) == 0 && c != 0;

    /// <summary>
    /// True when the foreground window really covers its whole monitor (a game, a video, a slide show).
    /// Windows' own "busy" state is not enough: it is also set by "do not disturb" and by focus sessions,
    /// which would hide the widget for no visible reason.
    /// </summary>
    /// <summary>Self-test only: behave as if no full-screen app were open, whatever the user has in front.</summary>
    internal static bool IgnoreFullScreen { get; set; }

    internal static bool ForegroundIsFullScreen()
    {
        if (IgnoreFullScreen) return false;
        // QUNS_RUNNING_D3D_FULL_SCREEN (3) and QUNS_PRESENTATION_MODE (4) are always full screen.
        if (SHQueryUserNotificationState(out int state) == 0 && state is 3 or 4) return true;
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == NativeMethods.GetDesktopWindow() || !NativeMethods.GetWindowRect(fg, out var r)) return false;
        var cls = new System.Text.StringBuilder(64);
        NativeMethods.GetClassName(fg, cls, cls.Capacity);
        // The desktop and the taskbar span the screen but are not full-screen apps.
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        foreach (var m in ScreenCapturer.GetMonitorsOnScreen())
        {
            var b = m.Bounds;
            if (r.Left <= b.X && r.Top <= b.Y && r.Right >= b.X + b.Width && r.Bottom >= b.Y + b.Height && m.Primary) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ virtual desktops (documented IVirtualDesktopManager)

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    static IVirtualDesktopManager? _vdm;
    static bool _vdmFailed;

    static IVirtualDesktopManager? Desktops()
    {
        if (_vdm != null || _vdmFailed) return _vdm;
        try { _vdm = (IVirtualDesktopManager?)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"))!); }
        catch { _vdmFailed = true; }
        return _vdm;
    }

    static bool IsOnCurrentVirtualDesktop(IntPtr h)
    {
        var m = Desktops();
        return m == null || m.IsWindowOnCurrentVirtualDesktop(h, out bool on) != 0 || on;
    }

    static void MoveToCurrentVirtualDesktop(IntPtr h)
    {
        var m = Desktops();
        // The foreground window is on the desktop the user is looking at; follow it.
        var fg = NativeMethods.GetForegroundWindow();
        if (m == null || fg == IntPtr.Zero || m.GetWindowDesktopId(fg, out var id) != 0 || id == Guid.Empty) return;
        m.MoveWindowToDesktop(h, ref id);
    }

    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
}
