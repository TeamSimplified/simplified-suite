using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimplifiedResourceExtractor.Core;
using SimplifiedResourceExtractor.Core.Config;

namespace SimplifiedResourceExtractor;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// Only the game folder lives here. The folder to search and the folder to write
/// to change from one job to the next and belong on the window; the install is
/// set once and then forgotten, and is only read at all when mipmaps are being
/// stripped.
/// </summary>
public static class ExtractorSettings
{
    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            Util.AppVersion.TitleFor("Simplified Resource Extractor"));

        var gameBox = new TextBox
        {
            Text = config.GameDir ?? "",
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
        };
        var browse = new Button { Content = "Browse" };

        dialog.Row("Game folder:", gameBox, browse);
        var status = dialog.Hint("");
        Describe(gameBox.Text, status);

        dialog.Hint(
            "Only stripping mipmaps reads this. A texture's full resolution lives in .mipmaps " +
            "files beside the game's archives rather than inside them, so folding it in means " +
            "reading the install.");

        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog
            {
                Title = "Select your game install folder",
                InitialDirectory = Directory.Exists(gameBox.Text) ? gameBox.Text : "",
            };
            if (picker.ShowDialog(owner) == true) gameBox.Text = picker.FolderName;
        };

        gameBox.TextChanged += (_, _) => Describe(gameBox.Text, status);

        dialog.Appearance();

        dialog.Credits(
            ("Petar Tasev", "Underlying ERP structure and format code"),
            ("Team Simplified", "App functionality and Design"),
            ("Claude Code", "Coding"));

        if (!dialog.Show(owner)) return false;

        var dir = gameBox.Text.Trim();
        config.GameDir = dir.Length == 0 ? null : dir;
        config.Theme = dialog.Theme;
        return true;
    }

    /// <summary>
    /// Say what the folder actually is, while it can still be changed. A folder
    /// with no .mipmaps under it is not an install, and browsing to one level
    /// off is the common mistake — the sidecars are the thing being looked for,
    /// so their absence is the honest test.
    /// </summary>
    private static void Describe(string text, TextBlock status)
    {
        var dir = text.Trim();

        if (dir.Length == 0)
        {
            SimplifiedUi.SettingsDialog.Say(status,
                "Not set. Everything but stripping mipmaps still works.", "TextMuted");
            return;
        }

        if (!Directory.Exists(dir))
        {
            SimplifiedUi.SettingsDialog.Say(status, "That folder doesn't exist.", "Danger");
            return;
        }

        // Enumerate rather than count: an install holds tens of thousands of
        // these, and the question is only whether there is one.
        var hasMipmaps = Directory
            .EnumerateFiles(dir, "*.mipmaps", SearchOption.AllDirectories)
            .Any();

        if (!hasMipmaps)
        {
            SimplifiedUi.SettingsDialog.Say(status,
                "No .mipmaps files under this folder — this may not be the game root. " +
                "Everything but stripping will still work.",
                "Danger");
            return;
        }

        SimplifiedUi.SettingsDialog.Say(status, "Found the game files.", "Accent");
    }
}
