using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using SimplifiedTeamEditor.Core.Config;
using SimplifiedTeamEditor.Util;
using SimplifiedUi;

namespace SimplifiedTeamEditor;

/// <summary>
/// The two things this app remembers between runs.
///
/// Both are conveniences rather than requirements — the game folder is worked
/// out when it isn't set, and the author only pre-fills a field in the package
/// dialog. So there is nothing to validate and nothing that can be left half
/// set: Save writes what is on screen, Cancel writes nothing.
///
/// No appearance row. The suite is dark, this app is dark, and a switch for it
/// would be the only setting here that isn't about the mod being made.
/// </summary>
public static class TeamSettings
{
    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(AppVersion.TitleFor("Simplified Team Editor"));

        // ── Game folder ─────────────────────────────────────────────────────
        var gameBox = new TextBox
        {
            Text = config.GameDir ?? "",
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
        };
        var browse = new Button { Content = "Browse" };
        var detect = new Button
        {
            Content = "Detect",
            ToolTip = "Look through Steam libraries and the usual EA folders",
        };

        dialog.Row("Game folder:", gameBox, browse, detect);
        var hint = dialog.Hint("Where the game is installed. Left empty, it is worked out when needed.");

        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "Select your game install folder" };
            if (picker.ShowDialog(owner) == true) gameBox.Text = picker.FolderName;
        };

        detect.Click += (_, _) =>
        {
            var hit = GameDirDetector.Detect();
            if (hit is null)
            {
                hint.Text = "Couldn't find it in any Steam library or the usual EA folders — use Browse.";
                return;
            }
            gameBox.Text = hit.Path;
            hint.Text = $"Found in {hit.Source}.";
        };

        // ── Author ──────────────────────────────────────────────────────────
        var authorBox = new TextBox
        {
            Text = config.DefaultAuthor ?? "",
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
            ToolTip = "Pre-fills the author when packaging",
        };
        dialog.Row("Default author:", authorBox);

        dialog.Credits(
            ("Petar Tasev", "Underlying ERP structure and format code"),
            ("Team Simplified", "App functionality and Design"),
            ("Claude Code", "Coding"));

        if (!dialog.Show(owner)) return false;

        config.GameDir = gameBox.Text.Trim() is { Length: > 0 } g ? g : null;
        config.DefaultAuthor = authorBox.Text.Trim() is { Length: > 0 } a ? a : null;

        try
        {
            config.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Couldn't save the settings: {ex.Message}",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return true;
    }
}
