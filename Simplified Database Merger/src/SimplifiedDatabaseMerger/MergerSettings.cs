using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimplifiedDatabaseMerger.Core;
using SimplifiedDatabaseMerger.Core.Config;

namespace SimplifiedDatabaseMerger;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// One real setting: where the game is, which is where From game takes the
/// clean database from. Everything else is picked file by file.
/// </summary>
public static class MergerSettings
{
    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            Util.AppVersion.TitleFor("Simplified Database Merger"));

        var gameBox = new TextBox
        {
            Text = config.GameDir ?? "",
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
        };
        var browse = new Button { Content = "Browse", Padding = new Thickness(12, 4, 12, 4) };
        var detect = new Button
        {
            Content = "Detect",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(6, 0, 0, 0),
        };

        dialog.Row("Game folder:", gameBox, browse, detect);
        var status = dialog.Hint("");
        Describe(gameBox.Text, status);

        dialog.Hint(
            "Only used by From game, to take the game's own database as the clean one. Verify the game's " +
            "files first if a mod has ever been installed over it.");

        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog
            {
                Title = "Select your game install folder",
                InitialDirectory = Directory.Exists(gameBox.Text) ? gameBox.Text : "",
            };
            if (picker.ShowDialog(owner) == true) gameBox.Text = picker.FolderName;
        };

        detect.Click += (_, _) =>
        {
            if (Simplified.Shared.GameDirDetector.Detect() is { } found) gameBox.Text = found.Path;
            else SimplifiedUi.SettingsDialog.Say(status, "Couldn't find an install — browse to it.", "Danger");
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

    /// <summary>Say whether the folder has a database to take, while it can still be changed.</summary>
    private static void Describe(string text, TextBlock status)
    {
        var dir = text.Trim();

        if (dir.Length == 0)
        {
            SimplifiedUi.SettingsDialog.Say(status, "Not set — From game needs it.", "Danger");
            return;
        }

        if (!Directory.Exists(dir))
        {
            SimplifiedUi.SettingsDialog.Say(status, "That folder doesn't exist.", "Danger");
            return;
        }

        var found = DatabaseInput.FindInGame(dir);
        if (found is null)
            SimplifiedUi.SettingsDialog.Say(status, "No game database under this folder — this may not be the install root.", "Danger");
        else
            SimplifiedUi.SettingsDialog.Say(status, $"Found {Path.GetFileName(found)}", "Accent");
    }
}
