using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EgoEngineLibrary.Archive.Erp;
using Microsoft.Win32;
using SimplifiedSwapper.Core.Config;
using SimplifiedSwapper.Core.Project;
using SimplifiedChassisSwapper.Core.Swap;
using SimplifiedChassisSwapper.Core.Util;

namespace SimplifiedSwapper;

public partial class ChassisPanel : UserControl, ISwapPanel
{
    private static Brush ErrorBrush => Themed("Danger", 0xB0, 0x2A, 0x2A);
    private static Brush HintBrush => Themed("TextMuted", 0x8A, 0x92, 0x9C);

    /// <summary>
    /// The status line's own colour. Full strength rather than the muted grey
    /// the field hints use: it is the one line on the bar, and grey text that
    /// small picks up a blue cast from subpixel rendering.
    /// </summary>
    private static Brush StatusBrush => Themed("TextPrimary", 0xEA, 0xEA, 0xEA);

    private static Brush OkBrush => Themed("Accent", 0x2E, 0x7D, 0x4F);

    private UserConfig _config = null!;
    private ISerpsHost _host = null!;

    /// <summary>Set once the constructor is done, so programmatic setup doesn't trigger validation.</summary>
    private bool _ready;

    /// <summary>True while a swap is running — inputs stay locked until it finishes.</summary>
    private bool _busy;

    // Values we filled in ourselves. A box is only re-derived while it still holds
    // what we put there, so anything the user typed survives.
    private string _autoOldName = "";

    /// <summary>
    /// How long a replacement name may be.
    ///
    /// Not the format's limit — identifiers take any length — but the tightest
    /// thing in the archive: a handful of .layer files store the name in a slot
    /// of fixed width, sixteen bytes in every 2026 car measured. The swap reads
    /// the real figure off the car it is given and refuses anything longer, so
    /// this is only what the panel says while typing.
    /// </summary>
    private const int MaxNameLength = 16;


    public ChassisPanel()
    {
        InitializeComponent();
        SwapButton.IsEnabledChanged += (_, _) => CanSwapChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Hand the panel the app's settings and the template host, and let it
    /// start. Separate from the constructor because a UserControl is built by
    /// XAML with no arguments, and there is nothing useful to do before there
    /// is a config.
    /// </summary>
    public void Initialise(UserConfig config, ISerpsHost host)
    {
        _config = config;
        _host = host;

        // Opening or closing a template is the difference between a swap having
        // somewhere to go and not — and it is also what decides the team slot.
        _host.TemplateChanged += (_, _) => RefreshTeamErps();

        _ready = true;
        RefreshCars();
        RefreshTeamErps();
        _ = RefreshEngineAudioAsync();
    }

    // -- The car -------------------------------------------------------------

    /// <summary>
    /// Fill the car list from the game folder. Every 2026 team's ERP, whichever
    /// template is open — the template says which slot a car goes into, not
    /// which car you want in it.
    /// </summary>
    private void RefreshCars()
    {
        if (!_ready) return;

        var cars = TeamErps.AllCars(_config.GameDir);

        var chosen = (ChassisCarBox.SelectedItem as TeamErps.Candidate)?.Path;
        ChassisCarBox.ItemsSource = cars;
        ChassisCarBox.IsEnabled = !_busy && cars.Count > 0;
        ChassisCarBox.SelectedItem = cars.FirstOrDefault(c =>
            string.Equals(c.Path, chosen, StringComparison.OrdinalIgnoreCase));

        ChassisHint.Text = cars.Count > 0
            ? "The car you want to drive — its model, textures and cameras are kept."
            : "No 2026 cars under your game folder — check the folder in Settings.";

        if (ChassisCarBox.SelectedItem is null) ChassisBox.Text = "";
        UpdateValidation();
    }

    private void ChassisCar_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ChassisBox is null) return;   // fires during InitializeComponent
        if (ChassisCarBox.SelectedItem is not TeamErps.Candidate car)
        {
            ChassisBox.Text = "";
            return;
        }

        ChassisBox.Text = car.Path;

        // The token to replace is the chassis ERP's own name in every case we've
        // seen, so fill it in — unless the user has typed something else.
        var token = Path.GetFileNameWithoutExtension(car.Path);
        if (OldNameBox.Text.Length == 0 || OldNameBox.Text == _autoOldName)
        {
            _autoOldName = token;
            OldNameBox.Text = token;
        }

    }

    // -- Team slot ----------------------------------------------------------

    /// <summary>
    /// Fill the team list from the open template.
    ///
    /// Which team a swap takes over is not a free choice: a template is built
    /// for one team's slot, and putting a car into a different team's ERP than
    /// the template expects makes a mod that doesn't work. So the shader package
    /// the template carries picks it, and this panel offers only what that
    /// leaves — one ERP for a real team, one per engine for My Team.
    /// </summary>
    private void RefreshTeamErps()
    {
        if (!_ready) return;

        var candidates = _host.HasTemplate
            ? TeamErps.For(_config.GameDir, _host.ShaderPackage)
            : new List<TeamErps.Candidate>();

        TeamErpBox.ItemsSource = candidates;
        TeamErpBox.IsEnabled = candidates.Count > 1;
        TeamErpBox.SelectedItem = candidates.Count > 0 ? candidates[0] : null;
        TeamErpHint.Text = TeamHint(candidates);

        // SelectionChanged doesn't fire for a list that went empty, so the store
        // is written here rather than only there.
        TeamBox.Text = (TeamErpBox.SelectedItem as TeamErps.Candidate)?.Path ?? "";
        UpdateValidation();
    }

    /// <summary>Why the list holds what it holds — the useful half when it's empty.</summary>
    private string TeamHint(IReadOnlyList<TeamErps.Candidate> candidates)
    {
        if (!_host.HasTemplate)
            return "The slot the car takes over. Open a template and it names its own team.";

        var team = TeamErps.TeamNameOf(_host.ShaderPackage);
        if (team is null)
            return "This template's shader package isn't one of the 2026 teams, so there is no slot to swap into.";

        if (candidates.Count == 0)
            return $"{team}'s ERP isn't in this game folder — check the folder in Settings.";

        return candidates.Count == 1
            ? $"{team}, from the open template. Its livery slot, wing setup and driver animations are used."
            : $"{team} — one slot per engine. Pick the one you race with.";
    }

    private void TeamErp_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TeamBox is null) return;   // fires during InitializeComponent
        TeamBox.Text = (TeamErpBox.SelectedItem as TeamErps.Candidate)?.Path ?? "";
    }


    /// <summary>
    /// Back to how the panel opens. The two ERPs, the rename and the animation
    /// picks were all chosen for the template that has just gone, so none of
    /// them should still be sitting there for the next one.
    ///
    /// The season, animsets and engine-audio lists come off the game folder
    /// rather than the template, so they are reloaded rather than emptied —
    /// same as at startup.
    /// </summary>
    public void Reset()
    {
        if (!_ready) return;

        // Off while the boxes are cleared: each one fires Inputs_Changed, and
        // there is no point validating four times on the way to empty.
        _ready = false;
        ChassisBox.Text = "";
        TeamBox.Text = "";
        OldNameBox.Text = "";
        NewNameBox.Text = "";
        _autoOldName = "";
        ChassisCarBox.ItemsSource = null;
        TeamErpBox.ItemsSource = null;
        LogBox.Clear();
        SwapProgress.Value = 0;
        ProgressLabel.Text = "";
        _completionMessage = null;
        _ready = true;

        RefreshCars();
        RefreshTeamErps();
        _ = RefreshEngineAudioAsync();
    }

    /// <summary>Settings closed. A different game install makes the cached animation lists stale.</summary>
    public void SettingsChanged(bool gameDirChanged)
    {
        if (!gameDirChanged) return;

        RefreshCars();
        RefreshTeamErps();
        _ = RefreshEngineAudioAsync();
    }

    // ── Pickers ─────────────────────────────────────────────────────────────

    private string? PickErp(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "ERP archives (*.erp)|*.erp|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = FirstExistingDir(_config.LastErpFolder, _config.GameDir) ?? "",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return null;

        _config.LastErpFolder = Path.GetDirectoryName(dlg.FileName);
        _config.Save();
        return dlg.FileName;
    }

    // ── Validation ──────────────────────────────────────────────────────────

    private void Inputs_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;

        UpdateValidation();
    }

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
        // Always the team: the control is hidden, so this is the answer rather
        // than a default someone can move off.
        EngineAudioBox.SelectedItem = choices.First(c => c.Source == EngineAudioSource.Team);

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

    private void UpdateValidation()
    {
        var old = OldNameBox.Text.Trim();
        var neu = NewNameBox.Text.Trim();

        // The two names no longer have to match in length: the rename knows how
        // the archive frames its strings and rewrites them properly. What is
        // left is a ceiling, from a few .layer files that keep the name in a
        // slot of fixed width. Every 2026 car measures the same sixteen, and
        // the swap checks the real figure for the car in hand before starting.
        if (neu.Length == 0)
        {
            LengthHint.Text = $"Up to {MaxNameLength} characters.";
            LengthHint.Foreground = HintBrush;
        }
        else if (neu.Length > MaxNameLength)
        {
            LengthHint.Text = $"'{neu}' is {neu.Length} characters — {MaxNameLength} is as much as " +
                              "the car's camera and effect layers have room for.";
            LengthHint.Foreground = ErrorBrush;
        }
        else
        {
            LengthHint.Text = $"{neu.Length} of {MaxNameLength} characters.";
            LengthHint.Foreground = OkBrush;
        }

        var problem = FirstProblem();
        StatusLabel.Foreground = StatusBrush;
        StatusLabel.Text = problem ?? "Ready to swap.";
        SwapButton.IsEnabled = problem is null;
    }

    /// <summary>The one thing still blocking a swap, or null when everything's set.</summary>
    private string? FirstProblem()
    {
        if (_busy) return "Swapping…";
        if (!_host.HasTemplate) return "Open a template to swap into.";
        if (ChassisBox.Text.Trim().Length == 0) return "Choose the car you want to drive.";
        if (!File.Exists(ChassisBox.Text.Trim())) return "That car's ERP isn't in the game folder.";
        if (TeamBox.Text.Trim().Length == 0) return "This template has no team ERP to swap into.";
        if (!File.Exists(TeamBox.Text.Trim())) return "This template's team ERP isn't in the game folder.";

        var old = OldNameBox.Text.Trim();
        var neu = NewNameBox.Text.Trim();
        if (old.Length == 0) return "Enter the name to replace.";
        if (neu.Length == 0) return "Enter the replacement name.";
        if (neu.Length > MaxNameLength)
            return $"The replacement name can be at most {MaxNameLength} characters.";
        if (string.Equals(old, neu, StringComparison.Ordinal))
            return "The replacement name is the same as the original.";

        return null;
    }

    // ── Swap ────────────────────────────────────────────────────────────────


    // ── What the window's Swap button drives ────────────────────────────────
    //
    // The button lives in the title bar so both tabs share one, which means the
    // panel has to say whether a swap is possible and do it when asked. The
    // hidden SwapButton is still the state: every validation path already sets
    // it, so mirroring it is cheaper and safer than a second source of truth.

    /// <summary>Whether a swap could start right now.</summary>
    public bool CanSwap => SwapButton.IsEnabled;

    /// <summary>Raised when that answer changes, so the window can follow.</summary>
    public event EventHandler? CanSwapChanged;


    /// <summary>What the button should read right now.</summary>
    public string SwapLabel => (string)SwapButton.Content;

    /// <summary>Start one, as if the panel's own button had been pressed.</summary>
    public void RunSwap() => Swap_Click(SwapButton, new RoutedEventArgs());

    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        if (FirstProblem() is not null) return;

        // Nobody picks where this goes any more: the swap writes to scratch and
        // the window files it in the open template. Outside the template until
        // it has finished, so a failed run leaves nothing in there.
        var outputPath = _host.ScratchErpPath(SwapParts.Chassis);

        var options = new ChassisSwapOptions
        {
            ChassisErpPath = ChassisBox.Text.Trim(),
            TeamErpPath = TeamBox.Text.Trim(),
            OldName = OldNameBox.Text.Trim(),
            NewName = NewNameBox.Text.Trim(),
            GameDir = _config.GameDir,
            OutputPath = outputPath,
            // No overrides: the season comes from the chassis's own year and both
            // animsets from matching their names, which is what this panel used
            // to do in front of it. One derivation, in the engine, where the
            // fallback already lived.
            EngineAudio = SelectedEngineChoice?.Source ?? EngineAudioSource.Chassis,
            EngineAudioPick = SelectedEngineChoice?.Entry,
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
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Swap failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ReportSuccess(ChassisSwapResult result, string outputPath)
    {
        var part = _host.PlaceSwap(outputPath, SwapParts.Chassis);

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
        Log($"Added to the template as {part}");

        SwapProgress.Value = 1;
        ProgressLabel.Text = "100%";
        _completionMessage = result.Warnings.Count > 0
            ? $"Done with {result.Warnings.Count} warning(s) — see the log."
            : "Done — added to the template. Save it to keep it.";
        _completionIsWarning = result.Warnings.Count > 0;
    }

    // Held until SetBusy(false) has re-run validation, which would otherwise
    // overwrite the outcome with "Ready to swap."
    private string? _completionMessage;
    private bool _completionIsWarning;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var c in new Control[] { OldNameBox, NewNameBox, EngineAudioBox })
            c.IsEnabled = !busy;

        ChassisCarBox.IsEnabled = !busy && ChassisCarBox.Items.Count > 0;

        // Only ever live when there is more than one slot to choose between.
        TeamErpBox.IsEnabled = !busy && TeamErpBox.Items.Count > 1;

        SwapButton.Content = busy ? "Swapping…" : "Swap";
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;

        if (busy)
        {
            SwapButton.IsEnabled = false;
            StatusLabel.Foreground = StatusBrush;
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

    /// <summary>Palette lookup with a fallback, for the few brushes set in code.</summary>
    private static Brush Themed(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(r, g, b));
}
