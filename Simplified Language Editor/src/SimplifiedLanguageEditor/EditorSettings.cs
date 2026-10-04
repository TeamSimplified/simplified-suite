using System.Windows;
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

        dialog.Hint(
            "A .lng is a hash table, not a list: the header's seed and multiplier decide where every key " +
            "lands. Saving rebuilds the whole table, so a file written here is laid out the way the game " +
            "expects to find it.");

        dialog.Appearance();

        dialog.Credits(
            ("Petar Tasev", "Underlying language file format code"),
            ("Team Simplified", "App functionality and Design"),
            ("Claude Code", "Coding"));

        if (!dialog.Show(owner)) return false;

        config.Theme = dialog.Theme;
        config.Save();
        return true;
    }
}
