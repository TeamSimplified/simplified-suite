using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using EgoEngineLibrary.Language;
using Microsoft.Win32;
using SimplifiedLanguageEditor.Core;
using SimplifiedLanguageEditor.Core.Config;
using SimplifiedLanguageEditor.Model;

namespace SimplifiedLanguageEditor;

/// <summary>
/// The window: a language file as a table of keys and values, with a filter, a
/// merge and an undo stack.
///
/// A <c>.lng</c> is a hash table rather than a list — every key is placed by the
/// hash settings in its header — so an edit is not a write at an offset. Saving
/// puts the whole table back through <see cref="LngFile"/>, which is what keeps
/// the buckets right and what the game needs in order to find a line at all.
/// </summary>
public partial class MainWindow : Window
{
    private readonly UserConfig _config = UserConfig.Load();

    private LngFile? _lng;
    private string? _lngPath;
    private readonly BulkObservableCollection<LngEntryVm> _entries = new();
    private CollectionView? _entriesView;
    private bool _isDirty;
    private bool _suppressDirty;   // true while the table is being rebuilt from a load

    // Undo stack: each entry is a full snapshot of the entry list. Small enough
    // (just strings) to afford multi-level; capped to keep memory bounded.
    private readonly Stack<List<(string Key, string Value)>> _undoStack = new();
    private const int MaxUndoLevels = 100;

    // The table as it was at the last load or save. Used to notice when an undo
    // has brought it back to what is on disk, so the "unsaved changes" mark can
    // go out rather than staying lit over a file that matches.
    private Dictionary<string, string>? _savedSnapshot;

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);

        VersionLabel.Text = Util.AppVersion.Display;
        ApplyWindowIcon();

        EntryGrid.ItemsSource = _entries;
        _entriesView = (CollectionView)CollectionViewSource.GetDefaultView(_entries);
        _entriesView.Filter = EntryFilter;
        UpdateCountLabel();
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

            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            Icon = image;
        }
        catch
        {
            // A missing or unreadable icon is not worth refusing to open over.
        }
    }

    // ────────────────────────── Opening ────────────────────────────────

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges("open another file")) return;

        var dialog = new OpenFileDialog
        {
            Title = "Open a language file",
            Filter = "Language files|*.lng|All files|*.*",
            InitialDirectory = StartingFolder(),
        };

        if (dialog.ShowDialog(this) == true) LoadLng(dialog.FileName);
    }

    private void LoadLng(string path)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _lng = new LngFile(file);
            _lngPath = path;

            RebuildEntries();
            _undoStack.Clear();
            CaptureSavedSnapshot();
            ClearDirty();
            Remember(path);
            Refresh();
            Log($"Opened {path}  ({_entries.Count} entries)");
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"That file couldn't be opened.\n\n{ex.Message}",
                "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RebuildEntries()
    {
        _suppressDirty = true;
        try
        {
            using (_entries.BeginBulkUpdate())
            {
                _entries.Clear();
                if (_lng is null) return;

                foreach (var entry in _lng.hsht.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                    _entries.Add(new LngEntryVm(entry.Key, entry.Value));
            }
        }
        finally
        {
            _suppressDirty = false;
        }

        UpdateCountLabel();
    }

    /// <summary>
    /// A blank file carrying the game's own header constants — DJB2 hashing,
    /// the LNGT/HSHS/HSHT/SIDA/SIDB/LNGB magics. The same for all eleven
    /// languages, which is what makes a new file usable by the game at all.
    /// </summary>
    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges("start a new file")) return;

        _lng = LngTemplates.NewEmpty();

        // No path yet, so Ctrl+S has nothing to overwrite and falls through to
        // Save as. Nothing on disk is at stake until it does.
        _lngPath = null;

        RebuildEntries();
        _undoStack.Clear();
        CaptureSavedSnapshot();
        ClearDirty();
        Refresh();
        Log("Started a new language file.");
    }

    // ────────────────────────── Merging ────────────────────────────────

    private enum MergeMode
    {
        /// <summary>The picked file wins where both have a key.</summary>
        Overwrite,

        /// <summary>This file wins; only keys it has not got are taken.</summary>
        Preserve,
    }

    private void MergeOverwrite_Click(object sender, RoutedEventArgs e) => Merge(MergeMode.Overwrite);

    private void MergePreserve_Click(object sender, RoutedEventArgs e) => Merge(MergeMode.Preserve);

    private void Merge(MergeMode mode)
    {
        if (_lng is null) return;

        var dialog = new OpenFileDialog
        {
            Title = mode == MergeMode.Overwrite
                ? "Merge in a file — the picked file wins"
                : "Merge in a file — this file wins",
            Filter = "Language files|*.lng|All files|*.*",
            InitialDirectory = StartingFolder(),
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            LngFile source;
            using (var file = File.Open(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                source = new LngFile(file);

            var coming = source.hsht.Entries.Select(entry => (entry.Key, entry.Value)).ToList();
            var here = _entries.ToDictionary(row => row.Key, row => row, StringComparer.Ordinal);

            PushUndo();

            var added = 0;
            var replaced = 0;
            var kept = 0;

            using (_entries.BeginBulkUpdate())
            {
                foreach (var (key, value) in coming)
                {
                    if (!here.TryGetValue(key, out var existing))
                    {
                        _entries.Add(new LngEntryVm(key, value));
                        added++;
                        continue;
                    }

                    if (mode == MergeMode.Overwrite && existing.Value != value)
                    {
                        existing.Value = value;
                        replaced++;
                    }
                    else
                    {
                        kept++;
                    }
                }
            }

            UpdateCountLabel();

            // Only if it actually changed something: a merge that found nothing
            // new has not made the file differ from what is on disk.
            if (added > 0 || replaced > 0)
            {
                if (MatchesSavedSnapshot()) ClearDirty();
                else MarkDirty();
            }

            Log($"Merged ({(mode == MergeMode.Overwrite ? "overwrite" : "preserve")}) " +
                $"{Path.GetFileName(dialog.FileName)}: +{added} added, ~{replaced} replaced, ={kept} kept " +
                $"(of {coming.Count} in that file)");
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"That file couldn't be merged in.\n\n{ex.Message}",
                "Merge failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ────────────────────────── Saving ─────────────────────────────────

    /// <summary>
    /// Overwrite the open file. Ctrl+S only — there is no button for it, because
    /// what is open is usually the game's own copy and the suite's rule is that
    /// the save icon never writes over a master copy.
    /// </summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_lng is null) return;
        if (_lngPath is null) { SaveAs_Click(sender, e); return; }

        SaveTo(_lngPath);
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_lng is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Save the language file as",
            Filter = "Language files|*.lng|All files|*.*",
            FileName = Path.GetFileName(_lngPath ?? "untitled.lng"),
            InitialDirectory = StartingFolder(),
        };

        if (dialog.ShowDialog(this) != true) return;

        SaveTo(dialog.FileName);
        _lngPath = dialog.FileName;
        Remember(dialog.FileName);
        Refresh();
    }

    private void SaveTo(string path)
    {
        if (_lng is null) return;

        try
        {
            SyncToLng();

            using var file = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _lng.Write(file);

            CaptureSavedSnapshot();
            ClearDirty();
            Remember(path);
            Refresh();
            Log($"Saved {path}  ({_entries.Count} entries)");
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"That file couldn't be saved.\n\n{ex.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Put the whole table back into the file.
    ///
    /// Every key is removed and re-added rather than the changes being worked
    /// out and applied: a key's place in the file is decided by its hash, so an
    /// edited key is not an edit at all but a removal and an insertion, and the
    /// table has to be rebuilt for the buckets to be right either way.
    /// </summary>
    private void SyncToLng()
    {
        if (_lng is null) return;

        foreach (var key in _lng.hsht.Entries.Select(entry => entry.Key).ToList())
            _lng.Remove(key, shouldResize: false);

        foreach (var row in _entries)
            _lng.Add(row.Key, row.Value, shouldResize: false);

        // One resize at the end rather than one per entry. Add does it for free
        // when asked, which on a file of nine thousand lines is nine thousand
        // rehashes of a table that is about to change again.
        if (_entries.Count > 0)
        {
            var last = _entries[^1];
            _lng.Add(last.Key, last.Value, shouldResize: true);
        }
    }

    // ────────────────────────── Rows ───────────────────────────────────

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_lng is null) return;

        if (AddRow.Show(this, key => _entries.Any(row => row.Key == key)) is not { } added) return;

        PushUndo();

        var row = new LngEntryVm(added.Key, added.Value);
        _entries.Add(row);
        EntryGrid.SelectedItem = row;
        EntryGrid.ScrollIntoView(row);

        MarkDirty();
        UpdateCountLabel();
        Log($"Added {row.Key}");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        // With cell-level selection a row appears once per selected cell, so the
        // same row can be in there twice.
        var chosen = EntryGrid.SelectedCells
            .Select(cell => cell.Item)
            .OfType<LngEntryVm>()
            .Distinct()
            .ToList();

        if (chosen.Count == 0) return;

        var answer = SimplifiedUi.MessageDialog.Choose(this, "Delete entries",
            chosen.Count == 1 ? $"Delete \"{chosen[0].Key}\"?" : $"Delete {chosen.Count} entries?",
            "The file on disk is untouched until it is saved, and Ctrl+Z brings them back.",
            ("Delete", MessageBoxResult.OK),
            ("Cancel", MessageBoxResult.Cancel));

        if (answer != MessageBoxResult.OK) return;

        PushUndo();
        foreach (var row in chosen) _entries.Remove(row);

        MarkDirty();
        UpdateCountLabel();
        Log(chosen.Count == 1 ? $"Deleted {chosen[0].Key}" : $"Deleted {chosen.Count} entries.");
    }

    private void EntryGrid_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
        => DeleteButton.IsEnabled = EntryGrid.SelectedCells
            .Select(cell => cell.Item).OfType<LngEntryVm>().Any();

    private void EntryGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        // Snapshot before the typing starts, so Ctrl+Z reaches the state before
        // the edit. Escaping out of it pops the snapshot again below.
        if (_suppressDirty) return;
        PushUndo();
    }

    private void EntryGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (_suppressDirty) return;

        if (e.EditAction == DataGridEditAction.Commit) MarkDirty();
        else if (_undoStack.Count > 0) _undoStack.Pop();   // escaped: nothing changed
    }

    // ────────────────────────── Undo ───────────────────────────────────

    private void PushUndo()
    {
        var snapshot = new List<(string Key, string Value)>(_entries.Count);
        foreach (var row in _entries) snapshot.Add((row.Key, row.Value));

        _undoStack.Push(snapshot);

        if (_undoStack.Count <= MaxUndoLevels) return;

        // Drop the oldest. A Stack has no bottom to take from, so it is rebuilt
        // from the newest MaxUndoLevels of it.
        var keep = _undoStack.Take(MaxUndoLevels).Reverse().ToList();
        _undoStack.Clear();
        foreach (var snap in keep) _undoStack.Push(snap);
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            Log("Nothing to undo.");
            return;
        }

        var snapshot = _undoStack.Pop();
        _suppressDirty = true;

        try
        {
            using (_entries.BeginBulkUpdate())
            {
                _entries.Clear();
                foreach (var (key, value) in snapshot) _entries.Add(new LngEntryVm(key, value));
            }
        }
        finally
        {
            _suppressDirty = false;
        }

        if (MatchesSavedSnapshot()) ClearDirty();
        else MarkDirty();

        UpdateCountLabel();
        Log($"Undone. ({_undoStack.Count} more)");
    }

    private void CaptureSavedSnapshot()
        => _savedSnapshot = _entries.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);

    private bool MatchesSavedSnapshot()
    {
        if (_savedSnapshot is null) return false;
        if (_savedSnapshot.Count != _entries.Count) return false;

        foreach (var row in _entries)
            if (!_savedSnapshot.TryGetValue(row.Key, out var saved) || saved != row.Value)
                return false;

        return true;
    }

    // ────────────────────────── Filter ─────────────────────────────────

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        _entriesView?.Refresh();
        UpdateCountLabel();
    }

    private bool EntryFilter(object item)
    {
        if (item is not LngEntryVm row) return false;

        var query = FilterBox.Text;
        if (string.IsNullOrWhiteSpace(query)) return true;

        return row.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.Value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateCountLabel()
    {
        var total = _entries.Count;
        var shown = _entriesView is null ? total : _entriesView.OfType<LngEntryVm>().Count();

        CountLabel.Text = total == 0 ? "" : shown == total ? $"{total} entries" : $"{shown} of {total}";
    }

    // ────────────────────────── State on screen ────────────────────────

    /// <summary>
    /// Everything that depends on whether a file is open, in one place: the
    /// buttons, the path, and what the header says.
    /// </summary>
    private void Refresh()
    {
        var open = _lng is not null;

        SaveAsButton.IsEnabled = open;
        AddButton.IsEnabled = open;
        MergeOverwriteButton.IsEnabled = open;
        MergePreserveButton.IsEnabled = open;

        FilePathLabel.Text = _lngPath ?? (open ? "Untitled" : "No file open");
        MetaPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (_lng is null) return;

        MagicLabel.Text = $"0x{_lng.magic:X8}";
        BucketsLabel.Text = _lng.hshs.Buckets.ToString();
        SeedLabel.Text = $"0x{_lng.hshs.Seed:X8}";
        MultiplierLabel.Text = $"0x{_lng.hshs.Multiplier:X8}";
    }

    private void MarkDirty()
    {
        if (_suppressDirty) return;

        _isDirty = true;
        DirtyLabel.Visibility = Visibility.Visible;
    }

    private void ClearDirty()
    {
        _isDirty = false;
        DirtyLabel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Ask about unsaved work before something that would lose it. True to go
    /// ahead.
    ///
    /// Cancel is last, so it is what Esc and the title-bar close answer — the
    /// choice that changes nothing.
    /// </summary>
    private bool ConfirmDiscardChanges(string about)
    {
        if (!_isDirty) return true;

        var answer = SimplifiedUi.MessageDialog.Choose(this, "Unsaved changes",
            "This file has changes that have not been saved.",
            $"Saving writes them out before it goes on to {about}.",
            ("Save", MessageBoxResult.Yes),
            ("Discard", MessageBoxResult.No),
            ("Cancel", MessageBoxResult.Cancel));

        switch (answer)
        {
            case MessageBoxResult.Yes:
                if (_lngPath is null) SaveAs_Click(this, new RoutedEventArgs());
                else SaveTo(_lngPath);
                return !_isDirty;   // a save that failed leaves it dirty, and stops here

            case MessageBoxResult.No:
                ClearDirty();
                return true;

            default:
                return false;
        }
    }

    // ────────────────────────── Window ─────────────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e) => EditorSettings.Show(this, _config);

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;

        var lng = dropped.FirstOrDefault(path => path.EndsWith(".lng", StringComparison.OrdinalIgnoreCase));
        if (lng is null) return;

        if (!ConfirmDiscardChanges("open the file that was dropped in")) return;
        LoadLng(lng);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Typing in a box is typing, not a shortcut — otherwise Ctrl+Z in the
        // filter undoes an edit to the table instead of the filter.
        if (Keyboard.FocusedElement is TextBox) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.O when !shift:
                Open_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;

            // Ctrl+S overwrites in place, Ctrl+Shift+S saves as. The standing
            // rule across the suite, and the reason the only button is Save as.
            case Key.S when !shift && _lng is not null:
                Save_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;

            case Key.S when shift && _lng is not null:
                SaveAs_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;

            case Key.N when !shift && AddButton.IsEnabled:
                Add_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;

            case Key.Z when !shift:
                Undo();
                e.Handled = true;
                return;
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_isDirty) return;

        var answer = SimplifiedUi.MessageDialog.Choose(this, "Unsaved changes",
            "This file has changes that have not been saved.",
            "Closing without saving throws them away.",
            ("Save", MessageBoxResult.Yes),
            ("Discard", MessageBoxResult.No),
            ("Cancel", MessageBoxResult.Cancel));

        switch (answer)
        {
            case MessageBoxResult.Yes:
                if (_lngPath is null) SaveAs_Click(this, new RoutedEventArgs());
                else SaveTo(_lngPath);

                // Still dirty means the save did not happen — either it failed
                // or the Save as was cancelled — so the window stays.
                if (_isDirty) e.Cancel = true;
                return;

            case MessageBoxResult.No:
                return;

            default:
                e.Cancel = true;
                return;
        }
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    // ────────────────────────── Odds and ends ──────────────────────────

    /// <summary>Where a picker should open: wherever the last file came from.</summary>
    private string StartingFolder()
        => Directory.Exists(_config.LastFolder) ? _config.LastFolder! : "";

    private void Remember(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || folder == _config.LastFolder) return;

        _config.LastFolder = folder;
        _config.Save();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void Log(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string>(Log), DispatcherPriority.Background, line);
            return;
        }

        LogBox.AppendText(line);
        LogBox.AppendText(Environment.NewLine);
        LogBox.ScrollToEnd();
    }
}
