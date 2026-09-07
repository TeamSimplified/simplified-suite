using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EgoEngineLibrary.Archive.Erp;
using Microsoft.Win32;
using SimplifiedChassisSwapper.Core.Config;
using SimplifiedChassisSwapper.Core.Swap;
using SimplifiedChassisSwapper.Core.Util;

namespace SimplifiedChassisSwapper;

public partial class MainWindow : Window
{
    private static Brush ErrorBrush => Themed("Danger", 0xE0, 0x44, 0x34);
    private static Brush HintBrush => Themed("TextMuted", 0xA4, 0xA4, 0xA4);
    /// <summary>
    /// What the status line says after something worked - which is the same
    /// colour it reads in the rest of the time. A line that turns accent-blue to
    /// announce success is louder than the news, and the wording already says
    /// it. A problem still goes red, because that one is worth interrupting for.
    /// </summary>
    private static Brush OkBrush => HintBrush;

    private readonly UserConfig _config = UserConfig.Load();

    /// <summary>Set once the constructor is done, so programmatic setup doesn't trigger validation.</summary>
    private bool _ready;

    /// <summary>True while a swap is running — inputs stay locked until it finishes.</summary>
    private bool _busy;

    // Values we filled in ourselves. A box is only re-derived while it still holds
    // what we put there, so anything the user typed survives.
    private string _autoOldName = "";
    private string _autoOutput = "";

    // Cached animset list, keyed by the ERP it came from so switching between two
    // cars of the same season doesn't reload it.
    private string? _loadedIncPath;
    private List<string> _animsets = new();
    private int _animsetLoadToken;

    private List<IncardriverLocator.IncardriverSeason> _seasons = new();

    /// <summary>Set while we fill the season box ourselves, so it doesn't read as a user pick.</summary>
    private bool _suppressSeasonChange;

    private string? _lastOutput;

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);
        VersionLabel.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(2) ?? "1.0");
        _ready = true;
        UpdateValidation();
        _ = RefreshSeasonsAsync();
        _ = RefreshEngineAudioAsync();
    }

    // ── Pickers ─────────────────────────────────────────────────────────────

    private void BrowseChassis_Click(object sender, RoutedEventArgs e)
    {
        var path = PickErp("Choose the chassis ERP — the car you want to drive");
        if (path is null) return;

        ChassisBox.Text = path;

        // The token to replace is the chassis ERP's own name in every case we've
        // seen, so fill it in — unless the user has typed something else.
        var token = Path.GetFileNameWithoutExtension(path);
        if (OldNameBox.Text.Length == 0 || OldNameBox.Text == _autoOldName)
        {
            _autoOldName = token;
            OldNameBox.Text = token;
        }
    }

    private void BrowseTeam_Click(object sender, RoutedEventArgs e)
    {
        var path = PickErp("Choose the team ERP — the slot the car takes over");
        if (path is null) return;
        TeamBox.Text = path;
    }

    private string? PickErp(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "ERP archives (*.erp)|*.erp|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = FirstExistingDir(_config.LastErpFolder, _config.GameDir) ?? "",
        };
        if (dlg.ShowDialog(this) != true) return null;

        _config.LastErpFolder = Path.GetDirectoryName(dlg.FileName);
        _config.Save();
        return dlg.FileName;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save the swapped ERP",
            Filter = "ERP archives (*.erp)|*.erp",
            DefaultExt = ".erp",
            AddExtension = true,
            FileName = SafeFileName(OutputBox.Text) ?? $"{NewNameBox.Text.Trim()}.erp",
            InitialDirectory = FirstExistingDir(
                SafeDirName(OutputBox.Text), _config.LastOutputFolder) ?? "",
            OverwritePrompt = false,   // confirmed at swap time instead
        };
        if (dlg.ShowDialog(this) != true) return;

        OutputBox.Text = dlg.FileName;
        _autoOutput = "";             // an explicit choice — stop re-deriving it
        _config.LastOutputFolder = Path.GetDirectoryName(dlg.FileName);
        _config.Save();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var before = _config.GameDir;
        if (!ChassisSettings.Show(this, _config)) return;

        _config.Save();
        if (!string.Equals(before, _config.GameDir, StringComparison.OrdinalIgnoreCase))
        {
            _loadedIncPath = null;    // different install — the cached lists are stale
            _ = RefreshSeasonsAsync();
            _ = RefreshEngineAudioAsync();
        }
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput is null || !File.Exists(_lastOutput)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true });
    }

    // ── Validation ──────────────────────────────────────────────────────────

    private void Inputs_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;

        // A different chassis implies a different season, so re-derive the default.
        // A different team only changes which animset gets preselected.
        if (ReferenceEquals(sender, ChassisBox)) _ = RefreshSeasonsAsync();
        else if (ReferenceEquals(sender, TeamBox)) _ = LoadAnimsetsAsync();

        if (ReferenceEquals(sender, NewNameBox)) SyncOutputPath();

        UpdateValidation();
    }

    private void Animset_Changed(object sender, SelectionChangedEventArgs e) => UpdateValidation();

    // ── Engine audio ────────────────────────────────────────────────────────

    /// <summary>
    /// One row of the engine dropdown. The two relative choices resolve against
    /// whichever ERPs are picked; the rest name a specific sound from the install.
    /// </summary>
    private sealed record EngineChoice(
        EngineAudioSource Source, string Label, EngineAudioCatalogue.EngineAudioEntry? Entry = null);

    private void EngineAudio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EngineAudioHint is null) return;   // fires during InitializeComponent
        EngineAudioHint.Text = (EngineAudioBox.SelectedItem as EngineChoice)?.Source switch
        {
            EngineAudioSource.Team =>
                "The car takes over the slot's engine sound, so it sounds like the team it replaces.",
            EngineAudioSource.Explicit when EngineAudioBox.SelectedItem is EngineChoice c && c.Entry is not null =>
                $"The car uses {c.Entry.Switch}, the engine {c.Entry.Team} runs in {c.Entry.Package}.",
            _ =>
                "The car keeps the donor's engine sound, which is the audio that belongs to the model.",
        };
    }

    /// <summary>
    /// Fill the engine dropdown by scanning the install. The pairings can't be
    /// derived from team names and differ per package, so they're read from the
    /// cars themselves — which also picks up modded ones. Runs off the UI thread;
    /// the two relative choices are usable immediately.
    /// </summary>
    private async Task RefreshEngineAudioAsync()
    {
        var choices = new List<EngineChoice>
        {
            new(EngineAudioSource.Chassis, "Sound like the chassis"),
            new(EngineAudioSource.Team, "Sound like the team"),
        };

        EngineAudioBox.ItemsSource = choices;
        EngineAudioBox.SelectedIndex = 0;

        var gameDir = _config.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir)) return;

        var token = ++_engineScanToken;
        List<EngineAudioCatalogue.EngineAudioEntry> entries;
        try
        {
            entries = await Task.Run(() => EngineAudioCatalogue.Scan(gameDir));
        }
        catch
        {
            return;   // keep the two relative choices
        }

        if (token != _engineScanToken) return;   // a newer scan supersedes this one

        // Rebuild rather than append, so re-scanning can't stack duplicates.
        var selected = EngineAudioBox.SelectedItem as EngineChoice;
        choices = new List<EngineChoice>
        {
            new(EngineAudioSource.Chassis, "Sound like the chassis"),
            new(EngineAudioSource.Team, "Sound like the team"),
        };
        foreach (var entry in entries)
            choices.Add(new EngineChoice(EngineAudioSource.Explicit, entry.Label, entry));

        EngineAudioBox.ItemsSource = choices;
        EngineAudioBox.SelectedItem =
            choices.FirstOrDefault(c => c.Label == selected?.Label) ?? choices[0];
    }

    private int _engineScanToken;

    private EngineChoice? SelectedEngineChoice => EngineAudioBox.SelectedItem as EngineChoice;

    /// <summary>Keep the output file named after the new car, until the user picks a path themselves.</summary>
    private void SyncOutputPath()
    {
        var name = NewNameBox.Text.Trim();
        if (name.Length == 0) return;
        if (OutputBox.Text.Length != 0 && OutputBox.Text != _autoOutput) return;

        var folder = FirstExistingDir(_config.LastOutputFolder)
                     ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try { _autoOutput = Path.Combine(folder, name + ".erp"); }
        catch { return; }             // characters the filesystem won't take
        OutputBox.Text = _autoOutput;
    }

    private void UpdateValidation()
    {
        var old = OldNameBox.Text.Trim();
        var neu = NewNameBox.Text.Trim();

        // Length is the one rule the user can't work around — fragment payloads are
        // patched by equal-length byte replacement — so say so as they type.
        if (old.Length == 0 || neu.Length == 0)
        {
            LengthHint.Text = "Both names must be the same number of characters.";
            LengthHint.Foreground = HintBrush;
        }
        else if (old.Length != neu.Length)
        {
            LengthHint.Text = $"'{old}' is {old.Length} characters, '{neu}' is {neu.Length} — " +
                              $"they must match, so add or remove {Math.Abs(old.Length - neu.Length)}.";
            LengthHint.Foreground = ErrorBrush;
        }
        else
        {
            LengthHint.Text = $"{neu.Length} characters — matches.";
            LengthHint.Foreground = OkBrush;
        }

        var problem = FirstProblem();
        StatusLabel.Foreground = HintBrush;
        StatusLabel.Text = problem ?? "Ready to swap.";
        SwapButton.IsEnabled = problem is null;
    }

    /// <summary>The one thing still blocking a swap, or null when everything's set.</summary>
    private string? FirstProblem()
    {
        if (_busy) return "Swapping…";
        if (ChassisBox.Text.Trim().Length == 0) return "Choose a chassis ERP.";
        if (!File.Exists(ChassisBox.Text.Trim())) return "That chassis ERP doesn't exist.";
        if (TeamBox.Text.Trim().Length == 0) return "Choose a team ERP.";
        if (!File.Exists(TeamBox.Text.Trim())) return "That team ERP doesn't exist.";

        // A team's wep/ folder holds its special liveries beside its car, and
        // those are fair game as slots — each is a whole car with its own /wep/
        // setup. A few ship as texture overlays instead, with no /wep/ at all,
        // and the swap grafts exactly that. Building one produces a file that
        // looks right and loads as nothing, so it is caught here rather than
        // found later in game.
        if (NotACar(TeamBox.Text.Trim()) is { } teamProblem) return teamProblem;
        if (NotACar(ChassisBox.Text.Trim()) is { } chassisProblem) return chassisProblem;

        var old = OldNameBox.Text.Trim();
        var neu = NewNameBox.Text.Trim();
        if (old.Length == 0) return "Enter the name to replace.";
        if (neu.Length == 0) return "Enter the replacement name.";
        if (old.Length != neu.Length) return "The two names must be the same length.";
        if (string.Equals(old, neu, StringComparison.Ordinal))
            return "The replacement name is the same as the original.";

        if (OutputBox.Text.Trim().Length == 0) return "Choose where to save the swapped ERP.";
        return null;
    }

    /// <summary>
    /// Why this ERP isn't a car, or null when it is one.
    ///
    /// Answered from a cache: the check opens the archive, and this runs on every
    /// keystroke in the path boxes.
    /// </summary>
    private string? NotACar(string path) =>
        IsCarCached(path) ? null
            : $"{Path.GetFileName(path)} has no /wep/ setup, so it isn't a car — " +
              "some special liveries ship as texture overlays rather than whole cars.";

    private readonly Dictionary<(string Path, long Length, DateTime Written), bool> _isCarMemo = new();

    private bool IsCarCached(string path)
    {
        (string, long, DateTime) key;
        try
        {
            var info = new FileInfo(path);
            key = (path, info.Length, info.LastWriteTimeUtc);
        }
        catch
        {
            return false;
        }

        if (_isCarMemo.TryGetValue(key, out var known)) return known;
        return _isCarMemo[key] = EgoEngineLibrary.Archive.Erp.CarErp.IsCar(path);
    }

    // ── Driver animations ───────────────────────────────────────────────────

    /// <summary>
    /// Refill the season dropdown from the game folder and default it to the
    /// chassis's own year, then load that season's animsets.
    ///
    /// The game ships one <c>incardriver_f1.erp</c> per season and they carry
    /// different animsets, so the season is an explicit choice — the chassis path
    /// only supplies the default.
    /// </summary>
    private async Task RefreshSeasonsAsync()
    {
        var seasons = IncardriverLocator.ListSeasons(_config.GameDir);

        if (seasons.Count == 0)
        {
            _seasons = seasons;
            _suppressSeasonChange = true;
            SeasonBox.ItemsSource = null;
            _suppressSeasonChange = false;
            SeasonBox.IsEnabled = false;
            SeasonHint.Text = string.IsNullOrWhiteSpace(_config.GameDir)
                ? "Set your game folder in Settings to include the in-car driver animations."
                : "No incardriver_f1.erp under your game folder — driver animations will be left untouched.";
            ClearAnimsets();
            return;
        }

        var wanted = IncardriverLocator.InferYear(ChassisBox.Text.Trim());
        var pick = seasons.FirstOrDefault(s => s.Season == wanted) ?? seasons[0];

        _suppressSeasonChange = true;
        _seasons = seasons;
        SeasonBox.ItemsSource = seasons;
        SeasonBox.SelectedItem = pick;
        _suppressSeasonChange = false;

        SeasonBox.IsEnabled = !_busy;
        SeasonHint.Text = wanted is not null && pick.Season == wanted
            ? $"Matched the chassis's year. {seasons.Count} seasons available."
            : $"The chassis path doesn't name a season, so this defaults to {pick.Season}.";

        await LoadAnimsetsAsync();
    }

    private void Season_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSeasonChange) return;
        SeasonHint.Text = "Using the season you picked.";
        _ = LoadAnimsetsAsync();
    }

    /// <summary>
    /// Populate the animset dropdowns from the selected season's ERP. The names
    /// are read from the ERP rather than built from the team names — the tokens
    /// don't always match (the folder <c>force_india</c> owns the animset
    /// <c>…_forceindia</c>) — and both picks stay editable so a bad automatic
    /// match can be corrected instead of silently shipping.
    /// </summary>
    private async Task LoadAnimsetsAsync()
    {
        if (SeasonBox.SelectedItem is not IncardriverLocator.IncardriverSeason season)
        {
            ClearAnimsets();
            return;
        }

        if (season.Path != _loadedIncPath)
        {
            var token = ++_animsetLoadToken;
            IncardriverStatus.Text = "Reading driver animations…";
            ChassisAnimsetBox.IsEnabled = TeamAnimsetBox.IsEnabled = false;

            List<string> names;
            try
            {
                names = await Task.Run(() =>
                {
                    ErpFile erp = ErpFileIO.Load(season.Path);
                    return IncardriverLocator.ListAnimsets(erp);
                });
            }
            catch (Exception ex)
            {
                if (token != _animsetLoadToken) return;
                IncardriverStatus.Text = $"Couldn't read {Path.GetFileName(season.Path)}: {ex.Message}";
                return;
            }

            // A newer request already started — its result wins.
            if (token != _animsetLoadToken) return;

            _loadedIncPath = season.Path;
            _animsets = names;
        }

        ChassisAnimsetBox.ItemsSource = _animsets;
        TeamAnimsetBox.ItemsSource = _animsets;
        ChassisAnimsetBox.IsEnabled = TeamAnimsetBox.IsEnabled = !_busy && _animsets.Count > 0;

        IncardriverStatus.Text = _animsets.Count > 0
            ? $"{season.Season} — {_animsets.Count} animation sets"
            : $"The {season.Season} incardriver_f1.erp contains no animation sets.";

        PreselectForCar(ChassisAnimsetBox, ChassisBox.Text.Trim());
        PreselectForCar(TeamAnimsetBox, TeamBox.Text.Trim());
    }

    private void ClearAnimsets()
    {
        _loadedIncPath = null;
        _animsets = new List<string>();
        ChassisAnimsetBox.ItemsSource = null;
        TeamAnimsetBox.ItemsSource = null;
        ChassisAnimsetBox.IsEnabled = TeamAnimsetBox.IsEnabled = false;
        IncardriverStatus.Text = "";
    }

    /// <summary>
    /// Point an animset box at the car's own set, falling back to its team's —
    /// the same order the swap uses, so what the box shows is what would be
    /// picked if it were left alone. A special livery has no set of its own.
    /// </summary>
    private void PreselectForCar(ComboBox box, string erpPath)
    {
        if (erpPath.Length == 0) return;
        var match = IncardriverLocator.MatchAnimsetForCar(_animsets, erpPath);
        if (match is not null) box.SelectedItem = match;
    }

    // ── Swap ────────────────────────────────────────────────────────────────

    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        if (FirstProblem() is not null) return;

        var outputPath = OutputBox.Text.Trim();
        if (File.Exists(outputPath))
        {
            var answer = SimplifiedUi.MessageDialog.Show(this,
                $"{Path.GetFileName(outputPath)} already exists. Replace it?",
                "Simplified Chassis Swapper", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }

        var options = new ChassisSwapOptions
        {
            ChassisErpPath = ChassisBox.Text.Trim(),
            TeamErpPath = TeamBox.Text.Trim(),
            OldName = OldNameBox.Text.Trim(),
            NewName = NewNameBox.Text.Trim(),
            GameDir = _config.GameDir,
            OutputPath = outputPath,
            // Use exactly the season shown in the dropdown rather than letting the
            // engine re-detect it, so what runs matches what was on screen.
            IncardriverErpPath = (SeasonBox.SelectedItem as IncardriverLocator.IncardriverSeason)?.Path,
            EngineAudio = SelectedEngineChoice?.Source ?? EngineAudioSource.Chassis,
            EngineAudioPick = SelectedEngineChoice?.Entry,
            ChassisAnimsetOverride = ChassisAnimsetBox.SelectedItem as string,
            TeamAnimsetOverride = TeamAnimsetBox.SelectedItem as string,
        };

        SetBusy(true);
        LogBox.Clear();
        Log($"Chassis   {Path.GetFileName(options.ChassisErpPath)}");
        Log($"Team      {Path.GetFileName(options.TeamErpPath)}");
        Log($"Rename    {options.OldName} -> {options.NewName}");
        Log("");

        var lastMessage = "";
        var progress = new Progress<(string Message, double Fraction)>(p =>
        {
            SwapProgress.Value = p.Fraction;
            ProgressLabel.Text = $"{(int)(p.Fraction * 100)}%";
            if (p.Message == lastMessage) return;
            lastMessage = p.Message;
            StatusLabel.Text = p.Message;
        });
        var reporter = (IProgress<(string, double)>)progress;

        try
        {
            var result = await Task.Run(() =>
                new ChassisSwapper().Run(options, (msg, frac) => reporter.Report((msg, frac))));

            ReportSuccess(result, outputPath);
        }
        catch (Exception ex)
        {
            SwapProgress.Value = 0;
            ProgressLabel.Text = "";
            Log("");
            Log($"FAILED: {ex.Message}");
            SimplifiedUi.MessageDialog.Show(this, ex.Message, "Swap failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ReportSuccess(ChassisSwapResult result, string outputPath)
    {
        Log($"resources   {result.ResourcesIn} -> {result.ResourcesOut}");
        Log($"textures    {result.TexturesEmbedded} embedded" +
            (result.TexturesMipsUnreachable > 0
                ? $", {result.TexturesMipsUnreachable} without external mipmaps"
                : ""));
        Log($"renamed     {result.IdentifiersRenamed} identifiers, {result.FragmentsPatched} fragments");
        Log($"removed     {result.RemovedVtf} vtf, {result.RemovedWep} wep");
        Log($"grafted     {result.LayersRebuilt} layers rebuilt, {result.WepCopied} copied");
        Log(result.AnimsetTransplanted
            ? $"animations  {result.ChassisAnimset} -> {result.TeamAnimset}"
            : "animations  not transplanted");
        if (result.EngineAudioSwitch is not null)
            Log($"engine      {result.EngineAudioSwitch}");

        if (result.Warnings.Count > 0)
        {
            Log("");
            foreach (var w in result.Warnings) Log($"warning: {w}");
        }

        Log("");
        Log($"Saved to {outputPath}");

        _lastOutput = outputPath;
        OpenOutputButton.IsEnabled = true;
        _config.LastOutputFolder = Path.GetDirectoryName(outputPath);
        _config.Save();

        SwapProgress.Value = 1;
        ProgressLabel.Text = "100%";
        _completionMessage = result.Warnings.Count > 0
            ? $"Done with {result.Warnings.Count} warning(s) — see the log."
            : $"Done — saved {Path.GetFileName(outputPath)}.";
        _completionIsWarning = result.Warnings.Count > 0;
    }

    // Held until SetBusy(false) has re-run validation, which would otherwise
    // overwrite the outcome with "Ready to swap."
    private string? _completionMessage;
    private bool _completionIsWarning;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var c in new Control[] { ChassisBox, TeamBox, OldNameBox, NewNameBox, OutputBox, EngineAudioBox })
            c.IsEnabled = !busy;

        // The dropdowns stay disabled when there was nothing to populate them with.
        SeasonBox.IsEnabled = !busy && _seasons.Count > 0;
        ChassisAnimsetBox.IsEnabled = TeamAnimsetBox.IsEnabled = !busy && _animsets.Count > 0;

        SwapButton.Content = busy ? "Swapping…" : "Swap";
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;

        if (busy)
        {
            SwapButton.IsEnabled = false;
            StatusLabel.Foreground = HintBrush;
            _completionMessage = null;
            return;
        }

        UpdateValidation();
        if (_completionMessage is not null)
        {
            StatusLabel.Text = _completionMessage;
            StatusLabel.Foreground = _completionIsWarning ? ErrorBrush : OkBrush;
        }
    }

    private void Log(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string? FirstExistingDir(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c)) return c;
        return null;
    }

    private static string? SafeFileName(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path); }
        catch { return null; }
    }

    private static string? SafeDirName(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path); }
        catch { return null; }
    }

    // ── Window chrome ───────────────────────────────────────────────────────
    //
    // The bar at the top is ours, so these three are too. Everything native
    // that a frameless window still needs - rounded corners, and a maximise
    // that respects the taskbar - is in SimplifiedUi.FramelessWindow.

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Palette lookup with a fallback, for the few brushes set in code.</summary>
    private static Brush Themed(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(r, g, b));
}
