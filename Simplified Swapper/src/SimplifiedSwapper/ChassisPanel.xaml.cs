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
    /// The car this template is already for. Selecting anything else is what
    /// makes this half of the page a request rather than a starting position —
    /// see <see cref="IsChanged"/>.
    /// </summary>
    private TeamErps.Candidate? _defaultCar;

    /// <summary>
    /// True once a car other than the template's own is chosen.
    ///
    /// With nothing chosen, or the template's own car chosen, there is nothing
    /// being asked for: rebuilding Ferrari's car as Ferrari's car is work with
    /// no result, so Swap leaves this half alone.
    /// </summary>
    public bool IsChanged =>
        ChassisCarBox.SelectedItem is TeamErps.Candidate car &&
        !string.Equals(car.Path, _defaultCar?.Path, StringComparison.OrdinalIgnoreCase);


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
        _host.TemplateChanged += (_, _) =>
        {
            RefreshCars();        // the template decides which car is the default
            RefreshTeamErps();
        };

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

        // A template the game won't take a swapped chassis for gets no car
        // list at all. Offering one and failing at the end, or worse producing
        // a mod that quietly doesn't load, is worse than saying so here.
        var allowed = !_host.HasTemplate || TeamErps.CanSwapChassis(_host.ShaderPackage);
        var cars = allowed ? TeamErps.AllCars(_config.GameDir) : [];

        var chosen = (ChassisCarBox.SelectedItem as TeamErps.Candidate)?.Path;
        ChassisCarBox.ItemsSource = cars;
        ChassisCarBox.IsEnabled = !_busy && cars.Count > 0;
        ChassisCarBox.SelectedItem = cars.FirstOrDefault(c =>
            string.Equals(c.Path, chosen, StringComparison.OrdinalIgnoreCase));

        // A template opens showing the car it is already for. That is the state
        // of not having asked for anything, and what IsChanged measures against.
        _defaultCar = _host.HasTemplate
            ? cars.FirstOrDefault(c => c.Label == TeamErps.TeamNameOf(_host.ShaderPackage))
            : null;
        ChassisCarBox.SelectedItem ??= _defaultCar;

        ChassisHint.Text = !allowed
            ? $"{TeamErps.TeamNameOf(_host.ShaderPackage)} can't take a swapped chassis — " +
              "the game doesn't load one."
            : cars.Count > 0
                ? "The car you want to drive — its model, textures and cameras are kept."
                : "No 2026 cars under your game folder — check the folder in Settings.";

        // The rename only means anything alongside a car.
        NewNameBox.IsEnabled = allowed;

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

        // No slot to offer where no chassis swap is going to happen. My Team's
        // six engine slots are a real choice about a swap that can't be made.
        var candidates = _host.HasTemplate && TeamErps.CanSwapChassis(_host.ShaderPackage)
            ? TeamErps.For(_config.GameDir, _host.ShaderPackage)
            : new List<TeamErps.Candidate>();

        TeamErpBox.ItemsSource = candidates;
        TeamErpBox.IsEnabled = candidates.Count > 1;
        TeamErpBox.SelectedItem = candidates.Count > 0 ? candidates[0] : null;

        // One slot is a fact, not a choice, so it reads as text. My Team's six
        // are a choice and keep the list.
        TeamErpBox.Visibility = candidates.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        // The car's own hint carries the reason when the swap is off, so this
        // one would only repeat it.
        TeamErpHint.Text = _host.HasTemplate && !TeamErps.CanSwapChassis(_host.ShaderPackage)
            ? ""
            : TeamHint(candidates);

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
        _host.ReportProgress(-1);
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

        // Length is the one rule the user can't work around — fragment payloads are
        // patched by equal-length byte replacement — so say so as they type.
        if (_host.HasTemplate && !TeamErps.CanSwapChassis(_host.ShaderPackage))
        {
            // Nothing to rename into, so nothing to say about lengths.
            LengthHint.Text = "";
        }
        else if (old.Length == 0 || neu.Length == 0)
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

        OldNameLabel.Text = old.Length == 0
            ? ""
            : $"Replaces '{old}', the name the car has now.";

        var problem = FirstProblem();
        SwapButton.IsEnabled = problem is null;

        // Not only when the button flips: choosing the template's own car again
        // leaves this half perfectly valid and no longer asked for, and the
        // window has to hear about that too.
        CanSwapChanged?.Invoke(this, EventArgs.Empty);

        // Only speak for the page when this half is the one with something to
        // say. Unchanged, it has nothing: the other half may be mid-swap.
        if (IsChanged || problem is null)
            _host.ReportStatus(problem ?? "Ready to swap.", SwapStatus.Plain);
    }

    /// <summary>The one thing still blocking a swap, or null when everything's set.</summary>
    private string? FirstProblem()
    {
        if (_busy) return "Swapping…";
        if (!_host.HasTemplate) return "Open a template to swap into.";
        if (!TeamErps.CanSwapChassis(_host.ShaderPackage))
            return $"{TeamErps.TeamNameOf(_host.ShaderPackage) ?? "This template"} can't take a swapped chassis.";
        if (ChassisBox.Text.Trim().Length == 0) return "Choose the car you want to drive.";
        if (!File.Exists(ChassisBox.Text.Trim())) return "That car's ERP isn't in the game folder.";
        if (TeamBox.Text.Trim().Length == 0) return "This template has no team ERP to swap into.";
        if (!File.Exists(TeamBox.Text.Trim())) return "This template's team ERP isn't in the game folder.";

        var old = OldNameBox.Text.Trim();
        var neu = NewNameBox.Text.Trim();
        if (old.Length == 0) return "Enter the name to replace.";
        if (neu.Length == 0) return "Enter the replacement name.";
        if (old.Length != neu.Length) return "The two names must be the same length.";
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


    /// <summary>
    /// The car being swapped in, as the dropdown names it. It becomes the
    /// folder's name in the template, so a template shows what was done to it
    /// without being opened.
    /// </summary>
    private string CarName => (ChassisCarBox.SelectedItem as TeamErps.Candidate)?.Label ?? "Swapped";

    /// <summary>Run this half's swap.</summary>
    public async Task RunSwapAsync()
    {
        if (FirstProblem() is not null) return;

        // Nobody picks where this goes any more: the swap writes to scratch and
        // the window files it in the open template. Outside the template until
        // it has finished, so a failed run leaves nothing in there.
        var outputPath = _host.ScratchErpPath(SwapParts.Chassis(CarName));

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
            _host.ReportProgress(p.Fraction);
            if (p.Message == lastMessage) return;
            lastMessage = p.Message;
            _host.ReportStatus(p.Message, SwapStatus.Plain);
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
            _host.ReportProgress(-1);
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
        var part = _host.PlaceSwap(outputPath, SwapParts.Chassis(CarName));

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

        _host.ReportProgress(1);
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

        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;

        if (busy)
        {
            SwapButton.IsEnabled = false;
            _completionMessage = null;
            return;
        }

        UpdateValidation();
        if (_completionMessage is not null)
            _host.ReportStatus(_completionMessage,
                _completionIsWarning ? SwapStatus.Problem : SwapStatus.Done);
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
