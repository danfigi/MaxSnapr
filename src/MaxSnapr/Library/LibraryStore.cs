using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using MaxSnapr.Editor.Model;
using MaxSnapr.Services;

namespace MaxSnapr.Library;

public sealed class LibraryItem : INotifyPropertyChanged
{
    string _name = "";
    bool _pinned;
    List<string> _tags = new();
    DateTime _modified;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get => _modified; set { _modified = value; OnChanged(); OnChanged(nameof(Subtitle)); } }
    public int Width { get; set; }
    public int Height { get; set; }
    public string SourceTitle { get; set; } = "";
    public string SourceApp { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    /// <summary>Text recognised in the background so the library search finds words inside captures.</summary>
    public string OcrText { get; set; } = "";
    [JsonIgnore] public string Site => Uri.TryCreate(SourceUrl.StartsWith("http") ? SourceUrl : "https://" + SourceUrl, UriKind.Absolute, out var u) && SourceUrl.Length > 0 ? u.Host.Replace("www.", "") : "";
    public string Kind { get; set; } = "Imagen";
    public double DurationSeconds { get; set; }
    [JsonIgnore] public bool IsVideo => Kind == "Vídeo";
    [JsonIgnore] public bool IsGif => Kind == "GIF";
    [JsonIgnore] public bool IsMedia => IsVideo || IsGif;
    [JsonIgnore] public string ThumbnailPath => FullPath + ".thumb.png";
    public bool InTray { get; set; } = true;
    public bool Pinned { get => _pinned; set { _pinned = value; OnChanged(); } }
    public string Name { get => _name; set { _name = value; OnChanged(); } }
    public List<string> Tags { get => _tags; set { _tags = value; OnChanged(); OnChanged(nameof(TagsText)); } }

    [JsonIgnore] public string TagsText => string.Join(", ", Tags);
    [JsonIgnore] public string Subtitle => IsMedia
        ? $"{Kind} · {TimeSpan.FromSeconds(DurationSeconds):m\\:ss} · {Width} × {Height}"
        : $"{Width} × {Height} · {Modified:g}";
    [JsonIgnore] public string FullPath => Path.Combine(LibraryStore.Instance.Root, FileName);
    [JsonIgnore] BitmapSource? _thumb;
    [JsonIgnore]
    public BitmapSource? Thumbnail
    {
        get => _thumb ??= IsMedia ? LibraryStore.LoadPng(ThumbnailPath) : EditorDocument.LoadPreview(FullPath);
        set { _thumb = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>Every capture is saved automatically as a .snapr file and indexed here, like a photo library.</summary>
public sealed class LibraryStore
{
    public static LibraryStore Instance { get; private set; } = null!;
    public string Root { get; }
    public ObservableCollection<LibraryItem> Items { get; } = new();
    string IndexPath => Path.Combine(Root, "library.json");

    LibraryStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        Load();
        if (AddDatesToOldNames() > 0) Save();
    }

    static readonly System.Text.RegularExpressions.Regex OldDefaultName = new(@"^(Captura|Grabación|GIF de prueba|Presentación|Combinado|Móvil) (\d{1,2}:\d{2}(:\d{2})?)$");

    /// <summary>
    /// Earlier versions named items "Captura 10:25:31" (time only). Adds the date the item was created,
    /// so "Captura 10:25:31" becomes "Captura 20-10-2026 10.25.31". Names the user typed are left alone.
    /// </summary>
    internal int AddDatesToOldNames()
    {
        int n = 0;
        foreach (var i in Items)
        {
            var m = OldDefaultName.Match(i.Name);
            if (!m.Success) continue;
            var time = m.Groups[2].Value.Replace(':', '.');
            i.Name = $"{m.Groups[1].Value} {i.Created:dd-MM-yyyy} {time}";
            n++;
        }
        return n;
    }

    public static void Initialize(string root) => Instance = new LibraryStore(root);

    void Load()
    {
        try
        {
            if (File.Exists(IndexPath))
            {
                var items = JsonSerializer.Deserialize<List<LibraryItem>>(File.ReadAllText(IndexPath)) ?? new();
                foreach (var i in items.Where(i => File.Exists(Path.Combine(Root, i.FileName))).OrderByDescending(i => i.Modified)) Items.Add(i);
            }
            // Pick up .snapr files copied into the folder by hand.
            var known = Items.Select(i => i.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(Root, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (EditorDocument.Extension or ".mp4" or ".gif")) continue;
                var rel = Path.GetRelativePath(Root, f);
                if (known.Contains(rel)) continue;
                var fi = new FileInfo(f);
                Items.Add(new LibraryItem
                {
                    FileName = rel, Created = fi.CreationTime, Modified = fi.LastWriteTime, Name = Path.GetFileNameWithoutExtension(f), InTray = false,
                    Kind = ext == ".mp4" ? "Vídeo" : ext == ".gif" ? "GIF" : "Imagen",
                });
            }
        }
        catch (Exception ex) { Log.Error("Biblioteca", ex); }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(IndexPath + ".tmp", JsonSerializer.Serialize(Items.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(IndexPath + ".tmp", IndexPath, true);
        }
        catch (Exception ex) { Log.Error("Guardar biblioteca", ex); }
    }

    public LibraryItem Add(EditorDocument doc, string sourceTitle, string sourceApp, string sourceUrl = "")
    {
        var now = DateTime.Now;
        // Unique on disk even for several captures in the same second; the visible name has no milliseconds.
        var rel = Path.Combine(now.ToString("yyyy-MM"), $"Captura {Naming.Stamp(now)}.{now:fff}{EditorDocument.Extension}");
        var item = new LibraryItem
        {
            Id = doc.LibraryId, FileName = rel, Created = now, Modified = now, Width = doc.Width, Height = doc.Height,
            SourceTitle = sourceTitle, SourceApp = sourceApp,
            Name = string.IsNullOrWhiteSpace(sourceTitle) ? Naming.Default("Captura", now) : sourceTitle,
        };
        item.SourceUrl = sourceUrl;
        doc.Save(item.FullPath);
        Items.Insert(0, item);
        Save();
        IndexText(item, doc.Base);
        return item;
    }

    public LibraryItem? Find(Guid id) => Items.FirstOrDefault(i => i.Id == id);

    /// <summary>Background OCR so searches match text inside images.</summary>
    public void IndexText(LibraryItem item, BitmapSource image)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var text = await OcrService.RecognizeAsync(image);
                item.OcrText = text.Length > 4000 ? text[..4000] : text;
                Save();
            }
            catch { /* no OCR language installed: search simply ignores image text */ }
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    public IEnumerable<string> AllSites => Items.Select(i => i.Site).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s);

    /// <summary>Writes the document back to its library file and refreshes metadata/thumbnail.</summary>
    public void Update(EditorDocument doc)
    {
        var item = Find(doc.LibraryId);
        if (item == null) return;
        doc.Save(item.FullPath);
        item.Width = doc.Width;
        item.Height = doc.Height;
        item.Modified = DateTime.Now;
        item.Thumbnail = null;
        item.Thumbnail = EditorDocument.LoadPreview(item.FullPath);
        Save();
    }

    public void Delete(LibraryItem item)
    {
        try
        {
            if (File.Exists(item.ThumbnailPath)) File.Delete(item.ThumbnailPath);
            if (File.Exists(item.FullPath))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.FullPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) { Log.Error("Eliminar de la biblioteca", ex); }
        Items.Remove(item);
        Save();
    }

    public static BitmapSource? LoadPng(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var img = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    /// <summary>Registers an MP4 or GIF; files outside the library are copied in first.</summary>
    public async System.Threading.Tasks.Task<LibraryItem> AddMediaAsync(string path, string sourceTitle)
    {
        var full = Path.GetFullPath(path);
        if (!PathUtil.IsInside(full, Root))
        {
            var dir = Path.Combine(Root, DateTime.Now.ToString("yyyy-MM"));
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, Path.GetFileName(path));
            if (File.Exists(dest)) dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)} {DateTime.Now:HHmmss}{Path.GetExtension(path)}");
            File.Copy(path, dest);
            full = dest;
        }
        bool gif = Path.GetExtension(full).Equals(".gif", StringComparison.OrdinalIgnoreCase);
        var now = DateTime.Now;
        var item = new LibraryItem
        {
            FileName = Path.GetRelativePath(Root, full), Created = now, Modified = now, Kind = gif ? "GIF" : "Vídeo",
            Name = string.IsNullOrWhiteSpace(sourceTitle) ? Path.GetFileNameWithoutExtension(full) : Naming.Default(sourceTitle, now), SourceTitle = sourceTitle,
        };
        BitmapSource thumb;
        if (gif)
        {
            using var fs = File.OpenRead(full);
            var dec = new GifBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            thumb = dec.Frames[0];
            item.Width = thumb.PixelWidth;
            item.Height = thumb.PixelHeight;
            item.DurationSeconds = Video.GifPlayer.TotalDuration(full).TotalSeconds;
        }
        else
        {
            var info = await Video.VideoTools.GetInfoAsync(full);
            item.Width = info.Width;
            item.Height = info.Height;
            item.DurationSeconds = info.Duration.TotalSeconds;
            thumb = await Video.VideoTools.FrameAsync(full, TimeSpan.FromSeconds(Math.Min(0.5, info.Duration.TotalSeconds / 2)), 360);
        }
        var small = EditorDocument.Thumbnail(thumb, 360);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(small));
        using (var ts = File.Create(item.ThumbnailPath)) enc.Save(ts);
        Items.Insert(0, item);
        Save();
        return item;
    }

    public LibraryItem Import(string imagePath)
    {
        var doc = new EditorDocument(ExportService.LoadImage(imagePath));
        var item = Add(doc, Path.GetFileNameWithoutExtension(imagePath), "Archivo");
        return item;
    }

    public IEnumerable<string> AllTags => Items.SelectMany(i => i.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(t => t);
    public IEnumerable<string> AllApps => Items.Select(i => i.SourceApp).Where(a => !string.IsNullOrEmpty(a)).Distinct().OrderBy(a => a);
}
