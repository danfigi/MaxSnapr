using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Windows;
using MaxSnapr.Services;
using Microsoft.Win32;

namespace MaxSnapr.Library;

/// <summary>ZIP backup of the whole library and a non-destructive restore.</summary>
public static class LibraryBackup
{
    public static void Create(Window owner)
    {
        var store = LibraryStore.Instance;
        store.Save();
        var dlg = new SaveFileDialog { Filter = "Copia de seguridad de MaxSnapr (*.zip)|*.zip", FileName = $"MaxSnapr copia {DateTime.Now:yyyy-MM-dd}" };
        if (dlg.ShowDialog(owner) != true) return;
        try
        {
            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            using var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create);
            foreach (var f in Directory.EnumerateFiles(store.Root, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".tmp")))
                zip.CreateEntryFromFile(f, Path.GetRelativePath(store.Root, f).Replace('\\', '/'), CompressionLevel.Fastest);
            MessageBox.Show(owner, $"Copia creada con {zip.Entries.Count} archivos.", "Copia de seguridad");
        }
        catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Copia de seguridad", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public static void Restore(Window owner)
    {
        var dlg = new OpenFileDialog { Filter = "Copia de seguridad de MaxSnapr (*.zip)|*.zip" };
        if (dlg.ShowDialog(owner) != true) return;
        var store = LibraryStore.Instance;
        int added = 0;
        try
        {
            using var zip = ZipFile.OpenRead(dlg.FileName);
            List<LibraryItem> index = new();
            var indexEntry = zip.GetEntry("library.json");
            if (indexEntry != null)
            {
                using var r = new StreamReader(indexEntry.Open());
                index = JsonSerializer.Deserialize<List<LibraryItem>>(r.ReadToEnd()) ?? new();
            }
            foreach (var e in zip.Entries.Where(e => e.FullName != "library.json" && e.Length > 0))
            {
                var dest = Path.GetFullPath(Path.Combine(store.Root, e.FullName));
                if (!PathUtil.IsInside(dest, store.Root) || dest.Equals(Path.GetFullPath(store.Root), StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                if (File.Exists(dest)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest);
                var rel = Path.GetRelativePath(store.Root, dest);
                if (dest.EndsWith(".thumb.png")) continue;
                if (store.Items.Any(i => string.Equals(i.FileName, rel, StringComparison.OrdinalIgnoreCase))) continue;
                var meta = index.FirstOrDefault(i => string.Equals(i.FileName, rel, StringComparison.OrdinalIgnoreCase));
                var ext = Path.GetExtension(dest).ToLowerInvariant();
                if (ext is not (".snapr" or ".mp4" or ".gif")) continue;
                meta ??= new LibraryItem { FileName = rel, Name = Path.GetFileNameWithoutExtension(dest), Kind = ext == ".mp4" ? "Vídeo" : ext == ".gif" ? "GIF" : "Imagen" };
                meta.InTray = false;
                store.Items.Add(meta);
                added++;
            }
            store.Save();
            MessageBox.Show(owner, added == 0 ? "No había capturas nuevas que restaurar." : $"Se han restaurado {added} capturas.", "Restaurar");
        }
        catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Restaurar", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
