using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaxSnapr.Services;

namespace MaxSnapr.Views;

public sealed class SettingsWindow : Window
{
    static T Res<T>(string k) => (T)Application.Current.FindResource(k);
    readonly AppSettings _s = App.Settings;

    public SettingsWindow(string? tab = null)
    {
        Title = "Preferencias de MaxSnapr";
        Width = 660;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Style = Res<Style>("AppWindow");
        Icon = EditorWindow.Logo;

        var tabs = new TabControl { Margin = new Thickness(12, 8, 12, 0) };
        tabs.Items.Add(Tab("General", General()));
        tabs.Items.Add(Tab("Atajos", Hotkeys()));
        tabs.Items.Add(Tab("Editor", EditorTab()));
        tabs.Items.Add(Tab("Compartir", ShareTab()));
        tabs.Items.Add(Tab("Biblioteca", LibraryTab()));
        tabs.Items.Add(Tab("Integración", IntegrationTab()));
        if (tab != null) tabs.SelectedItem = tabs.Items.Cast<TabItem>().FirstOrDefault(t => (string)t.Header == tab) ?? tabs.Items[0];

        var close = new Button { Content = "Cerrar", Style = Res<Style>("AccentButton"), IsDefault = true, IsCancel = true, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
        close.Click += (_, _) => Close();
        var root = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(tabs);
        Content = root;
        Closing += (_, _) => { _s.Save(); App.Current.RegisterHotkeys(); };
    }

    static TabItem Tab(string header, UIElement content) => new()
    {
        Header = header,
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6, 10, 6, 10) },
    };

    static TextBlock H(string t) => new() { Text = t, Style = Res<Style>("SectionHeader") };
    static TextBlock Note(string t) => new() { Text = t, Style = Res<Style>("Caption"), TextWrapping = TextWrapping.Wrap };

    static CheckBox Toggle(string label, bool value, Action<bool> f)
    {
        var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 8, 0, 0) };
        c.Click += (_, _) => f(c.IsChecked == true);
        return c;
    }

    static TextBox Field(Panel p, string label, string value, Action<string> changed, bool password = false)
    {
        p.Children.Add(new TextBlock { Text = label, Style = Res<Style>("Caption") });
        var tb = new TextBox { Text = value };
        tb.TextChanged += (_, _) => changed(tb.Text);
        p.Children.Add(tb);
        return tb;
    }

    static PasswordBox Secret(Panel p, string label, string protectedValue, Action<string> changed)
    {
        p.Children.Add(new TextBlock { Text = label, Style = Res<Style>("Caption") });
        var pb = new PasswordBox { Password = Secrets.Unprotect(protectedValue), Padding = new Thickness(6, 4, 6, 4), Background = Res<Brush>("Bg.Input"), Foreground = Res<Brush>("Fg.Primary"), BorderBrush = Res<Brush>("Line") };
        pb.PasswordChanged += (_, _) => changed(Secrets.Protect(pb.Password));
        p.Children.Add(pb);
        return pb;
    }

    UIElement General()
    {
        var p = new StackPanel();
        p.Children.Add(H("Apariencia"));
        var theme = new WrapPanel();
        foreach (var (key, name) in new[] { ("Dark", "Oscuro"), ("Light", "Claro"), ("System", "Según el sistema") })
        {
            var rb = new RadioButton { Content = name, GroupName = "theme", Style = Res<Style>("ChipRadio"), IsChecked = _s.Theme == key, MinWidth = 90 };
            var k = key;
            rb.Checked += (_, _) => { _s.Theme = k; ThemeService.Apply(k); };
            theme.Children.Add(rb);
        }
        p.Children.Add(theme);
        p.Children.Add(H("Inicio"));
        p.Children.Add(Toggle("Iniciar MaxSnapr con Windows (en el área de notificación)", _s.RunAtStartup, v => { _s.RunAtStartup = v; StartupService.Apply(v); }));
        p.Children.Add(Toggle("Avisar cuando haya una versión nueva (consulta anónima, una vez al día)", _s.CheckForUpdates, v => { _s.CheckForUpdates = v; _s.SkippedVersion = null; }));
        p.Children.Add(Toggle("Mostrar el widget de captura en el borde de la pantalla", _s.ShowEdgeWidget, v => { _s.ShowEdgeWidget = v; App.Current.UpdateEdgeWidget(); }));
        p.Children.Add(Toggle("Mostrar la bienvenida y los consejos al iniciar", _s.ShowWelcome, v => _s.ShowWelcome = v));
        p.Children.Add(Toggle("Guardar en la biblioteca las imágenes copiadas en otras aplicaciones", _s.WatchClipboard, v => { _s.WatchClipboard = v; App.Current.UpdateClipboardWatcher(); }));
        p.Children.Add(H("Después de capturar"));
        p.Children.Add(Toggle("Copiar al portapapeles", _s.Current.CopyToClipboard, v => _s.Current.CopyToClipboard = v));
        p.Children.Add(Toggle("Abrir en el editor", _s.Current.OpenInEditor, v => _s.Current.OpenInEditor = v));
        p.Children.Add(Toggle("Incluir el cursor", _s.Current.IncludeCursor, v => _s.Current.IncludeCursor = v));
        p.Children.Add(Note("Todas las capturas se guardan automáticamente en la biblioteca, así nunca se pierde ninguna."));
        return p;
    }

    UIElement Hotkeys()
    {
        var p = new StackPanel();
        p.Children.Add(H("Atajos globales"));
        p.Children.Add(Note("Funcionan aunque MaxSnapr esté en segundo plano. Si Windows usa Impr Pant para la Herramienta Recortes, desactívelo en Configuración > Accesibilidad > Teclado, o use el atajo alternativo."));
        void Row(string label, Hotkey value, Action<Hotkey> set)
        {
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var box = new HotkeyBox(value) { Width = 200 };
            box.TextChanged += (_, _) => set(box.Value);
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(row);
        }
        Row("Capturar", _s.CaptureHotkey, h => _s.CaptureHotkey = h);
        Row("Capturar (alternativo)", _s.FallbackCaptureHotkey, h => _s.FallbackCaptureHotkey = h);
        Row("Repetir la última captura", _s.RepeatHotkey, h => _s.RepeatHotkey = h);
        Row("Extraer texto", _s.GrabTextHotkey, h => _s.GrabTextHotkey = h);
        Row("Grabar vídeo", _s.VideoHotkey, h => _s.VideoHotkey = h);
        Row("Captura de pasos", _s.StepCaptureHotkey, h => _s.StepCaptureHotkey = h);
        p.Children.Add(Note("Durante una grabación: Mayús+F9 pausa o reanuda y Mayús+F10 detiene y guarda. Los preajustes de la ventana de captura pueden tener su propio atajo."));
        return p;
    }

    UIElement EditorTab()
    {
        var p = new StackPanel();
        p.Children.Add(H("Ventana"));
        p.Children.Add(Toggle("Mostrar la bandeja de capturas recientes", _s.ShowTray, v => _s.ShowTray = v));
        p.Children.Add(Toggle("Mostrar el panel de propiedades", _s.ShowPropertiesPanel, v => _s.ShowPropertiesPanel = v));
        p.Children.Add(Toggle("Esc cierra el editor", _s.EscClosesEditor, v => _s.EscClosesEditor = v));
        p.Children.Add(Toggle("Revisar la ortografía al escribir texto", _s.SpellCheck, v => _s.SpellCheck = v));
        p.Children.Add(Toggle("Mostrar la cuadrícula de píxeles con zoom alto", _s.PixelGrid, v => _s.PixelGrid = v));
        p.Children.Add(H("Barra de herramientas"));
        p.Children.Add(Note("Marque las herramientas que quiere ver en la barra; las demás quedan en «Más». Se aplica al reabrir el editor."));
        var tools = new WrapPanel();
        var current = _s.ToolbarTools ?? EditorWindow.ToolDefs.Where(d => d.Primary).Select(d => d.Kind).ToList();
        foreach (var d in EditorWindow.ToolDefs)
        {
            var def = d;
            var cb = new CheckBox { Content = d.Label, IsChecked = current.Contains(d.Kind), Margin = new Thickness(0, 6, 16, 0), Width = 150 };
            cb.Click += (_, _) =>
            {
                var list = _s.ToolbarTools ?? EditorWindow.ToolDefs.Where(x => x.Primary).Select(x => x.Kind).ToList();
                if (cb.IsChecked == true && !list.Contains(def.Kind)) list.Add(def.Kind);
                if (cb.IsChecked != true) list.Remove(def.Kind);
                _s.ToolbarTools = EditorWindow.ToolDefs.Select(x => x.Kind).Where(list.Contains).ToList();
            };
            tools.Children.Add(cb);
        }
        p.Children.Add(tools);
        p.Children.Add(H("Exportación"));
        p.Children.Add(new TextBlock { Text = "Formato predeterminado de «Guardar como»", Style = Res<Style>("Caption") });
        var fmt = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var f in new[] { "png", "jpg", "bmp", "gif", "tif", "pdf", "snapr" }) fmt.Items.Add(new ComboBoxItem { Content = f.ToUpperInvariant(), Tag = f, IsSelected = _s.DefaultExport == f });
        fmt.SelectionChanged += (_, _) => { if (fmt.SelectedItem is ComboBoxItem i) _s.DefaultExport = (string)i.Tag; };
        p.Children.Add(fmt);
        PropertiesPanel.SliderRow(p, "Calidad JPEG", 10, 100, _s.JpegQuality, v => _s.JpegQuality = (int)v, "%");
        p.Children.Add(H("Estilos"));
        var reset = new Button { Content = "Restablecer los estilos de todas las herramientas", Style = Res<Style>("OutlineButton"), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            _s.ToolStyles.Clear();
            _s.UserQuickStyles.Clear();
            _s.Save();
            MessageBox.Show(this, "Los estilos se restablecerán la próxima vez que abra el editor.", "MaxSnapr");
        };
        p.Children.Add(reset);
        return p;
    }

    UIElement ShareTab()
    {
        var o = _s.Output;
        var p = new StackPanel();
        p.Children.Add(H("Carpeta de salida y nombres automáticos"));
        var folder = Field(p, "Carpeta", o.Folder, v => o.Folder = v);
        var browse = new Button { Content = "Elegir carpeta…", Style = Res<Style>("OutlineButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        browse.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = o.Folder };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) folder.Text = dlg.SelectedPath;
        };
        p.Children.Add(browse);
        var preview = new TextBlock { Style = Res<Style>("Caption") };
        void Preview() => preview.Text = "Ejemplo: " + Naming.Build(o.NamePattern, "Informe", "Excel", "Ventas.xlsx", 1280, 720, advanceCounter: false) + "." + o.Format;
        Field(p, "Patrón del nombre", o.NamePattern, v => { o.NamePattern = v; Preview(); });
        p.Children.Add(Note("Etiquetas: " + string.Join("  ", Naming.Tokens)));
        Preview();
        p.Children.Add(preview);
        var fmt = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var f in new[] { "png", "jpg", "bmp", "gif", "tif", "pdf" }) fmt.Items.Add(new ComboBoxItem { Content = f.ToUpperInvariant(), Tag = f, IsSelected = o.Format == f });
        fmt.SelectionChanged += (_, _) => { if (fmt.SelectedItem is ComboBoxItem i) { o.Format = (string)i.Tag; Preview(); } };
        p.Children.Add(fmt);

        p.Children.Add(H("Programas"));
        p.Children.Add(Note("Añada aplicaciones (por ejemplo un editor de imágenes) para enviarles la captura desde «Compartir». {archivo} se sustituye por la ruta."));
        var programs = new ListBox { Height = 90, DisplayMemberPath = nameof(ProgramEntry.Name), ItemsSource = o.Programs };
        p.Children.Add(programs);
        var pr = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var add = new Button { Content = "Añadir…", Style = Res<Style>("OutlineButton"), Margin = new Thickness(0, 0, 6, 0) };
        add.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programas (*.exe)|*.exe" };
            if (dlg.ShowDialog(this) != true) return;
            o.Programs.Add(new ProgramEntry { Name = Path.GetFileNameWithoutExtension(dlg.FileName), Path = dlg.FileName });
            programs.Items.Refresh();
        };
        var remove = new Button { Content = "Quitar", Style = Res<Style>("OutlineButton") };
        remove.Click += (_, _) => { if (programs.SelectedItem is ProgramEntry pe) { o.Programs.Remove(pe); programs.Items.Refresh(); } };
        pr.Children.Add(add); pr.Children.Add(remove);
        p.Children.Add(pr);

        p.Children.Add(H("FTP / FTPS"));
        var ftp = o.Ftp;
        Field(p, "Servidor", ftp.Host, v => ftp.Host = v.Trim());
        Field(p, "Puerto", ftp.Port.ToString(), v => { if (int.TryParse(v, out var n)) ftp.Port = n; });
        Field(p, "Usuario", ftp.User, v => ftp.User = v);
        Secret(p, "Contraseña (se guarda cifrada para su usuario de Windows)", ftp.PasswordProtected, v => ftp.PasswordProtected = v);
        Field(p, "Carpeta remota", ftp.RemoteFolder, v => ftp.RemoteFolder = v);
        Field(p, "URL pública (opcional, para copiar el enlace)", ftp.PublicUrlPrefix, v => ftp.PublicUrlPrefix = v);
        p.Children.Add(Toggle("Usar TLS (FTPS)", ftp.UseTls, v => ftp.UseTls = v));
        p.Children.Add(Toggle("Modo pasivo", ftp.Passive, v => ftp.Passive = v));

        p.Children.Add(H("Servidor web propio (enlace para compartir)"));
        var http = o.Http;
        Field(p, "URL de subida (POST multipart)", http.Url, v => http.Url = v.Trim());
        Field(p, "Nombre del campo del archivo", http.FieldName, v => http.FieldName = v);
        Field(p, "Cabecera de autenticación (Nombre: valor)", http.AuthHeader, v => http.AuthHeader = v);
        Field(p, "Ruta del enlace en la respuesta JSON (p. ej. data.url)", http.LinkPath, v => http.LinkPath = v);

        p.Children.Add(H("Slack"));
        p.Children.Add(Note("Necesita un token de bot o de usuario con el permiso files:write. El enlace al archivo se copia al portapapeles."));
        Secret(p, "Token", o.Slack.TokenProtected, v => o.Slack.TokenProtected = v);
        Field(p, "ID del canal (opcional)", o.Slack.ChannelId, v => o.Slack.ChannelId = v.Trim());

        p.Children.Add(H("Carpetas sincronizadas detectadas"));
        var synced = Outputs.SyncedFolders().ToList();
        p.Children.Add(Note(synced.Count == 0 ? "No se ha encontrado OneDrive, Dropbox, Google Drive ni Box en este equipo." : string.Join("\n", synced.Select(s => $"{s.name}: {s.path}"))));

        p.Children.Add(H("Historial"));
        var clear = new Button { Content = $"Borrar el historial ({o.History.Count})", Style = Res<Style>("OutlineButton"), HorizontalAlignment = HorizontalAlignment.Left };
        clear.Click += (_, _) => { o.History.Clear(); clear.Content = "Borrar el historial (0)"; };
        p.Children.Add(clear);
        return p;
    }

    UIElement LibraryTab()
    {
        var p = new StackPanel();
        p.Children.Add(H("Ubicación"));
        var path = new TextBox { Text = _s.LibraryFolder, IsReadOnly = true };
        p.Children.Add(path);
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var change = new Button { Content = "Cambiar…", Style = Res<Style>("OutlineButton"), Margin = new Thickness(0, 0, 6, 0) };
        change.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _s.LibraryFolder, Description = "Carpeta de la biblioteca" };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            _s.LibraryFolder = dlg.SelectedPath;
            path.Text = dlg.SelectedPath;
            MessageBox.Show(this, "La nueva ubicación se usará al reiniciar MaxSnapr. Las capturas existentes no se mueven; puede copiarlas a la nueva carpeta y se detectarán automáticamente.", "Biblioteca");
        };
        var open = new Button { Content = "Abrir la carpeta", Style = Res<Style>("OutlineButton") };
        open.Click += (_, _) => { Directory.CreateDirectory(_s.LibraryFolder); System.Diagnostics.Process.Start("explorer.exe", _s.LibraryFolder); };
        row.Children.Add(change);
        row.Children.Add(open);
        p.Children.Add(row);
        p.Children.Add(Note("Cada captura es un archivo .snapr (ZIP con la imagen original, los objetos editables en JSON y una vista previa). Puede sincronizar la carpeta con OneDrive u otro servicio."));
        p.Children.Add(H("Copia de seguridad"));
        var backup = new WrapPanel();
        var mk = new Button { Content = "Crear copia de seguridad…", Style = Res<Style>("OutlineButton"), Margin = new Thickness(0, 0, 6, 0) };
        mk.Click += (_, _) => Library.LibraryBackup.Create(this);
        var restore = new Button { Content = "Restaurar…", Style = Res<Style>("OutlineButton") };
        restore.Click += (_, _) => Library.LibraryBackup.Restore(this);
        backup.Children.Add(mk); backup.Children.Add(restore);
        p.Children.Add(backup);
        p.Children.Add(Note("La copia es un ZIP con todas las capturas, vídeos e índice. Al restaurar se añaden las que falten sin borrar nada."));
        return p;
    }

    UIElement IntegrationTab()
    {
        var p = new StackPanel();
        p.Children.Add(H("Explorador de Windows"));
        p.Children.Add(Toggle("Abrir los archivos .snapr con MaxSnapr", Integration.SnaprAssociated, v => Integration.SetSnaprAssociation(v)));
        p.Children.Add(Toggle("Añadir «Editar con MaxSnapr» al menú contextual de las imágenes", Integration.ContextMenuInstalled, v => Integration.SetContextMenu(v)));
        p.Children.Add(Note("Solo afecta a su usuario (HKEY_CURRENT_USER); no requiere permisos de administrador."));
        p.Children.Add(H("Diagnóstico"));
        var diag = new Button { Content = "Guardar un informe de diagnóstico…", Style = Res<Style>("OutlineButton"), HorizontalAlignment = HorizontalAlignment.Left };
        diag.Click += (_, _) => Diagnostics.Save(this);
        p.Children.Add(diag);
        var resetAll = new Button { Content = "Restablecer todas las preferencias…", Style = Res<Style>("OutlineButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        resetAll.Click += (_, _) => Diagnostics.ResetSettings(this);
        p.Children.Add(resetAll);
        p.Children.Add(Note("Restablecer guarda antes una copia de los ajustes; no borra capturas, estilos personalizados ni preajustes de la biblioteca."));
        p.Children.Add(H("Móvil"));
        p.Children.Add(Note("Envíe fotos o capturas del móvil a la biblioteca desde el navegador del teléfono, en la misma red Wi-Fi."));
        var mobile = new Button { Content = "Recibir desde el móvil…", Style = Res<Style>("OutlineButton"), HorizontalAlignment = HorizontalAlignment.Left };
        mobile.Click += (_, _) => new MobileReceiveWindow { Owner = this }.Show();
        p.Children.Add(mobile);
        return p;
    }
}
