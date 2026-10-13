using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaxSnapr.Editor;
using MaxSnapr.Editor.Model;
using MaxSnapr.Editor.Raster;
using MaxSnapr.Editor.Tools;
using MaxSnapr.Library;
using MaxSnapr.Services;
using Microsoft.Win32;

namespace MaxSnapr.Views;

public sealed record ToolDef(ToolKind Kind, string Label, string Glyph, Key Shortcut, bool Primary);

public partial class EditorWindow : Window
{
    public static ImageSource Logo { get; } = AppIcon.CreateImageSource();

    public static readonly ToolDef[] ToolDefs =
    {
        new(ToolKind.Select, "Mover", "\uE7C2", Key.V, true),
        new(ToolKind.Arrow, "Flecha", "\uE72A", Key.A, true),
        new(ToolKind.Text, "Texto", "\uE8D2", Key.T, true),
        new(ToolKind.Callout, "Globo", "\uE8BD", Key.K, true),
        new(ToolKind.Shape, "Forma", "\uE739", Key.S, true),
        new(ToolKind.Step, "Paso", "\uE8FD", Key.N, true),
        new(ToolKind.Highlighter, "Resaltador", "\uE7E6", Key.H, true),
        new(ToolKind.Blur, "Difuminar", "\uE727", Key.B, true),
        new(ToolKind.Crop, "Recortar", "\uE7A8", Key.C, true),
        new(ToolKind.Line, "Línea", "\uE738", Key.L, false),
        new(ToolKind.Pen, "Pluma", "\uE70F", Key.P, false),
        new(ToolKind.Magnify, "Lupa", "\uE71E", Key.M, false),
        new(ToolKind.Spotlight, "Foco", "\uEA80", Key.O, false),
        new(ToolKind.Stamp, "Sello", "\uE734", Key.G, false),
        new(ToolKind.CutOut, "Recortar franja", "\uE8C6", Key.X, false),
        new(ToolKind.Selection, "Selección", "\uE8B3", Key.R, false),
        new(ToolKind.Fill, "Relleno", "\uE790", Key.F, false),
        new(ToolKind.Eraser, "Borrador", "\uE75C", Key.E, false),
        new(ToolKind.MagicWand, "Varita mágica", "\uE945", Key.W, false),
        new(ToolKind.Favorites, "Favoritos", "\uE735", Key.Q, false),
    };

    readonly Dictionary<ToolKind, ToolBase> _tools = new();
    readonly Dictionary<ToolKind, RadioButton> _toolButtons = new();
    RadioButton? _moreSlot;
    ToolKind _currentKind = ToolKind.Arrow;
    EditorDocument? _doc;
    LibraryItem? _item;
    readonly ICollectionView _trayView;
    readonly DispatcherTimer _autosave;
    List<Annotation> _objectClipboard = new();
    bool _suppressTraySelection;
    Point _trayDragStart;

    public EditorWindow()
    {
        InitializeComponent();
        Icon = Logo;
        SupportMenu.Visibility = SupportSeparator.Visibility = Support.Available ? Visibility.Visible : Visibility.Collapsed;
        foreach (var def in ToolDefs) _tools[def.Kind] = CreateTool(def.Kind);
        BuildToolStrip();

        var source = new CollectionViewSource { Source = LibraryStore.Instance.Items };
        _trayView = source.View;
        _trayView.Filter = o => o is LibraryItem i && i.InTray;
        Tray.ItemsSource = _trayView;

        Canvas.SelectionChanged += (_, _) => RefreshProperties();
        Canvas.ContextMenuRequested += (_, p) => ShowCanvasMenu();
        Canvas.DocumentChanged += (_, _) => UpdateStatus();
        Canvas.ZoomChanged += (_, _) => UpdateZoomLabel();
        Canvas.CursorMoved += (_, p) => CursorText.Text = _doc != null && p.X >= 0 && p.Y >= 0 && p.X < _doc.Width && p.Y < _doc.Height ? $"{(int)p.X}, {(int)p.Y}" : "";
        SelectionTool.ToolSwitched += (_, k) => SelectTool(k);
        ThemeService.ThemeChanged += (_, _) => ApplyThemeToCanvas();
        ApplyThemeToCanvas();

        ShowTrayItem.IsChecked = App.Settings.ShowTray;
        ShowPropsItem.IsChecked = App.Settings.ShowPropertiesPanel;
        ApplyPanelVisibility();
        EffectsPanel.Build(EffectsHost, this);

        _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autosave.Tick += (_, _) => { if (_doc?.IsDirty == true) SaveCurrent(); };
        _autosave.Start();

        PreviewKeyDown += OnPreviewKeyDown;
        Drop += OnDropFiles;
        Closing += (_, _) => { SaveCurrent(); App.Settings.Save(); };
        Loaded += (_, _) =>
        {
            SelectTool(ToolKind.Arrow);
            if (_doc == null)
            {
                var first = LibraryStore.Instance.Items.FirstOrDefault(i => i.InTray);
                if (first != null) OpenItem(first); else ShowEmpty();
            }
        };
        UpdateStatus();
    }

    void ApplyThemeToCanvas()
    {
        Canvas.Backdrop = (Brush)FindResource("Bg.Canvas");
        Canvas.Invalidate();
        EmptyHint.Text = $"Pulse {App.Current.CaptureHotkeyText} en cualquier momento o use el botón Capturar.\nTambién puede arrastrar aquí imágenes o pegarlas con Ctrl+V.";
    }

    // ================================================================ tools

    ToolBase CreateTool(ToolKind kind)
    {
        ToolBase t = kind switch
        {
            ToolKind.Select or ToolKind.Favorites => new SelectTool(),
            ToolKind.Crop => new CropTool(),
            ToolKind.CutOut => new CutOutTool(),
            ToolKind.Selection => new SelectionTool(),
            ToolKind.Fill => new FillTool(),
            ToolKind.Eraser => new EraserTool(),
            ToolKind.MagicWand => new MagicWandTool(),
            _ => new CreateTool(kind),
        };
        t.Style = App.Settings.ToolStyles.TryGetValue(kind, out var s) ? s : DefaultStyles.For(kind);
        App.Settings.ToolStyles[kind] = t.Style;
        if (t is CropTool crop) crop.StateChanged += (_, _) => UpdateStatus();
        return t;
    }

    void BuildToolStrip()
    {
        ToolStrip.Children.Clear();
        foreach (var def in ToolDefs.Where(IsPrimary))
        {
            var rb = MakeToolButton(def);
            _toolButtons[def.Kind] = rb;
            ToolStrip.Children.Add(rb);
        }
        // A slot that shows whichever secondary tool was last picked from "Más".
        _moreSlot = new RadioButton { Style = (Style)FindResource("ToolButton"), Visibility = Visibility.Collapsed };
        _moreSlot.Checked += (_, _) => { if (_moreSlot.DataContext is ToolDef d) SelectTool(d.Kind); };
        ToolStrip.Children.Add(_moreSlot);

        var more = new Button { Style = (Style)FindResource("FlatButton"), Width = 58, Height = 54, Padding = new Thickness(0), ToolTip = "Más herramientas" };
        more.Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "\uE712", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Más", FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) },
            },
        };
        more.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = more, Placement = PlacementMode.Bottom };
            foreach (var def in ToolDefs.Where(d => !IsPrimary(d)))
            {
                var mi = new MenuItem
                {
                    Header = def.Label, InputGestureText = def.Shortcut.ToString(),
                    Icon = new TextBlock { Text = def.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15 },
                };
                var d = def;
                mi.Click += (_, _) => SelectTool(d.Kind);
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        };
        ToolStrip.Children.Add(more);
    }

    // A narrow window hides the scrollbar; the wheel scrolls the tool strip sideways instead.
    void ToolScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        ToolScroll.ScrollToHorizontalOffset(ToolScroll.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    static bool IsPrimary(ToolDef d) => App.Settings.ToolbarTools?.Contains(d.Kind) ?? d.Primary;

    RadioButton MakeToolButton(ToolDef def)
    {
        var rb = new RadioButton { Style = (Style)FindResource("ToolButton"), Content = def.Label, Tag = def.Glyph, ToolTip = $"{def.Label} ({def.Shortcut})", DataContext = def };
        rb.Checked += (_, _) => SelectTool(def.Kind);
        return rb;
    }

    public void SelectTool(ToolKind kind)
    {
        _currentKind = kind;
        var def = ToolDefs.First(d => d.Kind == kind);
        if (_toolButtons.TryGetValue(kind, out var rb)) { if (rb.IsChecked != true) rb.IsChecked = true; }
        else if (_moreSlot != null)
        {
            _moreSlot.DataContext = def;
            _moreSlot.Content = def.Label;
            _moreSlot.Tag = def.Glyph;
            _moreSlot.ToolTip = $"{def.Label} ({def.Shortcut})";
            _moreSlot.Visibility = Visibility.Visible;
            if (_moreSlot.IsChecked != true) _moreSlot.IsChecked = true;
        }
        var tool = _tools[kind];
        if (Canvas.Tool != tool) Canvas.Tool = tool;
        tool.Reset();
        CropBar.Visibility = kind == ToolKind.Crop && _doc != null ? Visibility.Visible : Visibility.Collapsed;
        RefreshProperties();
        UpdateStatus();
    }

    ToolBase CurrentTool => _tools[_currentKind];

    // ================================================================ properties

    void RefreshProperties()
    {
        PropsHost.Children.Clear();
        var sel = Canvas.Selection;
        if (sel.Count > 0 && _currentKind is ToolKind.Select or ToolKind.Arrow or ToolKind.Line or ToolKind.Text or ToolKind.Callout or ToolKind.Shape
                or ToolKind.Step or ToolKind.Pen or ToolKind.Highlighter or ToolKind.Blur or ToolKind.Magnify or ToolKind.Spotlight or ToolKind.Stamp)
        {
            var first = sel[0];
            var kind = KindOf(first);
            var header = sel.Count == 1 ? first.DisplayName : $"{sel.Count} objetos seleccionados";
            PropertiesPanel.Build(PropsHost, kind, first.Style, header, isObject: true, change =>
            {
                _doc?.Checkpoint();
                foreach (var a in sel.Where(a => KindOf(a) == kind || sel.Count == 1)) change(a.Style);
                foreach (var t in sel.OfType<TextAnnotation>()) { t.EnsureTail(); t.FitHeight(Canvas.PixelsPerDip); }
                Canvas.StyleTextEditor();
                _doc?.RaiseChanged();
                // The tool remembers the last style used, like a sticky pen.
                change(_tools[kind].Style);
            }, this);
            PropertiesPanel.AddObjectActions(PropsHost, this);
            return;
        }
        var def = ToolDefs.First(d => d.Kind == _currentKind);
        PropertiesPanel.Build(PropsHost, _currentKind, CurrentTool.Style, def.Label, isObject: false, change =>
        {
            change(CurrentTool.Style);
            Canvas.Cursor = CurrentTool.Cursor;
            Canvas.Invalidate();
        }, this);
    }

    public static ToolKind KindOf(Annotation a) => a switch
    {
        LineAnnotation l => l.Style.StartCap == LineCap.None && l.Style.EndCap == LineCap.None ? ToolKind.Line : ToolKind.Arrow,
        TextAnnotation t => t.Style.Callout == CalloutKind.None ? ToolKind.Text : ToolKind.Callout,
        ShapeAnnotation => ToolKind.Shape,
        StepAnnotation => ToolKind.Step,
        PenAnnotation p => p.Highlighter ? ToolKind.Highlighter : ToolKind.Pen,
        RedactAnnotation => ToolKind.Blur,
        MagnifyAnnotation => ToolKind.Magnify,
        SpotlightAnnotation => ToolKind.Spotlight,
        StampAnnotation => ToolKind.Stamp,
        _ => ToolKind.Select,
    };

    public ToolKind CurrentKind => _currentKind;
    public EditorDocument? Document => _doc;
    public LibraryItem? CurrentItem => _item;
    public EditorCanvas EditorCanvas => Canvas;

    /// <summary>Applies a quick style to the current tool and to selected objects of that kind.</summary>
    public void ApplyQuickStyle(ToolKind kind, ToolStyle style)
    {
        var target = _tools[kind].Style;
        CopyStyle(style, target);
        if (Canvas.Selection.Count > 0 && _doc != null)
        {
            _doc.Checkpoint();
            foreach (var a in Canvas.Selection.Where(a => KindOf(a) == kind)) CopyStyle(style, a.Style);
            foreach (var t in Canvas.Selection.OfType<TextAnnotation>()) { t.EnsureTail(); t.FitHeight(Canvas.PixelsPerDip); }
            _doc.RaiseChanged();
        }
        RefreshProperties();
    }

    static void CopyStyle(ToolStyle from, ToolStyle to)
    {
        foreach (var p in typeof(ToolStyle).GetProperties().Where(p => p.CanWrite)) p.SetValue(to, p.GetValue(from));
    }

    // ================================================================ documents

    public void OpenDocument(EditorDocument doc, LibraryItem? item)
    {
        SaveCurrent();
        HideVideo();
        _doc = doc;
        _item = item;
        HideLibrary();
        EmptyState.Visibility = Visibility.Collapsed;
        CanvasScroll.Visibility = Visibility.Visible;
        Canvas.Document = doc;
        foreach (var t in _tools.Values) t.Reset();
        Dispatcher.BeginInvoke(() => ZoomFit_Click(this, new RoutedEventArgs()), DispatcherPriority.Loaded);
        if (item != null)
        {
            item.InTray = true;
            _trayView.Refresh();
            _suppressTraySelection = true;
            Tray.SelectedItem = item;
            Tray.ScrollIntoView(item);
            _suppressTraySelection = false;
        }
        SelectTool(_currentKind == ToolKind.Crop ? ToolKind.Arrow : _currentKind);
        UpdateStatus();
        Canvas.Focus();
    }

    void HideVideo()
    {
        if (VideoPane.Visibility != Visibility.Visible) return;
        VideoPane.Unload();
        VideoPane.Visibility = Visibility.Collapsed;
        ToolStrip.Visibility = Visibility.Visible;
        ApplyPanelVisibility();
    }

    void ShowMedia(LibraryItem item)
    {
        SaveCurrent();
        Canvas.Document = null;
        _doc = null;
        _item = item;
        HideLibrary();
        CanvasScroll.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        CropBar.Visibility = Visibility.Collapsed;
        VideoPane.Visibility = Visibility.Visible;
        ToolStrip.Visibility = Visibility.Hidden;
        PropsPanel.Visibility = Visibility.Collapsed;
        PropsColumn.Width = new GridLength(0);
        VideoPane.Load(item, this);
        item.InTray = true;
        _trayView.Refresh();
        _suppressTraySelection = true;
        Tray.SelectedItem = item;
        Tray.ScrollIntoView(item);
        _suppressTraySelection = false;
        Title = $"{item.Name} – MaxSnapr";
        TitleText.Text = item.Name;
        SizeText.Text = $"{item.Width} × {item.Height} px · {TimeSpan.FromSeconds(item.DurationSeconds):m\\:ss}";
        StatusText.Text = item.Kind;
    }

    /// <summary>Flattened pixels of a library item (first frame for video and GIF).</summary>
    public static BitmapSource RenderItem(LibraryItem i) => i.IsMedia ? (i.IsGif ? Video.GifPlayer.Load(i.FullPath, 1)[0].Image : i.Thumbnail!) : EditorDocument.Load(i.FullPath).Flatten();

    public void OpenItem(LibraryItem item)
    {
        try
        {
            if (item.IsMedia)
            {
                if (_item == item && VideoPane.Visibility == Visibility.Visible) { HideLibrary(); return; }
                ShowMedia(item);
                return;
            }
            if (_item == item && _doc != null) { HideLibrary(); return; }
            var doc = EditorDocument.Load(item.FullPath);
            doc.LibraryId = item.Id;
            OpenDocument(doc, item);
        }
        catch (Exception ex)
        {
            Log.Error("Abrir elemento", ex);
            MessageBox.Show(this, "No se pudo abrir la captura: " + ex.Message, "MaxSnapr", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void OpenFile(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".mp4" or ".gif" && (ext == ".mp4" || IsAnimatedGif(path)))
            {
                var existing = LibraryStore.Instance.Items.FirstOrDefault(i => string.Equals(i.FullPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                if (existing != null) OpenItem(existing); else OpenMediaFile(path);
                return;
            }
            if (Path.GetExtension(path).Equals(EditorDocument.Extension, StringComparison.OrdinalIgnoreCase))
            {
                var existing = LibraryStore.Instance.Items.FirstOrDefault(i => string.Equals(i.FullPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                if (existing != null) { OpenItem(existing); return; }
                var doc = EditorDocument.Load(path);
                doc.LibraryId = Guid.NewGuid();
                var item = LibraryStore.Instance.Add(doc, Path.GetFileNameWithoutExtension(path), "Archivo");
                OpenDocument(doc, item);
            }
            else if (ExportService.IsImageFile(path))
            {
                var item = LibraryStore.Instance.Import(path);
                OpenItem(item);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo abrir {Path.GetFileName(path)}: {ex.Message}", "MaxSnapr", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void SaveCurrent()
    {
        if (_doc == null || !_doc.IsDirty) return;
        Canvas.EndTextEdit();
        try
        {
            if (_item != null) LibraryStore.Instance.Update(_doc);
            else
            {
                _item = LibraryStore.Instance.Add(_doc, "Imagen", "");
                _trayView.Refresh();
            }
        }
        catch (Exception ex) { Log.Error("Autoguardado", ex); }
    }

    void ShowEmpty()
    {
        HideVideo();
        _doc = null;
        _item = null;
        Canvas.Document = null;
        CanvasScroll.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        CropBar.Visibility = Visibility.Collapsed;
        UpdateStatus();
    }

    public void ShowLibrary()
    {
        SaveCurrent();
        LibraryPane.Visibility = Visibility.Visible;
        LibraryToggle.IsChecked = true;
        PropsPanel.Visibility = Visibility.Collapsed;
        PropsColumn.Width = new GridLength(0);
        LibraryPane.Refresh(this);
    }

    public void HideLibrary()
    {
        LibraryPane.Visibility = Visibility.Collapsed;
        LibraryToggle.IsChecked = false;
        ApplyPanelVisibility();
    }

    void UpdateStatus()
    {
        if (_doc == null)
        {
            SizeText.Text = "";
            TitleText.Text = "MaxSnapr";
            Title = "MaxSnapr";
            StatusText.Text = "";
        }
        else
        {
            SizeText.Text = $"{_doc.Width} × {_doc.Height} px";
            var name = _item?.Name ?? "Imagen sin guardar";
            TitleText.Text = name + (_doc.IsDirty ? " •" : "");
            Title = $"{name} – MaxSnapr";
            StatusText.Text = Canvas.Selection.Count > 0 ? $"{Canvas.Selection.Count} objeto(s) seleccionado(s)" : $"{_doc.Annotations.Count} objeto(s)";
            if (_currentKind == ToolKind.Crop && _tools[ToolKind.Crop] is CropTool ct) StatusText.Text = "Arrastre los tiradores y pulse Intro para recortar";
        }
        UpdateZoomLabel();
    }

    void UpdateZoomLabel() => ZoomLabel.Content = $"{Math.Round(Canvas.Zoom * 100)} %";

    // ================================================================ keyboard

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Canvas.IsEditingText || e.OriginalSource is TextBox) return;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        Action? act = (ctrl, shift, key) switch
        {
            (true, false, Key.Z) => () => Undo_Click(this, e),
            (true, false, Key.Y) or (true, true, Key.Z) => () => Redo_Click(this, e),
            (true, true, Key.C) => () => CopyAll_Click(this, e),
            (true, false, Key.C) when Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) => () => Combine_Click(this, e),
            (true, false, Key.C) => () => Copy_Click(this, e),
            (true, false, Key.X) => () => Cut_Click(this, e),
            (true, false, Key.V) => () => Paste_Click(this, e),
            (true, false, Key.D) => () => Duplicate_Click(this, e),
            (true, false, Key.A) => () => SelectAll_Click(this, e),
            (true, false, Key.G) => () => Group_Click(this, e),
            (true, true, Key.G) => () => Ungroup_Click(this, e),
            (true, false, Key.S) => () => Save_Click(this, e),
            (true, true, Key.S) => () => SaveAs_Click(this, e),
            (true, false, Key.O) => () => Open_Click(this, e),
            (true, false, Key.N) => () => NewImage_Click(this, e),
            (true, true, Key.N) => () => NewCapture_Click(this, e),
            (true, false, Key.P) => () => Print_Click(this, e),
            (true, true, Key.P) => () => QuickPrint_Click(this, e),
            (true, false, Key.R) => () => Resize_Click(this, e),
            (true, false, Key.L) => () => Library_Click(this, e),
            (true, false, Key.OemPlus or Key.Add) => () => ZoomIn_Click(this, e),
            (true, false, Key.OemMinus or Key.Subtract) => () => ZoomOut_Click(this, e),
            (true, false, Key.D0 or Key.NumPad0) => () => Zoom100_Click(this, e),
            (true, false, Key.D9 or Key.NumPad9) => () => ZoomFit_Click(this, e),
            (true, false, Key.OemSemicolon) => () => FitCanvas_Click(this, e),
            (true, true, Key.Oem6) => () => BringFront_Click(this, e),
            (true, false, Key.Oem6) => () => BringForward_Click(this, e),
            (true, true, Key.Oem4) => () => SendBack_Click(this, e),
            (true, false, Key.Oem4) => () => SendBackward_Click(this, e),
            (false, false, Key.F1) => () => Shortcuts_Click(this, e),
            (true, false, Key.T) => CompactSelection,
            (true, true, Key.T) => () => Flatten_Click(this, e),
            (true, true, Key.D) => DuplicateImage,
            (false, false, Key.Enter) when Keyboard.Modifiers == ModifierKeys.Alt => ShowDetails,
            (false, false, Key.Left) when Keyboard.Modifiers == ModifierKeys.Alt => () => NavigateTray(-1),
            (false, false, Key.Right) when Keyboard.Modifiers == ModifierKeys.Alt => () => NavigateTray(1),
            _ => null,
        };
        if (act != null) { act(); e.Handled = true; return; }

        if (!ctrl && !shift && mods == ModifierKeys.None && LibraryPane.Visibility != Visibility.Visible)
        {
            if (key == Key.Escape && Canvas.Selection.Count > 0 && !CurrentTool.OnKey(key, mods)) { Canvas.Select(null); e.Handled = true; return; }
            if (key == Key.Escape && App.Settings.EscClosesEditor) { Close(); e.Handled = true; return; }
            if (Keyboard.FocusedElement is not (MaxSnapr.Editor.EditorCanvas or ListBoxItem or Window or RadioButton or Button)) return;
            var def = ToolDefs.FirstOrDefault(d => d.Shortcut == key);
            if (def != null && _doc != null && !(key is Key.Delete or Key.Back))
            {
                SelectTool(def.Kind);
                e.Handled = true;
            }
        }
    }

    void OnDropFiles(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files) OpenFile(f);
    }

    async void OpenMediaFile(string path)
    {
        try { OpenItem(await LibraryStore.Instance.AddMediaAsync(path, Path.GetFileNameWithoutExtension(path))); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Abrir vídeo", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ================================================================ menu: file

    void NewCapture_Click(object sender, RoutedEventArgs e) => _ = App.Current.Capture.RunAsync(App.Settings.Current);

    void NewImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NewImageDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var doc = new EditorDocument(RasterOps.Blank(dlg.PixelWidth, dlg.PixelHeight, dlg.FillColor)) { IsDirty = true };
        var item = LibraryStore.Instance.Add(doc, "Imagen nueva", "MaxSnapr");
        OpenDocument(doc, item);
    }

    void NewFromClipboard_Click(object sender, RoutedEventArgs e)
    {
        var img = ClipboardService.GetImage();
        if (img == null) { MessageBox.Show(this, "El portapapeles no contiene ninguna imagen.", "MaxSnapr"); return; }
        var doc = new EditorDocument(img);
        var item = LibraryStore.Instance.Add(doc, "Desde el portapapeles", "Portapapeles");
        OpenDocument(doc, item);
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Imágenes, vídeos y documentos|*.snapr;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.ico;*.mp4|Todos los archivos|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) OpenFile(f);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        _doc.IsDirty = true;
        SaveCurrent();
        UpdateStatus();
        StatusText.Text = "Guardado en la biblioteca";
    }

    void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        Canvas.EndTextEdit();
        var formats = new[] { "png", "jpg", "bmp", "gif", "tif", "pdf", "snapr", "ico" };
        var dlg = new SaveFileDialog
        {
            Filter = ExportService.SaveFilter,
            FilterIndex = Array.IndexOf(formats, App.Settings.DefaultExport) + 1,
            FileName = SafeName(_item?.Name ?? "Captura"),
            InitialDirectory = App.Settings.LastSaveFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (Path.GetExtension(dlg.FileName).Equals(".snapr", StringComparison.OrdinalIgnoreCase))
            {
                // Export a standalone copy; the library keeps its own file.
                var copy = EditorDocument.Load(_item!.FullPath);
                copy.Save(dlg.FileName);
            }
            else ExportService.Save(_doc.Flatten(), dlg.FileName, App.Settings.JpegQuality);
            App.Settings.LastSaveFolder = Path.GetDirectoryName(dlg.FileName);
            App.Settings.DefaultExport = formats[Math.Clamp(dlg.FilterIndex - 1, 0, formats.Length - 1)];
            StatusText.Text = "Exportado: " + dlg.FileName;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Guardar como", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    static bool IsAnimatedGif(string path)
    {
        try { using var fs = File.OpenRead(path); return new GifBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.None).Frames.Count > 1; }
        catch { return false; }
    }

    static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var dlg = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = SafeName(_item?.Name ?? "Captura") };
        if (dlg.ShowDialog(this) != true) return;
        ExportService.SavePdf(new[] { _doc.Flatten() }, dlg.FileName, App.Settings.JpegQuality);
        StatusText.Text = "PDF exportado";
    }

    void ExportTrayPdf_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        var items = _trayView.Cast<LibraryItem>().ToList();
        if (items.Count == 0) return;
        var dlg = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = "Capturas" };
        if (dlg.ShowDialog(this) != true) return;
        var pages = items.Where(i => !i.IsMedia).Select(i => EditorDocument.Load(i.FullPath).Flatten()).ToList();
        ExportService.SavePdf(pages, dlg.FileName, App.Settings.JpegQuality);
        StatusText.Text = $"PDF de {pages.Count} páginas exportado";
    }

    void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_doc != null) ExportService.Print(_doc.Flatten(), _item?.Name ?? "Captura");
    }

    void QuickPrint_Click(object sender, RoutedEventArgs e) { if (_doc != null) ExportService.Print(_doc.Flatten(), _item?.Name ?? "Captura", quick: true); }
    void PrintActual_Click(object sender, RoutedEventArgs e) { if (_doc != null) ExportService.Print(_doc.Flatten(), _item?.Name ?? "Captura", fitToPage: false); }
    void PrintLandscape_Click(object sender, RoutedEventArgs e) { if (_doc != null) ExportService.Print(_doc.Flatten(), _item?.Name ?? "Captura", landscape: true); }

    void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_item != null) Process.Start("explorer.exe", $"/select,\"{_item.FullPath}\"");
    }

    void Preferences_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();
    void CloseEditor_Click(object sender, RoutedEventArgs e) => Close();
    void Exit_Click(object sender, RoutedEventArgs e) => App.Current.Quit();

    // ================================================================ menu: edit

    void Undo_Click(object sender, RoutedEventArgs e) { Canvas.EndTextEdit(); _doc?.Undo(); Canvas.Selection.Clear(); RefreshProperties(); UpdateStatus(); }
    void Redo_Click(object sender, RoutedEventArgs e) { Canvas.EndTextEdit(); _doc?.Redo(); Canvas.Selection.Clear(); RefreshProperties(); UpdateStatus(); }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        if (CurrentTool is SelectionTool st && !st.SelectionRect.IsEmpty) { st.CopyPixels(); return; }
        if (Canvas.Selection.Count > 0)
        {
            _objectClipboard = Canvas.Selection.Select(a => a.Clone()).ToList();
            var bounds = Canvas.Selection.Select(a => a.Bounds).Aggregate(Rect.Union);
            var r = ToolBase.ScreenSnapshotRect(bounds, _doc.Width, _doc.Height);
            if (r.Width > 0 && r.Height > 0) ClipboardService.SetImage(RasterOps.Crop(_doc.Flatten(), r));
            _objectClipboardSequence = ClipboardSequence();
            StatusText.Text = "Objetos copiados";
            return;
        }
        CopyAll_Click(sender, e);
    }

    void Cut_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTool is SelectionTool st && !st.SelectionRect.IsEmpty) { st.CopyPixels(); st.Erase(); return; }
        Copy_Click(sender, e);
        Canvas.DeleteSelection();
    }

    void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        Canvas.EndTextEdit();
        ClipboardService.SetImage(_doc.Flatten());
        StatusText.Text = "Imagen copiada al portapapeles";
    }

    void Paste_Click(object sender, RoutedEventArgs e)
    {
        // Objects copied inside the editor paste as editable objects while the clipboard still holds that copy.
        if (_objectClipboard.Count > 0 && _doc != null && ClipboardSequence() == _objectClipboardSequence)
        {
            _doc.Checkpoint();
            var copies = _objectClipboard.Select(a => { var c = a.Duplicate(); c.Offset(new Vector(16, 16)); return c; }).ToList();
            _doc.Annotations.AddRange(copies);
            _objectClipboard = copies.Select(c => c.Clone()).ToList();
            _doc.RaiseChanged();
            Canvas.Selection.Clear();
            Canvas.Selection.AddRange(copies);
            Canvas.RaiseSelectionChanged();
            return;
        }
        var img = ClipboardService.GetImage();
        if (img == null)
        {
            if (Clipboard.ContainsText() && _doc != null)
            {
                _doc.Checkpoint();
                var t = new TextAnnotation { Text = Clipboard.GetText(), Style = _tools[ToolKind.Text].Style.Clone(), Box = new Rect(20, 20, 420, 40) };
                t.FitHeight(Canvas.PixelsPerDip);
                _doc.Add(t);
                Canvas.Select(t);
            }
            return;
        }
        if (_doc == null) { NewFromClipboard_Click(sender, e); return; }
        _doc.Checkpoint();
        var ann = new ImageAnnotation { Style = new ToolStyle { Shadow = false, Thickness = 0 } };
        ann.Image = RasterOps.Normalize(img);
        double w = Math.Min(img.PixelWidth, _doc.Width * 0.9), h = w * img.PixelHeight / Math.Max(1, img.PixelWidth);
        ann.Box = new Rect((_doc.Width - w) / 2, (_doc.Height - h) / 2, w, h);
        _doc.Add(ann);
        SelectTool(ToolKind.Select);
        Canvas.Select(ann);
    }

    uint _objectClipboardSequence = uint.MaxValue;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    static uint ClipboardSequence() => GetClipboardSequenceNumber();

    void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || Canvas.Selection.Count == 0) return;
        _doc.Checkpoint();
        var copies = Canvas.Selection.Select(a => { var c = a.Duplicate(); c.Offset(new Vector(16, 16)); return c; }).ToList();
        _doc.Annotations.AddRange(copies);
        _doc.RaiseChanged();
        Canvas.Selection.Clear();
        Canvas.Selection.AddRange(copies);
        Canvas.RaiseSelectionChanged();
    }

    void Delete_Click(object sender, RoutedEventArgs e) => Canvas.DeleteSelection();

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        if (_currentKind is not ToolKind.Select) SelectTool(ToolKind.Select);
        Canvas.SelectAll();
    }

    void Deselect_Click(object sender, RoutedEventArgs e) => Canvas.Select(null);
    void CompactSel_Click(object sender, RoutedEventArgs e) => CompactSelection();
    void DuplicateImage_Click(object sender, RoutedEventArgs e) => DuplicateImage();
    void Details_Click(object sender, RoutedEventArgs e) => ShowDetails();

    void Group_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || Canvas.Selection.Count < 2) return;
        _doc.Checkpoint();
        var id = Guid.NewGuid().ToString("N");
        foreach (var a in Canvas.Selection) a.GroupId = id;
        StatusText.Text = "Objetos agrupados";
    }

    void Ungroup_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        _doc.Checkpoint();
        foreach (var a in Canvas.Selection) a.GroupId = null;
        StatusText.Text = "Grupo deshecho";
    }

    void BringFront_Click(object sender, RoutedEventArgs e) { if (_doc == null || Canvas.Selection.Count == 0) return; _doc.Checkpoint(); _doc.BringToFront(Canvas.Selection); }
    void SendBack_Click(object sender, RoutedEventArgs e) { if (_doc == null || Canvas.Selection.Count == 0) return; _doc.Checkpoint(); _doc.SendToBack(Canvas.Selection); }
    void BringForward_Click(object sender, RoutedEventArgs e) { if (_doc == null || Canvas.Selection.Count != 1) return; _doc.Checkpoint(); _doc.BringForward(Canvas.Selection[0]); }
    void SendBackward_Click(object sender, RoutedEventArgs e) { if (_doc == null || Canvas.Selection.Count != 1) return; _doc.Checkpoint(); _doc.SendBackward(Canvas.Selection[0]); }

    void Align_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || Canvas.Selection.Count == 0) return;
        var mode = (string)((FrameworkElement)sender).Tag;
        // One object aligns to the canvas; several align to their combined bounds.
        var target = Canvas.Selection.Count == 1 ? new Rect(0, 0, _doc.Width, _doc.Height) : Canvas.Selection.Select(a => a.Bounds).Aggregate(Rect.Union);
        _doc.Checkpoint();
        foreach (var a in Canvas.Selection)
        {
            var b = a.Bounds;
            var v = mode switch
            {
                "L" => new Vector(target.Left - b.Left, 0),
                "C" => new Vector(target.Left + target.Width / 2 - (b.Left + b.Width / 2), 0),
                "R" => new Vector(target.Right - b.Right, 0),
                "T" => new Vector(0, target.Top - b.Top),
                "M" => new Vector(0, target.Top + target.Height / 2 - (b.Top + b.Height / 2)),
                _ => new Vector(0, target.Bottom - b.Bottom),
            };
            a.Offset(v);
        }
        _doc.RaiseChanged();
    }

    void FlipObjects_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || Canvas.Selection.Count == 0) return;
        bool h = (string)((FrameworkElement)sender).Tag == "H";
        _doc.Checkpoint();
        foreach (var a in Canvas.Selection)
        {
            var b = a.Bounds;
            switch (a)
            {
                case LineAnnotation l:
                    Point F(Point p) => h ? new Point(b.Left + b.Right - p.X, p.Y) : new Point(p.X, b.Top + b.Bottom - p.Y);
                    l.Start = F(l.Start); l.End = F(l.End); if (l.Control is Point c) l.Control = F(c);
                    break;
                case PenAnnotation p:
                    p.Points = p.Points.Select(pt => h ? new Point(b.Left + b.Right - pt.X, pt.Y) : new Point(pt.X, b.Top + b.Bottom - pt.Y)).ToList();
                    break;
                case ImageAnnotation img when img.Image != null:
                    img.Image = RasterOps.Flip(img.Image, h);
                    break;
                case TextAnnotation t when t.Tail is Point tail:
                    t.Tail = h ? new Point(b.Left + b.Right - tail.X, tail.Y) : new Point(tail.X, b.Top + b.Bottom - tail.Y);
                    break;
            }
        }
        _doc.RaiseChanged();
    }

    // ================================================================ menu: image

    void CropTool_Click(object sender, RoutedEventArgs e) => SelectTool(ToolKind.Crop);

    void CropToSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_tools[ToolKind.Selection] is SelectionTool st && !st.SelectionRect.IsEmpty) { st.CropToSelection(); return; }
        if (_doc == null || Canvas.Selection.Count == 0) return;
        var r = ToolBase.ScreenSnapshotRect(Canvas.Selection.Select(a => a.Bounds).Aggregate(Rect.Union), _doc.Width, _doc.Height);
        if (r.Width < 1) return;
        _doc.Checkpoint();
        _doc.ReplaceBase(RasterOps.Crop(_doc.Base, r), new Vector(-r.X, -r.Y));
    }

    void AutoTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        // Trim uniform borders of the same colour as the top-left pixel.
        var p = RasterOps.Read(_doc.Base);
        int bg = BitConverter.ToInt32(p.Data, 0);
        bool RowSame(int y) { for (int x = 0; x < p.W; x++) if (BitConverter.ToInt32(p.Data, p.Index(x, y)) != bg) return false; return true; }
        bool ColSame(int x, int y0, int y1) { for (int y = y0; y <= y1; y++) if (BitConverter.ToInt32(p.Data, p.Index(x, y)) != bg) return false; return true; }
        int top = 0, bottom = p.H - 1, left = 0, right = p.W - 1;
        while (top < bottom && RowSame(top)) top++;
        while (bottom > top && RowSame(bottom)) bottom--;
        while (left < right && ColSame(left, top, bottom)) left++;
        while (right > left && ColSame(right, top, bottom)) right--;
        var r = new Int32Rect(left, top, right - left + 1, bottom - top + 1);
        if (r.Width == p.W && r.Height == p.H) { StatusText.Text = "No hay bordes uniformes que recortar"; return; }
        _doc.Checkpoint();
        _doc.ReplaceBase(RasterOps.Crop(_doc.Base, r), new Vector(-r.X, -r.Y));
    }

    void FitCanvas_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _doc.Annotations.Count == 0) return;
        var all = _doc.Annotations.Select(a => a.Bounds).Aggregate(Rect.Union);
        all.Union(new Rect(0, 0, _doc.Width, _doc.Height));
        int left = (int)Math.Ceiling(-Math.Min(0, all.Left)), top = (int)Math.Ceiling(-Math.Min(0, all.Top));
        int right = (int)Math.Ceiling(Math.Max(0, all.Right - _doc.Width)), bottom = (int)Math.Ceiling(Math.Max(0, all.Bottom - _doc.Height));
        if (left + top + right + bottom == 0) return;
        _doc.Checkpoint();
        _doc.ReplaceBase(RasterOps.Expand(_doc.Base, left, top, right, bottom, _doc.CanvasColor), new Vector(left, top));
    }

    void Resize_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var dlg = new ResizeDialog(_doc.Width, _doc.Height) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        double sx = dlg.NewWidth / (double)_doc.Width, sy = dlg.NewHeight / (double)_doc.Height;
        _doc.Checkpoint();
        _doc.ReplaceBase(RasterOps.Resize(_doc.Base, dlg.NewWidth, dlg.NewHeight, dlg.Smooth));
        if (dlg.ScaleObjects) ScaleAnnotations(sx, sy);
        _doc.RaiseChanged();
        ZoomFit_Click(sender, e);
    }

    void ScaleAnnotations(double sx, double sy)
    {
        Point S(Point p) => new(p.X * sx, p.Y * sy);
        Rect R(Rect r) => new(S(r.TopLeft), S(r.BottomRight));
        foreach (var a in _doc!.Annotations)
            switch (a)
            {
                case BoxAnnotation b: b.Box = R(b.Box); if (b is TextAnnotation t && t.Tail is Point tl) t.Tail = S(tl); break;
                case LineAnnotation l: l.Start = S(l.Start); l.End = S(l.End); if (l.Control is Point c) l.Control = S(c); break;
                case PenAnnotation p: p.Points = p.Points.Select(S).ToList(); break;
                case StepAnnotation st: st.Center = S(st.Center); break;
            }
    }

    void CanvasSize_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var dlg = new CanvasSizeDialog(_doc.Width, _doc.Height, _doc.CanvasColor) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _doc.Checkpoint();
        var (l, t, r, b) = dlg.Margins;
        if (l < 0 || t < 0 || r < 0 || b < 0)
        {
            // Shrinking: crop first, then expand the remaining sides.
            var crop = new Int32Rect(Math.Max(0, -l), Math.Max(0, -t), Math.Max(1, _doc.Width + Math.Min(0, l) + Math.Min(0, r)), Math.Max(1, _doc.Height + Math.Min(0, t) + Math.Min(0, b)));
            _doc.ReplaceBase(RasterOps.Crop(_doc.Base, crop), new Vector(-crop.X, -crop.Y));
        }
        _doc.ReplaceBase(RasterOps.Expand(_doc.Base, Math.Max(0, l), Math.Max(0, t), Math.Max(0, r), Math.Max(0, b), dlg.Fill), new Vector(Math.Max(0, l), Math.Max(0, t)));
        ZoomFit_Click(sender, e);
    }

    void CanvasColor_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var c = ColorPicker.PickWithDialog(_doc.CanvasColor);
        if (c == null) return;
        _doc.Checkpoint();
        _doc.CanvasColor = c.Value;
        _doc.RaiseChanged();
    }

    void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        int deg = int.Parse((string)((FrameworkElement)sender).Tag);
        RotateDocument(deg);
    }

    public void RotateDocument(int deg)
    {
        if (_doc == null) return;
        _doc.Checkpoint();
        // Objects keep their editability by being burned only when they would be misplaced.
        if (_doc.Annotations.Count > 0) { var flat = _doc.Flatten(); _doc.Annotations.Clear(); _doc.ReplaceBase(RasterOps.Rotate(flat, deg)); }
        else _doc.ReplaceBase(RasterOps.Rotate(_doc.Base, deg));
        ZoomFit_Click(this, new RoutedEventArgs());
    }

    void RotateCustom_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var s = InputDialog.Ask(this, "Girar", "Ángulo en grados (sentido horario):", "15");
        if (s == null || !double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var deg)) return;
        _doc.Checkpoint();
        var flat = _doc.Flatten();
        _doc.Annotations.Clear();
        _doc.ReplaceBase(RasterOps.RotateArbitrary(flat, deg, _doc.CanvasColor));
        ZoomFit_Click(sender, e);
    }

    void Flip_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        bool h = (string)((FrameworkElement)sender).Tag == "H";
        _doc.Checkpoint();
        var flat = _doc.Annotations.Count > 0 ? _doc.Flatten() : _doc.Base;
        _doc.Annotations.Clear();
        _doc.ReplaceBase(RasterOps.Flip(flat, h));
    }

    void Flatten_Click(object sender, RoutedEventArgs e) => _doc?.FlattenInPlace();

    async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        try
        {
            StatusText.Text = "Reconociendo texto…";
            var text = await OcrService.RecognizeAsync(_doc.Flatten());
            ClipboardService.SetText(text);
            new TextResultWindow(text, null) { Owner = this }.Show();
            StatusText.Text = "Texto copiado al portapapeles";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Extraer texto", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ================================================================ canvas context menu

    void ShowCanvasMenu()
    {
        if (_doc == null) return;
        var menu = new ContextMenu();
        MenuItem Item(string header, string gesture, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
            return mi;
        }
        var re = new RoutedEventArgs();
        bool sel = Canvas.Selection.Count > 0;
        if (sel && Canvas.Selection.Count == 1)
        {
            var kind = KindOf(Canvas.Selection[0]);
            Item("Añadir a estilos rápidos", "", () =>
            {
                if (!App.Settings.UserQuickStyles.TryGetValue(kind, out var list)) App.Settings.UserQuickStyles[kind] = list = new();
                list.Add(new NamedStyle { Name = "Estilo", Style = Canvas.Selection[0].Style.Clone() });
                App.Settings.Save();
                RefreshProperties();
            });
            Item("Añadir a favoritos", "", () => { App.Settings.Favorites.Add(new FavoriteStyle { Kind = kind, Style = Canvas.Selection[0].Style.Clone() }); App.Settings.Save(); });
            if (Canvas.Selection[0] is TextAnnotation ta) Item("Editar el texto", "Doble clic", () => Canvas.BeginTextEdit(ta));
            if (Canvas.Selection[0] is StepAnnotation) Item("Reiniciar la secuencia", "", ResetStepSequence);
            menu.Items.Add(new Separator());
        }
        Item("Cortar", "Ctrl+X", () => Cut_Click(this, re), sel);
        Item("Copiar", "Ctrl+C", () => Copy_Click(this, re));
        Item("Pegar", "Ctrl+V", () => Paste_Click(this, re));
        Item("Duplicar", "Ctrl+D", () => Duplicate_Click(this, re), sel);
        Item("Eliminar", "Supr", DeleteSelected, sel);
        menu.Items.Add(new Separator());
        Item("Seleccionar todos los objetos", "Ctrl+A", () => SelectAll_Click(this, re));
        if (sel)
        {
            var kind = KindOf(Canvas.Selection[0]);
            int count = _doc.Annotations.Count(a => KindOf(a) == kind);
            Item($"Seleccionar todo de este tipo ({count})", "", () => SelectAllOfKind(kind));
        }
        Item("Anular la selección", "Esc", () => Canvas.Select(null), sel);
        if (sel)
        {
            menu.Items.Add(new Separator());
            var order = new MenuItem { Header = "Organizar" };
            void Sub(MenuItem parent, string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); parent.Items.Add(mi); }
            Sub(order, "Traer al frente", () => BringFront_Click(this, re));
            Sub(order, "Traer adelante", () => BringForward_Click(this, re));
            Sub(order, "Enviar atrás", () => SendBackward_Click(this, re));
            Sub(order, "Enviar al fondo", () => SendBack_Click(this, re));
            menu.Items.Add(order);
            var align = new MenuItem { Header = "Alinear" };
            foreach (var (h, tag) in new[] { ("Izquierda", "L"), ("Centro", "C"), ("Derecha", "R"), ("Superior", "T"), ("Medio", "M"), ("Inferior", "B") })
                Sub(align, h, () => Align_Click(new FrameworkElement { Tag = tag }, re));
            menu.Items.Add(align);
            var flip = new MenuItem { Header = "Voltear" };
            Sub(flip, "Horizontalmente", () => FlipObjects_Click(new FrameworkElement { Tag = "H" }, re));
            Sub(flip, "Verticalmente", () => FlipObjects_Click(new FrameworkElement { Tag = "V" }, re));
            menu.Items.Add(flip);
            Item("Agrupar", "Ctrl+G", () => Group_Click(this, re), Canvas.Selection.Count > 1);
            Item("Desagrupar", "Ctrl+Mayús+G", () => Ungroup_Click(this, re));
            Item("Compactar en la imagen", "Ctrl+T", CompactSelection);
        }
        menu.Items.Add(new Separator());
        Item("Compactar todo", "Ctrl+Mayús+T", () => Flatten_Click(this, re), _doc.Annotations.Count > 0);
        Item("Ajustar el lienzo a los objetos", "Ctrl+;", () => FitCanvas_Click(this, re));
        Item("Color del lienzo…", "", () => CanvasColor_Click(this, re));
        Item("Extraer texto", "", () => Ocr_Click(this, re));
        Item("Detalles…", "Alt+Intro", ShowDetails);
        menu.IsOpen = true;
    }

    public void SelectAllOfKind(ToolKind kind)
    {
        if (_doc == null) return;
        Canvas.Selection.Clear();
        Canvas.Selection.AddRange(_doc.Annotations.Where(a => KindOf(a) == kind));
        Canvas.RaiseSelectionChanged();
    }

    /// <summary>Burns only the selected objects into the image.</summary>
    public void CompactSelection()
    {
        if (_doc == null || Canvas.Selection.Count == 0) return;
        _doc.Checkpoint();
        var keep = _doc.Annotations.Except(Canvas.Selection).ToList();
        var chosen = Canvas.Selection.ToList();
        var ctx = _doc.CreateContext();
        var flat = RasterOps.Render(_doc.Width, _doc.Height, dc =>
        {
            dc.DrawImage(_doc.Base, new Rect(0, 0, _doc.Width, _doc.Height));
            foreach (var a in _doc.Annotations.Where(chosen.Contains)) a.Render(dc, ctx);
        });
        _doc.Annotations.Clear();
        _doc.Annotations.AddRange(keep);
        Canvas.Selection.Clear();
        _doc.ReplaceBase(flat);
        Canvas.RaiseSelectionChanged();
    }

    public void ShowDetails()
    {
        if (_item == null) return;
        new DetailsDialog(_item, _doc) { Owner = this }.ShowDialog();
        UpdateStatus();
    }

    void DuplicateImage()
    {
        if (_item == null || _item.IsMedia) return;
        SaveCurrent();
        var copy = EditorDocument.Load(_item.FullPath);
        copy.LibraryId = Guid.NewGuid();
        var item = LibraryStore.Instance.Add(copy, _item.Name + " (copia)", _item.SourceApp);
        item.Tags = _item.Tags.ToList();
        OpenDocument(copy, item);
    }

    void NavigateTray(int delta)
    {
        var list = _trayView.Cast<LibraryItem>().ToList();
        if (list.Count == 0) return;
        int i = _item == null ? 0 : list.IndexOf(_item);
        int n = Math.Clamp(i + delta, 0, list.Count - 1);
        if (n != i) OpenItem(list[n]);
    }

    // ================================================================ create / smart

    IEnumerable<LibraryItem> TrayImages() => _trayView.Cast<LibraryItem>().Where(i => !i.IsMedia);

    void Combine_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        var initial = _item != null && !_item.IsMedia ? new[] { _item }.Concat(TrayImages().Where(i => i != _item).Take(1)) : TrayImages().Take(2);
        new CombineWindow(initial) { Owner = this }.ShowDialog();
    }

    void SaveAsTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var name = InputDialog.Ask(this, "Guardar como plantilla", "Nombre de la plantilla:", _item?.Name ?? "Mi plantilla");
        if (string.IsNullOrWhiteSpace(name)) return;
        var t = TemplateFiles.FromDocument(_doc, name.Trim());
        if (t == null) { MessageBox.Show(this, "La plantilla se crea a partir de las imágenes insertadas en el documento (por ejemplo, pegadas o combinadas). Este documento no tiene ninguna.", "Guardar como plantilla"); return; }
        App.Settings.UserTemplates.Add(t);
        App.Settings.Save();
        StatusText.Text = $"Plantilla «{t.Name}» guardada con {t.Slots.Count} hueco(s)";
    }

    void ImagesToVideo_Click(object sender, RoutedEventArgs e) { SaveCurrent(); new ImagesToVideoWindow(TrayImages()) { Owner = this }.ShowDialog(); }
    void Batch_Click(object sender, RoutedEventArgs e) { SaveCurrent(); new BatchExportWindow(TrayImages()) { Owner = this }.ShowDialog(); }
    void Themes_Click(object sender, RoutedEventArgs e) { new ThemeEditorDialog { Owner = this }.ShowDialog(); RefreshProperties(); }
    void Welcome_Click(object sender, RoutedEventArgs e) => new WelcomeWindow { Owner = this }.ShowDialog();

    async void SmartRedact_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var dlg = new SmartRedactDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            StatusText.Text = "Buscando datos sensibles…";
            int n = await Editor.Smart.SmartFeatures.SmartRedactAsync(_doc, dlg.Categories, dlg.Kind, dlg.CustomWords);
            StatusText.Text = n == 0 ? "No se encontraron datos que ocultar" : $"{n} zona(s) ocultadas; puede moverlas o borrarlas como cualquier objeto";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ocultación inteligente", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public async void EditTextInSelection()
    {
        if (_doc == null) return;
        var region = _tools[ToolKind.Selection] is SelectionTool st && !st.SelectionRect.IsEmpty ? st.SelectionRect : new Rect(0, 0, _doc.Width, _doc.Height);
        try
        {
            StatusText.Text = "Reconociendo el texto…";
            int n = await Editor.Smart.SmartFeatures.EditTextAsync(_doc, region, Canvas.PixelsPerDip);
            _tools[ToolKind.Selection].Reset();
            SelectTool(ToolKind.Select);
            StatusText.Text = n == 0 ? "No se encontró texto" : $"{n} línea(s) convertidas en texto editable (doble clic para cambiarlas)";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Editar texto", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void EditText_Click(object sender, RoutedEventArgs e) => EditTextInSelection();

    async void Simplify_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var choice = MessageBox.Show(this, "¿Sustituir el texto por barras simplificadas?\n\nSí: borra el texto original y deja solo las barras.\nNo: deja el texto y añade las barras encima.",
            "Simplificar", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        try
        {
            StatusText.Text = "Simplificando…";
            int n = await Editor.Smart.SmartFeatures.SimplifyAsync(_doc, choice == MessageBoxResult.Yes, null);
            StatusText.Text = n == 0 ? "No se encontró texto que simplificar" : $"{n} línea(s) simplificadas";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Simplificar", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ================================================================ share

    /// <summary>What the Share menu sends: the flattened image, or the media file for videos and GIFs.</summary>
    public ShareItem? CurrentShareItem()
    {
        if (_item?.IsMedia == true) return new ShareItem { FilePath = _item.FullPath, Name = _item.Name, App = _item.SourceApp };
        if (_doc == null) return null;
        Canvas.EndTextEdit();
        return new ShareItem { Image = _doc.Flatten(), Name = _item?.Name ?? "Captura", App = _item?.SourceApp ?? "" };
    }

    void Share_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ShareButton, Placement = PlacementMode.Bottom };
        MenuItem Item(string header, string glyph, Action a)
        {
            var mi = new MenuItem { Header = header, Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15 } };
            mi.Click += (_, _) => a();
            return mi;
        }
        bool media = _item?.IsMedia == true;
        foreach (var d in Outputs.All.Concat(Outputs.Programs()).Where(d => d.Available && (!media || d.SupportsMedia)))
        {
            var dest = d;
            menu.Items.Add(Item(d.Name, d.Glyph, () => { if (CurrentShareItem() is { } si) _ = Outputs.ShareAsync(dest, si, this); }));
        }
        menu.Items.Add(new Separator());
        if (!media)
        {
            menu.Items.Add(Item("Guardar como archivo…", "\uE74E", () => SaveAs_Click(this, new RoutedEventArgs())));
            menu.Items.Add(Item("Exportar como PDF…", "\uEA90", () => ExportPdf_Click(this, new RoutedEventArgs())));
            menu.Items.Add(Item("Imprimir…", "\uE749", () => Print_Click(this, new RoutedEventArgs())));
        }
        menu.Items.Add(Item("Abrir con otro programa…", "\uE8E5", () => OpenWith_Click(this, new RoutedEventArgs())));
        menu.Items.Add(Item("Mostrar en la carpeta", "\uE8B7", () => ShowInFolder_Click(this, new RoutedEventArgs())));
        var history = new MenuItem { Header = "Historial" };
        foreach (var h in App.Settings.Output.History.Take(15))
        {
            var rec = h;
            var mi = new MenuItem { Header = $"{h.When:g} · {h.Destination} · {h.Item}", ToolTip = h.Target };
            mi.Click += (_, _) =>
            {
                if (Uri.TryCreate(rec.Target, UriKind.Absolute, out var link) && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp))
                    Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
                else if (File.Exists(rec.Target)) Process.Start("explorer.exe", $"/select,\"{rec.Target}\"");
            };
            history.Items.Add(mi);
        }
        if (history.Items.Count == 0) history.Items.Add(new MenuItem { Header = "(vacío)", IsEnabled = false });
        menu.Items.Add(history);
        menu.Items.Add(Item("Configurar destinos…", "\uE713", () => App.Current.ShowSettings("Compartir")));
        menu.IsOpen = true;
    }

    string ExportTemp(string ext = ".png")
    {
        var dir = Path.Combine(Path.GetTempPath(), "MaxSnapr");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SafeName(_item?.Name ?? "Captura") + ext);
        ExportService.Save(_doc!.Flatten(), path, App.Settings.JpegQuality);
        return path;
    }

    void Email_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentShareItem() is { } si) _ = Outputs.ShareAsync(Outputs.All.First(d => d.Name == "Correo electrónico"), si, this);
    }

    void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentShareItem() is not { } si) return;
        var path = si.EnsureFile();
        Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{path}\"") { UseShellExecute = false });
    }

    // ================================================================ view

    void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(Canvas.Zoom * 1.25);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(Canvas.Zoom / 1.25);
    void Zoom100_Click(object sender, RoutedEventArgs e) => SetZoom(1);

    void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        Canvas.FitToWindow(new Size(CanvasScroll.ActualWidth, CanvasScroll.ActualHeight));
        SyncZoomSlider();
    }

    bool _syncingSlider;
    void SetZoom(double z) { Canvas.Zoom = z; SyncZoomSlider(); }
    void SyncZoomSlider() { _syncingSlider = true; ZoomSlider.Value = Math.Log2(Canvas.Zoom); _syncingSlider = false; UpdateZoomLabel(); }

    void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider || Canvas == null) return;
        Canvas.Zoom = Math.Pow(2, e.NewValue);
    }

    void ToggleTray_Click(object sender, RoutedEventArgs e) { App.Settings.ShowTray = ShowTrayItem.IsChecked; ApplyPanelVisibility(); }
    void ToggleProps_Click(object sender, RoutedEventArgs e) { App.Settings.ShowPropertiesPanel = ShowPropsItem.IsChecked; ApplyPanelVisibility(); }

    void ApplyPanelVisibility()
    {
        TrayPanel.Visibility = App.Settings.ShowTray ? Visibility.Visible : Visibility.Collapsed;
        PropsPanel.Visibility = App.Settings.ShowPropertiesPanel ? Visibility.Visible : Visibility.Collapsed;
        PropsColumn.Width = App.Settings.ShowPropertiesPanel ? new GridLength(300) : new GridLength(0);
    }

    void Library_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryPane.Visibility == Visibility.Visible) HideLibrary(); else ShowLibrary();
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Theme = (string)((FrameworkElement)sender).Tag;
        ThemeService.Apply(App.Settings.Theme);
        App.Settings.Save();
    }

    void Shortcuts_Click(object sender, RoutedEventArgs e) => new ShortcutsWindow { Owner = this }.ShowDialog();
    void OpenLog_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", AppSettings.Folder);

    void Support_Click(object sender, RoutedEventArgs e) => Support.Show(this);

    void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        $"MaxSnapr {typeof(App).Assembly.GetName().Version?.ToString(2)}\nCaptura, anotación y grabación de pantalla para Windows.\nEn honor a Max.\n\nAplicación original: no contiene código, iconos ni recursos de terceros."
        + (Support.Available ? "\n\nSi te resulta útil, puedes apoyarla en Ayuda > Apoyar MaxSnapr." : ""),
        "Acerca de MaxSnapr", MessageBoxButton.OK, MessageBoxImage.Information);

    // ================================================================ crop bar

    void CropAspect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_tools.Count == 0 || _doc == null || _tools[ToolKind.Crop] is not CropTool ct) return;
        var tag = (CropAspect.SelectedItem as ComboBoxItem)?.Tag as string ?? "0";
        ct.SetAspect(double.Parse(tag, System.Globalization.CultureInfo.InvariantCulture));
    }

    void ApplyCrop_Click(object sender, RoutedEventArgs e) { (_tools[ToolKind.Crop] as CropTool)?.Apply(); ZoomFit_Click(sender, e); }
    void ResetCrop_Click(object sender, RoutedEventArgs e) => _tools[ToolKind.Crop].Reset();

    // ================================================================ tray

    void Tray_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTraySelection || Tray.SelectedItem is not LibraryItem item) return;
        OpenItem(item);
    }

    LibraryItem? TrayTarget => Tray.SelectedItem as LibraryItem;

    void TrayOpen_Click(object sender, RoutedEventArgs e) { if (TrayTarget is { } i) OpenItem(i); }

    void TrayCopy_Click(object sender, RoutedEventArgs e)
    {
        if (TrayTarget is not { } i) return;
        SaveCurrent();
        if (i.IsMedia) { var files = new System.Collections.Specialized.StringCollection { i.FullPath }; Clipboard.SetFileDropList(files); return; }
        ClipboardService.SetImage(EditorDocument.Load(i.FullPath).Flatten());
    }

    void TrayRename_Click(object sender, RoutedEventArgs e)
    {
        if (TrayTarget is not { } i) return;
        var name = InputDialog.Ask(this, "Cambiar el nombre", "Nombre:", i.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        i.Name = name.Trim();
        LibraryStore.Instance.Save();
        UpdateStatus();
    }

    void TrayPin_Click(object sender, RoutedEventArgs e)
    {
        if (TrayTarget is not { } i) return;
        i.Pinned = !i.Pinned;
        LibraryStore.Instance.Save();
    }

    void TrayShowInFolder_Click(object sender, RoutedEventArgs e) { if (TrayTarget is { } i) Process.Start("explorer.exe", $"/select,\"{i.FullPath}\""); }

    void TrayRemove_Click(object sender, RoutedEventArgs e)
    {
        if (TrayTarget is not { } i) return;
        RemoveFromTray(i);
    }

    void RemoveFromTray(LibraryItem i)
    {
        if (i == _item) { SaveCurrent(); }
        i.InTray = false;
        _trayView.Refresh();
        LibraryStore.Instance.Save();
        if (i == _item) OpenNextOrEmpty();
    }

    void OpenNextOrEmpty()
    {
        var next = _trayView.Cast<LibraryItem>().FirstOrDefault();
        if (next != null) { _item = null; _doc = null; OpenItem(next); } else ShowEmpty();
    }

    void TrayClear_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        foreach (var i in LibraryStore.Instance.Items.Where(i => i.InTray && !i.Pinned && i != _item)) i.InTray = false;
        _trayView.Refresh();
        LibraryStore.Instance.Save();
    }

    void TrayDelete_Click(object sender, RoutedEventArgs e)
    {
        if (TrayTarget is not { } i) return;
        if (MessageBox.Show(this, $"¿Enviar «{i.Name}» a la papelera?", "Eliminar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        bool current = i == _item;
        if (current) { HideVideo(); _doc = null; _item = null; }
        LibraryStore.Instance.Delete(i);
        if (current) OpenNextOrEmpty();
    }

    void Tray_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && TrayTarget is { } i) { RemoveFromTray(i); e.Handled = true; }
        if (e.Key == Key.F2) TrayRename_Click(sender, e);
    }

    void Tray_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _trayDragStart = e.GetPosition(null);

    void Tray_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Drag a tray thumbnail into another app (mail, chat, Explorer) as a PNG file.
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _trayDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>()?.DataContext is not LibraryItem item) return;
        SaveCurrent();
        var dir = Path.Combine(Path.GetTempPath(), "MaxSnapr");
        Directory.CreateDirectory(dir);
        string path;
        if (item.IsMedia) path = item.FullPath;
        else
        {
            path = Path.Combine(dir, SafeName(item.Name) + ".png");
            ExportService.Save(EditorDocument.Load(item.FullPath).Flatten(), path, App.Settings.JpegQuality);
        }
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        DragDrop.DoDragDrop(Tray, data, DragDropEffects.Copy);
    }

    void Tray_Drop(object sender, DragEventArgs e)
    {
        OnDropFiles(sender, e);
        e.Handled = true;
    }

    // ================================================================ object actions used by the panel

    public void DeleteSelected() => Canvas.DeleteSelection();
    public void DuplicateSelected() => Duplicate_Click(this, new RoutedEventArgs());
    public void BringSelectedToFront() => BringFront_Click(this, new RoutedEventArgs());
    public void SendSelectedToBack() => SendBack_Click(this, new RoutedEventArgs());

    public void ResetStepSequence()
    {
        if (_doc == null) return;
        _doc.Checkpoint();
        int n = 1;
        foreach (var s in _doc.Annotations.OfType<StepAnnotation>()) s.Number = n++;
        _doc.RaiseChanged();
    }

    public void SetStepNumber(int number)
    {
        if (_doc == null || Canvas.Selection.FirstOrDefault() is not StepAnnotation s) return;
        _doc.Checkpoint();
        s.Number = Math.Max(1, number);
        _doc.RaiseChanged();
    }

    public void ApplyEffect(Func<BitmapSource, BitmapSource> effect, bool includeObjects, Vector shift = default)
    {
        if (_doc == null) return;
        Canvas.EndTextEdit();
        _doc.Checkpoint();
        if (includeObjects && _doc.Annotations.Count > 0)
        {
            var flat = _doc.Flatten();
            _doc.Annotations.Clear();
            _doc.ReplaceBase(effect(flat));
        }
        else _doc.ReplaceBase(effect(_doc.Base), shift);
        ZoomFit_Click(this, new RoutedEventArgs());
    }
}

public static class VisualTreeExtensions
{
    public static T? FindAncestor<T>(this DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
