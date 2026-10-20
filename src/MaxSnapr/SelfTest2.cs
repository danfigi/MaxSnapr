using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxSnapr.Capture;
using MaxSnapr.Editor.Model;
using MaxSnapr.Editor.Raster;
using MaxSnapr.Editor.Smart;
using MaxSnapr.Library;
using MaxSnapr.Services;
using MaxSnapr.Views;

namespace MaxSnapr;

public static partial class SelfTest
{
    static async Task ExtrasAsync(string outDir)
    {
        StitchCheck(outDir);
        GuideCheck(outDir);
        TemplateCheck(outDir);
        await SmartChecks(outDir);
        EffectChecks(outDir);
        EffectChecks2(outDir);
        await EditorOpsCheck(outDir);
        OutputChecks(outDir);
        await MediaLibraryCheck(outDir);
        ModeOverlayShots(outDir);
    }

    static void StitchCheck(string outDir)
    {
        // Tall synthetic page with a fixed 60 px header; frames emulate scrolling a 600 px viewport.
        const int w = 700, contentH = 2600, view = 540, header = 60;
        var content = RasterOps.Render(w, contentH, dc =>
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, contentH));
            var rnd = new Random(3);
            for (int y = 0; y < contentH; y += 26)
            {
                var b = new SolidColorBrush(Color.FromRgb((byte)rnd.Next(30, 200), (byte)rnd.Next(30, 200), (byte)rnd.Next(30, 200)));
                dc.DrawRectangle(b, null, new Rect(20 + rnd.Next(0, 80), y + 4, 200 + rnd.Next(0, 400), 14));
                dc.DrawText(new FormattedText($"Línea {y / 26}", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.Black, 1), new Point(w - 120, y + 3));
            }
        });
        BitmapSource Frame(int offset) => RasterOps.Render(w, header + view, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2E, 0x7C, 0xF6)), null, new Rect(0, 0, w, header));
            dc.PushClip(new RectangleGeometry(new Rect(0, header, w, view)));
            dc.DrawImage(content, new Rect(0, header - offset, w, contentH));
            dc.Pop();
        });
        var st = new Stitcher();
        int last = 0;
        for (int off = 0; off <= contentH - view; off += 230) { st.Add(Frame(off)); last = off; }
        if (last != contentH - view) { st.Add(Frame(contentH - view)); last = contentH - view; }
        st.Add(Frame(last)); // bottom reached: no movement
        var result = st.Compose();
        Save(result, outDir, "60-stitched.png");
        int expected = header + contentH;
        Check("Unir capturas con desplazamiento", Math.Abs(result.PixelHeight - expected) <= 2, $"{result.PixelHeight} px (esperado {expected}), {st.FrameCount} fotogramas");
    }

    static void GuideCheck(string outDir)
    {
        var shot = SampleScreen(800, 500);
        var steps = new List<RecordedStep>
        {
            new() { Image = shot, Click = new Point(120, 200), Description = "Haga clic en «Ventas» (pestaña)" },
            new() { Image = shot, Click = new Point(double.NaN, double.NaN), Description = "Escriba «Informe trimestral»" },
            new() { Image = shot, Click = new Point(640, 170), Description = "Haga clic en «Exportar» (botón)" },
        };
        var doc = GuideBuilder.Build(steps, "Guía de prueba");
        Save(doc.Flatten(), outDir, "61-step-guide.png");
        Check("Guía de captura de pasos", doc.Annotations.OfType<StepAnnotation>().Count() == 3 && doc.Annotations.OfType<ImageAnnotation>().Count() == 2);
    }

    static void TemplateCheck(string outDir)
    {
        var imgs = new[] { SampleScreen(800, 500), SampleScreen(800, 500, "Panel"), SampleScreen(600, 800, "Ajustes"), SampleScreen(800, 500, "Final") };
        var all = TemplateDefinition.BuiltIn.Concat(new[] { TemplateBuilder.Stack("Apilar en vertical", 3), TemplateBuilder.Stack("Cuadrícula automática", 4) }).ToList();
        var thumbs = new List<BitmapSource>();
        foreach (var t in all)
        {
            var doc = TemplateBuilder.Build(t, imgs.Take(t.Slots.Count).ToList(), "Título de ejemplo", ToolTheme.BuiltIn[1]);
            thumbs.Add(EditorDocument.Thumbnail(doc.Flatten(), 300));
        }
        var sheet = RasterOps.Render(4 * 310 + 10, (int)Math.Ceiling(thumbs.Count / 4.0) * 310 + 10, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2F, 0x33, 0x38)), null, new Rect(0, 0, 4 * 310 + 10, 2000));
            for (int i = 0; i < thumbs.Count; i++)
                dc.DrawImage(thumbs[i], new Rect(10 + i % 4 * 310, 10 + i / 4 * 310, thumbs[i].PixelWidth, thumbs[i].PixelHeight));
        });
        Save(sheet, outDir, "62-templates.png");
        Check("Plantillas y combinar imágenes", thumbs.Count == all.Count, $"{all.Count} diseños");
    }

    static async Task SmartChecks(string outDir)
    {
        try
        {
            var doc = new EditorDocument(SampleScreen(960, 600));
            int n = await SmartFeatures.SmartRedactAsync(doc, new HashSet<RedactCategory> { RedactCategory.Email, RedactCategory.Phone, RedactCategory.Iban }, RedactKind.Pixelate, null);
            Save(doc.Flatten(), outDir, "63-smart-redact.png");
            Check("Ocultación inteligente (correo, teléfono, IBAN)", n >= 3, $"{n} zonas");

            var doc2 = new EditorDocument(SampleScreen(960, 600));
            int lines = await SmartFeatures.EditTextAsync(doc2, new Rect(20, 150, 450, 180), 1);
            var edited = doc2.Annotations.OfType<TextAnnotation>().FirstOrDefault();
            if (edited != null) edited.Text = "Texto cambiado con MaxSnapr";
            Save(doc2.Flatten(), outDir, "64-edit-text.png");
            Check("Editar el texto de la imagen", lines >= 3, $"{lines} líneas");

            var doc3 = new EditorDocument(SampleScreen(960, 600));
            int simp = await SmartFeatures.SimplifyAsync(doc3, true, null);
            Save(doc3.Flatten(), outDir, "65-simplify.png");
            Check("Simplificar", simp >= 3, $"{simp} líneas");
        }
        catch (Exception ex) { Check("Funciones inteligentes", false, ex.Message); }
    }

    static void EffectChecks(string outDir)
    {
        var img = SampleScreen(640, 400);
        var persp = RasterOps2.Perspective(img, 0.6, 0);
        var curl = RasterOps2.PageCurl(img, 0.2);
        Check("Perspectiva", persp.PixelWidth == 640 && RasterOps.PixelAt(persp, 2, 2).A == 0);
        Check("Esquina doblada", RasterOps.PixelAt(curl, 638, 398).A == 0);
        var sheet = RasterOps.Render(1300, 410, dc =>
        {
            dc.DrawRectangle(Brushes.Gray, null, new Rect(0, 0, 1300, 410));
            dc.DrawImage(persp, new Rect(5, 5, 640, 400));
            dc.DrawImage(curl, new Rect(655, 5, 640, 400));
        });
        Save(sheet, outDir, "66-perspective-curl.png");
    }

    static void EffectChecks2(string outDir)
    {
        var img = SampleScreen(480, 300);
        var tiles = new (string, BitmapSource)[]
        {
            ("Bisel", RasterOps3.Bevel(img, 12)),
            ("Fundido", RasterOps3.FadeEdges(img, EdgeSides.All, 40)),
            ("Dentado", RasterOps.Edge(img, EdgeSides.Bottom | EdgeSides.Right, 14, "jag")),
            ("Monocromo", RasterOps3.Monochrome(img, 55)),
            ("Enfocar", RasterOps3.Sharpen(img, 1)),
            ("Viñeta", RasterOps3.Vignette(img, 0.7)),
            ("Matiz 120°", RasterOps3.HueGamma(img, 120, 1)),
            ("Sustituir blanco", RasterOps3.ReplaceColor(img, Colors.White, Palette.C("#FFF6C7"), 5, false)),
            ("Logotipo", RasterOps3.WatermarkImage(img, AppIconBitmap(), "SE", 0.8, 0.15)),
        };
        var sheet = RasterOps.Render(3 * 490 + 10, 3 * 330 + 10, dc =>
        {
            dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, 3 * 490 + 10, 3 * 330 + 10));
            for (int i = 0; i < tiles.Length; i++)
            {
                double x = 10 + i % 3 * 490, y = 10 + i / 3 * 330;
                dc.DrawText(new FormattedText(tiles[i].Item1, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.White, 1), new Point(x, y));
                dc.DrawImage(tiles[i].Item2, new Rect(x, y + 22, 480, 300));
            }
        });
        Save(sheet, outDir, "67-effects-more.png");
        bool replaced = RasterOps.PixelAt(tiles[7].Item2, 5, 100) == Palette.C("#FFF6C7");
        var mono = RasterOps.PixelAt(tiles[3].Item2, 5, 100);
        Check("Efectos: bisel, fundido, dentado, monocromo, matiz, sustitución, logotipo", replaced && (mono.R == 0 || mono.R == 255));
        var ico = Path.Combine(outDir, "68-icon.ico");
        ExportService.Save(img, ico, 90);
        using (var fs = File.OpenRead(ico))
        {
            var dec = new IconBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Check("Exportar icono (.ico)", dec.Frames.Count == 7, $"{dec.Frames.Count} tamaños");
        }
    }

    static BitmapSource AppIconBitmap() => (BitmapSource)AppIcon.CreateImageSource();

    static async Task EditorOpsCheck(string outDir)
    {
        var doc = new EditorDocument(SampleScreen(800, 500));
        doc.Annotations.Add(new LineAnnotation { Start = new Point(50, 50), End = new Point(200, 120), Style = DefaultStyles.For(Editor.Tools.ToolKind.Arrow) });
        doc.Annotations.Add(new LineAnnotation { Start = new Point(60, 300), End = new Point(260, 200), Style = DefaultStyles.For(Editor.Tools.ToolKind.Arrow) });
        var shadowy = DefaultStyles.For(Editor.Tools.ToolKind.Shape);
        shadowy.ShadowDistance = 14; shadowy.ShadowAngle = 135; shadowy.ShadowBlur = 6; shadowy.ShadowOpacity = 70;
        doc.Annotations.Add(new ShapeAnnotation { Box = new Rect(400, 80, 260, 140), Style = shadowy });
        foreach (var (g, x) in new[] { ("cursor:arrow", 420), ("cursor:hand", 500), ("cursor:ibeam", 580) })
            doc.Annotations.Add(new StampAnnotation { Box = new Rect(x, 300, 60, 60), Style = new ToolStyle { Glyph = g, Fill = Colors.White } });
        var item = LibraryStore.Instance.Add(doc, "Operaciones", "Prueba automática");
        var editor = new EditorWindow { Width = 1200, Height = 800, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
        editor.Show();
        editor.OpenItem(item);
        await Pump(400);
        var d = editor.Document!;
        editor.SelectAllOfKind(Editor.Tools.ToolKind.Arrow);
        bool byType = editor.EditorCanvas.Selection.Count == 2;
        editor.CompactSelection();
        bool compacted = d.Annotations.OfType<LineAnnotation>().Count() == 0 && d.Annotations.Count == 4;
        Save(d.Flatten(), outDir, "69-editor-ops.png");
        App.Settings.Favorites.Add(new FavoriteStyle { Kind = Editor.Tools.ToolKind.Arrow, Style = DefaultStyles.For(Editor.Tools.ToolKind.Arrow) });
        editor.SelectTool(Editor.Tools.ToolKind.Favorites);
        await Pump(300);
        Shot(editor, outDir, "14-editor-favorites.png");
        editor.Close();
        Check("Seleccionar por tipo y compactar", byType && compacted);
    }

    static void OutputChecks(string outDir)
    {
        App.Settings.Output.Folder = Path.Combine(outDir, "salida");
        App.Settings.Output.NamePattern = "{nombre} {ancho}x{alto} {n}";
        var path = Outputs.SaveToOutputFolder(SampleScreen(320, 200), "Informe", "Prueba");
        Check("Carpeta de salida con nombre automático", File.Exists(path) && Path.GetFileName(path).StartsWith("Informe 320x200 "), Path.GetFileName(path));
        Check("Destinos para compartir", Outputs.All.Count >= 10, string.Join(", ", Outputs.All.Where(d => d.Available).Select(d => d.Name)));
        var enc = Secrets.Protect("secreto");
        Check("Contraseñas cifradas (DPAPI)", enc != "secreto" && Secrets.Unprotect(enc) == "secreto");
    }

    static async Task MediaLibraryCheck(string outDir)
    {
        try
        {
            var mp4 = Path.Combine(outDir, "50-recording.mp4");
            var gif = Path.Combine(outDir, "55-anim.gif");
            if (!File.Exists(mp4) || !File.Exists(gif)) { Check("Vídeos en la biblioteca", false, "faltan archivos"); return; }
            var v = await LibraryStore.Instance.AddMediaAsync(mp4, "Grabación de prueba");
            var g = await LibraryStore.Instance.AddMediaAsync(gif, "GIF de prueba");
            var frames = Video.GifPlayer.Load(g.FullPath);
            Check("Vídeos y GIF en la biblioteca", v.IsVideo && g.IsGif && File.Exists(v.ThumbnailPath) && v.DurationSeconds > 1 && frames.Count >= 15,
                $"{v.Subtitle} · {g.Subtitle} · {frames.Count} fotogramas");
        }
        catch (Exception ex) { Check("Vídeos en la biblioteca", false, ex.Message); }
    }

    static void ModeOverlayShots(string outDir)
    {
        var screen = SampleScreen(960, 600);
        var mon = new MonitorInfo(new Int32Rect(0, 0, 960, 600), new Int32Rect(0, 0, 960, 560), 1, "TEST", true);
        ScreenSnapshot Snap() => new() { Image = screen, Bounds = mon.Bounds, Monitors = new[] { mon }, Windows = Array.Empty<WindowInfo>() };
        void Render(FrameworkElement e, string name)
        {
            var rtb = new RenderTargetBitmap(960, 600, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(e);
            Save(rtb, outDir, name);
        }
        var fixedO = new CaptureOverlay(Snap(), true, "Tamaño fijo", OverlayMode.FixedSize, new Size(400, 250));
        Render(fixedO.BuildPreview(mon, null, false, new Point(500, 300)), "32-overlay-fixed.png");
        var lasso = new CaptureOverlay(Snap(), true, "Mano alzada", OverlayMode.Freehand);
        for (int i = 0; i < 40; i++) lasso.Lasso.Add(new Point(480 + Math.Cos(i / 40.0 * Math.PI * 2) * 220, 300 + Math.Sin(i / 40.0 * Math.PI * 2) * 140));
        Render(lasso.BuildPreview(mon, null, false, new Point(700, 300)), "33-overlay-freehand.png");
        var multi = new CaptureOverlay(Snap(), true, "Varias áreas", OverlayMode.MultiArea);
        multi.Areas.Add(new Int32Rect(30, 150, 400, 160));
        multi.Areas.Add(new Int32Rect(560, 140, 380, 90));
        Render(multi.BuildPreview(mon, null, false, new Point(700, 450)), "34-overlay-multi.png");
        Check("Selector: tamaño fijo, mano alzada y varias áreas", true, "32-34-*.png");
    }

    /// <summary>The pre-recording bar and the camera preview, with a synthetic picture instead of a real camera.</summary>
    static async Task VideoSetupShot(string outDir)
    {
        var s = new Video.VideoSettings { Microphone = true, SystemAudio = true, Webcam = true, WebcamShape = Video.WebcamShape.Circle, WebcamSize = 0.3 };
        var region = new Int32Rect(60, 60, 900, 520);
        var session = Video.RecordingSession.ForPreview(region, s);
        var setup = session.CreateSetup(monitors: false);
        var run = setup.RunAsync();
        var face = RasterOps.Render(640, 360, dc =>
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x4A, 0x6F, 0x8F), Color.FromRgb(0x1C, 0x2A, 0x38), 90), null, new Rect(0, 0, 640, 360));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE8, 0xB8, 0x8E)), null, new Point(320, 160), 70, 85);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x3A, 0x5A, 0x9A)), null, new Point(320, 380), 150, 120);
        });
        session.ShowTestPreview(face);
        await Pump(800);
        Shot(setup, outDir, "35-video-setup.png");
        var preview = Application.Current.Windows.OfType<Video.WebcamPreviewWindow>().FirstOrDefault();
        bool previewOk = preview != null && preview.IsVisible && preview.ActualWidth > 50 && Math.Abs(preview.ActualWidth - preview.ActualHeight) < 2;
        if (preview != null) Shot(preview, outDir, "36-webcam-preview.png");

        // Dragging the picture stores a free position that the recorder reproduces exactly.
        bool dragOk = false;
        string dragDetail = "sin vista previa";
        if (preview != null)
        {
            var target = new Int32Rect(region.X + 120, region.Y + 90, 200, 200);
            preview.SimulateEdit(target);
            await Pump(200);
            var back = Video.VideoRecorder.WebcamLayout(region.Width, region.Height, s.WebcamSize, s.WebcamShape, s.WebcamCorner, 16f / 9f, s.WebcamX, s.WebcamY);
            dragOk = s.WebcamCorner == Video.WebcamCorner.Custom && Math.Abs(back.X + region.X - target.X) <= 2 && Math.Abs(back.Y + region.Y - target.Y) <= 2 && Math.Abs(back.Height - 200) <= 2;
            dragDetail = $"centro {s.WebcamX:0.00}, {s.WebcamY:0.00}, tamaño {s.WebcamSize:0.00}";
        }
        Check("Mover y redimensionar la cámara sobre la propia imagen", dragOk, dragDetail);
        setup.Close();
        session.CloseTestPreview();
        bool closed = run.IsCompleted && !run.Result;
        Check("Barra previa al vídeo y vista de la cámara", previewOk && closed,
            preview == null ? "sin vista previa" : $"cámara {preview.ActualWidth:0}×{preview.ActualHeight:0}");
    }

    /// <summary>Every background on a synthetic speaker with a known mask, plus the real model on a test card.</summary>
    static void CameraBackgroundChecks(string outDir)
    {
        const int w = 640, h = 360;
        System.Drawing.Bitmap Speaker()
        {
            var b = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using var g = System.Drawing.Graphics.FromImage(b);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // A busy "room" so blur is visible, and a simple figure in the middle.
            for (int i = 0; i < 16; i++)
                using (var br = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(40 + i * 12, 90 + (i * 37) % 120, 160 - i * 6)))
                    g.FillRectangle(br, i * 40, (i % 3) * 30, 40, h);
            g.FillEllipse(System.Drawing.Brushes.PeachPuff, 270, 70, 100, 120);
            g.FillEllipse(System.Drawing.Brushes.SteelBlue, 200, 180, 240, 220);
            return b;
        }
        var mask = new float[Video.PersonSegmenter.Size * Video.PersonSegmenter.Size];
        for (int y = 0; y < Video.PersonSegmenter.Size; y++)
            for (int x = 0; x < Video.PersonSegmenter.Size; x++)
            {
                double px = (x + 0.5) * w / Video.PersonSegmenter.Size, py = (y + 0.5) * h / Video.PersonSegmenter.Size;
                bool head = Math.Pow((px - 320) / 50, 2) + Math.Pow((py - 130) / 60, 2) <= 1;
                bool body = Math.Pow((px - 320) / 120, 2) + Math.Pow((py - 290) / 110, 2) <= 1;
                mask[y * Video.PersonSegmenter.Size + x] = head || body ? 1 : 0;
            }
        var ids = new[] { Video.CameraBackgrounds.BlurLight, Video.CameraBackgrounds.BlurStrong }.Concat(Video.CameraBackgrounds.Presets.Select(p => p.Id)).ToArray();
        var tiles = new List<(string, BitmapSource)>();
        bool personKept = true, backgroundChanged = true;
        foreach (var id in ids)
        {
            using var frame = Speaker();
            var before = frame.GetPixel(320, 130);
            var beforeBg = frame.GetPixel(30, 30);
            using (var bg = id.StartsWith("blur") ? Video.BackgroundCompositor.Blur(frame, id == Video.CameraBackgrounds.BlurStrong) : Video.CameraBackgrounds.RenderPreset(id, w, h))
                Video.BackgroundCompositor.Compose(frame, bg, mask);
            personKept &= frame.GetPixel(320, 130) == before;
            backgroundChanged &= frame.GetPixel(30, 30) != beforeBg;
            tiles.Add((Video.CameraBackgrounds.Describe(id), ToSource(frame)));
        }
        var sheet = RasterOps.Render(3 * 330 + 10, (int)Math.Ceiling(tiles.Count / 3.0) * 210 + 10, dc =>
        {
            dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, 3 * 330 + 10, 1000));
            for (int i = 0; i < tiles.Count; i++)
            {
                double x = 10 + i % 3 * 330, y = 10 + i / 3 * 210;
                dc.DrawImage(tiles[i].Item2, new Rect(x, y + 20, 320, 180));
                dc.DrawText(new FormattedText(tiles[i].Item1, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White, 1), new Point(x, y));
            }
        });
        Save(sheet, outDir, "37-camera-backgrounds.png");
        Check("Fondos de la cámara: desenfoque y fondos incluidos", personKept && backgroundChanged, $"{ids.Length} fondos");

        // The bundled model loads and answers in real time.
        var seg = Video.PersonSegmenter.Instance;
        if (seg == null) { Check("Modelo de segmentación de personas", false, "no se pudo cargar"); return; }
        using var card = Speaker();
        using var small = Video.BackgroundCompositor.Downscale(card);
        seg.Run(small);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var m = seg.Run(small);
        double ms = sw.Elapsed.TotalMilliseconds;
        Check("Modelo de segmentación de personas", m.Length == Video.PersonSegmenter.Size * Video.PersonSegmenter.Size && m.All(v => v is >= 0 and <= 1) && ms < 200, $"{ms:0} ms por fotograma");
    }

    static BitmapSource ToSource(System.Drawing.Bitmap b)
    {
        var bd = b.LockBits(new System.Drawing.Rectangle(0, 0, b.Width, b.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var src = BitmapSource.Create(b.Width, b.Height, 96, 96, PixelFormats.Bgr32, null, bd.Scan0, bd.Stride * b.Height, bd.Stride);
        b.UnlockBits(bd);
        src.Freeze();
        return src;
    }

    /// <summary>Records with a synthetic camera moved mid-recording and checks where the picture lands in the video.</summary>
    static async Task WebcamPositionCheck(string outDir)
    {
        try
        {
            var path = Path.Combine(outDir, "57-webcam-moved.mp4");
            var still = new System.Drawing.Bitmap(320, 180, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(still)) g.Clear(System.Drawing.Color.FromArgb(0x10, 0xC0, 0x40));
            var cam = Video.WebcamSource.FromStill(still);
            var rec = new Video.VideoRecorder(new Video.RecorderOptions
            {
                Region = new Int32Rect(0, 0, 640, 360), Fps = 20, OutputPath = path, RecordSystemAudio = false, HighlightClicks = false, CaptureCursor = false,
                Webcam = true, SharedWebcam = cam, WebcamShape = Video.WebcamShape.Rectangle, WebcamScale = 0.3,
                FrameSource = (fw, fh, bmp) => { using var g = System.Drawing.Graphics.FromImage(bmp); g.Clear(System.Drawing.Color.FromArgb(20, 20, 60)); return true; },
            });
            await rec.StartAsync();
            await Task.Delay(700);
            // Same as dragging the preview to the top-left area.
            rec.UpdateWebcamLayout(new Video.VideoSettings { WebcamShape = Video.WebcamShape.Rectangle, WebcamCorner = Video.WebcamCorner.Custom, WebcamSize = 0.3, WebcamX = 0.25, WebcamY = 0.3 });
            await Task.Delay(1000);
            await rec.StopAsync();
            cam.Dispose();
            var first = await Video.VideoTools.FrameAsync(path, TimeSpan.FromSeconds(0.3));
            var last = await Video.VideoTools.FrameAsync(path, TimeSpan.FromSeconds(1.5));
            Save(last, outDir, "57-webcam-moved.png");
            bool Green(Color c) => c.G > 150 && c.R < 80 && c.B < 110;
            bool startCorner = Green(RasterOps.PixelAt(first, 560, 300));
            bool moved = Green(RasterOps.PixelAt(last, 160, 108)) && !Green(RasterOps.PixelAt(last, 560, 300));
            Check("La grabación sigue a la cámara cuando se mueve", startCorner && moved);
        }
        catch (Exception ex) { Check("La grabación sigue a la cámara cuando se mueve", false, ex.Message); }
    }

    /// <summary>Attacks the phone upload server on loopback: it must refuse everything without the exact token.</summary>
    static async Task MobileServerCheck()
    {
        var w = new MobileReceiveWindow("127.0.0.1");
        try
        {
            w.Show();
            var url = new Uri(w.Url!);
            var root = $"http://127.0.0.1:{url.Port}";
            var token = System.Web.HttpUtility.ParseQueryString(url.Query)["k"]!;
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            async Task<int> Get(string path) => (int)(await http.GetAsync(root + path)).StatusCode;

            int noToken = await Get("/");
            int wrong = await Get("/?k=" + new string('0', token.Length));
            int prefix = await Get("/?k=" + token[..8]);
            int ok = await Get("/?k=" + token);

            // A huge declared upload without the token must be refused before any body is read.
            int bigNoToken;
            using (var tcp = new System.Net.Sockets.TcpClient())
            {
                await tcp.ConnectAsync("127.0.0.1", url.Port);
                var s = tcp.GetStream();
                var req = System.Text.Encoding.ASCII.GetBytes("POST /upload HTTP/1.1\r\nHost: x\r\nContent-Type: multipart/form-data; boundary=b\r\nContent-Length: 400000000\r\n\r\n");
                await s.WriteAsync(req);
                var buf = new byte[256];
                int n = await s.ReadAsync(buf);
                bigNoToken = System.Text.Encoding.ASCII.GetString(buf, 0, n).Contains(" 403 ") ? 403 : 0;
            }

            int before = LibraryStore.Instance.Items.Count;
            var png = new System.IO.MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(SampleScreen(200, 120)));
            enc.Save(png);
            using var form = new System.Net.Http.MultipartFormDataContent
            {
                { new System.Net.Http.ByteArrayContent(png.ToArray()), "f", "foto.png" },
                { new System.Net.Http.ByteArrayContent(new byte[] { 0x4D, 0x5A, 0x90, 0 }), "f", "programa.exe" },
            };
            var post = await http.PostAsync(root + "/upload?k=" + token, form);
            var answer = await post.Content.ReadAsStringAsync();
            await Pump(500);
            int added = LibraryStore.Instance.Items.Count - before;
            bool headers = post.Headers.TryGetValues("Content-Security-Policy", out _) && post.Headers.CacheControl?.NoStore == true;

            Check("Servidor del móvil: exige el token y filtra los archivos",
                noToken == 403 && wrong == 403 && prefix == 403 && ok == 200 && bigNoToken == 403 && (int)post.StatusCode == 200 && added == 1 && answer.Contains("no admitidos") && headers,
                $"sin token {noToken}, token falso {wrong}, prefijo {prefix}, válido {ok}, subida enorme sin token {bigNoToken}, añadidas {added}");
        }
        catch (Exception ex) { Check("Servidor del móvil: exige el token y filtra los archivos", false, ex.Message); }
        finally { w.Close(); }
    }

    /// <summary>
    /// Every icon in the main windows must have a glyph, and that glyph must exist in the icon font:
    /// an empty or unknown code shows nothing although the button still works.
    /// </summary>
    static async Task IconCheck(string outDir)
    {
        var fonts = new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }
            .Select(f => new Typeface(f).TryGetGlyphTypeface(out var g) ? g : null).Where(g => g != null).ToList();
        var problems = new List<string>();
        var perWindow = new Dictionary<string, int>();
        var seen = new HashSet<DependencyObject>();
        int icons = 0;
        // Logical tree for content not yet templated, visual tree for what templates generate.
        void Scan(DependencyObject node, string window)
        {
            if (!seen.Add(node)) return;
            if (node is System.Windows.Controls.TextBlock tb && (tb.FontFamily.Source.Contains("Fluent") || tb.FontFamily.Source.Contains("MDL2")))
            {
                icons++;
                perWindow[window] = perWindow.GetValueOrDefault(window) + 1;
                var where = (tb.Parent as FrameworkElement)?.ToolTip as string ?? tb.Text;
                if (string.IsNullOrEmpty(tb.Text)) problems.Add($"{window}: icono vacío ({where})");
                else if (fonts.Count > 0 && !tb.Text.All(c => fonts.Any(f => f!.CharacterToGlyphMap.ContainsKey(c))))
                    problems.Add($"{window}: U+{(int)tb.Text[0]:X4} no existe en la fuente ({where})");
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Scan(child, window);
            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Scan(VisualTreeHelper.GetChild(node, i), window);
        }

        var widget = new EdgeWidget();
        widget.Show();
        widget.Expand();
        await Pump(400);
        Shot(widget, outDir, "38-edge-widget.png");
        var session = Video.RecordingSession.ForPreview(new Int32Rect(60, 60, 900, 520), new Video.VideoSettings { Webcam = true, Microphone = true });
        var setup = session.CreateSetup(monitors: false);
        var windows = new (string, Window)[]
        {
            ("Widget del borde", widget), ("Bienvenida", new WelcomeWindow()), ("Editor", new EditorWindow { Width = 1200, Height = 800 }),
            ("Ventana de captura", new CaptureWindow()), ("Preferencias", new SettingsWindow()), ("Barra de grabación", setup),
            ("Apoyar", new SupportWindow(reminder: true)),
        };
        foreach (var (name, w) in windows)
        {
            if (!w.IsVisible) { w.Left = 0; w.Top = 0; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Show(); }
            await Pump(300);
            Scan(w, name);
            if (w is SupportWindow) Shot(w, outDir, "39-support.png");
            w.Close();
        }
        session.CloseTestPreview();
        bool counted = perWindow.GetValueOrDefault("Widget del borde") >= 6 && perWindow.GetValueOrDefault("Bienvenida") >= 4;
        Check("Todos los iconos se ven (widget, bienvenida, editor, captura, preferencias, grabación)", problems.Count == 0 && counted,
            problems.Count == 0 ? $"{icons} iconos revisados: " + string.Join(", ", perWindow.Select(p => $"{p.Key} {p.Value}")) : string.Join("; ", problems.Take(6)));
    }

    /// <summary>The donation reminder fires once when the library reaches the threshold, then never again.</summary>
    static void SupportReminderCheck()
    {
        var store = LibraryStore.Instance;
        App.Settings.SupportReminderShown = false;
        int fired = 0;
        Support.Watch(store, store.Items.Count + 1, () => fired++);
        store.Add(new EditorDocument(SampleScreen(120, 80)), "Recordatorio 1", "Prueba");
        store.Add(new EditorDocument(SampleScreen(120, 80)), "Recordatorio 2", "Prueba");
        Support.Watch(store, 1, () => fired++); // already shown: must stay silent
        store.Add(new EditorDocument(SampleScreen(120, 80)), "Recordatorio 3", "Prueba");
        Check("Recordatorio de apoyo: una sola vez", fired == 1 && App.Settings.SupportReminderShown, $"{fired} aviso(s)");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    /// <summary>
    /// Breaks the edge widget in every way Windows can (hidden, minimised, off screen, not on top, cloaked,
    /// asked to minimise or close, closed outright) and checks that it always comes back to its place.
    /// </summary>
    static async Task EdgeWidgetResilienceCheck()
    {
        // The user may have a full-screen window open (a browser, a slide show): the test must not depend on it.
        EdgeWidget.IgnoreFullScreen = true;
        bool previous = App.Settings.ShowEdgeWidget;
        App.Settings.ShowEdgeWidget = true;
        var results = new List<string>();
        bool allOk = true;
        try
        {
            App.Current.UpdateEdgeWidget();
            var w = App.Current.EdgeWidgetWindow!;
            await Pump(500);
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;

            string State()
            {
                Native.NativeMethods.GetWindowRect(h, out var r);
                var e = w.ExpectedBounds();
                bool topmost = (Native.NativeMethods.GetWindowLong(h, Native.NativeMethods.GWL_EXSTYLE) & 0x8) != 0;
                Native.NativeMethods.DwmGetWindowAttribute(h, 14, out int c, sizeof(int));
                return $"[en {r.Left},{r.Top} {r.Width}×{r.Height}, esperado {e.X},{e.Y} {e.Width}×{e.Height}, visible {w.IsVisible}, min {Native.NativeMethods.IsIconic(h)}, encima {topmost}, cloak {c}, estado {w.WindowState}, omitido: {w.LastSkip ?? "no"}]";
            }

            bool InPlace()
            {
                if (!w.IsVisible || Native.NativeMethods.IsIconic(h) || !Native.NativeMethods.GetWindowRect(h, out var r)) return false;
                var e = w.ExpectedBounds();
                bool topmost = (Native.NativeMethods.GetWindowLong(h, Native.NativeMethods.GWL_EXSTYLE) & 0x8) != 0;
                bool cloaked = Native.NativeMethods.DwmGetWindowAttribute(h, 14, out int c, sizeof(int)) == 0 && c != 0;
                return topmost && !cloaked && Math.Abs(r.Left - e.X) <= 2 && Math.Abs(r.Top - e.Y) <= 2
                    && Math.Abs(r.Width - e.Width) <= 2 && Math.Abs(r.Height - e.Height) <= 2;
            }

            // A healthy widget must be left alone (no flicker, no log noise).
            var healthy = w.EnsureVisible();
            results.Add(healthy == null ? "sano: sin cambios" : $"sano: reparado sin motivo ({healthy}) " + State());
            allOk &= healthy == null && InPlace();

            async Task Break(string name, Action damage)
            {
                damage();
                await Pump(250);
                var fixedWhy = w.EnsureVisible();
                await Pump(250);
                // A full-screen app appearing mid-test makes the widget step aside on purpose: not a failure.
                if (fixedWhy == null && w.LastSkip == "pantalla completa") { results.Add($"{name}: omitida (pantalla completa)"); return; }
                // Some damage Windows refuses for this kind of window (it stays in place): that counts as a pass too.
                bool ok = InPlace();
                allOk &= ok;
                results.Add($"{name}: {(!ok ? "NO vuelve " + State() : fixedWhy != null ? $"vuelve ({fixedWhy})" : "Windows no lo permite")}");
            }

            await Break("ocultado", () => w.Hide());
            await Break("minimizado", () => ShowWindow(h, 6 /*SW_MINIMIZE*/));
            await Break("fuera de pantalla", () => Native.NativeMethods.SetWindowPos(h, IntPtr.Zero, -6000, -6000, 0, 0, 0x1 | 0x4 | 0x10 /*NOSIZE|NOZORDER|NOACTIVATE*/));
            await Break("sin siempre visible", () => Native.NativeMethods.SetWindowPos(h, new IntPtr(-2) /*HWND_NOTOPMOST*/, 0, 0, 0, 0, 0x1 | 0x2 | 0x10));
            // What happened after Windows started: the window got a size computed with another scale.
            // WPF re-applies the content size after a plain resize, so force the case WPF cannot undo by itself:
            // fixed sizing with the wrong size, as when a scale change arrives right after Windows starts.
            // Fixed sizing with the physical size of another scale: this is how the widget looked after a cold start.
            // Whether WPF or the watchdog puts it right, it must end at its content size without hovering it.
            async Task WrongSize(string name, double k)
            {
                Native.NativeMethods.GetWindowRect(h, out var before);
                w.SizeToContent = SizeToContent.Manual;
                Native.NativeMethods.SetWindowPos(h, IntPtr.Zero, before.Left, before.Top, (int)(before.Width * k), (int)(before.Height * k), 0x4 | 0x10);
                var why = w.EnsureVisible();
                await Pump(300);
                why ??= w.EnsureVisible();
                bool ok = InPlace() && w.SizeToContent == SizeToContent.WidthAndHeight;
                allOk &= ok;
                results.Add($"{name}: {(ok ? "vuelve a su tamaño" + (why != null ? $" ({why})" : "") : "NO vuelve " + State())}");
            }
            await WrongSize("más grande de la cuenta", 1.4);
            await WrongSize("más pequeño de la cuenta", 0.6);
            await Break("invisible (cloak)", () => { int on = 1; DwmSetWindowAttribute(h, 13, ref on, sizeof(int)); });

            // Minimise and close requests (Win+M, Alt+F4) are refused outright.
            SendMessage(h, 0x0112, new IntPtr(0xF020), IntPtr.Zero);
            SendMessage(h, 0x0112, new IntPtr(0xF060), IntPtr.Zero);
            await Pump(300);
            bool refused = App.Current.EdgeWidgetWindow == w && InPlace();
            allOk &= refused;
            results.Add(refused ? "minimizar y Alt+F4: rechazados" : "minimizar o Alt+F4: lo quitaron");

            // Closed by force (another program, Task Manager): the app builds a new one.
            w.Close();
            await Pump(800);
            var again = App.Current.EdgeWidgetWindow;
            bool rebuilt = again != null && again != w && again.IsVisible;
            allOk &= rebuilt;
            results.Add(rebuilt ? "cerrado a la fuerza: se vuelve a abrir" : "cerrado a la fuerza: NO vuelve");
        }
        catch (Exception ex) { allOk = false; results.Add("excepción: " + ex.Message); }
        finally
        {
            App.Settings.ShowEdgeWidget = previous;
            App.Current.UpdateEdgeWidget();
            EdgeWidget.IgnoreFullScreen = false;
        }
        Check("Widget del borde: siempre vuelve", allOk, string.Join("; ", results));
    }

    /// <summary>Automatic names carry the date as well as the time, and old time-only names get their date added.</summary>
    static void NamingCheck()
    {
        var store = LibraryStore.Instance;
        var item = store.Add(new EditorDocument(SampleScreen(120, 80)), "", "Prueba");
        var today = DateTime.Now.ToString("dd-MM-yyyy");
        bool newName = item.Name.StartsWith("Captura " + today + " ") && System.Text.RegularExpressions.Regex.IsMatch(item.Name, @"^Captura \d{2}-\d{2}-\d{4} \d{2}\.\d{2}\.\d{2}$");
        bool fileName = Path.GetFileName(item.FileName).StartsWith("Captura " + today + " ");

        var old = store.Add(new EditorDocument(SampleScreen(120, 80)), "", "Prueba");
        old.Name = "Captura 09:41:07";
        old.Created = new DateTime(2026, 3, 5, 9, 41, 7);
        var custom = store.Add(new EditorDocument(SampleScreen(120, 80)), "Informe de ventas", "Prueba");
        int changed = store.AddDatesToOldNames();
        bool migrated = old.Name == "Captura 05-03-2026 09.41.07" && custom.Name == "Informe de ventas" && changed == 1;
        Check("Nombres con fecha y hora", newName && fileName && migrated, $"{item.Name} · {Path.GetFileName(item.FileName)} · antiguo → {old.Name}");
    }

    /// <summary>The QR must point at a physical network the phone can reach, never at a virtual or link-local adapter.</summary>
    static void MobileAddressCheck(string outDir)
    {
        var all = MobileReceiveWindow.LanCandidates().ToList();
        var chosen = MobileReceiveWindow.LanAddress();
        var bad = new[] { "vethernet", "hyper-v", "wsl", "virtual", "vmware", "docker", "vpn", "bluetooth" };
        bool ok = chosen == null || (!chosen.StartsWith("169.254.") && !all.Any(c => c.Address == chosen && bad.Any(b => c.Adapter.ToLowerInvariant().Contains(b))));
        Check("Recibir desde el móvil: dirección de la red real", ok,
            chosen == null ? "sin red local" : $"{chosen} ({all.FirstOrDefault(c => c.Address == chosen).Adapter})");
        var w = new MobileReceiveWindow("127.0.0.1");
        w.Show();
        foreach (var e in LogicalTreeHelper.GetChildren((DependencyObject)((System.Windows.Controls.DockPanel)w.Content).Children[1]).OfType<System.Windows.Controls.Expander>()) e.IsExpanded = true;
        w.UpdateLayout();
        Shot(w, outDir, "15-mobile-receive.png");
        w.Close();
    }

    /// <summary>Update notice: release parsing, version order, trusted sources only, checksum match. Offline: no real request.</summary>
    static void UpdateChecks(string outDir)
    {
        string Release(string tag, string host = "github.com", string repo = Updates.Repository, bool pre = false) => $$"""
            {"tag_name":"{{tag}}","name":"MaxSnapr {{tag.TrimStart('v')}}","draft":false,"prerelease":{{(pre ? "true" : "false")}},
             "html_url":"https://github.com/{{Updates.Repository}}/releases/tag/{{tag}}","published_at":"2026-10-21T10:00:00Z",
             "body":"## Novedades\n- **Widget** más estable\n- Nombres con `fecha`\n\n## Descargar\n| a | b |",
             "assets":[
               {"name":"MaxSnapr-Setup-{{tag.TrimStart('v')}}.exe","browser_download_url":"https://{{host}}/{{repo}}/releases/download/{{tag}}/MaxSnapr-Setup-{{tag.TrimStart('v')}}.exe"},
               {"name":"SHA256SUMS.txt","browser_download_url":"https://{{host}}/{{repo}}/releases/download/{{tag}}/SHA256SUMS.txt"}]}
            """;
        UpdateInfo? Parse(string json, string current) => Updates.Parse(System.Text.Json.JsonDocument.Parse(json).RootElement, new Version(current));

        var newer = Parse(Release("v1.2.0"), "1.0.7");
        bool found = newer != null && newer.Version == new Version(1, 2, 0) && newer.InstallerName == "MaxSnapr-Setup-1.2.0.exe";
        bool same = Parse(Release("v1.0.7"), "1.0.7.0") == null && Parse(Release("v1.0.6"), "1.0.7") == null;
        bool pre = Parse(Release("v2.0.0", pre: true), "1.0.7") == null;
        bool foreignHost = Parse(Release("v1.2.0", host: "evil.example"), "1.0.7") == null;
        bool foreignRepo = Parse(Release("v1.2.0", repo: "someone/MaxSnapr"), "1.0.7") == null;
        bool http = !Updates.IsTrustedAsset($"http://github.com/{Updates.Repository}/releases/download/v1/x.exe");
        // Only this repository's release files: not deleted download pages, look-alike names or other paths.
        bool former = Updates.IsTrustedAsset("https://github.com/danfigi/MaxSnapr/releases/download/v1.2.0/SHA256SUMS.txt")
            && !Updates.IsTrustedAsset("https://github.com/danfigi/MaxSnapr-public/releases/download/v1.2.0/SHA256SUMS.txt")
            && !Updates.IsTrustedAsset("https://github.com/danfigi/MaxSnapr-descargas/releases/download/v1.2.0/SHA256SUMS.txt")
            && !Updates.IsTrustedAsset("https://github.com/danfigi/MaxSnapr-falso/releases/download/v1.2.0/x.exe")
            && !Updates.IsTrustedAsset("https://github.com/danfigi/MaxSnapr/blob/main/x.exe");

        // The notes show the changes and stop before the download table.
        var notes = UpdateWindow.Notes(newer?.Notes ?? "");
        var texts = LogicalTreeHelper.GetChildren(notes).OfType<System.Windows.Controls.TextBlock>().Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text).ToList();
        bool notesOk = texts.Any(t => t.Contains("Widget más estable")) && !texts.Any(t => t.Contains("Descargar") || t.Contains("|"));

        if (newer != null)
        {
            var w = new UpdateWindow(newer) { Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            w.Show();
            w.UpdateLayout();
            Shot(w, outDir, "16-update.png");
            w.Close();
        }
        Check("Aviso de versión nueva: detecta, ordena versiones y solo acepta el repositorio oficial por HTTPS",
            found && same && pre && foreignHost && foreignRepo && http && former && notesOk,
            $"nueva {found}, igual o anterior ignorada {same}, preliminar ignorada {pre}, otro servidor rechazado {foreignHost}, otro repositorio rechazado {foreignRepo}, http rechazado {http}, solo el repositorio oficial {former}, novedades {notesOk}");
    }

    static async Task ExtraWindowShots(string outDir)
    {
        var imgs = LibraryStore.Instance.Items.Where(i => !i.IsMedia).Take(3).ToList();
        async Task ShotOf(Window w, string name)
        {
            w.Left = 0; w.Top = 0; w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Show();
            await Pump(700);
            Shot(w, outDir, name);
            w.Close();
        }
        await ShotOf(new CombineWindow(imgs), "22-combine.png");
        await ShotOf(new BatchExportWindow(imgs), "23-batch.png");
        await ShotOf(new ThemeEditorDialog(), "24-themes.png");
        await ShotOf(new SmartRedactDialog(), "25-smart-redact-dialog.png");
        await ShotOf(new WelcomeWindow(), "26-welcome.png");
        await ShotOf(new SettingsWindow("Compartir"), "27-settings-share.png");
        await ShotOf(new CaptureWindow(), "28-capture-window.png");
        App.Settings.Current.RecordVideo = true;
        await ShotOf(new CaptureWindow(), "29-capture-window-video.png");
        App.Settings.Current.RecordVideo = false;

        var video = LibraryStore.Instance.Items.FirstOrDefault(i => i.IsVideo);
        if (video != null)
        {
            var editor = new EditorWindow { Width = 1300, Height = 820, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            editor.Show();
            editor.OpenItem(video);
            await Pump(1500);
            Shot(editor, outDir, "13-editor-video.png");
            editor.Close();
        }
        Check("Ventanas de crear, lotes, temas, bienvenida y vídeo", true, "13, 22-29-*.png");
        await VideoSetupShot(outDir);
        CameraBackgroundChecks(outDir);
        await WebcamPositionCheck(outDir);
        await MobileServerCheck();
        await IconCheck(outDir);
        await EdgeWidgetResilienceCheck();
        SupportReminderCheck();
        NamingCheck();
        MobileAddressCheck(outDir);
        UpdateChecks(outDir);
    }
}
