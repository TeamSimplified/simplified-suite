using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace SimplifiedUniversalExporter
{
    public partial class App : Application
    {
        /// <summary>
        /// The code the process reports when a person closed the window.
        ///
        /// Nothing in here shuts the app down on its own, so a normal exit means
        /// someone shut it. That is the difference the release smoke check needs:
        /// it starts the packaged exe and asks whether it is still up, and a build
        /// that quit by itself would otherwise report the same 0 that success
        /// reports. The command line keeps its own 0 / 1.
        /// </summary>
        public const int ClosedByUserExitCode = 1000;

        private bool _cli;

        protected override void OnExit(ExitEventArgs e)
        {
            if (!_cli) e.ApplicationExitCode = ClosedByUserExitCode;
            base.OnExit(e);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // The command line, used for release checks and batch work:
            //   "Simplified Universal Exporter.exe" --export <erpPath> <outFbxPath> [gameFolder] [gender]
            // gameFolder is optional; when present, high-res .mipmaps
            // textures from the game install are used. Bypasses the WPF
            // window entirely, runs the same export the GUI's "Export FBX"
            // button calls, logs to %TEMP%\simplified-universal-exporter\
            // cli_export.log, and exits with 0 on success / 1 on failure.
            // First, so a run from a script reads no settings and writes none.
            if (e.Args.Length >= 1 && e.Args[0] == "--export")
            {
                _cli = true;
                int code = RunCliExport(e.Args);
                Shutdown(code);
                return;
            }

            // The saved palette, before the first window is built, so the app
            // never flashes the wrong theme on the way up.
            try { SimplifiedUi.ThemeManager.Apply(Core.Config.UserConfig.Load().Theme); }
            catch { /* the default palette is already merged */ }

            // The game folder, the first time the app is run: with it, exports
            // use the full-resolution textures in the game's mip files.
            try
            {
                var config = Core.Config.UserConfig.Load();
                if (Simplified.Shared.GameDirDetector.DetectIfUnset(config.GameDir) is { } found)
                {
                    config.GameDir = found;
                    config.Save();
                }
            }
            catch { /* Settings still has Detect */ }

            base.OnStartup(e);
        }

        private static int RunCliExport(string[] args)
        {
            string logPath = Path.Combine(AppPaths.TempRoot, "cli_export.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

            try
            {
                if (args.Length < 3)
                {
                    log.WriteLine("Usage: --export <erpPath> <outFbxPath> [gameFolder] [gender=male|female|both]");
                    return 1;
                }
                string erpPath = args[1];
                string outFbxPath = args[2];
                string gameFolder = args.Length >= 4 ? args[3] : null;
                string gender = args.Length >= 5 ? args[4].ToLowerInvariant() : "male";
                F1ModelLoader.GenderFilter =
                    (gender == "male" || gender == "female") ? gender : null;

                log.WriteLine($"[{DateTime.Now:HH:mm:ss}] CLI export start");
                log.WriteLine($"  erp:        {erpPath}");
                log.WriteLine($"  outFbx:     {outFbxPath}");
                log.WriteLine($"  gameFolder: {gameFolder ?? "(none)"}");
                log.WriteLine($"  gender:     {gender}");

                if (!File.Exists(erpPath))
                {
                    log.WriteLine("  ERROR: ERP not found.");
                    return 1;
                }

                if (!string.IsNullOrWhiteSpace(gameFolder) && Directory.Exists(gameFolder))
                {
                    F1PackageLocator.Last = new F1PackageLocator.Located
                    {
                        GameRoot = gameFolder,
                        VehiclePackageRoot = gameFolder,
                        Kind = F1PackageLocator.PackageKind.F1_2025,
                        PackageFolderName = Path.GetFileName(gameFolder.TrimEnd(Path.DirectorySeparatorChar))
                    };
                    log.WriteLine($"  high-res mipmaps: enabled, scanning {gameFolder}");
                }
                else if (!string.IsNullOrWhiteSpace(gameFolder))
                {
                    log.WriteLine($"  WARNING: gameFolder does not exist; falling back to embedded mips.");
                }

                log.WriteLine($"[{DateTime.Now:HH:mm:ss}] Extracting ERP...");
                string primary = ErpIdfExtractor.ExtractEmbeddedIdfToTempFolder(erpPath);
                if (string.IsNullOrWhiteSpace(primary) || !Directory.Exists(primary))
                {
                    log.WriteLine("  ERROR: extractor returned no IDF folder.");
                    return 1;
                }
                log.WriteLine($"  primary IDF folder: {primary}");

                log.WriteLine($"[{DateTime.Now:HH:mm:ss}] Exporting FBX...");
                F1FbxExporter.CurrentExportFormatKind = F1FbxExporter.ExportFormatKind.Fbx;
                F1FbxExporter.PruneEmptySubtrees = true;
                // Native unit scale. The PatchFbxUnitScale step writes
                // UnitScaleFactor=100 (=> "1 unit = 1 m") into the FBX header
                // so Blender / Maya size the file correctly on import without
                // us inflating the geometry. Set to 1.0 (no pre-scale).
                F1FbxExporter.HelmetRootScale = 1.0f;
                F1FbxExporter.ExportOnlyLod0 = true;
                // Push decal / overlay vertices 0.5mm out along their normals
                // to neutralise z-fighting with the base body in Blender/Maya.
                F1FbxExporter.OffsetDecalsToFixZFighting = true;
                F1FbxExporter.ExportCarAllLodsFromExtractedIdfFolder(primary, outFbxPath, 2025);

                // The helmet ERP ships two IDF folders: the base helmet
                // (primary) and one or more attachment overlays (visor strip,
                // wings, replica decals). The latter live elsewhere in the
                // extraction cache. Find and export each as a sibling FBX so
                // the user gets every renderable piece.
                string cacheRoot = primary;
                for (int i = 0; i < 12 && !string.IsNullOrWhiteSpace(cacheRoot); i++)
                {
                    if (File.Exists(Path.Combine(cacheRoot, "_extract_log.txt")) ||
                        File.Exists(Path.Combine(cacheRoot, "_cache_ok.txt")))
                        break;
                    cacheRoot = Path.GetDirectoryName(cacheRoot);
                }
                if (!string.IsNullOrWhiteSpace(cacheRoot) && Directory.Exists(cacheRoot))
                {
                    string siblingOutDir = Path.GetDirectoryName(outFbxPath) ?? "";
                    string normPrimary = Path.GetFullPath(primary).TrimEnd(Path.DirectorySeparatorChar);
                    var idfFolders = System.Linq.Enumerable.Distinct(
                        System.Linq.Enumerable.Select(
                            Directory.EnumerateFiles(cacheRoot, "*.idf*", SearchOption.AllDirectories),
                            f => Path.GetFullPath(Path.GetDirectoryName(f) ?? "").TrimEnd(Path.DirectorySeparatorChar)),
                        StringComparer.OrdinalIgnoreCase);

                    // Each additional IDF (visor strip / wings / replica decals)
                    // gets exported directly into the SAME output directory so
                    // its PNG texture sidecars land alongside the primary's
                    // sidecars. After merging the attachment FBX(es) into the
                    // primary LOD0 FBX, we delete only the intermediate
                    // attachment .fbx files (the PNG sidecars stay so the
                    // merged FBX's texture references still resolve).
                    var extrasToMerge = new List<string>();
                    foreach (var extra in idfFolders)
                    {
                        if (string.Equals(extra, normPrimary, StringComparison.OrdinalIgnoreCase)) continue;
                        string extraStem = Path.GetFileName(Path.GetDirectoryName(extra)?.TrimEnd(Path.DirectorySeparatorChar))
                            ?? Path.GetFileName(extra);
                        string extraOut = Path.Combine(siblingOutDir, extraStem + ".fbx");
                        log.WriteLine($"[{DateTime.Now:HH:mm:ss}] Exporting attachment IDF: {extra}");
                        // Snapshot the dir BEFORE the attachment export so we
                        // know which .fbx files it produced (its
                        // ResolveTeamName + "_LOD0.fbx" naming may differ
                        // from extraStem).
                        var before = new HashSet<string>(
                            Directory.GetFiles(siblingOutDir, "*.fbx"),
                            StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            F1FbxExporter.ExportCarAllLodsFromExtractedIdfFolder(extra, extraOut, 2025);
                            foreach (var p in Directory.GetFiles(siblingOutDir, "*.fbx"))
                            {
                                if (!before.Contains(p) && !extrasToMerge.Contains(p))
                                    extrasToMerge.Add(p);
                            }
                        }
                        catch (Exception ex)
                        {
                            log.WriteLine($"  WARNING attachment export failed: {ex.Message}");
                        }
                    }

                    // Merge attachments into the primary helmet's LOD0 FBX,
                    // then delete the intermediate attachment .fbx files.
                    string finalMergedFbx = null;
                    if (extrasToMerge.Count > 0)
                    {
                        string primaryLod0 = Directory.GetFiles(siblingOutDir, "*_LOD0.fbx")
                            .Where(f => !extrasToMerge.Contains(f))
                            .FirstOrDefault();
                        if (!string.IsNullOrEmpty(primaryLod0))
                        {
                            log.WriteLine($"[{DateTime.Now:HH:mm:ss}] Merging {extrasToMerge.Count} attachment FBX(s) into {Path.GetFileName(primaryLod0)}");
                            var inputs = new List<string> { primaryLod0 };
                            inputs.AddRange(extrasToMerge);
                            F1FbxExporter.MergeFbxFiles(inputs.ToArray(), primaryLod0);
                            log.WriteLine($"  merge complete -> {primaryLod0}");
                            finalMergedFbx = primaryLod0;
                        }

                        // Clean up the intermediate attachment .fbx files +
                        // their .diag/.log siblings. PNG sidecars stay -- the
                        // merged FBX still references them.
                        foreach (var f in extrasToMerge)
                        {
                            try { File.Delete(f); } catch { }
                            // Keep .diag.txt + .loader.log for now -- useful
                            // for tracing texture binding on attachments.
                        }
                    }
                    else
                    {
                        // No attachments; primary is the only LOD0 FBX.
                        finalMergedFbx = Directory.GetFiles(siblingOutDir, "*_LOD0.fbx").FirstOrDefault();
                    }

                    // Rename the merged FBX to match the user's chosen output
                    // filename. F1FbxExporter writes "<teamName>_LOD0.fbx"
                    // (teamName derived from the IDF folder), which can be
                    // confusing when the picker chose a different stem for
                    // primary vs attachments. Renaming gives the user the
                    // filename they actually typed; PNG sidecar references
                    // inside the FBX are RELATIVE filenames so they keep
                    // resolving.
                    if (!string.IsNullOrEmpty(finalMergedFbx) && File.Exists(finalMergedFbx))
                    {
                        string desiredStem = Path.GetFileNameWithoutExtension(outFbxPath);
                        string desiredPath = Path.Combine(siblingOutDir, desiredStem + "_LOD0.fbx");
                        if (!string.Equals(finalMergedFbx, desiredPath, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                if (File.Exists(desiredPath)) File.Delete(desiredPath);
                                File.Move(finalMergedFbx, desiredPath);
                                log.WriteLine($"  renamed -> {Path.GetFileName(desiredPath)}");
                            }
                            catch (Exception ex)
                            {
                                log.WriteLine($"  rename failed ({ex.Message}); final FBX remains at {finalMergedFbx}");
                            }
                        }
                    }
                }

                log.WriteLine($"[{DateTime.Now:HH:mm:ss}] Export complete.");

                string outDir = Path.GetDirectoryName(outFbxPath) ?? "";
                if (Directory.Exists(outDir))
                {
                    log.WriteLine("Output dir contents:");
                    foreach (var f in Directory.EnumerateFiles(outDir))
                        log.WriteLine($"  {Path.GetFileName(f)}  ({new FileInfo(f).Length} bytes)");
                }
                return 0;
            }
            catch (Exception ex)
            {
                log.WriteLine($"[{DateTime.Now:HH:mm:ss}] EXCEPTION");
                log.WriteLine(ex.ToString());
                return 1;
            }
        }
    }
}
