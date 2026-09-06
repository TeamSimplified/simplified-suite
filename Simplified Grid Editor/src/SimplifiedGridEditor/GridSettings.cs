using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimplifiedGridEditor.Core.Config;

namespace SimplifiedGridEditor;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// The game folder is needed even though a project carries its own copies of
/// the database and the language files: the UI shader packages a logo edit is
/// merged into have to come from an install, since they are not ours to bundle.
/// </summary>
public static class GridSettings
{
    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            Util.AppVersion.TitleFor("Simplified Grid Editor"));

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

        var authorBox = new TextBox
        {
            Text = config.DefaultAuthor ?? "",
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
        };
        dialog.Row("Default author:", authorBox);
        dialog.Hint("Fills in the Author field when a project is created.");

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
            ("Petar Tasev", "Underlying ERP structure and format code"),
            ("Team Simplified", "App functionality and Design"),
            ("Claude Code", "Coding"));

        if (!dialog.Show(owner)) return false;

        var dir = gameBox.Text.Trim();
        config.GameDir = dir.Length == 0 ? null : dir;
        config.DefaultAuthor = authorBox.Text.Trim() is { Length: > 0 } author ? author : null;
        config.EnableAdvancedSwapping = advanced.IsChecked == true;
        config.Theme = dialog.Theme;
        return true;
    }

    /// <summary>
    /// Say what the folder actually is, while it can still be changed. The
    /// shader package folder is the giveaway for a real install, and browsing to
    /// one level off is the common mistake.
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

        var shaders = Path.Combine(dir, SimplifiedTeamEditor.Core.GameFiles.ShaderFolderName);
        if (!Directory.Exists(shaders))
        {
            SimplifiedUi.SettingsDialog.Say(status,
                $"No {SimplifiedTeamEditor.Core.GameFiles.ShaderFolderName} under this folder — this may not be " +
                "the game root. Everything but team logos will still package.",
                "Danger");
            return;
        }

        SimplifiedUi.SettingsDialog.Say(status, "Found the game files.", "Accent");
    }
}
