using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SimplifiedDatabaseMerger.Core;
using SimplifiedDatabaseMerger.Core.Config;

namespace SimplifiedDatabaseMerger;

/// <summary>
/// The window: a clean database and two edited ones in, one merged database
/// out.
///
/// The merge is worked out as soon as all three are in, so what it will do is
/// on screen before anything is written: what each mod changed, and every
/// record the two changed differently, each with its own choice. Saving only
/// writes what is already showing.
/// </summary>
public partial class MainWindow : Window
{
    private readonly UserConfig _config = UserConfig.Load();

    /// <summary>Clean, first, second — the order of the rows.</summary>
    private readonly LoadedDatabase?[] _loaded = new LoadedDatabase?[3];

    private MergePlan? _plan;

    /// <summary>
    /// Only one edited database is in. The plan then stands the clean one in
    /// for the missing one, so its changes can be looked through before there
    /// is anything to merge them with — nothing to choose, nothing to save.
    /// </summary>
    private bool _partial;
    private List<ConflictRow> _rows = [];

    /// <summary>Loading or saving. Every input waits for it, so nothing changes under the work.</summary>
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);

        VersionLabel.Text = Util.AppVersion.Display;
        ApplyWindowIcon();

        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => Save_Click(this, new RoutedEventArgs())),
            Key.S, ModifierKeys.Control | ModifierKeys.Shift));

        Refresh();
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

    // ── Choosing ────────────────────────────────────────────────────────────

    private static readonly string[] SlotNames = ["clean", "first", "second"];

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var slot)) return;

        var picker = new OpenFileDialog
        {
            Title = $"Choose the {SlotNames[slot]} database",
            Filter = "Game database or mod zip (*.bin;*.bak;*.zip)|*.bin;*.bak;*.zip|All files (*.*)|*.*",
            InitialDirectory = _config.LastInputFolder is { } dir && Directory.Exists(dir) ? dir : "",
        };

        if (picker.ShowDialog(this) != true) return;

        _config.LastInputFolder = Path.GetDirectoryName(picker.FileName);
        _config.Save();
        _ = Choose(slot, picker.FileName);
    }

    /// <summary>
    /// The game's own database as the clean one, from the game folder in
    /// Settings. Asked first, as every app in the suite asks before it takes
    /// anything from the game: whatever the game's file holds becomes the base
    /// of the merged one, including any mod still installed over it.
    /// </summary>
    private void FromGame_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var found = DatabaseInput.FindInGame(_config.GameDir);
        if (found is null)
        {
            SimplifiedUi.MessageDialog.Show(this,
                "There's no game database to take.\n\n" +
                (string.IsNullOrWhiteSpace(_config.GameDir)
                    ? "Set the game folder in Settings first, or browse to a clean database."
                    : $"Nothing called gamedatabases*.bin was found under {_config.GameDir}."),
                "From game", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!SimplifiedUi.GameFilesNotice.Confirm(this)) return;
        _ = Choose(0, found);
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = _busy ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>A file dropped on a row goes to that row.</summary>
    private void Slot_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_busy || sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var slot)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = Choose(slot, files[0]);
    }

    /// <summary>
    /// Files dropped anywhere else fill the empty rows in order, so dragging
    /// all three in at once from the clean one down does what it looks like.
    /// </summary>
    protected override async void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (e.Handled || _busy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        e.Handled = true;

        foreach (var file in files)
        {
            var slot = Array.FindIndex(_loaded, d => d is null);
            if (slot < 0) break;
            await Choose(slot, file);
        }
    }

    /// <summary>Read one database into its row, then work the merge out again if all three are in.</summary>
    private async Task Choose(int slot, string path)
    {
        if (_busy) return;
        _busy = true;
        _loaded[slot] = null;
        Box(slot).Text = path;
        Say(slot, "Reading…");
        Forget();
        Refresh();

        try
        {
            var loaded = await Task.Run(() => DatabaseInput.Load(path));
            _loaded[slot] = loaded;
            Say(slot, $"{loaded.Game} · {loaded.Database.Records.Count:N0} records" +
                      (loaded.Entry is null ? "" : $" · {loaded.Entry}"));
        }
        catch (Exception ex)
        {
            Say(slot, ex is InvalidDataException ? ex.Message : $"Couldn't read it: {ex.Message}", danger: true);
        }

        _busy = false;
        await Replan();
    }

    // ── The merge ───────────────────────────────────────────────────────────

    /// <summary>
    /// Work the merge out from what is loaded, and show it. Choices made on an
    /// earlier plan go with it: a different file is different conflicts.
    /// </summary>
    private async Task Replan()
    {
        Forget();

        if (_loaded is not [{ } clean, var first, var second] || (first is null && second is null))
        {
            Refresh();
            return;
        }

        _partial = first is null || second is null;

        if (DatabaseMerge.Check(clean, first, second) is { } problem)
        {
            Summary("These can't be merged", problem, danger: true);
            Refresh();
            return;
        }

        _busy = true;
        Summary("Comparing…", _partial
            ? "Every record of the edited database against the clean one."
            : "Every record of both edited databases against the clean one.");
        Refresh();

        try
        {
            // Compared with itself, the clean database changes nothing, which is
            // what makes it a stand-in for the database not chosen yet.
            var a = first?.Database ?? clean.Database;
            var b = second?.Database ?? clean.Database;
            _plan = await Task.Run(() => DatabaseMerge.Plan(clean.Database, a, b));
        }
        catch (Exception ex)
        {
            Summary("The databases couldn't be compared", ex.Message, danger: true);
        }

        _busy = false;

        if (_plan is { } plan)
        {
            if (first is not null) Say(1, Describe(first, plan.First));
            if (second is not null) Say(2, Describe(second, plan.Second));
            _rows = plan.Conflicts.Select(c => new ConflictRow(c)).ToList();
            ConflictGrid.ItemsSource = _rows;
            FirstGrid.ItemsSource = plan.First.List.Select(c => new ChangeRow(c, Keep.First, plan)).ToList();
            SecondGrid.ItemsSource = plan.Second.List.Select(c => new ChangeRow(c, Keep.Second, plan)).ToList();

            ConflictsTab.Header = $"Conflicts ({plan.Conflicts.Count:N0})";
            FirstTab.Header = $"First database ({plan.First.Total:N0})";
            SecondTab.Header = $"Second database ({plan.Second.Total:N0})";

            // A tab only for what is in. With one database there is nothing to
            // conflict with and no merge for the last column to describe.
            ConflictsTab.Visibility = _partial ? Visibility.Collapsed : Visibility.Visible;
            FirstTab.Visibility = first is null ? Visibility.Collapsed : Visibility.Visible;
            SecondTab.Visibility = second is null ? Visibility.Collapsed : Visibility.Visible;
            var merging = _partial ? Visibility.Collapsed : Visibility.Visible;
            FirstGrid.Columns[^1].Visibility = merging;
            SecondGrid.Columns[^1].Visibility = merging;

            // Opened where there is something to do: the conflicts when there
            // are any, otherwise the first database's changes.
            ResultTabs.SelectedItem = plan.Conflicts.Count > 0 ? ConflictsTab
                : first is not null && (plan.First.Total > 0 || second is null) ? FirstTab : SecondTab;

            if (_partial) SummariseOne(first is not null ? "first" : "second", first is not null ? plan.First : plan.Second);
            else Summarise(plan, first!, second!);
        }

        Refresh();
    }

    private static string Describe(LoadedDatabase loaded, Changes changes) =>
        changes.Total == 0
            ? $"{loaded.Game} · no changes from the clean database"
            : $"{loaded.Game} · {changes.Changed:N0} changed · {changes.Added:N0} added · {changes.Removed:N0} removed";

    private void SummariseOne(string which, Changes changes)
    {
        var other = which == "first" ? "second" : "first";

        if (changes.Total == 0)
            Summary("No changes", $"The {which} database is the same as the clean one. Choose the {other} database to merge.");
        else
            Summary($"{changes.Total:N0} change{(changes.Total == 1 ? "" : "s")} in the {which} database",
                $"Compared with the clean one. Choose the {other} database to merge the two.");
    }

    private void Summarise(MergePlan plan, LoadedDatabase first, LoadedDatabase second)
    {
        var same = string.Equals(Path.GetFullPath(first.Path), Path.GetFullPath(second.Path), StringComparison.OrdinalIgnoreCase)
                   && first.Entry == second.Entry;

        var agreed = plan.Agreed == 0 ? "" :
            $" {plan.Agreed:N0} change{(plan.Agreed == 1 ? "" : "s")} both made the same way {(plan.Agreed == 1 ? "goes" : "go")} in once.";

        if (plan.NothingToMerge)
            Summary("Nothing to merge", "Neither database changes anything from the clean one.");
        else if (plan.Conflicts.Count == 0)
            Summary("No conflicts",
                $"Every change from both fits: {plan.First.Total + plan.Second.Total - plan.Agreed:N0} in all.{agreed}" +
                (same ? " The first and second are the same file." : ""));
        else
            Summary($"{plan.Conflicts.Count:N0} conflict{(plan.Conflicts.Count == 1 ? "" : "s")}",
                $"Both databases changed {(plan.Conflicts.Count == 1 ? "this record" : "these records")} differently. " +
                "Choose which version each keeps — the first database's is kept unless you change it. " +
                $"Everything else goes in as it is.{agreed}");
    }

    /// <summary>
    /// A change tab coming to the front reads its "In the merge" column again,
    /// since the conflicts may have been answered differently in the meantime.
    /// </summary>
    private void ResultTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The grids and the Keep boxes inside the tabs raise this too, on its
        // way up; only the tabs themselves changing matters here.
        if (!ReferenceEquals(e.OriginalSource, ResultTabs)) return;

        if (ResultTabs.SelectedItem == FirstTab) FirstGrid.Items.Refresh();
        else if (ResultTabs.SelectedItem == SecondTab) SecondGrid.Items.Refresh();

        Refresh();
    }

    private void KeepAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<Keep>(tag, out var keep)) return;
        foreach (var row in _rows) row.Keep = keep;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _plan is not { } plan || _loaded[0] is not { } clean) return;

        // Named as the game names it whatever the clean one was picked as — a
        // .bak taken by one of the editors is still the database the game
        // wants called .bin.
        var name = Path.ChangeExtension(Path.GetFileName(clean.Entry ?? clean.Path), ".bin");

        var picker = new SaveFileDialog
        {
            Title = "Save the merged database",
            Filter = "Game database (*.bin)|*.bin",
            FileName = name,
            InitialDirectory = _config.LastOutputFolder is { } dir && Directory.Exists(dir) ? dir : "",
        };

        if (picker.ShowDialog(this) != true) return;
        var target = picker.FileName;

        // The picker has already asked about replacing a file. Being one of the
        // three inputs is a different question: the clean one in particular is
        // the only unedited copy anybody has pointed this app at.
        if (_loaded.OfType<LoadedDatabase>().Any(d => d.Entry is null &&
                string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            && SimplifiedUi.MessageDialog.Choose(this, "Save",
                   "That's one of the databases being merged.",
                   "Saving here replaces it with the merged one, and the original is gone.",
                   ("Replace it", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel)) != MessageBoxResult.OK)
            return;

        _config.LastOutputFolder = Path.GetDirectoryName(target);
        _config.Save();

        _busy = true;
        Refresh();
        MergeButton.Content = "Saving…";

        try
        {
            var count = await Task.Run(() =>
            {
                var merged = plan.Build();
                merged.SaveAtomic(target);
                return merged.Records.Count;
            });

            SimplifiedUi.MessageDialog.Show(this,
                $"Merged database saved.\n\n{target}\n\n{count:N0} records.",
                "Save", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"The merged database couldn't be saved.\n\n{ex.Message}",
                "Save", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    // ── State ───────────────────────────────────────────────────────────────

    /// <summary>Drop the plan and its conflicts, for a merge that no longer matches what is loaded.</summary>
    private void Forget()
    {
        _plan = null;
        _partial = false;
        _rows = [];
        ConflictGrid.ItemsSource = null;
        FirstGrid.ItemsSource = null;
        SecondGrid.ItemsSource = null;
    }

    private void Refresh()
    {
        var ready = _plan is not null && !_busy;
        MergeButton.IsEnabled = ready && !_partial && !_plan!.NothingToMerge;
        MergeButton.Content = "Save merged database";

        foreach (var button in new[] { FromGameButton, BrowseCleanButton, BrowseFirstButton, BrowseSecondButton, SettingsButton })
            button.IsEnabled = !_busy;

        var results = _plan is { NothingToMerge: false };
        var conflicts = _plan is { Conflicts.Count: > 0 };
        ResultTabs.Visibility = results ? Visibility.Visible : Visibility.Collapsed;
        NoConflictsNote.Visibility = conflicts ? Visibility.Collapsed : Visibility.Visible;

        // Only beside the conflicts: on a change tab the buttons would set
        // choices nobody can see.
        AllPanel.Visibility = conflicts && !_busy && ResultTabs.SelectedItem == ConflictsTab
            ? Visibility.Visible : Visibility.Collapsed;

        EmptyNote.Visibility = results ? Visibility.Collapsed : Visibility.Visible;
        EmptyNote.Text = _plan is null ? "Drop databases on the rows above, or browse to them." : "";

        if (_plan is null && !_busy && _loaded.Any(d => d is null))
            Summary("Choose the three databases",
                "The clean one, then the two edited ones. Every change either of them made is put into a fresh " +
                "copy of the clean one; where both changed the same record differently, you choose.");
    }

    private TextBox Box(int slot) => slot switch { 0 => CleanBox, 1 => FirstBox, _ => SecondBox };

    private void Say(int slot, string text, bool danger = false)
    {
        var info = slot switch { 0 => CleanInfo, 1 => FirstInfo, _ => SecondInfo };
        info.Text = text;
        info.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Danger" : "TextMuted");
    }

    private void Summary(string headline, string detail, bool danger = false)
    {
        SummaryHeadline.Text = headline;
        SummaryHeadline.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Danger" : "TextPrimary");
        SummaryDetail.Text = detail;
    }

    // ── Window ──────────────────────────────────────────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !MergerSettings.Show(this, _config)) return;
        _config.Save();
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>A shortcut's command, for the one key binding.</summary>
    private sealed class RelayCommand(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}
