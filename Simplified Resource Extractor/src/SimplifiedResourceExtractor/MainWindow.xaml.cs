using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SimplifiedResourceExtractor.Core;
using SimplifiedResourceExtractor.Core.Config;
using SimplifiedResourceExtractor.Core.Extract;
using SimplifiedResourceExtractor.Core.Patterns;
using SimplifiedResourceExtractor.Core.Scan;

namespace SimplifiedResourceExtractor;

public partial class MainWindow : Window
{
    private readonly UserConfig _config = UserConfig.Load();

    /// <summary>
    /// Lines waiting to be shown. A job writes to this from a worker thread and
    /// the timer below drains it, so a run that logs a line per archive costs
    /// one UI update every <see cref="LogFlushMs"/> rather than thousands.
    /// </summary>
    private readonly Queue<string> _pending = new();

    private readonly DispatcherTimer _logTimer;

    private CancellationTokenSource? _cancel;

    /// <summary>
    /// Whether the timer is draining the queue. The timer's own
    /// <c>IsEnabled</c> answers the same question, but <see cref="Log"/> is
    /// called from the pool and a DispatcherTimer belongs to the UI thread.
    /// </summary>
    private volatile bool _draining;

    /// <summary>The last scan, so Extract can reuse it rather than scan twice.</summary>
    private ScanResult? _scan;

    /// <summary>What <see cref="_scan"/> was produced from, to know when it is stale.</summary>
    private (string Path, string Rules, bool Recursive)? _scannedFor;

    private const int LogFlushMs = 100;

    public MainWindow()
    {
        InitializeComponent();

        VersionLabel.Text = Util.AppVersion.Display;
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);
        ApplyWindowIcon();

        SearchBox.Text = _config.SearchPath ?? "";
        RecursiveCheck.IsChecked = _config.Recursive;
        OutputBox.Text = _config.OutputDir ?? "";
        StripCheck.IsChecked = _config.StripMipmaps;
        IndividualCheck.IsChecked = _config.ExtractIndividually;
        PrefixCheck.IsChecked = _config.AddFolderPrefix;

        LoadRules();

        _logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(LogFlushMs),
                                        DispatcherPriority.Background, (_, _) => FlushLog(), Dispatcher);
        _logTimer.Stop();

        // The gutter needs the editor's template to exist before it can follow
        // its scrolling, and that isn't built until the window loads.
        Loaded += (_, _) => HookGutter();
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
            // A missing or unreadable icon is not worth failing startup over.
        }
    }

    // ── The rules ─────────────────────────────────────────────────────

    private void LoadRules()
    {
        try
        {
            // Whitespace counts as absent, not as a list of nothing. A file left
            // empty - by a half-finished write, or by somebody clearing it -
            // would otherwise open the app on a blank box with no hint that
            // there is a syntax at all.
            var saved = File.Exists(AppPaths.ReferenceFile)
                ? File.ReadAllText(AppPaths.ReferenceFile)
                : "";

            RulesBox.Text = saved.Trim().Length > 0 ? saved : ReferenceList.DefaultText;
        }
        catch (Exception e)
        {
            RulesBox.Text = ReferenceList.DefaultText;
            Log($"Couldn't read {Path.GetFileName(AppPaths.ReferenceFile)}: {e.Message}");
        }

        DescribeRules();
    }

    /// <summary>
    /// Write the rules back to their file. Called before a run and on the way
    /// out rather than on every keystroke: this is a file somebody may have
    /// open in an editor beside us.
    /// </summary>
    private void SaveRules()
    {
        try
        {
            File.WriteAllText(AppPaths.ReferenceFile, RulesBox.Text, Encoding.UTF8);
        }
        catch
        {
            // The app folder can be read-only - run straight out of the zip, say.
            // Losing the edit is bad; interrupting the job to say so is worse,
            // and the rules that matter are the ones in the box.
        }
    }

    private void Rules_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Editing invalidates a scan: what matched a moment ago answered a
        // different question. Extract would otherwise write yesterday's answer.
        _scan = null;
        _scannedFor = null;
        DescribeRules();
        UpdateGutter();
    }

    private void Rules_LostFocus(object sender, RoutedEventArgs e) => SaveRules();

    private void DescribeRules()
    {
        var rules = ReferenceList.Parse(RulesBox.Text);
        RulesCountLabel.Text = rules.RuleCount == 1 ? "1 RULE" : $"{rules.RuleCount} RULES";

        if (rules.Ignored.Count > 0)
        {
            RulesCountLabel.Text += $"  ({rules.Ignored.Count} IGNORED)";
        }
    }

    // ── Running ───────────────────────────────────────────────────────

    private void Scan_Click(object sender, RoutedEventArgs e) => Run(extract: false);

    private void Extract_Click(object sender, RoutedEventArgs e) => Run(extract: true);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancel?.Cancel();
        Status("Stopping...");
    }

    private async void Run(bool extract)
    {
        var searchPath = SearchBox.Text.Trim();
        var outputDir = OutputBox.Text.Trim();

        if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
        {
            SimplifiedUi.MessageDialog.Show(this,
                "That path isn't there.\n\nPick a folder of archives — usually the game install — " +
                "or one .erp on its own.",
                "Nothing to search", MessageBoxButton.OK);
            return;
        }

        if (extract && outputDir.Length == 0)
        {
            SimplifiedUi.MessageDialog.Show(this,
                "There's nowhere to put the results.\n\nPick an output folder first.",
                "No output folder", MessageBoxButton.OK);
            return;
        }

        var strip = StripCheck.IsChecked == true;
        if (extract && strip && !Directory.Exists(_config.GameDir ?? ""))
        {
            SimplifiedUi.MessageDialog.Show(this,
                "Stripping mipmaps needs the game folder.\n\nA texture's full resolution lives in " +
                ".mipmaps files beside the game's archives, so pulling it in means reading the install. " +
                "Set the game folder in Settings, or turn stripping off.",
                "Game folder not set", MessageBoxButton.OK);
            return;
        }

        SaveRules();
        SaveConfig();

        var rulesText = RulesBox.Text;
        var rules = ReferenceList.Parse(rulesText);
        if (rules.RuleCount == 0)
        {
            SimplifiedUi.MessageDialog.Show(this,
                "There are no rules to match against.\n\nWrite at least one line that isn't a comment.",
                "No rules", MessageBoxButton.OK);
            return;
        }

        var individual = IndividualCheck.IsChecked == true;
        var prefix = PrefixCheck.IsChecked == true;
        var gameDir = _config.GameDir;

        // Recursion only means something for a folder, and the box is disabled
        // for a single archive - but a disabled box keeps whatever it was last
        // ticked to, so the value is settled here rather than read raw.
        var recursive = Directory.Exists(searchPath) && RecursiveCheck.IsChecked == true;

        var reuse = _scan is not null && _scannedFor == (searchPath, rulesText, recursive) ? _scan : null;

        // Cleared before anything is written to it, or the first lines of the
        // run would be wiped by their own preamble.
        LogBox.Clear();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        Busy(true);

        foreach (var line in rules.Ignored) Log($"Ignored, since it asks for nothing: {line}");

        try
        {
            var scan = reuse;

            if (scan is null)
            {
                Log(Directory.Exists(searchPath)
                    ? $"Scanning {searchPath}{(recursive ? " and everything under it" : ", top level only")}..."
                    : $"Scanning {searchPath}...");

                var progress = new Progress<ScanProgress>(p =>
                    Status($"Scanned {p.Scanned:N0} of {p.Total:N0} archives — {p.Matches:N0} match(es)"));

                var started = DateTime.UtcNow;
                scan = await Task.Run(() => ArchiveScanner.Scan(searchPath, rules, recursive, progress, token), token);
                var took = DateTime.UtcNow - started;

                Log($"  {Count(scan.ArchivesSeen, "archive")}, {scan.ResourcesSeen:N0} resources, " +
                    $"{took.TotalSeconds:F1}s" +
                    (scan.ArchivesUnreadable > 0 ? $", {scan.ArchivesUnreadable:N0} not readable as ERP" : ""));

                _scan = scan;
                _scannedFor = (searchPath, rulesText, recursive);
            }
            else
            {
                Log("Using the last scan — nothing about it has changed.");
            }

            Log($"Matched {Count(scan.MatchedResources, "resource")} in {Count(scan.Matched.Count, "archive")}.");

            // Relative to the folder being searched, or to the one holding the
            // archive when a single file was named.
            var root = Directory.Exists(searchPath) ? searchPath : Path.GetDirectoryName(searchPath) ?? searchPath;
            foreach (var archive in scan.Matched)
            {
                Log($"  {archive.Identifiers.Count,6:N0}  {Relative(archive.Path, root)}");
            }

            if (scan.Matched.Count == 0)
            {
                Status("Nothing matched.");
                return;
            }

            if (!extract)
            {
                Status($"{Count(scan.MatchedResources, "resource")} in {Count(scan.Matched.Count, "archive")}. " +
                       "Press Extract to write them out.");
                return;
            }

            Log("");
            Status("Extracting...");

            var request = new JobRequest(scan, outputDir, individual, strip, prefix, gameDir);
            var result = await Task.Run(() => ExtractionJob.Run(request, Log, token), token);

            Log("");
            Log($"Wrote {Count(result.Written.Count, "archive")}, {Count(result.Resources, "resource")} in total.");
            Status($"Done — {Count(result.Written.Count, "archive")} in {outputDir}");
        }
        catch (OperationCanceledException)
        {
            Log("");
            Log("Stopped.");
            Status("Stopped.");
        }
        catch (Exception ex)
        {
            Log("");
            Log($"FAILED: {ex.Message}");
            Status("Failed. The log says why.");
        }
        finally
        {
            Busy(false);
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    /// <summary>
    /// A count with its noun, pluralised. Searching one archive is now an
    /// ordinary thing to do, and "1 archive(s)" is what a tool says when nobody
    /// expected that case.
    /// </summary>
    private static string Count(int n, string noun) => $"{n:N0} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>
    /// Archive paths under the folder being searched, shown relative to it. A
    /// game-wide scan otherwise reports the same 60-character prefix on every
    /// line, and the part that differs falls off the right of the pane.
    /// </summary>
    private static string Relative(string path, string root)
    {
        try
        {
            return Path.GetRelativePath(root, path);
        }
        catch
        {
            return path;
        }
    }

    private void Busy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        ExtractButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RulesBox.IsReadOnly = busy;
        _draining = busy;

        if (busy) _logTimer.Start();
        else
        {
            _logTimer.Stop();
            FlushLog();
        }
    }

    // ── Log and status ────────────────────────────────────────────────

    /// <summary>
    /// Add a line. Safe from any thread: the job runs on the pool and this is
    /// the only thing it calls.
    /// </summary>
    private void Log(string line)
    {
        lock (_pending) _pending.Enqueue(line);

        // Outside a run there is no timer to drain the queue, so say it now.
        if (!_draining) Dispatcher.BeginInvoke(FlushLog);
    }

    private void FlushLog()
    {
        string[] lines;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            lines = [.. _pending];
            _pending.Clear();
        }

        LogBox.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void Status(string text) => Dispatcher.BeginInvoke(() => StatusLabel.Text = text);

    // ── Folders ───────────────────────────────────────────────────────

    private void BrowseSearchFolder_Click(object sender, RoutedEventArgs e) =>
        Browse(SearchBox, "Select the folder to search");

    private void BrowseOutput_Click(object sender, RoutedEventArgs e) =>
        Browse(OutputBox, "Select where the results should go");

    private void BrowseSearchFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Select the archive to search",
            Filter = "ERP archives|*.erp|All files|*.*",
            CheckFileExists = true,
            InitialDirectory = StartingFolder(SearchBox.Text),
        };

        if (picker.ShowDialog(this) == true) SearchBox.Text = picker.FileName;
    }

    private void Browse(TextBox box, string title)
    {
        var picker = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = StartingFolder(box.Text),
        };

        if (picker.ShowDialog(this) == true) box.Text = picker.FolderName;
    }

    /// <summary>
    /// Where a picker should open, given whatever is in the box. A path naming
    /// an archive opens the folder holding it, so switching between the two
    /// buttons doesn't send you back to the top of the drive.
    /// </summary>
    private static string StartingFolder(string text)
    {
        var path = text.Trim();
        if (Directory.Exists(path)) return path;

        var parent = Path.GetDirectoryName(path);
        return Directory.Exists(parent) ? parent : "";
    }

    /// <summary>
    /// Subfolders are a question about a folder. Pointed at one archive there is
    /// nothing beneath it, so the checkbox goes quiet rather than sitting there
    /// implying the search could be wider than it is.
    /// </summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var isFolder = !File.Exists(SearchBox.Text.Trim());
        RecursiveCheck.IsEnabled = isFolder;
        RecursiveCheck.Opacity = isFolder ? 1 : 0.5;
    }

    // ── Chrome ────────────────────────────────────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (ExtractorSettings.Show(this, _config)) _config.Save();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Double-clicking the title bar maximises too — that's WindowChrome's
    /// doing, not ours — so the glyph has to follow the state rather than the
    /// click.
    /// </summary>
    private void Window_StateChanged(object sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // Escapes rather than pasted glyphs: these are Segoe MDL2 private-use
        // codepoints, which render as tofu in most editors and don't survive
        // every re-encode.
        WindowMaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        WindowMaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _cancel?.Cancel();
        SaveRules();
        SaveConfig();
    }

    private void SaveConfig()
    {
        _config.SearchPath = Blank(SearchBox.Text);
        _config.Recursive = RecursiveCheck.IsChecked == true;
        _config.OutputDir = Blank(OutputBox.Text);
        _config.StripMipmaps = StripCheck.IsChecked == true;
        _config.ExtractIndividually = IndividualCheck.IsChecked == true;
        _config.AddFolderPrefix = PrefixCheck.IsChecked == true;
        _config.Save();

        static string? Blank(string text) => text.Trim() is { Length: > 0 } t ? t : null;
    }
}
