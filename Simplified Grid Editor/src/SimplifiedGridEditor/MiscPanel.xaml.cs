using System.Windows;
using System.Windows.Controls;
using SimplifiedDriverSwapper.Core.Drivers;
using SimplifiedGridEditor.Core.Project;

namespace SimplifiedGridEditor;

/// <summary>
/// The Misc tab: the places the game shows three drivers or three cars outside a
/// race — the start screen, the career menu, and the featured cars.
///
/// All three behave identically, so a section is built per trio rather than
/// written out three times, and <see cref="MenuRecords"/> stays the one place
/// they are defined. Slots are laid out left to right as they appear on screen;
/// the records run the other way, which <see cref="MenuTrio"/> hides.
/// </summary>
public partial class MiscPanel : UserControl
{
    private sealed record Row(ComboBox Picker, TextBlock State, Button Reset);

    private readonly Dictionary<MenuTrio, Dictionary<MenuTrio.Slot, Row>> _rows = [];
    private readonly Dictionary<MenuTrio, Button> _resetAll = [];

    private GridDocument? _document;
    private Season _season = Season.F2026;

    /// <summary>True while the pickers are being filled, so that isn't read as an edit.</summary>
    private bool _filling;

    public MiscPanel() => InitializeComponent();

    /// <summary>Raised whenever an edit is staged, so the window can mark the project dirty.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised with a line worth putting on the status bar.</summary>
    public event EventHandler<PanelMessage>? Said;

    /// <summary>Point the panel at a project's database, or at nothing.</summary>
    public void Show(GridDocument? document, Season season)
    {
        var rebuild = !ReferenceEquals(document?.Menus, _menus);
        _document = document;
        _season = season;

        if (rebuild) BuildSections();
        Refresh();
    }

    private MenuRecords? _menus;

    public void Refresh()
    {
        var menus = _document?.Menus;

        MiscUnavailableLabel.Visibility = _document is not null && menus?.AnyAvailable != true
            ? Visibility.Visible
            : Visibility.Collapsed;

        var drivers = GridDrivers();
        var cars = MenuRecords.CarChoices();

        _filling = true;
        foreach (var (trio, rows) in _rows)
        {
            var pool = trio.Kind == TrioKind.Cars ? cars : drivers;

            foreach (var slot in MenuTrio.Slots)
            {
                var row = rows[slot];
                var id = trio.Effective(slot);

                // A list of its own per box. Three ItemsControls sharing one
                // collection share its default view, and a ComboBox follows that
                // view's current item — so picking in one row moved the others.
                var items = new List<Choice>(pool);

                // Whoever is in the slot now goes in the list even if they are no
                // longer on the grid, or the box would show nothing at all. It is
                // already the value, so having it there offers nobody new.
                var offGrid = id.Length > 0 && !items.Any(c => string.Equals(c.Id, id, StringComparison.Ordinal));
                if (offGrid) items.Insert(0, new Choice(id, Pretty(trio, id)));

                row.Picker.ItemsSource = items;
                row.Picker.SelectedItem = items.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

                row.State.Text =
                    !trio.Available ? "not in this database"
                    : offGrid ? "not on the 2026 grid"
                    : trio.IsStaged(slot) ? $"staged · was {Pretty(trio, trio.OnDisk(slot))}"
                    : trio.DiffersFromStock(slot) ? $"saved · originally {Pretty(trio, trio.Stock(slot))}"
                    : "stock";

                row.Reset.IsEnabled = trio.Available && trio.DiffersFromStock(slot);
            }

            _resetAll[trio].IsEnabled =
                trio.Available && MenuTrio.Slots.Any(s => trio.DiffersFromStock(s));
        }
        _filling = false;
    }

    /// <summary>A value as it should read: a driver's name, or a car's team.</summary>
    private static string Pretty(MenuTrio trio, string id)
    {
        if (id.Length == 0) return "";
        if (trio.Kind != TrioKind.Cars) return Season.Pretty(id);

        return MenuRecords.CarChoices().FirstOrDefault(c => c.Id == id)?.Display ?? id;
    }

    /// <summary>
    /// Who can be shown: the drivers on the 2026 grid, in grid order, as the Grid
    /// tab currently has it.
    ///
    /// Staged swaps count, which is the point — swap someone in and they can take
    /// a menu slot straight away, while whoever they replaced drops out. That is
    /// why this reads the seat plan rather than the driver catalogue, which holds
    /// all two hundred.
    ///
    /// Always the 2026 grid, whichever season the chips are on: these are single
    /// screens and do not change with them.
    /// </summary>
    private List<Choice> GridDrivers()
    {
        if (_document is null) return [];

        var plan = _season == Season.F2026
            ? _document.Seats
            : new SeatPlan(_document.Database, _document.Catalogue, Season.F2026, _document.Reference);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var drivers = new List<Choice>();

        foreach (var seat in Season.F2026.Seats)
        {
            var id = plan.Occupant(seat.Slot);
            if (id.Length == 0 || !seen.Add(id)) continue;

            var entry = _document.Catalogue.Drivers.FirstOrDefault(d => d.Id == id);
            drivers.Add(new Choice(id, entry?.Display ?? Season.Pretty(id)));
        }

        return drivers;
    }

    // ── Building the sections ───────────────────────────────────────────────

    private void BuildSections()
    {
        Sections.Children.Clear();
        _rows.Clear();
        _resetAll.Clear();
        _menus = _document?.Menus;

        if (_menus is null) return;

        foreach (var trio in _menus.All)
        {
            if (!trio.Available) continue;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var rows = new Dictionary<MenuTrio.Slot, Row>();

            for (var i = 0; i < MenuTrio.Slots.Count; i++)
            {
                var slot = MenuTrio.Slots[i];
                grid.RowDefinitions.Add(new RowDefinition());

                var label = new TextBlock
                {
                    Text = slot.ToString(),
                    Style = (Style)FindResource("FieldLabel"),
                    Margin = new Thickness(0, 4, 10, 4),
                };
                Grid.SetRow(label, i);
                grid.Children.Add(label);

                // Not editable, and no text search. Both lists are closed — a
                // driver from the grid, or one of the eleven teams — so there is
                // nothing to type. That matters beyond taste: an editable box
                // keeps matching its text against the items as containers are
                // realised, and it moved the selection on its own often enough
                // to stage edits nobody had made.
                var picker = new ComboBox
                {
                    IsEditable = false,
                    IsTextSearchEnabled = false,
                    // Never let this box's selection follow a collection view's
                    // current item either; the per-row list below is the other
                    // half of that.
                    IsSynchronizedWithCurrentItem = false,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                picker.SelectionChanged += (_, _) => Picked(trio, slot);
                Grid.SetRow(picker, i);
                Grid.SetColumn(picker, 1);
                grid.Children.Add(picker);

                var state = new TextBlock
                {
                    Style = (Style)FindResource("Hint"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 4, 0, 4),
                };
                Grid.SetRow(state, i);
                Grid.SetColumn(state, 2);
                grid.Children.Add(state);

                var reset = new Button
                {
                    Content = "Reset",
                    Style = (Style)FindResource("RowButton"),
                    IsEnabled = false,
                };
                reset.Click += (_, _) => ResetSlot(trio, slot);
                Grid.SetRow(reset, i);
                Grid.SetColumn(reset, 3);
                grid.Children.Add(reset);

                rows[slot] = new Row(picker, state, reset);
            }

            var resetAll = new Button { Content = "Reset all three", IsEnabled = false };
            resetAll.Click += (_, _) => ResetAll(trio);

            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = trio.Title,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
            });
            body.Children.Add(new TextBlock
            {
                Text = trio.Hint,
                Style = (Style)FindResource("Hint"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
            body.Children.Add(grid);
            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0),
                Children = { resetAll },
            });

            Sections.Children.Add(new Border
            {
                Style = (Style)FindResource("EditorPanel"),
                Margin = new Thickness(0, 0, 0, 14),
                Child = body,
            });

            _rows[trio] = rows;
            _resetAll[trio] = resetAll;
        }
    }

    // ── Editing ─────────────────────────────────────────────────────────────

    private void Picked(MenuTrio trio, MenuTrio.Slot slot)
    {
        if (_filling || !trio.Available) return;

        // Only a person choosing counts. WPF re-raises this on its own when a
        // list is replaced or its containers are first built — which happens the
        // moment this tab is shown — and that is not an edit.
        var picker = _rows[trio][slot].Picker;
        if (!picker.IsKeyboardFocusWithin) return;

        if (picker.SelectedItem is not Choice choice || choice.Id == trio.Effective(slot)) return;

        trio.Set(slot, choice.Id);
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        Said?.Invoke(this, new PanelMessage(
            $"{trio.Title}, {slot.ToString().ToLowerInvariant()}: {choice.Display}.", IsProblem: false));
    }

    /// <summary>
    /// Put one slot back to what the untouched original has there.
    ///
    /// That is a staged edit like any other rather than an undo, because the slot
    /// may already have been saved in an earlier session — there is nothing to
    /// undo then, only a different value to write.
    /// </summary>
    private void ResetSlot(MenuTrio trio, MenuTrio.Slot slot)
    {
        if (!trio.Available) return;

        trio.Set(slot, trio.Stock(slot));
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        Said?.Invoke(this, new PanelMessage(
            $"{trio.Title}, {slot.ToString().ToLowerInvariant()}: back to {Pretty(trio, trio.Stock(slot))}.",
            IsProblem: false));
    }

    private void ResetAll(MenuTrio trio)
    {
        if (!trio.Available) return;

        foreach (var slot in MenuTrio.Slots) trio.Set(slot, trio.Stock(slot));
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        Said?.Invoke(this, new PanelMessage($"{trio.Title}: all three back to stock.", IsProblem: false));
    }
}
