using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SimplifiedSponsorEditor.Core;
using SimplifiedSponsorEditor.Core.Config;
using SimplifiedSponsorEditor.Core.Package;
using SimplifiedSponsorEditor.Core.Project;
using SimplifiedSponsorEditor.Core.Sponsors;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The window: a project, and the 641 sponsor decals it can replace.
///
/// A decal is two textures — the one on the bodywork and the one the
/// customisation menu shows while you pick it — and the reason this app exists
/// rather than two passes over two archives is that replacing one without the
/// other leaves the menu advertising artwork the car no longer wears.
/// </summary>
public partial class MainWindow : Window
{
    private static Brush Themed(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(r, g, b));

    private static Brush MutedBrush => Themed("TextMuted", 0xA4, 0xA4, 0xA4);

    private readonly UserConfig _config = UserConfig.Load();

    /// <summary>What there is to edit. Read once — the archives never change.</summary>
    private Catalogue _catalogue = new([]);

    private DecalPreview? _preview;

    private SponsorProject? _project;
    private bool _dirty;

    // Why the sponsor archives couldn't be read, if they couldn't. Reported
    // once the window is up — see LoadCatalogue.
    private string? _catalogueError;

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);

        VersionLabel.Text = Util.AppVersion.Display;
        ApplyWindowIcon();
        ApplyThemeToBackdrop();

        // Before the first ShowSelectedDecal, so the pane opens on the view it
        // was left on. Frames is checked in the markup, so only the others need
        // saying — and the artwork view has no chip, so the bar is left claiming
        // nothing rather than claiming the frames it is not showing.
        if (_config.DecalView == "Car") CarViewChip.IsChecked = true;
        else if (_config.DecalView == "Artwork") FramesViewChip.IsChecked = false;

        ApplyBackdrop();

        LoadCatalogue();
        RefreshEverything();

        // A project handed over by Explorer — a double-click, or "Open with".
        //
        // ContentRendered rather than Loaded: Loaded fires before the first
        // paint, so acting on it would leave an unpainted window on screen for
        // however long opening takes.
        if (App.StartupFile is { } startupFile)
            ContentRendered += (_, _) => OpenProject(startupFile);

        // Nothing in this app works without the archives, so it is not
        // something to let someone read past — but it needs a window to be
        // owned by, which is why it waits for one.
        if (_catalogueError is { } why)
            ContentRendered += (_, _) => SimplifiedUi.MessageDialog.Show(this,
                $"The sponsor archives couldn't be read.\n\n{why}",
                "Sponsors", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void LoadCatalogue()
    {
        try
        {
            _catalogue = SponsorCatalogue.Read(AppPaths.OriginalsDir);
            _preview = new DecalPreview(Path.Combine(AppPaths.OriginalsDir, SponsorCatalogue.CarArchiveName));
        }
        catch (Exception ex)
        {
            // Held rather than shown: this runs from the constructor, and a
            // dialog owned by a window that has not been shown yet throws. The
            // window reports it once it is up.
            _catalogueError = ex.Message;
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
            // Behind a progress window, which only appears if this takes a
            // moment. Writing the project is a zip of whatever artwork is in it.
            _project = SimplifiedUi.Busy.Run(this, StatusSpinner, StatusLabel,
                $"Starting {request.ModName}", progress =>
                {
                    progress.Report("Setting up the project…");
                    var created = ProjectStore.Create(request.ModName, request.ModAuthor);

                    // Written to disk straight away, as the rest of the suite
                    // does it. The path was already asked for, and a project
                    // that exists only in a temp directory is one power cut
                    // from not existing at all.
                    progress.Report("Saving the project…");
                    ProjectStore.Save(created, request.SavePath);
                    return created;
                });
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
        OpenedProject();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges("open another project")) return;

        var picker = new OpenFileDialog
        {
            Title = "Open a sponsor project",
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
            _project = SimplifiedUi.Busy.Run(this, StatusSpinner, StatusLabel,
                $"Opening {Path.GetFileNameWithoutExtension(path)}",
                progress =>
                {
                    progress.Report("Unpacking the project…");
                    return ProjectStore.Open(path);
                });
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
        OpenedProject();

        if (EditorVersion.IsNewerThanCurrent(_project.Info.EditorVersion))
            SimplifiedUi.MessageDialog.Show(this,
                $"{_project.DisplayName} was written by Sponsor Editor {_project.Info.EditorVersion}, " +
                "which is newer than this build.\n\nAnything it added that this one doesn't know " +
                "about is kept, but not shown.",
                "Open", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenedProject()
    {
        _selected = null;
        RefreshEverything();
        SelectFirst();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save(saveAs: false);

    private bool Save(bool saveAs)
    {
        if (_project is null) return false;

        var target = _project.ZipPath;
        if (saveAs || target is null)
        {
            var picker = new SaveFileDialog
            {
                Title = "Save the project",
                Filter = $"Simplified projects (*{ProjectStore.ProjectFileExtension})|*{ProjectStore.ProjectFileExtension}",
                DefaultExt = ProjectStore.ProjectFileExtension,
                FileName = _project.DisplayName + ProjectStore.ProjectFileExtension,
                InitialDirectory = FirstExistingDir(_config.LastProjectFolder) ?? "",
            };

            if (picker.ShowDialog(this) != true) return false;
            target = picker.FileName;
        }

        try
        {
            ProjectStore.Save(_project, target);
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"The project couldn't be saved.\n\n{ex.Message}",
                "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        _config.LastProjectFolder = Path.GetDirectoryName(target);
        _config.Save();

        _dirty = false;
        RefreshChrome();
        return true;
    }

    private void CloseProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges("close this project")) return;

        CloseProject();
        RefreshEverything();
    }

    private void CloseProject()
    {
        // The renders are cached by decal name, which the next project uses too.
        // Cleared here rather than where a project is opened, because this runs
        // first on every path that swaps one — New, Open and closing outright.
        _artwork.Clear();

        if (_project is null) return;

        ProjectStore.Close(_project);
        _project = null;
        _dirty = false;
    }

    private void EditProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;

        if (EditProjectDialog.Show(this, _project.Info) is not { } edited) return;

        _project.Info.Name = edited.ModName;
        _project.Info.Author = edited.ModAuthor;

        MarkDirty();
        RefreshChrome();
    }

    // ── Packaging ───────────────────────────────────────────────────────────

    private void Package_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;

        var replaced = _project.ReplacedDecals.Count;
        if (replaced == 0)
        {
            SimplifiedUi.MessageDialog.Show(this,
                "Nothing has been replaced yet.\n\nPick a decal and choose a picture for it first.",
                "Package", MessageBoxButton.OK);
            return;
        }

        var gameDir = _config.GameDir;
        if (!Directory.Exists(gameDir ?? ""))
        {
            SimplifiedUi.MessageDialog.Show(this,
                "Packaging needs the game folder.\n\nThe mod is built by merging your decals into the " +
                "game's own shader archives, which aren't ours to ship. Set the game folder in Settings.",
                "Game folder not set", MessageBoxButton.OK);
            return;
        }

        var picker = new SaveFileDialog
        {
            Title = "Save the mod",
            Filter = "Zip archives (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = SafeFileName(_project.Info.Name.Length > 0 ? _project.Info.Name : _project.DisplayName) + ".zip",
            InitialDirectory = FirstExistingDir(_config.LastPackageFolder) ?? "",
        };

        if (picker.ShowDialog(this) != true) return;

        _config.LastPackageFolder = Path.GetDirectoryName(picker.FileName);
        _config.Save();

        PackageButton.IsEnabled = false;

        // Rendered here rather than inside the packager: the project keeps the
        // files somebody imported, and turning a drawing into pixels is a WPF
        // operation that wants this thread. The worker below gets a folder of
        // PNGs, which is all it ever wanted.
        var (artwork, skipped) = Materialise();

        var request = new PackageRequest(artwork, AppPaths.OriginalsDir, gameDir!, picker.FileName);

        try
        {
            // Behind a progress window: every replaced decal is converted and
            // then merged into both of the game's shader archives, which is the
            // longest thing this app does.
            var name = _project.Info.Name.Length > 0 ? _project.Info.Name : _project.DisplayName;
            var result = SimplifiedUi.Busy.Run(this, StatusSpinner, StatusLabel, $"Packaging {name}",
                say => SponsorPackager.Build(request, say: say));

            // Always told, not only when something went sideways: Studio reports
            // a finished package in a dialog too, and packaging is long enough
            // that "did that work?" is a fair question with nothing on screen.
            // The ones that never reached the packager go first: they are about
            // what is not in the zip, which is the more useful half of the news.
            var notes = skipped.Concat(result.Warnings).ToList();

            var summary = $"Packaged {result.CarDecals} decal(s) to {result.ZipPath}";
            SimplifiedUi.MessageDialog.Show(this,
                notes.Count > 0 ? $"{summary}\n\n{string.Join('\n', notes)}" : summary,
                "Package complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"The mod couldn't be packaged.\n\n{ex.Message}",
                "Package", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (Directory.Exists(artwork)) Directory.Delete(artwork, recursive: true); }
            catch { /* best effort — it is under the temp folder either way */ }

            PackageButton.IsEnabled = _project is not null;
        }
    }

    // ── Chrome and state ────────────────────────────────────────────────────

    private void MarkDirty()
    {
        _dirty = true;
        RefreshChrome();
    }

    private void RefreshEverything()
    {
        RefreshChrome();
        RebuildViews();
        ShowSelectedDecal();
    }

    private void RefreshChrome()
    {
        var open = _project is not null;

        SaveButton.IsEnabled = open;
        PackageButton.IsEnabled = open;
        CloseProjectButton.IsEnabled = open;
        EditProjectButton.IsEnabled = open;

        // The mod's name, not the file's. The file name is a working detail —
        // what the thing being made is called is the mod name, and that is what
        // the package is named after too.
        ProjectLabel.Text = open
            ? (_project!.Info.Name.Length > 0 ? _project.Info.Name : _project.DisplayName)
            : "No project open";
        ProjectLabel.Foreground = open ? MutedBrush : Themed("TextDisabled", 0x66, 0x66, 0x66);

        DirtyLabel.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;

        Title = open
            ? $"{_project!.DisplayName} — Simplified Sponsor Editor"
            : "Simplified Sponsor Editor";
    }

    /// <summary>
    /// Ask before throwing away unsaved work. True when it's fine to carry on.
    /// </summary>
    private bool ConfirmDiscardChanges(string what)
    {
        if (_project is null || !_dirty) return true;

        var answer = SimplifiedUi.MessageDialog.Choose(this, "Unsaved changes",
            $"Save {_project.DisplayName} before you {what}?",
            "The decals you've replaced since the last save would be lost.",
            ("Save", MessageBoxResult.Yes),
            ("Discard", MessageBoxResult.No),
            ("Cancel", MessageBoxResult.Cancel));

        return answer switch
        {
            MessageBoxResult.Yes => Save(saveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private static string? FirstExistingDir(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;

    /// <summary>A mod name as a file name, with the characters Windows refuses taken out.</summary>
    private static string SafeFileName(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return cleaned.Trim().Length > 0 ? cleaned.Trim() : "sponsor_mod";
    }

    // ── Window ──────────────────────────────────────────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!SponsorSettings.Show(this, _config)) return;
        _config.Save();

        // The palette may have changed under us. Everything in XAML follows it
        // through DynamicResource; the preview's ground is painted from code and
        // holds the brush it was handed, so it has to be repointed and repainted.
        ApplyThemeToBackdrop();
        ApplyBackdrop();
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Double-clicking the title bar maximises too — that's WindowChrome's
    /// doing, not ours — so the glyph has to follow the state rather than the
    /// click.
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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        // Ctrl+S writes to the project's own file; Ctrl+Shift+S asks where.
        Save(saveAs: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        e.Handled = true;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges("close")) { e.Cancel = true; return; }

        CloseProject();
        _preview?.Dispose();
        _menuPreview?.Dispose();
    }
}
