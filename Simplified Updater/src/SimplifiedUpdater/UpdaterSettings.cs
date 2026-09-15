using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimplifiedUpdater.Core;
using SimplifiedUpdater.Core.Config;

namespace SimplifiedUpdater;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// One real setting: where the game is. The two reference folders are not here
/// on purpose — they sit beside the exe, the window says what is in them, and a
/// path box pointing somewhere else would make "which files was this built
/// from" a question with two answers.
/// </summary>
public static class UpdaterSettings
{
    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            Util.AppVersion.TitleFor("Simplified Updater"));

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
            "The version being updated to. A mod's shader archives are rebuilt on the game's own, " +
            "so its shaders come from here — the mod keeps only what it added.");

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

        dialog.Hint(
            $"The files this compares against live beside the app: old\\ for the game as the mod found it, " +
            $"new\\ for the version you are updating to. Each wants the game's {GameFiles.LanguageFolderName} " +
            "folder and its gamedatabases .bin.");

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
    /// Say what the folder actually is, while it can still be changed. The
    /// shader folder is the giveaway for a real install, and browsing to one
    /// level off is the common mistake — so the test is for the thing the run
    /// will actually go looking for.
    /// </summary>
    private static void Describe(string text, TextBlock status)
    {
        var dir = text.Trim();

        if (dir.Length == 0)
        {
            SimplifiedUi.SettingsDialog.Say(status, "Not set. Nothing can be updated without it.", "Danger");
            return;
        }

        if (!Directory.Exists(dir))
        {
            SimplifiedUi.SettingsDialog.Say(status, "That folder doesn't exist.", "Danger");
            return;
        }

        var shaders = Path.Combine(dir, GameFiles.ShaderFolderName);
        if (!Directory.Exists(shaders))
        {
            SimplifiedUi.SettingsDialog.Say(status,
                $"No {GameFiles.ShaderFolderName} under this folder — this may not be the game root.",
                "Danger");
            return;
        }

        var count = Directory.EnumerateFiles(shaders, "*.erp").Count();
        SimplifiedUi.SettingsDialog.Say(status, $"Found the game — {count:N0} shader archives.", "Accent");
    }
}
