using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxSnapr.Editor.Model;
using MaxSnapr.Editor.Raster;
using MaxSnapr.Library;
using MaxSnapr.Services;
using MaxSnapr.Views;

namespace MaxSnapr.Capture;

/// <summary>Runs one capture end to end: hide our windows, delay, select, then route the pixels.</summary>
public sealed class CaptureService
{
    bool _busy;
    CapturePreset? _last;
    Int32Rect? _lastRect;
    CancellationTokenSource? _interval;

    public bool IntervalRunning => _interval != null;

    public async Task RepeatLastAsync()
    {
        if (_last == null || _lastRect == null) { await RunAsync(App.Settings.Current); return; }
        await RunAsync(_last, _lastRect);
    }

    public void StopInterval()
    {
        _interval?.Cancel();
        _interval = null;
    }

    public async Task RunAsync(CapturePreset preset, Int32Rect? fixedRect = null, bool isRepeat = false)
    {
        if (_busy || Video.RecordingSession.IsRecording) return;
        if (preset.Selection == CaptureSelectionMode.Clipboard && !preset.RecordVideo) { FromClipboard(); return; }
        _busy = true;
        var hidden = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible && w is not CountdownWindow).ToList();
        Int32Rect? captured = null;
        try
        {
            foreach (var w in hidden) w.Hide();
            // Give DWM a moment to remove our windows from the screen before freezing it.
            await Task.Delay(hidden.Count > 0 ? 250 : 60);
            int delay = preset.DelaySeconds;
            if (preset.Selection == CaptureSelectionMode.Menu && delay == 0 && !isRepeat) delay = 5;
            if (delay > 0 && !isRepeat) await CountdownWindow.RunAsync(delay);

            var snap = ScreenCapturer.Snapshot();
            Int32Rect? rect = fixedRect;
            WindowInfo? window = null;
            CaptureSelection? sel = null;
            CaptureAction action = preset.GrabText ? CaptureAction.GrabText : preset.RecordVideo ? CaptureAction.Video
                : preset.Selection == CaptureSelectionMode.Scrolling ? CaptureAction.Scroll
                : preset.Selection == CaptureSelectionMode.Panoramic ? CaptureAction.Panoramic : CaptureAction.Image;

            if (rect == null)
            {
                switch (preset.Selection)
                {
                    case CaptureSelectionMode.FullScreen when !preset.RecordVideo:
                        var cur = snap.CursorPosition;
                        var mon = snap.Monitors.FirstOrDefault(m => CaptureOverlay.Contains(m.Bounds, cur)) ?? snap.Monitors.First(m => m.Primary);
                        rect = mon.Bounds;
                        break;
                    case CaptureSelectionMode.AllScreens:
                        rect = new Int32Rect(0, 0, snap.Image.PixelWidth, snap.Image.PixelHeight);
                        break;
                    case CaptureSelectionMode.ActiveWindow:
                        var fg = Native.NativeMethods.GetForegroundWindow();
                        window = snap.Windows.FirstOrDefault(w => w.Handle == fg) ?? snap.Windows.FirstOrDefault();
                        rect = window?.Bounds;
                        break;
                    case CaptureSelectionMode.Menu:
                        rect = MenuBounds(snap);
                        if (rect == null) goto default;
                        break;
                    default:
                        var (mode, label) = preset.Selection switch
                        {
                            CaptureSelectionMode.FixedRegion => (OverlayMode.FixedSize, "Tamaño fijo"),
                            CaptureSelectionMode.Freehand => (OverlayMode.Freehand, "Mano alzada"),
                            CaptureSelectionMode.MultiArea => (OverlayMode.MultiArea, "Varias áreas"),
                            CaptureSelectionMode.Object => (OverlayMode.Object, "Objeto"),
                            CaptureSelectionMode.Window or CaptureSelectionMode.ResizedWindow => (OverlayMode.Window, "Ventana"),
                            CaptureSelectionMode.Scrolling => (OverlayMode.Standard, "Desplazamiento"),
                            CaptureSelectionMode.Panoramic => (OverlayMode.Standard, "Panorámica"),
                            _ => (OverlayMode.Standard, preset.GrabText ? "Extraer texto" : preset.RecordVideo ? "Vídeo" : "Captura"),
                        };
                        bool immediate = preset.GrabText || preset.RecordVideo || mode != OverlayMode.Standard || action is CaptureAction.Scroll or CaptureAction.Panoramic;
                        var overlay = new CaptureOverlay(snap, immediate, label, mode, new Size(preset.FixedWidth, preset.FixedHeight));
                        sel = await overlay.RunAsync();
                        if (sel == null) return;
                        rect = sel.Rect;
                        window = sel.Window;
                        if (action == CaptureAction.Image) action = sel.Action;
                        break;
                }
            }
            if (preset.Selection == CaptureSelectionMode.ResizedWindow && window != null && fixedRect == null)
            {
                var resized = await CaptureResizedWindow(window, preset.FixedWidth, preset.FixedHeight);
                if (resized != null) { snap = resized.Value.snap; rect = resized.Value.rect; }
            }
            if (rect is not Int32Rect r || r.Width < 1 || r.Height < 1) return;
            captured = r;

            if (action == CaptureAction.Video)
            {
                // Recording shows its own frame and HUD; our editor stays hidden until it finishes.
                await Video.RecordingSession.StartAsync(r, App.Settings.Video);
                return;
            }

            BitmapSource? image;
            if (action is CaptureAction.Scroll or CaptureAction.Panoramic)
            {
                image = action == CaptureAction.Scroll ? await ScrollCapture.AutoAsync(r, CancellationToken.None) : await ScrollCapture.ManualAsync(r, CancellationToken.None);
                if (image == null) return;
            }
            else
            {
                image = snap.Crop(r, preset.IncludeCursor);
                if (sel?.Lasso != null) image = Mask(image, r, lasso: sel.Lasso, areas: null);
                else if (sel?.Areas != null) image = Mask(image, r, lasso: null, areas: sel.Areas);
            }
            image = ApplyCaptureEffects(image, preset);
            _last = preset;
            _lastRect = r;
            var (title, app, url) = DescribeFull(window ?? snap.Windows.FirstOrDefault(w => CaptureOverlay.Contains(w.Bounds, new Point(r.X + r.Width / 2.0, r.Y + r.Height / 2.0))));

            switch (action)
            {
                case CaptureAction.GrabText:
                    await GrabTextAsync(image);
                    return;
                case CaptureAction.Copy:
                    ClipboardService.SetImage(image);
                    LibraryStore.Instance.Add(new EditorDocument(image), title, app, url);
                    App.Current.Notify("Captura copiada", $"{image.PixelWidth} × {image.PixelHeight} px en el portapapeles");
                    return;
                default:
                    var doc = new EditorDocument(image);
                    var item = LibraryStore.Instance.Add(doc, title, app, url);
                    if (preset.CopyToClipboard) ClipboardService.SetImage(image);
                    if (preset.ShareToFolder) Outputs.SaveToOutputFolder(image, title, app);
                    bool open = preset.OpenInEditor && !(isRepeat || preset.IntervalSeconds > 0);
                    if (open)
                    {
                        var editor = App.Current.ShowEditor();
                        editor.OpenDocument(doc, item);
                        hidden.Remove(editor);
                    }
                    else if (!isRepeat) App.Current.Notify("Captura guardada", preset.CopyToClipboard ? "Copiada al portapapeles y guardada en la biblioteca" : "Guardada en la biblioteca");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Captura", ex);
            MessageBox.Show("No se pudo completar la captura: " + ex.Message, "MaxSnapr", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            foreach (var w in hidden.Where(w => w.IsLoaded && w is not CountdownWindow)) w.Show();
            _busy = false;
        }
        if (!isRepeat && preset.IntervalSeconds > 0 && captured != null && !preset.GrabText && !preset.RecordVideo) StartInterval(preset, captured.Value);
    }

    void StartInterval(CapturePreset preset, Int32Rect rect)
    {
        StopInterval();
        var cts = _interval = new CancellationTokenSource();
        // Whole-screen modes recapture the screen; region modes keep the chosen area.
        Int32Rect? fixedRect = preset.Selection is CaptureSelectionMode.FullScreen or CaptureSelectionMode.AllScreens or CaptureSelectionMode.ActiveWindow ? null : rect;
        _ = Task.Run(async () =>
        {
            for (int i = 2; i <= preset.IntervalCount && !cts.IsCancellationRequested; i++)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(preset.IntervalSeconds), cts.Token); } catch { break; }
                int n = i;
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await RunAsync(preset, fixedRect, isRepeat: true);
                    App.Current.Notify("Captura programada", $"{n} de {preset.IntervalCount}");
                }).Task.Unwrap();
            }
            if (_interval == cts) _interval = null;
        });
        App.Current.Notify("Capturas programadas", $"Se harán {preset.IntervalCount} capturas cada {preset.IntervalSeconds} s. Puede detenerlas desde el icono de MaxSnapr.");
    }

    /// <summary>Resizes the window to an exact outer size, re-captures the screen, then restores it.</summary>
    static async Task<(ScreenSnapshot snap, Int32Rect rect)?> CaptureResizedWindow(WindowInfo w, int width, int height)
    {
        if (!Native.NativeMethods.GetWindowRect(w.Handle, out var original)) return null;
        // Visible frame (DWM bounds) differs from GetWindowRect by the invisible resize borders.
        int extraW = original.Width - w.Bounds.Width, extraH = original.Height - w.Bounds.Height;
        Native.NativeMethods.SetWindowPos(w.Handle, IntPtr.Zero, original.Left, original.Top, width + extraW, height + extraH, Native.NativeMethods.SWP_NOZORDER | Native.NativeMethods.SWP_NOACTIVATE);
        await Task.Delay(600);
        try
        {
            var snap = ScreenCapturer.Snapshot();
            var win = snap.Windows.FirstOrDefault(x => x.Handle == w.Handle);
            return win == null ? null : (snap, ScreenSnapshot.Intersect(win.Bounds, new Int32Rect(0, 0, snap.Image.PixelWidth, snap.Image.PixelHeight)));
        }
        finally
        {
            Native.NativeMethods.SetWindowPos(w.Handle, IntPtr.Zero, original.Left, original.Top, original.Width, original.Height, Native.NativeMethods.SWP_NOZORDER | Native.NativeMethods.SWP_NOACTIVATE);
        }
    }

    void FromClipboard()
    {
        var img = ClipboardService.GetImage();
        if (img == null) { MessageBox.Show("El portapapeles no contiene ninguna imagen.", "MaxSnapr"); return; }
        var doc = new EditorDocument(img);
        var item = LibraryStore.Instance.Add(doc, "Desde el portapapeles", "Portapapeles");
        App.Current.ShowEditor().OpenDocument(doc, item);
    }

    /// <summary>Union of the open popup menus (window class #32768), cascades included.</summary>
    static Int32Rect? MenuBounds(ScreenSnapshot snap)
    {
        var menus = snap.Windows.Where(w => w.ClassName == "#32768" || w.ClassName.Contains("DropDown", StringComparison.OrdinalIgnoreCase) || w.ClassName.Contains("Popup", StringComparison.OrdinalIgnoreCase)).ToList();
        if (menus.Count == 0) return null;
        int x1 = menus.Min(m => m.Bounds.X), y1 = menus.Min(m => m.Bounds.Y);
        int x2 = menus.Max(m => m.Bounds.X + m.Bounds.Width), y2 = menus.Max(m => m.Bounds.Y + m.Bounds.Height);
        return ScreenSnapshot.Intersect(new Int32Rect(x1, y1, x2 - x1, y2 - y1), new Int32Rect(0, 0, snap.Image.PixelWidth, snap.Image.PixelHeight));
    }

    static BitmapSource Mask(BitmapSource image, Int32Rect r, IReadOnlyList<Point>? lasso, IReadOnlyList<Int32Rect>? areas)
    {
        Geometry clip;
        if (lasso != null)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(lasso[0].X - r.X, lasso[0].Y - r.Y), true, true);
                c.PolyLineTo(lasso.Skip(1).Select(p => new Point(p.X - r.X, p.Y - r.Y)).ToList(), true, true);
            }
            clip = g;
        }
        else
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var a in areas!) group.Children.Add(new RectangleGeometry(new Rect(a.X - r.X, a.Y - r.Y, a.Width, a.Height)));
            clip = group;
        }
        return RasterOps.Render(image.PixelWidth, image.PixelHeight, dc =>
        {
            dc.PushClip(clip);
            dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            dc.Pop();
        }, BitmapScalingMode.NearestNeighbor);
    }

    static BitmapSource ApplyCaptureEffects(BitmapSource img, CapturePreset p)
    {
        if (p.EffectTornEdge) img = RasterOps.Edge(img, Editor.Raster.EdgeSides.Bottom, 14, "torn", Environment.TickCount);
        if (p.EffectBorder) img = RasterOps.Border(img, 2, Color.FromRgb(0x9C, 0xA3, 0xAF));
        if (p.EffectShadow) img = RasterOps.Shadow(img, 18, 6, Color.FromArgb(110, 0, 0, 0));
        return img;
    }

    public static (string title, string app) Describe(WindowInfo? w) { var (t, a, _) = DescribeFull(w); return (t, a); }

    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };

    /// <summary>Window title, application name and, for browsers, the page address (best effort through UI Automation).</summary>
    public static (string title, string app, string url) DescribeFull(WindowInfo? w)
    {
        if (w == null) return ("", "", "");
        string app = "", url = "";
        try
        {
            var p = Process.GetProcessById((int)w.ProcessId);
            app = p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d : p.ProcessName;
            if (Browsers.Contains(p.ProcessName.ToLowerInvariant()))
            {
                var h = w.Handle;
                var task = Task.Run(() =>
                {
                    var root = System.Windows.Automation.AutomationElement.FromHandle(h);
                    var edit = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Edit));
                    if (edit?.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var vp) == true)
                        return ((System.Windows.Automation.ValuePattern)vp).Current.Value;
                    return "";
                });
                if (task.Wait(800)) url = task.Result.Contains('.') && !task.Result.Contains(' ') ? task.Result : "";
            }
        }
        catch { }
        return (w.Title, app, url);
    }

    static async Task GrabTextAsync(BitmapSource image)
    {
        try
        {
            var text = await OcrService.RecognizeAsync(image);
            ClipboardService.SetText(text);
            new TextResultWindow(text, image).Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Extraer texto", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Screen-colour eyedropper using the same frozen-screen overlay.</summary>
    public static async Task<Color?> PickScreenColorAsync()
    {
        var hidden = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible).ToList();
        foreach (var w in hidden) w.Hide();
        await Task.Delay(200);
        try
        {
            var snap = ScreenCapturer.Snapshot();
            var sel = await new CaptureOverlay(snap, true, "Cuentagotas", OverlayMode.PickColor).RunAsync();
            if (sel == null) return null;
            return CaptureOverlay.OverlaySurface.PixelAt(snap.Image, sel.Rect.X, sel.Rect.Y);
        }
        finally { foreach (var w in hidden) w.Show(); }
    }
}

/// <summary>Click-through countdown in the centre of the primary screen.</summary>
public sealed class CountdownWindow : Window
{
    readonly TextBlock _text = new() { FontSize = 64, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    CountdownWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = Height = 140;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new Border { CornerRadius = new CornerRadius(70), Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x1E, 0x20, 0x24)), Child = _text };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.NativeMethods.SetWindowLong(h, Native.NativeMethods.GWL_EXSTYLE,
                Native.NativeMethods.GetWindowLong(h, Native.NativeMethods.GWL_EXSTYLE) | 0x20 /*WS_EX_TRANSPARENT*/ | Native.NativeMethods.WS_EX_TOOLWINDOW | Native.NativeMethods.WS_EX_NOACTIVATE);
            Video.CaptureExclusion.Apply(this);
        };
    }

    public static async Task RunAsync(int seconds)
    {
        var w = new CountdownWindow();
        w.Show();
        for (int i = seconds; i > 0; i--)
        {
            w._text.Text = i.ToString();
            await Task.Delay(1000);
        }
        w.Close();
        await Task.Delay(150);
    }
}
