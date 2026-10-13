using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace MaxSnapr.Services;

public sealed class ProgramEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "\"{archivo}\"";
}

public sealed class FtpSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 21;
    public string User { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public string RemoteFolder { get; set; } = "/";
    public bool UseTls { get; set; } = true;
    public bool Passive { get; set; } = true;
    public string PublicUrlPrefix { get; set; } = "";
}

public sealed class HttpUploadSettings
{
    public string Url { get; set; } = "";
    public string FieldName { get; set; } = "file";
    public string AuthHeader { get; set; } = "";
    /// <summary>Dot path inside the JSON response that contains the public link, e.g. "data.url".</summary>
    public string LinkPath { get; set; } = "url";
}

public sealed class SlackSettings
{
    public string TokenProtected { get; set; } = "";
    public string ChannelId { get; set; } = "";
}

public sealed class ShareRecord
{
    public DateTime When { get; set; } = DateTime.Now;
    public string Destination { get; set; } = "";
    public string Item { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class OutputSettings
{
    public string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MaxSnapr");
    public string NamePattern { get; set; } = "{nombre} {fecha} {hora}";
    public string Format { get; set; } = "png";
    public int Counter { get; set; } = 1;
    public List<ProgramEntry> Programs { get; set; } = new();
    public FtpSettings Ftp { get; set; } = new();
    public HttpUploadSettings Http { get; set; } = new();
    public SlackSettings Slack { get; set; } = new();
    public List<ShareRecord> History { get; set; } = new();
}

/// <summary>What is being shared: a rendered image or an existing media file.</summary>
public sealed class ShareItem
{
    public BitmapSource? Image { get; init; }
    public string? FilePath { get; init; }
    public string Name { get; init; } = "Captura";
    public string App { get; init; } = "";

    /// <summary>A file on disk with the content (images are exported to a temp file in the default format).</summary>
    public string EnsureFile(string? ext = null)
    {
        if (FilePath != null) return FilePath;
        var dir = Path.Combine(Path.GetTempPath(), "MaxSnapr", "share");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Naming.Safe(Name) + "." + (ext ?? MaxSnapr.App.Settings.Output.Format));
        ExportService.Save(Image!, path, MaxSnapr.App.Settings.JpegQuality);
        return path;
    }
}

public static class Naming
{
    public static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var r = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return r.Length == 0 ? "Captura" : r.Length > 120 ? r[..120] : r;
    }

    public static readonly string[] Tokens = { "{nombre}", "{fecha}", "{hora}", "{año}", "{mes}", "{día}", "{app}", "{ventana}", "{ancho}", "{alto}", "{n}", "{usuario}" };

    public static string Build(string pattern, string name, string app, string title, int w, int h, bool advanceCounter = true)
    {
        var o = MaxSnapr.App.Settings.Output;
        var now = DateTime.Now;
        var s = pattern
            .Replace("{nombre}", string.IsNullOrWhiteSpace(name) ? "Captura" : name)
            .Replace("{fecha}", now.ToString("yyyy-MM-dd")).Replace("{hora}", now.ToString("HH.mm.ss"))
            .Replace("{año}", now.ToString("yyyy")).Replace("{mes}", now.ToString("MM")).Replace("{día}", now.ToString("dd"))
            .Replace("{app}", app).Replace("{ventana}", title)
            .Replace("{ancho}", w.ToString()).Replace("{alto}", h.ToString())
            .Replace("{usuario}", Environment.UserName)
            .Replace("{n}", o.Counter.ToString("000"));
        if (advanceCounter && pattern.Contains("{n}")) o.Counter++;
        return Safe(s);
    }
}

public static class Secrets
{
    public static string Protect(string plain) => plain.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string blob)
    {
        try { return blob.Length == 0 ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}

public interface IShareDestination
{
    string Name { get; }
    string Glyph { get; }
    bool Available { get; }
    bool SupportsMedia { get; }
    /// <summary>Returns a short result (path or link) for the history, or null when cancelled.</summary>
    Task<string?> ShareAsync(ShareItem item, Window? owner);
}

public static class Outputs
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static IReadOnlyList<IShareDestination> All { get; } = BuildAll();

    static List<IShareDestination> BuildAll()
    {
        var list = new List<IShareDestination>
        {
            new Dest("Portapapeles", "\uE8C8", () => true, false, (i, _) =>
            {
                if (i.Image != null) ClipboardService.SetImage(i.Image);
                else System.Windows.Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { i.FilePath! });
                return Task.FromResult<string?>("Portapapeles");
            }, media: true),
            new Dest("Carpeta de salida", "\uE8B7", () => true, true, (i, _) => Task.FromResult<string?>(SaveToOutputFolder(i))),
            new Dest("Correo electrónico", "\uE715", () => true, true, (i, _) => Task.FromResult<string?>(Email(i))),
            new Dest("Microsoft Word", "\uE8A5", () => OfficeAvailable("Word.Application"), false, (i, _) => Office(i, "Word.Application")),
            new Dest("Microsoft PowerPoint", "\uE7C3", () => OfficeAvailable("PowerPoint.Application"), false, (i, _) => Office(i, "PowerPoint.Application")),
            new Dest("Microsoft Excel", "\uE80A", () => OfficeAvailable("Excel.Application"), false, (i, _) => Office(i, "Excel.Application")),
            new Dest("Microsoft Teams (pegar)", "\uE716", () => true, false, (i, _) =>
            {
                ClipboardService.SetImage(i.Image!);
                Process.Start(new ProcessStartInfo("msteams:") { UseShellExecute = true });
                return Task.FromResult<string?>("Teams: imagen lista para pegar");
            }),
            new Dest("Slack", "\uE8BD", () => MaxSnapr.App.Settings.Output.Slack.TokenProtected.Length > 0, true, (i, _) => Slack(i)),
            new Dest("FTP", "\uE753", () => MaxSnapr.App.Settings.Output.Ftp.Host.Length > 0, true, (i, _) => Ftp(i)),
            new Dest("Servidor web (enlace)", "\uE774", () => MaxSnapr.App.Settings.Output.Http.Url.Length > 0, true, (i, _) => HttpUpload(i)),
        };
        foreach (var (name, path) in SyncedFolders())
        {
            var p = path;
            list.Add(new Dest(name, "\uE753", () => Directory.Exists(p), true, (i, _) => Task.FromResult<string?>(CopyTo(i, Path.Combine(p, "MaxSnapr")))));
        }
        return list;
    }

    public static IEnumerable<IShareDestination> Programs() =>
        MaxSnapr.App.Settings.Output.Programs.Select(p => (IShareDestination)new Dest(p.Name, "\uE7AC", () => File.Exists(p.Path), true, (i, _) =>
        {
            var file = i.EnsureFile();
            // Quote the path unless the template already does, so spaces cannot split it into extra arguments.
            var args = p.Arguments.Contains("\"{archivo}\"") ? p.Arguments.Replace("{archivo}", file) : p.Arguments.Replace("{archivo}", $"\"{file}\"");
            Process.Start(new ProcessStartInfo(p.Path, args) { UseShellExecute = false });
            return Task.FromResult<string?>(p.Name);
        }));

    sealed class Dest : IShareDestination
    {
        readonly Func<bool> _available;
        readonly Func<ShareItem, Window?, Task<string?>> _share;
        public Dest(string name, string glyph, Func<bool> available, bool fileBased, Func<ShareItem, Window?, Task<string?>> share, bool media = false)
        {
            Name = name; Glyph = glyph; _available = available; _share = share; SupportsMedia = fileBased || media;
        }
        public string Name { get; }
        public string Glyph { get; }
        public bool Available => _available();
        public bool SupportsMedia { get; }
        public Task<string?> ShareAsync(ShareItem item, Window? owner) => _share(item, owner);
    }

    public static async Task ShareAsync(IShareDestination d, ShareItem item, Window? owner)
    {
        try
        {
            var result = await d.ShareAsync(item, owner);
            if (result == null) return;
            var h = MaxSnapr.App.Settings.Output.History;
            h.Insert(0, new ShareRecord { Destination = d.Name, Item = item.Name, Target = result });
            if (h.Count > 50) h.RemoveRange(50, h.Count - 50);
            MaxSnapr.App.Settings.Save();
            if (result.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                ClipboardService.SetText(result);
                MaxSnapr.App.Current.Notify("Enlace copiado", result);
            }
            else MaxSnapr.App.Current.Notify("Compartido", $"{d.Name}: {result}");
        }
        catch (Exception ex)
        {
            Log.Error("Compartir con " + d.Name, ex);
            MessageBox.Show(owner, $"No se pudo compartir con {d.Name}: {ex.Message}", "Compartir", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- destinations ---------------------------------------------------------------------------

    public static string SaveToOutputFolder(BitmapSource image, string title, string app) =>
        SaveToOutputFolder(new ShareItem { Image = image, Name = string.IsNullOrWhiteSpace(title) ? "Captura" : title, App = app });

    public static string SaveToOutputFolder(ShareItem i)
    {
        var o = MaxSnapr.App.Settings.Output;
        Directory.CreateDirectory(o.Folder);
        int w = i.Image?.PixelWidth ?? 0, h = i.Image?.PixelHeight ?? 0;
        var name = Naming.Build(o.NamePattern, i.Name, i.App, i.Name, w, h);
        var ext = i.FilePath != null ? Path.GetExtension(i.FilePath) : "." + o.Format;
        var path = Path.Combine(o.Folder, name + ext);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(o.Folder, $"{name} ({n}){ext}");
        if (i.FilePath != null) File.Copy(i.FilePath, path);
        else ExportService.Save(i.Image!, path, MaxSnapr.App.Settings.JpegQuality);
        return path;
    }

    static string CopyTo(ShareItem i, string folder)
    {
        Directory.CreateDirectory(folder);
        var src = i.EnsureFile();
        var dest = Path.Combine(folder, Path.GetFileName(src));
        for (int n = 2; File.Exists(dest); n++) dest = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(src)} ({n}){Path.GetExtension(src)}");
        File.Copy(src, dest);
        return dest;
    }

    /// <summary>OneDrive (personal and work), Dropbox, Google Drive and Box sync folders found on this PC.</summary>
    public static IEnumerable<(string name, string path)> SyncedFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<(string, string?)> Candidates()
        {
            yield return ("OneDrive", Environment.GetEnvironmentVariable("OneDriveConsumer"));
            yield return ("OneDrive (empresa)", Environment.GetEnvironmentVariable("OneDriveCommercial"));
            yield return ("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return ("Dropbox", DropboxPath());
            yield return ("Box", Path.Combine(home, "Box"));
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType != DriveType.CDRom))
            {
                foreach (var g in new[] { "Mi unidad", "My Drive" })
                {
                    var p = Path.Combine(d.RootDirectory.FullName, g);
                    if (Directory.Exists(p)) yield return ("Google Drive", p);
                }
            }
        }
        foreach (var (name, path) in Candidates())
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && seen.Add(path)) yield return (name, path);
    }

    static string? DropboxPath()
    {
        try
        {
            var info = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json");
            if (!File.Exists(info)) return null;
            var node = JsonNode.Parse(File.ReadAllText(info));
            return node?["personal"]?["path"]?.GetValue<string>() ?? node?["business"]?["path"]?.GetValue<string>();
        }
        catch { return null; }
    }

    static string Email(ShareItem i)
    {
        var file = i.EnsureFile();
        // Outlook through COM gives a proper draft with the attachment; MAPI is the generic fallback.
        try
        {
            var type = Type.GetTypeFromProgID("Outlook.Application");
            if (type != null)
            {
                dynamic outlook = Activator.CreateInstance(type)!;
                dynamic mail = outlook.CreateItem(0);
                mail.Subject = i.Name;
                mail.Attachments.Add(file);
                mail.Display(false);
                return "Outlook";
            }
        }
        catch (Exception ex) { Log.Error("Outlook", ex); }
        if (Views.MailService.SendWithAttachment(file, i.Name)) return "Correo";
        if (i.Image != null) ClipboardService.SetImage(i.Image);
        Process.Start(new ProcessStartInfo($"mailto:?subject={Uri.EscapeDataString(i.Name)}") { UseShellExecute = true });
        return "Correo (imagen en el portapapeles)";
    }

    static bool OfficeAvailable(string progId) => Type.GetTypeFromProgID(progId) != null;

    static Task<string?> Office(ShareItem i, string progId)
    {
        var file = i.EnsureFile("png");
        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID(progId)!)!;
        app.Visible = true;
        switch (progId)
        {
            case "Word.Application":
                dynamic doc = app.Documents.Count > 0 ? app.ActiveDocument : app.Documents.Add();
                app.Selection.InlineShapes.AddPicture(file);
                app.Selection.TypeParagraph();
                break;
            case "PowerPoint.Application":
                dynamic pres = app.Presentations.Count > 0 ? app.ActivePresentation : app.Presentations.Add();
                dynamic slide = pres.Slides.Add(pres.Slides.Count + 1, 12 /*ppLayoutBlank*/);
                float sw = pres.PageSetup.SlideWidth, sh = pres.PageSetup.SlideHeight;
                double scale = Math.Min(sw * 0.9 / Math.Max(1, i.Image!.PixelWidth * 0.75), sh * 0.9 / Math.Max(1, i.Image.PixelHeight * 0.75));
                float w = (float)(i.Image.PixelWidth * 0.75 * Math.Min(1, scale)), h = (float)(i.Image.PixelHeight * 0.75 * Math.Min(1, scale));
                slide.Shapes.AddPicture(file, 0, -1, (sw - w) / 2, (sh - h) / 2, w, h);
                break;
            case "Excel.Application":
                dynamic wb = app.Workbooks.Count > 0 ? app.ActiveWorkbook : app.Workbooks.Add();
                dynamic sheet = wb.ActiveSheet;
                dynamic cell = app.ActiveCell;
                sheet.Shapes.AddPicture(file, 0, -1, (float)cell.Left, (float)cell.Top, -1, -1);
                break;
        }
        return Task.FromResult<string?>(progId.Split('.')[0]);
    }

    static async Task<string?> Ftp(ShareItem i)
    {
        var s = MaxSnapr.App.Settings.Output.Ftp;
        var file = i.EnsureFile();
        var folder = s.RemoteFolder.Trim('/');
        var remote = $"ftp://{s.Host}:{s.Port}/" + (folder.Length > 0 ? folder + "/" : "") + Uri.EscapeDataString(Path.GetFileName(file));
#pragma warning disable SYSLIB0014 // FtpWebRequest is still the only FTP client in the BCL
        var req = (FtpWebRequest)WebRequest.Create(remote);
#pragma warning restore SYSLIB0014
        req.Method = WebRequestMethods.Ftp.UploadFile;
        req.Credentials = new NetworkCredential(s.User, Secrets.Unprotect(s.PasswordProtected));
        req.EnableSsl = s.UseTls;
        req.UsePassive = s.Passive;
        req.UseBinary = true;
        await using (var rs = await req.GetRequestStreamAsync())
        await using (var fs = File.OpenRead(file)) await fs.CopyToAsync(rs);
        using var resp = (FtpWebResponse)await req.GetResponseAsync();
        return s.PublicUrlPrefix.Length > 0 ? s.PublicUrlPrefix.TrimEnd('/') + "/" + Uri.EscapeDataString(Path.GetFileName(file)) : remote;
    }

    static async Task<string?> HttpUpload(ShareItem i)
    {
        var s = MaxSnapr.App.Settings.Output.Http;
        var file = i.EnsureFile();
        using var content = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(file));
        bytes.Headers.ContentType = new MediaTypeHeaderValue(MimeOf(file));
        content.Add(bytes, s.FieldName, Path.GetFileName(file));
        using var req = new HttpRequestMessage(HttpMethod.Post, s.Url) { Content = content };
        if (s.AuthHeader.Length > 0)
        {
            // Never send credentials in clear text, except to this same computer.
            var uri = new Uri(s.Url);
            if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
                throw new InvalidOperationException("Por seguridad, la cabecera de autenticación solo se envía a direcciones https://.");
            var parts = s.AuthHeader.Split(':', 2);
            if (parts.Length == 2) req.Headers.TryAddWithoutValidation(parts[0].Trim(), parts[1].Trim());
        }
        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        resp.EnsureSuccessStatusCode();
        try
        {
            JsonNode? node = JsonNode.Parse(body);
            foreach (var part in s.LinkPath.Split('.', StringSplitOptions.RemoveEmptyEntries)) node = node?[part];
            if (node != null) return node.GetValue<string>();
        }
        catch { }
        return body.Trim().StartsWith("http") ? body.Trim() : s.Url;
    }

    static async Task<string?> Slack(ShareItem i)
    {
        var s = MaxSnapr.App.Settings.Output.Slack;
        var token = Secrets.Unprotect(s.TokenProtected);
        var file = i.EnsureFile();
        var len = new FileInfo(file).Length;
        var auth = new AuthenticationHeaderValue("Bearer", token);

        using var r1 = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/files.getUploadURLExternal")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["filename"] = Path.GetFileName(file), ["length"] = len.ToString() }),
        };
        r1.Headers.Authorization = auth;
        var j1 = JsonNode.Parse(await (await Http.SendAsync(r1)).Content.ReadAsStringAsync())!;
        if (j1["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException("Slack: " + j1["error"]);
        var uploadUrl = j1["upload_url"]!.GetValue<string>();
        var fileId = j1["file_id"]!.GetValue<string>();

        using (var up = await Http.PostAsync(uploadUrl, new ByteArrayContent(await File.ReadAllBytesAsync(file)))) up.EnsureSuccessStatusCode();

        var files = new JsonArray(new JsonObject { ["id"] = fileId, ["title"] = i.Name });
        var form = new Dictionary<string, string> { ["files"] = files.ToJsonString() };
        if (s.ChannelId.Length > 0) form["channel_id"] = s.ChannelId;
        using var r3 = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/files.completeUploadExternal") { Content = new FormUrlEncodedContent(form) };
        r3.Headers.Authorization = auth;
        var j3 = JsonNode.Parse(await (await Http.SendAsync(r3)).Content.ReadAsStringAsync())!;
        if (j3["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException("Slack: " + j3["error"]);
        return j3["files"]?[0]?["permalink"]?.GetValue<string>() ?? "Slack";
    }

    static string MimeOf(string f) => Path.GetExtension(f).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff",
        ".pdf" => "application/pdf", ".mp4" => "video/mp4", _ => "application/octet-stream",
    };
}
