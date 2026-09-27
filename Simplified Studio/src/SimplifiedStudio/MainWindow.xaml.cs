using SimplifiedUi;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using EgoEngineLibrary.Archive.Erp;
using Microsoft.Win32;
using SimplifiedStudio.Core.Config;
using SimplifiedStudio.Core.Project;
using EgoEngineLibrary.Texconv;
using SimplifiedStudio.Core.Util;
using SimplifiedStudio.Model;
using SimplifiedStudio.Util;

namespace SimplifiedStudio;

/// <summary>
/// Texture browser for Codemasters ERP archives. Lists the GfxSRVResources,
/// previews them via BCnEncoder.Net, and exports/imports per texture as PNG
/// (with texconv handling the DDS↔PNG conversion).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// One ERP currently loaded into the texture pane. Multiple coexist in
    /// project mode (one per .erp inside the active version). In legacy
    /// standalone mode the list holds exactly one entry.
    /// </summary>
    private sealed class LoadedErp
    {
        /// <summary>Mutable — undo restore swaps a freshly-reparsed instance in.</summary>
        public ErpFile File { get; set; }
        /// <summary>Absolute path the ERP serializes back to on Save.</summary>
        public string Path { get; set; }
        /// <summary>Header label in the grouped texture list.</summary>
        public string DisplayName { get; }
        public LoadedErp(ErpFile file, string path, string displayName)
        { File = file; Path = path; DisplayName = displayName; }
    }

    private readonly List<LoadedErp> _loadedErps = new();

    /// <summary>True if at least one ERP is loaded in the texture pane.</summary>
    private bool HasAnyErp => _loadedErps.Count > 0;

    // v2 project state. Null means we're in legacy single-ERP mode.
    private SimplifiedProject? _project;

    // Holds whatever resources we surface in the main list — textures + PKG + XML
    // rows. The field name predates PKG/XML; left as-is to keep the diff small.
    private readonly ObservableCollection<ResourceListItem> _textures = new();
    private CollectionView? _texturesView;
    private readonly TexconvRunner _runner = new();
    private uint _sliceIndex;   // Current tex-array slice being previewed/edited (0 for single-slice textures)
    /// <summary>
    /// Where each loaded ERP sits in the project's arrangement, by the ErpFile
    /// instance a row carries. Empty outside a project, where name order wins.
    /// </summary>
    private readonly Dictionary<EgoEngineLibrary.Archive.Erp.ErpFile, int> _erpOrder =
        new(ReferenceEqualityComparer.Instance);

    private readonly PendingMips _pendingMips = new();  // New .mipmaps writes deferred until ERP save
    private bool _isDirty;      // Any in-memory change (import / strip / strip-all / pending mip writes)

    // Single-level undo: snapshot of every loaded ERP's bytes + pending mips taken
    // before each mutation. Ctrl+Z restores all of them; after restoring, the
    // snapshot is consumed (no redo / no chain). Keyed by LoadedErp identity so
    // the restore can swap the parsed ErpFile in place.
    private Dictionary<LoadedErp, byte[]>? _undoErpBytes;
    private Dictionary<string, byte[]>? _undoPendingMipsSnap;
    private bool _undoWasDirty;
    private bool _undoSavedSince;   // True if Save/Save As fired after this snapshot was taken
    private string? _undoDescription;
    private string? _undoSelectedIdentifier;
    private uint _undoSliceIndex;
    private HashSet<string>? _undoModified;

    // Which resources have been edited since the last save, by identifier. The
    // master copy lives here rather than on the rows because rows are rebuilt
    // from the ERPs after bulk imports and undo, which would drop a flag held
    // only on the view model. Identifiers are the full eaid:// path, unique
    // across every loaded ERP.
    private readonly HashSet<string> _modifiedIds = new(StringComparer.Ordinal);

    // Resource kinds the type chips are limiting the list to. Empty means no
    // restriction — that's what makes "nothing selected" show everything rather
    // than nothing.
    private readonly HashSet<ResourceKind> _kindFilter = new();

    /// <summary>Which channel of the previewed texture is on screen.</summary>
    private enum PreviewChannel { Rgb, Red, Green, Blue, Alpha }


    // The preview as decoded, before any channel isolation. Kept so switching
    // channels re-derives from the original rather than from an already
    // flattened greyscale.
    private System.Windows.Media.Imaging.BitmapSource? _previewOriginal;
    private PreviewChannel _previewChannel = PreviewChannel.Rgb;

    // Where the backdrop sits between the two ends: 0 is PreviewBackdropLight,
    // 1 is PreviewBackdropDark. Continuous rather than a pair of presets, since
    // the ground that shows a texture honestly is often neither extreme.
    // Sticky across selections, like the channel.
    private double _previewBackdropMix;

    // One brush, recoloured in place. A fresh SolidColorBrush per slider tick
    // would allocate on every pixel of the drag.
    private readonly System.Windows.Media.SolidColorBrush _previewBackdropBrush =
        new(System.Windows.Media.Colors.White);

    public MainWindow()
    {
        InitializeComponent();
        // Rounded corners, a taskbar-aware maximise, and a glyph that
        // follows the window state rather than the click.
        FramelessWindow.Attach(this, WindowMaximizeButton);

        ApplyThemeToBackdrop();

        // From the assembly rather than a literal, so the pill and the version
        // stamped into saved projects can never disagree.
        VersionLabel.Text = SimplifiedStudio.Util.AppVersion.Display;
        TextureList.ItemsSource = _textures;
        _texturesView = (CollectionView)CollectionViewSource.GetDefaultView(_textures);
        _texturesView.Filter = TextureFilter;
        // v2 Phase 3: group the list by source ERP. Hidden when only one ERP is
        // loaded — keeps the legacy single-ERP view clean (header would just
        // repeat the file name).
        _texturesView.GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(TextureVm.ParentDisplayName)));
        // Sort: ERP group → resource type → file name. The Kind enum order
        // (Texture, Pkg, Xml, Material, Context) drives type ordering, so the
        // list reads as TEX → PKG → XML → MAT → CON inside each ERP group.
        // The project's own ERP order comes first; the name is only the
        // tiebreak, which is what orders things outside a project.
        //
        // A comparer rather than SortDescriptions, because a SortDescription can
        // only name a property — which would mean stamping the project's order
        // onto every row, on a type both apps share and only one app orders.
        ((ListCollectionView)_texturesView).CustomSort = new ResourceRowOrder(_erpOrder);
        UpdateCountLabel();

        // Window icon (title bar + Alt-Tab). The .exe's Explorer icon comes from
        // <ApplicationIcon> at build time and doesn't need this.
        //
        // We use IconBitmapDecoder and hand WPF the LARGEST frame in the .ico
        // (256×256 here). BitmapImage(uri) would pick a single frame at decode
        // time — often the smallest 16×16 one — which Windows would then
        // upscale for the title bar, looking pixelated. The other end of that
        // needs guarding too, hence the 256 ceiling below.
        try
        {
            // AppContext.BaseDirectory works for both regular and single-file builds;
            // Assembly.Location returns "" in single-file (IL3000).
            // In runtime/ with the native libraries, so the app folder holds
            // the exe and little else.
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "runtime", "icon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                using var fs = new System.IO.FileStream(iconPath, System.IO.FileMode.Open, System.IO.FileAccess.Read);
                var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(
                    fs,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                // The largest frame is not always the usable one. These icons
                // carry a 512, and the shell tops out at 256 - hand it the 512
                // and the title bar and taskbar fall back to a default icon.
                // So: the biggest frame that is still 256 or under, and only if
                // there is none does the absolute largest get used.
                Icon = decoder.Frames.Where(f => f.PixelWidth <= 256)
                              .OrderByDescending(f => f.PixelWidth)
                              .FirstOrDefault()
                    ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            }
        }
        catch { /* not fatal */ }

        // Sweep stale project temp directories from previous crashed sessions.
        // 12-hour cutoff is generous; anything fresher is presumed to be a still-running studio.
        try { ProjectStore.CleanupStaleTempDirs(TimeSpan.FromHours(12)); } catch { }
        Closed += OnWindowClosed;

        // A portable app gets moved, and the .simplified association records an
        // absolute path. This writes only when that path has gone stale, so a
        // normal start touches the registry not at all - and does nothing
        // whatever if the association was never asked for.
        try { FileAssociation.RepairIfStale(); } catch { }

        // A file handed over by Explorer. Deferred to Loaded so the window is
        // on screen first: acting on one can take a moment, and a blank frozen
        // window is a bad first impression.
        //
        // A project opens; a template starts a new project from itself, which
        // is the only thing a template is for.
        if (App.StartupFile is { } startupFile)
        {
            // ContentRendered, not Loaded: Loaded fires before the first paint,
            // so a modal wizard opened there leaves the main window unpainted
            // behind it - which looked like the app had not started at all,
            // just a New project dialog on the desktop.
            ContentRendered += (_, _) =>
            {
                if (startupFile.EndsWith(ProjectStore.ProjectFileExtension, StringComparison.OrdinalIgnoreCase))
                    OpenProjectAt(startupFile);
                else
                    NewProjectFromTemplate(startupFile);
            };
        }

        // Restore persisted game directory.
        var cfg = UserConfig.Load();
        if (!string.IsNullOrWhiteSpace(cfg.GameDir))
        {
            GameDirBox.Text = cfg.GameDir;
        }
        else
        {
            // No saved game dir — auto-detect once the window is visible, so
            // the UI renders before we go walking the disk.
            Loaded += FirstLaunchDetectGameDir;
        }
        UpdateImportExportLabels();
    }

    /// <summary>
    /// One-shot game-directory autodetect for users who haven't configured it
    /// yet. Found → silently fill it in. Not found → stay quiet (user can still
    /// pick manually via Settings).
    /// </summary>
    private void FirstLaunchDetectGameDir(object sender, RoutedEventArgs e)
    {
        Loaded -= FirstLaunchDetectGameDir;   // run exactly once

        Simplified.Shared.GameDirDetector.Candidate? hit;
        try { hit = Simplified.Shared.GameDirDetector.Detect(); }
        catch { return; }

        var chosen = hit?.Path;
        if (string.IsNullOrEmpty(chosen)) return;

        // Writing GameDirBox.Text triggers GameDirBox_TextChanged, which
        // persists the value into UserConfig — no second Save needed here.
        GameDirBox.Text = chosen;
        Log($"Auto-detected game directory: {chosen}");
    }

    private string? GameDir =>
        string.IsNullOrWhiteSpace(GameDirBox.Text) ? null : GameDirBox.Text.Trim();

    /// <summary>
    /// The texture format Settings is set to — "DDS" or "PNG", never anything
    /// else. The shared half of the window needs this and the two apps keep
    /// their settings in differently-named files, so each states it here.
    /// </summary>
    private string TextureFileFormat =>
        TeamConfig.Load().FileFormat.ToUpperInvariant() is "PNG" ? "PNG" : "DDS";

    // ─────────────────────── File menu ─────────────────────────────────

    private void OpenErp_Click(object sender, RoutedEventArgs e)
    {
        // Loading a raw ERP doesn't compose with a project — the texture pane
        // would point outside the project's temp dir and "Save" would diverge.
        // Close the project (with the usual dirty prompt) first.
        if (_project is not null)
        {
            if (!ConfirmDiscardProject("open a standalone ERP")) return;
            CloseProjectInternal();
            OnProjectStateChanged();
        }
        var dlg = new OpenFileDialog
        {
            Title = "Open ERP",
            Filter = "ERP files|*.erp|All files|*.*"
        };
        if (dlg.ShowDialog() == true) LoadErp(dlg.FileName);
    }

    /// <summary>
    /// Load a standalone ERP — clears any currently-loaded ERPs first.
    /// Project-mode loading goes through <see cref="LoadErpFromProject"/>,
    /// which appends rather than replacing.
    /// </summary>
    private void LoadErp(string path)
    {
        if (_isDirty || _pendingMips.Count > 0)
        {
            var ans = ConfirmUnsavedChanges("You have unsaved changes.", "open the new ERP");

            switch (ans)
            {
                case MessageBoxResult.Yes:
                    try { FlushAllLoadedErps(); ClearDirty(); }
                    catch (Exception ex)
                    {
                        ShowMessage( $"Save failed:\n{ex.Message}", "Save failed",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    break;
                case MessageBoxResult.No:
                    _pendingMips.Clear();
                    ClearDirty();
                    break;
                default:
                    return;
            }
        }

        try
        {
            var erp = ErpFileIO.Load(path);
            _loadedErps.Clear();
            _loadedErps.Add(new LoadedErp(erp, path, UniqueDisplayErpName(Path.GetFileName(path))));
            FilePathLabel.Text = path;
            SaveButton.IsEnabled = true;
            SaveAsButton.IsEnabled = true;
            Log($"Loaded {path} — version {erp.Version}, {erp.Resources.Count} resources");
            RebuildTextureList();
            ClearDirty();
        }
        catch (Exception ex)
        {
            ShowMessage( $"Failed to open ERP:\n{ex.Message}", "Open failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RebuildTextureList()
    {
        _textures.Clear();
        if (!HasAnyErp) { UpdateCountLabel(); return; }

        // Advanced flag — when on, surface .material and .context resources too.
        var moreResources = TeamConfig.Load().EnableMoreResourceTypes;

        int failed = 0, pkgs = 0, xmls = 0, mats = 0, ctxs = 0, vtfs = 0;
        foreach (var loaded in _loadedErps)
        foreach (var r in loaded.File.Resources)
        {
            // Textures get their dedicated VM — same as before.
            if (r.ResourceType == "GfxSRVResource")
            {
                try { _textures.Add(new TextureVm(r, loaded.File, loaded.DisplayName)); }
                catch { failed++; }
                continue;
            }

            // Detect .material / .context by looking for a dot-separated
            // segment in the EAID file name. Plain EndsWith doesn't work for
            // .context because the engine often suffixes it with a shader-
            // model tag like ".sm51" (e.g. "foo.context.sm51"). Materials
            // can also carry trailing segments. A segment-equality check
            // matches both "foo.material" and "foo.material.bar".
            if (moreResources && HasNameSegment(r.FileName, "material"))
            {
                _textures.Add(new MaterialVm(r, loaded.File, loaded.DisplayName));
                mats++;
                continue;
            }

            // "context" in the name doesn't settle it. An EAID like
            // driver_gesture.idf?context=animstringtable names a context but
            // holds a PKG — incardriver_f1.erp has 380 of them alongside 13
            // genuine ones — so the name only decides once the content has had
            // its say. Materials need no such care: nothing named .material has
            // ever parsed as a PKG.
            //
            // Without this the flag above changes what a resource *is* rather
            // than merely whether it's shown: with it off those 380 read as
            // PKG/JSON, with it on they all turned to hex.
            var namedContext = moreResources && HasNameSegment(r.FileName, "context");

            // For anything else, peek at the first fragment and see if it parses
            // as PKG or XML. Fragments are tagged by content, not by resource
            // type, so trial-parsing is the cheapest reliable detector.
            if (r.Fragments.Count == 0)
            {
                // Nothing to trial-parse, so the name is all there is.
                if (namedContext)
                {
                    _textures.Add(new ContextVm(r, loaded.File, loaded.DisplayName));
                    ctxs++;
                }
                continue;
            }
            var first = r.Fragments[0];

            // Vehicle-handling resources are binary XML like any other, but they
            // define car performance, so they get their own category rather than
            // being lost among a thousand XML rows. The archive labels them, so
            // this doesn't have to guess from the file name.
            if (r.ResourceType == "VTF" && TryParseXml(first, out var vtf))
            {
                _textures.Add(new VtfVm(r, loaded.File, loaded.DisplayName, vtf));
                vtfs++;
                continue;
            }

            if (TryParsePkg(first, out var pkg))
            {
                _textures.Add(new PkgVm(r, loaded.File, loaded.DisplayName, pkg));
                pkgs++;
            }
            else if (namedContext)
            {
                // Didn't parse as a PKG, and the name says context — the hex
                // dump is the honest answer. Checked before XML so a context
                // that happens to parse as XML isn't dressed up as one.
                _textures.Add(new ContextVm(r, loaded.File, loaded.DisplayName));
                ctxs++;
            }
            else if (TryParseXml(first, out var xml))
            {
                _textures.Add(new XmlVm(r, loaded.File, loaded.DisplayName, xml));
                xmls++;
            }
            // else: opaque resource — not surfaced yet.
        }
        if (failed > 0) Log($"  ({failed} texture resources could not be parsed and were skipped)");
        if (pkgs + xmls + mats + ctxs + vtfs > 0)
            Log($"  surfaced {pkgs} PKG + {xmls} XML + {mats} MAT + {ctxs} CON + {vtfs} VTF resource(s)");
        // Stamp per-group totals so the group header can show a constant count
        // that doesn't shrink when the filter narrows visible rows.
        foreach (var grp in _textures.GroupBy(t => t.ParentDisplayName))
        {
            var c = grp.Count();
            foreach (var item in grp) item.ParentGroupCount = c;
        }

        // And where each ERP sits in the project's arrangement, so the panes
        // agree on order. Matched on the real file name, since the display name
        // has had its NNN-- prefix removed — which is also why the order cannot
        // simply be read back off the name the list shows.
        // Cleared whether or not there is a project: the entries hold ErpFile
        // references, and a stale one would keep a closed archive alive.
        _erpOrder.Clear();

        if (_project is not null)
        {
            var arrangement = ProjectArrangement.Load(_project);

            // Keyed by the ErpFile a row already points at, so the comparer can
            // look an order up without searching the loaded list per comparison.
            foreach (var loaded in _loadedErps)
                _erpOrder[loaded.File] = arrangement.OrderOf(_project.RelativePartPath(loaded.Path));

            // Rows were sorted into the view as they were added, before they
            // had an order to sort by, so the view has to be told to re-sort.
            _texturesView.Refresh();
        }
        RestoreModifiedFlags();
        RebuildKindChips();
        UpdateCountLabel();
    }

    /// <summary>
    /// True if <paramref name="fileName"/> contains a token equal to
    /// <paramref name="segment"/> (case-insensitive), where tokens are
    /// delimited by any of '.', '=', '/', '\\'. Used to classify
    /// .material / .context resources whose EAIDs may carry trailing
    /// segments like ".sm51" or "=&lt;identifier&gt;" (e.g.
    /// "foo.context=abcd1234"). The base name (the part before the first
    /// delimiter) is excluded so a resource literally called
    /// "mycontext.tif" doesn't falsely match.
    /// </summary>
    // EAID identifiers can mix path separators ('/', '\\') with dot-extensions
    // ('.material', '.context.sm51'), query-style suffixes ('haas.emb?context=65',
    // '.material=&lt;hash&gt;'), and URL-flavour separators ('&', '#').
    // Splitting on any of these isolates the "context" / "material" tokens
    // regardless of which flavour the engine chose.
    private static readonly char[] NameSegmentDelimiters = { '.', '=', '/', '\\', '?', '&', '#' };
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!HasAnyErp) return;

        // Project mode: route through SaveProject so the .simplified gets rezipped.
        if (_project is not null) { SaveProject_Click(sender, e); return; }

        // Legacy: one ERP loaded — write it back to its path.
        try
        {
            FlushAllLoadedErps();
            ClearDirty();
            if (_undoErpBytes is not null) _undoSavedSince = true;
        }
        catch (Exception ex)
        {
            ShowMessage( $"Save failed:\n{ex.Message}", "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (!HasAnyErp) return;

        // In project mode "Save As" means save the whole project somewhere new.
        if (_project is not null) { SaveProjectAs_Click(sender, e); return; }

        // Legacy: single loaded ERP — let the user choose a new .erp path.
        var loaded = _loadedErps[0];
        var dlg = new SaveFileDialog
        {
            Title = "Save ERP as",
            Filter = "ERP files|*.erp|All files|*.*",
            FileName = Path.GetFileName(loaded.Path),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var flushed = _pendingMips.Flush();
            ErpFileIO.Save(loaded.File, dlg.FileName);
            loaded.Path = dlg.FileName;
            FilePathLabel.Text = loaded.Path;
            Log($"Saved {loaded.Path}" + (flushed > 0 ? $"  (+ {flushed} .mipmaps file(s))" : ""));
            ClearDirty();
            if (_undoErpBytes is not null) _undoSavedSince = true;
        }
        catch (Exception ex)
        {
            ShowMessage( $"Save failed:\n{ex.Message}", "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Serialize every loaded ERP back to its path and flush any pending .mipmaps
    /// files. Called by both legacy Save and the project save paths; the latter
    /// follows up with a rezip.
    /// </summary>
    private void FlushAllLoadedErps()
    {
        var flushed = _pendingMips.Flush();
        foreach (var loaded in _loadedErps)
        {
            ErpFileIO.Save(loaded.File, loaded.Path);
            Log($"Saved {loaded.DisplayName}");
        }
        if (flushed > 0) Log($"  flushed {flushed} .mipmaps file(s)");
    }

    // ─────────────────────── Window chrome ────────────────────────────
    //
    // The title bar is ours, but the window is still an ordinary one:
    // WindowChrome (in the XAML) keeps the native resize borders, Aero Snap and
    // snap layouts, so only the painting and the three buttons are hand-rolled.

    // ── Maximising to the work area ────────────────────────────────────
    //
    // A window with no standard frame maximises to the whole monitor, border
    // and all, so its outer edges end up beyond the visible area: the top and
    // bottom of the layout get cut off and the taskbar is covered.
    //
    // Answering WM_GETMINMAXINFO with the monitor's work area fixes it at the
    // source. The alternative — insetting the content by a margin while
    // maximised — means guessing the border width, which is DPI-dependent and
    // wrong on any secondary monitor at a different scale.


    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Above the unsaved-changes question and above the early return below:
        // a board open in PureRef is work that hasn't reached the project at
        // all, so the project can be perfectly clean and there still be
        // something to lose when its working folder goes.
        if (!ConfirmBoardsClosed()) { e.Cancel = true; return; }

        if (!_isDirty && _pendingMips.Count == 0) return;

        var ans = ConfirmUnsavedChanges("You have unsaved changes.", "close");

        switch (ans)
        {
            case MessageBoxResult.Yes:
                try
                {
                    if (_project is not null) { FlushAllLoadedErps(); ProjectStore.Save(_project); }
                    else if (HasAnyErp) FlushAllLoadedErps();
                }
                catch (Exception ex)
                {
                    ShowMessage( $"Save failed:\n{ex.Message}\n\nClose anyway?",
                        "Save failed", MessageBoxButton.YesNo, MessageBoxImage.Error);
                    // If the user picked No, cancel the close.
                    // Re-using the Yes/No result here as a simple confirmation.
                    e.Cancel = true;
                }
                break;
            case MessageBoxResult.No:
                _pendingMips.Clear();
                ClearDirty();
                break;
            default:
                e.Cancel = true;
                break;
        }
    }

    /// <summary>Hook into Window.Closed (fires after Closing succeeds) for project temp-dir cleanup.</summary>
    private void OnWindowClosed(object? sender, EventArgs e) => CloseProjectInternal();

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var items = (string[])e.Data.GetData(DataFormats.FileDrop);
        var erp = items.FirstOrDefault(f => f.EndsWith(".erp", StringComparison.OrdinalIgnoreCase));
        if (erp is not null) LoadErp(erp);
    }

    // ─────────────────────── Filter + selection ────────────────────────

    private void UpdateCountLabel()
    {
        int total = _textures.Count;
        int shown = _texturesView is null ? total : _texturesView.OfType<ResourceListItem>().Count();
        CountLabel.Text = total == 0 ? "" : (shown == total ? $"{total}" : $"{shown}/{total}");
        SetBulkButtonsEnabled(true);
    }

    // ─────────────────────── Bulk export / import ─────────────────────
    //
    // These act across every ERP the project has loaded, not just one, so a
    // file's path carries the ERP it belongs to. Layout is
    //     <chosen folder>/<erp>/<resource folder>/<resource name>
    // which keeps two ERPs that hold a same-named resource apart and lets the
    // import side find each file again without guessing.

    /// <summary>Display order for the bulk picker.</summary>
    private static readonly ResourceKind[] BulkKindOrder =
    {
        ResourceKind.Texture, ResourceKind.Material, ResourceKind.Context,
        ResourceKind.Pkg, ResourceKind.Xml, ResourceKind.Vtf,
    };

    private void SetBulkButtonsEnabled(bool enabled)
    {
        var anyRows = _textures.Count > 0;
        ExportAllButton.IsEnabled = enabled && anyRows;
        ImportAllButton.IsEnabled = enabled && anyRows;

        // Export/Import belong to the selection, so hand them back to the
        // selection logic rather than guessing a state here.
        if (enabled) UpdateSelectionActions();
        else { ExportButton.IsEnabled = false; ImportButton.IsEnabled = false; }
    }

    /// <summary>
    /// Tick-list of the resource kinds present across every loaded ERP, so one
    /// run can cover any mix of them. Everything present starts ticked.
    /// Returns null when cancelled or when there's nothing to offer.
    /// </summary>
    private HashSet<ResourceKind>? PickResourceKinds(string verb)
    {
        var counts = new Dictionary<ResourceKind, int>();
        foreach (var r in _textures)
            counts[r.Kind] = counts.TryGetValue(r.Kind, out var n) ? n + 1 : 1;

        var present = BulkKindOrder.Where(counts.ContainsKey).ToList();
        if (present.Count == 0)
        {
            ShowMessage( "There are no resources this app can handle loaded.",
                $"{verb} all", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"Which resource types should be {verb.ToLowerInvariant()}ed?",
            Margin = new Thickness(0, 0, 0, 4),
        });
        var erpCount = _loadedErps.Count;
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = erpCount == 1
                ? "Across the loaded ERP."
                : $"Across all {erpCount} loaded ERPs — each file is filed under its ERP's name.",
            Foreground = ThemeBrush("TextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var boxes = new List<(ResourceKind Kind, System.Windows.Controls.CheckBox Box)>();
        foreach (var kind in present)
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Content = $"{ResourceListItem.TagFor(kind)}  —  {ResourceListItem.PluralFor(kind)} ({counts[kind]})",
                IsChecked = true,
                Margin = new Thickness(0, 5, 0, 5),
            };
            root.Children.Add(box);
            boxes.Add((kind, box));
        }

        var ok     = new System.Windows.Controls.Button { Content = verb,     IsDefault = true, Width = 90, Margin = new Thickness(0, 18, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel  = true, Width = 90, Margin = new Thickness(0, 18, 0, 0) };
        var btnRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);
        root.Children.Add(btnRow);

        var win = new Window
        {
            Title = $"{verb} all",
            Content = root,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 420, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);
        ok.Click += (_, _) => { win.DialogResult = true; };
        if (win.ShowDialog() != true) return null;

        var picked = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Kind).ToHashSet();
        if (picked.Count == 0)
        {
            ShowMessage( "No resource types were ticked.",
                $"{verb} all", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        return picked;
    }

    /// <summary>
    /// Where a resource's file sits under a chosen root:
    /// <c>&lt;root&gt;/&lt;erp&gt;/&lt;resource folder&gt;/&lt;name&gt;</c>.
    /// Shared by export and import so a round-trip lands on the same path.
    /// </summary>
    private static string BulkPathFor(ResourceListItem vm, string root, string textureExt)
    {
        var dir = Path.Combine(root, SafeFolderName(vm.ParentDisplayName));
        if (!string.IsNullOrEmpty(vm.Folder)) dir = Path.Combine(dir, vm.Folder);
        var name = vm is TextureVm
            ? vm.FileName.Replace("?", "%3F") + textureExt
            : ResourceFileIO.FileNameFor(vm);
        return Path.Combine(dir, name);
    }

    /// <summary>ERP display names come from file names, but strip anything a folder can't hold.</summary>
    private static string SafeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "erp" : name;
    }

    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (!HasAnyErp) return;

        var kinds = PickResourceKinds("Export");
        if (kinds is null) return;

        await RunBulkExport(_textures.Where(r => kinds.Contains(r.Kind)).ToList(),
            "Choose output folder");
    }

    /// <summary>
    /// The shared export run: pick a folder, then write every row in
    /// <paramref name="selected"/> to
    /// <c>&lt;folder&gt;/&lt;erp&gt;/&lt;resource folder&gt;/&lt;name&gt;</c>.
    /// </summary>
    private async Task RunBulkExport(IReadOnlyList<ResourceListItem> selected, string folderPrompt)
    {
        var fmt = TeamConfig.Load().FileFormat.ToUpperInvariant();
        if (fmt != "DDS" && fmt != "PNG") fmt = "PNG";
        var ext = fmt == "DDS" ? ".dds" : ".png";

        var dlg = new OpenFolderDialog { Title = folderPrompt };
        if (dlg.ShowDialog() != true) return;
        var outDir = dlg.FolderName;
        var gd = GameDir;

        // PNG goes through texconv → not every BCn format converts. DDS is a
        // straight copy of the existing pixels, so every format works. Only
        // textures are ever skipped for this reason.
        var targets = selected
            .Where(r => r is not TextureVm t || fmt == "DDS" || TexconvRunner.IsSupportedForPngConversion(t.ImageType))
            .ToList();
        var skipped = selected.Count - targets.Count;
        if (targets.Count == 0)
        {
            ShowMessage(
                fmt == "PNG"
                    ? "Nothing to export — no textures are supported for PNG export (BC6H/HDR not handled)."
                    : "Nothing to export.",
                "Nothing to export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBulkButtonsEnabled(false);
        try
        {
            int ok = 0, fail = 0;
            await Task.Run(() =>
            {
                foreach (var vm in targets)
                {
                    try
                    {
                        var outPath = BulkPathFor(vm, outDir, ext);
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

                        string note;
                        if (vm is TextureVm tex)
                        {
                            var r = fmt == "DDS"
                                ? TextureIO.ExportDds(tex, outPath, gd, 0, _pendingMips)
                                : TextureIO.ExportPng(tex, outPath, _runner, gd, 0, _pendingMips);
                            note = $"{r.Width}×{r.Height}";
                        }
                        else
                        {
                            ResourceFileIO.Export(vm, outPath);
                            note = vm.KindTag;
                        }
                        ok++;
                        Dispatcher.BeginInvoke(() => Log($"  ✓ {vm.ParentDisplayName}  {vm.FileName}  {note}"));
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Dispatcher.BeginInvoke(() => Log($"  ✗ {vm.ParentDisplayName}  {vm.FileName}: {ex.Message}"));
                    }
                }
            });
            var erpsTouched = targets.Select(r => r.ParentDisplayName).Distinct().Count();
            Log($"Exported {ok} of {targets.Count} resource(s) from {erpsTouched} ERP(s) to {outDir}" +
                $"  [{KindSummary(targets)}]" +
                (skipped > 0 ? $" ({skipped} unsupported skipped)" : "") +
                (fail > 0 ? $", {fail} failed" : "") + ".");
        }
        finally
        {
            SetBulkButtonsEnabled(true);
        }
    }

    private async void ImportAll_Click(object sender, RoutedEventArgs e)
    {
        if (!HasAnyErp) return;

        var kinds = PickResourceKinds("Import");
        if (kinds is null) return;

        var fmt = TeamConfig.Load().FileFormat.ToUpperInvariant();
        if (fmt != "DDS" && fmt != "PNG") fmt = "PNG";
        var ext = fmt == "DDS" ? ".dds" : ".png";

        var dlg = new OpenFolderDialog { Title = "Choose folder containing the files to import" };
        if (dlg.ShowDialog() != true) return;
        var inDir = dlg.FolderName;
        var gd = GameDir;

        var toImport = _textures
            .Where(r => kinds.Contains(r.Kind))
            .Where(r => r is not TextureVm t || fmt == "DDS" || TexconvRunner.IsSupportedForPngConversion(t.ImageType))
            .Select(vm => (Vm: vm, Path: BulkPathFor(vm, inDir, ext)))
            .Where(p => File.Exists(p.Path))
            .ToList();

        if (toImport.Count == 0)
        {
            ShowMessage(
                $"No matching files found under:\n{inDir}\n\n" +
                "Expected layout:\n<chosen-folder>\\<erp>\\<resource-folder>\\<resource-name>\n\n" +
                $"Textures carry {ext}; PKG .json; XML and VTF .xml; Material and Context keep their own extension.",
                "Nothing to import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var erpsTouched = toImport.Select(p => p.Vm.ParentDisplayName).Distinct().Count();
        var confirm = ShowMessage(
            $"Import {toImport.Count} file(s) across {erpsTouched} ERP(s)?  [{KindSummary(toImport.Select(p => p.Vm))}]\n\n" +
            "Each will be applied to its matching resource. The changes " +
            "apply in memory — remember to Save when done.",
            "Import all", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        // The list is rebuilt afterwards when non-texture resources changed, so
        // remember what was selected and restore it once the rows are new.
        var selectedIdentifier = (TextureList.SelectedItem as ResourceListItem)?.Identifier;
        var rebuildNeeded = toImport.Any(p => p.Vm is not TextureVm);

        SetBulkButtonsEnabled(false);
        try
        {
            TakeUndoSnapshot($"import {toImport.Count} file(s)");
            int ok = 0, fail = 0;
            var imported = new List<ResourceListItem>();
            await Task.Run(() =>
            {
                foreach (var p in toImport)
                {
                    try
                    {
                        string note;
                        if (p.Vm is TextureVm tex)
                        {
                            var r = fmt == "DDS"
                                ? TextureIO.ImportDds(tex, p.Path, gd, 0, _pendingMips)
                                : TextureIO.ImportPng(tex, p.Path, _runner, gd, 0, _pendingMips);
                            note = $"{r.Width}×{r.Height}";
                        }
                        else
                        {
                            ResourceFileIO.Import(p.Vm, p.Path);
                            note = p.Vm.KindTag;
                        }
                        ok++;
                        imported.Add(p.Vm);
                        Dispatcher.BeginInvoke(() => Log($"  ✓ {p.Vm.ParentDisplayName}  {p.Vm.FileName}  {note}"));
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Dispatcher.BeginInvoke(() => Log($"  ✗ {p.Vm.ParentDisplayName}  {p.Vm.FileName}: {ex.Message}"));
                    }
                }
            });

            if (ok > 0) MarkDirty(imported);
            Log($"Imported {ok} of {toImport.Count} file(s)" +
                (fail > 0 ? $", {fail} failed" : "") + ".");

            // PKG / XML rows cache their parsed form, so they have to be rebuilt
            // from the new bytes before the preview can show them.
            if (ok > 0 && rebuildNeeded)
            {
                RebuildTextureList();
                if (selectedIdentifier is not null) ReselectByIdentifier(selectedIdentifier);
            }
            else
            {
                ShowPreview(TextureList.SelectedItem as TextureVm);
            }
        }
        finally
        {
            SetBulkButtonsEnabled(true);
        }
    }

    private void TextureList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _sliceIndex = 0;

        // The list is Extended-select, so Ctrl+click, Shift+click and Ctrl+A
        // can put several rows in play. There's nothing sensible to preview for
        // a mixed bag, so say how many are held instead.
        var selected = SelectedResources();
        if (selected.Count > 1) ShowMultiSelectionPreview(selected);
        else ShowPreviewForItem(selected.Count == 1 ? selected[0] : null);
    }

    /// <summary>Every row currently selected, in list order.</summary>
    private List<ResourceListItem> SelectedResources() =>
        TextureList.SelectedItems.OfType<ResourceListItem>().ToList();

    /// <summary>
    /// Stand-in preview for a multi-row selection: the count, plus the per-kind
    /// breakdown so it's clear what Export is about to take.
    /// </summary>
    private void ShowMultiSelectionPreview(IReadOnlyList<ResourceListItem> selected)
    {
        StructuredPanel.Visibility = Visibility.Collapsed;
        SetPreviewImage(null);
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Collapsed;
        ExportAllSlicesButton.Visibility = Visibility.Collapsed;
        UpdateSliceBar(null);
        ClearMeta();

        PreviewMessage.Visibility = Visibility.Visible;
        PreviewMessage.Text = $"{selected.Count} resources selected\n{KindSummary(selected)}";

        MetaName.Text = $"{selected.Count} resources selected";

        UpdateSelectionActions();
    }

    /// <summary>
    /// Export / Import state for whatever is selected. One row behaves as it
    /// always has — the label follows the kind. Several rows turn Export into
    /// "Export selected", which writes a file each, and Import into "Import
    /// into selected", which is the opposite shape rather than the mirror: one
    /// chosen file goes into every row held.
    /// </summary>
    private void UpdateSelectionActions()
    {
        var selected = SelectedResources();
        if (selected.Count <= 1)
        {
            // UpdateActionButtons restores the single-row labels and visibility.
            UpdateActionButtons(selected.Count == 1 ? selected[0] : null);
            return;
        }

        ExportButton.Content    = "Export selected";
        ExportButton.IsEnabled  = true;

        ImportButton.Content    = "Import into selected";
        ImportButton.IsEnabled  = true;
        ImportButton.Visibility = Visibility.Visible;
        ImportButton.ToolTip    = "Put one file into every selected resource, replacing what is there";
    }

    /// <summary>
    /// Relabel and route the Export/Import buttons based on the selected
    /// resource kind: PNG/DDS for textures, JSON for PKG, XML for XML.
    /// Disables them when nothing's selected.
    /// </summary>
    private void UpdateActionButtons(ResourceListItem? item)
    {
        // VtfVm derives from XmlVm, so it has to be matched first or it'd be
        // labelled XML. Same ordering rule applies to every switch below.
        var (label, enabled) = item switch
        {
            TextureVm  => (TeamConfig.Load().FileFormat.ToUpperInvariant(), true),
            PkgVm      => ("JSON", true),
            VtfVm      => ("VTF",  true),
            XmlVm      => ("XML",  true),
            MaterialVm => ("Material", true),
            ContextVm  => ("Context", true),
            // Nothing selected — no format to name, so the buttons read plain
            // "Export" / "Import" rather than guessing one.
            _          => ("", false),
        };
        ExportButton.Content   = $"Export {label}".TrimEnd();
        ImportButton.Content   = $"Import {label}".TrimEnd();
        ExportButton.IsEnabled = enabled;
        ImportButton.IsEnabled = enabled;

        // Undo what a multi-row selection did to Import. This has to live here
        // rather than in UpdateSelectionActions: the single-row path arrives
        // via ShowPreviewForItem, which calls straight into this.
        ImportButton.Visibility = Visibility.Visible;
        ImportButton.ToolTip    = null;
    }

    // ─────────────────────── Tex-array slice nav ───────────────────────

    /// <summary>
    /// Keyboard shortcuts:
    ///   Left / Right         — step through tex-array slices
    ///   Ctrl+O               — Open ERP…
    ///   Ctrl+S               — Save
    ///   Ctrl+Shift+S         — Save As…
    ///   Ctrl+Z               — Undo last mutation (one level)
    /// Up / Down already navigate the texture list via the ListBox's built-in
    /// handling, so we leave those alone. All shortcuts skip when focus is in
    /// a text input so typing still works.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Typing in a box owns the plain keys — the arrows below would step
        // through texture slices mid-word. It never owned the Ctrl ones, and
        // swallowing those meant Ctrl+S did nothing while the filter had focus.
        var typing = System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox;

        var mods = System.Windows.Input.Keyboard.Modifiers;
        var ctrl  = (mods & System.Windows.Input.ModifierKeys.Control) != 0;
        var shift = (mods & System.Windows.Input.ModifierKeys.Shift)   != 0;

        if (ctrl)
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.O when !shift:
                    OpenErp_Click(sender, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.S when shift && (_project is not null || SaveAsButton.IsEnabled):
                    SaveAs_Click(sender, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.S when !shift && (_project is not null || SaveButton.IsEnabled):
                    Save_Click(sender, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.Z when !shift:
                    Undo();
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.T when !shift:
                    // Advanced menu is gated behind a "team.simplified" marker
                    // file next to the exe. Without it, Ctrl+T does nothing.
                    if (TeamUnlocked)
                    {
                        OpenAdvancedDialog();
                        e.Handled = true;
                    }
                    return;
            }
        }

        if (typing) return;
        if (e.Key != System.Windows.Input.Key.Left && e.Key != System.Windows.Input.Key.Right) return;

        var vm = TextureList.SelectedItem as TextureVm;
        if (vm is null || vm.ArraySize <= 1) return;

        if (e.Key == System.Windows.Input.Key.Left)
            PrevSlice_Click(sender, new RoutedEventArgs());
        else
            NextSlice_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    // ─────────────────────── Undo ─────────────────────────────────────
    //
    // Single-level: snapshot the ERP + pending-mips state before each
    // mutating operation. Ctrl+Z restores them. After restoring, the snapshot
    // is consumed — there's no redo and no chain. This is plenty for the
    // "oops, didn't mean that" case without the memory cost of multi-level.

    private void TakeUndoSnapshot(string description)
    {
        if (!HasAnyErp) return;
        try
        {
            // Snapshot every loaded ERP — bulk ops can touch multiple at once
            // (or just one); keeping the model uniform is simpler than per-op
            // scoping. Cost scales with project size; acceptable for now.
            var dict = new Dictionary<LoadedErp, byte[]>(_loadedErps.Count);
            foreach (var loaded in _loadedErps)
            {
                using var ms = new MemoryStream();
                loaded.File.Write(ms);
                dict[loaded] = ms.ToArray();
            }
            _undoErpBytes = dict;
            _undoPendingMipsSnap = _pendingMips.CaptureSnapshot();
            _undoWasDirty = _isDirty;
            _undoSavedSince = false;
            _undoDescription = description;
            _undoSelectedIdentifier = (TextureList.SelectedItem as TextureVm)?.Identifier;
            _undoSliceIndex = _sliceIndex;
            _undoModified = new HashSet<string>(_modifiedIds, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Failing to snapshot shouldn't block the user from doing the op.
            Log($"  (couldn't capture undo snapshot: {ex.Message})");
            _undoErpBytes = null;
            _undoPendingMipsSnap = null;
            _undoDescription = null;
        }
    }

    private void Undo()
    {
        if (_undoErpBytes is null || _undoPendingMipsSnap is null)
        {
            Log("Nothing to undo.");
            return;
        }
        try
        {
            // Reparse each snapshotted ERP and swap in. LoadedErp entries that
            // didn't exist when the snapshot was taken (rare — would mean an
            // ERP was added after the op) are left alone.
            foreach (var (loaded, bytes) in _undoErpBytes)
            {
                using var ms = new MemoryStream(bytes, writable: false);
                var erp = new EgoEngineLibrary.Archive.Erp.ErpFile();
                erp.Read(ms);
                loaded.File = erp;
            }
            _pendingMips.RestoreSnapshot(_undoPendingMipsSnap);
            RebuildTextureList();

            if (!string.IsNullOrEmpty(_undoSelectedIdentifier))
            {
                var match = _textures.FirstOrDefault(t => t.Identifier == _undoSelectedIdentifier);
                if (match is not null)
                {
                    TextureList.SelectedItem = match;
                    TextureList.ScrollIntoView(match);
                    _sliceIndex = _undoSliceIndex;
                    ShowPreview(match as TextureVm);   // preview is texture-only; PKG/XML rows clear it
                }
            }

            // Mark dirty if the snapshot was already dirty OR a save happened
            // after we took it (in which case in-memory now disagrees with disk).
            if (_undoWasDirty || _undoSavedSince) MarkDirty(); else ClearDirty();

            // ClearDirty above wipes the set, so restoring the snapshot's own
            // record of what was edited has to come after it.
            if (_undoModified is not null)
            {
                _modifiedIds.Clear();
                foreach (var id in _undoModified) _modifiedIds.Add(id);
                RestoreModifiedFlags();
            }
            Log($"Undid: {_undoDescription ?? "last action"}"
                + (_undoSavedSince ? "  (file on disk no longer matches — save again to commit)" : ""));
        }
        catch (Exception ex)
        {
            Log($"!! Undo failed: {ex.Message}");
            ShowMessage( $"Undo failed:\n{ex.Message}", "Undo failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // Single-level: snapshot is consumed.
            _undoErpBytes = null;
            _undoPendingMipsSnap = null;
            _undoDescription = null;
            _undoSelectedIdentifier = null;
        }
    }

    private void ShowPreview(TextureVm? vm)
    {
        ExportButton.IsEnabled = vm is not null;
        ImportButton.IsEnabled = vm is not null;
        ExportAllSlicesButton.Visibility = vm is not null && vm.ArraySize > 1
            ? Visibility.Visible : Visibility.Collapsed;

        StructuredPanel.Visibility = Visibility.Collapsed;

        // Switch the preview layer back to image mode.
        PreviewText.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;

        UpdateSliceBar(vm);

        if (vm is null)
        {
            SetPreviewImage(null);
            PreviewMessage.Visibility = Visibility.Visible;
            PreviewMessage.Text = "Select a resource to preview it.";
            ClearMeta();
            return;
        }

        // Metadata
        MetaName.Text       = vm.FileName;
        MetaFormat.Text     = vm.ImageType.ToString();
        MetaResolution.Text = $"{vm.FullWidth} × {vm.FullHeight}" +
                              (vm.HasExternalMips ? $"   (base in ERP: {vm.Width} × {vm.Height})" : "");
        MetaMips.Text       = vm.HasExternalMips ? $"{vm.TotalMipLevels} ({vm.Srv.SurfaceRes.Frag2.Mips.Count} external)" : vm.MipMapCount.ToString();
        MetaArraySize.Text  = vm.ArraySize.ToString();
        MetaMipFile.Text    = string.IsNullOrEmpty(vm.MipMapFileName) ? "(none)" : vm.MipMapFileName;
        MetaIdentifier.Text = vm.Identifier;

        // Image
        var (bmp, error, usedFullRes) = TexturePreview.DecodeBaseImage(vm, _runner, GameDir, _sliceIndex, _pendingMips);
        if (bmp is not null)
        {
            SetPreviewImage(bmp);
            PreviewMessage.Visibility = Visibility.Collapsed;
            if (vm.HasExternalMips && !usedFullRes)
            {
                // Texture has external mips but we couldn't load them — show a hint.
                MetaResolution.Text += "    [base image only — set game folder for full-res]";
            }
        }
        else
        {
            SetPreviewImage(null);
            PreviewMessage.Visibility = Visibility.Visible;
            PreviewMessage.Text = $"Couldn't render preview:\n{error}";
        }
    }

    // ─────────────────────── Export / Import ───────────────────────────

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        // Several rows held: the button reads "Export selected" and runs the
        // same job Export all does, narrowed to the selection. No type picker —
        // the selection already says exactly what to take.
        var selection = SelectedResources();
        if (selection.Count > 1)
        {
            await RunBulkExport(selection,
                $"Choose output folder for {selection.Count} selected resource(s)");
            return;
        }

        // Route non-texture rows to their format-specific exporters first.
        switch (TextureList.SelectedItem)
        {
            case PkgVm pkg:      ExportPkg(pkg); return;
            case XmlVm xml:      ExportXml(xml); return;
            case MaterialVm mat: ExportRaw(mat, "material"); return;
            case ContextVm ctx:  ExportRaw(ctx,  "context");  return;
        }
        var vm = TextureList.SelectedItem as TextureVm;
        if (vm is null) return;

        var fmt = TeamConfig.Load().FileFormat.ToUpperInvariant();
        if (fmt != "DDS" && fmt != "PNG") fmt = "DDS";

        // PNG can only do LDR-ish formats — texconv refuses BC6H/HDR. DDS export
        // is format-agnostic (we just dump the on-disk bytes) so it's fine.
        if (fmt == "PNG" && !TexconvRunner.IsSupportedForPngConversion(vm.ImageType))
        {
            ShowMessage(
                $"This texture is {vm.ImageType}, which can't be converted to PNG in this version (BC6H/HDR not supported). " +
                "Switch the file format to DDS in Settings to export it directly.",
                "Unsupported format", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sliceSuffix = vm.ArraySize > 1 ? $".slice{_sliceIndex}" : "";
        var ext = fmt == "DDS" ? ".dds" : ".png";
        var filter = fmt == "DDS" ? "DDS image|*.dds" : "PNG image|*.png";
        var dlg = new SaveFileDialog
        {
            Title = vm.ArraySize > 1
                ? $"Export slice {_sliceIndex + 1}/{vm.ArraySize} of {vm.FileName} as {fmt}"
                : $"Export texture as {fmt}",
            Filter = filter,
            FileName = vm.FileName.Replace("?", "%3F") + sliceSuffix + ext,
        };
        if (dlg.ShowDialog() != true) return;

        ExportButton.IsEnabled = false;
        try
        {
            var gd = GameDir;
            var idx = _sliceIndex;
            var result = fmt == "DDS"
                ? await Task.Run(() => TextureIO.ExportDds(vm, dlg.FileName, gd, idx, _pendingMips))
                : await Task.Run(() => TextureIO.ExportPng(vm, dlg.FileName, _runner, gd, idx, _pendingMips));
            var resTag = result.UsedFullRes ? " (full-res)" : (vm.HasExternalMips ? " (base only — game folder not set)" : "");
            var sliceTag = vm.ArraySize > 1 ? $" [slice {idx + 1}/{vm.ArraySize}]" : "";
            Log($"Exported {fmt} → {dlg.FileName}  {result.Width}×{result.Height}{resTag}{sliceTag}");
        }
        catch (Exception ex)
        {
            Log($"!! Export failed: {ex.Message}");
            ShowMessage( ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ExportButton.IsEnabled = true; }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        // Several rows held: one file goes into all of them. Export's
        // multi-row path fans out to a file each; this is the opposite shape,
        // not its mirror, which is the whole point of the button.
        var selection = SelectedResources();
        if (selection.Count > 1)
        {
            await ImportIntoSelected(selection);
            return;
        }

        switch (TextureList.SelectedItem)
        {
            case PkgVm pkg:      ImportPkg(pkg); return;
            case XmlVm xml:      ImportXml(xml); return;
            case MaterialVm mat: ImportRaw(mat, "material"); return;
            case ContextVm ctx:  ImportRaw(ctx,  "context");  return;
        }
        var vm = TextureList.SelectedItem as TextureVm;
        if (vm is null) return;

        var fmt = TeamConfig.Load().FileFormat.ToUpperInvariant();
        if (fmt != "DDS" && fmt != "PNG") fmt = "DDS";

        if (fmt == "PNG" && !TexconvRunner.IsSupportedForPngConversion(vm.ImageType))
        {
            ShowMessage(
                $"This texture is {vm.ImageType}, which can't be replaced from PNG in this version (BC6H/HDR not supported). " +
                "Switch the file format to DDS in Settings to import a same-format DDS directly.",
                "Unsupported format", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var filter = fmt == "DDS" ? "DDS image|*.dds|All files|*.*" : "PNG image|*.png|All files|*.*";
        var dlg = new OpenFileDialog
        {
            Title = vm.ArraySize > 1
                ? $"Import {fmt} into slice {_sliceIndex + 1}/{vm.ArraySize} of {vm.FileName} (existing format: {vm.ImageType})"
                : $"Import {fmt} into {vm.FileName} (existing format: {vm.ImageType})",
            Filter = filter,
        };
        if (dlg.ShowDialog() != true) return;

        ImportButton.IsEnabled = false;
        try
        {
            TakeUndoSnapshot($"import {vm.FileName}");
            var gd = GameDir;
            var idx = _sliceIndex;
            var result = fmt == "DDS"
                ? await Task.Run(() => TextureIO.ImportDds(vm, dlg.FileName, gd, idx, _pendingMips))
                : await Task.Run(() => TextureIO.ImportPng(vm, dlg.FileName, _runner, gd, idx, _pendingMips));
            var sliceTag = vm.ArraySize > 1 ? $" [slice {idx + 1}/{vm.ArraySize}]" : "";
            Log($"Imported {fmt} {dlg.FileName} → {vm.FileName}  ({result.Width}×{result.Height} {vm.ImageType}){sliceTag}");
            // A slice of a tex array is a fixed size, so the file was fitted to
            // it. Saying so beats the user wondering why their larger canvas
            // came back smaller.
            if (result.ResizedFrom is { } arrived)
                Log($"  resized from {arrived.Width}×{arrived.Height} to fit the array's slice size.");
            if (result.MipMapsWrittenTo is not null && !result.MipMapsPending)
            {
                Log(result.MipMapsWrittenTo.StartsWith(gd ?? "\0", StringComparison.OrdinalIgnoreCase)
                    ? $"  .mipmaps written into game folder: {result.MipMapsWrittenTo}"
                    : $"  .mipmaps sidecar written next to PNG: {result.MipMapsWrittenTo}  (set Game folder to write directly to the game install)");
            }
            MarkDirty(vm);
            ShowPreview(vm); // refresh preview + metadata
        }
        catch (Exception ex)
        {
            Log($"!! Import failed: {ex.Message}");
            ShowMessage( ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ImportButton.IsEnabled = true; }
    }

    /// <summary>
    /// One file into every selected resource.
    ///
    /// Not the mirror of "Export selected": that fans a selection out to a
    /// file each, while this pushes a single file into all of them. It is how
    /// one decal, livery or blank goes across a batch of textures without
    /// repeating the same import by hand.
    ///
    /// The file decides what it lands on — a row is a target when its kind
    /// takes that extension — so a mixed selection is fine and whatever does
    /// not fit is counted in the confirmation rather than refused outright.
    /// The picker only offers extensions something in the selection can take,
    /// so landing on nothing means a name typed past the filter.
    /// </summary>
    private async Task ImportIntoSelected(IReadOnlyList<ResourceListItem> selected)
    {
        var fmt = TeamConfig.Load().FileFormat.ToUpperInvariant();
        var dlg = new OpenFileDialog
        {
            Title = $"Choose one file to import into all {selected.Count} selected resources",
            Filter = ImportFilterFor(selected, fmt == "PNG" ? ".png" : ".dds"),
        };
        if (dlg.ShowDialog() != true) return;

        var source = dlg.FileName;
        var name = Path.GetFileName(source);
        var fromPng = Path.GetExtension(source).Equals(".png", StringComparison.OrdinalIgnoreCase);

        // PNG goes through texconv, which refuses BC6H/HDR, so those textures
        // are dropped here with a count rather than failing one at a time in
        // the loop. DDS is a straight copy of the pixels, so it always works.
        var accepted = selected.Where(r => ImportAccepts(r, source)).ToList();
        var targets = accepted
            .Where(r => r is not TextureVm t || !fromPng || TexconvRunner.IsSupportedForPngConversion(t.ImageType))
            .ToList();

        var wrongKind = selected.Count - accepted.Count;
        var unsupported = accepted.Count - targets.Count;

        if (targets.Count == 0)
        {
            ShowMessage(
                $"Nothing in the selection takes {name}.\n\n" +
                $"Selected: {KindSummary(selected)}" +
                (unsupported > 0
                    ? $"\n{unsupported} of them are BC6H/HDR textures, which can't be replaced from PNG."
                    : ""),
                "Nothing to import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var skipped = new List<string>();
        if (wrongKind > 0) skipped.Add($"{wrongKind} of a kind that doesn't take this file");
        if (unsupported > 0) skipped.Add($"{unsupported} BC6H/HDR texture(s), which can't come from PNG");

        var confirm = ShowMessage(
            $"Put {name} into {targets.Count} of {selected.Count} selected resource(s)?" +
            $"  [{KindSummary(targets)}]\n\n" +
            (skipped.Count > 0 ? $"Skipping {string.Join(" and ", skipped)}.\n\n" : "") +
            "The same file replaces the contents of every one of them. The " +
            "changes apply in memory — remember to Save when done.",
            "Import into selected", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        // Rows are rebuilt afterwards when a non-texture changed, which hands
        // back fresh objects, so the selection is remembered by identifier.
        var selectedIds = selected.Select(r => r.Identifier).ToList();
        var rebuildNeeded = targets.Any(r => r is not TextureVm);

        SetBulkButtonsEnabled(false);
        try
        {
            TakeUndoSnapshot($"import {name} into {targets.Count} resource(s)");
            var gd = GameDir;
            int ok = 0, fail = 0;
            var imported = new List<ResourceListItem>();
            await Task.Run(() =>
            {
                foreach (var vm in targets)
                {
                    try
                    {
                        string note;
                        if (vm is TextureVm tex)
                        {
                            var r = fromPng
                                ? TextureIO.ImportPng(tex, source, _runner, gd, 0, _pendingMips)
                                : TextureIO.ImportDds(tex, source, gd, 0, _pendingMips);
                            note = $"{r.Width}×{r.Height}";
                        }
                        else
                        {
                            ResourceFileIO.Import(vm, source);
                            note = vm.KindTag;
                        }
                        ok++;
                        imported.Add(vm);
                        Dispatcher.BeginInvoke(() => Log($"  ✓ {vm.ParentDisplayName}  {vm.FileName}  {note}"));
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Dispatcher.BeginInvoke(() => Log($"  ✗ {vm.ParentDisplayName}  {vm.FileName}: {ex.Message}"));
                    }
                }
            });

            if (ok > 0) MarkDirty(imported);
            Log($"Imported {name} into {ok} of {targets.Count} selected resource(s)" +
                (fail > 0 ? $", {fail} failed" : "") + ".");

            // PKG / XML rows cache their parsed form, so they have to be rebuilt
            // from the new bytes before the preview can show them.
            if (ok > 0 && rebuildNeeded)
            {
                RebuildTextureList();
                ReselectByIdentifiers(selectedIds);
            }
        }
        finally
        {
            SetBulkButtonsEnabled(true);
        }
    }


    /// <summary>
    /// Mark the project dirty and flag <paramref name="vm"/>'s row as edited.
    /// Use this wherever a change belongs to particular resources; the
    /// no-argument overload stays for project-level changes (renames, part
    /// selection) that no single row owns.
    /// </summary>
    private void MarkDirty(ResourceListItem vm) => MarkDirty(new[] { vm });

    private void MarkDirty(IEnumerable<ResourceListItem> vms)
    {
        foreach (var vm in vms)
        {
            _modifiedIds.Add(vm.Identifier);
            vm.IsModified = true;
        }
        MarkDirty();
    }

    // ─────────────────────── Preview channels ─────────────────────────

    private System.Windows.Media.Color EndColour(string key, System.Windows.Media.Color fallback)
        => TryFindResource(key) is System.Windows.Media.SolidColorBrush brush ? brush.Color : fallback;

    private void UpdateDirtyLabel()
    {
        var anyChange = _isDirty || _pendingMips.Count > 0;
        PendingLabel.Visibility = anyChange ? Visibility.Visible : Visibility.Collapsed;
        if (anyChange) PendingLabel.Text = "unsaved changes";
    }

    // ─────────────────────── Preview text ─────────────────────────────

    /// <summary>
    /// Highlighting is capped on spans, not characters: cost tracks the number
    /// of text runs built, and the two languages differ by an order of magnitude
    /// per character. Measured across the archives in References — the densest
    /// JSON is 162k chars in 11k spans (111ms), the densest XML 183k chars in
    /// 40k spans (254ms), since XML emits a run per bracket, name and value
    /// where JSON runs whole stretches of punctuation together.
    ///
    /// At roughly 6-10µs a span, this ceiling is about a third of a second in
    /// the worst case, and it leaves every document in those archives coloured.
    /// </summary>
    private const int HighlightSpanCap = 50_000;

    /// <summary>
    /// A guard on the scan itself, so something enormous isn't tokenised just to
    /// be thrown away by the span cap. Scanning is linear and cheap — 5ms for
    /// the largest real document — so this only has to exclude the absurd.
    /// </summary>
    private const int HighlightCharCap = 1_000_000;

    /// <summary>Which scanner, if any, colours the preview text.</summary>
    private enum PreviewLanguage { None, Json, Xml }

    // ─────────────────────── Material preview ─────────────────────────

    /// <summary>
    /// Raw bytes rather than the parse, for the resource on screen.
    ///
    /// Deliberately not sticky, unlike the preview channel: that is a way of
    /// looking at every texture, while this is an escape hatch for one file.
    /// Carried across selections, one click would leave every material and
    /// context afterwards showing hex, which reads as the parse having broken.
    /// </summary>
    private bool _structuredShowRaw;

    /// <summary>Which resource the flag above belongs to.</summary>
    private string? _structuredRawFor;

    // ─────────────────────── Log ───────────────────────────────────────

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    // ────────────────────────── v2 — Project bar ─────────────────────────
    //
    // The project lives as a .simplified zip on disk. While open, the contents
    // are extracted to a temp dir: one `ERPs/` folder holding every part, with
    // category folders inside it, and `info.json` naming which parts are
    // switched on. The texture pane shows those. Toggling a part is a write to
    // that list — the file never moves, so edits survive being switched off.

    private void NewProject_Click(object sender, RoutedEventArgs e) => NewProjectFromTemplate(null);

    /// <summary>
    /// The New project flow. <paramref name="templatePath"/> is the .serps to
    /// start from when Explorer handed one over, and null when the user came
    /// through the button and will pick one in the wizard.
    /// </summary>
    private void NewProjectFromTemplate(string? templatePath)
    {
        if (!ConfirmDiscardProject("create a new project")) return;

        var w = RunNewProjectWizard(templatePath);
        if (w is null) return;

        try
        {
            CloseProjectInternal();
            _project = w.IsCustom
                ? ProjectStore.CreateCustom(w.ModName, w.ModAuthor, w.CustomShaderPackage)
                : ProjectStore.CreateFromSerps(w.SerpsPath, w.ModName, w.ModAuthor);
            ProjectStore.Save(_project, w.SavePath);
            RememberProjectFolder(w.SavePath);
            OnProjectStateChanged();
            Log($"Created project: {w.SavePath}");
            Log($"  shader package: {_project.Info.ShaderPackage}");
            if (w.IsCustom)
            {
                Log("  custom template — use 'Choose ERPs' to add ERP files to this project.");
                // Open the ERPs chooser immediately so the user can populate the project.
                OpenErpsChooser(initial: true);
            }
            else
            {
                Log($"  parts available: {_project.AllPartPaths.Count()}");
                // Immediately ask the user which parts to enable.
                OpenPartsChooser(initial: true);
            }
        }
        catch (Exception ex)
        {
            ShowMessage( $"Couldn't create project:\n{ex.Message}",
                "New project failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardProject("open another project")) return;

        var cfg = UserConfig.Load();
        var dlg = new OpenFileDialog
        {
            Title = "Open project",
            Filter = $"Simplified Studio project|*{ProjectStore.ProjectFileExtension}|All files|*.*",
            InitialDirectory = (cfg.LastProjectFolder is { } d && Directory.Exists(d)) ? d : null
        };
        if (dlg.ShowDialog() != true) return;

        OpenProjectAt(dlg.FileName);
    }

    /// <summary>
    /// Open a project by path. The dialog and a double-click in Explorer both
    /// come through here, so a project opened either way is opened the same
    /// way - including the format check, which is the part it would be easy to
    /// skip on a second route.
    /// </summary>
    private void OpenProjectAt(string path)
    {
        try
        {
            if (!ConfirmProjectFormat(path)) return;

            CloseProjectInternal();

            // Behind a progress window, which only appears if this takes a
            // moment. The project's own log lines double as the progress line,
            // so what it says here is what it says in the log.
            _project = SimplifiedUi.ProgressDialog.Run(this,
                $"Opening {System.IO.Path.GetFileNameWithoutExtension(path)}",
                progress => ProjectStore.Open(path, line =>
                {
                    progress.Report(line);
                    Dispatcher.BeginInvoke(() => Log(line));
                }));
            RememberProjectFolder(path);
            OnProjectStateChanged();
            ReloadEnabledErps();
            Log($"Opened project: {path}");
        }
        catch (Exception ex)
        {
            ShowMessage( $"Couldn't open project:\n{ex.Message}",
                "Open project failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Check what a project file was written by, and get the user's agreement
    /// before converting an older one. Returns false to abandon the open.
    ///
    /// Converting rewrites every part's file name — the conflict digit moved
    /// from the front of the prefix to the back in 1.3 — so it isn't something
    /// to do to someone's project without asking.
    /// </summary>
    private bool ConfirmProjectFormat(string zipPath)
    {
        ProjectStore.Inspection inspection;
        try { inspection = ProjectStore.Inspect(zipPath); }
        catch { return true; }   // unreadable — let Open report the real problem

        if (inspection.Format == ProjectStore.ProjectFormat.Newer)
        {
            // Additive changes are the norm, so opening is more useful than
            // refusing — but say so, or a silently dropped field becomes a
            // mystery later.
            ShowMessage(
                $"This project was saved by Simplified Studio v{inspection.StudioVersion}, " +
                $"which is newer than this build (v{SimplifiedStudio.Core.Project.StudioVersion.Current}).\n\n" +
                "It will open, but anything the newer version added may be lost when you save.",
                "Newer project", MessageBoxButton.OK, MessageBoxImage.Warning);
            Log($"  project was saved by v{inspection.StudioVersion} — newer than this build.");
            return true;
        }

        if (inspection.Format != ProjectStore.ProjectFormat.Legacy) return true;

        return RunLegacyConvertDialog(System.IO.Path.GetFileName(zipPath));
    }

    /// <summary>
    /// "Convert" or "Close file" for a pre-1.3 project. Returns true to go ahead.
    /// </summary>
    private bool RunLegacyConvertDialog(string fileName)
        => RunChoiceDialog(
            "Older project",
            $"'{fileName}' was made on an older version of Simplified Studio.",
            "It will be converted to the latest version: parts move into a single "
          + "ERPs folder with your categories as folders inside it, and each part is "
          + "renumbered for the current ordering scheme.\n\n"
          + "The original file isn't touched until you save. Every rename is written "
          + "to the log.",
            ("Convert", MessageBoxResult.Yes),
            ("Close file", MessageBoxResult.No)) == MessageBoxResult.Yes;

    /// <summary>Clear the texture pane and load every part currently switched on.</summary>
    private void ReloadEnabledErps()
    {
        if (_project is null) return;
        _loadedErps.Clear();
        _textures.Clear();
        var paths = _project.EnabledErpPaths.ToList();
        foreach (var p in paths) LoadErpFromProject(p);
        if (paths.Count == 0)
        {
            // No enabled parts — collapse the per-loaded-ERP buttons.
            UpdateProjectFilePathLabel();
            SaveButton.IsEnabled = false;
            SaveAsButton.IsEnabled = false;
            Log("No enabled parts — use 'Choose parts…' to pick some.");
        }
        else
        {
            Log($"Loaded {paths.Count} enabled ERP(s).");
        }
    }

    /// <summary>
    /// Entry point for the "Choose parts" button. Opens a checkbox-list dialog
    /// over every part in the project, applies the user's enable/disable
    /// diff, then reloads the texture pane.
    /// </summary>
    private void ChooseParts_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        // Enable/disable is identical for template and custom projects — both
        // toggle parts on and off.
        OpenPartsChooser(initial: false);
    }

    /// <summary>
    /// "Edit project" — modal for editing the live mod info on an open project.
    /// Mod name and Author are editable; Shader package and SERPs template are
    /// shown read-only (they came from the original .serps template and aren't
    /// meant to drift). OK marks the project dirty so mod.json gets rewritten on save.
    /// </summary>
    /// <summary>
    /// "Notes" — the project's notes and moodboards.
    ///
    /// Everything it writes goes into the project's temp folder like any other
    /// edit, so the project still has to be saved afterwards for it to reach
    /// the .simplified on disk.
    /// </summary>
    private void Notes_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;

        // Modeless, and only ever one: notes are for keeping beside the work
        // rather than instead of it, so the main window stays live while it is
        // open and a second press brings the one already there forward.
        if (_notesWindow is not null) { _notesWindow.Activate(); return; }

        _notesWindow = new Notes.NotesWindow(_project, this);
        _notesWindow.ContentChanged += (_, _) => MarkDirty();
        _notesWindow.SaveRequested += (_, _) => SaveProject_Click(this, new RoutedEventArgs());
        _notesWindow.Closed += (_, _) => _notesWindow = null;
        _notesWindow.Show();
    }

    /// <summary>The notes window while it is open. Belongs to the open project.</summary>
    private Notes.NotesWindow? _notesWindow;

    private void EditProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        var info = _project.Info;
        var result = RunEditProjectDialog(info);
        if (result is null) return;

        // Apply the plain field edits first. Shader package is only editable
        // for custom-template projects — for SERPs-template ones the dialog
        // returns it read-only, so ignore changes to it there.
        var shaderChanged = info.IsCustomTemplate && info.ShaderPackage != result.ShaderPackage;
        var fieldsChanged =
            info.Name != result.ModName ||
            info.Author  != result.ModAuthor ||
            shaderChanged;
        if (fieldsChanged)
        {
            info.Name = result.ModName;
            info.Author  = result.ModAuthor;
            if (shaderChanged) info.ShaderPackage = result.ShaderPackage;
        }

        // Then, if requested, convert the whole project to a custom template.
        if (result.ConvertToCustom && !info.IsCustomTemplate)
        {
            try
            {
                // Flush in-memory edits so what's on disk is current before
                // the folder shuffle.
                if (HasAnyErp) FlushAllLoadedErps();
                ProjectStore.ConvertToCustom(_project);
                MarkDirty();
                OnProjectStateChanged();   // relabels "Choose parts" → "Choose ERPs"
                Log("Converted project to a custom template. " +
                    "Use 'Choose ERPs' to add / remove / enable / disable ERPs; " +
                    "the shader package is now editable in Edit project.");
                return;
            }
            catch (Exception ex)
            {
                ShowMessage( $"Couldn't convert the project:\n{ex.Message}",
                    "Convert to custom template", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        if (fieldsChanged)
        {
            MarkDirty();
            OnProjectStateChanged();   // refreshes ProjectNameLabel from the new name
            Log(shaderChanged
                ? $"Project info updated. Shader package now: {info.ShaderPackage}"
                : "Project info updated.");
        }

        // "Manage ERPs…" from the dialog opens the ERP-pool manager afterwards.
        if (result.ManageErps && info.IsCustomTemplate)
            OpenErpsChooser(initial: false);
    }

    private void OpenPartsChooser(bool initial)
    {
        if (_project is null) return;
        // Already in category-then-part order; the chooser groups on the way in.
        var available = _project.AllPartPaths.ToList();
        if (available.Count == 0)
        {
            ShowMessage(
                $"This project has no parts in {ProjectStore.ErpsFolderName}/ — nothing to choose from.",
                "Choose parts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var enabledNow = _project.EnabledPartPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var picks = RunPartsChooser(available, enabledNow, initial);
        if (picks is null) return;   // cancelled

        // Apply diff. Flush in-memory edits first so a part being switched off
        // keeps them — the file stays put now, only the selection changes.
        try
        {
            if (HasAnyErp) FlushAllLoadedErps();

            int enabledCount = 0, disabledCount = 0;
            // Off first: enabling resolves conflicts against what's already on,
            // so clearing the deselected ones avoids a stale part displacing a
            // new one and undoing the user's own choice.
            foreach (var part in available)
            {
                if (picks.Contains(part) || !enabledNow.Contains(part)) continue;
                ProjectStore.DisablePart(_project, part);
                disabledCount++;
            }
            foreach (var part in available)
            {
                if (!picks.Contains(part) || enabledNow.Contains(part)) continue;
                ProjectStore.EnablePart(_project, part);
                enabledCount++;
            }

            if (enabledCount + disabledCount > 0)
            {
                MarkDirty();
                Log($"Parts updated — {enabledCount} enabled, {disabledCount} disabled.");
                ReloadEnabledErps();
            }
        }
        catch (Exception ex)
        {
            ShowMessage( $"Couldn't apply parts selection:\n{ex.Message}",
                "Choose parts failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Modal text prompt — "Rename ERP" with the current name pre-filled and
    /// the .erp suffix non-selected so the user can edit the base name
    /// straight away. Returns the user's input (trimmed), or null on cancel
    /// / empty input. Trailing .erp is not required; the rename helper
    /// re-adds it if missing.
    /// </summary>
    private string? PromptForRenamedErp(string oldName)
    {
        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"Rename '{oldName}' to:",
            Margin = new Thickness(0, 0, 0, 8),
        });

        var box = new System.Windows.Controls.TextBox
        {
            Text = oldName,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
        };
        root.Children.Add(box);

        var btnRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok     = new System.Windows.Controls.Button { Content = "Rename", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel  = true, Width = 80 };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);
        root.Children.Add(btnRow);

        var win = new Window
        {
            Title = "Rename ERP",
            Content = root,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 460, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);
        ok.Click += (_, _) => { win.DialogResult = true; };

        box.Loaded += (_, _) =>
        {
            box.Focus();
            // Pre-select the base name so typing immediately replaces it while
            // keeping the .erp extension in view.
            var dot = oldName.LastIndexOf('.');
            if (dot > 0) box.Select(0, dot);
            else         box.SelectAll();
        };

        if (win.ShowDialog() != true) return null;
        var v = box.Text.Trim();
        return string.IsNullOrEmpty(v) || v == oldName ? null : v;
    }

    /// <summary>
    /// Custom-template counterpart to <see cref="OpenPartsChooser"/>. Lets
    /// the user add ERPs from anywhere on disk and remove ERPs currently in
    /// the project. Mutations are applied immediately (each Add copies a
    /// file in, each Remove deletes one) — there is no "OK / Cancel"
    /// staging, just a Close button.
    /// </summary>
    private void OpenErpsChooser(bool initial)
    {
        if (_project is null) return;
        if (!_project.Info.IsCustomTemplate)
        {
            // Defensive — shouldn't happen because ChooseParts_Click routes by type.
            OpenPartsChooser(initial);
            return;
        }

        var anyChanged = RunErpsChooser(initial);
        if (anyChanged)
        {
            MarkDirty();
            ReloadEnabledErps();
        }
    }

    /// <summary>
    /// Modal ERPs manager for custom-template projects. Lists every ERP
    /// currently in the project, lets the user add new ones from disk and
    /// remove existing ones. Returns true if any add/remove happened so the
    /// caller can mark the project dirty and reload the texture pane.
    /// </summary>
    /// <summary>Row model for the custom-project ERP chooser: name + on/off state.</summary>
    /// <summary>
    /// Modal ERP-pool manager for custom-template projects. Lists every ERP in
    /// the project's pool (<c>ERPs/</c>) and lets the user add new ones from
    /// disk, rename, export, and remove them. Enabling/disabling parts is a
    /// separate concern handled by the shared parts chooser ("Choose parts").
    /// Mutations apply immediately; returns true if anything changed so the
    /// caller can mark dirty and reload the pane.
    /// </summary>
    private bool RunErpsChooser(bool initial)
    {
        var p = _project!;

        // Set by anything that mutates the project, so the caller knows to mark
        // dirty. Declared up front because handlers built further down capture it.
        var anyChanged = false;

        // The arrangement is the truth: which categories exist, in what order,
        // and which parts sit in each, in what order. The list below is a
        // projection of it, rebuilt after every change.
        var state = ProjectArrangement.Load(p);

        // Every part in the project, whether currently switched on or not —
        // one copy of each, wherever its category folder puts it.
        var pool = p.AllPartPaths.ToList();

        ProjectArrangement.CategoryGroup Uncategorized()
        {
            var group = state.Groups.FirstOrDefault(g => g.IsUncategorized);
            if (group is null)
            {
                group = new ProjectArrangement.CategoryGroup { Name = ProjectArrangement.UncategorizedName };
                state.Groups.Add(group);
            }
            return group;
        }

        // Reconcile the saved arrangement with what's actually on disk: drop
        // parts that have gone, and file anything new under Uncategorized so it
        // can't be invisible.
        void Reconcile()
        {
            var known = pool.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var group in state.Groups)
                group.Parts.RemoveAll(part => !known.Contains(part));

            var placed = state.Groups.SelectMany(g => g.Parts).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var loose = Uncategorized();
            foreach (var name in pool.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                if (!placed.Contains(name)) loose.Parts.Add(name);

            // Always last, so it can't push real categories below it.
            state.Groups.Remove(loose);
            state.Groups.Add(loose);
        }
        Reconcile();

        var items = new System.Collections.ObjectModel.ObservableCollection<ManagedErp>();

        // Items land in display order, so grouping alone puts the categories and
        // their parts on screen the right way round — no sorting involved.
        void Rebuild()
        {
            items.Clear();
            foreach (var group in state.Groups)
            {
                if (group.Parts.Count == 0)
                {
                    // Nothing to group means no header, so an empty category is
                    // held open by a stand-in.
                    items.Add(ManagedErp.PlaceholderFor(group.Name));
                    continue;
                }
                foreach (var part in group.Parts)
                    items.Add(new ManagedErp(part) { Category = group.Name });
            }
        }

        // Saving renumbers folders and files and moves parts between them, so
        // every path the rows were built from is stale afterwards. Re-read the
        // arrangement from disk and rebuild, or the next drag looks up a folder
        // name that no longer exists and the next rename acts on a file that
        // has already moved.
        void PersistCategories()
        {
            try
            {
                ProjectArrangement.Save(p, state);
            }
            catch (Exception ex)
            {
                Log($"!! Couldn't save the arrangement: {ex.Message}");
                ShowMessage( $"Couldn't save the arrangement:\n{ex.Message}",
                    "Manage ERPs", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            state = ProjectArrangement.Load(p);
            Rebuild();
        }

        var listBox = new System.Windows.Controls.ListBox
        {
            ItemsSource = items,
            SelectionMode = System.Windows.Controls.SelectionMode.Extended,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MaxHeight = 480,
            BorderBrush = ThemeBrush("BorderSoft"),
            BorderThickness = new Thickness(1),
            Background = ThemeBrush("Surface"),
            ItemTemplate = BuildManagedErpTemplate(),
            AllowDrop = true,
        };

        var listView = System.Windows.Data.CollectionViewSource.GetDefaultView(items);
        listView.GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(ManagedErp.Category)));

        // Right-click a category header to reorder, rename or delete it. Built
        // here so it can reach the arrangement; the category it acts on comes
        // from whichever header it was opened over.
        var headerMenu = new System.Windows.Controls.ContextMenu();
        var moveUpItem   = new System.Windows.Controls.MenuItem { Header = "Move up" };
        var moveDownItem = new System.Windows.Controls.MenuItem { Header = "Move down" };
        var renameCategoryItem = new System.Windows.Controls.MenuItem { Header = "Rename category…" };
        var deleteCategoryItem = new System.Windows.Controls.MenuItem { Header = "Delete category" };
        headerMenu.Items.Add(moveUpItem);
        headerMenu.Items.Add(moveDownItem);
        headerMenu.Items.Add(new System.Windows.Controls.Separator());
        headerMenu.Items.Add(renameCategoryItem);
        headerMenu.Items.Add(deleteCategoryItem);

        string? MenuCategory() =>
            (headerMenu.PlacementTarget as FrameworkElement)?.DataContext
                is System.Windows.Data.CollectionViewGroup g ? g.Name as string : null;

        int IndexOfCategory(string? name) =>
            name is null ? -1 : state.Groups.FindIndex(g =>
                string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

        headerMenu.Opened += (_, _) =>
        {
            // Uncategorized is where parts go when they're in no category, so it
            // can't be renamed, deleted or moved off the bottom.
            var category = MenuCategory();
            var real = category is not null &&
                !string.Equals(category, ProjectArrangement.UncategorizedName, StringComparison.OrdinalIgnoreCase);
            renameCategoryItem.IsEnabled = real;
            deleteCategoryItem.IsEnabled = real;

            var at = real ? IndexOfCategory(category) : -1;
            // The last real category can't move down past Uncategorized.
            var lastMovable = state.Groups.Count - (state.Groups.Any(g => g.IsUncategorized) ? 2 : 1);
            moveUpItem.IsEnabled = at > 0;
            moveDownItem.IsEnabled = at >= 0 && at < lastMovable;
        };

        void MoveCategory(int delta)
        {
            var category = MenuCategory();
            var at = IndexOfCategory(category);
            var to = at + delta;
            if (at < 0 || to < 0 || to >= state.Groups.Count) return;
            if (state.Groups[to].IsUncategorized) return;   // it stays last

            var group = state.Groups[at];
            state.Groups.RemoveAt(at);
            state.Groups.Insert(to, group);

            Rebuild();
            PersistCategories();
            anyChanged = true;
            Log($"Moved category '{category}' {(delta < 0 ? "up" : "down")}.");
        }

        moveUpItem.Click   += (_, _) => MoveCategory(-1);
        moveDownItem.Click += (_, _) => MoveCategory(+1);

        listBox.GroupStyle.Add(BuildManagedGroupStyle(headerMenu));
        Rebuild();

        var counter = new System.Windows.Controls.TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("TextMuted"),
        };
        void RefreshCounter() => counter.Text = $"{items.Count} ERP{(items.Count == 1 ? "" : "s")} in project";
        RefreshCounter();
        items.CollectionChanged += (_, _) => RefreshCounter();

        var header = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var hint = new System.Windows.Controls.TextBlock
        {
            Text = initial
                ? "Add the ERP files that make up this mod:"
                : "Drag ERPs onto a category to file them; right-click a category to rename or delete it. " +
                  "Use “Choose parts” to enable/disable them.",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        System.Windows.Controls.DockPanel.SetDock(hint, System.Windows.Controls.Dock.Left);
        System.Windows.Controls.DockPanel.SetDock(counter, System.Windows.Controls.Dock.Right);
        header.Children.Add(counter);
        header.Children.Add(hint);

        // Add / Rename / Export / Remove buttons row.
        var addBtn = new System.Windows.Controls.Button
        { Content = "Add ERPs…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        var renameBtn = new System.Windows.Controls.Button
        { Content = "Rename…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0), IsEnabled = false };
        var exportBtn = new System.Windows.Controls.Button
        { Content = "Export…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0), IsEnabled = false };
        var categoryBtn = new System.Windows.Controls.Button
        { Content = "New category…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        var removeBtn = new System.Windows.Controls.Button
        { Content = "Remove", Padding = new Thickness(12, 4, 12, 4), IsEnabled = false };
        var toolbar = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(addBtn);
        toolbar.Children.Add(renameBtn);
        toolbar.Children.Add(categoryBtn);
        toolbar.Children.Add(exportBtn);
        toolbar.Children.Add(removeBtn);

        // Placeholders stand in for empty categories; they're not ERPs, so no
        // action applies to them.
        List<ManagedErp> SelectedErps() =>
            listBox.SelectedItems.Cast<ManagedErp>().Where(i => !i.IsPlaceholder).ToList();

        listBox.SelectionChanged += (_, _) =>
        {
            var count = SelectedErps().Count;
            removeBtn.IsEnabled = count > 0;
            renameBtn.IsEnabled = count == 1;   // rename only makes sense one at a time
            exportBtn.IsEnabled = count > 0;
        };

        // ── Drag ERPs between categories ────────────────────────────────────
        // Threshold before a press becomes a drag, or every click on a row
        // would start one and selection would stop working.
        var dragOrigin = default(Point);
        listBox.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragOrigin = e.GetPosition(null);
            // A header has no row behind it, which is what distinguishes
            // "drag this category" from "drag these parts".
            var (category, part) = DropTarget(e.OriginalSource);
            _dragStartCategory = part is null && category is not null &&
                                 !string.Equals(category, ProjectArrangement.UncategorizedName, StringComparison.OrdinalIgnoreCase) &&
                                 IsHeader(e.OriginalSource)
                ? category
                : null;
        };
        listBox.MouseMove += (_, e) =>
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

            var delta = e.GetPosition(null) - dragOrigin;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            // A press that began on a header drags the category itself.
            if (_dragStartCategory is { } category)
            {
                DragDrop.DoDragDrop(listBox,
                    new System.Windows.DataObject(CategoryDragFormat, category), DragDropEffects.Move);
                return;
            }

            var dragged = SelectedErps();
            if (dragged.Count == 0) return;

            DragDrop.DoDragDrop(listBox, new System.Windows.DataObject(typeof(List<ManagedErp>), dragged),
                DragDropEffects.Move);
        };

        listBox.DragOver += (_, e) =>
        {
            var overSomething = DropTarget(e.OriginalSource).Category is not null;
            e.Effects = overSomething &&
                        (e.Data.GetDataPresent(typeof(List<ManagedErp>)) ||
                         e.Data.GetDataPresent(CategoryDragFormat))
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        };

        listBox.Drop += (_, e) =>
        {
            if (e.Data.GetData(CategoryDragFormat) is string movedCategory)
            {
                var (overCategory, _) = DropTarget(e.OriginalSource);
                var from = IndexOfCategory(movedCategory);
                var to = IndexOfCategory(overCategory);
                if (from < 0 || to < 0 || from == to) return;
                if (state.Groups[to].IsUncategorized) to = state.Groups.Count - 2;   // it stays last
                if (to < 0 || from == to) return;

                var moved = state.Groups[from];
                state.Groups.RemoveAt(from);
                state.Groups.Insert(to, moved);

                Rebuild();
                PersistCategories();
                anyChanged = true;
                Log($"Moved category '{movedCategory}'.");
                return;
            }

            if (e.Data.GetData(typeof(List<ManagedErp>)) is not List<ManagedErp> dragged) return;

            var (targetCategory, targetPart) = DropTarget(e.OriginalSource);
            if (targetCategory is null) return;

            var group = state.Groups.FirstOrDefault(g =>
                string.Equals(g.Name, targetCategory, StringComparison.OrdinalIgnoreCase));
            if (group is null) return;

            var moving = dragged.Where(d => !d.IsPlaceholder).Select(d => d.Name).ToList();
            if (moving.Count == 0) return;

            // Lift them out wherever they were, then put them back at the drop
            // point. Doing it in that order means a move within one category
            // lands where the pointer is rather than where the gap used to be.
            foreach (var other in state.Groups)
                other.Parts.RemoveAll(part => moving.Contains(part, StringComparer.OrdinalIgnoreCase));

            var at = targetPart is null
                ? group.Parts.Count                                  // dropped on the header: append
                : group.Parts.FindIndex(part =>
                      string.Equals(part, targetPart, StringComparison.OrdinalIgnoreCase));
            if (at < 0) at = group.Parts.Count;

            group.Parts.InsertRange(at, moving);

            Rebuild();
            PersistCategories();
            anyChanged = true;

            // Re-select what was dragged, so a second drag continues from it.
            listBox.SelectedItems.Clear();
            foreach (var row in items.Where(i => moving.Contains(i.Name, StringComparer.OrdinalIgnoreCase)))
                listBox.SelectedItems.Add(row);

            Log(group.IsUncategorized
                ? $"Moved {moving.Count} ERP{(moving.Count == 1 ? "" : "s")} out of their category."
                : $"Moved {moving.Count} ERP{(moving.Count == 1 ? "" : "s")} into '{group.Name}'.");
        };

        renameCategoryItem.Click += (_, _) =>
        {
            var category = MenuCategory();
            var at = IndexOfCategory(category);
            if (at < 0 || state.Groups[at].IsUncategorized) return;

            // Its own name is left out, so re-confirming it isn't a clash.
            var others = state.Groups
                .Where(g => !string.Equals(g.Name, category, StringComparison.OrdinalIgnoreCase))
                .Select(g => g.Name).ToList();
            var renamed = PromptForCategoryName(
                "Rename category", $"New name for '{category}':", "Rename", category!, others);
            if (renamed is null || string.Equals(renamed, category, StringComparison.Ordinal)) return;

            state.Groups[at].Name = renamed;   // in place, so it keeps its position

            Rebuild();
            PersistCategories();
            anyChanged = true;
            Log($"Renamed category '{PartPrefix.Display(category ?? "")}' → '{PartPrefix.Display(renamed)}'.");
        };

        deleteCategoryItem.Click += (_, _) =>
        {
            var category = MenuCategory();
            var at = IndexOfCategory(category);
            if (at < 0 || state.Groups[at].IsUncategorized) return;

            var group = state.Groups[at];
            var held = group.Parts.Count;

            // Only worth asking when something would move; an empty category
            // has nothing to lose.
            if (held > 0)
            {
                var confirm = ShowMessage(
                    $"Delete '{group.Name}'? Its {held} ERP{(held == 1 ? "" : "s")} stay in the project and become uncategorised.",
                    "Delete category", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.OK) return;
            }

            state.Groups.RemoveAt(at);
            Uncategorized().Parts.AddRange(group.Parts);   // in order

            Rebuild();
            PersistCategories();
            anyChanged = true;
            Log(held > 0
                ? $"Deleted category '{group.Name}'; {held} ERP{(held == 1 ? "" : "s")} became uncategorised."
                : $"Deleted empty category '{group.Name}'.");
        };

        categoryBtn.Click += (_, _) =>
        {
            var created = PromptForNewCategory(state.Groups.Select(g => g.Name).ToList());
            if (created is null) return;

            var group = new ProjectArrangement.CategoryGroup { Name = created };

            // Above Uncategorized, which stays at the bottom.
            var at = state.Groups.FindIndex(g => g.IsUncategorized);
            if (at < 0) state.Groups.Add(group); else state.Groups.Insert(at, group);

            // Anything selected goes straight in — making a category to hold
            // what you've picked is the common case.
            var selected = SelectedErps().Select(i => i.Name).ToList();
            foreach (var other in state.Groups)
                other.Parts.RemoveAll(part => selected.Contains(part, StringComparer.OrdinalIgnoreCase));
            group.Parts.AddRange(selected);

            Rebuild();
            PersistCategories();
            anyChanged = true;

            Log(selected.Count > 0
                ? $"Created category '{created}' with {selected.Count} ERP{(selected.Count == 1 ? "" : "s")}."
                : $"Created category '{created}'. Drag ERPs onto it to fill it.");
        };

        addBtn.Click += (_, _) =>
        {
            var d = new OpenFileDialog
            {
                Title = "Add ERPs to project",
                Filter = "ERP files (*.erp)|*.erp|All files|*.*",
                Multiselect = true,
            };
            if (d.ShowDialog() != true) return;

            var added = 0; var failed = new List<string>();
            foreach (var src in d.FileNames)
            {
                try
                {
                    ProjectStore.AddCustomErp(p, src);   // into the pool + enabled
                    // A newly added ERP starts uncategorised, at the bottom.
                    pool.Add(System.IO.Path.GetFileName(src));
                    Uncategorized().Parts.Add(System.IO.Path.GetFileName(src));
                    added++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{System.IO.Path.GetFileName(src)}: {ex.Message}");
                }
            }
            if (added > 0)
            {
                anyChanged = true;
                Log($"Added {added} ERP{(added == 1 ? "" : "s")} to project.");
            }
            if (failed.Count > 0)
            {
                ShowMessage(
                    "Some ERPs couldn't be added:\n\n" + string.Join("\n", failed),
                    "Add ERPs", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        renameBtn.Click += (_, _) =>
        {
            if (SelectedErps() is not [var target] ) return;
            var oldName = target.Name;
            var newName = PromptForRenamedErp(System.IO.Path.GetFileName(oldName));
            if (newName is null) return;
            try
            {
                // RenamePart moves the file and follows it in the selection,
                // and hands back the path it ended up at.
                var actualName = ProjectStore.RenamePart(p, oldName, newName);
                anyChanged = true;
                Log($"Renamed '{System.IO.Path.GetFileName(oldName)}' → '{System.IO.Path.GetFileName(actualName)}'.");
                // Re-read rather than patching the row: the file has moved, so
                // every other row's path may have shifted with it.
                PersistCategories();
                listBox.SelectedItem = items.FirstOrDefault(i =>
                    string.Equals(i.Name, actualName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                ShowMessage( $"Couldn't rename '{oldName}':\n{ex.Message}",
                    "Rename ERP", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        exportBtn.Click += (_, _) =>
        {
            var selected = SelectedErps().Select(i => i.Name).ToList();
            if (selected.Count == 0) return;

            // Flush in-memory edits so an enabled ERP's on-disk copy is current.
            if (HasAnyErp) FlushAllLoadedErps();

            // There's one copy of every part now, wherever its category folder
            // puts it — no enabled/disabled/default to choose between.
            string SourcePathFor(string partPath) => p.PathOf(partPath);

            if (selected.Count == 1)
            {
                var name = selected[0];
                var dlg = new SaveFileDialog
                {
                    Title = $"Export {name}",
                    Filter = "ERP files (*.erp)|*.erp|All files|*.*",
                    FileName = name,
                };
                if (dlg.ShowDialog() != true) return;
                try
                {
                    File.Copy(SourcePathFor(name), dlg.FileName, overwrite: true);
                    Log($"Exported {name} → {dlg.FileName}");
                }
                catch (Exception ex)
                {
                    ShowMessage( $"Couldn't export '{name}':\n{ex.Message}",
                        "Export ERP", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                var d = new OpenFolderDialog
                { Title = $"Export {selected.Count} ERPs to folder" };
                if (d.ShowDialog() != true) return;

                var ok = 0; var failed = new List<string>();
                foreach (var name in selected)
                {
                    try
                    {
                        File.Copy(SourcePathFor(name), System.IO.Path.Combine(d.FolderName, name), overwrite: true);
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{name}: {ex.Message}");
                    }
                }
                Log($"Exported {ok} ERP{(ok == 1 ? "" : "s")} → {d.FolderName}");
                if (failed.Count > 0)
                {
                    ShowMessage(
                        "Some ERPs couldn't be exported:\n\n" + string.Join("\n", failed),
                        "Export ERPs", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        };

        removeBtn.Click += (_, _) =>
        {
            var toRemove = SelectedErps().Select(i => i.Name).ToList();
            if (toRemove.Count == 0) return;

            var msg = toRemove.Count == 1
                ? $"Remove '{toRemove[0]}' from this project? This deletes the ERP from the project file."
                : $"Remove {toRemove.Count} ERPs from this project? This deletes them from the project file.";
            var confirm = ShowMessage( msg, "Remove ERPs",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            var removed = 0;
            foreach (var name in toRemove)
            {
                try
                {
                    ProjectStore.RemoveErp(p, name);
                    var row = items.FirstOrDefault(i => i.Name == name);
                    if (row is not null) items.Remove(row);
                    removed++;
                    anyChanged = true;
                }
                catch (Exception ex)
                {
                    ShowMessage( $"Couldn't remove '{name}':\n{ex.Message}",
                        "Remove ERPs", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            if (removed > 0) Log($"Removed {removed} ERP{(removed == 1 ? "" : "s")} from project.");
        };

        var close = new System.Windows.Controls.Button
        { Content = "Close", IsDefault = true, IsCancel = true, Width = 90, Padding = new Thickness(0, 6, 0, 6) };
        var btnRow = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btnRow.Children.Add(close);

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        root.Children.Add(header);
        root.Children.Add(toolbar);
        root.Children.Add(listBox);
        root.Children.Add(new System.Windows.Controls.Border { Height = 12 });
        root.Children.Add(btnRow);

        var win = new Window
        {
            Title = initial ? "Add ERPs to project" : "Manage ERPs",
            Content = root,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 640, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);
        close.Click += (_, _) => { win.DialogResult = true; };
        win.ShowDialog();
        return anyChanged;
    }

    /// <summary>
    /// Add an ERP that lives inside the project's temp dir to the loaded set.
    /// Idempotent on path — re-calling for an already-loaded ERP is a no-op.
    /// Unlike <see cref="LoadErp"/> this APPENDS rather than replacing, so
    /// multiple ERPs from the active version coexist in the grouped texture list.
    /// </summary>
    private void LoadErpFromProject(string tempPath)
    {
        if (_loadedErps.Any(l => string.Equals(l.Path, tempPath, StringComparison.OrdinalIgnoreCase)))
            return;
        try
        {
            var erp = ErpFileIO.Load(tempPath);
            _loadedErps.Add(new LoadedErp(erp, tempPath, UniqueDisplayErpName(Path.GetFileName(tempPath))));
            UpdateProjectFilePathLabel();
            SaveButton.IsEnabled = true;
            SaveAsButton.IsEnabled = true;
            Log($"Loaded project ERP: {DisplayErpName(Path.GetFileName(tempPath))} — {erp.Resources.Count} resources");
            RebuildTextureList();
            // Don't ClearDirty here — the project itself may already be dirty.
        }
        catch (Exception ex)
        {
            ShowMessage( $"Failed to load ERP from project:\n{ex.Message}",
                "Load failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Project-mode file-path label: e.g. "3 ERPs (in project)".</summary>
    private void UpdateProjectFilePathLabel()
    {
        var n = _loadedErps.Count;
        FilePathLabel.Text = n == 0
            ? "(no ERPs)"
            : n == 1
                ? _loadedErps[0].DisplayName + "  (in project)"
                : $"{n} ERPs  (in project)";
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        if (_project.ZipPath is null) { SaveProjectAs_Click(sender, e); return; }
        try
        {
            FlushLoadedProjectErp();
            ProjectStore.Save(_project);
            ClearDirty();
            Log($"Saved project: {_project.ZipPath}");
            OnProjectStateChanged();
        }
        catch (Exception ex)
        {
            ShowMessage( $"Save failed:\n{ex.Message}", "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveProjectAs_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        var cfg = UserConfig.Load();
        var dlg = new SaveFileDialog
        {
            Title = "Save project as",
            Filter = $"Simplified Studio project|*{ProjectStore.ProjectFileExtension}|All files|*.*",
            FileName = _project.ZipPath is null
                ? "untitled" + ProjectStore.ProjectFileExtension
                : Path.GetFileName(_project.ZipPath),
            InitialDirectory = (cfg.LastProjectFolder is { } d && Directory.Exists(d)) ? d : null
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            FlushLoadedProjectErp();
            ProjectStore.Save(_project, dlg.FileName);
            RememberProjectFolder(dlg.FileName);
            ClearDirty();
            Log($"Saved project: {dlg.FileName}");
            OnProjectStateChanged();
        }
        catch (Exception ex)
        {
            ShowMessage( $"Save failed:\n{ex.Message}", "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Flush every loaded project-ERP to its temp-dir path before rezipping.
    /// Pending .mipmaps writes are flushed once for the whole batch.
    /// </summary>
    private void FlushLoadedProjectErp()
    {
        if (_project is null || !HasAnyErp) return;
        var flushed = _pendingMips.Flush();
        foreach (var loaded in _loadedErps)
        {
            if (!loaded.Path.StartsWith(_project.TempDir, StringComparison.OrdinalIgnoreCase)) continue;
            ErpFileIO.Save(loaded.File, loaded.Path);
        }
        if (flushed > 0) Log($"  flushed {flushed} .mipmaps file(s)");
    }

    private void CloseProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        // The board check lives inside ConfirmDiscardProject, which asks it
        // before the save prompt for every path that discards a project.
        if (!ConfirmDiscardProject("close this project")) return;
        var path = _project.ZipPath ?? "(untitled)";
        CloseProjectInternal();
        OnProjectStateChanged();
        Log($"Closed project: {path}");
    }

    /// <summary>
    /// Make sure PureRef isn't still holding a board from this project.
    ///
    /// Closing deletes the working folder the boards live in, and PureRef
    /// doesn't lock them or notice they've gone — so a board left open loses
    /// whatever is done to it next, silently. Returns whether to carry on.
    /// </summary>
    private bool ConfirmBoardsClosed()
    {
        if (_project is null) return true;

        // A project with no boards has nothing to warn about, and no reason to
        // go looking through other processes to find that out.
        var boards = ProjectWorkspace.Boards(_project).Select(b => b.Path).ToList();
        if (boards.Count == 0) return true;

        while (true)
        {
            var open = Notes.PureRef.OpenBoards(boards);
            if (open.Count == 0) return true;

            var names = string.Join(", ", open);
            var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };

            var heading = new System.Windows.Controls.TextBlock
            {
                Text = open.Count == 1 ? "A board is still open" : "Boards are still open",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            };
            heading.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimary");
            root.Children.Add(heading);

            var body = new System.Windows.Controls.TextBlock
            {
                Text = $"PureRef still has {names} open." + Environment.NewLine + Environment.NewLine +
                       "Closing the project deletes the working copy these are opened from, and PureRef " +
                       "won't notice — anything saved after that goes nowhere. Save and close them in " +
                       "PureRef first, then check again.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16),
                LineHeight = 19,
            };
            body.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextMuted");
            root.Children.Add(body);

            var row = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var again = new System.Windows.Controls.Button
            {
                Content = "Check again",
                IsDefault = true,
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
            };
            var anyway = new System.Windows.Controls.Button
            {
                Content = "Close anyway",
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 0),
            };
            var cancel = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                IsCancel = true,
                Padding = new Thickness(16, 6, 16, 6),
            };
            row.Children.Add(again);
            row.Children.Add(anyway);
            row.Children.Add(cancel);
            root.Children.Add(row);

            var win = new Window
            {
                Title = "Boards still open",
                Content = root,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 480,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };
            SimplifiedUi.DialogChrome.Apply(win);

            // true = look again, false = go ahead regardless, null = don't close.
            again.Click  += (_, _) => { win.Tag = true;  win.DialogResult = true; };
            anyway.Click += (_, _) => { win.Tag = false; win.DialogResult = true; };

            if (win.ShowDialog() != true) return false;
            if (win.Tag is false) return true;
        }
    }

    private void CloseProjectInternal()
    {
        if (_project is null) return;
        // Drop any loaded ERPs that came from this project's temp dir before
        // the temp dir vanishes. Anything else is a legacy standalone load and
        // can stay (defensive — in practice project mode owns all loads now).
        var projectTemp = _project.TempDir;
        var stale = _loadedErps
            .Where(l => l.Path.StartsWith(projectTemp, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var s in stale) _loadedErps.Remove(s);
        if (_loadedErps.Count == 0) UnloadErp(); else RebuildTextureList();

        // The notes window reads and writes inside the temp dir that is about
        // to be deleted, so it goes with the project rather than being left
        // pointing at nothing.
        _notesWindow?.Close();

        ProjectStore.Close(_project);
        _project = null;
    }

    /// <summary>Clear the texture pane back to its no-ERP state.</summary>
    private void UnloadErp()
    {
        _loadedErps.Clear();
        _textures.Clear();
        FilePathLabel.Text = "(no ERP)";
        SaveButton.IsEnabled = false;
        SaveAsButton.IsEnabled = false;
        _pendingMips.Clear();
        ClearDirty();
        UpdateCountLabel();
    }

    /// <summary>
    /// Prompt before discarding unsaved project work. The project zip is
    /// considered out-of-date whenever _isDirty is set (texture edits) or
    /// .mipmaps are pending — in project mode "Save project" flushes both
    /// the ERP and rezips, so a single dirty flag covers everything.
    /// </summary>
    private bool ConfirmDiscardProject(string action)
    {
        if (_project is null) return true;

        // First, and before the dirty check: every one of these discards the
        // working folder the boards are open from, and a board open in PureRef
        // is work that never reached the project — so there is something to
        // lose even when nothing here is dirty.
        if (!ConfirmBoardsClosed()) return false;

        if (!_isDirty && _pendingMips.Count == 0) return true;

        var ans = ConfirmUnsavedChanges("The project has unsaved changes.", action);

        switch (ans)
        {
            case MessageBoxResult.Yes:
                try
                {
                    if (_project.ZipPath is null)
                    {
                        // Force a Save As; abort the outer action if the user cancels it.
                        SaveProjectAs_Click(this, new RoutedEventArgs());
                        if (_isDirty) return false;
                    }
                    else
                    {
                        FlushLoadedProjectErp();
                        ProjectStore.Save(_project);
                        ClearDirty();
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    ShowMessage( $"Save failed:\n{ex.Message}", "Save failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            case MessageBoxResult.No:
                _pendingMips.Clear();
                ClearDirty();
                return true;
            default:
                return false;
        }
    }

    private void RememberProjectFolder(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;
            var cfg = UserConfig.Load();
            cfg.LastProjectFolder = dir;
            cfg.Save();
        }
        catch { /* config write failures aren't fatal */ }
    }

    private void OnProjectStateChanged()
    {
        var open = _project is not null;
        SaveProjectButton.IsEnabled = open;
        SaveProjectAsButton.IsEnabled = open;
        CloseProjectButton.IsEnabled = open;
        ChoosePartsButton.IsEnabled = open;
        EditProjectButton.IsEnabled = open;
        NotesButton.IsEnabled = open;
        ExportProjectButton.IsEnabled = open;

        // v2-simplified-ui: inline mod-info panel replaces the version+modinfo rows.
        InlineModInfoPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        NoProjectLabel.Visibility     = open ? Visibility.Collapsed : Visibility.Visible;

        if (!open) return;

        var p = _project!;
        // Show the name from info.json (set in the wizard / Edit project).
        // Fall back to the .simplified file name if it's blank.
        ProjectNameLabel.Text = string.IsNullOrWhiteSpace(p.Info.Name)
            ? p.DisplayName
            : p.Info.Name;
        RepopulateModInfoBar();
    }

    // ─────────────────────── Mod info editor (Phase 5) ───────────────────

    /// <summary>True while we're programmatically loading values into the boxes
    /// — used to keep TextChanged handlers from firing back as user edits.</summary>
    private bool _suppressModInfoEvents;

    private void RepopulateModInfoBar()
    {
        if (_project is null) return;
        var info = _project.Info;
        _suppressModInfoEvents = true;
        try
        {
            ModNameBox.Text    = info.Name;
            ModAuthorBox.Text  = info.Author;
        }
        finally { _suppressModInfoEvents = false; }
    }

    private void ModInfoField_Changed(object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressModInfoEvents || _project is null) return;
        var info = _project.Info;
        info.Name = ModNameBox.Text;
        info.Author  = ModAuthorBox.Text;
        MarkDirty();   // mod.json will be rewritten on next project save
    }

    // ─────────────────────── Dialogs ───────────────────────────────────

    /// <summary>
    /// Wizard result. Exactly one of <see cref="SerpsPath"/> /
    /// <see cref="CustomShaderPackage"/> is non-empty:
    /// SerpsPath when the user picked a .serps template,
    /// CustomShaderPackage when the user ticked "Custom template" and
    /// typed a shader-package identifier instead.
    /// </summary>
    private sealed record NewProjectWizardResult(
        string SavePath,
        string ModName,
        string ModAuthor,
        string SerpsPath,
        string CustomShaderPackage)
    {
        public bool IsCustom => string.IsNullOrEmpty(SerpsPath)
                             && !string.IsNullOrWhiteSpace(CustomShaderPackage);
    }
    /// <summary>
    /// Result of the Edit project dialog. <see cref="ShaderPackage"/> is only
    /// meaningful for custom-template projects — for SERPs-template projects
    /// the shader package is read-only and the caller ignores this field.
    /// <see cref="ConvertToCustom"/> is set when the user confirmed converting
    /// a template project into a custom one. <see cref="ManageErps"/> is set
    /// when the user clicked "Manage ERPs…" on a custom project.
    /// </summary>
    private sealed record EditProjectResult(
        string ModName, string ModAuthor, string ShaderPackage,
        bool ConvertToCustom, bool ManageErps);
    private sealed record ExportProjectResult(string SaveLocation, string ModVersion);
    private sealed record SettingsResult(string? GameDir, string DefaultAuthor, string Theme,
        bool AssociateProjects);

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var cfg = UserConfig.Load();
        var result = RunSettingsDialog(cfg);
        if (result is null) return;

        cfg.GameDir       = result.GameDir;
        cfg.DefaultAuthor = result.DefaultAuthor;
        cfg.Theme         = result.Theme;

        // Applied on Save rather than as the box is ticked: this one reaches
        // outside the app, so it should follow the same "yes, I meant it" as
        // every other setting. Only acted on when it actually changed.
        try
        {
            if (result.AssociateProjects != FileAssociation.IsRegistered)
            {
                if (result.AssociateProjects) FileAssociation.Register();
                else FileAssociation.Unregister();
                Log(result.AssociateProjects
                    ? $"{FileAssociation.ExtensionList} files now open with this copy of Simplified Studio."
                    : $"{FileAssociation.ExtensionList} associations removed.");

                // Windows keeps a per-user "always open with" that outranks any
                // registration and refuses to be deleted by anything but the
                // user. Registering while one of those is set looks like it
                // worked and changes nothing, so say what happened.
                if (result.AssociateProjects)
                {
                    var blocked = FileAssociation.BlockedByWindows();
                    if (blocked.Count > 0)
                    {
                        var which = string.Join(", ",
                            blocked.Select(b => $"{b.Kind.Extension} (currently {b.PinnedTo})"));
                        foreach (var b in blocked)
                            Log($"  {b.Kind.Extension} is pinned to {b.PinnedTo} by Windows — unchanged.");

                        ShowMessage(
                            "Windows is still opening some of these with another app.\n\n"
                            + which + "\n\n"
                            + "That choice was made in Explorer and only you can change it: right-click "
                            + "one of those files, choose Open with, then Choose another app, pick "
                            + "Simplified Studio and tick \"Always use this app\". Windows protects the "
                            + "setting so that no program can take a file type on its own.",
                            "Already assigned", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"  (couldn't change the file association: {ex.Message})");
        }

        try
        {
            cfg.Save();
            // Mirror the new game dir into the (hidden) GameDirBox — its
            // TextChanged handler refreshes the texture preview.
            GameDirBox.Text = cfg.GameDir ?? "";
            UpdateImportExportLabels();
            Log("Settings saved.");
        }
        catch (Exception ex)
        {
            ShowMessage( $"Couldn't save settings:\n{ex.Message}",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// The Advanced menu is a team-only feature, unlocked by a
    /// <c>team.simplified</c> file next to the executable whose JSON contains
    /// <c>"advanced": true</c>. A missing/empty/wrong-content file doesn't
    /// count, so the unlock can't be forged just by knowing the file name.
    /// Without a valid marker, the Ctrl+T shortcut is inert.
    /// </summary>
    private static bool TeamUnlocked => TeamConfig.Load().Advanced;

    /// <summary>
    /// Advanced menu (Ctrl+T, team-only). Hosts the Show/Hide log toggle and
    /// feature gates that aren't part of the normal Settings flow:
    ///  - Enable custom templates → toggles the "Custom template" tickbox
    ///    in the New project wizard.
    ///  - Enable more resource types → surfaces .material / .context in the
    ///    resource list.
    /// Feature-gate checkboxes persist in <see cref="TeamConfig"/> (the
    /// team.simplified marker), so they can't be flipped from a per-user
    /// config.json; the log toggle is session-only.
    /// </summary>
    private void OpenAdvancedDialog()
    {
        // The menu only opens when the marker is present, so this carries the
        // real flag values — and Advanced=true, which we preserve on Save.
        var cfg = TeamConfig.Load();

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };

        // Top: log toggle (immediate, session-only).
        var logBtn = new System.Windows.Controls.Button
        {
            Content = IsLogVisible ? "Hide log" : "Show log",
            Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Toggle the troubleshooting log panel at the bottom of the window",
        };
        logBtn.Click += (_, _) =>
        {
            SetLogVisible(!IsLogVisible);
            logBtn.Content = IsLogVisible ? "Hide log" : "Show log";
        };
        root.Children.Add(logBtn);

        // Divider before the feature gates.
        root.Children.Add(new System.Windows.Controls.Border
        {
            Height = 1,
            Background = ThemeBrush("BorderSoft"),
            Margin = new Thickness(0, 14, 0, 10),
        });
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Feature flags",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var customCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Enable custom templates",
            IsChecked = cfg.EnableCustomTemplates,
            Margin = new Thickness(0, 4, 0, 4),
            ToolTip = "Show the \"Custom template\" tickbox in the New project wizard, " +
                      "letting you type a shader package and build a project from scratch " +
                      "without a .serps template.",
        };
        root.Children.Add(customCheck);

        var moreResCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Enable more resource types",
            IsChecked = cfg.EnableMoreResourceTypes,
            Margin = new Thickness(0, 4, 0, 4),
            ToolTip = "Surface .material and .context resources alongside textures " +
                      "in the project view.",
        };
        root.Children.Add(moreResCheck);

        // Package format dropdown.
        var pkgFormatRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 10, 0, 4) };
        var pkgFormatLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Package format:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        System.Windows.Controls.DockPanel.SetDock(pkgFormatLabel, System.Windows.Controls.Dock.Left);
        var pkgFormatCombo = new System.Windows.Controls.ComboBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            Width = 120,
            ToolTip = "Output format used by Package: .zip (installable mod) or .serps (shareable template).",
        };
        pkgFormatCombo.Items.Add(".zip");
        pkgFormatCombo.Items.Add(".serps");
        pkgFormatCombo.SelectedItem = string.Equals(cfg.PackageFormat, "serps", StringComparison.OrdinalIgnoreCase) ? ".serps" : ".zip";
        pkgFormatRow.Children.Add(pkgFormatLabel);
        pkgFormatRow.Children.Add(pkgFormatCombo);
        root.Children.Add(pkgFormatRow);

        // Texture format dropdown (moved here from Settings for now).
        var texFormatRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 6, 0, 4) };
        var texFormatLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Texture format:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        System.Windows.Controls.DockPanel.SetDock(texFormatLabel, System.Windows.Controls.Dock.Left);
        var texFormatCombo = new System.Windows.Controls.ComboBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            Width = 120,
            ToolTip = "Format used by the per-texture and bulk Export/Import buttons: DDS or PNG.",
        };
        texFormatCombo.Items.Add("DDS");
        texFormatCombo.Items.Add("PNG");
        texFormatCombo.SelectedItem = string.Equals(cfg.FileFormat, "PNG", StringComparison.OrdinalIgnoreCase) ? "PNG" : "DDS";
        texFormatRow.Children.Add(texFormatLabel);
        texFormatRow.Children.Add(texFormatCombo);
        root.Children.Add(texFormatRow);

        // Bottom: Save / Cancel.
        var btnRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var save   = new System.Windows.Controls.Button { Content = "Save",   IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel  = true, Width = 80 };
        btnRow.Children.Add(save);
        btnRow.Children.Add(cancel);
        root.Children.Add(btnRow);

        var win = new Window
        {
            Title = "Advanced",
            Content = root,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 460, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);
        save.Click += (_, _) => { win.DialogResult = true; };

        if (win.ShowDialog() != true) return;

        var moreResWas = cfg.EnableMoreResourceTypes;
        cfg.EnableCustomTemplates   = customCheck.IsChecked  == true;
        cfg.EnableMoreResourceTypes = moreResCheck.IsChecked == true;
        cfg.PackageFormat = (string?)pkgFormatCombo.SelectedItem == ".serps" ? "serps" : "zip";
        cfg.FileFormat = (string?)texFormatCombo.SelectedItem == "PNG" ? "PNG" : "DDS";
        try
        {
            cfg.Save();
            Log("Advanced settings saved.");
            // Surface / hide the new resource kinds without forcing a project reload.
            if (moreResWas != cfg.EnableMoreResourceTypes && HasAnyErp)
                RebuildTextureList();
            // Relabel the per-texture Export/Import buttons for the new format.
            UpdateImportExportLabels();
        }
        catch (Exception ex)
        {
            ShowMessage( $"Couldn't save advanced settings:\n{ex.Message}",
                "Advanced", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool IsLogVisible => LogPanel.Visibility == Visibility.Visible;

    /// <summary>
    /// Refresh the Import/Export button labels after the chosen FileFormat
    /// changes. Goes through the selection logic rather than stamping a format
    /// directly: with nothing selected the buttons read plain "Export" /
    /// "Import", and with several rows held they must keep "Export selected"
    /// and the hidden Import.
    /// </summary>
    private void UpdateImportExportLabels() => UpdateSelectionActions();

    private SettingsResult? RunSettingsDialog(UserConfig cfg)
    {
        string gameDir = cfg.GameDir ?? "";

        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        // Rows 0-3: existing settings fields. Row 4: credits divider + heading + body. Row 5: Save/Cancel.
        // Show/Hide log moved out of here into the Advanced menu (Ctrl+T).
        for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        void Lab(string text, int row)
        {
            var tb = new System.Windows.Controls.TextBlock
            { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 8, 6) };
            System.Windows.Controls.Grid.SetRow(tb, row);
            System.Windows.Controls.Grid.SetColumn(tb, 0);
            grid.Children.Add(tb);
        }

        // Row 0: game directory + Browse
        Lab("Game directory:", 0);
        var gameBox = new System.Windows.Controls.TextBox
        {
            Text = gameDir,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 6, 0, 6),
        };
        System.Windows.Controls.Grid.SetRow(gameBox, 0);
        System.Windows.Controls.Grid.SetColumn(gameBox, 1);
        grid.Children.Add(gameBox);
        var gameBtns = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(8, 6, 0, 6) };
        var gameBtn    = new System.Windows.Controls.Button
        { Content = "Browse", Padding = new Thickness(12, 4, 12, 4) };
        var detectBtn  = new System.Windows.Controls.Button
        { Content = "Detect", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0),
          ToolTip = "Scan Steam libraries and EA install folders for an F1 game directory" };
        gameBtns.Children.Add(gameBtn);
        gameBtns.Children.Add(detectBtn);
        System.Windows.Controls.Grid.SetRow(gameBtns, 0);
        System.Windows.Controls.Grid.SetColumn(gameBtns, 2);
        grid.Children.Add(gameBtns);

        gameBtn.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { Title = "Select your game install folder" };
            if (d.ShowDialog() == true) gameBox.Text = d.FolderName;
        };
        detectBtn.Click += (_, _) =>
        {
            var hit = Simplified.Shared.GameDirDetector.Detect();
            if (hit is null)
            {
                ShowMessage(
                    $"Couldn't find {SimplifiedStudio.Core.Project.ProjectPackager.GameFolderName} " +
                    "in any Steam library or common EA folder.\n\n" +
                    "Use Browse to pick it manually.",
                    "Detect", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            gameBox.Text = hit.Path;
        };

        // Row 1 (texture format) moved to the Advanced menu (Ctrl+T).

        // Row 2: default author
        Lab("Default author:", 2);
        var authorBox = new System.Windows.Controls.TextBox
        {
            Text = cfg.DefaultAuthor ?? "",
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 6, 0, 6),
            ToolTip = "Pre-fills the 'Mod author' field in the New project wizard",
        };
        System.Windows.Controls.Grid.SetRow(authorBox, 2);
        System.Windows.Controls.Grid.SetColumn(authorBox, 1);
        System.Windows.Controls.Grid.SetColumnSpan(authorBox, 2);
        grid.Children.Add(authorBox);

        // Row 3: appearance. (It used to hold a "zip during export" checkbox,
        // gone since packages always zip.)
        Lab("Appearance:", 3);
        var themeRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var lightChip = new System.Windows.Controls.RadioButton
        {
            Content = "Light",
            Style = FindResource("ChannelChip") as Style,
            IsChecked = !ThemeManager.IsDark,
        };
        var darkChip = new System.Windows.Controls.RadioButton
        {
            Content = "Dark",
            Style = FindResource("ChannelChip") as Style,
            Margin = new Thickness(0),
            IsChecked = ThemeManager.IsDark,
        };
        themeRow.Children.Add(lightChip);
        themeRow.Children.Add(darkChip);
        System.Windows.Controls.Grid.SetRow(themeRow, 3);
        System.Windows.Controls.Grid.SetColumn(themeRow, 1);
        System.Windows.Controls.Grid.SetColumnSpan(themeRow, 2);
        grid.Children.Add(themeRow);

        // Applied as it is picked rather than on Save — choosing a palette from
        // a dialog still wearing the old one is guessing. Cancel puts it back.
        var themeOnEntry = ThemeManager.Current;
        lightChip.Checked += (_, _) => { ThemeManager.Apply(ThemeManager.Light); RefreshThemedUi(); };
        darkChip.Checked  += (_, _) => { ThemeManager.Apply(ThemeManager.Dark);  RefreshThemedUi(); };

        // Row 4 holds everything below the settings fields, stacked: the file
        // association, then the credits. Both were briefly given row 4
        // separately, which put them in the same cell - a grid stacks nothing,
        // it overlays.
        var credits = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 18, 0, 0) };

        // The one switch here that changes something outside the app, so it sits
        // apart from the fields above rather than among them.
        var associate = new System.Windows.Controls.CheckBox
        {
            Content = $"Give {FileAssociation.ExtensionList} files their own icon, and open them with this app",
            IsChecked = FileAssociation.IsRegistered,
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "Adds a few keys under HKEY_CURRENT_USER. No administrator rights, "
                    + "only this Windows account, and unticking removes them again.",
        };
        credits.Children.Add(associate);
        credits.Children.Add(new System.Windows.Controls.Border
        {
            Height = 1,
            Margin = new Thickness(0, 16, 0, 10),
        }.Themed(System.Windows.Controls.Border.BackgroundProperty, "BorderSoft"));
        credits.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Credits",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        void Credit(string name, string role)
        {
            var line = new System.Windows.Controls.TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2),
            }.Themed(System.Windows.Controls.TextBlock.ForegroundProperty, "TextMuted");
            line.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new System.Windows.Documents.Run($" — {role}"));
            credits.Children.Add(line);
        }
        Credit("Petar Tasev", "Underlying ERP structure and format code");
        Credit("Team Simplified", "App functionality and Design");
        Credit("Claude Code", "Coding");
        System.Windows.Controls.Grid.SetRow(credits, 4);
        System.Windows.Controls.Grid.SetColumnSpan(credits, 3);
        grid.Children.Add(credits);

        // Row 5: Save / Cancel (right). Show/Hide log lives in Advanced (Ctrl+T) now.
        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var save   = new System.Windows.Controls.Button { Content = "Save",   IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel  = true, Width = 80 };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        System.Windows.Controls.Grid.SetRow(buttons, 5);
        System.Windows.Controls.Grid.SetColumnSpan(buttons, 3);
        grid.Children.Add(buttons);

        // Name and version above the first field. Sits outside the grid rather
        // than as a row, so the six existing rows keep their numbering.
        var titleLine = new System.Windows.Controls.TextBlock
        {
            Text = SimplifiedStudio.Util.AppVersion.TitleFor("Simplified Studio"),
            Margin = new Thickness(16, 16, 16, 0),
        }.Themed(System.Windows.Controls.TextBlock.ForegroundProperty, "TextMuted");
        var settingsRoot = new System.Windows.Controls.StackPanel();
        settingsRoot.Children.Add(titleLine);
        settingsRoot.Children.Add(grid);

        var win = new Window
        {
            Title = "Settings",
            Content = settingsRoot,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 640, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);
        save.Click += (_, _) => { win.DialogResult = true; };

        if (win.ShowDialog() != true)
        {
            // Cancelled — put back the palette the dialog was opened with,
            // since picking one applied it on the spot.
            if (ThemeManager.Current != themeOnEntry)
            {
                ThemeManager.Apply(themeOnEntry);
                RefreshThemedUi();
            }
            return null;
        }

        return new SettingsResult(
            string.IsNullOrWhiteSpace(gameBox.Text) ? null : gameBox.Text.Trim(),
            authorBox.Text.Trim(),
            ThemeManager.Current,
            associate.IsChecked == true);
    }


    /// <summary>
    /// "Export" — opens the export dialog. The actual packaging logic isn't
    /// hooked up yet; for now we just collect the user's choices and log them.
    /// </summary>
    private async void ExportProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        var result = RunExportDialog();
        if (result is null) return;

        // .zip is the installable-mod path and needs the game folder to merge
        // into the shader-package ERPs. .serps is just a template archive — it
        // doesn't need the game folder.
        var packageFormat = (TeamConfig.Load().PackageFormat ?? "zip").ToLowerInvariant();
        var isSerps = packageFormat == "serps";

        var gameDir = GameDir;
        if (!isSerps && string.IsNullOrWhiteSpace(gameDir))
        {
            ShowMessage(
                "Game directory isn't set — open Settings and pick (or Detect) the F1 install folder first.",
                "Game directory required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Make sure on-disk ERPs reflect the user's in-memory edits before merge.
        try { if (HasAnyErp) FlushAllLoadedErps(); }
        catch (Exception ex)
        {
            ShowMessage( $"Couldn't flush in-memory edits before packaging:\n{ex.Message}",
                "Package failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var project = _project;
        ExportProjectButton.IsEnabled = false;
        Log($"Packaging '{project.Info.Name}' as .{packageFormat}…");
        try
        {
            var output = await Task.Run(() => isSerps
                ? ProjectPackager.PackageAsSerps(project, gameDir!, result.SaveLocation, result.ModVersion)
                : ProjectPackager.Package(project, gameDir!, result.SaveLocation, zip: true, result.ModVersion, log: Log));
            Log($"Package written: {output}");
            ShowMessage(
                $"Package written:\n{output}",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"!! Package failed: {ex.Message}");
            ShowMessage( ex.Message, "Package failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ExportProjectButton.IsEnabled = _project is not null; }
    }

    /// <summary>
    /// Modal export dialog. Collects: save location (folder), zip-the-mod flag,
    /// mod version string. Returns null on cancel.
    /// </summary>
    private ExportProjectResult? RunExportDialog()
    {
        string saveLocation = "";

        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        void Lab(string text, int row)
        {
            var tb = new System.Windows.Controls.TextBlock
            { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 8, 6) };
            System.Windows.Controls.Grid.SetRow(tb, row);
            System.Windows.Controls.Grid.SetColumn(tb, 0);
            grid.Children.Add(tb);
        }

        // Row 0: Save location + Browse
        Lab("Save location:", 0);
        var locBox = new System.Windows.Controls.TextBox
        {
            IsReadOnly = true,
            Background = ThemeBrush("SurfaceAlt"),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 6, 0, 6),
        };
        System.Windows.Controls.Grid.SetRow(locBox, 0);
        System.Windows.Controls.Grid.SetColumn(locBox, 1);
        grid.Children.Add(locBox);
        var locBtn = new System.Windows.Controls.Button
        { Content = "Browse…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 6, 0, 6) };
        System.Windows.Controls.Grid.SetRow(locBtn, 0);
        System.Windows.Controls.Grid.SetColumn(locBtn, 2);
        grid.Children.Add(locBtn);

        // Row 1 used to be a "Zip the mod" checkbox — packages always zip now,
        // controlled by the Package format dropdown in Advanced.

        // Row 2: Mod version
        Lab("Mod version:", 2);
        var versionBox = new System.Windows.Controls.TextBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 6, 0, 6),
        };
        System.Windows.Controls.Grid.SetRow(versionBox, 2);
        System.Windows.Controls.Grid.SetColumn(versionBox, 1);
        System.Windows.Controls.Grid.SetColumnSpan(versionBox, 2);
        grid.Children.Add(versionBox);

        // Row 3: buttons
        var buttons = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var package = new System.Windows.Controls.Button { Content = "Package", IsDefault = true, Width = 100, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel  = new System.Windows.Controls.Button { Content = "Cancel", IsCancel  = true, Width = 80 };
        buttons.Children.Add(package);
        buttons.Children.Add(cancel);
        System.Windows.Controls.Grid.SetRow(buttons, 3);
        System.Windows.Controls.Grid.SetColumnSpan(buttons, 3);
        grid.Children.Add(buttons);

        void Refresh() => package.IsEnabled = !string.IsNullOrWhiteSpace(saveLocation);

        locBtn.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { Title = "Choose where to save the export" };
            if (d.ShowDialog() == true)
            {
                saveLocation = d.FolderName;
                locBox.Text = saveLocation;
                Refresh();
            }
        };

        var win = new Window
        {
            Title = "Export project",
            Content = grid,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 560, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);
        package.Click += (_, _) => { win.DialogResult = true; };

        return win.ShowDialog() == true
            ? new ExportProjectResult(saveLocation, versionBox.Text.Trim())
            : null;
    }


    /// <summary>
    /// Modal for editing the open project's mod info. Mod name + Author are
    /// editable; shader package and SERPs template are shown read-only so the
    /// user sees what they came from without being able to corrupt them.
    /// Returns null on cancel.
    /// </summary>
    private EditProjectResult? RunEditProjectDialog(SimplifiedStudio.Core.Project.ProjectInfo info)
    {
        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 5; i++) grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        System.Windows.Controls.TextBlock Lab(string text, int row)
        {
            var tb = new System.Windows.Controls.TextBlock
            { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 8, 6) };
            System.Windows.Controls.Grid.SetRow(tb, row);
            grid.Children.Add(tb);
            return tb;
        }
        System.Windows.Controls.TextBox Field(int row, string text, bool readOnly)
        {
            var b = new System.Windows.Controls.TextBox
            {
                Text = text,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4),
                Margin = new Thickness(0, 6, 0, 6),
                IsReadOnly = readOnly,
                Background = ThemeBrush(readOnly ? "SurfaceAlt" : "Surface"),
            };
            System.Windows.Controls.Grid.SetRow(b, row);
            System.Windows.Controls.Grid.SetColumn(b, 1);
            grid.Children.Add(b);
            return b;
        }

        // Custom-template projects let the user edit the shader package and
        // hide the SERPs template row entirely (it's always empty for them).
        // Normal projects keep both rows read-only.
        var isCustom = info.IsCustomTemplate;

        Lab("Mod name:",   0); var nameBox   = Field(0, info.Name, readOnly: false);
        Lab("Mod author:", 1); var authorBox = Field(1, info.Author,  readOnly: false);

        Lab("Shader package:", 2);
        var shaderBox = Field(2, info.ShaderPackage, readOnly: !isCustom);
        if (isCustom)
            shaderBox.ToolTip = "Engine shader-package identifier, e.g. object_my_team.nefx2.sm51";

        var serpsLab = Lab("SERPs template:", 3);
        var serpsBox = Field(3, info.SerpsTemplate, readOnly: true);
        if (isCustom)
        {
            // Custom projects never have a SERPs template — hide the row.
            serpsLab.Visibility = Visibility.Collapsed;
            serpsBox.Visibility = Visibility.Collapsed;
        }

        // Window created up-front so the Convert button's handler can close it.
        var win = new Window
        {
            Title = "Edit project",
            Content = grid,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 560, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);

        // Bottom row: on the left, a project-type action button — "Manage ERPs…"
        // for custom projects, or "Convert to custom template…" for SERPs-template
        // projects (gated on the Advanced feature flag). On the right, OK / Cancel.
        var buttons = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 14, 0, 0) };

        var convertRequested = false;
        var manageRequested = false;

        if (isCustom)
        {
            var manageBtn = new System.Windows.Controls.Button
            {
                Content = "Manage ERPs…",
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Add, rename, export or remove the ERPs in this project's pool.",
            };
            System.Windows.Controls.DockPanel.SetDock(manageBtn, System.Windows.Controls.Dock.Left);
            buttons.Children.Add(manageBtn);
            manageBtn.Click += (_, _) =>
            {
                manageRequested = true;
                win.DialogResult = true;   // close the dialog; caller opens the ERP manager
            };
        }
        else if (TeamConfig.Load().EnableCustomTemplates)
        {
            var convertBtn = new System.Windows.Controls.Button
            {
                Content = "Convert to custom template…",
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Turn this template-based project into a custom one you can add/remove ERPs in.",
            };
            System.Windows.Controls.DockPanel.SetDock(convertBtn, System.Windows.Controls.Dock.Left);
            buttons.Children.Add(convertBtn);
            convertBtn.Click += (_, _) =>
            {
                var poolCount = _project?.AllPartPaths.Count() ?? 0;
                var shader = string.IsNullOrWhiteSpace(info.ShaderPackage) ? "(none)" : info.ShaderPackage;
                var msg =
                    "Convert this project to a custom template?\n\n" +
                    $"• Keeps all {poolCount} ERP(s) and their enabled/disabled state.\n" +
                    "• Lets you add / remove / rename ERPs via \"Manage ERPs\" — the part pool becomes yours to edit.\n" +
                    $"• Keeps the shader package ({shader}) and makes it editable.\n\n" +
                    "This can't be undone (no way back to the original .serps template link).";
                var confirm = ShowMessage( msg, "Convert to custom template",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.OK) return;
                convertRequested = true;
                win.DialogResult = true;   // close the dialog; caller performs the conversion
            };
        }

        var okCancel = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok     = new System.Windows.Controls.Button { Content = "OK",     IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel  = true, Width = 80 };
        okCancel.Children.Add(ok);
        okCancel.Children.Add(cancel);
        buttons.Children.Add(okCancel);   // fills remaining space → right-aligns

        System.Windows.Controls.Grid.SetRow(buttons, 4);
        System.Windows.Controls.Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        ok.Click += (_, _) => { win.DialogResult = true; };

        return win.ShowDialog() == true
            ? new EditProjectResult(nameBox.Text.Trim(), authorBox.Text.Trim(), shaderBox.Text.Trim(), convertRequested, manageRequested)
            : null;
    }


    /// <summary>
    /// Modal "New project" wizard: pick the .simplified save path, the mod
    /// name + author, and a .serps template. Returns null on cancel. All four
    /// fields must be filled before Start project enables.
    /// </summary>
    /// <param name="initialTemplate">
    /// Pre-fills the template field, for a .serps opened from Explorer. The
    /// wizard is otherwise unchanged - the user still names the project and
    /// chooses where it goes.
    /// </param>
    private NewProjectWizardResult? RunNewProjectWizard(string? initialTemplate = null)
    {
        var cfg = UserConfig.Load();
        string savePath = "", serpsPath = initialTemplate ?? "";

        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        // Rows: 0 save · 1 mod name · 2 author · 3 serps · 4 custom-tick · 5 shader package · 6 buttons
        for (int i = 0; i < 7; i++) grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        System.Windows.Controls.TextBlock Label(string text, int row)
        {
            var tb = new System.Windows.Controls.TextBlock
            { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 8, 6) };
            System.Windows.Controls.Grid.SetRow(tb, row);
            System.Windows.Controls.Grid.SetColumn(tb, 0);
            grid.Children.Add(tb);
            return tb;
        }
        System.Windows.Controls.TextBox Field(int row)
        {
            var b = new System.Windows.Controls.TextBox
            { VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 6) };
            System.Windows.Controls.Grid.SetRow(b, row);
            System.Windows.Controls.Grid.SetColumn(b, 1);
            grid.Children.Add(b);
            return b;
        }
        System.Windows.Controls.Button BrowseButton(int row)
        {
            var b = new System.Windows.Controls.Button
            { Content = "Browse…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 6, 0, 6) };
            System.Windows.Controls.Grid.SetRow(b, row);
            System.Windows.Controls.Grid.SetColumn(b, 2);
            grid.Children.Add(b);
            return b;
        }

        Label("Save .simplified to:", 0);
        var saveBox = Field(0); saveBox.IsReadOnly = true; saveBox.Background = ThemeBrush("SurfaceAlt");
        var saveBtn = BrowseButton(0);

        Label("Mod name:",   1);
        var nameBox   = Field(1);
        Label("Mod author:", 2);
        var authorBox = Field(2);
        // Pre-fill author from the Default author setting so repeat users don't retype.
        authorBox.Text = cfg.DefaultAuthor ?? "";

        var serpsLabel = Label("SERPs template:", 3);
        var serpsBox = Field(3); serpsBox.IsReadOnly = true;
        // Filled when Explorer handed a .serps over. The variable above already
        // held it; the box did not, so the wizard opened looking empty while
        // believing it had a template.
        serpsBox.Background = ThemeBrush("SurfaceAlt");

        // Show the file name, not the path. The name says which template this
        // is; the drive and folders in front of it say nothing and push the
        // useful part out of sight. The full path goes in the tooltip, and
        // serpsPath - not this box - is what the wizard returns.
        //
        // Scrolling the box to its end was tried first and does not work: a
        // read-only TextBox that has never had focus will not scroll from
        // CaretIndex, so it sat showing the drive letter regardless.
        void ShowTemplate(string path)
        {
            serpsBox.Text = System.IO.Path.GetFileName(path);
            serpsBox.ToolTip = string.IsNullOrEmpty(path) ? null : path;
        }

        ShowTemplate(serpsPath);

        var serpsBtn = BrowseButton(3);

        // Custom-template tickbox sits between the SERPs and Shader package rows.
        // Feature-gated: only visible when the user has enabled it from the
        // Advanced menu (Ctrl+T).
        var customCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Custom template",
            Margin = new Thickness(0, 4, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Skip the .serps template and type a shader package identifier directly.",
            Visibility = TeamConfig.Load().EnableCustomTemplates ? Visibility.Visible : Visibility.Collapsed,
        };
        System.Windows.Controls.Grid.SetRow(customCheck, 4);
        System.Windows.Controls.Grid.SetColumn(customCheck, 1);
        grid.Children.Add(customCheck);

        var shaderLabel = Label("Shader package:", 5);
        var shaderBox = Field(5);
        shaderBox.ToolTip = "Engine shader-package identifier, e.g. object_my_team.nefx2.sm51";
        // Hidden until Custom template is ticked.
        shaderLabel.Visibility = Visibility.Collapsed;
        shaderBox.Visibility   = Visibility.Collapsed;

        var buttons = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var start  = new System.Windows.Controls.Button { Content = "Start project", IsDefault = true, Width = 120, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, Width = 80 };
        buttons.Children.Add(start);
        buttons.Children.Add(cancel);
        System.Windows.Controls.Grid.SetRow(buttons, 6);
        System.Windows.Controls.Grid.SetColumn(buttons, 0);
        System.Windows.Controls.Grid.SetColumnSpan(buttons, 3);
        grid.Children.Add(buttons);

        void Refresh()
        {
            var custom = customCheck.IsChecked == true;
            var templateOk = custom
                ? !string.IsNullOrWhiteSpace(shaderBox.Text)
                : !string.IsNullOrWhiteSpace(serpsPath);
            // Author is optional — leaving it blank is fine.
            start.IsEnabled =
                !string.IsNullOrWhiteSpace(savePath) &&
                templateOk &&
                !string.IsNullOrWhiteSpace(nameBox.Text);
        }

        customCheck.Checked += (_, _) =>
        {
            serpsLabel.Visibility  = Visibility.Collapsed;
            serpsBox.Visibility    = Visibility.Collapsed;
            serpsBtn.Visibility    = Visibility.Collapsed;
            shaderLabel.Visibility = Visibility.Visible;
            shaderBox.Visibility   = Visibility.Visible;
            shaderBox.Focus();
            Refresh();
        };
        customCheck.Unchecked += (_, _) =>
        {
            serpsLabel.Visibility  = Visibility.Visible;
            serpsBox.Visibility    = Visibility.Visible;
            serpsBtn.Visibility    = Visibility.Visible;
            shaderLabel.Visibility = Visibility.Collapsed;
            shaderBox.Visibility   = Visibility.Collapsed;
            Refresh();
        };

        nameBox.TextChanged   += (_, _) => Refresh();
        authorBox.TextChanged += (_, _) => Refresh();
        shaderBox.TextChanged += (_, _) => Refresh();

        saveBtn.Click += (_, _) =>
        {
            var d = new SaveFileDialog
            {
                Title = "Where should the .simplified file be saved?",
                Filter = $"Simplified Studio project|*{ProjectStore.ProjectFileExtension}|All files|*.*",
                FileName = "untitled" + ProjectStore.ProjectFileExtension,
                InitialDirectory = (cfg.LastProjectFolder is { } dir && Directory.Exists(dir)) ? dir : null,
            };
            if (d.ShowDialog() == true)
            {
                savePath = d.FileName;
                saveBox.Text = savePath;

                // Suggest a mod name from the file name if the box is still empty.
                if (string.IsNullOrWhiteSpace(nameBox.Text))
                    nameBox.Text = Path.GetFileNameWithoutExtension(savePath);
                Refresh();
            }
        };
        serpsBtn.Click += (_, _) =>
        {
            var d = new OpenFileDialog
            {
                Title = "Choose the .serps template to start from",
                Filter = "SERPs template|*.serps|All files|*.*",
            };
            if (d.ShowDialog() == true)
            {
                serpsPath = d.FileName;
                ShowTemplate(serpsPath);
                Refresh();
            }
        };

        var win = new Window
        {
            Title = "New project",
            Content = grid,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 640, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);
        start.Click += (_, _) => { win.DialogResult = true; };

        if (win.ShowDialog() != true) return null;
        var isCustom = customCheck.IsChecked == true;
        return new NewProjectWizardResult(
            savePath,
            nameBox.Text.Trim(),
            authorBox.Text.Trim(),
            SerpsPath:           isCustom ? "" : serpsPath,
            CustomShaderPackage: isCustom ? shaderBox.Text.Trim() : "");
    }

    /// <summary>
    /// Modal parts chooser. Lists every part in the project with a checkbox.
    /// Returns the set of names the user wants enabled, or null on cancel.
    /// </summary>
    /// <summary>
    /// Parts chooser styled like the main texture list — a ListBox in
    /// Multiple-select mode where each click toggles a row in/out of the
    /// selection. Pre-seeded with whatever's currently enabled. A search
    /// box filters the visible rows; Select all / Select none operate on
    /// the visible subset.
    /// </summary>
    private HashSet<string>? RunPartsChooser(IList<string> available, ISet<string> enabledNow, bool initial)
    {
        // Backing model. Selection state lives here (not on the ListBox), which
        // is what makes search + multi-select robust.
        // Both the assignments and the order the categories were arranged in, so
        // the chooser shows them the way Manage ERPs left them.
        var categoryState = _project is null
            ? new ProjectArrangement.State()
            : ProjectArrangement.Load(_project);

        var partItems = available
            .Select(n => new PartItem(n, StripErp(DisplayErpName(n)))
            {
                IsSelected = enabledNow.Contains(n),
                Category = PartPrefix.Display(categoryState.CategoryOf(n)),
                // One number covering both category order and position within
                // it, so the chooser lists parts exactly as Manage ERPs does. A
                // part the arrangement doesn't mention falls to the bottom.
                CategoryOrder = categoryState.OrderOf(n),
            })
            .ToList();

        var listBox = new System.Windows.Controls.ListBox
        {
            ItemsSource = partItems,
            SelectionMode = System.Windows.Controls.SelectionMode.Multiple,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MaxHeight = 480,
            BorderBrush = ThemeBrush("BorderSoft"),
            BorderThickness = new Thickness(1),
            Background = ThemeBrush("Surface"),
            ItemTemplate = BuildPartItemTemplate(),
            ItemContainerStyle = BuildPartItemContainerStyle(),
        };

        // Header: hint + live counter. The counter listens to per-item changes
        // so it stays in sync even when filter + Select all / None fire.
        var counter = new System.Windows.Controls.TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("TextMuted"),
        };
        void RefreshCounter()
            => counter.Text = $"{partItems.Count(p => p.IsSelected)} of {partItems.Count} enabled";

        // Ticking a part unticks whatever it's an alternative to. Two rules
        // apply: another part in the same folder sharing its conflict digit, and
        // — the wider one — every part in any folder whose own digit clashes
        // with this part's folder. One tick can therefore clear several rows.
        // Guarded against re-entry: clearing the others raises their
        // PropertyChanged, which lands back here.
        var resolving = false;
        void ResolveConflicts(PartItem changed)
        {
            if (resolving || !changed.IsSelected) return;

            var enabledNames = partItems.Where(p => p.IsSelected).Select(p => p.Name);
            var displaced = PartConflicts.Displaced(changed.Name, enabledNames);
            if (displaced.Count == 0) return;

            resolving = true;
            try
            {
                var gone = displaced.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var other in partItems)
                    if (other.IsSelected && gone.Contains(other.Name)) other.IsSelected = false;
            }
            finally { resolving = false; }
        }

        foreach (var pi in partItems)
        {
            var item = pi;
            item.PropertyChanged += (_, _) => { ResolveConflicts(item); RefreshCounter(); };
        }

        // An existing project could already have two of a group enabled — from
        // before the parts were grouped, or from a .serps package. Settle that on
        // open rather than leaving the dialog showing an impossible state.
        var settled = PartConflicts
            .Reconcile(partItems.Where(p => p.IsSelected).Select(p => p.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pi in partItems.Where(p => p.IsSelected))
            if (!settled.Contains(pi.Name)) pi.IsSelected = false;

        RefreshCounter();

        var header = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var baseHint = initial
            ? "Pick the parts to enable in this new project:"
            : "Choose which parts are active:";
        // Only worth saying when a group actually exists in this project.
        var hasGroups = partItems.Any(p => HasConflictGroup(p.Name));
        var hint = new System.Windows.Controls.TextBlock
        {
            Text = hasGroups ? baseHint + "  Alternatives replace each other." : baseHint,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        System.Windows.Controls.DockPanel.SetDock(hint, System.Windows.Controls.Dock.Left);
        System.Windows.Controls.DockPanel.SetDock(counter, System.Windows.Controls.Dock.Right);
        header.Children.Add(counter);
        header.Children.Add(hint);

        // Toolbar: search on the left, Select all / Select none on the right
        var toolbar = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var bulkBtns = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var selAll  = new System.Windows.Controls.Button { Content = "Select all",  Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        var selNone = new System.Windows.Controls.Button { Content = "Select none", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        bulkBtns.Children.Add(selAll);
        bulkBtns.Children.Add(selNone);
        System.Windows.Controls.DockPanel.SetDock(bulkBtns, System.Windows.Controls.Dock.Right);
        var searchBox = new System.Windows.Controls.TextBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 4, 8, 4),
            ToolTip = "Filter parts by name",
        };
        toolbar.Children.Add(bulkBtns);
        toolbar.Children.Add(searchBox);

        // Filter via the ListBox's CollectionView. Hidden items keep their
        // IsSelected flag — that's the whole point of moving selection state
        // onto the model.
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(listBox.ItemsSource);

        // The arrangement decides the order whether or not any categories exist
        // — parts can be reordered inside Uncategorized alone.
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
            nameof(PartItem.CategoryOrder), System.ComponentModel.ListSortDirection.Ascending));

        // Headers only earn their place once there's more than the catch-all to
        // label, or every row would sit under one pointless heading.
        if (partItems.Any(pi => pi.Category != UncategorisedGroup))
        {
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(PartItem.Category)));
            listBox.GroupStyle.Add(BuildPartGroupStyle());
        }

        searchBox.TextChanged += (_, _) =>
        {
            var q = searchBox.Text.Trim();
            view.Filter = string.IsNullOrEmpty(q)
                ? null
                : (o => o is PartItem pi && pi.Display.Contains(q, StringComparison.OrdinalIgnoreCase));
        };

        IEnumerable<PartItem> VisibleItems() => view.Cast<object>().OfType<PartItem>();
        selAll.Click += (_, _) =>
        {
            // "All" can't mean every part of a conflict group, nor every folder
            // of a conflicting pair — the first one reached wins and the rest
            // stay off.
            var visible = VisibleItems().ToList();
            var allowed = PartConflicts.Reconcile(visible.Select(p => p.Name))
                                       .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var pi in visible) pi.IsSelected = allowed.Contains(pi.Name);
        };
        selNone.Click += (_, _) => { foreach (var pi in VisibleItems()) pi.IsSelected = false; };

        // Bottom row: OK / Cancel
        var ok     = new System.Windows.Controls.Button { Content = "OK",     IsDefault = true, Width = 90, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel  = true, Width = 90, Padding = new Thickness(0, 6, 0, 6) };
        var btnRow = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        root.Children.Add(header);
        root.Children.Add(toolbar);
        root.Children.Add(listBox);
        root.Children.Add(new System.Windows.Controls.Border { Height = 12 });   // breathing room
        root.Children.Add(btnRow);

        var win = new Window
        {
            Title = initial ? "Choose initial parts" : "Choose parts",
            Content = root,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 620, SizeToContent = SizeToContent.Height,
            ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);
        ok.Click += (_, _) => { win.DialogResult = true; };

        if (win.ShowDialog() != true) return null;
        return partItems
            .Where(p => p.IsSelected)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One row in the parts chooser. IsSelected is two-way bound to the
    /// ListBoxItem.IsSelected — keeping it on the model (instead of in the
    /// ListBox's internal selection list) is what lets filtering preserve
    /// selection. WPF's SelectionMode.Multiple + CollectionView filtering
    /// otherwise drops hidden items when you click a visible one.
    /// </summary>
    private sealed class PartItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string Name    { get; }
        public string Display { get; }

        /// <summary>
        /// Group header this row sits under. Parts with no category share one
        /// bucket rather than each becoming a group of one.
        /// </summary>
        public string Category { get; init; } = UncategorisedGroup;

        /// <summary>
        /// Position of this row's category in the project's own order. The
        /// catch-all sits last, wherever the named ones end up.
        /// </summary>
        public int CategoryOrder { get; init; } = int.MaxValue;
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        public PartItem(string name, string display) { Name = name; Display = display; }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// A row in Manage ERPs. Carries its category so the list can group by it,
    /// which is what makes categories read as folders and gives drag-and-drop
    /// somewhere to drop.
    /// </summary>
    /// <summary>
    /// Renders a category header in Manage ERPs: the folder name with its order
    /// hidden and its conflict group left showing. The binding stays on the raw
    /// folder name because that's the grouping key and what every lookup in the
    /// dialog matches on — only what's drawn changes.
    /// </summary>
    private sealed class CategoryHeaderConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter,
                              System.Globalization.CultureInfo culture) =>
            PartPrefix.DisplayWithGroup(value as string ?? "");

        public object ConvertBack(object value, Type targetType, object parameter,
                                  System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ManagedErp : System.ComponentModel.INotifyPropertyChanged
    {
        public ManagedErp(string name) => _name = name;

        private string _name;
        public string Name
        {
            get => _name;
            set { _name = value; Raise(nameof(Name)); Raise(nameof(Display)); }
        }

        private string _category = UncategorisedGroup;
        public string Category
        {
            get => _category;
            set { _category = value; Raise(nameof(Category)); }
        }

        /// <summary>
        /// True for the stand-in that keeps an empty category on screen. It has
        /// no ERP behind it, so no action applies to it.
        /// </summary>
        public bool IsPlaceholder { get; private init; }

        public static ManagedErp PlaceholderFor(string category) =>
            new("(empty — drag ERPs here)") { Category = category, IsPlaceholder = true };

        /// <summary>Full file name, prefix included — this is where it's edited.</summary>
        /// <summary>
        /// What the row shows: the file name only. Name is a path relative to
        /// ERPs/ now, and the folder half of it is already the group header —
        /// repeating it on every row would just be noise. The 000-- prefix
        /// stays, because Manage ERPs is the one place it's meant to be seen.
        /// </summary>
        public string Display => IsPlaceholder
            ? Name
            : PartPrefix.DisplayWithGroup(System.IO.Path.GetFileName(Name));

        /// <summary>
        /// Position of this row's category in the project's own order, set by
        /// the manager whenever that order or this row's category changes. The
        /// catch-all sits last.
        /// </summary>
        private int _categoryOrder = int.MaxValue;
        public int CategoryOrder
        {
            get => _categoryOrder;
            set { _categoryOrder = value; Raise(nameof(CategoryOrder)); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string prop) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));
    }

    /// <summary>Manage ERPs row — the file name, or the placeholder for an empty category.</summary>
    private static DataTemplate BuildManagedErpTemplate()
    {
        var name = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        name.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(ManagedErp.Display)));
        name.SetValue(System.Windows.Controls.TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(System.Windows.Controls.TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        name.SetValue(System.Windows.Controls.TextBlock.MarginProperty, new Thickness(14, 0, 0, 0));

        // The placeholder reads as a hint, not as a file.
        var placeholderTrigger = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(ManagedErp.IsPlaceholder)),
            Value = true,
        };
        placeholderTrigger.Setters.Add(new Setter(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            ThemeBrush("TextDisabled")));
        placeholderTrigger.Setters.Add(new Setter(
            System.Windows.Controls.TextBlock.FontStyleProperty, FontStyles.Italic));

        var template = new DataTemplate { VisualTree = name };
        template.Triggers.Add(placeholderTrigger);
        return template;
    }

    /// <summary>
    /// Category header in Manage ERPs — the folder the rows below it sit in.
    /// The catch-all is named here, unlike in the parts chooser: this is where
    /// you drag things out of a category, so it needs to be a visible target.
    /// </summary>
    private static System.Windows.Controls.GroupStyle BuildManagedGroupStyle(
        System.Windows.Controls.ContextMenu? headerMenu = null)
    {
        var header = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        header.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
            new System.Windows.Data.Binding("Name") { Converter = new CategoryHeaderConverter() });
        // One menu shared by every header — which category it acts on comes from
        // the header it was opened over, so there's nothing per-group to build.
        if (headerMenu is not null)
            header.SetValue(FrameworkElement.ContextMenuProperty, headerMenu);
        header.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, FontWeights.SemiBold);
        header.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty,
            ThemeBrush("TextMuted"));
        header.SetValue(System.Windows.Controls.TextBlock.MarginProperty, new Thickness(6, 8, 6, 3));
        header.SetValue(System.Windows.Controls.TextBlock.BackgroundProperty,
            System.Windows.Media.Brushes.Transparent);   // hit-testable, so it can be dropped on

        var containerStyle = new Style(typeof(System.Windows.Controls.GroupItem));
        containerStyle.Setters.Add(new Setter(
            System.Windows.Controls.Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        return new System.Windows.Controls.GroupStyle
        {
            HeaderTemplate = new DataTemplate { VisualTree = header },
            ContainerStyle = containerStyle,
        };
    }

    /// <summary>
    /// What the pointer is over during a drag: the category, and the part the
    /// drop should land in front of. The part is null when the pointer is on a
    /// group header or on the stand-in for an empty category, both of which mean
    /// "into this category, at the end".
    ///
    /// Category is null when the pointer is over nothing droppable.
    /// </summary>
    private static (string? Category, string? Part) DropTarget(object source)
    {
        var element = source as DependencyObject;
        while (element is not null)
        {
            if (element is System.Windows.Controls.ListBoxItem row &&
                row.DataContext is ManagedErp item)
                return (item.Category, item.IsPlaceholder ? null : item.Name);

            if (element is System.Windows.Controls.GroupItem group &&
                group.Content is System.Windows.Data.CollectionViewGroup viewGroup)
                return (viewGroup.Name as string, null);

            element = System.Windows.Media.VisualTreeHelper.GetParent(element)
                      ?? (element as FrameworkElement)?.Parent as DependencyObject;
        }
        return (null, null);
    }

    /// <summary>Ask for a new category name.</summary>
    private string? PromptForNewCategory(IList<string> existing) =>
        PromptForCategoryName("New category", "New category name:", "Create", seed: "", taken: existing);

    /// <summary>
    /// Ask for a category name. Returns null on cancel, and refuses a name
    /// already in <paramref name="taken"/> rather than silently merging into it
    /// — for a rename, the category's own name is left out of that list so
    /// re-confirming it isn't treated as a clash.
    /// </summary>
    private string? PromptForCategoryName(
        string title, string prompt, string confirmLabel, string seed, IList<string> taken)
    {
        var box = new System.Windows.Controls.TextBox
        { Text = seed, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 6) };

        var error = new System.Windows.Controls.TextBlock
        {
            Foreground = ThemeBrush("Danger"),
            FontSize = 11.5,
            Visibility = Visibility.Collapsed,
        };

        var ok = new System.Windows.Controls.Button
        { Content = confirmLabel, IsDefault = true, Width = 90, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button
        { Content = "Cancel", IsCancel = true, Width = 90, Padding = new Thickness(0, 6, 0, 6) };
        var btnRow = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new System.Windows.Controls.TextBlock
        { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(box);
        root.Children.Add(error);
        root.Children.Add(btnRow);

        var win = new Window
        {
            Title = title,
            Content = root,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 380, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);

        ok.Click += (_, _) =>
        {
            var name = box.Text.Trim();
            if (name.Length == 0)
            {
                error.Text = "Give the category a name.";
                error.Visibility = Visibility.Visible;
                return;
            }
            if (string.Equals(name, UncategorisedGroup, StringComparison.OrdinalIgnoreCase) ||
                taken.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                error.Text = $"'{name}' already exists.";
                error.Visibility = Visibility.Visible;
                return;
            }
            win.DialogResult = true;
        };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return win.ShowDialog() == true ? box.Text.Trim() : null;
    }

    /// <summary>
    /// Ask which category the selected ERPs should take. Returns the chosen
    /// name, an empty string to clear it, or null when cancelled — the three
    /// outcomes are distinct, so "clear" can't be mistaken for "cancel".
    /// </summary>
    private string? PromptForCategory(IList<string> selected, IList<string> existing, string seed)
    {
        var heading = new System.Windows.Controls.TextBlock
        {
            Text = selected.Count == 1
                ? $"Category for {DisplayErpName(selected[0])}:"
                : $"Category for {selected.Count} selected ERPs:",
            Margin = new Thickness(0, 0, 0, 10),
        };

        // Editable so an existing category can be picked or a new one typed —
        // one control instead of a list plus a "new category" flow.
        var combo = new System.Windows.Controls.ComboBox
        {
            IsEditable = true,
            ItemsSource = existing,
            Text = seed,
            Padding = new Thickness(6, 4, 6, 4),
        };

        var hint = new System.Windows.Controls.TextBlock
        {
            Text = existing.Count > 0
                ? "Pick one, or type a new name. Leave blank to remove the category."
                : "Type a name to create the first category. Leave blank to remove it.",
            Foreground = ThemeBrush("TextMuted"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var ok = new System.Windows.Controls.Button
        { Content = "OK", IsDefault = true, Width = 90, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button
        { Content = "Cancel", IsCancel = true, Width = 90, Padding = new Thickness(0, 6, 0, 6) };
        var btnRow = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        root.Children.Add(heading);
        root.Children.Add(combo);
        root.Children.Add(hint);
        root.Children.Add(btnRow);

        var win = new Window
        {
            Title = "Assign category",
            Content = root,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);
        ok.Click += (_, _) => { win.DialogResult = true; };
        combo.Loaded += (_, _) => combo.Focus();

        return win.ShowDialog() == true ? (combo.Text ?? "").Trim() : null;
    }

    /// <summary>
    /// Header for parts in no category. Defined by the Core, since it's also a
    /// real entry in the saved arrangement rather than a UI-only label.
    /// </summary>
    private const string UncategorisedGroup = "Uncategorized";

    /// <summary>Clipboard format for a category being dragged to a new position.</summary>
    private const string CategoryDragFormat = "SimplifiedStudio.Category";

    /// <summary>Set on mouse-down when the press landed on a category header.</summary>
    private string? _dragStartCategory;

    /// <summary>True when the element sits inside a group header rather than a row.</summary>
    private static bool IsHeader(object source)
    {
        var element = source as DependencyObject;
        while (element is not null)
        {
            if (element is System.Windows.Controls.ListBoxItem) return false;
            if (element is System.Windows.Controls.GroupItem) return true;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element)
                      ?? (element as FrameworkElement)?.Parent as DependencyObject;
        }
        return false;
    }

    /// <summary>
    /// Shows an element for one kind of group only. Pass <c>"uncategorised"</c>
    /// to show it just for the catch-all group, anything else to show it just
    /// for real categories.
    /// </summary>
    private sealed class GroupKindVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var isUncategorised = value as string == UncategorisedGroup;
            var wantUncategorised = parameter as string == "uncategorised";
            return isUncategorised == wantUncategorised ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Category header in the parts chooser.
    ///
    /// Real categories get their name. The catch-all gets a rule instead — it
    /// still needs to read as its own section, but a label there would be naming
    /// the absence of a category rather than a category. The spacing lives on
    /// the container, so both kinds sit apart from the group above them.
    /// </summary>
    private static System.Windows.Controls.GroupStyle BuildPartGroupStyle()
    {
        var visibility = new GroupKindVisibilityConverter();

        var header = new FrameworkElementFactory(typeof(System.Windows.Controls.StackPanel));
        header.SetValue(System.Windows.Controls.StackPanel.MarginProperty, new Thickness(6, 10, 6, 4));

        var name = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        name.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
            new System.Windows.Data.Binding("Name"));
        name.SetBinding(System.Windows.Controls.TextBlock.VisibilityProperty,
            new System.Windows.Data.Binding("Name") { Converter = visibility, ConverterParameter = "named" });
        name.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, FontWeights.SemiBold);
        // Palette lookups rather than literals, so these headings follow the
        // theme like the rest of the app instead of staying light-mode grey.
        name.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty,
            ThemeBrush("TextMuted"));

        var rule = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        rule.SetBinding(System.Windows.Controls.Border.VisibilityProperty,
            new System.Windows.Data.Binding("Name") { Converter = visibility, ConverterParameter = "uncategorised" });
        rule.SetValue(System.Windows.Controls.Border.HeightProperty, 1.0);
        rule.SetValue(System.Windows.Controls.Border.BackgroundProperty,
            ThemeBrush("BorderSoft", System.Windows.Media.Brushes.LightGray));
        rule.SetValue(System.Windows.Controls.Border.MarginProperty, new Thickness(0, 4, 0, 2));

        header.AppendChild(name);
        header.AppendChild(rule);

        // Stretch the container, or the header sits squashed against the left
        // edge instead of spanning the row.
        var containerStyle = new Style(typeof(System.Windows.Controls.GroupItem));
        containerStyle.Setters.Add(new Setter(
            System.Windows.Controls.Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        return new System.Windows.Controls.GroupStyle
        {
            HeaderTemplate = new DataTemplate { VisualTree = header },
            ContainerStyle = containerStyle,
        };
    }

    private static string StripErp(string name) =>
        name.EndsWith(".erp", StringComparison.OrdinalIgnoreCase)
            ? name.Substring(0, name.Length - 4)
            : name;

    /// <summary>
    /// A part as the user should see it: just the file name, with its
    /// <c>000--</c> prefix removed — <c>011--Wings/001--front.erp</c> reads as
    /// <c>front.erp</c>.
    ///
    /// The prefix carries order and conflict group and is part of the real file
    /// name, so it stays visible in Manage ERPs — that's where it's edited and
    /// where knowing the order matters. Everywhere else it's noise.
    /// </summary>
    private static string DisplayErpName(string name) =>
        PartPrefix.Display(System.IO.Path.GetFileName(name ?? "") ?? "");

    /// <summary>
    /// <see cref="DisplayErpName"/>, but keeps the prefix when dropping it would
    /// collide with an ERP already loaded.
    ///
    /// The resource list groups rows by this name, so <c>000--car.erp</c> and
    /// <c>010--car.erp</c> both reading as "car.erp" would silently fold two
    /// ERPs into one group. Where that would happen the prefix is what tells
    /// them apart, so it stays.
    /// </summary>
    private string UniqueDisplayErpName(string fileName)
    {
        var display = DisplayErpName(fileName);
        return _loadedErps.Any(l => string.Equals(l.DisplayName, display, StringComparison.OrdinalIgnoreCase))
            ? fileName
            : display;
    }

    /// <summary>
    /// Whether a part belongs to any conflict group at all — the third digit of
    /// its <c>000--</c> prefix being non-zero. Which parts it actually clashes
    /// with is <see cref="PartConflicts"/>'s job, since that depends on the
    /// folder it sits in as well as the digit.
    /// </summary>
    private static bool HasConflictGroup(string partPath) =>
        PartConflicts.PartConflictGroup(partPath) is not null ||
        PartConflicts.FolderConflictGroup(SimplifiedProject.CategoryOf(partPath)) is not null;

    /// <summary>
    /// DataTemplate for parts chooser rows — binds the Display field (no .erp).
    /// </summary>
    private System.Windows.DataTemplate BuildPartItemTemplate()
    {
        var xaml =
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
              "<StackPanel Margin='0,2'>" +
                "<TextBlock Text='{Binding Display}' FontWeight='SemiBold' TextTrimming='CharacterEllipsis'/>" +
              "</StackPanel>" +
            "</DataTemplate>";
        return (System.Windows.DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    /// <summary>
    /// ItemContainerStyle for parts chooser rows. Two-way binds IsSelected to
    /// the model so search-filtered items keep their selection, and replaces
    /// the default Aero selection chrome with a gray fill instead of blue.
    /// </summary>
    // ─────────────────────── PKG / XML I/O ────────────────────────────

    /// <summary>
    /// Preview pane for "we know what this resource is but we don't yet have
    /// a parser that can pretty-print it" — currently Material and Context.
    /// Renders the first fragment as a classic hex+ASCII dump. Large fragments
    /// are truncated to keep the preview responsive; the size in the metadata
    /// always reflects the full byte count.
    /// </summary>
    private const int HexPreviewByteCap = 64 * 1024;
    /// <summary>
    /// Display name for a raw resource extension. Material and Context are
    /// words, so they read as Title Case; acronym formats (PKG, XML, JSON,
    /// DDS) are spelled out in caps at their own call sites.
    /// </summary>
    private static string KindLabel(string ext) =>
        ext.Length == 0 ? ext : char.ToUpperInvariant(ext[0]) + ext[1..];

    /// <summary>
    /// Built from a XAML string, which is worth knowing: markup parsed at runtime
    /// is invisible to any search of this file for brushes or colour literals.
    /// Its colours are DynamicResource so they follow the palette like everything
    /// else — XamlReader resolves those against the application's resources at
    /// runtime, the same as markup compiled in.
    /// </summary>
    private Style BuildPartItemContainerStyle()
    {
        var xaml =
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                   "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " +
                   "TargetType='ListBoxItem'>" +
              "<Setter Property='IsSelected' Value='{Binding IsSelected, Mode=TwoWay}'/>" +
              "<Setter Property='Cursor' Value='Hand'/>" +
              "<Setter Property='Padding' Value='8,5'/>" +
              "<Setter Property='Background' Value='Transparent'/>" +
              "<Setter Property='Template'>" +
                "<Setter.Value>" +
                  "<ControlTemplate TargetType='ListBoxItem'>" +
                    "<Border x:Name='Bd' Background='{TemplateBinding Background}' " +
                            "Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>" +
                      "<ContentPresenter SnapsToDevicePixels='{TemplateBinding SnapsToDevicePixels}'/>" +
                    "</Border>" +
                    "<ControlTemplate.Triggers>" +
                      "<Trigger Property='IsMouseOver' Value='True'>" +
                        "<Setter TargetName='Bd' Property='Background' Value='{DynamicResource Hover}'/>" +
                      "</Trigger>" +
                      "<Trigger Property='IsSelected' Value='True'>" +
                        "<Setter TargetName='Bd' Property='Background' Value='{DynamicResource Selected}'/>" +
                        "<Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>" +
                      "</Trigger>" +
                    "</ControlTemplate.Triggers>" +
                  "</ControlTemplate>" +
                "</Setter.Value>" +
              "</Setter>" +
            "</Style>";
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }
}

/// <summary>
/// Row order for the resource list: the project's ERP order first, then the
/// ERP's own name, then resource type, then file name.
///
/// This exists instead of SortDescriptions because only the first key is a
/// judgement the row cannot make for itself - it belongs to the project, not
/// to the resource - and a SortDescription can only sort by a property, which
/// would mean putting that judgement on a type Simplified ERP shares and has
/// no arrangement for.
///
/// Outside a project the lookup is empty, every row scores the same, and the
/// name decides. That is the pre-project behaviour, unchanged.
/// </summary>
internal sealed class ResourceRowOrder : System.Collections.IComparer
{
    private readonly Dictionary<EgoEngineLibrary.Archive.Erp.ErpFile, int> _erpOrder;

    internal ResourceRowOrder(Dictionary<EgoEngineLibrary.Archive.Erp.ErpFile, int> erpOrder)
        => _erpOrder = erpOrder;

    public int Compare(object? x, object? y)
    {
        if (x is not ResourceListItem a || y is not ResourceListItem b) return 0;

        var byErp = OrderOf(a).CompareTo(OrderOf(b));
        if (byErp != 0) return byErp;

        // CurrentCulture, not Ordinal: that is what a SortDescription used, and
        // this change is meant to move where the order comes from, not what it is.
        var byName = string.Compare(a.ParentDisplayName, b.ParentDisplayName,
                                    StringComparison.CurrentCulture);
        if (byName != 0) return byName;

        var byKind = ((int)a.Kind).CompareTo((int)b.Kind);
        if (byKind != 0) return byKind;

        return string.Compare(a.FileName, b.FileName, StringComparison.CurrentCulture);
    }

    /// <summary>Unknown ERPs sort last, as an unplaced part does.</summary>
    private int OrderOf(ResourceListItem item)
        => item.ParentErp is not null && _erpOrder.TryGetValue(item.ParentErp, out var at)
            ? at
            : int.MaxValue;
}
