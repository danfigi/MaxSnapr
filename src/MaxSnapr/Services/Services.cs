using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxSnapr.Native;

namespace MaxSnapr.Services;

public static class ClipboardService
{
    public static void SetImage(BitmapSource img)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                // PNG keeps transparency for apps that understand it; the bitmap is the universal fallback.
                var data = new DataObject();
                data.SetImage(img);
                var ms = new MemoryStream();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                enc.Save(ms);
                data.SetData("PNG", ms, false);
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(60); // clipboard briefly locked by another process
            }
        }
    }

    public static void SetText(string text)
    {
        try { Clipboard.SetText(text); } catch (Exception ex) { Log.Error("Portapapeles", ex); }
    }

    public static BitmapSource? GetImage()
    {
        try
        {
            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream ms)
                return BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (Clipboard.ContainsImage()) return Clipboard.GetImage();
            if (Clipboard.ContainsFileDropList())
                foreach (var f in Clipboard.GetFileDropList())
                    if (ExportService.IsImageFile(f)) return ExportService.LoadImage(f!);
        }
        catch (Exception ex) { Log.Error("Pegar desde el portapapeles", ex); }
        return null;
    }
}

public static class ExportService
{
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico" };
    public static bool IsImageFile(string? path) => path != null && ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public const string SaveFilter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|BMP (*.bmp)|*.bmp|GIF (*.gif)|*.gif|TIFF (*.tif)|*.tif|PDF (*.pdf)|*.pdf|Documento MaxSnapr (*.snapr)|*.snapr|Icono (*.ico)|*.ico";

    public static BitmapSource LoadImage(string path)
    {
        using var fs = File.OpenRead(path);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = dec.Frames[0];
        frame.Freeze();
        return frame;
    }

    public static void Save(BitmapSource img, string path, int jpegQuality)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf") { SavePdf(new[] { img }, path, jpegQuality); return; }
        BitmapEncoder enc = ext switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => new PngBitmapEncoder(),
        };
        BitmapSource frame = img;
        if (ext == ".ico") { SaveIco(img, path); return; }
        if (enc is JpegBitmapEncoder or BmpBitmapEncoder)
        {
            // These formats have no alpha: composite on white like other editors do.
            frame = Editor.Raster.RasterOps.Render(img.PixelWidth, img.PixelHeight, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
                dc.DrawImage(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
            });
        }
        int dpi = MaxSnapr.App.Settings.ExportDpi;
        if (dpi != 96) frame = Editor.Raster.RasterOps3.WithDpi(frame, dpi);
        enc.Frames.Add(BitmapFrame.Create(frame));
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) enc.Save(fs);
        File.Move(tmp, path, true);
    }

    /// <summary>Windows icon with PNG-compressed 16–256 px images.</summary>
    static void SaveIco(BitmapSource img, string path)
    {
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (var s in sizes)
        {
            double k = Math.Min(s / (double)img.PixelWidth, s / (double)img.PixelHeight);
            var scaled = Editor.Raster.RasterOps.Render(s, s, dc => dc.DrawImage(img, new Rect((s - img.PixelWidth * k) / 2, (s - img.PixelHeight * k) / 2, img.PixelWidth * k, img.PixelHeight * k)));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(scaled));
            using var ms = new MemoryStream();
            enc.Save(ms);
            pngs.Add(ms.ToArray());
        }
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }

    /// <summary>Minimal PDF writer: one page per image, JPEG-compressed, page size = image size at 96 DPI.</summary>
    public static void SavePdf(IReadOnlyList<BitmapSource> images, string path, int jpegQuality)
    {
        var objects = new List<byte[]>();
        var pageIds = new List<int>();
        int Next() => objects.Count + 1;
        // 1 = catalog, 2 = pages; reserve.
        objects.Add(Array.Empty<byte>());
        objects.Add(Array.Empty<byte>());
        foreach (var img in images)
        {
            var jpg = new JpegBitmapEncoder { QualityLevel = jpegQuality };
            var flat = Editor.Raster.RasterOps.Render(img.PixelWidth, img.PixelHeight, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
                dc.DrawImage(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
            });
            jpg.Frames.Add(BitmapFrame.Create(flat));
            var ms = new MemoryStream();
            jpg.Save(ms);
            var data = ms.ToArray();
            double w = img.PixelWidth * 72.0 / 96, h = img.PixelHeight * 72.0 / 96;
            string W = w.ToString("0.##", CultureInfo.InvariantCulture), H = h.ToString("0.##", CultureInfo.InvariantCulture);
            int imgId = Next();
            objects.Add(Concat(Ascii($"<< /Type /XObject /Subtype /Image /Width {img.PixelWidth} /Height {img.PixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {data.Length} >>\nstream\n"), data, Ascii("\nendstream")));
            var content = Ascii($"q {W} 0 0 {H} 0 0 cm /Im0 Do Q");
            int contentId = Next();
            objects.Add(Concat(Ascii($"<< /Length {content.Length} >>\nstream\n"), content, Ascii("\nendstream")));
            int pageId = Next();
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W} {H}] /Resources << /XObject << /Im0 {imgId} 0 R >> >> /Contents {contentId} 0 R >>"));
            pageIds.Add(pageId);
        }
        objects[0] = Ascii("<< /Type /Catalog /Pages 2 0 R >>");
        objects[1] = Ascii($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(i => $"{i} 0 R"))}] /Count {pageIds.Count} >>");
        using var fs = File.Create(path);
        var offsets = new List<long>();
        void Put(byte[] b) => fs.Write(b, 0, b.Length);
        Put(Ascii("%PDF-1.4\n%âãÏÓ\n"));
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(fs.Position);
            Put(Ascii($"{i + 1} 0 obj\n"));
            Put(objects[i]);
            Put(Ascii("\nendobj\n"));
        }
        long xref = fs.Position;
        var sb = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Put(Ascii(sb.ToString()));
    }

    static byte[] Ascii(string s) => Encoding.Latin1.GetBytes(s);
    static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <param name="quick">Print to the default printer without showing the dialog.</param>
    public static void Print(BitmapSource img, string title, bool quick = false, bool fitToPage = true, bool landscape = false)
    {
        var dlg = new PrintDialog();
        if (landscape) dlg.PrintTicket.PageOrientation = System.Printing.PageOrientation.Landscape;
        if (!quick && dlg.ShowDialog() != true) return;
        double pw = dlg.PrintableAreaWidth, ph = dlg.PrintableAreaHeight;
        // Image pixels at 96 DPI map 1:1 to WPF units; "fit" scales down (never up) to the page.
        double s = fitToPage ? Math.Min(1, Math.Min(pw / img.PixelWidth, ph / img.PixelHeight)) : 1;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawImage(img, new Rect(Math.Max(0, (pw - img.PixelWidth * s) / 2), 0, img.PixelWidth * s, img.PixelHeight * s));
        dlg.PrintVisual(dv, title);
    }
}

public static class OcrService
{
    public static async Task<string> RecognizeAsync(BitmapSource img)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows no tiene instalado ningún idioma de OCR. Añada un paquete de idioma con reconocimiento óptico de caracteres en Configuración > Hora e idioma.");
        // Small captures recognise much better when upscaled.
        if (Math.Max(img.PixelWidth, img.PixelHeight) < 1200)
        {
            double s = Math.Min(3, 1200.0 / Math.Max(img.PixelWidth, img.PixelHeight));
            img = Editor.Raster.RasterOps.Resize(img, (int)(img.PixelWidth * s), (int)(img.PixelHeight * s));
        }
        if (img.PixelWidth > Windows.Media.Ocr.OcrEngine.MaxImageDimension || img.PixelHeight > Windows.Media.Ocr.OcrEngine.MaxImageDimension)
        {
            double s = Windows.Media.Ocr.OcrEngine.MaxImageDimension / (double)Math.Max(img.PixelWidth, img.PixelHeight);
            img = Editor.Raster.RasterOps.Resize(img, (int)(img.PixelWidth * s), (int)(img.PixelHeight * s));
        }
        var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        enc.Save(ms);
        using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await ras.WriteAsync(ms.ToArray().AsBuffer());
        ras.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        using var sb = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(sb);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }
}

/// <summary>System-wide hotkeys through a hidden message window.</summary>
public sealed class HotkeyService : IDisposable
{
    readonly HwndSource _source;
    readonly Dictionary<int, Action> _actions = new();
    int _nextId = 1;

    public HotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("MaxSnaprHotkeys") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    public bool Register(Hotkey hk, Action action)
    {
        if (hk.Key == 0) return false;
        uint mods = NativeMethods.MOD_NOREPEAT;
        if (hk.Ctrl) mods |= NativeMethods.MOD_CONTROL;
        if (hk.Shift) mods |= NativeMethods.MOD_SHIFT;
        if (hk.Alt) mods |= NativeMethods.MOD_ALT;
        if (hk.Win) mods |= NativeMethods.MOD_WIN;
        int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey((System.Windows.Input.Key)hk.Key);
        int id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, mods, (uint)vk))
        {
            Log.Info($"Atajo no disponible: {hk}");
            return false;
        }
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            Application.Current.Dispatcher.BeginInvoke(a);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}

public static class StartupService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (enabled) key.SetValue("MaxSnapr", $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue("MaxSnapr", false);
        }
        catch (Exception ex) { Log.Error("Inicio con Windows", ex); }
    }
}
