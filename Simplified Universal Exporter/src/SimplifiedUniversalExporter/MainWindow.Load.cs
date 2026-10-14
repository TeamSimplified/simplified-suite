using HelixToolkit.Wpf;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Media3D;

namespace SimplifiedUniversalExporter
{
    /// <summary>ERP load pipeline, Models sidebar, livery contexts, preview reload.</summary>
    public partial class MainWindow
    {
        private async void OnLoadErpClick(object sender, RoutedEventArgs e)
        {
            // Opens where the last ERP came from, or else the game's asset
            // groups, which is where almost every ERP worth exporting lives.
            string start = _config.LastErpFolder;
            if (string.IsNullOrWhiteSpace(start) || !Directory.Exists(start))
                start = Directory.Exists(Path.Combine(GameFolder, "2025_asset_groups"))
                    ? Path.Combine(GameFolder, "2025_asset_groups") : "";

            var ofd = new OpenFileDialog
            {
                Title = "Open F1 25 ERP",
                Filter = "F1 ERP (*.erp)|*.erp|All Files|*.*",
                InitialDirectory = start,
            };
            if (ofd.ShowDialog(this) != true) return;

            _config.LastErpFolder = Path.GetDirectoryName(ofd.FileName);
            SaveSettings();
            await LoadErpFromPath(ofd.FileName);
        }

        /// <summary>The open ERP's name in the title bar, or "No ERP open".</summary>
        private void ShowErpName(string erpPath)
        {
            bool open = !string.IsNullOrWhiteSpace(erpPath);
            ErpNameLabel.Text = open ? Path.GetFileName(erpPath) : "";
            ErpNameLabel.ToolTip = open ? erpPath : null;
            ErpNameLabel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            NoErpLabel.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Shared ERP-load pipeline. Called by the Open ERP button and by
        /// the command-line auto-load (pass an .erp path as the first
        /// argument to load it on startup — used for debug iteration).
        /// </summary>
        private async Task LoadErpFromPath(string erpPath)
        {
            string gameFolder = GameFolder;
            // Gender filter for paired male/female assets (race suits, drivers).
            // "Male" / "Female" drop the opposite variant; "Both" disables the
            // filter and loads everything (the original stacked behaviour).
            string genderUi = ((GenderComboBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "Male").ToLowerInvariant();
            F1ModelLoader.GenderFilter = (genderUi == "male" || genderUi == "female") ? genderUi : null;
            SaveSettings();
            LoadErpButton.IsEnabled = false;
            ExportFbxButton.IsEnabled = false;
            CloseErpButton.IsEnabled = false;

            // A previous ERP goes away FIRST, visibly, so the load starts
            // from a clean window instead of mutating a stale scene.
            if (_currentModel != null || Models.Count > 0)
            {
                LogToUi("Closing the open ERP...");
                CloseCurrentErp();
                PumpUi();
            }

            StatusText.Text = "Extracting ERP...";

            try
            {
                // Point the high-res mipmap sidecar loaders at the game
                // install so they can locate the full-resolution .mipmaps
                // files. Setting it BEFORE extraction lets the loader's
                // texture cache populate with hi-res brushes during load.
                // Pass the ERP path so the locator anchors at the right
                // vehicle_package (otherwise the recursive mipmaps scan
                // can return wrong-year textures).
                ConfigurePackageLocator(gameFolder, erpPath);
                // Drop every cached mipmaps path resolution from the
                // previous load. Across different yearly vehicle
                // packages the basename for the same team's textures
                // collides (e.g. "redbull_decal_da_tif.mipmaps"
                // exists for 2025 AND 2026), so a stale cache hit
                // would serve a wrong-year file.
                GenericMipmapsSidecarLoader.ResetPathCache();
                MipmapsSidecarLoader.ResetPathCache();
                LogToUi($"Open ERP: {Path.GetFileName(erpPath)}");
                if (!string.IsNullOrWhiteSpace(gameFolder))
                    LogToUi($"Game folder: {gameFolder}");
                LogToUi("Extracting ERP (clears old caches first)...");

                // Extraction is CPU+IO heavy on a fresh cache. Move it off
                // the UI thread so the status text actually paints.
                string primary = await Task.Run(() =>
                    ErpIdfExtractor.ExtractEmbeddedIdfToTempFolder(erpPath));

                if (string.IsNullOrWhiteSpace(primary) || !Directory.Exists(primary))
                    throw new InvalidOperationException(
                        "ERP extraction returned no IDF folder. See %TEMP%\\simplified-universal-exporter\\erp_cache\\<this ERP>\\_extract_log.txt");

                _loadedErpPath = erpPath;
                ShowErpName(erpPath);
                _primaryIdfFolder = primary;
                // Decode the package's GfxMaterialRes set (shader family,
                // PerMaterial values, texture bindings) so the DX showroom
                // can render with the game's own material response.
                // Best-effort -- the preview works without it.
                try { GameMaterialCatalog.LoadFromErp(erpPath); }
                catch { }
                _additionalIdfFolders.Clear();
                _additionalIdfFolders.AddRange(DiscoverAdditionalIdfFolders(primary));
                _childIdfFolder.Clear();
                LogToUi($"Primary IDF: {Path.GetFileName(Path.GetDirectoryName(primary))}");
                LogToUi($"Found {_additionalIdfFolders.Count} additional IDF folder(s).");
                // Refresh the sidebar checkbox list. Done BEFORE the load
                // loop so EnumerateAllIdfFolders() (now sidebar-aware) sees
                // the freshly-populated entries -- all checked by default
                // so this load behaves identically to before.
                RepopulateModelsList();
                // Driver-number livery contexts. Each car IDF ships
                // ~100 .emb files (one per driver number). Populating
                // the dropdown lets the user pick a number atlas;
                // until they pick one we leave it on "Auto" (the
                // loader's old behaviour: first numeric context wins).
                RepopulateContextList();
                UpdateGenderVisibility();

                StatusText.Text = $"Loading model (primary + {_additionalIdfFolders.Count} additional IDF folder(s))...";
                PumpUi();

                // Live progress through the UI-thread-bound mesh loop:
                // one status line + a paint per descriptor.
                F1ModelLoader.DescriptorPulse = (name, i, n) =>
                {
                    string shown = name;
                    int bang = shown.IndexOf("!!!", StringComparison.Ordinal);
                    if (bang > 0) shown = shown.Substring(0, bang);
                    StatusText.Text = $"Loading meshes: {shown} ({i}/{n})...";
                    PumpUi();
                };

                var merged = new Model3DGroup();
                int totalParts = 0;

                // Mesh registries (family / submesh / texture-EAID) are
                // keyed BY MESH INSTANCE, so entries from different assets
                // can never collide -- clear them ONCE per load session,
                // not per folder. Clearing inside the loop wiped every
                // earlier asset's registrations, which left multi-asset
                // character ERPs (racesuit + gloves + logos) with only the
                // LAST asset's texture EAIDs -- the FBX exporter and the
                // UV-template naming both saw earlier assets as unnamed.
                F1ModelLoader.ClearRegistries();

                foreach (var folder in EnumerateAllIdfFolders())
                {
                    // ModelPartNameRegistry is NAME-keyed -- this one does
                    // need a per-folder reset so part-name collisions
                    // between assets don't cross-pollute.
                    ModelPartNameRegistry.Clear();
                    // Apply the user's chosen livery context BEFORE
                    // LoadFolderExplicitLod runs -- AutoConfigure skips
                    // its own context discovery when one's already set.
                    ApplySelectedContextForFolder(folder);

                    Model3DGroup group;
                    try
                    {
                        // Per-folder loader log lives next to the IDF files
                        // so it's easy to grab when something looks wrong.
                        string logPath = Path.Combine(folder, "_helmet_load.log");
                        group = F1ModelLoader.LoadFolderExplicitLod(folder, 0, logPath);
                    }
                    catch (Exception ex)
                    {
                        // Don't kill the whole load if one IDF blows up --
                        // we'd rather show what we got and report.
                        StatusText.Text = $"Warning: failed to load {Path.GetFileName(folder)}: {ex.Message}";
                        continue;
                    }

                    if (group != null && group.Children.Count > 0)
                    {
                        // Find the IdfChoice this folder maps to so we
                        // can register its parts as we discover them
                        // (and honour the user's checkbox state in
                        // subsequent reloads).
                        IdfChoice choice = null;
                        foreach (var m in Models)
                        {
                            if (string.Equals(m.FolderPath, folder, StringComparison.OrdinalIgnoreCase))
                            {
                                choice = m;
                                break;
                            }
                        }
                        foreach (var child in group.Children.ToList())
                        {
                            group.Children.Remove(child);
                            if (AddChildHonouringPartFilter(merged, child, folder, choice))
                                totalParts++;
                        }
                    }
                }

                // The live DX scene refers to the old scene's materials --
                // tear it down (keeping the DX viewport fronted) before
                // replacing the scene.
                ResetDxScene();
                if (_currentModel != null)
                    Viewport.Children.Remove(_currentModel);

                // Stand the model upright. Helmet IDFs (and several other
                // F1 25 assets) ship in game-space Z-up; HelixToolkit's
                // viewport uses Y-up. +90 around X has been verified by the
                // user across helmet / suit / car loads, so we apply it
                // unconditionally to match what they've been seeing.
                var uprightTransform = new RotateTransform3D(
                    new AxisAngleRotation3D(new Vector3D(1, 0, 0), 90));
                _currentModel = new ModelVisual3D
                {
                    Content = merged,
                    Transform = uprightTransform
                };
                Viewport.Children.Add(_currentModel);
                // Re-apply the flat-shaded swap if Untextured is on
                // (freshly loaded leaves haven't been swapped yet).
                _savedMaterials.Clear();
                if (UntexturedCheckBox?.IsChecked == true)
                    OnUntexturedToggle(this, new RoutedEventArgs());
                // Same for the UV view: the old snapshots/composites refer
                // to the previous scene's leaves and bitmaps.
                _uvViewSaved.Clear();
                _uvOverlayCache.Clear();
                _uvDecalTextures.Clear();
                if (UvViewCheckBox?.IsChecked == true)
                    OnUvViewToggle(this, new RoutedEventArgs());

                // If the user has the Assemble checkbox already on (e.g. they
                // toggled it for a previous load), apply the world transforms
                // immediately so the new model lands assembled too. Otherwise
                // the freshly-loaded parts sit at identity until they toggle.
                if (AssembleCheckBox?.IsChecked == true)
                    ApplyAssemblyTransforms(merged);

                SetDefaultCameraView();
                RefreshDxView();

                LogToUi($"Loaded {totalParts} part(s) from {1 + _additionalIdfFolders.Count} IDF folder(s). Ready to export.");
                ExportFbxButton.IsEnabled = true;
                CloseErpButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                SimplifiedUi.MessageDialog.Show(this, "Failed to load ERP.\n\n" + ex.Message,
                    "Load ERP", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Load failed: " + ex.Message;
            }
            finally
            {
                F1ModelLoader.DescriptorPulse = null;
                LoadErpButton.IsEnabled = true;
            }
        }

        private IEnumerable<string> EnumerateAllIdfFolders()
        {
            // When the sidebar list is populated, honour the user's
            // per-model checkboxes -- this is the "only export the
            // models I want" knob for track ERPs with dozens of IDFs.
            // When the list is empty (legacy / no load yet) fall back
            // to the original behaviour so we don't break helmet/car
            // flows that never touch the sidebar.
            //
            // An IDF is considered "selected" when EITHER its own
            // checkbox is ticked OR at least one Part underneath it
            // is ticked. The second clause covers the case where the
            // user unticks the IDF parent but still wants specific
            // parts from it -- we have to load the folder to be able
            // to filter on those parts inside AddChildHonouringPartFilter.
            if (Models.Count > 0)
            {
                foreach (var m in Models)
                {
                    if (string.IsNullOrWhiteSpace(m.FolderPath)) continue;
                    bool anyPartTicked = false;
                    foreach (var p in m.Parts)
                        if (p.IsChecked) { anyPartTicked = true; break; }
                    if (m.IsChecked || anyPartTicked)
                        yield return m.FolderPath;
                }
                yield break;
            }
            yield return _primaryIdfFolder;
            foreach (var f in _additionalIdfFolders) yield return f;
        }

        /// <summary>
        /// Builds a short, human-friendly label for an IDF folder so the
        /// sidebar doesn't get swamped with full temp-cache paths. Picks
        /// the leaf folder name when it looks distinctive, or "parent /
        /// leaf" when the leaf is the generic "idf" / "model" container.
        /// </summary>
        private static string MakeIdfDisplayName(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "(unnamed)";
            string leaf = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
            string parent = Path.GetFileName(
                Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar)) ?? "");
            bool genericLeaf = string.Equals(leaf, "idf", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(leaf, "model", StringComparison.OrdinalIgnoreCase);
            if (genericLeaf && !string.IsNullOrEmpty(parent))
                return parent;
            if (!string.IsNullOrEmpty(parent))
                return parent + " / " + leaf;
            return leaf;
        }

        /// <summary>
        /// Refreshes the Models sidebar from _primaryIdfFolder +
        /// _additionalIdfFolders. Marks the primary so the user can
        /// spot it at a glance. Called after every ERP load.
        /// </summary>
        private void RepopulateModelsList()
        {
            Models.Clear();
            if (!string.IsNullOrWhiteSpace(_primaryIdfFolder))
            {
                Models.Add(new IdfChoice
                {
                    FolderPath = _primaryIdfFolder,
                    DisplayName = "* " + MakeIdfDisplayName(_primaryIdfFolder),
                    IsPrimary = true,
                    IsChecked = true,
                });
            }
            foreach (var f in _additionalIdfFolders.OrderBy(p =>
                MakeIdfDisplayName(p), StringComparer.OrdinalIgnoreCase))
            {
                Models.Add(new IdfChoice
                {
                    FolderPath = f,
                    DisplayName = MakeIdfDisplayName(f),
                    IsPrimary = false,
                    IsChecked = true,
                });
            }
            ModelsHintText.Text = Models.Count == 0
                ? "(No IDF folders found)"
                : $"{Models.Count} model(s). Untick to exclude from export.";
        }

        /// <summary>
        /// Scans the primary IDF folder for .emb context files and
        /// populates ContextComboBox. Called after every ERP load.
        /// "Auto" is always present at the head (preserves the old
        /// behaviour where the loader picked context #1 by itself).
        /// </summary>
        private void RepopulateContextList()
        {
            _suppressContextAutoReload = true;
            try
            {
                ContextComboBox.Items.Clear();
                ContextComboBox.Items.Add(new ContextChoice { Id = -1, Label = "Auto" });

                if (string.IsNullOrWhiteSpace(_primaryIdfFolder) ||
                    !Directory.Exists(_primaryIdfFolder))
                {
                    ContextComboBox.SelectedIndex = 0;
                    ContextComboBox.IsEnabled = false;
                    return;
                }

                try
                {
                    foreach (var (id, _) in EmbContextReader.EnumerateContexts(_primaryIdfFolder))
                    {
                        string label;
                        if (id == EmbContextReader.DefaultContextId)
                            label = "Default";
                        else
                            label = $"Driver #{id}";
                        ContextComboBox.Items.Add(new ContextChoice { Id = id, Label = label });
                    }
                }
                catch { /* enumeration is best-effort */ }

                ContextComboBox.SelectedIndex = 0;
                bool hasContexts = ContextComboBox.Items.Count > 1;
                ContextComboBox.IsEnabled = hasContexts;
                // Only surface the picker when this ERP actually ships
                // livery contexts (car ERPs); helmets/suits/tracks
                // shouldn't carry a dead dropdown.
                ContextComboBox.Visibility = hasContexts ? Visibility.Visible : Visibility.Collapsed;
                ContextLabel.Visibility = ContextComboBox.Visibility;
                _selectedContextId = -1;
            }
            finally
            {
                _suppressContextAutoReload = false;
            }
        }

        /// <summary>
        /// "Close" button: unload the open ERP -- tear down the scene,
        /// empty the sidebar and dropdowns, restore the view toggles and
        /// return the app to its just-started state.
        /// </summary>
        private void OnCloseErpClick(object sender, RoutedEventArgs e)
        {
            CloseCurrentErp();
            ExportFbxButton.IsEnabled = false;
            CloseErpButton.IsEnabled = false;
            LogToUi("ERP closed.");
        }

        /// <summary>Process everything queued on the dispatcher (paint,
        /// status updates) and come straight back -- called between mesh
        /// descriptors so the window keeps rendering through the
        /// UI-thread-bound model load.</summary>
        private static void PumpUi()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        /// <summary>Unload everything belonging to the open ERP. Shared
        /// by the Close button and the start of a new load.</summary>
        private void CloseCurrentErp()
        {
            // Scene teardown: DX first (it references the live materials),
            // then the WPF visual. The DX viewport stays fronted, empty.
            SetViewMode(false);
            _textures2D.Clear();
            ResetDxScene();
            if (_currentModel != null)
            {
                Viewport.Children.Remove(_currentModel);
                _currentModel = null;
            }

            // View toggles back to neutral, with their snapshots dropped
            // (the leaves they reference are gone with the scene).
            UvViewCheckBox.IsChecked = false;
            UntexturedCheckBox.IsChecked = false;
            _savedMaterials.Clear();
            _uvViewSaved.Clear();
            _uvOverlayCache.Clear();
                _uvDecalTextures.Clear();
            _childIdfFolder.Clear();
            F1ModelLoader.ClearRegistries();

            // Forget the ERP.
            _loadedErpPath = "";
            ShowErpName(null);
            _primaryIdfFolder = "";
            _additionalIdfFolders.Clear();
            Models.Clear();
            ModelsHintText.Text = "(Load an ERP)";
            RepopulateContextList();
            UpdateGenderVisibility();

        }

        /// <summary>
        /// Shows the Gender picker only when the loaded IDF folders
        /// actually contain gendered mesh variants -- detected with the
        /// same whole-word male/female token match the loader's filter
        /// uses on mesh filenames.
        /// </summary>
        private void UpdateGenderVisibility()
        {
            bool gendered = false;
            try
            {
                var folders = new List<string>();
                if (!string.IsNullOrWhiteSpace(_primaryIdfFolder)) folders.Add(_primaryIdfFolder);
                folders.AddRange(_additionalIdfFolders);
                foreach (var folder in folders)
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (var f in Directory.EnumerateFiles(folder))
                    {
                        string name = Path.GetFileName(f).ToLowerInvariant();
                        if (F1ModelLoader.ContainsGenderToken(name, "male") ||
                            F1ModelLoader.ContainsGenderToken(name, "female"))
                        {
                            gendered = true;
                            break;
                        }
                    }
                    if (gendered) break;
                }
            }
            catch { /* visibility is best-effort; default to hidden */ }

            GenderComboBox.Visibility = gendered ? Visibility.Visible : Visibility.Collapsed;
            GenderLabel.Visibility = GenderComboBox.Visibility;
            // Decal z-fighting only matters for racewear (suits, gloves)
            // -- exactly the gendered content, so it rides the same
            // detection.
            DecalZFightCheckBox.Visibility = GenderComboBox.Visibility;
        }

        /// <summary>
        /// Reads the current ContextComboBox selection and applies it
        /// to F1ModelLoader for the given IDF folder. Called once per
        /// folder load (since each folder has its own .emb files and
        /// the chosen ID needs to be re-resolved per-folder). Picks
        /// "Auto" when the chosen ID isn't available for this folder
        /// so missing-context cars fall through to the loader's
        /// existing first-context-wins behaviour.
        /// </summary>
        private void ApplySelectedContextForFolder(string folder)
        {
            int id = _selectedContextId;
            if (id == -1)
            {
                F1ModelLoader.ClearActiveContext();
                return;
            }
            EmbContextReader reader = null;
            try { reader = EmbContextReader.LoadFromFolder(folder, id); }
            catch { reader = null; }
            if (reader != null)
                F1ModelLoader.SetActiveContext(reader);
            else
                F1ModelLoader.ClearActiveContext();
        }

        private void OnContextSelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ContextComboBox.SelectedItem is ContextChoice cc)
                _selectedContextId = cc.Id;
            else
                _selectedContextId = -1;

            // Auto-refresh the preview so the picker behaves like a
            // live driver-number switcher. Suppressed while
            // RepopulateContextList is the one mutating the selection
            // (initial load -- nothing to refresh yet).
            if (!_suppressContextAutoReload && Models.Count > 0)
                TriggerPreviewReload();
        }

/// <summary>
        /// Programmatic Reload Preview -- same code path as the
        /// sidebar button. Used by the context / number-uv dropdowns
        /// to keep the viewport in sync with user changes.
        /// </summary>
        private void TriggerPreviewReload()
        {
            // Bail out cleanly if a reload is already running --
            // ReloadSelectedButton is disabled during the reload pass.
            if (ReloadSelectedButton != null && !ReloadSelectedButton.IsEnabled)
                return;
            OnReloadSelectedClick(this, new RoutedEventArgs());
        }

        private void OnSelectAllModelsClick(object sender, RoutedEventArgs e)
        {
            foreach (var m in Models)
            {
                m.IsChecked = true;
                foreach (var p in m.Parts) p.IsChecked = true;
            }
        }

        private void OnSelectNoneModelsClick(object sender, RoutedEventArgs e)
        {
            foreach (var m in Models)
            {
                m.IsChecked = false;
                foreach (var p in m.Parts) p.IsChecked = false;
            }
        }

        /// <summary>
        /// Adds a child Model3D to `merged`, unless the user has
        /// unticked its part inside the matching IdfChoice. Populates
        /// the IdfChoice.Parts list on the fly so newly-seen parts
        /// appear in the sidebar tree. Returns true if the child made
        /// it into the merged group.
        /// </summary>
        private bool AddChildHonouringPartFilter(
            Model3DGroup merged, Model3D child, string folder, IdfChoice choice)
        {
            // Identify this child by (descriptor part name, material
            // slot name). Both come from the loader-side registries.
            // The slot name is what lets us distinguish "boots" /
            // "gloves" / "harness" inside the one "body" descriptor
            // that the driver suit uses -- without it everything
            // collapses onto a single "body" tree entry.
            string partName = GetPartNameForGroup(child) ?? "(unnamed)";
            string slotName = GetSlotNameForGroup(child);

            // Track/car loaders sometimes hand us purely-numeric slot
            // names ("12", "0") for submeshes that don't have a
            // distinctive material -- those would replace nice
            // descriptor names like "pit_perch_ferrari" with just "12"
            // in the tree. Treat those as no-slot so we fall back to
            // the descriptor name instead.
            if (IsUselessSlotName(slotName))
                slotName = null;

            // Display rules -- resolved to preserve the team suffix
            // that lives on the DESCRIPTOR name for track content
            // (e.g. pit_perch_ferrari.idf produces partName=
            // "pit_perch_ferrari", while its material slots might
            // just be "pit_perch_01_c" -- no team info). Previously
            // we preferred the slot name and lost the team suffix
            // for anything whose slots weren't already team-tagged;
            // most Mexico pit perches ended up showing generic names.
            //
            //   slot present + slot == part                    -> part
            //   slot present + slot fully contains part        -> slot
            //     (slot IS a superset, e.g. "pit_perch_ferrari_01_c"
            //     contains "pit_perch_ferrari" -- keep the fuller form)
            //   slot present + part fully contains slot        -> part
            //     (part IS a superset, keep the fuller form)
            //   slot present, neither contains the other       -> "part / slot"
            //   no slot                                        -> part
            string display;
            if (!string.IsNullOrWhiteSpace(slotName))
            {
                bool slotEqualsPart = string.Equals(slotName, partName,
                    StringComparison.OrdinalIgnoreCase);
                bool slotContainsPart = !slotEqualsPart &&
                    slotName.IndexOf(partName, StringComparison.OrdinalIgnoreCase) >= 0;
                bool partContainsSlot = !slotEqualsPart &&
                    partName.IndexOf(slotName, StringComparison.OrdinalIgnoreCase) >= 0;
                if (slotEqualsPart) display = partName;
                else if (slotContainsPart) display = slotName;
                else if (partContainsSlot) display = partName;
                else display = partName + " / " + slotName;
            }
            else
            {
                display = partName;
            }

            PartChoice match = null;
            if (choice != null)
            {
                foreach (var p in choice.Parts)
                {
                    bool samePart = string.Equals(p.PartName, partName,
                        StringComparison.OrdinalIgnoreCase);
                    bool sameSlot = string.Equals(p.SlotName ?? "", slotName ?? "",
                        StringComparison.OrdinalIgnoreCase);
                    if (samePart && sameSlot) { match = p; break; }
                }
                if (match == null)
                {
                    match = new PartChoice
                    {
                        PartName = partName,
                        SlotName = slotName,
                        DisplayName = display,
                        IsChecked = true,
                    };
                    choice.Parts.Add(match);
                }
            }

            if (match != null && !match.IsChecked)
                return false;

            merged.Children.Add(child);
            _childIdfFolder[child] = folder;
            return true;
        }

        /// <summary>
        /// Walks a Model3D's descendant geometry and returns the
        /// material-slot leaf for the first registered submesh -- e.g.
        /// "boots" extracted from
        /// eaid://.../materials/boots.material. Returns null when no
        /// material EAID is registered, which is fine: callers fall
        /// back to MeshFamilyRegistry's descriptor name in that case.
        /// </summary>
        private static string GetSlotNameForGroup(Model3D model)
        {
            if (model == null) return null;
            var stack = new Stack<Model3D>();
            stack.Push(model);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == null) continue;
                if (cur is Model3DGroup grp)
                {
                    foreach (var ch in grp.Children) stack.Push(ch);
                    continue;
                }
                if (cur is GeometryModel3D gm && gm.Geometry is MeshGeometry3D mg)
                {
                    string eaid = MeshSubmeshRegistry.GetMaterialEaid(mg);
                    string leaf = ExtractMaterialLeaf(eaid);
                    if (!string.IsNullOrWhiteSpace(leaf)) return leaf;
                    // Fallback: the submesh's slot name (less canonical
                    // than the material leaf but still distinctive --
                    // "carbon_tile_1" vs "carbon_tile_2" etc.)
                    string submeshSlot = MeshSubmeshRegistry.GetSubmeshName(mg);
                    if (!string.IsNullOrWhiteSpace(submeshSlot)) return submeshSlot;
                }
            }
            return null;
        }

        /// <summary>
        /// Mirrors F1FbxExporter.DeriveMaterialDisplayName for the
        /// EAID-only case: pulls "boots" out of
        /// eaid://.../materials/boots.material. Inlined here so the
        /// sidebar doesn't have to crack open the exporter's private
        /// helper.
        /// </summary>
        /// <summary>
        /// Filters out slot names that are useless as user-facing
        /// labels: empty, purely numeric (the track loader assigns
        /// "0" / "12" etc. when an asset has no distinctive material
        /// slots), or single characters. These get replaced with the
        /// descriptor part name in the sidebar tree.
        /// </summary>
        private static bool IsUselessSlotName(string slot)
        {
            if (string.IsNullOrWhiteSpace(slot)) return true;
            if (slot.Length <= 1) return true;
            foreach (char c in slot)
                if (!char.IsDigit(c)) return false;
            return true; // all-digits
        }

        private static string ExtractMaterialLeaf(string materialEaid)
        {
            if (string.IsNullOrWhiteSpace(materialEaid)) return null;
            int q = materialEaid.IndexOfAny(new[] { '?', '#' });
            string eaid = q >= 0 ? materialEaid.Substring(0, q) : materialEaid;
            int slash = eaid.LastIndexOf('/');
            string leaf = slash >= 0 ? eaid.Substring(slash + 1) : eaid;
            string[] knownSuffixes = { ".material", ".tif", ".png", ".tga", ".dds" };
            foreach (string s in knownSuffixes)
            {
                if (leaf.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                {
                    leaf = leaf.Substring(0, leaf.Length - s.Length);
                    break;
                }
            }
            return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
        }

        /// <summary>
        /// Rebuild the viewport using only the currently-ticked models.
        /// Same code path the initial load takes -- just without the
        /// extraction step. Lets the user fine-tune their selection
        /// visually before kicking off an export. </summary>
        private async void OnReloadSelectedClick(object sender, RoutedEventArgs e)
        {
            if (Models.Count == 0)
            {
                StatusText.Text = "Load an ERP first.";
                return;
            }
            ReloadSelectedButton.IsEnabled = false;
            LoadErpButton.IsEnabled = false;
            ExportFbxButton.IsEnabled = false;
            try
            {
                var selected = EnumerateAllIdfFolders().ToList();
                if (selected.Count == 0)
                {
                    StatusText.Text = "No models selected.";
                    return;
                }
                StatusText.Text = $"Reloading preview ({selected.Count} model(s))...";
                // Let the status text actually paint before we head into
                // the synchronous load loop below.
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Background);

                var merged = new Model3DGroup();
                int totalParts = 0;
                _childIdfFolder.Clear();

                // Model3DGroup / GeometryModel3D are WPF DependencyObjects
                // and inherit their parent Freezable's thread affinity.
                // The original OnLoadErpClick keeps F1ModelLoader on the
                // UI thread for exactly this reason -- we mirror that here
                // instead of moving the loop into Task.Run.
                F1ModelLoader.ClearRegistries();   // once per session; see OnLoadErpClick
                F1ModelLoader.DescriptorPulse = (name, i, n) =>
                {
                    string shown = name;
                    int bang = shown.IndexOf("!!!", StringComparison.Ordinal);
                    if (bang > 0) shown = shown.Substring(0, bang);
                    StatusText.Text = $"Loading meshes: {shown} ({i}/{n})...";
                    PumpUi();
                };
                foreach (var folder in selected)
                {
                    ModelPartNameRegistry.Clear();
                    ApplySelectedContextForFolder(folder);
                    Model3DGroup group;
                    try
                    {
                        string logPath = Path.Combine(folder, "_helmet_load.log");
                        group = F1ModelLoader.LoadFolderExplicitLod(folder, 0, logPath);
                    }
                    catch { continue; }
                    if (group != null && group.Children.Count > 0)
                    {
                        IdfChoice choice = null;
                        foreach (var m in Models)
                        {
                            if (string.Equals(m.FolderPath, folder, StringComparison.OrdinalIgnoreCase))
                            {
                                choice = m;
                                break;
                            }
                        }
                        foreach (var child in group.Children.ToList())
                        {
                            group.Children.Remove(child);
                            if (AddChildHonouringPartFilter(merged, child, folder, choice))
                                totalParts++;
                        }
                    }
                }

                // The live DX scene refers to the old scene's materials --
                // tear it down (keeping the DX viewport fronted) before
                // replacing the scene.
                ResetDxScene();
                if (_currentModel != null)
                    Viewport.Children.Remove(_currentModel);
                var uprightTransform = new RotateTransform3D(
                    new AxisAngleRotation3D(new Vector3D(1, 0, 0), 90));
                _currentModel = new ModelVisual3D
                {
                    Content = merged,
                    Transform = uprightTransform
                };
                Viewport.Children.Add(_currentModel);
                // Re-apply the flat-shaded swap if Untextured is on
                // (freshly loaded leaves haven't been swapped yet).
                _savedMaterials.Clear();
                if (UntexturedCheckBox?.IsChecked == true)
                    OnUntexturedToggle(this, new RoutedEventArgs());
                // Same for the UV view: the old snapshots/composites refer
                // to the previous scene's leaves and bitmaps.
                _uvViewSaved.Clear();
                _uvOverlayCache.Clear();
                _uvDecalTextures.Clear();
                if (UvViewCheckBox?.IsChecked == true)
                    OnUvViewToggle(this, new RoutedEventArgs());
                if (AssembleCheckBox?.IsChecked == true)
                    ApplyAssemblyTransforms(merged);
                SetDefaultCameraView();
                RefreshDxView();
                StatusText.Text = $"Preview reloaded: {totalParts} part(s) from {selected.Count} model(s).";
                ExportFbxButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Reload failed: " + ex.Message;
            }
            finally
            {
                F1ModelLoader.DescriptorPulse = null;
                ReloadSelectedButton.IsEnabled = true;
                LoadErpButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Walks the ERP-extraction cache root looking for IDF folders OTHER
        /// than the primary one returned by ExtractEmbeddedIdfToTempFolder.
        /// The helmet ERP we tested ships two IDFs: the base helmet and a
        /// livery-attachments overlay. The extractor picks one; we need the
        /// others too for the viewport / FBX to include all parts.
        /// </summary>
        private static IEnumerable<string> DiscoverAdditionalIdfFolders(string primaryIdfFolder)
        {
            // Cache root is the ancestor that contains the EAID-mirrored
            // package directories (character_package, livery_package, ...).
            // Walk up until we find one that holds an "_extract_log.txt"
            // marker -- that's the cacheRoot ErpIdfExtractor created.
            string cacheRoot = primaryIdfFolder;
            for (int i = 0; i < 12 && !string.IsNullOrWhiteSpace(cacheRoot); i++)
            {
                if (File.Exists(Path.Combine(cacheRoot, "_extract_log.txt")) ||
                    File.Exists(Path.Combine(cacheRoot, "_cache_ok.txt")))
                    break;
                cacheRoot = Path.GetDirectoryName(cacheRoot);
            }
            if (string.IsNullOrWhiteSpace(cacheRoot) || !Directory.Exists(cacheRoot))
                return Array.Empty<string>();

            var results = new List<string>();
            string normalizedPrimary = NormalizePath(primaryIdfFolder);

            // An "IDF folder" is any directory that directly contains at
            // least one .idf or .idfdesc file. Use SearchOption.AllDirectories
            // and project to parent dirs, deduped.
            try
            {
                var idfFiles = Directory.EnumerateFiles(cacheRoot, "*.idf*", SearchOption.AllDirectories);
                var folders = idfFiles
                    .Select(p => Path.GetDirectoryName(p))
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(NormalizePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(d => !string.Equals(d, normalizedPrimary, StringComparison.OrdinalIgnoreCase));

                results.AddRange(folders);
            }
            catch
            {
                // If enumeration blows up (locked file, permission issue),
                // fall back to the single-IDF behaviour.
            }
            return results;
        }

        private static string NormalizePath(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return p ?? ""; }
        }
    }
}
