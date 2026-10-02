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

    private static Brush ErrorBrush => Themed("Danger", 0xE0, 0x44, 0x34);
    private static Brush MutedBrush => Themed("TextMuted", 0xA4, 0xA4, 0xA4);
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

    /// <summary>Which season the Teams tab is showing. Always an F1 one.</summary>
    private Season _season = Season.Y2026;

    /// <summary>
    /// Showing the 2026 Formula 2 grid rather than an F1 one.
    ///
    /// Kept beside <see cref="_season"/> rather than folded into it because the
    /// two tabs want different things: F2 has its own twenty-two seats, but no
    /// teams the Teams tab can edit — the eleven F2 outfits have no name keys or
    /// colour records of the kind that tab works in. So the Teams tab stays on
    /// its F1 season and is switched off while this is set.
    /// </summary>
    private bool _f2;

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

        // ContentRendered rather than Loaded: Loaded fires before the first
        // paint, so anything slow hung on it leaves an unpainted window on
        // screen for as long as it takes, and a dialog can't be given an owner
        // that hasn't been shown yet. Studio defers for the same reason.
        ContentRendered += (_, _) =>
        {
            // The game's files first: a project is built out of them, and this
            // is where the waiting belongs. Nothing to do on all but the first
            // run of a game build.
            EnsureGameFiles();

            // Then a project handed over by Explorer — a double-click, or
            // "Open with".
            if (App.StartupFile is { } startupFile) OpenProject(startupFile);
        };
    }

    /// <summary>
    /// Make sure the game files the app works from are the installed game's.
    ///
    /// The language files are the ones that need fetching: the game ships none
    /// on disk, they live inside its exe, and getting them out the first time on
    /// a build means reading 408 MB — half a minute, once. It happens here, at
    /// startup, rather than when a project is started, for two reasons. A person
    /// opening an app is expecting it to take a moment; a person who has just
    /// named a mod and pressed Create is expecting a project. And the check that
    /// decides whether to do it at all is the same one either way: whether what
    /// is on disk came out of the exe that is installed now.
    /// </summary>
    private void EnsureGameFiles()
    {
        var state = Simplified.Language.LanguageFiles.Check(_config.GameDir, AppPaths.LanguageDir);
        if (Simplified.Language.LanguageFiles.Headline(state) is not { } headline) return;

        try
        {
            SimplifiedUi.Busy.Run(this, StatusSpinner, StatusLabel, headline, progress =>
                Simplified.Language.LanguageFiles.Refresh(
                    _config.GameDir, AppPaths.LanguageDir, AppPaths.LanguageCacheDir, progress));
        }
        catch (Exception ex)
        {
            // Said on the status line rather than in a dialog. The app opens and
            // works; what it cannot do until this is sorted out is start a new
            // project, and that path says so itself.
            SetStatus($"The game's files couldn't be read, so a new project can't be started yet: {ex.Message}",
                      ErrorBrush);
        }
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
            string? seedNote = null;

            // Behind a progress window: this copies the 18 MB database, the
            // eleven language files and the UI archives, then writes the lot
            // back out as a zip. It only appears if that takes a moment.
            _project = SimplifiedUi.Busy.Run(this, StatusSpinner, StatusLabel,
                $"Starting {request.ModName}", progress =>
                {
                    var created = ProjectStore.Create(
                        AppPaths.OriginalsDir, request.ModName, request.ModAuthor,
                        _config.GameDir, note => seedNote = note, progress);

                    // Written to disk straight away, as Studio does it. The path
                    // was already asked for, and a project that exists only in a
                    // temp directory is one power cut from not existing at all.
                    progress.Report("Saving the project…");
                    ProjectStore.Save(created, request.SavePath);
                    return created;
                });

            if (seedNote is not null) SetStatus(seedNote, MutedBrush);
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

        string? openNote = null;

        try
        {
            // The project holds its changes, not the files it changed, so
            // opening one reads the installed game and puts those changes back.
            _project = SimplifiedUi.Busy.Run(this, StatusSpinner, StatusLabel,
                $"Opening {Path.GetFileNameWithoutExtension(path)}",
                progress => ProjectStore.Open(
                    path, AppPaths.OriginalsDir, _config.GameDir,
                    note => openNote = note, progress));
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

        if (openNote is not null) SetStatus(openNote, MutedBrush);

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

        var notes = new List<string>();

        try
        {
            // The tabs hold their edits in memory while they are being made;
            // this is where they become the project's files. Read on this
            // thread, written behind a progress window: an 18 MB database,
            // eleven language files and a 90 MB zip is several seconds, and the
            // window used to stop drawing for all of them.
            var staged = StagedEdits();

            SimplifiedUi.Busy.Run(this, StatusSpinner, StatusLabel,
                $"Saving {_project.DisplayName}", say =>
            {
                say.Report("Writing your changes into the project…");
                WriteEditsInto(staged, notes);

                // What the project stores is the difference between these
                // files and the game’s own, so it is read back out here.
                ProjectStore.CaptureEdits(_project, AppPaths.OriginalsDir, _config.GameDir, say);

                say.Report("Writing the project file…");
                ProjectStore.Save(_project, target);
            });
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"The project couldn't be saved.\n\n{ex.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        AfterEditsWritten(notes);

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

    /// <summary>
    /// Put the open project away without shutting the app, as Studio's Close
    /// does. New and Open already did this on the way to something else; on its
    /// own it is how you stop working on a project and leave the window up.
    /// </summary>
    private void CloseProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        if (!ConfirmDiscardChanges("close this project")) return;

        var name = _project.DisplayName;
        CloseProject();
        RefreshEverything();
        SetStatus($"Closed {name}.", MutedBrush);
    }

    // ── Season ──────────────────────────────────────────────────────────────

    private void Season_Checked(object sender, RoutedEventArgs e)
    {
        if (_settingSeasonChip) return;

        var f2 = ReferenceEquals(sender, SeasonF2Chip);

        // F2 leaves the Teams tab on whichever F1 season it was showing, so
        // coming back from F2 lands where the user left off rather than jumping.
        var chosen = f2 ? _season
            : ReferenceEquals(sender, Season2025Chip) ? Season.Y2025
            : Season.Y2026;

        if (f2 == _f2 && chosen == _season) return;

        _f2 = f2;
        _season = chosen;
        ApplyF2ToTabs();
        ShowSeasonInTabs();
        RefreshEverything();
    }

    /// <summary>Point the chips at a season without asking anything.</summary>
    private void ShowSeason(Season season)
    {
        _season = season;
        _f2 = false;
        _settingSeasonChip = true;
        Season2026Chip.IsChecked = season == Season.Y2026;
        Season2025Chip.IsChecked = season == Season.Y2025;
        SeasonF2Chip.IsChecked = false;
        _settingSeasonChip = false;
        ApplyF2ToTabs();
    }

    /// <summary>
    /// Turn the Teams tab off for F2, and step off it if it was the one on show
    /// — a disabled tab that stays selected leaves the window looking broken.
    /// </summary>
    private void ApplyF2ToTabs()
    {
        TeamsTab.IsEnabled = !_f2;
        if (_f2 && ReferenceEquals(Tabs.SelectedItem, TeamsTab))
            Tabs.SelectedIndex = 1;   // Grid
    }

    // ── Packaging ───────────────────────────────────────────────────────────

    private void Package_Click(object sender, RoutedEventArgs e) => PackageProject();

    /// <summary>
    /// Bring something in from outside the project. Team logos, for now, which
    /// is why it hands straight to the Teams tab — the pictures land on its
    /// rows, whichever tab is in front.
    /// </summary>
    private void Import_Click(object sender, RoutedEventArgs e) => Teams.ImportLogos();

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

        // A game folder that has just been set or changed is the other moment
        // the language files can go from missing to gettable — without this the
        // fix would be to set the folder and then restart the app.
        EnsureGameFiles();
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
        ProjectLabel.Foreground = open ? MutedBrush : Themed("TextDisabled", 0x66, 0x66, 0x66);
        DirtyLabel.Visibility = open && HasPendingChanges ? Visibility.Visible : Visibility.Collapsed;

        SaveButton.IsEnabled = open;
        EditProjectButton.IsEnabled = open;
        ImportButton.IsEnabled = open;
        PackageButton.IsEnabled = open;
        CloseProjectButton.IsEnabled = open;
        Tabs.IsEnabled = open;

        RefreshTabs();
    }

    /// <summary>
    /// Something the window itself changed and no tab knows about — the mod's
    /// name or its author. A tab's own edits aren't latched here: they are
    /// asked for, through <c>TabsHaveChanges</c>, so that a notification the
    /// tabs raise while they are being filled can't be mistaken for one.
    /// </summary>
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
