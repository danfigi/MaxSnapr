using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaxSnapr.Editor.Model;
using MaxSnapr.Editor.Raster;
using MaxSnapr.Editor.Tools;
using MaxSnapr.Library;
using MaxSnapr.Services;
using MaxSnapr.Views;

namespace MaxSnapr;

/// <summary>
/// Headless-ish verification run (<c>MaxSnapr --selftest DIR</c>): exercises the document model, raster ops,
/// persistence, OCR and renders every window to PNG so the UI can be reviewed without driving the mouse.
/// Use with MAXSNAPR_HOME pointing at a scratch folder.
/// </summary>
public static partial class SelfTest
{
    static readonly List<string> Report = new();
    static int _failures;

    static void Check(string name, bool ok, string detail = "")
    {
        Report.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _failures++;
    }

    public static async Task<int> RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        try
        {
            var baseImg = SampleScreen(960, 600);
            Save(baseImg, outDir, "00-base.png");

            var doc = new EditorDocument(baseImg);
            AddEverything(doc);
            var flat = doc.Flatten();
            Save(flat, outDir, "01-annotations.png");
            Check("Aplanado con todos los objetos", flat.PixelWidth == 960 && flat.PixelHeight == 600, $"{doc.Annotations.Count} objetos");

            // Persistence round trip must reproduce identical pixels.
            var path = Path.Combine(outDir, "roundtrip.snapr");
            doc.Save(path);
            var loaded = EditorDocument.Load(path);
            var a = RasterOps.Read(flat).Data;
            var b = RasterOps.Read(loaded.Flatten()).Data;
            int diff = a.Where((v, i) => Math.Abs(v - b[i]) > 2).Count();
            Check("Guardar y abrir .snapr sin pérdidas", loaded.Annotations.Count == doc.Annotations.Count && diff < a.Length / 1000,
                $"{loaded.Annotations.Count} objetos, {diff} bytes distintos");

            // Undo / redo.
            int before = doc.Annotations.Count;
            doc.Checkpoint();
            doc.Remove(doc.Annotations.Take(3).ToList());
            doc.Undo();
            bool undoOk = doc.Annotations.Count == before;
            doc.Redo();
            Check("Deshacer y rehacer", undoOk && doc.Annotations.Count == before - 3);
            doc.Undo();

            RasterChecks(baseImg, outDir);
            QuickStyleSheet(outDir);

            try
            {
                var text = await OcrService.RecognizeAsync(baseImg);
                Check("OCR de Windows", text.Contains("Informe", StringComparison.OrdinalIgnoreCase) || text.Contains("ventas", StringComparison.OrdinalIgnoreCase),
                    text.Replace(Environment.NewLine, " | ").Substring(0, Math.Min(90, text.Length)));
            }
            catch (Exception ex) { Check("OCR de Windows", false, ex.Message); }

            await VideoChecks(outDir);
            await ExtrasAsync(outDir);
            OverlayShots(baseImg, outDir);

            // Real screen grab: verify geometry only, never store the user's desktop.
            var snap = Capture.ScreenCapturer.Snapshot();
            var vs = Capture.ScreenCapturer.VirtualScreen;
            Check("Captura GDI de la pantalla virtual", snap.Image.PixelWidth == vs.Width && snap.Image.PixelHeight == vs.Height,
                $"{vs.Width}×{vs.Height}, {snap.Monitors.Count} monitor(es), {snap.Windows.Count} ventanas");
            var part = snap.Crop(new Int32Rect(10, 10, 120, 80), includeCursor: true);
            Check("Recorte de la captura con cursor", part.PixelWidth == 120 && part.PixelHeight == 80);
            await WindowShots(doc, outDir);
            await ExtraWindowShots(outDir);
        }
        catch (Exception ex)
        {
            Check("Excepción inesperada", false, ex.ToString());
        }
        File.WriteAllLines(Path.Combine(outDir, "report.txt"), Report);
        return _failures;
    }

    static void AddEverything(EditorDocument doc)
    {
        var red = Palette.C("#E83A3A");
        ToolStyle S(ToolKind k) => DefaultStyles.For(k);
        doc.Annotations.Add(new LineAnnotation { Start = new Point(560, 330), End = new Point(420, 220), Style = S(ToolKind.Arrow) });
        var curved = new LineAnnotation { Start = new Point(80, 520), End = new Point(300, 470), Control = new Point(150, 420), Style = S(ToolKind.Arrow) };
        curved.Style.Curved = true; curved.Style.StartCap = LineCap.Circle; curved.Style.Stroke = Palette.C("#2E7CF6");
        doc.Annotations.Add(curved);
        doc.Annotations.Add(new ShapeAnnotation { Box = new Rect(40, 150, 300, 120), Style = S(ToolKind.Shape) });
        var t = new TextAnnotation { Box = new Rect(600, 40, 320, 40), Text = "Texto con contorno", Style = S(ToolKind.Text) };
        t.FitHeight(1);
        doc.Annotations.Add(t);
        var c = new TextAnnotation { Box = new Rect(600, 330, 300, 60), Text = "Globo con punta orientable", Tail = new Point(560, 470), Style = S(ToolKind.Callout) };
        c.FitHeight(1);
        doc.Annotations.Add(c);
        for (int i = 1; i <= 3; i++) doc.Annotations.Add(new StepAnnotation { Center = new Point(40 + i * 60, 110), Number = i, Style = S(ToolKind.Step) });
        doc.Annotations.Add(new PenAnnotation { Points = Enumerable.Range(0, 30).Select(i => new Point(380 + i * 6, 560 + Math.Sin(i / 3.0) * 14)).ToList(), Style = S(ToolKind.Pen) });
        doc.Annotations.Add(new PenAnnotation { Highlighter = true, Points = new() { new Point(60, 300), new Point(330, 300) }, Style = S(ToolKind.Highlighter) });
        doc.Annotations.Add(new RedactAnnotation { Box = new Rect(620, 150, 260, 30), Style = S(ToolKind.Blur) });
        var blur = S(ToolKind.Blur); blur.Redact = RedactKind.Blur;
        doc.Annotations.Add(new RedactAnnotation { Box = new Rect(620, 190, 260, 30), Style = blur });
        doc.Annotations.Add(new MagnifyAnnotation { Box = new Rect(820, 470, 110, 110), Style = S(ToolKind.Magnify) });
        var stamp = S(ToolKind.Stamp);
        doc.Annotations.Add(new StampAnnotation { Box = new Rect(500, 470, 64, 64), Style = stamp });
        var badge = S(ToolKind.Stamp); badge.Glyph = "NUEVO"; badge.Fill = red;
        doc.Annotations.Add(new StampAnnotation { Box = new Rect(600, 500, 130, 40), Style = badge });
        doc.Annotations.Add(new SpotlightAnnotation { Box = new Rect(30, 140, 320, 140), Style = With(S(ToolKind.Spotlight), s => s.Intensity = 25) });
    }

    static ToolStyle With(ToolStyle s, Action<ToolStyle> f) { f(s); return s; }

    static void RasterChecks(BitmapSource img, string outDir)
    {
        var crop = RasterOps.Crop(img, new Int32Rect(10, 10, 200, 100));
        Check("Recortar", crop.PixelWidth == 200 && crop.PixelHeight == 100);
        var cut = RasterOps.CutOut(img, 100, 300, vertical: true);
        Check("Recortar franja vertical", cut.PixelWidth == img.PixelWidth - 200);
        var cutH = RasterOps.CutOut(img, 50, 80, vertical: false);
        Check("Recortar franja horizontal", cutH.PixelHeight == img.PixelHeight - 30);
        var rot = RasterOps.Rotate(img, 90);
        Check("Girar 90°", rot.PixelWidth == img.PixelHeight && rot.PixelHeight == img.PixelWidth);
        var border = RasterOps.Border(img, 10, Colors.Black);
        Check("Borde", border.PixelWidth == img.PixelWidth + 20);
        var shadow = RasterOps.Shadow(img, 16, 6, Color.FromArgb(120, 0, 0, 0));
        Check("Sombra", shadow.PixelWidth > img.PixelWidth);
        var torn = RasterOps.Edge(img, EdgeSides.Bottom | EdgeSides.Right, 14, "torn");
        Check("Bordes rasgados", RasterOps.PixelAt(torn, img.PixelWidth - 1, img.PixelHeight - 1).A == 0);
        var gray = RasterOps.Grayscale(img);
        var px = RasterOps.PixelAt(gray, 5, 5);
        Check("Escala de grises", px.R == px.G && px.G == px.B);
        var filled = RasterOps.FloodFill(img, 3, 3, Colors.Lime, 10);
        Check("Relleno", RasterOps.PixelAt(filled, 3, 3) == Colors.Lime);
        var resized = RasterOps.Resize(img, 480, 300);
        Check("Cambiar tamaño", resized.PixelWidth == 480 && resized.PixelHeight == 300);

        var sheet = RasterOps.Render(980, 640, dc =>
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 980, 640));
            var tiles = new (string, BitmapSource)[] { ("Sombra", shadow), ("Rasgado", torn), ("Grises", gray), ("Franja", cut), ("Sepia", RasterOps.Sepia(img)), ("Pixelar", RasterOps.Obscure(img, true, 60)) };
            for (int i = 0; i < tiles.Length; i++)
            {
                var (name, t) = tiles[i];
                double x = 10 + (i % 3) * 320, y = 10 + (i / 3) * 315;
                double s = Math.Min(300.0 / t.PixelWidth, 280.0 / t.PixelHeight);
                dc.DrawImage(t, new Rect(x, y + 22, t.PixelWidth * s, t.PixelHeight * s));
                dc.DrawText(new FormattedText(name, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.Black, 1), new Point(x, y));
            }
        });
        Save(sheet, outDir, "02-effects.png");
    }

    static void QuickStyleSheet(string outDir)
    {
        var kinds = new[] { ToolKind.Arrow, ToolKind.Text, ToolKind.Callout, ToolKind.Shape, ToolKind.Step, ToolKind.Highlighter, ToolKind.Blur, ToolKind.Magnify, ToolKind.Stamp, ToolKind.Pen };
        var sheet = RasterOps.Render(12 * 70 + 10, kinds.Length * 56 + 10, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(Palette.C("#2F3338")), null, new Rect(0, 0, 12 * 70 + 10, kinds.Length * 56 + 10));
            for (int r = 0; r < kinds.Length; r++)
            {
                var styles = DefaultStyles.QuickStyles(kinds[r]).Take(12).ToList();
                for (int i = 0; i < styles.Count; i++)
                {
                    var img = PropertiesPanel.Preview(kinds[r], styles[i]);
                    dc.DrawRoundedRectangle(new SolidColorBrush(Palette.C("#24272B")), null, new Rect(8 + i * 70, 8 + r * 56, 64, 50), 6, 6);
                    dc.DrawImage(img, new Rect(10 + i * 70, 10 + r * 56, 60, 45));
                }
            }
        });
        Save(sheet, outDir, "03-quickstyles.png");
        Check("Vistas previas de estilos rápidos", true, $"{kinds.Length} herramientas");
    }

    static async Task WindowShots(EditorDocument doc, string outDir)
    {
        var item = LibraryStore.Instance.Add(doc, "Informe de ventas (prueba)", "Prueba automática");
        item.Tags = new() { "prueba", "ventas" };
        LibraryStore.Instance.Add(new EditorDocument(SampleScreen(800, 500, "Panel de control")), "Panel de control", "Explorador");
        LibraryStore.Instance.Add(new EditorDocument(SampleScreen(640, 900, "Configuración")), "Configuración", "Ajustes");

        await ToolInteractions(outDir);

        foreach (var theme in new[] { "Dark", "Light" })
        {
            ThemeService.Apply(theme);
            var editor = new EditorWindow { Width = 1400, Height = 900, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            editor.Show();
            editor.OpenItem(item);
            await Pump(600);
            editor.SelectTool(ToolKind.Callout);
            // The editor reloads the item from disk, so select from its own document instance.
            editor.EditorCanvas.Select(editor.EditorCanvas.Document?.Annotations.OfType<TextAnnotation>().FirstOrDefault(t => t.Style.Callout != CalloutKind.None));
            await Pump(400);
            Shot(editor, outDir, $"10-editor-{theme}.png");

            editor.SelectTool(ToolKind.Crop);
            await Pump(300);
            Shot(editor, outDir, $"11-editor-crop-{theme}.png");

            editor.ShowLibrary();
            await Pump(500);
            Shot(editor, outDir, $"12-library-{theme}.png");
            editor.HideLibrary();
            editor.SaveCurrent();
            editor.Close();
        }
        ThemeService.Apply("Dark");

        var cw = new CaptureWindow { Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
        cw.Show();
        await Pump(400);
        Shot(cw, outDir, "20-capture-window.png");
        cw.Close();

        var sw = new SettingsWindow { Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
        sw.Show();
        await Pump(400);
        Shot(sw, outDir, "21-settings.png");
        sw.Close();
        Check("Capturas de las ventanas", true, "10-21-*.png");
    }

    /// <summary>Drives every tool through the same entry points the mouse uses.</summary>
    static async Task ToolInteractions(string outDir)
    {
        var doc = new EditorDocument(SampleScreen(800, 500));
        var item = LibraryStore.Instance.Add(doc, "Interacciones", "Prueba automática");
        var editor = new EditorWindow { Width = 1200, Height = 800, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
        editor.Show();
        editor.OpenDocument(doc, item);
        await Pump(300);
        var canvas = editor.EditorCanvas;
        var none = System.Windows.Input.ModifierKeys.None;

        void Drag(ToolKind kind, Point a, Point b, System.Windows.Input.ModifierKeys mods = default)
        {
            editor.SelectTool(kind);
            var t = canvas.Tool;
            t.OnDown(a, mods, 1);
            for (int i = 1; i <= 8; i++) t.OnMove(a + (b - a) * (i / 8.0), mods, true);
            t.OnUp(b, mods);
            canvas.EndTextEdit();
        }

        int n0 = doc.Annotations.Count;
        Drag(ToolKind.Arrow, new Point(100, 400), new Point(300, 300));
        Drag(ToolKind.Shape, new Point(420, 60), new Point(700, 160));
        Drag(ToolKind.Blur, new Point(420, 200), new Point(700, 240));
        Drag(ToolKind.Highlighter, new Point(40, 230), new Point(330, 230), System.Windows.Input.ModifierKeys.Shift);
        Drag(ToolKind.Pen, new Point(500, 420), new Point(700, 470));
        Drag(ToolKind.Magnify, new Point(600, 300), new Point(700, 400));
        Drag(ToolKind.Spotlight, new Point(30, 160), new Point(350, 320));
        Drag(ToolKind.Step, new Point(60, 120), new Point(60, 120));
        Drag(ToolKind.Step, new Point(110, 120), new Point(110, 120));
        Drag(ToolKind.Stamp, new Point(380, 430), new Point(380, 430));
        Drag(ToolKind.Line, new Point(30, 480), new Point(330, 480));
        Check("Herramientas de dibujo crean objetos", doc.Annotations.Count == n0 + 11, $"{doc.Annotations.Count - n0} objetos nuevos");
        var steps = doc.Annotations.OfType<StepAnnotation>().Select(s => s.Number).ToList();
        Check("Pasos numerados consecutivos", steps.SequenceEqual(new[] { 1, 2 }), string.Join(",", steps));

        // Text: click creates a box in edit mode; type and commit.
        editor.SelectTool(ToolKind.Text);
        canvas.Tool.OnDown(new Point(430, 330), none, 1);
        canvas.Tool.OnUp(new Point(430, 330), none);
        var text = doc.Annotations.OfType<TextAnnotation>().LastOrDefault();
        Check("Texto entra en modo edición", canvas.IsEditingText && text != null);
        if (text != null) text.Text = "Hola";
        canvas.EndTextEdit();

        // Select tool: move the arrow by dragging its body.
        var arrow = doc.Annotations.OfType<LineAnnotation>().First();
        var before = arrow.Start;
        editor.SelectTool(ToolKind.Select);
        var mid = new Point((arrow.Start.X + arrow.End.X) / 2, (arrow.Start.Y + arrow.End.Y) / 2);
        canvas.Tool.OnDown(mid, none, 1);
        canvas.Tool.OnMove(mid + new Vector(20, 10), none, true);
        canvas.Tool.OnUp(mid + new Vector(20, 10), none);
        Check("Mover objetos con la herramienta Mover", arrow.Start == before + new Vector(20, 10));
        doc.Undo();
        arrow = doc.Annotations.OfType<LineAnnotation>().First();
        Check("Deshacer el movimiento", arrow.Start == before);

        Save(doc.Flatten(), outDir, "40-interactions.png");

        // Raster tools.
        int w0 = doc.Width;
        editor.SelectTool(ToolKind.CutOut);
        ((ToolBase)canvas.Tool).Style.Vertical = true;
        canvas.Tool.OnDown(new Point(350, 10), none, 1);
        canvas.Tool.OnMove(new Point(400, 10), none, true);
        canvas.Tool.OnUp(new Point(400, 10), none);
        Check("Recortar franja (herramienta)", doc.Width == w0 - 50, $"{w0} → {doc.Width}");

        editor.SelectTool(ToolKind.Fill);
        canvas.Tool.OnDown(new Point(5, 495), none, 1);
        Check("Relleno (herramienta)", Editor.Raster.RasterOps.PixelAt(doc.Base, 5, 495) == ((ToolBase)canvas.Tool).Style.Fill);

        editor.SelectTool(ToolKind.Selection);
        canvas.Tool.OnDown(new Point(20, 160), none, 1);
        canvas.Tool.OnMove(new Point(200, 260), none, true);
        canvas.Tool.OnUp(new Point(200, 260), none);
        canvas.Tool.OnDown(new Point(100, 200), none, 1); // press inside lifts the pixels
        Check("Selección: mover píxeles como objeto", doc.Annotations.LastOrDefault() is ImageAnnotation && editor.CurrentKind == ToolKind.Select);

        editor.SelectTool(ToolKind.Eraser);
        canvas.Tool.OnDown(new Point(600, 20), none, 1);
        canvas.Tool.OnMove(new Point(700, 20), none, true);
        canvas.Tool.OnUp(new Point(700, 20), none);
        Check("Borrador", Editor.Raster.RasterOps.PixelAt(doc.Base, 650, 20).A == 0);

        int h0 = doc.Height;
        editor.SelectTool(ToolKind.Crop);
        canvas.Tool.OnDown(new Point(50, 50), none, 1);
        canvas.Tool.OnMove(new Point(450, 350), none, true);
        canvas.Tool.OnUp(new Point(450, 350), none);
        canvas.Tool.OnKey(System.Windows.Input.Key.Enter, none);
        Check("Recortar (herramienta)", doc.Width == 400 && doc.Height == 300, $"{doc.Width}×{doc.Height}");

        Save(doc.Flatten(), outDir, "41-after-raster-tools.png");
        editor.SaveCurrent();
        editor.Close();
    }

    static async Task VideoChecks(string outDir)
    {
        var path = Path.Combine(outDir, "50-recording.mp4");
        int frame = 0;
        var rec = new Video.VideoRecorder(new Video.RecorderOptions
        {
            Region = new Int32Rect(0, 0, 640, 360), Fps = 20, OutputPath = path, RecordSystemAudio = false, HighlightClicks = false,
            // A 440 Hz tone instead of the real microphone or speakers, so the track is known to be audible.
            TestAudio = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.3 },
            // Synthetic moving content: never records the user's screen.
            FrameSource = (w, h, bmp) =>
            {
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.Clear(System.Drawing.Color.FromArgb(30, 40, 60));
                int x = (frame++ * 12) % (w - 80);
                g.FillEllipse(System.Drawing.Brushes.Orange, x, h / 2 - 40, 80, 80);
                g.DrawString($"Fotograma {frame}", new System.Drawing.Font("Segoe UI", 20), System.Drawing.Brushes.White, 20, 20);
                return true;
            },
        });
        try
        {
            await rec.StartAsync();
            await Task.Delay(1500);
            rec.Pause();
            await Task.Delay(500);
            rec.Resume();
            await Task.Delay(1000);
            bool ok = await rec.StopAsync();
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var props = await file.Properties.GetVideoPropertiesAsync();
            Check("Grabación de vídeo MP4 (H.264 + audio)", ok && props.Width == 640 && props.Height == 360 && props.Duration.TotalSeconds is > 1.8 and < 3.5,
                $"{props.Width}×{props.Height}, {props.Duration.TotalSeconds:0.0} s, {rec.FramesCaptured} fotogramas, {new FileInfo(path).Length / 1024} KB");
        }
        catch (Exception ex) { Check("Grabación de vídeo MP4 (H.264 + audio)", false, ex.Message); return; }

        try
        {
            var (rms, seconds) = AudioRms(path);
            Check("El vídeo lleva sonido audible (AAC)", rms > 0.08 && seconds > 1.8, $"RMS {rms:0.000} durante {seconds:0.0} s");
        }
        catch (Exception ex) { Check("El vídeo lleva sonido audible (AAC)", false, ex.Message); }

        try
        {
            var still = await Video.VideoTools.FrameAsync(path, TimeSpan.FromSeconds(1));
            Save(still, outDir, "51-video-frame.png");
            Check("Extraer fotograma", still.PixelWidth == 640);

            var trimmed = Path.Combine(outDir, "52-trim.mp4");
            await Video.VideoTools.TrimAsync(path, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5), trimmed);
            var ti = await Video.VideoTools.GetInfoAsync(trimmed);
            Check("Recortar vídeo", Math.Abs(ti.Duration.TotalSeconds - 1) < 0.3, $"{ti.Duration.TotalSeconds:0.00} s");

            var cut = Path.Combine(outDir, "53-cut.mp4");
            await Video.VideoTools.CutAsync(path, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5), cut);
            var ci = await Video.VideoTools.GetInfoAsync(cut);
            var oi = await Video.VideoTools.GetInfoAsync(path);
            Check("Cortar un tramo", Math.Abs(ci.Duration.TotalSeconds - (oi.Duration.TotalSeconds - 1)) < 0.35, $"{oi.Duration.TotalSeconds:0.00} → {ci.Duration.TotalSeconds:0.00} s");

            var joined = Path.Combine(outDir, "54-concat.mp4");
            await Video.VideoTools.ConcatAsync(new[] { trimmed, trimmed }, joined);
            var ji = await Video.VideoTools.GetInfoAsync(joined);
            Check("Combinar vídeos", Math.Abs(ji.Duration.TotalSeconds - 2 * ti.Duration.TotalSeconds) < 0.3, $"{ji.Duration.TotalSeconds:0.00} s");

            var gifPath = Path.Combine(outDir, "55-anim.gif");
            await Video.VideoTools.ToGifAsync(path, TimeSpan.Zero, TimeSpan.FromSeconds(2), 10, 480, gifPath);
            using (var gs = File.OpenRead(gifPath))
            {
                var dec = new GifBitmapDecoder(gs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check("Crear GIF animado", dec.Frames.Count >= 15 && dec.Frames[0].PixelWidth == 480, $"{dec.Frames.Count} fotogramas, {new FileInfo(gifPath).Length / 1024} KB");
            }

            var imgs = new[] { Path.Combine(outDir, "00-base.png"), Path.Combine(outDir, "01-annotations.png") };
            var slides = Path.Combine(outDir, "56-from-images.mp4");
            await Video.VideoTools.ImagesToVideoAsync(imgs, TimeSpan.FromSeconds(1), slides);
            var si = await Video.VideoTools.GetInfoAsync(slides);
            Check("Vídeo a partir de imágenes", Math.Abs(si.Duration.TotalSeconds - 2) < 0.3, $"{si.Duration.TotalSeconds:0.00} s, {si.Width}×{si.Height}");
        }
        catch (Exception ex) { Check("Edición de vídeo", false, ex.ToString()); }
    }

    static void OverlayShots(BitmapSource screen, string outDir)
    {
        var mon = new Capture.MonitorInfo(new Int32Rect(0, 0, screen.PixelWidth, screen.PixelHeight), new Int32Rect(0, 0, screen.PixelWidth, screen.PixelHeight - 40), 1, "TEST", true);
        var win = new Capture.WindowInfo(IntPtr.Zero, new Int32Rect(20, 150, 520, 200), "Tabla", "Test", 0);
        Capture.ScreenSnapshot Snap() => new() { Image = screen, Bounds = mon.Bounds, Monitors = new[] { mon }, Windows = new[] { win } };
        void Render(FrameworkElement e, string name)
        {
            var rtb = new RenderTargetBitmap(screen.PixelWidth, screen.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(e);
            Save(rtb, outDir, name);
        }
        Render(new Capture.CaptureOverlay(Snap(), false, "Captura").BuildPreview(mon, null, false, new Point(300, 260)), "30-overlay-hover.png");
        Render(new Capture.CaptureOverlay(Snap(), false, "Captura").BuildPreview(mon, new Int32Rect(560, 130, 360, 120), true, new Point(700, 200)), "31-overlay-selection.png");
        Check("Selector de captura (vista previa)", true, "30-31-*.png");
    }

    /// <summary>Decodes the audio track with Media Foundation and returns its RMS level and length.</summary>
    static (double rms, double seconds) AudioRms(string path)
    {
        using var reader = new NAudio.Wave.MediaFoundationReader(path);
        var sp = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader);
        var buf = new float[48000];
        double sum = 0;
        long n = 0;
        int read;
        while ((read = sp.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < read; i++) sum += buf[i] * (double)buf[i];
            n += read;
        }
        return (n == 0 ? 0 : Math.Sqrt(sum / n), n / (double)sp.WaveFormat.Channels / sp.WaveFormat.SampleRate);
    }

    static async Task Pump(int ms)
    {
        await Task.Delay(ms);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    static void Shot(Window w, string dir, string name)
    {
        var content = (FrameworkElement)w.Content;
        double dpi = VisualTreeHelper.GetDpi(w).PixelsPerInchX;
        var rtb = new RenderTargetBitmap((int)(content.ActualWidth * dpi / 96), (int)(content.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)w.FindResource("Bg.Window"), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        rtb.Render(dv);
        Save(rtb, dir, name);
    }

    static void Save(BitmapSource img, string dir, string name)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(Path.Combine(dir, name));
        enc.Save(fs);
    }

    /// <summary>Synthetic "application window" with readable text for OCR and visual checks.</summary>
    public static BitmapSource SampleScreen(int w, int h, string title = "Informe de ventas")
    {
        return RasterOps.Render(w, h, dc =>
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new SolidColorBrush(Palette.C("#F3F4F6")), null, new Rect(0, 0, w, 48));
            var tf = new Typeface("Segoe UI");
            FormattedText T(string s, double size, Brush b) => new(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, b, 1);
            dc.DrawText(T(title, 22, Brushes.Black), new Point(20, 10));
            var lines = new[] { "Trimestre   Región   Ventas   Objetivo", "T1   Norte   12.450 €   11.000 €", "T2   Sur   9.870 €   10.500 €", "T3   Este   15.320 €   13.000 €", "T4   Oeste   8.110 €   9.000 €" };
            for (int i = 0; i < lines.Length; i++) dc.DrawText(T(lines[i], 16, i == 0 ? Brushes.DimGray : Brushes.Black), new Point(40, 170 + i * 30));
            for (int i = 0; i < 6; i++)
            {
                double bh = 40 + (i * 37 % 120);
                dc.DrawRectangle(new SolidColorBrush(Palette.C(i % 2 == 0 ? "#2E7CF6" : "#14B8C4")), null, new Rect(w - 360 + i * 50, h - 80 - bh, 36, bh));
            }
            dc.DrawText(T("Correo: ana.garcia@example.com   Tel.: 600 123 456", 14, Brushes.Black), new Point(w - 380, 160));
            dc.DrawText(T("Cuenta: ES12 3456 7890 1234 5678", 14, Brushes.Black), new Point(w - 380, 200));
        });
    }
}
