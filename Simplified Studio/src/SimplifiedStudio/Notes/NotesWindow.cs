using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SimplifiedStudio.Core.Notes;
using SimplifiedStudio.Core.Project;

namespace SimplifiedStudio.Notes;

/// <summary>
/// The project's notes and moodboards, in one window.
///
/// Notes are written and read here — the point of an editor in the app rather
/// than handing <c>.md</c> to whatever the system opens is that a project
/// claiming to be the one place the mod lives shouldn't send you to Notepad.
/// Boards are opened in PureRef, which owns that format; this window only keeps
/// them, names them, and notices when PureRef has saved.
/// </summary>
public sealed class NotesWindow : Window
{
    private readonly SimplifiedProject _project;

    private readonly ListBox _notes = new() { DisplayMemberPath = "Name" };
    private readonly ListBox _boards = new() { DisplayMemberPath = "Name" };

    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12.5,

        // Always visible in its own right: the pane around it is what appears
        // and disappears, so hiding the box as well would leave editing showing
        // nothing but the syntax line.
        // The suite's TextBox style centres its content — right for a one-line
        // field, wrong for a page of writing, which has to start at the top.
        VerticalContentAlignment = VerticalAlignment.Top,
        Padding = new Thickness(8, 6, 8, 6),
    };

    /// <summary>
    /// The editor and the syntax line under it.
    ///
    /// The line exists because the checkbox is the reason this editor was
    /// written at all, and nothing on screen said the syntax for one — a note
    /// starts blank, so a reader who doesn't already know Markdown never finds
    /// the feature the whole thing was justified by.
    /// </summary>
    private readonly DockPanel _editorPane = new() { Visibility = Visibility.Collapsed };

    private readonly MarkdownView _preview = new();
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _subtitle = new() { FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Button _edit = new() { Content = "Edit", Padding = new Thickness(14, 5, 14, 5) };
    private readonly Button _rename = new() { Content = "Rename", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _delete = new() { Content = "Delete", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 0, 0, 0) };
    private readonly Panel _boardPane;
    private readonly TextBlock _boardBlurb = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };

    private FileSystemWatcher? _watcher;

    /// <summary>The note on screen, or null when a board is selected or nothing is.</summary>
    private ProjectWorkspace.Item? _openNote;

    /// <summary>Set whenever anything is written, so the caller knows to mark the project dirty.</summary>
    public bool Changed { get; private set; }

    /// <summary>
    /// Something was written. Raised as it happens rather than saved up for
    /// closing time, because the window is modeless and may never close before
    /// the user reaches for Save.
    /// </summary>
    public event EventHandler? ContentChanged;

    /// <summary>
    /// Ctrl+S was pressed here. The window can write a note into the project's
    /// temp folder but has no business writing the .simplified itself, so it
    /// asks the window that owns the project to do it.
    /// </summary>
    public event EventHandler? SaveRequested;

    private void MarkChanged()
    {
        Changed = true;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    public NotesWindow(SimplifiedProject project, Window owner)
    {
        _project = project;

        Title = $"Notes and boards — {project.DisplayName}";
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 1040;
        Height = 700;
        MinWidth = 720;
        MinHeight = 460;
        ShowInTaskbar = false;

        _boardPane = BuildBoardPane();
        Content = BuildLayout();

        SimplifiedUi.DialogChrome.Apply(this);

        _notes.SelectionChanged += (_, _) => { if (_notes.SelectedItem is not null) { _boards.SelectedItem = null; ShowNote(); } };
        _boards.SelectionChanged += (_, _) => { if (_boards.SelectedItem is not null) { SaveOpenNote(); _notes.SelectedItem = null; ShowBoard(); } };

        _editor.TextChanged += (_, _) => _preview.Source = _editor.Text;
        _preview.TaskToggled += TickTask;
        _edit.Click += (_, _) => SetEditing(_editorPane.Visibility != Visibility.Visible);

        Refresh();
        WatchBoards();

        Closing += (_, _) =>
        {
            SaveOpenNote();
            _watcher?.Dispose();

            // Say where activation goes before this window is gone. An owned
            // window that isn't in the taskbar leaves Windows to pick the next
            // one on its own, and what it picks is something behind Studio —
            // which reads as the whole app minimising when notes are closed.
            Owner?.Activate();
        };

        // Ctrl+S here means the same as Ctrl+S anywhere else in the app. It has
        // to be handled rather than left to bubble: this is its own window, so
        // nothing of the main one's keyboard handling reaches it — and it must
        // work while typing in the editor, which is exactly when it is wanted.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.S) return;
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;

            SaveOpenNote();
            SaveRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    private UIElement BuildLayout()
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });

        // Left: the two lists, notes above boards, each with its own New.
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        left.Children.Add(SectionHeader("Notes", "New note", NewNote, 0));
        Grid.SetRow(_notes, 1);
        _notes.Margin = new Thickness(0, 0, 0, 14);
        left.Children.Add(_notes);

        left.Children.Add(SectionHeader("Boards", "New board", NewBoard, 2));
        Grid.SetRow(_boards, 3);
        left.Children.Add(_boards);

        _boards.MouseDoubleClick += (_, _) => OpenBoard();

        grid.Children.Add(left);

        // Right: whichever pane the selection calls for.
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_edit);
        buttons.Children.Add(_rename);
        buttons.Children.Add(_delete);
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);

        var titles = new StackPanel();
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        header.Children.Add(titles);

        right.Children.Add(header);

        _editorPane.Children.Add(SyntaxLine());
        _editorPane.Children.Add(_editor);

        var body = new Grid();
        body.Children.Add(_preview);
        body.Children.Add(_editorPane);
        body.Children.Add(_boardPane);
        Grid.SetRow(body, 1);
        right.Children.Add(body);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        _rename.Click += (_, _) => RenameSelected();
        _delete.Click += (_, _) => DeleteSelected();

        return grid;
    }

    private UIElement SectionHeader(string text, string buttonText, Action onClick, int row)
    {
        var bar = new DockPanel { Margin = new Thickness(0, row == 0 ? 0 : 0, 0, 6) };

        var button = new Button { Content = buttonText, Padding = new Thickness(10, 3, 10, 3), FontSize = 11.5 };
        button.Click += (_, _) => onClick();
        DockPanel.SetDock(button, Dock.Right);
        bar.Children.Add(button);

        var label = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        bar.Children.Add(label);

        Grid.SetRow(bar, row);
        return bar;
    }

    /// <summary>
    /// What you can write, shown as the thing itself rather than described.
    /// The task syntax comes first because it is the one worth knowing.
    /// </summary>
    private UIElement SyntaxLine()
    {
        var line = new WrapPanel { Margin = new Thickness(2, 8, 2, 0) };

        foreach (var token in new[]
                 {
                     "- [ ] to do", "- [x] done", "# heading", "**bold**",
                     "*italic*", "`code`", "> quote", "[text](link)",
                 })
        {
            line.Children.Add(new TextBlock
            {
                Text = token,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 16, 4),
                Foreground = Application.Current?.TryFindResource("TextMuted") as Brush
                             ?? new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9C)),
            });
        }

        DockPanel.SetDock(line, Dock.Bottom);
        return line;
    }

    private Panel BuildBoardPane()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        _boardBlurb.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        panel.Children.Add(_boardBlurb);

        var open = new Button
        {
            Content = "Open in PureRef",
            Padding = new Thickness(18, 8, 18, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            FontWeight = FontWeights.SemiBold,
        };
        open.Click += (_, _) => OpenBoard();
        panel.Children.Add(open);

        return panel;
    }

    // ── Listing ─────────────────────────────────────────────────────────────

    private void Refresh(string? selectNote = null, string? selectBoard = null)
    {
        _notes.ItemsSource = ProjectWorkspace.Notes(_project);
        _boards.ItemsSource = ProjectWorkspace.Boards(_project);

        if (selectNote is not null)
            _notes.SelectedItem = _notes.Items.Cast<ProjectWorkspace.Item>()
                .FirstOrDefault(i => i.Path == selectNote);
        if (selectBoard is not null)
            _boards.SelectedItem = _boards.Items.Cast<ProjectWorkspace.Item>()
                .FirstOrDefault(i => i.Path == selectBoard);

        if (_notes.SelectedItem is null && _boards.SelectedItem is null) ShowNothing();
    }

    // ── Notes ───────────────────────────────────────────────────────────────

    private void NewNote()
    {
        var name = Ask("New note", "What should it be called?", "Untitled");
        if (name is null) return;

        SaveOpenNote();
        var item = ProjectWorkspace.CreateNote(_project, name);
        MarkChanged();
        Refresh(selectNote: item.Path);
        SetEditing(true);
    }

    private void ShowNote()
    {
        SaveOpenNote();

        if (_notes.SelectedItem is not ProjectWorkspace.Item item) return;
        _openNote = item;

        string text;
        try { text = File.ReadAllText(item.Path); }
        catch (Exception ex) { text = $"This note couldn't be read: {ex.Message}"; }

        _editor.Text = text;
        _preview.Source = text;

        _title.Text = item.Name;
        _subtitle.Text = $"Note · edited {Ago(item.Modified)}";

        _boardPane.Visibility = Visibility.Collapsed;
        _edit.Visibility = Visibility.Visible;
        _rename.IsEnabled = _delete.IsEnabled = true;
        SetEditing(false);
    }

    private void SetEditing(bool editing)
    {
        if (_openNote is null) return;

        _editorPane.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _preview.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        _edit.Content = editing ? "Done" : "Edit";

        if (editing) { _editor.Focus(); _editor.CaretIndex = _editor.Text.Length; }
        else SaveOpenNote();
    }

    /// <summary>
    /// Write the open note back if it has changed.
    ///
    /// There is no Save button: a note is saved when you look away from it —
    /// switching notes, stopping editing, or closing the window. What that
    /// writes is still only into the temp folder, so the project's own Save is
    /// what puts it in the file.
    /// </summary>
    private void SaveOpenNote()
    {
        if (_openNote is null) return;

        try
        {
            if (File.Exists(_openNote.Path) && File.ReadAllText(_openNote.Path) == _editor.Text) return;
            File.WriteAllText(_openNote.Path, _editor.Text);
            MarkChanged();
        }
        catch { /* the temp folder went away — the project's Save will report it */ }
    }

    /// <summary>A checkbox in the preview was clicked: write the tick into the source.</summary>
    private void TickTask(object? sender, (int Line, bool Checked) task)
    {
        var updated = Markdown.SetTask(_editor.Text, task.Line, task.Checked);
        if (updated == _editor.Text) return;

        _editor.Text = updated;   // the preview re-renders from this
        SaveOpenNote();
    }

    // ── Boards ──────────────────────────────────────────────────────────────

    private void NewBoard()
    {
        // Asked before the name, not after: there is no point choosing what to
        // call a board that nothing on this machine can open.
        if (!OfferPureRef()) return;

        var name = Ask("New board", "What should it be called?", "References");
        if (name is null) return;

        // The blank board ships with the app: PureRef has no command line, so a
        // new one can only ever be a copy of one that already exists.
        using var template = typeof(NotesWindow).Assembly
            .GetManifestResourceStream("SimplifiedStudio.Notes.blank.pur");

        if (template is null)
        {
            SimplifiedUi.MessageDialog.Show(this, "The blank board is missing from this build, so a new one can't be started.",
                "New board", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var item = ProjectWorkspace.CreateBoard(_project, name, template);
        MarkChanged();
        Refresh(selectBoard: item.Path);
        OpenBoard();
    }

    private void ShowBoard()
    {
        if (_boards.SelectedItem is not ProjectWorkspace.Item item) return;
        _openNote = null;

        _title.Text = item.Name;
        _subtitle.Text = $"Board · {item.Size / 1024:N0} KB · edited {Ago(item.Modified)}";
        _boardBlurb.Text =
            "Moodboards open in PureRef. Save there and the board is updated inside this project — " +
            "the project itself still needs saving afterwards.";

        _editorPane.Visibility = Visibility.Collapsed;
        _preview.Visibility = Visibility.Collapsed;
        _boardPane.Visibility = Visibility.Visible;
        _edit.Visibility = Visibility.Collapsed;
        _rename.IsEnabled = _delete.IsEnabled = true;
    }

    private void OpenBoard()
    {
        if (_boards.SelectedItem is not ProjectWorkspace.Item item) return;
        if (!OfferPureRef()) return;

        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"Couldn't open {item.Name}: {ex.Message}\n\n" +
                "Boards are PureRef files — opening one needs PureRef installed.",
                "Open board", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Check PureRef is here, and explain it if not.
    ///
    /// Returns whether to carry on. A machine without it can still hold boards
    /// in a project — they just can't be opened — so this is an explanation and
    /// a way forward rather than an error.
    /// </summary>
    private bool OfferPureRef()
    {
        if (PureRef.IsInstalled) return true;

        var root = new StackPanel { Margin = new Thickness(18) };

        var heading = new TextBlock
        {
            Text = "Boards need PureRef",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        root.Children.Add(heading);

        var body = new TextBlock
        {
            Text = "Moodboards are PureRef files, and PureRef is what opens them. " +
                   "It's a free program — you can name your own price, including nothing — " +
                   "and it's a single download from their website." + Environment.NewLine + Environment.NewLine +
                   "Install it, then come back and the board will open.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
            LineHeight = 19,
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        root.Children.Add(body);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var go = new Button
        {
            Content = "Go to website",
            IsDefault = true,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0),
            FontWeight = FontWeights.SemiBold,
        };
        var close = new Button { Content = "Not now", IsCancel = true, Padding = new Thickness(16, 6, 16, 6) };
        row.Children.Add(go);
        row.Children.Add(close);
        root.Children.Add(row);

        var win = new Window
        {
            Title = "Boards need PureRef",
            Content = root,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        SimplifiedUi.DialogChrome.Apply(win);

        go.Click += (_, _) => { PureRef.OpenWebsite(); win.DialogResult = false; };
        win.ShowDialog();

        // Either way the board isn't opening now — they've gone to install it,
        // or they've said not now.
        return false;
    }

    /// <summary>
    /// Notice when PureRef saves a board.
    ///
    /// It writes to the project's temp folder without telling anyone, so
    /// without this the project would look clean and the edit would be thrown
    /// away when it closed.
    /// </summary>
    private void WatchBoards()
    {
        var folder = ProjectWorkspace.BoardsFolder(_project);
        Directory.CreateDirectory(folder);

        try
        {
            _watcher = new FileSystemWatcher(folder, "*" + ProjectWorkspace.BoardExtension)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            void Touched(object _, FileSystemEventArgs __) =>
                Dispatcher.BeginInvoke(() =>
                {
                    MarkChanged();
                    var selected = (_boards.SelectedItem as ProjectWorkspace.Item)?.Path;
                    Refresh(selectBoard: selected);
                    if (selected is not null) ShowBoard();
                });

            _watcher.Changed += Touched;
            _watcher.Created += Touched;
            _watcher.Deleted += Touched;
            _watcher.Renamed += Touched;
        }
        catch
        {
            // Without a watcher a board edit still lands in the temp folder; it
            // just won't mark the project dirty on its own.
        }
    }

    // ── Both ────────────────────────────────────────────────────────────────

    private ProjectWorkspace.Item? Selected =>
        (_notes.SelectedItem ?? _boards.SelectedItem) as ProjectWorkspace.Item;

    private void RenameSelected()
    {
        if (Selected is not { } item) return;

        var name = Ask("Rename", "What should it be called?", item.Name);
        if (name is null) return;

        SaveOpenNote();
        var renamed = ProjectWorkspace.Rename(item, name);
        MarkChanged();

        var isNote = _notes.SelectedItem is not null;
        Refresh(selectNote: isNote ? renamed.Path : null, selectBoard: isNote ? null : renamed.Path);
    }

    private void DeleteSelected()
    {
        if (Selected is not { } item) return;

        var what = _notes.SelectedItem is not null ? "note" : "board";
        if (SimplifiedUi.MessageDialog.Show(this, $"Delete the {what} \"{item.Name}\"? This can't be undone.",
                "Delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _openNote = null;   // don't write it back on the way out
        ProjectWorkspace.Delete(item);
        MarkChanged();
        Refresh();
    }

    private void ShowNothing()
    {
        _openNote = null;
        _title.Text = "Nothing selected";
        _subtitle.Text = "Notes are yours to write; boards open in PureRef.";
        _editorPane.Visibility = Visibility.Collapsed;
        _preview.Visibility = Visibility.Collapsed;
        _boardPane.Visibility = Visibility.Collapsed;
        _edit.Visibility = Visibility.Collapsed;
        _rename.IsEnabled = _delete.IsEnabled = false;
    }

    /// <summary>A one-line prompt, matching the rename dialogs elsewhere in Studio.</summary>
    private string? Ask(string title, string prompt, string initial)
    {
        var root = new StackPanel { Margin = new Thickness(16) };

        var label = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        root.Children.Add(label);

        var box = new TextBox { Text = initial };
        root.Children.Add(box);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        root.Children.Add(row);

        var win = new Window
        {
            Title = title,
            Content = root,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        SimplifiedUi.DialogChrome.Apply(win);
        ok.Click += (_, _) => { win.DialogResult = true; };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        if (win.ShowDialog() != true) return null;
        var value = box.Text.Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>"just now", "20 minutes ago", "3 days ago" — enough to place it.</summary>
    private static string Ago(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} minute(s) ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hour(s) ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} day(s) ago";
        return when.ToString("d MMM yyyy");
    }
}
