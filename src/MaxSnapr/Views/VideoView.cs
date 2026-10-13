using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaxSnapr.Editor.Model;
using MaxSnapr.Library;
using MaxSnapr.Services;
using MaxSnapr.Video;
using Microsoft.Win32;

namespace MaxSnapr.Views;

/// <summary>Timeline with a playhead and a selection range (start/end handles).</summary>
public sealed class TimelineBar : FrameworkElement
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan Position { get; set; }
    public TimeSpan SelStart { get; set; }
    public TimeSpan SelEnd { get; set; }
    public event EventHandler<TimeSpan>? Seek;
    public event EventHandler? SelectionChanged;
    int _drag = -1; // 0 playhead, 1 start, 2 end
    const double Pad = 10;

    public TimelineBar() { Height = 46; Cursor = Cursors.Hand; }

    double X(TimeSpan t) => Pad + (ActualWidth - Pad * 2) * Math.Clamp(t.TotalSeconds / Math.Max(0.001, Duration.TotalSeconds), 0, 1);
    TimeSpan T(double x) => TimeSpan.FromSeconds(Math.Clamp((x - Pad) / Math.Max(1, ActualWidth - Pad * 2), 0, 1) * Duration.TotalSeconds);

    protected override void OnRender(DrawingContext dc)
    {
        var bg = (Brush)FindResource("Bg.Pressed");
        var accent = (Brush)FindResource("Accent");
        double y = 18, h = 12;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.DrawRoundedRectangle(bg, null, new Rect(Pad, y, ActualWidth - Pad * 2, h), 4, 4);
        var sel = new Rect(new Point(X(SelStart), y), new Point(X(SelEnd), y + h));
        var selBrush = new SolidColorBrush(Color.FromArgb(0x90, 0x2E, 0x9B, 0xFF));
        dc.DrawRectangle(selBrush, null, sel);
        foreach (var x in new[] { X(SelStart), X(SelEnd) })
            dc.DrawRoundedRectangle(accent, null, new Rect(x - 4, y - 6, 8, h + 12), 3, 3);
        double px = X(Position);
        dc.DrawLine(new Pen(Brushes.White, 2), new Point(px, 4), new Point(px, ActualHeight - 4));
        dc.DrawEllipse(Brushes.White, new Pen(accent, 2), new Point(px, 6), 6, 6);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var x = e.GetPosition(this).X;
        _drag = Math.Abs(x - X(SelStart)) < 8 ? 1 : Math.Abs(x - X(SelEnd)) < 8 ? 2 : 0;
        CaptureMouse();
        Apply(x);
    }

    protected override void OnMouseMove(MouseEventArgs e) { if (_drag >= 0 && e.LeftButton == MouseButtonState.Pressed) Apply(e.GetPosition(this).X); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { _drag = -1; ReleaseMouseCapture(); }

    void Apply(double x)
    {
        var t = T(x);
        if (_drag == 1) { SelStart = t < SelEnd ? t : SelEnd; SelectionChanged?.Invoke(this, EventArgs.Empty); }
        else if (_drag == 2) { SelEnd = t > SelStart ? t : SelStart; SelectionChanged?.Invoke(this, EventArgs.Empty); }
        Position = _drag == 0 ? t : _drag == 1 ? SelStart : SelEnd;
        Seek?.Invoke(this, Position);
        InvalidateVisual();
    }
}

/// <summary>Player and editor for MP4 recordings and animated GIFs in the library.</summary>
public sealed class VideoView : Grid
{
    readonly MediaElement _media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, ScrubbingEnabled = true };
    readonly Image _gifImage = new() { Stretch = Stretch.Uniform };
    readonly TimelineBar _timeline = new();
    readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), Width = 150 };
    readonly TextBlock _selText = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("Fg.Secondary") };
    readonly Button _play;
    Button? _mute;
    readonly Border _busy;
    readonly TextBlock _busyText = new() { Foreground = Brushes.White, FontSize = 15 };
    readonly ProgressBar _progress = new() { Width = 260, Height = 6, Margin = new Thickness(0, 10, 0, 0), Maximum = 1 };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    List<GifPlayer.Frame>? _gif;
    int _gifIndex;
    DateTime _gifFrameStarted;
    bool _playing;
    LibraryItem? _item;
    EditorWindow? _editor;
    CancellationTokenSource? _cts;

    static T Res<T>(string k) => (T)Application.Current.FindResource(k);

    public VideoView()
    {
        Background = Res<Brush>("Bg.Canvas");
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var stage = new Grid { Margin = new Thickness(24) };
        stage.Children.Add(_media);
        stage.Children.Add(_gifImage);
        Children.Add(stage);

        var bar = new StackPanel { Background = Res<Brush>("Bg.Toolbar") };
        SetRow(bar, 1);
        _timeline.Margin = new Thickness(14, 6, 14, 0);
        bar.Children.Add(_timeline);
        _selText.Margin = new Thickness(24, 0, 14, 2);
        bar.Children.Add(_selText);
        var row = new DockPanel { Margin = new Thickness(14, 2, 14, 10) };
        _play = IconButton("\uE768", "Reproducir / pausa (Espacio)", TogglePlay);
        row.Children.Add(IconButton("\uE892", "Ir al principio (Inicio)", () => SeekTo(TimeSpan.Zero)));
        row.Children.Add(IconButton("\uE76B", "Fotograma anterior (←)", () => Step(-1)));
        row.Children.Add(_play);
        row.Children.Add(IconButton("\uE76C", "Fotograma siguiente (→)", () => Step(1)));
        row.Children.Add(IconButton("\uE893", "Ir al final (Fin)", () => SeekTo(_timeline.Duration - TimeSpan.FromMilliseconds(40))));
        _mute = IconButton("\uE767", "Silenciar", () => { _media.IsMuted = !_media.IsMuted; ((TextBlock)_mute!.Content).Text = _media.IsMuted ? "\uE74F" : "\uE767"; });
        row.Children.Add(_mute);
        row.Children.Add(_time);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Right);
        actions.Children.Add(TextButton("\uE722", "Guardar fotograma", SaveFrame));
        actions.Children.Add(TextButton("\uE7A8", "Recortar a la selección", () => Run("Recortando…", Trim)));
        actions.Children.Add(TextButton("\uE8C6", "Quitar la selección", () => Run("Cortando…", Cut)));
        actions.Children.Add(TextButton("\uE710", "Selección como nuevo", () => Run("Guardando…", SaveSelectionAsNew)));
        actions.Children.Add(TextButton("\uE8B9", "Crear GIF", ShowGifMenu));
        actions.Children.Add(TextButton("\uE71B", "Combinar…", () => Run("Combinando…", Combine)));
        actions.Children.Add(TextButton("\uE74E", "Exportar…", Export));
        row.Children.Add(actions);
        bar.Children.Add(row);
        Children.Add(bar);

        _busy = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), Visibility = Visibility.Collapsed,
            Child = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { _busyText, _progress } },
        };
        SetRowSpan(_busy, 2);
        Children.Add(_busy);

        _timeline.Seek += (_, t) => SeekTo(t);
        _timeline.SelectionChanged += (_, _) => UpdateTexts();
        _media.MediaOpened += (_, _) =>
        {
            if (_media.NaturalDuration.HasTimeSpan) SetDuration(_media.NaturalDuration.TimeSpan);
            _media.Pause();
            _media.Position = TimeSpan.Zero;
        };
        _media.MediaEnded += (_, _) => { _playing = false; UpdatePlayIcon(); };
        _timer.Tick += (_, _) => Tick();
        Focusable = true;
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.Left: Step(-1); break;
                case Key.Right: Step(1); break;
                case Key.Home: SeekTo(TimeSpan.Zero); break;
                case Key.End: SeekTo(_timeline.Duration - TimeSpan.FromMilliseconds(40)); break;
                default: return;
            }
            e.Handled = true;
        };
    }

    Button IconButton(string glyph, string tip, Action a)
    {
        var b = new Button { Style = Res<Style>("FlatButton"), Width = 38, Height = 34, Padding = new Thickness(0), ToolTip = tip, HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new TextBlock { Text = glyph, FontFamily = Res<FontFamily>("IconFont"), FontSize = 16 } };
        b.Click += (_, _) => a();
        return b;
    }

    Button TextButton(string glyph, string text, Action a)
    {
        var b = new Button { Style = Res<Style>("OutlineButton"), Margin = new Thickness(6, 0, 0, 4), Padding = new Thickness(10, 5, 10, 5) };
        b.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = glyph, FontFamily = Res<FontFamily>("IconFont"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0) } } };
        b.Click += (_, _) => a();
        return b;
    }

    public LibraryItem? Item => _item;

    public void Load(LibraryItem item, EditorWindow editor)
    {
        Unload();
        _item = item;
        _editor = editor;
        if (item.IsGif)
        {
            _media.Visibility = Visibility.Collapsed;
            _gifImage.Visibility = Visibility.Visible;
            _gif = GifPlayer.Load(item.FullPath);
            _gifIndex = 0;
            _gifImage.Source = _gif.FirstOrDefault()?.Image;
            SetDuration(TimeSpan.FromMilliseconds(_gif.Sum(f => f.DelayMs)));
        }
        else
        {
            _gif = null;
            _gifImage.Visibility = Visibility.Collapsed;
            _media.Visibility = Visibility.Visible;
            _media.Source = new Uri(item.FullPath);
            _media.Play();
            _media.Pause();
            SetDuration(TimeSpan.FromSeconds(Math.Max(0.1, item.DurationSeconds)));
        }
        _timer.Start();
        Focus();
    }

    public void Unload()
    {
        _timer.Stop();
        _playing = false;
        _media.Stop();
        _media.Source = null;
        _gif = null;
        _gifImage.Source = null;
        _item = null;
        UpdatePlayIcon();
    }

    void SetDuration(TimeSpan d)
    {
        _timeline.Duration = d;
        _timeline.SelStart = TimeSpan.Zero;
        _timeline.SelEnd = d;
        _timeline.Position = TimeSpan.Zero;
        _timeline.InvalidateVisual();
        UpdateTexts();
    }

    TimeSpan Position => _gif != null ? TimeSpan.FromMilliseconds(_gif.Take(_gifIndex).Sum(f => f.DelayMs)) : _media.Position;

    void SeekTo(TimeSpan t)
    {
        if (_gif != null)
        {
            int acc = 0;
            _gifIndex = 0;
            for (int i = 0; i < _gif.Count; i++) { if (acc + _gif[i].DelayMs > t.TotalMilliseconds) { _gifIndex = i; break; } acc += _gif[i].DelayMs; _gifIndex = i; }
            _gifImage.Source = _gif[_gifIndex].Image;
        }
        else _media.Position = t;
        UpdateTexts();
    }

    void Step(int frames)
    {
        _playing = false;
        _media.Pause();
        UpdatePlayIcon();
        if (_gif != null)
        {
            _gifIndex = Math.Clamp(_gifIndex + frames, 0, _gif.Count - 1);
            _gifImage.Source = _gif[_gifIndex].Image;
        }
        else
        {
            var t = _media.Position + TimeSpan.FromSeconds(frames / 30.0);
            _media.Position = t < TimeSpan.Zero ? TimeSpan.Zero : t > _timeline.Duration ? _timeline.Duration : t;
        }
        UpdateTexts();
    }

    void TogglePlay()
    {
        if (_item == null) return;
        _playing = !_playing;
        if (_gif == null)
        {
            if (_playing)
            {
                if (_media.Position >= _timeline.SelEnd - TimeSpan.FromMilliseconds(50)) _media.Position = _timeline.SelStart;
                _media.Play();
            }
            else _media.Pause();
        }
        _gifFrameStarted = DateTime.Now;
        UpdatePlayIcon();
    }

    void UpdatePlayIcon() => ((TextBlock)_play.Content).Text = _playing ? "\uE769" : "\uE768";

    void Tick()
    {
        if (_gif != null && _playing && _gif.Count > 0)
        {
            if ((DateTime.Now - _gifFrameStarted).TotalMilliseconds >= _gif[_gifIndex].DelayMs)
            {
                _gifIndex = (_gifIndex + 1) % _gif.Count;
                _gifImage.Source = _gif[_gifIndex].Image;
                _gifFrameStarted = DateTime.Now;
            }
        }
        if (_gif == null && _playing && _media.Position >= _timeline.SelEnd && _timeline.SelEnd < _timeline.Duration)
        {
            _media.Pause();
            _playing = false;
            UpdatePlayIcon();
        }
        _timeline.Position = Position;
        _timeline.InvalidateVisual();
        UpdateTexts();
    }

    static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";

    void UpdateTexts()
    {
        _time.Text = $"{Fmt(Position)} / {Fmt(_timeline.Duration)}";
        var sel = _timeline.SelEnd - _timeline.SelStart;
        _selText.Text = sel < _timeline.Duration - TimeSpan.FromMilliseconds(30) ? $"Selección: {Fmt(_timeline.SelStart)} – {Fmt(_timeline.SelEnd)} ({sel.TotalSeconds:0.0} s)" : "Arrastre los extremos azules para seleccionar un tramo";
    }

    // ---- operations -----------------------------------------------------------------------------

    async void Run(string message, Func<IProgress<double>, CancellationToken, Task> work)
    {
        if (_item == null) return;
        _playing = false;
        _media.Pause();
        _busyText.Text = message;
        _progress.Value = 0;
        _busy.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        try { await work(new Progress<double>(p => _progress.Value = p), _cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(message, ex);
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Vídeo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy.Visibility = Visibility.Collapsed; }
    }

    string TempOut(string ext) => Path.Combine(Path.GetDirectoryName(_item!.FullPath)!, $"{Path.GetFileNameWithoutExtension(_item.FullPath)} ({DateTime.Now:HHmmss}){ext}");

    async void SaveFrame()
    {
        if (_item == null) return;
        try
        {
            BitmapSource frame = _gif != null ? _gif[_gifIndex].Image : await VideoTools.FrameAsync(_item.FullPath, _media.Position);
            var doc = new EditorDocument(frame);
            var item = LibraryStore.Instance.Add(doc, $"Fotograma de {_item.Name}", _item.SourceApp);
            _editor?.OpenDocument(doc, item);
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Guardar fotograma"); }
    }

    async Task Replace(string newPath)
    {
        // Edits produce a new file and replace the library entry so the original stays recoverable from the recycle bin.
        var old = _item!;
        var item = await LibraryStore.Instance.AddMediaAsync(newPath, old.SourceTitle);
        item.Name = old.Name;
        item.Tags = old.Tags;
        LibraryStore.Instance.Delete(old);
        LibraryStore.Instance.Save();
        _editor?.OpenItem(item);
    }

    async Task Trim(IProgress<double> p, CancellationToken ct)
    {
        if (_gif != null) { await Replace(GifRange(_timeline.SelStart, _timeline.SelEnd, keep: true)); return; }
        var outp = TempOut(".mp4");
        await VideoTools.TrimAsync(_item!.FullPath, _timeline.SelStart, _timeline.SelEnd, outp, p, ct);
        await Replace(outp);
    }

    async Task Cut(IProgress<double> p, CancellationToken ct)
    {
        if (_gif != null) { await Replace(GifRange(_timeline.SelStart, _timeline.SelEnd, keep: false)); return; }
        var outp = TempOut(".mp4");
        await VideoTools.CutAsync(_item!.FullPath, _timeline.SelStart, _timeline.SelEnd, outp, p, ct);
        await Replace(outp);
    }

    async Task SaveSelectionAsNew(IProgress<double> p, CancellationToken ct)
    {
        string outp;
        if (_gif != null) outp = GifRange(_timeline.SelStart, _timeline.SelEnd, keep: true);
        else
        {
            outp = TempOut(".mp4");
            await VideoTools.TrimAsync(_item!.FullPath, _timeline.SelStart, _timeline.SelEnd, outp, p, ct);
        }
        var item = await LibraryStore.Instance.AddMediaAsync(outp, _item!.SourceTitle);
        _editor?.OpenItem(item);
    }

    /// <summary>Writes the frames inside (keep) or outside (!keep) the range as a new GIF.</summary>
    string GifRange(TimeSpan a, TimeSpan b, bool keep)
    {
        var outp = TempOut(".gif");
        int t = 0;
        var frames = new List<GifPlayer.Frame>();
        foreach (var f in _gif!)
        {
            bool inside = t >= a.TotalMilliseconds && t < b.TotalMilliseconds;
            if (inside == keep) frames.Add(f);
            t += f.DelayMs;
        }
        if (frames.Count == 0) throw new InvalidOperationException("La selección no contiene fotogramas.");
        using var fs = File.Create(outp);
        using var enc = new GifEncoder(fs, frames[0].Image.PixelWidth, frames[0].Image.PixelHeight);
        foreach (var f in frames) enc.AddFrame(Editor.Raster.RasterOps.Read(f.Image).Data, Math.Max(2, f.DelayMs / 10));
        return outp;
    }

    void ShowGifMenu()
    {
        if (_item == null || _gif != null) return;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        foreach (var (profile, i) in GifProfile.All.Select((p, i) => (p, i)))
        {
            var mi = new MenuItem { Header = profile.Name };
            mi.Click += (_, _) => Run("Creando GIF…", (p, ct) => MakeGif(profile.Fps, profile.MaxWidth, p, ct));
            menu.Items.Add(mi);
        }
        var custom = new MenuItem { Header = "Personalizado…" };
        custom.Click += (_, _) =>
        {
            var s = InputDialog.Ask(Window.GetWindow(this), "GIF personalizado", "Ancho máximo (px) y fotogramas por segundo, p. ej. «600 12»:", "600 12");
            var parts = s?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts?.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var fps))
                Run("Creando GIF…", (p, ct) => MakeGif(Math.Clamp(fps, 1, 30), Math.Clamp(w, 64, 3840), p, ct));
        };
        menu.Items.Add(custom);
        menu.IsOpen = true;
    }

    async Task MakeGif(int fps, int width, IProgress<double> p, CancellationToken ct)
    {
        var outp = TempOut(".gif");
        await VideoTools.ToGifAsync(_item!.FullPath, _timeline.SelStart, _timeline.SelEnd, fps, width, outp, p, ct);
        var item = await LibraryStore.Instance.AddMediaAsync(outp, _item.SourceTitle);
        _editor?.OpenItem(item);
    }

    async Task Combine(IProgress<double> p, CancellationToken ct)
    {
        if (_gif != null) throw new InvalidOperationException("Combinar está disponible para vídeos MP4.");
        var others = LibraryStore.Instance.Items.Where(i => i.IsVideo && i != _item).ToList();
        var dlg = new PickItemsDialog("Combinar vídeos", "Elija los vídeos que se añadirán al final, en este orden:", others) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Selected.Count == 0) return;
        var outp = TempOut(".mp4");
        await VideoTools.ConcatAsync(new[] { _item!.FullPath }.Concat(dlg.Selected.Select(i => i.FullPath)), outp, p, ct);
        var item = await LibraryStore.Instance.AddMediaAsync(outp, "Combinado");
        _editor?.OpenItem(item);
    }

    void Export()
    {
        if (_item == null) return;
        var ext = Path.GetExtension(_item.FullPath);
        var dlg = new SaveFileDialog { FileName = _item.Name, Filter = ext == ".gif" ? "GIF animado (*.gif)|*.gif" : "Vídeo MP4 (*.mp4)|*.mp4", DefaultExt = ext };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        File.Copy(_item.FullPath, dlg.FileName, true);
    }
}

/// <summary>Checklist of library items in a chosen order.</summary>
public sealed class PickItemsDialog : DialogWindow
{
    readonly ListBox _list = new() { Height = 300, SelectionMode = SelectionMode.Multiple };
    public List<LibraryItem> Selected { get; } = new();

    public PickItemsDialog(string title, string prompt, IEnumerable<LibraryItem> items) : base(title, 460)
    {
        Caption(prompt);
        _list.ItemContainerStyle = (Style)Application.Current.FindResource("CardItem");
        _list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Orientation='Horizontal'>" +
            "<Image Source='{Binding Thumbnail}' Width='72' Height='44' Stretch='Uniform'/><StackPanel Margin='10,0,0,0' VerticalAlignment='Center'>" +
            "<TextBlock Text='{Binding Name}'/><TextBlock Text='{Binding Subtitle}' FontSize='11' Opacity='0.7'/></StackPanel></StackPanel></DataTemplate>");
        foreach (var i in items) _list.Items.Add(i);
        _list.SelectionChanged += (_, e) =>
        {
            foreach (LibraryItem a in e.AddedItems) Selected.Add(a);
            foreach (LibraryItem r in e.RemovedItems) Selected.Remove(r);
        };
        Body.Children.Add(_list);
    }
}
