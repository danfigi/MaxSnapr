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
        // The first layout pass can still change the size after Loaded; place it again once it has been drawn.
        ContentRendered += (_, _) => Reposition();
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

    /// <summary>Where the widget belongs: centred on the top edge of the primary screen's work area, in physical pixels.</summary>
    internal Int32Rect ExpectedBounds()
    {
        MonitorInfo? primary = null;
        foreach (var m in ScreenCapturer.GetMonitors()) if (m.Primary) primary = m;
        primary ??= new MonitorInfo(new Int32Rect(0, 0, 1920, 1080), new Int32Rect(0, 0, 1920, 1040), 1, "", true);
        var wa = primary.WorkArea;
        int w = Math.Max(1, (int)Math.Round(ActualWidth * primary.Scale)), h = Math.Max(1, (int)Math.Round(ActualHeight * primary.Scale));
        return new Int32Rect(wa.X + (wa.Width - w) / 2, wa.Y, w, h);
    }

    void Reposition()
    {
        if (_hwnd == IntPtr.Zero || ActualWidth <= 0) return;
        // Physical coordinates keep it right across screens with different scales; this also re-asserts always-on-top.
        Video.CaptureExclusion.PlacePhysical(this, ExpectedBounds());
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

        var expected = ExpectedBounds();
        if (NativeMethods.GetWindowRect(_hwnd, out var r))
        {
            if (Math.Abs(r.Left - expected.X) > 2 || Math.Abs(r.Top - expected.Y) > 2) reason ??= "fuera de su sitio";
            else if (Covered(r)) reason ??= "tapado por otra ventana";
        }
        if (_buttons.IsVisible && !IsMouseOver) Collapse();
        if (reason == null) return null;

        Reposition();
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
        return pid != (uint)Environment.ProcessId; // our own menus and tooltips do not count
    }

    static bool Cloaked(IntPtr h) => NativeMethods.DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out int c, sizeof(int)) == 0 && c != 0;

    internal static bool ForegroundIsFullScreen()
    {
        // QUNS_BUSY (2), QUNS_RUNNING_D3D_FULL_SCREEN (3) and QUNS_PRESENTATION_MODE (4).
        return SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4;
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
