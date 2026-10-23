using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SimplifiedTrackManager.Core.Track;
using SimplifiedUi;

namespace SimplifiedTrackManager;

/// <summary>
/// Picking one of the game's countries, by name or by its flag, for the track's
/// flags to come from. A search box over the list, since there are ninety.
/// </summary>
public static class CountryDialog
{
    /// <summary>The chosen country, or null when cancelled.</summary>
    /// <param name="current">The country to start on, by its key, such as the track's nationality.</param>
    public static GameFlags.Country? Show(Window owner, IReadOnlyList<GameFlags.Country> countries, string? current)
    {
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(4) };
        var list = new ListBox { Height = 380 };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

        foreach (var c in countries) list.Items.Add(Row(c));
        list.Items.Filter = item =>
            item is ListBoxItem { Tag: GameFlags.Country c }
            && (search.Text.Trim().Length == 0 || c.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        search.TextChanged += (_, _) => list.Items.Refresh();

        var start = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((GameFlags.Country)i.Tag).Key == current);
        if (start is not null)
        {
            list.SelectedItem = start;
            list.Loaded += (_, _) => list.ScrollIntoView(start);
        }

        var ok = new Button { Content = "Use these flags", IsDefault = true, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        ok.IsEnabled = list.SelectedItem is not null;
        list.SelectionChanged += (_, _) => ok.IsEnabled = list.SelectedItem is not null;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var note = new TextBlock
        {
            Text = "The round flag is the game's own where it has one, and one from flag-icons otherwise. " +
                   "The rectangular flags are always the game's.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Margin = new Thickness(0, 8, 0, 0),
        }.Themed(TextBlock.ForegroundProperty, "TextMuted");

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(search);
        root.Children.Add(list);
        root.Children.Add(note);
        root.Children.Add(buttons);

        var window = new Window
        {
            Title = "Replace with in-game flags",
            Content = root,
            Owner = owner,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        DialogChrome.Apply(window);
        ok.Click += (_, _) => window.DialogResult = true;
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is not null) window.DialogResult = true;
        };
        window.Loaded += (_, _) => { search.Focus(); Keyboard.Focus(search); };

        return window.ShowDialog() == true && list.SelectedItem is ListBoxItem { Tag: GameFlags.Country chosen } ? chosen : null;
    }

    /// <summary>A country's row: its small flag, its round one (or the cut it would get), and its name.</summary>
    private static ListBoxItem Row(GameFlags.Country c)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
        row.Children.Add(new Image { Source = Load(c.Small), Width = 40, Height = 28, Margin = new Thickness(0, 0, 8, 0) });
        var round = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) };
        try { round.Source = c.Round is { } r ? Load(r) : FlagCrop.Round(c.Large, 28); } catch { }
        row.Children.Add(round);
        row.Children.Add(new TextBlock { Text = c.Name, VerticalAlignment = VerticalAlignment.Center });
        return new ListBoxItem { Content = row, Tag = c };
    }

    private static BitmapImage? Load(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
