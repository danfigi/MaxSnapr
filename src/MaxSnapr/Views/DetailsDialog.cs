using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MaxSnapr.Editor.Model;
using MaxSnapr.Library;

namespace MaxSnapr.Views;

/// <summary>Capture metadata (Alt+Enter): name, tags and read-only facts.</summary>
public sealed class DetailsDialog : DialogWindow
{
    readonly LibraryItem _item;
    readonly TextBox _name, _tags;

    public DetailsDialog(LibraryItem item, EditorDocument? doc) : base("Detalles de «" + item.Name + "»", 460)
    {
        _item = item;
        _name = Field("Nombre", item.Name);
        _tags = Field("Etiquetas (separadas por comas)", item.TagsText);
        var size = File.Exists(item.FullPath) ? new FileInfo(item.FullPath).Length : 0;
        var facts = new (string, string)[]
        {
            ("Tipo", item.Kind),
            ("Dimensiones", doc != null ? $"{doc.Width} × {doc.Height} px" : $"{item.Width} × {item.Height} px"),
            ("Objetos editables", doc != null ? doc.Annotations.Count.ToString() : "—"),
            ("Duración", item.IsMedia ? TimeSpan.FromSeconds(item.DurationSeconds).ToString(@"m\:ss") : "—"),
            ("Aplicación", string.IsNullOrEmpty(item.SourceApp) ? "—" : item.SourceApp),
            ("Ventana", string.IsNullOrEmpty(item.SourceTitle) ? "—" : item.SourceTitle),
            ("Sitio web", string.IsNullOrEmpty(item.SourceUrl) ? "—" : item.SourceUrl),
            ("Creada", item.Created.ToString("F")),
            ("Modificada", item.Modified.ToString("F")),
            ("Archivo", $"{item.FullPath} ({size / 1024.0:0} KB)"),
        };
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (k, v) in facts)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var kt = new TextBlock { Text = k, Style = (Style)Application.Current.FindResource("Caption"), Margin = new Thickness(0, 3, 0, 3) };
            var vt = new TextBox { Text = v, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0), Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(kt, r); Grid.SetRow(vt, r); Grid.SetColumn(vt, 1);
            grid.Children.Add(kt); grid.Children.Add(vt);
        }
        Body.Children.Add(grid);
    }

    protected override bool Validate()
    {
        if (!string.IsNullOrWhiteSpace(_name.Text)) _item.Name = _name.Text.Trim();
        _item.Tags = _tags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        LibraryStore.Instance.Save();
        return true;
    }
}
