using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Simplified.Language;
using SimplifiedLanguageExtractor.Core;
using SimplifiedLanguageExtractor.Core.Config;

namespace SimplifiedLanguageExtractor;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// One real setting: where the game is. It is the only way the exe is found,
/// so without it there is nothing to extract from.
/// </summary>
public static class ExtractorSettings
{
    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            Util.AppVersion.TitleFor("Simplified Language Extractor"));

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
            "Only used to find the exe. The language files are inside it — the game ships no .lng of its " +
            "own, which is why they have to come out of there.");

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
            "The first extraction searches the exe end to end, which takes about half a minute. Where the " +
            "archive was found is then kept, so every run after that is immediate until the game updates.");

        dialog.Appearance();

        dialog.Credits(
            ("victorbush", "NefsLib — reading the archive inside the game exe"),
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
    /// is for the exe, since that is the only thing this app needs out of an
    /// install.
    /// </summary>
    private static void Describe(string text, TextBlock status)
    {
        var dir = text.Trim();

        if (dir.Length == 0)
        {
            SimplifiedUi.SettingsDialog.Say(status,
                "Not set — nothing can be extracted without it.", "Danger");
            return;
        }

        if (!Directory.Exists(dir))
        {
            SimplifiedUi.SettingsDialog.Say(status, "That folder doesn't exist.", "Danger");
            return;
        }

        try
        {
            var exe = Directory.EnumerateFiles(dir, LanguageFiles.ExeNamePattern,
                SearchOption.TopDirectoryOnly).FirstOrDefault();

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
