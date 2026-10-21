using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MaxSnapr.Services;

/// <summary>
/// Anonymous usage counter. At most once a day, a while after start-up, it sends the app version, the Windows
/// version and build, the Windows display language, whether it is the installed or portable copy, whether this is
/// its first report of the week, of the month or ever, and how many times each feature was used on previous days.
/// There is no identifier of any kind, and no file names, text, images, paths, account or computer details.
/// Nothing is sent when the user turns it off in Preferences, in isolated test runs, or offline (counts wait).
/// </summary>
public static class Usage
{
    public const string Endpoint = "https://maxsnapr-usage.danfigi.workers.dev/v1/ping";

    /// <summary>Feature keys the server accepts; anything else is ignored on both sides.</summary>
    public static readonly IReadOnlySet<string> Features = new HashSet<string>
    {
        "capture", "capture.region", "capture.window", "capture.fullscreen", "capture.scrolling", "capture.panoramic",
        "capture.repeat", "capture.steps", "ocr", "video", "video.mic", "video.system_audio", "video.webcam",
        "video.background", "video.gif", "editor.open", "editor.arrow", "editor.text", "editor.callout", "editor.shape",
        "editor.step", "editor.pen", "editor.blur", "editor.magnify", "editor.spotlight", "editor.stamp", "editor.crop",
        "smart_redact", "export", "share", "mobile_receive", "template", "library.open",
    };

    static readonly object Gate = new();
    static UsageState? _state;
    static DateTime _lastSave;

    /// <summary>Counts one use of a feature today. Cheap, never throws, and does nothing when sharing is off.</summary>
    public static void Count(string feature)
    {
        try
        {
            if (!App.Settings.ShareUsage || !Features.Contains(feature)) return;
            lock (Gate)
            {
                var s = State;
                var today = Today;
                if (s.Day != today) { s.Pending = Merge(s.Pending, s.Day, s.Counts); s.Day = today; s.Counts = new(); }
                s.Counts[feature] = Math.Min(1000, s.Counts.GetValueOrDefault(feature) + 1);
                // Write at most every few seconds; Quit and the daily report save the rest.
                if (DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(20)) SaveLocked();
            }
        }
        catch { /* counting must never break a feature */ }
    }

    /// <summary>Sends today's report a while after start-up, at most once a day.</summary>
    public static void ScheduleStartupReport()
    {
        if (!Enabled) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            try { await ReportAsync(); }
            catch (Exception ex) { Log.Info("Recuento de uso: " + ex.Message); }
        });
    }

    /// <summary>Isolated test runs and the opt-out never send anything.</summary>
    static bool Enabled => App.Settings.ShareUsage && AppSettings.Home == null && Environment.GetEnvironmentVariable("MAXSNAPR_NO_USAGE") != "1";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MaxSnapr", Updates.CurrentVersion.ToString(3)));
        return h;
    }

    static async Task ReportAsync()
    {
        string body;
        string? sent;
        lock (Gate)
        {
            var s = State;
            if (s.LastReport == Today) return;
            // Move finished days into the queue first, so what is sent is exactly what gets removed below.
            if (s.Day != Today) { s.Pending = Merge(s.Pending, s.Day, s.Counts); s.Day = Today; s.Counts = new(); }
            sent = Oldest(s.Pending);
            body = BuildReport(s, DateTime.UtcNow.Date, Environment.OSVersion.Version, CultureInfo.InstalledUICulture, IsPortable);
        }
        using var resp = await Http.PostAsync(Endpoint, new StringContent(body, Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode) { Log.Info($"Recuento de uso: respuesta {(int)resp.StatusCode}"); return; }
        lock (Gate)
        {
            var s = State;
            s.LastReport = Today;
            s.LastWeek = WeekOf(DateTime.UtcNow.Date);
            s.LastMonth = DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            s.Reported = true;
            if (sent != null) s.Pending.Remove(sent);
            SaveLocked();
        }
    }

    /// <summary>Diagnostic: the report this copy would send now, and optionally the server's answer to it.</summary>
    internal static async Task<string> ProbeAsync(bool send)
    {
        string body;
        lock (Gate) body = BuildReport(State, DateTime.UtcNow.Date, Environment.OSVersion.Version, CultureInfo.InstalledUICulture, IsPortable);
        var sb = new StringBuilder($"Destino: {Endpoint}{Environment.NewLine}Envío: {body}{Environment.NewLine}");
        if (send)
        {
            using var resp = await Http.PostAsync(Endpoint, new StringContent(body, Encoding.UTF8, "application/json"));
            sb.AppendLine($"Respuesta: {(int)resp.StatusCode}");
        }
        return sb.ToString();
    }

    /// <summary>The exact JSON that is sent. Kept separate so the self-test can check it holds nothing personal.</summary>
    internal static string BuildReport(UsageState s, DateTime todayUtc, Version os, CultureInfo ui, bool portable)
    {
        var today = todayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // Counts from previous days only: today's are still growing. One day per report keeps it small.
        var pending = s.Day != today ? Merge(s.Pending, s.Day, s.Counts) : s.Pending;
        var day = Oldest(pending);
        var report = new Dictionary<string, object>
        {
            ["v"] = Updates.CurrentVersion.ToString(3),
            ["os"] = $"{(os.Build >= 22000 ? 11 : 10)} {os.Build}",
            ["lang"] = ui.TwoLetterISOLanguageName is { Length: 2 or 3 } l && l.All(char.IsAsciiLetterLower) ? l : "?",
            ["install"] = portable ? "portable" : "setup",
            ["first"] = !s.Reported,
            ["nw"] = s.LastWeek != WeekOf(todayUtc),
            ["nm"] = s.LastMonth != todayUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        };
        if (day != null)
        {
            report["fd"] = day;
            report["f"] = pending[day].Where(kv => Features.Contains(kv.Key) && kv.Value > 0).ToDictionary(kv => kv.Key, kv => Math.Min(1000, kv.Value));
        }
        return JsonSerializer.Serialize(report);
    }

    static Dictionary<string, Dictionary<string, int>> Merge(Dictionary<string, Dictionary<string, int>> pending, string? day, Dictionary<string, int> counts)
    {
        var copy = pending.ToDictionary(kv => kv.Key, kv => new Dictionary<string, int>(kv.Value));
        if (day != null && counts.Count > 0)
        {
            var d = copy.TryGetValue(day, out var existing) ? existing : copy[day] = new();
            foreach (var (k, v) in counts) d[k] = Math.Min(1000, d.GetValueOrDefault(k) + v);
        }
        // Keep at most a month of unsent days (the server ignores older ones anyway).
        foreach (var old in copy.Keys.OrderByDescending(k => k, StringComparer.Ordinal).Skip(31).ToList()) copy.Remove(old);
        return copy;
    }

    static string? Oldest(Dictionary<string, Dictionary<string, int>> pending) => pending.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();

    static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Monday of the ISO week, so weekly counts line up with the dashboard.</summary>
    internal static string WeekOf(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static bool IsPortable
    {
        get
        {
            var exe = Environment.ProcessPath ?? "";
            var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            return !(PathUtil.IsInside(exe, programs) || PathUtil.IsInside(exe, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        }
    }

    // ---- local state (counts waiting to be sent), in %APPDATA%\MaxSnapr\usage.json ----

    static string FilePath => Path.Combine(AppSettings.Folder, "usage.json");

    static UsageState State
    {
        get
        {
            if (_state != null) return _state;
            try { if (File.Exists(FilePath)) _state = JsonSerializer.Deserialize<UsageState>(File.ReadAllText(FilePath)); }
            catch { /* a damaged file just starts over */ }
            return _state ??= new UsageState();
        }
    }

    public static void Save() { lock (Gate) if (_state != null) SaveLocked(); }

    /// <summary>Forgets counts that have not been sent yet (used when the user turns sharing off).</summary>
    public static void ClearPending()
    {
        lock (Gate)
        {
            var s = State;
            s.Counts.Clear();
            s.Pending.Clear();
            SaveLocked();
        }
    }

    static void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(_state));
            File.Move(FilePath + ".tmp", FilePath, true);
            _lastSave = DateTime.UtcNow;
        }
        catch { }
    }
}

public sealed class UsageState
{
    public string? Day { get; set; }
    public Dictionary<string, int> Counts { get; set; } = new();
    public Dictionary<string, Dictionary<string, int>> Pending { get; set; } = new();
    public string? LastReport { get; set; }
    public string? LastWeek { get; set; }
    public string? LastMonth { get; set; }
    public bool Reported { get; set; }
}
