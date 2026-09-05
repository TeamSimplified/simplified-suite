using SimplifiedUi;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using EgoEngineLibrary.Archive.Erp;
using Microsoft.Win32;
using SimplifiedStudio.Core.Config;
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
    /// The single ERP currently loaded for editing. Null when nothing's open.
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

    private LoadedErp? _erp;
    private bool HasErp => _erp is not null;

    // Holds whatever resources we surface in the main list — textures + PKG + XML
    // rows. The field name predates PKG/XML; left as-is to keep the diff small.
    private readonly ObservableCollection<ResourceListItem> _textures = new();
    private CollectionView? _texturesView;
    private readonly TexconvRunner _runner = new();
    private uint _sliceIndex;   // Current tex-array slice being previewed/edited (0 for single-slice textures)
    private readonly PendingMips _pendingMips = new();  // New .mipmaps writes deferred until ERP save
    private bool _isDirty;      // Any in-memory change (import / strip / strip-all / pending mip writes)

    // Resource kinds the type chips are limiting the list to. Empty means no
    // restriction — that's what makes "nothing selected" show everything rather
    // than nothing.
    private readonly HashSet<ResourceKind> _kindFilter = new();

    // Which resources have been edited since the last save, by identifier. The
    // master copy lives here rather than on the rows because rows are rebuilt
    // from the ERP after bulk imports and undo, which would drop a flag held
    // only on the view model.
    private readonly HashSet<string> _modifiedIds = new(StringComparer.Ordinal);

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

    // Single-level undo: snapshot of the ERP's bytes + pending mips taken
    // before each mutation. Ctrl+Z restores; after restoring, the snapshot
    // is consumed (no redo / no chain).
    private byte[]? _undoErpBytes;
    private Dictionary<string, byte[]>? _undoPendingMipsSnap;
    private bool _undoWasDirty;
    private bool _undoSavedSince;   // True if Save/Save As fired after this snapshot was taken
    private string? _undoDescription;
    private string? _undoSelectedIdentifier;
    private HashSet<string>? _undoModified;
    private uint _undoSliceIndex;

    public MainWindow()
    {
        InitializeComponent();
        // Rounded corners, a taskbar-aware maximise, and a glyph that
        // follows the window state rather than the click.
        FramelessWindow.Attach(this, WindowMaximizeButton);

        ApplyThemeToBackdrop();

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
        _texturesView.SortDescriptions.Add(
            new System.ComponentModel.SortDescription(nameof(TextureVm.ParentDisplayName), System.ComponentModel.ListSortDirection.Ascending));
        _texturesView.SortDescriptions.Add(
            new System.ComponentModel.SortDescription(nameof(ResourceListItem.Kind), System.ComponentModel.ListSortDirection.Ascending));
        _texturesView.SortDescriptions.Add(
            new System.ComponentModel.SortDescription(nameof(TextureVm.FileName), System.ComponentModel.ListSortDirection.Ascending));
        UpdateCountLabel();

        // Window icon (title bar + Alt-Tab). The .exe's Explorer icon comes from
        // <ApplicationIcon> at build time and doesn't need this.
        //
        // We use IconBitmapDecoder and hand WPF the LARGEST frame in the .ico
        // (256×256 here). BitmapImage(uri) would pick a single frame at decode
        // time — often the smallest 16×16 one — which Windows would then
        // upscale for the title bar, looking pixelated.
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
                Icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            }
        }
        catch { /* not fatal */ }

        Closed += OnWindowClosed;

        // Restore persisted game directory. Nothing is detected on first launch:
        // an ERP opened from the game locates its own external mipmaps, and this
        // setting is only the backup for ERPs opened from elsewhere.
        var cfg = UserConfig.Load();
        if (!string.IsNullOrWhiteSpace(cfg.GameDir))
            GameDirBox.Text = cfg.GameDir;
        UpdateImportExportLabels();
    }

    private string? GameDir =>
        string.IsNullOrWhiteSpace(GameDirBox.Text) ? null : GameDirBox.Text.Trim();

    // ─────────────────────── File menu ─────────────────────────────────

    private void OpenErp_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardCurrentErp("open another ERP")) return;
        var dlg = new OpenFileDialog
        {
            Title = "Open ERP",
            Filter = "ERP files|*.erp|All files|*.*"
        };
        if (dlg.ShowDialog() == true) LoadErp(dlg.FileName);
    }

    /// <summary>
    /// Load an ERP for editing. Closes any currently-open ERP first.
    /// </summary>
    private void LoadErp(string path)
    {
        try
        {
            var erp = ErpFileIO.Load(path);
            _erp = new LoadedErp(erp, path, Path.GetFileName(path));
            SimplifiedStudio.Model.ExternalMips.ClearCache();
            _uvSession = null;   // belongs to the ERP that was open, not this one
            SaveButton.IsEnabled = true;
            SaveAsButton.IsEnabled = true;
            StripAllButton.IsEnabled = true;
            ExportAllButton.IsEnabled = true;
            ImportAllButton.IsEnabled = true;
            MergeErpButton.IsEnabled = true;
            CloseErpButton.IsEnabled = true;
            Log($"Loaded {path} — version {erp.Version}, {erp.Resources.Count} resources");
            RebuildTextureList();
            ClearDirty();   // also refreshes FilePathLabel + Title via UpdateDirtyLabel
        }
        catch (Exception ex)
        {
            ShowMessage( $"Failed to open ERP:\n{ex.Message}", "Open failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseErp_Click(object sender, RoutedEventArgs e)
    {
        if (!HasErp) return;
        if (!ConfirmDiscardCurrentErp("close it")) return;

        var name = Path.GetFileName(_erp!.Path);
        _erp = null;
        _uvSession = null;                 // belonged to the ERP just closed
        _pendingMips.Clear();
        SimplifiedStudio.Model.ExternalMips.ClearCache();

        // Drop the undo snapshot with the archive it came from: restoring it
        // afterwards would put bytes back into an ERP that is no longer open.
        _undoErpBytes = null;
        _undoPendingMipsSnap = null;
        _undoDescription = null;

        RebuildTextureList();              // no ERP — clears the rows and the chips
        ShowPreviewForItem(null);          // clears the preview and the metadata
        SaveButton.IsEnabled = false;
        SaveAsButton.IsEnabled = false;
        MergeErpButton.IsEnabled = false;
        CloseErpButton.IsEnabled = false;
        SetBulkButtonsEnabled(false);
        ClearDirty();                      // resets the title-bar label and the window title
        Log($"Closed {name}");
    }


    /// <summary>
    /// True if there are no unsaved changes, or the user explicitly opts to
    /// discard / save them. False means the caller should bail out and keep
    /// the current ERP open.
    /// </summary>
    private bool ConfirmDiscardCurrentErp(string action)
    {
        if (!_isDirty && _pendingMips.Count == 0) return true;
        var ans = ConfirmUnsavedChanges("You have unsaved changes.", action);
        switch (ans)
        {
            case MessageBoxResult.Yes:
                try { FlushLoadedErp(); ClearDirty(); return true; }
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

    private void RebuildTextureList()
    {
        _textures.Clear();
        if (!HasErp) { UpdateCountLabel(); return; }

        var loaded = _erp!;
        int failed = 0, pkgs = 0, xmls = 0, mats = 0, ctxs = 0, vtfs = 0;
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
            if (HasNameSegment(r.FileName, "material"))
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
            var namedContext = HasNameSegment(r.FileName, "context");

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
        // Remember where the archive came from — that's how textures find their
        // external .mipmaps without a configured game folder.
        foreach (var item in _textures) item.ParentPath = loaded.Path;
        // Stamp per-group totals so the group header can show a constant count
        // that doesn't shrink when the filter narrows visible rows.
        foreach (var grp in _textures.GroupBy(t => t.ParentDisplayName))
        {
            var c = grp.Count();
            foreach (var item in grp) item.ParentGroupCount = c;
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
        if (!HasErp) return;
        try
        {
            FlushLoadedErp();
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
        if (!HasErp) return;
        var loaded = _erp!;
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
            Log($"Saved {loaded.Path}" + (flushed > 0 ? $"  (+ {flushed} .mipmaps file(s))" : ""));
            ClearDirty();   // also refreshes FilePathLabel + Title via UpdateDirtyLabel
            if (_undoErpBytes is not null) _undoSavedSince = true;
        }
        catch (Exception ex)
        {
            ShowMessage( $"Save failed:\n{ex.Message}", "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Serialize the loaded ERP back to its path and flush any pending .mipmaps files.
    /// </summary>
    private void FlushLoadedErp()
    {
        if (!HasErp) return;
        var flushed = _pendingMips.Flush();
        var loaded = _erp!;
        ErpFileIO.Save(loaded.File, loaded.Path);
        Log($"Saved {loaded.Path}" + (flushed > 0 ? $"  (+ {flushed} .mipmaps file(s))" : ""));
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isDirty && _pendingMips.Count == 0) return;

        var ans = ConfirmUnsavedChanges("You have unsaved changes.", "close");

        switch (ans)
        {
            case MessageBoxResult.Yes:
                try { if (HasErp) FlushLoadedErp(); }
                catch (Exception ex)
                {
                    ShowMessage( $"Save failed:\n{ex.Message}\n\nClose anyway?",
                        "Save failed", MessageBoxButton.YesNo, MessageBoxImage.Error);
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

    /// <summary>Hook into Window.Closed (fires after Closing succeeds). No-op here.</summary>
    private void OnWindowClosed(object? sender, EventArgs e) { }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var items = (string[])e.Data.GetData(DataFormats.FileDrop);
        var erp = items.FirstOrDefault(f => f.EndsWith(".erp", StringComparison.OrdinalIgnoreCase));
        if (erp is null) return;
        if (!ConfirmDiscardCurrentErp("open the dropped ERP")) return;
        LoadErp(erp);
    }

    // ─────────────────────── Filter + selection ────────────────────────

    private void UpdateCountLabel()
    {
        int total = _textures.Count;
        int shown = _texturesView is null ? total : _texturesView.OfType<ResourceListItem>().Count();
        CountLabel.Text = total == 0 ? "" : (shown == total ? $"{total}" : $"{shown}/{total}");
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

        ExtractErpButton.IsEnabled = selected.Count > 0;
    }

    /// <summary>Every row currently selected, in list order.</summary>
    private List<ResourceListItem> SelectedResources() =>
        TextureList.SelectedItems.OfType<ResourceListItem>().ToList();

    /// <summary>
    /// Stand-in preview for a multi-row selection: the count, plus the per-kind
    /// breakdown so it's clear what Extract is about to take.
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

        // Nothing single-resource applies to a multi-row selection.
        UpdateSelectionActions();
    }

    /// <summary>
    /// Export / Import / Strip state for whatever is selected. One row behaves
    /// as it always has — the label follows the kind. Several rows turn Export
    /// into "Export selected", which writes a file each, and Import into
    /// "Import into selected", which is the opposite shape rather than the
    /// mirror: one chosen file goes into every row held.
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

        // Strip works on a selection too, but only textures carrying an
        // external chain can be stripped — a selection of PKG rows leaves
        // nothing to do.
        StripButton.Content   = "Strip mipmaps from selected";
        StripButton.IsEnabled = selected.OfType<TextureVm>().Any(t => t.HasExternalMips);

        // A UV template belongs to one texture, so there's nothing to offer for
        // a multi-row selection.
        ExportUvButton.Visibility = Visibility.Collapsed;
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
            TextureVm  => (UserConfig.Load().FileFormat?.ToUpperInvariant() ?? "DDS", true),
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
        // Strip mipmaps only ever applies to textures that have an external chain.
        StripButton.IsEnabled  = item is TextureVm tex && tex.HasExternalMips;

        // Undo whatever a multi-row selection did to these two. This has to
        // live here rather than in UpdateSelectionActions: the single-row path
        // arrives via ShowPreviewForItem, which calls straight into this.
        StripButton.Content     = "Strip mipmaps";
        ImportButton.Visibility = Visibility.Visible;
        ImportButton.ToolTip    = null;

        // Only textures have a UV layout, so the button appears with one
        // selected and gets out of the way otherwise.
        ExportUvButton.Visibility = item is TextureVm ? Visibility.Visible : Visibility.Collapsed;
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
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;

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
                case System.Windows.Input.Key.S when shift && SaveAsButton.IsEnabled:
                    SaveAs_Click(sender, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.S when !shift && SaveButton.IsEnabled:
                    Save_Click(sender, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.Z when !shift:
                    Undo();
                    e.Handled = true;
                    return;
            }
        }

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
        if (!HasErp) return;
        try
        {
            using var ms = new MemoryStream();
            _erp!.File.Write(ms);
            _undoErpBytes = ms.ToArray();
            _undoPendingMipsSnap = _pendingMips.CaptureSnapshot();
            _undoWasDirty = _isDirty;
            _undoSavedSince = false;
            _undoDescription = description;
            _undoSelectedIdentifier = (TextureList.SelectedItem as TextureVm)?.Identifier;
            _undoModified = new HashSet<string>(_modifiedIds, StringComparer.Ordinal);
            _undoSliceIndex = _sliceIndex;
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
            // Reparse the snapshotted bytes and swap in.
            using (var ms = new MemoryStream(_undoErpBytes, writable: false))
            {
                var erp = new EgoEngineLibrary.Archive.Erp.ErpFile();
                erp.Read(ms);
                if (_erp is not null) _erp.File = erp;
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
            PreviewMessage.Text = "Select a row to preview it.";
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

        var fmt = (UserConfig.Load().FileFormat ?? "DDS").ToUpperInvariant();
        if (fmt != "DDS" && fmt != "PNG") fmt = "PNG";

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

        var fmt = (UserConfig.Load().FileFormat ?? "DDS").ToUpperInvariant();
        if (fmt != "DDS" && fmt != "PNG") fmt = "PNG";

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
        var fmt = (UserConfig.Load().FileFormat ?? "DDS").ToUpperInvariant();
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
                        Dispatcher.BeginInvoke(() => Log($"  ✓ {vm.FileName}  {note}"));
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Dispatcher.BeginInvoke(() => Log($"  ✗ {vm.FileName}: {ex.Message}"));
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

    // ─────────────────────── Strip mipmaps ────────────────────────────

    // ─────────────────────── UV map export ────────────────────────────
    //
    // Built from the loaded ERP's model assets, not from the texture itself: a
    // UV layout only exists where geometry samples the texture. Getting there
    // means unpacking the archive and loading every model in it, which takes a
    // few seconds — so the result is kept for as long as the same ERP stays
    // open and a second texture's map comes out immediately.
    private SimplifiedStudio.UvEngine.UvMapSession? _uvSession;

    private async void ExportUv_Click(object sender, RoutedEventArgs e)
    {
        if (_erp is null) return;
        if (TextureList.SelectedItem is not TextureVm vm) return;

        var stem = System.IO.Path.GetFileNameWithoutExtension(vm.FileName.Replace("?", "%3F"));
        var dlg = new SaveFileDialog
        {
            Title = $"Save the UV map for {vm.FileName}",
            Filter = "PNG image|*.png",
            FileName = "uvmap_" + stem + ".png",
        };
        if (dlg.ShowDialog() != true) return;

        SetBulkButtonsEnabled(false);
        var previousCursor = Cursor;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            if (_uvSession is null)
            {
                Log("Building UV maps — unpacking the ERP and loading its models…");
                var erpPath = _erp.Path;
                _uvSession = await Task.Run(() =>
                    SimplifiedStudio.UvEngine.UvMapSession.Load(
                        erpPath,
                        line => Dispatcher.BeginInvoke(() => Log("  " + line))));

                if (_uvSession is null)
                {
                    ShowMessage(
                        "This ERP has no model assets, so there are no UV layouts to draw.\n\n" +
                        "UV maps come from the meshes that sample a texture — a car or character " +
                        "ERP has them; a texture pack or shader package doesn't.",
                        "No UV maps", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                Log($"  {_uvSession.Templates.Count} texture(s) have a UV layout" +
                    (_uvSession.Skipped > 0 ? $"  ({_uvSession.Skipped} mesh(es) skipped)" : "") + ".");
            }

            var template = _uvSession.Find(vm.FileName);
            if (template is null)
            {
                ShowMessage(
                    $"Nothing in this ERP's models samples {vm.FileName}, so it has no UV layout.\n\n" +
                    "Textures used only by materials with no geometry — or by geometry in " +
                    "another ERP — can't be templated from here.",
                    "No UV map for this texture", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Draw at the texture's real size, not the engine's. Where a
            // texture keeps its detail in an external .mipmaps file the engine
            // may only have loaded the small tail embedded in the ERP, and a
            // template has to match the file being painted on — FullWidth /
            // FullHeight is the size the surface resource itself declares.
            var session = _uvSession;
            var target = dlg.FileName;
            var name = vm.FileName;
            var width = (int)vm.FullWidth;
            var height = (int)vm.FullHeight;
            await Task.Run(() => session.Render(name, target, width, height));

            var scaled = width != template.Width || height != template.Height;
            Log($"UV map → {target}   ({width}×{height}" +
                (scaled ? $", drawn at full resolution — the ERP only holds {template.Width}×{template.Height}" : "") +
                $", {template.EdgeCount:N0} edges" +
                (template.Sources.Count > 0 ? $", from {string.Join(", ", template.Sources)}" : "") + ")");
        }
        catch (Exception ex)
        {
            Log($"!! UV map failed: {ex.Message}");
            ShowMessage( ex.Message, "Export UV map failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = previousCursor;
            SetBulkButtonsEnabled(true);
        }
    }

    private async void Strip_Click(object sender, RoutedEventArgs e)
    {
        // Several rows held: the button reads "Strip mipmaps from selected" and
        // runs the same job Strip all mipmaps does, narrowed to the selection.
        var selection = SelectedResources();
        if (selection.Count > 1)
        {
            var picked = selection.OfType<TextureVm>().Where(t => t.HasExternalMips).ToList();
            if (picked.Count == 0)
            {
                ShowMessage( "None of the selected resources are textures with external mipmaps.",
                    "Nothing to strip", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await RunStripMipmaps(picked, "Strip mipmaps from selected");
            return;
        }

        var vm = TextureList.SelectedItem as TextureVm;
        if (vm is null) return;

        if (!vm.HasExternalMips)
        {
            ShowMessage( "This texture doesn't have external mipmaps to strip.",
                "Nothing to strip", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var gd = GameDir;
        var mipPath = vm.ResolveExternalMipPath(gd);
        var haveExternal = !string.IsNullOrEmpty(mipPath) && File.Exists(mipPath);
        if (!haveExternal)
        {
            var goAhead = ShowMessage(
                "The external .mipmaps file for this texture isn't accessible. " +
                "Without it, stripping would store only the small in-ERP base image " +
                "(losing the full resolution). Continue anyway?",
                "External mipmaps not found", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (goAhead != MessageBoxResult.OK) return;
        }

        var confirm = ShowMessage(
            $"Strip external mipmaps from {vm.FileName}?\n\n" +
            "The full mip chain will be moved INSIDE the ERP. No external .mipmaps " +
            "file will be needed afterwards.\n\n" +
            "The change applies to the loaded ERP — remember to Save when done.",
            "Strip mipmaps", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        StripButton.IsEnabled = false;
        try
        {
            TakeUndoSnapshot($"strip mipmaps on {vm.FileName}");
            var result = await Task.Run(() => MipmapStripper.Strip(vm, gd, _pendingMips));
            vm.Refresh();
            MarkDirty(vm);

            var view = (CollectionView?)CollectionViewSource.GetDefaultView(_textures);
            view?.Refresh();

            var srcTag = result.UsedFullRes ? "full-res from external mips" : "base image only — full-res NOT recovered";
            Log($"Stripped {vm.FileName} — {result.Width}×{result.Height}, " +
                $"{result.MipLevelsInline} mip(s) now inline ({srcTag})");
            ShowPreview(vm);
        }
        catch (Exception ex)
        {
            Log($"!! Strip failed: {ex.Message}");
            ShowMessage( ex.Message, "Strip failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StripButton.IsEnabled = vm.HasExternalMips;
        }
    }

    private async void StripAll_Click(object sender, RoutedEventArgs e)
    {
        if (_erp is null) return;
        var targets = _textures.OfType<TextureVm>().Where(t => t.HasExternalMips).ToList();
        if (targets.Count == 0)
        {
            ShowMessage( "No textures in this ERP have external mipmaps.",
                "Nothing to strip", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunStripMipmaps(targets, "Strip all mipmaps");
    }

    /// <summary>
    /// The shared strip run over <paramref name="targets"/> — every texture in
    /// the ERP for Strip all mipmaps, or just the selected ones when several
    /// rows are held. Callers filter to textures that actually have an external
    /// chain before getting here.
    /// </summary>
    private async Task RunStripMipmaps(IReadOnlyList<TextureVm> targets, string dialogTitle)
    {
        var gd = GameDir;
        var inaccessible = targets.Count(t =>
        {
            var p = t.ResolveExternalMipPath(gd);
            return string.IsNullOrEmpty(p) || !File.Exists(p);
        });

        var msg = $"Strip external mipmaps from {targets.Count} texture(s)?\n\n" +
                  "The full mip chain will be moved INSIDE the ERP for each one. " +
                  "No external .mipmaps files will be needed afterwards.\n\n" +
                  (inaccessible > 0
                      ? $"⚠ {inaccessible} texture(s) cannot reach their external mipmap file — those " +
                        $"will be reduced to the small in-ERP base image. Set the Game folder first " +
                        $"to keep them at full resolution.\n\n"
                      : "") +
                  "The change applies to the loaded ERP — remember to Save when done.";
        var confirm = ShowMessage( msg, dialogTitle,
            MessageBoxButton.OKCancel,
            inaccessible > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        SetBulkButtonsEnabled(false);
        try
        {
            TakeUndoSnapshot($"strip mipmaps on {targets.Count} texture(s)");
            int ok = 0, fail = 0, downgraded = 0;
            var stripped = new List<ResourceListItem>();
            await Task.Run(() =>
            {
                foreach (var vm in targets)
                {
                    try
                    {
                        var r = MipmapStripper.Strip(vm, gd, _pendingMips);
                        if (!r.UsedFullRes) downgraded++;
                        ok++;
                        stripped.Add(vm);
                        Dispatcher.BeginInvoke(() =>
                            Log($"  ✓ {vm.FileName}  {r.Width}×{r.Height}, {r.MipLevelsInline} mip(s) inline" +
                                (r.UsedFullRes ? "" : "  [base only — full-res not recovered]")));
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Dispatcher.BeginInvoke(() => Log($"  ✗ {vm.FileName}: {ex.Message}"));
                    }
                }
            });

            foreach (var vm in targets) vm.Refresh();
            var view = (CollectionView?)CollectionViewSource.GetDefaultView(_textures);
            view?.Refresh();
            ShowPreview(TextureList.SelectedItem as TextureVm);

            Log($"Stripped {ok} of {targets.Count} texture(s)" +
                (downgraded > 0 ? $" ({downgraded} base-only)" : "") +
                (fail > 0 ? $", {fail} failed" : "") + ".");
            if (ok > 0) MarkDirty(stripped);
        }
        finally
        {
            SetBulkButtonsEnabled(true);
        }
    }

    // ─────────────────────── Bulk export / import ──────────────────────

    /// <summary>
    /// Display order for the bulk picker — the order the kinds are listed in
    /// the resource list's own tags.
    /// </summary>
    private static readonly ResourceKind[] BulkKindOrder =
    {
        ResourceKind.Texture, ResourceKind.Material, ResourceKind.Context,
        ResourceKind.Pkg, ResourceKind.Xml, ResourceKind.Vtf,
    };

    /// <summary>
    /// Tick-list of the resource kinds present in the loaded ERP, so a bulk
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
            ShowMessage( "This ERP has no resources this app can handle.",
                $"{verb} all", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"Which resource types should be {verb.ToLowerInvariant()}ed?",
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
            Width = 380, SizeToContent = SizeToContent.Height,
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
    /// Where a resource's file sits under a chosen root: the ERP's own folder
    /// structure, then the resource's file name carrying its kind's extension.
    /// Shared by export and import so a round-trip lands on the same path.
    /// </summary>
    private static string BulkPathFor(ResourceListItem vm, string root, string textureExt)
    {
        var sub = string.IsNullOrEmpty(vm.Folder) ? root : Path.Combine(root, vm.Folder);
        var name = vm is TextureVm
            ? vm.FileName.Replace("?", "%3F") + textureExt
            : ResourceFileIO.FileNameFor(vm);
        return Path.Combine(sub, name);
    }

    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (_erp is null) return;

        var kinds = PickResourceKinds("Export");
        if (kinds is null) return;

        await RunBulkExport(_textures.Where(r => kinds.Contains(r.Kind)).ToList(),
            "Choose output folder");
    }

    /// <summary>
    /// The shared export run: pick a folder, then write every row in
    /// <paramref name="selected"/> under the ERP's own folder structure.
    /// Export all hands it a type-filtered set; the Export button hands it the
    /// current multi-row selection.
    /// </summary>
    private async Task RunBulkExport(IReadOnlyList<ResourceListItem> selected, string folderPrompt)
    {
        var fmt = (UserConfig.Load().FileFormat ?? "DDS").ToUpperInvariant();
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
                        Dispatcher.BeginInvoke(() => Log($"  ✓ {vm.FileName}  {note}"));
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Dispatcher.BeginInvoke(() => Log($"  ✗ {vm.FileName}: {ex.Message}"));
                    }
                }
            });
            Log($"Exported {ok} of {targets.Count} resource(s) to {outDir}  [{KindSummary(targets)}]" +
                (skipped > 0 ? $" ({skipped} unsupported skipped)" : "") +
                (fail > 0 ? $", {fail} failed" : "") + ".");
        }
        finally
        {
            SetBulkButtonsEnabled(true);
        }
    }

    /// <summary>
    /// Build a new ERP from the selected rows and save it wherever the user
    /// picks. A texture is two resources in the archive — the <c>.tif</c> the
    /// list shows and the <c>.image</c> holding its surface — so selecting a
    /// texture takes both; an extracted ERP with only the .tif half would be
    /// unreadable.
    /// </summary>
    /// <summary>What a merge run was asked to do.</summary>
    private sealed record MergePlan(IReadOnlyList<string> Sources, bool Overwrite);

    /// <summary>
    /// Pick what to merge and how. Source can be a single ERP or a folder, in
    /// which case every .erp beneath it is taken, sorted by path so a run is
    /// repeatable. Returns null when cancelled.
    /// </summary>
    private MergePlan? PickMergeSources()
    {
        var chosen = new List<string>();

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Merge into the open ERP",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var pathBox = new System.Windows.Controls.TextBlock
        {
            Text = "No source chosen yet.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        }.Themed(System.Windows.Controls.TextBlock.ForegroundProperty, "TextMuted");

        var pickErp    = new System.Windows.Controls.Button { Content = "Choose ERP…",    Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        var pickFolder = new System.Windows.Controls.Button { Content = "Choose folder…", Padding = new Thickness(12, 6, 12, 6) };
        var pickRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 14),
        };
        pickRow.Children.Add(pickErp);
        pickRow.Children.Add(pickFolder);
        root.Children.Add(pickRow);
        root.Children.Add(pathBox);

        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "When a resource is in both:",
            Margin = new Thickness(0, 0, 0, 6),
        });

        var preserve = new System.Windows.Controls.RadioButton
        {
            Content = "Preserve existing",
            Style = FindResource("ChannelChip") as Style,
            IsChecked = true,
            ToolTip = "The open ERP wins. Only resources it doesn't already have are added.",
        };
        var overwrite = new System.Windows.Controls.RadioButton
        {
            Content = "Overwrite",
            Style = FindResource("ChannelChip") as Style,
            Margin = new Thickness(0),
            ToolTip = "The source wins, replacing the resource already here.",
        };
        var modeRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 16),
        };
        modeRow.Children.Add(preserve);
        modeRow.Children.Add(overwrite);
        root.Children.Add(modeRow);

        var go     = new System.Windows.Controls.Button { Content = "Merge",  IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel  = true, Width = 90 };
        var btnRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        btnRow.Children.Add(go);
        btnRow.Children.Add(cancel);
        root.Children.Add(btnRow);

        var win = new Window
        {
            Title = "Merge",
            Content = root,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 440, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);

        // The open archive is not a valid source for itself: merging a file into
        // itself would clash on every identifier and do nothing but confuse.
        // folderPath is set when the source came from Choose folder, so the
        // line can name the folder rather than listing what is inside it.
        void SetSources(IEnumerable<string> found, string describeEmpty, string? folderPath = null)
        {
            var open = _erp?.Path;
            chosen.Clear();
            chosen.AddRange(found
                .Where(f => open is null || !string.Equals(f, open, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

            pathBox.Text = chosen.Count == 0
                ? describeEmpty
                : folderPath is not null
                    ? $"{folderPath}  ({chosen.Count} ERP" + (chosen.Count == 1 ? ")" : "s)")
                    : chosen[0];
            go.IsEnabled = chosen.Count > 0;
        }

        pickErp.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose an ERP to merge in",
                Filter = "ERP archive|*.erp|All files|*.*",
            };
            if (dlg.ShowDialog() == true)
                SetSources(new[] { dlg.FileName }, "That is the ERP already open.");
        };

        pickFolder.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose a folder of ERPs to merge in" };
            if (dlg.ShowDialog() != true) return;
            var found = Directory.EnumerateFiles(dlg.FolderName, "*.erp", SearchOption.AllDirectories);
            SetSources(found, "No ERPs in that folder.", dlg.FolderName);
        };

        go.Click += (_, _) => { win.DialogResult = true; };
        if (win.ShowDialog() != true || chosen.Count == 0) return null;

        return new MergePlan(chosen, overwrite.IsChecked == true);
    }

    private async void MergeErp_Click(object sender, RoutedEventArgs e)
    {
        if (_erp is null) return;

        var plan = PickMergeSources();
        if (plan is null) return;

        TakeUndoSnapshot($"Merge {plan.Sources.Count} source(s)");
        SetBulkButtonsEnabled(false);
        try
        {
            var target = _erp.File;
            var touched = new List<string>();
            int added = 0, replaced = 0, preserved = 0, unreadable = 0;

            await Task.Run(() =>
            {
                foreach (var path in plan.Sources)
                {
                    EgoEngineLibrary.Archive.Erp.ErpFile source;
                    try
                    {
                        source = ErpFileIO.Load(path);
                    }
                    catch (Exception ex)
                    {
                        unreadable++;
                        Dispatcher.Invoke(() => Log($"  !! {Path.GetFileName(path)} couldn't be read: {ex.Message}"));
                        continue;
                    }

                    int a = 0, r = 0, p = 0;
                    foreach (var res in source.Resources)
                    {
                        var existing = target.Resources.Find(x => x.Identifier == res.Identifier);
                        if (existing is not null)
                        {
                            if (!plan.Overwrite) { p++; continue; }
                            target.Resources.Remove(existing);
                            Adopt(target, res);
                            r++;
                        }
                        else
                        {
                            Adopt(target, res);
                            a++;
                        }
                        touched.Add(res.Identifier);
                    }

                    added += a; replaced += r; preserved += p;
                    Dispatcher.Invoke(() => Log(
                        $"  + {Path.GetFileName(path)}: {a} added, {r} replaced, {p} preserved"));
                }
            });

            RebuildTextureList();

            // Dot the rows that actually changed, the way an import does.
            var changed = new HashSet<string>(touched, StringComparer.Ordinal);
            var rows = _textures.Where(t => changed.Contains(t.Identifier)).ToList();
            if (rows.Count > 0) MarkDirty(rows); else if (added + replaced > 0) MarkDirty();

            var summary = $"Merged {plan.Sources.Count} source(s): {added} added, {replaced} replaced, {preserved} preserved"
                        + (unreadable > 0 ? $", {unreadable} unreadable" : "");
            Log(summary + (added + replaced > 0 ? "  — save to commit." : ""));

            if (added + replaced == 0)
                ShowMessage(
                    "Nothing changed.\n\n" + (preserved > 0
                        ? $"All {preserved} resource(s) in the source are already here, and this ERP was set to win."
                        : "The source had no resources to add."),
                    "Merge", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"!! Merge failed: {ex.Message}");
            ShowMessage(ex.Message, "Merge failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBulkButtonsEnabled(true);
        }
    }

    /// <summary>
    /// Move a resource into <paramref name="target"/>, fragments and all. Used
    /// by both operations that put a resource in a different archive: Merge and
    /// Extract.
    ///
    /// The fragments matter: ErpFragment reads ParentFile.Version when it
    /// writes, and ErpResource.UpdateOffsets sizes its record from the same
    /// place. Repointing only the resource would leave its fragments measuring
    /// themselves against the archive they came from, which differs from the
    /// target whenever the two ERPs are different versions.
    ///
    /// Extract cannot hit that today — it stamps the new archive with the
    /// source's version, so the two always agree — which is exactly why its
    /// copy of this went unnoticed. Going through one helper means the next
    /// operation to move a resource cannot reintroduce it.
    /// </summary>
    private static void Adopt(EgoEngineLibrary.Archive.Erp.ErpFile target,
                              EgoEngineLibrary.Archive.Erp.ErpResource resource)
    {
        resource.ParentFile = target;
        foreach (var fragment in resource.Fragments)
            fragment.ParentFile = target;
        target.Resources.Add(resource);
    }

    private void ExtractToErp_Click(object sender, RoutedEventArgs e)
    {
        if (_erp is null) return;

        var selected = SelectedResources();
        if (selected.Count == 0)
        {
            ShowMessage( "Select the resources to extract first.",
                "Extract", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var companions = 0;
        foreach (var row in selected)
        {
            wanted.Add(row.Identifier);
            if (row is not TextureVm tex) continue;
            var surface = tex.Srv.Fragment0.SurfaceResourceName;
            if (!string.IsNullOrEmpty(surface) && wanted.Add(surface)) companions++;
        }

        var suggested = Path.GetFileNameWithoutExtension(_erp.Path) + "_extract.erp";
        var dlg = new SaveFileDialog
        {
            Title = $"Save {selected.Count} extracted resource(s) as a new ERP",
            Filter = "ERP archive|*.erp|All files|*.*",
            FileName = suggested,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // Adding a resource to another archive means repointing its
            // ParentFile, which would corrupt the ERP still open for editing.
            // Round-trip the in-memory bytes first and cut up the copy — that
            // also means unsaved edits come along.
            byte[] snapshot;
            using (var ms = new MemoryStream())
            {
                _erp.File.Write(ms);
                snapshot = ms.ToArray();
            }
            var source = new EgoEngineLibrary.Archive.Erp.ErpFile();
            using (var ms = new MemoryStream(snapshot)) source.Read(ms);

            var extracted = new EgoEngineLibrary.Archive.Erp.ErpFile { Version = source.Version };
            foreach (var res in source.Resources.Where(r => wanted.Contains(r.Identifier)))
                Adopt(extracted, res);           // safe: 'source' is a throwaway copy

            var missing = wanted.Count - extracted.Resources.Count;
            if (extracted.Resources.Count == 0)
                throw new InvalidOperationException("None of the selected resources could be found in the archive.");

            using (var fs = File.Create(dlg.FileName))
                extracted.Write(fs);

            Log($"Extracted {extracted.Resources.Count} resource(s) → {dlg.FileName}" +
                $"  [{KindSummary(selected)}" +
                (companions > 0 ? $" + {companions} .image" : "") + "]" +
                (missing > 0 ? $"  ({missing} not found)" : ""));
        }
        catch (Exception ex)
        {
            Log($"!! Extract failed: {ex.Message}");
            ShowMessage( ex.Message, "Extract failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ImportAll_Click(object sender, RoutedEventArgs e)
    {
        if (_erp is null) return;

        var kinds = PickResourceKinds("Import");
        if (kinds is null) return;

        var fmt = (UserConfig.Load().FileFormat ?? "DDS").ToUpperInvariant();
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
                "Expected layout:\n<chosen-folder>\\<resource-folder>\\<resource-name>\n\n" +
                $"Textures carry {ext}; PKG .json; XML .xml; Material and Context keep their own extension.",
                "Nothing to import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = ShowMessage(
            $"Import {toImport.Count} file(s)?  [{KindSummary(toImport.Select(p => p.Vm))}]\n\n" +
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
                        Dispatcher.BeginInvoke(() => Log($"  ✓ {p.Vm.FileName}  {note}"));
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Dispatcher.BeginInvoke(() => Log($"  ✗ {p.Vm.FileName}: {ex.Message}"));
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

    /// <summary>Flip every bulk + per-row action button at once for the duration of a bulk op.</summary>
    private void SetBulkButtonsEnabled(bool enabled)
    {
        StripAllButton.IsEnabled   = enabled;
        ExportAllButton.IsEnabled  = enabled;
        ImportAllButton.IsEnabled  = enabled;
        MergeErpButton.IsEnabled   = enabled;
        ExtractErpButton.IsEnabled = enabled && TextureList.SelectedItems.Count > 0;

        // Export/Import/Strip belong to the selection, so hand them back to the
        // selection logic rather than guessing a state here.
        if (enabled) UpdateSelectionActions();
        else
        {
            ExportButton.IsEnabled = false;
            ImportButton.IsEnabled = false;
            StripButton.IsEnabled  = false;
        }
    }


    /// <summary>
    /// Mark the archive dirty and flag <paramref name="vm"/>'s row as edited.
    /// Use this wherever a change belongs to particular resources; the
    /// no-argument overload stays for anything no single row owns.
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

    private void UpdateDirtyLabel()
    {
        if (!HasErp)
        {
            FilePathLabel.Text = "No ERP open";
            FilePathLabel.Foreground = ThemeBrush("TextDisabled");
            FilePathLabel.ToolTip = null;
            PendingLabel.Visibility = Visibility.Collapsed;
            Title = "Simplified ERP";
            return;
        }

        var fileName = Path.GetFileName(_erp!.Path);
        var anyChange = _isDirty || _pendingMips.Count > 0;
        FilePathLabel.Text = fileName;
        FilePathLabel.Foreground = ThemeBrush("TextPrimary");
        FilePathLabel.ToolTip = _erp.Path;   // hover for full path
        PendingLabel.Visibility = anyChange ? Visibility.Visible : Visibility.Collapsed;
        Title = anyChange ? $"Simplified ERP — {fileName} *" : $"Simplified ERP — {fileName}";
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

    // ─────────────────────── Preview channels ─────────────────────────

    private System.Windows.Media.Color EndColour(string key, System.Windows.Media.Color fallback)
        => TryFindResource(key) is System.Windows.Media.SolidColorBrush brush ? brush.Color : fallback;

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

    private sealed record SettingsResult(string? GameDir, string FileFormat, string Theme);

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var cfg = UserConfig.Load();
        var result = RunSettingsDialog(cfg);
        if (result is null) return;

        cfg.GameDir    = result.GameDir;
        cfg.FileFormat = result.FileFormat;
        cfg.Theme      = result.Theme;
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

    private bool IsLogVisible => LogPanel.Visibility == Visibility.Visible;

    /// <summary>
    /// Refresh the per-texture Import/Export button labels so they reflect the
    /// currently-chosen FileFormat ("Export PNG" / "Export DDS", etc).
    /// </summary>
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
        // Rows 0-3: existing settings fields. Row 4: credits divider + heading + body.
        // Row 5: Show/Hide log on the left, Save/Cancel on the right.
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
        gameBtns.Children.Add(gameBtn);
        System.Windows.Controls.Grid.SetRow(gameBtns, 0);
        System.Windows.Controls.Grid.SetColumn(gameBtns, 2);
        grid.Children.Add(gameBtns);

        gameBtn.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { Title = "Select your game install folder" };
            if (d.ShowDialog() == true) gameBox.Text = d.FolderName;
        };

        // Row 1: file format combo
        Lab("Texture format:", 1);
        var fmtBox = new System.Windows.Controls.ComboBox
        {
            Margin = new Thickness(0, 6, 0, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        fmtBox.Items.Add("PNG");
        fmtBox.Items.Add("DDS");
        fmtBox.SelectedItem = string.Equals(cfg.FileFormat, "PNG", StringComparison.OrdinalIgnoreCase) ? "PNG" : "DDS";
        System.Windows.Controls.Grid.SetRow(fmtBox, 1);
        System.Windows.Controls.Grid.SetColumn(fmtBox, 1);
        System.Windows.Controls.Grid.SetColumnSpan(fmtBox, 2);
        grid.Children.Add(fmtBox);

        // Row 2 (default author) removed — was only used by the now-deleted New project wizard.

        // Row 3: appearance. (It used to hold a "zip during export" checkbox.)
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

        // Row 3 used to be a "zip files during export by default" checkbox. It's
        // gone now — packages always zip — and Row 4's credits + Row 5's buttons
        // shift up. The grid is still allocated with 6 rows so existing SetRow
        // calls remain valid; row 3 just stays empty.

        // Row 4: Credits — separator line + heading + placeholder body text.
        var credits = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        credits.Children.Add(new System.Windows.Controls.Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 10),
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

        // Row 5: Show/Hide log on the left, Save / Cancel on the right. Both
        // sit in the same cell, pinned to opposite edges.
        var logBtn = new System.Windows.Controls.Button
        {
            Content = IsLogVisible ? "Hide log" : "Show log",
            Padding = new Thickness(14, 4, 14, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
            ToolTip = "Toggle the troubleshooting log panel at the bottom of the window",
        };
        // Applies immediately — it's a view toggle, not a saved setting, so it
        // deliberately survives Cancel.
        logBtn.Click += (_, _) =>
        {
            SetLogVisible(!IsLogVisible);
            logBtn.Content = IsLogVisible ? "Hide log" : "Show log";
        };
        System.Windows.Controls.Grid.SetRow(logBtn, 5);
        System.Windows.Controls.Grid.SetColumnSpan(logBtn, 3);
        grid.Children.Add(logBtn);

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
            Text = SimplifiedStudio.Util.AppVersion.TitleFor("Simplified ERP"),
            Foreground = ThemeBrush("TextMuted"),
            Margin = new Thickness(16, 16, 16, 0),
        };
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
            (string?)fmtBox.SelectedItem ?? "DDS",
            ThemeManager.Current);
    }

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

}
