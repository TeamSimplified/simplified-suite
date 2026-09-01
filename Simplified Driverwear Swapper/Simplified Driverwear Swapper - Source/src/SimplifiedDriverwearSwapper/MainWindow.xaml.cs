using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SimplifiedDriverwearSwapper.Core.Config;
using SimplifiedDriverwearSwapper.Core.Driverwear;
using SimplifiedDriverwearSwapper.Core.Toolkit;

namespace SimplifiedDriverwearSwapper;

public partial class MainWindow : Window
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x2A, 0x2A));
    private static readonly Brush HintBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9C));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x4F));

    private readonly UserConfig _config = UserConfig.Load();

    private List<DriverwearCatalogue.DriverwearEntry> _driverwear = new();
    private List<DriverwearCatalogue.DriverwearEntry> _suits = new();

    /// <summary>The Toolkit version, once confirmed working; null until then.</summary>
    private readonly string _toolkitVersion = SimplifiedDriverwearSwapper.Core.Toolkit.Erp.Version;

    public MainWindow()
    {
        InitializeComponent();
        _ = StartupAsync();
    }

    private async Task StartupAsync()
    {
        RefreshDriverwearLists();
        RefreshStatus();
        RefreshSwapButton();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_config) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _config.Save();
        RefreshDriverwearLists();
        RefreshStatus();
        RefreshSwapButton();
    }

    /// <summary>
    /// Fill the dropdowns. Driverwear offers the team liveries; Team offers those
    /// plus the MyTeam suits, so a suit can be dressed in a team's wear.
    /// </summary>
    private void RefreshDriverwearLists()
    {
        _driverwear = DriverwearCatalogue.Scan(_config.GameDir);
        _suits = DriverwearCatalogue.ScanMyTeamSuits(_config.GameDir);

        var teamTargets = _driverwear.Concat(_suits).ToList();

        DriverwearBox.ItemsSource = _driverwear;
        TeamBox.ItemsSource = teamTargets;
        DriverwearBox.IsEnabled = _driverwear.Count > 0;
        TeamBox.IsEnabled = teamTargets.Count > 0;

        DriverwearHint.Text = "The suit, gloves and boots you want to use.";
        TeamHint.Text = _suits.Count > 0
            ? $"The team that will wear it — or one of your {_suits.Count} MyTeam suits."
            : "The team that will wear it.";
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DriverwearHint is null) return;   // fires during InitializeComponent

        DriverwearHint.Text = Describe(
            DriverwearBox.SelectedItem as DriverwearCatalogue.DriverwearEntry,
            "The suit, gloves and boots you want to use.");
        TeamHint.Text = Describe(
            TeamBox.SelectedItem as DriverwearCatalogue.DriverwearEntry,
            "The team that will wear it.");

        SyncOutputPath();
        RefreshStatus();
        RefreshSwapButton();
    }

    // ── Output ──────────────────────────────────────────────────────────────

    /// <summary>The path we filled in ourselves, so a typed one is never clobbered.</summary>
    private string _autoOutput = "";

    /// <summary>
    /// Default file name for a swap onto a given set. Named after the team, since
    /// it's the team's ERP being rebuilt, and prefixed so a swapped file is never
    /// mistaken for the game's own.
    /// </summary>
    private static string DefaultOutputName(DriverwearCatalogue.DriverwearEntry team) =>
        $"swap_{team.Name}.erp";

    /// <summary>
    /// Keep the output named after the current team, until the user picks a path
    /// themselves.
    /// </summary>
    private void SyncOutputPath()
    {
        if (TeamBox.SelectedItem is not DriverwearCatalogue.DriverwearEntry team) return;
        if (OutputBox.Text.Length != 0 && OutputBox.Text != _autoOutput) return;

        var folder = FirstExistingDir(_config.LastOutputFolder)
                     ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try { _autoOutput = Path.Combine(folder, DefaultOutputName(team)); }
        catch { return; }   // characters the filesystem won't take
        OutputBox.Text = _autoOutput;
    }

    private void Output_Changed(object sender, TextChangedEventArgs e)
    {
        if (OutputHint is null) return;   // fires during InitializeComponent
        RefreshSwapButton();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var team = TeamBox.SelectedItem as DriverwearCatalogue.DriverwearEntry;

        var dlg = new SaveFileDialog
        {
            Title = "Save the swapped ERP",
            Filter = "ERP archives (*.erp)|*.erp",
            DefaultExt = ".erp",
            AddExtension = true,
            FileName = SafeFileName(OutputBox.Text) ?? (team is null ? "" : DefaultOutputName(team)),
            InitialDirectory = FirstExistingDir(SafeDirName(OutputBox.Text), _config.LastOutputFolder) ?? "",
            OverwritePrompt = false,   // confirmed at swap time instead
        };
        if (dlg.ShowDialog(this) != true) return;

        OutputBox.Text = dlg.FileName;
        _autoOutput = "";              // an explicit choice — stop re-deriving it
        _config.LastOutputFolder = Path.GetDirectoryName(dlg.FileName);
        _config.Save();
    }

    /// <summary>
    /// Say what a pick actually points at. A set whose ERP is missing is called
    /// out here rather than at swap time.
    /// </summary>
    private static string Describe(DriverwearCatalogue.DriverwearEntry? entry, string fallback)
    {
        if (entry is null) return fallback;
        return entry.HasErp
            ? Path.GetFileName(entry.ErpPath)
            : $"No {entry.Name}.erp in this folder.";
    }

    private void RefreshStatus()
    {
        var gameDir = _config.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            SetStatus($"Toolkit {_toolkitVersion} ready. Set your game folder in Settings to begin.", HintBrush);
            return;
        }

        if (_driverwear.Count == 0)
        {
            SetStatus("No driverwear found — expected character_package/team_livery under the game folder.",
                ErrorBrush);
            return;
        }

        var from = DriverwearBox.SelectedItem as DriverwearCatalogue.DriverwearEntry;
        var to = TeamBox.SelectedItem as DriverwearCatalogue.DriverwearEntry;

        if (from is null || to is null)
        {
            SetStatus($"{_driverwear.Count} driverwear sets found. Choose which wear goes on which team.",
                MutedBrush);
            return;
        }

        if (from.Name == to.Name)
        {
            SetStatus($"{from.Name} already wears its own driverwear.", MutedBrush);
            return;
        }

        if (OutputBox.Text.Trim().Length == 0)
        {
            SetStatus("Choose where to save the swapped ERP.", MutedBrush);
            return;
        }

        SetStatus($"{from.Name} driverwear on {to.Name}.", MutedBrush);
    }

    /// <summary>
    /// Both sides must be chosen and different — putting a set's own wear back on
    /// itself is a no-op, not a swap.
    /// </summary>
    private void RefreshSwapButton()
    {
        var from = DriverwearBox.SelectedItem as DriverwearCatalogue.DriverwearEntry;
        var to = TeamBox.SelectedItem as DriverwearCatalogue.DriverwearEntry;

        SwapButton.IsEnabled =
            from is not null && to is not null &&
            !string.Equals(from.Name, to.Name, StringComparison.OrdinalIgnoreCase) &&
            OutputBox.Text.Trim().Length > 0;
    }

    // ── Swap ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The swap folder, beside the exe. Kept next to the app rather than in temp
    /// so the extracts are easy to find and inspect between runs.
    /// </summary>
    private static string WorkingDir => Path.Combine(AppContext.BaseDirectory, "swap");

    /// <summary>
    /// Pull out the pieces the swap works from. The two sides aren't symmetric:
    /// the driverwear gives its livery ERP and its textures — the wear itself —
    /// while the team gives its livery ERP plus each of its drivers, who all have
    /// to change for the team to actually wear it.
    ///
    /// Nothing is written to the output ERP yet; this only prepares.
    /// </summary>
    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        if (DriverwearBox.SelectedItem is not DriverwearCatalogue.DriverwearEntry from ||
            TeamBox.SelectedItem is not DriverwearCatalogue.DriverwearEntry to) return;

        var outputPath = OutputBox.Text.Trim();
        if (File.Exists(outputPath))
        {
            var answer = MessageBox.Show(this,
                $"{Path.GetFileName(outputPath)} already exists. Replace it?",
                "Simplified Driverwear Swapper", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }

        var log = new List<string>();
        void Log(string line)
        {
            log.Add(line);
            DriverList.ItemsSource = null;
            DriverList.ItemsSource = log;
            if (log.Count > 0) DriverList.ScrollIntoView(log[^1]);
        }

        SetBusy(true);
        DriversHint.Text = $"Working in {WorkingDir}";
        SetStatus($"Swapping {from.Name} driverwear onto {to.Name}…", MutedBrush);

        try
        {
            var result = await DriverwearPrepare.RunAsync(new DriverwearPrepare.PrepareOptions
            {
                GameDir = _config.GameDir!,
                Driverwear = from,
                Team = to,
                WorkingDir = WorkingDir,
                OutputPath = outputPath,
            }, Log);

            Log("");
            foreach (var w in result.Warnings) Log($"warning: {w}");

            DriversHint.Text = $"{result.Outputs.Count} item(s) in {WorkingDir}";

            if (result.OutputPath is null)
                SetStatus("The output wasn't written — see the list.", ErrorBrush);
            else if (result.Warnings.Count > 0)
                SetStatus($"Wrote {Path.GetFileName(result.OutputPath)} " +
                          $"({result.OutputResources} resources) with {result.Warnings.Count} warning(s).", ErrorBrush);
            else
            {
                _config.LastOutputFolder = Path.GetDirectoryName(result.OutputPath);
                _config.Save();
                SetStatus($"Wrote {Path.GetFileName(result.OutputPath)} — " +
                          $"{result.OutputResources} resources, {result.DriverCount} driver(s).", OkBrush);
            }
        }
        catch (Exception ex)
        {
            Log("");
            Log($"FAILED: {ex.Message}");
            SetStatus($"Preparation failed: {ex.Message}", ErrorBrush);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (var c in new Control[] { DriverwearBox, TeamBox, OutputBox })
            c.IsEnabled = !busy;

        SwapButton.Content = busy ? "Working…" : "Swap";
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;

        if (busy) SwapButton.IsEnabled = false;
        else
        {
            DriverwearBox.IsEnabled = _driverwear.Count > 0;
            TeamBox.IsEnabled = _driverwear.Count + _suits.Count > 0;
            RefreshSwapButton();
        }
    }

    private void SetStatus(string text, Brush brush)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = brush;
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
}
