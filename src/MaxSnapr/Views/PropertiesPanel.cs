using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxSnapr.Editor.Model;
using MaxSnapr.Editor.Raster;
using MaxSnapr.Editor.Tools;
using MaxSnapr.Services;

namespace MaxSnapr.Views;

public static class Palette
{
    public static readonly Color[] Colors =
    {
        C("#E83A3A"), C("#F28C28"), C("#FFD23F"), C("#2BB673"), C("#14B8C4"), C("#2E7CF6"), C("#8B5CF6"), C("#EC4899"),
        C("#1D1D1F"), C("#4B5563"), C("#9CA3AF"), C("#FFFFFF"), C("#7F1D1D"), C("#92400E"), C("#14532D"), C("#1E3A8A"),
        C("#FDE2E2"), C("#FFEDD5"), C("#FEF9C3"), C("#DCFCE7"), C("#CFFAFE"), C("#DBEAFE"), C("#EDE9FE"), C("#FCE7F3"),
    };

    public static readonly Color[] QuickColors = { C("#E83A3A"), C("#F28C28"), C("#FFD23F"), C("#2BB673"), C("#2E7CF6"), C("#8B5CF6"), C("#1D1D1F"), C("#FFFFFF") };

    public static Color C(string hex) => ToolStyle.FromHex(hex);
}

public static class DefaultStyles
{
    public static ToolStyle For(ToolKind kind)
    {
        var red = Palette.C("#E83A3A");
        return kind switch
        {
            ToolKind.Arrow => new ToolStyle { Stroke = red, Thickness = 6, EndCap = LineCap.Arrow },
            ToolKind.Line => new ToolStyle { Stroke = red, Thickness = 5, StartCap = LineCap.None, EndCap = LineCap.None },
            ToolKind.Text => new ToolStyle { TextColor = red, Stroke = Colors.White, TextOutline = true, Shadow = false, FontSize = 30, Bold = true, Thickness = 0 },
            ToolKind.Callout => new ToolStyle { Callout = CalloutKind.Bubble, Fill = Colors.White, Stroke = red, Thickness = 3, TextColor = Palette.C("#1D2025"), FontSize = 22, Bold = false, CornerRadius = 14 },
            ToolKind.Shape => new ToolStyle { Stroke = red, Thickness = 5, Shape = ShapeKind.RoundedRectangle, CornerRadius = 10 },
            ToolKind.Step => new ToolStyle { Fill = red, Stroke = red, TextColor = Colors.White, Size = 44, Thickness = 2 },
            ToolKind.Pen => new ToolStyle { Stroke = red, Thickness = 4 },
            ToolKind.Highlighter => new ToolStyle { Stroke = Palette.C("#FFE600"), Thickness = 24, Opacity = 0.5, Shadow = false },
            ToolKind.Blur => new ToolStyle { Redact = RedactKind.Pixelate, Intensity = 45, Fill = Palette.C("#1D1D1F"), Shadow = false },
            ToolKind.Magnify => new ToolStyle { Shape = ShapeKind.Ellipse, Zoom = 2, Stroke = Colors.White, Thickness = 4 },
            ToolKind.Spotlight => new ToolStyle { Shape = ShapeKind.RoundedRectangle, Intensity = 55, Stroke = Colors.White, Thickness = 0, Shadow = false },
            ToolKind.Stamp => new ToolStyle { Glyph = "\uE735", Stroke = Palette.C("#F2B01E"), Fill = Colors.Transparent, TextColor = Colors.White, Size = 40 },
            ToolKind.Fill => new ToolStyle { Fill = red, Tolerance = 15 },
            ToolKind.Eraser => new ToolStyle { Thickness = 12 },
            ToolKind.MagicWand => new ToolStyle { Tolerance = 18 },
            ToolKind.CutOut => new ToolStyle { Vertical = true, EdgeStyle = "saw" },
            _ => new ToolStyle(),
        };
    }

    /// <summary>Built-in quick styles: the default style recoloured with the quick palette, plus a few variants.</summary>
    public static IEnumerable<ToolStyle> QuickStyles(ToolKind kind)
    {
        var baseStyle = For(kind);
        switch (kind)
        {
            case ToolKind.Blur:
                yield return baseStyle;
                yield return With(baseStyle, s => { s.Redact = RedactKind.Blur; s.Intensity = 60; });
                yield return With(baseStyle, s => { s.Redact = RedactKind.Pixelate; s.Intensity = 90; });
                yield return With(baseStyle, s => { s.Redact = RedactKind.Solid; s.Fill = Palette.C("#1D1D1F"); });
                yield return With(baseStyle, s => { s.Redact = RedactKind.Solid; s.Fill = Colors.White; });
                yield break;
            case ToolKind.Crop or ToolKind.CutOut or ToolKind.Selection or ToolKind.Eraser or ToolKind.MagicWand or ToolKind.Select:
                yield break;
        }
        foreach (var c in ToolTheme.Active.Colors.Take(8))
        {
            yield return With(baseStyle, s =>
            {
                switch (kind)
                {
                    case ToolKind.Text:
                        s.TextColor = c;
                        s.Stroke = c.R + c.G + c.B > 600 ? Palette.C("#1D1D1F") : Colors.White;
                        break;
                    case ToolKind.Callout:
                        s.Stroke = c; s.Fill = c.R + c.G + c.B > 600 ? Colors.White : Palette.C("#FFFFFF");
                        if (c == Palette.C("#1D1D1F")) { s.Fill = c; s.TextColor = Colors.White; }
                        break;
                    case ToolKind.Step:
                        s.Fill = c; s.Stroke = c; s.TextColor = c.R + c.G + c.B > 600 ? Palette.C("#1D1D1F") : Colors.White;
                        break;
                    case ToolKind.Fill:
                        s.Fill = c;
                        break;
                    case ToolKind.Highlighter:
                        s.Stroke = c;
                        break;
                    default:
                        s.Stroke = c;
                        break;
                }
            });
        }
        switch (kind)
        {
            case ToolKind.Arrow:
                yield return With(baseStyle, s => { s.StartCap = LineCap.Arrow; });
                yield return With(baseStyle, s => { s.EndCap = LineCap.OpenArrow; s.Thickness = 4; });
                yield return With(baseStyle, s => { s.Dash = LineDash.Dash; s.Thickness = 4; });
                yield return With(baseStyle, s => { s.StartCap = LineCap.Circle; s.Thickness = 4; });
                break;
            case ToolKind.Shape:
                yield return With(baseStyle, s => { s.Fill = Color.FromArgb(0x55, 0xE8, 0x3A, 0x3A); });
                yield return With(baseStyle, s => { s.Shape = ShapeKind.Ellipse; });
                yield return With(baseStyle, s => { s.Fill = Palette.C("#2E7CF6"); s.Stroke = Colors.White; s.Thickness = 3; });
                break;
            case ToolKind.Callout:
                yield return With(baseStyle, s => { s.Callout = CalloutKind.Rounded; s.Fill = Palette.C("#FFF6C7"); s.Stroke = Palette.C("#F2B01E"); });
                yield return With(baseStyle, s => { s.Callout = CalloutKind.RoundBubble; });
                yield return With(baseStyle, s => { s.Callout = CalloutKind.Rectangle; s.Fill = Palette.C("#1D2025"); s.Stroke = Palette.C("#1D2025"); s.TextColor = Colors.White; s.CornerRadius = 0; });
                break;
            case ToolKind.Text:
                yield return With(baseStyle, s => { s.TextOutline = false; s.Shadow = true; s.TextColor = Colors.White; });
                yield return With(baseStyle, s => { s.TextOutline = false; s.Bold = false; s.TextColor = Palette.C("#1D1D1F"); s.FontSize = 20; });
                break;
        }
    }

    static ToolStyle With(ToolStyle s, Action<ToolStyle> f)
    {
        var c = s.Clone();
        f(c);
        return c;
    }
}

/// <summary>Builds the contextual property controls for the active tool or the selected objects.</summary>
public static class PropertiesPanel
{
    static readonly BitmapSource SampleBase = RasterOps.Render(80, 60, dc =>
    {
        dc.DrawRectangle(new LinearGradientBrush(Palette.C("#7AB8FF"), Palette.C("#FFB86B"), 45), null, new Rect(0, 0, 80, 60));
        for (int i = 0; i < 6; i++) dc.DrawRectangle(Brushes.White, null, new Rect(8, 8 + i * 8, 30 + (i * 7 % 30), 3));
    });

    public static void Build(Panel host, ToolKind kind, ToolStyle style, string header, bool isObject, Action<Action<ToolStyle>> apply, EditorWindow w)
    {
        host.Children.Add(new TextBlock { Text = header, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
        host.Children.Add(new TextBlock { Text = Hint(kind, isObject), Style = Res<Style>("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });

        var quick = DefaultStyles.QuickStyles(kind).ToList();
        App.Settings.UserQuickStyles.TryGetValue(kind, out var user);
        if (quick.Count > 0 || user?.Count > 0 || kind is not (ToolKind.Select or ToolKind.Crop or ToolKind.CutOut or ToolKind.Selection or ToolKind.Eraser or ToolKind.MagicWand))
        {
            if (kind is not (ToolKind.Select or ToolKind.Crop or ToolKind.CutOut or ToolKind.Selection or ToolKind.Eraser or ToolKind.MagicWand))
                if (kind != ToolKind.Favorites) AddQuickStyles(host, kind, style, quick, user, w);
        }

        void Set(Action<ToolStyle> a) => apply(a);

        switch (kind)
        {
            case ToolKind.Arrow:
            case ToolKind.Line:
                ColorRow(host, "Color", style.Stroke, c => Set(s => s.Stroke = c));
                SliderRow(host, "Grosor", 1, 40, style.Thickness, v => Set(s => s.Thickness = v));
                if (kind == ToolKind.Arrow)
                {
                    EnumChips(host, "Extremo inicial", style.StartCap, CapNames, v => Set(s => s.StartCap = v));
                    EnumChips(host, "Extremo final", style.EndCap, CapNames, v => Set(s => s.EndCap = v));
                }
                EnumChips(host, "Trazo", style.Dash, DashNames, v => Set(s => s.Dash = v));
                Toggle(host, "Curva (arrastre el tirador central)", style.Curved, v => Set(s => s.Curved = v));
                SliderRow(host, "Opacidad", 10, 100, style.Opacity * 100, v => Set(s => s.Opacity = v / 100), "%");
                ShadowRows(host, style, Set);
                break;

            case ToolKind.Text:
            case ToolKind.Callout:
                if (kind == ToolKind.Callout)
                {
                    EnumChips(host, "Forma del globo", style.Callout, CalloutNames, v => Set(s => s.Callout = v));
                    ColorRow(host, "Relleno", style.Fill, c => Set(s => s.Fill = c), allowNone: true);
                    ColorRow(host, "Borde", style.Stroke, c => Set(s => s.Stroke = c));
                    SliderRow(host, "Grosor del borde", 0, 16, style.Thickness, v => Set(s => s.Thickness = v));
                    SliderRow(host, "Redondeo", 0, 40, style.CornerRadius, v => Set(s => s.CornerRadius = v));
                }
                TextProps(host, style, Set);
                if (kind == ToolKind.Text)
                {
                    Toggle(host, "Contorno del texto", style.TextOutline, v => Set(s => s.TextOutline = v));
                    ColorRow(host, "Color del contorno", style.Stroke, c => Set(s => s.Stroke = c));
                    ColorRow(host, "Fondo", style.Fill, c => Set(s => { s.Fill = c; if (c.A > 0 && s.Callout == CalloutKind.None) s.Callout = CalloutKind.Rectangle; else if (c.A == 0) s.Callout = CalloutKind.None; }), allowNone: true);
                }
                SliderRow(host, "Opacidad", 10, 100, style.Opacity * 100, v => Set(s => s.Opacity = v / 100), "%");
                ShadowRows(host, style, Set);
                break;

            case ToolKind.Shape:
                EnumChips(host, "Forma", style.Shape, ShapeNames, v => Set(s => s.Shape = v));
                ColorRow(host, "Borde", style.Stroke, c => Set(s => s.Stroke = c), allowNone: true);
                ColorRow(host, "Relleno", style.Fill, c => Set(s => s.Fill = c), allowNone: true);
                SliderRow(host, "Grosor del borde", 0, 40, style.Thickness, v => Set(s => s.Thickness = v));
                SliderRow(host, "Redondeo de esquinas", 0, 60, style.CornerRadius, v => Set(s => s.CornerRadius = v));
                EnumChips(host, "Trazo", style.Dash, DashNames, v => Set(s => s.Dash = v));
                SliderRow(host, "Opacidad", 10, 100, style.Opacity * 100, v => Set(s => s.Opacity = v / 100), "%");
                ShadowRows(host, style, Set);
                break;

            case ToolKind.Step:
                EnumChips(host, "Numeración", style.StepFormat, StepFormatNames, v => Set(s => s.StepFormat = v));
                EnumChips(host, "Forma", style.StepShape, StepShapeNames, v => Set(s => s.StepShape = v));
                ColorRow(host, "Color", style.Fill, c => Set(s => { s.Fill = c; s.Stroke = c; }));
                ColorRow(host, "Color del número", style.TextColor, c => Set(s => s.TextColor = c));
                SliderRow(host, "Tamaño", 16, 160, style.Size, v => Set(s => s.Size = v));
                Toggle(host, "Contorno blanco", style.Thickness > 0, v => Set(s => s.Thickness = v ? 2 : 0));
                ShadowRows(host, style, Set);
                var reset = new Button { Content = "Reiniciar la secuencia (1, 2, 3…)", Style = Res<Style>("OutlineButton"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                reset.Click += (_, _) => w.ResetStepSequence();
                host.Children.Add(reset);
                if (isObject && w.EditorCanvas.Selection.FirstOrDefault() is StepAnnotation step)
                    NumberRow(host, "Número de este paso", step.Number, n => w.SetStepNumber(n));
                break;

            case ToolKind.Pen:
                ColorRow(host, "Color", style.Stroke, c => Set(s => s.Stroke = c));
                SliderRow(host, "Grosor", 1, 40, style.Thickness, v => Set(s => s.Thickness = v));
                EnumChips(host, "Trazo", style.Dash, DashNames, v => Set(s => s.Dash = v));
                SliderRow(host, "Opacidad", 10, 100, style.Opacity * 100, v => Set(s => s.Opacity = v / 100), "%");
                ShadowRows(host, style, Set);
                Caption(host, "Mantenga Mayús para trazar líneas rectas.");
                break;

            case ToolKind.Highlighter:
                ColorRow(host, "Color", style.Stroke, c => Set(s => s.Stroke = c));
                SliderRow(host, "Grosor", 4, 80, style.Thickness, v => Set(s => s.Thickness = v));
                SliderRow(host, "Opacidad", 10, 50, style.Opacity * 100, v => Set(s => s.Opacity = v / 100), "%");
                Caption(host, "Mantenga Mayús para resaltar en línea recta.");
                break;

            case ToolKind.Blur:
                EnumChips(host, "Tipo", style.Redact, RedactNames, v => Set(s => s.Redact = v));
                SliderRow(host, "Intensidad", 1, 100, style.Intensity, v => Set(s => s.Intensity = v), "%");
                ColorRow(host, "Color (sólido)", style.Fill, c => Set(s => s.Fill = c));
                Caption(host, "El difuminado se calcula a partir de la imagen original, así que se puede mover y redimensionar sin perder calidad.");
                break;

            case ToolKind.Magnify:
                EnumChips(host, "Forma", style.Shape, new Dictionary<ShapeKind, string> { [ShapeKind.Ellipse] = "Círculo", [ShapeKind.Rectangle] = "Rectángulo", [ShapeKind.RoundedRectangle] = "Redondeado" }, v => Set(s => s.Shape = v));
                SliderRow(host, "Aumento", 1.25, 8, style.Zoom, v => Set(s => s.Zoom = v), "×", 2);
                ColorRow(host, "Borde", style.Stroke, c => Set(s => s.Stroke = c));
                SliderRow(host, "Grosor del borde", 0, 16, style.Thickness, v => Set(s => s.Thickness = v));
                ShadowRows(host, style, Set);
                break;

            case ToolKind.Spotlight:
                EnumChips(host, "Forma", style.Shape, new Dictionary<ShapeKind, string> { [ShapeKind.RoundedRectangle] = "Rectángulo", [ShapeKind.Ellipse] = "Elipse" }, v => Set(s => s.Shape = v));
                SliderRow(host, "Oscurecer el resto", 5, 100, style.Intensity, v => Set(s => s.Intensity = v), "%");
                ColorRow(host, "Borde", style.Stroke, c => Set(s => s.Stroke = c));
                SliderRow(host, "Grosor del borde", 0, 4, style.Thickness, v => Set(s => s.Thickness = v));
                break;

            case ToolKind.Stamp:
                StampGallery(host, style, Set);
                ColorRow(host, "Color", style.Stroke, c => Set(s => s.Stroke = c));
                ColorRow(host, "Fondo", style.Fill, c => Set(s => s.Fill = c), allowNone: true);
                ColorRow(host, "Texto (etiquetas)", style.TextColor, c => Set(s => s.TextColor = c));
                SliderRow(host, "Tamaño al hacer clic", 16, 200, style.Size, v => Set(s => s.Size = v));
                SliderRow(host, "Opacidad", 10, 100, style.Opacity * 100, v => Set(s => s.Opacity = v / 100), "%");
                ShadowRows(host, style, Set);
                break;

            case ToolKind.CutOut:
                EnumChips(host, "Dirección", style.Vertical, new Dictionary<bool, string> { [true] = "Vertical", [false] = "Horizontal" }, v => Set(s => s.Vertical = v));
                EnumChips(host, "Marca de la unión", style.EdgeStyle, new Dictionary<string, string> { ["straight"] = "Ninguna", ["saw"] = "Dientes", ["wave"] = "Onda" }, v => Set(s => s.EdgeStyle = v));
                break;

            case ToolKind.Selection:
                EnumChips(host, "Forma de la selección", style.SelectionShape, new Dictionary<string, string> { ["rect"] = "Rectángulo", ["ellipse"] = "Elipse", ["lasso"] = "Mano alzada" }, v => Set(s => s.SelectionShape = v));
                Toggle(host, "Rellenar el hueco al mover (relleno inteligente)", style.EdgeStyle == "smart", v => Set(s => s.EdgeStyle = v ? "smart" : "straight"));
                var editText = new Button { Content = "Editar el texto de la selección", Style = Res<Style>("OutlineButton"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                editText.Click += (_, _) => w.EditTextInSelection();
                host.Children.Add(editText);
                break;

            case ToolKind.Fill:
                ColorRow(host, "Color de relleno", style.Fill, c => Set(s => s.Fill = c), allowNone: true);
                SliderRow(host, "Tolerancia", 0, 100, style.Tolerance, v => Set(s => s.Tolerance = v), "%");
                break;

            case ToolKind.Favorites:
                FavoritesGallery(host, w);
                break;

            case ToolKind.Eraser:
                SliderRow(host, "Tamaño", 2, 80, style.Thickness, v => Set(s => s.Thickness = v));
                break;

            case ToolKind.MagicWand:
                SliderRow(host, "Tolerancia", 0, 100, style.Tolerance, v => Set(s => s.Tolerance = v), "%");
                break;
        }
    }

    public static void AddObjectActions(Panel host, EditorWindow w)
    {
        host.Children.Add(new TextBlock { Text = "Organizar", Style = Res<Style>("SectionHeader") });
        var wrap = new WrapPanel();
        void B(string glyph, string tip, Action a)
        {
            var b = new Button { Style = Res<Style>("OutlineButton"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 6), ToolTip = tip, Content = Icon(glyph, 14) };
            b.Click += (_, _) => a();
            wrap.Children.Add(b);
        }
        B("\uE74D", "Eliminar (Supr)", w.DeleteSelected);
        B("\uE8C8", "Duplicar (Ctrl+D)", w.DuplicateSelected);
        B("\uE70E", "Traer al frente", w.BringSelectedToFront);
        B("\uE70D", "Enviar al fondo", w.SendSelectedToBack);
        host.Children.Add(wrap);
    }

    // ---- building blocks -----------------------------------------------------------------------

    static string Hint(ToolKind k, bool isObject) => isObject ? "Los cambios se aplican a la selección y se recuerdan para la herramienta." : k switch
    {
        ToolKind.Select => "Seleccione, mueva y redimensione objetos. Arrastre en vacío para seleccionar varios. Espacio + arrastrar para desplazarse.",
        ToolKind.Arrow => "Arrastre para dibujar una flecha. Mayús limita el ángulo a pasos de 15°.",
        ToolKind.Text => "Haga clic o arrastre para crear un cuadro de texto. Doble clic sobre un texto para editarlo.",
        ToolKind.Callout => "Arrastre para crear un globo; mueva el tirador amarillo para orientar la punta.",
        ToolKind.Shape => "Arrastre para dibujar. Mayús dibuja proporciones iguales.",
        ToolKind.Step => "Haga clic para colocar pasos numerados consecutivos.",
        ToolKind.Highlighter => "Arrastre sobre el contenido que quiera destacar.",
        ToolKind.Blur => "Arrastre sobre datos sensibles para ocultarlos.",
        ToolKind.Crop => "Ajuste el marco y pulse Intro o «Aplicar recorte». Doble clic dentro también recorta.",
        ToolKind.CutOut => "Arrastre sobre la franja que quiera eliminar; las dos partes se unen automáticamente.",
        ToolKind.Selection => "Seleccione píxeles: Supr borra, Ctrl+C copia, arrastre dentro para mover. Alt + arrastrar rellena el hueco.",
        ToolKind.Fill => "Haga clic para rellenar un área de color uniforme.",
        ToolKind.Eraser => "Borre píxeles de la imagen base (los objetos no se ven afectados).",
        ToolKind.MagicWand => "Haga clic en un área de color similar y pulse Supr para borrarla.",
        ToolKind.Magnify => "Arrastre para crear una lupa sobre un detalle.",
        ToolKind.Spotlight => "Arrastre para destacar una zona y oscurecer el resto.",
        ToolKind.Stamp => "Elija un sello y haga clic para colocarlo, o arrastre para darle tamaño.",
        ToolKind.Favorites => "Sus estilos favoritos de todas las herramientas en un solo sitio: haga clic para usarlos.",
        _ => "",
    };

    static readonly Dictionary<LineCap, string> CapNames = new() { [LineCap.None] = "—", [LineCap.Arrow] = "▶", [LineCap.OpenArrow] = "›", [LineCap.Circle] = "●", [LineCap.Square] = "■", [LineCap.Bar] = "|" };
    static readonly Dictionary<LineDash, string> DashNames = new() { [LineDash.Solid] = "Continuo", [LineDash.Dash] = "Discontinuo", [LineDash.Dot] = "Puntos" };
    static readonly Dictionary<CalloutKind, string> CalloutNames = new() { [CalloutKind.Bubble] = "Bocadillo", [CalloutKind.RoundBubble] = "Ovalado", [CalloutKind.Rounded] = "Redondeado", [CalloutKind.Rectangle] = "Rectángulo", [CalloutKind.None] = "Sin forma" };
    static readonly Dictionary<ShapeKind, string> ShapeNames = new() { [ShapeKind.Rectangle] = "▭", [ShapeKind.RoundedRectangle] = "▢", [ShapeKind.Ellipse] = "◯", [ShapeKind.Triangle] = "△", [ShapeKind.Diamond] = "◇", [ShapeKind.Star] = "☆" };
    static readonly Dictionary<StepFormat, string> StepFormatNames = new() { [StepFormat.Number] = "1 2 3", [StepFormat.UpperLetter] = "A B C", [StepFormat.LowerLetter] = "a b c", [StepFormat.Roman] = "I II III" };
    static readonly Dictionary<StepShape, string> StepShapeNames = new() { [StepShape.Circle] = "●", [StepShape.RoundedSquare] = "▢", [StepShape.Square] = "■", [StepShape.Pin] = "📍" };
    static readonly Dictionary<RedactKind, string> RedactNames = new() { [RedactKind.Pixelate] = "Pixelar", [RedactKind.Blur] = "Desenfocar", [RedactKind.Solid] = "Tapar" };

    static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    static TextBlock Icon(string glyph, double size) => new() { Text = glyph, FontFamily = Res<FontFamily>("IconFont"), FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    static void Caption(Panel host, string text) => host.Children.Add(new TextBlock { Text = text, Style = Res<Style>("Caption"), TextWrapping = TextWrapping.Wrap });

    static void Label(Panel host, string text) => host.Children.Add(new TextBlock { Text = text, Style = Res<Style>("Caption") });

    static void AddQuickStyles(Panel host, ToolKind kind, ToolStyle current, List<ToolStyle> builtIn, List<NamedStyle>? user, EditorWindow w)
    {
        var head = new DockPanel { Margin = new Thickness(0, 12, 0, 6) };
        var themeBtn = new Button { Content = "Tema: " + ToolTheme.Active.Name, Style = Res<Style>("FlatButton"), Padding = new Thickness(6, 2, 6, 2), ToolTip = "Elegir o editar el tema de colores de los estilos rápidos" };
        themeBtn.Click += (_, _) => { new ThemeEditorDialog { Owner = w }.ShowDialog(); w.SelectTool(w.CurrentKind); };
        DockPanel.SetDock(themeBtn, Dock.Right);
        head.Children.Add(themeBtn);
        head.Children.Add(new TextBlock { Text = "Estilos rápidos", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        host.Children.Add(head);
        var wrap = new WrapPanel();
        foreach (var s in builtIn) wrap.Children.Add(StyleSwatch(kind, s, () => w.ApplyQuickStyle(kind, s), null));
        if (user != null)
            foreach (var ns in user.ToList())
                wrap.Children.Add(StyleSwatch(kind, ns.Style, () => w.ApplyQuickStyle(kind, ns.Style), () =>
                {
                    user.Remove(ns);
                    App.Settings.Save();
                    w.SelectTool(w.CurrentKind);
                }));
        var add = new Button { Width = 58, Height = 44, Margin = new Thickness(0, 0, 6, 6), Style = Res<Style>("OutlineButton"), Padding = new Thickness(0), ToolTip = "Guardar el estilo actual como estilo rápido", Content = Icon("\uE710", 14) };
        add.Click += (_, _) =>
        {
            if (!App.Settings.UserQuickStyles.TryGetValue(kind, out var list)) App.Settings.UserQuickStyles[kind] = list = new List<NamedStyle>();
            list.Add(new NamedStyle { Name = $"Estilo {list.Count + 1}", Style = current.Clone() });
            App.Settings.Save();
            w.SelectTool(w.CurrentKind);
        };
        wrap.Children.Add(add);
        host.Children.Add(wrap);
    }

    static FrameworkElement StyleSwatch(ToolKind kind, ToolStyle style, Action click, Action? remove)
    {
        var b = new Button
        {
            Width = 58, Height = 44, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(2),
            Style = Res<Style>("OutlineButton"),
            Content = new Image { Source = Preview(kind, style), Stretch = Stretch.Uniform },
        };
        b.Click += (_, _) => click();
        var cm = new ContextMenu();
        var fav = new MenuItem { Header = "Añadir a favoritos" };
        fav.Click += (_, _) => { App.Settings.Favorites.Add(new FavoriteStyle { Kind = kind, Style = style.Clone() }); App.Settings.Save(); };
        cm.Items.Add(fav);
        if (remove != null)
        {
            var mi = new MenuItem { Header = "Eliminar este estilo rápido" };
            mi.Click += (_, _) => remove();
            cm.Items.Add(mi);
            b.ToolTip = "Estilo personalizado";
        }
        b.ContextMenu = cm;
        return b;
    }

    public static ImageSource Preview(ToolKind kind, ToolStyle style)
    {
        var s = style.Clone();
        var ctx = new RenderContext { BaseImage = SampleBase };
        // Scale thickness-like values so previews read well at 56×40.
        s.Thickness = Math.Min(s.Thickness, 8);
        s.FontSize = 26;
        s.Size = 30;
        Annotation a = kind switch
        {
            ToolKind.Arrow or ToolKind.Line => new LineAnnotation { Start = new Point(10, 50), End = new Point(70, 12), Style = s },
            ToolKind.Text => new TextAnnotation { Box = new Rect(4, 8, 80, 44), Text = "Aa", Style = s },
            ToolKind.Callout => new TextAnnotation { Box = new Rect(8, 6, 64, 32), Text = "Aa", Tail = new Point(16, 56), Style = With(s, x => { x.FontSize = 18; x.Thickness = Math.Min(x.Thickness, 3); }) },
            ToolKind.Shape => new ShapeAnnotation { Box = new Rect(10, 10, 60, 40), Style = s },
            ToolKind.Step => new StepAnnotation { Center = new Point(40, 30), Number = 1, Style = With(s, x => x.Size = 40) },
            ToolKind.Pen or ToolKind.Highlighter => new PenAnnotation { Highlighter = kind == ToolKind.Highlighter, Points = new() { new(8, 44), new(22, 20), new(40, 40), new(58, 16), new(72, 30) }, Style = With(s, x => x.Thickness = kind == ToolKind.Highlighter ? 14 : Math.Min(style.Thickness, 6)) },
            ToolKind.Blur => new RedactAnnotation { Box = new Rect(0, 0, 80, 60), Style = s },
            ToolKind.Magnify => new MagnifyAnnotation { Box = new Rect(16, 6, 48, 48), Style = s },
            ToolKind.Spotlight => new SpotlightAnnotation { Box = new Rect(20, 14, 40, 32), Style = s },
            ToolKind.Stamp => new StampAnnotation { Box = new Rect(16, 6, 48, 48), Style = s },
            ToolKind.Fill => new ShapeAnnotation { Box = new Rect(10, 10, 60, 40), Style = With(s, x => { x.Fill = style.Fill; x.Stroke = Colors.Transparent; x.Thickness = 0; x.Shadow = false; }) },
            _ => new ShapeAnnotation { Box = new Rect(10, 10, 60, 40), Style = s },
        };
        var dg = new DrawingGroup();
        using (var dc = dg.Open())
        {
            bool darkText = kind == ToolKind.Text && style.TextColor.R * 0.3 + style.TextColor.G * 0.59 + style.TextColor.B * 0.11 < 90;
            Brush backdrop = kind is ToolKind.Blur or ToolKind.Magnify or ToolKind.Spotlight ? new ImageBrush(SampleBase) : darkText ? new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)) : Brushes.Transparent;
            dc.DrawRoundedRectangle(backdrop, null, new Rect(0, 0, 80, 60), 6, 6);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, 80, 60)));
            a.Render(dc, ctx);
            dc.Pop();
        }
        var img = new DrawingImage(dg);
        img.Freeze();
        return img;
    }

    static ToolStyle With(ToolStyle s, Action<ToolStyle> f) { var c = s.Clone(); f(c); return c; }

    public static void ColorRow(Panel host, string label, Color value, Action<Color> changed, bool allowNone = false)
    {
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var picker = new ColorPicker(value, allowNone) { Width = 64, Height = 28 };
        picker.ColorChanged += (_, c) => changed(c);
        DockPanel.SetDock(picker, Dock.Right);
        row.Children.Add(picker);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        host.Children.Add(row);
    }

    public static void SliderRow(Panel host, string label, double min, double max, double value, Action<double> changed, string unit = "", int decimals = 0)
    {
        var head = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var valueText = new TextBlock { Foreground = Res<Brush>("Fg.Secondary"), Text = Fmt(value, decimals, unit) };
        DockPanel.SetDock(valueText, Dock.Right);
        head.Children.Add(valueText);
        head.Children.Add(new TextBlock { Text = label });
        host.Children.Add(head);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), SmallChange = 1, LargeChange = (max - min) / 10 };
        slider.ValueChanged += (_, e) =>
        {
            var v = decimals == 0 ? Math.Round(e.NewValue) : Math.Round(e.NewValue, decimals);
            valueText.Text = Fmt(v, decimals, unit);
            changed(v);
        };
        host.Children.Add(slider);
    }

    static string Fmt(double v, int decimals, string unit) => (decimals == 0 ? Math.Round(v).ToString() : v.ToString("0." + new string('0', decimals))) + (unit.Length > 0 ? " " + unit : "");

    public static void Toggle(Panel host, string label, bool value, Action<bool> changed)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 10, 0, 0) };
        cb.Click += (_, _) => changed(cb.IsChecked == true);
        host.Children.Add(cb);
    }

    public static void EnumChips<T>(Panel host, string label, T value, IReadOnlyDictionary<T, string> names, Action<T> changed) where T : notnull
    {
        Label(host, label);
        var wrap = new WrapPanel();
        string group = Guid.NewGuid().ToString();
        foreach (var (k, text) in names)
        {
            var rb = new RadioButton { Content = text, GroupName = group, Style = Res<Style>("ChipRadio"), IsChecked = EqualityComparer<T>.Default.Equals(k, value) };
            var key = k;
            rb.Checked += (_, _) => changed(key);
            wrap.Children.Add(rb);
        }
        host.Children.Add(wrap);
    }

    static void NumberRow(Panel host, string label, int value, Action<int> changed)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var tb = new TextBox { Width = 64, Text = value.ToString() };
        tb.LostFocus += (_, _) => { if (int.TryParse(tb.Text, out var n)) changed(n); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter && int.TryParse(tb.Text, out var n)) changed(n); };
        DockPanel.SetDock(tb, Dock.Right);
        row.Children.Add(tb);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        host.Children.Add(row);
    }

    static void TextProps(Panel host, ToolStyle style, Action<Action<ToolStyle>> set)
    {
        Label(host, "Fuente");
        var fonts = new ComboBox { IsEditable = false, MaxDropDownHeight = 320 };
        var families = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(f => f).ToList();
        foreach (var f in families) fonts.Items.Add(new ComboBoxItem { Content = f, FontFamily = new FontFamily(f), Tag = f });
        fonts.SelectedItem = fonts.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == style.FontFamily);
        fonts.SelectionChanged += (_, _) => { if (fonts.SelectedItem is ComboBoxItem i) set(s => s.FontFamily = (string)i.Tag); };
        host.Children.Add(fonts);
        SliderRow(host, "Tamaño", 8, 160, style.FontSize, v => set(s => s.FontSize = v), "pt");

        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        ToggleButton TB(string glyph, string tip, bool on, Action<bool> f)
        {
            var t = new ToggleButton { Content = Icon(glyph, 14), IsChecked = on, ToolTip = tip, Width = 34, Height = 30, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(0) };
            t.Click += (_, _) => f(t.IsChecked == true);
            return t;
        }
        row.Children.Add(TB("\uE8DD", "Negrita", style.Bold, v => set(s => s.Bold = v)));
        row.Children.Add(TB("\uE8DB", "Cursiva", style.Italic, v => set(s => s.Italic = v)));
        row.Children.Add(TB("\uE8DC", "Subrayado", style.Underline, v => set(s => s.Underline = v)));
        row.Children.Add(new Border { Width = 10 });
        string g = Guid.NewGuid().ToString();
        foreach (var (al, glyph) in new[] { (TextAlign.Left, "\uE8E4"), (TextAlign.Center, "\uE8E3"), (TextAlign.Right, "\uE8E2") })
        {
            var rb = new RadioButton { GroupName = g, Content = Icon(glyph, 14), IsChecked = style.Align == al, Style = Res<Style>("ChipRadio"), MinWidth = 34 };
            var a = al;
            rb.Checked += (_, _) => set(s => s.Align = a);
            row.Children.Add(rb);
        }
        host.Children.Add(row);
        ColorRow(host, "Color del texto", style.TextColor, c => set(s => s.TextColor = c));
    }

    static void FavoritesGallery(Panel host, EditorWindow w)
    {
        var favs = App.Settings.Favorites;
        if (favs.Count == 0)
        {
            Caption(host, "Todavía no hay favoritos. Haga clic con el botón derecho en un estilo rápido de cualquier herramienta y elija «Añadir a favoritos».");
            return;
        }
        var wrap = new WrapPanel();
        foreach (var f in favs.ToList())
        {
            var fav = f;
            var def = EditorWindow.ToolDefs.First(d => d.Kind == fav.Kind);
            var b = new Button { Width = 58, Height = 44, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(2), Style = Res<Style>("OutlineButton"), ToolTip = def.Label,
                Content = new Image { Source = Preview(fav.Kind, fav.Style), Stretch = Stretch.Uniform } };
            b.Click += (_, _) => { w.SelectTool(fav.Kind); w.ApplyQuickStyle(fav.Kind, fav.Style); };
            var cm = new ContextMenu();
            var del = new MenuItem { Header = "Quitar de favoritos" };
            del.Click += (_, _) => { favs.Remove(fav); App.Settings.Save(); w.SelectTool(ToolKind.Favorites); };
            cm.Items.Add(del);
            b.ContextMenu = cm;
            wrap.Children.Add(b);
        }
        host.Children.Add(wrap);
    }

    static void ShadowRows(Panel host, ToolStyle style, Action<Action<ToolStyle>> set)
    {
        var details = new StackPanel { Margin = new Thickness(12, 0, 0, 0), Visibility = style.Shadow ? Visibility.Visible : Visibility.Collapsed };
        var cb = new CheckBox { Content = "Sombra", IsChecked = style.Shadow, Margin = new Thickness(0, 10, 0, 0) };
        cb.Click += (_, _) => { set(s => s.Shadow = cb.IsChecked == true); details.Visibility = cb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; };
        host.Children.Add(cb);
        SliderRow(details, "Distancia", 0, 30, style.ShadowDistance, v => set(s => s.ShadowDistance = v), "px");
        SliderRow(details, "Ángulo", 0, 359, style.ShadowAngle, v => set(s => s.ShadowAngle = v), "°");
        SliderRow(details, "Desenfoque", 1, 8, style.ShadowBlur, v => set(s => s.ShadowBlur = v));
        SliderRow(details, "Opacidad", 5, 100, style.ShadowOpacity, v => set(s => s.ShadowOpacity = v), "%");
        host.Children.Add(details);
    }

    static readonly (string glyph, string tip)[] IconStamps =
    {
        ("\uE735", "Estrella"), ("\uE73E", "Correcto"), ("\uE711", "Incorrecto"), ("\uE7BA", "Aviso"), ("\uE946", "Información"),
        ("\uE897", "Ayuda"), ("\uEB51", "Corazón"), ("\uE8E1", "Me gusta"), ("\uE8E0", "No me gusta"), ("\uE945", "Rayo"),
        ("\uE7C1", "Bandera"), ("\uE718", "Chincheta"), ("\uE72E", "Candado"), ("\uE8D7", "Llave"), ("\uE715", "Correo"),
        ("\uE717", "Teléfono"), ("\uE80F", "Inicio"), ("\uE713", "Engranaje"), ("\uE721", "Buscar"), ("\uE77B", "Persona"),
        ("\uE8BD", "Chat"), ("\uE787", "Calendario"), ("\uE823", "Reloj"), ("\uE8B7", "Carpeta"), ("\uE8A5", "Documento"),
        ("\uE722", "Cámara"), ("\uE714", "Vídeo"), ("\uE7F4", "Pantalla"), ("\uE962", "Ratón"), ("\uE765", "Teclado"),
        ("\uE72A", "Flecha derecha"), ("\uE72B", "Flecha izquierda"), ("\uE74A", "Flecha arriba"), ("\uE74B", "Flecha abajo"), ("\uEA80", "Bombilla"),
    };

    static readonly string[] BadgeStamps = { "NUEVO", "APROBADO", "BORRADOR", "IMPORTANTE", "PENDIENTE", "HECHO", "OK", "BETA", "1", "2", "3" };

    static void StampGallery(Panel host, ToolStyle style, Action<Action<ToolStyle>> set)
    {
        host.Children.Add(new TextBlock { Text = "Sellos", Style = Res<Style>("SectionHeader") });
        var wrap = new WrapPanel();
        string g = Guid.NewGuid().ToString();
        foreach (var (glyph, tip) in IconStamps)
        {
            var rb = new RadioButton { GroupName = g, Style = Res<Style>("ChipRadio"), ToolTip = tip, Width = 38, Height = 38, Content = Icon(glyph, 18), IsChecked = style.Glyph == glyph };
            var gl = glyph;
            rb.Checked += (_, _) => set(s => s.Glyph = gl);
            wrap.Children.Add(rb);
        }
        host.Children.Add(wrap);
        Label(host, "Cursores");
        var cursors = new WrapPanel();
        foreach (var (key, tip, label) in new[] { ("cursor:arrow", "Puntero", "↖"), ("cursor:hand", "Mano", "☝"), ("cursor:ibeam", "Texto", "I") })
        {
            var rb = new RadioButton { GroupName = g, Style = Res<Style>("ChipRadio"), ToolTip = tip, Width = 38, Height = 38, Content = new TextBlock { Text = label, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center }, IsChecked = style.Glyph == key };
            var k = key;
            rb.Checked += (_, _) => set(s => { s.Glyph = k; s.Fill = Colors.White; });
            cursors.Children.Add(rb);
        }
        host.Children.Add(cursors);
        Label(host, "Etiquetas");
        var badges = new WrapPanel();
        foreach (var text in BadgeStamps)
        {
            var rb = new RadioButton { GroupName = g, Style = Res<Style>("ChipRadio"), Content = new TextBlock { Text = text, FontWeight = FontWeights.Bold, FontSize = 11 }, IsChecked = style.Glyph == text };
            var t = text;
            rb.Checked += (_, _) => set(s => { s.Glyph = t; if (s.Fill.A == 0) s.Fill = s.Stroke; });
            badges.Children.Add(rb);
        }
        var custom = new TextBox { Width = 120, Margin = new Thickness(0, 0, 4, 4), ToolTip = "Escriba un texto y pulse Intro para crear su propia etiqueta" };
        custom.KeyDown += (_, e) => { if (e.Key == Key.Enter && custom.Text.Trim().Length > 0) { var t = custom.Text.Trim().ToUpperInvariant(); set(s => { s.Glyph = t; if (s.Fill.A == 0) s.Fill = s.Stroke; }); } };
        badges.Children.Add(custom);
        host.Children.Add(badges);
    }
}

/// <summary>Swatch button that opens a palette popup with recent colours, transparency and a full colour dialog.</summary>
public sealed class ColorPicker : Button
{
    Color _color;
    readonly bool _allowNone;
    readonly Border _swatch;

    public event EventHandler<Color>? ColorChanged;

    public ColorPicker(Color initial, bool allowNone)
    {
        _color = initial;
        _allowNone = allowNone;
        Style = (Style)Application.Current.FindResource("OutlineButton");
        Padding = new Thickness(3);
        _swatch = new Border { Width = 52, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)) };
        Content = _swatch;
        UpdateSwatch();
        Click += (_, _) => OpenPopup();
    }

    void UpdateSwatch()
    {
        _swatch.Background = _color.A == 0 ? NoneBrush() : new SolidColorBrush(_color);
        ToolTip = _color.A == 0 ? "Sin color" : ToolStyle.ToHex(_color);
    }

    static Brush NoneBrush()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 10, 10))));
        g.Children.Add(new GeometryDrawing(null, new Pen(Brushes.Red, 1.2), new LineGeometry(new Point(0, 10), new Point(10, 0))));
        return new DrawingBrush(g) { Stretch = Stretch.Fill };
    }

    void Set(Color c, Popup? popup)
    {
        _color = c;
        UpdateSwatch();
        if (c.A > 0)
        {
            var hex = ToolStyle.ToHex(c);
            App.Settings.RecentColors.Remove(hex);
            App.Settings.RecentColors.Insert(0, hex);
            if (App.Settings.RecentColors.Count > 8) App.Settings.RecentColors.RemoveAt(8);
        }
        ColorChanged?.Invoke(this, c);
        if (popup != null) popup.IsOpen = false;
    }

    void OpenPopup()
    {
        var popup = new Popup { PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var panel = new StackPanel { Margin = new Thickness(10) };
        Button Swatch(Color c)
        {
            var b = new Button { Width = 24, Height = 24, Margin = new Thickness(2), Padding = new Thickness(0), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                ToolTip = ToolStyle.ToHex(c) };
            b.Template = SwatchTemplate();
            b.Background = new SolidColorBrush(c);
            b.Click += (_, _) => Set(c, popup);
            return b;
        }
        var grid = new UniformGrid { Columns = 8 };
        foreach (var c in Palette.Colors) grid.Children.Add(Swatch(c));
        panel.Children.Add(grid);
        if (App.Settings.RecentColors.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Recientes", Style = (Style)Application.Current.FindResource("Caption") });
            var recent = new WrapPanel();
            foreach (var h in App.Settings.RecentColors) recent.Children.Add(Swatch(ToolStyle.FromHex(h)));
            panel.Children.Add(recent);
        }
        var opacityRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        opacityRow.Children.Add(new TextBlock { Text = "Transparencia", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = (Brush)Application.Current.FindResource("Fg.Secondary") });
        var alpha = new Slider { Minimum = 0, Maximum = 255, Value = _color.A == 0 ? 255 : _color.A, Width = 120 };
        alpha.ValueChanged += (_, e) => { if (_color.A > 0 || e.NewValue > 0) Set(Color.FromArgb((byte)e.NewValue, _color.R, _color.G, _color.B), null); };
        opacityRow.Children.Add(alpha);
        panel.Children.Add(opacityRow);
        var hexRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var hex = new TextBox { Text = ToolStyle.ToHex(_color), Width = 96 };
        hex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            try { Set(ToolStyle.FromHex(hex.Text.Trim()), popup); } catch { hex.Text = ToolStyle.ToHex(_color); }
        };
        hexRow.Children.Add(hex);
        var more = new Button { Content = "Más colores…", Margin = new Thickness(6, 0, 0, 0) };
        more.Click += (_, _) => { popup.IsOpen = false; var c = PickWithDialog(_color); if (c != null) Set(c.Value, null); };
        hexRow.Children.Add(more);
        var dropper = new Button { Content = new TextBlock { Text = "\uEF3C", FontFamily = (FontFamily)Application.Current.FindResource("IconFont") }, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Cuentagotas: tomar un color de la pantalla" };
        dropper.Click += async (_, _) =>
        {
            popup.IsOpen = false;
            var c = await Capture.CaptureService.PickScreenColorAsync();
            if (c != null) Set(c.Value, null);
        };
        hexRow.Children.Add(dropper);
        panel.Children.Add(hexRow);
        if (_allowNone)
        {
            var none = new Button { Content = "Sin color", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            none.Click += (_, _) => Set(Colors.Transparent, popup);
            panel.Children.Add(none);
        }
        popup.Child = new Border
        {
            Background = (Brush)Application.Current.FindResource("Bg.Raised"),
            BorderBrush = (Brush)Application.Current.FindResource("Line"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = panel,
        };
        popup.IsOpen = true;
    }

    static ControlTemplate? _swatchTemplate;
    static ControlTemplate SwatchTemplate()
    {
        if (_swatchTemplate != null) return _swatchTemplate;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        return _swatchTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    public static Color? PickWithDialog(Color initial)
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(initial.A == 0 ? 255 : initial.A, initial.R, initial.G, initial.B) };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
        return Color.FromArgb(255, dlg.Color.R, dlg.Color.G, dlg.Color.B);
    }
}
