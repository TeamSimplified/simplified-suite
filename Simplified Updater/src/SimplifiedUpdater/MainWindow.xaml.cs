using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SimplifiedUpdater.Core;
using SimplifiedUpdater.Core.Config;
using SimplifiedUpdater.Core.Merge;
using SimplifiedUpdater.Core.Mod;

namespace SimplifiedUpdater;

/// <summary>
/// The window: one mod in, the same mod out, built for the game as it is now.
///
/// There is nothing to edit here and nothing to choose, which is why it is a
/// path, a readiness panel and a log rather than an editor. The whole of the
/// judgement is in what counts as the modder's change and what counts as the
/// update's, and that is settled by the files in old\ and new\ — so those are
/// on screen before the button is pressed, and a pair that can't answer the
/// question stops the run instead of colouring the log.
/// </summary>
public partial class MainWindow : Window
{
    private static Brush Themed(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(r, g, b));

    private static Brush MutedBrush => Themed("TextMuted", 0xA4, 0xA4, 0xA4);
    private static Brush DangerBrush => Themed("Danger", 0xE0, 0x44, 0x34);
    private static Brush AccentBrush => Themed("Accent", 0x4C, 0x8E, 0xFF);

    private readonly UserConfig _config = UserConfig.Load();

    private string? _modPath;
    private bool _running;

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);

        VersionLabel.Text = Util.AppVersion.Display;
        ApplyWindowIcon();
        Refresh();

        // A mod handed over by Explorer. ContentRendered rather than Loaded:
        // Loaded fires before the first paint, so acting on it would leave an
        // unpainted window on screen while the archive is read.
        if (App.StartupFile is { } startupFile)
            ContentRendered += (_, _) => ChooseMod(startupFile);
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

    // ── Choosing a mod ──────────────────────────────────────────────────────

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose the mod to update",
            Filter = ModArchive.FileFilter + "|All files (*.*)|*.*",
            InitialDirectory = FirstExistingDir(_config.LastModFolder) ?? "",
        };

        if (picker.ShowDialog(this) == true) ChooseMod(picker.FileName);
    }

    private void ChooseMod(string path)
    {
        if (!ModArchive.LooksLikeMod(path))
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"{Path.GetFileName(path)} isn't an archive this can open.\n\n" +
                $"It reads {string.Join(", ", ModArchive.Extensions)}.",
                "Mod", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _modPath = path;
        _config.LastModFolder = Path.GetDirectoryName(path);
        _config.Save();

        Log($"Mod: {path}");
        Refresh();
    }

    /// <summary>Dropping a mod on the window is the same as browsing to it.</summary>
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (_running) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        if (files.FirstOrDefault(ModArchive.LooksLikeMod) is { } mod) ChooseMod(mod);
    }

    // ── The run ─────────────────────────────────────────────────────────────

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _modPath is null) return;

        var request = BuildRequest("");
        var problems = ModUpdater.Preflight(request);
        if (problems.Count > 0)
        {
            foreach (var problem in problems) Log("! " + problem);
            SimplifiedUi.MessageDialog.Show(this, string.Join("\n\n", problems),
                "Not ready", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new SaveFileDialog
        {
            Title = "Save the updated mod",
            Filter = "Zip archives (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = Path.GetFileNameWithoutExtension(_modPath) + " (updated).zip",
            InitialDirectory = FirstExistingDir(_config.LastOutputFolder)
                               ?? FirstExistingDir(_config.LastModFolder) ?? "",
        };

        if (picker.ShowDialog(this) != true) return;

        _config.LastOutputFolder = Path.GetDirectoryName(picker.FileName);
        _config.Save();

        _running = true;
        Refresh();
        Log("");

        try
        {
            // Marshalled onto the window's thread as it goes, so the log fills
            // in while the work runs rather than all at once at the end.
            var report = await Task.Run(() => ModUpdater.Run(
                BuildRequest(picker.FileName),
                line => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Log(line))));

            Summarise(report);

            SimplifiedUi.MessageDialog.Show(this,
                $"Updated mod written:\n{report.OutputPath}",
                "Update complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (UpdateRefused refused)
        {
            foreach (var line in refused.Message.Split("\n\n")) Log("! " + line);
            SimplifiedUi.MessageDialog.Show(this, refused.Message,
                "Not ready", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("! " + ex.Message);
            SimplifiedUi.MessageDialog.Show(this,
                $"The mod couldn't be updated.\n\n{ex.Message}",
                "Update", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            Refresh();
        }
    }

    private UpdateRequest BuildRequest(string output) =>
        new(_modPath ?? "", output, _config.GameDir ?? "", AppPaths.OldDir, AppPaths.NewDir);

    /// <summary>
    /// What the run came to, in the log. The dialog says where the file went;
    /// this says what is in it, which is the part worth reading twice.
    /// </summary>
    private void Summarise(UpdateReport report)
    {
        Log("");

        if (report.TouchedNothing)
        {
            Log("! This mod holds no database, language files or shader archives — " +
                "nothing needed updating, and the zip is the mod as it was.");
        }

        if (report.Database is { } database)
            Log($"Database: {database.Edited:N0} record(s) changed, {database.Added:N0} added" +
                (database.Conflicts > 0 ? $", {database.Conflicts:N0} also changed by the update" : "") + ".");

        foreach (var (file, result) in report.Languages)
            Log($"{file}: {result.Edited:N0} entrie(s) changed, {result.Added:N0} added" +
                (result.Conflicts > 0 ? $", {result.Conflicts:N0} also changed by the update" : "") + ".");

        foreach (var shader in report.Shaders)
            Log($"{shader.Name}: {shader.Carried:N0} of the mod's resource(s) carried over, " +
                $"{shader.Preserved:N0} left at the game's.");

        Log($"Wrote {report.FilesWritten:N0} file(s) to {report.OutputPath}.");
    }

    // ── State ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-read everything the run depends on and say where it stands. Cheap —
    /// it looks at names and sizes, not contents — so it runs on every change
    /// rather than being something to remember to press.
    /// </summary>
    private void Refresh()
    {
        ModBox.Text = _modPath ?? "";
        ModLabel.Text = _modPath is null ? "No mod chosen" : Path.GetFileName(_modPath);

        var gameDir = _config.GameDir;
        var shaderFolder = string.IsNullOrWhiteSpace(gameDir)
            ? null : Path.Combine(gameDir, GameFiles.ShaderFolderName);

        if (string.IsNullOrWhiteSpace(gameDir))
            Say(GameDirValue, "Not set — open Settings and pick (or detect) the install.", DangerBrush);
        else if (shaderFolder is null || !Directory.Exists(shaderFolder))
            Say(GameDirValue, $"{gameDir} — no {GameFiles.ShaderFolderName} in it.", DangerBrush);
        else
            Say(GameDirValue, gameDir, MutedBrush);

        Describe(AppPaths.OldDir, OldValue);
        Describe(AppPaths.NewDir, NewValue);

        var problems = ModUpdater.Preflight(BuildRequest(""));
        UpdateButton.IsEnabled = !_running && problems.Count == 0;
        UpdateButton.Content = _running ? "Updating…" : "Update";
        BrowseButton.IsEnabled = !_running;
        RecheckButton.IsEnabled = !_running;
    }

    /// <summary>What one of the two reference folders holds, in a line.</summary>
    private void Describe(string folder, System.Windows.Controls.TextBlock target)
    {
        if (!Directory.Exists(folder))
        {
            Say(target, "Missing. It wants the game's database and language files from that version.",
                DangerBrush);
            return;
        }

        var database = Directory.EnumerateFiles(folder, "*.bin", SearchOption.AllDirectories)
            .FirstOrDefault(GameFiles.IsDatabase);
        var languages = Directory.EnumerateFiles(folder, "*.lng", SearchOption.AllDirectories).Count();

        if (database is null && languages == 0)
        {
            Say(target, "Nothing in it yet — see the note in the folder.", DangerBrush);
            return;
        }

        var parts = new List<string>();
        parts.Add(database is null ? "no database" : Path.GetFileName(database));
        parts.Add(languages switch
        {
            0 => "no language files",
            1 => "1 language file",
            _ => $"{languages} language files",
        });

        Say(target, string.Join("   ·   ", parts),
            database is null || languages == 0 ? DangerBrush : MutedBrush);
    }

    private static void Say(System.Windows.Controls.TextBlock target, string text, Brush brush)
    {
        target.Text = text;
        target.Foreground = brush;
    }

    private void Recheck_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
        Log("Re-read old\\, new\\ and the game folder.");
    }

    private static string? FirstExistingDir(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;

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
        if (!UpdaterSettings.Show(this, _config)) return;
        _config.Save();
        Refresh();
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    // No StateChanged handler: FramelessWindow.Attach was given the maximise
    // button and keeps its glyph following the window state itself, including
    // when a double-click on the title bar is what changed it.
}
