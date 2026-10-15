using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimplifiedUniversalExporter.Core.Config;

namespace SimplifiedUniversalExporter;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// One real setting: where the game is. It used to be a box in the toolbar,
/// asked every time the window opened, for an answer that never changes.
/// </summary>
public static class ExporterSettings
{
    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            Util.AppVersion.TitleFor("Simplified Universal Exporter"));

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
            "Optional. An ERP carries small copies of its textures; with the game folder set, exports " +
            "use the full-resolution ones from the game's mip files instead.");

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
            ("RM Designs", "Model, mesh and helmet format research, and the Web 3D renderer"),
            ("Jefry", "Showroom lighting, from Jefry's Model Handler"),
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
    /// is for the exe, the one file every install has at its top.
    /// </summary>
    private static void Describe(string text, TextBlock status)
    {
        var dir = text.Trim();

        if (dir.Length == 0)
        {
            SimplifiedUi.SettingsDialog.Say(status, "Not set — exports use the small textures inside each ERP.", "Accent");
            return;
        }

        if (!Directory.Exists(dir))
        {
            SimplifiedUi.SettingsDialog.Say(status, "That folder doesn't exist.", "Danger");
            return;
        }

        try
        {
            var exe = Directory.EnumerateFiles(dir, "F1_*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (exe is null)
            {
                SimplifiedUi.SettingsDialog.Say(status,
                    "No game exe at the top of this folder — this may not be the install root.", "Danger");
                return;
            }

            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            SimplifiedUi.SettingsDialog.Say(status,
                $"Found {Path.GetFileName(exe)} — {info.FileMajorPart}.{info.FileMinorPart}." +
                $"{info.FileBuildPart}.{info.FilePrivatePart}", "Accent");
        }
        catch
        {
            SimplifiedUi.SettingsDialog.Say(status, "That folder couldn't be read.", "Danger");
        }
    }
}
