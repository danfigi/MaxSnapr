using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxSnapr.Services;

namespace MaxSnapr.Views;

/// <summary>Small themed dialog shell with a content area and OK/Cancel buttons.</summary>
public class DialogWindow : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(20, 16, 20, 8) };
    protected readonly StackPanel Buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 8, 20, 16) };

    public DialogWindow(string title, double width = 380, bool okCancel = true)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Style = (Style)Application.Current.FindResource("AppWindow");
        var root = new DockPanel();
        DockPanel.SetDock(Buttons, Dock.Bottom);
        root.Children.Add(Buttons);
        root.Children.Add(Body);
        Content = root;
        if (okCancel)
        {
            var ok = new Button { Content = "Aceptar", Style = (Style)Application.Current.FindResource("AccentButton"), IsDefault = true, MinWidth = 90 };
            ok.Click += (_, _) => { if (Validate()) DialogResult = true; };
            var cancel = new Button { Content = "Cancelar", Style = (Style)Application.Current.FindResource("OutlineButton"), IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            Buttons.Children.Add(ok);
            Buttons.Children.Add(cancel);
        }
    }

    protected virtual bool Validate() => true;

    protected TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("Caption") };
        Body.Children.Add(t);
        return t;
    }

    protected TextBox Field(string label, string value)
    {
        Caption(label);
        var tb = new TextBox { Text = value };
        Body.Children.Add(tb);
        return tb;
    }

    protected static int ParseInt(TextBox tb, int fallback) => int.TryParse(tb.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) ? v : fallback;
}

public sealed class InputDialog : DialogWindow
{
    readonly TextBox _box;

    InputDialog(string title, string prompt, string value) : base(title)
    {
        _box = Field(prompt, value);
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window? owner, string title, string prompt, string value)
    {
        var d = new InputDialog(title, prompt, value) { Owner = owner };
        return d.ShowDialog() == true ? d._box.Text : null;
    }
}

public sealed class NewImageDialog : DialogWindow
{
    readonly TextBox _w, _h;
    Color _bg = Colors.White;

    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }
    public Color FillColor { get; private set; } = Colors.White;

    public NewImageDialog() : base("Nueva imagen")
    {
        _w = Field("Ancho (px)", "1280");
        _h = Field("Alto (px)", "720");
        Caption("Fondo");
        var row = new WrapPanel();
        foreach (var (name, c) in new[] { ("Blanco", Colors.White), ("Negro", Colors.Black), ("Transparente", Colors.Transparent), ("Gris", Palette.C("#F3F4F6")) })
        {
            var rb = new RadioButton { Content = name, GroupName = "bg", Style = (Style)Application.Current.FindResource("ChipRadio"), IsChecked = c == Colors.White };
            var col = c;
            rb.Checked += (_, _) => _bg = col;
            row.Children.Add(rb);
        }
        Body.Children.Add(row);
        Caption("Tamaños habituales");
        var presets = new WrapPanel();
        foreach (var (name, w, h) in new[] { ("HD 1280×720", 1280, 720), ("Full HD", 1920, 1080), ("Cuadrado 1080", 1080, 1080), ("A4 (96 ppp)", 794, 1123), ("Banner 1200×630", 1200, 630) })
        {
            var b = new Button { Content = name, Style = (Style)Application.Current.FindResource("OutlineButton"), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 4, 8, 4) };
            b.Click += (_, _) => { _w.Text = w.ToString(); _h.Text = h.ToString(); };
            presets.Children.Add(b);
        }
        Body.Children.Add(presets);
    }

    protected override bool Validate()
    {
        PixelWidth = Math.Clamp(ParseInt(_w, 0), 1, 20000);
        PixelHeight = Math.Clamp(ParseInt(_h, 0), 1, 20000);
        FillColor = _bg;
        return true;
    }
}

public sealed class ResizeDialog : DialogWindow
{
    readonly int _ow, _oh;
    readonly TextBox _w, _h, _pct;
    readonly CheckBox _keep, _smooth, _objects;
    bool _sync;

    public int NewWidth { get; private set; }
    public int NewHeight { get; private set; }
    public bool Smooth => _smooth.IsChecked == true;
    public bool ScaleObjects => _objects.IsChecked == true;

    public ResizeDialog(int w, int h) : base("Cambiar tamaño de la imagen")
    {
        _ow = w; _oh = h;
        Caption($"Tamaño actual: {w} × {h} px");
        _pct = Field("Porcentaje", "100");
        _w = Field("Ancho (px)", w.ToString());
        _h = Field("Alto (px)", h.ToString());
        _keep = new CheckBox { Content = "Mantener la proporción", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        _smooth = new CheckBox { Content = "Escalado suave", IsChecked = true };
        _objects = new CheckBox { Content = "Escalar también los objetos", IsChecked = true };
        Body.Children.Add(_keep);
        Body.Children.Add(_smooth);
        Body.Children.Add(_objects);
        _pct.TextChanged += (_, _) => Sync(() => { var p = ParseInt(_pct, 100); _w.Text = Math.Max(1, _ow * p / 100).ToString(); _h.Text = Math.Max(1, _oh * p / 100).ToString(); });
        _w.TextChanged += (_, _) => Sync(() => { if (_keep.IsChecked == true) _h.Text = Math.Max(1, (int)Math.Round(ParseInt(_w, _ow) * (double)_oh / _ow)).ToString(); _pct.Text = (ParseInt(_w, _ow) * 100 / _ow).ToString(); });
        _h.TextChanged += (_, _) => Sync(() => { if (_keep.IsChecked == true) _w.Text = Math.Max(1, (int)Math.Round(ParseInt(_h, _oh) * (double)_ow / _oh)).ToString(); _pct.Text = (ParseInt(_h, _oh) * 100 / _oh).ToString(); });
    }

    void Sync(Action a)
    {
        if (_sync) return;
        _sync = true;
        try { a(); } finally { _sync = false; }
    }

    protected override bool Validate()
    {
        NewWidth = Math.Clamp(ParseInt(_w, _ow), 1, 30000);
        NewHeight = Math.Clamp(ParseInt(_h, _oh), 1, 30000);
        return true;
    }
}

public sealed class CanvasSizeDialog : DialogWindow
{
    readonly TextBox _l, _t, _r, _b, _nw, _nh;
    readonly int _w0, _h0;
    string _anchor = "C";
    bool _absolute;
    Color _fill;

    public (int, int, int, int) Margins { get; private set; }
    public Color Fill => _fill;

    public CanvasSizeDialog(int w, int h, Color canvas) : base("Tamaño del lienzo")
    {
        _fill = canvas.A == 0 ? Colors.White : canvas;
        Caption($"Tamaño actual: {w} × {h} px. Use valores negativos para recortar.");
        _t = Field("Añadir arriba (px)", "20");
        _b = Field("Añadir abajo (px)", "20");
        _l = Field("Añadir a la izquierda (px)", "20");
        _r = Field("Añadir a la derecha (px)", "20");
        _w0 = w; _h0 = h;
        Caption("O bien indique el tamaño total y el anclaje de la imagen:");
        var size = new StackPanel { Orientation = Orientation.Horizontal };
        _nw = new TextBox { Width = 80, Text = w.ToString() };
        _nh = new TextBox { Width = 80, Text = h.ToString(), Margin = new Thickness(8, 0, 0, 0) };
        _nw.TextChanged += (_, _) => _absolute = true;
        _nh.TextChanged += (_, _) => _absolute = true;
        size.Children.Add(_nw); size.Children.Add(new TextBlock { Text = " × ", VerticalAlignment = VerticalAlignment.Center }); size.Children.Add(_nh);
        Body.Children.Add(size);
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (a, g) in new[] { ("NW", "↖"), ("N", "↑"), ("NE", "↗"), ("W", "←"), ("C", "•"), ("E", "→"), ("SW", "↙"), ("S", "↓"), ("SE", "↘") })
        {
            var rb = new RadioButton { Content = g, GroupName = "anchor", Style = (Style)Application.Current.FindResource("ChipRadio"), MinWidth = 34, IsChecked = a == "C" };
            var aa = a;
            rb.Checked += (_, _) => { _anchor = aa; _absolute = true; };
            grid.Children.Add(rb);
        }
        Body.Children.Add(grid);
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var picker = new ColorPicker(_fill, allowNone: true) { Width = 64, Height = 28 };
        picker.ColorChanged += (_, c) => _fill = c;
        DockPanel.SetDock(picker, Dock.Right);
        row.Children.Add(picker);
        row.Children.Add(new TextBlock { Text = "Color del área nueva", VerticalAlignment = VerticalAlignment.Center });
        Body.Children.Add(row);
    }

    protected override bool Validate()
    {
        if (_absolute)
        {
            // Distribute the size change according to the anchor (where the image stays).
            int dw = Math.Clamp(ParseInt(_nw, _w0), 1, 30000) - _w0, dh = Math.Clamp(ParseInt(_nh, _h0), 1, 30000) - _h0;
            int l = _anchor.Contains('W') ? 0 : _anchor.Contains('E') ? dw : dw / 2;
            int t = _anchor.Contains('N') ? 0 : _anchor.Contains('S') ? dh : dh / 2;
            Margins = (l, t, dw - l, dh - t);
        }
        else Margins = (ParseInt(_l, 0), ParseInt(_t, 0), ParseInt(_r, 0), ParseInt(_b, 0));
        return true;
    }
}

public sealed class TextResultWindow : DialogWindow
{
    public TextResultWindow(string text, BitmapSource? source) : base("Texto extraído", 560, okCancel: false)
    {
        ResizeMode = ResizeMode.CanResize;
        SizeToContent = SizeToContent.Manual;
        Height = 460;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Caption(string.IsNullOrWhiteSpace(text) ? "No se ha reconocido texto en la imagen." : "El texto ya está en el portapapeles. Puede editarlo antes de volver a copiarlo.");
        var box = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 300, FontSize = 14 };
        Body.Children.Add(box);
        var copy = new Button { Content = "Copiar", Style = (Style)Application.Current.FindResource("AccentButton"), MinWidth = 90 };
        copy.Click += (_, _) => ClipboardService.SetText(box.Text);
        var close = new Button { Content = "Cerrar", Style = (Style)Application.Current.FindResource("OutlineButton"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        if (source != null)
        {
            var open = new Button { Content = "Abrir imagen en el editor", Style = (Style)Application.Current.FindResource("OutlineButton"), Margin = new Thickness(0, 0, 8, 0) };
            open.Click += (_, _) =>
            {
                var doc = new Editor.Model.EditorDocument(source);
                var item = Library.LibraryStore.Instance.Add(doc, "Texto extraído", "");
                App.Current.ShowEditor().OpenDocument(doc, item);
                Close();
            };
            Buttons.Children.Add(open);
        }
        Buttons.Children.Add(copy);
        Buttons.Children.Add(close);
    }
}

public sealed class ShortcutsWindow : DialogWindow
{
    public ShortcutsWindow() : base("Atajos de teclado", 520, okCancel: false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string keys, string what)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = keys, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 3) };
            var w = new TextBlock { Text = what, Foreground = (Brush)Application.Current.FindResource("Fg.Secondary"), Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(k, r); Grid.SetRow(w, r); Grid.SetColumn(w, 1);
            grid.Children.Add(k); grid.Children.Add(w);
        }
        Row(App.Current.CaptureHotkeyText, "Capturar (en cualquier aplicación)");
        Row(App.Settings.RepeatHotkey.ToString(), "Repetir la última captura");
        Row(App.Settings.GrabTextHotkey.ToString(), "Extraer texto de la pantalla");
        Row(App.Settings.VideoHotkey.ToString(), "Grabar vídeo");
        Row(App.Settings.StepCaptureHotkey.ToString(), "Captura de pasos (iniciar / detener)");
        Row("Mayús+F9 / Mayús+F10", "Pausar o reanudar / detener la grabación");
        Row("Ctrl+Mayús+F", "Cámara a pantalla completa durante la grabación");
        Row("Arrastrar / clic / Intro", "Región / ventana / pantalla completa en el selector");
        Row("Flechas en el selector", "Mover el cursor píxel a píxel");
        foreach (var d in EditorWindow.ToolDefs) Row(d.Shortcut.ToString(), "Herramienta " + d.Label);
        Row("Ctrl+Z / Ctrl+Y", "Deshacer / rehacer");
        Row("Ctrl+C / Ctrl+Mayús+C", "Copiar selección / copiar todo");
        Row("Ctrl+V", "Pegar imagen, texto u objetos");
        Row("Ctrl+D", "Duplicar");
        Row("Ctrl+G / Ctrl+Mayús+G", "Agrupar / desagrupar");
        Row("Ctrl+] / Ctrl+[", "Adelante / atrás (con Mayús: al frente / al fondo)");
        Row("Flechas (Mayús)", "Mover objetos 1 px (10 px)");
        Row("Ctrl + rueda", "Zoom");
        Row("Espacio + arrastrar", "Desplazar el lienzo");
        Row("Ctrl+0 / Ctrl+9", "100 % / ajustar a la ventana");
        Row("Ctrl+L", "Biblioteca");
        Row("Ctrl+T / Ctrl+Mayús+T", "Compactar la selección / compactar todo");
        Row("Ctrl+Mayús+D", "Duplicar la imagen");
        Row("Alt+Intro", "Detalles de la captura");
        Row("Alt+← / Alt+→", "Imagen anterior / siguiente de la bandeja");
        Row("Ctrl+Alt+C", "Combinar en una plantilla");
        Row("Ctrl+Mayús+P", "Impresión rápida");
        Row("Clic derecho", "Menú contextual del lienzo");
        Row("Espacio · ← → · Inicio/Fin", "Vídeo: reproducir, fotograma a fotograma, principio y final");
        var sv = new ScrollViewer { Content = grid, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Body.Children.Add(sv);
        var close = new Button { Content = "Cerrar", Style = (Style)Application.Current.FindResource("AccentButton"), IsCancel = true, IsDefault = true, MinWidth = 90 };
        close.Click += (_, _) => Close();
        Buttons.Children.Add(close);
    }
}

/// <summary>Simple MAPI: opens the default mail client with the image attached, when one is registered.</summary>
public static class MailService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    sealed class MapiMessage
    {
        public int reserved; public string? subject; public string? noteText; public string? messageType; public string? dateReceived;
        public string? conversationID; public int flags; public IntPtr originator; public int recipCount; public IntPtr recips;
        public int fileCount; public IntPtr files;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    sealed class MapiFileDesc
    {
        public int reserved; public int flags; public int position = -1; public string? path; public string? name; public IntPtr type;
    }

    [DllImport("MAPI32.DLL", CharSet = CharSet.Ansi)]
    static extern int MAPISendMail(IntPtr session, IntPtr hwnd, MapiMessage message, int flags, int reserved);

    public static bool SendWithAttachment(string path, string subject)
    {
        IntPtr filePtr = IntPtr.Zero;
        try
        {
            var file = new MapiFileDesc { path = path, name = System.IO.Path.GetFileName(path) };
            filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<MapiFileDesc>());
            Marshal.StructureToPtr(file, filePtr, false);
            var msg = new MapiMessage { subject = subject, noteText = "", fileCount = 1, files = filePtr };
            int rc = MAPISendMail(IntPtr.Zero, IntPtr.Zero, msg, 0x1 | 0x8 /* logon UI | dialog */, 0);
            return rc == 0 || rc == 1; // 1 = user cancelled, still handled
        }
        catch (Exception ex)
        {
            Log.Error("MAPI", ex);
            return false;
        }
        finally
        {
            if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
        }
    }
}
