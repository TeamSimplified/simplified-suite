using System.Windows;
using SimplifiedSwapper.Core.Config;

namespace SimplifiedSwapper;

/// <summary>
/// The shell around the two swaps: a title bar, a tab each, and one set of
/// settings.
///
/// Neither swap is implemented here. Each tab is the corresponding app's own
/// window turned into a control, and the engines stay in their own projects —
/// so what a tab does is what that app does, which is what makes the two
/// comparable while they are simplified.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// One config for the app, handed to both panels.
    ///
    /// The two swappers each had their own, and both declared the same four
    /// settings and wrote them to the same file name — so two copies of one
    /// thing, which is exactly what would have clobbered each other in one
    /// process.
    /// </summary>
    private readonly UserConfig _config = UserConfig.Load();

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);
        VersionLabel.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(2) ?? "1.0");

        Chassis.Initialise(_config);
        Driverwear.Initialise(_config);
    }

    /// <summary>
    /// Settings belongs to the window rather than a tab: both swaps read the
    /// same game folder, and having it in one tab would imply otherwise.
    /// </summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var before = _config.GameDir;
        var dlg = new SettingsWindow(_config) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _config.Save();

        var gameDirChanged = !string.Equals(before, _config.GameDir, StringComparison.OrdinalIgnoreCase);
        Chassis.SettingsChanged(gameDirChanged);
        Driverwear.SettingsChanged(gameDirChanged);
    }

    // ── Window chrome ───────────────────────────────────────────────────────
    //
    // The bar at the top is ours, so these three are too. Everything native
    // that a frameless window still needs is in SimplifiedUi.FramelessWindow.

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();
}
