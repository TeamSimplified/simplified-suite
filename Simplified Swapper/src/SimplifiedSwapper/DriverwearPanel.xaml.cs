using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SimplifiedSwapper.Core.Config;
using SimplifiedSwapper.Core.Project;
using SimplifiedDriverwearSwapper.Core.Driverwear;
using SimplifiedDriverwearSwapper.Core.Toolkit;

namespace SimplifiedSwapper;

public partial class DriverwearPanel : UserControl, ISwapPanel
{
    private static Brush ErrorBrush => Themed("Danger", 0xE0, 0x44, 0x34);
    private static Brush HintBrush => Themed("TextMuted", 0xA4, 0xA4, 0xA4);

    /// <summary>
    /// The status line's own colour. Full strength rather than the muted grey
    /// the field hints use: it is the one line on the bar, and grey text that
    /// small picks up a blue cast from subpixel rendering.
    /// </summary>
    /// <summary>
    /// The status line, in the muted grey every app in the suite reads its
    /// status in. This one was TextPrimary, which made the same sentence look
    /// like an announcement here and a remark everywhere else.
    /// </summary>
    private static Brush StatusBrush => Themed("TextMuted", 0xA4, 0xA4, 0xA4);

    /// <summary>
    /// What the status line says after something worked - which is the same
    /// colour it reads in the rest of the time. A line that turns accent-blue to
    /// announce success is louder than the news, and the wording already says
    /// it. A problem still goes red, because that one is worth interrupting for.
    /// </summary>
    private static Brush OkBrush => StatusBrush;

    private UserConfig _config = null!;
    private ISerpsHost _host = null!;

    /// <summary>
    /// The wear this template's team already races in. Choosing anything else
    /// is what makes this half a request — see <see cref="IsChanged"/>.
    /// </summary>
    private DriverwearCatalogue.DriverwearEntry? _defaultWear;

    /// <summary>
    /// True once wear other than the team's own is chosen.
    ///
    /// Dressing Ferrari in Ferrari's own driverwear is work with no result, so
    /// Swap leaves this half alone until it is asked for something else.
    /// </summary>
    public bool IsChanged =>
        DriverwearBox.SelectedItem is DriverwearCatalogue.DriverwearEntry wear &&
        !string.Equals(wear.Name, _defaultWear?.Name, StringComparison.OrdinalIgnoreCase);

    private List<DriverwearCatalogue.DriverwearEntry> _driverwear = new();
    private List<DriverwearCatalogue.DriverwearEntry> _suits = new();

    /// <summary>The Toolkit version, once confirmed working; null until then.</summary>

    public DriverwearPanel()
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
        // somewhere to go and not, so it reads as an input like any other.
        _host.TemplateChanged += (_, _) =>
        {
            RefreshDriverwearLists();   // the template decides who wears it
            RefreshStatus();
            RefreshSwapButton();
        };

        RefreshDriverwearLists();
        RefreshStatus();
        RefreshSwapButton();
    }

    /// <summary>
    /// Back to how the panel opens. Both picks were made for the template that
    /// has just gone, so neither should still be sitting there for the next one.
    /// The lists themselves come off the game folder, so they are rebuilt.
    /// </summary>
    public void Reset()
    {
        if (_config is null) return;   // never initialised — nothing to reset

        DriverwearBox.SelectedItem = null;
        TeamBox.SelectedItem = null;
        _defaultWear = null;
        DriverList.ItemsSource = null;
        DriversHint.Text = "";

        RefreshDriverwearLists();
        RefreshStatus();
        RefreshSwapButton();
    }

    /// <summary>Settings closed. The lists come from the game folder, so they are rebuilt either way.</summary>
    public void SettingsChanged(bool gameDirChanged)
    {
        _ = gameDirChanged;

        RefreshDriverwearLists();
        RefreshStatus();
        RefreshSwapButton();
    }

    private async Task StartupAsync()
    {
        RefreshDriverwearLists();
        RefreshStatus();
        RefreshSwapButton();
    }

    /// <summary>
    /// Fill the dropdowns.
    ///
    /// Driverwear offers every set, because which wear you want is the whole
    /// choice. Team offers only what the open template is for: its drivers are
    /// the ones being re-dressed, and dressing a team the template isn't for
    /// would put a part in it that disagrees with the rest.
    /// </summary>
    private void RefreshDriverwearLists()
    {
        _driverwear = DriverwearCatalogue.Scan(_config.GameDir);
        _suits = DriverwearCatalogue.ScanMyTeamSuits(_config.GameDir);

        var allowed = !_host.HasTemplate || TeamErps.CanSwapDriverwear(_host.ShaderPackage);
        var targets = allowed ? TeamTargets() : new List<DriverwearCatalogue.DriverwearEntry>();

        DriverwearBox.ItemsSource = allowed ? _driverwear : new List<DriverwearCatalogue.DriverwearEntry>();
        DriverwearBox.IsEnabled = allowed && _driverwear.Count > 0;
        DriverwearHint.Text = allowed
            ? "The suit, gloves and boots the team will race in."
            : "Coming soon — My Team is built differently from a team and needs its own swap.";

        TeamBox.ItemsSource = targets;
        TeamBox.IsEnabled = targets.Count > 1;

        // A real team has one target and it doesn't matter which way this is
        // written. My Team has eighty-one, ordered by nothing in particular, so
        // the one the game itself defaults to is named rather than left to
        // whatever sorts first.
        TeamBox.SelectedItem =
            targets.FirstOrDefault(t => t.Name.Equals(TeamErps.MyTeamDefaultSuit, StringComparison.OrdinalIgnoreCase))
            ?? (targets.Count > 0 ? targets[0] : null);

        // One team is a fact, not a choice, so it reads as text. My Team's
        // suits are a choice and keep the list.
        TeamBox.Visibility = targets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        // The wear's own hint carries the reason when the swap isn't offered,
        // so this one would only repeat it.
        TeamHint.Text = allowed ? TargetHint(targets) : "";

        // The wear a template opens with is its own team's, which is the state
        // of not having asked for anything.
        _defaultWear = _host.HasTemplate
            ? _driverwear.FirstOrDefault(d => d.Name.Equals(
                TeamErps.DriverwearSetOf(_host.ShaderPackage), StringComparison.OrdinalIgnoreCase))
            : null;
        DriverwearBox.SelectedItem ??= _defaultWear;
    }

    /// <summary>
    /// Who the open template can dress: the one team it is for, or — for My
    /// Team, whose drivers wear the player's own suits rather than a team set —
    /// whichever of those suits they want.
    /// </summary>
    private List<DriverwearCatalogue.DriverwearEntry> TeamTargets()
    {
        if (!_host.HasTemplate) return new List<DriverwearCatalogue.DriverwearEntry>();

        if (TeamErps.DriverwearSetOf(_host.ShaderPackage) is { } set)
            return _driverwear
                .Where(d => d.Name.Equals(set, StringComparison.OrdinalIgnoreCase))
                .ToList();

        return TeamErps.WearsMyTeamSuits(_host.ShaderPackage)
            ? _suits
            : new List<DriverwearCatalogue.DriverwearEntry>();
    }

    /// <summary>Why the list holds what it holds — the useful half when it's empty.</summary>
    private string TargetHint(List<DriverwearCatalogue.DriverwearEntry> targets)
    {
        if (!_host.HasTemplate)
            return "Who will wear it. Open a template and it names its own team.";

        var team = TeamErps.TeamNameOf(_host.ShaderPackage);
        if (team is null)
            return "This template's shader package isn't one of the 2026 teams, so there is nobody to dress.";

        if (targets.Count == 0)
            return TeamErps.WearsMyTeamSuits(_host.ShaderPackage)
                ? "No MyTeam suits in this game folder — check the folder in Settings."
                : $"{team}'s driverwear isn't in this game folder — check the folder in Settings.";

        return targets.Count == 1
            ? $"{team}, from the open template. Its drivers are the ones being re-dressed."
            : $"My Team — {targets.Count} suits. Pick the one you race in.";
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DriverwearHint is null) return;   // fires during InitializeComponent

        // No file name here: the dropdown above already says which set this
        // is, and repeating it as an .erp only adds a line.
        DriverwearHint.Text = DriverwearBox.SelectedItem is DriverwearCatalogue.DriverwearEntry wear && !wear.HasErp
            ? $"No {wear.Name}.erp in this folder."
            : "The suit, gloves and boots the team will race in.";
        // The team hint says which template this is for, which a pick doesn't
        // change — only the source needs re-describing.

        RefreshStatus();
        RefreshSwapButton();
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
        if (_host.HasTemplate && !TeamErps.CanSwapDriverwear(_host.ShaderPackage))
        {
            SetStatus($"{TeamErps.TeamNameOf(_host.ShaderPackage)} driverwear is coming soon — " +
                      "nothing to do here yet.", StatusBrush);
            return;
        }

        var gameDir = _config.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            SetStatus("Set your game folder in Settings to begin.", StatusBrush);
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

        // Asked first: with no template there is no team to dress, so every
        // message below it would be about something the app can't know yet.
        if (!_host.HasTemplate)
        {
            SetStatus(from is null
                ? $"{_driverwear.Count} driverwear sets found. Open a template to swap into."
                : $"Open a template to swap {from.Name} driverwear into.", StatusBrush);
            return;
        }

        if (to is null)
        {
            // Not "see the team box": that box is only on screen for My Team's
            // suits, so for everyone else it names something invisible.
            var team = TeamErps.TeamNameOf(_host.ShaderPackage);
            SetStatus(team is null
                ? "This template isn't one of the 2026 teams, so there is nobody to dress."
                : $"{team}'s driverwear isn't in this game folder — check the folder in Settings.",
                StatusBrush);
            return;
        }

        if (from is null)
        {
            SetStatus($"{_driverwear.Count} driverwear sets found. Choose which wear {to.Name} will race in.",
                StatusBrush);
            return;
        }

        if (from.Name == to.Name)
        {
            SetStatus($"{from.Name} already wears its own driverwear.", StatusBrush);
            return;
        }

        SetStatus($"{from.Name} driverwear on {to.Name}.", StatusBrush);
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
            _host.HasTemplate && TeamErps.CanSwapDriverwear(_host.ShaderPackage) &&
            from is not null && to is not null &&
            !string.Equals(from.Name, to.Name, StringComparison.OrdinalIgnoreCase);

        // Same reason as the other half: choosing the team's own wear back is
        // still valid and no longer a request.
        CanSwapChanged?.Invoke(this, EventArgs.Empty);
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

    // ── What the window's Swap button drives ────────────────────────────────
    //
    // The button lives on the action bar so both panels share one, which means the
    // panel has to say whether a swap is possible and do it when asked. The
    // hidden SwapButton is still the state: every validation path already sets
    // it, so mirroring it is cheaper and safer than a second source of truth.

    /// <summary>Whether a swap could start right now.</summary>
    public bool CanSwap => SwapButton.IsEnabled;

    /// <summary>Raised when that answer changes, so the window can follow.</summary>
    public event EventHandler? CanSwapChanged;


    /// <summary>
    /// The wear being put on, as the dropdown names it. It becomes the folder's
    /// name in the template, so a template shows what was done to it without
    /// being opened.
    /// </summary>
    private string WearName =>
        (DriverwearBox.SelectedItem as DriverwearCatalogue.DriverwearEntry)?.Name ?? "Swapped";

    /// <summary>
    /// The ERP of whatever wear is selected, or null when nothing is or it has
    /// none. Independent of the template and of any swap — reading a UV layout
    /// out of an archive asks nothing of either.
    /// </summary>
    public string? SelectedWearErp =>
        (DriverwearBox.SelectedItem as DriverwearCatalogue.DriverwearEntry)?.ErpPath;

    /// <summary>What that wear is called, for naming the folder it writes into.</summary>
    public string? SelectedWearName =>
        (DriverwearBox.SelectedItem as DriverwearCatalogue.DriverwearEntry)?.Name;

    /// <summary>Run this half's swap.</summary>
    public async Task RunSwapAsync()
    {
        if (DriverwearBox.SelectedItem is not DriverwearCatalogue.DriverwearEntry from ||
            TeamBox.SelectedItem is not DriverwearCatalogue.DriverwearEntry to) return;

        // Nobody picks where this goes any more: the swap writes to scratch and
        // the window files it in the open template. Outside the template until
        // it has finished, so a failed run leaves nothing in there.
        var outputPath = _host.ScratchErpPath(SwapParts.Driverwear(WearName));

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
        SetStatus($"Swapping {from.Name} driverwear onto {to.Name}…", StatusBrush);

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
            {
                SetStatus("The swap produced nothing to add.", ErrorBrush);
                return;
            }

            // A warning means the swap is partial, and a partial swap is not
            // something to put in a template: it would sit there looking
            // finished. So it fails, says why, and the template is left alone.
            //
            // What it must not do is what it used to — build the ERP, report
            // "wrote" in red, and quietly drop it in a scratch folder, which
            // read as a warning about something that had actually happened.
            if (result.Warnings.Count > 0)
            {
                var more = result.Warnings.Count > 1 ? $" (+{result.Warnings.Count - 1} more)" : "";
                SetStatus($"Swap failed: {result.Warnings[0]}{more} " +
                          "Nothing was added to the template.", ErrorBrush);
                return;
            }

            var part = _host.PlaceSwap(result.OutputPath, SwapParts.Driverwear(WearName));
            Log($"Added to the template as {part}");
            SetStatus($"Added {Path.GetFileName(part)} to the template — " +
                      $"{result.OutputResources} resources, {result.DriverCount} driver(s). " +
                      "Save it to keep it.", OkBrush);
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
        foreach (var c in new Control[] { DriverwearBox, TeamBox })
            c.IsEnabled = !busy;

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
        _host.ReportStatus(text,
            ReferenceEquals(brush, ErrorBrush) ? SwapStatus.Problem :
            ReferenceEquals(brush, OkBrush) ? SwapStatus.Done : SwapStatus.Plain);
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
