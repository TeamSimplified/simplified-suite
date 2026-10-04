using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace SimplifiedLanguageEditor;

/// <summary>What a comparison was asked for.</summary>
/// <param name="Against">The file to compare with.</param>
/// <param name="FromThisFile">
/// Which side's text goes into the export. True for the open file ~ "what I
/// changed" ~ and false for the other one, which is what pulls somebody else's
/// changes out of their file instead.
/// </param>
public sealed record CompareChoice(string Against, bool FromThisFile);

/// <summary>
/// The dialog Compare opens: which file to measure against, and which side's
/// text to keep.
///
/// Both of those are real questions and neither has a safe default, which is why
/// this is a window rather than a file picker. A comparison run the wrong way
/// round produces a perfectly valid language file full of the wrong text, and
/// nothing about it afterwards says so.
///
/// Built in code and wrapped in the shared chrome, as every dialog in the suite
/// is.
/// </summary>
public static class CompareDialog
{
    /// <summary>Ask. Null when it was cancelled.</summary>
    /// <param name="owner">The window it belongs to.</param>
    /// <param name="startIn">Where the file picker should open.</param>
    /// <param name="openFile">What the open file is called, for the chips.</param>
    public static CompareChoice? Show(Window owner, string startIn, string openFile)
    {
        var root = new StackPanel { Margin = new Thickness(18), Width = 520 };

        root.Children.Add(Label("Compare against"));

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var path = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        var browse = new Button
        {
            Content = "Browse",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(6, 0, 0, 0),
        };

        Grid.SetColumn(path, 0);
        Grid.SetColumn(browse, 1);
        row.Children.Add(path);
        row.Children.Add(browse);
        root.Children.Add(row);

        root.Children.Add(Hint(
            "The file being measured against — the game's own, usually, or the version this one started from."));

        root.Children.Add(Label("Take the text from", first: false));

        // Chips, as the Settings dialog states a two-state choice.
        var chip = Application.Current?.TryFindResource("ChannelChip") as Style;
        var chips = new StackPanel { Orientation = Orientation.Horizontal };

        var mine = new RadioButton { Content = Short(openFile), Style = chip, IsChecked = true };
        var theirs = new RadioButton { Content = "The other file", Style = chip, Margin = new Thickness(0) };

        chips.Children.Add(mine);
        chips.Children.Add(theirs);
        root.Children.Add(chips);

        var explain = Hint("");
        root.Children.Add(explain);

        void Explain() => explain.Text = mine.IsChecked == true
            ? "The export holds what this file says: the lines it has rewritten, and the lines it adds."
            : "The export holds what the other file says: the lines it has rewritten, and the lines it adds. "
              + "For taking somebody else's changes out of their file.";

        mine.Checked += (_, _) => Explain();
        theirs.Checked += (_, _) => Explain();
        Explain();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        // The action first and Cancel last, as every dialog in the suite has
        // them: the first answers Enter and the last answers Esc, so the one
        // that changes nothing is the one on the end.
        var go = new Button
        {
            Content = "Compare",
            Padding = new Thickness(18, 4, 18, 4),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
            IsEnabled = false,
        };

        var cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(18, 4, 18, 4),
            IsCancel = true,
        };

        buttons.Children.Add(go);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        var window = new Window
        {
            Title = "Compare",
            Content = root,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        // Nothing to compare against is the one thing that stops this being
        // answerable, so the button follows the box.
        path.TextChanged += (_, _) => go.IsEnabled = path.Text.Trim().Length > 0;

        browse.Click += (_, _) =>
        {
            var picker = new OpenFileDialog
            {
                Title = "Compare against which file?",
                Filter = "Language files|*.lng|All files|*.*",
                InitialDirectory = Directory.Exists(startIn) ? startIn : "",
            };

            if (picker.ShowDialog(window) == true) path.Text = picker.FileName;
        };

        go.Click += (_, _) => window.DialogResult = true;

        // After Content, never before: DialogChrome wraps whatever is there at
        // the time, so content assigned afterwards replaces the title bar too.
        SimplifiedUi.DialogChrome.Apply(window);

        window.Loaded += (_, _) => browse.Focus();

        return window.ShowDialog() == true
            ? new CompareChoice(path.Text.Trim(), mine.IsChecked == true)
            : null;
    }

    /// <summary>
    /// The open file's name, short enough to sit on a chip. A path would make
    /// the chip wider than the dialog.
    /// </summary>
    private static string Short(string openFile)
    {
        var name = Path.GetFileName(openFile);
        if (name.Length == 0) return "This file";

        return name.Length <= 24 ? name : name[..22] + "…";
    }

    private static TextBlock Label(string text, bool first = true) => new()
    {
        Text = text,
        Foreground = Themed("TextMuted"),
        Margin = new Thickness(0, first ? 0 : 18, 0, 6),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = Themed("TextMuted"),
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0),
    };

    /// <summary>
    /// A palette brush, looked up each time rather than held: one resolved once
    /// is frozen through a theme change.
    /// </summary>
    private static System.Windows.Media.Brush? Themed(string key)
        => Application.Current?.TryFindResource(key) as System.Windows.Media.Brush;
}
