using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimplifiedDriverSwapper.Core.Config;
using EgoEngineLibrary.Data.GameDatabases;

namespace SimplifiedDriverSwapper;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// Two fields. The game folder's status line is the one thing here that isn't
/// plain: pointing at the wrong folder doesn't fail anything outright, it just
/// leaves New with nothing to start from, so it is worth saying while the path
/// is still being typed rather than in a dialog afterwards.
/// </summary>
public static class DriverSettings
{
    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            Util.AppVersion.TitleFor("Simplified Driver Swapper"));

        var gameBox = new TextBox
        {
            Text = config.GameDir ?? "",
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
        };
        var browse = new Button { Content = "Browse" };

        dialog.Row("Game folder:", gameBox, browse);
        var status = dialog.Hint("");

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

        var advanced = new CheckBox
        {
            Content = "Show the per-seat helmet, suit and glove controls",
            IsChecked = config.EnableAdvancedSwapping,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dialog.Row("Advanced swapping:", advanced);
        dialog.Hint(
            "Off by default — kit follows the driver on its own, so these are only " +
            "needed for a seat that wants something specific. The rules stay on either way.");

        dialog.Appearance();

        dialog.Credits(
            ("Team Simplified", "App functionality and Design"),
            ("Claude Code", "Coding"));

        if (!dialog.Show(owner)) return false;

        var dir = gameBox.Text.Trim();
        config.GameDir = dir.Length == 0 ? null : dir;
        config.EnableAdvancedSwapping = advanced.IsChecked == true;
        config.Theme = dialog.Theme;
        return true;
    }

    /// <summary>
    /// Say what the folder actually is, while it can still be changed. A game
    /// folder with no <c>gamedatabases.bin</c> under it is the one New reads
    /// from, so browsing a level off is worth catching here.
    /// </summary>
    private static void Describe(string text, TextBlock status)
    {
        var dir = text.Trim();

        if (dir.Length == 0)
        {
            SimplifiedUi.SettingsDialog.Say(status, "");
            return;
        }

        if (!Directory.Exists(dir))
        {
            SimplifiedUi.SettingsDialog.Say(status, "That folder doesn't exist.", "Danger");
            return;
        }

        var database = GameDatabase.FindInGameDirectory(dir);
        if (database is null)
        {
            SimplifiedUi.SettingsDialog.Say(status,
                "No gamedatabases.bin under this folder — this may not be the game root. " +
                "New has nothing to start from until it points at one.",
                "Danger");
            return;
        }

        SimplifiedUi.SettingsDialog.Say(status,
            $"Found the game files ({Path.GetFileName(database)}).",
            "Accent");
    }
}
