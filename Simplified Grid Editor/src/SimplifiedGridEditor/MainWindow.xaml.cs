using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SimplifiedGridEditor.Core;
using SimplifiedGridEditor.Core.Config;
using SimplifiedGridEditor.Core.Project;
using SimplifiedTeamEditor.Core.Teams;

namespace SimplifiedGridEditor;

/// <summary>
/// The window: a project, a season, and three tabs onto the same files.
///
/// Teams, Grid and Drivers all edit one <c>gamedatabases.2025.bin</c> — team
/// colours, the entrant lists and the driver table are records in it — which is
/// the reason this app exists rather than two that each rewrite the file and
/// overwrite each other's work.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// A palette brush, looked up each time rather than cached in a static: a
    /// static readonly would be resolved once at type init, possibly before the
    /// dictionary is merged, and would then keep the old colour through a theme
    /// change. The literal is the fallback for both cases.
    /// </summary>
    private static Brush Themed(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(r, g, b));

    private static Brush ErrorBrush => Themed("Danger", 0xB0, 0x2A, 0x2A);
    private static Brush MutedBrush => Themed("TextMuted", 0x66, 0x66, 0x66);
    /// <summary>
    /// What the status line says after something worked - which is the same
    /// colour it reads in the rest of the time. A line that turns accent-blue to
    /// announce success is louder than the news, and the wording already says
    /// it. A problem still goes red, because that one is worth interrupting for.
    /// </summary>
    private static Brush OkBrush => MutedBrush;

    private readonly UserConfig _config = UserConfig.Load();

    private GridProject? _project;
    private bool _dirty;

    /// <summary>Which grid the Teams and Grid tabs are showing.</summary>
    private Season _season = Season.Y2026;

    /// <summary>
    /// True while the season chips are being set from code, so putting them back
    /// after a cancelled switch doesn't read as a second switch.
    /// </summary>
    private bool _settingSeasonChip;

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);

        VersionLabel.Text = Util.AppVersion.Display;
        ApplyWindowIcon();

        InitTabs();
        RefreshEverything();
    }

    /// <summary>
    /// The window icon, from the .ico shipped beside the exe. The embedded
    /// application icon gives the taskbar its picture but not the Alt-Tab entry,
    /// which reads the window's own Icon and otherwise falls back to a blank page.
    /// </summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var path = Path.Combine(AppPaths.Root, "icon.ico");
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

    // ── Project lifecycle ───────────────────────────────────────────────────

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges("start a new project")) return;

        var request = NewProjectDialog.Show(this, _config.DefaultAuthor, _config.LastProjectFolder);
        if (request is null) return;

        CloseProject();

        try
        {
            _project = ProjectStore.Create(AppPaths.OriginalsDir, request.ModName, request.ModAuthor);

            // Written to disk straight away, as Studio does it. The path was
            // already asked for, and a project that exists only in a temp
            // directory is one power cut from not existing at all.
            ProjectStore.Save(_project, request.SavePath);
        }
        catch (Exception ex)
        {
            _project = null;
            SimplifiedUi.MessageDialog.Show(this,
                $"The project couldn't be created.\n\n{ex.Message}",
                "New project", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshEverything();
            return;
        }

        _config.LastProjectFolder = Path.GetDirectoryName(request.SavePath);
        if (!string.IsNullOrWhiteSpace(request.ModAuthor)) _config.DefaultAuthor = request.ModAuthor;
        _config.Save();

        _dirty = false;
        OpenedProject($"Created {_project.DisplayName} — the mod is called {_project.Info.Name}.");
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges("open another project")) return;

        var picker = new OpenFileDialog
        {
            Title = "Open a grid project",
            Filter = $"Simplified projects (*{ProjectStore.ProjectFileExtension})|*{ProjectStore.ProjectFileExtension}|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = FirstExistingDir(_config.LastProjectFolder) ?? "",
        };
        if (picker.ShowDialog(this) != true) return;

        OpenProject(picker.FileName);
    }

    private void OpenProject(string path)
    {
        CloseProject();

        try
        {
            _project = ProjectStore.Open(path);
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"{Path.GetFileName(path)} couldn't be opened.\n\n{ex.Message}",
                "Open project", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshEverything();
            return;
        }

        _config.LastProjectFolder = Path.GetDirectoryName(path);
        _config.Save();

        _dirty = false;
        OpenedProject($"Opened {_project.DisplayName}.");

        if (EditorVersion.IsNewerThanCurrent(_project.Info.EditorVersion))
            SetStatus(
                $"{_project.DisplayName} was written by Grid Editor {_project.Info.EditorVersion}, " +
                "which is newer than this build. Anything it added that this one doesn't know about " +
                "is kept, but not shown.", ErrorBrush);
    }

    /// <summary>Everything that has to happen once a project is in <c>_project</c>.</summary>
    private void OpenedProject(string message)
    {
        LoadProjectIntoTabs();
        RefreshEverything();
        SetStatus(message, OkBrush);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveProject(saveAs: false);

    /// <summary>
    /// Write the project out. Returns false when nothing was written — the user
    /// cancelled the picker, or the write failed — so a caller that was saving
    /// on the way to something else can stop.
    /// </summary>
    private bool SaveProject(bool saveAs)
    {
        if (_project is null) return false;

        var target = _project.ZipPath;
        if (saveAs || target is null)
        {
            var picker = new SaveFileDialog
            {
                Title = saveAs ? "Save the project as" : "Save the project",
                Filter = $"Simplified projects (*{ProjectStore.ProjectFileExtension})|*{ProjectStore.ProjectFileExtension}",
                // The file's own name, not the mod's: this is Save as, so the
                // thing being renamed is the project on disk. A project saved as
                // "Bugatti 2026" should not offer to become "Test" again just
                // because that is what the mod inside it is called.
                FileName = _project.ZipPath is { } existing
                    ? Path.GetFileNameWithoutExtension(existing)
                    : SafeFileName(_project.Info.Name.Length > 0 ? _project.Info.Name : "grid"),
                DefaultExt = ProjectStore.ProjectFileExtension,
                InitialDirectory = FirstExistingDir(_config.LastProjectFolder) ?? "",
            };
            if (picker.ShowDialog(this) != true) return false;
            target = picker.FileName;
        }

        try
        {
            // The tabs hold their edits in memory while they are being made;
            // this is where they become the project's files.
            ApplyEditsToProject();
            ProjectStore.Save(_project, target);
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"The project couldn't be saved.\n\n{ex.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        _config.LastProjectFolder = Path.GetDirectoryName(target);
        _config.Save();

        _dirty = false;
        RefreshEverything();
        SetStatus($"Saved {_project.DisplayName}.", OkBrush);
        return true;
    }

    private void CloseProject()
    {
        if (_project is null) return;
        ProjectStore.Close(_project);
        _project = null;
        _dirty = false;
        ClearTabs();
    }

    // ── Season ──────────────────────────────────────────────────────────────

    private void Season_Checked(object sender, RoutedEventArgs e)
    {
        if (_settingSeasonChip) return;

        var chosen = ReferenceEquals(sender, Season2025Chip) ? Season.Y2025 : Season.Y2026;
        if (chosen == _season) return;

        _season = chosen;
        ShowSeasonInTabs();
        RefreshEverything();
    }

    /// <summary>Point the chips at a season without asking anything.</summary>
    private void ShowSeason(Season season)
    {
        _season = season;
        _settingSeasonChip = true;
        Season2026Chip.IsChecked = season == Season.Y2026;
        Season2025Chip.IsChecked = season == Season.Y2025;
        _settingSeasonChip = false;
    }

    // ── Packaging ───────────────────────────────────────────────────────────

    private void Package_Click(object sender, RoutedEventArgs e) => PackageProject();

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

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges("close")) { e.Cancel = true; return; }
        CloseProject();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        var project = paths.FirstOrDefault(p =>
            string.Equals(Path.GetExtension(p), ProjectStore.ProjectFileExtension, StringComparison.OrdinalIgnoreCase));
        if (project is null) return;

        if (!ConfirmDiscardChanges("open another project")) return;
        OpenProject(project);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            // The suite's rule: Ctrl+S overwrites, Ctrl+Shift+S asks where.
            case Key.S when _project is not null:
                SaveProject(saveAs: shift);
                e.Handled = true;
                break;
            case Key.N when !shift:
                New_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.O when !shift:
                Open_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    // ── Settings ────────────────────────────────────────────────────────────

    private void EditProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;

        var request = EditProjectDialog.Show(this, _project.Info);
        if (request is null) return;
        if (request.ModName == _project.Info.Name && request.ModAuthor == _project.Info.Author) return;

        _project.Info.Name = request.ModName;
        _project.Info.Author = request.ModAuthor;

        MarkDirty();
        SetStatus($"The mod is now called {request.ModName}.", OkBrush);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!GridSettings.Show(this, _config)) return;

        _config.Save();
        ApplyAdvancedSwapping(_config.EnableAdvancedSwapping);
    }

    // ── Shared ──────────────────────────────────────────────────────────────

    /// <summary>Anything unsaved, in any tab.</summary>
    private bool HasPendingChanges => _dirty || TabsHaveChanges;

    /// <summary>
    /// Ask about unsaved work before <paramref name="action"/>. Buttons that say
    /// what they do, so the body doesn't have to gloss Yes and No.
    /// </summary>
    private bool ConfirmDiscardChanges(string action)
    {
        if (_project is null || !HasPendingChanges) return true;

        var answer = SimplifiedUi.MessageDialog.Choose(this,
            "Unsaved changes",
            $"{_project.DisplayName} has changes that haven't been saved.",
            $"Save writes them into the project first. Discard throws them away. Either way the app will then {action}.",
            ("Save", MessageBoxResult.Yes),
            ("Discard", MessageBoxResult.No),
            ("Cancel", MessageBoxResult.Cancel));

        return answer switch
        {
            MessageBoxResult.Yes => SaveProject(saveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void RefreshEverything()
    {
        var open = _project is not null;

        // The mod's name, not the file's. The file name is a working detail -
        // what the thing being made is called is the mod name, and that is what
        // the package is named after too.
        ProjectLabel.Text = open
            ? (_project!.Info.Name.Length > 0 ? _project.Info.Name : _project.DisplayName)
            : "No project open";
        ProjectLabel.Foreground = open ? MutedBrush : Themed("TextDisabled", 0x8A, 0x92, 0x9C);
        DirtyLabel.Visibility = open && HasPendingChanges ? Visibility.Visible : Visibility.Collapsed;

        SaveButton.IsEnabled = open;
        EditProjectButton.IsEnabled = open;
        PackageButton.IsEnabled = open;
        Tabs.IsEnabled = open;

        RefreshTabs();
    }

    /// <summary>Something in a tab changed. Nothing is written until Save.</summary>
    private void MarkDirty()
    {
        _dirty = true;
        RefreshEverything();
    }

    private void SetStatus(string text, Brush brush)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = brush;
    }

    private static string? FirstExistingDir(params string?[] candidates)
    {
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>A name as a file name, with what the filesystem refuses taken out.</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().Trim('.');
        return cleaned.Length == 0 ? "grid" : cleaned;
    }
}
