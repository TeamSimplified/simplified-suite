using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimplifiedTrackInstaller.Core.Config;
using SimplifiedTrackInstaller.Core.Track;

namespace SimplifiedTrackInstaller;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// One real setting: where the game is. The shader packages a track is merged
/// into and the menu and handling files it is added to all come from there.
/// </summary>
public static class InstallerSettings
{
    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            Util.AppVersion.TitleFor("Simplified Track Installer"));

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
            "The track is merged into the game's own track_info shader packages, and added to the menus' " +
            "and the 2026 cars' files read from here — so build against the game the mod is for.");

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

    /// <summary>
    /// Say what the folder actually is, while it can still be changed. The test
    /// is for the shader package a track goes into, since nothing can be built
    /// without it.
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

        var shaders = Path.Combine(dir, TrackPackager.ShaderFolderName);
        var missing = TrackPackager.ShaderArchiveNames.FirstOrDefault(n => !File.Exists(Path.Combine(shaders, n)));
        if (missing is not null)
        {
            SimplifiedUi.SettingsDialog.Say(status,
                $"No {TrackPackager.ShaderFolderName}\\{missing} here — this may not be the install root.", "Danger");
            return;
        }

        var name = Simplified.Shared.GameVersion.ReadName(dir);
        var version = Simplified.Shared.GameVersion.Read(dir);
        SimplifiedUi.SettingsDialog.Say(status,
            name is null ? "Found the shader packages." : $"Found {name} — {version}", "Accent");
    }
}
