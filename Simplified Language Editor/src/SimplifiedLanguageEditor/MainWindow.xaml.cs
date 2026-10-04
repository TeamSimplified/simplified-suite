using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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

        ApplyLogVisibility(_config.ShowLog);
        ApplyTableSelection();
        ApplyColumnRule();
        ApplySplit(_config.KeyShare);

        // The gripper in each column header is a Thumb, and its DragCompleted
        // bubbles out to here. There is no "a column was resized" event on
        // DataGrid, and this is the moment the drag is finished rather than
        // every pixel of it.
        EntryGrid.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(Column_DragCompleted), handledEventsToo: true);

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

    // ────────────────────────── Comparing ──────────────────────────────

    /// <summary>
    /// Write out what this file says that another one does not: the entries
    /// whose text has changed, and the entries it adds.
    ///
    /// The file that is picked is the one being compared against — the game's
    /// own, usually — and what comes out is this file's work, which is what a
    /// language mod is. A whole file is six megabytes of mostly the game's own
    /// writing; the part somebody did is a few dozen lines, and a mod of only
    /// those still applies after the game has been patched.
    /// </summary>
    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_lng is null) return;

        if (CompareDialog.Show(this, StartingFolder(), _lngPath ?? "This file") is not { } asked) return;

        try
        {
            LngFile theirs;
            using (var file = File.Open(asked.Against, FileMode.Open, FileAccess.Read, FileShare.Read))
                theirs = new LngFile(file);

            // The table is the truth here, not the file as it was opened: a
            // comparison that ignored the edits on screen would be comparing
            // somebody's work against itself.
            SyncToLng();

            // Whichever way round it was asked for: the source is the file whose
            // text is wanted, and the baseline is the one it is measured
            // against. Everything below is the same either way.
            var source = asked.FromThisFile ? _lng : theirs;
            var baseline = asked.FromThisFile ? theirs : _lng;

            var difference = LngCompare.Measure(source, baseline);

            if (difference.Total == 0)
            {
                var which = asked.FromThisFile ? "this file" : Path.GetFileName(asked.Against);
                var other = asked.FromThisFile ? Path.GetFileName(asked.Against) : "this file";

                SimplifiedUi.MessageDialog.Show(this,
                    $"Nothing to export.{Environment.NewLine}{Environment.NewLine}" +
                    $"Every line {which} has, {other} has as well, word for word.",
                    "No differences", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Named after the file the text is coming out of, because that is
            // what is in it.
            var from = asked.FromThisFile ? _lngPath ?? "untitled" : asked.Against;
            var suggested = Path.GetFileNameWithoutExtension(from) + " - changes.lng";

            var saver = new SaveFileDialog
            {
                Title = "Save the differences as",
                Filter = "Language files|*.lng|All files|*.*",
                FileName = suggested,
                InitialDirectory = StartingFolder(),
            };

            if (saver.ShowDialog(this) != true) return;

            var subset = LngCompare.Extract(source, baseline);

            using (var output = File.Open(saver.FileName, FileMode.Create, FileAccess.Write, FileShare.Read))
                subset.Write(output);

            Remember(saver.FileName);

            Log($"Compared with {Path.GetFileName(asked.Against)}, keeping " +
                $"{(asked.FromThisFile ? "this file's" : "its")} text: " +
                $"{difference.Changed} changed, {difference.Added} new " +
                $"~ wrote {difference.Total} to {Path.GetFileName(saver.FileName)}");

            SimplifiedUi.MessageDialog.Show(this,
                Summary(difference, Path.GetFileName(saver.FileName), asked, theirs.Count),
                "Compared", MessageBoxButton.OK);
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"That comparison couldn't be done.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Compare failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// What the comparison came to, as the message dialog wants it: a headline,
    /// a blank line, then the detail.
    ///
    /// Both numbers are given even when one of them is zero. "3 changed" alone
    /// leaves the reader working out whether nothing was added or whether the
    /// app does not count that — and the whole point of the dialog is to say
    /// what went into the file without opening it.
    /// </summary>
    private string Summary(Difference difference, string saved, CompareChoice asked, int theirs)
    {
        var entries = difference.Total == 1 ? "entry" : "entries";
        var against = Path.GetFileName(asked.Against);
        var blank = Environment.NewLine + Environment.NewLine;

        // Which way round it was run, in the sentence rather than implied: the
        // two exports are different files and look alike afterwards.
        var side = asked.FromThisFile
            ? $"Holding this file's text, against {against}."
            : $"Holding {against}'s text, against this file.";

        return $"Saved {difference.Total:N0} {entries} to {saved}" + blank +
               $"{difference.Changed:N0} rewritten, {difference.Added:N0} new." + Environment.NewLine +
               side + Environment.NewLine +
               $"{_entries.Count:N0} entries here, {theirs:N0} there.";
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
        CloseFileButton.IsEnabled = open;
        AddButton.IsEnabled = open;
        MergeOverwriteButton.IsEnabled = open;
        MergePreserveButton.IsEnabled = open;
        CompareButton.IsEnabled = open;

        ShowWhatIsOpen();
    }

    /// <summary>
    /// What is open, said three times over: the name in the title bar, the whole
    /// path on hovering it, and the name again in the window's own title, which
    /// is what the taskbar and Alt-Tab show.
    ///
    /// The name rather than the path, as every other app in the suite has it: a
    /// path is mostly folders somebody already knows they are in, and the one
    /// word that tells two language files apart is at the end of it.
    /// </summary>
    private void ShowWhatIsOpen()
    {
        const string app = "Simplified Language Editor";

        if (_lng is null)
        {
            FilePathLabel.Text = "No file open";
            FilePathLabel.SetResourceReference(ForegroundProperty, "TextDisabled");
            FilePathLabel.ToolTip = null;
            Title = app;
            return;
        }

        var name = _lngPath is null ? "Untitled" : Path.GetFileName(_lngPath);

        FilePathLabel.Text = name;

        // A resource reference rather than the brush itself: looking the key up
        // and assigning what comes back freezes the colour the palette had at
        // that moment, and the name then stays dark on a window that has since
        // gone dark. This is what DynamicResource does, from code.
        FilePathLabel.SetResourceReference(ForegroundProperty, "TextPrimary");
        FilePathLabel.ToolTip = _lngPath;
        Title = _isDirty ? $"{app} — {name} *" : $"{app} — {name}";
    }

    /// <summary>
    /// Mark a selected cell in the accent blue rather than the suite's grey.
    ///
    /// Grey is right where selection is incidental; here it is the whole point
    /// of the table — a cell is picked to edit it or to delete its row, and on
    /// a page of grey rows a grey mark is the hardest thing to find.
    ///
    /// Put in the grid's own resources rather than the app's, so it is this
    /// table that reads differently and not every list in the window. In code
    /// rather than in the markup because the brush is the palette's, and a
    /// palette key cannot be aliased to another one in markup — it has to be
    /// looked up and put there, and looked up again when the palette changes.
    /// </summary>
    private void ApplyTableSelection()
    {
        if (Application.Current?.TryFindResource("SelectedAccent") is System.Windows.Media.Brush blue)
            EntryGrid.Resources["Selected"] = blue;
    }

    /// <summary>
    /// The hairline between the two columns.
    ///
    /// Fainter than the lines between rows, which are the palette's BorderSoft
    /// at full strength: a key and its value are one line of the file, and a
    /// divider as strong as the one separating rows would cut each line in two
    /// before it said which cell was which. Half strength is enough to follow
    /// the column down and little enough to stop reading as a border.
    ///
    /// Taken from the palette and faded rather than written as a colour, so it
    /// is still the theme's line in either palette, and re-made when the
    /// palette changes.
    /// </summary>
    private void ApplyColumnRule()
    {
        if (Application.Current?.TryFindResource("BorderSoft") is not SolidColorBrush line) return;

        // A frozen brush cannot be given an opacity, and the palette's are
        // frozen: this is a copy of it, faded.
        var faint = new SolidColorBrush(line.Color) { Opacity = 0.45 };

        EntryGrid.VerticalGridLinesBrush = faint;

        // And the same one through the header, which draws its own edge rather
        // than taking the grid's lines — so without this the rule would start
        // under the headings instead of at the top of the table.
        EntryGrid.Resources["ColumnRule"] = faint;
    }

    /// <summary>
    /// Set the split between the two columns, as the key column's share of the
    /// table.
    ///
    /// Stars rather than widths: a star column is a proportion of whatever the
    /// table has, so the split survives the window being resized, maximised or
    /// opened on another screen. Kept away from nothing at either end, because a
    /// column dragged to zero cannot be dragged back — its gripper goes with it.
    /// </summary>
    private void ApplySplit(double keyShare)
    {
        var key = Math.Clamp(keyShare, 0.15, 0.85);

        EntryGrid.Columns[0].Width = new DataGridLength(key, DataGridLengthUnitType.Star);
        EntryGrid.Columns[1].Width = new DataGridLength(1 - key, DataGridLengthUnitType.Star);
    }

    /// <summary>
    /// A column has been dragged. WPF resizes by setting a width in pixels,
    /// which loses the proportion — so the new widths are read back and put
    /// in as stars again, and the share is remembered.
    /// </summary>
    private void Column_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var key = EntryGrid.Columns[0].ActualWidth;
        var value = EntryGrid.Columns[1].ActualWidth;

        if (key <= 0 || value <= 0) return;

        var share = key / (key + value);
        ApplySplit(share);

        if (Math.Abs(share - _config.KeyShare) < 0.005) return;

        _config.KeyShare = Math.Clamp(share, 0.15, 0.85);
        _config.Save();
    }

    /// <summary>
    /// Show or hide the log. Collapsed rather than hidden, so the table takes
    /// the room back rather than leaving a gap where the log would have been.
    /// </summary>
    private void ApplyLogVisibility(bool show)
        => LogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    private void MarkDirty()
    {
        if (_suppressDirty) return;

        _isDirty = true;
        DirtyLabel.Visibility = Visibility.Visible;
        ShowWhatIsOpen();
    }

    private void ClearDirty()
    {
        _isDirty = false;
        DirtyLabel.Visibility = Visibility.Collapsed;
        ShowWhatIsOpen();
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

    /// <summary>
    /// Put the file down. The app stays where it is — this is the file being
    /// closed, not the window.
    /// </summary>
    private void CloseFile_Click(object sender, RoutedEventArgs e)
    {
        if (_lng is null) return;
        if (!ConfirmDiscardChanges("close it")) return;

        var was = _lngPath;

        _lng = null;
        _lngPath = null;
        _savedSnapshot = null;
        _undoStack.Clear();

        RebuildEntries();
        ClearDirty();
        Refresh();

        Log(was is null ? "Closed the new file." : $"Closed {was}");
    }

    // ────────────────────────── Window ─────────────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!EditorSettings.Show(this, _config)) return;

        ApplyLogVisibility(_config.ShowLog);

        // The palette may have changed with it, and both of these are the
        // palette's.
        ApplyTableSelection();
        ApplyColumnRule();
    }

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
