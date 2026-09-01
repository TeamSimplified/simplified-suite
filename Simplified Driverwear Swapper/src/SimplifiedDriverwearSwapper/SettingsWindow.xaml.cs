using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SimplifiedDriverwearSwapper.Core.Config;

namespace SimplifiedDriverwearSwapper;

public partial class SettingsWindow : Window
{
    private static readonly Brush HintBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9C));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x2A, 0x2A));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x4F));

    private const string DefaultHint = "Your F1 25 install — where the game's own files are read from.";

    private readonly UserConfig _config;

    public SettingsWindow(UserConfig config)
    {
        InitializeComponent();
        // The same frameless dialog the rest of the suite uses: it wraps
        // the content in a title bar and drops the system frame.
        SimplifiedUi.DialogChrome.Apply(this);
        _config = config;
        GameDirBox.Text = config.GameDir ?? "";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select your game install folder",
            InitialDirectory = Directory.Exists(GameDirBox.Text) ? GameDirBox.Text : "",
        };
        if (dlg.ShowDialog(this) == true) GameDirBox.Text = dlg.FolderName;
    }

    /// <summary>
    /// Confirm the folder is the one we want while it's still editable. The asset
    /// group folders are the giveaway for a game root, and browsing to one level
    /// off is the common mistake.
    /// </summary>
    private void GameDir_Changed(object sender, TextChangedEventArgs e)
    {
        var dir = GameDirBox.Text.Trim();
        if (dir.Length == 0)
        {
            GameDirStatus.Foreground = HintBrush;
            GameDirStatus.Text = DefaultHint;
            return;
        }

        if (!Directory.Exists(dir))
        {
            GameDirStatus.Foreground = WarnBrush;
            GameDirStatus.Text = "That folder doesn't exist.";
            return;
        }

        bool looksLikeGame;
        try { looksLikeGame = Directory.EnumerateDirectories(dir, "*asset_groups*").Any(); }
        catch { looksLikeGame = false; }

        GameDirStatus.Foreground = looksLikeGame ? OkBrush : WarnBrush;
        GameDirStatus.Text = looksLikeGame
            ? "Found the game files."
            : "No asset group folders here — this may not be the game root.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dir = GameDirBox.Text.Trim();
        _config.GameDir = dir.Length == 0 ? null : dir;
        DialogResult = true;
    }
}
