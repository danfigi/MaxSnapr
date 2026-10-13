using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaxSnapr.Services;
using MaxSnapr.Video;

namespace MaxSnapr.Views;

/// <summary>Text box that records a key combination instead of text.</summary>
public sealed class HotkeyBox : TextBox
{
    Hotkey _value;
    public Hotkey Value { get => _value; set { _value = value; Text = value.ToString(); } }

    public HotkeyBox(Hotkey initial)
    {
        _value = initial;
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Text = initial.ToString();
        ToolTip = "Haga clic y pulse la combinación de teclas. Retroceso para borrar.";
        Cursor = Cursors.Hand;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        if (key is Key.Back or Key.Delete) { Value = new Hotkey(); return; }
        if (key == Key.Tab) { e.Handled = false; return; }
        var m = Keyboard.Modifiers;
        Value = new Hotkey
        {
            Ctrl = m.HasFlag(ModifierKeys.Control), Shift = m.HasFlag(ModifierKeys.Shift), Alt = m.HasFlag(ModifierKeys.Alt), Win = m.HasFlag(ModifierKeys.Windows),
            Key = (int)key,
        };
    }
}

/// <summary>The capture launcher: image / text / video tabs, selection type, delay, output and effect options, presets.</summary>
public sealed class CaptureWindow : Window
{
    readonly CapturePreset _p = App.Settings.Current;
    readonly VideoSettings _v = App.Settings.Video;
    readonly ListBox _presets = new() { Height = 330, Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel _imageOptions = new();
    readonly StackPanel _videoOptions = new();
    readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel _fixedRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

    static T Res<T>(string k) => (T)Application.Current.FindResource(k);

    static readonly (CaptureSelectionMode mode, string name, string hint)[] Modes =
    {
        (CaptureSelectionMode.Region, "Región (todo en uno)", "Arrastre para una región o haga clic en una ventana; después puede ajustar la selección."),
        (CaptureSelectionMode.Window, "Ventana", "Haga clic en la ventana que quiera capturar."),
        (CaptureSelectionMode.Object, "Objeto de la interfaz", "Señale un botón, panel o menú: se resalta el elemento exacto bajo el cursor."),
        (CaptureSelectionMode.FullScreen, "Pantalla completa", "Captura el monitor donde está el cursor."),
        (CaptureSelectionMode.AllScreens, "Todas las pantallas", "Captura todos los monitores en una sola imagen."),
        (CaptureSelectionMode.ActiveWindow, "Ventana activa", "Captura la ventana en primer plano (útil con retraso)."),
        (CaptureSelectionMode.FixedRegion, "Región de tamaño fijo", "Un marco del tamaño indicado sigue al cursor; haga clic para capturar."),
        (CaptureSelectionMode.Freehand, "Mano alzada", "Dibuje el contorno; lo de fuera queda transparente."),
        (CaptureSelectionMode.MultiArea, "Varias áreas", "Arrastre varias regiones (cada arrastre añade una) y pulse Intro."),
        (CaptureSelectionMode.Menu, "Menú desplegable", "Abra el menú durante la cuenta atrás; se capturan el menú y sus submenús."),
        (CaptureSelectionMode.Scrolling, "Con desplazamiento (automático)", "Seleccione una ventana o región desplazable: MaxSnapr se desplaza solo y une las imágenes."),
        (CaptureSelectionMode.Panoramic, "Panorámica (manual)", "Seleccione una región y desplácese usted mismo; pulse Detener al terminar."),
        (CaptureSelectionMode.ResizedWindow, "Ventana a un tamaño concreto", "Haga clic en una ventana: se redimensiona al ancho y alto indicados, se captura y vuelve a su tamaño."),
        (CaptureSelectionMode.Clipboard, "Desde el portapapeles", "Abre en el editor la imagen que haya en el portapapeles."),
    };

    public CaptureWindow()
    {
        Title = "MaxSnapr – Captura";
        Width = 700;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Style = Res<Style>("AppWindow");
        Icon = EditorWindow.Logo;
        Topmost = true;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(BuildPresets());

        var right = new StackPanel { Margin = new Thickness(20, 16, 20, 20) };
        Grid.SetColumn(right, 1);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        tabs.Children.Add(Chip("Imagen", !_p.GrabText && !_p.RecordVideo, () => { _p.GrabText = false; _p.RecordVideo = false; UpdateMode(); }));
        tabs.Children.Add(Chip("Texto (OCR)", _p.GrabText, () => { _p.GrabText = true; _p.RecordVideo = false; UpdateMode(); }));
        tabs.Children.Add(Chip("Vídeo", _p.RecordVideo, () => { _p.RecordVideo = true; _p.GrabText = false; UpdateMode(); }));
        right.Children.Add(tabs);

        right.Children.Add(Caption("Selección"));
        var sel = new ComboBox { MaxDropDownHeight = 420 };
        foreach (var (mode, name, _) in Modes) sel.Items.Add(new ComboBoxItem { Content = name, Tag = mode, IsSelected = _p.Selection == mode });
        if (sel.SelectedItem == null) sel.SelectedIndex = 0;
        sel.SelectionChanged += (_, _) => { if (sel.SelectedItem is ComboBoxItem i) { _p.Selection = (CaptureSelectionMode)i.Tag; UpdateMode(); } };
        right.Children.Add(sel);
        _hint.Style = Res<Style>("Caption");
        right.Children.Add(_hint);
        var fw = new TextBox { Width = 70, Text = _p.FixedWidth.ToString() };
        var fh = new TextBox { Width = 70, Text = _p.FixedHeight.ToString() };
        fw.TextChanged += (_, _) => { if (int.TryParse(fw.Text, out var v)) _p.FixedWidth = Math.Clamp(v, 8, 10000); };
        fh.TextChanged += (_, _) => { if (int.TryParse(fh.Text, out var v)) _p.FixedHeight = Math.Clamp(v, 8, 10000); };
        _fixedRow.Children.Add(new TextBlock { Text = "Ancho", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _fixedRow.Children.Add(fw);
        _fixedRow.Children.Add(new TextBlock { Text = "Alto", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
        _fixedRow.Children.Add(fh);
        right.Children.Add(_fixedRow);

        right.Children.Add(Caption("Retraso"));
        var delay = new ComboBox();
        foreach (var s in new[] { 0, 3, 5, 10, 15, 30 })
            delay.Items.Add(new ComboBoxItem { Content = s == 0 ? "Sin retraso" : $"{s} segundos", Tag = s, IsSelected = _p.DelaySeconds == s });
        if (delay.SelectedItem == null) delay.SelectedIndex = 0;
        delay.SelectionChanged += (_, _) => { if (delay.SelectedItem is ComboBoxItem i) _p.DelaySeconds = (int)i.Tag; };
        right.Children.Add(delay);

        BuildImageOptions();
        BuildVideoOptions();
        right.Children.Add(_imageOptions);
        right.Children.Add(_videoOptions);

        var capture = new Button { Style = Res<Style>("CaptureButton"), Height = 52, Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        capture.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = "\uE722", FontFamily = Res<FontFamily>("IconFont"), FontSize = 20, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "Capturar", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = App.Current.CaptureHotkeyText, Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        capture.Click += (_, _) => Start(_p);
        right.Children.Add(capture);
        root.Children.Add(right);
        Content = root;
        UpdateMode();

        Closing += (_, _) => App.Settings.Save();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); if (e.Key == Key.Enter && e.OriginalSource is not TextBox) Start(_p); };
    }

    UIElement BuildPresets()
    {
        var left = new StackPanel { Margin = new Thickness(16) };
        left.Children.Add(new TextBlock { Text = "Preajustes", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        ScrollViewer.SetHorizontalScrollBarVisibility(_presets, ScrollBarVisibility.Disabled);
        _presets.DisplayMemberPath = nameof(CapturePreset.Name);
        RefreshPresets();
        _presets.MouseDoubleClick += (_, _) => { if (_presets.SelectedItem is CapturePreset cp) Start(cp); };
        _presets.SelectionChanged += (_, _) => { if (_presets.SelectedItem is CapturePreset cp) LoadPreset(cp); };
        left.Children.Add(_presets);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Button B(string text, Action a)
        {
            var b = new Button { Content = text, Style = Res<Style>("OutlineButton"), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 5, 10, 5) };
            b.Click += (_, _) => a();
            buttons.Children.Add(b);
            return b;
        }
        B("Guardar actual…", SavePreset);
        B("Eliminar", () =>
        {
            if (_presets.SelectedItem is not CapturePreset cp) return;
            App.Settings.Presets.Remove(cp);
            App.Settings.Save();
            App.Current.RegisterHotkeys();
            RefreshPresets();
        });
        B("Exportar…", () => PresetFiles.Export(this));
        B("Importar…", () => { if (PresetFiles.Import(this)) { App.Current.RegisterHotkeys(); RefreshPresets(); } });
        left.Children.Add(buttons);
        left.Children.Add(new TextBlock { Text = "Doble clic en un preajuste para capturar con él. Puede asignarle un atajo al guardarlo.", Style = Res<Style>("Caption"), TextWrapping = TextWrapping.Wrap });
        return new Border { Background = Res<Brush>("Bg.Panel"), BorderBrush = Res<Brush>("Line"), BorderThickness = new Thickness(0, 0, 1, 0), Child = left };
    }

    void BuildImageOptions()
    {
        var o = _imageOptions;
        o.Children.Add(Toggle("Capturar el cursor", _p.IncludeCursor, v => _p.IncludeCursor = v));
        o.Children.Add(Toggle("Copiar al portapapeles", _p.CopyToClipboard, v => _p.CopyToClipboard = v));
        o.Children.Add(Toggle("Abrir en el editor", _p.OpenInEditor, v => _p.OpenInEditor = v));
        o.Children.Add(Toggle("Guardar también en la carpeta de salida (nombre automático)", _p.ShareToFolder, v => _p.ShareToFolder = v));
        o.Children.Add(Caption("Efectos al capturar"));
        var fx = new WrapPanel();
        fx.Children.Add(Toggle("Borde", _p.EffectBorder, v => _p.EffectBorder = v, 14));
        fx.Children.Add(Toggle("Sombra", _p.EffectShadow, v => _p.EffectShadow = v, 14));
        fx.Children.Add(Toggle("Borde rasgado", _p.EffectTornEdge, v => _p.EffectTornEdge = v, 14));
        o.Children.Add(fx);
        o.Children.Add(Caption("Repetir automáticamente"));
        var rep = new StackPanel { Orientation = Orientation.Horizontal };
        var every = new ComboBox { Width = 150 };
        foreach (var s in new[] { 0, 5, 10, 30, 60, 300 })
            every.Items.Add(new ComboBoxItem { Content = s == 0 ? "Una sola vez" : s < 60 ? $"Cada {s} s" : $"Cada {s / 60} min", Tag = s, IsSelected = _p.IntervalSeconds == s });
        if (every.SelectedItem == null) every.SelectedIndex = 0;
        every.SelectionChanged += (_, _) => { if (every.SelectedItem is ComboBoxItem i) _p.IntervalSeconds = (int)i.Tag; };
        var count = new TextBox { Width = 50, Text = _p.IntervalCount.ToString(), Margin = new Thickness(8, 0, 6, 0), ToolTip = "Número de capturas" };
        count.TextChanged += (_, _) => { if (int.TryParse(count.Text, out var n)) _p.IntervalCount = Math.Clamp(n, 1, 1000); };
        rep.Children.Add(every); rep.Children.Add(count);
        rep.Children.Add(new TextBlock { Text = "veces", VerticalAlignment = VerticalAlignment.Center });
        o.Children.Add(rep);
    }

    void BuildVideoOptions()
    {
        var o = _videoOptions;
        o.Children.Add(Caption("Fotogramas por segundo"));
        var fps = new WrapPanel();
        foreach (var f in new[] { 15, 24, 30, 60 }) { var ff = f; fps.Children.Add(Chip($"{f} fps", _v.Fps == f, () => _v.Fps = ff, "fps")); }
        o.Children.Add(fps);
        o.Children.Add(Toggle("Audio del sistema", _v.SystemAudio, v => _v.SystemAudio = v));
        var micRow = new DockPanel();
        var mics = new ComboBox { Width = 230 };
        mics.Items.Add(new ComboBoxItem { Content = "Micrófono predeterminado", Tag = null, IsSelected = _v.MicrophoneId == null });
        foreach (var d in Devices.Microphones()) mics.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id, IsSelected = d.Id == _v.MicrophoneId });
        if (mics.SelectedItem == null) mics.SelectedIndex = 0;
        mics.SelectionChanged += (_, _) => _v.MicrophoneId = (mics.SelectedItem as ComboBoxItem)?.Tag as string;
        DockPanel.SetDock(mics, Dock.Right);
        micRow.Children.Add(mics);
        micRow.Children.Add(Toggle("Micrófono", _v.Microphone, v => _v.Microphone = v));
        o.Children.Add(micRow);
        var camRow = new DockPanel();
        var cams = new ComboBox { Width = 230 };
        cams.Items.Add(new ComboBoxItem { Content = "Cámara predeterminada", Tag = null, IsSelected = true });
        _ = FillCameras(cams);
        cams.SelectionChanged += (_, _) => _v.WebcamId = (cams.SelectedItem as ComboBoxItem)?.Tag as string;
        DockPanel.SetDock(cams, Dock.Right);
        camRow.Children.Add(cams);
        camRow.Children.Add(Toggle("Cámara web", _v.Webcam, v => _v.Webcam = v));
        o.Children.Add(camRow);
        var shape = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (s, n) in new[] { (WebcamShape.Circle, "Círculo"), (WebcamShape.Rounded, "Redondeada"), (WebcamShape.Rectangle, "Rectángulo") })
            shape.Children.Add(Chip(n, _v.WebcamShape == s, () => _v.WebcamShape = s, "camshape"));
        foreach (var (c, n) in new[] { (WebcamCorner.BottomRight, "↘"), (WebcamCorner.BottomLeft, "↙"), (WebcamCorner.TopRight, "↗"), (WebcamCorner.TopLeft, "↖") })
        {
            var rb = Chip(n, _v.WebcamCorner == c, () => _v.WebcamCorner = c, "camcorner");
            rb.MinWidth = 34;
            shape.Children.Add(rb);
        }
        o.Children.Add(shape);
        o.Children.Add(Toggle("Grabar el cursor", _v.Cursor, v => _v.Cursor = v));
        o.Children.Add(Toggle("Resaltar los clics", _v.HighlightClicks, v => _v.HighlightClicks = v));
        o.Children.Add(Toggle("Cuenta atrás de 3 segundos", _v.Countdown, v => _v.Countdown = v));
        var gifRow = new DockPanel();
        var gif = new ComboBox { Width = 230 };
        foreach (var (p, i) in GifProfile.All.Select((p, i) => (p, i))) gif.Items.Add(new ComboBoxItem { Content = p.Name, IsSelected = _v.GifProfile == i });
        gif.SelectionChanged += (_, _) => _v.GifProfile = gif.SelectedIndex;
        DockPanel.SetDock(gif, Dock.Right);
        gifRow.Children.Add(gif);
        gifRow.Children.Add(Toggle("Guardar como GIF", _v.SaveAsGif, v => _v.SaveAsGif = v));
        o.Children.Add(gifRow);
        o.Children.Add(Toggle("Abrir en el editor", _v.OpenInEditor, v => _v.OpenInEditor = v));
        o.Children.Add(new TextBlock { Text = "Durante la grabación: Mayús+F9 pausa y Mayús+F10 detiene. El marco y los controles no aparecen en el vídeo.", Style = Res<Style>("Caption"), TextWrapping = TextWrapping.Wrap });
    }

    async System.Threading.Tasks.Task FillCameras(ComboBox cams)
    {
        foreach (var d in await Devices.CamerasAsync()) cams.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id, IsSelected = d.Id == _v.WebcamId });
    }

    void UpdateMode()
    {
        _imageOptions.Visibility = _p.RecordVideo ? Visibility.Collapsed : Visibility.Visible;
        _videoOptions.Visibility = _p.RecordVideo ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = Modes.FirstOrDefault(m => m.mode == _p.Selection).hint ?? "";
        if (_p.RecordVideo) _hint.Text = "Vídeo: seleccione una región, una ventana o pulse Intro para la pantalla completa.";
        _fixedRow.Visibility = _p.Selection is CaptureSelectionMode.FixedRegion or CaptureSelectionMode.ResizedWindow ? Visibility.Visible : Visibility.Collapsed;
    }

    static TextBlock Caption(string t) => new() { Text = t, Style = Res<Style>("Caption") };

    static CheckBox Toggle(string label, bool value, Action<bool> f, double rightMargin = 0)
    {
        var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 8, rightMargin, 0) };
        c.Click += (_, _) => f(c.IsChecked == true);
        return c;
    }

    static RadioButton Chip(string text, bool on, Action a, string group = "mode")
    {
        var rb = new RadioButton { Content = text, GroupName = group, Style = Res<Style>("ChipRadio"), IsChecked = on, MinWidth = 80 };
        rb.Checked += (_, _) => a();
        return rb;
    }

    void RefreshPresets()
    {
        _presets.ItemsSource = null;
        _presets.ItemsSource = App.Settings.Presets;
    }

    void LoadPreset(CapturePreset cp)
    {
        foreach (var prop in typeof(CapturePreset).GetProperties().Where(p => p.CanWrite && p.Name is not (nameof(CapturePreset.Name) or nameof(CapturePreset.Hotkey))))
            prop.SetValue(_p, prop.GetValue(cp));
        // Rebuild so every control reflects the preset.
        var w = new CaptureWindow { Left = Left, Top = Top, WindowStartupLocation = WindowStartupLocation.Manual };
        w.Show();
        Close();
    }

    void SavePreset()
    {
        var dlg = new PresetDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var cp = new CapturePreset();
        foreach (var prop in typeof(CapturePreset).GetProperties().Where(p => p.CanWrite)) prop.SetValue(cp, prop.GetValue(_p));
        cp.Name = dlg.PresetName;
        cp.Hotkey = dlg.Hotkey.Key == 0 ? null : dlg.Hotkey;
        App.Settings.Presets.Add(cp);
        App.Settings.Save();
        App.Current.RegisterHotkeys();
        RefreshPresets();
    }

    void Start(CapturePreset preset)
    {
        App.Settings.Save();
        Close();
        _ = App.Current.Capture.RunAsync(preset);
    }

    sealed class PresetDialog : DialogWindow
    {
        readonly TextBox _name;
        readonly HotkeyBox _hk;
        public string PresetName => string.IsNullOrWhiteSpace(_name.Text) ? "Preajuste" : _name.Text.Trim();
        public Hotkey Hotkey => _hk.Value;

        public PresetDialog() : base("Guardar preajuste")
        {
            _name = Field("Nombre", "Mi preajuste");
            Caption("Atajo de teclado (opcional)");
            _hk = new HotkeyBox(new Hotkey());
            Body.Children.Add(_hk);
        }
    }
}

/// <summary>Import/export of capture presets as JSON.</summary>
public static class PresetFiles
{
    public static void Export(Window owner)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Preajustes de MaxSnapr (*.json)|*.json", FileName = "Preajustes MaxSnapr" };
        if (dlg.ShowDialog(owner) != true) return;
        System.IO.File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(App.Settings.Presets, new System.Text.Json.JsonSerializerOptions(Editor.Model.ToolStyle.Json) { WriteIndented = true }));
    }

    public static bool Import(Window owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Preajustes de MaxSnapr (*.json)|*.json" };
        if (dlg.ShowDialog(owner) != true) return false;
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<CapturePreset>>(System.IO.File.ReadAllText(dlg.FileName), Editor.Model.ToolStyle.Json);
            if (list == null) return false;
            App.Settings.Presets.AddRange(list);
            App.Settings.Save();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "El archivo no es válido: " + ex.Message, "Importar preajustes");
            return false;
        }
    }
}
