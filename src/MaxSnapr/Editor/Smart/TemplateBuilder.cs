using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxSnapr.Editor.Model;
using MaxSnapr.Editor.Raster;

namespace MaxSnapr.Editor.Smart;

/// <summary>Builds editable documents that combine several images: templates and simple stacks.</summary>
public static class TemplateBuilder
{
    /// <summary>Dynamic layouts based on how many images there are.</summary>
    public static TemplateDefinition Stack(string kind, int count)
    {
        count = Math.Max(1, count);
        var t = new TemplateDefinition { Name = kind, Category = "Combinar", Title = false, Captions = false };
        switch (kind)
        {
            case "Apilar en vertical":
                t.Width = 1200; t.Height = 700 * count;
                for (int i = 0; i < count; i++) t.Slots.Add(new TemplateSlot { Area = new Rect(0, i / (double)count, 1, 1.0 / count) });
                break;
            case "En fila":
                t.Width = 900 * count; t.Height = 800;
                for (int i = 0; i < count; i++) t.Slots.Add(new TemplateSlot { Area = new Rect(i / (double)count, 0, 1.0 / count, 1) });
                break;
            default:
                int cols = (int)Math.Ceiling(Math.Sqrt(count)), rows = (int)Math.Ceiling(count / (double)cols);
                t.Width = 900 * cols; t.Height = 700 * rows;
                for (int i = 0; i < count; i++) t.Slots.Add(new TemplateSlot { Area = new Rect(i % cols / (double)cols, i / cols / (double)rows, 1.0 / cols, 1.0 / rows) });
                break;
        }
        return t;
    }

    public static EditorDocument Build(TemplateDefinition def, IReadOnlyList<BitmapSource> images, string title, ToolTheme theme)
    {
        var accent = theme.Colors.ElementAtOrDefault(4);
        if (accent.A == 0) accent = Color.FromRgb(0x2E, 0x7C, 0xF6);
        var ink = Color.FromRgb(0x1D, 0x20, 0x25);
        bool meme = def.Name == "Meme";
        int W = def.Width, H = def.Height;
        const double margin = 50, gap = 34;
        double header = def.Title && !meme ? 120 : 0;
        bool plainStack = def.Category == "Combinar";
        var bg = RasterOps.Render(W, H, dc =>
        {
            if (meme) { dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, W, H)); return; }
            if (plainStack) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H)); return; }
            var lg = new LinearGradientBrush(Color.FromRgb(0xF8, 0xF9, 0xFB), Color.FromRgb(0xEC, 0xEF, 0xF3), 90);
            dc.DrawRectangle(lg, null, new Rect(0, 0, W, H));
            dc.DrawRectangle(new SolidColorBrush(accent), null, new Rect(0, 0, W, 10));
        });
        var doc = new EditorDocument(bg);
        if (header > 0)
            doc.Annotations.Add(new TextAnnotation
            {
                Box = new Rect(margin, 34, W - margin * 2, 70), Text = string.IsNullOrWhiteSpace(title) ? "Título" : title,
                Style = new ToolStyle { TextColor = ink, FontSize = 46, Bold = true, Shadow = false, Thickness = 0, FontFamily = theme.Font },
            });
        var content = plainStack ? new Rect(0, 0, W, H) : new Rect(margin, margin + header, W - margin * 2, H - margin * 2 - header);
        double captionH = def.Captions && !meme && !plainStack ? 56 : 0;
        var centers = new List<Rect>();
        for (int i = 0; i < def.Slots.Count; i++)
        {
            var a = def.Slots[i].Area;
            var slot = new Rect(content.X + a.X * content.Width, content.Y + a.Y * content.Height, a.Width * content.Width, a.Height * content.Height);
            double g = plainStack ? 0 : gap / 2;
            slot.Inflate(-g, -g);
            var imgArea = new Rect(slot.X, slot.Y, slot.Width, Math.Max(10, slot.Height - captionH));
            if (i < images.Count)
            {
                var img = images[i];
                double s = Math.Min(imgArea.Width / img.PixelWidth, imgArea.Height / img.PixelHeight);
                if (plainStack) s = Math.Min(s, 1);
                var box = new Rect(imgArea.X + (imgArea.Width - img.PixelWidth * s) / 2, imgArea.Y + (imgArea.Height - img.PixelHeight * s) / 2, img.PixelWidth * s, img.PixelHeight * s);
                var ann = new ImageAnnotation { Box = box, Style = new ToolStyle { Shadow = !plainStack && !meme, Thickness = 0 } };
                ann.Image = img;
                doc.Annotations.Add(ann);
                centers.Add(box);
            }
            else
            {
                // Empty slot placeholder the user can replace by pasting an image.
                doc.Annotations.Add(new ShapeAnnotation { Box = imgArea, Style = new ToolStyle { Shape = ShapeKind.RoundedRectangle, Stroke = Color.FromRgb(0xC5, 0xCB, 0xD3), Dash = LineDash.Dash, Thickness = 3, Fill = Colors.Transparent, Shadow = false } });
                centers.Add(imgArea);
            }
            if (captionH > 0)
                doc.Annotations.Add(new TextAnnotation
                {
                    Box = new Rect(slot.X, slot.Bottom - captionH + 8, slot.Width, captionH - 8), Text = def.Slots[i].Caption.Length > 0 ? def.Slots[i].Caption : $"Imagen {i + 1}",
                    Style = new ToolStyle { TextColor = ink, FontSize = 24, Bold = false, Shadow = false, Thickness = 0, Align = TextAlign.Center, FontFamily = theme.Font },
                });
            if (def.Numbers)
                doc.Annotations.Add(new StepAnnotation { Center = new Point(slot.X + 26, slot.Y + 26), Number = i + 1, Style = new ToolStyle { Fill = accent, Stroke = accent, TextColor = Colors.White, Size = 48, Thickness = 2 } });
        }
        if (def.Arrows)
            for (int i = 0; i + 1 < centers.Count; i++)
            {
                var a = centers[i]; var b = centers[i + 1];
                bool horizontal = Math.Abs(b.X - a.X) > Math.Abs(b.Y - a.Y);
                var start = horizontal ? new Point(a.Right - 10, a.Y + a.Height / 2) : new Point(a.X + a.Width / 2, a.Bottom - 10);
                var end = horizontal ? new Point(b.X + 10, b.Y + b.Height / 2) : new Point(b.X + b.Width / 2, b.Y + 10);
                doc.Annotations.Add(new LineAnnotation { Start = start, End = end, Style = new ToolStyle { Stroke = accent, Thickness = 8, EndCap = LineCap.Arrow } });
            }
        if (meme)
        {
            var memeStyle = new ToolStyle { TextColor = Colors.White, Stroke = Colors.Black, TextOutline = true, FontSize = 76, Bold = true, Shadow = false, Align = TextAlign.Center, FontFamily = Fonts.SystemFontFamilies.Any(f => f.Source == "Impact") ? "Impact" : "Segoe UI Black", Thickness = 0 };
            doc.Annotations.Add(new TextAnnotation { Box = new Rect(30, 30, W - 60, 100), Text = string.IsNullOrWhiteSpace(title) ? "TEXTO SUPERIOR" : title.ToUpperInvariant(), Style = memeStyle.Clone() });
            doc.Annotations.Add(new TextAnnotation { Box = new Rect(30, H - 140, W - 60, 100), Text = def.Slots[0].Caption, Style = memeStyle.Clone() });
        }
        if (plainStack && images.Count > 0)
        {
            // Trim the canvas to the images when stacking so there is no empty margin.
            var all = doc.Annotations.Select(a => a.Bounds).Aggregate(Rect.Union);
            var crop = Tools.ToolBase.ScreenSnapshotRect(all, doc.Width, doc.Height);
            doc.ReplaceBase(RasterOps.Crop(doc.Base, crop), new Vector(-crop.X, -crop.Y));
        }
        foreach (var t in doc.Annotations.OfType<TextAnnotation>()) t.FitHeight(1);
        doc.IsDirty = true;
        return doc;
    }

    public static BitmapSource Preview(TemplateDefinition def, ToolTheme theme, int maxSize = 220)
    {
        var placeholder = RasterOps.Render(640, 400, dc =>
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x9E, 0xC9, 0xFF), Color.FromRgb(0xC8, 0xB6, 0xFF), 30), null, new Rect(0, 0, 640, 400));
            for (int i = 0; i < 5; i++) dc.DrawRoundedRectangle(Brushes.White, null, new Rect(40, 50 + i * 60, 260 + i * 37 % 200, 22), 8, 8);
        });
        var imgs = Enumerable.Repeat(placeholder, def.Slots.Count).ToList();
        var doc = Build(def, imgs, "Título", theme);
        return EditorDocument.Thumbnail(doc.Flatten(), maxSize);
    }
}
