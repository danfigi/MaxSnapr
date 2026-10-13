using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MaxSnapr.Library;
using MaxSnapr.Services;
using Microsoft.Win32;

namespace MaxSnapr.Views;

/// <summary>Slim capture tab docked to the top edge of the primary screen; expands on hover.</summary>
public sealed class EdgeWidget : Window
{
    readonly Border _tab;
    readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(6, 4, 6, 6) };

    public EdgeWidget()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        void B(string glyph, string tip, Action a)
        {
            var b = new Button { Style = (Style)Application.Current.FindResource("FlatButton"), Width = 40, Height = 36, Padding = new Thickness(0), ToolTip = tip,
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 17, Foreground = Brushes.White } };
            b.Click += (_, _) => { Collapse(); a(); };
            _buttons.Children.Add(b);
        }
        B("", "Capturar imagen", () => _ = App.Current.Capture.RunAsync(App.Settings.Current.AsImagePreset()));
        B("", "Grabar vídeo", () => _ = App.Current.Capture.RunAsync(new CapturePreset { RecordVideo = true }));
        B("", "Extraer texto", () => _ = App.Current.Capture.RunAsync(new CapturePreset { GrabText = true, OpenInEditor = false }));
        B("", "Captura de pasos", () => App.Current.StartStepCapture());
        B("", "Ventana de captura", () => App.Current.ShowCaptureWindow());
        B("", "Abrir el editor", () => App.Current.ShowEditor());
        var grip = new Border { Width = 46, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0xC0, 255, 255, 255)), Margin = new Thickness(0, 4, 0, 3), HorizontalAlignment = HorizontalAlignment.Center };
        _tab = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x24, 0x27, 0x2B)), CornerRadius = new CornerRadius(0, 0, 10, 10),
            Child = new StackPanel { Children = { _buttons, grip } }, MinWidth = 90,
        };
        Content = _tab;
        MouseEnter += (_, _) => { _buttons.Visibility = Visibility.Visible; Reposition(); };
        MouseLeave += (_, _) => Collapse();
        SourceInitialized += (_, _) => Video.CaptureExclusion.Apply(this);
        Loaded += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
    }

    void Collapse() { _buttons.Visibility = Visibility.Collapsed; }

    void Reposition()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top;
    }
}

internal static class PresetExtensions
{
    // Edge widget "image" button: current preset but forced to image mode.
    public static CapturePreset AsImagePreset(this CapturePreset p) => new()
    {
        Selection = p.Selection, DelaySeconds = p.DelaySeconds, IncludeCursor = p.IncludeCursor, CopyToClipboard = p.CopyToClipboard, OpenInEditor = p.OpenInEditor,
        FixedWidth = p.FixedWidth, FixedHeight = p.FixedHeight, EffectBorder = p.EffectBorder, EffectShadow = p.EffectShadow, EffectTornEdge = p.EffectTornEdge, ShareToFolder = p.ShareToFolder,
    };
}

/// <summary>First-run welcome with the essentials.</summary>
public sealed class WelcomeWindow : DialogWindow
{
    public WelcomeWindow() : base("Bienvenido a MaxSnapr", 560, okCancel: false)
    {
        Body.Children.Add(new Image { Source = EditorWindow.Logo, Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Left });
        Body.Children.Add(new TextBlock { Text = "Capture, anote y comparta en segundos", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 10) });
        var tips = new (string glyph, string title, string text)[]
        {
            ("", $"Pulse {App.Current.CaptureHotkeyText}", "Arrastre para una región, haga clic en una ventana o pulse Intro para la pantalla completa. La barra flotante permite copiar, extraer texto, grabar vídeo o capturar con desplazamiento."),
            ("", "Anote en el editor", "Flechas, globos, pasos numerados, difuminado, lupa… Todo sigue siendo editable y se guarda automáticamente en la biblioteca."),
            ("", "Grabe vídeo y GIF", $"{App.Settings.VideoHotkey} graba la pantalla con audio y cámara. Después puede recortar, cortar tramos o crear un GIF."),
            ("", "Comparta", "Copiar todo, carpeta con nombres automáticos, correo, Office, Slack, FTP o un servidor propio desde el botón Compartir."),
        };
        foreach (var (g, t, x) in tips)
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var icon = new TextBlock { Text = g, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 22, Foreground = (Brush)Application.Current.FindResource("Accent"), Width = 40, VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new StackPanel { Children = { new TextBlock { Text = t, FontWeight = FontWeights.SemiBold }, new TextBlock { Text = x, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("Fg.Secondary") } } });
            Body.Children.Add(row);
        }
        var dont = new CheckBox { Content = "No volver a mostrar", Margin = new Thickness(0, 16, 0, 0) };
        Body.Children.Add(dont);
        var start = new Button { Content = "Empezar", Style = (Style)Application.Current.FindResource("AccentButton"), MinWidth = 110, IsDefault = true, IsCancel = true };
        start.Click += (_, _) => { if (dont.IsChecked == true) { App.Settings.ShowWelcome = false; App.Settings.Save(); } Close(); };
        Buttons.Children.Add(start);
    }
}

/// <summary>Per-user Explorer integration (no admin rights).</summary>
public static class Integration
{
    const string Classes = @"Software\Classes";
    static string Exe => Environment.ProcessPath ?? "MaxSnapr.exe";

    public static bool SnaprAssociated => Registry.CurrentUser.OpenSubKey($@"{Classes}\.snapr")?.GetValue("") as string == "MaxSnapr.Document";
    public static bool ContextMenuInstalled => Registry.CurrentUser.OpenSubKey($@"{Classes}\SystemFileAssociations\image\shell\MaxSnaprEdit") != null;

    public static void SetSnaprAssociation(bool on)
    {
        try
        {
            if (on)
            {
                using (var k = Registry.CurrentUser.CreateSubKey($@"{Classes}\.snapr")) k.SetValue("", "MaxSnapr.Document");
                using (var k = Registry.CurrentUser.CreateSubKey($@"{Classes}\MaxSnapr.Document")) k.SetValue("", "Documento de MaxSnapr");
                using (var k = Registry.CurrentUser.CreateSubKey($@"{Classes}\MaxSnapr.Document\DefaultIcon")) k.SetValue("", $"\"{Exe}\",0");
                using (var k = Registry.CurrentUser.CreateSubKey($@"{Classes}\MaxSnapr.Document\shell\open\command")) k.SetValue("", $"\"{Exe}\" \"%1\"");
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\.snapr", false);
                Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\MaxSnapr.Document", false);
            }
            Notify();
        }
        catch (Exception ex) { Log.Error("Asociación .snapr", ex); }
    }

    public static void SetContextMenu(bool on)
    {
        try
        {
            var path = $@"{Classes}\SystemFileAssociations\image\shell\MaxSnaprEdit";
            if (on)
            {
                using (var k = Registry.CurrentUser.CreateSubKey(path)) { k.SetValue("MUIVerb", "Editar con MaxSnapr"); k.SetValue("Icon", $"\"{Exe}\",0"); }
                using (var k = Registry.CurrentUser.CreateSubKey(path + @"\command")) k.SetValue("", $"\"{Exe}\" \"%1\"");
            }
            else Registry.CurrentUser.DeleteSubKeyTree(path, false);
            Notify();
        }
        catch (Exception ex) { Log.Error("Menú contextual", ex); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern void SHChangeNotify(int e, int f, IntPtr a, IntPtr b);
    static void Notify() => SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);
}

/// <summary>
/// Local web page the phone opens through a QR code to upload photos into the library. It listens only
/// on the local network address while this window is open, needs a random 128-bit token, checks it
/// before reading any upload, limits size, time and concurrent connections, and stops after 30 idle minutes.
/// </summary>
public sealed class MobileReceiveWindow : DialogWindow
{
    TcpListener? _listener;
    readonly CancellationTokenSource _cts = new();
    readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    readonly SemaphoreSlim _slots = new(4);
    readonly System.Windows.Threading.DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(30) };
    DateTime _lastActivity = DateTime.UtcNow;
    const long MaxUpload = 512L * 1024 * 1024;
    static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".mp4" };
    readonly ListBox _received = new() { Height = 120 };
    readonly TextBlock _url = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };

    /// <summary>Address the phone opens (null when there is no network).</summary>
    public string? Url { get; private set; }

    /// <param name="bindAddress">Self-test only: listen on this address (loopback) instead of the LAN.</param>
    public MobileReceiveWindow(string? bindAddress = null) : base("Recibir desde el móvil", 420, okCancel: false)
    {
        Topmost = true;
        var ip = bindAddress ?? LanAddress();
        if (ip == null)
        {
            Caption("No se ha encontrado ninguna red local. Conecte el equipo a una red Wi-Fi o Ethernet.");
        }
        else
        {
            // Only the LAN address: not VPN, other adapters or the internet.
            _listener = new TcpListener(IPAddress.Parse(ip), 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            var url = $"http://{ip}:{port}/?k={_token}";
            Caption("Escanee el código con la cámara del móvil (misma red Wi-Fi) y elija las fotos. Windows puede pedir permiso en el firewall la primera vez.");
            Body.Children.Add(new Image { Source = Qr(url), Width = 220, Height = 220, Margin = new Thickness(0, 10, 0, 10) });
            _url.Text = url;
            Url = url;
            Body.Children.Add(_url);
            Caption("Recibidas");
            Body.Children.Add(_received);
            _ = AcceptLoop();
            _idle.Tick += (_, _) => { if (DateTime.UtcNow - _lastActivity > TimeSpan.FromMinutes(30)) Close(); };
            _idle.Start();
        }
        var close = new Button { Content = "Terminar", Style = (Style)Application.Current.FindResource("AccentButton"), MinWidth = 100, IsCancel = true };
        close.Click += (_, _) => Close();
        Buttons.Children.Add(close);
        Closed += (_, _) => { _idle.Stop(); _cts.Cancel(); _listener?.Stop(); };
    }

    static ImageSource Qr(string text)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        var img = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        img.Freeze();
        return img;
    }

    static string? LanAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString();

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => Handle(client));
        }
    }

    async Task Handle(TcpClient client)
    {
        using var _ = client;
        if (!await _slots.WaitAsync(0)) return; // too many connections at once
        try
        {
            using var time = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            time.CancelAfter(TimeSpan.FromSeconds(20));
            var stream = client.GetStream();
            var (method, target, headers) = await ReadHead(stream, time.Token);
            // The token is checked before anything else is read or stored.
            if (!HasToken(target)) { await Respond(stream, 403, "text/plain; charset=utf-8", "Enlace no válido"u8.ToArray()); return; }
            _lastActivity = DateTime.UtcNow;
            var route = target.Split('?')[0];
            if (method == "GET" && route == "/") { await Respond(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page(""))); return; }
            if (method == "POST" && route == "/upload" && headers.TryGetValue("content-type", out var ct)
                && ct.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) && ct.Contains("boundary="))
            {
                if (!headers.TryGetValue("content-length", out var lenS) || !long.TryParse(lenS, out var len) || len <= 0)
                { await Respond(stream, 411, "text/plain; charset=utf-8", "Falta el tamaño"u8.ToArray()); return; }
                if (len > MaxUpload) { await Respond(stream, 413, "text/plain; charset=utf-8", "Demasiado grande (máximo 512 MB por envío)"u8.ToArray()); return; }
                time.CancelAfter(TimeSpan.FromMinutes(10));
                var body = await ReadBody(stream, len, time.Token);
                var boundary = "--" + ct[(ct.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase) + 9)..].Split(';')[0].Trim().Trim('"');
                int count = 0, skipped = 0;
                foreach (var (name, data) in Multipart(body, boundary))
                {
                    var safe = Naming.Safe(Path.GetFileName(name));
                    if (!Allowed.Contains(Path.GetExtension(safe))) { skipped++; continue; }
                    var dir = Path.Combine(Path.GetTempPath(), "MaxSnapr", "mobile");
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff} {safe}");
                    await File.WriteAllBytesAsync(path, data, time.Token);
                    await Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        try
                        {
                            var ext = Path.GetExtension(path).ToLowerInvariant();
                            var item = ext is ".mp4" ? await LibraryStore.Instance.AddMediaAsync(path, "Móvil") : LibraryStore.Instance.Import(path);
                            item.SourceApp = "Móvil";
                            item.InTray = true;
                            LibraryStore.Instance.Save();
                            _received.Items.Insert(0, $"{DateTime.Now:T}  {safe}");
                        }
                        catch (Exception ex) { _received.Items.Insert(0, $"No admitido: {safe} ({ex.Message})"); }
                        finally { try { File.Delete(path); } catch { } } // the library keeps its own copy
                    }).Task.Unwrap();
                    count++;
                }
                var msg = $"Recibidas {count} imagen(es)." + (skipped > 0 ? $" {skipped} archivo(s) no admitidos." : "") + " Puede enviar más.";
                await Respond(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page(msg)));
                return;
            }
            await Respond(stream, 400, "text/plain; charset=utf-8", "Solicitud no válida"u8.ToArray());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Servidor móvil", ex); }
        finally { _slots.Release(); }
    }

    bool HasToken(string target)
    {
        int q = target.IndexOf('?');
        if (q < 0) return false;
        foreach (var pair in target[(q + 1)..].Split('&'))
            if (pair.StartsWith("k="))
                return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pair[2..]), Encoding.ASCII.GetBytes(_token));
        return false;
    }

    string Page(string message) => $$"""
        <!doctype html><html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>MaxSnapr</title><style>body{font-family:system-ui,sans-serif;background:#1e2024;color:#eceef1;margin:0;padding:28px}
        h1{font-size:22px}.card{background:#26292e;border-radius:14px;padding:22px}input{margin:18px 0;color:#eceef1}
        button{background:#2e9bff;color:#fff;border:0;border-radius:10px;padding:14px 22px;font-size:17px;width:100%}p{color:#a3a9b2}</style></head>
        <body><div class="card"><h1>Enviar a MaxSnapr</h1><p>{{WebUtility.HtmlEncode(message)}}</p>
        <form method="post" enctype="multipart/form-data" action="/upload?k={{_token}}"><input type="file" name="f" accept="image/*,video/mp4" multiple>
        <button type="submit">Enviar</button></form></div></body></html>
        """;

    static async Task<(string method, string target, Dictionary<string, string> headers)> ReadHead(NetworkStream s, CancellationToken ct)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        // Header block up to CRLFCRLF, at most 16 KB.
        while (buf.Count < 16384)
        {
            if (await s.ReadAsync(one, ct) == 0) break;
            buf.Add(one[0]);
            int n = buf.Count;
            if (n >= 4 && buf[n - 4] == 13 && buf[n - 3] == 10 && buf[n - 2] == 13 && buf[n - 1] == 10) break;
        }
        var lines = Encoding.ASCII.GetString(buf.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1)) { int i = l.IndexOf(':'); if (i > 0) headers[l[..i].Trim()] = l[(i + 1)..].Trim(); }
        return (first.ElementAtOrDefault(0) ?? "", first.ElementAtOrDefault(1) ?? "", headers);
    }

    static async Task<byte[]> ReadBody(NetworkStream s, long len, CancellationToken ct)
    {
        var body = new byte[len];
        int read = 0;
        while (read < len)
        {
            int r = await s.ReadAsync(body.AsMemory(read), ct);
            if (r == 0) throw new EndOfStreamException("Envío interrumpido");
            read += r;
        }
        return body;
    }

    static IEnumerable<(string name, byte[] data)> Multipart(byte[] body, string boundary)
    {
        var b = Encoding.ASCII.GetBytes(boundary);
        int pos = IndexOf(body, b, 0);
        while (pos >= 0)
        {
            int start = pos + b.Length + 2;
            int headerEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), start);
            if (headerEnd < 0) yield break;
            var header = Encoding.UTF8.GetString(body, start, headerEnd - start);
            int next = IndexOf(body, b, headerEnd);
            if (next < 0) yield break;
            int dataStart = headerEnd + 4, dataEnd = next - 2;
            // Browsers quote the name; other clients may not.
            var m = System.Text.RegularExpressions.Regex.Match(header, "filename=(?:\"([^\"]*)\"|([^;\\r\\n]+))");
            var fileName = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value.Trim()) : "";
            if (fileName.Length > 0 && dataEnd > dataStart)
                yield return (fileName, body[dataStart..dataEnd]);
            pos = next;
        }
    }

    static int IndexOf(byte[] hay, byte[] needle, int from)
    {
        if (from > hay.Length) return -1;
        int i = hay.AsSpan(from).IndexOf(needle);
        return i < 0 ? -1 : from + i;
    }

    static async Task Respond(NetworkStream s, int code, string type, byte[] body)
    {
        // The token travels in the URL, so keep it out of caches and referrers; the page needs no scripts.
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {(code == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\n"
            + "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; form-action 'self'\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head);
        await s.WriteAsync(body);
    }
}
