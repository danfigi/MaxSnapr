using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaxSnapr.Editor.Model;
using MaxSnapr.Library;
using MaxSnapr.Services;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace MaxSnapr.Views;

public partial class LibraryView : UserControl, INotifyPropertyChanged
{
    sealed record Filter(string Label, Func<LibraryItem, bool> Match, bool Header = false)
    {
        public override string ToString() => Label;
    }

    EditorWindow? _editor;
    double _thumbWidth = 170;

    public double ThumbWidth { get => _thumbWidth; set { _thumbWidth = value; OnChanged(); OnChanged(nameof(ThumbHeight)); } }
    public double ThumbHeight => _thumbWidth * 0.66;

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    public LibraryView()
    {
        InitializeComponent();
        DataContext = this;
    }

    public void Refresh(EditorWindow editor)
    {
        _editor = editor;
        var store = LibraryStore.Instance;
        var selected = (Filters.SelectedItem as Filter)?.Label;
        var list = new List<object>
        {
            new Filter("Todas las capturas", _ => true),
            new Filter("Hoy", i => i.Created.Date == DateTime.Today),
            new Filter("Últimos 7 días", i => i.Created >= DateTime.Today.AddDays(-7)),
            new Filter("Este mes", i => i.Created.Year == DateTime.Today.Year && i.Created.Month == DateTime.Today.Month),
            new Filter("Imágenes", i => !i.IsMedia),
            new Filter("Vídeos", i => i.IsVideo),
            new Filter("GIF animados", i => i.IsGif),
            new Filter("En la bandeja", i => i.InTray),
            new Filter("Fijadas", i => i.Pinned),
        };
        var apps = store.AllApps.ToList();
        if (apps.Count > 0)
        {
            list.Add(Header("APLICACIONES"));
            list.AddRange(apps.Select(a => new Filter(a, i => i.SourceApp == a)));
        }
        var sites = store.AllSites.ToList();
        if (sites.Count > 0)
        {
            list.Add(Header("SITIOS WEB"));
            list.AddRange(sites.Select(s => new Filter(s, i => string.Equals(i.Site, s, StringComparison.OrdinalIgnoreCase))));
        }
        var tags = store.AllTags.ToList();
        if (tags.Count > 0)
        {
            list.Add(Header("ETIQUETAS"));
            list.AddRange(tags.Select(t => new Filter("# " + t, i => i.Tags.Contains(t, StringComparer.CurrentCultureIgnoreCase))));
        }
        Filters.ItemsSource = list;
        Filters.SelectedItem = list.OfType<Filter>().FirstOrDefault(f => f.Label == selected) ?? list[0];
        Apply();
        Search.Focus();
    }

    static TextBlock Header(string text) => new() { Text = text, FontSize = 10.5, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Fg.Secondary"), Margin = new Thickness(0, 12, 0, 2), IsHitTestVisible = false };

    void Apply()
    {
        var filter = Filters.SelectedItem as Filter;
        var q = Search.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        IEnumerable<LibraryItem> items = LibraryStore.Instance.Items.Where(i => filter == null || filter.Match(i));
        if (q.Length > 0)
            items = items.Where(i => i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.SourceApp.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || i.SourceTitle.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Tags.Any(t => t.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                || i.SourceUrl.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.OcrText.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        items = Sort.SelectedIndex switch
        {
            1 => items.OrderBy(i => i.Created),
            2 => items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            3 => items.OrderByDescending(i => (long)i.Width * i.Height),
            _ => items.OrderByDescending(i => i.Created),
        };
        var result = items.ToList();
        var keep = Current;
        ItemsGrid.ItemsSource = result;
        ItemsGrid.SelectedItem = keep != null && result.Contains(keep) ? keep : result.FirstOrDefault();
        CountText.Text = result.Count == 1 ? "1 captura" : $"{result.Count} capturas";
        ShowDetails();
    }

    LibraryItem? Current => ItemsGrid.SelectedItem as LibraryItem;

    void ShowDetails()
    {
        var i = Current;
        Details.IsEnabled = i != null;
        DetailImage.Source = i?.Thumbnail;
        DetailName.Text = i?.Name ?? "";
        DetailTags.Text = i?.TagsText ?? "";
        DetailInfo.Text = i == null ? "Seleccione una captura para ver sus detalles." :
            $"{i.Width} × {i.Height} px\nCreada: {i.Created:f}\nModificada: {i.Modified:f}" +
            (string.IsNullOrEmpty(i.SourceApp) ? "" : $"\nAplicación: {i.SourceApp}") +
            (string.IsNullOrEmpty(i.SourceTitle) ? "" : $"\nVentana: {i.SourceTitle}") +
            (string.IsNullOrEmpty(i.SourceUrl) ? "" : $"\nSitio: {i.SourceUrl}");
    }

    void Filters_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Filters.SelectedItem is not Filter) { if (e.RemovedItems.Count > 0) Filters.SelectedItem = e.RemovedItems[0]; return; }
        Apply();
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => Apply();
    void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Apply(); }
    void ThumbSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ThumbWidth = e.NewValue;
    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails();

    void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

    void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Open_Click(sender, e);
        else if (e.Key == Key.Delete) Delete_Click(sender, e);
        else if (e.Key == Key.Escape) _editor?.HideLibrary();
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } i) { _editor?.OpenItem(i); _editor?.HideLibrary(); }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } i) return;
        if (i.IsMedia) Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { i.FullPath });
        else ClipboardService.SetImage(EditorDocument.Load(i.FullPath).Flatten());
    }

    void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } i) Process.Start("explorer.exe", $"/select,\"{i.FullPath}\"");
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = ItemsGrid.SelectedItems.Cast<LibraryItem>().ToList();
        if (selected.Count == 0) return;
        var msg = selected.Count == 1 ? $"¿Enviar «{selected[0].Name}» a la papelera?" : $"¿Enviar {selected.Count} capturas a la papelera?";
        if (MessageBox.Show(Window.GetWindow(this), msg, "Eliminar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var i in selected) LibraryStore.Instance.Delete(i);
        Apply();
    }

    void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        var selected = ItemsGrid.SelectedItems.Cast<LibraryItem>().Where(i => !i.IsMedia).OrderBy(i => i.Created).ToList();
        if (selected.Count == 0) return;
        var dlg = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = "Capturas" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        ExportService.SavePdf(selected.Select(i => EditorDocument.Load(i.FullPath).Flatten()).ToList(), dlg.FileName, App.Settings.JpegQuality);
    }

    List<LibraryItem> SelectedImages => ItemsGrid.SelectedItems.Cast<LibraryItem>().Where(i => !i.IsMedia).OrderBy(i => i.Created).ToList();
    void Combine_Click(object sender, RoutedEventArgs e) => new CombineWindow(SelectedImages) { Owner = Window.GetWindow(this) }.ShowDialog();
    void Slides_Click(object sender, RoutedEventArgs e) => new ImagesToVideoWindow(SelectedImages) { Owner = Window.GetWindow(this) }.ShowDialog();
    void Batch_Click(object sender, RoutedEventArgs e) => new BatchExportWindow(SelectedImages) { Owner = Window.GetWindow(this) }.ShowDialog();

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Imágenes y vídeos|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.ico;*.mp4", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var f in dlg.FileNames)
        {
            try
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                var item = ext == ".mp4" ? await LibraryStore.Instance.AddMediaAsync(f, Path.GetFileNameWithoutExtension(f)) : LibraryStore.Instance.Import(f);
                item.InTray = false;
            }
            catch (Exception ex) { Log.Error("Importar " + f, ex); }
        }
        LibraryStore.Instance.Save();
        Apply();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", LibraryStore.Instance.Root);

    void DetailName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Current is not { } i || string.IsNullOrWhiteSpace(DetailName.Text) || DetailName.Text == i.Name) return;
        i.Name = DetailName.Text.Trim();
        LibraryStore.Instance.Save();
    }

    void DetailTags_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Current is not { } i) return;
        var tags = DetailTags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (tags.SequenceEqual(i.Tags)) return;
        i.Tags = tags;
        LibraryStore.Instance.Save();
        if (_editor != null) Dispatcher.BeginInvoke(() => Refresh(_editor));
    }

    void Detail_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender == DetailName) DetailName_LostFocus(sender, e); else DetailTags_LostFocus(sender, e);
    }
}
