using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MaxSnapr.Services;

namespace MaxSnapr.Views;

/// <summary>"New version available" with its release notes, download-and-install, later, and skip.</summary>
public sealed class UpdateWindow : DialogWindow
{
    readonly UpdateInfo _u;
    readonly Button _install, _later, _skip;
    readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed, Maximum = 1 };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    CancellationTokenSource? _cts;
    bool _failed;

    public UpdateWindow(UpdateInfo u) : base("Actualización de MaxSnapr", 560, okCancel: false)
    {
        _u = u;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var logo = new Image { Source = EditorWindow.Logo, Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(logo, Dock.Left);
        head.Children.Add(logo);
        head.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Hay una versión nueva de MaxSnapr", FontSize = 18, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = $"{u.Version.ToString(3)} · tienes la {Updates.CurrentVersion.ToString(3)}", Foreground = Res("Fg.Secondary") },
            },
        });
        Body.Children.Add(head);
        Body.Children.Add(new TextBlock { Text = "Novedades", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
        Body.Children.Add(new Border
        {
            Background = Res("Bg.Input"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Child = new ScrollViewer { MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12, 8, 12, 8), Content = Notes(u.Notes) },
        });
        Body.Children.Add(_bar);
        Body.Children.Add(_status);

        _install = new Button { Content = "Descargar e instalar", Style = (Style)Application.Current.FindResource("AccentButton"), MinWidth = 160, IsDefault = true };
        _install.Click += async (_, _) =>
        {
            if (_failed) OpenPage(u.PageUrl);
            else await InstallAsync();
        };
        _later = new Button { Content = "Más tarde", Style = (Style)Application.Current.FindResource("OutlineButton"), MinWidth = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        _later.Click += (_, _) => Close();
        _skip = new Button { Content = "Omitir esta versión", Style = (Style)Application.Current.FindResource("FlatButton"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
        _skip.Click += (_, _) => { App.Settings.SkippedVersion = u.Tag; App.Settings.Save(); Close(); };
        Buttons.Children.Add(_skip);
        Buttons.Children.Add(_install);
        Buttons.Children.Add(_later);
        Closed += (_, _) => _cts?.Cancel();
    }

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    async System.Threading.Tasks.Task InstallAsync()
    {
        _install.IsEnabled = _skip.IsEnabled = false;
        _bar.Visibility = Visibility.Visible;
        _status.Text = "Descargando…";
        _cts = new CancellationTokenSource();
        try
        {
            var path = await Updates.DownloadAsync(_u, new Progress<double>(v => _bar.Value = v), _cts.Token);
            _status.Text = "Descarga verificada. Instalando: MaxSnapr se cerrará y volverá a abrirse.";
            await System.Threading.Tasks.Task.Delay(800);
            Updates.RunInstaller(path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Actualizar", ex);
            _bar.Visibility = Visibility.Collapsed;
            _status.Text = "No se pudo descargar la actualización: " + ex.Message;
            _install.Content = "Abrir la página de descargas";
            _install.IsEnabled = true;
            _failed = true;
        }
    }

    static void OpenPage(string url)
    {
        if (Updates.IsTrustedAsset(url) || url.StartsWith(Updates.ReleasesPage, StringComparison.OrdinalIgnoreCase))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Shows the release notes' simple Markdown (headings, bullets, bold) as formatted text.</summary>
    internal static FrameworkElement Notes(string markdown)
    {
        var panel = new StackPanel();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            panel.Children.Add(new TextBlock { Text = "Mejoras y correcciones.", TextWrapping = TextWrapping.Wrap });
            return panel;
        }
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            // Download tables, checksums and quotes are for the web page, not for this window.
            if (line.StartsWith("|") || line.StartsWith(">") || line.StartsWith("<")) continue;
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            if (line.StartsWith("#"))
            {
                var title = line.TrimStart('#').Trim();
                if (title.Equals("Descargar", StringComparison.OrdinalIgnoreCase)) break; // the notes end where the download help starts
                if (title.Equals("Novedades", StringComparison.OrdinalIgnoreCase)) continue; // the window already says it
                tb.FontWeight = FontWeights.SemiBold;
                tb.FontSize = 14;
                tb.Margin = new Thickness(0, 8, 0, 2);
                Inline(tb, title);
            }
            else if (Regex.IsMatch(line, @"^\s*[-*•]\s+"))
            {
                tb.Margin = new Thickness(Regex.Match(line, @"^\s*").Length > 1 ? 30 : 12, 1, 0, 1);
                tb.Inlines.Add(new Run("•  "));
                Inline(tb, Regex.Replace(line, @"^\s*[-*•]\s+", ""));
            }
            else Inline(tb, line);
            panel.Children.Add(tb);
        }
        if (panel.Children.Count == 0) panel.Children.Add(new TextBlock { Text = "Mejoras y correcciones.", TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    /// <summary>**bold** and `code`; links keep only their text.</summary>
    static void Inline(TextBlock tb, string text)
    {
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        foreach (var part in Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)"))
        {
            if (part.Length == 0) continue;
            if (part.StartsWith("**") && part.EndsWith("**")) tb.Inlines.Add(new Bold(new Run(part[2..^2])));
            else if (part.StartsWith("`") && part.EndsWith("`")) tb.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Consolas") });
            else tb.Inlines.Add(new Run(part));
        }
    }
}
