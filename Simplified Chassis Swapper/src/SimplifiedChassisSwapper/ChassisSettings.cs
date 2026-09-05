using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SimplifiedChassisSwapper.Core.Config;
using SimplifiedChassisSwapper.Core.Swap;

namespace SimplifiedChassisSwapper;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// The game folder's status line is the only thing here that isn't a plain
/// field: pointing at the wrong folder doesn't fail a swap, it quietly drops
/// the in-car driver animations, so it is worth saying while the path is still
/// being typed rather than in a warning afterwards.
/// </summary>
public static class ChassisSettings
{
    private const string GameDirHint =
        "Your F1 25 install. Used to pull each texture's full-resolution mipmaps into the swap, " +
        "and to find the in-car driver animations.";

    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            "Simplified Chassis Swapper - v" + (System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(2) ?? "1.0"));

        var gameBox = new TextBox
        {
            Text = config.GameDir ?? "",
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
        };
        var browse = new Button { Content = "Browse" };

        dialog.Row("Game folder:", gameBox, browse);
        var status = dialog.Hint(GameDirHint);

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
    /// Say what the folder actually is, while it can still be changed. A game
    /// folder with no <c>incardriver_f1.erp</c> under it means the swap would
    /// silently skip the driver animations.
    /// </summary>
    private static void Describe(string text, TextBlock status)
    {
        var dir = text.Trim();

        if (dir.Length == 0)
        {
            Say(status, GameDirHint, "TextMuted", 0x8A, 0x92, 0x9C);
            return;
        }

        if (!Directory.Exists(dir))
        {
            Say(status, "That folder doesn't exist.", "Danger", 0xB0, 0x2A, 0x2A);
            return;
        }

        var incar = IncardriverLocator.FindIncardriverErp(dir);
        if (incar is null)
        {
            Say(status,
                "No incardriver_f1.erp under this folder — this may not be the game root. " +
                "Swaps will still run, but without the in-car driver animations.",
                "Danger", 0xB0, 0x2A, 0x2A);
            return;
        }

        Say(status,
            $"Found the game files (driver animations at {Path.GetFileName(Path.GetDirectoryName(incar))}).",
            "Accent", 0x2E, 0x7D, 0x4F);
    }

    private static void Say(TextBlock status, string text, string key, byte r, byte g, byte b)
    {
        status.Text = text;
        status.Foreground = Application.Current?.TryFindResource(key) as Brush
                            ?? new SolidColorBrush(Color.FromRgb(r, g, b));
    }
}
