using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaxSnapr.Services;

/// <summary>A newer version published on the public downloads page.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Title, string Notes, string PageUrl, string InstallerUrl, string InstallerName, string ChecksumsUrl, DateTimeOffset Published);

/// <summary>
/// Checks the public downloads repository for a newer release and installs it.
/// The request is anonymous (no identifiers, no data about this computer). The installer is only taken from
/// that repository's release assets over HTTPS, and only run when its SHA-256 matches the published checksum list.
/// </summary>
public static class Updates
{
    public const string Repository = "danfigi/MaxSnapr";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";
    static string LatestApi => $"https://api.github.com/repos/{Repository}/releases/latest";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub requires a User-Agent; it names the app only, never the user or the computer.
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MaxSnapr", CurrentVersion.ToString(3)));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    public static Version CurrentVersion => typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Latest published release when it is newer than this one; null when up to date or offline.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(LatestApi, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return Parse(doc.RootElement, CurrentVersion);
    }

    /// <summary>Reads a GitHub "release" object; separated from the request so the self-test can check it offline.</summary>
    internal static UpdateInfo? Parse(JsonElement r, Version current)
    {
        if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v) || Normalize(v) <= Normalize(current)) return null;
        string? installer = null, installerName = null, sums = null;
        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!IsTrustedAsset(url)) continue;
            if (name.StartsWith("MaxSnapr-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { installer = url; installerName = name; }
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = url;
        }
        if (installer == null || sums == null) return null;
        var page = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        var title = r.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } t ? t : $"MaxSnapr {v.ToString(3)}";
        var notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var published = r.TryGetProperty("published_at", out var pa) && pa.TryGetDateTimeOffset(out var when) ? when : DateTimeOffset.Now;
        return new UpdateInfo(v, tag, title, notes, page, installer, installerName!, sums, published);
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Only release assets of our own public repository, over HTTPS.</summary>
    internal static bool IsTrustedAsset(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com"
        && u.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads the installer, verifies its SHA-256 against the published list and returns its path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct)
    {
        var sums = await Http.GetStringAsync(u.ChecksumsUrl, ct);
        var expected = sums.Split('\n')
            .Select(l => l.Trim().Split(new[] { ' ', '*' }, 2, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].Trim().TrimStart('*').Equals(u.InstallerName, StringComparison.OrdinalIgnoreCase))?[0];
        if (expected is not { Length: 64 }) throw new InvalidDataException("La versión publicada no incluye la huella del instalador.");

        var dir = Path.Combine(Path.GetTempPath(), "MaxSnapr", "update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, u.InstallerName);
        using (var resp = await Http.GetAsync(u.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
            var buf = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report(read / (double)total);
            }
        }
        string actual;
        await using (var fs = File.OpenRead(path)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("El instalador descargado no coincide con el publicado y se ha descartado.");
        }
        return path;
    }

    /// <summary>Starts the verified installer and lets it close this instance (it asks before replacing a running app).</summary>
    public static void RunInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path, "/SILENT /SP- /NOCANCEL /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS") { UseShellExecute = true });
        App.Current.Quit();
    }

    /// <summary>Checks at most once a day, a while after start-up so it never slows it down.</summary>
    public static void ScheduleStartupCheck()
    {
        var s = App.Settings;
        if (!s.CheckForUpdates || DateTime.Now - s.LastUpdateCheck < TimeSpan.FromHours(20)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            try
            {
                var u = await CheckAsync();
                s.LastUpdateCheck = DateTime.Now;
                if (u == null || s.SkippedVersion == u.Tag) return;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // Never interrupt a capture or a recording; the next start will offer it again.
                    if (Capture.CaptureService.HidingWindows || Video.RecordingSession.IsRecording) return;
                    new Views.UpdateWindow(u).Show();
                });
            }
            catch (Exception ex) { Log.Info("Comprobación de actualizaciones: " + ex.Message); }
        });
    }
}
