using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxSnapr.Editor.Model;
using MaxSnapr.Editor.Raster;

namespace MaxSnapr.Editor.Tools;

public enum ToolKind
{
    Select, Arrow, Line, Text, Callout, Shape, Step, Pen, Highlighter, Blur, Magnify, Spotlight, Stamp, Favorites,
    Crop, CutOut, Selection, Fill, Eraser, MagicWand,
}

public interface ITool
{
    ToolKind Kind { get; }
    Cursor Cursor { get; }
    bool ShowsSelection { get; }
    void Attach(EditorCanvas canvas);
    void Detach();
    void Reset();
    void OnDown(Point p, ModifierKeys mods, int clicks);
    void OnMove(Point p, ModifierKeys mods, bool pressed);
    void OnUp(Point p, ModifierKeys mods);
    bool OnKey(Key key, ModifierKeys mods);
    void RenderDocOverlay(DrawingContext dc);
    void RenderScreenOverlay(DrawingContext dc);
}

public abstract class ToolBase : ITool
{
    protected EditorCanvas Canvas = null!;
    protected EditorDocument Doc => Canvas.Document!;
    public abstract ToolKind Kind { get; }
    public virtual Cursor Cursor => Cursors.Cross;
    public virtual bool ShowsSelection => true;
    public ToolStyle Style { get; set; } = new();

    public virtual void Attach(EditorCanvas canvas) => Canvas = canvas;
    public virtual void Detach() { }
    public virtual void Reset() { }
    public abstract void OnDown(Point p, ModifierKeys mods, int clicks);
    public abstract void OnMove(Point p, ModifierKeys mods, bool pressed);
    public abstract void OnUp(Point p, ModifierKeys mods);
    public virtual bool OnKey(Key key, ModifierKeys mods) => false;
    public virtual void RenderDocOverlay(DrawingContext dc) { }
    public virtual void RenderScreenOverlay(DrawingContext dc) { }

    protected static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)));
    protected static readonly Pen AccentPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2E, 0x9B, 0xFF)), 1.5));
    protected static readonly Pen MarchPen = Frozen(new Pen(Brushes.White, 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) });
    protected static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected Rect ScreenRect(Rect doc) => new(Canvas.ToLocal(doc.TopLeft), Canvas.ToLocal(doc.BottomRight));
    protected Int32Rect PixelRect(Rect r) => ScreenSnapshotRect(r, Doc.Width, Doc.Height);

    public static Int32Rect ScreenSnapshotRect(Rect r, int w, int h)
    {
        int x1 = (int)Math.Floor(Math.Max(0, r.Left)), y1 = (int)Math.Floor(Math.Max(0, r.Top));
        int x2 = (int)Math.Ceiling(Math.Min(w, r.Right)), y2 = (int)Math.Ceiling(Math.Min(h, r.Bottom));
        return new Int32Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }
}

/// <summary>Move / select / resize objects. Every drawing tool falls back to this behaviour when clicking an object.</summary>
public class SelectTool : ToolBase
{
    public override ToolKind Kind => ToolKind.Select;
    public override Cursor Cursor => Cursors.Arrow;

    enum Mode { None, Move, Handle, Band }
    Mode _mode;
    Point _start, _last;
    int _handle = -1;
    Annotation? _handleObj;
    bool _checkpointed;
    Rect _band;

    /// <summary>Returns true when the press was consumed by an existing object.</summary>
    protected bool TrySelectBehaviour(Point p, ModifierKeys mods, int clicks)
    {
        var h = Canvas.HitHandle(p);
        if (h != null)
        {
            _mode = Mode.Handle; _handleObj = h.Value.obj; _handle = h.Value.handle; _start = _last = p; _checkpointed = false;
            return true;
        }
        var hit = Canvas.HitObject(p);
        if (hit == null) return false;
        if (clicks == 2 && hit is TextAnnotation t) { Canvas.Select(t); Canvas.BeginTextEdit(t); _mode = Mode.None; return true; }
        bool add = mods.HasFlag(ModifierKeys.Shift) || mods.HasFlag(ModifierKeys.Control);
        if (!Canvas.Selection.Contains(hit)) Canvas.Select(hit, add);
        else if (add) { Canvas.Selection.Remove(hit); Canvas.RaiseSelectionChanged(); }
        _mode = Mode.Move; _start = _last = p; _checkpointed = false;
        return true;
    }

    public override void OnDown(Point p, ModifierKeys mods, int clicks)
    {
        if (TrySelectBehaviour(p, mods, clicks)) return;
        if (!mods.HasFlag(ModifierKeys.Shift)) Canvas.Select(null);
        _mode = Mode.Band; _start = p; _band = new Rect(p, p);
    }

    protected bool HandleSelectMove(Point p, ModifierKeys mods)
    {
        switch (_mode)
        {
            case Mode.Move:
                if (!_checkpointed) { Doc.Checkpoint(); _checkpointed = true; }
                var v = p - _last;
                if (mods.HasFlag(ModifierKeys.Shift))
                {
                    // Constrain to the dominant axis relative to the press point.
                    var total = p - _start;
                    var constrained = Math.Abs(total.X) > Math.Abs(total.Y) ? new Point(p.X, _start.Y) : new Point(_start.X, p.Y);
                    v = constrained - _last;
                    _last = constrained;
                }
                else _last = p;
                foreach (var a in Canvas.Selection) a.Offset(v);
                Doc.RaiseChanged();
                return true;
            case Mode.Handle:
                if (!_checkpointed) { Doc.Checkpoint(); _checkpointed = true; }
                _handleObj!.MoveHandle(_handle, p, mods.HasFlag(ModifierKeys.Shift));
                if (_handleObj is TextAnnotation ta) { ta.FitHeight(Canvas.PixelsPerDip); }
                Doc.RaiseChanged();
                return true;
            case Mode.Band:
                _band = new Rect(_start, p);
                Canvas.Invalidate();
                return true;
        }
        return false;
    }

    protected bool HandleSelectUp()
    {
        var m = _mode;
        _mode = Mode.None;
        if (m == Mode.Band)
        {
            if (_band.Width > 2 || _band.Height > 2)
            {
                foreach (var a in Doc.Annotations.Where(a => _band.Contains(a.Bounds) || _band.IntersectsWith(a.Bounds) && a.Bounds.Width > 0))
                    if (!Canvas.Selection.Contains(a)) Canvas.Selection.Add(a);
                Canvas.RaiseSelectionChanged();
            }
            _band = Rect.Empty;
            Canvas.Invalidate();
        }
        if (m == Mode.Handle || m == Mode.Move) Canvas.RaiseSelectionChanged();
        return m != Mode.None;
    }

    public override void OnMove(Point p, ModifierKeys mods, bool pressed)
    {
        if (pressed && HandleSelectMove(p, mods)) return;
        UpdateHoverCursor(p);
    }

    protected void UpdateHoverCursor(Point p)
    {
        if (Canvas.HitHandle(p) != null) Canvas.Cursor = Cursors.SizeAll;
        else if (Canvas.HitObject(p) != null) Canvas.Cursor = Cursors.SizeAll;
        else Canvas.Cursor = Cursor;
    }

    public override void OnUp(Point p, ModifierKeys mods) => HandleSelectUp();

    public override void RenderScreenOverlay(DrawingContext dc)
    {
        if (_mode == Mode.Band && !_band.IsEmpty)
        {
            var r = ScreenRect(_band);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0x2E, 0x9B, 0xFF)), AccentPen, r);
        }
    }

    protected bool IsInteracting => _mode != Mode.None;
}

/// <summary>Draws a new object of one kind; existing objects are still selectable and editable.</summary>
public sealed class CreateTool : SelectTool
{
    readonly ToolKind _kind;
    Annotation? _creating;
    Point _origin;

    public CreateTool(ToolKind kind) { _kind = kind; }
    public override ToolKind Kind => _kind;
    public override Cursor Cursor => _kind == ToolKind.Text ? Cursors.IBeam : Cursors.Cross;

    Annotation Create(Point p)
    {
        var s = Style.Clone();
        Annotation a = _kind switch
        {
            ToolKind.Arrow => new LineAnnotation { Start = p, End = p },
            ToolKind.Line => new LineAnnotation { Start = p, End = p },
            ToolKind.Shape => new ShapeAnnotation { Box = new Rect(p, p) },
            ToolKind.Text => new TextAnnotation { Box = new Rect(p, p) },
            ToolKind.Callout => new TextAnnotation { Box = new Rect(p, p) },
            ToolKind.Step => new StepAnnotation { Center = p, Number = Doc.NextStepNumber() },
            ToolKind.Pen => new PenAnnotation(),
            ToolKind.Highlighter => new PenAnnotation { Highlighter = true },
            ToolKind.Blur => new RedactAnnotation { Box = new Rect(p, p) },
            ToolKind.Magnify => new MagnifyAnnotation { Box = new Rect(p, p) },
            ToolKind.Spotlight => new SpotlightAnnotation { Box = new Rect(p, p) },
            ToolKind.Stamp => new StampAnnotation { Box = new Rect(p, p) },
            _ => throw new InvalidOperationException(),
        };
        a.Style = s;
        if (_kind == ToolKind.Line) { s.StartCap = LineCap.None; s.EndCap = LineCap.None; }
        return a;
    }

    public override void OnDown(Point p, ModifierKeys mods, int clicks)
    {
        // Steps and stamps are placed by clicking, so only grab objects through their handles or with Ctrl.
        bool clickPlaced = _kind is ToolKind.Step;
        if (!clickPlaced || mods.HasFlag(ModifierKeys.Control) || Canvas.HitHandle(p) != null)
            if (TrySelectBehaviour(p, mods, clicks)) return;
        Doc.Checkpoint();
        _origin = p;
        _creating = Create(p);
        Doc.Add(_creating);
        Canvas.Select(_creating);
    }

    public override void OnMove(Point p, ModifierKeys mods, bool pressed)
    {
        if (pressed && _creating != null)
        {
            _creating.Drag(_origin, p, mods.HasFlag(ModifierKeys.Shift));
            Doc.RaiseChanged();
            return;
        }
        if (pressed && HandleSelectMove(p, mods)) return;
        if (!pressed) UpdateHoverCursor(p);
    }

    public override void OnUp(Point p, ModifierKeys mods)
    {
        if (_creating == null) { HandleSelectUp(); return; }
        var a = _creating;
        _creating = null;
        var b = a.Bounds;
        bool tiny = b.Width < 4 && b.Height < 4;
        switch (a)
        {
            case TextAnnotation t:
                if (tiny || t.Box.Width < 30) t.Box = new Rect(t.Box.X, t.Box.Y, Math.Max(t.Box.Width, t.Style.FontSize * 9), Math.Max(t.Box.Height, 10));
                t.EnsureTail();
                t.FitHeight(Canvas.PixelsPerDip);
                Doc.RaiseChanged();
                Canvas.BeginTextEdit(t);
                break;
            case StampAnnotation st when tiny:
                double size = Math.Max(48, st.Style.Size * 1.6);
                bool badge = !StampAnnotation.IsIconGlyph(st.Style.Glyph);
                st.Box = badge ? new Rect(p.X - size * 1.2, p.Y - size * 0.35, size * 2.4, size * 0.7) : new Rect(p.X - size / 2, p.Y - size / 2, size, size);
                Doc.RaiseChanged();
                break;
            case MagnifyAnnotation or SpotlightAnnotation when tiny:
                a.Drag(new Point(p.X - 80, p.Y - 80), new Point(p.X + 80, p.Y + 80), false);
                Doc.RaiseChanged();
                break;
            case StepAnnotation:
                break;
            case LineAnnotation l when (l.End - l.Start).Length < 4:
            case PenAnnotation pa when pa.Points.Count < 2 && !tiny:
            case BoxAnnotation when tiny:
                Doc.Remove(new[] { a });
                Canvas.Select(null);
                break;
        }
        Canvas.RaiseSelectionChanged();
    }
}

/// <summary>Crop: drag or adjust a rectangle, then Enter / double-click / apply button.</summary>
public sealed class CropTool : ToolBase
{
    public override ToolKind Kind => ToolKind.Crop;
    public override bool ShowsSelection => false;
    Rect _rect;
    int _handle = -1;
    Point _start;
    Rect _startRect;
    bool _creating;

    public event EventHandler? StateChanged;
    public Rect CropRect => _rect;

    public override void Attach(EditorCanvas canvas)
    {
        base.Attach(canvas);
        Reset();
    }

    public override void Reset()
    {
        _rect = Canvas?.Document == null ? Rect.Empty : new Rect(0, 0, Doc.Width, Doc.Height);
        Canvas?.Invalidate();
    }

    Point[] Handles(Rect r) => new[]
    {
        r.TopLeft, new Point(r.Left + r.Width / 2, r.Top), r.TopRight, new Point(r.Right, r.Top + r.Height / 2),
        r.BottomRight, new Point(r.Left + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.Left, r.Top + r.Height / 2),
    };

    int Hit(Point p)
    {
        var hs = Handles(_rect);
        double tol = Canvas.HandleTolerance * 1.5;
        for (int i = 0; i < hs.Length; i++) if (Math.Abs(hs[i].X - p.X) <= tol && Math.Abs(hs[i].Y - p.Y) <= tol) return i;
        return _rect.Contains(p) ? 8 : -1;
    }

    public override void OnDown(Point p, ModifierKeys mods, int clicks)
    {
        if (clicks == 2 && _rect.Contains(p)) { Apply(); return; }
        _handle = Hit(p);
        _start = p;
        _startRect = _rect;
        // While the frame still covers the whole image, dragging inside draws a new frame.
        bool full = _rect.Width >= Doc.Width - 0.5 && _rect.Height >= Doc.Height - 0.5;
        _creating = _handle < 0 || (_handle == 8 && full);
        if (_creating) _rect = new Rect(p, p);
    }

    public override void OnMove(Point p, ModifierKeys mods, bool pressed)
    {
        if (!pressed)
        {
            int h = Hit(p);
            Canvas.Cursor = h switch { 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, 3 or 7 => Cursors.SizeWE, 8 => Cursors.SizeAll, _ => Cursors.Cross };
            return;
        }
        var docRect = new Rect(0, 0, Doc.Width, Doc.Height);
        if (_creating) _rect = new Rect(_start, p);
        else
        {
            double l = _startRect.Left, t = _startRect.Top, r = _startRect.Right, b = _startRect.Bottom;
            var d = p - _start;
            switch (_handle)
            {
                case 0: l += d.X; t += d.Y; break;
                case 1: t += d.Y; break;
                case 2: r += d.X; t += d.Y; break;
                case 3: r += d.X; break;
                case 4: r += d.X; b += d.Y; break;
                case 5: b += d.Y; break;
                case 6: l += d.X; b += d.Y; break;
                case 7: l += d.X; break;
                case 8:
                    var off = new Vector(Math.Clamp(d.X, -_startRect.Left, docRect.Right - _startRect.Right), Math.Clamp(d.Y, -_startRect.Top, docRect.Bottom - _startRect.Bottom));
                    l += off.X; r += off.X; t += off.Y; b += off.Y; break;
            }
            _rect = new Rect(new Point(l, t), new Point(r, b));
        }
        _rect = Rect.Intersect(_rect, docRect);
        if (_rect.IsEmpty) _rect = new Rect(p, p);
        StateChanged?.Invoke(this, EventArgs.Empty);
        Canvas.Invalidate();
    }

    public override void OnUp(Point p, ModifierKeys mods)
    {
        _handle = -1;
        if (_creating && (_rect.Width < 3 || _rect.Height < 3)) Reset();
        _creating = false;
    }

    public override bool OnKey(Key key, ModifierKeys mods)
    {
        if (key == Key.Enter) { Apply(); return true; }
        if (key == Key.Escape) { Reset(); return true; }
        return false;
    }

    public void Apply()
    {
        var r = PixelRect(_rect);
        if (r.Width < 1 || r.Height < 1 || (r.Width == Doc.Width && r.Height == Doc.Height)) return;
        Doc.Checkpoint();
        Doc.ReplaceBase(RasterOps.Crop(Doc.Base, r), new Vector(-r.X, -r.Y));
        Reset();
    }

    public void SetAspect(double aspect)
    {
        if (aspect <= 0) { Reset(); return; }
        double w = Doc.Width, h = w / aspect;
        if (h > Doc.Height) { h = Doc.Height; w = h * aspect; }
        _rect = new Rect((Doc.Width - w) / 2, (Doc.Height - h) / 2, w, h);
        Canvas.Invalidate();
    }

    public override void RenderScreenOverlay(DrawingContext dc)
    {
        if (Canvas.Document == null || _rect.IsEmpty) return;
        var img = ScreenRect(new Rect(0, 0, Doc.Width, Doc.Height));
        var r = ScreenRect(_rect);
        dc.DrawGeometry(DimBrush, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(img), new RectangleGeometry(r)));
        dc.DrawRectangle(null, new Pen(Brushes.White, 1.5), r);
        // Rule-of-thirds guides.
        var guide = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)), 1);
        for (int i = 1; i < 3; i++)
        {
            dc.DrawLine(guide, new Point(r.Left + r.Width * i / 3, r.Top), new Point(r.Left + r.Width * i / 3, r.Bottom));
            dc.DrawLine(guide, new Point(r.Left, r.Top + r.Height * i / 3), new Point(r.Right, r.Top + r.Height * i / 3));
        }
        foreach (var h in Handles(r)) dc.DrawRectangle(Brushes.White, AccentPen, new Rect(h.X - 5, h.Y - 5, 10, 10));
        var px = PixelRect(_rect);
        var ft = new FormattedText($"{px.Width} × {px.Height}", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, Brushes.White, Canvas.PixelsPerDip);
        var bg = new Rect(r.Left, Math.Max(0, r.Top - ft.Height - 10), ft.Width + 14, ft.Height + 6);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xD0, 0x20, 0x22, 0x25)), null, bg, 4, 4);
        dc.DrawText(ft, new Point(bg.X + 7, bg.Y + 3));
    }
}

/// <summary>Cut Out: removes a vertical or horizontal band and closes the gap.</summary>
public sealed class CutOutTool : ToolBase
{
    public override ToolKind Kind => ToolKind.CutOut;
    public override bool ShowsSelection => false;
    double _a, _b;
    bool _active;

    public override Cursor Cursor => Style.Vertical ? Cursors.SizeWE : Cursors.SizeNS;

    public override void OnDown(Point p, ModifierKeys mods, int clicks)
    {
        _active = true;
        _a = _b = Style.Vertical ? p.X : p.Y;
    }

    public override void OnMove(Point p, ModifierKeys mods, bool pressed)
    {
        Canvas.Cursor = Cursor;
        if (!_active) return;
        _b = Style.Vertical ? p.X : p.Y;
        Canvas.Invalidate();
    }

    public override void OnUp(Point p, ModifierKeys mods)
    {
        if (!_active) return;
        _active = false;
        int from = (int)Math.Round(Math.Min(_a, _b)), to = (int)Math.Round(Math.Max(_a, _b));
        Canvas.Invalidate();
        if (to - from < 2) return;
        bool vertical = Style.Vertical;
        Doc.Checkpoint();
        var cut = RasterOps.CutOut(Doc.Base, from, to, vertical);
        if (Style.EdgeStyle != "straight")
        {
            // Mark the seam with a subtle jagged edge so readers notice content was removed.
            cut = DrawSeam(cut, from, vertical, Style.EdgeStyle);
        }
        // Objects past the band slide back by the band width.
        foreach (var a in Doc.Annotations)
        {
            var c = vertical ? a.Bounds.Left : a.Bounds.Top;
            if (c >= to) a.Offset(vertical ? new Vector(-(to - from), 0) : new Vector(0, -(to - from)));
        }
        Doc.ReplaceBase(cut);
    }

    static BitmapSource DrawSeam(BitmapSource img, int at, bool vertical, string style)
    {
        return RasterOps.Render(img.PixelWidth, img.PixelHeight, dc =>
        {
            dc.DrawImage(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
            var g = new StreamGeometry();
            int len = vertical ? img.PixelHeight : img.PixelWidth;
            using (var c = g.Open())
            {
                bool first = true;
                for (int i = 0; i <= len; i += 6)
                {
                    double off = style == "wave" ? Math.Sin(i / 10.0) * 4 : (i / 6 % 2 == 0 ? -4 : 4);
                    var pt = vertical ? new Point(at + off, i) : new Point(i, at + off);
                    if (first) { c.BeginFigure(pt, false, false); first = false; } else c.LineTo(pt, true, true);
                }
            }
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x80, 0x80, 0x80)), 2), g);
        }, BitmapScalingMode.NearestNeighbor);
    }

    public override void RenderScreenOverlay(DrawingContext dc)
    {
        if (!_active || Canvas.Document == null) return;
        double lo = Math.Min(_a, _b), hi = Math.Max(_a, _b);
        var band = Style.Vertical ? new Rect(lo, 0, hi - lo, Doc.Height) : new Rect(0, lo, Doc.Width, hi - lo);
        var r = ScreenRect(band);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x70, 0xE8, 0x3A, 0x3A)), new Pen(Brushes.White, 1), r);
    }
}

/// <summary>Pixel selection (rectangle, ellipse or freehand): delete, copy, cut, crop, or drag to lift the pixels into a movable image.</summary>
public sealed class SelectionTool : ToolBase
{
    public override ToolKind Kind => ToolKind.Selection;
    public override bool ShowsSelection => false;
    Rect _sel = Rect.Empty;
    readonly List<Point> _lasso = new();
    Geometry? _shape;
    Point _start;
    bool _dragging;

    public Rect SelectionRect => _sel;
    string Mode => Style.SelectionShape;
    public override void Reset() { _sel = Rect.Empty; _shape = null; _lasso.Clear(); Canvas?.Invalidate(); }

    public override void OnDown(Point p, ModifierKeys mods, int clicks)
    {
        if (!_sel.IsEmpty && (_shape?.FillContains(p) ?? _sel.Contains(p)))
        {
            Lift(smartFill: mods.HasFlag(ModifierKeys.Alt) || Style.EdgeStyle == "smart");
            return;
        }
        _start = p;
        _dragging = true;
        _lasso.Clear();
        _lasso.Add(p);
        _sel = new Rect(p, p);
        _shape = null;
    }

    public override void OnMove(Point p, ModifierKeys mods, bool pressed)
    {
        Canvas.Cursor = !_sel.IsEmpty && (_shape?.FillContains(p) ?? _sel.Contains(p)) ? Cursors.SizeAll : Cursors.Cross;
        if (!_dragging) return;
        var docRect = new Rect(0, 0, Doc.Width, Doc.Height);
        if (Mode == "lasso")
        {
            var c = new Point(Math.Clamp(p.X, 0, Doc.Width), Math.Clamp(p.Y, 0, Doc.Height));
            if ((_lasso[^1] - c).Length > 1.5) _lasso.Add(c);
            _shape = LassoGeometry();
            _sel = _shape.Bounds;
        }
        else
        {
            var end = mods.HasFlag(ModifierKeys.Shift) ? SquareFrom(_start, p) : p;
            _sel = Rect.Intersect(new Rect(_start, end), docRect);
            _shape = _sel.IsEmpty ? null : Mode == "ellipse" ? new EllipseGeometry(_sel) : new RectangleGeometry(_sel);
        }
        Canvas.Invalidate();
    }

    Geometry LassoGeometry()
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(_lasso[0], true, true);
            c.PolyLineTo(_lasso.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    static Point SquareFrom(Point a, Point b)
    {
        double s = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        return new Point(a.X + Math.Sign(b.X - a.X) * s, a.Y + Math.Sign(b.Y - a.Y) * s);
    }

    public override void OnUp(Point p, ModifierKeys mods)
    {
        _dragging = false;
        if (_sel.IsEmpty || _sel.Width < 2 || _sel.Height < 2) Reset();
        Canvas.Invalidate();
    }

    public override bool OnKey(Key key, ModifierKeys mods)
    {
        if (_sel.IsEmpty) return false;
        if (key is Key.Delete or Key.Back) { Erase(); return true; }
        if (key == Key.Escape) { Reset(); return true; }
        if (key == Key.C && mods == ModifierKeys.Control) { CopyPixels(); return true; }
        if (key == Key.X && mods == ModifierKeys.Control) { CopyPixels(); Erase(); return true; }
        return false;
    }

    Geometry Shape => _shape ?? new RectangleGeometry(_sel);

    /// <summary>Selected pixels cut to the selection shape (transparent outside).</summary>
    BitmapSource Piece(BitmapSource source, Int32Rect r)
    {
        var crop = RasterOps.Crop(source, r);
        if (Mode == "rect") return crop;
        var g = Shape.Clone();
        g.Transform = new TranslateTransform(-r.X, -r.Y);
        return RasterOps.Render(r.Width, r.Height, dc => { dc.PushClip(g); dc.DrawImage(crop, new Rect(0, 0, r.Width, r.Height)); dc.Pop(); }, BitmapScalingMode.NearestNeighbor);
    }

    public BitmapSource? CopyPixels()
    {
        var r = PixelRect(_sel);
        if (r.Width < 1 || r.Height < 1) return null;
        var img = Piece(Doc.Flatten(), r);
        Services.ClipboardService.SetImage(img);
        return img;
    }

    public void Erase()
    {
        var r = PixelRect(_sel);
        if (r.Width < 1) return;
        Doc.Checkpoint();
        Doc.ReplaceBase(RasterOps.PaintGeometry(Doc.Base, Shape, null, Brushes.Black, Doc.CanvasColor));
        Reset();
    }

    public void CropToSelection()
    {
        var r = PixelRect(_sel);
        if (r.Width < 1) return;
        Doc.Checkpoint();
        var piece = Piece(Doc.Base, r);
        Doc.ReplaceBase(piece, new Vector(-r.X, -r.Y));
        Reset();
    }

    void Lift(bool smartFill)
    {
        var r = PixelRect(_sel);
        if (r.Width < 1) return;
        Doc.Checkpoint();
        var piece = Piece(Doc.Base, r);
        var hole = smartFill ? RasterOps.FillFromSurroundings(Doc.Base, r)
            : RasterOps.PaintGeometry(Doc.Base, Shape, null, Brushes.Black, Doc.CanvasColor);
        Doc.ReplaceBase(hole);
        var img = new ImageAnnotation { Box = new Rect(r.X, r.Y, r.Width, r.Height), Style = new ToolStyle { Shadow = false, Thickness = 0 } };
        img.Image = piece;
        Doc.Add(img);
        Reset();
        Canvas.Select(img);
        // The lifted pixels are now an object; hand over to the move tool so the drag continues there.
        ToolSwitched?.Invoke(this, ToolKind.Select);
    }

    public static event EventHandler<ToolKind>? ToolSwitched;

    public override void RenderDocOverlay(DrawingContext dc)
    {
        if (_sel.IsEmpty) return;
        var thin = 1 / Canvas.Scale;
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x22, 0x2E, 0x9B, 0xFF)), new Pen(Brushes.Black, thin), Shape);
        dc.DrawGeometry(null, new Pen(Brushes.White, thin) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) }, Shape);
    }
}

public sealed class FillTool : ToolBase
{
    public override ToolKind Kind => ToolKind.Fill;
    public override bool ShowsSelection => false;

    public override void OnDown(Point p, ModifierKeys mods, int clicks)
    {
        int x = (int)p.X, y = (int)p.Y;
        if (x < 0 || y < 0 || x >= Doc.Width || y >= Doc.Height) return;
        Doc.Checkpoint();
        var color = Style.Fill.A > 0 ? Style.Fill : Style.Stroke;
        Doc.ReplaceBase(RasterOps.FloodFill(Doc.Base, x, y, color, Style.Tolerance));
    }

    public override void OnMove(Point p, ModifierKeys mods, bool pressed) { }
    public override void OnUp(Point p, ModifierKeys mods) { }
}

public sealed class EraserTool : ToolBase
{
    public override ToolKind Kind => ToolKind.Eraser;
    public override bool ShowsSelection => false;
    readonly List<Point> _pts = new();
    Point _cursor;

    public override void OnDown(Point p, ModifierKeys mods, int clicks) { _pts.Clear(); _pts.Add(p); Canvas.Invalidate(); }

    public override void OnMove(Point p, ModifierKeys mods, bool pressed)
    {
        _cursor = p;
        if (pressed && _pts.Count > 0 && (_pts[^1] - p).Length > 1) _pts.Add(p);
        Canvas.Invalidate();
    }

    public override void OnUp(Point p, ModifierKeys mods)
    {
        if (_pts.Count == 0) return;
        var g = Path();
        Doc.Checkpoint();
        var pen = new Pen(Brushes.Black, Style.Thickness * 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        Doc.ReplaceBase(RasterOps.PaintGeometry(Doc.Base, g, pen, null, Doc.CanvasColor));
        _pts.Clear();
    }

    Geometry Path()
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(_pts[0], false, false);
            if (_pts.Count == 1) c.LineTo(_pts[0] + new Vector(0.1, 0), true, true);
            c.PolyLineTo(_pts.Skip(1).ToList(), true, true);
        }
        return g;
    }

    public override void RenderDocOverlay(DrawingContext dc)
    {
        if (_pts.Count > 0)
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 255, 255, 255)), Style.Thickness * 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Path());
        dc.DrawEllipse(null, new Pen(Brushes.Gray, 1 / Canvas.Scale), _cursor, Style.Thickness, Style.Thickness);
    }
}

public sealed class MagicWandTool : ToolBase
{
    public override ToolKind Kind => ToolKind.MagicWand;
    public override bool ShowsSelection => false;
    bool[]? _mask;
    Geometry? _outline;

    public override void Reset() { _mask = null; _outline = null; Canvas?.Invalidate(); }

    public override void OnDown(Point p, ModifierKeys mods, int clicks)
    {
        var bounds = RasterOps.MagicWandBounds(Doc.Base, (int)p.X, (int)p.Y, Style.Tolerance, out var mask);
        if (bounds.Width <= 0) { Reset(); return; }
        _mask = mask;
        _outline = BuildOutline(mask, Doc.Width, bounds);
        Canvas.Invalidate();
    }

    static Geometry BuildOutline(bool[] mask, int w, Int32Rect b)
    {
        // Horizontal runs merged into one geometry; good enough to show what will be affected.
        var g = new StreamGeometry();
        using (var c = g.Open())
            for (int y = b.Y; y < b.Y + b.Height; y++)
            {
                int x = b.X;
                while (x < b.X + b.Width)
                {
                    while (x < b.X + b.Width && !mask[y * w + x]) x++;
                    int s = x;
                    while (x < b.X + b.Width && mask[y * w + x]) x++;
                    if (x > s)
                    {
                        c.BeginFigure(new Point(s, y), true, true);
                        c.PolyLineTo(new[] { new Point(x, y), new Point(x, y + 1), new Point(s, y + 1) }, false, false);
                    }
                }
            }
        g.Freeze();
        return g;
    }

    public override void OnMove(Point p, ModifierKeys mods, bool pressed) { }
    public override void OnUp(Point p, ModifierKeys mods) { }

    public override bool OnKey(Key key, ModifierKeys mods)
    {
        if (_mask == null) return false;
        if (key is Key.Delete or Key.Back)
        {
            Doc.Checkpoint();
            var px = RasterOps.Read(Doc.Base);
            var cc = Doc.CanvasColor;
            for (int i = 0; i < _mask.Length; i++)
                if (_mask[i]) { int k = i * 4; px.Data[k] = cc.B; px.Data[k + 1] = cc.G; px.Data[k + 2] = cc.R; px.Data[k + 3] = cc.A; }
            Doc.ReplaceBase(RasterOps.Write(px));
            Reset();
            return true;
        }
        if (key == Key.Escape) { Reset(); return true; }
        return false;
    }

    public override void RenderDocOverlay(DrawingContext dc)
    {
        if (_outline != null) dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x70, 0x2E, 0x9B, 0xFF)), null, _outline);
    }
}
