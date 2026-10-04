using System.Windows;
using System.Windows.Controls;
using SimplifiedLanguageEditor.Core.Config;

namespace SimplifiedLanguageEditor;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// There is no game folder here and no output folder, because this app is handed
/// a file and writes a file: everything it needs arrives with the thing being
/// opened. What is left is the palette, which the whole suite shares, and the
/// credits.
/// </summary>
public static class EditorSettings
{
    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            Util.AppVersion.TitleFor("Simplified Language Editor"));

        dialog.Appearance();

        // Chips, as Appearance builds them: two states side by side is what a
        // choice looks like in this suite, and a lone checkbox is not.
        var chip = Application.Current?.TryFindResource("ChannelChip") as Style;

        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var hidden = new RadioButton
        {
            Content = "Hidden",
            Style = chip,
            IsChecked = !config.ShowLog,
        };

        var shown = new RadioButton
        {
            Content = "Shown",
            Style = chip,
            Margin = new Thickness(0),
            IsChecked = config.ShowLog,
        };

        // Grouped by their parent, which is how the appearance chips do it — no
        // GroupName, so two of these in one dialog could not interfere anyway.
        chips.Children.Add(hidden);
        chips.Children.Add(shown);
        dialog.Row("Log:", chips);

        dialog.Hint(
            "Hidden by default — it says what has just happened, which is mostly what the table has " +
            "already shown. Worth having for a merge, which is the one action that reports numbers.");

        dialog.Hint(
            "A .lng is a hash table, not a list: the header's seed and multiplier decide where every key " +
            "lands. Saving rebuilds the whole table, so a file written here is laid out the way the game " +
            "expects to find it.");

        dialog.Credits(
            ("Petar Tasev", "Underlying language file format code"),
            ("Team Simplified", "App functionality and Design"),
            ("Claude Code", "Coding"));

        if (!dialog.Show(owner)) return false;

        config.ShowLog = shown.IsChecked == true;
        config.Theme = dialog.Theme;
        config.Save();
        return true;
    }
}
