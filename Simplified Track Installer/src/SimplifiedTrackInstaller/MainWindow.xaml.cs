using System.IO;
using System.Windows;
using Microsoft.Win32;
using SimplifiedTrackInstaller.Core;
using SimplifiedTrackInstaller.Core.Config;
using SimplifiedTrackInstaller.Core.Track;

namespace SimplifiedTrackInstaller;

/// <summary>
/// The window: a track zip in, a mod for the launcher out.
///
/// Two things to pick — the track, and optionally a game database to ship with
/// it — and Package, which asks where the mod goes the way every packager in
/// the suite does. The game folder is a setting rather than a row, since it is
/// the same answer every time.
/// </summary>
public partial class MainWindow : Window
{
    private readonly UserConfig _config = UserConfig.Load();

    private string? _trackPath;
    private TrackSummary? _track;
    private string? _databasePath;
    private bool _running;

    /// <summary>What the zip said about itself when it was picked.</summary>
    private sealed record TrackSummary(string Name, string Code, string Author, string Version,
                                       string? BuiltOn, string SuggestedFileName, string Carries);

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);

        VersionLabel.Text = Util.AppVersion.Display;
        ApplyWindowIcon();
        Refresh();
    }

    /// <summary>
    /// The window icon, from the .ico shipped in runtime\. The embedded
    /// application icon gives the taskbar its picture but not the Alt-Tab entry,
    /// which reads the window's own Icon and otherwise falls back to a blank page.
    /// </summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var path = AppPaths.IconFile;
            if (!File.Exists(path)) return;

            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            Icon = image;
        }
        catch
        {
            // A missing or unreadable icon is not worth refusing to open over.
        }
    }

    // ── The track ───────────────────────────────────────────────────────────

    private void BrowseTrack_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Pick a track zip",
            Filter = "Track zip (*.zip)|*.zip",
            InitialDirectory = ExistingDir(_config.LastTrackFolder),
        };

        if (picker.ShowDialog(this) == true) PickTrack(picker.FileName);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_running && DroppedZip(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_running && DroppedZip(e) is { } path) PickTrack(path);
    }

    private static string? DroppedZip(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files
        && files[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? files[0] : null;

    /// <summary>
    /// Read the zip's manifest now, so a zip that isn't a track is turned away
    /// here rather than a minute into packaging.
    /// </summary>
    private void PickTrack(string path)
    {
        _config.LastTrackFolder = Path.GetDirectoryName(path);
        _config.Save();

        try
        {
            using var zip = TrackZip.Open(path);
            _trackPath = path;
            _track = new TrackSummary(zip.Name, zip.Code, zip.Author, zip.Version, zip.CopyFromTrack,
                TrackPackager.SuggestedFileName(zip),
                $"{zip.Erp.Length / 1048576.0:N0} MB of track, {zip.Mipmaps.Count} mip files, {zip.Ers.Count} ERS values");
            Status($"{zip.Name} loaded.");
        }
        catch (Exception ex)
        {
            _trackPath = null;
            _track = null;
            SimplifiedUi.MessageDialog.Show(this,
                $"That zip couldn't be read as a track.\n\n{ex.Message}",
                "Track zip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Refresh();
    }

    // ── The database ────────────────────────────────────────────────────────

    private void BrowseDatabase_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Pick the game database to package",
            Filter = "Game database (*.bin)|*.bin|All files|*.*",
            InitialDirectory = ExistingDir(_config.LastDatabaseFolder),
        };

        if (picker.ShowDialog(this) != true) return;

        _config.LastDatabaseFolder = Path.GetDirectoryName(picker.FileName);
        _config.Save();

        if (TrackPackager.CheckDatabase(picker.FileName) is { } problem)
        {
            SimplifiedUi.MessageDialog.Show(this, problem,
                "Game database", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _databasePath = picker.FileName;
        Refresh();
    }

    private void ClearDatabase_Click(object sender, RoutedEventArgs e)
    {
        _databasePath = null;
        Refresh();
    }

    // ── Package ─────────────────────────────────────────────────────────────

    private async void Package_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _trackPath is null || _track is null) return;

        if (!HasGame())
        {
            SimplifiedUi.MessageDialog.Show(this,
                "Packaging needs your F1 25 folder: the track is merged into the game's own shader " +
                "packages and added to its menu and handling files. Set it in Settings.",
                "Package", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new SaveFileDialog
        {
            Title = "Save the mod",
            Filter = "Zip archives (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = _track.SuggestedFileName,
            InitialDirectory = ExistingDir(_config.LastPackageFolder),
            OverwritePrompt = true,
        };

        if (picker.ShowDialog(this) != true) return;

        _config.LastPackageFolder = Path.GetDirectoryName(picker.FileName);
        _config.Save();

        var request = new TrackPackageRequest(_trackPath, _config.GameDir!, picker.FileName, _databasePath,
            [TrackPackager.AppId, Util.AppVersion.Display]);

        _running = true;
        Refresh();
        Status("Packaging…");
        ReportProgress(0);

        // Progress<T> reports on the thread that made it, which is this one.
        var say = new Progress<string>(Status);
        var progress = new Progress<double>(ReportProgress);

        try
        {
            var result = await Task.Run(() => TrackPackager.Build(request, say: say, progress: progress));

            Status($"{result.TrackName} packaged — {new FileInfo(result.ZipPath).Length / 1048576.0:N0} MB.");

            var lines = new List<string> { $"{result.TrackName} is packaged to\n{result.ZipPath}" };
            if (!result.Database)
                lines.Add("No game database is in it, so the track won't be in the track list until " +
                          "one that names it is installed.");
            lines.AddRange(result.Warnings);

            SimplifiedUi.MessageDialog.Show(this, string.Join("\n\n", lines),
                "Package complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Status("The mod couldn't be packaged.");
            SimplifiedUi.MessageDialog.Show(this,
                $"The mod couldn't be packaged.\n\n{ex.Message}",
                "Package", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ReportProgress(-1);
            _running = false;
            Refresh();
        }
    }

    /// <summary>The bar and its percentage, as the Swapper shows them. Negative clears both.</summary>
    private void ReportProgress(double fraction)
    {
        if (fraction < 0)
        {
            PackageProgress.Value = 0;
            ProgressLabel.Text = "";
            return;
        }

        PackageProgress.Value = fraction;
        ProgressLabel.Text = $"{(int)(fraction * 100)}%";
    }

    // ── State ───────────────────────────────────────────────────────────────

    private bool HasGame() =>
        !string.IsNullOrWhiteSpace(_config.GameDir)
        && Directory.Exists(Path.Combine(_config.GameDir, TrackPackager.ShaderFolderName));

    private void Refresh()
    {
        TrackBox.Text = _trackPath ?? "";
        DatabaseBox.Text = _databasePath ?? "";

        if (_track is { } t)
        {
            TrackNameLabel.Text = t.Name;
            TrackNameLabel.Visibility = Visibility.Visible;
            NoTrackLabel.Visibility = Visibility.Collapsed;

            var by = t.Author.Length > 0 ? $" by {t.Author}" : "";
            var version = t.Version.Length > 0 ? $" {t.Version}" : "";
            var on = t.BuiltOn is { } b ? $", built on {b}" : "";
            TrackHint.Text = $"{t.Name}{version}{by} — takes the {t.Code} slot{on}. {t.Carries}.";
        }
        else
        {
            TrackNameLabel.Visibility = Visibility.Collapsed;
            NoTrackLabel.Visibility = Visibility.Visible;
            TrackHint.Text = "The track as the EgoGameDatabasesEditor takes it — or drop the zip on the window.";
        }

        DatabaseHint.Text = _databasePath is null
            ? "Optional. A gamedatabases.2025.bin with the track added — e.g. from the EgoGameDatabasesEditor — " +
              "shipped as it is. Without one the track is installed but isn't in the track list."
            : "Shipped as it is, in place of the game's own database.";

        PackageButton.IsEnabled = _track is not null && !_running;
        PackageButton.Content = _running ? "Packaging…" : "Package";
        BrowseTrackButton.IsEnabled = !_running;
        BrowseDatabaseButton.IsEnabled = !_running;
        ClearDatabaseButton.IsEnabled = !_running && _databasePath is not null;
        SettingsButton.IsEnabled = !_running;
        WindowCloseButton.IsEnabled = !_running;

        if (!_running && !HasGame())
            Status("No game folder — set it in Settings before packaging.");
        else if (!_running && StatusLabel.Text.StartsWith("No game folder", StringComparison.Ordinal))
            Status("");
    }

    private void Status(string text) => StatusLabel.Text = text;

    private static string ExistingDir(string? dir) => Directory.Exists(dir ?? "") ? dir! : "";

    // ── Window ──────────────────────────────────────────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!InstallerSettings.Show(this, _config)) return;
        _config.Save();
        Refresh();
    }

    /// <summary>
    /// Not while a package is being written: the staging folder would be left
    /// half full and the zip half written, with nothing to say which.
    /// </summary>
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_running) e.Cancel = true;
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();
}
