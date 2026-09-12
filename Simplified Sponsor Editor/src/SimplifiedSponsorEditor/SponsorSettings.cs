using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimplifiedSponsorEditor.Core.Config;
using SimplifiedSponsorEditor.Core.Package;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// The game folder is needed even though the sponsor archives are bundled: a
/// package is built by merging the changed decals into the game's own shader
/// archives, and those are not ours to ship.
/// </summary>
public static class SponsorSettings
{
    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            Util.AppVersion.TitleFor("Simplified Sponsor Editor"));

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
            "Only packaging reads this. Your decals are merged into the game's own " +
            $"{SponsorPackager.ShaderFolderName} archives, which is how the game picks them up.");

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

        dialog.Appearance();

        dialog.Credits(
            ("Petar Tasev", "Underlying ERP structure and format code"),
            ("Jonnie", "Discovering how to replace in game sponsors"),
            ("Team Simplified", "App functionality and Design"),
            ("Claude Code", "Coding"));

        if (!dialog.Show(owner)) return false;

        var dir = gameBox.Text.Trim();
        config.GameDir = dir.Length == 0 ? null : dir;
        config.DefaultAuthor = authorBox.Text.Trim() is { Length: > 0 } author ? author : null;
        config.Theme = dialog.Theme;
        return true;
    }

    /// <summary>
    /// Say what the folder actually is, while it can still be changed. The
    /// shader folder is the giveaway for a real install, and browsing to one
    /// level off is the common mistake — so the test is for the thing packaging
    /// will actually go looking for.
    /// </summary>
    private static void Describe(string text, TextBlock status)
    {
        var dir = text.Trim();

        if (dir.Length == 0)
        {
            SimplifiedUi.SettingsDialog.Say(status,
                "Not set. Everything but packaging still works.", "TextMuted");
            return;
        }

        if (!Directory.Exists(dir))
        {
            SimplifiedUi.SettingsDialog.Say(status, "That folder doesn't exist.", "Danger");
            return;
        }

        var shaders = Path.Combine(dir, SponsorPackager.ShaderFolderName);
        if (!Directory.Exists(shaders))
        {
            SimplifiedUi.SettingsDialog.Say(status,
                $"No {SponsorPackager.ShaderFolderName} under this folder — this may not be the game root.",
                "Danger");
            return;
        }

        var missing = SponsorPackager.ShaderArchiveNames
            .Where(name => !File.Exists(Path.Combine(shaders, name)))
            .ToList();

        if (missing.Count > 0)
        {
            SimplifiedUi.SettingsDialog.Say(status,
                $"Found the game, but not {string.Join(" or ", missing)}. Packaging needs both.",
                "Danger");
            return;
        }

        SimplifiedUi.SettingsDialog.Say(status, "Found the game files.", "Accent");
    }
}
