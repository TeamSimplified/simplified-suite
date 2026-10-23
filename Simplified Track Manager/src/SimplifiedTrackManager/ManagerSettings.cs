using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimplifiedTrackManager.Core.Config;

namespace SimplifiedTrackManager;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// One real setting: where the game is. Its game database says which track
/// codes and initials are taken, and what the base track's rows hold.
/// </summary>
public static class ManagerSettings
{
    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            Util.AppVersion.TitleFor("Simplified Track Manager"));

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
            "The game's database is read from here: which codes and initials its own tracks have, " +
            "and what the base track a zip is built on holds. Nothing in the folder is changed.");

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
            ("Panayiotis Lipiridis", "Round country flags, from flag-icons (MIT)"),
            ("Claude Code", "Coding"));

        if (!dialog.Show(owner)) return false;

        var dir = gameBox.Text.Trim();
        config.GameDir = dir.Length == 0 ? null : dir;
        config.Theme = dialog.Theme;
        return true;
    }

    /// <summary>
    /// Say what the folder actually is, while it can still be changed. The test
    /// is for the game database, since nothing can be built without it.
    /// </summary>
    private static void Describe(string text, TextBlock status)
    {
        var dir = text.Trim();

        if (dir.Length == 0)
        {
            SimplifiedUi.SettingsDialog.Say(status, "Not set — nothing can be built without it.", "Danger");
            return;
        }

        if (!Directory.Exists(dir))
        {
            SimplifiedUi.SettingsDialog.Say(status, "That folder doesn't exist.", "Danger");
            return;
        }

        if (EgoEngineLibrary.Data.GameDatabases.GameDatabase.FindInGameDirectory(dir) is null)
        {
            SimplifiedUi.SettingsDialog.Say(status,
                "No game database here (game_package\\gamedatabases) — this may not be the install root.", "Danger");
            return;
        }

        var name = Simplified.Shared.GameVersion.ReadName(dir);
        var version = Simplified.Shared.GameVersion.Read(dir);
        SimplifiedUi.SettingsDialog.Say(status,
            name is null ? "Found the game database." : $"Found {name} — {version}", "Accent");
    }
}
