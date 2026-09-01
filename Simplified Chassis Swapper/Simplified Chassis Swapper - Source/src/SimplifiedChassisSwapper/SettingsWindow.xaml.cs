using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SimplifiedChassisSwapper.Core.Config;
using SimplifiedChassisSwapper.Core.Swap;

namespace SimplifiedChassisSwapper;

public partial class SettingsWindow : Window
{
    private static readonly Brush HintBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9C));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x2A, 0x2A));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x4F));

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
    /// Confirm the folder is the one we want while it's still editable — a game
    /// folder with no <c>incardriver_f1.erp</c> under it means the swap would
    /// silently skip the driver animations.
    /// </summary>
    private void GameDir_Changed(object sender, TextChangedEventArgs e)
    {
        var dir = GameDirBox.Text.Trim();
        if (dir.Length == 0)
        {
            GameDirStatus.Foreground = HintBrush;
            GameDirStatus.Text = "Your F1 25 install. Used to pull each texture's full-resolution mipmaps " +
                                 "into the swap, and to find the in-car driver animations.";
            return;
        }

        if (!Directory.Exists(dir))
        {
            GameDirStatus.Foreground = WarnBrush;
            GameDirStatus.Text = "That folder doesn't exist.";
            return;
        }

        var inc = IncardriverLocator.FindIncardriverErp(dir);
        if (inc is null)
        {
            GameDirStatus.Foreground = WarnBrush;
            GameDirStatus.Text = "No incardriver_f1.erp under this folder — this may not be the game root. " +
                                 "Swaps will still run, but without the in-car driver animations.";
            return;
        }

        GameDirStatus.Foreground = OkBrush;
        GameDirStatus.Text = $"Found the game files (driver animations at {Path.GetFileName(Path.GetDirectoryName(inc))}).";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dir = GameDirBox.Text.Trim();
        _config.GameDir = dir.Length == 0 ? null : dir;
        DialogResult = true;
    }
}
