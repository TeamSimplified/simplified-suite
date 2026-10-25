using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SimplifiedTrackManager.Core;
using SimplifiedTrackManager.Core.Config;
using SimplifiedTrackManager.Core.Track;
using SimplifiedUi;

namespace SimplifiedTrackManager;

/// <summary>
/// The window: a track zip or a Swofty track addon in, a Swofty addon of a
/// track of its own out.
///
/// Picking a zip fills the form from it and from the base track it is built
/// on; everything on the form can then be changed. The checks that stop a
/// build — a code the game already has, initials one of its tracks uses — run
/// as the fields are typed in, so the button is only on when the addon can be
/// built. The game folder is a setting rather than a row, since it is the same
/// answer every time.
/// </summary>
public partial class MainWindow : Window
{
    private readonly UserConfig _config = UserConfig.Load();

    private StockGame? _game;
    private string? _gameError;
    private string? _trackPath;
    private LoadedTrack? _track;
    private bool _running;
    private bool _filling;

    /// <summary>What the zip said about itself when it was picked.</summary>
    private sealed record LoadedTrack(
        string Name, string Code, string Author, string Version, string BaseTrack, string SlotTrack,
        IReadOnlyList<(string Key, string Value)> Ers, IReadOnlyList<TrackPictures.Slot> Slots,
        IReadOnlyList<TrackOverride> TrackOverrides, IReadOnlyList<TrackOverride> Overrides, string Manifest,
        IReadOnlyDictionary<string, string> Names, bool IsAddon, long ErpBytes, int MipFiles);

    // The crowd colours and pictures, kept here because their controls only show them.
    private readonly int?[] _crowd = new int?[3];

    /// <summary>The crowd colours the track came with — the zip's, else the base track's — for resetting to.</summary>
    private readonly int?[] _crowdOriginal = new int?[3];
    private readonly Dictionary<string, string> _pictures = new(StringComparer.Ordinal);

    /// <summary>What the Grand Prix titles were last filled with, so they follow the short name until edited.</summary>
    private (string Y2025, string Y2026, string Previous) _derived = ("", "", "");

    /// <summary>
    /// The words the game's own tracks use, from its language files.
    /// </summary>
    private static readonly string[] Difficulties = ["Very Easy", "Easy", "Medium", "Hard", "Very Hard"];

    /// <summary>
    /// The menus' aero stat, said from the track's downforce level: the game's
    /// tracks read Very Low at very_low, Low at low, High at high and Very High
    /// at very_high, every one of them. Its medium tracks it calls Low or High,
    /// having no word between; this track's own text can say Medium.
    /// </summary>
    private static string AeroFor(string downforce) => downforce switch
    {
        "very_low" => "Very Low",
        "low" => "Low",
        "high" => "High",
        "very_high" => "Very High",
        _ => "Medium",
    };

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);

        VersionLabel.Text = Util.AppVersion.Display;
        ApplyWindowIcon();

        DifficultyBox.ItemsSource = Difficulties;
        DifficultyBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(Field_Changed));
        NationalityBox.SelectionChanged += Nationality_Selected;
        foreach (var box in new[] { TyreHardBox, TyreMediumBox, TyreSoftBox }) box.ItemsSource = TrackTuning.Compounds;
        DownforceBox.ItemsSource = TrackTuning.DownforceLevels;
        PerformanceBox.ItemsSource = TrackTuning.PerformanceTypes;
        TyreWearBox.ItemsSource = TrackTuning.TyreWears;
        ContinentBox.ItemsSource = TrackTuning.Continents;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control && !_running)
            {
                OpenTrack_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };

        LoadGame();
        LoadGameFlags();
        Refresh();

        // A zip handed over on the command line — opened with the app, or
        // dropped on its exe — is picked as if browsed to.
        Loaded += (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Skip(1)
                    .FirstOrDefault(a => IsTrackFile(a) && File.Exists(a)) is { } track)
                PickTrack(track);
        };
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

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            Icon = image;
        }
        catch
        {
            // A missing or unreadable icon is not worth refusing to open over.
        }
    }

    // ── The game ────────────────────────────────────────────────────────────

    /// <summary>
    /// Read the game's database, which says what is taken. Quick enough to do
    /// on the spot: the file is 19 MB.
    /// </summary>
    private void LoadGame()
    {
        _game = null;
        _gameError = null;
        if (string.IsNullOrWhiteSpace(_config.GameDir))
        {
            _gameError = "No game folder — set it in Settings.";
            return;
        }

        try
        {
            _game = StockGame.Load(_config.GameDir);
            NationalityBox.ItemsSource = _game.Nationalities;
            NationalityBox.ItemTemplate ??= NationalityTemplate();
        }
        catch (Exception ex)
        {
            _gameError = $"The game's database couldn't be read: {ex.Message}";
        }
    }

    // ── The track ───────────────────────────────────────────────────────────

    private void OpenTrack_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Pick a track",
            Filter = "Track zip or Swofty addon (*.zip;*.swoftyaddon)|*.zip;*.swoftyaddon|" +
                     "Track zip (*.zip)|*.zip|Swofty addon (*.swoftyaddon)|*.swoftyaddon",
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
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && IsTrackFile(files[0]) ? files[0] : null;

    private static bool IsTrackFile(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".swoftyaddon", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Close the open track: the form goes, its pictures and previews with it,
    /// and nothing is written — what this app writes is the package.
    /// </summary>
    private void CloseTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _track is null) return;
        _previewCancel?.Cancel();
        _previewCancel = null;
        DeletePreviews();
        _previews = new Dictionary<string, string>(StringComparer.Ordinal);
        _previewsLoading = false;
        _track = null;
        _trackPath = null;
        _codeLength = null;
        _tuningWas = null;
        _pictures.Clear();
        DatabaseBox.Text = "";
        DeleteWrapped();
        Refresh();
    }

    /// <summary>
    /// Read the zip's manifest now, so a zip that isn't a track is turned away
    /// here rather than a minute into the build, and fill the form from it.
    /// </summary>
    /// <summary>Where an addon taken out of a zip went, deleted when the track is closed or another opened.</summary>
    private string? _wrappedDir;

    private void DeleteWrapped()
    {
        if (_wrappedDir is null) return;
        try { Directory.Delete(_wrappedDir, recursive: true); } catch { }
        _wrappedDir = null;
    }

    private void PickTrack(string path)
    {
        _config.LastTrackFolder = Path.GetDirectoryName(path);
        _config.Save();

        try
        {
            // An addon handed out inside a zip: an archive in an archive can't
            // be read in place, so it's taken out to a scratch folder first.
            if (TrackZip.WrappedAddon(path) is { } entry)
            {
                _previewCancel?.Cancel();
                DeleteWrapped();
                var dir = Path.Combine(Path.GetTempPath(), "simplified-track-manager", "wrapped", Guid.NewGuid().ToString("N"));
                _wrappedDir = dir;
                path = SimplifiedUi.Busy.Run(this, StatusSpinner, StatusLabel, $"Taking {Path.GetFileName(entry)} out of the zip",
                    _ => TrackZip.ExtractWrapped(path, entry, dir));
            }

            using var zip = TrackZip.Open(path);
            var baseTrack = zip.CopyFromTrack ?? zip.CopySlot
                ?? throw new InvalidDataException("Its manifest names no base track (activation.copy_from_track).");
            _trackPath = path;
            _track = new LoadedTrack(zip.Name, zip.Code, zip.Author, zip.Version, baseTrack, zip.CopySlot ?? baseTrack,
                zip.Ers, TrackPictures.Slots(zip), zip.TrackOverrides, zip.Overrides, zip.ReadText("manifest.json"),
                zip.Names, zip.IsAddon, zip.Erp.Length, zip.Mipmaps.Count);
            _previews = new Dictionary<string, string>(StringComparer.Ordinal);
            _previewsLoading = true;
            Fill(_track);
            LoadPreviews(path, _track.Slots);

            // An addon's other database ops — on other rows, or not sets —
            // can't come over, and are said rather than dropped quietly.
            if (zip.Unported.Count > 0)
                SimplifiedUi.MessageDialog.Show(this,
                    $"{zip.Unported.Count} of the addon's database change(s) aren't on this track's rows, " +
                    "so they won't be in the package:\n\n" +
                    string.Join("\n", zip.Unported.Take(5)) + (zip.Unported.Count > 5 ? "\n…" : ""),
                    "Track", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _trackPath = null;
            _track = null;
            SimplifiedUi.MessageDialog.Show(this,
                $"That file couldn't be read as a track.\n\n{ex.Message}",
                "Track", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Refresh();
    }

    /// <summary>
    /// The form, from the track and its base track. A track zip carries no
    /// text, so the names start from the track's name; an addon's names come
    /// back from its language keys. The numbers, crowd and pit lane come from
    /// the author's overrides where it has them, else from the base track.
    /// </summary>
    private void Fill(LoadedTrack t)
    {
        _filling = true;
        try
        {
            string? Override(string field) => t.TrackOverrides.LastOrDefault(o => o.Path == field)?.Value;
            string? Base(string field) => _game?.TrackValue(t.SlotTrack, field);
            string? Either(string field) => Override(field) ?? Base(field);

            NameBox.Text = t.Name;
            AuthorBox.Text = t.Author;
            VersionBox.Text = TrackSettings.SemVer(t.Version.Length > 0 ? t.Version : "1.0.0");

            var code = t.Code;
            if (_game?.IsStockTrack(code) != false) code = TrackSettings.CodeFrom(t.Name);
            while (_game?.IsStockTrack(code) == true) code += "_track";
            CodeBox.Text = code;
            _suggestedCode = code;

            InitialsBox.Text = GuessInitials(t, Override("title"));

            var firstWord = t.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? t.Name;
            CountryBox.Text = "";
            CityBox.Text = firstWord;
            CircuitBox.Text = t.Name.ToUpperInvariant();
            F2TitleBox.Text = firstWord.ToUpperInvariant();
            GrandPrixBox.Text = "";
            _derived = ("", "", "");
            GrandPrix2025Box.Text = GrandPrix2026Box.Text = GrandPrixPreviousBox.Text = "";

            LengthBox.Text = int.TryParse(Override("length_metres"), out var metres)
                ? (metres / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) : "";
            CornersBox.Text = Override("numberOfCorners") ?? "";
            LapRecordBox.Text = LapRecordHolderBox.Text = "";
            F2LapRecordBox.Text = F2LapRecordHolderBox.Text = "";
            DifficultyBox.Text = "Medium";

            if (t.Names.Count > 0) FillNames(t);

            // A record the track doesn't give reads "-", as the game writes one it hasn't got.
            foreach (var box in new[] { LapRecordBox, LapRecordHolderBox, F2LapRecordBox, F2LapRecordHolderBox })
                if (box.Text.Trim().Length == 0) box.Text = "-";

            Choose(NationalityBox, Either("nationality") ?? "");
            for (var i = 0; i < 3; i++)
                _crowd[i] = _crowdOriginal[i] = int.TryParse(Either($"TrackCrowds_0.crowdColour{i + 1}"), out var colour) ? colour : null;
            PitMirroredBox.IsChecked = string.Equals(Either("pitMirrored"), "True", StringComparison.OrdinalIgnoreCase);

            FillTuning(t);

            _pictures.Clear();
            BuildCrowd();
            BuildPictures(t);
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>
    /// Initials to start from: the ones the author's own keys use, when the
    /// game doesn't have them, else the name's first letters.
    /// </summary>
    private string GuessInitials(LoadedTrack t, string? titleKey)
    {
        bool Free(string s) => s.Length == 3 && s.All(char.IsAsciiLetter) && _game?.TakenInitials.Contains(s) != true;

        if (titleKey is { } key && key.StartsWith("lng_track_", StringComparison.Ordinal) && key.Length >= 13)
        {
            var infix = key.Substring("lng_track_".Length, 3);
            if (Free(infix)) return infix.ToUpperInvariant();
        }

        var letters = new string(t.Name.Where(char.IsAsciiLetter).ToArray()).ToUpperInvariant();
        for (var i = 0; i + 3 <= letters.Length; i++)
            if (Free(letters.Substring(i, 3))) return letters.Substring(i, 3);
        return "";
    }

    /// <summary>
    /// The names, from an addon's language keys: the initials are the infix
    /// of its <c>lng_track_xxx_title</c>, and each box takes its key's text. A
    /// key it lacks leaves the box as the track's name made it.
    /// </summary>
    private void FillNames(LoadedTrack t)
    {
        var infix = t.Names.Keys
            .Select(k => System.Text.RegularExpressions.Regex.Match(k, "^lng_track_([a-z]{3})_title$"))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value;
        if (infix is null) return;

        string? Name(string key) => t.Names.TryGetValue(key, out var v) ? v : null;
        // "-" is what an empty record or holder is written as.
        string? Stat(string key) => Name(key) is { } v && v != "-" ? v : null;
        void Put(TextBox box, string? value) { if (value is not null) box.Text = value; }

        InitialsBox.Text = infix.ToUpperInvariant();
        Put(CountryBox, Name($"lng_track_{infix}_title"));
        Put(CircuitBox, Name($"lng_track_{infix}_subtitle"));
        Put(CityBox, Name($"lng_track_{infix}_city_name"));
        Put(F2TitleBox, Name($"lng_track_{infix}_f2_title"));
        // The short name first: the three long ones follow it as it changes,
        // and then take their own.
        Put(GrandPrixBox, Name($"lng_track_{infix}_gp_title_short"));
        Put(GrandPrix2025Box, Name($"lng_track_{infix}_gp_title"));
        Put(GrandPrix2026Box, Name($"lng_track_{infix}_gp_title_26"));
        Put(GrandPrixPreviousBox, Name($"lng_track_{infix}_gp_title_multiline_previous"));
        Put(LapRecordBox, Stat($"lng_stat_{infix}_lap_record"));
        Put(LapRecordHolderBox, Stat($"lng_track_{infix}_lap_record_holder"));
        Put(F2LapRecordBox, Stat($"lng_stat_{infix}_lap_record_f2"));
        Put(F2LapRecordHolderBox, Stat($"lng_track_{infix}_lap_record_holder_f2"));
        if (Stat($"lng_stat_{infix}_corners") is { } corners && CornersBox.Text.Length == 0) CornersBox.Text = corners;
        if (Stat($"lng_stat_{infix}_difficulty") is { } difficulty) DifficultyBox.Text = difficulty;
    }

    private void GrandPrix_Changed(object sender, TextChangedEventArgs e)
    {
        // The long titles follow the short name while they still read as it
        // last made them; one that has been typed over is left alone.
        var gp = GrandPrixBox.Text.Trim();
        var next = gp.Length == 0 ? ("", "", "")
            : ($"FORMULA 1 {gp} 2025", $"FORMULA 1 {gp} 2026", $"FORMULA 1 {gp} 2024");
        if (GrandPrix2025Box.Text == _derived.Y2025) GrandPrix2025Box.Text = next.Item1;
        if (GrandPrix2026Box.Text == _derived.Y2026) GrandPrix2026Box.Text = next.Item2;
        if (GrandPrixPreviousBox.Text == _derived.Previous) GrandPrixPreviousBox.Text = next.Item3;
        _derived = next;
        Field_Changed(sender, e);
    }

    /// <summary>
    /// The country chosen names the track in the menus too: its title is the
    /// country's name, Turkey for turkey, Great Britain for britain, kept in
    /// step behind the scenes. The no-country one names nothing, and the
    /// track's name is used instead. Not while the form is filled from a
    /// track, which brings its own title.
    /// </summary>
    private void Nationality_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        CountryBox.Text = Nationality() is { Length: > 0 } key && key != StockGame.NoCountry ? GameFlags.Display(key) : "";
        Refresh();
    }

    /// <summary>The country chosen, by the game's key; empty when none is.</summary>
    private string Nationality() => NationalityBox.SelectedItem as string ?? "";

    // ── Race weekend, conditions, character, location ───────────────────────

    /// <summary>The tuning as the track came, which only changes from it are written against.</summary>
    private TrackTuning? _tuningWas;

    /// <summary>
    /// The race weekend, conditions, character and location as the track has
    /// them — its author's values, else its base track's. The menus' Laps stat
    /// starts as the race laps when the track gives none.
    /// </summary>
    private void FillTuning(LoadedTrack t)
    {
        _tuningWas = _game is null ? null
            : TrackTuning.Current(new TrackSource(_game, t.BaseTrack, t.SlotTrack, t.TrackOverrides, t.Overrides));
        var tuning = _tuningWas ?? new TrackTuning();

        F1RaceLapsBox.Text = tuning.F1RaceLaps;
        F1SprintLapsBox.Text = tuning.F1SprintLaps;
        F2FeatureLapsBox.Text = tuning.F2FeatureLaps;
        F2SprintLapsBox.Text = tuning.F2SprintLaps;
        Choose(TyreHardBox, tuning.TyreHard);
        Choose(TyreMediumBox, tuning.TyreMedium);
        Choose(TyreSoftBox, tuning.TyreSoft);
        for (var i = 0; i < SessionBoxes.Length; i++) SessionBoxes[i].Text = tuning.SessionTimes[i];
        F1PitLimitBox.Text = tuning.F1PitLimit;
        F2PitLimitBox.Text = tuning.F2PitLimit;
        NightStartBox.IsChecked = tuning.NightStart;
        WorkOutFuelBox.IsChecked = true;

        for (var i = 0; i < WeatherBoxes.Length; i++) WeatherBoxes[i].Text = tuning.Weather[i];
        TemperatureMinBox.Text = tuning.TemperatureMin;
        TemperatureMaxBox.Text = tuning.TemperatureMax;
        DaylightStartBox.Text = tuning.DaylightStart;
        DaylightEndBox.Text = tuning.DaylightEnd;

        Choose(DownforceBox, tuning.DownforceLevel);
        Choose(PerformanceBox, tuning.PerformanceType);
        Choose(TyreWearBox, tuning.TyreWear);
        AiLapTimeBox.Text = tuning.AiLapTime;
        F1FuelBurnBox.Text = tuning.F1FuelBurn;
        F2FuelBurnBox.Text = tuning.F2FuelBurn;

        LatitudeBox.Text = tuning.Latitude;
        LongitudeBox.Text = tuning.Longitude;
        Choose(ContinentBox, tuning.Continent);
        AirPressureBox.Text = tuning.AirPressure;

        NoteDerived();

    }

    // ── Worked out from other values ────────────────────────────────────────

    /// <summary>
    /// What each worked-out box was last set to. A box follows while it still
    /// reads that — or is empty — and is left alone once something else is
    /// typed, so a value the author set off the formula stays theirs.
    /// </summary>
    private readonly Dictionary<Control, string> _followed = [];

    /// <summary>The values the boxes follow, from the length, the downforce and where the track is.</summary>
    private IEnumerable<(Control Box, string? Value)> Derived()
    {
        var km = TrackTuning.Number(LengthBox.Text);
        string? Laps(double race) => TrackTuning.LapsFor(km, race)?.ToString(CultureInfo.InvariantCulture);
        string? Burn(double? kg) => kg?.ToString("0.00", CultureInfo.InvariantCulture);
        var downforce = DownforceBox.SelectedItem as string ?? "";
        yield return (F1RaceLapsBox, Laps(TrackTuning.F1RaceKm));
        yield return (F1SprintLapsBox, Laps(TrackTuning.F1SprintKm));
        yield return (F2FeatureLapsBox, Laps(TrackTuning.F2FeatureKm));
        yield return (F2SprintLapsBox, Laps(TrackTuning.F2SprintKm));
        yield return (F1FuelBurnBox, Burn(TrackTuning.F1FuelBurnFor(km, downforce)));
        yield return (F2FuelBurnBox, Burn(TrackTuning.F2FuelBurnFor(km, downforce)));
        yield return (ContinentBox, TrackTuning.ContinentFor(TrackTuning.Number(LatitudeBox.Text), TrackTuning.Number(LongitudeBox.Text)));
    }

    /// <summary>What the boxes follow, as a track opens: nothing changes, it is only noted.</summary>
    private void NoteDerived()
    {
        _followed.Clear();
        foreach (var (box, value) in Derived())
            if (value is not null) _followed[box] = value;
    }

    /// <summary>Bring the following boxes up to date after the length, downforce or location changed.</summary>
    private void FollowDerived()
    {
        foreach (var (box, value) in Derived())
        {
            if (value is null) continue;
            var last = _followed.GetValueOrDefault(box);
            switch (box)
            {
                case TextBox text when text.Text.Trim().Length == 0 || text.Text.Trim() == last:
                    text.Text = value;
                    break;
                case ComboBox choice when choice.SelectedItem is null || choice.SelectedItem as string == last:
                    Choose(choice, value);
                    break;
            }
            _followed[box] = value;
        }
    }

    private TextBox[] SessionBoxes => [SessionP1Box, SessionP2Box, SessionP3Box, SessionQualifyingBox, SessionRaceBox];
    private TextBox[] WeatherBoxes => [WeatherClearBox, WeatherCloudyBox, WeatherOvercastBox, WeatherLightRainBox, WeatherHeavyRainBox];

    /// <summary>The form's tuning, as written.</summary>
    private TrackTuning ReadTuning() => new()
    {
        F1RaceLaps = F1RaceLapsBox.Text.Trim(),
        F1SprintLaps = F1SprintLapsBox.Text.Trim(),
        F2FeatureLaps = F2FeatureLapsBox.Text.Trim(),
        F2SprintLaps = F2SprintLapsBox.Text.Trim(),
        TyreHard = TyreHardBox.SelectedItem as string ?? "",
        TyreMedium = TyreMediumBox.SelectedItem as string ?? "",
        TyreSoft = TyreSoftBox.SelectedItem as string ?? "",
        SessionTimes = SessionBoxes.Select(b => b.Text.Trim()).ToArray(),
        F1PitLimit = F1PitLimitBox.Text.Trim(),
        F2PitLimit = F2PitLimitBox.Text.Trim(),
        NightStart = NightStartBox.IsChecked == true,
        WorkOutFuel = WorkOutFuelBox.IsChecked == true,
        Weather = WeatherBoxes.Select(b => b.Text.Trim()).ToArray(),
        TemperatureMin = TemperatureMinBox.Text.Trim(),
        TemperatureMax = TemperatureMaxBox.Text.Trim(),
        DaylightStart = DaylightStartBox.Text.Trim(),
        DaylightEnd = DaylightEndBox.Text.Trim(),
        DownforceLevel = DownforceBox.SelectedItem as string ?? "",
        PerformanceType = PerformanceBox.SelectedItem as string ?? "",
        TyreWear = TyreWearBox.SelectedItem as string ?? "",
        AiLapTime = AiLapTimeBox.Text.Trim(),
        F1FuelBurn = F1FuelBurnBox.Text.Trim(),
        F2FuelBurn = F2FuelBurnBox.Text.Trim(),
        Latitude = LatitudeBox.Text.Trim(),
        Longitude = LongitudeBox.Text.Trim(),
        Continent = ContinentBox.SelectedItem as string ?? "",
        AirPressure = AirPressureBox.Text.Trim(),
    };

    /// <summary>A drop-down's choice; a value the game has that the list lacks is added rather than lost.</summary>
    private static void Choose(ComboBox box, string value)
    {
        var items = (box.ItemsSource as IEnumerable<string> ?? []).ToList();
        if (value.Length > 0 && !items.Contains(value))
        {
            items.Add(value);
            box.ItemsSource = items;
        }
        box.SelectedItem = value.Length > 0 ? value : null;
    }

    private void Choice_Changed(object sender, RoutedEventArgs e)
    {
        if (!_filling && ReferenceEquals(sender, DownforceBox)) FollowDerived();
        if (!_filling) Refresh();
    }

    /// <summary>The weather's total under its boxes, red until it is 100%.</summary>
    private void ShowWeatherTotal()
    {
        var values = WeatherBoxes.Select(b => TrackTuning.Number(b.Text)).ToList();
        if (values.Any(v => v is null)) { Say(WeatherTotal, "Each a number, adding up to 100%", "TextMuted"); return; }
        var total = values.Sum(v => v!.Value);
        Say(WeatherTotal, Math.Abs(total - 100) <= 0.5 ? "Adds up to 100%" : $"Adds up to {total:0.##}%, not 100%",
            Math.Abs(total - 100) <= 0.5 ? "TextMuted" : "Danger");
    }

    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_filling && (ReferenceEquals(sender, LengthBox) || ReferenceEquals(sender, LatitudeBox) || ReferenceEquals(sender, LongitudeBox)))
            FollowDerived();
        if (!_filling) Refresh();
    }

    // ── Crowd colours ───────────────────────────────────────────────────────

    /// <summary>
    /// The three crowd colours as cards, the way the Grid Editor shows a team's
    /// colours: a swatch to click and change, the name, the hex, and a reset
    /// back to what the track came with — the zip's colour, else the base
    /// track's.
    /// </summary>
    private void BuildCrowd()
    {
        CrowdPanel.Children.Clear();
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            var colour = _crowd[index];
            var original = _crowdOriginal[index];
            var modified = colour != original;

            var swatch = new Button
            {
                Width = 56,
                Height = 56,
                Padding = new Thickness(0),
                Template = Simplified.Shared.FlatButton.Template(),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Click to change",
            };
            if (colour is { } c) swatch.Background = new SolidColorBrush(ToColor(c));
            else swatch.SetResourceReference(BackgroundProperty, "SurfaceSunken");
            swatch.SetResourceReference(BorderBrushProperty, "BorderStrong");
            swatch.Click += (_, _) =>
            {
                var current = _crowd[index] is { } now ? Hex(now) : "#FFFFFF";
                if (Simplified.Shared.ColourDialog.Show(this, $"Crowd colour {index + 1}", current) is not { } picked) return;
                var rgb = int.Parse(picked.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                _crowd[index] = unchecked((int)0xFF000000 | rgb);
                BuildCrowd();
            };

            var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = $"Colour {index + 1}", FontWeight = FontWeights.SemiBold }
                .Themed(TextBlock.ForegroundProperty, "TextPrimary"));
            text.Children.Add(new TextBlock
            {
                Text = colour is { } h ? Hex(h) : "none",
                FontFamily = new FontFamily("Consolas, Cascadia Mono"),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
            }.Themed(TextBlock.ForegroundProperty, modified ? "Accent" : "TextMuted"));
            text.Children.Add(new TextBlock
            {
                Text = modified ? "Changed" : "As the track came",
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
            }.Themed(TextBlock.ForegroundProperty, "TextMuted"));

            var reset = new Button
            {
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = original is { } o ? $"Put this back to {Hex(o)}" : "Put this back as the track came",
                Style = FindResource("RestoreButton") as Style,
                IsEnabled = modified,
            }.Themed(ForegroundProperty, modified ? "Accent" : "TextDisabled");
            reset.Click += (_, _) =>
            {
                _crowd[index] = _crowdOriginal[index];
                BuildCrowd();
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 1);
            Grid.SetColumn(reset, 2);
            grid.Children.Add(swatch);
            grid.Children.Add(text);
            grid.Children.Add(reset);

            var card = new Border { Padding = new Thickness(12), Margin = new Thickness(0, 0, 12, 12), Child = grid };
            card.SetResourceReference(StyleProperty, "Panel");
            CrowdPanel.Children.Add(card);
        }
    }

    private static Color ToColor(int argb) =>
        Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static string Hex(int argb) => $"#{argb & 0xFFFFFF:X6}";

    // ── Pictures ────────────────────────────────────────────────────────────

    private void BuildPictures(LoadedTrack t)
    {
        // The flags have a section of their own, with the button that takes
        // them from the game; the rest of the pictures go under Pictures.
        FlagsPanel.Children.Clear();
        foreach (var slot in t.Slots.Where(s => TrackPictures.IsFlag(s.Key)))
            FlagsPanel.Children.Add(PictureCard(slot));

        // The rest in their groups — Menus, Map, Map X mode — each a title
        // over a wrapping grid of cards.
        PicturesPanel.Children.Clear();
        foreach (var group in t.Slots.Where(s => !TrackPictures.IsFlag(s.Key)).GroupBy(s => s.Group))
        {
            PicturesPanel.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Style = (Style)FindResource("GroupTitle") });
            var cards = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -12, 10) };
            foreach (var slot in group) cards.Children.Add(PictureCard(slot));
            PicturesPanel.Children.Add(cards);
        }
    }

    /// <summary>
    /// One picture as a card: its name and the size the menus show it at, a
    /// preview of what will go in, where that comes from, and Browse and Clear.
    /// </summary>
    private FrameworkElement PictureCard(TrackPictures.Slot slot)
    {
        const double PreviewHeight = 112;

        var title = new TextBlock { Text = slot.Label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }
            .Themed(TextBlock.ForegroundProperty, "TextPrimary");
        var size = new TextBlock
        {
            Text = TrackPictures.MenuSize(slot.Key) is { } s ? s : "Track's own size",
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 8),
        }.Themed(TextBlock.ForegroundProperty, "TextMuted");

        // The picture as it will go in: the chosen PNG, or the zip's own once
        // the previews are out. On a sunken tile so a picture with
        // transparency still shows its edges; hover shows it larger.
        var thumb = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
        var large = new Image { Stretch = Stretch.Uniform, MaxWidth = 480, MaxHeight = 320 };
        var frame = new Border
        {
            Height = PreviewHeight,
            Padding = new Thickness(4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = thumb,
        }.Themed(Border.BackgroundProperty, "SurfaceSunken").Themed(Border.BorderBrushProperty, "BorderSoft");
        ToolTipService.SetInitialShowDelay(frame, 150);

        var source = new TextBlock { FontSize = 11, Margin = new Thickness(0, 8, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis };
        var browse = new Button { Content = "Browse", Padding = new Thickness(12, 4, 12, 4) };
        var clear = new Button { Content = "Clear", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };

        void Show()
        {
            string? picture;
            if (_pictures.TryGetValue(slot.Key, out var png))
            {
                source.Text = Path.GetFileName(png);
                source.ToolTip = png;
                source.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                picture = png;
                clear.IsEnabled = true;
            }
            else
            {
                var own = _previews.TryGetValue(slot.Key, out var p) ? p : null;
                source.Text = own is not null || !_previewsLoading ? "The track's own" : "The track's own (loading…)";
                source.ToolTip = null;
                source.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                picture = own;
                clear.IsEnabled = false;
            }

            thumb.Source = picture is null ? null : Thumbnail(picture, 224);
            large.Source = picture is null ? null : Thumbnail(picture, 640);
            frame.ToolTip = picture is null ? null : new ToolTip { Content = large };

            // A flag is shown at its actual size, as the menus show it — a
            // chosen PNG at the size it will be converted to. Any other small
            // picture would smear if stretched to fill the tile, so it is shown
            // at most three times its size, with its pixels kept sharp.
            var (w, h) = thumb.Source is BitmapSource b ? (b.PixelWidth, b.PixelHeight) : (0, 0);
            if (TrackPictures.IsFlag(slot.Key) && TrackPictures.MenuSize(slot.Key) is { } menu
                && menu.Split('×') is [var mw, var mh] && double.TryParse(mw, out var fw) && double.TryParse(mh, out var fh))
            {
                thumb.Stretch = Stretch.Fill;
                thumb.Width = fw;
                thumb.Height = fh;
                RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
            }
            else
            {
                var small = h is > 0 and <= 64;
                thumb.MaxWidth = small ? w * 3 : double.PositiveInfinity;
                thumb.MaxHeight = small ? h * 3 : double.PositiveInfinity;
                RenderOptions.SetBitmapScalingMode(thumb, small ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            }
        }

        browse.Click += (_, _) =>
        {
            var picker = new OpenFileDialog
            {
                Title = $"Pick a PNG for {slot.Label.ToLowerInvariant()}",
                Filter = "PNG image (*.png)|*.png",
                InitialDirectory = ExistingDir(_config.LastPictureFolder),
            };
            if (picker.ShowDialog(this) != true) return;
            _config.LastPictureFolder = Path.GetDirectoryName(picker.FileName);
            _config.Save();
            _pictures[slot.Key] = picker.FileName;
            Show();
        };
        clear.Click += (_, _) =>
        {
            _pictures.Remove(slot.Key);
            Show();
        };

        Show();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(browse);
        buttons.Children.Add(clear);

        var body = new StackPanel();
        body.Children.Add(title);
        body.Children.Add(size);
        body.Children.Add(frame);
        body.Children.Add(source);
        body.Children.Add(buttons);

        return new Border
        {
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 12, 12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = body,
        }.Themed(Border.BackgroundProperty, "SurfaceAlt").Themed(Border.BorderBrushProperty, "BorderSoft");
    }

    // ── The game's flags ────────────────────────────────────────────────────

    /// <summary>The game's countries' flags, taken out once in the background when the game is read.</summary>
    private Task<GameFlags>? _gameFlags;
    private string? _gameFlagsDir;

    /// <summary>
    /// Start taking the game's flags out, so the button has them by the time
    /// it is pressed: some 350 pictures, ten seconds or so.
    /// </summary>
    private void LoadGameFlags()
    {
        if (_game is null || string.IsNullOrWhiteSpace(_config.GameDir)) return;
        var game = _game;
        var gameDir = _config.GameDir;
        var dir = Path.Combine(Path.GetTempPath(), "simplified-track-manager", "flags", Guid.NewGuid().ToString("N"));
        DeleteGameFlags();
        _gameFlagsDir = dir;
        _gameFlags = Task.Run(() => GameFlags.Extract(gameDir, game, new EgoEngineLibrary.Texconv.TexconvRunner(), dir));
    }

    private void DeleteGameFlags()
    {
        if (_gameFlagsDir is null) return;
        try { Directory.Delete(_gameFlagsDir, recursive: true); } catch { }
        _gameFlagsDir = null;
    }

    private void InGameFlags_Click(object sender, RoutedEventArgs e)
    {
        if (_track is not { } t) return;
        if (_gameFlags is null) LoadGameFlags();
        if (_gameFlags is not { } pending)
        {
            SimplifiedUi.MessageDialog.Show(this, _gameError ?? "The game's flags need the game folder — set it in Settings.",
                "Replace with in game", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        GameFlags flags;
        try
        {
            // Started when the app opened, so usually done by now; if not, the
            // wait is shown along the bottom like any other.
            flags = pending.IsCompleted
                ? pending.Result
                : SimplifiedUi.Busy.Run(this, StatusSpinner, StatusLabel, "Taking the game's flags out",
                                        _ => pending.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            _gameFlags = null;
            SimplifiedUi.MessageDialog.Show(this, $"The game's flags couldn't be read.\n\n{ex.InnerException?.Message ?? ex.Message}",
                "Replace with in game", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The chequered flag first, then the countries.
        var choices = new List<GameFlags.Country>(flags.Countries.Count + 1);
        if (Chequered() is { } chequered) choices.Add(chequered);
        choices.AddRange(flags.Countries);
        if (CountryDialog.Show(this, choices, Nationality()) is not { } country) return;

        // The round flag is the game's own where it has one, else the one the
        // app ships from flag-icons, else a cut of the large flag; the
        // rectangle comes from the 128×80 flag, which the build sizes to
        // 100×64; the small one is the game's 40×28 as it is.
        var round = country.Round;
        if (round is null)
        {
            round = Path.Combine(_gameFlagsDir!, "cut", country.Key + ".png");
            FlagCrop.Save(FlagCrop.Round(country.Large, 28), round);
        }
        _pictures["flag_circle"] = round;
        _pictures["flag_rectangle"] = country.Banner;
        _pictures["flag_rectangle_small"] = country.Small;

        // The chequered flag is no country's, so it means the game's own no-country nationality.
        if (Nationality().Length == 0)
            Choose(NationalityBox, country.Key == ChequeredKey ? StockGame.NoCountry : country.Key);
        BuildPictures(t);
    }

    /// <summary>
    /// The nationality list as the game's keys, which is what the box holds
    /// and the track gets, with the no-country one said in words in the list.
    /// </summary>
    private static DataTemplate NationalityTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new NationalityLabel() });
        return new DataTemplate { VisualTree = text };
    }

    private sealed class NationalityLabel : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is string key ? key == StockGame.NoCountry ? "Default (no country)" : GameFlags.Display(key) : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Not a country, so not a nationality either.</summary>
    private const string ChequeredKey = "chequered";

    /// <summary>
    /// A chequered flag, drawn at the game's flag sizes — it has none of its
    /// own — beside the game's flags, for a track that belongs to no country.
    /// </summary>
    private GameFlags.Country? Chequered()
    {
        if (_gameFlagsDir is null) return null;
        var dir = Path.Combine(_gameFlagsDir, ChequeredKey);
        string Drawn(string name, int width, int height, int columns, bool round = false)
        {
            var path = Path.Combine(dir, name + ".png");
            if (!File.Exists(path)) FlagCrop.Save(FlagCrop.Chequered(width, height, columns, round), path);
            return path;
        }
        try
        {
            return new GameFlags.Country(ChequeredKey, "Chequered flag",
                Small: Drawn("small", 40, 28, 10),
                Banner: Drawn("banner", 128, 80, 16),
                Large: Drawn("large", 268, 152, 16),
                Round: Drawn("round", 28, 28, 7, round: true),
                RoundFromGame: false);
        }
        catch (Exception)
        {
            return null; // no chequered flag is no reason to lose the countries
        }
    }

    // ── Previews of the zip's own pictures ──────────────────────────────────

    /// <summary>The zip's own pictures as PNGs, by slot key, once they are out.</summary>
    private Dictionary<string, string> _previews = new(StringComparer.Ordinal);

    /// <summary>
    /// The length the code has to be — the track's own code's — when its
    /// meshes and materials name their files with it; found by the preview
    /// pass, which reads the archive anyway.
    /// </summary>
    private int? _codeLength;

    /// <summary>The code the form last made up, so it knows whether the box still holds it.</summary>
    private string? _suggestedCode;
    private bool _previewsLoading;
    private CancellationTokenSource? _previewCancel;
    private string? _previewDir;

    /// <summary>
    /// Take the zip's pictures out in the background. It means writing the
    /// track's archive out and reading it whole, which takes a few seconds on
    /// a large track, so the form stays usable meanwhile and the rows fill in
    /// when it's done. Picking another zip drops the one in progress.
    /// </summary>
    private async void LoadPreviews(string zipPath, IReadOnlyList<TrackPictures.Slot> slots)
    {
        _previewCancel?.Cancel();
        DeletePreviews();
        var cancel = new CancellationTokenSource();
        _previewCancel = cancel;
        var dir = Path.Combine(Path.GetTempPath(), "simplified-track-manager", "previews", Guid.NewGuid().ToString("N"));
        _previewDir = dir;
        _previews = new Dictionary<string, string>(StringComparer.Ordinal);
        _previewsLoading = true;
        _codeLength = null;
        int? codeLength = null;

        try
        {
            var previews = await Task.Run(() =>
            {
                using var zip = TrackZip.Open(zipPath);
                var oldCode = zip.Code;
                return TrackPictures.ExtractPreviews(zip, slots, new EgoEngineLibrary.Texconv.TexconvRunner(), dir, cancel.Token,
                    erp => codeLength = TrackRecode.EmbedsCode(erp, oldCode) ? oldCode.Length : null);
            }, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            _previews = previews;
            if (codeLength is { } length && _track is { } loaded) NeedCodeLength(loaded, length);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cancel.IsCancellationRequested) return;
            SimplifiedUi.MessageDialog.Show(this, $"The track's pictures couldn't be previewed.\n\n{ex.Message}",
                "Pictures", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (ReferenceEquals(_previewCancel, cancel)) _previewsLoading = false;
        }

        if (ReferenceEquals(_previewCancel, cancel) && _track is { } t) BuildPictures(t);
    }

    private void DeletePreviews()
    {
        if (_previewDir is null) return;
        try { Directory.Delete(_previewDir, recursive: true); } catch { }
        _previewDir = null;
    }

    /// <summary>A copy of a PNG at most <paramref name="height"/> high, read whole so the file isn't held open.</summary>
    private static BitmapImage? Thumbnail(string path, int height)
    {
        try
        {
            // Never decoded larger than it is: a small picture keeps its own
            // pixels, and the card decides how far to show it enlarged.
            var own = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None)
                .Frames[0].PixelHeight;
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelHeight = Math.Min(height, own);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    // ── Build ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The form, as settings. The circuit, F2 title and Grand Prix titles are
    /// capitals in the game's own tracks; their boxes type in capitals, and a
    /// value pasted or filled some other way is raised here too.
    /// </summary>
    private TrackSettings Settings()
    {
        var s = new TrackSettings
        {
            Code = CodeBox.Text.Trim(),
            Name = NameBox.Text.Trim(),
            Author = AuthorBox.Text.Trim(),
            // Numbers as typed, made into the semver Swofty takes: 1.00.0 and
            // 2.72 become 1.0.0 and 2.72.0. Anything else is left for the
            // check to name.
            Version = System.Text.RegularExpressions.Regex.IsMatch(VersionBox.Text.Trim(), @"^\d+(\.\d+){0,2}$")
                ? TrackSettings.SemVer(VersionBox.Text.Trim())
                : VersionBox.Text.Trim(),
            Initials = InitialsBox.Text.Trim(),
            // The title the country names, else the country's own name, else
            // (left empty) the track's name.
            Country = CountryBox.Text.Trim().Length > 0 ? CountryBox.Text.Trim()
                : Nationality() is { Length: > 0 } nation && nation != StockGame.NoCountry ? GameFlags.Display(nation) : "",
            Circuit = CircuitBox.Text.Trim().ToUpperInvariant(),
            City = CityBox.Text.Trim(),
            GrandPrix = GrandPrixBox.Text.Trim().ToUpperInvariant(),
            GrandPrix2025 = GrandPrix2025Box.Text.Trim().ToUpperInvariant(),
            GrandPrix2026 = GrandPrix2026Box.Text.Trim().ToUpperInvariant(),
            GrandPrixPrevious = GrandPrixPreviousBox.Text.Trim().ToUpperInvariant(),
            F2Title = F2TitleBox.Text.Trim().ToUpperInvariant(),
            LengthKm = LengthBox.Text.Trim(),
            Corners = CornersBox.Text.Trim(),
            // The laps the menus show are the race weekend's.
            Laps = F1RaceLapsBox.Text.Trim(),
            LapRecord = LapRecordBox.Text.Trim(),
            LapRecordHolder = LapRecordHolderBox.Text.Trim(),
            F2LapRecord = F2LapRecordBox.Text.Trim(),
            F2LapRecordHolder = F2LapRecordHolderBox.Text.Trim(),
            Difficulty = DifficultyBox.Text.Trim(),
            // Not asked: the aero stat follows the downforce level.
            AeroBias = AeroFor(DownforceBox.SelectedItem as string ?? ""),
            Nationality = Nationality(),
            PitMirrored = PitMirroredBox.IsChecked == true,
            // Not on the form: the track's ERS goes over as the zip gave it.
            Ers = _track?.Ers.ToList() ?? [],
            CodeLength = _track is { } t && t.Code != CodeBox.Text.Trim() ? _codeLength : null,
        };
        for (var i = 0; i < 3; i++) s.CrowdColours[i] = _crowd[i];
        foreach (var (key, png) in _pictures) s.Pictures[key] = png;
        if (_tuningWas is not null)
        {
            s.Tuning = ReadTuning();
            s.TuningWas = _tuningWas;
        }
        return s;
    }

    /// <summary>
    /// The track's data names its files with its code, so a new code has to
    /// be as long. A code the form made up is made up again at that length;
    /// one that was typed is left, and the line under it says why it won't do.
    /// </summary>
    private void NeedCodeLength(LoadedTrack t, int length)
    {
        _codeLength = length;
        var code = CodeBox.Text.Trim();
        if (code.Length != length && code == _suggestedCode)
        {
            var next = TrackSettings.CodeFrom(t.Name, length);
            for (var i = 1; _game?.IsStockTrack(next) == true && i < 10; i++)
                next = next[..(length - 1)] + i.ToString(CultureInfo.InvariantCulture);
            _suggestedCode = next;
            CodeBox.Text = next;
        }
        Refresh();
    }

    private void Package_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _trackPath is null || _track is null || _game is null) return;

        var settings = Settings();
        if (settings.Problems(_game) is { Count: > 0 } problems)
        {
            SimplifiedUi.MessageDialog.Show(this, string.Join("\n", problems),
                "Package", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var save = new SaveFileDialog
        {
            Title = "Save the track addon",
            Filter = "Swofty addon (*.swoftyaddon)|*.swoftyaddon",
            FileName = SafeFileName($"{settings.Name} - {settings.Version}") + ".swoftyaddon",
            InitialDirectory = ExistingDir(_config.LastPackageFolder),
        };
        if (save.ShowDialog(this) != true) return;

        _config.LastPackageFolder = Path.GetDirectoryName(save.FileName);
        _config.Save();

        var game = _game;
        var zipPath = _trackPath;
        _running = true;
        try
        {
            // Along the bottom, as Studio packages: the steps on the line, and
            // the rest of the window switched off until it's written.
            var result = SimplifiedUi.Busy.Run(this, StatusSpinner, StatusLabel, $"Packaging {settings.Name}",
                say => AddonBuilder.Build(zipPath, settings, game, save.FileName, say: say));

            var lines = new List<string>
            {
                $"{settings.Name} is packaged as track {result.Code} to\n{result.OutputPath}",
                $"{result.SetOps} database changes, {result.LanguageKeys} names and " +
                $"{result.Pictures} new picture(s). Install it on Swofty's Addons page.",
            };
            lines.AddRange(result.Warnings);
            SimplifiedUi.MessageDialog.Show(this, string.Join("\n\n", lines),
                "Packaged", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"The track couldn't be packaged.\n\n{ex.Message}",
                "Package", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
        }
    }

    // ── State ───────────────────────────────────────────────────────────────

    private void Refresh()
    {
        TrackForm.Visibility = _track is null ? Visibility.Collapsed : Visibility.Visible;
        CloseTrackButton.IsEnabled = _track is not null;
        TrackNameLabel.Text = _track is null ? "No track open"
            : NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : _track.Name;
        TrackNameLabel.ToolTip = _trackPath;
        TrackNameLabel.FontWeight = _track is null ? FontWeights.Normal : FontWeights.SemiBold;
        TrackNameLabel.SetResourceReference(TextBlock.ForegroundProperty, _track is null ? "TextDisabled" : "TextPrimary");

        if (_track is { } t)
        {

            // One line: who made it, its code (and that it needs a new one),
            // what it is built on, and how big it is.
            var by = t.Author.Length > 0 ? $" by {t.Author}" : "";
            var code = _game?.IsStockTrack(t.Code) == true ? $"code {t.Code} (the game's, so it gets its own)" : $"code {t.Code}";
            TrackHint.Text = $"{t.Name} {t.Version}{by}  ·  {code}  ·  built on {t.BaseTrack}  ·  " +
                             $"{t.ErpBytes / 1048576.0:N0} MB, {t.MipFiles} mip files";
            Validate();
            ShowWeatherTotal();
            ShowDatabase(t);
        }
        else
        {
            TrackHint.Text = "Open a track zip, as the EgoGameDatabasesEditor takes it, or a Swofty track addon — or drop one on the window.";
        }

        // Without the game nothing can be checked or packaged, which the line
        // under the zip says, in red, in place of what it would say.
        if (_gameError is not null) Say(TrackHint, _gameError, "Danger");
        else TrackHint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var problems = _game is null || _track is null ? null : Settings().Problems(_game);
        PackageButton.IsEnabled = _game is not null && _track is not null && problems is { Count: 0 };
        PackageButton.ToolTip = problems is { Count: > 0 }
            ? string.Join("\n", problems)
            : "Package the track as a Swofty addon, with its own code, names and pictures";
        InGameFlagsButton.IsEnabled = _game is not null;
    }

    /// <summary>
    /// The Database section: what the package carries over that the form
    /// doesn't show, worked out the way the package works it out, so it
    /// follows the code and the fields the form takes over.
    /// </summary>
    private void ShowDatabase(LoadedTrack t)
    {
        string text;
        if (_game is null) text = "";
        else
        {
            try
            {
                text = AddonBuilder.ToJson(AddonBuilder.Unshown(t.Manifest, t.TrackOverrides, t.Overrides, t.Ers,
                                                                 t.BaseTrack, t.SlotTrack, Settings(), _game));
            }
            catch (Exception ex)
            {
                text = $"// Couldn't be worked out: {ex.Message}";
            }
        }
        if (DatabaseBox.Text != text) DatabaseBox.Text = text;
    }

    /// <summary>The code and initials, said right under them as they are typed.</summary>
    /// <summary>
    /// The code and initials, checked as they are typed. Their one line under
    /// the box shows what's wrong when something is, and Spa's example
    /// otherwise, so the row stays the height of every other; the longer
    /// explanation lives in the box's tooltip.
    /// </summary>
    private void Validate()
    {
        var code = CodeBox.Text.Trim();
        CodeBox.ToolTip = $"The track's id in the game. Its addon id is track.{code}.";
        if (_game?.IsStockTrack(code) == true)
            Say(CodeHint, $"The game already has a track called {code}", "Danger");
        else if (code.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(code, "^[a-z][a-z0-9_-]{1,39}$"))
            Say(CodeHint, "Lower-case letters, digits, _ or -, starting with a letter", "Danger");
        else if (_codeLength is { } length && _track is { } t && code != t.Code && code.Length != length)
            Say(CodeHint, $"Has to be {length} characters, as {t.Code} is: its meshes name their files with it", "Danger");
        else
            Say(CodeHint, "Spa: spa_francorchamps", "TextMuted");

        var initials = InitialsBox.Text.Trim();
        InitialsBox.ToolTip = initials.Length == 3
            ? $"Shown as the track's acronym; its names are lng_track_{initials.ToLowerInvariant()}_…"
            : "Three letters, shown as the track's acronym and used in its language keys";
        if (initials.Length > 0 && (initials.Length < 3 || !initials.All(char.IsAsciiLetter)))
            Say(InitialsHint, "Three letters", "Danger");
        else if (initials.Length == 3 && _game?.TakenInitials.Contains(initials) == true)
            Say(InitialsHint, $"{initials} belongs to one of the game's own tracks", "Danger");
        else
            Say(InitialsHint, "Spa: BEL", "TextMuted");
    }

    private void Say(TextBlock hint, string text, string brush)
    {
        hint.Text = text;
        hint.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    private static string ExistingDir(string? dir) => Directory.Exists(dir ?? "") ? dir! : "";

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().Trim('.');
        return cleaned.Length == 0 ? "track" : cleaned;
    }

    // ── Window ──────────────────────────────────────────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!ManagerSettings.Show(this, _config)) return;
        _config.Save();
        LoadGame();
        _gameFlags = null;
        LoadGameFlags();
        if (_track is not null) Fill(_track);
        Refresh();
    }

    /// <summary>
    /// Not while an addon is being written: the staging folder would be left
    /// half full and the file half written, with nothing to say which.
    /// </summary>
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            return;
        }

        // The previews, the game's flags and an addon taken out of a zip are
        // scratch files; a job still taking previews out stops.
        _previewCancel?.Cancel();
        DeletePreviews();
        DeleteGameFlags();
        DeleteWrapped();
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();
}
