using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaxSnapr.Library;
using MaxSnapr.Services;

namespace MaxSnapr.Views;

/// <summary>Voluntary support (donations). Nothing here limits the app; an empty URL hides every entry point.</summary>
public static class Support
{
    /// <summary>Donation page (Buy Me a Coffee, Ko-fi, GitHub Sponsors…).</summary>
    public const string Url = "";

    /// <summary>The single reminder appears once the library reaches this many items.</summary>
    public const int ReminderAfter = 50;

    public static bool Available => Url.StartsWith("https://", StringComparison.Ordinal);

    public static void OpenPage()
    {
        if (!Available) return;
        try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Abrir la página de apoyo", ex); }
    }

    public static void Show(Window? owner, bool reminder = false)
    {
        var w = new SupportWindow(reminder);
        if (owner is { IsVisible: true }) { w.Owner = owner; w.ShowDialog(); }
        else { w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.Show(); }
    }

    /// <summary>Shows one gentle reminder, once ever, after the library grows past <see cref="ReminderAfter"/>.</summary>
    public static void WatchLibrary(LibraryStore store)
    {
        if (!Available) return;
        // After the capture has opened, never on top of a selection or a recording.
        Watch(store, ReminderAfter, () => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!Video.RecordingSession.IsRecording) Show(App.Current.Editor, reminder: true);
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle));
    }

    /// <summary>Calls <paramref name="remind"/> the first time the library reaches <paramref name="threshold"/> items, and never again.</summary>
    internal static void Watch(LibraryStore store, int threshold, Action remind)
    {
        if (App.Settings.SupportReminderShown) return;
        NotifyCollectionChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add || store.Items.Count < threshold || App.Settings.SupportReminderShown) return;
            store.Items.CollectionChanged -= handler;
            App.Settings.SupportReminderShown = true;
            App.Settings.Save();
            remind();
        };
        store.Items.CollectionChanged += handler;
    }
}

public sealed class SupportWindow : DialogWindow
{
    public SupportWindow(bool reminder = false) : base("Apoyar MaxSnapr", 460, okCancel: false)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var logo = new Image { Source = EditorWindow.Logo, Width = 52, Height = 52, Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(logo, Dock.Left);
        head.Children.Add(logo);
        head.Children.Add(new TextBlock
        {
            Text = reminder ? "¿Te está siendo útil MaxSnapr?" : "Apoyar MaxSnapr",
            FontSize = 19, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        Body.Children.Add(head);
        Body.Children.Add(Paragraph(reminder
            ? $"Ya llevas {LibraryStore.Instance.Items.Count} capturas y grabaciones. MaxSnapr es gratuito, sin anuncios y no envía ningún dato."
            : "MaxSnapr es gratuito, sin anuncios y no envía ningún dato."));
        Body.Children.Add(Paragraph("Si te ahorra tiempo, puedes apoyar su desarrollo invitando a un café. Es totalmente voluntario: la aplicación seguirá completa igualmente."));
        Body.Children.Add(Paragraph("Lleva el nombre de Max, el perro de su autor.", secondary: true));
        if (reminder) Body.Children.Add(Paragraph("Este aviso no volverá a aparecer. Siempre puedes encontrarlo en Ayuda > Apoyar MaxSnapr.", secondary: true));

        var coffee = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), MinWidth = 170, IsDefault = true, IsEnabled = Support.Available };
        coffee.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = "\uEB52", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                new TextBlock { Text = "Invítame a un café", VerticalAlignment = VerticalAlignment.Center },
            },
        };
        coffee.Click += (_, _) => { Support.OpenPage(); Close(); };
        var later = new Button { Content = reminder ? "Ahora no" : "Cerrar", Style = (Style)Application.Current.FindResource("OutlineButton"), MinWidth = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        later.Click += (_, _) => Close();
        Buttons.Children.Add(coffee);
        Buttons.Children.Add(later);
    }

    static TextBlock Paragraph(string text, bool secondary = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        Foreground = (Brush)Application.Current.FindResource(secondary ? "Fg.Secondary" : "Fg.Primary"),
    };
}
