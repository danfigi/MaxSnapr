using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxSnapr.Editor.Raster;

namespace MaxSnapr.Editor.Model;

/// <summary>
/// A capture being edited: a raster base image plus editable vector objects on top.
/// All coordinates are base-image pixels. Saved as a .snapr zip (document.json + base.png + preview.png).
/// </summary>
public sealed class EditorDocument
{
    public const string Extension = ".snapr";

    BitmapSource _base;
    readonly Stack<Memento> _undo = new();
    readonly Stack<Memento> _redo = new();

    public EditorDocument(BitmapSource image)
    {
        _base = RasterOps.Normalize(image);
    }

    public BitmapSource Base => _base;
    public List<Annotation> Annotations { get; private set; } = new();
    public Color CanvasColor { get; set; } = Colors.Transparent;
    public Guid LibraryId { get; set; } = Guid.NewGuid();
    public string? FilePath { get; set; }
    public bool IsDirty { get; set; }
    public int Width => _base.PixelWidth;
    public int Height => _base.PixelHeight;

    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    sealed record Memento(BitmapSource Base, List<Annotation> Annotations);

    Memento Snapshot() => new(_base, Annotations.Select(a => a.Clone()).ToList());

    /// <summary>Call before any modification so it can be undone.</summary>
    public void Checkpoint()
    {
        _undo.Push(Snapshot());
        _redo.Clear();
        if (_undo.Count > 200) TrimUndo();
        IsDirty = true;
    }

    void TrimUndo()
    {
        var keep = _undo.Take(150).Reverse().ToList();
        _undo.Clear();
        foreach (var m in keep) _undo.Push(m);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
    }

    void Restore(Memento m)
    {
        _base = m.Base;
        Annotations = m.Annotations.Select(a => a.Clone()).ToList();
        IsDirty = true;
        RaiseChanged();
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Replaces the base raster; <paramref name="shift"/> moves objects to follow the pixels.</summary>
    public void ReplaceBase(BitmapSource image, Vector shift = default)
    {
        _base = RasterOps.Normalize(image);
        if (shift != default) foreach (var a in Annotations) a.Offset(shift);
        IsDirty = true;
        RaiseChanged();
    }

    public void Add(Annotation a) { Annotations.Add(a); IsDirty = true; RaiseChanged(); }

    public void Remove(IEnumerable<Annotation> items)
    {
        var set = items.ToHashSet();
        Annotations.RemoveAll(set.Contains);
        IsDirty = true;
        RaiseChanged();
    }

    public void BringToFront(IEnumerable<Annotation> items) { var l = items.ToList(); Annotations.RemoveAll(l.Contains); Annotations.AddRange(l); RaiseChanged(); }
    public void SendToBack(IEnumerable<Annotation> items) { var l = items.ToList(); Annotations.RemoveAll(l.Contains); Annotations.InsertRange(0, l); RaiseChanged(); }

    public void BringForward(Annotation a)
    {
        int i = Annotations.IndexOf(a);
        if (i < 0 || i == Annotations.Count - 1) return;
        (Annotations[i], Annotations[i + 1]) = (Annotations[i + 1], Annotations[i]);
        RaiseChanged();
    }

    public void SendBackward(Annotation a)
    {
        int i = Annotations.IndexOf(a);
        if (i <= 0) return;
        (Annotations[i], Annotations[i - 1]) = (Annotations[i - 1], Annotations[i]);
        RaiseChanged();
    }

    public int NextStepNumber() => Annotations.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;

    public RenderContext CreateContext(double pixelsPerDip = 1) => new() { BaseImage = _base, PixelsPerDip = pixelsPerDip };

    public void RenderContent(DrawingContext dc, double pixelsPerDip = 1, IEnumerable<Annotation>? skip = null)
    {
        var ctx = CreateContext(pixelsPerDip);
        if (CanvasColor.A > 0) dc.DrawRectangle(new SolidColorBrush(CanvasColor), null, new Rect(0, 0, Width, Height));
        dc.DrawImage(_base, new Rect(0, 0, Width, Height));
        var skipSet = skip?.ToHashSet();
        foreach (var a in Annotations)
            if (skipSet == null || !skipSet.Contains(a)) a.Render(dc, ctx);
    }

    /// <summary>Final pixels: base plus every object.</summary>
    public BitmapSource Flatten() => RasterOps.Render(Width, Height, dc => RenderContent(dc));

    /// <summary>Burns all objects into the base image (irreversible apart from undo).</summary>
    public void FlattenInPlace()
    {
        Checkpoint();
        _base = Flatten();
        Annotations.Clear();
        RaiseChanged();
    }

    // ---- persistence ---------------------------------------------------------------------------

    sealed class DocumentFile
    {
        public int Version { get; set; } = 1;
        public string App { get; set; } = "MaxSnapr";
        public Guid LibraryId { get; set; }
        public Color CanvasColor { get; set; }
        public List<Annotation> Annotations { get; set; } = new();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var json = JsonSerializer.Serialize(new DocumentFile { LibraryId = LibraryId, CanvasColor = CanvasColor, Annotations = Annotations }, ToolStyle.Json);
                using (var w = new StreamWriter(zip.CreateEntry("document.json").Open())) w.Write(json);
                WritePng(zip.CreateEntry("base.png", CompressionLevel.Fastest).Open(), _base);
                WritePng(zip.CreateEntry("preview.png", CompressionLevel.Fastest).Open(), Thumbnail(Flatten(), 360));
            }
            File.Move(tmp, path, true);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
        FilePath = path;
        IsDirty = false;
    }

    public static EditorDocument Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        // A document is at most a few hundred MB; refuse anything that would exhaust memory.
        var baseEntry = zip.GetEntry("base.png") ?? throw new InvalidDataException("El archivo no es un documento de MaxSnapr (falta la imagen).");
        if (baseEntry.Length > 512L * 1024 * 1024) throw new InvalidDataException("La imagen del documento es demasiado grande.");
        BitmapSource baseImg;
        using (var s = baseEntry.Open())
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            var frame = BitmapFrame.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if ((long)frame.PixelWidth * frame.PixelHeight > 250_000_000) throw new InvalidDataException("La imagen del documento es demasiado grande.");
            ms.Position = 0;
            baseImg = BitmapFrame.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        var doc = new EditorDocument(baseImg) { FilePath = path };
        var entry = zip.GetEntry("document.json");
        if (entry != null && entry.Length > 64L * 1024 * 1024) throw new InvalidDataException("Los objetos del documento ocupan demasiado.");
        if (entry != null)
        {
            using var r = new StreamReader(entry.Open());
            var file = JsonSerializer.Deserialize<DocumentFile>(r.ReadToEnd(), ToolStyle.Json);
            if (file != null)
            {
                doc.Annotations = file.Annotations;
                doc.CanvasColor = file.CanvasColor;
                if (file.LibraryId != Guid.Empty) doc.LibraryId = file.LibraryId;
            }
        }
        return doc;
    }

    public static BitmapSource? LoadPreview(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var e = zip.GetEntry("preview.png");
            if (e == null) return null;
            using var s = e.Open();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            var img = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public static BitmapSource Thumbnail(BitmapSource src, int max)
    {
        double s = Math.Min(1, max / (double)Math.Max(src.PixelWidth, src.PixelHeight));
        if (s >= 1) return src;
        return RasterOps.Resize(src, Math.Max(1, (int)(src.PixelWidth * s)), Math.Max(1, (int)(src.PixelHeight * s)));
    }

    static void WritePng(Stream s, BitmapSource img)
    {
        // WIC needs a seekable stream and zip entries are write-only, so encode in memory first.
        using (s)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using var ms = new MemoryStream();
            enc.Save(ms);
            ms.Position = 0;
            ms.CopyTo(s);
        }
    }
}
