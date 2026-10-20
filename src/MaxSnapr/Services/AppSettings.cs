using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MaxSnapr.Editor.Model;
using MaxSnapr.Editor.Tools;

namespace MaxSnapr.Services;

public enum CaptureSelectionMode
{
    Region, Window, FullScreen, ActiveWindow, FixedRegion, Freehand, MultiArea, Menu, Object, Scrolling, Panoramic, Clipboard, AllScreens, ResizedWindow,
}

public sealed class Hotkey
{
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public bool Win { get; set; }
    public int Key { get; set; } // System.Windows.Input.Key

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Mayús");
        if (Alt) parts.Add("Alt");
        if (Win) parts.Add("Win");
        var k = (System.Windows.Input.Key)Key;
        parts.Add(k switch
        {
            System.Windows.Input.Key.Snapshot => "Impr Pant",
            System.Windows.Input.Key.None => "—",
            _ => k.ToString(),
        });
        return string.Join("+", parts);
    }
}

public sealed class CapturePreset
{
    public string Name { get; set; } = "Imagen";
    public CaptureSelectionMode Selection { get; set; } = CaptureSelectionMode.Region;
    public int DelaySeconds { get; set; }
    public bool IncludeCursor { get; set; }
    public bool CopyToClipboard { get; set; } = true;
    public bool OpenInEditor { get; set; } = true;
    public bool GrabText { get; set; }
    public bool RecordVideo { get; set; }
    public int FixedWidth { get; set; } = 800;
    public int FixedHeight { get; set; } = 600;
    /// <summary>Repeat the capture every N seconds (0 = once), Count times; useful for timed captures.</summary>
    public int IntervalSeconds { get; set; }
    public int IntervalCount { get; set; } = 5;
    public bool EffectBorder { get; set; }
    public bool EffectShadow { get; set; }
    public bool EffectTornEdge { get; set; }
    public bool ShareToFolder { get; set; }
    public Hotkey? Hotkey { get; set; }
}

public sealed class FavoriteStyle
{
    public ToolKind Kind { get; set; }
    public ToolStyle Style { get; set; } = new();
}

public sealed class NamedStyle
{
    public string Name { get; set; } = "";
    public ToolStyle Style { get; set; } = new();
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark"; // Dark | Light | System
    public string LibraryFolder { get; set; } = Home != null ? Path.Combine(Home, "Biblioteca") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MaxSnapr", "Biblioteca");
    public Hotkey CaptureHotkey { get; set; } = new() { Key = (int)System.Windows.Input.Key.Snapshot };
    public Hotkey FallbackCaptureHotkey { get; set; } = new() { Ctrl = true, Shift = true, Key = (int)System.Windows.Input.Key.C };
    public Hotkey RepeatHotkey { get; set; } = new() { Ctrl = true, Shift = true, Key = (int)System.Windows.Input.Key.R };
    public Hotkey GrabTextHotkey { get; set; } = new() { Ctrl = true, Shift = true, Key = (int)System.Windows.Input.Key.T };
    public CapturePreset Current { get; set; } = new();
    public Video.VideoSettings Video { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public List<ToolKind>? ToolbarTools { get; set; }
    public Hotkey VideoHotkey { get; set; } = new() { Ctrl = true, Shift = true, Key = (int)System.Windows.Input.Key.V };
    public List<CapturePreset> Presets { get; set; } = DefaultPresets();
    public bool RunAtStartup { get; set; }
    public bool ShowEdgeWidget { get; set; }
    public bool WatchClipboard { get; set; }
    public bool ShowWelcome { get; set; } = true;
    public bool SupportReminderShown { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    public string? SkippedVersion { get; set; }
    public bool SpellCheck { get; set; } = true;
    public bool PixelGrid { get; set; } = true;
    public Hotkey StepCaptureHotkey { get; set; } = new() { Ctrl = true, Shift = true, Key = (int)System.Windows.Input.Key.P };
    public List<Editor.Model.ToolTheme> Themes { get; set; } = new();
    public string? ActiveTheme { get; set; }
    public List<Editor.Model.TemplateDefinition> UserTemplates { get; set; } = new();
    public bool ShowTray { get; set; } = true;
    public bool ShowPropertiesPanel { get; set; } = true;
    public int JpegQuality { get; set; } = 90;
    public int ExportDpi { get; set; } = 96;
    public string DefaultExport { get; set; } = "png";
    public bool EscClosesEditor { get; set; }
    public Dictionary<ToolKind, ToolStyle> ToolStyles { get; set; } = new();
    public Dictionary<ToolKind, List<NamedStyle>> UserQuickStyles { get; set; } = new();
    public List<string> RecentColors { get; set; } = new();
    public List<FavoriteStyle> Favorites { get; set; } = new();
    public string? LastSaveFolder { get; set; }

    static List<CapturePreset> DefaultPresets() => new()
    {
        new CapturePreset { Name = "Región en el editor", Selection = CaptureSelectionMode.Region },
        new CapturePreset { Name = "Ventana al portapapeles", Selection = CaptureSelectionMode.Window, OpenInEditor = false },
        new CapturePreset { Name = "Pantalla completa con retardo", Selection = CaptureSelectionMode.FullScreen, DelaySeconds = 5, IncludeCursor = true },
        new CapturePreset { Name = "Extraer texto", Selection = CaptureSelectionMode.Region, GrabText = true, OpenInEditor = false, CopyToClipboard = true },
    };

    /// <summary>MAXSNAPR_HOME redirects settings and the default library, so test runs never touch real user data.</summary>
    public static string? Home => Environment.GetEnvironmentVariable("MAXSNAPR_HOME") is { Length: > 0 } h ? h : null;
    public static string Folder => Home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MaxSnapr");
    static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), ToolStyle.Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudieron leer los ajustes; se usan los predeterminados", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var opts = new JsonSerializerOptions(ToolStyle.Json) { WriteIndented = true };
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, opts));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudieron guardar los ajustes", ex);
        }
    }
}

public static class PathUtil
{
    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> itself or inside it (not merely a name prefix).</summary>
    public static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.Equals(r, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

public static class Log
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(AppSettings.Folder, "maxsnapr.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
