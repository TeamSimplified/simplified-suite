using System.Windows;
using System.Windows.Controls;
using SimplifiedDriverSwapper.Core.Drivers;
using SimplifiedGridEditor.Core.Project;

namespace SimplifiedGridEditor;

/// <summary>
/// The Drivers tab: every driver record in the database, and the seven identity
/// fields on whichever is selected.
///
/// The three name fields hold language keys rather than names — the game
/// resolves them through the project's .lng files — which is why the pickers
/// offer what is already in use rather than a free-text box alone.
/// </summary>
public partial class DriversPanel : UserControl
{
    private GridDocument? _document;

    /// <summary>The editors, built once from the field list.</summary>
    private readonly Dictionary<string, ComboBox> _editors = [];

    public DriversPanel()
    {
        InitializeComponent();
        BuildEditors();
    }

    /// <summary>Raised whenever an edit is staged, so the window can mark the project dirty.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised with a line worth putting on the status bar.</summary>
    public event EventHandler<PanelMessage>? Said;

    /// <summary>Point the panel at a project's database, or at nothing.</summary>
    public void Show(GridDocument? document)
    {
        _document = document;

        if (document is not null)
        {
            var pools = DriverAttributes.ValuePools(document.Drivers);
            foreach (var (field, editor) in _editors)
                editor.ItemsSource = pools.GetValueOrDefault(field, []);
        }
        else
        {
            foreach (var editor in _editors.Values) editor.ItemsSource = null;
        }

        Refresh();
    }

    public void Refresh()
    {
        RefreshTable();
        RefreshDetail();
    }

    /// <summary>
    /// Lay the seven identity fields out two columns wide. Built in code so the
    /// field list stays the single source — adding one here needs no XAML.
    /// </summary>
    private void BuildEditors()
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
            Grid.SetRow(label, row);
            Grid.SetColumn(label, column);
            AttributeFieldGrid.Children.Add(label);

            // Editable so a key that isn't in use yet can still be typed.
            var editor = new ComboBox
            {
                IsEditable = true,
                IsTextSearchEnabled = true,
                Margin = new Thickness(0, 3, 0, 3),
            };
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, column + 1);
            AttributeFieldGrid.Children.Add(editor);

            _editors[field] = editor;
        }
    }

    // ── The table ───────────────────────────────────────────────────────────

    private int? SelectedIndex => (DriverGrid.SelectedItem as DriverRow)?.Index;

    private void AttributeSearch_Changed(object sender, TextChangedEventArgs e) => RefreshTable();

    private void RefreshTable()
    {
        var selected = SelectedIndex;
        var query = AttributeSearchBox.Text.Trim();
        var rows = new List<DriverRow>();

        foreach (var driver in _document?.Drivers ?? [])
        {
            var values = Effective(driver);

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
                IsChanged = _document?.PendingAttributes.ContainsKey(driver.Index) == true,
            });
        }

        DriverGrid.ItemsSource = rows;
        if (selected is not null)
            DriverGrid.SelectedItem = rows.FirstOrDefault(r => r.Index == selected.Value);
    }

    /// <summary>A driver's fields with any unsaved edits laid over what's on disk.</summary>
    private Dictionary<string, string> Effective(DriverAttributes.DriverRow driver)
    {
        var values = new Dictionary<string, string>(driver.Values, StringComparer.Ordinal);
        if (_document?.PendingAttributes.TryGetValue(driver.Index, out var pending) == true)
            foreach (var (field, value) in pending) values[field] = value;
        return values;
    }

    private void DriverGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDetail();

    private void RefreshDetail()
    {
        var index = SelectedIndex;
        var driver = index is null ? null : _document?.Drivers.FirstOrDefault(d => d.Index == index.Value);

        AttributeApplyButton.IsEnabled = driver is not null;
        AttributeRevertButton.IsEnabled =
            driver is not null && _document?.PendingAttributes.ContainsKey(driver.Index) == true;

        if (driver is null)
        {
            DriverSelectionLabel.Text = "No driver selected";
            foreach (var editor in _editors.Values) editor.Text = "";
            return;
        }

        DriverSelectionLabel.Text = $"driver_{driver.Index} · {driver.DriverId} · {Season.Pretty(driver.DriverId)}";

        var values = Effective(driver);
        foreach (var (field, editor) in _editors) editor.Text = values.GetValueOrDefault(field, "");
    }

    // ── Editing ─────────────────────────────────────────────────────────────

    private void AttributeApply_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || SelectedIndex is not { } index) return;
        var driver = _document.Drivers.FirstOrDefault(d => d.Index == index);
        if (driver is null) return;

        var proposed = _editors.ToDictionary(kv => kv.Key, kv => kv.Value.Text.Trim(), StringComparer.Ordinal);

        if (DriverAttributes.Validate(proposed) is { } problem)
        {
            Said?.Invoke(this, new PanelMessage(problem, IsProblem: true));
            return;
        }

        var changed = proposed
            .Where(kv => kv.Value != driver[kv.Key])
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        string message;
        if (changed.Count == 0)
        {
            _document.PendingAttributes.Remove(index);
            message = $"driver_{index}: nothing to change.";
        }
        else
        {
            _document.PendingAttributes[index] = changed;
            message = $"driver_{index} ({driver.DriverId}): {changed.Count} field(s) staged.";
        }

        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        Said?.Invoke(this, new PanelMessage(message, IsProblem: false));
    }

    private void AttributeRevert_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || SelectedIndex is not { } index) return;

        _document.PendingAttributes.Remove(index);
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        Said?.Invoke(this, new PanelMessage($"driver_{index}: staged changes discarded.", IsProblem: false));
    }
}
