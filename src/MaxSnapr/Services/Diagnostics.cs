using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using MaxSnapr.Library;
using Microsoft.Win32;

namespace MaxSnapr.Services;

/// <summary>Watches the clipboard and stores images copied in other applications.</summary>
public sealed class ClipboardWatcher : IDisposable
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();

    readonly HwndSource _src;
    uint _ignoreUntil;

    public ClipboardWatcher()
    {
        _src = new HwndSource(new HwndSourceParameters("MaxSnaprClipboard") { Width = 0, Height = 0, WindowStyle = 0 });
        _src.AddHook(WndProc);
        AddClipboardFormatListener(_src.Handle);
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x031D /*WM_CLIPBOARDUPDATE*/)
        {
            // Skip images MaxSnapr itself put on the clipboard.
            Native.NativeMethods.GetWindowThreadProcessId(GetClipboardOwner(), out uint pid);
            if (pid != Environment.ProcessId && Environment.TickCount > _ignoreUntil)
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (!Clipboard.ContainsImage()) return;
                        var img = ClipboardService.GetImage();
                        if (img == null) return;
                        var item = LibraryStore.Instance.Add(new Editor.Model.EditorDocument(img), "Desde el portapapeles", "Portapapeles");
                        item.InTray = true;
                        App.Current.Notify("Imagen guardada", "Se ha añadido a la biblioteca la imagen copiada.");
                        _ignoreUntil = (uint)Environment.TickCount + 1000;
                    }
                    catch (Exception ex) { Log.Error("Supervisar portapapeles", ex); }
                });
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_src.Handle);
        _src.Dispose();
    }
}

public static class Diagnostics
{
    /// <summary>ZIP with the log, settings without secrets, and environment facts.</summary>
    public static void Save(Window owner)
    {
        var dlg = new SaveFileDialog { Filter = "Diagnóstico (*.zip)|*.zip", FileName = $"MaxSnapr diagnóstico {DateTime.Now:yyyy-MM-dd HHmm}" };
        if (dlg.ShowDialog(owner) != true) return;
        try
        {
            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            using var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create);
            var log = Path.Combine(AppSettings.Folder, "maxsnapr.log");
            if (File.Exists(log)) zip.CreateEntryFromFile(log, "maxsnapr.log");
            var settings = JsonNode.Parse(JsonSerializer.Serialize(App.Settings, Editor.Model.ToolStyle.Json))!.AsObject();
            // Never include credentials or history.
            if (settings["Output"] is JsonObject o)
            {
                o.Remove("History");
                if (o["Ftp"] is JsonObject f) f["PasswordProtected"] = "(oculto)";
                if (o["Slack"] is JsonObject s) s["TokenProtected"] = "(oculto)";
                if (o["Http"] is JsonObject h) h["AuthHeader"] = "(oculto)";
            }
            using (var w = new StreamWriter(zip.CreateEntry("settings.json").Open())) w.Write(settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var env = new StringBuilder();
            env.AppendLine($"MaxSnapr {typeof(App).Assembly.GetName().Version}");
            env.AppendLine($"Windows {Environment.OSVersion.Version}  {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");
            env.AppendLine($".NET {Environment.Version}");
            env.AppendLine($"Pantallas: {string.Join("; ", Capture.ScreenCapturer.GetMonitors().Select(m => $"{m.Bounds.Width}×{m.Bounds.Height} @ {m.Scale * 100:0}%"))}");
            env.AppendLine($"Biblioteca: {LibraryStore.Instance.Items.Count} elementos");
            env.AppendLine($"OCR: {(Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() != null ? "disponible" : "no disponible")}");
            using (var w = new StreamWriter(zip.CreateEntry("entorno.txt").Open())) w.Write(env.ToString());
            MessageBox.Show(owner, "Informe guardado. No contiene capturas ni contraseñas.", "Diagnóstico");
        }
        catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Diagnóstico"); }
    }

    public static void ResetSettings(Window owner)
    {
        if (MessageBox.Show(owner, "¿Restablecer todas las preferencias? Se guardará una copia de los ajustes actuales.", "Restablecer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var file = Path.Combine(AppSettings.Folder, "settings.json");
        if (File.Exists(file)) File.Copy(file, Path.Combine(AppSettings.Folder, $"settings copia {DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
        var keepLibrary = App.Settings.LibraryFolder;
        var fresh = new AppSettings { LibraryFolder = keepLibrary };
        fresh.Save();
        MessageBox.Show(owner, "Preferencias restablecidas. Reinicie MaxSnapr para aplicarlas por completo.", "Restablecer");
    }
}
