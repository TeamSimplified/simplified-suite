using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SimplifiedUi;

/// <summary>
/// The suite's Settings window, as a shell each app fills in.
///
/// Studio and ERP had this shape first and everything else drifted from it —
/// four apps, four hand-built layouts, none of them agreeing on label width,
/// button order or where the credits sat. The shape is the same job every time,
/// so it lives here once: a title line, a column of labelled fields, the
/// credits, and Save/Cancel.
///
/// What differs between apps is only ever the rows, so that is all an app
/// supplies. Build one, add rows in order, then <see cref="Show"/>.
/// </summary>
public sealed class SettingsDialog
{
    /// <summary>Label column. Wide enough for "Default author:" without wrapping.</summary>
    private const double LabelWidth = 150;

    private readonly string _title;
    private readonly Grid _grid = new() { Margin = new Thickness(16) };
    private readonly StackPanel _tail = new();

    private string? _themeOnEntry;
    private RadioButton? _dark;

    /// <param name="appTitle">
    /// The app's name as it should read at the top — pass it through the app's
    /// own <c>AppVersion.TitleFor</c> so the version comes with it.
    /// </param>
    public SettingsDialog(string appTitle)
    {
        _title = appTitle;
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    }

    /// <summary>
    /// Fired when the palette is changed from the appearance row, so an app
    /// that paints anything in code can repaint it. Not raised when there is no
    /// appearance row.
    /// </summary>
    public event EventHandler? ThemeChanged;

    // ── Rows ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A labelled field, and optionally buttons to its right.
    ///
    /// The field spans the button column when there are none, so a row without
    /// buttons still reaches the full width instead of stopping short where
    /// another row's Browse happens to start.
    /// </summary>
    public void Row(string label, UIElement field, params Button[] buttons)
    {
        var row = _grid.RowDefinitions.Count;
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 8, 6),
        };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        _grid.Children.Add(text);

        if (field is FrameworkElement fe) fe.Margin = new Thickness(0, 6, 0, 6);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        if (buttons.Length == 0) Grid.SetColumnSpan(field, 2);
        _grid.Children.Add(field);

        if (buttons.Length == 0) return;

        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 6, 0, 6),
        };
        foreach (var button in buttons)
        {
            button.Padding = new Thickness(12, 4, 12, 4);
            if (strip.Children.Count > 0) button.Margin = new Thickness(6, 0, 0, 0);
            strip.Children.Add(button);
        }
        Grid.SetRow(strip, row);
        Grid.SetColumn(strip, 2);
        _grid.Children.Add(strip);
    }

    /// <summary>
    /// A note under the row above, in the muted voice the hints use elsewhere.
    ///
    /// Empty takes no room. A field whose only note is a problem — a folder
    /// that isn't there — starts with nothing to say and shouldn't leave a gap
    /// where the saying would go; set <see cref="Say"/> against the returned
    /// block and it appears and disappears with its text.
    /// </summary>
    public TextBlock Hint(string text)
    {
        var row = _grid.RowDefinitions.Count;
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var block = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        }.Themed(TextBlock.ForegroundProperty, "TextMuted");

        block.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        Grid.SetRow(block, row);
        Grid.SetColumn(block, 1);
        Grid.SetColumnSpan(block, 2);
        _grid.Children.Add(block);
        return block;
    }

    /// <summary>
    /// Put something in a hint, or take it away. The palette key names the
    /// voice — TextMuted for a remark, Danger for a problem, Accent for a
    /// confirmation.
    /// </summary>
    public static void Say(TextBlock hint, string text, string key = "TextMuted")
    {
        hint.Text = text;
        hint.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (text.Length > 0) hint.Themed(TextBlock.ForegroundProperty, key);
    }

    /// <summary>
    /// The Light/Dark chips.
    ///
    /// Applied as it is picked rather than on Save — choosing a palette from a
    /// dialog still wearing the old one is guessing — and put back on Cancel.
    /// </summary>
    public void Appearance()
    {
        _themeOnEntry = ThemeManager.Current;

        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var light = new RadioButton
        {
            Content = "Light",
            Style = Application.Current?.TryFindResource("ChannelChip") as Style,
            IsChecked = !ThemeManager.IsDark,
        };
        _dark = new RadioButton
        {
            Content = "Dark",
            Style = Application.Current?.TryFindResource("ChannelChip") as Style,
            Margin = new Thickness(0),
            IsChecked = ThemeManager.IsDark,
        };

        light.Checked += (_, _) => { ThemeManager.Apply(ThemeManager.Light); ThemeChanged?.Invoke(this, EventArgs.Empty); };
        _dark.Checked += (_, _) => { ThemeManager.Apply(ThemeManager.Dark); ThemeChanged?.Invoke(this, EventArgs.Empty); };

        chips.Children.Add(light);
        chips.Children.Add(_dark);
        Row("Appearance:", chips);
    }

    /// <summary>
    /// Who did what, under a rule. Every app in the suite carries the same
    /// first and last lines; the ones in between are whatever that app owes.
    /// </summary>
    public void Credits(params (string Name, string Role)[] credits)
    {
        var block = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };

        block.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 10),
        }.Themed(Border.BackgroundProperty, "BorderSoft"));

        block.Children.Add(new TextBlock
        {
            Text = "Credits",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });

        foreach (var (name, role) in credits)
        {
            var line = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2),
            }.Themed(TextBlock.ForegroundProperty, "TextMuted");

            line.Inlines.Add(new Run(name) { FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new Run($" — {role}"));
            block.Children.Add(line);
        }

        _tail.Children.Add(block);
    }

    // ── Showing it ──────────────────────────────────────────────────────────

    /// <summary>
    /// Put it on screen. True when Save was pressed; on Cancel the palette goes
    /// back to what it was, so an app only has to read its fields on true.
    /// </summary>
    public bool Show(Window owner)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var save = new Button { Content = "Save", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        _tail.Children.Add(buttons);

        // Outside the grid rather than as a row, so an app's rows are the only
        // things numbered and the title can't be pushed about by them.
        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = _title,
            Margin = new Thickness(16, 16, 16, 0),
        }.Themed(TextBlock.ForegroundProperty, "TextMuted"));
        root.Children.Add(_grid);

        _tail.Margin = new Thickness(16, 0, 16, 16);
        root.Children.Add(_tail);

        var window = new Window
        {
            Title = "Settings",
            Content = root,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 640,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        DialogChrome.Apply(window);
        save.Click += (_, _) => window.DialogResult = true;

        if (window.ShowDialog() == true) return true;

        // Cancelled — put back the palette the dialog was opened with, since
        // picking one applied it on the spot.
        if (_themeOnEntry is not null && ThemeManager.Current != _themeOnEntry)
        {
            ThemeManager.Apply(_themeOnEntry);
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
        return false;
    }

    /// <summary>Which palette the chips ended on. Meaningless without <see cref="Appearance"/>.</summary>
    public string Theme => _dark?.IsChecked == true ? ThemeManager.Dark : ThemeManager.Light;
}
