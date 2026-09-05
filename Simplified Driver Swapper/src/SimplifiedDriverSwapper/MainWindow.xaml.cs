using System.IO;
// CreateEntryFromFile is an extension method, so this has to be a real using —
// a fully-qualified ZipArchive won't resolve it.
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SimplifiedDriverSwapper.Core.Config;
using SimplifiedDriverSwapper.Core.Database;
using SimplifiedDriverSwapper.Core.Drivers;

namespace SimplifiedDriverSwapper;

public partial class MainWindow : Window
{
    /// <summary>
    /// A palette brush, looked up each time rather than cached in a static.
    ///
    /// Two reasons it can't be a <c>static readonly</c>: the field would be
    /// resolved once at type init, which can happen before the dictionary is
    /// merged, and it would then keep the old colour through a theme change.
    /// The literal is the fallback for both cases.
    /// </summary>
    private static Brush Themed(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(r, g, b));

    private static Brush ErrorBrush => Themed("Danger", 0xB0, 0x2A, 0x2A);
    private static Brush HintBrush => Themed("TextDisabled", 0x8A, 0x92, 0x9C);
    private static Brush MutedBrush => Themed("TextMuted", 0x66, 0x66, 0x66);
    private static Brush OkBrush => Themed("Accent", 0x2E, 0x7D, 0x4F);

    private readonly UserConfig _config = UserConfig.Load();
    private readonly KitOptions _kit = new();

    private string? _path;
    private GameDatabase? _db;

    /// <summary>Which grid is being edited. Changing it rebuilds the seat plan.</summary>
    private Season _season = Season.All[0];
    private DriverCatalogue? _catalogue;

    /// <summary>
    /// The backup, read alongside. It names designs and, by comparison, is the
    /// only way to tell which seats an earlier session already changed.
    /// </summary>
    private DatabaseSnapshot? _reference;
    private SeatPlan? _seats;

    private List<DriverAttributes.DriverRow> _attributeRows = [];

    /// <summary>Driver record index → field → new value, before it's written.</summary>
    private readonly Dictionary<int, Dictionary<string, string>> _pendingAttributes = [];

    /// <summary>The editors on the Drivers tab, built once from the field list.</summary>
    private readonly Dictionary<string, ComboBox> _attributeEditors = [];

    /// <summary>
    /// The helmet picker's contents: each design by name, and the record now
    /// carrying it. Names come from the backup, so they stay meaningful in a
    /// database that has already been saved once.
    /// </summary>
    private List<(string Name, int Record)> _helmetChoices = [];

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);

        VersionLabel.Text = Util.AppVersion.Display;
        ApplyWindowIcon();
        BuildAttributeEditors();

        ShowSeason(_season);

        ApplyLogVisibility(TeamConfig.Load().ShowLog);
        ApplyAdvancedSwapping(_config.EnableAdvancedSwapping);

        RefreshEverything();
    }

    /// <summary>
    /// The window icon, from the .ico shipped beside the exe.
    ///
    /// The embedded application icon gives the taskbar its picture but not the
    /// Alt-Tab entry, which reads the window's own Icon and otherwise falls
    /// back to a blank page.
    /// </summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var path = Path.Combine(Core.AppPaths.Root, "icon.ico");
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

    // ── Opening ─────────────────────────────────────────────────────────────

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;

        var dlg = new OpenFileDialog
        {
            Title = "Choose a gamedatabases.bin",
            Filter = "Game databases (*.bin)|*.bin|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = FirstExistingDir(_config.LastFileFolder, _config.GameDir) ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;

        _config.LastFileFolder = Path.GetDirectoryName(dlg.FileName);
        _config.Save();
        Load(dlg.FileName);
    }

    /// <summary>
    /// Start again from the game's own database. When a backup exists it is put
    /// back first, so this returns to the file as it was before the app ever
    /// touched it rather than to whatever the last save left behind.
    /// </summary>
    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;

        var path = GameDatabase.FindInGameDirectory(_config.GameDir);
        if (path is null)
        {
            SetStatus(
                string.IsNullOrWhiteSpace(_config.GameDir)
                    ? "Set your game folder in Settings first, then New can find the database."
                    : "No gamedatabases.bin under your game folder — check the folder in Settings.",
                ErrorBrush);
            return;
        }

        var backup = GameDatabase.BackupPathFor(path);
        if (File.Exists(backup))
        {
            // Overwrites the working database, so say exactly what goes.
            var answer = SimplifiedUi.MessageDialog.Show(this,
                $"This replaces {Path.GetFileName(path)} with {Path.GetFileName(backup)}, " +
                "discarding every change saved to it.\n\nContinue?",
                "Start from the backup", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            try
            {
                GameDatabase.RestoreFromBackup(path);
            }
            catch (Exception ex)
            {
                SetStatus($"Couldn't restore the backup: {ex.Message}", ErrorBrush);
                SimplifiedUi.MessageDialog.Show(this, ex.Message, "Restore failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Load(path);
            Log($"restored {Path.GetFileName(path)} from {Path.GetFileName(backup)}");
            SetStatus($"Restored {Path.GetFileName(path)} from its backup.", OkBrush);
            return;
        }

        // No backup yet — opening takes one, so this is already a clean start.
        Load(path);
    }

    /// <summary>
    /// True while the chips are being set from code, so putting them back after
    /// a cancelled switch doesn't read as a second switch.
    /// </summary>
    private bool _settingSeasonChip;

    private static Season SeasonFor(object chip, FrameworkElement chip2025) =>
        ReferenceEquals(chip, chip2025) ? Season.F2025 : Season.F2026;

    /// <summary>Point the chips at a season without asking anything.</summary>
    private void ShowSeason(Season season)
    {
        _season = season;
        _settingSeasonChip = true;
        Season2026Chip.IsChecked = ReferenceEquals(season, Season.F2026);
        Season2025Chip.IsChecked = ReferenceEquals(season, Season.F2025);
        _settingSeasonChip = false;
    }

    /// <summary>
    /// Switch which grid is being edited. The seat plan is tied to one entrant
    /// list, so it's rebuilt — and any staged seat changes belong to the old
    /// grid, so they're confirmed away rather than silently carried over.
    /// </summary>
    private void Season_Checked(object sender, RoutedEventArgs e)
    {
        if (_settingSeasonChip) return;

        var chosen = SeasonFor(sender, Season2025Chip);
        if (ReferenceEquals(chosen, _season)) return;

        if (_seats?.HasChanges == true)
        {
            var answer = SimplifiedUi.MessageDialog.Show(this,
                $"Switching to the {chosen.Label} discards the seat changes staged for the {_season.Label}.\n\nContinue?",
                "Change grid", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK)
            {
                ShowSeason(_season);   // put the chips back
                return;
            }
        }

        ShowSeason(chosen);

        // Driver attribute edits aren't tied to a grid, so they survive.
        if (_db is not null && _catalogue is not null)
            _seats = new SeatPlan(_db, _catalogue, _season, _reference);

        RefreshEverything();
        Log($"editing the {_season.Label} (entrant list {_season.EntrantList})");
        SetStatus($"Now editing the {_season.Label}.", MutedBrush);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        if (!ConfirmDiscardChanges()) return;
        Load(paths[0]);
    }

    private void Load(string path)
    {
        try
        {
            _db = GameDatabase.Load(path);
        }
        catch (Exception ex)
        {
            _db = null; _catalogue = null; _seats = null; _path = null;
            RefreshEverything();
            SetStatus($"Couldn't open {Path.GetFileName(path)}: {ex.Message}", ErrorBrush);
            return;
        }

        // Take the untouched original aside before anything can be written over
        // it. Only ever done once — see EnsureBackup.
        string? backup = null;
        try { backup = GameDatabase.EnsureBackup(path); }
        catch (Exception ex) { Log($"couldn't write a backup: {ex.Message}"); }
        if (backup is not null) Log($"backed up to {Path.GetFileName(backup)}");

        _path = path;

        // The backup still names every design; this file may not, once saved.
        GameDatabase? reference = null;
        var backupPath = GameDatabase.BackupPathFor(path);
        if (File.Exists(backupPath))
        {
            try { reference = GameDatabase.Load(backupPath); }
            catch (Exception ex) { Log($"couldn't read {Path.GetFileName(backupPath)} for helmet names: {ex.Message}"); }
        }

        _catalogue = DriverCatalogue.Read(_db, reference);
        _reference = reference is null ? null : new DatabaseSnapshot(reference, DriverCatalogue.Read(reference));
        _seats = new SeatPlan(_db, _catalogue, _season, _reference);
        _attributeRows = DriverAttributes.Read(_db);
        _pendingAttributes.Clear();

        PopulatePickers();
        LogBox.Clear();
        RefreshEverything();

        SetStatus(
            $"Loaded {_db.Cipher.Game} — {_db.Records.Count:N0} records, {_catalogue.Drivers.Count} drivers, " +
            $"{_catalogue.Helmets.Count} helmets. Editing the {_season.Label} (entrant list {_season.EntrantList}).", MutedBrush);
    }

    private void PopulatePickers()
    {
        if (_catalogue is null) return;

        DriverBox.ItemsSource = _catalogue.Drivers;
        PopulateKitPickers();

        var pools = DriverAttributes.ValuePools(_attributeRows);
        foreach (var (field, editor) in _attributeEditors)
            editor.ItemsSource = pools.GetValueOrDefault(field, []);
    }

    /// <summary>
    /// Fill the helmet, suit and glove lists from the open database.
    ///
    /// Also run when the advanced panel is revealed, not just on load: the panel
    /// can be switched on at any point, including long after a database was
    /// opened, and its pickers have to hold that database's records either way.
    /// </summary>
    private void PopulateKitPickers()
    {
        if (_catalogue is null) return;

        _helmetChoices = _catalogue.HelmetChoices();
        HelmetBox.ItemsSource = _helmetChoices.Select(c => c.Name).ToList();
        SuitBox.ItemsSource = _catalogue.Suits.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        GloveBox.ItemsSource = _catalogue.Gloves.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    // ── Grid tab ────────────────────────────────────────────────────────────

    private int? SelectedSlot => (SeatGrid.SelectedItem as SeatRow)?.Slot;

    private void RefreshSeatTable()
    {
        var selected = SelectedSlot;
        var rows = new List<SeatRow>();

        if (_seats is not null)
        {
            foreach (var seat in _season.Seats)
            {
                var slot = seat.Slot;
                var driverIsDefault = _seats.DriverIsDefault(slot);

                rows.Add(new SeatRow
                {
                    Slot = slot,
                    Team = seat.Team,
                    Driver = Season.Pretty(_seats.Occupant(slot)),
                    Source = driverIsDefault ? "default" : "assigned",
                    Number = NumberCell(slot, driverIsDefault),
                    Helmet = HelmetCell(slot, driverIsDefault),
                    Suit = KitCell(_seats.SuitOverride(slot), driverIsDefault, _kit.Suit,
                                   _seats.IsRestoringSuit(slot) ? KitSource.Stock : _seats.SavedSuit(slot)),
                    Gloves = KitCell(_seats.GloveOverride(slot), driverIsDefault, _kit.Gloves,
                                     _seats.IsRestoringGloves(slot) ? KitSource.Stock : _seats.SavedGloves(slot)),
                    IsChanged = !_seats.IsDefault(slot),
                });
            }
        }

        SeatGrid.ItemsSource = rows;
        if (selected is not null)
            SeatGrid.SelectedItem = rows.FirstOrDefault(r => r.Slot == selected.Value);
    }

    private string NumberCell(int slot, bool driverIsDefault)
    {
        if (_seats!.NumberOverride(slot) is { } forced) return forced + " *";
        if (_seats.Saved(slot).Number && !_seats.IsRestoringNumber(slot))
            return _seats.EffectiveNumber(slot) + " *";
        var effective = _seats.EffectiveNumber(slot);
        if (driverIsDefault && effective == "255") return "—";
        return effective;
    }

    private string HelmetCell(int slot, bool driverIsDefault)
    {
        if (_seats!.HelmetOverride(slot) is not null) return "override";
        // A pending restore reads as stock: the file still has the change, but
        // this is what the seat will be.
        if (_seats.Saved(slot).Helmet && !_seats.IsRestoringHelmet(slot))
        {
            // A helmet that followed a driver keeps saying so after the save.
            return _seats.SavedHelmet(slot) switch
            {
                HelmetSource.Own => "own",
                HelmetSource.Generic => "generic",
                _ => "override",
            };
        }
        if (!_kit.Helmet) return "off";
        if (driverIsDefault) return "stock";
        if (_seats.HasOwnHelmet(_seats.Occupant(slot))) return "own";
        return _seats.HelmetAvailable(_seats.Occupant(slot)) ? "generic" : "default";
    }

    private static string KitCell(string? over, bool driverIsDefault, bool followTeam, KitSource saved)
    {
        if (over is not null) return "override";

        // Team kit applied by an earlier save keeps saying team, the same way a
        // helmet that followed its driver keeps saying own.
        if (saved is KitSource.Team) return "team";
        if (saved is KitSource.Chosen) return "override";

        if (driverIsDefault) return "stock";
        return followTeam ? "team" : "off";
    }

    private void SeatGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSeatDetail();

    private void RefreshSeatDetail()
    {
        var slot = SelectedSlot;
        var enabled = slot is not null && _seats is not null;

        AssignButton.IsEnabled = enabled;
        HelmetSetButton.IsEnabled = enabled;
        NumberSetButton.IsEnabled = enabled;
        SuitSetButton.IsEnabled = enabled;
        GloveSetButton.IsEnabled = enabled;

        if (!enabled)
        {
            SeatSelectionLabel.Text = "No seat selected";
            SeatDetailLabel.Text = "";
            ResetSeatButton.IsEnabled = false;
            HelmetClearButton.IsEnabled = NumberClearButton.IsEnabled = false;
            SuitClearButton.IsEnabled = GloveClearButton.IsEnabled = false;
            return;
        }

        var s = slot!.Value;
        var seat = _season.Seats[s];

        SeatSelectionLabel.Text =
            $"Seat {s} · {seat.Team} · {Season.Pretty(_seats!.Occupant(s))}  (default: {Season.Pretty(seat.DefaultDriver)})";

        var helmetOverride = _seats.HelmetOverride(s);
        // Prefer the name it had when chosen — the record's own metadata is
        // overwritten once the choice is saved.
        var helmet = helmetOverride is not null
            ? $"{_seats.HelmetOverrideLabel(s) ?? _catalogue!.HelmetMetaByIndex.GetValueOrDefault(helmetOverride.Value, "?")} (override)"
            : _seats.EffectiveHelmet(s) is { } eff
                ? _catalogue!.HelmetName(eff)
                : "default";

        SeatDetailLabel.Text =
            $"helmet: {helmet}   ·   number: {_seats.EffectiveNumber(s)}" +
            $"   ·   suit: {KitDescription(_seats.SuitOverride(s), s, _kit.Suit)}" +
            $"   ·   gloves: {KitDescription(_seats.GloveOverride(s), s, _kit.Gloves)}";

        NumberBox.Text = _seats.NumberOverride(s) ?? _seats.EffectiveNumber(s);

        // Clear is offered for anything that isn't stock, including changes an
        // earlier session saved — those are undone by writing the backup's
        // values back rather than by dropping a pending choice.
        var saved = _seats.Saved(s);
        ResetSeatButton.IsEnabled = !_seats.IsDefault(s);
        HelmetClearButton.IsEnabled = helmetOverride is not null || saved.Helmet;
        NumberClearButton.IsEnabled = _seats.NumberOverride(s) is not null || saved.Number;
        SuitClearButton.IsEnabled = _seats.SuitOverride(s) is not null || saved.Suit;
        GloveClearButton.IsEnabled = _seats.GloveOverride(s) is not null || saved.Gloves;
    }

    private string KitDescription(string? over, int slot, bool followTeam)
    {
        if (over is not null) return $"{over} (override)";
        if (_seats!.DriverIsDefault(slot)) return "stock";
        return followTeam ? "team kit" : "unchanged";
    }

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (_seats is null || SelectedSlot is not { } slot) return;

        var driver = DriverBox.SelectedItem as DriverCatalogue.DriverEntry
                     ?? _catalogue?.Drivers.FirstOrDefault(d =>
                         string.Equals(d.Display, DriverBox.Text, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(d.Id, DriverBox.Text, StringComparison.OrdinalIgnoreCase));

        if (driver is null)
        {
            SetStatus("Pick a driver from the list first.", ErrorBrush);
            return;
        }

        _seats.Assign(slot, driver.Id);
        var note = _seats.HasOwnHelmet(driver.Id)
            ? ""
            : _seats.HelmetAvailable(driver.Id)
                ? "  (no helmet of his own — using a generic one)"
                : "  (no helmet available — keeps the default)";
        Log($"seat {slot}: {Season.Pretty(driver.Id)}{note}");
        RefreshEverything();
    }

    private void ResetSeat_Click(object sender, RoutedEventArgs e)
    {
        if (_seats is null || SelectedSlot is not { } slot) return;
        _seats.Reset(slot);
        Log($"seat {slot}: reset to {Season.Pretty(_season.Seats[slot].DefaultDriver)}");
        RefreshEverything();
    }

    private void HelmetOverride_Click(object sender, RoutedEventArgs e)
    {
        if (_seats is null || _catalogue is null || SelectedSlot is not { } slot) return;
        var meta = HelmetBox.SelectedItem as string ?? HelmetBox.Text;
        var choice = _helmetChoices.FirstOrDefault(c => c.Name == meta);
        if (choice.Name is null)
        {
            SetStatus("Pick a helmet from the list first.", ErrorBrush);
            return;
        }
        _seats.SetHelmetOverride(slot, choice.Record, choice.Name);
        Log($"seat {slot}: helmet -> {meta}");
        RefreshEverything();
    }

    private void HelmetClear_Click(object sender, RoutedEventArgs e) =>
        ClearOverride(s => _seats!.ClearHelmetOverride(s), "helmet override cleared");

    private void NumberOverride_Click(object sender, RoutedEventArgs e)
    {
        if (_seats is null || SelectedSlot is not { } slot) return;

        var text = NumberBox.Text.Trim();
        if (!int.TryParse(text, out var value) || (value != 255 && value is < 0 or > 99))
        {
            SetStatus("Enter 0–99, or 255 for the driver's own number.", ErrorBrush);
            return;
        }

        _seats.SetNumberOverride(slot, value.ToString());
        Log($"seat {slot}: number -> {value}");
        RefreshEverything();
    }

    private void NumberClear_Click(object sender, RoutedEventArgs e) =>
        ClearOverride(s => _seats!.ClearNumberOverride(s), "number override cleared");

    private void SuitOverride_Click(object sender, RoutedEventArgs e) =>
        SetKitOverride(SuitBox, _catalogue?.Suits, (s, m) => _seats!.SetSuitOverride(s, m), "suit");

    private void SuitClear_Click(object sender, RoutedEventArgs e) =>
        ClearOverride(s => _seats!.ClearSuitOverride(s), "suit override cleared");

    private void GloveOverride_Click(object sender, RoutedEventArgs e) =>
        SetKitOverride(GloveBox, _catalogue?.Gloves, (s, m) => _seats!.SetGloveOverride(s, m), "gloves");

    private void GloveClear_Click(object sender, RoutedEventArgs e) =>
        ClearOverride(s => _seats!.ClearGloveOverride(s), "gloves override cleared");

    private void SetKitOverride(
        ComboBox box,
        Dictionary<string, DriverCatalogue.KitEntry>? table,
        Action<int, string> set,
        string label)
    {
        if (_seats is null || table is null || SelectedSlot is not { } slot) return;

        var meta = box.SelectedItem as string ?? box.Text;
        if (meta is null || !table.ContainsKey(meta))
        {
            SetStatus($"Pick a {label} from the list first.", ErrorBrush);
            return;
        }

        set(slot, meta);
        Log($"seat {slot}: {label} -> {meta}");
        RefreshEverything();
    }

    private void ClearOverride(Action<int> clear, string message)
    {
        if (_seats is null || SelectedSlot is not { } slot) return;
        clear(slot);
        Log($"seat {slot}: {message}");
        RefreshEverything();
    }

    private void KitOption_Changed(object sender, RoutedEventArgs e)
    {
        _kit.Helmet = HelmetFollowsBox.IsChecked == true;
        _kit.Suit = SuitTeamBox.IsChecked == true;
        _kit.Gloves = GloveTeamBox.IsChecked == true;
        RefreshEverything();
    }

    // ── Drivers tab ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lay the seven identity fields out two columns wide. Built in code so the
    /// field list stays the single source — adding one here needs no XAML.
    /// </summary>
    private void BuildAttributeEditors()
    {
        var fields = DriverAttributes.Fields;
        var perColumn = (fields.Count + 1) / 2;

        for (var i = 0; i < perColumn; i++) AttributeFieldGrid.RowDefinitions.Add(new RowDefinition());

        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var row = i % perColumn;
            var column = (i / perColumn) * 3;

            var label = new TextBlock
            {
                Text = DriverAttributes.Labels[field],
                Style = (Style)FindResource("FieldLabel"),
                Margin = new Thickness(0, 3, 8, 3),
            };
            System.Windows.Controls.Grid.SetRow(label, row);
            System.Windows.Controls.Grid.SetColumn(label, column);
            AttributeFieldGrid.Children.Add(label);

            // Editable so a key that isn't in use yet can still be typed.
            var editor = new ComboBox
            {
                IsEditable = true,
                IsTextSearchEnabled = true,
                Margin = new Thickness(0, 3, 0, 3),
            };
            System.Windows.Controls.Grid.SetRow(editor, row);
            System.Windows.Controls.Grid.SetColumn(editor, column + 1);
            AttributeFieldGrid.Children.Add(editor);

            _attributeEditors[field] = editor;
        }
    }

    private int? SelectedDriverIndex => (DriverGrid.SelectedItem as DriverRow)?.Index;

    private void AttributeSearch_Changed(object sender, TextChangedEventArgs e) => RefreshDriverTable();

    private void RefreshDriverTable()
    {
        var selected = SelectedDriverIndex;
        var query = AttributeSearchBox.Text.Trim();
        var rows = new List<DriverRow>();

        foreach (var driver in _attributeRows)
        {
            var values = EffectiveAttributes(driver);

            if (query.Length > 0)
            {
                var haystack = driver.DriverId + " " + string.Join(' ', values.Values);
                if (!haystack.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            }

            rows.Add(new DriverRow
            {
                Index = driver.Index,
                DriverId = driver.DriverId,
                Initials = values[DriverAttributes.Initials],
                Nationality = values[DriverAttributes.Nationality],
                FirstName = values[DriverAttributes.FirstName],
                LastName = values[DriverAttributes.LastName],
                FullName = values[DriverAttributes.FullName],
                Head = values[DriverAttributes.Head],
                AudioName = values[DriverAttributes.AudioName],
                IsChanged = _pendingAttributes.ContainsKey(driver.Index),
            });
        }

        DriverGrid.ItemsSource = rows;
        if (selected is not null)
            DriverGrid.SelectedItem = rows.FirstOrDefault(r => r.Index == selected.Value);
    }

    /// <summary>A driver's fields with any unsaved edits laid over what's on disk.</summary>
    private Dictionary<string, string> EffectiveAttributes(DriverAttributes.DriverRow driver)
    {
        var values = new Dictionary<string, string>(driver.Values, StringComparer.Ordinal);
        if (_pendingAttributes.TryGetValue(driver.Index, out var pending))
            foreach (var (field, value) in pending) values[field] = value;
        return values;
    }

    private void DriverGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDriverDetail();

    private void RefreshDriverDetail()
    {
        var index = SelectedDriverIndex;
        var driver = index is null ? null : _attributeRows.FirstOrDefault(d => d.Index == index.Value);

        AttributeApplyButton.IsEnabled = driver is not null;
        AttributeRevertButton.IsEnabled = driver is not null && _pendingAttributes.ContainsKey(driver.Index);

        if (driver is null)
        {
            DriverSelectionLabel.Text = "No driver selected";
            foreach (var editor in _attributeEditors.Values) editor.Text = "";
            return;
        }

        DriverSelectionLabel.Text = $"driver_{driver.Index} · {driver.DriverId} · {Season.Pretty(driver.DriverId)}";

        var values = EffectiveAttributes(driver);
        foreach (var (field, editor) in _attributeEditors) editor.Text = values.GetValueOrDefault(field, "");
    }

    private void AttributeApply_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDriverIndex is not { } index) return;
        var driver = _attributeRows.FirstOrDefault(d => d.Index == index);
        if (driver is null) return;

        var proposed = _attributeEditors.ToDictionary(kv => kv.Key, kv => kv.Value.Text.Trim(), StringComparer.Ordinal);

        if (DriverAttributes.Validate(proposed) is { } problem)
        {
            SetStatus(problem, ErrorBrush);
            return;
        }

        var changed = proposed
            .Where(kv => kv.Value != driver[kv.Key])
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        if (changed.Count == 0)
        {
            _pendingAttributes.Remove(index);
            Log($"driver_{index}: no changes");
        }
        else
        {
            _pendingAttributes[index] = changed;
            Log($"driver_{index} ({driver.DriverId}): {changed.Count} field(s) staged");
        }

        RefreshEverything();
    }

    private void AttributeRevert_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDriverIndex is not { } index) return;
        _pendingAttributes.Remove(index);
        Log($"driver_{index}: staged changes discarded");
        RefreshEverything();
    }

    // ── Import / export ─────────────────────────────────────────────────────

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_seats is null || _catalogue is null) return;

        var set = ChangeSet.Capture(_seats, _kit, _catalogue, _pendingAttributes, _attributeRows);
        if (set.SeatCount == 0 && set.AttributeFieldCount == 0)
        {
            SetStatus("There are no changes to export yet.", ErrorBrush);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Export changes",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "driver-changes.json",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            set.Save(dlg.FileName);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't export: {ex.Message}", ErrorBrush);
            return;
        }

        SetStatus($"Exported {set.SeatCount} seat change(s) and {set.AttributeFieldCount} attribute field(s).", OkBrush);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_seats is null || _catalogue is null) return;

        var dlg = new OpenFileDialog
        {
            Title = "Import changes",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        ChangeSet set;
        try
        {
            set = ChangeSet.Load(dlg.FileName);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't read that file: {ex.Message}", ErrorBrush);
            return;
        }

        var applied = 0;
        var skipped = new List<string>();

        HelmetFollowsBox.IsChecked = set.Grid.Options.Helmet;
        SuitTeamBox.IsChecked = set.Grid.Options.Suit;
        GloveTeamBox.IsChecked = set.Grid.Options.Gloves;
        KitOption_Changed(this, new RoutedEventArgs());

        var driverIds = _catalogue.Drivers.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var (slotText, change) in set.Grid.Seats)
        {
            if (!int.TryParse(slotText, out var slot) || slot < 0 || slot >= _season.Seats.Count)
            {
                skipped.Add($"seat {slotText} isn't on this grid");
                continue;
            }

            if (change.Driver is { } driver)
            {
                if (driverIds.Contains(driver)) { _seats.Assign(slot, driver); applied++; }
                else skipped.Add($"seat {slot}: no driver '{driver}'");
            }

            if (change.Number is { } number) { _seats.SetNumberOverride(slot, number); applied++; }

            if (change.Helmet is { } helmet)
            {
                var match = _helmetChoices.FirstOrDefault(c => c.Name == helmet);
                if (match.Name is not null) { _seats.SetHelmetOverride(slot, match.Record, match.Name); applied++; }
                else skipped.Add($"seat {slot}: no helmet '{helmet}'");
            }

            if (change.Suit is { } suit)
            {
                if (_catalogue.Suits.ContainsKey(suit)) { _seats.SetSuitOverride(slot, suit); applied++; }
                else skipped.Add($"seat {slot}: no suit '{suit}'");
            }

            if (change.Gloves is { } gloves)
            {
                if (_catalogue.Gloves.ContainsKey(gloves)) { _seats.SetGloveOverride(slot, gloves); applied++; }
                else skipped.Add($"seat {slot}: no gloves '{gloves}'");
            }
        }

        var indexById = _attributeRows.ToDictionary(r => r.DriverId, r => r.Index, StringComparer.Ordinal);
        foreach (var (driverId, fields) in set.Attributes)
        {
            if (!indexById.TryGetValue(driverId, out var index))
            {
                skipped.Add($"no driver '{driverId}'");
                continue;
            }

            var row = _attributeRows.First(r => r.Index == index);
            foreach (var (field, value) in fields)
            {
                if (!DriverAttributes.Fields.Contains(field) || value.Trim().Length == 0) continue;
                if (value.Trim() == row[field]) continue;

                if (!_pendingAttributes.TryGetValue(index, out var pending))
                    _pendingAttributes[index] = pending = new Dictionary<string, string>(StringComparer.Ordinal);
                pending[field] = value.Trim();
                applied++;
            }
        }

        RefreshEverything();

        Log($"imported {applied} change(s) from {Path.GetFileName(dlg.FileName)}");
        foreach (var s in skipped) Log($"  skipped: {s}");

        SetStatus(
            skipped.Count == 0
                ? $"Staged {applied} change(s). Review them, then Save."
                : $"Staged {applied} change(s); {skipped.Count} didn't apply to this database — see the log.",
            skipped.Count == 0 ? OkBrush : ErrorBrush);
    }

    // ── Saving ──────────────────────────────────────────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_db is null || _path is null || _seats is null) return;

        if (_seats.Duplicates().Count > 0)
        {
            SetStatus("Two seats hold the same driver — fix that before saving.", ErrorBrush);
            return;
        }

        var plan = _seats.Apply(_kit);

        var attributeWrites = 0;
        foreach (var (index, fields) in _pendingAttributes)
        {
            foreach (var (field, value) in fields)
            {
                var record = _db.Find(Season.DriverPath(index, field));
                if (record is null || record.Text == value) continue;
                record.Text = value;
                attributeWrites++;
            }
        }

        try
        {
            _db.SaveAtomic(_path);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't save: {ex.Message}", ErrorBrush);
            SimplifiedUi.MessageDialog.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Log($"saved: {plan.Seats.Count} seat, {plan.Helmets.Count} helmet, {plan.Suits.Count} suit, " +
            $"{plan.Gloves.Count} glove, {attributeWrites} attribute write(s)");

        var summary = $"Saved {Path.GetFileName(_path)} — {plan.Total + attributeWrites} change(s) written.";

        // Reopen so what's shown is what's on disk, not what we hoped to write.
        var before = _seats;
        Load(_path);

        // Kit choices survive the reopen: they're on disk now, but a saved helmet
        // can't be recognised by reading it back, so the view would otherwise
        // forget which one the seat is wearing.
        _seats?.AdoptAsSaved(before);
        RefreshEverything();
        SetStatus(summary, OkBrush);
    }

    // ── Packaging ───────────────────────────────────────────────────────────

    /// <summary>
    /// Write the edited database into a zip laid out like the game folder, ready
    /// to drop into a mod manager, without touching the installed files.
    ///
    /// The edits are replayed onto a fresh copy read from disk rather than
    /// applied to the open one, so packaging leaves the staged changes exactly
    /// as they were — you can package and then keep working, or also Save.
    /// </summary>
    private void Package_Click(object sender, RoutedEventArgs e)
    {
        if (_db is null || _path is null || _seats is null) return;

        if (_seats.Duplicates().Count > 0)
        {
            SetStatus("Two seats hold the same driver — fix that before packaging.", ErrorBrush);
            return;
        }

        var entryPath = GameRelativePath(_path);

        var dlg = new SaveFileDialog
        {
            Title = "Package the edited database",
            Filter = "Zip archives (*.zip)|*.zip|All files (*.*)|*.*",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = "Driver Swaps.zip",
        };
        if (dlg.ShowDialog(this) != true) return;

        var staging = Path.Combine(Path.GetTempPath(), $"driverswap_{Guid.NewGuid():N}.bin");

        try
        {
            var copy = GameDatabase.Load(_path);
            var writes = ReplayChangesOnto(copy);
            copy.Save(staging);

            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            using (var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(staging, entryPath, CompressionLevel.Optimal);

            Log($"packaged {writes} change(s) as {entryPath}");
            SetStatus($"Packaged {Path.GetFileName(dlg.FileName)} — {writes} change(s) at {entryPath}.", OkBrush);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't package: {ex.Message}", ErrorBrush);
            SimplifiedUi.MessageDialog.Show(this, ex.Message, "Package failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (File.Exists(staging)) try { File.Delete(staging); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Put the staged edits onto another copy of the same database. Returns how
    /// many records were written.
    /// </summary>
    private int ReplayChangesOnto(GameDatabase copy)
    {
        var catalogue = DriverCatalogue.Read(copy);
        var plan = new SeatPlan(copy, catalogue, _season);

        // Mirror exactly the seats that were touched, so the package matches what
        // Save would write — no more, no less.
        foreach (var slot in _seats!.TouchedSeats)
        {
            // Reset marks the seat and clears its overrides, so it has to come
            // before they're re-applied below.
            if (_seats.DriverIsDefault(slot)) plan.Reset(slot);
            else plan.Assign(slot, _seats.Occupant(slot));

            if (_seats.NumberOverride(slot) is { } number) plan.SetNumberOverride(slot, number);
            if (_seats.SuitOverride(slot) is { } suit) plan.SetSuitOverride(slot, suit);
            if (_seats.GloveOverride(slot) is { } gloves) plan.SetGloveOverride(slot, gloves);

            // Helmet overrides are held as record indexes, which are only
            // meaningful against the catalogue they came from — carry the
            // metadata string across and look it up again.
            if (_seats.HelmetOverride(slot) is { } helmetIndex
                && _catalogue!.HelmetMetaByIndex.TryGetValue(helmetIndex, out var meta)
                && catalogue.Helmets.TryGetValue(meta, out var copyIndex))
            {
                plan.SetHelmetOverride(slot, copyIndex);
            }
        }

        var applied = plan.Apply(_kit);

        var attributeWrites = 0;
        foreach (var (index, fields) in _pendingAttributes)
        {
            foreach (var (field, value) in fields)
            {
                var record = copy.Find(Season.DriverPath(index, field));
                if (record is null || record.Text == value) continue;
                record.Text = value;
                attributeWrites++;
            }
        }

        return applied.Total + attributeWrites;
    }

    /// <summary>
    /// Where a file sits in the game, as a zip entry path.
    ///
    /// Rooted at the game folder itself (<c>F1 25/game_package/…</c>) so the
    /// archive mirrors the install from one level up, which is what a mod
    /// manager expects to unpack.
    ///
    /// Falls back to the folder the game keeps its databases in when the open
    /// file lives somewhere else, so a copy on the desktop still packages to the
    /// right place.
    /// </summary>
    private string GameRelativePath(string path)
    {
        const string defaultGameFolder = "F1 25";

        var gameDir = _config.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return $"{defaultGameFolder}/game_package/gamedatabases/{Path.GetFileName(path)}";

        // Trim first: GetFileName gives back nothing for a trailing separator.
        var gameFolder = Path.GetFileName(
            gameDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(gameFolder)) gameFolder = defaultGameFolder;

        var relative = Path.GetRelativePath(gameDir, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return $"{gameFolder}/game_package/gamedatabases/{Path.GetFileName(path)}";

        return $"{gameFolder}/{relative.Replace('\\', '/')}";
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges()) e.Cancel = true;
    }

    // ── Window chrome ───────────────────────────────────────────────────────

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Keep the middle button showing what it will do. Double-clicking the title
    /// bar maximises too — that's WindowChrome's doing, not ours — so the glyph
    /// has to follow the state rather than the click.
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

    private bool HasPendingChanges =>
        (_seats?.HasChanges ?? false) || _pendingAttributes.Count > 0;

    private bool ConfirmDiscardChanges()
    {
        if (!HasPendingChanges) return true;

        var answer = SimplifiedUi.MessageDialog.Show(this,
            "There are changes that haven't been saved. Discard them?",
            "Simplified Driver Swapper", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return answer == MessageBoxResult.OK;
    }

    // ── Shared refresh ──────────────────────────────────────────────────────

    private void RefreshEverything()
    {
        RefreshSeatTable();
        RefreshSeatDetail();
        RefreshDriverTable();
        RefreshDriverDetail();
        RefreshHeaderAndSave();
    }

    private void RefreshHeaderAndSave()
    {
        FileNameLabel.Text = _path is null ? "No database open" : Path.GetFileName(_path);
        FileNameLabel.Foreground = _path is null ? HintBrush : MutedBrush;

        ImportButton.IsEnabled = _db is not null;
        ExportButton.IsEnabled = _db is not null;
        PendingLabel.Visibility = HasPendingChanges ? Visibility.Visible : Visibility.Collapsed;

        if (_db is null || _seats is null)
        {
            SaveButton.IsEnabled = false;
            PackageButton.IsEnabled = false;
            return;
        }

        var duplicates = _seats.Duplicates();
        if (duplicates.Count > 0)
        {
            SaveButton.IsEnabled = false;
            PackageButton.IsEnabled = false;
            var lines = duplicates.Select(kv => $"{Season.Pretty(kv.Key)} in seats {string.Join(", ", kv.Value)}");
            SetStatus(
                "The same driver is in more than one seat, so the grid would show him twice. " +
                "Change the other seat too: " + string.Join("; ", lines), ErrorBrush);
            return;
        }

        var plan = _seats.Plan(_kit);
        var attributeFields = _pendingAttributes.Values.Sum(v => v.Count);
        var total = plan.Total + attributeFields;

        SaveButton.IsEnabled = total > 0;

        // Packaging an unchanged database is pointless but harmless, so this only
        // needs a file open and no duplicates to resolve.
        PackageButton.IsEnabled = true;

        if (total == 0)
        {
            SetStatus("No changes pending.", MutedBrush);
            return;
        }

        var message = $"Ready to save: {plan.Seats.Count} seat, {plan.Helmets.Count} helmet, " +
                      $"{plan.Suits.Count} suit, {plan.Gloves.Count} glove, {attributeFields} attribute change(s).";
        if (plan.Notes.Count > 0) message += "  " + string.Join("  ", plan.Notes);
        SetStatus(message, plan.Notes.Count > 0 ? ErrorBrush : OkBrush);
    }

    // ── Team menu (Ctrl+T) ──────────────────────────────────────────────────

    /// <summary>
    /// The team menu is unlocked by a <c>team.simplified</c> file next to the
    /// exe whose JSON says <c>"advanced": true</c>. A missing, empty or
    /// wrong-content file doesn't count, so knowing the file name isn't enough
    /// to forge the unlock. Without a valid marker Ctrl+T does nothing at all.
    /// </summary>
    private static bool TeamUnlocked => TeamConfig.Load().Advanced;

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        if (!ctrl || e.Key != System.Windows.Input.Key.T) return;
        if (!TeamUnlocked) return;

        OpenTeamMenu();
        e.Handled = true;
    }

    private void OpenTeamMenu()
    {
        // Only reachable with a marker present, so this carries the real values
        // — Advanced included, which has to survive the save below.
        var team = TeamConfig.Load();

        var root = new StackPanel { Margin = new Thickness(18) };

        root.Children.Add(new TextBlock
        {
            Text = "Team Simplified",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var logButton = new Button
        {
            Content = LogBox.Visibility == Visibility.Visible ? "Hide log" : "Show log",
            Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Show or hide the log panel under the status line.",
        };
        logButton.Click += (_, _) =>
        {
            var show = LogBox.Visibility != Visibility.Visible;
            ApplyLogVisibility(show);
            logButton.Content = show ? "Hide log" : "Show log";

            // Persisted in the marker rather than the per-user config, so it
            // can't be turned on by editing config.json.
            team.ShowLog = show;
            team.Save();
        };
        root.Children.Add(logButton);

        root.Children.Add(new TextBlock
        {
            Text = "Off by default. The setting is remembered in team.simplified.",
            Foreground = HintBrush,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var close = new Button
        {
            Content = "Close",
            Padding = new Thickness(18, 4, 18, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            IsCancel = true,
        };
        root.Children.Add(close);

        var window = new Window
        {
            Title = "Team Simplified",
            Content = root,
            Owner = this,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 320,
        };

        // After Content, never before: DialogChrome wraps whatever is there at
        // the time, so content assigned afterwards replaces the title bar too.
        SimplifiedUi.DialogChrome.Apply(window);
        window.ShowDialog();
    }

    private void ApplyLogVisibility(bool show) =>
        LogBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Show or hide the per-seat kit controls.
    ///
    /// Only the controls move — the three rules stay on regardless, because
    /// having the kit follow the driver is the behaviour we want by default.
    /// Hiding them takes away the choice, not the effect.
    /// </summary>
    private void ApplyAdvancedSwapping(bool show)
    {
        AdvancedKitPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            // Revealed after the database was opened, so fill the lists now.
            PopulateKitPickers();
            RefreshSeatDetail();
            return;
        }

        // Back to the standard behaviour, in case they were unticked while visible.
        HelmetFollowsBox.IsChecked = true;
        SuitTeamBox.IsChecked = true;
        GloveTeamBox.IsChecked = true;
        KitOption_Changed(this, new RoutedEventArgs());
    }

    // ── Settings & helpers ──────────────────────────────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!DriverSettings.Show(this, _config)) return;

        _config.Save();
        ApplyAdvancedSwapping(_config.EnableAdvancedSwapping);
    }

    private void SetStatus(string text, Brush brush)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = brush;
    }

    private void Log(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private static string? FirstExistingDir(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c)) return c;
        return null;
    }
}
