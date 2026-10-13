using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace MaxSnapr.Editor.Model;

public enum LineCap { None, Arrow, OpenArrow, Circle, Square, Bar }
public enum LineDash { Solid, Dash, Dot }
public enum ShapeKind { Rectangle, RoundedRectangle, Ellipse, Triangle, Diamond, Star }
public enum CalloutKind { None, Rectangle, Rounded, Bubble, RoundBubble }
public enum RedactKind { Pixelate, Blur, Solid }
public enum StepFormat { Number, UpperLetter, LowerLetter, Roman }
public enum StepShape { Circle, Square, RoundedSquare, Pin }
public enum TextAlign { Left, Center, Right }

/// <summary>
/// Every visual parameter a tool or object can use. Objects keep their own copy so editing one
/// object never changes the tool default, and quick styles are just named snapshots of this.
/// </summary>
public sealed class ToolStyle
{
    public Color Stroke { get; set; } = Color.FromRgb(0xE8, 0x3A, 0x3A);
    public Color Fill { get; set; } = Colors.Transparent;
    public Color TextColor { get; set; } = Colors.Black;
    public double Thickness { get; set; } = 6;
    public double Opacity { get; set; } = 1;
    public bool Shadow { get; set; } = true;
    public double ShadowDistance { get; set; } = 5;
    public double ShadowAngle { get; set; } = 55;
    public double ShadowBlur { get; set; } = 3;
    public double ShadowOpacity { get; set; } = 45;
    public LineCap StartCap { get; set; } = LineCap.None;
    public LineCap EndCap { get; set; } = LineCap.Arrow;
    public LineDash Dash { get; set; } = LineDash.Solid;
    public bool Curved { get; set; }
    public ShapeKind Shape { get; set; } = ShapeKind.Rectangle;
    public double CornerRadius { get; set; } = 12;
    public CalloutKind Callout { get; set; } = CalloutKind.None;
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 28;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public TextAlign Align { get; set; } = TextAlign.Left;
    public bool TextOutline { get; set; }
    public RedactKind Redact { get; set; } = RedactKind.Pixelate;
    public double Intensity { get; set; } = 50;
    public StepFormat StepFormat { get; set; } = StepFormat.Number;
    public StepShape StepShape { get; set; } = StepShape.Circle;
    public double Size { get; set; } = 44;
    public double Zoom { get; set; } = 2;
    public string Glyph { get; set; } = "\uE73E";
    public double Tolerance { get; set; } = 20;
    public bool Vertical { get; set; } = true;
    public string EdgeStyle { get; set; } = "straight";
    public string SelectionShape { get; set; } = "rect";

    public ToolStyle Clone() => (ToolStyle)MemberwiseClone();

    public static readonly JsonSerializerOptions Json = CreateJson();

    static JsonSerializerOptions CreateJson()
    {
        var o = new JsonSerializerOptions { WriteIndented = false, Converters = { new ColorConverter(), new RectConverter(), new JsonStringEnumConverter() } };
        return o;
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    public static Color FromHex(string s)
    {
        s = s.TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        uint v = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

public sealed class ColorConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ToolStyle.FromHex(reader.GetString()!);
    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) => writer.WriteStringValue(ToolStyle.ToHex(value));
}

public sealed class RectConverter : JsonConverter<Rect>
{
    public override Rect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var parts = reader.GetString()!.Split(',');
        if (parts.Length != 4) return Rect.Empty;
        double P(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
        return new Rect(P(0), P(1), P(2), P(3));
    }

    public override void Write(Utf8JsonWriter writer, Rect r, JsonSerializerOptions options) =>
        writer.WriteStringValue(r.IsEmpty ? "" : string.Create(CultureInfo.InvariantCulture, $"{r.X},{r.Y},{r.Width},{r.Height}"));
}
