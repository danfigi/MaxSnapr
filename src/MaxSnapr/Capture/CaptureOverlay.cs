using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static MaxSnapr.Native.NativeMethods;

namespace MaxSnapr.Capture;

public enum CaptureAction { Image, GrabText, Copy, Video, Scroll, Panoramic, PickColor }

public enum OverlayMode { Standard, Window, FixedSize, Freehand, MultiArea, Object, PickColor }

public sealed record CaptureSelection(Int32Rect Rect, WindowInfo? Window, CaptureAction Action)
{
    /// <summary>Freehand outline (physical, virtual-screen relative) or null.</summary>
    public IReadOnlyList<Point>? Lasso { get; init; }
    /// <summary>Multi-area rectangles, or null.</summary>
    public IReadOnlyList<Int32Rect>? Areas { get; init; }
}

/// <summary>
/// Region/window/screen selector shown over every monitor. Coordinates are physical pixels relative to
/// the virtual screen; each monitor gets its own overlay window so mixed-DPI setups render crisply.
/// </summary>
public sealed class CaptureOverlay
{
    readonly ScreenSnapshot _snap;
    readonly bool _confirmImmediately;
    readonly List<OverlayWindow> _windows = new();
    readonly TaskCompletionSource<CaptureSelection?> _tcs = new();
    readonly OverlayMode _mode;
    readonly Size _fixed;
    readonly ElementFinder? _elements;

    internal Int32Rect? Selection;
    internal WindowInfo? HoverWindow;
    internal Int32Rect? HoverElement;
    internal Point? Cursor;
    internal bool Dragging;
    internal bool Adjusting;
    internal Point DragStart;
    internal string ModeLabel;
    internal readonly List<Point> Lasso = new();
    internal readonly List<Int32Rect> Areas = new();

    public CaptureOverlay(ScreenSnapshot snap, bool confirmImmediately, string modeLabel, OverlayMode mode = OverlayMode.Standard, Size fixedSize = default)
    {
        _snap = snap;
        _confirmImmediately = confirmImmediately;
        ModeLabel = modeLabel;
        _mode = mode;
        _fixed = fixedSize;
        if (mode == OverlayMode.Object) _elements = new ElementFinder(snap);
    }

    internal ScreenSnapshot Snapshot => _snap;
    internal OverlayMode Mode => _mode;
    internal Size FixedSize => _fixed;

    public Task<CaptureSelection?> RunAsync()
    {
        foreach (var m in _snap.Monitors)
        {
            var w = new OverlayWindow(this, m);
            _windows.Add(w);
            w.Show();
        }
        var cursorMon = _windows.FirstOrDefault(w => Contains(w.Monitor.Bounds, CurrentCursor())) ?? _windows.First();
        cursorMon.Activate();
        cursorMon.Focus();
        Cursor = CurrentCursor();
        UpdateHover();
        Invalidate();
        return _tcs.Task;
    }

    internal Point CurrentCursor()
    {
        GetCursorPos(out var p);
        var vs = _snap.Bounds;
        return new Point(p.X - vs.X, p.Y - vs.Y);
    }

    internal void Invalidate() { foreach (var w in _windows) w.Surface.InvalidateVisual(); }

    internal void UpdateHover()
    {
        if (Cursor is not Point c || Dragging || Adjusting) return;
        if (_mode is OverlayMode.FixedSize or OverlayMode.Freehand or OverlayMode.PickColor) { HoverWindow = null; return; }
        HoverWindow = _snap.Windows.FirstOrDefault(w => Contains(w.Bounds, c));
        if (HoverWindow != null) HoverWindow = HoverWindow with { Bounds = ScreenSnapshot.Intersect(HoverWindow.Bounds, ImageRect) };
        if (_elements != null)
        {
            HoverElement = _elements.Find(c, HoverWindow);
            if (_elements.Pending) _ = _elements.WaitAsync().ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => { UpdateHover(); Invalidate(); }));
        }
    }

    internal Int32Rect ImageRect => new(0, 0, _snap.Image.PixelWidth, _snap.Image.PixelHeight);

    internal Int32Rect FixedRectAt(Point c)
    {
        int w = (int)_fixed.Width, h = (int)_fixed.Height;
        int x = (int)Math.Clamp(c.X - w / 2.0, 0, Math.Max(0, _snap.Image.PixelWidth - w));
        int y = (int)Math.Clamp(c.Y - h / 2.0, 0, Math.Max(0, _snap.Image.PixelHeight - h));
        return new Int32Rect(x, y, w, h);
    }

    internal static bool Contains(Int32Rect r, Point p) => p.X >= r.X && p.Y >= r.Y && p.X < r.X + r.Width && p.Y < r.Y + r.Height;

    internal void Finish(CaptureAction action)
    {
        if (_mode == OverlayMode.MultiArea && Areas.Count > 0)
        {
            var all = Areas.Aggregate(Union);
            Complete(new CaptureSelection(all, null, action) { Areas = Areas.ToList() });
            return;
        }
        Int32Rect? rect = Selection;
        WindowInfo? win = null;
        if (rect == null && HoverElement != null) rect = HoverElement;
        if (rect == null && HoverWindow != null) { rect = HoverWindow.Bounds; win = HoverWindow; }
        if (rect is not Int32Rect r || r.Width < 2 || r.Height < 2) return;
        Complete(new CaptureSelection(r, win ?? HoverWindow, action));
    }

    internal void FinishLasso()
    {
        if (Lasso.Count < 3) return;
        double x1 = Lasso.Min(p => p.X), y1 = Lasso.Min(p => p.Y), x2 = Lasso.Max(p => p.X), y2 = Lasso.Max(p => p.Y);
        var r = ScreenSnapshot.Intersect(new Int32Rect((int)x1, (int)y1, (int)Math.Ceiling(x2 - x1), (int)Math.Ceiling(y2 - y1)), ImageRect);
        if (r.Width < 4 || r.Height < 4) { Lasso.Clear(); Invalidate(); return; }
        Complete(new CaptureSelection(r, null, CaptureAction.Image) { Lasso = Lasso.ToList() });
    }

    static Int32Rect Union(Int32Rect a, Int32Rect b)
    {
        int x1 = Math.Min(a.X, b.X), y1 = Math.Min(a.Y, b.Y);
        int x2 = Math.Max(a.X + a.Width, b.X + b.Width), y2 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new Int32Rect(x1, y1, x2 - x1, y2 - y1);
    }

    internal void FinishMonitor(MonitorInfo m) => Complete(new CaptureSelection(m.Bounds, null, CaptureAction.Image));

    void Complete(CaptureSelection s)
    {
        Close();
        _tcs.TrySetResult(s);
    }

    internal void Cancel()
    {
        Close();
        _tcs.TrySetResult(null);
    }

    internal void CommitSelection()
    {
        if (_confirmImmediately) Finish(CaptureAction.Image);
        else { Adjusting = true; Invalidate(); foreach (var w in _windows) w.UpdateActionBar(); }
    }

    /// <summary>Lays out the selector for one monitor off-screen so the self-test can render it.</summary>
    internal FrameworkElement BuildPreview(MonitorInfo m, Int32Rect? selection, bool adjusting, Point cursor)
    {
        Selection = selection;
        Adjusting = adjusting;
        Cursor = cursor;
        UpdateHover();
        var w = new OverlayWindow(this, m);
        var content = (FrameworkElement)w.Content;
        w.Content = null;
        var size = new Size(m.Bounds.Width, m.Bounds.Height);
        content.Measure(size);
        content.Arrange(new Rect(size));
        w.UpdateActionBar();
        content.UpdateLayout();
        return content;
    }

    void Close()
    {
        foreach (var w in _windows) w.Close();
        _windows.Clear();
    }

    // ---------------------------------------------------------------------------------------------

    internal sealed class OverlayWindow : Window
    {
        readonly CaptureOverlay _owner;
        public MonitorInfo Monitor { get; }
        public OverlaySurface Surface { get; }
        readonly Canvas _root = new();
        readonly Border _actionBar;

        public OverlayWindow(CaptureOverlay owner, MonitorInfo monitor)
        {
            _owner = owner;
            Monitor = monitor;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            Left = (owner._snap.Bounds.X + monitor.Bounds.X) / monitor.Scale;
            Top = (owner._snap.Bounds.Y + monitor.Bounds.Y) / monitor.Scale;
            Width = monitor.Bounds.Width / monitor.Scale;
            Height = monitor.Bounds.Height / monitor.Scale;
            Surface = new OverlaySurface(owner, this);
            _actionBar = BuildActionBar();
            _actionBar.Visibility = Visibility.Collapsed;
            var grid = new Grid();
            grid.Children.Add(Surface);
            grid.Children.Add(_root);
            _root.Children.Add(_actionBar);
            Content = grid;
            Cursor = owner._mode == OverlayMode.PickColor ? Cursors.Pen : Cursors.Cross;
            SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                var vs = owner._snap.Bounds;
                SetWindowPos(h, HWND_TOPMOST, vs.X + monitor.Bounds.X, vs.Y + monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height, SWP_SHOWWINDOW);
            };
            KeyDown += OnKey;
        }

        public double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

        public Point ToPhysical(Point p) => new(Monitor.Bounds.X + p.X * Scale, Monitor.Bounds.Y + p.Y * Scale);
        public Point ToLocal(Point phys) => new((phys.X - Monitor.Bounds.X) / Scale, (phys.Y - Monitor.Bounds.Y) / Scale);
        public Rect ToLocal(Int32Rect r) => new(ToLocal(new Point(r.X, r.Y)), new Size(r.Width / Scale, r.Height / Scale));

        Border BuildActionBar()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(ActionButton("\uE722", "Capturar imagen (Intro)", () => _owner.Finish(CaptureAction.Image)));
            panel.Children.Add(ActionButton("\uE8C8", "Copiar al portapapeles (Ctrl+C)", () => _owner.Finish(CaptureAction.Copy)));
            panel.Children.Add(ActionButton("\uE8D2", "Extraer texto (OCR)", () => _owner.Finish(CaptureAction.GrabText)));
            panel.Children.Add(ActionButton("\uE714", "Grabar vídeo de esta zona", () => _owner.Finish(CaptureAction.Video)));
            panel.Children.Add(ActionButton("\uE74B", "Captura con desplazamiento automático", () => _owner.Finish(CaptureAction.Scroll)));
            panel.Children.Add(ActionButton("\uE80A", "Panorámica: desplácese usted mismo", () => _owner.Finish(CaptureAction.Panoramic)));
            panel.Children.Add(ActionButton("\uE711", "Cancelar (Esc)", () => _owner.Cancel()));
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x2B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x44, 0x4A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                Child = panel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, Opacity = 0.5, ShadowDepth = 2 },
            };
        }

        static Button ActionButton(string glyph, string tip, Action click)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18, Foreground = Brushes.White },
                ToolTip = tip, Width = 40, Height = 36, Margin = new Thickness(2),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            };
            b.Click += (_, _) => click();
            return b;
        }

        public void UpdateActionBar()
        {
            if (!_owner.Adjusting || _owner.Selection is not Int32Rect sel) { _actionBar.Visibility = Visibility.Collapsed; return; }
            var local = ToLocal(sel);
            double aw = Surface.ActualWidth, ah = Surface.ActualHeight;
            var mine = new Rect(0, 0, aw, ah);
            if (!mine.IntersectsWith(local)) { _actionBar.Visibility = Visibility.Collapsed; return; }
            _actionBar.Visibility = Visibility.Visible;
            _actionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var sz = _actionBar.DesiredSize;
            double x = Math.Clamp(local.Right - sz.Width, 4, Math.Max(4, aw - sz.Width - 4));
            double y = local.Bottom + 8;
            if (y + sz.Height > ah - 4) y = Math.Max(4, local.Top - sz.Height - 8);
            if (y < 4) y = Math.Max(4, local.Bottom - sz.Height - 8);
            Canvas.SetLeft(_actionBar, x);
            Canvas.SetTop(_actionBar, y);
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    if (_owner.Adjusting) { _owner.Adjusting = false; _owner.Selection = null; _owner.UpdateHover(); _owner.Invalidate(); _owner._windows.ForEach(w => w.UpdateActionBar()); }
                    else if (_owner._mode == OverlayMode.MultiArea && _owner.Areas.Count > 0) { _owner.Areas.RemoveAt(_owner.Areas.Count - 1); _owner.Invalidate(); }
                    else _owner.Cancel();
                    break;
                case Key.Enter:
                    if (_owner._mode == OverlayMode.MultiArea) _owner.Finish(CaptureAction.Image);
                    else if (_owner.Selection == null && _owner.HoverWindow == null && _owner.HoverElement == null) _owner.FinishMonitor(Monitor);
                    else _owner.Finish(CaptureAction.Image);
                    break;
                case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                    _owner.Finish(CaptureAction.Copy);
                    break;
                case Key.F:
                    _owner.FinishMonitor(Monitor);
                    break;
                case Key.Left or Key.Right or Key.Up or Key.Down:
                    GetCursorPos(out var p);
                    p.X += e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
                    p.Y += e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                    System.Windows.Forms.Cursor.Position = new System.Drawing.Point(p.X, p.Y);
                    e.Handled = true;
                    break;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------

    internal sealed class OverlaySurface : FrameworkElement
    {
        readonly CaptureOverlay _o;
        readonly OverlayWindow _w;
        readonly BitmapSource _background;
        static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0));
        static readonly Pen AccentPen = new(new SolidColorBrush(Color.FromRgb(0x2E, 0x9B, 0xFF)), 2);
        static readonly Pen ElementPen = new(new SolidColorBrush(Color.FromRgb(0xF2, 0xB0, 0x1E)), 2);
        static readonly Pen GuidePen = new(new SolidColorBrush(Color.FromArgb(0xB0, 0x2E, 0x9B, 0xFF)), 1) { DashStyle = DashStyles.Dash };
        static readonly Brush HandleBrush = Brushes.White;
        static readonly Typeface Ui = new("Segoe UI");
        int _activeHandle = -1;
        Int32Rect _handleStartRect;
        Point _handleStartPoint;

        static OverlaySurface()
        {
            Dim.Freeze(); AccentPen.Freeze(); GuidePen.Freeze(); ElementPen.Freeze();
        }

        public OverlaySurface(CaptureOverlay o, OverlayWindow w)
        {
            _o = o;
            _w = w;
            var b = w.Monitor.Bounds;
            _background = new CroppedBitmap(o._snap.Image, ScreenSnapshot.Intersect(b, o.ImageRect));
            _background.Freeze();
            Focusable = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var full = new Rect(0, 0, ActualWidth, ActualHeight);
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
            dc.DrawImage(_background, full);

            Geometry? hole = null;
            Rect? hi = null;
            string? size = null;
            if (_o._mode == OverlayMode.Freehand && _o.Lasso.Count > 1)
            {
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(_w.ToLocal(_o.Lasso[0]), true, true);
                    gc.PolyLineTo(_o.Lasso.Skip(1).Select(_w.ToLocal).ToList(), true, true);
                }
                hole = g;
            }
            else if (_o._mode == OverlayMode.MultiArea && (_o.Areas.Count > 0 || _o.Selection != null))
            {
                var group = new GeometryGroup();
                foreach (var a in _o.Areas) group.Children.Add(new RectangleGeometry(_w.ToLocal(a)));
                if (_o.Selection is Int32Rect s) group.Children.Add(new RectangleGeometry(_w.ToLocal(s)));
                hole = group;
                if (_o.Selection is Int32Rect cur) size = $"{cur.Width} × {cur.Height}";
            }
            else if (_o._mode == OverlayMode.FixedSize && _o.Cursor is Point fc)
            {
                var r = _o.FixedRectAt(fc);
                hi = _w.ToLocal(r);
                size = $"{r.Width} × {r.Height}";
            }
            else if (_o.Selection is Int32Rect sel) { hi = _w.ToLocal(sel); size = $"{sel.Width} × {sel.Height}"; }
            else if (_o.HoverElement is Int32Rect el) { hi = _w.ToLocal(el); size = $"{el.Width} × {el.Height}"; }
            else if (_o.HoverWindow != null) { hi = _w.ToLocal(_o.HoverWindow.Bounds); size = $"{_o.HoverWindow.Bounds.Width} × {_o.HoverWindow.Bounds.Height}"; }

            if (hi is Rect h) hole = new RectangleGeometry(h);
            var dim = hole != null ? new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), hole) : (Geometry)new RectangleGeometry(full);
            if (_o._mode != OverlayMode.PickColor) dc.DrawGeometry(Dim, null, dim);
            if (hole != null) dc.DrawGeometry(null, _o.HoverElement != null && _o.Selection == null ? ElementPen : AccentPen, hole);

            if (hi is Rect r2)
            {
                if (_o.Adjusting) foreach (var hr in HandleRects(r2)) dc.DrawRectangle(HandleBrush, AccentPen, hr);
                if (size != null) DrawLabel(dc, size, new Point(r2.Left, r2.Top - 26 < 0 ? r2.Top + 4 : r2.Top - 26));
            }

            if (_o.Cursor is Point c && !_o.Adjusting)
            {
                var lc = _w.ToLocal(c);
                if (full.Contains(lc))
                {
                    if (_o._mode != OverlayMode.FixedSize)
                    {
                        dc.DrawLine(GuidePen, new Point(0, lc.Y), new Point(ActualWidth, lc.Y));
                        dc.DrawLine(GuidePen, new Point(lc.X, 0), new Point(lc.X, ActualHeight));
                    }
                    DrawLoupe(dc, c, lc);
                }
            }

            if (_o.Selection == null && !_o.Dragging && _o.Lasso.Count == 0)
                DrawLabel(dc, Instructions(), new Point(16, 16));
        }

        string Instructions() => _o._mode switch
        {
            OverlayMode.FixedSize => $"{_o.ModeLabel} · Mueva el marco de {_o._fixed.Width}×{_o._fixed.Height} y haga clic para capturar · Esc para cancelar",
            OverlayMode.Freehand => $"{_o.ModeLabel} · Dibuje el contorno manteniendo pulsado el botón · Esc para cancelar",
            OverlayMode.MultiArea => $"{_o.ModeLabel} · Arrastre para añadir áreas ({_o.Areas.Count}) · Intro para capturar · Esc deshace la última",
            OverlayMode.Object => $"{_o.ModeLabel} · Señale un elemento de la interfaz y haga clic · Esc para cancelar",
            OverlayMode.PickColor => "Cuentagotas · Haga clic en el color que quiera tomar · Esc para cancelar",
            _ => $"{_o.ModeLabel} · Arrastre para seleccionar una región, clic para una ventana, Intro o F para toda la pantalla, Esc para cancelar",
        };

        void DrawLoupe(DrawingContext dc, Point phys, Point local)
        {
            const int px = 15;
            const double size = 132;
            var img = _o._snap.Image;
            int sx = (int)phys.X - px / 2, sy = (int)phys.Y - px / 2;
            var src = ScreenSnapshot.Intersect(new Int32Rect(sx, sy, px, px), _o.ImageRect);
            if (src.Width <= 0 || src.Height <= 0) return;
            double x = local.X + 24, y = local.Y + 24;
            if (x + size > ActualWidth) x = local.X - size - 24;
            if (y + size + 40 > ActualHeight) y = local.Y - size - 64;
            var box = new Rect(x, y, size, size);
            dc.PushClip(new RectangleGeometry(box, 8, 8));
            dc.DrawRectangle(Brushes.Black, null, box);
            double cell = size / px;
            dc.DrawImage(new CroppedBitmap(img, src), new Rect(x + (src.X - sx) * cell, y + (src.Y - sy) * cell, src.Width * cell, src.Height * cell));
            // Light and dark outlines keep the loupe readable over any content.
            var mid = new Rect(x + (px / 2) * cell, y + (px / 2) * cell, cell, cell);
            dc.DrawRectangle(null, new Pen(Brushes.Black, 3), mid);
            dc.DrawRectangle(null, new Pen(Brushes.White, 1.2), mid);
            dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 4), box, 8, 8);
            dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 2), box, 8, 8);
            var color = PixelAt(img, (int)phys.X, (int)phys.Y);
            DrawLabel(dc, $"X {(int)phys.X}  Y {(int)phys.Y}   #{color.R:X2}{color.G:X2}{color.B:X2}", new Point(x, y + size + 6));
        }

        internal static Color PixelAt(BitmapSource img, int x, int y)
        {
            if (x < 0 || y < 0 || x >= img.PixelWidth || y >= img.PixelHeight) return Colors.Black;
            var buf = new byte[4];
            img.CopyPixels(new Int32Rect(x, y, 1, 1), buf, 4, 0);
            return Color.FromRgb(buf[2], buf[1], buf[0]);
        }

        void DrawLabel(DrawingContext dc, string text, Point at)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Ui, 12.5, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var bg = new Rect(at.X, at.Y, ft.Width + 16, ft.Height + 8);
            if (bg.Right > ActualWidth) bg.X = Math.Max(0, ActualWidth - bg.Width - 4);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xE0, 0x1E, 0x21, 0x24)), null, bg, 5, 5);
            dc.DrawText(ft, new Point(bg.X + 8, bg.Y + 4));
        }

        static IEnumerable<Rect> HandleRects(Rect r)
        {
            const double s = 8;
            foreach (var p in HandlePoints(r)) yield return new Rect(p.X - s / 2, p.Y - s / 2, s, s);
        }

        static Point[] HandlePoints(Rect r) => new[]
        {
            r.TopLeft, new Point(r.Left + r.Width / 2, r.Top), r.TopRight, new Point(r.Right, r.Top + r.Height / 2),
            r.BottomRight, new Point(r.Left + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.Left, r.Top + r.Height / 2),
        };

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var phys = _w.ToPhysical(e.GetPosition(this));
            _o.Cursor = phys;
            if (_o._mode == OverlayMode.Freehand && e.LeftButton == MouseButtonState.Pressed && _o.Dragging)
            {
                if (_o.Lasso.Count == 0 || (_o.Lasso[^1] - phys).Length > 2) _o.Lasso.Add(phys);
            }
            else if (_activeHandle >= 0 && e.LeftButton == MouseButtonState.Pressed) ResizeSelection(phys);
            else if (_o.Dragging && e.LeftButton == MouseButtonState.Pressed)
            {
                var a = _o.DragStart;
                if (Math.Abs(phys.X - a.X) >= 3 || Math.Abs(phys.Y - a.Y) >= 3 || _o.Selection != null)
                    _o.Selection = FromPoints(a, phys);
            }
            else if (_o.Adjusting && _o.Selection is Int32Rect sel)
            {
                int h = HitHandle(_w.ToLocal(sel), e.GetPosition(this));
                Cursor = h switch { 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, 3 or 7 => Cursors.SizeWE, 8 => Cursors.SizeAll, _ => Cursors.Cross };
            }
            _o.UpdateHover();
            _o.Invalidate();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            var local = e.GetPosition(this);
            var phys = _w.ToPhysical(local);
            switch (_o._mode)
            {
                case OverlayMode.PickColor:
                    _o.Complete(new CaptureSelection(new Int32Rect((int)phys.X, (int)phys.Y, 1, 1), null, CaptureAction.PickColor));
                    return;
                case OverlayMode.FixedSize:
                    _o.Complete(new CaptureSelection(_o.FixedRectAt(phys), null, CaptureAction.Image));
                    return;
                case OverlayMode.Object when _o.HoverElement is Int32Rect el:
                    _o.Complete(new CaptureSelection(el, _o.HoverWindow, CaptureAction.Image));
                    return;
                case OverlayMode.Freehand:
                    _o.Lasso.Clear();
                    _o.Lasso.Add(phys);
                    _o.Dragging = true;
                    CaptureMouse();
                    return;
            }
            if (_o.Adjusting && _o.Selection is Int32Rect sel)
            {
                int h = HitHandle(_w.ToLocal(sel), local);
                if (h >= 0)
                {
                    _activeHandle = h;
                    _handleStartRect = sel;
                    _handleStartPoint = phys;
                    CaptureMouse();
                    return;
                }
                _o.Adjusting = false;
                _o.Selection = null;
                foreach (var w in _o._windows) w.UpdateActionBar();
            }
            if (e.ClickCount == 2 && _o.HoverWindow != null && _o._mode == OverlayMode.Standard) { _o.Finish(CaptureAction.Image); return; }
            _o.Dragging = true;
            _o.DragStart = phys;
            CaptureMouse();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();
            if (_o._mode == OverlayMode.Freehand) { _o.Dragging = false; _o.FinishLasso(); return; }
            if (_activeHandle >= 0) { _activeHandle = -1; foreach (var w in _o._windows) w.UpdateActionBar(); return; }
            if (!_o.Dragging) return;
            _o.Dragging = false;
            if (_o._mode == OverlayMode.MultiArea)
            {
                if (_o.Selection is Int32Rect a && a.Width > 2 && a.Height > 2) _o.Areas.Add(a);
                else if (_o.HoverWindow != null) _o.Areas.Add(_o.HoverWindow.Bounds);
                _o.Selection = null;
                _o.Invalidate();
                return;
            }
            if (_o.Selection is Int32Rect s && s.Width > 2 && s.Height > 2) _o.CommitSelection();
            else if (_o.HoverWindow != null)
            {
                _o.Selection = _o.HoverWindow.Bounds;
                var win = _o.HoverWindow;
                _o.CommitSelection();
                _o.HoverWindow = win;
            }
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) => _o.Cancel();

        void ResizeSelection(Point p)
        {
            var r = _handleStartRect;
            double dx = p.X - _handleStartPoint.X, dy = p.Y - _handleStartPoint.Y;
            double l = r.X, t = r.Y, ri = r.X + r.Width, b = r.Y + r.Height;
            switch (_activeHandle)
            {
                case 0: l += dx; t += dy; break;
                case 1: t += dy; break;
                case 2: ri += dx; t += dy; break;
                case 3: ri += dx; break;
                case 4: ri += dx; b += dy; break;
                case 5: b += dy; break;
                case 6: l += dx; b += dy; break;
                case 7: l += dx; break;
                case 8: l += dx; ri += dx; t += dy; b += dy; break;
            }
            _o.Selection = FromPoints(new Point(l, t), new Point(ri, b));
            foreach (var w in _o._windows) w.UpdateActionBar();
        }

        static int HitHandle(Rect r, Point p)
        {
            var pts = HandlePoints(r);
            for (int i = 0; i < pts.Length; i++)
                if (Math.Abs(pts[i].X - p.X) <= 7 && Math.Abs(pts[i].Y - p.Y) <= 7) return i;
            return r.Contains(p) ? 8 : -1;
        }

        static Int32Rect FromPoints(Point a, Point b)
        {
            int x1 = (int)Math.Round(Math.Min(a.X, b.X)), y1 = (int)Math.Round(Math.Min(a.Y, b.Y));
            int x2 = (int)Math.Round(Math.Max(a.X, b.X)), y2 = (int)Math.Round(Math.Max(a.Y, b.Y));
            return new Int32Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
        }
    }
}

/// <summary>
/// UI Automation element rectangles of the window under the cursor, collected once per window on a
/// background thread (the overlay itself would otherwise be the element under the cursor).
/// </summary>
internal sealed class ElementFinder
{
    readonly ScreenSnapshot _snap;
    readonly Dictionary<IntPtr, List<Int32Rect>> _cache = new();
    Task? _loading;
    IntPtr _loadingFor;

    public ElementFinder(ScreenSnapshot snap) { _snap = snap; }

    public bool Pending => _loading != null && !_loading.IsCompleted;
    public Task WaitAsync() => _loading ?? Task.CompletedTask;

    public Int32Rect? Find(Point p, WindowInfo? window)
    {
        if (window == null) return null;
        List<Int32Rect>? rects;
        lock (_cache) _cache.TryGetValue(window.Handle, out rects);
        if (rects == null)
        {
            if (_loadingFor != window.Handle || _loading == null)
            {
                _loadingFor = window.Handle;
                var h = window.Handle;
                var vs = _snap.Bounds;
                _loading = Task.Run(() =>
                {
                    var list = Collect(h, vs);
                    lock (_cache) _cache[h] = list;
                });
            }
            return null;
        }
        // Smallest element containing the point.
        Int32Rect? best = null;
        long bestArea = long.MaxValue;
        foreach (var r in rects)
        {
            if (!CaptureOverlay.Contains(r, p)) continue;
            long a = (long)r.Width * r.Height;
            if (a < bestArea) { bestArea = a; best = r; }
        }
        return best;
    }

    static List<Int32Rect> Collect(IntPtr hwnd, Int32Rect vs)
    {
        var list = new List<Int32Rect>();
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            var walker = System.Windows.Automation.TreeWalker.ControlViewWalker;
            var stack = new Stack<(System.Windows.Automation.AutomationElement el, int depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0 && list.Count < 4000)
            {
                var (el, depth) = stack.Pop();
                try
                {
                    var r = el.Current.BoundingRectangle;
                    if (!r.IsEmpty && r.Width >= 6 && r.Height >= 6)
                        list.Add(new Int32Rect((int)r.X - vs.X, (int)r.Y - vs.Y, (int)r.Width, (int)r.Height));
                    if (depth > 30) continue;
                    var child = walker.GetFirstChild(el);
                    while (child != null) { stack.Push((child, depth + 1)); child = walker.GetNextSibling(child); }
                }
                catch { /* elements can vanish while walking */ }
            }
        }
        catch (Exception ex) { Services.Log.Error("UI Automation", ex); }
        return list;
    }
}
