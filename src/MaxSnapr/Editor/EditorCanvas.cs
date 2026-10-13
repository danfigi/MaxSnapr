using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaxSnapr.Editor.Model;
using MaxSnapr.Editor.Tools;

namespace MaxSnapr.Editor;

/// <summary>
/// Draws the document at the current zoom and routes input to the active tool.
/// Document coordinates are image pixels; at 100 % one image pixel maps to one device pixel.
/// </summary>
public sealed class EditorCanvas : Canvas
{
    public const double Pad = 48;
    public const double HandleSize = 9;

    EditorDocument? _doc;
    double _zoom = 1;
    ITool _tool;
    readonly TextBox _textEditor;
    TextAnnotation? _editingText;
    Point _panStart;
    Vector _panOffsetStart;
    bool _panning;

    public EditorCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        _tool = new SelectTool();
        _textEditor = new TextBox
        {
            AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Visibility = Visibility.Collapsed,
        };
        _textEditor.TextChanged += (_, _) =>
        {
            if (_editingText == null) return;
            _editingText.Text = _textEditor.Text;
            _editingText.FitHeight(PixelsPerDip);
            PositionTextEditor();
            InvalidateVisual();
        };
        _textEditor.LostKeyboardFocus += (_, _) => EndTextEdit();
        _textEditor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { EndTextEdit(); Focus(); e.Handled = true; }
        };
        Children.Add(_textEditor);
        _tool.Attach(this);
    }

    // ---- state ---------------------------------------------------------------------------------

    public EditorDocument? Document
    {
        get => _doc;
        set
        {
            EndTextEdit();
            if (_doc != null) _doc.Changed -= OnDocChanged;
            _doc = value;
            if (_doc != null) _doc.Changed += OnDocChanged;
            Selection.Clear();
            _tool.Reset();
            RaiseSelectionChanged();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    void OnDocChanged(object? s, EventArgs e)
    {
        Selection.RemoveAll(a => _doc == null || !_doc.Annotations.Contains(a));
        // Undo replaces object instances; keep the selection pointing at live ones by id.
        InvalidateMeasure();
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public List<Annotation> Selection { get; } = new();
    public event EventHandler? SelectionChanged;
    public event EventHandler? ZoomChanged;
    public event EventHandler? DocumentChanged;
    public event EventHandler<Point>? CursorMoved;
    public event EventHandler<Point>? ContextMenuRequested;

    public void RaiseSelectionChanged() { SelectionChanged?.Invoke(this, EventArgs.Empty); InvalidateVisual(); }

    public ITool Tool
    {
        get => _tool;
        set
        {
            EndTextEdit();
            _tool.Detach();
            _tool = value;
            _tool.Attach(this);
            Cursor = _tool.Cursor;
            InvalidateVisual();
        }
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            var z = Math.Clamp(value, 0.05, 16);
            if (Math.Abs(z - _zoom) < 1e-6) return;
            _zoom = z;
            PositionTextEditor();
            InvalidateMeasure();
            InvalidateVisual();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;
    /// <summary>DIPs per document pixel.</summary>
    public double Scale => _zoom / PixelsPerDip;
    public Brush Backdrop { get; set; } = new SolidColorBrush(Color.FromRgb(0x2B, 0x2E, 0x33));

    public Point ToDoc(Point local) => new((local.X - Pad) / Scale, (local.Y - Pad) / Scale);
    public Point ToLocal(Point doc) => new(doc.X * Scale + Pad, doc.Y * Scale + Pad);
    public double HandleTolerance => HandleSize / Scale;

    public void Invalidate() => InvalidateVisual();
    public void InvalidateLayoutAndVisual() { InvalidateMeasure(); InvalidateVisual(); }

    public void FitToWindow(Size viewport)
    {
        if (_doc == null) return;
        double sx = (viewport.Width - Pad * 2) / _doc.Width, sy = (viewport.Height - Pad * 2) / _doc.Height;
        Zoom = Math.Max(0.05, Math.Min(1, Math.Min(sx, sy)) * PixelsPerDip);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        foreach (UIElement c in Children) c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (_doc == null) return new Size(0, 0);
        return new Size(_doc.Width * Scale + Pad * 2, _doc.Height * Scale + Pad * 2);
    }

    // ---- rendering -----------------------------------------------------------------------------

    static readonly Brush Checker = CreateChecker();

    static Brush CreateChecker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE3)), null,
            new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) } }));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_doc == null) return;
        var imgRect = new Rect(Pad, Pad, _doc.Width * Scale, _doc.Height * Scale);
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 6), Rect.Inflate(imgRect, 3, 3));
        dc.DrawRectangle(Checker, null, imgRect);

        RenderOptions.SetBitmapScalingMode(this, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        dc.PushTransform(new TranslateTransform(Pad, Pad));
        dc.PushTransform(new ScaleTransform(Scale, Scale));
        _doc.RenderContent(dc, PixelsPerDip);
        _tool.RenderDocOverlay(dc);
        dc.Pop();
        dc.Pop();

        if (App.Settings.PixelGrid && Scale >= 8) DrawPixelGrid(dc, imgRect);
        _tool.RenderScreenOverlay(dc);
        if (_tool.ShowsSelection) DrawSelection(dc);
    }

    void DrawPixelGrid(DrawingContext dc, Rect imgRect)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)), 1);
        var view = new Rect(0, 0, ActualWidth, ActualHeight);
        if (FindScrollViewer() is ScrollViewer sv) view = new Rect(sv.HorizontalOffset, sv.VerticalOffset, sv.ViewportWidth, sv.ViewportHeight);
        var r = Rect.Intersect(imgRect, view);
        if (r.IsEmpty) return;
        double step = Scale;
        double x0 = Pad + Math.Ceiling((r.Left - Pad) / step) * step, y0 = Pad + Math.Ceiling((r.Top - Pad) / step) * step;
        for (double x = x0; x < r.Right; x += step) dc.DrawLine(pen, new Point(x, r.Top), new Point(x, r.Bottom));
        for (double y = y0; y < r.Bottom; y += step) dc.DrawLine(pen, new Point(r.Left, y), new Point(r.Right, y));
    }

    static readonly Pen SelPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x2E, 0x9B, 0xFF)), 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) });
    static readonly Pen HandlePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x2E, 0x9B, 0xFF)), 1.5));
    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    void DrawSelection(DrawingContext dc)
    {
        foreach (var a in Selection)
        {
            var b = a.Bounds;
            if (b.IsEmpty) continue;
            var r = new Rect(ToLocal(b.TopLeft), ToLocal(b.BottomRight));
            dc.DrawRectangle(null, SelPen, r);
            if (Selection.Count != 1) continue;
            var handles = a.GetHandles();
            for (int i = 0; i < handles.Count; i++)
            {
                var p = ToLocal(handles[i]);
                bool special = a is LineAnnotation && i == 2 || a is TextAnnotation && i == 8;
                var hr = new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize);
                if (special) dc.DrawEllipse(Brushes.Gold, HandlePen, p, HandleSize / 2, HandleSize / 2);
                else if (a is LineAnnotation) dc.DrawEllipse(Brushes.White, HandlePen, p, HandleSize / 2, HandleSize / 2);
                else dc.DrawRectangle(Brushes.White, HandlePen, hr);
            }
        }
    }

    // ---- hit testing helpers used by tools -----------------------------------------------------

    public Annotation? HitObject(Point doc) => _doc?.Annotations.AsEnumerable().Reverse().FirstOrDefault(a => a.HitTest(doc, HandleTolerance));

    public (Annotation obj, int handle)? HitHandle(Point doc)
    {
        if (Selection.Count != 1) return null;
        var a = Selection[0];
        var hs = a.GetHandles();
        for (int i = hs.Count - 1; i >= 0; i--)
            if (Math.Abs(hs[i].X - doc.X) <= HandleTolerance && Math.Abs(hs[i].Y - doc.Y) <= HandleTolerance) return (a, i);
        return null;
    }

    public void Select(Annotation? a, bool add = false)
    {
        if (!add) Selection.Clear();
        if (a != null)
        {
            var group = a.GroupId != null && _doc != null ? _doc.Annotations.Where(x => x.GroupId == a.GroupId) : new[] { a };
            foreach (var g in group) if (!Selection.Contains(g)) Selection.Add(g);
        }
        RaiseSelectionChanged();
    }

    public void SelectAll()
    {
        if (_doc == null) return;
        Selection.Clear();
        Selection.AddRange(_doc.Annotations);
        RaiseSelectionChanged();
    }

    // ---- text editing --------------------------------------------------------------------------

    public void BeginTextEdit(TextAnnotation t)
    {
        EndTextEdit();
        _editingText = t;
        t.IsEditing = true;
        _textEditor.Text = t.Text;
        _textEditor.Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag);
        SpellCheck.SetIsEnabled(_textEditor, App.Settings.SpellCheck);
        StyleTextEditor();
        _textEditor.Visibility = Visibility.Visible;
        PositionTextEditor();
        Dispatcher.BeginInvoke(() => { _textEditor.Focus(); _textEditor.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        InvalidateVisual();
    }

    public void StyleTextEditor()
    {
        if (_editingText == null) return;
        var s = _editingText.Style;
        _textEditor.FontFamily = new FontFamily(s.FontFamily);
        _textEditor.FontWeight = s.Bold ? FontWeights.Bold : FontWeights.Normal;
        _textEditor.FontStyle = s.Italic ? FontStyles.Italic : FontStyles.Normal;
        _textEditor.Foreground = new SolidColorBrush(s.TextColor);
        _textEditor.CaretBrush = new SolidColorBrush(s.TextColor.R + s.TextColor.G + s.TextColor.B > 380 ? Colors.Black : s.TextColor);
        _textEditor.TextAlignment = s.Align switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left };
        _textEditor.TextDecorations = s.Underline ? TextDecorations.Underline : null;
        PositionTextEditor();
    }

    void PositionTextEditor()
    {
        if (_editingText == null) return;
        var t = _editingText;
        var o = ToLocal(t.TextOrigin);
        SetLeft(_textEditor, o.X - 2);
        SetTop(_textEditor, o.Y);
        double pad = t.TextOrigin.X - t.Box.X;
        _textEditor.Width = Math.Max(20, (t.Box.Width - pad * 2) * Scale + 4);
        _textEditor.FontSize = Math.Max(1, t.Style.FontSize * Scale);
    }

    public bool IsEditingText => _editingText != null;

    public void EndTextEdit()
    {
        if (_editingText == null) return;
        var t = _editingText;
        _editingText = null;
        t.IsEditing = false;
        t.Text = _textEditor.Text;
        _textEditor.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(t.Text) && t.Style.Callout == CalloutKind.None && _doc != null)
        {
            _doc.Remove(new[] { t });
            Selection.Remove(t);
            RaiseSelectionChanged();
        }
        _doc?.RaiseChanged();
        InvalidateVisual();
    }

    // ---- input ---------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (_doc == null) return;
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        {
            _panning = true;
            _panStart = e.GetPosition(Parent as IInputElement ?? this);
            var sv = FindScrollViewer();
            _panOffsetStart = sv == null ? default : new Vector(sv.HorizontalOffset, sv.VerticalOffset);
            CaptureMouse();
            Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        if (_editingText != null && !_textEditor.IsMouseOver) EndTextEdit();
        Focus();
        CaptureMouse();
        _tool.OnDown(ToDoc(e.GetPosition(this)), Keyboard.Modifiers, e.ClickCount);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_doc == null) return;
        if (_panning)
        {
            var sv = FindScrollViewer();
            if (sv != null)
            {
                var d = e.GetPosition(Parent as IInputElement ?? this) - _panStart;
                sv.ScrollToHorizontalOffset(_panOffsetStart.X - d.X);
                sv.ScrollToVerticalOffset(_panOffsetStart.Y - d.Y);
            }
            return;
        }
        var p = ToDoc(e.GetPosition(this));
        CursorMoved?.Invoke(this, p);
        _tool.OnMove(p, Keyboard.Modifiers, e.LeftButton == MouseButtonState.Pressed);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = _tool.Cursor;
            return;
        }
        if (e.ChangedButton != MouseButton.Left || _doc == null) return;
        ReleaseMouseCapture();
        _tool.OnUp(ToDoc(e.GetPosition(this)), Keyboard.Modifiers);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (_doc == null) return;
        var p = ToDoc(e.GetPosition(this));
        var hit = HitObject(p);
        if (hit != null && !Selection.Contains(hit)) Select(hit);
        ContextMenuRequested?.Invoke(this, p);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Zoom *= e.Delta > 0 ? 1.15 : 1 / 1.15;
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_doc == null || _editingText != null) return;
        if (_tool.OnKey(e.Key, Keyboard.Modifiers)) { e.Handled = true; return; }
        if (Selection.Count > 0 && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            var v = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), _ => new Vector(0, step) };
            _doc.Checkpoint();
            foreach (var a in Selection) a.Offset(v);
            _doc.RaiseChanged();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && Selection.Count > 0)
        {
            DeleteSelection();
            e.Handled = true;
        }
    }

    public void DeleteSelection()
    {
        if (_doc == null || Selection.Count == 0) return;
        _doc.Checkpoint();
        _doc.Remove(Selection.ToList());
        Selection.Clear();
        RaiseSelectionChanged();
    }

    ScrollViewer? FindScrollViewer()
    {
        DependencyObject? d = this;
        while (d != null && d is not ScrollViewer) d = VisualTreeHelper.GetParent(d);
        return d as ScrollViewer;
    }
}
