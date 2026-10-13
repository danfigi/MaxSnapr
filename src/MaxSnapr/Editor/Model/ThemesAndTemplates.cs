using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace MaxSnapr.Editor.Model;

/// <summary>A named colour set; quick styles for every tool are generated from it.</summary>
public sealed class ToolTheme
{
    public string Name { get; set; } = "Tema";
    public List<Color> Colors { get; set; } = new();
    public string Font { get; set; } = "Segoe UI";

    public ToolTheme Clone() => new() { Name = Name, Colors = Colors.ToList(), Font = Font };

    static Color C(string h) => ToolStyle.FromHex(h);

    public static readonly ToolTheme[] BuiltIn =
    {
        new() { Name = "MaxSnapr", Colors = new() { C("#E83A3A"), C("#F28C28"), C("#FFD23F"), C("#2BB673"), C("#2E7CF6"), C("#8B5CF6"), C("#1D1D1F"), C("#FFFFFF") } },
        new() { Name = "Corporativo", Colors = new() { C("#0B3D91"), C("#1F8A9E"), C("#F28C28"), C("#5B6770"), C("#C8102E"), C("#2E7D32"), C("#111827"), C("#FFFFFF") } },
        new() { Name = "Pastel", Colors = new() { C("#F7A1A1"), C("#F9C784"), C("#F6E58D"), C("#A8E6CF"), C("#8EC5FC"), C("#C3AED6"), C("#4A4A4A"), C("#FFFFFF") } },
        new() { Name = "Alto contraste", Colors = new() { C("#FF0000"), C("#FFFF00"), C("#00FF00"), C("#00FFFF"), C("#0000FF"), C("#FF00FF"), C("#000000"), C("#FFFFFF") } },
        new() { Name = "Monocromo", Colors = new() { C("#111111"), C("#3A3A3A"), C("#5E5E5E"), C("#858585"), C("#ABABAB"), C("#D1D1D1"), C("#000000"), C("#FFFFFF") } },
        new() { Name = "Naturaleza", Colors = new() { C("#2D6A4F"), C("#40916C"), C("#95D5B2"), C("#B5838D"), C("#E9C46A"), C("#F4A261"), C("#264653"), C("#FFFFFF") } },
    };

    public static ToolTheme Active
    {
        get
        {
            var name = App.Settings.ActiveTheme;
            return App.Settings.Themes.FirstOrDefault(t => t.Name == name) ?? BuiltIn.FirstOrDefault(t => t.Name == name) ?? BuiltIn[0];
        }
    }

    public static IEnumerable<ToolTheme> All => BuiltIn.Concat(App.Settings.Themes);
}

/// <summary>Slot for one image in a layout, in relative (0–1) coordinates of the content area.</summary>
public sealed class TemplateSlot
{
    public Rect Area { get; set; }
    public string Caption { get; set; } = "";
}

/// <summary>A layout that combines several captures with titles, captions and step numbers.</summary>
public sealed class TemplateDefinition
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "General";
    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 1000;
    public bool Title { get; set; } = true;
    public bool Numbers { get; set; }
    public bool Captions { get; set; } = true;
    public bool Arrows { get; set; }
    public List<TemplateSlot> Slots { get; set; } = new();

    static TemplateSlot S(double x, double y, double w, double h, string caption = "") => new() { Area = new Rect(x, y, w, h), Caption = caption };

    static TemplateDefinition Grid(string name, string cat, int cols, int rows, bool numbers, int w = 1600, int h = 1000)
    {
        var t = new TemplateDefinition { Name = name, Category = cat, Numbers = numbers, Width = w, Height = h };
        double gw = 1.0 / cols, gh = 1.0 / rows;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                t.Slots.Add(S(c * gw, r * gh, gw, gh, numbers ? "Describa este paso" : ""));
        return t;
    }

    public static readonly TemplateDefinition[] BuiltIn =
    {
        new() { Name = "Imagen con título", Category = "Básicas", Captions = false, Width = 1600, Height = 1000, Slots = { S(0, 0, 1, 1) } },
        new() { Name = "Antes y después", Category = "Básicas", Arrows = true, Slots = { S(0, 0, 0.5, 1, "Antes"), S(0.5, 0, 0.5, 1, "Después") } },
        Grid("Dos pasos", "Pasos", 2, 1, true),
        Grid("Tres pasos", "Pasos", 3, 1, true, 1800, 800),
        Grid("Cuatro pasos (cuadrícula)", "Pasos", 2, 2, true, 1600, 1300),
        Grid("Seis pasos", "Pasos", 3, 2, true, 1800, 1200),
        new() { Name = "Pasos en vertical", Category = "Pasos", Numbers = true, Width = 1100, Height = 1600,
            Slots = { S(0, 0, 1, 1.0 / 3, "Primer paso"), S(0, 1.0 / 3, 1, 1.0 / 3, "Segundo paso"), S(0, 2.0 / 3, 1, 1.0 / 3, "Tercer paso") } },
        new() { Name = "Línea de tiempo", Category = "Pasos", Numbers = true, Arrows = true, Width = 2000, Height = 800,
            Slots = { S(0, 0, 0.25, 1, "Inicio"), S(0.25, 0, 0.25, 1, "Después"), S(0.5, 0, 0.25, 1, "Más tarde"), S(0.75, 0, 0.25, 1, "Final") } },
        new() { Name = "Destacado y detalles", Category = "Básicas", Width = 1800, Height = 1100,
            Slots = { S(0, 0, 0.64, 1, "Vista general"), S(0.64, 0, 0.36, 0.5, "Detalle 1"), S(0.64, 0.5, 0.36, 0.5, "Detalle 2") } },
        Grid("Collage 2×2", "Collage", 2, 2, false, 1600, 1300),
        Grid("Comparación de tres", "Collage", 3, 1, false, 1800, 800),
        new() { Name = "Meme", Category = "Divertidas", Title = true, Captions = true, Width = 1200, Height = 1200, Slots = { S(0, 0, 1, 1, "TEXTO INFERIOR") } },
    };
}
