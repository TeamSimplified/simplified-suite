using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SimplifiedDriverwearSwapper.Core.Config;

namespace SimplifiedDriverwearSwapper;

/// <summary>
/// The app's settings, in the suite's Settings shell.
///
/// One field. The asset group folders are the giveaway for a game root, and
/// browsing to one level off is the common mistake — so the folder is checked
/// while it is still being typed rather than at swap time.
/// </summary>
public static class DriverwearSettings
{
    private const string GameDirHint =
        "Your F1 25 install — where the game's own files are read from.";

    /// <summary>Show the settings. True when they were saved.</summary>
    public static bool Show(Window owner, UserConfig config)
    {
        var dialog = new SimplifiedUi.SettingsDialog(
            "Simplified Driverwear Swapper - v" + (System.Reflection.Assembly.GetExecutingAssembly()
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
            ("BIKER7202", "For discovering the method to swap driverwear"),
            ("Team Simplified", "App functionality and Design"),
            ("Claude Code", "Coding"));

        if (!dialog.Show(owner)) return false;

        var dir = gameBox.Text.Trim();
        config.GameDir = dir.Length == 0 ? null : dir;
        config.Theme = dialog.Theme;
        return true;
    }

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

        bool looksLikeGame;
        try { looksLikeGame = Directory.EnumerateDirectories(dir, "*asset_groups*").Any(); }
        catch { looksLikeGame = false; }

        if (looksLikeGame) Say(status, "Found the game files.", "Accent", 0x2E, 0x7D, 0x4F);
        else Say(status, "No asset group folders here — this may not be the game root.",
                 "Danger", 0xB0, 0x2A, 0x2A);
    }

    private static void Say(TextBlock status, string text, string key, byte r, byte g, byte b)
    {
        status.Text = text;
        status.Foreground = Application.Current?.TryFindResource(key) as Brush
                            ?? new SolidColorBrush(Color.FromRgb(r, g, b));
    }
}
