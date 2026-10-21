using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxSnapr.Editor.Model;
using MaxSnapr.Editor.Raster;
using MaxSnapr.Editor.Smart;
using MaxSnapr.Library;
using MaxSnapr.Services;
using Microsoft.Win32;

namespace MaxSnapr.Views;

/// <summary>Picker of library images with order, shared by Combine, Batch and Video-from-images.</summary>
public sealed class ImagePickList : DockPanel
{
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended };
    public ObservableCollection<LibraryItem> Chosen { get; } = new();

    public ImagePickList(IEnumerable<LibraryItem> initial, bool imagesOnly = true)
    {
        foreach (var i in initial) Chosen.Add(i);
        _list.ItemsSource = Chosen;
        _list.ItemContainerStyle = (Style)Application.Current.FindResource("CardItem");
        _list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Orientation='Horizontal'>" +
            "<Image Source='{Binding Thumbnail}' Width='80' Height='50' Stretch='Uniform'/><StackPanel Margin='10,0,0,0' VerticalAlignment='Center'>" +
            "<TextBlock Text='{Binding Name}' Width='150' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Subtitle}' FontSize='11' Opacity='0.7'/></StackPanel></StackPanel></DataTemplate>");
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        SetDock(buttons, Dock.Bottom);
        void B(string t, Action a) { var b = new Button { Content = t, Style = (Style)Application.Current.FindResource("OutlineButton"), Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 4, 8, 4) }; b.Click += (_, _) => a(); buttons.Children.Add(b); }
        B("Añadir…", () =>
        {
            var pool = LibraryStore.Instance.Items.Where(i => (!imagesOnly || !i.IsMedia) && !Chosen.Contains(i)).ToList();
            var dlg = new PickItemsDialog("Añadir imágenes", "Elija imágenes de la biblioteca:", pool) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true) foreach (var i in dlg.Selected) Chosen.Add(i);
        });
        B("Subir", () => Move(-1));
        B("Bajar", () => Move(1));
        B("Quitar", () => { foreach (var i in _list.SelectedItems.Cast<LibraryItem>().ToList()) Chosen.Remove(i); });
        Children.Add(buttons);
        Children.Add(_list);
    }

    void Move(int d)
    {
        if (_list.SelectedItem is not LibraryItem i) return;
        int idx = Chosen.IndexOf(i), n = idx + d;
        if (n < 0 || n >= Chosen.Count) return;
        Chosen.Move(idx, n);
        _list.SelectedItem = i;
    }
}

/// <summary>Create → combine images into a template or a simple stack.</summary>
public sealed class CombineWindow : Window
{
    readonly ImagePickList _pick;
    readonly ListBox _templates = new();
    readonly TextBox _title = new() { Text = "Título" };
    readonly ComboBox _theme = new();
    static T Res<T>(string k) => (T)Application.Current.FindResource(k);

    sealed record Choice(TemplateDefinition Def, ImageSource Preview);

    public CombineWindow(IEnumerable<LibraryItem> images)
    {
        Title = "Crear a partir de plantilla";
        Width = 1040; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = Res<Style>("AppWindow");
        Icon = EditorWindow.Logo;
        _pick = new ImagePickList(images) { Margin = new Thickness(14) };
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel();
        var lh = new TextBlock { Text = "Imágenes (en orden)", Style = Res<Style>("SectionHeader"), Margin = new Thickness(14, 12, 0, 0) };
        DockPanel.SetDock(lh, Dock.Top);
        left.Children.Add(lh);
        left.Children.Add(_pick);
        root.Children.Add(new Border { Child = left, Background = Res<Brush>("Bg.Panel"), BorderBrush = Res<Brush>("Line"), BorderThickness = new Thickness(0, 0, 1, 0) });

        var right = new DockPanel { Margin = new Thickness(16, 12, 16, 16) };
        Grid.SetColumn(right, 1);
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock { Text = "Título", Style = Res<Style>("Caption") });
        top.Children.Add(_title);
        top.Children.Add(new TextBlock { Text = "Tema de color", Style = Res<Style>("Caption") });
        foreach (var t in ToolTheme.All) _theme.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t, IsSelected = t.Name == ToolTheme.Active.Name });
        _theme.SelectionChanged += (_, _) => LoadTemplates();
        top.Children.Add(_theme);
        top.Children.Add(new TextBlock { Text = "Plantilla", Style = Res<Style>("SectionHeader") });
        right.Children.Add(top);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var import = new Button { Content = "Importar plantilla…", Style = Res<Style>("OutlineButton"), Margin = new Thickness(0, 0, 8, 0) };
        import.Click += (_, _) => { if (TemplateFiles.Import(this)) LoadTemplates(); };
        var export = new Button { Content = "Exportar plantilla…", Style = Res<Style>("OutlineButton"), Margin = new Thickness(0, 0, 16, 0) };
        export.Click += (_, _) => { if (_templates.SelectedItem is Choice ch) TemplateFiles.Export(this, ch.Def); };
        bottom.Children.Add(import); bottom.Children.Add(export);
        var create = new Button { Content = "Crear", Style = Res<Style>("AccentButton"), MinWidth = 110, IsDefault = true };
        create.Click += (_, _) => Create();
        var cancel = new Button { Content = "Cancelar", Style = Res<Style>("OutlineButton"), MinWidth = 100, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        bottom.Children.Add(create); bottom.Children.Add(cancel);
        right.Children.Add(bottom);
        _templates.ItemContainerStyle = Res<Style>("CardItem");
        _templates.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        ScrollViewer.SetHorizontalScrollBarVisibility(_templates, ScrollBarVisibility.Disabled);
        _templates.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Width='200'>" +
            "<Border Height='130' CornerRadius='4' Background='#20808080'><Image Source='{Binding Preview}' Stretch='Uniform' Margin='4'/></Border>" +
            "<TextBlock Text='{Binding Def.Name}' Margin='2,6,2,0' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Def.Category}' FontSize='11' Opacity='0.7' Margin='2,0,2,2'/></StackPanel></DataTemplate>");
        right.Children.Add(_templates);
        root.Children.Add(right);
        Content = root;
        Loaded += (_, _) => LoadTemplates();
    }

    ToolTheme Theme => (_theme.SelectedItem as ComboBoxItem)?.Tag as ToolTheme ?? ToolTheme.Active;

    void LoadTemplates()
    {
        var defs = new[] { "Apilar en vertical", "En fila", "Cuadrícula automática" }.Select(k => TemplateBuilder.Stack(k, Math.Max(2, _pick.Chosen.Count)))
            .Concat(TemplateDefinition.BuiltIn).Concat(App.Settings.UserTemplates).ToList();
        var theme = Theme;
        _templates.ItemsSource = defs.Select(d => new Choice(d, TemplateBuilder.Preview(d, theme))).ToList();
        _templates.SelectedIndex = 0;
    }

    void Create()
    {
        if (_templates.SelectedItem is not Choice c) return;
        var def = c.Def.Category == "Combinar" ? TemplateBuilder.Stack(c.Def.Name, _pick.Chosen.Count) : c.Def;
        var imgs = _pick.Chosen.Select(EditorWindow.RenderItem).ToList();
        if (imgs.Count == 0) { MessageBox.Show(this, "Añada al menos una imagen.", "Crear"); return; }
        var doc = TemplateBuilder.Build(def, imgs, _title.Text, Theme);
        Usage.Count("template");
        var item = LibraryStore.Instance.Add(doc, string.IsNullOrWhiteSpace(_title.Text) ? def.Name : _title.Text, "Plantilla");
        App.Current.ShowEditor().OpenDocument(doc, item);
        Close();
    }
}

public static class TemplateFiles
{
    public static void Export(Window owner, TemplateDefinition t)
    {
        var dlg = new SaveFileDialog { Filter = "Plantilla de MaxSnapr (*.plantilla.json)|*.plantilla.json", FileName = t.Name };
        if (dlg.ShowDialog(owner) == true)
            File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(t, new System.Text.Json.JsonSerializerOptions(ToolStyle.Json) { WriteIndented = true }));
    }

    public static bool Import(Window owner)
    {
        var dlg = new OpenFileDialog { Filter = "Plantilla de MaxSnapr (*.json)|*.json" };
        if (dlg.ShowDialog(owner) != true) return false;
        try
        {
            var t = System.Text.Json.JsonSerializer.Deserialize<TemplateDefinition>(File.ReadAllText(dlg.FileName), ToolStyle.Json);
            if (t == null || t.Slots.Count == 0) throw new InvalidDataException("La plantilla no tiene huecos para imágenes.");
            t.Category = "Mis plantillas";
            App.Settings.UserTemplates.Add(t);
            App.Settings.Save();
            return true;
        }
        catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Importar plantilla"); return false; }
    }

    /// <summary>Turns the open document's images into the slots of a reusable template.</summary>
    public static TemplateDefinition? FromDocument(EditorDocument doc, string name)
    {
        var images = doc.Annotations.OfType<ImageAnnotation>().ToList();
        if (images.Count == 0) return null;
        var t = new TemplateDefinition { Name = name, Category = "Mis plantillas", Width = doc.Width, Height = doc.Height, Title = false, Captions = false };
        foreach (var i in images.OrderBy(i => i.Box.Y).ThenBy(i => i.Box.X))
            t.Slots.Add(new TemplateSlot { Area = new Rect(i.Box.X / doc.Width, i.Box.Y / doc.Height, i.Box.Width / doc.Width, i.Box.Height / doc.Height) });
        t.Numbers = doc.Annotations.OfType<StepAnnotation>().Any();
        return t;
    }
}

/// <summary>Smart redact options.</summary>
public sealed class SmartRedactDialog : DialogWindow
{
    readonly Dictionary<RedactCategory, CheckBox> _boxes = new();
    readonly TextBox _custom = new();
    RedactKind _kind = RedactKind.Pixelate;
    public HashSet<RedactCategory> Categories { get; } = new();
    public RedactKind Kind => _kind;
    public List<string> CustomWords { get; } = new();

    public SmartRedactDialog() : base("Ocultación inteligente", 420)
    {
        Caption("MaxSnapr reconoce el texto de la imagen y tapa lo que elija. Cada zona queda como objeto editable.");
        foreach (RedactCategory c in Enum.GetValues<RedactCategory>().Where(c => c != RedactCategory.Custom))
        {
            var cb = new CheckBox { Content = SmartFeatures.Label(c), IsChecked = c is RedactCategory.Email or RedactCategory.Phone or RedactCategory.Iban or RedactCategory.Card or RedactCategory.IdNumber };
            _boxes[c] = cb;
            Body.Children.Add(cb);
        }
        Caption("Otras palabras (separadas por comas)");
        Body.Children.Add(_custom);
        Caption("Cómo ocultar");
        var kinds = new WrapPanel();
        foreach (var (k, n) in new[] { (RedactKind.Pixelate, "Pixelar"), (RedactKind.Blur, "Desenfocar"), (RedactKind.Solid, "Tapar") })
        {
            var rb = new RadioButton { Content = n, GroupName = "rk", Style = (Style)Application.Current.FindResource("ChipRadio"), IsChecked = k == _kind };
            var kk = k;
            rb.Checked += (_, _) => _kind = kk;
            kinds.Children.Add(rb);
        }
        Body.Children.Add(kinds);
    }

    protected override bool Validate()
    {
        foreach (var (c, cb) in _boxes) if (cb.IsChecked == true) Categories.Add(c);
        CustomWords.AddRange(_custom.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (CustomWords.Count > 0) Categories.Add(RedactCategory.Custom);
        return Categories.Count > 0;
    }
}

/// <summary>Batch conversion of library images: format, size, effects, naming and folder.</summary>
public sealed class BatchExportWindow : Window
{
    readonly ImagePickList _pick;
    static T Res<T>(string k) => (T)Application.Current.FindResource(k);

    public BatchExportWindow(IEnumerable<LibraryItem> items)
    {
        Title = "Exportación por lotes";
        Width = 820; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = Res<Style>("AppWindow");
        Icon = EditorWindow.Logo;
        _pick = new ImagePickList(items) { Margin = new Thickness(14) };
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(new Border { Child = _pick, Background = Res<Brush>("Bg.Panel"), BorderBrush = Res<Brush>("Line"), BorderThickness = new Thickness(0, 0, 1, 0) });

        var o = new StackPanel { Margin = new Thickness(18, 10, 18, 18) };
        Grid.SetColumn(o, 1);
        string format = "png"; int scale = 100, maxWidth = 0; bool border = false, shadow = false, gray = false, flatten = true;
        string pattern = "{nombre}", folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MaxSnapr exportadas");
        o.Children.Add(new TextBlock { Text = "Formato", Style = Res<Style>("SectionHeader") });
        var fmts = new WrapPanel();
        foreach (var f in new[] { "png", "jpg", "bmp", "gif", "tif", "pdf" })
        {
            var rb = new RadioButton { Content = f.ToUpperInvariant(), GroupName = "fmt", Style = Res<Style>("ChipRadio"), IsChecked = f == format };
            var ff = f;
            rb.Checked += (_, _) => format = ff;
            fmts.Children.Add(rb);
        }
        o.Children.Add(fmts);
        o.Children.Add(new TextBlock { Text = "Tamaño", Style = Res<Style>("SectionHeader") });
        PropertiesPanel.SliderRow(o, "Escala", 10, 200, 100, v => scale = (int)v, "%");
        o.Children.Add(new TextBlock { Text = "Ancho máximo en píxeles (0 = sin límite)", Style = Res<Style>("Caption") });
        var mw = new TextBox { Text = "0" };
        mw.TextChanged += (_, _) => int.TryParse(mw.Text, out maxWidth);
        o.Children.Add(mw);
        o.Children.Add(new TextBlock { Text = "Efectos", Style = Res<Style>("SectionHeader") });
        CheckBox T(string label, bool on, Action<bool> f) { var c = new CheckBox { Content = label, IsChecked = on }; c.Click += (_, _) => f(c.IsChecked == true); o.Children.Add(c); return c; }
        T("Incluir los objetos (aplanar)", flatten, v => flatten = v);
        T("Borde", border, v => border = v);
        T("Sombra", shadow, v => shadow = v);
        T("Escala de grises", gray, v => gray = v);
        o.Children.Add(new TextBlock { Text = "Nombre de archivo", Style = Res<Style>("SectionHeader") });
        var pat = new TextBox { Text = pattern };
        pat.TextChanged += (_, _) => pattern = pat.Text;
        o.Children.Add(pat);
        o.Children.Add(new TextBlock { Text = "Etiquetas: " + string.Join(" ", Naming.Tokens), Style = Res<Style>("Caption"), TextWrapping = TextWrapping.Wrap });
        o.Children.Add(new TextBlock { Text = "Carpeta de destino", Style = Res<Style>("SectionHeader") });
        var dir = new TextBox { Text = folder };
        dir.TextChanged += (_, _) => folder = dir.Text;
        o.Children.Add(dir);
        var progress = new ProgressBar { Height = 6, Margin = new Thickness(0, 14, 0, 0), Maximum = 1 };
        o.Children.Add(progress);
        var run = new Button { Content = "Exportar", Style = Res<Style>("AccentButton"), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 110, Margin = new Thickness(0, 12, 0, 0) };
        run.Click += async (_, _) =>
        {
            var items = _pick.Chosen.Where(i => !i.IsMedia).ToList();
            if (items.Count == 0) return;
            run.IsEnabled = false;
            Directory.CreateDirectory(folder);
            var pdfPages = new List<BitmapSource>();
            int done = 0;
            foreach (var item in items)
            {
                await Task.Yield();
                var doc = EditorDocument.Load(item.FullPath);
                BitmapSource img = flatten ? doc.Flatten() : doc.Base;
                double s = scale / 100.0;
                if (maxWidth > 0 && img.PixelWidth * s > maxWidth) s = maxWidth / (double)img.PixelWidth;
                if (Math.Abs(s - 1) > 0.001) img = RasterOps.Resize(img, Math.Max(1, (int)(img.PixelWidth * s)), Math.Max(1, (int)(img.PixelHeight * s)));
                if (gray) img = RasterOps.Grayscale(img);
                if (border) img = RasterOps.Border(img, 2, Color.FromRgb(0x9C, 0xA3, 0xAF));
                if (shadow) img = RasterOps.Shadow(img, 18, 6, Color.FromArgb(110, 0, 0, 0));
                if (format == "pdf") pdfPages.Add(img);
                else
                {
                    var name = Naming.Build(pattern, item.Name, item.SourceApp, item.SourceTitle, img.PixelWidth, img.PixelHeight);
                    var path = Path.Combine(folder, name + "." + format);
                    for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name} ({n}).{format}");
                    ExportService.Save(img, path, App.Settings.JpegQuality);
                }
                progress.Value = ++done / (double)items.Count;
            }
            if (pdfPages.Count > 0) ExportService.SavePdf(pdfPages, Path.Combine(folder, Naming.Safe(pattern.Replace("{nombre}", "Capturas")) + ".pdf"), App.Settings.JpegQuality);
            System.Diagnostics.Process.Start("explorer.exe", folder);
            Close();
        };
        o.Children.Add(run);
        root.Children.Add(new ScrollViewer { Content = o, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        ((ScrollViewer)root.Children[1]).SetValue(Grid.ColumnProperty, 1);
        Content = root;
    }
}

/// <summary>Create, edit, import and export colour themes for quick styles.</summary>
public sealed class ThemeEditorDialog : DialogWindow
{
    readonly ComboBox _list = new();
    readonly WrapPanel _colors = new();
    readonly TextBox _name = new();
    ToolTheme? _current;

    public ThemeEditorDialog() : base("Temas de herramientas", 520, okCancel: false)
    {
        Caption("Los estilos rápidos de todas las herramientas se generan con los colores del tema activo.");
        Body.Children.Add(_list);
        Caption("Nombre");
        Body.Children.Add(_name);
        Caption("Colores");
        Body.Children.Add(_colors);
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void B(string t, Action a) { var b = new Button { Content = t, Style = (Style)Application.Current.FindResource("OutlineButton"), Margin = new Thickness(0, 0, 6, 6) }; b.Click += (_, _) => a(); row.Children.Add(b); }
        B("Nuevo a partir de este", () =>
        {
            var t = (_current ?? ToolTheme.Active).Clone();
            t.Name = t.Name + " (copia)";
            App.Settings.Themes.Add(t);
            Reload(t.Name);
        });
        B("Eliminar", () =>
        {
            if (_current == null || !App.Settings.Themes.Contains(_current)) return;
            App.Settings.Themes.Remove(_current);
            Reload(ToolTheme.BuiltIn[0].Name);
        });
        B("Importar…", () =>
        {
            var dlg = new OpenFileDialog { Filter = "Tema de MaxSnapr (*.json)|*.json" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var t = System.Text.Json.JsonSerializer.Deserialize<ToolTheme>(File.ReadAllText(dlg.FileName), ToolStyle.Json);
                if (t != null) { App.Settings.Themes.Add(t); Reload(t.Name); }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Importar tema"); }
        });
        B("Exportar…", () =>
        {
            if (_current == null) return;
            var dlg = new SaveFileDialog { Filter = "Tema de MaxSnapr (*.json)|*.json", FileName = _current.Name };
            if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(_current, new System.Text.Json.JsonSerializerOptions(ToolStyle.Json) { WriteIndented = true }));
        });
        Body.Children.Add(row);
        var use = new Button { Content = "Usar este tema", Style = (Style)Application.Current.FindResource("AccentButton"), MinWidth = 120 };
        use.Click += (_, _) => { if (_current != null) { App.Settings.ActiveTheme = _current.Name; App.Settings.Save(); DialogResult = true; } };
        var close = new Button { Content = "Cerrar", Style = (Style)Application.Current.FindResource("OutlineButton"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        Buttons.Children.Add(use); Buttons.Children.Add(close);
        _list.SelectionChanged += (_, _) => Show((_list.SelectedItem as ComboBoxItem)?.Tag as ToolTheme);
        _name.LostFocus += (_, _) =>
        {
            if (_current == null || !App.Settings.Themes.Contains(_current) || string.IsNullOrWhiteSpace(_name.Text)) return;
            bool active = App.Settings.ActiveTheme == _current.Name;
            _current.Name = _name.Text.Trim();
            if (active) App.Settings.ActiveTheme = _current.Name;
            Reload(_current.Name);
        };
        Reload(ToolTheme.Active.Name);
    }

    void Reload(string select)
    {
        _list.Items.Clear();
        foreach (var t in ToolTheme.All) _list.Items.Add(new ComboBoxItem { Content = t.Name + (ToolTheme.BuiltIn.Contains(t) ? " (integrado)" : ""), Tag = t, IsSelected = t.Name == select });
        if (_list.SelectedItem == null && _list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    void Show(ToolTheme? t)
    {
        _current = t;
        _colors.Children.Clear();
        if (t == null) return;
        bool editable = App.Settings.Themes.Contains(t);
        _name.Text = t.Name;
        _name.IsEnabled = editable;
        for (int i = 0; i < t.Colors.Count; i++)
        {
            int idx = i;
            var p = new ColorPicker(t.Colors[i], false) { Width = 52, Height = 30, Margin = new Thickness(0, 0, 6, 6), IsEnabled = editable };
            p.ColorChanged += (_, c) => t.Colors[idx] = c;
            _colors.Children.Add(p);
        }
        if (!editable) _colors.Children.Add(new TextBlock { Text = "Los temas integrados no se pueden modificar; cree una copia.", Style = (Style)Application.Current.FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center });
    }
}

/// <summary>Seconds per image for "video from images".</summary>
public sealed class ImagesToVideoWindow : Window
{
    public ImagesToVideoWindow(IEnumerable<LibraryItem> items)
    {
        Title = "Crear vídeo a partir de imágenes";
        Width = 560; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.FindResource("AppWindow");
        Icon = EditorWindow.Logo;
        var pick = new ImagePickList(items) { Margin = new Thickness(14) };
        var bottom = new StackPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        double seconds = 3;
        PropertiesPanel.SliderRow(bottom, "Segundos por imagen", 1, 15, seconds, v => seconds = v, "s");
        var status = new TextBlock { Style = (Style)Application.Current.FindResource("Caption") };
        bottom.Children.Add(status);
        var run = new Button { Content = "Crear vídeo", Style = (Style)Application.Current.FindResource("AccentButton"), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 120, Margin = new Thickness(0, 10, 0, 0) };
        run.Click += async (_, _) =>
        {
            if (pick.Chosen.Count == 0) return;
            run.IsEnabled = false;
            status.Text = "Generando…";
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "MaxSnapr", "slides");
                Directory.CreateDirectory(dir);
                var paths = new List<string>();
                int n = 0;
                foreach (var i in pick.Chosen)
                {
                    var p = Path.Combine(dir, $"{n++:000}.png");
                    ExportService.Save(EditorWindow.RenderItem(i), p, 95);
                    paths.Add(p);
                }
                var outp = Path.Combine(LibraryStore.Instance.Root, DateTime.Now.ToString("yyyy-MM"), $"{Naming.Default("Presentación", DateTime.Now)}.mp4");
                await Video.VideoTools.ImagesToVideoAsync(paths, TimeSpan.FromSeconds(seconds), outp, new Progress<double>(p => status.Text = $"Generando… {p:P0}"));
                var item = await LibraryStore.Instance.AddMediaAsync(outp, "Presentación");
                App.Current.ShowEditor().OpenItem(item);
                Close();
            }
            catch (Exception ex) { status.Text = ex.Message; run.IsEnabled = true; }
        };
        bottom.Children.Add(run);
        var root = new DockPanel();
        root.Children.Add(bottom);
        root.Children.Add(pick);
        Content = root;
    }
}
