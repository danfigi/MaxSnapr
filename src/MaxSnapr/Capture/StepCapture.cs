using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaxSnapr.Editor.Model;
using MaxSnapr.Editor.Raster;
using MaxSnapr.Library;
using MaxSnapr.Native;
using MaxSnapr.Services;

namespace MaxSnapr.Capture;

/// <summary>One recorded step of a procedure.</summary>
public sealed class RecordedStep
{
    public required BitmapSource Image { get; init; }
    public Point Click { get; init; }       // in image coordinates, or NaN for typing steps
    public string Description { get; set; } = "";
}

/// <summary>
/// Step capture: every click captures the window under the cursor (before the click takes effect) and
/// describes it from UI Automation ("Haga clic en «Guardar»"); typing between clicks becomes its own step.
/// Stop builds an editable step-by-step guide in the editor.
/// </summary>
public sealed class StepCaptureSession
{
    static StepCaptureSession? _active;
    public static bool IsActive => _active != null;

    readonly List<RecordedStep> _steps = new();
    readonly StringBuilder _typed = new();
    volatile bool _passwordRun;
    Video.MouseHook? _mouse;
    KeyboardHook? _keys;
    Window? _hud;
    TextBlock? _count;
    bool _paused;

    public static void Start()
    {
        if (_active != null) { _ = _active.StopAsync(); return; }
        _active = new StepCaptureSession();
        _active.Begin();
    }

    void Begin()
    {
        _mouse = new Video.MouseHook();
        _mouse.ButtonDown += (p, right) => { if (!_paused && !OverHud(p)) OnClick(p, right); };
        _keys = new KeyboardHook();
        _keys.Typed += c =>
        {
            if (_paused) return;
            if (_typed.Length == 0)
            {
                // Never record what is typed into password boxes.
                _passwordRun = false;
                Task.Run(() => { try { _passwordRun = System.Windows.Automation.AutomationElement.FocusedElement?.Current.IsPassword == true; } catch { } });
            }
            _typed.Append(c);
        };
        _keys.Special += k => { if (!_paused && k == "Intro" && _typed.Length > 0) FlushTyping(" y pulse Intro"); };
        ShowHud();
        App.Current.Notify("Captura de pasos", "Haga clic por la aplicación como de costumbre. Pulse Detener al terminar.");
    }

    bool OverHud(System.Drawing.Point p)
    {
        if (_hud == null) return false;
        var h = new System.Windows.Interop.WindowInteropHelper(_hud).Handle;
        NativeMethods.GetWindowRect(h, out var r);
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    void OnClick(System.Drawing.Point p, bool right)
    {
        FlushTyping("");
        // Capture immediately, inside the hook, so the screen still shows the state before the click.
        var pt = new NativeMethods.POINT { X = p.X, Y = p.Y };
        var hwnd = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(pt), 2 /*GA_ROOT*/);
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT wr, Marshal.SizeOf<NativeMethods.RECT>()) != 0)
            NativeMethods.GetWindowRect(hwnd, out wr);
        var vs = ScreenCapturer.VirtualScreen;
        var rect = ScreenSnapshot.Intersect(new Int32Rect(wr.Left, wr.Top, wr.Width, wr.Height), vs);
        if (rect.Width < 20 || rect.Height < 20) return;
        BitmapSource img;
        try { img = ScreenCapturer.CaptureRect(rect); } catch { return; }
        var click = new Point(p.X - rect.X, p.Y - rect.Y);
        var step = new RecordedStep { Image = img, Click = click, Description = right ? "Haga clic con el botón derecho" : "Haga clic" };
        _steps.Add(step);
        UpdateHud();
        // UI Automation lookups can be slow, so describe the element off the hook thread.
        Task.Run(() =>
        {
            var d = Describe(p, right);
            Application.Current.Dispatcher.BeginInvoke(() => step.Description = d);
        });
    }

    void FlushTyping(string suffix)
    {
        if (_typed.Length == 0) return;
        var text = _typed.ToString();
        _typed.Clear();
        if (_passwordRun) { text = ""; suffix = ""; }
        var last = _steps.LastOrDefault();
        if (last == null) return;
        _steps.Add(new RecordedStep { Image = last.Image, Click = new Point(double.NaN, double.NaN), Description = text.Length == 0 ? "Escriba la contraseña" : $"Escriba «{text}»{suffix}" });
        UpdateHud();
    }

    static string Describe(System.Drawing.Point p, bool right)
    {
        string verb = right ? "Haga clic con el botón derecho en" : "Haga clic en";
        try
        {
            var el = System.Windows.Automation.AutomationElement.FromPoint(new Point(p.X, p.Y));
            var name = el.Current.Name?.Trim();
            var type = el.Current.LocalizedControlType?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                var parent = System.Windows.Automation.TreeWalker.ControlViewWalker.GetParent(el);
                name = parent?.Current.Name?.Trim();
            }
            if (!string.IsNullOrEmpty(name)) return string.IsNullOrEmpty(type) ? $"{verb} «{name}»" : $"{verb} «{name}» ({type})";
            if (!string.IsNullOrEmpty(type)) return $"{verb} el elemento de tipo {type}";
        }
        catch { }
        return verb + " el punto señalado";
    }

    void ShowHud()
    {
        _count = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0), Text = "Pasos: 0" };
        var pause = new Button { Content = "Pausa", Style = (Style)Application.Current.FindResource("OutlineButton"), Margin = new Thickness(4), Padding = new Thickness(10, 4, 10, 4) };
        pause.Click += (_, _) => { _paused = !_paused; pause.Content = _paused ? "Reanudar" : "Pausa"; };
        var undo = new Button { Content = "Quitar el último", Style = (Style)Application.Current.FindResource("OutlineButton"), Margin = new Thickness(4), Padding = new Thickness(10, 4, 10, 4) };
        undo.Click += (_, _) => { if (_steps.Count > 0) { _steps.RemoveAt(_steps.Count - 1); UpdateHud(); } };
        var stop = new Button { Content = "Detener y crear guía", Style = (Style)Application.Current.FindResource("CaptureButton"), Foreground = Brushes.White, Margin = new Thickness(4), Padding = new Thickness(12, 4, 12, 4) };
        stop.Click += (_, _) => _ = StopAsync();
        var cancel = new Button { Content = "Cancelar", Style = (Style)Application.Current.FindResource("FlatButton"), Foreground = Brushes.White, Margin = new Thickness(4) };
        cancel.Click += (_, _) => Cancel();
        _hud = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x20, 0x24)), CornerRadius = new CornerRadius(10), Padding = new Thickness(4),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "\uE7C1", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = Brushes.OrangeRed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) }, _count, pause, undo, stop, cancel } },
            },
        };
        _hud.SourceInitialized += (_, _) => Video.CaptureExclusion.Apply(_hud);
        _hud.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBlock { Parent: Button }) _hud.DragMove(); };
        _hud.Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            _hud.Left = wa.Right - _hud.ActualWidth - 20;
            _hud.Top = wa.Bottom - _hud.ActualHeight - 20;
        };
        _hud.Show();
    }

    void UpdateHud() { if (_count != null) _count.Text = $"Pasos: {_steps.Count}"; }

    void Cleanup()
    {
        _mouse?.Dispose();
        _keys?.Dispose();
        _hud?.Close();
        _active = null;
    }

    void Cancel()
    {
        Cleanup();
        App.Current.Notify("Captura de pasos", "Cancelada");
    }

    async Task StopAsync()
    {
        FlushTyping("");
        Cleanup();
        await Task.Delay(400); // let pending UIA descriptions land
        if (_steps.Count == 0) { App.Current.Notify("Captura de pasos", "No se registró ningún paso."); return; }
        var doc = GuideBuilder.Build(_steps, $"Guía paso a paso · {DateTime.Now:d}");
        var item = LibraryStore.Instance.Add(doc, "Guía paso a paso", "Captura de pasos");
        App.Current.ShowEditor().OpenDocument(doc, item);
    }
}

/// <summary>Lays recorded steps out as one long, fully editable document.</summary>
public static class GuideBuilder
{
    public static EditorDocument Build(IReadOnlyList<RecordedStep> steps, string title)
    {
        const int width = 1100, margin = 50, gap = 36;
        var accent = ToolTheme.Active.Colors.ElementAtOrDefault(4);
        if (accent.A == 0) accent = Color.FromRgb(0x2E, 0x7C, 0xF6);
        // Measure first: each step is a header line plus an optional scaled screenshot.
        var layout = new List<(RecordedStep step, Rect img, double y)>();
        double y = 130;
        foreach (var s in steps)
        {
            double headerY = y;
            y += 64;
            Rect img = Rect.Empty;
            if (!double.IsNaN(s.Click.X))
            {
                double sc = Math.Min(1, (width - margin * 2) / (double)s.Image.PixelWidth);
                img = new Rect(margin, y, s.Image.PixelWidth * sc, s.Image.PixelHeight * sc);
                y += img.Height;
            }
            layout.Add((s, img, headerY));
            y += gap;
        }
        int height = (int)y + 30;
        var background = RasterOps.Render(width, height, dc =>
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(accent), null, new Rect(0, 0, width, 8));
        });
        var doc = new EditorDocument(background);
        var titleStyle = new ToolStyle { TextColor = Color.FromRgb(0x1D, 0x20, 0x25), FontSize = 40, Bold = true, Shadow = false, Thickness = 0 };
        var t = new TextAnnotation { Box = new Rect(margin, 40, width - margin * 2, 60), Text = title, Style = titleStyle };
        doc.Annotations.Add(t);
        int n = 1;
        foreach (var (s, img, hy) in layout)
        {
            var stepStyle = new ToolStyle { Fill = accent, Stroke = accent, TextColor = Colors.White, Size = 40, Shadow = false, Thickness = 0 };
            doc.Annotations.Add(new StepAnnotation { Center = new Point(margin + 20, hy + 24), Number = n, Style = stepStyle });
            var textStyle = new ToolStyle { TextColor = Color.FromRgb(0x1D, 0x20, 0x25), FontSize = 24, Bold = false, Shadow = false, Thickness = 0 };
            doc.Annotations.Add(new TextAnnotation { Box = new Rect(margin + 56, hy + 6, width - margin * 2 - 56, 40), Text = s.Description, Style = textStyle });
            if (!img.IsEmpty)
            {
                var ann = new ImageAnnotation { Box = img, Style = new ToolStyle { Shadow = true, Thickness = 0 } };
                ann.Image = s.Image;
                doc.Annotations.Add(ann);
                double sc = img.Width / s.Image.PixelWidth;
                var c = new Point(img.X + s.Click.X * sc, img.Y + s.Click.Y * sc);
                var ring = new ToolStyle { Stroke = Color.FromRgb(0xE8, 0x3A, 0x3A), Thickness = 4, Shape = ShapeKind.Ellipse, Shadow = false };
                doc.Annotations.Add(new ShapeAnnotation { Box = new Rect(c.X - 22, c.Y - 22, 44, 44), Style = ring });
            }
            n++;
        }
        doc.IsDirty = true;
        return doc;
    }
}

/// <summary>Low-level keyboard hook that reports typed characters (respecting layout) and a few special keys.</summary>
public sealed class KeyboardHook : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public int vkCode, scanCode, flags, time; public IntPtr extra; }
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern int ToUnicode(uint vk, uint scan, byte[] state, StringBuilder buf, int size, uint flags);
    [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);

    readonly HookProc _proc;
    IntPtr _hook;
    public event Action<char>? Typed;
    public event Action<string>? Special;

    public KeyboardHook()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _proc, GetModuleHandle(null), 0);
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam.ToInt32() == 0x0100 || wParam.ToInt32() == 0x0104))
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (k.vkCode == 0x0D) Special?.Invoke("Intro");
            else if (k.vkCode == 0x09) Special?.Invoke("Tab");
            else
            {
                var state = new byte[256];
                GetKeyboardState(state);
                // Low-level hooks do not see modifier state in GetKeyboardState, so add it explicitly.
                if (GetKeyState(0x10) < 0) state[0x10] = 0x80;
                if (GetKeyState(0x14) % 2 != 0) state[0x14] = 0x01;
                bool ctrl = GetKeyState(0x11) < 0, alt = GetKeyState(0x12) < 0;
                if (!ctrl || alt)
                {
                    var sb = new StringBuilder(4);
                    if (ToUnicode((uint)k.vkCode, (uint)k.scanCode, state, sb, sb.Capacity, 0x4) == 1 && !char.IsControl(sb[0])) Typed?.Invoke(sb[0]);
                }
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
