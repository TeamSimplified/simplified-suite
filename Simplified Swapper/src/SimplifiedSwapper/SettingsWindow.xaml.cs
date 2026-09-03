using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SimplifiedSwapper.Core.Config;
using SimplifiedChassisSwapper.Core.Swap;

namespace SimplifiedSwapper;

public partial class SettingsWindow : Window
{
    // Read from the palette rather than pinned, so they are right in both of
    // them. Resolved per use because the palette can change while this dialog
    // is open — picking a theme applies it immediately.
    private static Brush HintBrush => Themed("TextMuted", 0x8A, 0x92, 0x9C);
    private static Brush WarnBrush => Themed("Danger", 0xB0, 0x2A, 0x2A);
    private static Brush OkBrush => Themed("Accent", 0x2E, 0x7D, 0x4F);

    private static Brush Themed(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) as Brush
        ?? new SolidColorBrush(Color.FromRgb(r, g, b));

    private readonly UserConfig _config;

    /// <summary>
    /// The palette on the way in, so Cancel can put it back. A theme is applied
    /// as it is picked — choosing one from a dialog still wearing the old one is
    /// guessing — which means leaving without saving has to undo it.
    /// </summary>
    private readonly string _themeOnEntry = SimplifiedUi.ThemeManager.Current;

    public SettingsWindow(UserConfig config)
    {
        InitializeComponent();
        // The same frameless dialog the rest of the suite uses: it wraps
        // the content in a title bar and drops the system frame.
        SimplifiedUi.DialogChrome.Apply(this);
        _config = config;
        GameDirBox.Text = config.GameDir ?? "";

        // Set before the handlers can fire, so opening the dialog doesn't count
        // as picking anything.
        LightChip.IsChecked = !SimplifiedUi.ThemeManager.IsDark;
        DarkChip.IsChecked = SimplifiedUi.ThemeManager.IsDark;
    }

    private void Light_Checked(object sender, RoutedEventArgs e) =>
        SimplifiedUi.ThemeManager.Apply(SimplifiedUi.ThemeManager.Light);

    private void Dark_Checked(object sender, RoutedEventArgs e) =>
        SimplifiedUi.ThemeManager.Apply(SimplifiedUi.ThemeManager.Dark);

    /// <summary>Cancel, or the close button, puts the palette back.</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true) SimplifiedUi.ThemeManager.Apply(_themeOnEntry);
        base.OnClosed(e);
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
        _config.Theme = SimplifiedUi.ThemeManager.Current;
        DialogResult = true;
    }
}
