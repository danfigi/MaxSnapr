using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaxSnapr.Editor.Raster;

namespace MaxSnapr.Views;

/// <summary>Image effects tab: each block has its own settings and an "Aplicar" button (undoable).</summary>
public static class EffectsPanel
{
    static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    public static void Build(Panel host, EditorWindow w)
    {
        host.Children.Add(new TextBlock { Text = "Efectos de imagen", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
        host.Children.Add(new TextBlock { Text = "Se aplican a la imagen completa. «Incluir objetos» los integra en la imagen; si no, los objetos siguen siendo editables.", Style = Res<Style>("Caption"), TextWrapping = TextWrapping.Wrap });
        var include = new CheckBox { Content = "Incluir objetos", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        host.Children.Add(include);
        bool Inc() => include.IsChecked == true;

        // Border
        Section(host, "Borde");
        int borderWidth = 6; Color borderColor = Palette.C("#1D1D1F");
        PropertiesPanel.SliderRow(host, "Ancho", 1, 80, borderWidth, v => borderWidth = (int)v, "px");
        PropertiesPanel.ColorRow(host, "Color", borderColor, c => borderColor = c);
        ApplyButton(host, () => w.ApplyEffect(img => RasterOps.Border(img, borderWidth, borderColor), Inc(), new Vector(borderWidth, borderWidth)));

        // Shadow
        Section(host, "Sombra");
        int shadowSize = 18, shadowOffset = 6; double shadowOpacity = 45;
        PropertiesPanel.SliderRow(host, "Desenfoque", 2, 60, shadowSize, v => shadowSize = (int)v, "px");
        PropertiesPanel.SliderRow(host, "Desplazamiento", 0, 40, shadowOffset, v => shadowOffset = (int)v, "px");
        PropertiesPanel.SliderRow(host, "Opacidad", 5, 100, shadowOpacity, v => shadowOpacity = v, "%");
        ApplyButton(host, () =>
        {
            int pad = shadowSize + shadowOffset;
            w.ApplyEffect(img => RasterOps.Shadow(img, shadowSize, shadowOffset, Color.FromArgb((byte)(shadowOpacity * 2.55), 0, 0, 0)), Inc(), new Vector(pad, pad));
        });

        // Edges
        Section(host, "Bordes recortados");
        string edgeStyle = "torn"; int depth = 14;
        bool top = false, right = false, bottom = true, left = false;
        PropertiesPanel.EnumChips(host, "Estilo", edgeStyle, new System.Collections.Generic.Dictionary<string, string> { ["torn"] = "Rasgado", ["saw"] = "Serrado", ["jag"] = "Dentado", ["wave"] = "Ondulado", ["fade"] = "Fundido", ["bevel"] = "Bisel" }, v => edgeStyle = v);
        PropertiesPanel.SliderRow(host, "Profundidad", 4, 60, depth, v => depth = (int)v, "px");
        var sides = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        CheckBox Side(string label, bool on, Action<bool> f) { var c = new CheckBox { Content = label, IsChecked = on, Margin = new Thickness(0, 4, 14, 0) }; c.Click += (_, _) => f(c.IsChecked == true); return c; }
        sides.Children.Add(Side("Arriba", top, v => top = v));
        sides.Children.Add(Side("Derecha", right, v => right = v));
        sides.Children.Add(Side("Abajo", bottom, v => bottom = v));
        sides.Children.Add(Side("Izquierda", left, v => left = v));
        host.Children.Add(sides);
        ApplyButton(host, () =>
        {
            var s = (top ? EdgeSides.Top : 0) | (right ? EdgeSides.Right : 0) | (bottom ? EdgeSides.Bottom : 0) | (left ? EdgeSides.Left : 0);
            if (s == EdgeSides.None && edgeStyle != "bevel") return;
            w.ApplyEffect(img => edgeStyle switch
            {
                "fade" => RasterOps3.FadeEdges(img, s, depth * 3),
                "bevel" => RasterOps3.Bevel(img, depth),
                _ => RasterOps.Edge(img, s, depth, edgeStyle, Environment.TickCount),
            }, Inc());
        });

        // Filters
        Section(host, "Filtros");
        var filters = new WrapPanel();
        void F(string name, Func<System.Windows.Media.Imaging.BitmapSource, System.Windows.Media.Imaging.BitmapSource> f)
        {
            var b = new Button { Content = name, Style = Res<Style>("OutlineButton"), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => w.ApplyEffect(f, Inc());
            filters.Children.Add(b);
        }
        F("Escala de grises", RasterOps.Grayscale);
        F("Invertir", RasterOps.Invert);
        F("Sepia", RasterOps.Sepia);
        F("Posterizar", img => RasterOps.Posterize(img, 5));
        F("Alto contraste", img => RasterOps.Adjust(img, 0, 45, 10));
        F("Monocromo", img => RasterOps3.Monochrome(img, 55));
        F("Suavizar", img => RasterOps3.Blur(img, 2));
        F("Enfocar", img => RasterOps3.Sharpen(img, 0.8));
        F("Viñeta", img => RasterOps3.Vignette(img, 0.6));
        F("Pixelar", img => RasterOps.Obscure(img, true, 25));
        host.Children.Add(filters);

        // Colour adjustment
        Section(host, "Ajuste de color");
        double br = 0, ct = 0, sat = 0, hue = 0, gamma = 1;
        PropertiesPanel.SliderRow(host, "Brillo", -100, 100, 0, v => br = v);
        PropertiesPanel.SliderRow(host, "Contraste", -100, 100, 0, v => ct = v);
        PropertiesPanel.SliderRow(host, "Saturación", -100, 100, 0, v => sat = v);
        PropertiesPanel.SliderRow(host, "Matiz", -180, 180, 0, v => hue = v, "°");
        PropertiesPanel.SliderRow(host, "Gamma", 0.2, 3, 1, v => gamma = v, "", 2);
        ApplyButton(host, () => w.ApplyEffect(img =>
        {
            var r = RasterOps.Adjust(img, br, ct, sat);
            return Math.Abs(hue) > 0.5 || Math.Abs(gamma - 1) > 0.01 ? RasterOps3.HueGamma(r, hue, gamma) : r;
        }, Inc()));

        Section(host, "Sustitución de color");
        Color from = Colors.White, to = Palette.C("#FFD23F");
        double tol = 12; bool swap = false;
        PropertiesPanel.ColorRow(host, "Color actual", from, c => from = c);
        PropertiesPanel.ColorRow(host, "Color nuevo", to, c => to = c);
        PropertiesPanel.SliderRow(host, "Tolerancia", 0, 60, tol, v => tol = v, "%");
        PropertiesPanel.Toggle(host, "Intercambiar ambos colores", swap, v => swap = v);
        ApplyButton(host, () => w.ApplyEffect(img => RasterOps3.ReplaceColor(img, from, to, tol, swap), Inc()));

        // Watermark
        Section(host, "Marca de agua");
        var wmText = new TextBox { Text = "Confidencial", Margin = new Thickness(0, 4, 0, 0) };
        host.Children.Add(wmText);
        double wmOpacity = 25;
        PropertiesPanel.SliderRow(host, "Opacidad", 5, 80, wmOpacity, v => wmOpacity = v, "%");
        ApplyButton(host, () => w.ApplyEffect(img => Watermark(img, wmText.Text, wmOpacity / 100), Inc()));
        host.Children.Add(new TextBlock { Text = "O bien un logotipo:", Style = Res<Style>("Caption") });
        string pos = "SE"; double logoSize = 18;
        PropertiesPanel.EnumChips(host, "Posición", pos, new System.Collections.Generic.Dictionary<string, string>
            { ["NW"] = "↖", ["N"] = "↑", ["NE"] = "↗", ["W"] = "←", ["C"] = "•", ["E"] = "→", ["SW"] = "↙", ["S"] = "↓", ["SE"] = "↘" }, v => pos = v);
        PropertiesPanel.SliderRow(host, "Tamaño del logotipo", 4, 60, logoSize, v => logoSize = v, "%");
        ApplyButton(host, () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico" };
            if (dlg.ShowDialog() != true) return;
            var logo = Services.ExportService.LoadImage(dlg.FileName);
            w.ApplyEffect(img => RasterOps3.WatermarkImage(img, logo, pos, wmOpacity / 100, logoSize / 100), Inc());
        }, "Elegir logotipo y aplicar…");

        Section(host, "Perspectiva");
        double tiltH = 0.5, tiltV = 0;
        PropertiesPanel.SliderRow(host, "Inclinación horizontal", -100, 100, 50, v => tiltH = v / 100, "%");
        PropertiesPanel.SliderRow(host, "Inclinación vertical", -100, 100, 0, v => tiltV = v / 100, "%");
        ApplyButton(host, () => w.ApplyEffect(img => RasterOps2.Perspective(img, tiltH, tiltV), true));

        Section(host, "Esquina doblada");
        double curl = 18;
        PropertiesPanel.SliderRow(host, "Tamaño", 5, 45, curl, v => curl = v, "%");
        ApplyButton(host, () => w.ApplyEffect(img => RasterOps2.PageCurl(img, curl / 100), true));

        Section(host, "Información de captura");
        bool infoDate = true, infoApp = true, infoTitle = false, infoUrl = true, infoOs = false;
        PropertiesPanel.Toggle(host, "Fecha y hora", infoDate, v => infoDate = v);
        PropertiesPanel.Toggle(host, "Aplicación", infoApp, v => infoApp = v);
        PropertiesPanel.Toggle(host, "Título de la ventana", infoTitle, v => infoTitle = v);
        PropertiesPanel.Toggle(host, "Sitio web", infoUrl, v => infoUrl = v);
        PropertiesPanel.Toggle(host, "Sistema operativo", infoOs, v => infoOs = v);
        ApplyButton(host, () =>
        {
            var item = w.CurrentItem;
            var parts = new System.Collections.Generic.List<string>();
            if (infoDate) parts.Add((item?.Created ?? DateTime.Now).ToString("f"));
            if (infoApp && item?.SourceApp is { Length: > 0 } app) parts.Add(app);
            if (infoTitle && item?.SourceTitle is { Length: > 0 } t) parts.Add(t);
            if (infoUrl && item?.SourceUrl is { Length: > 0 } u) parts.Add(u);
            if (infoOs) parts.Add($"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Build}");
            w.ApplyEffect(img => CaptionBar(img, string.Join("   ·   ", parts)), Inc());
        }, "Añadir al pie");

        Section(host, "Resolución de exportación");
        host.Children.Add(new TextBlock { Text = "Puntos por pulgada guardados en los archivos exportados (cambia el tamaño de impresión, no los píxeles).", Style = Res<Style>("Caption"), TextWrapping = TextWrapping.Wrap });
        PropertiesPanel.SliderRow(host, "PPP", 72, 600, App.Settings.ExportDpi, v => { App.Settings.ExportDpi = (int)v; App.Settings.Save(); });
    }

    static void Section(Panel host, string title) =>
        host.Children.Add(new TextBlock { Text = title, Style = Res<Style>("SectionHeader"), Margin = new Thickness(0, 18, 0, 0) });

    static void ApplyButton(Panel host, Action a, string text = "Aplicar")
    {
        var b = new Button { Content = text, Style = Res<Style>("AccentButton"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => a();
        host.Children.Add(b);
    }

    static System.Windows.Media.Imaging.BitmapSource Watermark(System.Windows.Media.Imaging.BitmapSource img, string text, double opacity)
    {
        if (string.IsNullOrWhiteSpace(text)) return img;
        return RasterOps.Render(img.PixelWidth, img.PixelHeight, dc =>
        {
            dc.DrawImage(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
            double size = Math.Max(14, Math.Min(img.PixelWidth, img.PixelHeight) / 9.0);
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size, new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), 255, 255, 255)), 1);
            var shadow = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size, new SolidColorBrush(Color.FromArgb((byte)(opacity * 160), 0, 0, 0)), 1);
            dc.PushTransform(new RotateTransform(-30, img.PixelWidth / 2.0, img.PixelHeight / 2.0));
            // Tile the text diagonally over the whole image.
            for (double y = -img.PixelHeight; y < img.PixelHeight * 2; y += ft.Height * 3)
                for (double x = -img.PixelWidth; x < img.PixelWidth * 2; x += ft.Width + size * 2)
                {
                    dc.DrawText(shadow, new Point(x + 2, y + 2));
                    dc.DrawText(ft, new Point(x, y));
                }
            dc.Pop();
        });
    }

    static System.Windows.Media.Imaging.BitmapSource CaptionBar(System.Windows.Media.Imaging.BitmapSource img, string text)
    {
        int bar = 30;
        return RasterOps.Render(img.PixelWidth, img.PixelHeight + bar, dc =>
        {
            dc.DrawImage(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
            dc.DrawRectangle(new SolidColorBrush(Palette.C("#1D1D1F")), null, new Rect(0, img.PixelHeight, img.PixelWidth, bar));
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White, 1);
            dc.DrawText(ft, new Point(10, img.PixelHeight + (bar - ft.Height) / 2));
        });
    }
}
