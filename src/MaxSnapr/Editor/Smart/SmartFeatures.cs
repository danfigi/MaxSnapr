using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxSnapr.Editor.Model;
using MaxSnapr.Editor.Raster;

namespace MaxSnapr.Editor.Smart;

public sealed record OcrWord(string Text, Rect Bounds);
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words)
{
    public Rect Bounds => Words.Select(w => w.Bounds).Aggregate(Rect.Union);
}

public enum RedactCategory { Email, Phone, Iban, Card, IdNumber, Ip, Url, Numbers, Faces, Custom }

/// <summary>OCR with word geometry, face detection and the features built on them.</summary>
public static class SmartFeatures
{
    static async Task<Windows.Graphics.Imaging.SoftwareBitmap> ToSoftwareBitmap(BitmapSource img, Windows.Graphics.Imaging.BitmapPixelFormat format)
    {
        var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        enc.Save(ms);
        using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await ras.WriteAsync(ms.ToArray().AsBuffer());
        ras.Seek(0);
        var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        return await dec.GetSoftwareBitmapAsync(format, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
    }

    /// <summary>Lines with word rectangles in the coordinates of <paramref name="img"/>.</summary>
    public static async Task<List<OcrLine>> OcrAsync(BitmapSource img)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows no tiene ningún idioma de OCR instalado.");
        double scale = 1;
        var src = img;
        int max = Math.Max(img.PixelWidth, img.PixelHeight);
        if (max < 1400) scale = Math.Min(3, 1400.0 / max);
        if (max * scale > Windows.Media.Ocr.OcrEngine.MaxImageDimension) scale = Windows.Media.Ocr.OcrEngine.MaxImageDimension / (double)max;
        if (Math.Abs(scale - 1) > 0.01) src = RasterOps.Resize(img, (int)(img.PixelWidth * scale), (int)(img.PixelHeight * scale));
        using var sb = await ToSoftwareBitmap(src, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8);
        var result = await engine.RecognizeAsync(sb);
        return result.Lines.Select(l => new OcrLine(l.Text, l.Words.Select(w =>
            new OcrWord(w.Text, new Rect(w.BoundingRect.X / scale, w.BoundingRect.Y / scale, w.BoundingRect.Width / scale, w.BoundingRect.Height / scale))).ToList())).ToList();
    }

    public static async Task<List<Rect>> FacesAsync(BitmapSource img)
    {
        if (!Windows.Media.FaceAnalysis.FaceDetector.IsSupported) return new();
        var detector = await Windows.Media.FaceAnalysis.FaceDetector.CreateAsync();
        using var sb = await ToSoftwareBitmap(img, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8);
        using var gray = Windows.Graphics.Imaging.SoftwareBitmap.Convert(sb, Windows.Graphics.Imaging.BitmapPixelFormat.Gray8);
        var faces = await detector.DetectFacesAsync(gray);
        return faces.Select(f => new Rect(f.FaceBox.X, f.FaceBox.Y, f.FaceBox.Width, f.FaceBox.Height)).ToList();
    }

    static readonly Dictionary<RedactCategory, Regex> Patterns = new()
    {
        [RedactCategory.Email] = new(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", RegexOptions.IgnoreCase),
        [RedactCategory.Phone] = new(@"(\+?\d{1,3}[\s.-]?)?(\(?\d{2,4}\)?[\s.-]?)?\d{3}[\s.-]?\d{2,3}[\s.-]?\d{2,4}"),
        [RedactCategory.Iban] = new(@"\b[A-Z]{2}\d{2}(\s?[A-Z0-9]{4}){3,7}(\s?[A-Z0-9]{1,4})?\b", RegexOptions.IgnoreCase),
        [RedactCategory.Card] = new(@"\b(?:\d[ -]?){13,19}\b"),
        [RedactCategory.IdNumber] = new(@"\b([XYZ]?\d{7,8}[-\s]?[A-Z])\b", RegexOptions.IgnoreCase),
        [RedactCategory.Ip] = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b"),
        [RedactCategory.Url] = new(@"\b(?:https?://|www\.)\S+", RegexOptions.IgnoreCase),
        [RedactCategory.Numbers] = new(@"\b\d[\d.,]{3,}\b"),
    };

    public static string Label(RedactCategory c) => c switch
    {
        RedactCategory.Email => "Correos electrónicos",
        RedactCategory.Phone => "Teléfonos",
        RedactCategory.Iban => "Cuentas bancarias (IBAN)",
        RedactCategory.Card => "Tarjetas de crédito",
        RedactCategory.IdNumber => "DNI / NIE",
        RedactCategory.Ip => "Direcciones IP",
        RedactCategory.Url => "Direcciones web",
        RedactCategory.Numbers => "Cifras y cantidades",
        RedactCategory.Faces => "Caras",
        _ => "Palabras personalizadas",
    };

    static bool Luhn(string digits)
    {
        int sum = 0; bool alt = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (alt) { d *= 2; if (d > 9) d -= 9; }
            sum += d; alt = !alt;
        }
        return digits.Length >= 13 && sum % 10 == 0;
    }

    /// <summary>Rectangles to obscure for the chosen categories (and custom words).</summary>
    public static async Task<List<(RedactCategory cat, Rect rect)>> FindSensitiveAsync(BitmapSource img, ISet<RedactCategory> cats, IEnumerable<string>? customWords = null)
    {
        var found = new List<(RedactCategory, Rect)>();
        var textCats = cats.Where(c => c is not (RedactCategory.Faces or RedactCategory.Custom)).ToList();
        var custom = (customWords ?? Enumerable.Empty<string>()).Where(w => w.Trim().Length > 0).Select(w => w.Trim()).ToList();
        if (textCats.Count > 0 || custom.Count > 0)
        {
            foreach (var line in await OcrAsync(img))
            {
                // Map character offsets in the joined line back to the words they came from.
                var spans = new List<(int start, int end, OcrWord w)>();
                var text = "";
                foreach (var w in line.Words)
                {
                    if (text.Length > 0) text += " ";
                    spans.Add((text.Length, text.Length + w.Text.Length, w));
                    text += w.Text;
                }
                Rect Cover(int s, int e)
                {
                    var hits = spans.Where(x => x.end > s && x.start < e).Select(x => x.w.Bounds).ToList();
                    return hits.Count == 0 ? Rect.Empty : hits.Aggregate(Rect.Union);
                }
                foreach (var c in textCats)
                    foreach (Match m in Patterns[c].Matches(text))
                    {
                        if (c == RedactCategory.Card && !Luhn(new string(m.Value.Where(char.IsDigit).ToArray()))) continue;
                        if (c == RedactCategory.Phone && m.Value.Count(char.IsDigit) < 9) continue;
                        var r = Cover(m.Index, m.Index + m.Length);
                        if (!r.IsEmpty) found.Add((c, r));
                    }
                foreach (var word in custom)
                {
                    int i = 0;
                    while ((i = text.IndexOf(word, i, StringComparison.CurrentCultureIgnoreCase)) >= 0)
                    {
                        var r = Cover(i, i + word.Length);
                        if (!r.IsEmpty) found.Add((RedactCategory.Custom, r));
                        i += word.Length;
                    }
                }
            }
        }
        if (cats.Contains(RedactCategory.Faces))
            foreach (var f in await FacesAsync(img)) found.Add((RedactCategory.Faces, f));
        return found;
    }

    /// <summary>Adds editable redaction objects over every sensitive match.</summary>
    public static async Task<int> SmartRedactAsync(EditorDocument doc, ISet<RedactCategory> cats, RedactKind kind, IEnumerable<string>? custom)
    {
        var hits = await FindSensitiveAsync(doc.Base, cats, custom);
        if (hits.Count == 0) return 0;
        doc.Checkpoint();
        foreach (var (_, r) in hits)
        {
            var box = Rect.Inflate(r, Math.Max(2, r.Height * 0.15), Math.Max(2, r.Height * 0.2));
            doc.Annotations.Add(new RedactAnnotation { Box = box, Style = new ToolStyle { Redact = kind, Intensity = 70, Fill = Color.FromRgb(0x1D, 0x1D, 0x1F), Shadow = false } });
        }
        doc.RaiseChanged();
        return hits.Count;
    }

    /// <summary>
    /// Turns recognised text inside <paramref name="region"/> into editable text objects and removes the
    /// original pixels by filling from the surrounding background.
    /// </summary>
    public static async Task<int> EditTextAsync(EditorDocument doc, Rect region, double pixelsPerDip)
    {
        var r = Tools.ToolBase.ScreenSnapshotRect(region, doc.Width, doc.Height);
        if (r.Width < 4 || r.Height < 4) r = new Int32Rect(0, 0, doc.Width, doc.Height);
        var crop = RasterOps.Crop(doc.Base, r);
        var lines = MergeRows(await OcrAsync(crop));
        if (lines.Count == 0) return 0;
        doc.Checkpoint();
        var baseImg = doc.Base;
        var px = RasterOps.Read(baseImg);
        foreach (var line in lines)
        {
            var b = line.Bounds;
            var lr = new Int32Rect((int)(b.X + r.X) - 2, (int)(b.Y + r.Y) - 2, (int)b.Width + 5, (int)b.Height + 5);
            var textColor = DominantInk(px, lr);
            baseImg = RasterOps.FillFromSurroundings(baseImg, lr);
            // Word boxes span cap height only (or ascender to descender), so convert to an em size.
            bool descender = line.Text.Any(ch => "gjpqyçQ,;".Contains(ch));
            bool lower = line.Text.Any(char.IsLower);
            double em = b.Height * (descender ? 1.05 : lower ? 1.2 : 1.36);
            var style = new ToolStyle
            {
                TextColor = textColor, FontSize = Math.Max(8, em), Bold = false, Shadow = false, Thickness = 0, Callout = CalloutKind.None,
                FontFamily = "Segoe UI",
            };
            double top = b.Y + r.Y - (em - b.Height) * 0.62 - 4;
            var t = new TextAnnotation { Box = new Rect(b.X + r.X - 4, top, Math.Max(40, b.Width * 1.2 + 16), em + 8), Text = line.Text, Style = style };
            t.FitHeight(pixelsPerDip);
            doc.Annotations.Add(t);
        }
        doc.ReplaceBase(baseImg);
        return lines.Count;
    }

    /// <summary>Joins OCR lines that sit on the same row (the engine splits lines at wide gaps).</summary>
    static List<OcrLine> MergeRows(List<OcrLine> lines)
    {
        var rows = new List<List<OcrLine>>();
        foreach (var l in lines.OrderBy(l => l.Bounds.Y))
        {
            var b = l.Bounds;
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].Bounds.Y + r[0].Bounds.Height / 2 - (b.Y + b.Height / 2)) < Math.Max(r[0].Bounds.Height, b.Height) * 0.5);
            if (row == null) rows.Add(new List<OcrLine> { l }); else row.Add(l);
        }
        return rows.Select(r =>
        {
            var ordered = r.OrderBy(l => l.Bounds.X).ToList();
            var words = ordered.SelectMany(l => l.Words).ToList();
            return new OcrLine(string.Join("   ", ordered.Select(l => l.Text)), words);
        }).ToList();
    }

    /// <summary>Most contrasting colour inside a rectangle relative to its border (the "ink").</summary>
    static Color DominantInk(RasterOps.Pixels p, Int32Rect r)
    {
        r = Capture.ScreenSnapshot.Intersect(r, new Int32Rect(0, 0, p.W, p.H));
        if (r.Width <= 0) return Colors.Black;
        int bi = p.Index(r.X, r.Y);
        int bgL = p.Data[bi] + p.Data[bi + 1] + p.Data[bi + 2];
        int best = -1; Color c = Colors.Black;
        for (int y = r.Y; y < r.Y + r.Height; y += 2)
            for (int x = r.X; x < r.X + r.Width; x += 2)
            {
                int i = p.Index(x, y);
                int d = Math.Abs(p.Data[i] + p.Data[i + 1] + p.Data[i + 2] - bgL);
                if (d > best) { best = d; c = Color.FromRgb(p.Data[i + 2], p.Data[i + 1], p.Data[i]); }
            }
        return c;
    }

    /// <summary>
    /// Simplified look: text lines become soft rounded bars in a colour sampled from the text, so the
    /// screenshot shows structure without distracting details.
    /// </summary>
    public static async Task<int> SimplifyAsync(EditorDocument doc, bool removeText, Color? barColor)
    {
        var lines = await OcrAsync(doc.Base);
        if (lines.Count == 0) return 0;
        doc.Checkpoint();
        var px = RasterOps.Read(doc.Base);
        var baseImg = doc.Base;
        foreach (var line in lines)
        {
            var b = line.Bounds;
            var lr = new Int32Rect((int)b.X - 1, (int)b.Y - 1, (int)b.Width + 3, (int)b.Height + 3);
            var ink = DominantInk(px, lr);
            if (removeText) baseImg = RasterOps.FillFromSurroundings(baseImg, lr);
            double h = Math.Max(4, b.Height * 0.55);
            var color = barColor ?? Color.FromArgb(0xB0, ink.R, ink.G, ink.B);
            // One bar per word keeps the rhythm of the original text.
            foreach (var w in line.Words)
            {
                var wb = w.Bounds;
                doc.Annotations.Add(new ShapeAnnotation
                {
                    Box = new Rect(wb.X, wb.Y + (wb.Height - h) / 2, Math.Max(h, wb.Width), h),
                    Style = new ToolStyle { Shape = ShapeKind.RoundedRectangle, CornerRadius = h / 2, Fill = color, Thickness = 0, Shadow = false },
                });
            }
        }
        if (removeText) doc.ReplaceBase(baseImg); else doc.RaiseChanged();
        return lines.Count;
    }
}
