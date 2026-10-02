using System.Windows;
using System.Windows.Controls;
using SimplifiedDriverSwapper.Core.Drivers;
using SimplifiedGridEditor.Core.Project;

namespace SimplifiedGridEditor;

/// <summary>
/// The Grid tab: one row per seat on the season's entrant list, and an editor
/// for whichever is selected.
///
/// Lifted from the Driver Swapper, whose whole window this was. The difference
/// is where the state lives: there it was fields on the window, here it is the
/// <see cref="GridDocument"/> this panel shares with the Drivers tab, because
/// both are looking at records in the same database.
/// </summary>
public partial class GridPanel : UserControl
{
    private GridDocument? _document;
    private Season _season = Season.All[0];
    private readonly KitOptions _kit = new();

    /// <summary>
    /// The helmet picker's contents: each design by name, and the record now
    /// carrying it. Names come from the pristine copy, so they stay meaningful
    /// in a database that has already been saved once.
    /// </summary>
    private List<(string Name, int Record)> _helmetChoices = [];

    public GridPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised whenever an edit is made, so the window can mark the project dirty.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised with a line worth putting on the status bar.</summary>
    public event EventHandler<PanelMessage>? Said;

    /// <summary>Point the panel at a project's database, or at nothing.</summary>
    public void Show(GridDocument? document, Season season)
    {
        _document = document;
        _season = season;

        if (document is not null)
        {
            DriverBox.ItemsSource = document.Catalogue.Drivers;
            PopulateKitPickers();
        }
        else
        {
            DriverBox.ItemsSource = null;
            HelmetBox.ItemsSource = SuitBox.ItemsSource = GloveBox.ItemsSource = null;
        }

        Refresh();
    }

    /// <summary>Switch which season's seats are shown. The document rebuilds its plan.</summary>
    public void ShowSeason(Season season)
    {
        _season = season;
        _document?.ShowSeason(season);
        Refresh();
    }

    /// <summary>
    /// Show or hide the per-seat kit controls.
    ///
    /// Only the controls move — the three rules stay on regardless, because
    /// having the kit follow the driver is the behaviour we want by default.
    /// Hiding them takes away the choice, not the effect.
    /// </summary>
    public void ShowAdvanced(bool show)
    {
        AdvancedKitPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            // Revealed after the project was opened, so fill the lists now.
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

    /// <summary>The seat changes staged here, ready to be written into the database.</summary>
    public SeatPlan? Plan => _document?.Seats;

    public KitOptions Kit => _kit;

    public void Refresh()
    {
        RefreshSeatTable();
        RefreshSeatDetail();
    }

    private void PopulateKitPickers()
    {
        if (_document is null) return;

        _helmetChoices = _document.Catalogue.HelmetChoices();
        HelmetBox.ItemsSource = _helmetChoices.Select(c => c.Name).ToList();
        SuitBox.ItemsSource = _document.Catalogue.Suits.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        GloveBox.ItemsSource = _document.Catalogue.Gloves.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    // ── The table ───────────────────────────────────────────────────────────

    private int? SelectedSlot => (SeatGrid.SelectedItem as SeatRow)?.Slot;

    private void RefreshSeatTable()
    {
        var selected = SelectedSlot;
        var rows = new List<SeatRow>();
        var seats = _document?.Seats;

        if (seats is not null)
        {
            foreach (var seat in _season.Seats)
            {
                var slot = seat.Slot;
                var driverIsDefault = seats.DriverIsDefault(slot);

                rows.Add(new SeatRow
                {
                    Slot = slot,
                    Team = seat.Team,
                    Driver = Season.Pretty(seats.Occupant(slot)),
                    Source = driverIsDefault ? "default" : "assigned",
                    Number = NumberCell(seats, slot, driverIsDefault),
                    Helmet = HelmetCell(seats, slot, driverIsDefault),
                    Suit = KitCell(seats.SuitOverride(slot), driverIsDefault, _kit.Suit,
                                   seats.IsRestoringSuit(slot) ? KitSource.Stock : seats.SavedSuit(slot)),
                    Gloves = KitCell(seats.GloveOverride(slot), driverIsDefault, _kit.Gloves,
                                     seats.IsRestoringGloves(slot) ? KitSource.Stock : seats.SavedGloves(slot)),
                    IsChanged = !seats.IsDefault(slot),
                });
            }
        }

        SeatGrid.ItemsSource = rows;
        if (selected is not null)
            SeatGrid.SelectedItem = rows.FirstOrDefault(r => r.Slot == selected.Value);
    }

    private string NumberCell(SeatPlan seats, int slot, bool driverIsDefault)
    {
        if (seats.NumberOverride(slot) is { } forced) return forced + " *";
        if (seats.Saved(slot).Number && !seats.IsRestoringNumber(slot))
            return seats.EffectiveNumber(slot) + " *";
        var effective = seats.EffectiveNumber(slot);
        if (driverIsDefault && effective == "255") return "—";
        return effective;
    }

    private string HelmetCell(SeatPlan seats, int slot, bool driverIsDefault)
    {
        if (seats.HelmetOverride(slot) is not null) return "override";
        // A pending restore reads as stock: the file still has the change, but
        // this is what the seat will be.
        if (seats.Saved(slot).Helmet && !seats.IsRestoringHelmet(slot))
        {
            // A helmet that followed a driver keeps saying so after the save.
            return seats.SavedHelmet(slot) switch
            {
                HelmetSource.Own => "own",
                HelmetSource.Generic => "generic",
                _ => "override",
            };
        }
        if (!_kit.Helmet) return "off";
        if (driverIsDefault) return "stock";
        if (seats.HasOwnHelmet(seats.Occupant(slot))) return "own";
        return seats.HelmetAvailable(seats.Occupant(slot)) ? "generic" : "default";
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
        var seats = _document?.Seats;
        var enabled = slot is not null && seats is not null;

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
            $"Seat {s} · {seat.Team} · {Season.Pretty(seats!.Occupant(s))}  (default: {Season.Pretty(seat.DefaultDriver)})";

        var helmetOverride = seats.HelmetOverride(s);
        // Prefer the name it had when chosen — the record's own metadata is
        // overwritten once the choice is saved.
        var helmet = helmetOverride is not null
            ? $"{seats.HelmetOverrideLabel(s) ?? _document!.Catalogue.HelmetMetaByIndex.GetValueOrDefault(helmetOverride.Value, "?")} (override)"
            : seats.EffectiveHelmet(s) is { } eff
                ? _document!.Catalogue.HelmetName(eff)
                : "default";

        SeatDetailLabel.Text =
            $"helmet: {helmet}   ·   number: {seats.EffectiveNumber(s)}" +
            $"   ·   suit: {KitDescription(seats, seats.SuitOverride(s), s, _kit.Suit)}" +
            $"   ·   gloves: {KitDescription(seats, seats.GloveOverride(s), s, _kit.Gloves)}";

        NumberBox.Text = seats.NumberOverride(s) ?? seats.EffectiveNumber(s);

        // Clear is offered for anything that isn't stock, including changes an
        // earlier save wrote — those are undone by writing the pristine values
        // back rather than by dropping a pending choice.
        var saved = seats.Saved(s);
        ResetSeatButton.IsEnabled = !seats.IsDefault(s);
        HelmetClearButton.IsEnabled = helmetOverride is not null || saved.Helmet;
        NumberClearButton.IsEnabled = seats.CanClearNumber(s);
        SuitClearButton.IsEnabled = seats.SuitOverride(s) is not null || saved.Suit;
        GloveClearButton.IsEnabled = seats.GloveOverride(s) is not null || saved.Gloves;
    }

    private static string KitDescription(SeatPlan seats, string? over, int slot, bool followTeam)
    {
        if (over is not null) return $"{over} (override)";
        if (seats.DriverIsDefault(slot)) return "stock";
        return followTeam ? "team kit" : "unchanged";
    }

    // ── Editing ─────────────────────────────────────────────────────────────

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || SelectedSlot is not { } slot) return;

        var driver = DriverBox.SelectedItem as DriverCatalogue.DriverEntry
                     ?? _document.Catalogue.Drivers.FirstOrDefault(d =>
                         string.Equals(d.Display, DriverBox.Text, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(d.Id, DriverBox.Text, StringComparison.OrdinalIgnoreCase));

        if (driver is null)
        {
            Say("Pick a driver from the list first.", problem: true);
            return;
        }

        // Whether this puts the same driver in two seats, asked before the
        // assignment rather than after it.
        var clash = SeatHolding(driver.Id, except: slot);

        _document.Seats.Assign(slot, driver.Id);
        var note = _document.Seats.HasOwnHelmet(driver.Id)
            ? ""
            : _document.Seats.HelmetAvailable(driver.Id)
                ? "  (no helmet of his own — using a generic one)"
                : "  (no helmet available — keeps the default)";

        // One driver in two seats is a state a grid passes through rather than
        // a mistake to refuse: swapping two drivers is two assignments, and the
        // first of them always makes a pair. Refusing it meant a grid could not
        // be rearranged at all without an empty seat to park somebody in.
        //
        // So it is allowed and said plainly, and Package is where it is refused
        // — that is the moment it actually matters, since the game shows the
        // same face twice, and by then it is one dialog naming every clash
        // rather than a rule firing on every move.
        if (clash is { } taken)
        {
            Edited("");
            Say($"{Season.Pretty(driver.Id)} is now in two seats — {taken} ({_season.Seats[taken].Team}) " +
                $"and {slot} ({_season.Seats[slot].Team}). One of them has to change before this can " +
                "be packaged.", problem: true);
            return;
        }

        Edited($"Seat {slot}: {Season.Pretty(driver.Id)}{note}");
    }

    /// <summary>
    /// The seat this driver is already in, if any — so an assignment that makes
    /// a pair can say so.
    ///
    /// Compared against who each seat would hold after the pending changes, not
    /// against the file: two seats staged for the same driver is the case worth
    /// noticing, and neither of them is on disk yet.
    /// </summary>
    private int? SeatHolding(string driverId, int except)
    {
        var seats = _document?.Seats;
        if (seats is null) return null;

        foreach (var seat in _season.Seats)
        {
            if (seat.Slot == except) continue;
            if (string.Equals(seats.Occupant(seat.Slot), driverId, StringComparison.Ordinal))
                return seat.Slot;
        }
        return null;
    }

    private void ResetSeat_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || SelectedSlot is not { } slot) return;
        _document.Seats.Reset(slot);
        Edited($"Seat {slot}: reset to {Season.Pretty(_season.Seats[slot].DefaultDriver)}.");
    }

    private void HelmetOverride_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || SelectedSlot is not { } slot) return;

        var meta = HelmetBox.SelectedItem as string ?? HelmetBox.Text;
        var choice = _helmetChoices.FirstOrDefault(c => c.Name == meta);
        if (choice.Name is null)
        {
            Say("Pick a helmet from the list first.", problem: true);
            return;
        }

        _document.Seats.SetHelmetOverride(slot, choice.Record, choice.Name);
        Edited($"Seat {slot}: helmet → {meta}.");
    }

    private void HelmetClear_Click(object sender, RoutedEventArgs e) =>
        ClearOverride(s => _document!.Seats.ClearHelmetOverride(s), "helmet override cleared");

    private void NumberOverride_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || SelectedSlot is not { } slot) return;

        var text = NumberBox.Text.Trim();
        if (!int.TryParse(text, out var value) || (value != 255 && value is < 0 or > 99))
        {
            Say("Enter 0–99, or 255 for the driver's own number.", problem: true);
            return;
        }

        _document.Seats.SetNumberOverride(slot, value.ToString());
        Edited($"Seat {slot}: number → {value}.");
    }

    private void NumberClear_Click(object sender, RoutedEventArgs e) =>
        ClearOverride(s => _document!.Seats.ClearNumberOverride(s), "number override cleared");

    private void SuitOverride_Click(object sender, RoutedEventArgs e) =>
        SetKitOverride(SuitBox, _document?.Catalogue.Suits, (s, m) => _document!.Seats.SetSuitOverride(s, m), "suit");

    private void SuitClear_Click(object sender, RoutedEventArgs e) =>
        ClearOverride(s => _document!.Seats.ClearSuitOverride(s), "suit override cleared");

    private void GloveOverride_Click(object sender, RoutedEventArgs e) =>
        SetKitOverride(GloveBox, _document?.Catalogue.Gloves, (s, m) => _document!.Seats.SetGloveOverride(s, m), "gloves");

    private void GloveClear_Click(object sender, RoutedEventArgs e) =>
        ClearOverride(s => _document!.Seats.ClearGloveOverride(s), "gloves override cleared");

    private void SetKitOverride(
        ComboBox box,
        Dictionary<string, DriverCatalogue.KitEntry>? table,
        Action<int, string> set,
        string label)
    {
        if (_document is null || table is null || SelectedSlot is not { } slot) return;

        var meta = box.SelectedItem as string ?? box.Text;
        if (meta is null || !table.ContainsKey(meta))
        {
            Say($"Pick a {label} from the list first.", problem: true);
            return;
        }

        set(slot, meta);
        Edited($"Seat {slot}: {label} → {meta}.");
    }

    private void ClearOverride(Action<int> clear, string message)
    {
        if (_document is null || SelectedSlot is not { } slot) return;
        clear(slot);
        Edited($"Seat {slot}: {message}.");
    }

    private void KitOption_Changed(object sender, RoutedEventArgs e)
    {
        _kit.Helmet = HelmetFollowsBox.IsChecked == true;
        _kit.Suit = SuitTeamBox.IsChecked == true;
        _kit.Gloves = GloveTeamBox.IsChecked == true;

        // Fires while the panel is being built, before there is anything to say.
        if (_document is null) { Refresh(); return; }
        Edited("");
    }

    private void Edited(string message)
    {
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        if (message.Length > 0) Say(message, problem: false);
    }

    private void Say(string message, bool problem) =>
        Said?.Invoke(this, new PanelMessage(message, problem));
}

/// <summary>Something a tab wants said on the status bar.</summary>
public sealed record PanelMessage(string Text, bool IsProblem);
