using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Simplified.Language;
using SimplifiedLanguageExtractor.Core;
using SimplifiedLanguageExtractor.Core.Config;

namespace SimplifiedLanguageExtractor;

/// <summary>
/// The window: the game's exe in, eleven language files out.
///
/// There is nothing to choose here beyond where they go, which is why it is one
/// path and a log. The exe is not asked for — the game folder is already a
/// setting and the exe is the one at the top of it. The interesting part is
/// entirely in the Core, and in how long the first run takes, which the log has
/// to explain while it happens rather than after.
/// </summary>
public partial class MainWindow : Window
{
    private readonly UserConfig _config = UserConfig.Load();

    private string? _exePath;
    private string? _outputDir;
    private bool _running;

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);

        VersionLabel.Text = Util.AppVersion.Display;
        ApplyWindowIcon();

        _exePath = LanguageFiles.FindExe(_config.GameDir);
        _outputDir = _config.LastOutputFolder;
        Refresh();

        if (_exePath is not null) Log($"Game exe: {_exePath}");
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

    // ── Choosing ────────────────────────────────────────────────────────────

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Where should the language files go?",
            InitialDirectory = _outputDir is { } dir && Directory.Exists(dir) ? dir : "",
        };

        if (picker.ShowDialog(this) != true) return;

        _outputDir = picker.FolderName;
        _config.LastOutputFolder = _outputDir;
        _config.Save();
        Refresh();
    }

    // ── The run ─────────────────────────────────────────────────────────────

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _exePath is null || _outputDir is null) return;

        _running = true;
        Refresh();
        Log("");

        try
        {
            var gameDir = Path.GetDirectoryName(_exePath)!;
            var output = _outputDir;

            // Marshalled onto the window's thread as it goes, so the log fills
            // in while the work runs rather than all at once at the end. Where
            // the archive was found is remembered by the library, in runtime\.
            var result = await Task.Run(() => LanguageFiles.ExtractToAsync(
                gameDir, output, AppPaths.LanguageCacheDir,
                line => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Log(line))));

            Log("");
            Log($"Wrote {result.Written.Count} language file(s) to {result.OutputFolder} " +
                $"in {result.Elapsed.TotalSeconds:F1}s" +
                (result.FoundBySearch ? " — the offset is remembered, so the next run skips the search" : "") + ".");

            SimplifiedUi.MessageDialog.Show(this,
                $"{result.Written.Count} language file(s) written to:\n{result.OutputFolder}",
                "Extract complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ExtractionFailed failed)
        {
            Log("! " + failed.Message);
            SimplifiedUi.MessageDialog.Show(this, failed.Message,
                "Extract", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log("! " + ex.Message);
            SimplifiedUi.MessageDialog.Show(this,
                $"The language files couldn't be extracted.\n\n{ex.Message}",
                "Extract", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            Refresh();
        }
    }

    // ── State ───────────────────────────────────────────────────────────────

    private void Refresh()
    {
        OutputBox.Text = _outputDir ?? "";

        var ready = _exePath is not null && File.Exists(_exePath) && !string.IsNullOrWhiteSpace(_outputDir);
        ExtractButton.IsEnabled = ready && !_running;
        ExtractButton.Content = _running ? "Extracting…" : "Extract";
        BrowseOutputButton.IsEnabled = !_running;

        SubtitleLabel.Text = _exePath is null
            ? "No game exe found — set the game folder in Settings"
            : "Language files, out of the game's own exe";
    }

    // ── Log ─────────────────────────────────────────────────────────────────

    private void Log(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    // ── Window ──────────────────────────────────────────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!ExtractorSettings.Show(this, _config)) return;
        _config.Save();

        // The exe is found through the game folder, so a change there is a
        // change here - looked up again rather than kept from startup.
        var found = LanguageFiles.FindExe(_config.GameDir);
        if (found is not null && found != _exePath) Log($"Game exe: {found}");
        _exePath = found;
        Refresh();
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();
}
