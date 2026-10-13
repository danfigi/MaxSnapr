using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MaxSnapr.Editor.Model;

/// <summary>What an annotation may need from its document while rendering.</summary>
public sealed class RenderContext
{
    public required BitmapSource BaseImage { get; init; }
    public double PixelsPerDip { get; init; } = 1;
    public Size DocumentSize => new(BaseImage.PixelWidth, BaseImage.PixelHeight);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(LineAnnotation), "line")]
[JsonDerivedType(typeof(ShapeAnnotation), "shape")]
[JsonDerivedType(typeof(TextAnnotation), "text")]
[JsonDerivedType(typeof(StepAnnotation), "step")]
[JsonDerivedType(typeof(PenAnnotation), "pen")]
[JsonDerivedType(typeof(RedactAnnotation), "redact")]
[JsonDerivedType(typeof(MagnifyAnnotation), "magnify")]
[JsonDerivedType(typeof(StampAnnotation), "stamp")]
[JsonDerivedType(typeof(ImageAnnotation), "image")]
[JsonDerivedType(typeof(SpotlightAnnotation), "spotlight")]
public abstract class Annotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ToolStyle Style { get; set; } = new();
    public string? GroupId { get; set; }

    [JsonIgnore] public abstract Rect Bounds { get; }
    [JsonIgnore] public virtual string DisplayName => GetType().Name;

    public abstract void Render(DrawingContext dc, RenderContext ctx);
    public abstract bool HitTest(Point p, double tolerance);
    public abstract IReadOnlyList<Point> GetHandles();
    public abstract void MoveHandle(int index, Point p, bool constrain);
    public abstract void Offset(Vector v);
    /// <summary>Called while the object is first drawn: <paramref name="start"/> is the press point.</summary>
    public abstract void Drag(Point start, Point current, bool constrain);

    public virtual Annotation Clone()
    {
        var c = (Annotation)MemberwiseClone();
        c.Style = Style.Clone();
        return c;
    }

    public Annotation Duplicate()
    {
        var c = Clone();
        c.Id = Guid.NewGuid();
        return c;
    }

    // ---- shared helpers ------------------------------------------------------------------------

    protected static Brush BrushOf(Color c, double opacity = 1)
    {
        var b = new SolidColorBrush(opacity >= 1 ? c : Color.FromArgb((byte)(c.A * opacity), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    protected Pen StrokePen(double? thickness = null, PenLineCap cap = PenLineCap.Round)
    {
        var p = new Pen(BrushOf(Style.Stroke, Style.Opacity), thickness ?? Style.Thickness)
        {
            StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round,
            DashCap = PenLineCap.Round,
            DashStyle = Style.Dash switch { LineDash.Dash => new DashStyle(new double[] { 3, 2 }, 0), LineDash.Dot => new DashStyle(new double[] { 0, 2 }, 0), _ => DashStyles.Solid },
        };
        p.Freeze();
        return p;
    }

    /// <summary>Cheap soft drop shadow: a few offset, widening, translucent copies of the outline.</summary>
    /// <summary>Soft drop shadow: offset, widening, translucent copies of the outline (direction, distance, blur and opacity from the style).</summary>
    protected void DrawShadow(DrawingContext dc, Geometry g, double strokeWidth, bool filled)
    {
        double a = Style.ShadowAngle * Math.PI / 180, d = Style.ShadowDistance;
        int layers = Math.Clamp((int)Math.Round(Style.ShadowBlur), 1, 8);
        byte alpha = (byte)Math.Clamp(Style.ShadowOpacity * 2.55 / Math.Sqrt(layers) * 0.9, 4, 255);
        dc.PushTransform(new TranslateTransform(Math.Cos(a) * d, Math.Sin(a) * d));
        for (int i = layers; i >= 1; i--)
        {
            var b = BrushOf(Color.FromArgb(alpha, 0, 0, 0));
            var pen = new Pen(b, strokeWidth + i * 2.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(filled ? b : null, pen, g);
        }
        dc.Pop();
    }

    protected static Rect Normalize(Point a, Point b) => new(a, b);

    protected static Point ConstrainSquare(Point start, Point p)
    {
        double dx = p.X - start.X, dy = p.Y - start.Y;
        double s = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Point(start.X + Math.Sign(dx == 0 ? 1 : dx) * s, start.Y + Math.Sign(dy == 0 ? 1 : dy) * s);
    }

    protected static Point ConstrainAngle(Point start, Point p)
    {
        var v = p - start;
        double angle = Math.Atan2(v.Y, v.X);
        double step = Math.PI / 12; // 15°
        angle = Math.Round(angle / step) * step;
        double len = v.Length;
        return new Point(start.X + Math.Cos(angle) * len, start.Y + Math.Sin(angle) * len);
    }
}

/// <summary>Objects defined by a rectangle; 8 resize handles.</summary>
public abstract class BoxAnnotation : Annotation
{
    public Rect Box { get; set; }
    public override Rect Bounds => Box;

    public override IReadOnlyList<Point> GetHandles()
    {
        var r = Box;
        return new[]
        {
            r.TopLeft, new Point(r.Left + r.Width / 2, r.Top), r.TopRight, new Point(r.Right, r.Top + r.Height / 2),
            r.BottomRight, new Point(r.Left + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.Left, r.Top + r.Height / 2),
        };
    }

    public override void MoveHandle(int index, Point p, bool constrain)
    {
        double l = Box.Left, t = Box.Top, r = Box.Right, b = Box.Bottom;
        switch (index)
        {
            case 0: l = p.X; t = p.Y; break;
            case 1: t = p.Y; break;
            case 2: r = p.X; t = p.Y; break;
            case 3: r = p.X; break;
            case 4: r = p.X; b = p.Y; break;
            case 5: b = p.Y; break;
            case 6: l = p.X; b = p.Y; break;
            case 7: l = p.X; break;
        }
        if (constrain && index % 2 == 0)
        {
            double aspect = Box.Height > 0 ? Box.Width / Box.Height : 1;
            double w = Math.Abs(r - l), h = w / aspect;
            if (index == 0 || index == 2) t = b - h; else b = t + h;
        }
        Box = new Rect(new Point(l, t), new Point(r, b));
    }

    public override void Offset(Vector v) => Box = Rect.Offset(Box, v);

    public override void Drag(Point start, Point current, bool constrain)
        => Box = Normalize(start, constrain ? ConstrainSquare(start, current) : current);

    public override bool HitTest(Point p, double tolerance)
    {
        var r = Box;
        r.Inflate(tolerance, tolerance);
        return r.Contains(p);
    }
}

// =================================================================================================

public sealed class LineAnnotation : Annotation
{
    public Point Start { get; set; }
    public Point End { get; set; }
    public Point? Control { get; set; }

    public override string DisplayName => Style.EndCap != LineCap.None || Style.StartCap != LineCap.None ? "Flecha" : "Línea";

    public override Rect Bounds
    {
        get
        {
            var r = new Rect(Start, End);
            if (Control is Point c) r.Union(c);
            r.Inflate(Style.Thickness * 2, Style.Thickness * 2);
            return r;
        }
    }

    Point Ctrl => Control ?? new Point((Start.X + End.X) / 2, (Start.Y + End.Y) / 2);
    bool IsCurved => Style.Curved && Control.HasValue;

    double HeadLength => Math.Max(12, Style.Thickness * 3.2);

    Geometry BuildPath(out Vector startDir, out Vector endDir)
    {
        var s = Start; var e = End;
        if (IsCurved) { startDir = s - Ctrl; endDir = e - Ctrl; }
        else { startDir = s - e; endDir = e - s; }
        if (startDir.Length > 0) startDir.Normalize();
        if (endDir.Length > 0) endDir.Normalize();
        // Pull line ends inside filled arrow heads so the stroke never pokes through the tip.
        if (Style.StartCap is LineCap.Arrow) s -= startDir * HeadLength * 0.7;
        if (Style.EndCap is LineCap.Arrow) e -= endDir * HeadLength * 0.7;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(s, false, false);
            if (IsCurved) ctx.QuadraticBezierTo(Ctrl, e, true, true);
            else ctx.LineTo(e, true, true);
        }
        g.Freeze();
        return g;
    }

    Geometry? CapGeometry(LineCap cap, Point tip, Vector dir)
    {
        if (cap == LineCap.None || dir.Length == 0) return null;
        double len = HeadLength, half = len * 0.55;
        var normal = new Vector(-dir.Y, dir.X);
        switch (cap)
        {
            case LineCap.Arrow:
            case LineCap.OpenArrow:
            {
                var b = tip - dir * len;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(b + normal * half, cap == LineCap.Arrow, cap == LineCap.Arrow);
                    c.LineTo(tip, true, true);
                    c.LineTo(b - normal * half, true, true);
                }
                g.Freeze();
                return g;
            }
            case LineCap.Circle:
                return new EllipseGeometry(tip, half * 0.8, half * 0.8);
            case LineCap.Square:
                return new RectangleGeometry(new Rect(tip.X - half * 0.7, tip.Y - half * 0.7, half * 1.4, half * 1.4), 0, 0,
                    new RotateTransform(Math.Atan2(dir.Y, dir.X) * 180 / Math.PI, tip.X, tip.Y));
            case LineCap.Bar:
            {
                var g = new StreamGeometry();
                using (var c = g.Open()) { c.BeginFigure(tip + normal * half, false, false); c.LineTo(tip - normal * half, true, true); }
                g.Freeze();
                return g;
            }
        }
        return null;
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var path = BuildPath(out var sd, out var ed);
        var startCap = CapGeometry(Style.StartCap, Start, sd);
        var endCap = CapGeometry(Style.EndCap, End, ed);
        if (Style.Shadow)
        {
            DrawShadow(dc, path, Style.Thickness, false);
            if (startCap != null) DrawShadow(dc, startCap, 1, true);
            if (endCap != null) DrawShadow(dc, endCap, 1, true);
        }
        dc.DrawGeometry(null, StrokePen(), path);
        var fill = BrushOf(Style.Stroke, Style.Opacity);
        foreach (var (cap, g) in new[] { (Style.StartCap, startCap), (Style.EndCap, endCap) })
        {
            if (g == null) continue;
            bool open = cap is LineCap.OpenArrow or LineCap.Bar;
            dc.DrawGeometry(open ? null : fill, StrokePen(open ? Style.Thickness : Math.Max(1, Style.Thickness / 3)), g);
        }
    }

    public override bool HitTest(Point p, double tolerance)
    {
        var path = BuildPath(out _, out _);
        return path.StrokeContains(new Pen(Brushes.Black, Style.Thickness + tolerance * 2), p)
            || (Start - p).Length < tolerance + HeadLength / 2 || (End - p).Length < tolerance + HeadLength / 2;
    }

    public override IReadOnlyList<Point> GetHandles() => new[] { Start, End, Ctrl };

    public override void MoveHandle(int index, Point p, bool constrain)
    {
        if (index == 0) Start = constrain ? ConstrainAngle(End, p) : p;
        else if (index == 1) End = constrain ? ConstrainAngle(Start, p) : p;
        else { Control = p; Style.Curved = true; }
    }

    public override void Offset(Vector v)
    {
        Start += v; End += v;
        if (Control is Point c) Control = c + v;
    }

    public override void Drag(Point start, Point current, bool constrain)
    {
        Start = start;
        End = constrain ? ConstrainAngle(start, current) : current;
        Control = null;
    }
}

// =================================================================================================

public sealed class ShapeAnnotation : BoxAnnotation
{
    public override string DisplayName => "Forma";

    public Geometry BuildGeometry()
    {
        var r = Box;
        switch (Style.Shape)
        {
            case ShapeKind.Ellipse: return new EllipseGeometry(r);
            case ShapeKind.RoundedRectangle: return new RectangleGeometry(r, Math.Min(Style.CornerRadius, r.Width / 2), Math.Min(Style.CornerRadius, r.Height / 2));
            case ShapeKind.Triangle: return Polygon(new Point(r.Left + r.Width / 2, r.Top), r.BottomRight, r.BottomLeft);
            case ShapeKind.Diamond: return Polygon(new Point(r.Left + r.Width / 2, r.Top), new Point(r.Right, r.Top + r.Height / 2), new Point(r.Left + r.Width / 2, r.Bottom), new Point(r.Left, r.Top + r.Height / 2));
            case ShapeKind.Star:
            {
                var pts = new List<Point>();
                var c = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
                for (int i = 0; i < 10; i++)
                {
                    double a = -Math.PI / 2 + i * Math.PI / 5;
                    double k = i % 2 == 0 ? 1 : 0.42;
                    pts.Add(new Point(c.X + Math.Cos(a) * r.Width / 2 * k, c.Y + Math.Sin(a) * r.Height / 2 * k));
                }
                return Polygon(pts.ToArray());
            }
            default: return new RectangleGeometry(r);
        }
    }

    static Geometry Polygon(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], true, true);
            c.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var g = BuildGeometry();
        bool filled = Style.Fill.A > 0;
        if (Style.Shadow) DrawShadow(dc, g, Style.Thickness, filled);
        dc.DrawGeometry(filled ? BrushOf(Style.Fill, Style.Opacity) : null, Style.Thickness > 0 ? StrokePen(cap: PenLineCap.Flat) : null, g);
    }

    public override bool HitTest(Point p, double tolerance)
    {
        var g = BuildGeometry();
        return (Style.Fill.A > 0 && g.FillContains(p)) || g.StrokeContains(new Pen(Brushes.Black, Style.Thickness + tolerance * 2), p)
            || (Box.Width < 24 || Box.Height < 24) && base.HitTest(p, tolerance);
    }
}

// =================================================================================================

public sealed class TextAnnotation : BoxAnnotation
{
    public string Text { get; set; } = "";
    public Point? Tail { get; set; }
    [JsonIgnore] public bool IsEditing { get; set; }

    public override string DisplayName => Style.Callout == CalloutKind.None ? "Texto" : "Globo";
    double Padding => Style.Callout == CalloutKind.None ? 4 : Math.Max(10, Style.FontSize * 0.45);

    public FormattedText Format(double pixelsPerDip, string? text = null)
    {
        var tf = new Typeface(new FontFamily(Style.FontFamily), Style.Italic ? FontStyles.Italic : FontStyles.Normal,
            Style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(string.IsNullOrEmpty(text ?? Text) ? " " : text ?? Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf,
            Math.Max(4, Style.FontSize), BrushOf(Style.TextColor, Style.Opacity), pixelsPerDip)
        {
            MaxTextWidth = Math.Max(10, Box.Width - Padding * 2),
            TextAlignment = Style.Align switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left },
        };
        if (Style.Underline) ft.SetTextDecorations(TextDecorations.Underline);
        return ft;
    }

    /// <summary>Grows the box vertically so wrapped text always fits.</summary>
    public void FitHeight(double pixelsPerDip)
    {
        var ft = Format(pixelsPerDip);
        double need = ft.Height + Padding * 2;
        if (Box.Height < need) Box = new Rect(Box.X, Box.Y, Box.Width, need);
    }

    public Point TextOrigin => new(Box.X + Padding, Box.Y + Padding);

    Geometry? BackgroundGeometry()
    {
        var r = Box;
        Geometry? body = Style.Callout switch
        {
            CalloutKind.Rectangle => new RectangleGeometry(r),
            CalloutKind.Rounded or CalloutKind.Bubble => new RectangleGeometry(r, Math.Min(Style.CornerRadius, r.Height / 2), Math.Min(Style.CornerRadius, r.Height / 2)),
            CalloutKind.RoundBubble => new EllipseGeometry(new Rect(r.X - r.Width * 0.08, r.Y - r.Height * 0.12, r.Width * 1.16, r.Height * 1.24)),
            _ => null,
        };
        if (body == null) return null;
        if (Style.Callout is CalloutKind.Bubble or CalloutKind.RoundBubble && Tail is Point tail && !r.Contains(tail))
        {
            var c = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            var dir = tail - c;
            var n = new Vector(-dir.Y, dir.X);
            n.Normalize();
            double w = Math.Min(r.Width, r.Height) * 0.22;
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(c + n * w, true, true);
                g.LineTo(tail, true, true);
                g.LineTo(c - n * w, true, true);
            }
            tri.Freeze();
            return new CombinedGeometry(GeometryCombineMode.Union, body, tri);
        }
        return body;
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var bg = BackgroundGeometry();
        if (bg != null)
        {
            if (Style.Shadow) DrawShadow(dc, bg, Style.Thickness, true);
            dc.DrawGeometry(Style.Fill.A > 0 ? BrushOf(Style.Fill, Style.Opacity) : null, Style.Thickness > 0 ? StrokePen() : null, bg);
        }
        if (IsEditing) return; // the inline editor draws the live text
        var ft = Format(ctx.PixelsPerDip);
        if (Style.TextOutline || (bg == null && Style.Shadow))
        {
            var geo = ft.BuildGeometry(TextOrigin);
            if (Style.TextOutline) dc.DrawGeometry(null, new Pen(BrushOf(Style.Stroke, Style.Opacity), Math.Max(2, Style.FontSize / 9)) { LineJoin = PenLineJoin.Round }, geo);
            else DrawShadow(dc, geo, 0.5, true);
        }
        dc.DrawText(ft, TextOrigin);
    }

    public override bool HitTest(Point p, double tolerance)
    {
        if (base.HitTest(p, tolerance)) return true;
        var bg = BackgroundGeometry();
        return bg != null && bg.FillContains(p);
    }

    public override IReadOnlyList<Point> GetHandles()
    {
        var h = base.GetHandles().ToList();
        if (Style.Callout is CalloutKind.Bubble or CalloutKind.RoundBubble) h.Add(Tail ?? DefaultTail);
        return h;
    }

    Point DefaultTail => new(Box.Left + Box.Width * 0.2, Box.Bottom + Math.Max(30, Box.Height * 0.8));

    public override void MoveHandle(int index, Point p, bool constrain)
    {
        if (index == 8) { Tail = p; return; }
        base.MoveHandle(index, p, constrain);
    }

    public override void Offset(Vector v)
    {
        base.Offset(v);
        if (Tail is Point t) Tail = t + v;
    }

    public void EnsureTail()
    {
        if (Style.Callout is CalloutKind.Bubble or CalloutKind.RoundBubble && Tail == null) Tail = DefaultTail;
    }
}

// =================================================================================================

public sealed class StepAnnotation : Annotation
{
    public Point Center { get; set; }
    public int Number { get; set; } = 1;
    public override string DisplayName => "Paso";

    public override Rect Bounds => new(Center.X - Style.Size / 2, Center.Y - Style.Size / 2, Style.Size, Style.Size);

    public static string Label(int n, StepFormat f) => f switch
    {
        StepFormat.UpperLetter => Letters(n).ToUpperInvariant(),
        StepFormat.LowerLetter => Letters(n),
        StepFormat.Roman => Roman(n),
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    static string Letters(int n)
    {
        var s = "";
        while (n > 0) { n--; s = (char)('a' + n % 26) + s; n /= 26; }
        return s;
    }

    static string Roman(int n)
    {
        var map = new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        var s = "";
        foreach (var (v, r) in map) while (n >= v) { s += r; n -= v; }
        return s;
    }

    Geometry Shape()
    {
        var r = Bounds;
        return Style.StepShape switch
        {
            StepShape.Square => new RectangleGeometry(r),
            StepShape.RoundedSquare => new RectangleGeometry(r, r.Width * 0.22, r.Height * 0.22),
            StepShape.Pin => Pin(r),
            _ => new EllipseGeometry(r),
        };
    }

    static Geometry Pin(Rect r)
    {
        var g = new StreamGeometry();
        var c = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        double rad = r.Width / 2;
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, r.Bottom + rad * 0.7), true, true);
            ctx.LineTo(new Point(c.X - rad * 0.7, c.Y + rad * 0.7), true, true);
            ctx.ArcTo(new Point(c.X + rad * 0.7, c.Y + rad * 0.7), new Size(rad, rad), 0, true, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var g = Shape();
        if (Style.Shadow) DrawShadow(dc, g, 1, true);
        var fill = Style.Fill.A > 0 ? Style.Fill : Style.Stroke;
        dc.DrawGeometry(BrushOf(fill, Style.Opacity), Style.Thickness > 0 ? new Pen(BrushOf(Colors.White, Style.Opacity), Math.Max(2, Style.Size / 16)) : null, g);
        var label = Label(Number, Style.StepFormat);
        var tf = new Typeface(new FontFamily(Style.FontFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double size = Style.Size * (label.Length > 2 ? 0.36 : label.Length == 2 ? 0.46 : 0.56);
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, BrushOf(Style.TextColor, Style.Opacity), ctx.PixelsPerDip);
        dc.DrawText(ft, new Point(Center.X - ft.Width / 2, Center.Y - ft.Height / 2));
    }

    public override bool HitTest(Point p, double tolerance) => (p - Center).Length <= Style.Size / 2 + tolerance;
    public override IReadOnlyList<Point> GetHandles() => new[] { new Point(Center.X + Style.Size / 2, Center.Y + Style.Size / 2) };
    public override void MoveHandle(int index, Point p, bool constrain)
    {
        var d = p - Center;
        Style.Size = Math.Clamp(Math.Max(Math.Abs(d.X), Math.Abs(d.Y)) * 2, 12, 400);
    }
    public override void Offset(Vector v) => Center += v;
    public override void Drag(Point start, Point current, bool constrain) => Center = current;
}

// =================================================================================================

public sealed class PenAnnotation : Annotation
{
    public List<Point> Points { get; set; } = new();
    public bool Highlighter { get; set; }
    public override string DisplayName => Highlighter ? "Resaltador" : "Pluma";

    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0) return Rect.Empty;
            var r = new Rect(Points[0], Points[0]);
            foreach (var p in Points) r.Union(p);
            r.Inflate(Style.Thickness, Style.Thickness);
            return r;
        }
    }

    Geometry Build()
    {
        var g = new StreamGeometry();
        if (Points.Count == 0) return g;
        using (var c = g.Open())
        {
            c.BeginFigure(Points[0], false, false);
            if (Points.Count == 1) c.LineTo(Points[0] + new Vector(0.1, 0), true, true);
            // Midpoint quadratic smoothing gives a natural ink stroke from raw mouse samples.
            for (int i = 1; i < Points.Count - 1; i++)
            {
                var mid = new Point((Points[i].X + Points[i + 1].X) / 2, (Points[i].Y + Points[i + 1].Y) / 2);
                c.QuadraticBezierTo(Points[i], mid, true, true);
            }
            if (Points.Count > 1) c.LineTo(Points[^1], true, true);
        }
        g.Freeze();
        return g;
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var g = Build();
        if (Highlighter)
        {
            var pen = new Pen(BrushOf(Style.Stroke, Math.Min(Style.Opacity, 0.5)), Style.Thickness) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, g);
            return;
        }
        if (Style.Shadow) DrawShadow(dc, g, Style.Thickness, false);
        dc.DrawGeometry(null, StrokePen(), g);
    }

    public override bool HitTest(Point p, double tolerance) => Build().StrokeContains(new Pen(Brushes.Black, Style.Thickness + tolerance * 2), p);
    /// <summary>Editable points: about 16 evenly spaced samples along the stroke.</summary>
    int HandleStep => Math.Max(1, (int)Math.Ceiling(Points.Count / 16.0));

    public override IReadOnlyList<Point> GetHandles()
    {
        var list = new List<Point>();
        for (int i = 0; i < Points.Count; i += HandleStep) list.Add(Points[i]);
        if (Points.Count > 1 && (Points.Count - 1) % HandleStep != 0) list.Add(Points[^1]);
        return list;
    }

    public override void MoveHandle(int index, Point p, bool constrain)
    {
        int step = HandleStep;
        int center = Math.Min(index * step, Points.Count - 1);
        var delta = p - Points[center];
        // Drag nearby samples too, fading with distance, so the stroke bends smoothly.
        for (int i = Math.Max(0, center - step); i <= Math.Min(Points.Count - 1, center + step); i++)
        {
            double w = 1 - Math.Abs(i - center) / (double)(step + 1);
            Points[i] += delta * w;
        }
    }

    public override void Offset(Vector v) { for (int i = 0; i < Points.Count; i++) Points[i] += v; }

    public override void Drag(Point start, Point current, bool constrain)
    {
        if (constrain)
        {
            // Shift draws straight segments, the usual highlighter behaviour.
            var end = Math.Abs(current.X - start.X) > Math.Abs(current.Y - start.Y) ? new Point(current.X, start.Y) : new Point(start.X, current.Y);
            Points = new List<Point> { start, end };
            return;
        }
        if (Points.Count == 0) Points.Add(start);
        if ((Points[^1] - current).Length >= 1.5) Points.Add(current);
    }

    public override Annotation Clone()
    {
        var c = (PenAnnotation)base.Clone();
        c.Points = new List<Point>(Points);
        return c;
    }
}

// =================================================================================================

public sealed class RedactAnnotation : BoxAnnotation
{
    public override string DisplayName => "Difuminar";
    [JsonIgnore] BitmapSource? _cache;
    [JsonIgnore] string? _cacheKey;

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var r = Rect.Intersect(Box, new Rect(ctx.DocumentSize));
        if (r.IsEmpty || r.Width < 1 || r.Height < 1) return;
        if (Style.Redact == RedactKind.Solid)
        {
            dc.DrawRectangle(BrushOf(Style.Fill.A > 0 ? Style.Fill : Colors.Black), null, r);
            return;
        }
        var src = new Int32Rect((int)r.X, (int)r.Y, Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height));
        src = Capture.ScreenSnapshot.Intersect(src, new Int32Rect(0, 0, ctx.BaseImage.PixelWidth, ctx.BaseImage.PixelHeight));
        if (src.Width < 1 || src.Height < 1) return;
        string key = $"{src}|{Style.Redact}|{Style.Intensity}|{ctx.BaseImage.GetHashCode()}";
        if (key != _cacheKey || _cache == null)
        {
            _cache = Raster.RasterOps.Obscure(new CroppedBitmap(ctx.BaseImage, src), Style.Redact == RedactKind.Pixelate, Style.Intensity);
            _cacheKey = key;
        }
        dc.DrawImage(_cache, new Rect(src.X, src.Y, src.Width, src.Height));
    }

    public override Annotation Clone()
    {
        var c = (RedactAnnotation)base.Clone();
        c._cache = null; c._cacheKey = null;
        return c;
    }
}

// =================================================================================================

public sealed class MagnifyAnnotation : BoxAnnotation
{
    public override string DisplayName => "Lupa";

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var r = Box;
        if (r.Width < 2 || r.Height < 2) return;
        bool round = Style.Shape != ShapeKind.Rectangle && Style.Shape != ShapeKind.RoundedRectangle;
        Geometry clip = round ? new EllipseGeometry(r) : new RectangleGeometry(r, Style.Shape == ShapeKind.RoundedRectangle ? 12 : 0, Style.Shape == ShapeKind.RoundedRectangle ? 12 : 0);
        if (Style.Shadow) DrawShadow(dc, clip, Style.Thickness, true);
        double z = Math.Max(1.1, Style.Zoom);
        var c = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        dc.PushClip(clip);
        dc.DrawRectangle(Brushes.White, null, r);
        // Scale the whole base image around the lens centre; the clip limits it to the lens.
        dc.PushTransform(new ScaleTransform(z, z, c.X, c.Y));
        dc.DrawImage(ctx.BaseImage, new Rect(ctx.DocumentSize));
        dc.Pop();
        dc.Pop();
        if (Style.Thickness > 0) dc.DrawGeometry(null, StrokePen(), clip);
    }
}

// =================================================================================================

public sealed class SpotlightAnnotation : BoxAnnotation
{
    public override string DisplayName => "Foco";

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var full = new RectangleGeometry(new Rect(ctx.DocumentSize));
        Geometry hole = Style.Shape == ShapeKind.Ellipse ? new EllipseGeometry(Box) : new RectangleGeometry(Box, 8, 8);
        var dim = new CombinedGeometry(GeometryCombineMode.Exclude, full, hole);
        dc.DrawGeometry(BrushOf(Color.FromArgb((byte)(Math.Clamp(Style.Intensity, 0, 100) * 2.2), 0, 0, 0)), null, dim);
        if (Style.Thickness > 0) dc.DrawGeometry(null, StrokePen(Math.Min(Style.Thickness, 4)), hole);
    }

    public override bool HitTest(Point p, double tolerance)
    {
        // Only the outline is grabbable; the inside must stay clickable for other objects.
        var outer = Box; outer.Inflate(tolerance, tolerance);
        var inner = Box; inner.Inflate(-tolerance, -tolerance);
        return outer.Contains(p) && (inner.IsEmpty || !inner.Contains(p));
    }
}

// =================================================================================================

public sealed class StampAnnotation : BoxAnnotation
{
    public override string DisplayName => "Sello";
    public static bool IsIconGlyph(string g) => g.Length == 1 && g[0] >= '\uE000' && g[0] <= '\uF8FF';
    public static bool IsCursor(string g) => g.StartsWith("cursor:");

    /// <summary>Vector mouse pointers (arrow, hand, text beam) in a 0–1 box.</summary>
    static Geometry CursorGeometry(string kind, Rect r)
    {
        Point P(double x, double y) => new(r.X + x * r.Width, r.Y + y * r.Height);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            switch (kind)
            {
                case "cursor:hand":
                    c.BeginFigure(P(0.38, 0.05), true, true);
                    c.PolyLineTo(new[] { P(0.48, 0.05), P(0.48, 0.45), P(0.58, 0.42), P(0.68, 0.45), P(0.78, 0.49), P(0.88, 0.55), P(0.88, 0.8), P(0.78, 0.97),
                        P(0.42, 0.97), P(0.18, 0.68), P(0.12, 0.56), P(0.2, 0.52), P(0.38, 0.66) }, true, true);
                    break;
                case "cursor:ibeam":
                    c.BeginFigure(P(0.3, 0.02), true, true);
                    c.PolyLineTo(new[] { P(0.7, 0.02), P(0.7, 0.1), P(0.56, 0.1), P(0.56, 0.9), P(0.7, 0.9), P(0.7, 0.98), P(0.3, 0.98), P(0.3, 0.9), P(0.44, 0.9), P(0.44, 0.1), P(0.3, 0.1) }, true, true);
                    break;
                default:
                    c.BeginFigure(P(0.15, 0.02), true, true);
                    c.PolyLineTo(new[] { P(0.15, 0.82), P(0.34, 0.65), P(0.48, 0.97), P(0.62, 0.91), P(0.48, 0.6), P(0.74, 0.6) }, true, true);
                    break;
            }
        }
        g.Freeze();
        return g;
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var r = Box;
        if (r.Width < 2 || r.Height < 2) return;
        var glyph = string.IsNullOrEmpty(Style.Glyph) ? "\uE735" : Style.Glyph;
        if (IsCursor(glyph))
        {
            var cg = CursorGeometry(glyph, r);
            if (Style.Shadow) DrawShadow(dc, cg, 1, true);
            dc.DrawGeometry(BrushOf(Style.Fill.A > 0 ? Style.Fill : Colors.White, Style.Opacity), new Pen(BrushOf(Colors.Black, Style.Opacity), Math.Max(1, r.Width / 28)) { LineJoin = PenLineJoin.Round }, cg);
            return;
        }
        if (IsIconGlyph(glyph))
        {
            if (Style.Fill.A > 0)
            {
                var bg = new EllipseGeometry(r);
                if (Style.Shadow) DrawShadow(dc, bg, 1, true);
                dc.DrawGeometry(BrushOf(Style.Fill, Style.Opacity), null, bg);
            }
            var tf = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double size = Math.Min(r.Width, r.Height) * (Style.Fill.A > 0 ? 0.6 : 0.95);
            var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, BrushOf(Style.Stroke, Style.Opacity), ctx.PixelsPerDip);
            var origin = new Point(r.Left + (r.Width - ft.Width) / 2, r.Top + (r.Height - ft.Height) / 2);
            if (Style.Fill.A == 0 && Style.Shadow) DrawShadow(dc, ft.BuildGeometry(origin), 0.5, true);
            dc.DrawText(ft, origin);
        }
        else
        {
            // Text badge stamp ("NUEVO", "APROBADO"...).
            var g = new RectangleGeometry(r, r.Height * 0.2, r.Height * 0.2);
            if (Style.Shadow) DrawShadow(dc, g, 1, true);
            dc.DrawGeometry(BrushOf(Style.Fill.A > 0 ? Style.Fill : Style.Stroke, Style.Opacity), null, g);
            var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
            var ft = new FormattedText(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, Math.Max(4, r.Height * 0.5), BrushOf(Style.TextColor, Style.Opacity), ctx.PixelsPerDip);
            double scale = Math.Min(1, (r.Width * 0.86) / Math.Max(1, ft.Width));
            dc.PushTransform(new ScaleTransform(scale, scale, r.Left + r.Width / 2, r.Top + r.Height / 2));
            dc.DrawText(ft, new Point(r.Left + (r.Width - ft.Width) / 2, r.Top + (r.Height - ft.Height) / 2));
            dc.Pop();
        }
    }
}

// =================================================================================================

public sealed class ImageAnnotation : BoxAnnotation
{
    public byte[] Png { get; set; } = Array.Empty<byte>();
    [JsonIgnore] BitmapSource? _image;
    public override string DisplayName => "Imagen";

    [JsonIgnore]
    public BitmapSource? Image
    {
        get
        {
            if (_image == null && Png.Length > 0)
            {
                var dec = new PngBitmapDecoder(new MemoryStream(Png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                _image = dec.Frames[0];
                _image.Freeze();
            }
            return _image;
        }
        set
        {
            _image = value;
            if (value == null) { Png = Array.Empty<byte>(); return; }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(value));
            using var ms = new MemoryStream();
            enc.Save(ms);
            Png = ms.ToArray();
        }
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var img = Image;
        if (img == null) return;
        if (Style.Shadow) DrawShadow(dc, new RectangleGeometry(Box), 1, true);
        dc.PushOpacity(Style.Opacity);
        dc.DrawImage(img, Box);
        dc.Pop();
    }
}
