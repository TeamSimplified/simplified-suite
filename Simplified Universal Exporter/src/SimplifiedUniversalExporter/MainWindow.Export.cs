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
    /// <summary>FBX export pipeline and the UV Maps template export button.</summary>
    public partial class MainWindow
    {

        private async void OnExportFbxClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_loadedErpPath) || string.IsNullOrWhiteSpace(_primaryIdfFolder))
            {
                SimplifiedUi.MessageDialog.Show(this, "Open an ERP first.", "Export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var opts = PickExportOptions();
            if (opts == null) return;

            // FBX / textures share the model pipeline and are named after
            // the .fbx the user picks; a UV-maps-only run just needs a
            // folder.
            string outDir;
            string fbxPath = null;
            if (opts.Fbx || opts.Textures)
            {
                var sfd = new SaveFileDialog
                {
                    Title = "Export model",
                    Filter = "Autodesk FBX (*.fbx)|*.fbx",
                    FileName = Path.GetFileNameWithoutExtension(_loadedErpPath) + ".fbx"
                };
                if (sfd.ShowDialog() != true) return;
                fbxPath = sfd.FileName;
                outDir = Path.GetDirectoryName(fbxPath) ?? "";
            }
            else
            {
                var ofd = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Pick the output folder for the UV template PNGs"
                };
                if (ofd.ShowDialog(this) != true) return;
                outDir = ofd.FolderName;
            }

            if (fbxPath != null)
            {
                // The FBX pipeline writes the model and its PNG texture
                // sidecars in one pass -- exactly the pairing the Blender
                // add-on consumes, so Textures exports the same way it
                // always has. A deselected half is removed AFTERWARDS,
                // tracked by before/after snapshots so files from earlier
                // exports into the same folder are never touched.
                string texDir = Path.Combine(outDir, "textures");
                var beforeFbx = SnapshotFiles(outDir, "*.fbx");
                var beforePng = SnapshotFiles(texDir, "*.png");
                await ExportFbxToPath(fbxPath);
                if (!opts.Fbx)
                {
                    foreach (var f in NewFiles(outDir, "*.fbx", beforeFbx))
                    {
                        TryDelete(f);
                        TryDelete(f + ".diag.txt");
                        TryDelete(f + ".loader.log");
                    }
                }
                if (!opts.Textures)
                {
                    foreach (var f in NewFiles(texDir, "*.png", beforePng))
                        TryDelete(f);
                    // Drop the folder too when this run created it and
                    // nothing is left inside.
                    try
                    {
                        if (Directory.Exists(texDir) &&
                            Directory.GetFileSystemEntries(texDir).Length == 0)
                            Directory.Delete(texDir);
                    }
                    catch { }
                }
            }

            // UV templates mirror the textures convention: their own
            // subfolder beside the model.
            if (opts.UvMaps)
                await ExportUvTemplatesTo(Path.Combine(outDir, "uv"));
        }

        private sealed class ExportOptions
        {
            public bool Textures;
            public bool UvMaps;
            public bool Fbx;
        }

        /// <summary>
        /// The export picker (Simplified ERP's "export all" pattern, built
        /// in code so it inherits application resources): tick what should
        /// be exported. Returns null on cancel; the ticks persist in
        /// config.json as the next run's defaults.
        /// </summary>
        private ExportOptions PickExportOptions()
        {
            var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "What should be exported?",
                Margin = new Thickness(0, 0, 0, 10),
            });

            System.Windows.Controls.CheckBox Make(string label, bool ticked)
            {
                var box = new System.Windows.Controls.CheckBox
                {
                    Content = label,
                    IsChecked = ticked,
                    Margin = new Thickness(0, 5, 0, 5),
                };
                root.Children.Add(box);
                return box;
            }
            var texBox = Make("Textures  \u2014  the model's texture PNGs", _exportTexturesPref);
            var uvBox = Make("UV Maps  \u2014  one wireframe template per texture", _exportUvMapsPref);
            var fbxBox = Make("FBX  \u2014  the model itself", _exportFbxPref);

            var ok = new System.Windows.Controls.Button
            { Content = "Export", IsDefault = true, Width = 90, Margin = new Thickness(0, 18, 8, 0) };
            var cancel = new System.Windows.Controls.Button
            { Content = "Cancel", IsCancel = true, Width = 90, Margin = new Thickness(0, 18, 0, 0) };
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
                Title = "Export",
                Content = root,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };
            SimplifiedUi.DialogChrome.Apply(win);
            ok.Click += (_, _) => { win.DialogResult = true; };
            if (win.ShowDialog() != true) return null;

            var opts = new ExportOptions
            {
                Textures = texBox.IsChecked == true,
                UvMaps = uvBox.IsChecked == true,
                Fbx = fbxBox.IsChecked == true,
            };
            if (!opts.Textures && !opts.UvMaps && !opts.Fbx)
            {
                SimplifiedUi.MessageDialog.Show(this, "Nothing was ticked.", "Export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            _exportTexturesPref = opts.Textures;
            _exportUvMapsPref = opts.UvMaps;
            _exportFbxPref = opts.Fbx;
            SaveSettings();
            return opts;
        }

        private static HashSet<string> SnapshotFiles(string dir, string pattern)
        {
            try
            {
                return new HashSet<string>(Directory.GetFiles(dir, pattern),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        private static IEnumerable<string> NewFiles(string dir, string pattern, HashSet<string> before)
        {
            try
            {
                return Directory.GetFiles(dir, pattern)
                    .Where(f => !before.Contains(f)).ToList();
            }
            catch { return new List<string>(); }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>
        /// Shared FBX-export pipeline. Called by the Export FBX button
        /// and by the command-line auto-export (pass an output .fbx path
        /// as the SECOND argument after the .erp path to export headless
        /// after the auto-load finishes — used for debug iteration).
        /// </summary>
        private async Task ExportFbxToPath(string exportOutPath)
        {
            string outPath = exportOutPath;
            string outDir = Path.GetDirectoryName(outPath) ?? "";

            // Snapshot the selected IDF folders before going off-thread.
            // The sidebar checkbox list drives this; when nothing has
            // been unticked we get back the same primary + additionals
            // the loader saw, so non-track ERPs behave identically.
            var selectedFolders = EnumerateAllIdfFolders().ToList();
            if (selectedFolders.Count == 0)
            {
                SimplifiedUi.MessageDialog.Show(this, "No models selected -- tick at least one model in the sidebar before exporting.",
                    "Export FBX", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // First selected folder becomes the "primary" the exporter
            // names its output after; everything else gets merged in.
            string exportPrimary = selectedFolders[0];
            var exportAdditional = selectedFolders.Skip(1).ToList();

            // Per-folder part filters + per-part slot whitelists. The
            // PartName set decides which descriptor parts the exporter
            // even visits; the SlotByPart dictionary then narrows
            // *inside* a part to specific material slots (the case for
            // the driver suit, whose "body" descriptor carries
            // boots/gloves/harness/etc. as slots).
            //
            // Rules:
            //   - all PartChoices ticked  ->  no filter (export all)
            //   - none ticked             ->  no filter (still export
            //                                 all -- user can untick
            //                                 the IDF itself)
            //   - mixed                   ->  filter built per below
            //
            // For a PartName with multiple PartChoices (one per slot),
            // we treat it as fully included if every slot under it is
            // ticked, else we record a per-slot whitelist of just the
            // ticked slots.
            var partFiltersByFolder = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            var slotFiltersByFolder = new Dictionary<string, Dictionary<string, HashSet<string>>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var folder in selectedFolders)
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
                if (choice == null || choice.Parts.Count == 0) continue;
                int total = choice.Parts.Count;
                int ticked = 0;
                foreach (var p in choice.Parts) if (p.IsChecked) ticked++;
                if (ticked == total) continue;
                if (ticked == 0) continue;

                // Group choices by descriptor part name.
                var byPart = new Dictionary<string, List<PartChoice>>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var p in choice.Parts)
                {
                    if (!byPart.TryGetValue(p.PartName, out var list))
                        byPart[p.PartName] = list = new List<PartChoice>();
                    list.Add(p);
                }

                var partSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var slotByPart = new Dictionary<string, HashSet<string>>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var kv in byPart)
                {
                    string partName = kv.Key;
                    var entries = kv.Value;
                    int slotCount = entries.Count;
                    int slotTicked = 0;
                    foreach (var pc in entries) if (pc.IsChecked) slotTicked++;
                    if (slotTicked == 0) continue;          // skip this part
                    if (slotTicked == slotCount)
                    {
                        // All slots ticked: whole part survives without
                        // a slot whitelist.
                        partSet.Add(partName);
                        continue;
                    }
                    // Partial: include the part AND a slot whitelist
                    // narrowing it.
                    partSet.Add(partName);
                    var slotSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pc in entries)
                    {
                        if (!pc.IsChecked) continue;
                        if (!string.IsNullOrWhiteSpace(pc.SlotName)) slotSet.Add(pc.SlotName);
                        // Also add the partName so single-slot parts
                        // whose slot is the part name still match.
                        slotSet.Add(pc.PartName);
                    }
                    slotByPart[partName] = slotSet;
                }
                if (partSet.Count > 0) partFiltersByFolder[folder] = partSet;
                if (slotByPart.Count > 0) slotFiltersByFolder[folder] = slotByPart;
            }

            // Snapshot UI state before going off-thread.
            bool onlyLod0 = OnlyLod0CheckBox?.IsChecked == true;
            bool fixDecalZFight = DecalZFightCheckBox?.IsChecked == true;
            string gameFolder = GameFolder;
            // Re-apply the gender filter in case the user changed the combobox
            // between Load and Export (the export re-runs the model loader on
            // each IDF folder, so the filter must still be set when those run).
            string exportGender = ((GenderComboBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "Male").ToLowerInvariant();
            F1ModelLoader.GenderFilter = (exportGender == "male" || exportGender == "female") ? exportGender : null;
            // Keep the game folder pinned for the duration of the export so
            // attachment IDF loads (which re-run F1ModelLoader internally)
            // also pick up hi-res mipmaps. Pass the loaded ERP so the
            // locator anchors at the right vehicle_package.
            ConfigurePackageLocator(gameFolder, _loadedErpPath);
            StatusText.Text = "Exporting FBX...";
            ExportFbxButton.IsEnabled = false;
            LoadErpButton.IsEnabled = false;

            try
            {
                await Task.Run(() =>
                {
                    // The F1FbxExporter writes per-LOD outputs named
                    // "<teamName>_LOD{n}.fbx" into the output directory.
                    // Switch its global format flag for the duration of
                    // this call only; restore even if export throws so a
                    // later (hypothetical) glTF call isn't affected.
                    var prevFmt = F1FbxExporter.CurrentExportFormatKind;
                    var prevPrune = F1FbxExporter.PruneEmptySubtrees;
                    var prevScale = F1FbxExporter.HelmetRootScale;
                    var prevOnlyLod0 = F1FbxExporter.ExportOnlyLod0;
                    var prevLoadTyres = F1ModelLoader.LoadTyres;
                    var prevDecalOffset = F1FbxExporter.OffsetDecalsToFixZFighting;
                    F1FbxExporter.CurrentExportFormatKind = F1FbxExporter.ExportFormatKind.Fbx;
                    F1FbxExporter.PruneEmptySubtrees = true;
                    // No pre-scale: PatchFbxUnitScale stamps the FBX header
                    // with UnitScaleFactor=100 so Blender / Maya size the
                    // file from metadata rather than us inflating geometry.
                    F1FbxExporter.HelmetRootScale = 1.0f;
                    F1FbxExporter.ExportOnlyLod0 = onlyLod0;
                    // Tyres are VIEW-ONLY. The exporter re-runs the model
                    // loaders internally, so its tyre pipeline is disabled
                    // for the whole export.
                    F1ModelLoader.LoadTyres = false;
                    F1FbxExporter.OffsetDecalsToFixZFighting = fixDecalZFight;
                    try
                    {
                        // Year 2025 -- the helmet ERP is F1 25 content, which
                        // routes the 2021+ loader path inside the exporter.
                        // Apply the per-folder part filter (if any) so the
                        // exporter drops parts the user unticked in the
                        // sidebar tree; restored to null in a finally so
                        // it can't leak into the additional-IDF loop below.
                        partFiltersByFolder.TryGetValue(exportPrimary,
                            out var primaryFilter);
                        slotFiltersByFolder.TryGetValue(exportPrimary,
                            out var primarySlotFilter);
                        F1FbxExporter.PartNameFilter = primaryFilter;
                        F1FbxExporter.SlotNameFilterByPart = primarySlotFilter;
                        ApplySelectedContextForFolder(exportPrimary);
                        try
                        {
                            F1FbxExporter.ExportCarAllLodsFromExtractedIdfFolder(
                                exportPrimary, outPath, 2025);
                        }
                        finally
                        {
                            F1FbxExporter.PartNameFilter = null;
                            F1FbxExporter.SlotNameFilterByPart = null;
                        }

                        // Export each additional IDF (attachments / visor strip /
                        // wings) directly into the same output directory so its
                        // PNG texture sidecars land next to the primary's. After
                        // merging the attachment FBX(es) into the primary's
                        // LOD0 FBX we delete only the intermediate .fbx files
                        // (PNG sidecars stay so the merged FBX still resolves
                        // its texture references).
                        var extrasToMerge = new List<string>();
                        foreach (var extra in exportAdditional)
                        {
                            string extraStem = Path.GetFileName(Path.GetDirectoryName(extra)?.TrimEnd(Path.DirectorySeparatorChar))
                                ?? Path.GetFileName(extra);
                            string extraOut = Path.Combine(outDir, extraStem + ".fbx");
                            var beforeFbx = new HashSet<string>(
                                Directory.GetFiles(outDir, "*.fbx"),
                                StringComparer.OrdinalIgnoreCase);
                            partFiltersByFolder.TryGetValue(extra, out var extraFilter);
                            slotFiltersByFolder.TryGetValue(extra, out var extraSlotFilter);
                            F1FbxExporter.PartNameFilter = extraFilter;
                            F1FbxExporter.SlotNameFilterByPart = extraSlotFilter;
                            ApplySelectedContextForFolder(extra);
                            try
                            {
                                F1FbxExporter.ExportCarAllLodsFromExtractedIdfFolder(extra, extraOut, 2025);
                                foreach (var p in Directory.GetFiles(outDir, "*.fbx"))
                                {
                                    if (!beforeFbx.Contains(p) && !extrasToMerge.Contains(p))
                                        extrasToMerge.Add(p);
                                }
                            }
                            catch
                            {
                                // Non-fatal: keep going so the primary helmet
                                // at least lands successfully.
                            }
                            finally
                            {
                                F1FbxExporter.PartNameFilter = null;
                                F1FbxExporter.SlotNameFilterByPart = null;
                            }
                        }

                        string finalMergedFbx = null;
                        if (extrasToMerge.Count > 0)
                        {
                            string primaryLod0 = Directory.GetFiles(outDir, "*_LOD0.fbx")
                                .Where(f => !extrasToMerge.Contains(f))
                                .FirstOrDefault();
                            if (!string.IsNullOrEmpty(primaryLod0))
                            {
                                var inputs = new List<string> { primaryLod0 };
                                inputs.AddRange(extrasToMerge);
                                F1FbxExporter.MergeFbxFiles(inputs.ToArray(), primaryLod0);
                                finalMergedFbx = primaryLod0;
                            }
                            foreach (var f in extrasToMerge)
                            {
                                try { File.Delete(f); } catch { }
                                try { File.Delete(f + ".diag.txt"); } catch { }
                                try { File.Delete(f + ".loader.log"); } catch { }
                            }
                        }
                        else
                        {
                            finalMergedFbx = Directory.GetFiles(outDir, "*_LOD0.fbx").FirstOrDefault();
                        }

                        // Rename the merged FBX to the filename the user
                        // typed in the Save dialog. F1FbxExporter names files
                        // after the IDF's resolved team name, which can be
                        // confusing when primary vs attachments stems differ.
                        if (!string.IsNullOrEmpty(finalMergedFbx) && File.Exists(finalMergedFbx))
                        {
                            string desiredStem = Path.GetFileNameWithoutExtension(outPath);
                            string desiredPath = Path.Combine(outDir, desiredStem + "_LOD0.fbx");
                            if (!string.Equals(finalMergedFbx, desiredPath, StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    if (File.Exists(desiredPath)) File.Delete(desiredPath);
                                    File.Move(finalMergedFbx, desiredPath);
                                }
                                catch { /* leave at original name */ }
                            }
                        }
                    }
                    finally
                    {
                        F1FbxExporter.CurrentExportFormatKind = prevFmt;
                        F1FbxExporter.PruneEmptySubtrees = prevPrune;
                        F1FbxExporter.HelmetRootScale = prevScale;
                        F1FbxExporter.ExportOnlyLod0 = prevOnlyLod0;
                        F1ModelLoader.LoadTyres = prevLoadTyres;
                        F1FbxExporter.OffsetDecalsToFixZFighting = prevDecalOffset;
                    }
                });

                StatusText.Text = $"Export complete. Output directory: {outDir}";
                SimplifiedUi.MessageDialog.Show(this, $"FBX exported successfully.\n\nLook in:\n{outDir}\n\nYou'll see one .fbx per LOD plus sidecar texture PNGs.",
                    "Export FBX", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SimplifiedUi.MessageDialog.Show(this, "FBX export failed.\n\n" + ex.Message,
                    "Export FBX", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Export failed: " + ex.Message;
            }
            finally
            {
                ExportFbxButton.IsEnabled = true;
                LoadErpButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// UV templates: one transparent wireframe PNG per distinct
        /// texture the loaded model samples, at the texture's exact
        /// pixel size, into <paramref name="outDir"/>. Awaitable so the
        /// export orchestrator can sequence it after the FBX pass. See
        /// UvTemplateExporter for the mechanics.
        /// </summary>
        private System.Threading.Tasks.Task ExportUvTemplatesTo(string outDir)
        {
            if (_currentModel == null || !(_currentModel.Content is Model3DGroup root))
            {
                StatusText.Text = "Load an ERP first, then export UV maps.";
                return System.Threading.Tasks.Task.CompletedTask;
            }

            // Phase 1 on the UI thread (reads the live visual tree) --
            // fast. Phase 2 (rasterizing into 2K/4K bitmaps) runs on a
            // dedicated STA worker so the window never goes Not
            // Responding; progress streams back via the dispatcher.
            List<UvTemplateExporter.TexGroup> groups;
            int skipped;
            try
            {
                (groups, skipped) = UvTemplateExporter.Collect(root);
            }
            catch (Exception ex)
            {
                StatusText.Text = "UV template collection failed: " + ex.Message;
                return System.Threading.Tasks.Task.CompletedTask;
            }
            if (groups.Count == 0)
            {
                StatusText.Text = "No textured meshes to template.";
                return System.Threading.Tasks.Task.CompletedTask;
            }

            StatusText.Text = $"Rendering {groups.Count} UV template(s)...";
            var done = new System.Threading.Tasks.TaskCompletionSource<bool>();

            var worker = new System.Threading.Thread(() =>
            {
                int written = 0;
                string error = null;
                try
                {
                    written = UvTemplateExporter.RenderAll(groups, outDir,
                        msg => Dispatcher.BeginInvoke(new Action(() => StatusText.Text = msg)));
                }
                catch (Exception ex) { error = ex.Message; }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatusText.Text = error != null
                        ? "UV template export failed: " + error
                        : $"UV templates: {written} PNG(s) written to {outDir}" +
                          (skipped > 0 ? $" ({skipped} untextured mesh(es) skipped)." : ".");
                    done.TrySetResult(true);
                }));
            });
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
            return done.Task;
        }
    }
}
