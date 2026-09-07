using System.Windows;
using System.Windows.Controls;
using SimplifiedDriverSwapper.Core.Drivers;
using SimplifiedGridEditor.Core.Project;

namespace SimplifiedGridEditor;

/// <summary>
/// The Start tab: who stands on the game's start screen.
///
/// Three slots, laid out left to right as they appear on screen. The records
/// behind them run the other way, which is the sort of detail that belongs in
/// <see cref="StartScreen"/> rather than up here — this only ever asks for a
/// slot by where it stands.
///
/// A row is built per slot rather than written out in XAML, so the three stay
/// identical and the list of slots remains the one place they are defined.
/// </summary>
public partial class StartPanel : UserControl
{
    private sealed record Row(ComboBox Picker, TextBlock State, Button Reset);

    private readonly Dictionary<StartScreen.Slot, Row> _rows = [];
    private GridDocument? _document;
    private Season _season = Season.F2026;

    /// <summary>True while the pickers are being filled, so that isn't read as an edit.</summary>
    private bool _filling;

    public StartPanel()
    {
        InitializeComponent();
        BuildRows();
    }

    /// <summary>Raised whenever an edit is staged, so the window can mark the project dirty.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised with a line worth putting on the status bar.</summary>
    public event EventHandler<PanelMessage>? Said;

    /// <summary>Point the panel at a project's database, or at nothing.</summary>
    public void Show(GridDocument? document, Season season)
    {
        _document = document;
        _season = season;
        Refresh();
    }

    /// <summary>
    /// Who can stand on the start screen: the drivers on the 2026 grid, in grid
    /// order, as the Grid tab currently has it.
    ///
    /// Staged swaps count, which is the point — swap someone in and they can
    /// take the start screen straight away, while whoever they replaced drops
    /// out of the list. That is why this is read from the seat plan rather than
    /// from the driver catalogue, which holds all two hundred.
    ///
    /// Always the 2026 grid, whichever season the chips are on: the start screen
    /// is one screen and does not change with them. When the Grid tab is showing
    /// 2026 its own plan is used, so staged swaps are included; on another season
    /// that plan is a different entrant list, so the 2026 grid is read fresh from
    /// the database instead.
    /// </summary>
    private List<DriverCatalogue.DriverEntry> GridDrivers()
    {
        if (_document is null) return [];

        var plan = _season == Season.F2026
            ? _document.Seats
            : new SeatPlan(_document.Database, _document.Catalogue, Season.F2026, _document.Reference);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var drivers = new List<DriverCatalogue.DriverEntry>();

        foreach (var seat in Season.F2026.Seats)
        {
            var id = plan.Occupant(seat.Slot);
            if (id.Length == 0 || !seen.Add(id)) continue;

            drivers.Add(_document.Catalogue.Drivers.FirstOrDefault(d => d.Id == id)
                        ?? new DriverCatalogue.DriverEntry(id, "255"));
        }

        return drivers;
    }

    public void Refresh()
    {
        var start = _document?.Start;
        var available = start?.Available == true;

        StartUnavailableLabel.Visibility = _document is not null && !available
            ? Visibility.Visible
            : Visibility.Collapsed;
        SlotGrid.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        StartResetAllButton.IsEnabled = available && StartScreen.Slots.Any(s => start!.DiffersFromStock(s));

        var pool = GridDrivers();

        _filling = true;
        foreach (var slot in StartScreen.Slots)
        {
            var row = _rows[slot];
            var id = start is null ? "" : start.Effective(slot);

            // A list of its own. Three ItemsControls sharing one collection share
            // its default view, and a ComboBox follows that view's current item —
            // so picking a driver in one row moved the other two, and the panel
            // reported edits nobody had made.
            row.Picker.ItemsSource = new List<DriverCatalogue.DriverEntry>(pool);

            // A driver who has since been swapped off the grid is no longer in
            // the list, so their name is shown as text rather than as a
            // selection — still readable, no longer offered.
            var onGrid = pool.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
            row.Picker.SelectedItem = onGrid;
            if (onGrid is null) row.Picker.Text = id.Length > 0 ? Season.Pretty(id) : "";

            row.State.Text = start is null || !available ? ""
                : onGrid is null && id.Length > 0 ? $"{Season.Pretty(id)} is not on the 2026 grid"
                : start.IsStaged(slot) ? $"staged · was {Season.Pretty(start.OnDisk(slot))}"
                : start.DiffersFromStock(slot) ? $"saved · originally {Season.Pretty(start.Stock(slot))}"
                : "stock";

            row.Reset.IsEnabled = available && start!.DiffersFromStock(slot);
        }
        _filling = false;
    }

    /// <summary>One row per slot: a label, a driver picker, what state it is in, and a reset.</summary>
    private void BuildRows()
    {
        for (var i = 0; i < StartScreen.Slots.Count; i++)
        {
            var slot = StartScreen.Slots[i];
            SlotGrid.RowDefinitions.Add(new RowDefinition());

            var label = new TextBlock
            {
                Text = slot.ToString(),
                Style = (Style)FindResource("FieldLabel"),
                Margin = new Thickness(0, 4, 10, 4),
            };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            SlotGrid.Children.Add(label);

            var picker = new ComboBox
            {
                IsEditable = true,
                IsTextSearchEnabled = true,
                DisplayMemberPath = "Display",
                // Belt and braces with the per-row list above: never let this
                // box's selection be driven by a collection view's current item.
                IsSynchronizedWithCurrentItem = false,
                Margin = new Thickness(0, 4, 0, 4),
            };
            picker.SelectionChanged += (_, _) => Picked(slot);
            Grid.SetRow(picker, i);
            Grid.SetColumn(picker, 1);
            SlotGrid.Children.Add(picker);

            var state = new TextBlock
            {
                Style = (Style)FindResource("Hint"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 4, 0, 4),
            };
            Grid.SetRow(state, i);
            Grid.SetColumn(state, 2);
            SlotGrid.Children.Add(state);

            var reset = new Button
            {
                Content = "Reset",
                Style = (Style)FindResource("RowButton"),
                IsEnabled = false,
            };
            reset.Click += (_, _) => ResetSlot(slot);
            Grid.SetRow(reset, i);
            Grid.SetColumn(reset, 3);
            SlotGrid.Children.Add(reset);

            _rows[slot] = new Row(picker, state, reset);
        }
    }

    // ── Editing ─────────────────────────────────────────────────────────────

    private void Picked(StartScreen.Slot slot)
    {
        if (_filling || _document?.Start is not { Available: true } start) return;

        // Only a person choosing counts. WPF re-raises this on its own when a
        // list is replaced or its containers are first built — which happens the
        // moment this tab is shown — and that is not an edit.
        var picker = _rows[slot].Picker;
        if (!picker.IsKeyboardFocusWithin) return;

        if (picker.SelectedItem is not DriverCatalogue.DriverEntry driver) return;
        if (driver.Id == start.Effective(slot)) return;

        start.Set(slot, driver.Id);
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        Said?.Invoke(this, new PanelMessage(
            $"Start screen, {slot.ToString().ToLowerInvariant()}: {Season.Pretty(driver.Id)}.", IsProblem: false));
    }

    /// <summary>
    /// Put one slot back to the driver the untouched original has there.
    ///
    /// That is a staged edit like any other rather than an undo, because the
    /// slot may already have been saved in an earlier session — there is nothing
    /// to undo then, only a different driver to write.
    /// </summary>
    private void ResetSlot(StartScreen.Slot slot)
    {
        if (_document?.Start is not { Available: true } start) return;

        start.Set(slot, start.Stock(slot));
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        Said?.Invoke(this, new PanelMessage(
            $"Start screen, {slot.ToString().ToLowerInvariant()}: back to {Season.Pretty(start.Stock(slot))}.",
            IsProblem: false));
    }

    private void StartResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (_document?.Start is not { Available: true } start) return;

        foreach (var slot in StartScreen.Slots) start.Set(slot, start.Stock(slot));
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        Said?.Invoke(this, new PanelMessage("Start screen: all three back to stock.", IsProblem: false));
    }
}
