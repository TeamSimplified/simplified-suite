using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;

namespace SimplifiedUniversalExporter
{
    /// <summary>
    /// Loader for Codemasters EGO engine F1-2017..2021 vehicle packages
    /// (NEFX2 shader family, 20-stream vertex layout, ERPK container).
    ///
    /// Parses the *decompressed* IDF mesh records + vertex/index buffer
    /// payloads that have been extracted to a folder as
    /// "{team}!!!temp000*" (descriptor) and "{team}!!!temp001*" (payload)
    /// pairs. Assembles the geometry into WPF HelixToolkit MeshGeometry3D
    /// objects and returns them as a Model3DGroup.
    ///
    /// Optional layers, applied in this order if configured:
    ///   1. EmbContextReader  -- picks which .material each mesh slot maps to
    ///      (per-driver number livery binding). Set with SetActiveContext().
    ///   2. TextureCache      -- resolves .material -> .tif texture -> WPF
    ///      ImageBrush. Set with SetTextureCache(). Without this, preview
    ///      falls back to slot-coloured DiffuseMaterial.
    ///
    /// Verified against mclaren2017..mclaren2021 ERPs including the 2021
    /// McLaren IDF: 136 scene-graph nodes, 20 VBs, 1 IB of 1,807,092
    /// uint32 indices, 100 driver-number livery contexts.
    ///
    /// F1 25 (v17.1): the IDF uses ONE uint16 index buffer shared by every
    /// LOD, with a per-submesh base vertex. Index width is read from the
    /// buffer header, so both layouts go through the same path. Vertex
    /// stream roles are resolved from the descriptor's own vertex-element
    /// table when it validates (see DescriptorDecl); descriptors that fail
    /// validation keep the original positional slot mapping.
    /// </summary>
    public static class F1ModelLoader
    {
        // =====================================================================
        // LOGGING
        // =====================================================================

        private static string _logPath = "";

        /// <summary>
        /// Optional UI progress sink shared by all three loaders and the
        /// glTF exporter. Receives every log line; MainWindow uses it to
        /// surface status text and keep the dispatcher pumped during
        /// long UI-thread loads. Invoked from whatever thread the caller
        /// runs on -- the sink is responsible for marshalling.
        /// </summary>
        public static Action<string> UiProgress;

        private static void Log(string message)
        {
            UiProgress?.Invoke(message);
            if (string.IsNullOrEmpty(_logPath))
                return;
            try
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
            }
            catch
            {
                // Ignore logging errors
            }
        }

        private static void ResetLog(string logFile)
        {
            _logPath = logFile ?? "";
            if (string.IsNullOrEmpty(_logPath))
                return;
            try
            {
                File.WriteAllText(_logPath, "F1ModelLoader Log\r\n");
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// Decide whether <paramref name="inputPath"/> is classic content
        /// (f1_classic_0[123]_vehicle_package -- handled by
        /// F1ModelLoaderClassic) rather than modern NEFX2 content.
        /// Returns true with a human-readable <paramref name="reason"/>
        /// when any classic marker is found.
        ///
        /// THIS FUNCTION LOGS EVERY STEP. The log noise is intentional --
        /// silent failure here was indistinguishable from "detection
        /// patch not deployed" in earlier debug rounds. With unconditional
        /// logging we can always read exactly which signals were checked
        /// and why each returned false.
        ///
        /// Detection signals (any one is sufficient):
        ///
        ///   1. Input path contains "f1_classic_" -- authoritative when
        ///      the dispatcher passed us the original ERP path with the
        ///      package folder still in it.
        ///
        ///   2. Walking up at most 6 levels from input, ANY directory
        ///      visited (or its immediate subdirectories) contains a
        ///      file matching "*___temp00*" -- the classic underscore
        ///      naming convention. Stock 2018 caches NEVER produce
        ///      "___" in their cache filenames; the marker is unique
        ///      to classic content.
        ///
        /// Stock 2018 hits neither signal: paths contain
        /// "f1_2018_vehicle_package" (no "f1_classic_") and cache files
        /// use "!!!" separator. Stock loads continue to flow through
        /// F1ModelLoader unchanged.
        /// </summary>
        // v12.05: visibility lifted from private -> internal so the
        // legacy loader's classic-content pre-flight dispatch (the
        // LoadFolder pre-check that forwards Benetton 94 / McLaren 98
        // / etc. classic ERPs to F1ModelLoaderClassic instead of
        // letting them fall through to the 2016/2017-era LegacyMesh
        // placeholder path) can share the exact same detection logic
        // used here. Two callers, identical predicate; if this method
        // is updated, both call sites pick up the change automatically.
        internal static bool LooksLikeClassicContent(string inputPath, out string reason)
        {
            reason = null;

            Log($"  Classic detection: inputPath = '{inputPath ?? "(null)"}'");

            if (string.IsNullOrEmpty(inputPath))
            {
                Log("  Classic detection: inputPath is null/empty -> not classic.");
                return false;
            }

            // v12.17: EARLY EXIT for F1 2021-2025 modern stock content.
            //
            // The signal checks below are designed to recognise F1 2018-
            // era classic ERPs (and the F1 2019/2020 classic-expansion
            // content) that share the 9-int / 88-byte mesh-record format
            // and must be parsed by F1ModelLoaderClassic. Signal 3/4
            // walks up to 6 ancestor levels from the input folder and
            // byte-sniffs `*temp000*mesh*` files for the ASCII substring
            // "f1_classic_". For modern stock loads this produces false
            // positives when the tool's working temp root (e.g.
            // `…\simplified-universal-exporter`) contains sibling `erp_extract` /
            // `erp_cache` folders with unrelated classic-car descriptors
            // from earlier sessions -- those ARE classic, but they
            // belong to a DIFFERENT car, not the one being loaded right
            // now. Observed in the field: a 2025 McLaren load got
            // bounced to F1ModelLoaderClassic because hop 5 reached
            // `…\simplified-universal-exporter` and the descendant sniff found a
            // `c_mclaren_91_*` extract folder from a previous session.
            //
            // F1PackageLocator.Last is populated by MainWindow.LoadIdf_Click
            // BEFORE either dispatcher runs, and reports the picked
            // ERP's actual on-disk game install (e.g. Kind=F1_2025 for
            // an ERP under `…\F1 25\…`). When the resolved game year is
            // 2021-2025 the picked ERP is modern stock content BY
            // CONSTRUCTION, regardless of what neighbouring extract
            // folders happen to contain on disk. F1ModelLoader (this
            // file, NEFX2) is the correct parser for those years; we
            // must NOT bounce to F1ModelLoaderClassic or fall through
            // to F1ModelLoaderLegacy. Return false unconditionally so
            // the load stays on the F1ModelLoader path.
            //
            // The existing signal checks below still run for older
            // years (F1 2018-2020 classic-content bounce, F1 2017
            // legacy fall-through) and for loads where the locator
            // wasn't populated (loose ERPs picked from outside any
            // recognised install). This block is purely additive --
            // it short-circuits ONLY the modern-stock case, leaving
            // every other code path unchanged.
            try
            {
                var located = F1PackageLocator.Last;
                if (located != null)
                {
                    int year = F1PackageLocator.GetYearForKind(located.Kind);
                    if (year >= 2021 && year <= 2025)
                    {
                        Log($"  Classic detection: F1PackageLocator.Last " +
                            $"reports modern stock year ({year}) via " +
                            $"Kind={located.Kind}; skipping classic-content " +
                            $"sniff -- F1 2021-2025 loads always route " +
                            $"through F1ModelLoader (NEFX2).");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"  Classic detection: F1PackageLocator year-probe " +
                    $"threw ({ex.Message}); falling through to existing " +
                    $"signal checks.");
            }

            // Signal 1: explicit "f1_classic_" in the path itself.
            if (inputPath.IndexOf("f1_classic_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "input path contains 'f1_classic_'";
                Log($"  Classic detection: SIGNAL 1 HIT -- {reason}");
                return true;
            }
            Log("  Classic detection: signal 1 (path contains 'f1_classic_') = false.");

            // Resolve to an existing folder we can scan.
            string folder;
            try
            {
                if (File.Exists(inputPath))
                    folder = Path.GetDirectoryName(inputPath);
                else if (Directory.Exists(inputPath))
                    folder = inputPath;
                else
                    folder = Path.GetDirectoryName(inputPath);
                if (string.IsNullOrEmpty(folder))
                {
                    Log("  Classic detection: could not resolve a folder from inputPath -> not classic.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"  Classic detection: folder-resolve threw ({ex.Message}) -> not classic.");
                return false;
            }

            // Signal 2: walk up looking for any "*___temp00*" file
            // anywhere reachable -- in this folder, in any sibling, or
            // in any subdirectory of any ancestor up to 6 levels.
            //
            // We do this in two cheap passes per ancestor:
            //   * direct children files in the ancestor itself
            //   * one-level-down children of every subdirectory of the
            //     ancestor (so e.g. <cache>\teams\c_mclaren_98\idf is
            //     reachable from <cache> in one extra hop)
            //
            // First match wins -- we don't need an exhaustive scan.
            string cur;
            try { cur = Path.GetFullPath(folder); }
            catch (Exception ex)
            {
                Log($"  Classic detection: GetFullPath threw ({ex.Message}) -> not classic.");
                return false;
            }

            for (int hop = 0; hop < 6 && !string.IsNullOrEmpty(cur); hop++)
            {
                Log($"  Classic detection: hop {hop} scanning '{cur}'");

                // (a) direct match in this folder for "*___temp00*" files
                if (FolderHasClassicMarker(cur, recurseOneLevel: false))
                {
                    reason = $"'*___temp00*' file found in '{cur}'";
                    Log($"  Classic detection: SIGNAL 2 HIT -- {reason}");
                    return true;
                }

                // (b) one-level-down: enumerate subdirs of `cur`, look
                //     in their direct children. This catches the common
                //     layout where `cur` is the cache root and we want
                //     to find <cur>\teams\<X>\idf\<files>.
                string foundDescendant = TryFindClassicMarkerOneLevelDown(cur);
                if (foundDescendant != null)
                {
                    reason = $"'*___temp00*' file found under '{foundDescendant}'";
                    Log($"  Classic detection: SIGNAL 2 HIT -- {reason}");
                    return true;
                }

                // (c) Signal 3: descriptor-byte sniff. The classic
                //     extractor in current use produces "!!!"-style
                //     filenames (same as stock 2018), so signals 1 and
                //     2 above can both miss. But the descriptor BINARY
                //     always contains the EAID strings of the package
                //     it came from, including ASCII
                //     "f1_classic_0[123]_vehicle_package". Pick any
                //     "*temp000*mesh*" file in `cur` and grep its
                //     bytes. One match decides.
                string sniffedPath = TrySniffClassicMarkerInDescriptors(cur);
                if (sniffedPath != null)
                {
                    reason = $"descriptor bytes contain 'f1_classic_' (file: '{sniffedPath}')";
                    Log($"  Classic detection: SIGNAL 3 HIT -- {reason}");
                    return true;
                }

                // (d) one-level-down sniff: same as (c) but in subdirs.
                //     Catches the case where the input folder itself
                //     has no descriptors but a sibling/descendant folder
                //     does (e.g. input is <cache>\shared and we want to
                //     find descriptors in <cache>\shared\tyres\idf).
                string sniffedDescendant = TrySniffClassicMarkerOneLevelDown(cur);
                if (sniffedDescendant != null)
                {
                    reason = $"descriptor bytes contain 'f1_classic_' (file: '{sniffedDescendant}')";
                    Log($"  Classic detection: SIGNAL 3 HIT -- {reason}");
                    return true;
                }

                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) ||
                    string.Equals(parent, cur, StringComparison.Ordinal))
                {
                    Log($"  Classic detection: hop {hop} reached filesystem root, stopping.");
                    break;
                }
                cur = parent;
            }

            Log("  Classic detection: no signal triggered -- treating as modern content.");
            return false;
        }

        /// <summary>
        /// Look for ASCII "f1_classic_" inside any "*temp000*mesh*"
        /// file's byte contents in <paramref name="folder"/> (top level
        /// only). Returns the matching descriptor's full path, or null.
        ///
        /// Reads at most the first 256 KB of each candidate; the
        /// package-name EAID always sits in the descriptor's name
        /// table near the start, well within that window.
        /// </summary>
        private static string TrySniffClassicMarkerInDescriptors(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;
                foreach (var path in Directory.EnumerateFiles(folder, "*temp000*mesh*",
                                                              SearchOption.TopDirectoryOnly))
                {
                    if (DescriptorBytesContainClassicMarker(path))
                        return path;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Same idea as <see cref="TrySniffClassicMarkerInDescriptors"/>
        /// but scans one and two levels of subdirectories. Caps work
        /// to keep the cold-cache cost bounded.
        /// </summary>
        private static string TrySniffClassicMarkerOneLevelDown(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return null;
                foreach (var sub in Directory.EnumerateDirectories(root))
                {
                    string hit = TrySniffClassicMarkerInDescriptors(sub);
                    if (hit != null) return hit;
                    try
                    {
                        foreach (var subsub in Directory.EnumerateDirectories(sub))
                        {
                            string hit2 = TrySniffClassicMarkerInDescriptors(subsub);
                            if (hit2 != null) return hit2;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// True iff the file at <paramref name="path"/> contains the
        /// ASCII byte sequence "f1_classic_" within its first 256 KB.
        /// Works regardless of whether the descriptor itself is
        /// compressed inside the cache (the EAID strings appear
        /// uncompressed near the start in the cached files we've seen).
        /// </summary>
        private static bool DescriptorBytesContainClassicMarker(string path)
        {
            const int MAX_READ = 256 * 1024;
            byte[] needle = System.Text.Encoding.ASCII.GetBytes("f1_classic_");
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    int len = (int)Math.Min(MAX_READ, fs.Length);
                    byte[] buf = new byte[len];
                    int read = fs.Read(buf, 0, len);
                    if (read < needle.Length) return false;
                    int limit = read - needle.Length;
                    for (int i = 0; i <= limit; i++)
                    {
                        bool match = true;
                        for (int j = 0; j < needle.Length; j++)
                        {
                            if (buf[i + j] != needle[j]) { match = false; break; }
                        }
                        if (match) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// True iff <paramref name="folder"/> directly contains any file
        /// matching the classic underscore-convention pattern. When
        /// <paramref name="recurseOneLevel"/> is true, also enumerates
        /// immediate subdirectories' direct children.
        /// </summary>
        private static bool FolderHasClassicMarker(string folder, bool recurseOneLevel)
        {
            try
            {
                if (!Directory.Exists(folder)) return false;
                using (var en = Directory.EnumerateFiles(folder, "*___temp00*",
                                                         SearchOption.TopDirectoryOnly).GetEnumerator())
                {
                    if (en.MoveNext()) return true;
                }
                if (recurseOneLevel)
                {
                    foreach (var sub in Directory.EnumerateDirectories(folder))
                    {
                        if (FolderHasClassicMarker(sub, recurseOneLevel: false))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Look for "*___temp00*" files in any level-1 or level-2
        /// subdirectory of <paramref name="root"/>. Returns the first
        /// matching subdirectory's path, or null if none. Cheap because
        /// we early-out as soon as we find one.
        /// </summary>
        private static string TryFindClassicMarkerOneLevelDown(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return null;
                foreach (var sub in Directory.EnumerateDirectories(root))
                {
                    // direct children of sub
                    try
                    {
                        using (var en = Directory.EnumerateFiles(sub, "*___temp00*",
                                                                 SearchOption.TopDirectoryOnly).GetEnumerator())
                        {
                            if (en.MoveNext()) return sub;
                        }
                    }
                    catch { }
                    // grandchildren of sub (one more level down -- catches
                    // <cache>\teams\<team>\idf when root is <cache>)
                    try
                    {
                        foreach (var subsub in Directory.EnumerateDirectories(sub))
                        {
                            using (var en = Directory.EnumerateFiles(subsub, "*___temp00*",
                                                                     SearchOption.TopDirectoryOnly).GetEnumerator())
                            {
                                if (en.MoveNext()) return subsub;
                            }
                            // and one MORE level (e.g. <cache>\teams\<team>\idf\)
                            try
                            {
                                foreach (var subsubsub in Directory.EnumerateDirectories(subsub))
                                {
                                    using (var en2 = Directory.EnumerateFiles(subsubsub, "*___temp00*",
                                                                              SearchOption.TopDirectoryOnly).GetEnumerator())
                                    {
                                        if (en2.MoveNext()) return subsubsub;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // =====================================================================
        // PUBLIC STATE
        // =====================================================================

        public static string LastLoadedFolder { get; private set; }

        /// <summary>
        /// Optional livery binding. When set (via SetActiveContext), submeshes
        /// are colour-tinted by the material EAID resolved for each slot
        /// instead of by the raw slot-name hash. Unchanged when null.
        /// </summary>
        private static EmbContextReader _activeContext;

        // HELMET FIX: F1 25 helmet .material files contain shader parameters
        // only, no TextureEaids. Texture binding follows a livery-naming
        // convention: <liveryContextName>_<slot>_<channel>.tif under the
        // livery_package_shared/replica/<liveryContextName>/textures/ folder.
        // The context NAME (not ID) is captured here from any non-default,
        // non-numeric .emb?context=<name> file in the IDF folder and used as
        // a fallback texture lookup at submesh-binding time.
        private static string _helmetLiveryContextName;

        public static void SetActiveContext(EmbContextReader ctx) => _activeContext = ctx;

        /// <summary>
        /// Livery context id AutoConfigure should prefer on the next load
        /// (driver-number selection from the UI dropdown). -1 = first
        /// available context (legacy behaviour).
        /// </summary>
        public static int PreferredContextId { get; set; } = -1;

        /// <summary>
        /// Context id the loader ACTUALLY bound on the last load (-1 =
        /// none). The driver-number dropdown syncs to this after a load
        /// so the UI never shows a number the car isn't wearing.
        /// </summary>
        public static int ActiveContextId => _activeContext != null ? _activeContext.ContextId : -1;

        /// <summary>
        /// When false, the shared tyre pipeline is skipped on every load
        /// (all three loaders honour this). Tyres are preview dressing --
        /// their rubber lives in the shared tyres.erp, not the car ERP --
        /// so exports meant for round-trip import should load without
        /// them to keep wheel_* parts free of tyre wrapper geometry.
        /// </summary>
        public static bool LoadTyres { get; set; } = true;

        /// <summary>
        /// Gender filter for race-suit / driver / character ERPs that ship
        /// parallel male+female mesh files. Set to "male", "female", or
        /// null/empty (= load both, original behaviour). See the per-file
        /// drop logic where this is applied.
        /// </summary>
        public static string GenderFilter = null;

        /// <summary>
        /// Invoked before each mesh descriptor loads: (leaf name, index,
        /// total). The shell uses it to update the status bar and pump
        /// the dispatcher, so a multi-minute car load keeps painting
        /// instead of ghosting into "Not Responding" (the load must stay
        /// on the UI thread -- WPF 3D objects are thread-affine).
        /// </summary>
        public static Action<string, int, int> DescriptorPulse;

        private static string[] ApplyGenderFilter(string[] files, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return files;
            string keep = filter.Trim().ToLowerInvariant();
            string drop;
            if (keep == "male") drop = "female";
            else if (keep == "female") drop = "male";
            else return files; // unknown filter -> no-op

            int dropped = 0;
            var kept = new List<string>(files.Length);
            foreach (var f in files)
            {
                string name = Path.GetFileName(f).ToLowerInvariant();
                if (ContainsGenderToken(name, drop) && !ContainsGenderToken(name, keep))
                {
                    dropped++;
                    continue;
                }
                kept.Add(f);
            }
            if (dropped > 0)
                Log($"Gender filter ('{keep}'): dropped {dropped} '{drop}'-variant mesh file(s).");
            return kept.ToArray();
        }

        /// <summary>
        /// True when the filename has "&lt;token&gt;" appearing as a whole word --
        /// preceded by '_' / '-' / start, and followed by '_' / '-' / '.' /
        /// '&amp;' / end. Prevents substring false-positives like "female"
        /// matching when we look for "male".
        /// </summary>
        internal static bool ContainsGenderToken(string lowercaseName, string token)
        {
            int idx = 0;
            while (true)
            {
                idx = lowercaseName.IndexOf(token, idx, StringComparison.Ordinal);
                if (idx < 0) return false;
                bool startsOk = idx == 0 ||
                    lowercaseName[idx - 1] == '_' || lowercaseName[idx - 1] == '-' ||
                    lowercaseName[idx - 1] == '/' || lowercaseName[idx - 1] == '\\';
                int end = idx + token.Length;
                bool endsOk = end == lowercaseName.Length ||
                    lowercaseName[end] == '_' || lowercaseName[end] == '-' ||
                    lowercaseName[end] == '.' || lowercaseName[end] == '&' ||
                    lowercaseName[end] == '!' || lowercaseName[end] == '%';
                if (startsOk && endsOk) return true;
                idx = end;
            }
        }

        /// <summary>
        /// UV-channel override for "number" / "carbon number" submeshes
        /// only (ported from the UniversalExporter, where it is user-
        /// verified across every 2025 team AND the FOM car). Default is
        /// Pair-2 (vb=3 pair-2): the vb=4-first heuristic was wrong on
        /// Ferrari's front number and both FOM number submeshes (it
        /// picked a pixel-strip on the driver atlas instead of the
        /// digit region). Auto re-enables the old heuristic chain;
        /// Primary/Secondary force those channels for any future car
        /// that needs them.
        /// </summary>
        public enum NumberUvSource { Auto, Primary, Pair2, Secondary }
        public static NumberUvSource NumberUvOverride = NumberUvSource.Pair2;
        public static void SetNumberUvOverride(NumberUvSource src) =>
            NumberUvOverride = src;

        public static void ClearActiveContext() => _activeContext = null;

        /// <summary>The modern loader's active TextureCache (null until a
        /// 2021+ load configures one). Exposed for the glTF exporter's
        /// texture embedding, which historically only consulted the
        /// classic/legacy caches -- so 2021+ exports shipped untextured.</summary>
        public static TextureCache ActiveTextureCache => _textureCache;

        /// <summary>
        /// Optional texture cache. When set, each submesh gets a DiffuseMaterial
        /// whose Brush is the decoded BC1/BC3 texture for the submesh's material.
        /// When null, falls back to solid-coloured DiffuseMaterial.
        /// </summary>
        private static TextureCache _textureCache;

        // FIX 20f: cached overlay resolution from the last body submesh,
        // reused for detail submeshes that carry decal UVs but whose
        // material (vehicle_generic) doesn't have me_logoMap.
        private static TextureCache.PreviewResolution _lastOverlayResolution;

        // DIAGNOSTIC (kept in place for future debugging): when true, number-type
        // overlay submeshes render with a solid opaque red brush instead of the
        // driver atlas. Lets us tell, at a glance, whether the overlay mesh is
        // being rasterised at all. See the DIAGNOSTIC MODE block in
        // TryBuildSponsorOverlay. Leave false during normal operation.
        //
        // Confirmed rasterising cleanly on F1 22 McLaren (red patches visible
        // in the overlay positions). Diagnosis result: overlay MESH is fine,
        // the invisibility with the real driver atlas was a WPF DiffuseMaterial
        // alpha-blending quirk on BGRA buffers with >90% transparent pixels.
        // FIX 34 in EgoTexturePipeline.cs turns on the opaque-bake path so
        // the atlas ships every pixel at alpha=255 -- the issue no longer
        // applies. Flag reset to false for normal rendering.
        //
        // F1 25 UPDATE: the same symptom is now back on SPONSOR overlays for
        // the SF-25 -- every load-time signal is green (74 overlay submeshes,
        // atlas regions landing on real content) and nothing shows on the car.
        // The flag is therefore widened from numbers to ALL overlays and made
        // public so it can be flipped without editing this file. Sponsor
        // overlays reach the brush through HighResBrushProvider.WrapBrush with
        // the 4096x4096 sidecar, which is a different path from the one FIX 34
        // bakes opaque -- so the >90%-transparent-BGRA quirk that was fixed for
        // numbers may simply never have been fixed for this path.
        //
        // Set true for one load:
        //   red patches where sponsors belong -> mesh rasterises; the brush is
        //     the problem (alpha handling / opaque bake missing on the sidecar
        //     path).
        //   no red at all -> the overlay meshes are not reaching the renderer;
        //     the problem is scene-graph / depth / material setup, downstream
        //     of everything in this file.
        public static bool DebugSolidRedOverlays = false;

        public static void SetTextureCache(TextureCache cache)
        {
            _textureCache = cache;
            // v17.0: the game-material catalog decodes auxiliary maps
            // (normals etc.) for the DX showroom through the same cache.
            GameMaterialCatalog.TextureResolver = cache != null
                ? (Func<string, ImageBrush>)cache.GetBrushForTexture
                : null;
        }
        public static void ClearTextureCache()
        {
            _textureCache = null;
            GameMaterialCatalog.TextureResolver = null;   // v17.0
        }

        // =====================================================================
        // DATA STRUCTURES
        // =====================================================================

        private class VertexBufferInfo
        {
            public string Name;
            public string Suffix;
            public string DataPath;
            public uint Type;
            public int Stride;
            public int Count;
            public uint Fvf;

            // v17.4: vertex format declared by the descriptor's element
            // table (35 = f16x4, 29 = f16x2, 47 = f32x3, ...), 0 when the
            // descriptor didn't declare one. The buffer header's "Fvf" is a
            // usage field that only coincides with the format on some
            // titles/teams, so decoders prefer this when it is set.
            public int DeclFormat;
        }

        private class IndexBufferInfo
        {
            public string Name;
            public string Suffix;
            public string DataPath;
            public uint Type;
            public int IndexSize;
            public int Count;
            public uint Fvf;
        }

        private class SubMeshInfo
        {
            public string Name;
            public int IOffset;
            public int IndexCount;
            public int VOffset;
            public int VBufferIndex;
            public int NrmBufferIndex;
            public int UvBufferIndex;        // pair-1 stream (paint/carbon base UV)
            public int UvBufferIndex2 = -1;  // pair-2 stream (decal/logo UV, fmt 29)

            // FIX 33: additional UV-bearing stream pointed at by slots[1] in
            // the submesh block. Previously IGNORED by the loader (only
            // slots[0]/[2]/[3] were consumed). Observed on F1 22 mclaren
            // to be an Fvf=35 stride=8 UV-pair stream that authors a subset
            // of submeshes' decal UVs (notably safetycell.5 carbon_number,
            // sidepod_fl.3 carbon_number, steering_wheel.5 number on the
            // F1 22 McLaren). The CountDigitCoverage picker adds this stream
            // as two extra candidates (pair-1 and pair-2). Default -1 so
            // code paths that don't care about it are unchanged.
            public int UvBufferIndex3 = -1;

            public int IBufferIndex;
            public string DescriptorHint;
            public string MaterialSlot;
            public int SubmeshIndex;

            // v17.1: submesh-block bytes +0x18..+0x27 are the submesh
            // BOUNDING SPHERE (centre xyz, radius) -- not a UV scale/bias.
            // Verified on the F1 25 Ferrari: the wheel submesh holds
            // (0.004, -0.004, 0.001, r=0.339). FIX 19 had labelled these
            // globalSlotUVScaleAndBias; nothing ever consumed them, and
            // applying them to decal UVs would corrupt every decal.
            public float SphereCenterX;
            public float SphereCenterY;
            public float SphereCenterZ;
            public float SphereRadius;

            // v17.1: stream roles resolved from the descriptor's vertex-
            // element table (true) or from the legacy positional slots.
            public bool DeclDriven;

            // v17.3: keep the original 0..maxIndex vertex arrays (no v17.1
            // tight-window rebase). Set for the shared tyres descriptor.
            public bool LegacyVertexWindow;

            // v17.1: the primary UV stream is a 4-component FP16 element
            // (UV0 at bytes 0/2, UV1 at bytes 4/6) and there is no separate
            // fmt-29 secondary stream. Decal-class samplers read bytes 4/6.
            public bool UvPair2InPrimary;

            // v17.1: informational -- streams that are NOT UVs and must
            // never be fed to UV decoders (tangent frame, part-slot info).
            public int TangentBufferIndex = -1;
            public int PartInfoBufferIndex = -1;
        }

        // =====================================================================
        // LOAD STATISTICS
        // =====================================================================
        //
        // Collects every submesh's slot/material/texture/triangle count
        // during the build pass and emits three roll-up tables at the end
        // of the log so the user can see at a glance:
        //   1. Each part name, how many submeshes it has, and what slots
        //      they cover.
        //   2. Each unique texture, how many submeshes use it, total tri
        //      count rendered with it, and one example slot name.
        //   3. Each missing-on-disk material EAID, with the count of slots
        //      that wanted it (so the user knows which extra ERPs to extract).
        //
        // The static instance is shared across LoadFolder calls and is
        // Reset() at the top of every load so stats reflect the most
        // recent build only.

        private sealed class SubmeshRecord
        {
            public string PartName;
            public string Slot;
            public string MaterialEaid;     // resolved EAID, or null
            public string TextureFile;      // short filename, or null if untextured
            public int TriangleCount;
            public string DescriptorFile;   // the mesh-descriptor filename this submesh came from
        }

        private sealed class LoadStats
        {
            private readonly List<SubmeshRecord> _records = new List<SubmeshRecord>(256);
            private int _sponsorOverlayCount;

            public void Reset()
            {
                _records.Clear();
                _sponsorOverlayCount = 0;
            }

            public void RecordSponsorOverlay() => _sponsorOverlayCount++;

            public void RecordSubmesh(string partName, string slot, string materialEaid,
                                      string textureEaid, int triangleCount,
                                      string descriptorFile)
            {
                _records.Add(new SubmeshRecord
                {
                    PartName = partName ?? "?",
                    Slot = slot ?? "?",
                    MaterialEaid = materialEaid,
                    TextureFile = textureEaid,
                    TriangleCount = triangleCount,
                    DescriptorFile = descriptorFile,
                });
            }

            public void WriteSummary(Action<string> log)
            {
                if (_records.Count == 0) return;

                log("");
                log("==================================================");
                log("LOAD SUMMARY");
                log("==================================================");

                // ---- 1. Parts ----
                var byPart = _records
                    .GroupBy(r => r.PartName)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                log($"OBJECTS LOADED: {byPart.Count} part(s) / {_records.Count} submesh(es) / " +
                    $"{_records.Sum(r => r.TriangleCount):N0} triangles" +
                    (_sponsorOverlayCount > 0 ? $" + {_sponsorOverlayCount} sponsor overlay submesh(es)" : ""));
                log("");
                log($"  {"PART",-32} {"SUBMESHES",-9} {"TRIS",-8}  SLOTS");
                log($"  {new string('-', 32)} {new string('-', 9)} {new string('-', 8)}  {new string('-', 40)}");

                foreach (var grp in byPart)
                {
                    int n = grp.Count();
                    int tris = grp.Sum(r => r.TriangleCount);
                    string slots = string.Join(", ",
                        grp.Select(r => r.Slot).Distinct(StringComparer.OrdinalIgnoreCase));
                    log($"  {grp.Key,-32} {n,9} {tris,8:N0}  {slots}");
                }

                // ---- Defensive: detect part-name collisions ----
                //
                // If two distinct mesh-descriptor files end up producing the
                // same part name, they'll be lumped into one Model3DGroup
                // and visually overlap. The most common cause is a Code-
                // masters URL pattern we don't yet recognise (e.g. an extra
                // segment that gets stripped, or a typo in the regex).
                var collisions = byPart
                    .Select(g => new
                    {
                        Part = g.Key,
                        Sources = g.Select(r => r.DescriptorFile)
                                   .Where(s => !string.IsNullOrEmpty(s))
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .ToList(),
                    })
                    .Where(x => x.Sources.Count > 1)
                    .ToList();
                if (collisions.Count > 0)
                {
                    log("");
                    log($"WARNING: {collisions.Count} part name(s) fed by MORE THAN ONE mesh file:");
                    foreach (var c in collisions)
                    {
                        log($"  '{c.Part}' came from:");
                        foreach (var src in c.Sources)
                            log($"      {src}");
                    }
                    log("(this means submeshes from different mesh files are " +
                        "rendering on top of each other -- usually a sign of");
                    log(" an unrecognised filename pattern. The part-name " +
                        "regex may need extending.)");
                }

                // ---- 2. Textures actually applied ----
                var byTexture = _records
                    .Where(r => !string.IsNullOrEmpty(r.TextureFile))
                    .GroupBy(r => r.TextureFile, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Sum(r => r.TriangleCount))
                    .ToList();

                log("");
                log($"TEXTURES APPLIED: {byTexture.Count} unique");
                log("");
                log($"  {"TEXTURE",-44} {"SUBMESHES",-9} {"TRIS",-8}  EXAMPLE SLOT");
                log($"  {new string('-', 44)} {new string('-', 9)} {new string('-', 8)}  {new string('-', 24)}");
                foreach (var grp in byTexture)
                {
                    int n = grp.Count();
                    int tris = grp.Sum(r => r.TriangleCount);
                    string exampleSlot = grp.First().Slot;
                    log($"  {grp.Key,-44} {n,9} {tris,8:N0}  {exampleSlot}");
                }

                // ---- 3. Untextured + missing materials ----
                var untextured = _records.Where(r => string.IsNullOrEmpty(r.TextureFile)).ToList();
                if (untextured.Count > 0)
                {
                    log("");
                    log($"NEUTRAL-WHITE FALLBACKS: {untextured.Count} submesh(es)");
                    log("(no real material file on disk -- usually a shared/generics");
                    log(" .material that lives in a separate ERP archive)");
                    log("");
                    log($"  {"SLOT",-22} {"MATERIAL EAID (or '-')",-44} COUNT");
                    log($"  {new string('-', 22)} {new string('-', 44)} -----");

                    foreach (var grp in untextured
                        .GroupBy(r => (Slot: r.Slot, Mat: r.MaterialEaid ?? "-"))
                        .OrderByDescending(g => g.Count()))
                    {
                        log($"  {grp.Key.Slot,-22} {ShortenEaid(grp.Key.Mat),-44} {grp.Count(),5}");
                    }
                }

                log("==================================================");
                log("");
            }
        }

        private static readonly LoadStats _stats = new LoadStats();

        // =====================================================================
        // PUBLIC ENTRY POINTS  (signatures preserved verbatim)
        // =====================================================================

        public static Model3DGroup LoadFolder(string inputPath)
        {
            string folder;

            if (string.IsNullOrWhiteSpace(inputPath))
                folder = Directory.GetCurrentDirectory();
            else if (File.Exists(inputPath))
                folder = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
            else
                folder = inputPath;

            string logFile = Path.Combine(folder, "F1_HybridLoader_Log.txt");
            return LoadFolder(inputPath, logFile);
        }

        public static Model3DGroup LoadFolder(string inputPath, string logFile)
        {
            ResetLog(logFile);
            Log("==================================================");
            Log("F1ModelLoader.LoadFolder (NEFX2 / F1 2017-2025, data-driven descriptor layout)");
            Log($"Input: {inputPath}");

            // -------------------------------------------------------------
            // CLASSIC CONTENT BOUNCE (v2 — verbose-logging detection)
            // -------------------------------------------------------------
            //
            // F1ModelLoader handles the modern NEFX2 / F1 2017-2021 family.
            // F1 2018's classic content (f1_classic_0[123]_vehicle_package
            // ERPs -- c_mclaren_98, c_brawn_09, c_williams_92, etc.) uses
            // the same 9-int / 88-byte mesh-record format as 2018 stock
            // and is loaded by F1ModelLoaderClassic. The dispatcher
            // upstream of these two loaders currently routes ALL F1 2018
            // ERPs (stock and classic) to F1ModelLoader, which has no
            // notion of classic content and silently produces 0 meshes
            // for it.
            //
            // We catch the misroute here: if the input path or the cache
            // around it shows classic markers, hand off to
            // F1ModelLoaderClassic.LoadFolder and return its result.
            //
            // The version tag below makes it obvious from a log read
            // whether the deployed build contains this dispatcher; if
            // you don't see "[CLASSIC-DISPATCH v2]" in the F1 hybrid
            // log, the binary still has the old F1ModelLoader.cs.
            Log("[CLASSIC-DISPATCH v2] Pre-flight check: deciding whether to forward to F1ModelLoaderClassic.");
            try
            {
                if (LooksLikeClassicContent(inputPath, out string classicReason))
                {
                    Log($"[CLASSIC-DISPATCH v2] CLASSIC CONTENT DETECTED ({classicReason}) -- " +
                        "forwarding to F1ModelLoaderClassic.LoadFolder. The classic " +
                        "loader writes its own log to F1_ClassicLoader_Log.txt in " +
                        "the resolved idf folder.");
                    return F1ModelLoaderClassic.LoadFolder(inputPath);
                }
                Log("[CLASSIC-DISPATCH v2] Not classic content; continuing with F1ModelLoader path.");
            }
            catch (Exception ex)
            {
                Log($"[CLASSIC-DISPATCH v2] Detection threw ({ex.Message}); " +
                    "continuing with F1ModelLoader path.");
            }
            // -------------------------------------------------------------

            // Auto-configure per-era compatibility flags from the game
            // install root detected by F1PackageLocator. The locator is
            // populated by MainWindow when the user picks the ERP.
            //
            // Detection signal: the GAME ROOT FOLDER NAME, not the package
            // folder name. Package folders are ambiguous -- both F1 2021
            // and F1 22 installs ship multiple "fN_20XX_vehicle_package"
            // folders (e.g. F1 2021 has both f1_2020_vehicle_package and
            // f1_2021_vehicle_package), and which one a given ERP lives in
            // depends on where Codemasters stored that specific car. The
            // GAME root folder name is unambiguous about which TITLE the
            // assets are being read from. Steam / Epic / standalone
            // installs all use these names for the game directory.
            //
            // Supported game-root folder-name patterns (case-insensitive):
            //
            //   "F1 2020"          -> year 2020
            //   "F1 2021"          -> year 2021   (enables decal compat)
            //   "F1 22"            -> year 2022
            //   "F1 23"            -> year 2023   (sets ApplyF1_2023_Compat)
            //   "F1 24"            -> year 2024   (sets ApplyF1_2024_Compat)
            //   "F1 25"            -> year 2025   (sets ApplyF1_2025_Compat)
            //
            // Codemasters dropped the century digits from F1 22 onward, so
            // the short form ("F1 22", "F1 23", ...) is what actually ships
            // on disk. The long form ("F1 2022") is accepted too for
            // forward-compat with user-renamed installs.
            //
            // FALLBACK: if no game-root name is available (e.g. ERP picked
            // from a loose folder that isn't under a real install), we
            // infer the year from the package folder name reported by the
            // locator (f1_2023_vehicle_package -> 2023 etc.). The package
            // name is less reliable than the game root (a single install
            // ships multiple package folders for multiple seasons) but is
            // better than leaving DetectedGameYear at zero.
            //
            // All per-era compat flags are RESET to their defaults on
            // every successful detect, so that re-picking an ERP across
            // different titles in a single session doesn't carry stale
            // flags from the previous pick. Callers that want to OVERRIDE
            // a flag must set it AFTER calling LoadFolder, not before --
            // the pre-call value is intentionally ignored in favour of
            // the detected-year default, because the alternative (sticky
            // overrides) caused the "one ERP decoded right, the next one
            // decoded wrong with no visible cause" class of bug.
            int detectedYear = 0;
            string detectionSource = "none";
            try
            {
                var located = F1PackageLocator.Last;

                // Step 1: try the game-root folder name.
                if (located != null && !string.IsNullOrEmpty(located.GameRoot))
                {
                    string rootName = Path.GetFileName(
                        located.GameRoot.TrimEnd(Path.DirectorySeparatorChar,
                                                 Path.AltDirectorySeparatorChar)) ?? "";

                    detectedYear = TryParseGameRootYear(rootName);
                    if (detectedYear != 0)
                        detectionSource = "gameRoot=\"" + rootName + "\"";
                }

                // Step 2: fall back to the PackageKind the locator matched.
                if (detectedYear == 0 && located != null)
                {
                    int fromKind = F1PackageLocator.GetYearForKind(located.Kind);
                    if (fromKind != 0)
                    {
                        detectedYear = fromKind;
                        detectionSource = "packageKind=" + located.Kind;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"AutoConfigure: detection threw ({ex.Message}); " +
                    "leaving per-era flags at their prior values.");
                detectedYear = 0;
            }

            // v17.4 Step 3: the locator had nothing (ERP opened from a loose
            // folder, renamed install, ...). Read the season straight from
            // the data: every descriptor names its buffers with the EAID of
            // the package it was built in ("eaid://f1_2025_vehicle_package/...").
            if (detectedYear == 0)
            {
                try
                {
                    string probeFolder = File.Exists(inputPath)
                        ? Path.GetDirectoryName(inputPath)
                        : inputPath;
                    int fromData = TryDetectYearFromDescriptors(probeFolder, out string pkg);
                    if (fromData != 0)
                    {
                        detectedYear = fromData;
                        detectionSource = "descriptor EAIDs (" + pkg + ")";
                    }
                }
                catch { /* data probe is best-effort */ }
            }

            // Apply the detected year to the per-era flags. Only runs when
            // detection actually succeeded; otherwise leave prior values.
            if (detectedYear != 0)
            {
                DetectedGameYear = detectedYear;
                ApplyF1_2021_DecalCompatibility = (detectedYear == 2021);
                ApplyF1_2023_Compat = (detectedYear == 2023);
                ApplyF1_2024_Compat = (detectedYear == 2024);
                ApplyF1_2025_Compat = (detectedYear == 2025);
                Log($"AutoConfigure: detected game year = {detectedYear} " +
                    $"(source: {detectionSource})");
            }
            else
            {
                Log("AutoConfigure: no game year detected -- F1PackageLocator.Last " +
                    "is null or its GameRoot/Kind didn't match any known pattern. " +
                    "Leaving per-era flags at their current values: " +
                    $"DetectedGameYear={DetectedGameYear}, " +
                    $"Apply2021={ApplyF1_2021_DecalCompatibility}, " +
                    $"Apply2023={ApplyF1_2023_Compat}, " +
                    $"Apply2024={ApplyF1_2024_Compat}, " +
                    $"Apply2025={ApplyF1_2025_Compat}.");
            }

            Log($"Livery context: {(_activeContext != null ? "yes" : "no")}");
            Log($"Texture cache:  {(_textureCache != null ? _textureCache.PackageRoot : "no (solid colour preview)")}");
            Log($"FlipTextureV:   {FlipTextureV}  (toggle via F1ModelLoader.FlipTextureV before LoadFolder)");
            Log($"MaxUvTileRate:  {MaxUvTileRate}  (UV tile cap; raise to disable, or set FlipTextureV)");
            Log($"DetectedGameYear: {DetectedGameYear}");
            Log($"ApplyF1_2021_DecalCompatibility: {ApplyF1_2021_DecalCompatibility}");
            Log($"ApplyF1_2023_Compat:             {ApplyF1_2023_Compat}");
            Log($"ApplyF1_2024_Compat:             {ApplyF1_2024_Compat}");
            Log($"ApplyF1_2025_Compat:             {ApplyF1_2025_Compat}");
            return LoadFolderInternal(inputPath, explicitLod: null);
        }

        /// <summary>
        /// Parse a game-install root folder name ("F1 2021", "F1 22",
        /// "F1 23", "F1 2024 copy", ...) into a four-digit year. Returns 0
        /// when no recognised year token is found.
        ///
        /// Handles both forms Codemasters has shipped:
        ///   - Long form:  "F1 2020", "F1 2021"  (F1 2016-2021 era)
        ///   - Short form: "F1 22", "F1 23", "F1 24", "F1 25"  (F1 22+)
        ///
        /// Short-form matches are anchored on "F1 &lt;NN&gt;" to avoid
        /// colliding with version numbers or build IDs that might happen
        /// to contain two-digit integers. Long-form matches accept any
        /// 20YY substring in the supported range.
        ///
        /// Matching is case-insensitive and is tolerant of extra suffixes
        /// the user might have appended (e.g. "F1 23 - backup").
        /// </summary>
        internal static int TryParseGameRootYear(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
                return 0;

            string s = folderName;

            // Long form first: "F1 20YY" -- unambiguous, so if it's there
            // we trust it. Only accept the years we actually care about.
            for (int y = 2016; y <= 2025; y++)
            {
                string token = "F1 " + y.ToString(CultureInfo.InvariantCulture);
                if (s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return y;
            }

            // Short form: "F1 NN" where NN is 22..25 (2-digit year).
            // The token must be followed by a non-digit or end-of-string,
            // otherwise "F1 2021" would match as "F1 20" + "21" garbage.
            // We scan manually because Regex would pull in an extra
            // dependency for a one-line pattern.
            for (int y = 2022; y <= 2025; y++)
            {
                string yy = (y % 100).ToString("D2", CultureInfo.InvariantCulture);
                string token = "F1 " + yy;
                int idx = 0;
                while (true)
                {
                    idx = s.IndexOf(token, idx, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;

                    int endIdx = idx + token.Length;
                    bool okBoundary = endIdx >= s.Length || !char.IsDigit(s[endIdx]);
                    if (okBoundary)
                        return y;

                    idx = endIdx;
                }
            }

            return 0;
        }

        public static Model3DGroup LoadFolderExplicitLod(string inputPath, int lod, string logFile)
        {
            ResetLog(logFile);
            Log("==================================================");
            Log("F1ModelLoader.LoadFolderExplicitLod (NEFX2 / F1 2017-2021)");
            Log($"Input: {inputPath}");
            Log($"Requested LOD: {lod}");

            // Same classic-content bounce as LoadFolder. The exporter
            // iterates LODs by calling this method per N; we have to
            // forward each call to F1ModelLoaderClassic.LoadFolderExplicitLod
            // so classic ERPs get per-LOD glTF output too.
            Log("[CLASSIC-DISPATCH v2] Pre-flight check (ExplicitLod path).");
            try
            {
                if (LooksLikeClassicContent(inputPath, out string classicReason))
                {
                    Log($"[CLASSIC-DISPATCH v2] CLASSIC CONTENT DETECTED ({classicReason}) -- forwarding " +
                        $"to F1ModelLoaderClassic.LoadFolderExplicitLod(lod={lod}).");
                    return F1ModelLoaderClassic.LoadFolderExplicitLod(inputPath, lod, logFile);
                }
                Log("[CLASSIC-DISPATCH v2] Not classic content; continuing with F1ModelLoader path.");
            }
            catch (Exception ex)
            {
                Log($"[CLASSIC-DISPATCH v2] Detection threw ({ex.Message}); " +
                    "continuing with F1ModelLoader path.");
            }

            return LoadFolderInternal(inputPath, explicitLod: lod);
        }

        /// <summary>
        /// Marks a GeometryModel3D as a decal overlay rather than opaque body
        /// geometry. Read by ShowroomDx when it converts the scene, so both
        /// views classify a submesh the same way off one flag instead of each
        /// guessing from the material.
        /// </summary>
        public static readonly System.Windows.DependencyProperty IsOverlayProperty =
            System.Windows.DependencyProperty.RegisterAttached(
                "IsOverlay", typeof(bool), typeof(F1ModelLoader),
                new System.Windows.PropertyMetadata(false));

        public static void SetIsOverlay(System.Windows.DependencyObject o, bool v) =>
            o.SetValue(IsOverlayProperty, v);

        public static bool GetIsOverlay(System.Windows.DependencyObject o) =>
            o != null && (bool)o.GetValue(IsOverlayProperty);

        /// <summary>
        /// v17.6: marks a base (non-overlay) submesh whose material is
        /// see-through -- the vehicle_glass family (windscreens and, on 2026
        /// cars, the lens over each mirror light). Like overlays these must be
        /// drawn after the opaque geometry behind them: MoveOverlaysAfterBase
        /// orders them between the opaque submeshes and the overlays, and
        /// ShowroomDx routes them through its transparent pass.
        /// </summary>
        public static readonly System.Windows.DependencyProperty IsTransparentBaseProperty =
            System.Windows.DependencyProperty.RegisterAttached(
                "IsTransparentBase", typeof(bool), typeof(F1ModelLoader),
                new System.Windows.PropertyMetadata(false));

        public static void SetIsTransparentBase(System.Windows.DependencyObject o, bool v) =>
            o.SetValue(IsTransparentBaseProperty, v);

        public static bool GetIsTransparentBase(System.Windows.DependencyObject o) =>
            o != null && (bool)o.GetValue(IsTransparentBaseProperty);

        /// <summary>
        /// v17.6: glass-family submesh test. The slot is "glass" on every 2025
        /// and 2026 car that has one, bound to shared generics
        /// glass.material (shader vehicle_generic/vehicle_glass.nefx2,
        /// glass_da alpha ~25%).
        /// </summary>
        internal static bool IsGlassSubmesh(string slot, string materialEaid)
        {
            if (SlotIs(slot, "glass")) return true;
            if (string.IsNullOrEmpty(materialEaid)) return false;
            string leaf = materialEaid;
            int q = leaf.IndexOf('?');
            if (q >= 0) leaf = leaf.Substring(0, q);
            leaf = leaf.Substring(leaf.LastIndexOfAny(new[] { '/', '\\' }) + 1);
            return leaf.Equals("glass.material", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Direction the overlay should be displaced in: +1 when the submesh's
        /// normals face outward, -1 when they face inward. Measured per submesh
        /// in TryBuildSponsorOverlay, consumed by ApplyOverlayDepthOffset.
        /// </summary>
        public static readonly System.Windows.DependencyProperty OverlayOffsetSignProperty =
            System.Windows.DependencyProperty.RegisterAttached(
                "OverlayOffsetSign", typeof(double), typeof(F1ModelLoader),
                new System.Windows.PropertyMetadata(1.0));

        public static void SetOverlayOffsetSign(System.Windows.DependencyObject o, double v) =>
            o.SetValue(OverlayOffsetSignProperty, v);

        public static double GetOverlayOffsetSign(System.Windows.DependencyObject o) =>
            o == null ? 1.0 : (double)o.GetValue(OverlayOffsetSignProperty);

        /// <summary>
        /// Lifts every decal overlay off the surface it was copied from, by an
        /// amount derived from the model that was actually loaded.
        ///
        /// Depth-buffer resolution is a property of the scene, not of a panel.
        /// The viewport frames the whole model (ZoomExtents), so the depth range
        /// the buffer has to divide up is set by the model's own extent, and the
        /// clearance an overlay needs to stay on the near side of its panel
        /// scales with that extent. Measuring it here, after the last part is
        /// in, means the same code is right for a full modern chassis, a classic
        /// car authored in different units, or a single wing opened on its own.
        ///
        /// OverlayDepthRatio is dimensionless -- a fraction of the model's
        /// bounding diagonal, so it carries no assumption about units or content
        /// scale. Its value is calibrated against the one configuration known to
        /// work: the driver-number path, which has used a 0.002 lift since
        /// 0.0005 was found to z-fight, on a car whose diagonal measures about
        /// six units. That is 1/3000, and the same ratio reproduces that lift on
        /// that car while scaling to anything else.
        ///
        /// Overlays are built coincident with their base, so this runs exactly
        /// once per load. Vertices whose normal is zero -- the two UV sentinels
        /// FIX 34c appends -- are unaffected, as they should be.
        /// </summary>
        /// <summary>
        /// Slot-name mirror of the FBX exporter's IsDecalLikeMeshName: does
        /// this submesh belong to a decal / overlay layer that's known to be
        /// authored coincident with a base body mesh? The exporter matches
        /// full mesh names ("safetycell_logos"); here we get the raw slot
        /// name from the registry ("logos", "body_logos"), so bare names
        /// count too. Keep the two lists in sync.
        /// </summary>
        internal static bool IsDecalLikeSlotName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string m = name.Trim();
            // Strip a numeric suffix ("logos_2") so the semantic suffix
            // just before it is what gets matched.
            int u = m.LastIndexOf('_');
            if (u >= 0 && u < m.Length - 1)
            {
                bool allDigits = true;
                for (int k = u + 1; k < m.Length; k++)
                    if (m[k] < '0' || m[k] > '9') { allDigits = false; break; }
                if (allDigits) m = m.Substring(0, u);
            }
            return m.EndsWith("_decal", StringComparison.OrdinalIgnoreCase)
                || m.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase)
                || m.EndsWith("_logo", StringComparison.OrdinalIgnoreCase)
                || m.EndsWith("_logos", StringComparison.OrdinalIgnoreCase)
                || m.Equals("decal", StringComparison.OrdinalIgnoreCase)
                || m.Equals("overlay", StringComparison.OrdinalIgnoreCase)
                || m.Equals("logo", StringComparison.OrdinalIgnoreCase)
                || m.Equals("logos", StringComparison.OrdinalIgnoreCase);
        }

        private static int ApplyOverlayDepthOffset(Model3DGroup root)
        {
            if (root == null) return 0;

            Rect3D bounds = root.Bounds;
            if (bounds.IsEmpty) return 0;

            double diagonal = Math.Sqrt(
                bounds.SizeX * bounds.SizeX +
                bounds.SizeY * bounds.SizeY +
                bounds.SizeZ * bounds.SizeZ);
            if (!(diagonal > 0)) return 0;

            double lift = diagonal * OverlayDepthRatio;
            int moved = 0;
            int named = 0;
            // A mesh can only be lifted once, however many models point at
            // it (tyre wrappers reuse geometry across wheels, for example).
            var lifted = new HashSet<MeshGeometry3D>();

            void Walk(Model3D m)
            {
                if (m is Model3DGroup g)
                {
                    if (g.Children != null)
                        foreach (var child in g.Children) Walk(child);
                    return;
                }
                if (!(m is GeometryModel3D gm)) return;
                if (!(gm.Geometry is MeshGeometry3D mesh) || mesh.IsFrozen) return;

                bool isOverlay = GetIsOverlay(gm);
                // PREVIEW Z-FIGHT FIX: decal-named BASE submeshes (a suit's
                // "body_logos", car "*_decal"/"*_logo(s)" slots) are their
                // own geometry authored coincident with the body underneath
                // -- not loader-built overlay passes, so the IsOverlay lift
                // above never touched them and they flickered in the
                // viewport. The FBX exporter has always pushed exactly
                // these meshes out along their normals (IsDecalLikeMeshName
                // + DecalOffsetMeters); give the preview the same
                // treatment, scaled by the model diagonal like the
                // overlay lift.
                bool isNamedDecal = !isOverlay &&
                    IsDecalLikeSlotName(MeshSubmeshRegistry.GetSubmeshName(mesh));
                if (!isOverlay && !isNamedDecal) return;
                if (!lifted.Add(mesh)) return;

                var pos = mesh.Positions;
                var nrm = mesh.Normals;
                if (pos == null || nrm == null || nrm.Count < pos.Count) return;

                double d = lift * (isOverlay ? GetOverlayOffsetSign(gm) : 1.0);
                for (int i = 0; i < pos.Count; i++)
                {
                    var p = pos[i];
                    var n = nrm[i];
                    pos[i] = new Point3D(p.X + n.X * d, p.Y + n.Y * d, p.Z + n.Z * d);
                }
                if (isOverlay) moved++; else named++;
            }

            Walk(root);

            if (moved + named > 0)
                Log($"Overlay depth lift: model diagonal {diagonal:F3} -> lift {lift:F6} " +
                    $"applied to {moved} overlay(s) and {named} decal-named base submesh(es).");
            return moved + named;
        }

        public static void ClearRegistries()
        {
            MeshFamilyRegistry.Clear();
            MeshSubmeshRegistry.Clear();
        }

        // =====================================================================
        // FOLDER-LEVEL LOAD
        // =====================================================================

        private static Model3DGroup LoadFolderInternal(string inputPath, int? explicitLod)
        {
            var group = new Model3DGroup();

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                Log("ERROR: inputPath is null/empty.");
                return group;
            }

            bool isFile = File.Exists(inputPath);
            string folder;
            if (isFile)
                folder = Path.GetDirectoryName(inputPath)!;
            else if (Directory.Exists(inputPath))
                folder = inputPath;
            else
                folder = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();

            if (!Directory.Exists(folder))
            {
                Log($"ERROR: Folder does not exist: {folder}");
                return group;
            }

            LastLoadedFolder = folder;

            // Auto-discover package root + a default livery context if the
            // caller hasn't configured them explicitly. This means a plain
            // LoadFolder(idfFolder) call now loads textures out-of-the-box
            // as long as the idf folder sits under an intact vehicle package.
            AutoConfigureFromFolder(folder);

            Log($"Scanning folder for mesh descriptors (temp000*mesh*) in: {folder}");
            string[] allMeshFiles = Directory.GetFiles(folder, "*temp000*mesh*", SearchOption.TopDirectoryOnly);

            if (allMeshFiles.Length == 0)
            {
                Log("ERROR: No temp000 mesh files (*temp000*mesh*) found in folder.");
                return group;
            }

            Array.Sort(allMeshFiles, StringComparer.OrdinalIgnoreCase);
            Log($"Found {allMeshFiles.Length} mesh descriptor file(s) (all LODs).");

            // Optional gender filter. Race-suit / driver / character ERPs
            // routinely ship parallel male+female variants of each part in
            // the same IDF folder ("driver_body_v2_male", "..._female",
            // "<livery>_male", "<livery>_female", "<livery>_body_logos_male",
            // ...). Loading both stacks the geometry at identical world
            // positions, which the user sees as a duplicated suit. The
            // filter accepts "male" / "female" / null (= both) and drops
            // descriptors whose filename contains the opposite gender token.
            // Token matching is anchored to "_male" / "_female" (with a
            // following non-letter) so substrings like "femalexyz" or
            // arbitrary asset names that happen to contain "male" anywhere
            // don't trip it.
            allMeshFiles = ApplyGenderFilter(allMeshFiles, GenderFilter);
            if (allMeshFiles.Length == 0)
            {
                Log($"WARNING: gender filter '{GenderFilter}' removed every mesh; restoring full list.");
                allMeshFiles = Directory.GetFiles(folder, "*temp000*mesh*", SearchOption.TopDirectoryOnly);
                Array.Sort(allMeshFiles, StringComparer.OrdinalIgnoreCase);
            }

            string[] meshFilesToUse = SelectLodFiles(allMeshFiles, explicitLod);

            // v17.4: the car's own node graph, read once per load, so each
            // part can be named after the exact node its vertices are bound
            // to instead of the (sometimes truncated) descriptor file name.
            var renderGraph = CarRenderGraph.TryLoad(folder, Log);
            Log($"Final mesh descriptor count after LOD filter: {meshFilesToUse.Length}");

            int totalParts = 0;
            int totalSubmeshes = 0;
            int texturedSubmeshes = 0;
            int skippedPlaceholders = 0;
            _stats.Reset();

            int descriptorIndex = 0;
            foreach (var meshFile in meshFilesToUse)
            {
                descriptorIndex++;
                try
                {
                    DescriptorPulse?.Invoke(Path.GetFileName(meshFile),
                        descriptorIndex, meshFilesToUse.Length);
                }
                catch { /* status is cosmetic */ }

                if (!LoadTyres && Path.GetFileName(meshFile)
                        .StartsWith("tyres!!!", StringComparison.OrdinalIgnoreCase))
                {
                    Log("--------------------------------------------------");
                    Log($"Skipping tyres descriptor (tyres disabled): {meshFile}");
                    continue;
                }

                Log("--------------------------------------------------");
                Log($"Loading mesh descriptor: {meshFile}");

                // TYRES: the shared tyres descriptor (auto-merged from
                // common.erp) binds its "wall"/"tread" slots via its OWN
                // per-compound .emb contexts (tyres!!!temp000.emb?context=
                // supersoft/medium/wet/...), not the car's driver-number
                // context. Swap the tyre context in for just this
                // descriptor and restore afterwards so car parts keep the
                // user's selected livery context. (Ported from the
                // UniversalExporter's per-descriptor context switch, gated
                // to the tyres stem only -- car descriptors must keep
                // honouring the driver-number dropdown.)
                var savedContext = _activeContext;
                // The tyres descriptor resolves through the shared
                // pipeline; suppress its classic-era whole-brush sidewall
                // tint for the duration (the logo merge applies the
                // compound colour properly).
                bool savedSuppress = TextureCache.SuppressTyreTint;
                if (Path.GetFileName(meshFile).StartsWith("tyres!!!",
                        StringComparison.OrdinalIgnoreCase))
                    TextureCache.SuppressTyreTint = true;
                var descContext = TryLoadTyresDescriptorContext(meshFile)
                    // Track ERPs (pit perches etc.) ship one descriptor per
                    // team, each with its OWN .emb?context=<team> sibling --
                    // fall through to that per-descriptor lookup.
                    ?? TryLoadPerDescriptorContext(meshFile);
                if (descContext != null)
                {
                    _activeContext = descContext;
                    Log($"  per-descriptor context: ctxId={descContext.ContextId} " +
                        $"({descContext.EntryCount} slot bindings)");
                }

                try
                {
                    var (partGroup, tex) = LoadOneMeshFile(meshFile, out int partSlot);

                    if (partGroup == null)
                    {
                        Log("  -> (skipped: placeholder / all-zero LOD record)");
                        skippedPlaceholders++;
                        continue;
                    }

                    if (partGroup.Children.Count > 0)
                    {
                        bool isTyresDescriptor = Path.GetFileName(meshFile)
                            .StartsWith("tyres!!!", StringComparison.OrdinalIgnoreCase);
                        if (isTyresDescriptor)
                        {
                            // Shared tyres: split the four origin-local
                            // tyres into per-wheel wrappers so Assemble
                            // can place each at its wheel node, and fix
                            // the tyre-specific material quirks (alpha
                            // mask, tiling tread UVs).
                            int added = AddTyreWheelWrappers(partGroup, group,
                                _textureCache, _activeContext, Log);
                            totalParts++;
                            totalSubmeshes += added;
                            texturedSubmeshes += tex;
                        }
                        else
                        {
                            int deferred = MoveOverlaysAfterBase(partGroup);
                            if (deferred > 0)
                                Log($"  draw order: {deferred} see-through/decal submesh(es) moved after the part's opaque submeshes.");
                            group.Children.Add(partGroup);
                            RegisterPartNode(partGroup, meshFile, partSlot, renderGraph);
                            totalParts++;
                            totalSubmeshes += partGroup.Children.Count;
                            texturedSubmeshes += tex;
                        }
                    }

                    Log($"  -> Added {partGroup.Children.Count} submesh(es) ({tex} textured).");
                }
                catch (Exception ex)
                {
                    Log($"EXCEPTION while loading '{meshFile}':");
                    Log(ex.ToString());
                }
                finally
                {
                    // Restore the folder-level context so the NEXT
                    // descriptor's lookup starts from the same baseline
                    // (whichever context AutoConfigure picked).
                    _activeContext = savedContext;
                    TextureCache.SuppressTyreTint = savedSuppress;
                }
            }

            Log($"TOTAL: {totalParts} part groups, {totalSubmeshes} submeshes " +
                $"({texturedSubmeshes} textured, {totalSubmeshes - texturedSubmeshes} solid-colour), " +
                $"placeholders skipped: {skippedPlaceholders}.");

            if (_textureCache != null)
                Log($"TextureCache: {_textureCache.DecodedTextureCount} unique textures decoded.");

            _stats.WriteSummary(Log);

            if (totalParts == 0)
                Log("WARNING: No geometry was added. Check paths / headers / data files.");
            else
                Log("Model successfully built.");

            ApplyOverlayDepthOffset(group);

            return group;
        }

        private static string[] SelectLodFiles(string[] allMeshFiles, int? explicitLod)
        {
            var cmp = StringComparison.OrdinalIgnoreCase;

            // Drop "consolidated" mesh files that have no part name between
            // lod=N& and &mesh -- e.g. "mclaren!!!temp000.idf?lod=0&mesh".
            // These are the whole-car geometry packed into one mesh file
            // (used by the game for shadow casting / distant LODs). They
            // duplicate the per-part meshes we already load and produce a
            // bogus "part" entry in the load summary covering every slot.
            // HELMET FIX: helmet IDFs ship ONLY a consolidated mesh per LOD
            // (no per-part subdivision -- the whole helmet is one mesh file).
            // Blindly applying the drop-consolidated filter empties the list
            // and produces a geometry-free load. Only drop consolidated files
            // when per-part files exist alongside them; if every file is
            // consolidated, keep them all.
            int beforeFilter = allMeshFiles.Length;
            var nonConsolidated = allMeshFiles.Where(f => !IsConsolidatedWholeCarMesh(f)).ToArray();
            if (nonConsolidated.Length > 0)
            {
                allMeshFiles = nonConsolidated;
                int dropped = beforeFilter - allMeshFiles.Length;
                if (dropped > 0)
                    Log($"LOD selection: dropped {dropped} consolidated whole-car mesh file(s) " +
                        "(lod=N&mesh with no part name).");
            }
            else
            {
                Log($"LOD selection: all {beforeFilter} mesh files are consolidated " +
                    "(helmet-style single-mesh-per-LOD); keeping all.");
            }

            if (explicitLod.HasValue)
            {
                string lodToken = $"lod={explicitLod.Value}";
                var exact = allMeshFiles.Where(f => f.IndexOf(lodToken, cmp) >= 0).ToArray();
                if (exact.Length > 0)
                {
                    Log($"LOD explicit filter: Using {exact.Length} file(s) matching '{lodToken}'.");
                    return exact;
                }
                Log($"LOD explicit filter: No files matched '{lodToken}'. Falling back to default LOD selection.");
            }

            var lod0 = allMeshFiles.Where(f => f.IndexOf("lod=0", cmp) >= 0).ToArray();
            if (lod0.Length > 0)
            {
                Log($"LOD selection: Using {lod0.Length} file(s) with 'lod=0' (highest detail).");
                return lod0;
            }

            var lodAll = allMeshFiles.Where(f => f.IndexOf("lod=all", cmp) >= 0).ToArray();
            if (lodAll.Length > 0)
            {
                Log($"LOD selection: No 'lod=0' files. Using {lodAll.Length} file(s) with 'lod=all'.");
                return lodAll;
            }

            var withLod = allMeshFiles
                .Select(f => new { File = f, Lod = ExtractLodValueFromPath(f) })
                .Where(x => x.Lod.HasValue)
                .ToArray();

            if (withLod.Length > 0)
            {
                int minLod = withLod.Min(x => x.Lod.Value);
                var picked = withLod.Where(x => x.Lod.Value == minLod).Select(x => x.File).ToArray();
                Log($"LOD selection: Using {picked.Length} file(s) with smallest lod={minLod}.");
                return picked;
            }

            Log("LOD selection: No 'lod=' pattern found in filenames. Using ALL mesh files.");
            return allMeshFiles;
        }

        /// <summary>
        /// Detects the consolidated whole-car mesh: filename has "&mesh"
        /// preceded immediately by "lod=N&" with no part name in between.
        /// Real per-part meshes always have a part name segment between
        /// the LOD and the &mesh marker, e.g. "lod=0&sidepod_fl&mesh".
        /// </summary>
        private static bool IsConsolidatedWholeCarMesh(string filePath)
        {
            string fileName = Path.GetFileName(filePath);

            // The shared TYRES descriptor ("tyres!!!temp000.idf?lod=N&mesh",
            // auto-merged into the car idf folder from common.erp) is also
            // a part-nameless lod=N&mesh file, but it is NOT a duplicate of
            // the per-part car meshes -- it's the only source of tyre
            // geometry. Exempt it from the consolidated-mesh drop.
            if (fileName.StartsWith("tyres!!!", StringComparison.OrdinalIgnoreCase))
                return false;

            // URL-decode %3F -> ? so the regex sees the original query string.
            string decoded = Uri.UnescapeDataString(fileName);
            return System.Text.RegularExpressions.Regex.IsMatch(
                decoded,
                @"lod=\d+&mesh\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // =====================================================================
        // AUTO-DISCOVERY OF PACKAGE ROOT + DEFAULT CONTEXT
        // =====================================================================
        //
        // The intended Codemasters folder layout is:
        //   <packageRoot>/
        //       teams/<team>/idf/           <- mesh + emb files
        //       teams/<team>/materials/     <- .material files referenced by EAID
        //       teams/<team>/textures/      <- .tif + .image payloads
        //       shared/generics/materials/
        //       shared/generics/textures/
        //
        // When the caller just says "LoadFolder(idfFolder)" without touching
        // SetTextureCache/SetActiveContext, we walk up the directory tree
        // looking for the package root (folder that has BOTH a "teams" and
        // a "shared" subfolder, or any ancestor that contains the textures
        // dir along a known relative path). We also pick the first available
        // EMB context file so the car has a driver binding.
        //
        // Rationale: the symptom "car is all white" almost always means a
        // caller loaded meshes without wiring up the texture pipeline. This
        // auto-setup removes that footgun.

        private static void AutoConfigureFromFolder(string idfFolder)
        {
            // FIX (stale-cache guard): the static _textureCache and
            // _activeContext persist across LoadFolder calls by design --
            // callers that want to reuse a configured cache across
            // mutiple loads rely on that. But when loading a DIFFERENT
            // ERP in the same session (e.g. F1 2021 mclaren.erp followed
            // by F1 22 mclaren.erp), the previous ERP's cache is still
            // live and its PackageRoot points into a completely unrelated
            // temp folder -- every material/texture lookup for the new
            // car then resolves to paths in the old cache and fails with
            // "Material file not found on disk", rendering the car white.
            //
            // Detect that scenario here: if the incoming idfFolder is
            // NOT inside the cached packageRoot, the cache belongs to a
            // different package/ERP and must be dropped. We leave legit
            // reuse (same packageRoot, multiple LoadFolder calls under
            // different part subfolders) untouched.
            //
            // Normalization protects against casing differences and
            // trailing-separator variation between the two paths.
            if (_textureCache != null && !string.IsNullOrEmpty(_textureCache.PackageRoot) &&
                !string.IsNullOrEmpty(idfFolder))
            {
                string idfNorm, pkgNorm;
                try
                {
                    idfNorm = Path.GetFullPath(idfFolder).TrimEnd(Path.DirectorySeparatorChar,
                                                                 Path.AltDirectorySeparatorChar);
                    pkgNorm = Path.GetFullPath(_textureCache.PackageRoot).TrimEnd(Path.DirectorySeparatorChar,
                                                                                  Path.AltDirectorySeparatorChar);
                }
                catch
                {
                    idfNorm = idfFolder;
                    pkgNorm = _textureCache.PackageRoot;
                }

                string pkgWithSep = pkgNorm + Path.DirectorySeparatorChar;
                bool idfIsInsidePkg =
                    idfNorm.Equals(pkgNorm, StringComparison.OrdinalIgnoreCase) ||
                    idfNorm.StartsWith(pkgWithSep, StringComparison.OrdinalIgnoreCase);

                if (!idfIsInsidePkg)
                {
                    Log("AutoConfigure: stale static state detected -- cached TextureCache " +
                        "belongs to a different ERP than the one being loaded.");
                    Log($"AutoConfigure:   incoming idfFolder = {idfNorm}");
                    Log($"AutoConfigure:   cached packageRoot = {pkgNorm}");
                    Log("AutoConfigure: resetting _textureCache and _activeContext so the " +
                        "new ERP gets its own cache and livery context.");
                    _textureCache = null;
                    _activeContext = null;
                    // Stale TextureCache means stale hi-res brushes too.
                    HighResBrushProvider.Clear();
                }
            }

            // Already configured? Respect the caller's choice -- EXCEPT
            // when the user changed the driver-number preference: a
            // same-ERP reload (dropdown) keeps the cache, so honour the
            // new context here or the dropdown has no live effect.
            if (_textureCache != null && _activeContext != null)
            {
                if (PreferredContextId >= 0 && _activeContext.ContextId != PreferredContextId)
                {
                    foreach (var c in EmbContextReader.EnumerateContexts(idfFolder))
                    {
                        if (c.id != PreferredContextId) continue;
                        try
                        {
                            _activeContext = EmbContextReader.FromFile(c.path);
                            Log($"AutoConfigure: livery context SWITCHED to user-preferred " +
                                $"context={c.id} (same-ERP reload).");
                        }
                        catch (Exception ex)
                        {
                            Log($"AutoConfigure: failed to switch context: {ex.Message}");
                        }
                        break;
                    }
                }
                Log($"AutoConfigure: skipped (texture cache + context already set). " +
                    $"packageRoot={_textureCache.PackageRoot}, contextId={_activeContext.ContextId}");
                return;
            }

            string packageRoot = _textureCache?.PackageRoot
                                 ?? DiscoverPackageRoot(idfFolder);

            if (packageRoot == null)
            {
                Log("AutoConfigure: *** package root NOT FOUND. ***");
                Log("AutoConfigure: The idf folder does not sit under a recognisable");
                Log("AutoConfigure: vehicle-package layout (missing sibling 'materials'");
                Log("AutoConfigure: and 'textures' dirs). Textures will NOT load --");
                Log("AutoConfigure: every submesh will use the neutral white fallback.");
                Log("AutoConfigure: To fix: extract the full f1_XXXX_vehicle_package.erp");
                Log("AutoConfigure: so the layout is  packageRoot/teams/<team>/{idf,");
                Log("AutoConfigure: materials,textures}  and pass the package root to");
                Log("AutoConfigure: SetTextureCache(new TextureCache(packageRoot)).");
                return;
            }

            Log($"AutoConfigure: package root = {packageRoot}");

            if (_textureCache == null)
            {
                _textureCache = new TextureCache(packageRoot, Log);
                Log("AutoConfigure: TextureCache created (BC1/BC3 decoder enabled).");
            }

            if (_activeContext == null)
            {
                // HELMET FIX: prefer the NAMED livery context over the
                // generic "default" one. F1 25 helmet ERPs ship two .emb
                // files per IDF:
                //   <stem>.emb?context=default            -- template
                //     bindings (e.g. helmet -> drivers/gender/teamname_charname/.../helmet.material)
                //     pointing at paths that DO NOT exist inside the helmet
                //     ERP -- loading these resolves every material to "not
                //     found" and yields a textureless export.
                //   <stem>.emb?context=<livery_name>      -- livery-specific
                //     bindings (e.g. helmet -> team_livery/classic-custom-senna01/.../helmet.material)
                //     pointing at the REAL .material files that the ERP
                //     actually ships.
                // We scan first for a named context file and load that; only
                // when no named context is present do we fall back to the
                // numeric/default ordering EmbContextReader.EnumerateContexts
                // returns.
                string namedContextPath = null;
                string namedContextName = null;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(idfFolder, "*emb*context=*"))
                    {
                        // The shared tyres' per-compound contexts
                        // (tyres!!!temp000.emb?context=soft/hard/...) are
                        // named too, but they bind TYRE slots only --
                        // picking one as the folder-level context leaves
                        // the whole car white. They are handled by the
                        // tyres descriptor's own per-descriptor switch.
                        if (Path.GetFileName(f).StartsWith("tyres",
                                StringComparison.OrdinalIgnoreCase))
                            continue;   // covers tyres!!! AND tyres_wet!!!
                        var m = System.Text.RegularExpressions.Regex.Match(f,
                            @"context=([A-Za-z0-9][A-Za-z0-9_\-]*)");
                        if (!m.Success) continue;
                        string name = m.Groups[1].Value;
                        if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase)) continue;
                        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d+$")) continue;
                        namedContextPath = f;
                        namedContextName = name;
                        break;
                    }
                }
                catch { }

                if (namedContextPath != null)
                {
                    try
                    {
                        _activeContext = EmbContextReader.FromFile(namedContextPath);
                        Log($"AutoConfigure: named livery context loaded ('{namedContextName}') " +
                            $"from {Path.GetFileName(namedContextPath)}.");
                    }
                    catch (Exception ex)
                    {
                        Log($"AutoConfigure: failed to load named context " +
                            $"{namedContextPath}: {ex.Message}. Falling back to default context.");
                    }
                }

                if (_activeContext == null)
                {
                    // Fall back to the numeric/default ordering (car ERPs).
                    // The shared tyres' per-compound contexts are named but
                    // bind TYRE slots only -- filter them out, then honour
                    // the driver-number dropdown's PreferredContextId.
                    var contexts = EmbContextReader.EnumerateContexts(idfFolder)
                        .Where(c => !Path.GetFileName(c.path ?? "").StartsWith(
                            "tyres", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (contexts.Count > 0)
                    {
                        var (id, path) = contexts[0];
                        if (PreferredContextId >= 0)
                        {
                            foreach (var c in contexts)
                            {
                                if (c.id == PreferredContextId) { (id, path) = c; break; }
                            }
                        }
                        try
                        {
                            _activeContext = EmbContextReader.FromFile(path);
                            Log($"AutoConfigure: livery context loaded (context={id}" +
                                (PreferredContextId >= 0 && id == PreferredContextId
                                    ? ", user-preferred" : ", default") + "). " +
                                $"Call SetActiveContext(...) with another context to change driver.");
                        }
                        catch (Exception ex)
                        {
                            Log($"AutoConfigure: failed to load context {path}: {ex.Message}");
                        }
                    }
                    else
                    {
                        Log("AutoConfigure: no .emb?context=* files found in idf folder -- " +
                            "material slot names won't resolve to materials.");
                    }
                }

                // HELMET FIX: also record the named livery context's NAME (we
                // already scanned for it above). Some downstream fallbacks
                // still consult _helmetLiveryContextName for texture-path
                // guesses when a material file is missing.
                try
                {
                    _helmetLiveryContextName = namedContextName;
                    if (_helmetLiveryContextName != null)
                        Log($"AutoConfigure: detected helmet livery context name = '{_helmetLiveryContextName}'.");
                    // Legacy scan kept for completeness in case the above
                    // didn't already pick one up.
                    if (_helmetLiveryContextName == null)
                    foreach (var f in Directory.EnumerateFiles(idfFolder, "*emb*context=*"))
                    {
                        // Tyre compound contexts are named but bind tyre
                        // slots only -- never the livery context name.
                        if (Path.GetFileName(f).StartsWith("tyres",
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        // Accept the full token after `context=` up to the
                        // next non-name character (file extension delimiter,
                        // end of path, etc.). Stock-driver contexts like
                        // "2025-sauber_nico_hulkenberg" start with digits and
                        // include hyphens, so we accept any mix of letters,
                        // digits, '-', and '_' here.
                        var m = System.Text.RegularExpressions.Regex.Match(f,
                            @"context=([A-Za-z0-9][A-Za-z0-9_\-]*)");
                        if (!m.Success) continue;
                        string name = m.Groups[1].Value;
                        if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase)) continue;
                        // Skip pure-numeric contexts (those are the per-driver
                        // car contexts the old loader already understands).
                        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d+$")) continue;
                        _helmetLiveryContextName = name;
                        Log($"AutoConfigure: detected helmet livery context name = '{name}'.");
                        break;
                    }
                    if (_helmetLiveryContextName == null)
                        Log("AutoConfigure: no named (non-default) helmet livery context found; " +
                            "texture binding will rely on material file sampler bindings only.");
                }
                catch (Exception ex)
                {
                    Log($"AutoConfigure: helmet livery context sniff threw ({ex.Message}); continuing.");
                    _helmetLiveryContextName = null;
                }
            }
        }

        private static string DiscoverPackageRoot(string startFolder)
        {
            // Authoritative path: walk up looking for the ERP extractor's
            // marker file. ErpIdfExtractor writes _extract_log.txt and
            // _cache_ok.txt at the cache root every time it explodes an ERP.
            // Anything WITHIN that root is part of the ERP's content tree;
            // the cache root itself IS the packageRoot we want -- joining
            // it with an EAID's stripped relative path yields the right
            // on-disk file regardless of how many levels of grouping
            // (drivers/<gender>/<X>, teams/<X>, team_livery/<X>, ...) sit
            // between root and the materials/textures pair.
            //
            // Falling back to the heuristic LooksLikePackageRoot only when
            // the marker is absent keeps the function working for non-cache
            // inputs (raw IDF folder on disk, classic-content workflows).
            string cur = Path.GetFullPath(startFolder);
            for (int hop = 0; hop < 12 && !string.IsNullOrEmpty(cur); hop++)
            {
                try
                {
                    if (File.Exists(Path.Combine(cur, "_extract_log.txt")) ||
                        File.Exists(Path.Combine(cur, "_cache_ok.txt")))
                        return cur;
                }
                catch { }
                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, cur, StringComparison.Ordinal))
                    break;
                cur = parent;
            }

            // No extractor marker found -- fall back to the layout heuristic
            // (8 hops, structural pattern match).
            cur = Path.GetFullPath(startFolder);
            for (int hop = 0; hop < 8 && !string.IsNullOrEmpty(cur); hop++)
            {
                if (LooksLikePackageRoot(cur)) return cur;
                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, cur, StringComparison.Ordinal))
                    break;
                cur = parent;
            }
            return null;
        }

        private static bool LooksLikePackageRoot(string path)
        {
            try
            {
                string teams = Path.Combine(path, "teams");
                string shared = Path.Combine(path, "shared");
                if (Directory.Exists(teams))
                {
                    // Any team under teams/ that has both materials AND textures is enough.
                    foreach (var team in Directory.EnumerateDirectories(teams))
                    {
                        string mats = Path.Combine(team, "materials");
                        string texs = Path.Combine(team, "textures");
                        if (Directory.Exists(mats) && Directory.Exists(texs))
                            return true;
                    }

                    // Also fine if shared/generics/textures exists at this level.
                    if (Directory.Exists(Path.Combine(shared, "generics", "textures")))
                        return true;
                }
            }
            catch { }

            // AUDIT: non-teams packagings. f1_safetycar_package lays its
            // cars out as <package>/safety_car/{idf,materials,textures}
            // with NO teams/ level, so the teams-shaped check above never
            // matches and every submesh went white. Accept any direct
            // child that carries the full idf+materials+textures triple.
            // The "teams" leaf is excluded so a teams-shaped cache still
            // resolves its root one level up (at the package, not at
            // teams/ -- teams/<team> children would match this scan too).
            try
            {
                string leaf = Path.GetFileName(
                    path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.Equals(leaf, "teams", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var child in Directory.EnumerateDirectories(path))
                    {
                        if (Directory.Exists(Path.Combine(child, "idf")) &&
                            Directory.Exists(Path.Combine(child, "materials")) &&
                            Directory.Exists(Path.Combine(child, "textures")))
                            return true;
                    }
                }
            }
            catch { }

            // UNIVERSAL helmet/character/livery layout: ANY directory at
            // any depth under `path` that has BOTH a `materials` and a
            // `textures` sibling counts as a valid package root.
            //
            // Helmet ERPs use multiple parent groupings:
            //   drivers/<gender>/<team_driver>/{materials,textures}   (stock drivers)
            //   team_livery/<custom>/{materials,textures}             (classic / custom heritage)
            //   livery_package_shared/replica/<ctx>/{materials,textures}
            // etc. Rather than enumerate every parent name as it ships,
            // we scan the tree (capped depth) and return true on the
            // first hit. The cap keeps a malformed cache from walking
            // forever; helmet caches stay well within it.
            return ContainsMaterialsAndTexturesPair(path, depth: 0, maxDepth: 6);
        }

        // =====================================================================
        // CORE: PARSE ONE MESH FILE (which may contain N submeshes)
        // =====================================================================
        // See comments on the sub-mesh 88-byte block layout in the previous
        // version; unchanged here.
        // =====================================================================

        /// <summary>
        /// Preferred tyre compound for the auto-loaded shared tyres.
        /// "soft" matches the red-walled race-start look; change to any
        /// context name shipped in common.erp (default / hard / medium /
        /// soft / supersoft / ultrasoft / hypersoft / superhard /
        /// intermediate / wet ...).
        /// </summary>
        public static string TyreCompoundPreference = "supersoft";

        /// <summary>
        /// Per-descriptor .emb context for the shared TYRES descriptor
        /// only. Returns null for every other descriptor so car parts
        /// keep the folder-level (driver-number) context.
        /// </summary>
        private static EmbContextReader TryLoadTyresDescriptorContext(string meshFilePath)
        {
            try
            {
                string leaf = Path.GetFileName(meshFilePath);
                if (!leaf.StartsWith("tyres!!!", StringComparison.OrdinalIgnoreCase))
                    return null;

                string folder = Path.GetDirectoryName(meshFilePath);
                if (string.IsNullOrEmpty(folder)) return null;

                var matches = Directory.EnumerateFiles(
                    folder, "tyres!!!*emb*context=*",
                    SearchOption.TopDirectoryOnly).ToList();
                if (matches.Count == 0) return null;

                // Preference order: the configured compound, then any
                // non-"default" compound, then whatever is first.
                string pref = TyreCompoundPreference ?? "";
                string picked =
                    matches.FirstOrDefault(f => f.IndexOf(
                        "context=" + pref, StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? matches.FirstOrDefault(f => f.IndexOf(
                        "context=default", StringComparison.OrdinalIgnoreCase) < 0)
                    ?? matches[0];

                return EmbContextReader.FromFile(picked);
            }
            catch
            {
                return null;
            }
        }

        private static bool ContainsMaterialsAndTexturesPair(string dir, int depth, int maxDepth)
        {
            if (depth > maxDepth) return false;
            try
            {
                // Only ACCEPT the pair when it sits at depth >= 1, i.e. inside
                // a child folder of the candidate. If the candidate IS itself
                // the directory holding materials/textures as direct children
                // (e.g. a driver folder like .../drivers/male/<X>/), then
                // accepting it as packageRoot would duplicate the path: the
                // EAID's stripped relative path ALREADY includes "drivers/
                // male/<X>/materials/..." -- joining that onto a packageRoot
                // that's already inside <X> produces ".../drivers/male/<X>/
                // drivers/male/<X>/materials/...". We want packageRoot to be
                // at the LEVEL THAT CONTAINS the grouping dir (drivers,
                // teams, team_livery, ...), not the leaf X dir itself.
                if (depth >= 1)
                {
                    string mats = Path.Combine(dir, "materials");
                    string texs = Path.Combine(dir, "textures");
                    if (Directory.Exists(mats) && Directory.Exists(texs))
                        return true;
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (ContainsMaterialsAndTexturesPair(sub, depth + 1, maxDepth))
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Track ERPs pack many mesh descriptors into one folder,
        /// each with its own `<basename>.emb?context=<team>` binding.
        /// This looks up the .emb sibling that matches
        /// <paramref name="meshFilePath"/>'s basename and returns
        /// a loaded EmbContextReader for it, or null when no
        /// matching sibling exists (in which case the folder-level
        /// context stays in effect).
        ///
        /// Filename convention on disk (per ErpIdfExtractor):
        ///   mesh:    &lt;basename&gt;!!!temp000.idf?lod=...&amp;mesh
        ///   emb:     &lt;basename&gt;!!!temp000.emb?context=&lt;team&gt;
        /// The '?' in the EAID is URL-encoded to "%3F" on disk. We
        /// match by "&lt;basename&gt;!!!*emb*context=*" so both the
        /// literal and URL-encoded forms are covered.
        /// </summary>
        private static EmbContextReader TryLoadPerDescriptorContext(string meshFilePath)
        {
            try
            {
                string folder = Path.GetDirectoryName(meshFilePath);
                if (string.IsNullOrEmpty(folder)) return null;
                string leaf = Path.GetFileName(meshFilePath);
                int marker = leaf.IndexOf("!!!", StringComparison.Ordinal);
                if (marker <= 0) return null;
                string basename = leaf.Substring(0, marker);
                if (string.IsNullOrEmpty(basename)) return null;

                var matches = Directory.EnumerateFiles(
                    folder, basename + "!!!*emb*context=*",
                    SearchOption.TopDirectoryOnly).ToList();
                if (matches.Count == 0) return null;

                // GATE: only per-descriptor mode when the descriptor's
                // basename has just a HANDFUL of matching context
                // files. Track content typically ships one default
                // and one team-specific context per basename (2-3
                // files max). CAR ERPs pack 100 driver-number
                // contexts under one basename (e.g. redbull!!!
                // emb?context=1..99 + context=default), and the
                // MainWindow's ContextComboBox is supposed to pick
                // which one via ApplySelectedContextForFolder. If we
                // override that here we'd silently ignore the user's
                // driver choice AND (worse) mispick context=1 for
                // every car load. Keep this gate conservative -- 4
                // is comfortably above the tracks' 2-3 and far below
                // the cars' 100.
                const int kPerDescriptorMaxMatches = 4;
                if (matches.Count > kPerDescriptorMaxMatches)
                    return null;

                // Prefer any non-"default" context (per-team bindings
                // like "2026-ferrari" carry the actual slot mapping;
                // "default" often points at placeholder / template
                // materials that don't exist in the ERP itself).
                string picked = matches.FirstOrDefault(f =>
                    f.IndexOf("context=default", StringComparison.OrdinalIgnoreCase) < 0)
                    ?? matches[0];

                return EmbContextReader.FromFile(picked);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Post-process for the shared TYRES descriptor (auto-merged from
        /// common.erp). The descriptor holds FOUR origin-local tyres as
        /// alternating wall/tread submeshes. Mirrors the proven classic
        /// (F1 2018 showroom) treatment:
        ///
        ///   1. Force per-pixel OPAQUE brushes + BackMaterial. Sidewall
        ///      textures carry a game-side alpha mask that WPF treats as
        ///      transparency -- without this the sidewall is invisible.
        ///   2. Tread UVs tile (u spans [0,3]) -- swap in a tiling brush
        ///      whose viewport is one UV unit so WPF reproduces GPU wrap.
        ///   3. Pair wall+tread per tyre, split fronts (narrow) from
        ///      rears (wide) by bounds, wrap each pair in a Model3DGroup
        ///      tagged wheel_fl / wheel_fr / wheel_bl / wheel_br via
        ///      ModelPartNameRegistry. "Assemble car" then places each
        ///      wrapper at its wheel node from the car's ?render graph.
        ///
        /// Returns the number of submeshes added to <paramref name="root"/>.
        /// </summary>
        /// <summary>
        /// Shared decal-overlay UV validation + preparation (the FIX 34
        /// discipline), callable from the legacy loader so 2019/2020
        /// overlays get the same accuracy treatment as modern content:
        ///
        ///   - per-triangle filter: keep only triangles whose three
        ///     vertices sit inside [0,1] (±0.05) -- drops tiled carbon
        ///     copies without killing the sponsor faces next to them
        ///   - reject overlays that are fully parked (single-point or
        ///     bottom-right corner) -- they'd render a stretched pixel
        ///     and punch depth holes
        ///   - preview UVs clamped to [0,1] with two sentinel entries
        ///     appended to pin WPF's brush-mapping bbox (caller must
        ///     append two matching positions/normals)
        ///   - export UVs raw with dropped-face verts parked at the
        ///     transparent corner, one per ORIGINAL vertex (for
        ///     SetRawDecalUv / TEXCOORD_1)
        /// </summary>
        internal sealed class FilteredOverlayUvs
        {
            public string RejectReason;                       // null = accepted
            public int[] KeptIndices;
            public System.Windows.Point[] PreviewUvs;         // length = verts + 2
            public System.Windows.Media.PointCollection ExportUvs; // length = verts
        }

        /// <summary>
        /// TILE-SHIFT normalization: decal UVs are sometimes authored one
        /// (or more) whole wrap tiles away from [0,1] -- the 2021 Red Bull
        /// sidepod number's pair-2 spans u=[1.117,1.889], which the game's
        /// wrap sampler reads as [0.117,0.889]. When the USED range fits
        /// inside a single integer tile, shift the whole (per-call) array
        /// back so the standard [0,1] filters and brushes see the true
        /// atlas region. No-op when the range already touches [0,1] or
        /// crosses a tile boundary (a genuine wrap seam we can't shift).
        /// U and V shift independently.
        /// </summary>
        internal static void NormalizeUvTileOffset(
            System.Windows.Point[] uvArr, int[] indices)
        {
            if (uvArr == null || indices == null || indices.Length == 0) return;
            double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
            double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
            foreach (int vi in indices)
            {
                if (vi < 0 || vi >= uvArr.Length) continue;
                double u = uvArr[vi].X, v = uvArr[vi].Y;
                if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                if (v < vMin) vMin = v; if (v > vMax) vMax = v;
            }
            if (double.IsPositiveInfinity(uMin)) return;

            double du = 0, dv = 0;
            double fu = Math.Floor(uMin);
            if (fu != 0 && uMax - fu <= 1.001) du = -fu;
            double fv = Math.Floor(vMin);
            if (fv != 0 && vMax - fv <= 1.001) dv = -fv;
            if (du == 0 && dv == 0) return;

            for (int k = 0; k < uvArr.Length; k++)
                uvArr[k] = new System.Windows.Point(uvArr[k].X + du, uvArr[k].Y + dv);
        }

        internal static FilteredOverlayUvs BuildFilteredOverlayUvs(
            System.Windows.Point[] uvs, int[] indices)
        {
            var res = new FilteredOverlayUvs();
            if (uvs == null || indices == null || indices.Length < 3)
            {
                res.RejectReason = "no UVs / indices";
                return res;
            }
            NormalizeUvTileOffset(uvs, indices);

            const double kLo = -0.05, kHi = 1.05;
            var keep = new List<int>(indices.Length);
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                bool ok = true;
                for (int k = 0; k < 3; k++)
                {
                    int vi = indices[t + k];
                    if (vi < 0 || vi >= uvs.Length) { ok = false; break; }
                    var p = uvs[vi];
                    if (p.X < kLo || p.X > kHi || p.Y < kLo || p.Y > kHi)
                    { ok = false; break; }
                }
                if (ok)
                {
                    keep.Add(indices[t]);
                    keep.Add(indices[t + 1]);
                    keep.Add(indices[t + 2]);
                }
            }
            if (keep.Count == 0)
            {
                res.RejectReason = "no in-range triangles (tiled UVs)";
                return res;
            }

            double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
            double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
            bool anyReal = false;
            foreach (int vi in keep)
            {
                double u = uvs[vi].X, v = uvs[vi].Y;
                if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                if (v < vMin) vMin = v; if (v > vMax) vMax = v;
                if (u < 0.97 || v < 0.97) anyReal = true;
            }
            if (uMax - uMin < 0.002 && vMax - vMin < 0.002)
            {
                res.RejectReason = $"single-point park at ({uMin:F3},{vMin:F3})";
                return res;
            }
            if (!anyReal)
            {
                res.RejectReason = "all UVs corner-parked (transparent)";
                return res;
            }

            var keptVerts = new HashSet<int>(keep);
            var park = new System.Windows.Point(0.002, 0.998);

            var preview = new System.Windows.Point[uvs.Length + 2];
            var export = new System.Windows.Point[uvs.Length];
            for (int i = 0; i < uvs.Length; i++)
            {
                double cu = uvs[i].X, cv = uvs[i].Y;
                if (cu < 0) cu = 0; else if (cu > 1) cu = 1;
                if (cv < 0) cv = 0; else if (cv > 1) cv = 1;
                preview[i] = new System.Windows.Point(cu, cv);
                export[i] = keptVerts.Contains(i) ? uvs[i] : park;
            }
            preview[uvs.Length] = new System.Windows.Point(0, 0);
            preview[uvs.Length + 1] = new System.Windows.Point(1, 1);

            res.KeptIndices = keep.ToArray();
            res.PreviewUvs = preview;
            res.ExportUvs = new System.Windows.Media.PointCollection(export);
            return res;
        }

        /// <summary>
        /// True for slots that belong to a tyre's SIDEWALL. Modern
        /// descriptors label them "wall"; classic-era descriptors label
        /// the first pair "wall"/"sidewall" and auto-generate "mesh_N"
        /// for the rest (even N = sidewall, odd N = tread).
        /// </summary>
        internal static bool IsTyreWallSlot(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return false;
            if (slot.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string t = slot.Trim().ToLowerInvariant();
            if (t.StartsWith("mesh_") && int.TryParse(t.Substring(5), out int n))
                return n % 2 == 0;
            return false;
        }

        internal static int AddTyreWheelWrappers(Model3DGroup tyresGroup, Model3DGroup root,
            TextureCache cache, EmbContextReader tyreContext, Action<string> log)
        {
            // ---- 1+2: material fixes per submesh. The tinted Pirelli
            // sidewall logo is decoded ONCE and merged INTO the wall
            // diffuse during the bake -- a single opaque bitmap renders
            // identically in WPF and the DX PBR pass (a separate alpha
            // overlay mesh needed per-renderer transparency handling
            // and washed the whole wall with the tint under DX). ----
            TryDecodeSidewallLogo(cache, tyreContext, log,
                out byte[] logoPx, out int logoW, out int logoH);
            foreach (var child in tyresGroup.Children)
            {
                if (child is GeometryModel3D gm && !gm.IsFrozen)
                {
                    var mesh0 = gm.Geometry as MeshGeometry3D;
                    string slot0 = mesh0 != null
                        ? (MeshSubmeshRegistry.GetSubmeshName(mesh0) ?? "") : "";
                    gm.Material = MakeTyreMaterial(gm.Material, mesh0, slot0,
                        logoPx, logoW, logoH, log);
                    gm.BackMaterial = gm.Material;
                }
            }

            // ---- 3: pair wall+tread per tyre ----
            var pairs = new List<List<Model3D>>();
            List<Model3D> cur = null;
            foreach (var child in tyresGroup.Children)
            {
                string slot = "";
                if (child is GeometryModel3D g && g.Geometry is MeshGeometry3D mg)
                    slot = MeshSubmeshRegistry.GetSubmeshName(mg) ?? "";
                bool isWall = IsTyreWallSlot(slot);
                if (isWall || cur == null)
                {
                    cur = new List<Model3D>();
                    pairs.Add(cur);
                }
                cur.Add(child);
            }

            if (pairs.Count != 4)
            {
                // Unexpected layout -- keep everything in one group so
                // nothing is lost; it just won't split per wheel.
                Log($"  [TYRES] expected 4 wall/tread pairs, got {pairs.Count}; " +
                    "adding as a single group (no per-wheel split).");
                root.Children.Add(tyresGroup);
                return tyresGroup.Children.Count;
            }

            // Wheel assignment. Two signals, in preference order:
            //
            // 1. WIDTH: when the four pairs' tyre widths differ (2021
            //    Ferrari: fronts 0.379, rears 0.461 -- min bounds
            //    extent, since the other two axes are the diameter),
            //    the two NARROW pairs are the fronts and the two WIDE
            //    pairs the rears, keeping record order for left/right.
            //
            // 2. RECORD ORDER bl,br,fl,fr: F1 25 content reports
            //    identical bounds for all four pairs, so width can't
            //    discriminate; the descriptor's pair order matches the
            //    ERP's skinning resource order (x0_tyre_bl, _br, _fl,
            //    _fr -- verified in the preview).
            double PairWidth(List<Model3D> p)
            {
                double w = 0;
                foreach (var m in p)
                {
                    var b = m.Bounds;
                    w = Math.Max(w, Math.Min(b.SizeX, Math.Min(b.SizeY, b.SizeZ)));
                }
                return w;
            }
            var widths = pairs.Select(PairWidth).ToArray();
            double wMin = widths.Min(), wMax = widths.Max();
            bool widthsDiffer = wMax > wMin * 1.05;

            for (int pi = 0; pi < pairs.Count; pi++)
            {
                var slots = pairs[pi].Select(m =>
                    (m is GeometryModel3D g && g.Geometry is MeshGeometry3D mg)
                        ? (MeshSubmeshRegistry.GetSubmeshName(mg) ?? "?") : "?");
                var pb = pairs[pi][0].Bounds;
                log($"  [TYRES] pair[{pi}]: slots=[{string.Join(",", slots)}] " +
                    $"width={widths[pi]:F3} " +
                    $"center=({pb.X + pb.SizeX / 2:F3},{pb.Y + pb.SizeY / 2:F3},{pb.Z + pb.SizeZ / 2:F3})");
            }

            // Width-split quality: a trustworthy measurement gives TWO
            // narrow (fronts) and TWO wide (rears). Legacy descriptors
            // auto-name six of the eight submeshes "mesh_N" and their
            // measured widths come out wrong (2019 Ferrari: one pair at
            // 0.381 and THREE at 0.468 -- physically impossible), which
            // scrambled the by-width assignment. Only trust widths when
            // they split cleanly 2+2.
            var sortedW = widths.OrderBy(w => w).ToArray();
            bool clean22 = pairs.Count == 4 &&
                sortedW[1] <= sortedW[0] * 1.05 &&
                sortedW[3] <= sortedW[2] * 1.05 &&
                sortedW[2] > sortedW[1] * 1.05;

            var wheelNameByPair = new Dictionary<List<Model3D>, string>();
            if (clean22)
            {
                var ordered = Enumerable.Range(0, pairs.Count)
                    .OrderBy(i => widths[i])
                    .ThenBy(i => i)
                    .ToArray();
                string[] byWidthNames = { "wheel_fl", "wheel_fr", "wheel_bl", "wheel_br" };
                for (int k = 0; k < ordered.Length && k < byWidthNames.Length; k++)
                    wheelNameByPair[pairs[ordered[k]]] = byWidthNames[k];
                log($"  [TYRES] wheel mapping by WIDTH (front={wMin:F3}, rear={wMax:F3})");
            }
            else if (widthsDiffer)
            {
                // Widths vary but don't split 2+2 -- only pair[0] (the
                // properly-labelled wall/tread window) measures reliably.
                // The legacy (2019/2020) record order is DIAGONAL:
                // (front-left, back-right, back-left, front-right) --
                // established empirically on the 2019 Ferrari: plain
                // axle-consecutive fl,fr,bl,br placed the left side
                // correctly but swapped FR and BR. Anchor on pair[0]'s
                // width: narrow first = the fl,br,bl,fr diagonal;
                // wide first = its mirror bl,fr,fl,br.
                bool frontsFirst = widths[0] <= (wMin + wMax) / 2.0;
                string[] order = frontsFirst
                    ? new[] { "wheel_fl", "wheel_br", "wheel_bl", "wheel_fr" }
                    : new[] { "wheel_bl", "wheel_fr", "wheel_fl", "wheel_br" };
                for (int wi = 0; wi < pairs.Count; wi++)
                    wheelNameByPair[pairs[wi]] = order[wi];
                log($"  [TYRES] wheel mapping by ANCHORED DIAGONAL ORDER " +
                    $"({(frontsFirst ? "front-left" : "back-left")} first; widths not a clean 2+2 split)");
            }
            else
            {
                string[] wheelOrder = { "wheel_bl", "wheel_br", "wheel_fl", "wheel_fr" };
                for (int wi = 0; wi < pairs.Count; wi++)
                    wheelNameByPair[pairs[wi]] = wheelOrder[wi];
                log($"  [TYRES] wheel mapping by RECORD ORDER (uniform widths {wMin:F3})");
            }

            int added = 0;
            foreach (var pair in pairs)   // preserve record order
            {
                var wrapper = new Model3DGroup();
                foreach (var m in pair)
                    wrapper.Children.Add(m);
                string wheelName = wheelNameByPair[pair];
                ModelPartNameRegistry.Register(wrapper, wheelName);
                root.Children.Add(wrapper);
                added += pair.Count;
                var pb = wrapper.Bounds;
                Log($"  [TYRES] wrapper '{wheelName}': {wrapper.Children.Count} submesh(es) " +
                    $"bounds size=({pb.SizeX:F3},{pb.SizeY:F3},{pb.SizeZ:F3}) " +
                    $"origin=({pb.X:F3},{pb.Y:F3},{pb.Z:F3})");
            }
            return added;
        }

        /// <summary>
        /// Decodes the sidewall LOGO paint mask (slice 0 of the
        /// sidewall_logo_surface texarray: R = dry-compound paint,
        /// alpha = coverage) and tints it with the compound material's
        /// me_logoColour. Output is straight-BGRA where RGB is the tint
        /// and A the (thresholded, sqrt-boosted) coverage -- ready to be
        /// alpha-merged into the wall diffuse. All-out params are zero /
        /// null when anything is missing (no logo drawn).
        /// </summary>
        internal static void TryDecodeSidewallLogo(TextureCache cache,
            EmbContextReader tyreContext, Action<string> log,
            out byte[] logoPx, out int logoW, out int logoH)
        {
            logoPx = null; logoW = 0; logoH = 0;
            try
            {
                if (cache == null) return;

                string matEaid = tyreContext?.Resolve("wall") ?? "";
                if (string.IsNullOrEmpty(matEaid)) return;

                var mf = cache.GetMaterial(matEaid);
                string logoEaid = null;
                if (mf?.BindingsBySlot != null)
                {
                    foreach (var kv in mf.BindingsBySlot)
                    {
                        if (kv.Value != null && kv.Value.IndexOf("sidewall_logo_surface",
                                StringComparison.OrdinalIgnoreCase) >= 0)
                        { logoEaid = kv.Value; break; }
                    }
                }
                if (logoEaid == null) return;

                byte[] px = null;
                int w = 0, h = 0;
                string rel = EaidPathResolver.StripEaidPrefix(logoEaid);
                if (rel == null) return;
                string arrDir = Path.Combine(cache.PackageRoot,
                    Path.GetDirectoryName(rel) ?? "");
                string leaf = Path.GetFileName(rel);
                string stubPath = Path.Combine(arrDir, leaf + "!!!temp000.image");
                string payloadPath = Path.Combine(arrDir, leaf + "!!!temp001.image");
                if (!File.Exists(stubPath) || !File.Exists(payloadPath)) return;

                byte[] stub = File.ReadAllBytes(stubPath);
                if (stub.Length < 0x20) return;
                w = BitConverter.ToInt32(stub, 0x0C);
                h = BitConverter.ToInt32(stub, 0x10);
                int mips = BitConverter.ToInt32(stub, 0x18);
                int slices = BitConverter.ToInt32(stub, 0x1C);
                if (w <= 0 || h <= 0 || mips <= 0 || mips > 16 || slices < 2)
                    return;

                byte[] payload = File.ReadAllBytes(payloadPath);
                long topMipBytes = ((w + 3) / 4) * (long)((h + 3) / 4) * 16;
                if (payload.LongLength < topMipBytes) return;

                byte[] bc7 = new byte[topMipBytes];
                Array.Copy(payload, 0, bc7, 0, topMipBytes);
                px = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC7(bc7, w, h);   // BGRA32
                if (px == null) return;

                double lr = 1, lg = 1, lb = 1;
                var lc = TryReadMaterialVec3(matEaid, "me_logoColour", cache);
                if (lc.HasValue) { lr = lc.Value.r; lg = lc.Value.g; lb = lc.Value.b; }

                // Slice 0's colour channels are three SEPARATE masks:
                //   R = dry-tyre paint (band + PIRELLI lettering)
                //   G = wet/intermediate paint, B = motion-blurred.
                // Slick compounds render the RED channel, tinted with
                // me_logoColour; alpha modulates coverage.
                for (int i = 0; i < px.Length; i += 4)
                {
                    int r = px[i + 2], a = px[i + 3];
                    int cov = (a >= 255) ? r : (a * r) / 255;
                    // Threshold first (the sqrt boost amplifies mask
                    // noise into a tint wash), then push mid-intensity
                    // paint to full while keeping edges smooth.
                    cov = cov <= 10 ? 0 : (int)(Math.Sqrt(cov / 255.0) * 255.0);
                    px[i + 2] = (byte)(255 * lr);
                    px[i + 1] = (byte)(255 * lg);
                    px[i + 0] = (byte)(255 * lb);
                    px[i + 3] = (byte)cov;
                }

                logoPx = px; logoW = w; logoH = h;
                log($"  [TYRES] sidewall logo decoded ({w}x{h}, colour {lr:F2},{lg:F2},{lb:F2} " +
                    $"from {Path.GetFileNameWithoutExtension(matEaid)})");
            }
            catch (Exception ex)
            {
                logoPx = null; logoW = 0; logoH = 0;
                log("  [TYRES] logo decode failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Reads a vec3 float parameter (e.g. me_logoColour) from a
        /// .material file's structured constant buffer. Standalone
        /// implementation of the F1 25 GfxMaterialRes layout (header:
        /// descCount/bufferCount/cbufSize/preStrings; 36-byte descriptor
        /// entries at 0x20; cbuf data after the entries; shader EAID +
        /// name pool after preStrings) -- verified against the 2026
        /// Red Bull carbon and 2025 tyre sidewall materials.
        /// </summary>
        internal static (double r, double g, double b)? TryReadMaterialVec3(
            string materialEaid, string paramName, TextureCache cache)
        {
            try
            {
                if (cache == null || string.IsNullOrEmpty(materialEaid)) return null;
                string path = EaidPathResolver.ResolveDescriptor(materialEaid, cache.PackageRoot);
                if (path == null || !File.Exists(path)) return null;
                byte[] d = File.ReadAllBytes(path);
                if (d.Length < 0x30) return null;

                uint descCount = BitConverter.ToUInt32(d, 0x00);
                uint preStrings = BitConverter.ToUInt32(d, 0x0C);
                if (descCount == 0 || descCount > 256) return null;

                int blockBase = 0x20;
                int blockEnd = blockBase + (int)descCount * 36;
                int cbufStart = blockEnd;
                int shaderOff = blockEnd + (int)preStrings;
                if (shaderOff <= 0 || shaderOff >= d.Length) return null;
                int se = shaderOff;
                while (se < d.Length && d[se] != 0) se++;
                int nameBase = se + 1;

                for (int i = 0; i < (int)descCount; i++)
                {
                    int off = blockBase + i * 36;
                    if (off + 36 > d.Length) break;
                    uint nameOff = BitConverter.ToUInt32(d, off);
                    uint flag1 = BitConverter.ToUInt32(d, off + 4);
                    uint typeCode = BitConverter.ToUInt32(d, off + 8);
                    uint esize = BitConverter.ToUInt32(d, off + 0x10);
                    uint cbufOff = BitConverter.ToUInt32(d, off + 0x1C);
                    if (flag1 != 1 || typeCode != 0x30003 || esize != 12) continue;

                    int np = nameBase + (int)nameOff;
                    if (np < 0 || np >= d.Length) continue;
                    int ne = np;
                    while (ne < d.Length && d[ne] != 0) ne++;
                    string name = System.Text.Encoding.ASCII.GetString(d, np, ne - np);
                    if (!string.Equals(name, paramName, StringComparison.OrdinalIgnoreCase)) continue;

                    int vp = cbufStart + (int)cbufOff;
                    if (vp + 12 > d.Length) continue;
                    return (BitConverter.ToSingle(d, vp),
                            BitConverter.ToSingle(d, vp + 4),
                            BitConverter.ToSingle(d, vp + 8));
                }

                // FALLBACK: pre-2022 GfxMaterialRes layout -- 16-byte
                // header (count/fxNameOffset/paramNameOffset/
                // resourceNameOffset), 16-byte entries (nameOffset rel
                // to name pool, type 1 = constant buffer, size,
                // dataOffset rel to data pool base). The structured
                // 36-byte parse above returns nothing for these files
                // (classic-era tyre colours came back white without it).
                {
                    int count = BitConverter.ToInt32(d, 0);
                    int paramNameOffset = BitConverter.ToInt32(d, 8);
                    if (count > 0 && count <= 256)
                    {
                        int poolBase = 16 + 16 * count;
                        int nameBase16 = poolBase + paramNameOffset;
                        if (poolBase < d.Length && nameBase16 > 0 && nameBase16 < d.Length)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                int eo = 16 + i * 16;
                                if (eo + 16 > d.Length) break;
                                uint nameOfs = BitConverter.ToUInt32(d, eo);
                                uint type = BitConverter.ToUInt32(d, eo + 4);
                                uint size = BitConverter.ToUInt32(d, eo + 8);
                                uint dataOfs = BitConverter.ToUInt32(d, eo + 12);
                                if (type != 1 || size != 12) continue;

                                int np = nameBase16 + (int)nameOfs;
                                if (np < 0 || np >= d.Length) continue;
                                int ne = np;
                                while (ne < d.Length && d[ne] != 0) ne++;
                                string name = System.Text.Encoding.ASCII.GetString(d, np, ne - np);
                                if (!string.Equals(name, paramName, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                int vp = poolBase + (int)dataOfs;
                                if (vp < 0 || vp + 12 > d.Length) continue;
                                float fr = BitConverter.ToSingle(d, vp);
                                float fg = BitConverter.ToSingle(d, vp + 4);
                                float fb = BitConverter.ToSingle(d, vp + 8);
                                if (float.IsNaN(fr) || float.IsNaN(fg) || float.IsNaN(fb)) continue;
                                return (fr, fg, fb);
                            }
                        }
                    }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Tyre-specific material fixup: bake the alpha channel opaque
        /// (sidewall mask) and, when the mesh's UVs extend beyond [0,1]
        /// (tiling tread), set a tiling viewport of exactly one UV unit
        /// relative to the full TextureCoordinates bbox (which is what
        /// WPF stretches a 3D material brush across).
        /// </summary>
        internal static System.Windows.Media.Media3D.Material MakeTyreMaterial(
            System.Windows.Media.Media3D.Material mat, MeshGeometry3D mesh, string slotName,
            byte[] logoPx, int logoW, int logoH,
            Action<string> log)
        {
            if (!(mat is DiffuseMaterial dm) ||
                !(dm.Brush is ImageBrush ib) ||
                !(ib.ImageSource is System.Windows.Media.Imaging.BitmapSource bs))
                return mat;

            try
            {
                bool isWall = IsTyreWallSlot(slotName);
                bool isTread = !isWall && !string.IsNullOrEmpty(slotName) &&
                    (slotName.IndexOf("tread", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     slotName.Trim().StartsWith("mesh_", StringComparison.OrdinalIgnoreCase));

                // TREAD: slick_diffuse.tif stacks 8 square bands
                // vertically (static clean/sand/grass/worn, then the 4
                // motion-blurred versions). The mesh UVs address ONE band;
                // crop to the top band (clean static rubber) before
                // building the brush, else all 8 bands smear around the
                // tyre circumference.
                if (isTread && bs.PixelHeight >= bs.PixelWidth * 2)
                {
                    // 2022+ layout: square bands stacked VERTICALLY
                    // (8 on F1 25). Top band = clean static rubber.
                    int band = bs.PixelWidth;
                    bs = new System.Windows.Media.Imaging.CroppedBitmap(
                        bs, new System.Windows.Int32Rect(0, 0, bs.PixelWidth, band));
                    log($"  [TYRES] tread band crop (vertical strip): top {band}px");
                }
                else if (isTread && bs.PixelWidth >= bs.PixelHeight * 2)
                {
                    // Pre-2022 layout: two slices side by side
                    // HORIZONTALLY. Left slice = clean rubber.
                    int band = bs.PixelHeight;
                    bs = new System.Windows.Media.Imaging.CroppedBitmap(
                        bs, new System.Windows.Int32Rect(0, 0, band, bs.PixelHeight));
                    log($"  [TYRES] tread band crop (horizontal strip): left {band}px");
                }

                // Opaque bake.
                var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    bs, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                byte[] px = new byte[w * h * 4];
                conv.CopyPixels(px, w * 4, 0);
                for (int i = 3; i < px.Length; i += 4) px[i] = 255;

                // WALL: merge the tinted Pirelli logo/compound band
                // directly into the diffuse. When the logo mask is the
                // higher-resolution side, bake at ITS resolution so the
                // lettering stays crisp.
                if (isWall && logoPx != null && logoW > 0 && logoH > 0)
                {
                    if (logoW > w || logoH > h)
                    {
                        var up = new byte[logoW * logoH * 4];
                        for (int y = 0; y < logoH; y++)
                        {
                            int sy = y * h / logoH;
                            for (int x = 0; x < logoW; x++)
                            {
                                int si = (sy * w + x * w / logoW) * 4;
                                int di = (y * logoW + x) * 4;
                                up[di] = px[si]; up[di + 1] = px[si + 1];
                                up[di + 2] = px[si + 2]; up[di + 3] = 255;
                            }
                        }
                        px = up; w = logoW; h = logoH;
                    }
                    for (int y = 0; y < h; y++)
                    {
                        int ly = y * logoH / h;
                        for (int x = 0; x < w; x++)
                        {
                            int li = (ly * logoW + x * logoW / w) * 4;
                            int cov = logoPx[li + 3];
                            if (cov == 0) continue;
                            int di = (y * w + x) * 4;
                            px[di] = (byte)((logoPx[li] * cov + px[di] * (255 - cov)) / 255);
                            px[di + 1] = (byte)((logoPx[li + 1] * cov + px[di + 1] * (255 - cov)) / 255);
                            px[di + 2] = (byte)((logoPx[li + 2] * cov + px[di + 2] * (255 - cov)) / 255);
                        }
                    }
                    log("  [TYRES] sidewall logo merged into wall diffuse " +
                        $"({w}x{h})");
                }

                var opaque = System.Windows.Media.Imaging.BitmapSource.Create(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
                opaque.Freeze();

                var nb = new ImageBrush(opaque)
                {
                    Stretch = ib.Stretch,
                    TileMode = ib.TileMode,
                    ViewportUnits = ib.ViewportUnits,
                    Viewport = ib.Viewport,
                    Opacity = 1.0,
                };

                // Tiling correction from the FULL TextureCoordinates bbox.
                if (mesh?.TextureCoordinates != null && mesh.TextureCoordinates.Count > 0)
                {
                    double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
                    double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
                    foreach (var p in mesh.TextureCoordinates)
                    {
                        if (p.X < uMin) uMin = p.X; if (p.X > uMax) uMax = p.X;
                        if (p.Y < vMin) vMin = p.Y; if (p.Y > vMax) vMax = p.Y;
                    }
                    bool outOfRange = uMin < -0.05 || uMax > 1.05 || vMin < -0.05 || vMax > 1.05;
                    double spanU = uMax - uMin, spanV = vMax - vMin;
                    if (outOfRange && spanU > 1e-6 && spanV > 1e-6)
                    {
                        nb.TileMode = TileMode.Tile;
                        nb.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
                        nb.Viewport = new System.Windows.Rect(
                            -uMin / spanU, -vMin / spanV, 1.0 / spanU, 1.0 / spanV);
                        log($"  [TYRES] tiling brush: uv bbox=[{uMin:F2},{uMax:F2}]x[{vMin:F2},{vMax:F2}]");
                    }
                }

                nb.Freeze();
                var outMat = new DiffuseMaterial(nb);
                outMat.Freeze();
                return outMat;
            }
            catch
            {
                return mat;
            }
        }

        private static (Model3DGroup group, int texturedCount) LoadOneMeshFile(
            string meshTemp000Path, out int partSlot)
        {
            // v17.4: part slot (PARTINFO byte 0) of this descriptor's
            // vertices, or -1 when the descriptor doesn't declare one.
            partSlot = -1;

            if (!File.Exists(meshTemp000Path))
            {
                Log("ERROR: Mesh file not found.");
                return (null, 0);
            }

            string folder = Path.GetDirectoryName(meshTemp000Path)!;
            string meshFileName = Path.GetFileName(meshTemp000Path);

            string teamPrefix = meshFileName;
            int exclIndex = meshFileName.IndexOf("!!!", StringComparison.Ordinal);
            if (exclIndex > 0)
                teamPrefix = meshFileName.Substring(0, exclIndex);

            byte[] meshBytes = File.ReadAllBytes(meshTemp000Path);

            // v17.3: the shared TYRES descriptor ("tyres!!!temp000.idf?lod=N&mesh")
            // loads exactly as it did before v17.1. AddTyreWheelWrappers,
            // MakeTyreMaterial and the sidewall logo brush were tuned against
            // the original decode (positional stream slots, full 0..maxIndex
            // vertex arrays, original header handling), so every v17.1
            // descriptor change below is bypassed for this file.
            bool legacyTyrePath = meshFileName.StartsWith("tyres!!!", StringComparison.OrdinalIgnoreCase);

            if (IsAllZero(meshBytes))
            {
                Log("  All-zero placeholder -- this LOD has no geometry for this part.");
                return (null, 0);
            }

            // v17.1: a descriptor with zero submeshes is a legitimate empty
            // part at this LOD (18 of them in the F1 25 Ferrari), not an
            // error. Check it before the size gate, since these records are
            // shorter than a descriptor with at least one submesh block.
            if (!legacyTyrePath && meshBytes.Length >= 8 && ReadU32(meshBytes, 0x00) == 0)
            {
                Log("  Empty descriptor (submeshCount=0) -- this LOD has no geometry for this part.");
                return (null, 0);
            }

            if (meshBytes.Length < 0xA4)
            {
                Log($"  ERROR: Mesh record too short ({meshBytes.Length} bytes).");
                return (null, 0);
            }

            // ---- File-level header ----
            uint submeshCount = ReadU32(meshBytes, 0x00);
            uint streamCount = ReadU32(meshBytes, 0x04);
            uint eaidOffsetM76 = ReadU32(meshBytes, 0x14);

            if (legacyTyrePath ? (submeshCount == 0 || submeshCount > 32) : submeshCount > 256)
            {
                Log($"  ERROR: implausible submeshCount={submeshCount}.");
                return (null, 0);
            }

            // v17.1: the header offset (+0x14, relative to the 0x4C header)
            // is authoritative. The old code replaced it with
            // 0x4C + submeshCount*88 whenever the two disagreed, which is
            // wrong for descriptors that carry extra tables between the
            // submesh blocks and the name pool (F1 25 lod=1 root mesh:
            // header says 9924, the computed value is 1584). The computed
            // value is kept as a fallback when the header offset does not
            // land on a stream name.
            int calcEaidStart = 0x4C + (int)submeshCount * 88;
            int eaidStart = (int)eaidOffsetM76 + 76;
            if (legacyTyrePath)
            {
                // Original behaviour: always the computed offset.
                if (eaidStart != calcEaidStart)
                    eaidStart = calcEaidStart;
            }
            else if (eaidStart != calcEaidStart && !LooksLikeStreamName(meshBytes, eaidStart))
            {
                Log($"  NOTE: header name offset 0x{eaidStart:X} does not hold a stream name; " +
                    $"using computed offset 0x{calcEaidStart:X}.");
                eaidStart = calcEaidStart;
            }

            if (eaidStart < 0 || eaidStart >= meshBytes.Length)
            {
                Log($"  ERROR: EAID region (0x{eaidStart:X}) out of file bounds.");
                return (null, 0);
            }

            // HELMET FIX: consolidated whole-asset descriptors (helmet IDFs,
            // possibly other character kits) use larger per-submesh blocks
            // than per-part car descriptors -- the "88 bytes per submesh"
            // assumption embedded in calcEaidStart lands in the middle of
            // those blocks, and the descriptor's own header offset can point
            // at a SECOND region (IB + submesh labels) that passes the
            // LooksLikeStreamName check via its "ib=" substring without
            // being the VB EAID pool. When the chosen offset doesn't begin
            // with the canonical "eaid://" ASCII prefix, scan forward to
            // find it.
            //
            // For car descriptors the chosen offset already lands on
            // "eaid://" exactly, so this scan is a no-op and behaviour is
            // unchanged.
            if (!MatchesAscii(meshBytes, eaidStart, "eaid://"))
            {
                int scan = IndexOfAscii(meshBytes, eaidStart, "eaid://");
                if (scan > eaidStart && scan < meshBytes.Length)
                {
                    Log($"  EAID region: offset 0x{eaidStart:X} did not begin with 'eaid://'; scanned forward to 0x{scan:X}.");
                    eaidStart = scan;
                }
            }

            var eaids = ReadNullTerminatedStrings(meshBytes, eaidStart, (int)streamCount + 1);
            if (eaids.Count < (int)streamCount + 1)
            {
                Log($"  ERROR: Only {eaids.Count} EAID(s) read; expected {streamCount + 1}.");
                return (null, 0);
            }

            Log($"  submeshCount={submeshCount}, streamCount={streamCount}");

            var vbufs = new List<VertexBufferInfo>();

            // 2020 ADDENDUM: track the safetycell mirror-glass position
            // buffer (the EAID-named "vb=11" stream) so the mirror reroute
            // below has a concrete VB index to redirect to. In F1 2020 the
            // mirror_left / mirror_right submeshes inside the safetycell
            // pool reference vb=11 (stride 40, FVF 0x27, 156 verts in the
            // McLaren ERP) via a single VB-info-block selector in their
            // mesh header, but the slot-derived posIdx for these one-VB
            // submeshes lands on an unrelated stride-8 packed-normals
            // buffer. The reroute only fires when this index is set, so on
            // pools that don't have an EAID-named "vb=11" stream
            // (everything outside safetycell) the addendum is a no-op.
            int idxVb11 = -1;

            for (int i = 0; i < (int)streamCount; i++)
            {
                string suffix = BuildSuffixFromName(eaids[i], teamPrefix);
                var vInfo = LoadBufVertex(folder, teamPrefix, suffix);
                vInfo.Name = eaids[i];
                vbufs.Add(vInfo);

                if ((eaids[i] != null &&
                     eaids[i].IndexOf("vb=11", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (suffix != null &&
                     suffix.IndexOf("vb=11", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    idxVb11 = i;
                }
            }

            // FIX 20e: log all VB formats so we can verify slot assignments
            for (int i = 0; i < vbufs.Count; i++)
            {
                var b = vbufs[i];
                Log($"    stream[{i}] Fvf={b.Fvf} stride={b.Stride} count={b.Count}");
            }

            // v17.1: prefer the header's IB-name offset (+0x18); fall back to
            // "the string after the last VB name" when it doesn't validate.
            string ibEaid = eaids[eaids.Count - 1];
            if (!legacyTyrePath)
            {
                int ibNamePos = 76 + (int)ReadU32(meshBytes, 0x18);
                if (ibNamePos != eaidStart && LooksLikeStreamName(meshBytes, ibNamePos))
                {
                    var ibNames = ReadNullTerminatedStrings(meshBytes, ibNamePos, 1);
                    if (ibNames.Count == 1 &&
                        ibNames[0].IndexOf("ib=", StringComparison.OrdinalIgnoreCase) >= 0)
                        ibEaid = ibNames[0];
                }
            }
            var ibuf = LoadBufIndex(folder, teamPrefix, BuildSuffixFromName(ibEaid, teamPrefix));
            ibuf.Name = ibEaid;

            // Material-slot / label names per submesh.
            //
            // FIX 2: the format stores these in a label pool at file offset
            // (MeshHeaderSize + h[7]), and each 88-byte submesh block carries
            // a byte offset `nameOfs` at +0x34 that indexes into that pool.
            // The *previous* code read N null-terminated strings sequentially
            // starting right after the EAID list; that happens to work on
            // non-deduped pools (the McLaren 2021 case) but silently breaks
            // whenever two submeshes share the same label � the dedupe puts a
            // single string in the pool, both submeshes point at the same
            // offset via nameOfs, and the sequential reader skips past the
            // first null-terminator to read garbage for the second submesh.
            //
            // The exporter's ParseDescriptor uses the indexed form and is
            // proof of the correct layout, so we mirror it here.
            //
            // Sequential reads are kept as a fallback so files with h[7]==0
            // (unlikely in practice, but possible on malformed extractions)
            // still get something rather than nothing.
            const int MeshHeaderSize = 76; // 19 ints
            uint strOfs = ReadU32(meshBytes, 0x1C); // h[7]
            int labelBase = MeshHeaderSize + (int)strOfs;
            bool haveIndexedLabels =
                strOfs > 0 && labelBase > 0 && labelBase < meshBytes.Length;

            // Sequential fallback (preserved for files missing a label pool).
            int lastEaidEnd = eaidStart;
            for (int i = 0; i < eaids.Count; i++)
                lastEaidEnd += eaids[i].Length + 1;

            var sequentialSlotNames = ReadNullTerminatedStrings(meshBytes, lastEaidEnd, (int)submeshCount);

            // v17.1: vertex-element + vbRef tables (header +0x24/+0x28/+0x2C).
            // Null when the tables don't validate -> legacy slot mapping.
            var decl = legacyTyrePath
                ? null
                : DescriptorDecl.TryRead(meshBytes, (int)streamCount, vbufs);
            if (legacyTyrePath)
                Log("  tyres descriptor: original positional slot mapping and vertex decode (v17.1 changes bypassed).");
            else if (decl != null)
                Log($"  vertex-element table: {decl.Count} element(s) -- stream roles from descriptor.");
            else
                Log("  vertex-element table not usable -- using positional slot mapping.");

            // Walk all submesh blocks.
            var subMeshes = new List<SubMeshInfo>();
            for (int si = 0; si < (int)submeshCount; si++)
            {
                int blockOff = 0x4C + si * 88;
                if (blockOff + 88 > meshBytes.Length) break;

                uint firstIndex = ReadU32(meshBytes, blockOff + 0x0C);
                uint indexCount = ReadU32(meshBytes, blockOff + 0x10);
                uint baseVertex = ReadU32(meshBytes, blockOff + 0x14);

                // FIX 9: nameOfs lives at +0x30 in the 88-byte submesh
                // block, not +0x34. Verified by hex-inspecting the safety-
                // cell mesh of the McLaren 2021 package (sub_count=7, label
                // pool at file offset 0x7EF containing
                //   "body" / "generic" / "detail" / "carbon_tile_1" /
                //   "number" / "rear light" / "rainbeads"
                // at relative offsets 0/5/13/20/34/41/52). Each submesh's
                // +0x30 field holds exactly one of those values; the +0x34
                // field is zero on every submesh.
                //
                // Reading nameOfs from +0x34 made every submesh resolve to
                // the FIRST label in the pool, which on body parts happens
                // to be "body". Symptoms in the viewport:
                //   - the driver-number patch (786 indices on safetycell)
                //     was bound through mclaren_paint.material instead of
                //     mclaren_number_N.material, so the number never showed
                //   - carbon-tile, generic, detail, rear-light, and rainbeads
                //     submeshes all resolved as "body" and pulled paint
                //     textures
                //   - the rainbeads-skip-on-dry-parts guard never fired,
                //     because no submesh was ever named "rainbeads"
                uint nameOfs = ReadU32(meshBytes, blockOff + 0x30);
                string indexedLabel = null;
                if (haveIndexedLabels)
                {
                    int namePos = labelBase + (int)nameOfs;
                    if (namePos >= 0 && namePos < meshBytes.Length)
                    {
                        var tmp = ReadNullTerminatedStrings(meshBytes, namePos, 1);
                        if (tmp.Count > 0 && !string.IsNullOrEmpty(tmp[0]))
                            indexedLabel = tmp[0];
                    }
                }

                var slots = new uint[5];
                for (int k = 0; k < 5; k++)
                    slots[k] = ReadU32(meshBytes, blockOff + 0x3C + k * 4);

                if (indexCount == 0) continue;

                int posIdx = (int)slots[0] - 1;
                int nrmIdx = (int)slots[0];
                int uvIdx = (int)slots[2];   // pair-1 UV stream (fmt 35 packed)
                int uvIdx2 = (int)slots[3];   // pair-2 UV stream (fmt 29, 4 B/v)

                // FIX 33: slots[1] points at a stream that the original
                // loader never read. On F1 22 McLaren this slot consistently
                // maps to an Fvf=35 stride=8 UV-pair stream that carries
                // authored decal UVs for a subset of number submeshes whose
                // "canonical" vb=4 entries are vestigial/degenerate (and
                // whose vb=3[4,6] also misses the digit). Capturing it as a
                // third UV candidate lets the digit-coverage picker discover
                // those authored UVs instead of defaulting to a pixel-strip
                // sample that misses the atlas. Kept as a "candidate" only
                // -- the picker scores all candidates and chooses the best,
                // so streams that turn out to carry tangent/normal/other
                // data automatically score 0 hits and are skipped.
                int uvIdx3 = (int)slots[1];

                // v17.1: descriptor-driven stream roles.
                //
                // The positional mapping above reads vbRefs from +0x3C (the
                // SECOND vbRef; the first sits at +0x38) and assumes a fixed
                // stream order. On F1 25 that order is
                //   POS, NRM, TAN, UV(fmt35: uv0+uv1)[, UV(fmt29)], PARTINFO
                // so slots[1] is the TANGENT stream (FIX 33 was scoring
                // tangents as a UV candidate) and, on 5-stream pools,
                // slots[3] is the PARTINFO stream (decoded as a decal UV,
                // which collapsed every decal UV onto one point on wheel,
                // disc, detail, generic, lcd, flag, digi revs, rear light,
                // steering wheel and mirror submeshes). The buffer header
                // "Fvf" is a usage field -- NRM, TAN and UV0 all carry 0x23
                // -- so it cannot tell those streams apart; the element
                // table can.
                bool declDriven = false;
                bool uvPair2InPrimary = false;
                int tanIdx = -1, partInfoIdx = -1;
                string declWhy = null;
                if (decl != null &&
                    decl.TryResolve(meshBytes, blockOff, vbufs, out var roles, out declWhy))
                {
                    declDriven = true;
                    posIdx = roles.Position;
                    nrmIdx = roles.Normal;
                    uvIdx = roles.Uv;
                    uvIdx2 = roles.Uv2;
                    uvIdx3 = roles.UvExtra;
                    tanIdx = roles.Tangent;
                    partInfoIdx = roles.PartInfo;
                    uvPair2InPrimary = roles.UvHasPair2 && roles.Uv2 < 0;
                    foreach (var kv in roles.Formats)
                    {
                        var fb = vbufs[kv.Key];
                        if (fb.DeclFormat == 0) fb.DeclFormat = kv.Value;
                        else if (fb.DeclFormat != kv.Value)
                            Log($"  NOTE: vb{kv.Key} declared as format {fb.DeclFormat} and {kv.Value} " +
                                "by different submeshes; keeping the first.");
                    }
                }
                else if (decl != null)
                {
                    Log($"  submesh[{si}] element table rejected ({declWhy}) -- positional slots.");
                }

                // FIX 20e: log slots and VB properties for debugging
                {
                    string _vb(int idx)
                    {
                        if (idx < 0 || idx >= vbufs.Count) return "NONE";
                        var b = vbufs[idx];
                        return $"vb{idx}(Fvf={b.Fvf},stride={b.Stride},n={b.Count})";
                    }
                    Log($"  submesh[{si}] '{indexedLabel ?? "?"}' slots=[{slots[0]},{slots[1]},{slots[2]},{slots[3]},{slots[4]}] " +
                        $"pos={_vb(posIdx)} uv1={_vb(uvIdx)} uv2={_vb(uvIdx2)} uv3={_vb(uvIdx3)}" +
                        (declDriven
                            ? $" [decl] nrm={_vb(nrmIdx)} tan={_vb(tanIdx)} part={_vb(partInfoIdx)}" +
                              (uvPair2InPrimary ? " uv2=uv1[4,6]" : "")
                            : " [positional]"));
                }

                // 2020 ADDENDUM: mirror_left / mirror_right reroute to vb=11.
                //
                // In F1 2020 the mirror glass got folded into the safetycell
                // mesh as two extra material-distinct submeshes. Their
                // slot[0]-derived posIdx in the McLaren 2020 ERP doesn't
                // resolve to the actual mirror-glass position buffer --
                // depending on how the slot value parses, posIdx ends up
                // either out of range (silently skipped by the guard below)
                // or pointing at an unrelated stride-8 packed-normals VB.
                //
                // The fix routes mirror submeshes explicitly to vb=11, the
                // dedicated mirror-glass position buffer (stride 40, FVF
                // 0x27, 156 verts in the McLaren ERP). Verified empirically:
                // vb=11[0..19] bounds (X 0.484..0.626, Y 0.396..0.447,
                // Z 0.664..0.718) ctr (+0.555, 0.422, 0.691) match the
                // mirror_left submesh sphere center (+0.554, 0.429, 0.691)
                // radius 0.080; vb=11[20..39] mirror those across X for
                // mirror_right. Position is at byte offset 0 of each 40-byte
                // vertex.
                //
                // nrmIdx is set to -1: vb=11 has FVF 0x27 which
                // DecodeNormalStream doesn't handle (it only reads FVF 35
                // and 29), so DecodeNormalStream would return null anyway
                // for vb=11 -- but explicitly clearing nrmIdx prevents the
                // slot-derived value from pointing at a stride-8 packed-
                // normals VB whose first 40 entries belong to an unrelated
                // submesh. Mirror glass is flat, so face-computed normals
                // produce a visually correct surface.
                //
                // Pool-narrow: idxVb11 is only set when the pool has an
                // EAID-named "vb=11" stream, which for the McLaren 2020 ERP
                // is the safetycell pool only. Pools without a vb=11 leave
                // idxVb11 = -1 and this gate is a no-op, so older ERPs
                // where mirror_* are their own pools are unaffected.
                // Reroute ONLY when the slot-derived posIdx is genuinely
                // unusable (out of range, or not a stride-12 position
                // buffer -- the 2020 failure modes above). Modern ERPs
                // (2021+) give mirror_* a valid FVF-39 stride-12 position
                // stream of their own; hijacking it to vb=11 mangles the
                // mirror geometry (seen on the 2026 Ferrari wing mirrors).
                if (!declDriven &&
                    (SlotIs(indexedLabel, "mirror_left") || SlotIs(indexedLabel, "mirror_right")) &&
                    idxVb11 >= 0 && idxVb11 < vbufs.Count &&
                    posIdx != idxVb11 &&
                    (posIdx < 0 || posIdx >= vbufs.Count ||
                     vbufs[posIdx].Stride != 12))
                {
                    Log($"  [mirror-glass-reroute] '{indexedLabel}': slot-derived posIdx={posIdx} " +
                        $"-> vb=11 ({idxVb11}) (2020 mirror-glass routing)");
                    posIdx = idxVb11;
                    nrmIdx = -1;
                }

                if (posIdx < 0 || posIdx >= vbufs.Count) continue;
                if (nrmIdx >= vbufs.Count) nrmIdx = -1;
                if (uvIdx >= vbufs.Count) uvIdx = -1;
                if (uvIdx == posIdx) uvIdx = -1;
                if (uvIdx2 >= vbufs.Count) uvIdx2 = -1;
                if (uvIdx2 == posIdx || uvIdx2 == uvIdx) uvIdx2 = -1;

                // FIX 33: accept uv3 only if it's a valid stream index that
                // isn't already serving as position/uv1/uv2. Duplicates are
                // either pointing at the same buffer we already have (no
                // new information) or are a legacy auto-sharing pattern
                // we'd be double-scoring on. In both cases we reject to
                // keep the candidate set unambiguous.
                if (uvIdx3 < 0 || uvIdx3 >= vbufs.Count) uvIdx3 = -1;
                if (uvIdx3 == posIdx || uvIdx3 == uvIdx || uvIdx3 == uvIdx2) uvIdx3 = -1;

                // FIX 2/3: prefer the indexed label; fall back to the sequential
                // reader only when the indexed read returned nothing. In the
                // Codemasters authoring convention the same string serves as
                // both the geometric submesh label and the material slot name,
                // so we assign it to both fields. If a future format splits
                // them, the two SubMeshInfo fields are ready for it.
                string slotName = indexedLabel;
                if (string.IsNullOrEmpty(slotName))
                {
                    slotName = (si < sequentialSlotNames.Count && !string.IsNullOrEmpty(sequentialSlotNames[si]))
                        ? sequentialSlotNames[si]
                        : $"submesh{si}";
                }

                subMeshes.Add(new SubMeshInfo
                {
                    Name = slotName,
                    IOffset = (int)firstIndex,
                    IndexCount = (int)indexCount,
                    VOffset = (int)baseVertex,
                    VBufferIndex = posIdx,
                    NrmBufferIndex = nrmIdx,
                    UvBufferIndex = uvIdx,
                    UvBufferIndex2 = uvIdx2,
                    UvBufferIndex3 = uvIdx3,
                    IBufferIndex = 0,
                    DescriptorHint = meshFileName,
                    MaterialSlot = slotName,
                    SubmeshIndex = si,

                    // v17.1: +0x18..+0x27 = bounding sphere (see SubMeshInfo).
                    SphereCenterX = ReadF32(meshBytes, blockOff + 0x18),
                    SphereCenterY = ReadF32(meshBytes, blockOff + 0x1C),
                    SphereCenterZ = ReadF32(meshBytes, blockOff + 0x20),
                    SphereRadius = ReadF32(meshBytes, blockOff + 0x24),

                    DeclDriven = declDriven,
                    LegacyVertexWindow = legacyTyrePath,
                    UvPair2InPrimary = uvPair2InPrimary,
                    TangentBufferIndex = tanIdx,
                    PartInfoBufferIndex = partInfoIdx,
                });
            }

            if (subMeshes.Count == 0)
            {
                Log("  No usable submeshes -- nothing to draw.");
                return (null, 0);
            }

            partSlot = TryReadPartSlot(vbufs, ibuf, subMeshes);

            return BuildGeometryFromSubMeshes(vbufs, new List<IndexBufferInfo> { ibuf }, subMeshes);
        }

        // =====================================================================
        // SHARED GEOMETRY BUILDER
        // =====================================================================

        private static (Model3DGroup group, int texturedCount) BuildGeometryFromSubMeshes(
            List<VertexBufferInfo> vbufs,
            List<IndexBufferInfo> ibufs,
            List<SubMeshInfo> subMeshes)
        {
            var group = new Model3DGroup();
            int texturedCount = 0;

            string descriptorPartName = "part";
            if (subMeshes != null && subMeshes.Count > 0)
                descriptorPartName = ResolveDescriptorPartName(subMeshes[0].DescriptorHint);
            if (string.IsNullOrWhiteSpace(descriptorPartName))
                descriptorPartName = "part";

            // Whether this mesh part is the wet-weather variant of a body
            // panel. The 2017/18 classic loader exposed these as separate
            // mesh files like "lod=0&safetycell&water&mesh", which our
            // ResolveDescriptorPartName joins with an underscore so they
            // become "safetycell_water" / "frontwing_fl_01_water" etc.
            // 2021 packages don't ship _water variants, so this is always
            // false there.
            bool isWaterPart = !string.IsNullOrEmpty(descriptorPartName) &&
                descriptorPartName.IndexOf("_water", StringComparison.OrdinalIgnoreCase) >= 0;

            foreach (var sm in subMeshes)
            {
                // v12.10 (additive filter): rainbeads NEVER loads on ANY
                // model. Single-line filter at the top of the loop;
                // the original "skip on dry parts" gate below remains
                // intact and untouched -- it just never fires anymore
                // because this filter catches every rainbeads-slot
                // submesh first.
                if (SlotIs(sm.MaterialSlot, "rainbeads"))
                    continue;

                // Skip the wet-weather "rainbeads" overlay submesh on dry
                // body parts. The rainbeads slot exists on every body and
                // most carbon panels in 2021 (20 of them on McLaren) and
                // is meant to be enabled only when the wetness intensity
                // is non-zero. Codemasters' shader fades it in with a
                // scalar parameter the static preview can't read; rendering
                // it unconditionally produces opaque grey droplet patterns
                // on every panel because the rainbeads BC1 texture has no
                // alpha channel and tiles 16x across the surface.
                //
                // The classic 2017/18 packages handle this by emitting
                // separate "<part>&water&mesh" descriptors for the wet
                // variants -- those geometries DO get rainbeads. So the
                // rule is: rainbeads belongs only on parts whose name
                // ends in "_water" (post-resolve). Everything else skips.
                if (SlotIs(sm.MaterialSlot, "rainbeads") &&
                    !isWaterPart)
                {
                    continue;
                }

                if (sm.IBufferIndex < 0 || sm.IBufferIndex >= ibufs.Count) continue;

                var ib = ibufs[sm.IBufferIndex];
                if (!File.Exists(ib.DataPath)) continue;
                byte[] iData = File.ReadAllBytes(ib.DataPath);

                long iByteOffset = (long)sm.IOffset * ib.IndexSize;
                long iByteCount = (long)sm.IndexCount * ib.IndexSize;
                if (iByteOffset + iByteCount > iData.LongLength) continue;

                var rawIndices = new int[sm.IndexCount];
                int maxIndex = 0;
                for (int k = 0; k < sm.IndexCount; k++)
                {
                    long basePos = iByteOffset + (long)k * ib.IndexSize;
                    int idx = (ib.IndexSize == 2)
                        ? BitConverter.ToUInt16(iData, (int)basePos)
                        : BitConverter.ToInt32(iData, (int)basePos);
                    rawIndices[k] = idx;
                    if (idx > maxIndex) maxIndex = idx;
                }

                // v17.1: tight vertex window.
                //
                // Pool-absolute indices mean a submesh whose vertices sit at
                // 26776..26784 used to allocate (and decode) 26,785 vertices
                // to draw 8. Across the F1 25 Ferrari LOD0 parts that was
                // 4,084,025 vertices for 179,323 used, and WPF derives
                // MeshGeometry3D.Bounds from every Position, so foreign data
                // leaked into bounds-driven logic (overlay depth ratio,
                // zoom-extents). Each submesh's used indices form one
                // contiguous run, so rebasing by the minimum index removes
                // the waste exactly. Every downstream reader addresses
                // vertices as sm.VOffset + rawIndices[i], so shifting both
                // keeps every decoder, overlay and debug dump consistent.
                if (sm.IndexCount > 0 && !sm.LegacyVertexWindow)
                {
                    int minIndex = int.MaxValue;
                    for (int k = 0; k < rawIndices.Length; k++)
                        if (rawIndices[k] < minIndex) minIndex = rawIndices[k];
                    if (minIndex > 0 && minIndex != int.MaxValue)
                    {
                        for (int k = 0; k < rawIndices.Length; k++)
                            rawIndices[k] -= minIndex;
                        maxIndex -= minIndex;
                        sm.VOffset += minIndex;
                    }
                }

                int vIndex = sm.VBufferIndex;
                if (vIndex < 0 || vIndex >= vbufs.Count)
                {
                    vIndex = AutoPickPositionVb(vbufs, sm.VOffset, maxIndex);
                    if (vIndex < 0) continue;
                }

                var vb = vbufs[vIndex];
                if (!File.Exists(vb.DataPath)) continue;
                byte[] vData = File.ReadAllBytes(vb.DataPath);
                long vStartByte = (long)sm.VOffset * vb.Stride;
                long neededBytes = vStartByte + (long)(maxIndex + 1) * vb.Stride;
                if (neededBytes > vData.LongLength) continue;

                var positions = new Point3D[maxIndex + 1];
                for (int vi = 0; vi <= maxIndex; vi++)
                {
                    long off = vStartByte + (long)vi * vb.Stride;
                    positions[vi] = new Point3D(
                        BitConverter.ToSingle(vData, (int)off + 0),
                        BitConverter.ToSingle(vData, (int)off + 4),
                        BitConverter.ToSingle(vData, (int)off + 8));
                }

                Vector3D[] normals = DecodeNormalStream(vbufs, sm.NrmBufferIndex, sm.VOffset, maxIndex);
                if (normals == null && vb.Stride >= 24)
                {
                    normals = new Vector3D[maxIndex + 1];
                    for (int vi = 0; vi <= maxIndex; vi++)
                    {
                        long off = vStartByte + (long)vi * vb.Stride;
                        if (off + 24 <= vData.LongLength)
                            normals[vi] = new Vector3D(
                                BitConverter.ToSingle(vData, (int)off + 12),
                                BitConverter.ToSingle(vData, (int)off + 16),
                                BitConverter.ToSingle(vData, (int)off + 20));
                    }
                }

                // === Pick material EARLY so we know which UV pair to decode ===
                //
                // EGO vb=3 stores TWO UV sets: pair 1 is the primary
                // (paint / carbon-weave / detail), pair 2 is the
                // logo/decal atlas UV. The shader picks based on which
                // sampler the material uses, so we mirror that here:
                // anything resolving through me_logoMap reads pair 2,
                // everything else reads pair 1.
                string materialEaid = null;
                if (_activeContext != null)
                    materialEaid = _activeContext.Resolve(sm.MaterialSlot);

                TextureCache.PreviewResolution? earlyPick = null;
                if (_textureCache != null && _activeContext != null && !string.IsNullOrEmpty(materialEaid))
                {
                    // Pass the mesh slot name through. For paint and carbon
                    // shader families this is the only way to distinguish
                    // a "number" submesh (driver decal as base) from a "body"
                    // submesh (paint atlas as base, decal as overlay) -- both
                    // use mclaren_paint.material/mclaren_number_N.material
                    // with the same shader.
                    earlyPick = _textureCache.ResolveDiffuseForMaterial(materialEaid, sm.MaterialSlot);
                }

                // Decal-class samplers read from UV pair 2 (the atlas/decal
                // UV) instead of pair 1 (paint/tile UV). The 3DSimEd plugin
                // binary lists exactly these decal-class slots:
                //   me_logoMap     -> driver number, team logo
                //   me_sponsorMap  -> sponsor stickers
                //
                // For "number" / "carbon number" mesh slots, the resolver has
                // already promoted me_logoMap to the BASE layer (its texture is
                // mclaren_driver_N.tif, the actual visible number). The
                // geometry is a small panel whose decal-style UVs are authored
                // into the separate fmt-29 vb=4 buffer at slots[3] -- captured
                // in sm.UvBufferIndex2 -- not the upper FP16 pair of vb=3.
                //
                // Verified against safetycell.number (786 indices, baseV=107194):
                //   vb=3 UV span: 0.022 x 0.010   (single-pixel paint atlas
                //                                  reference, NOT the number)
                //   vb=4 UV span: 0.302 x 0.647   (proper rectangle on the
                //                                  driver-N atlas where the
                //                                  number geometry samples it)
                //
                // Sampling vb=3 produced a uniform dark colour from the paint
                // atlas's transparent corner (visible image: blurry "L" /
                // "3" floating in a transparent panel cutout). Sampling vb=4
                // puts the actual number where the geometry expects it.
                bool useSecondaryUvPair = false;
                bool isNumberSubmesh = IsNumberSlot(sm.Name);
                // WHAT DOES THE NUMBER BASE BIND? 2023+ is per-TEAM:
                // Ferrari-style number materials bind the DRIVER digit
                // atlas as albedo (base IS the digit pass, pair-2 UVs,
                // no overlay); Lotus/Alpine-style bind the PAINT atlas
                // (2021-style: base renders body paint on primary UVs
                // and the digit arrives via the me_logoMap overlay).
                // Forcing pair-2 onto a paint-bound base painted the
                // number panel with a paint-atlas patch AND suppressed
                // the overlay -- "uses its paint texture as its number".
                bool numberBaseIsDigitAtlas = isNumberSubmesh &&
                    earlyPick.HasValue &&
                    !string.IsNullOrEmpty(earlyPick.Value.TextureEaid) &&
                    earlyPick.Value.TextureEaid.IndexOf("_driver_",
                        StringComparison.OrdinalIgnoreCase) >= 0;

                if (earlyPick.HasValue && !string.IsNullOrEmpty(earlyPick.Value.PickedSlot))
                {
                    string s = earlyPick.Value.PickedSlot;
                    bool slotMentionsLogo =
                        s.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        s.IndexOf("sponsor", StringComparison.OrdinalIgnoreCase) >= 0;
                    // Ported from the UniversalExporter: NUMBER submeshes
                    // also need the secondary UV pair. Their resolved base
                    // sampler is me_albedoMap (which on <team>_number_N.
                    // material binds to the driver-N digit atlas on
                    // 2023-2026 ERPs, NOT the paint atlas), so the slot
                    // name doesn't mention "logo" -- but the digit lives
                    // in a small UV-panel region authored on vb=4 (or
                    // vb=3 pair-2 as fallback), not on the body's tiled
                    // pair-1. Without this, the body UV tiled the digit
                    // into strips or landed on transparent atlas regions
                    // -- numbers invisible on 2023+ cars.
                    // CHARACTER shaders: cloth-logo geometry (suits,
                    // gloves) authors its logo diffuse on a parallel
                    // Fvf=13 stride-4 buffer captured in UvBufferIndex2,
                    // exactly like car decal channels -- the logo pick
                    // above routes it here. Character BODY shaders
                    // (cloth-skinned, helmet, skin) have NO separate logo
                    // geometry: their base diffuse is authored against
                    // UV1, so exempt them or every suit/helmet samples
                    // the wrong channel and renders untextured.
                    var fam = earlyPick.Value.Shader;
                    bool isCharacterBody =
                        (fam == MaterialSlotResolver.ShaderFamily.CharacterCloth ||
                         fam == MaterialSlotResolver.ShaderFamily.CharacterSkin ||
                         fam == MaterialSlotResolver.ShaderFamily.CharacterHelmet) &&
                        s.StartsWith("me_baseDiffuse", StringComparison.OrdinalIgnoreCase);
                    if ((slotMentionsLogo || numberBaseIsDigitAtlas) && !isCharacterBody)
                    {
                        useSecondaryUvPair = true;
                    }
                }

                // FIX 6: UV range is now a per-submesh local, not a static field.
                var uvRange = (uMin: 0.0, uMax: 0.0, vMin: 0.0, vMax: 0.0);

                bool overrideActive = numberBaseIsDigitAtlas &&
                    NumberUvOverride != NumberUvSource.Auto;
                System.Windows.Point[] uvs;
                if (overrideActive)
                {
                    switch (NumberUvOverride)
                    {
                        case NumberUvSource.Primary:
                            uvs = DecodeUvStream(vbufs, sm.UvBufferIndex,
                                sm.VOffset, maxIndex,
                                useSecondaryUvPair: false, out uvRange);
                            Log($"  NUMBER UV OVERRIDE: '{sm.Name}' forced to PRIMARY (vb=3 pair-1).");
                            break;
                        case NumberUvSource.Pair2:
                            uvs = DecodeUvStream(vbufs, sm.UvBufferIndex,
                                sm.VOffset, maxIndex,
                                useSecondaryUvPair: true, out uvRange);
                            Log($"  NUMBER UV OVERRIDE: '{sm.Name}' forced to PAIR-2 (vb=3 pair-2).");
                            break;
                        case NumberUvSource.Secondary:
                            if (sm.UvBufferIndex2 >= 0 && sm.UvBufferIndex2 < vbufs.Count)
                            {
                                uvs = DecodeUvStream(vbufs, sm.UvBufferIndex2,
                                    sm.VOffset, maxIndex,
                                    useSecondaryUvPair: false, out uvRange);
                                Log($"  NUMBER UV OVERRIDE: '{sm.Name}' forced to SECONDARY (vb=4).");
                            }
                            else
                            {
                                uvs = DecodeUvStream(vbufs, sm.UvBufferIndex,
                                    sm.VOffset, maxIndex,
                                    useSecondaryUvPair: false, out uvRange);
                                Log($"  NUMBER UV OVERRIDE: '{sm.Name}' SECONDARY requested but vb=4 missing; using PRIMARY.");
                            }
                            break;
                        default:
                            uvs = null;
                            break;
                    }
                }
                else if (useSecondaryUvPair && sm.UvBufferIndex2 >= 0 && sm.UvBufferIndex2 < vbufs.Count)
                {
                    uvs = DecodeUvStream(
                        vbufs, sm.UvBufferIndex2, sm.VOffset, maxIndex,
                        useSecondaryUvPair: false, out uvRange);

                    // FIX 14: number / logo submeshes can land in a near-
                    // degenerate UV bbox on vb=4 even when the buffer is
                    // present and decodable -- the geometry was authored to
                    // sample its decal from a different UV channel and the
                    // vb=4 entries for those verts are vestigial (a few
                    // adjacent FP16 values pointing at a pixel-strip on the
                    // driver atlas). Verified concrete failure on 2021
                    // McLaren sidepod_fl number (22 verts, baseV=107194):
                    //   vb=4 USED-only bbox: U=[0.573, 0.631] V=[0.849, 0.877]
                    //                        = 5x1 pixels on the 64x32
                    //                        driver_1.tif atlas, in a
                    //                        100% transparent area.
                    //   vb=3 offsets 4,6 USED bbox: U=[0.049, 0.457]
                    //                               V=[0.560, 1.007]
                    //                       = 27x15 pixels enclosing the
                    //                       bold "1" digit at (15-20, 12-27).
                    // The same submesh's geometry exists on the safetycell
                    // (191 verts) where vb=4 lands correctly on the digit
                    // and vb=3[4,6] is tangent-noise -- so neither channel
                    // is universally right. The discriminator: when vb=4's
                    // USED-vert bbox is sub-degenerate (< ~3% in either
                    // axis, i.e. less than ~2 pixels on a 64-wide atlas)
                    // it cannot possibly be enclosing a readable decal, so
                    // try vb=3 offsets 4,6 instead. If that channel also
                    // looks degenerate or wraps the full atlas, we keep
                    // vb=4 (the sub-pixel sample is at least benign --
                    // it'll render as a flat colour).
                    //
                    // The bbox is computed over rawIndices (USED verts only)
                    // for the same reason as Fix 13: the shared vb=4 buffer
                    // contains UVs for every vertex referenced by any
                    // submesh of the part, and we don't want neighbours'
                    // values to shift our discriminator.
                    if (uvs != null && rawIndices != null && rawIndices.Length > 0)
                    {
                        double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
                        double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
                        for (int ri = 0; ri < rawIndices.Length; ri++)
                        {
                            int vi = rawIndices[ri];
                            if (vi < 0 || vi >= uvs.Length) continue;
                            double u = uvs[vi].X, v = uvs[vi].Y;
                            if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                            if (v < vMin) vMin = v; if (v > vMax) vMax = v;
                        }
                        const double kDegenerateSpan = 0.07;  // ~4 pixels on 64-wide
                        bool degenerate = !double.IsPositiveInfinity(uMin) &&
                                          ((uMax - uMin) < kDegenerateSpan ||
                                           (vMax - vMin) < kDegenerateSpan);

                        // Per-triangle degeneracy: the union bbox can look
                        // healthy while every triangle is point-parked
                        // (2026 Ferrari safetycell number: 60 tris on one
                        // digit texel + 88 in a sliver). NUMBER submeshes
                        // only -- sponsor sheets legitimately have many
                        // tiny per-triangle sticker islands.
                        if (!degenerate && isNumberSubmesh && rawIndices.Length >= 3)
                        {
                            int degTris = 0, totTris = 0;
                            for (int t = 0; t + 2 < rawIndices.Length; t += 3)
                            {
                                double tu0 = double.PositiveInfinity, tu1 = double.NegativeInfinity;
                                double tv0 = double.PositiveInfinity, tv1 = double.NegativeInfinity;
                                bool ok = true;
                                for (int k = 0; k < 3; k++)
                                {
                                    int vi = rawIndices[t + k];
                                    if (vi < 0 || vi >= uvs.Length) { ok = false; break; }
                                    double u = uvs[vi].X, v = uvs[vi].Y;
                                    if (u < tu0) tu0 = u; if (u > tu1) tu1 = u;
                                    if (v < tv0) tv0 = v; if (v > tv1) tv1 = v;
                                }
                                if (!ok) continue;
                                totTris++;
                                if ((tu1 - tu0) < 0.02 && (tv1 - tv0) < 0.02) degTris++;
                            }
                            if (totTris > 0 && degTris >= totTris * 0.8)
                            {
                                degenerate = true;
                                Log($"  NOTE: submesh chan vb=4 is per-triangle " +
                                    $"point-parked ({degTris}/{totTris} tris degenerate) " +
                                    "despite healthy union span; treating channel as parked.");
                            }
                        }

                        if (degenerate &&
                            sm.UvBufferIndex >= 0 && sm.UvBufferIndex < vbufs.Count)
                        {
                            // Try vb=3 offsets 4,6 (the "second pair" of the
                            // primary UV buffer). On most submeshes this is
                            // tangent data, but on number/logo submeshes
                            // whose vb=4 is a vestigial pixel-strip it tends
                            // to be a real decal-atlas UV.
                            var altRange = (uMin: 0.0, uMax: 0.0, vMin: 0.0, vMax: 0.0);
                            var altUvs = DecodeUvStream(
                                vbufs, sm.UvBufferIndex, sm.VOffset, maxIndex,
                                useSecondaryUvPair: true, out altRange);
                            if (altUvs != null)
                            {
                                // Same USED-only bbox check on the candidate.
                                double aUMin = double.PositiveInfinity, aUMax = double.NegativeInfinity;
                                double aVMin = double.PositiveInfinity, aVMax = double.NegativeInfinity;
                                for (int ri = 0; ri < rawIndices.Length; ri++)
                                {
                                    int vi = rawIndices[ri];
                                    if (vi < 0 || vi >= altUvs.Length) continue;
                                    double u = altUvs[vi].X, v = altUvs[vi].Y;
                                    if (u < aUMin) aUMin = u; if (u > aUMax) aUMax = u;
                                    if (v < aVMin) aVMin = v; if (v > aVMax) aVMax = v;
                                }
                                bool altDegenerate = double.IsPositiveInfinity(aUMin) ||
                                                     (aUMax - aUMin) < kDegenerateSpan ||
                                                     (aVMax - aVMin) < kDegenerateSpan;
                                bool altFullAtlas = (aUMax - aUMin) >= 0.95 &&
                                                    (aVMax - aVMin) >= 0.95;
                                // Numbers tolerate slightly-out-of-[0,1] digit
                                // panels (2026 Ferrari: -0.126); other slots
                                // keep the strict bound.
                                double wrapTol = isNumberSubmesh ? 0.25 : 0.05;
                                bool altWraparound = aUMax > 1.0 + wrapTol || aUMin < -wrapTol ||
                                                     aVMax > 1.0 + wrapTol || aVMin < -wrapTol;

                                // For CHARACTER CLOTH-LOGO shaders the
                                // full-atlas / wraparound rejection rules
                                // don't apply: cloth-logo geometry routinely
                                // covers most of the suit / glove and its
                                // valid UV2 channel maps across the entire
                                // logo atlas, which would normally trip the
                                // full-atlas guard. The guard is car-decal
                                // logic where logos should land in a small
                                // atlas region. Relax it for cloth so
                                // body_logos can adopt vb3 pair-2 when vb4
                                // is degenerate.
                                bool isClothLogoShader =
                                    earlyPick.HasValue &&
                                    earlyPick.Value.Shader == MaterialSlotResolver.ShaderFamily.CharacterCloth &&
                                    !string.IsNullOrEmpty(earlyPick.Value.PickedSlot) &&
                                    earlyPick.Value.PickedSlot.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0;
                                bool acceptAlt = isClothLogoShader
                                    ? !altDegenerate
                                    : (!altDegenerate && !altFullAtlas && !altWraparound);
                                if (!acceptAlt)
                                {
                                    Log($"  NOTE: submesh '{sm.Name}' vb=4 parked AND " +
                                        $"vb=3[4,6] rejected (deg={altDegenerate} " +
                                        $"full={altFullAtlas} wrap={altWraparound}, " +
                                        $"bbox [{aUMin:F3},{aUMax:F3}]x[{aVMin:F3},{aVMax:F3}]) " +
                                        "-- keeping vb=4.");
                                }
                                if (acceptAlt)
                                {
                                    Log($"  NOTE: submesh '{sm.Name}' vb=4 decal UV span " +
                                        $"({uMax - uMin:0.000} x {vMax - vMin:0.000}) is " +
                                        $"degenerate; falling back to vb=3[4,6] (" +
                                        $"{aUMax - aUMin:0.000} x {aVMax - aVMin:0.000}).");
                                    uvs = altUvs;
                                    uvRange = altRange;
                                }
                            }
                        }
                    }
                }
                else if (useSecondaryUvPair && sm.UvPair2InPrimary &&
                         TryDecodePrimaryPair2(vbufs, sm, maxIndex, rawIndices,
                                               out var pair2Uvs, out var pair2Range))
                {
                    // v17.1: descriptor says the primary UV element carries
                    // two pairs and there is no separate secondary stream
                    // (F1 25 pools A/C). The decal UV is bytes 4/6 of the
                    // primary stream. Taken only when the used-vertex span is
                    // non-degenerate, so the FIX 7 case (constant 4/6 bytes on
                    // 2021 data) still falls through to pair-1 below.
                    uvs = pair2Uvs;
                    uvRange = pair2Range;
                    Log($"  NOTE: submesh '{sm.Name}' secondary UV read from primary stream bytes 4/6 " +
                        "(descriptor: no separate secondary stream).");
                }
                else if (useSecondaryUvPair)
                {
                    // FIX 7: the secondary UV buffer (slots[3] / vb=4) is
                    // invalid or absent for this submesh. The old code fell
                    // through to reading "pair 2" from the primary UV buffer
                    // (vb=3, offsets 4,6). On 2021 data vb=3 is 8 bytes per
                    // vertex (Fvf=35), and hex inspection of the McLaren
                    // package shows the layout is:
                    //   offsets 0,2 = real tiled UV (e.g. -17.7..+8.4 on
                    //                 body geometry, used for paint/carbon
                    //                 atlas wrap-around)
                    //   offsets 4,6 = constants near 0.99 -- NOT a second
                    //                 UV pair. Sampling them as UVs pins
                    //                 every vertex to the same atlas pixel.
                    // The tile-rate cap downstream then silently rescales
                    // those near-1.0 values to look plausible, which is
                    // why the bug looked like "decals land in wrong areas"
                    // rather than "no decals at all".
                    //
                    // Instead: fall back to pair-1 UVs. The texture will
                    // sample from the paint/carbon atlas coordinates rather
                    // than the intended decal/logo atlas, which isn't perfect
                    // but is at least recognisable geometry rather than a
                    // single-pixel sample.
                    Log($"  WARNING: submesh '{sm.Name}' needs secondary UV (logo/sponsor) " +
                        $"but has no valid pair-2 buffer (UvBufferIndex2={sm.UvBufferIndex2}). " +
                        "Falling back to pair-1 UVs.");
                    uvs = DecodeUvStream(
                        vbufs, sm.UvBufferIndex, sm.VOffset, maxIndex,
                        useSecondaryUvPair: false, out uvRange);
                }
                else
                {
                    uvs = DecodeUvStream(
                        vbufs, sm.UvBufferIndex, sm.VOffset, maxIndex, false, out uvRange);
                }
                if (uvs == null)
                {
                    // FP32 fallback path.  FIX 6: now also computes uvRange
                    // locally so the tile-rate cap below has correct data
                    // instead of reading stale values from the previous submesh.
                    int uvByteOffset = DetectUvByteOffset(vb.Stride);
                    if (uvByteOffset > 0)
                    {
                        uvs = new System.Windows.Point[maxIndex + 1];
                        double fuMin = double.PositiveInfinity, fuMax = double.NegativeInfinity;
                        double fvMin = double.PositiveInfinity, fvMax = double.NegativeInfinity;
                        for (int vi = 0; vi <= maxIndex; vi++)
                        {
                            long off = vStartByte + (long)vi * vb.Stride;
                            if (off + uvByteOffset + 8 <= vData.LongLength)
                            {
                                float u = BitConverter.ToSingle(vData, (int)off + uvByteOffset);
                                float v = BitConverter.ToSingle(vData, (int)off + uvByteOffset + 4);
                                double vOut = FlipTextureV ? (1.0 - v) : v;
                                uvs[vi] = new System.Windows.Point(u, vOut);
                                if (u < fuMin) fuMin = u; if (u > fuMax) fuMax = u;
                                if (vOut < fvMin) fvMin = vOut; if (vOut > fvMax) fvMax = vOut;
                            }
                        }
                        uvRange = (fuMin, fuMax, fvMin, fvMax);
                    }
                }

                // ---- UV MODIFICATIONS REMOVED (per user request) ----
                //
                // Two UV-modifying blocks previously ran here:
                //
                //   1. Decal UV clamp to [0,1]: on useSecondaryUvPair
                //      submeshes, clipped UV values outside [0,1] to the
                //      unit square.
                //
                //   2. UV tile cap (FIX 32): if the USED-vert bbox of a
                //      submesh's UVs had any component with |value| >
                //      MaxUvTileRate, scaled every UV in the buffer down
                //      so the bbox fit inside that cap.
                //
                // Both have been removed. UVs now reach the WPF sampler
                // exactly as decoded from the vertex buffer -- no clamping,
                // no scaling. WPF's ImageBrush (TileMode.Tile +
                // ViewportUnits.Absolute) wraps out-of-range UVs naturally,
                // so carbon weave, paint, and decal atlases all see their
                // authored UV ranges untouched.
                //
                // ---- F1 2021 compatibility addon ----
                //
                // The F1 2021 decal atlases need the clamp + cap to render
                // correctly. This block re-enables BOTH mods as a
                // compile-time-reversible addon, gated on the public
                // ApplyF1_2021_DecalCompatibility flag. The flag is
                // default-false, so F1 22 (the current user case) sees no
                // modifications. Callers that load an F1 2021 ERP set the
                // flag to true before invoking LoadFolder, which restores
                // the earlier build's behaviour byte-for-byte:
                //
                //   Clamp (useSecondaryUvPair only, exactly as earlier build)
                //   Cap   (buffer-bbox, NOT FIX 32's used-verts variant)
                //
                // Using the buffer-bbox tile cap here is deliberate: the
                // earlier F1-2021-working build used it, and the more
                // surgical FIX 32 used-verts version is part of what we
                // removed. Keeping the original semantics means this
                // addon is a faithful restoration, not a new implementation
                // with subtle differences. F1 22 doesn't get the cap at
                // all (flag is false), so its rendering stays untouched.
                if (ApplyF1_2021_DecalCompatibility)
                {
                    // (1) Clamp decal UVs to [0,1] to prevent wrap-around.
                    if (useSecondaryUvPair && uvs != null)
                    {
                        bool clamped = false;
                        double cuMin = double.PositiveInfinity, cuMax = double.NegativeInfinity;
                        double cvMin = double.PositiveInfinity, cvMax = double.NegativeInfinity;
                        for (int k = 0; k < uvs.Length; k++)
                        {
                            double u = uvs[k].X, v = uvs[k].Y;
                            double uClamped = u < 0 ? 0 : (u > 1 ? 1 : u);
                            double vClamped = v < 0 ? 0 : (v > 1 ? 1 : v);
                            if (uClamped != u || vClamped != v) clamped = true;
                            uvs[k] = new System.Windows.Point(uClamped, vClamped);
                            if (uClamped < cuMin) cuMin = uClamped;
                            if (uClamped > cuMax) cuMax = uClamped;
                            if (vClamped < cvMin) cvMin = vClamped;
                            if (vClamped > cvMax) cvMax = vClamped;
                        }
                        if (clamped)
                            uvRange = (cuMin, cuMax, cvMin, cvMax);
                    }

                    // (2) AUDIT (assumption-audit branch): the
                    // MaxUvTileRate rescale that lived here is GONE. It
                    // predated the tiling wrap brushes, the per-triangle
                    // overlay filtering (FIX 34) and tile-shift
                    // normalization, and it had already been proven
                    // destructive for wrapping paint atlases (2021 Red
                    // Bull). Carbon/detail tiling now renders through the
                    // same wrap-brush path that made the paint exemption
                    // correct, so the cap protects nothing and only
                    // distorts raw UV data.
                }

                // Ported from the UniversalExporter: on current ERPs
                // (2023-2026) the number BASE mesh carries the digit atlas
                // itself (me_albedoMap binds <team>_driver_N.tif) and the
                // overlay system doesn't fire for these materials -- the
                // base IS the digit pass. Push its vertices ~5mm out along
                // their normals or it z-fights with the body underneath
                // and disappears under WPF's depth test ("numbers missing
                // or only visible front/rear"). Normals that point INTO
                // the body (inverted authoring, e.g. Ferrari topcanopy)
                // are detected against the submesh centroid and flipped.
                // NUMBER DIAGNOSTIC: cluster the used triangles by UV
                // centroid (0.1 grid) and log each cluster's span. Front/
                // rear/side number placements live in ONE submesh on
                // 2023+ cars; a cluster with a near-zero span is a
                // placement whose UVs collapsed to a point (renders as a
                // solid colour -- the "completely white front number").
                if (isNumberSubmesh && uvs != null && rawIndices != null)
                {
                    var clusters = new Dictionary<(int, int), (int count, double u0, double u1, double v0, double v1)>();
                    for (int t = 0; t + 2 < rawIndices.Length; t += 3)
                    {
                        double cu = 0, cv = 0; bool ok = true;
                        double tu0 = double.PositiveInfinity, tu1 = double.NegativeInfinity;
                        double tv0 = double.PositiveInfinity, tv1 = double.NegativeInfinity;
                        for (int k = 0; k < 3; k++)
                        {
                            int vi = rawIndices[t + k];
                            if (vi < 0 || vi >= uvs.Length) { ok = false; break; }
                            double uu = uvs[vi].X, vv = uvs[vi].Y;
                            cu += uu; cv += vv;
                            if (uu < tu0) tu0 = uu; if (uu > tu1) tu1 = uu;
                            if (vv < tv0) tv0 = vv; if (vv > tv1) tv1 = vv;
                        }
                        if (!ok) continue;
                        var key = ((int)Math.Floor(cu / 3 * 10), (int)Math.Floor(cv / 3 * 10));
                        if (!clusters.TryGetValue(key, out var c))
                            c = (0, double.PositiveInfinity, double.NegativeInfinity,
                                 double.PositiveInfinity, double.NegativeInfinity);
                        clusters[key] = (c.count + 1,
                            Math.Min(c.u0, tu0), Math.Max(c.u1, tu1),
                            Math.Min(c.v0, tv0), Math.Max(c.v1, tv1));
                    }
                    foreach (var kv in clusters)
                    {
                        var c = kv.Value;
                        Log($"     number-uv cluster @({kv.Key.Item1 / 10.0:F1},{kv.Key.Item2 / 10.0:F1}): " +
                            $"{c.count} tris, span {c.u1 - c.u0:F3}x{c.v1 - c.v0:F3} " +
                            $"[{c.u0:F3},{c.u1:F3}]x[{c.v0:F3},{c.v1:F3}]");
                    }
                }

                Point3D[] meshPositions = positions;
                if (numberBaseIsDigitAtlas && normals != null && normals.Length == positions.Length)
                {
                    const double kNumberBaseOffset = 0.005; // ~5mm in game units

                    // Inverted-normal detection, ONE decision per submesh:
                    // majority vote of authored normals against the
                    // face-winding normals. (The earlier per-vertex
                    // centroid test was unstable on elongated submeshes --
                    // the 2026 Ferrari's front number lives in the
                    // whole-car safetycell number submesh, where "away
                    // from centroid" points forward while the nose-top
                    // normal points up, so nose vertices got pushed INTO
                    // the body and the front number vanished.)
                    double agree = 0;
                    if (rawIndices != null)
                    {
                        for (int t = 0; t + 2 < rawIndices.Length; t += 3)
                        {
                            int a = rawIndices[t], b = rawIndices[t + 1], c = rawIndices[t + 2];
                            if (a < 0 || b < 0 || c < 0 ||
                                a >= positions.Length || b >= positions.Length || c >= positions.Length)
                                continue;
                            var pa = positions[a]; var pb = positions[b]; var pc = positions[c];
                            double e1x = pb.X - pa.X, e1y = pb.Y - pa.Y, e1z = pb.Z - pa.Z;
                            double e2x = pc.X - pa.X, e2y = pc.Y - pa.Y, e2z = pc.Z - pa.Z;
                            double fx = e1y * e2z - e1z * e2y;
                            double fy = e1z * e2x - e1x * e2z;
                            double fz = e1x * e2y - e1y * e2x;
                            var na = normals[a]; var nb2 = normals[b]; var nc2 = normals[c];
                            double anx = na.X + nb2.X + nc2.X;
                            double any = na.Y + nb2.Y + nc2.Y;
                            double anz = na.Z + nb2.Z + nc2.Z;
                            agree += Math.Sign(fx * anx + fy * any + fz * anz);
                        }
                    }
                    double sign = agree < 0 ? -1.0 : 1.0;

                    meshPositions = new Point3D[positions.Length];
                    for (int i = 0; i < positions.Length; i++)
                    {
                        var n = normals[i];
                        double nx = n.X, ny = n.Y, nz = n.Z;
                        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (len > 1e-6) { nx /= len; ny /= len; nz /= len; }

                        var p = positions[i];
                        meshPositions[i] = new Point3D(
                            p.X + nx * kNumberBaseOffset * sign,
                            p.Y + ny * kNumberBaseOffset * sign,
                            p.Z + nz * kNumberBaseOffset * sign);
                    }
                }

                var mesh = new MeshGeometry3D
                {
                    Positions = new Point3DCollection(meshPositions),
                    TriangleIndices = new Int32Collection(rawIndices),
                };
                if (normals != null) mesh.Normals = new Vector3DCollection(normals);
                if (uvs != null) mesh.TextureCoordinates = new PointCollection(uvs);

                MeshFamilyRegistry.Register(mesh, descriptorPartName);
                // Register with material EAID too so the FBX exporter can
                // name the Assimp Material after the game's actual
                // .material file AND resolve the shader through the
                // texture cache -- the manifest's ShaderEaid, Carbon
                // params, normal/surface roles and decal wiring all hang
                // off GetMaterialEaid(mesh).
                MeshSubmeshRegistry.Register(mesh, sm.Name, sm.DescriptorHint, materialEaid);

                // ---- Build the material for this submesh ----
                //
                // Every submesh gets ONE log line in the form
                //   [PART.N] slot=<SLOT> mat=<MATERIAL> pick=<SAMPLER> tex=<TEXTURE>  [OK|WHITE: <reason>]
                // This is the primary diagnostic tool when textures land
                // on the wrong geometry: compare the per-submesh line to
                // where that submesh sits in 3D space.
                Material material = null;
                SeeThroughMaterial seeThrough = null;
                string logSlot = sm.MaterialSlot ?? "?";
                string logMat = "-";
                string logPick = "-";
                string logTex = "-";
                string logStatus;

                if (!string.IsNullOrEmpty(materialEaid))
                    logMat = ShortenEaid(materialEaid);

                if (_textureCache == null)
                {
                    logStatus = "WHITE: no TextureCache (package root not discovered)";
                }
                else if (_activeContext == null)
                {
                    logStatus = "WHITE: no active EMB context";
                }
                else if (string.IsNullOrEmpty(materialEaid))
                {
                    logStatus = $"WHITE: slot not bound in context {_activeContext.ContextId}";
                }
                else if (uvs == null)
                {
                    logStatus = "WHITE: submesh has no UV stream";
                }
                else
                {
                    // Use the result we already computed before UV decode -- avoids
                    // a second material-file parse + texture cache lookup.
                    var res = earlyPick ?? _textureCache.ResolveDiffuseForMaterial(materialEaid, sm.MaterialSlot);

                    // HELMET FALLBACK: when the .material file carries no
                    // sampler bindings (helmet.material, visor.material etc.
                    // are parameter-only shaders), look up a texture by the
                    // livery-naming convention instead:
                    //   eaid://livery_package_shared/replica/<context>/textures/<context>_<slot>_d.tif
                    // <context> is the named livery context captured during
                    // AutoConfigure (e.g. "replica_zenith25a"). For each
                    // submesh slot we try the bespoke per-livery texture
                    // first, then optionally fall through to nothing (white).
                    if (res.Brush == null && !string.IsNullOrEmpty(sm.MaterialSlot))
                    {
                        // Build candidate token list. Slot name first, then the
                        // material's basename (e.g. helmet_wing.material has
                        // stem "helmet_wing" which is the actual texture infix
                        // for a submesh slotted as just "wing"). Adds robustness
                        // when the IDF's slot name and the .material filename
                        // disagree -- which is the common case for attachments.
                        var tokenCandidates = new List<string> { sm.MaterialSlot };
                        string matStem = ExtractMaterialStem(materialEaid);
                        if (!string.IsNullOrEmpty(matStem) &&
                            !string.Equals(matStem, sm.MaterialSlot, StringComparison.OrdinalIgnoreCase))
                            tokenCandidates.Add(matStem);

                        foreach (var token in tokenCandidates)
                        {
                            if (res.Brush != null) break;
                            foreach (var (eaid, label) in EnumerateHelmetFallbackTextureEaids(
                                _helmetLiveryContextName, token, channel: "d"))
                            {
                                var fbBrush = _textureCache.GetBrushForTexture(eaid);
                                if (fbBrush != null)
                                {
                                    res.Brush = fbBrush;
                                    res.TextureEaid = eaid;
                                    res.PickedSlot = label + " (" + token + ")";
                                    res.FailureReason = null;
                                    break;
                                }
                            }
                        }
                    }

                    logPick = res.PickedSlot ?? "-";
                    logTex = res.TextureEaid != null ? ShortenEaid(res.TextureEaid) : "-";
                    if (res.Brush != null)
                    {
                        // Substitute hi-res mipmaps-sidecar brush when
                        // available; falls through to res.Brush untouched
                        // when no sidecar exists for this texture.
                        var diffuseBrush = HighResBrushProvider.WrapBrush(
                            res.TextureEaid, res.Brush, _textureCache, Log);

                        // See-through character materials (visor, tear-off,
                        // clear plastic) are blended at the game's own opacity
                        // instead of drawn solid; the brush is a copy, since
                        // the cache shares one per texture (blank_d.tif backs
                        // opaque slots too).
                        seeThrough = SeeThroughMaterial.For(_textureCache.GetMaterial(materialEaid));
                        if (seeThrough != null && diffuseBrush != null)
                        {
                            diffuseBrush = diffuseBrush.CloneCurrentValue();
                            diffuseBrush.Opacity = seeThrough.Opacity;
                            diffuseBrush.Freeze();
                        }

                        material = new DiffuseMaterial(diffuseBrush);
                        // v17.0: associate this WPF material with its decoded
                        // GfxMaterialRes so the DX showroom can apply the
                        // game's own paint/carbon/rubber/glass response.
                        GameMaterialCatalog.Tag(material, materialEaid);
                        GameMaterialCatalog.TagSeeThrough(material, seeThrough);
                        // Record the BASE texture EAID for the exporter.
                        // Classic and Legacy always did this; the modern
                        // loader only recorded OVERLAY texture EAIDs, so
                        // 2021+ glTF exports shipped every base material
                        // untextured (carbon/rear-light/paint alike).
                        if (!string.IsNullOrEmpty(res.TextureEaid))
                            MeshSubmeshRegistry.SetTextureEaid(mesh, res.TextureEaid);
                        texturedCount++;
                        logStatus = seeThrough == null ? "OK"
                            : $"OK see-through {seeThrough.Kind} opacity={seeThrough.Opacity:0.###}";
                    }
                    else
                    {
                        logStatus = $"WHITE: {res.FailureReason}";
                    }
                }

                if (material == null)
                    material = _neutralMaterial;

                string uvRangeStr;
                if (uvs != null)
                {
                    var r = uvRange;
                    uvRangeStr = $"uv=[{r.uMin:F2},{r.uMax:F2}]x[{r.vMin:F2},{r.vMax:F2}]";
                }
                else
                {
                    uvRangeStr = "uv=none";
                }

                Log($"  [{descriptorPartName}.{sm.SubmeshIndex}] slot={logSlot,-18} " +
                    $"mat={logMat,-34} pick={logPick,-18} tex={logTex,-40} {uvRangeStr,-32} {logStatus}");

                _stats.RecordSubmesh(
                    partName: descriptorPartName,
                    slot: logSlot,
                    materialEaid: materialEaid,
                    textureEaid: (logStatus == "OK") ? logTex : null,
                    triangleCount: rawIndices.Length / 3,
                    descriptorFile: sm.DescriptorHint);

                var model = new GeometryModel3D(mesh, material)
                {
                    BackMaterial = material,
                };
                if (seeThrough != null || IsGlassSubmesh(sm.MaterialSlot ?? sm.Name, materialEaid))
                    SetIsTransparentBase(model, true);

                group.Children.Add(model);

                // ---- Sponsor / decal overlay submesh ----
                //
                // The shader-aware MaterialSlotResolver decided upstream
                // whether this material has an overlay pass and, if so, which
                // texture drives it. That decision is carried in the
                // TextureCache.PreviewResolution (via HasOverlay +
                // OverlayTextureEaid + OverlayBrush). The overlay code below
                // simply honours that decision -- no filename sniffing, no
                // UV-range heuristics, no per-material blocklist. Submeshes
                // whose shader family carries no overlay (wheel, mirror,
                // shadow, generic, rainbeads, lcd, revs, rear_light) return
                // HasOverlay=false and skip cleanly.
                //
                // Skipped when:
                //   - main pass is itself an overlay-class slot (the logo
                //     sub-pass was already applied as the base; no second
                //     pass needed)
                //   - the resolver decoded no overlay brush (shader has no
                //     overlay, or the overlay texture failed to decode)
                //   - normals are missing (can't compute the z-offset)
                if (uvs != null && normals != null && !useSecondaryUvPair &&
                    _textureCache != null && !string.IsNullOrEmpty(materialEaid) &&
                    earlyPick.HasValue && earlyPick.Value.HasOverlay)
                {
                    // FIX 18d revised: the original guard blocked number /
                    // carbon-number overlays because the raw driver atlas is
                    // 92% transparent BC3 and WPF punches depth-buffer holes.
                    // The NUMBER-OVERLAY system in EgoTexturePipeline now
                    // composites onto a solid background first, so the
                    // transparency issue no longer applies. Number overlays
                    // are handled identically to sponsor decals.
                    _lastOverlayResolution = earlyPick.Value;
                    var sponsorModel = TryBuildSponsorOverlay(
                        earlyPick.Value, vbufs, sm, vb, vData, vStartByte, maxIndex,
                        positions, normals, rawIndices, mesh,
                        out string sponsorTexShort);
                    if (sponsorModel != null)
                    {
                        group.Children.Add(sponsorModel);
                        string overlayKind = isNumberSubmesh
                            ? $"+ number decal overlay on {descriptorPartName} ({positions.Length} verts, {rawIndices.Length / 3} tris)"
                            : $"+ sponsor overlay: {sponsorTexShort}";
                        Log($"     {overlayKind}");
                        _stats.RecordSponsorOverlay();
                    }
                }
                // FIX 20f: "detail" submeshes can carry decal UVs (e.g.
                // McLaren swoosh on nosecone detail layer) but their
                // material uses vehicle_generic which has no me_logoMap.
                // Borrow the overlay brush from the last body resolution.
                else if (uvs != null && normals != null && !useSecondaryUvPair &&
                         _lastOverlayResolution.HasOverlay &&
                         SlotIs(sm.Name, "detail"))
                {
                    var sponsorModel = TryBuildSponsorOverlay(
                        _lastOverlayResolution, vbufs, sm, vb, vData, vStartByte, maxIndex,
                        positions, normals, rawIndices, mesh,
                        out string sponsorTexShort);
                    if (sponsorModel != null)
                    {
                        group.Children.Add(sponsorModel);
                        Log($"     + sponsor overlay (detail): {sponsorTexShort}");
                        _stats.RecordSponsorOverlay();
                    }
                }
                // DIAG: carbon_tile overlay probe.
                //
                // FIX 20c blocked ALL carbon_tile overlays because vb=4
                // UVs were full-atlas garbage. But the game's vehicle_carbon
                // shader DOES read me_logoMap for sponsors on carbon panels
                // (e.g. McLaren swoosh on nosecone tip). The resolver returns
                // HasOverlay=false for carbon tiles, so the main overlay gate
                // above never fires. This block probes vb=3 pair2 (the same
                // UV source that works for paint overlays) and, if the UVs
                // look like valid sponsor positions, builds the overlay using
                // the last resolved sponsor brush.
                //
                // Guarded by: slot name starts with "carbon_tile" or
                // "carbon tile", and the _lastOverlayResolution carries a
                // valid sponsor brush from a preceding body submesh.
                else if (uvs != null && normals != null && !useSecondaryUvPair &&
                         _lastOverlayResolution.HasOverlay &&
                         !string.IsNullOrEmpty(sm.MaterialSlot) &&
                         (sm.MaterialSlot.Trim().StartsWith("carbon_tile", StringComparison.OrdinalIgnoreCase) ||
                          sm.MaterialSlot.Trim().StartsWith("carbon tile", StringComparison.OrdinalIgnoreCase)))
                {
                    // Probe vb=3 pair2 UVs for this carbon tile.
                    var carbonProbeUvs = DecodeUvStream(
                        vbufs, sm.UvBufferIndex, sm.VOffset, maxIndex,
                        useSecondaryUvPair: true, out _);

                    if (carbonProbeUvs != null && rawIndices != null && rawIndices.Length > 0)
                    {
                        // Compute USED-vertex-only bounding box.
                        double cpUmin = double.PositiveInfinity, cpUmax = double.NegativeInfinity;
                        double cpVmin = double.PositiveInfinity, cpVmax = double.NegativeInfinity;
                        int cornerParkedCount = 0;
                        const double kCorner = 0.97;
                        for (int ri = 0; ri < rawIndices.Length; ri++)
                        {
                            int vi = rawIndices[ri];
                            if (vi < 0 || vi >= carbonProbeUvs.Length) continue;
                            double u = carbonProbeUvs[vi].X, v = carbonProbeUvs[vi].Y;
                            if (u < cpUmin) cpUmin = u; if (u > cpUmax) cpUmax = u;
                            if (v < cpVmin) cpVmin = v; if (v > cpVmax) cpVmax = v;
                            if (u > kCorner && v > kCorner) cornerParkedCount++;
                        }

                        double cpURange = cpUmax - cpUmin;
                        double cpVRange = cpVmax - cpVmin;
                        bool allCornerParked = (cornerParkedCount == rawIndices.Length);
                        bool tiled = (cpUmax >= 2.0 || cpVmax >= 2.0 ||
                                      cpUmin < -2.0 || cpVmin < -2.0);

                        Log($"     CARBON-TILE-PROBE [{descriptorPartName}.{sm.SubmeshIndex}] " +
                            $"slot='{sm.MaterialSlot}' vb3p2 bbox=[{cpUmin:F3},{cpUmax:F3}]x" +
                            $"[{cpVmin:F3},{cpVmax:F3}] span={cpURange:F3}x{cpVRange:F3} " +
                            $"cornerParked={allCornerParked} tiled={tiled}");

                        // If the UVs look like valid sponsor positions, build
                        // the overlay. The same filters from TryBuildSponsorOverlay
                        // (tiling rejection, corner-park rejection) will catch
                        // panels that genuinely don't carry sponsors.
                        if (!allCornerParked && !tiled)
                        {
                            var sponsorModel = TryBuildSponsorOverlay(
                                _lastOverlayResolution, vbufs, sm, vb, vData, vStartByte, maxIndex,
                                positions, normals, rawIndices, mesh,
                                out string sponsorTexShort);
                            if (sponsorModel != null)
                            {
                                group.Children.Add(sponsorModel);
                                Log($"     + sponsor overlay (carbon_tile): {sponsorTexShort}");
                                _stats.RecordSponsorOverlay();
                            }
                            else
                            {
                                Log($"     ~ carbon_tile overlay REJECTED by TryBuildSponsorOverlay");
                            }
                        }
                        else
                        {
                            Log($"     ~ carbon_tile overlay SKIP: " +
                                (allCornerParked ? "all corner-parked" : "tiled UVs"));
                            // FIX 34f: same parked TEXCOORD_1 stash as the
                            // builder's skip paths — Blender needs the
                            // channel to exist for decal-wired materials.
                            var parkedArr = new System.Windows.Point[carbonProbeUvs.Length];
                            for (int pk = 0; pk < parkedArr.Length; pk++)
                            {
                                double pu = carbonProbeUvs[pk].X, pv = carbonProbeUvs[pk].Y;
                                parkedArr[pk] = (pu >= -0.05 && pu <= 1.05 && pv >= -0.05 && pv <= 1.05)
                                    ? carbonProbeUvs[pk]
                                    : new System.Windows.Point(0.002, 0.998);
                            }
                            MeshSubmeshRegistry.SetRawDecalUv(mesh,
                                new System.Windows.Media.PointCollection(parkedArr));
                        }
                    }
                    else
                    {
                        Log($"     ~ carbon_tile overlay SKIP: vb3p2 decode failed or no indices");
                    }
                }
                else if (earlyPick.HasValue && earlyPick.Value.HasOverlay)
                {
                    // FIX 20: log why the overlay gate rejected this submesh
                    // even though the resolver said it should have one.
                    string reason =
                        uvs == null ? "no UVs" :
                        normals == null ? "no normals" :
                        useSecondaryUvPair ? "base uses pair-2 (logo/number pass)" :
                        SlotIs(sm.Name, "number") ? "number submesh" :
                        SlotIs(sm.Name, "carbon_number") ? "carbon_number submesh" :
                        "unknown (texCache/mat missing)";
                    Log($"     ~ overlay SKIPPED (gate): {reason}");
                }
            }

            return (group, texturedCount);
        }

        /// <summary>
        /// Builds a sponsor / decal overlay submesh on top of an already-
        /// rendered main submesh, or returns null if the resolver decided
        /// this material has no overlay pass.
        ///
        /// The shader-aware MaterialSlotResolver tells us authoritatively
        /// which shader families carry overlays and which don't:
        ///   vehicle_metallic_paint  -> overlay = mclaren_decal_da.tif (sponsors)
        ///                              or mclaren_driver_N.tif for number_ materials
        ///   vehicle_carbon          -> overlay = same decal sheet / driver number
        ///   vehicle_steering        -> overlay = steering_wheel_decal_da.tif
        ///   all other shaders       -> no overlay
        ///
        /// This replaces an earlier heuristic-based overlay gate that tried
        /// to infer overlay eligibility from the material's filename, the
        /// preview-picker's chosen sampler, and the UV2 range of the submesh.
        /// Those heuristics produced inconsistent results across submeshes
        /// that shared the same material (e.g. bargeboard_fl getting no
        /// overlay while bargeboard_fr did, despite byte-for-byte identical
        /// submesh slot data). Trusting the shader table makes the decision
        /// deterministic: every paint submesh gets a sponsor pass, every
        /// carbon submesh with a pair-2 UV buffer gets one, and everything
        /// else skips cleanly.
        ///
        /// The overlay shares the main submesh's index buffer but uses:
        ///   - UV pair 2 (the decal/atlas UV) read from slots[3] -> vb=4
        ///   - positions pushed outward along the normal by a small amount
        ///     to defeat z-fighting
        ///   - the sponsor texture's brush (cached, BC3 with per-pixel alpha)
        /// </summary>
        private static GeometryModel3D TryBuildSponsorOverlay(
            TextureCache.PreviewResolution resolved,
            List<VertexBufferInfo> vbufs,
            SubMeshInfo sm,
            VertexBufferInfo vb,
            byte[] vData, long vStartByte,
            int maxIndex,
            Point3D[] positions,
            Vector3D[] normals,
            int[] rawIndices,
            MeshGeometry3D baseMesh,
            out string sponsorTexShort)
        {
            sponsorTexShort = null;

            // FIX 34f: when this overlay is SKIPPED (parked / tiled UVs),
            // the FBX still needs a TEXCOORD_1 on the BASE mesh — the
            // Blender addon wires the decal mix for every decal-capable
            // material, and a MISSING second UV layer makes it fall back
            // to the tiling UV, smearing atlas content across the panel
            // (2026 Mercedes safetycell carbon). Stash a parked set on
            // the base mesh: authentic values where in-range, transparent
            // corner elsewhere. Overlays that DO render stash their own
            // (better) set further down, keyed off the overlay mesh.
            void StashParkedExportUv(System.Windows.Point[] src)
            {
                if (baseMesh == null || src == null) return;
                var arr = new System.Windows.Point[src.Length];
                for (int k = 0; k < arr.Length; k++)
                {
                    double su = src[k].X, sv = src[k].Y;
                    arr[k] = (su >= -0.05 && su <= 1.05 && sv >= -0.05 && sv <= 1.05)
                        ? src[k]
                        : new System.Windows.Point(0.002, 0.998);
                }
                MeshSubmeshRegistry.SetRawDecalUv(baseMesh,
                    new System.Windows.Media.PointCollection(arr));
            }

            // 1. The shader-aware resolver has already decided whether this
            //    material has an overlay pass and which texture drives it.
            //    If the resolver said "no overlay", return cleanly.
            if (!resolved.HasOverlay) { Log($"     ~ overlay SKIP '{sm.Name}': resolver says no overlay"); return null; }

            // 2. Decode the decal / logo atlas UV stream.
            //
            // FIX 20f: the correct decal UV is the SECOND half-float pair
            // (bytes 4-7) of the primary UV buffer (vb=3, Fvf=35, stride=8),
            // NOT the separate vb=4 (Fvf=29, stride=4) buffer.
            //
            // Verified by scatter-plotting all VB candidates on the decoded
            // mclaren_decal_da.tif atlas for the McLaren 2021:
            //   vb=4: nosecone lands on Arrow/Richard Mille (WRONG)
            //   vb=3 pair2: nosecone spans Miory Steel→Pirelli left strip (CORRECT)
            //   vb=3 pair2: safetycell covers End Racism/Alteryx/DeWalt (CORRECT)
            //
            // Vertices without sponsor content are parked at (0.993, 0.988)
            // — the transparent bottom-right corner of the atlas — so they
            // produce no visible overlay even though they're sampled.
            //
            // FIX 21b: number / carbon-number submeshes have their driver
            // atlas UVs in the separate vb=4 buffer (UvBufferIndex2, fmt-29,
            // stride=4) — NOT in vb=3 pair-2 where sponsors live.
            //
            // FIX 24 / FIX 25: on many parts, vb=4 holds vestigial UVs that
            // don't actually cover the digit region of the driver atlas.
            // The right source varies per-submesh: verified concrete cases
            // from the 2021 McLaren —
            //   sidepod_fl.number   (22 verts):  vb=4 empty → vb=3[4,6] hits "1"
            //   topcanopy_bm.number (40 verts):  vb=4 empty → vb=3[4,6] hits "1"
            //   safetycell.number  (339 verts):  vb=4 bbox wide but
            //                                    triangles don't cover the
            //                                    digit cluster → vb=3[4,6]
            //                                    needed instead
            //   (and sometimes vb=4 is right: FIX 24 comment claims
            //    safetycell at 191-vert geometry lands correctly on vb=4)
            //
            // So the right heuristic is content-based, not bbox-based:
            // sample the source atlas's alpha channel at every used vertex
            // of both UV sources and pick whichever has more vertices
            // landing on digit pixels (alpha >= 128). Ties go to vb=4
            // (the conventional choice). This supersedes the FIX 24
            // degenerate-span check, which only caught one failure mode.
            //
            // The pipeline hands us the source alpha via
            // resolved.OverlaySourceAlpha. If it's null (non-number
            // resolution, or pixel decode failed) we fall back to the
            // original vb=4-then-span-check logic.
            bool isNumberOverlay =
                IsNumberSlot(sm.Name);


            // ---- UNIFIED overlay UV path: numbers load exactly like decals ----
            //
            // Per user's general rule: "if the number does NOT load like the
            // decals, then it MUST load like the decals". Previously number
            // submeshes had an elaborate content-coverage picker that chose
            // between vb=4 (decal buffer, Fvf=29), vb=3[4,6] (secondary pair
            // of primary UV buffer), and a FIX 33 candidate stream -- trying
            // to find whichever one landed on digit pixels of the driver
            // atlas. That picker produced fragmentary digits (tiny UV rects
            // covering only a corner of a digit) and caused regressions on
            // submeshes where its scoring misranked candidates.
            //
            // Sponsors never had this problem: they just decode vb=3 pair-2
            // (Fvf=35 stride=8, offsets 4,6) and render. Numbers now take
            // the same path -- one UV source, no scoring, no per-submesh
            // special cases. If vb=3 pair-2 doesn't contain usable UVs for
            // a particular number submesh, the standard UV-tiling /
            // corner-park filters below will reject the overlay cleanly,
            // exactly as they do for sponsors.
            // FIX 34: candidate-chain UV sourcing. vb=3 pair-2 stays the
            // primary source (verified on 2021 McLaren sponsors), but some
            // meshes store a TILED copy of the carbon UV there and keep the
            // real decal UVs in the separate vb=4 fmt-29 buffer instead
            // (2026 Red Bull rearwing_top.carbon_tile_2: pair-2 spans
            // [0,2.545] -> rejected -> sponsor decal missing from the
            // bottom wing plane, while vb=4 holds the correct atlas UVs).
            // Try each candidate through the same validity filters and use
            // the first that passes; the filters that used to hard-return
            // now just advance to the next candidate.
            //
            // Filters, per candidate:
            //
            // a) Extreme tiling (topcanopy at [-66,+67]) — UV values
            //    beyond ±2.0 are clearly tiled secondary textures.
            //    (FIX 20d: full-atlas [0,1]x[0,1] spans stay VALID — the
            //    safetycell IS the panel that shows the whole sheet.)
            //
            // b) FIX 34: degenerate single-point UVs. Codemasters parks
            //    unused decal verts at a corner; on 2026 content that's
            //    (0,1)/(1,1) — corners the old bottom-right-only check
            //    missed. 21 submeshes on the 2026 Red Bull rendered
            //    "overlay OK" with ALL verts at one point: a one-pixel
            //    sample stretched across the panel that both tints it
            //    and punches depth holes over real decals beneath.
            //    A real decal region always has measurable span; anything
            //    under 0.002 (≈8px on a 4K atlas) in BOTH axes is a park.
            //
            // c) FIX 20f: all USED vertices corner-parked at the
            //    transparent bottom-right (U>0.97, V>0.97) — a 100%
            //    transparent overlay that writes depth and hides
            //    sponsors on geometry beneath it.
            // Local validator shared by both candidates. Returns null when
            // the UV set is a plausible decal region, otherwise a reason
            // string. Out-params carry the USED-vertex bbox for the wrap
            // decision below.
            string ValidateOverlayUvs(
                System.Windows.Point[] uvArr, int[] indices, double tilingLimit,
                out double oUmin, out double oUmax, out double oVmin, out double oVmax)
            {
                oUmin = double.PositiveInfinity; oUmax = double.NegativeInfinity;
                oVmin = double.PositiveInfinity; oVmax = double.NegativeInfinity;
                for (int ri = 0; ri < indices.Length; ri++)
                {
                    int vi = indices[ri];
                    if (vi < 0 || vi >= uvArr.Length) continue;
                    double u = uvArr[vi].X, v = uvArr[vi].Y;
                    if (u < oUmin) oUmin = u; if (u > oUmax) oUmax = u;
                    if (v < oVmin) oVmin = v; if (v > oVmax) oVmax = v;
                }
                if (double.IsPositiveInfinity(oUmin)) return "no used verts";

                // (a) tiling beyond the limit — not decal UVs.
                if (oUmax >= tilingLimit || oVmax >= tilingLimit ||
                    oUmin < -tilingLimit || oVmin < -tilingLimit)
                    return $"TILING bbox=[{oUmin:F3},{oUmax:F3}]x[{oVmin:F3},{oVmax:F3}]";

                // (b) FIX 34: degenerate single-point park. 2026 content
                // parks unused decal verts at (0,1)/(1,1) — corners the
                // bottom-right-only check misses. A one-point UV region
                // stretches a single atlas pixel across the panel AND
                // punches depth holes over real decals beneath it.
                const double kDegenerateSpan = 0.002;
                if (oUmax - oUmin < kDegenerateSpan && oVmax - oVmin < kDegenerateSpan)
                    return $"single-point park at ({oUmin:F3},{oVmin:F3})";

                // (c) FIX 20f: all verts parked at the transparent
                // bottom-right corner of the atlas.
                const double kCornerThreshold = 0.97;
                bool anyReal = false;
                for (int ri = 0; ri < indices.Length; ri++)
                {
                    int vi = indices[ri];
                    if (vi < 0 || vi >= uvArr.Length) continue;
                    if (uvArr[vi].X < kCornerThreshold || uvArr[vi].Y < kCornerThreshold)
                    {
                        anyReal = true;
                        break;
                    }
                }
                if (!anyReal) return "all UVs corner-parked (transparent)";

                return null;
            }

            // FIX 34 (v3): PER-TRIANGLE decal filtering.
            //
            // A single submesh can mix sponsor-bearing faces (pair-2 UVs
            // inside [0,1], pointing at real atlas content) with faces
            // whose pair-2 is a tiled copy of the base carbon UV (2026
            // Red Bull rearwing_top.carbon_tile_2: sponsor faces sit at
            // the giant rotated "Gate" banner around [0,0.31]x[0.5,1]
            // while carbon faces run out to u=2.545 — the union bbox is
            // what the old whole-submesh check saw and rejected). Any
            // whole-submesh accept/reject is therefore the wrong
            // altitude:
            //   * reject  -> sponsor faces lose their decal (missing
            //                bottom half of the rear wing banner);
            //   * accept+wrap -> carbon faces sample random atlas rows
            //                (the "random parts of the decal" symptom).
            //
            // Instead: keep only triangles whose three vertices all sit
            // inside [0,1] (small tolerance), drop the rest. Validity
            // checks (degenerate park / corner park) then run on the
            // KEPT subset only.
            int[] FilterOverlayTris(System.Windows.Point[] uvArr,
                                    double kLo = -0.05, double kHi = 1.05)
            {
                var keep = new List<int>(rawIndices.Length);
                for (int t = 0; t + 2 < rawIndices.Length; t += 3)
                {
                    bool ok = true;
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = rawIndices[t + k];
                        if (vi < 0 || vi >= uvArr.Length) { ok = false; break; }
                        var p = uvArr[vi];
                        if (p.X < kLo || p.X > kHi || p.Y < kLo || p.Y > kHi)
                        { ok = false; break; }
                    }
                    if (ok)
                    {
                        keep.Add(rawIndices[t]);
                        keep.Add(rawIndices[t + 1]);
                        keep.Add(rawIndices[t + 2]);
                    }
                }
                return keep.ToArray();
            }

            System.Windows.Point[] sponsorUvs = null;
            int[] overlayIndices = null;
            string pickedUvSource = null;
            bool overlayNeedsWrap = false;
            bool overlayWrapUvs = false;
            int[] seamFaces = Array.Empty<int>();

            var priUvs = DecodeUvStream(
                vbufs, sm.UvBufferIndex, sm.VOffset, maxIndex,
                useSecondaryUvPair: true, out _);
            if (priUvs != null) NormalizeUvTileOffset(priUvs, rawIndices);
            string priReject;
            if (priUvs == null)
            {
                priReject = "decode failed";
            }
            else
            {
                int[] kept = FilterOverlayTris(priUvs);
                priReject = kept.Length == 0
                    ? "no in-range triangles"
                    : ValidateOverlayUvs(priUvs, kept, 2.0, out _, out _, out _, out _);

                // ---- EVERY FACE, ALWAYS --------------------------------
                //
                // Sort the submesh's faces into two buckets and keep both.
                //
                //   tile-coherent -- three corners in the same tile, so WRAP
                //                    resolves them exactly and they can share
                //                    vertices and be folded per vertex.
                //   seam          -- corners in different tiles. They get
                //                    private vertex copies at mesh build and
                //                    are folded per face instead.
                //
                // Between them that is every face the submesh has. Nothing is
                // discarded for being out of range: on the GPU those faces are
                // not out of range at all, the sampler is in WRAP, and the
                // overlay brush is already TileMode=Tile on an Absolute 0,0,1,1
                // viewport so WPF resolves them identically.
                var tiles = new HashSet<long>();
                var wideList = new List<int>(rawIndices.Length);
                var seamList = new List<int>();
                for (int t = 0; t + 2 < rawIndices.Length; t += 3)
                {
                    int a2 = rawIndices[t], b2 = rawIndices[t + 1], c2 = rawIndices[t + 2];
                    if (a2 < 0 || a2 >= priUvs.Length ||
                        b2 < 0 || b2 >= priUvs.Length ||
                        c2 < 0 || c2 >= priUvs.Length) continue;

                    int tu = TileIndex(priUvs[a2].X), tv = TileIndex(priUvs[a2].Y);
                    if (TileIndex(priUvs[b2].X) != tu || TileIndex(priUvs[c2].X) != tu ||
                        TileIndex(priUvs[b2].Y) != tv || TileIndex(priUvs[c2].Y) != tv)
                    {
                        seamList.Add(a2); seamList.Add(b2); seamList.Add(c2);
                        continue;
                    }
                    tiles.Add(((long)tu << 32) ^ (uint)tv);
                    wideList.Add(a2); wideList.Add(b2); wideList.Add(c2);
                }
                int[] wide = wideList.ToArray();
                int allFaces = (wide.Length + seamList.Count) / 3;

                if (OverlayWrapRecovery && !isNumberOverlay &&
                    (priReject != null || wide.Length > kept.Length || seamList.Count > 0))
                {
                    // Validate on the WRAPPED coordinates -- raw, a set spread
                    // over several tiles reads as a span of 2 or 3 and trips the
                    // tiling limit. Wrapped is what actually gets sampled.
                    var wrappedUvs = new System.Windows.Point[priUvs.Length];
                    for (int k = 0; k < priUvs.Length; k++)
                        wrappedUvs[k] = new System.Windows.Point(
                            WrapUv(priUvs[k].X), WrapUv(priUvs[k].Y));

                    string wideReject = wide.Length == 0
                        ? "no tile-coherent faces"
                        : ValidateOverlayUvs(wrappedUvs, wide, 2.0, out _, out _, out _, out _);

                    // OverlayRejectNothing: build the overlay even when the
                    // wrapped set still fails. The validator's job was to stop
                    // a bad UV set painting nonsense, but a parked set samples a
                    // transparent corner of the atlas and therefore draws
                    // nothing -- the cost of keeping it is a no-op quad, while
                    // the cost of rejecting it is a sponsor that never appears.
                    // Given the choice, draw it.
                    bool overrideReject = wideReject != null && OverlayRejectNothing;

                    if (wideReject == null || overrideReject)
                    {
                        Log($"     overlay all-faces '{sm.Name}': " +
                            $"{kept.Length / 3} -> {allFaces} face(s) " +
                            $"({wide.Length / 3} tile-coherent across {tiles.Count} tile(s), " +
                            $"{seamList.Count / 3} seam)" +
                            (priReject != null ? $" -- was '{priReject}'" : "") +
                            (overrideReject ? $" -- validator said '{wideReject}', kept anyway" : ""));

                        kept = wide;
                        seamFaces = seamList.ToArray();
                        overlayWrapUvs = true;
                        priReject = null;
                    }
                }

                if (priReject == null)
                {
                    sponsorUvs = priUvs;
                    overlayIndices = kept;
                    pickedUvSource = "vb3-pair2";
                    if ((kept.Length + seamFaces.Length) < rawIndices.Length)
                        Log($"     overlay tri-filter '{sm.Name}': kept " +
                            $"{(kept.Length + seamFaces.Length) / 3}/{rawIndices.Length / 3} triangles");
                }
            }

            if (sponsorUvs == null &&
                (priReject == "no in-range triangles" || priReject == "decode failed" ||
                 // NUMBER submeshes: a parked primary is NOT proof of
                 // "no content" -- the digit must render somewhere, and
                 // several 2021 cars park pair-2 on ONE side of the car
                 // while the digit UVs live in vb4 (AlphaTauri right
                 // sidepod). Sponsor overlays keep the strict rule
                 // (parked primary = skip; vb4 is vestigial for them).
                 (isNumberOverlay && priReject != null &&
                  (priReject.StartsWith("single-point park") ||
                   priReject == "all UVs corner-parked (transparent)"))))
            {
                // FIX 34 diagnostic: dump the USED-vertex bbox of every
                // candidate UV stream so a misrouted decal can be diagnosed
                // from the log alone (which stream holds the region that
                // lines up with the neighbouring body panel's decal?).
                void DumpUvCandidate(string lbl, int bufIdx, bool secondary)
                {
                    if (bufIdx < 0 || bufIdx >= vbufs.Count)
                    { Log($"       uv-dump {lbl}: n/a"); return; }
                    var d = DecodeUvStream(vbufs, bufIdx, sm.VOffset, maxIndex, secondary, out _);
                    if (d == null) { Log($"       uv-dump {lbl}: decode failed"); return; }
                    double duMin = double.PositiveInfinity, duMax = double.NegativeInfinity;
                    double dvMin = double.PositiveInfinity, dvMax = double.NegativeInfinity;
                    for (int ri = 0; ri < rawIndices.Length; ri++)
                    {
                        int vi = rawIndices[ri];
                        if (vi < 0 || vi >= d.Length) continue;
                        var p = d[vi];
                        if (p.X < duMin) duMin = p.X; if (p.X > duMax) duMax = p.X;
                        if (p.Y < dvMin) dvMin = p.Y; if (p.Y > dvMax) dvMax = p.Y;
                    }
                    Log($"       uv-dump {lbl}(vb{bufIdx}): bbox=[{duMin:F3},{duMax:F3}]x[{dvMin:F3},{dvMax:F3}]");
                }
                DumpUvCandidate("pair1", sm.UvBufferIndex, false);
                DumpUvCandidate("pair2", sm.UvBufferIndex, true);
                DumpUvCandidate("decalbuf", sm.UvBufferIndex2, true);
                DumpUvCandidate("uv3-pair1", sm.UvBufferIndex3, false);
                DumpUvCandidate("uv3-pair2", sm.UvBufferIndex3, true);

                // Alternate decode of the decal buffer: unorm16 pairs
                // instead of half-floats, plus raw hex of a few used
                // verts so the true encoding can be judged by eye.
                if (sm.UvBufferIndex2 >= 0 && sm.UvBufferIndex2 < vbufs.Count)
                {
                    var dvb = vbufs[sm.UvBufferIndex2];
                    try
                    {
                        byte[] raw = File.ReadAllBytes(dvb.DataPath);
                        long baseOfs = (long)sm.VOffset * dvb.Stride;
                        double auMin = double.PositiveInfinity, auMax = double.NegativeInfinity;
                        double avMin = double.PositiveInfinity, avMax = double.NegativeInfinity;
                        var seen = new HashSet<int>();
                        var hexSamples = new List<string>();
                        for (int ri = 0; ri < rawIndices.Length; ri++)
                        {
                            int vi = rawIndices[ri];
                            if (vi < 0 || !seen.Add(vi)) continue;
                            long o = baseOfs + (long)vi * dvb.Stride;
                            if (o + 4 > raw.LongLength) continue;
                            double u = BitConverter.ToUInt16(raw, (int)o) / 65535.0;
                            double v = BitConverter.ToUInt16(raw, (int)o + 2) / 65535.0;
                            if (u < auMin) auMin = u; if (u > auMax) auMax = u;
                            if (v < avMin) avMin = v; if (v > avMax) avMax = v;
                            if (hexSamples.Count < 8)
                                hexSamples.Add($"{BitConverter.ToUInt16(raw, (int)o):X4},{BitConverter.ToUInt16(raw, (int)o + 2):X4}");
                        }
                        Log($"       uv-dump decalbuf-as-unorm16: bbox=[{auMin:F3},{auMax:F3}]x[{avMin:F3},{avMax:F3}] raw={string.Join(" ", hexSamples)}");
                    }
                    catch (Exception ex)
                    {
                        Log($"       uv-dump decalbuf-as-unorm16: failed {ex.Message}");
                    }
                }

                if (sm.UvBufferIndex2 >= 0 && sm.UvBufferIndex2 < vbufs.Count &&
                    sm.UvBufferIndex2 != sm.UvBufferIndex)
                {
                    var fbUvs = DecodeUvStream(
                        vbufs, sm.UvBufferIndex2, sm.VOffset, maxIndex,
                        useSecondaryUvPair: true, out _);
                    if (fbUvs != null) NormalizeUvTileOffset(fbUvs, rawIndices);
                    int[] fbKept = fbUvs == null ? Array.Empty<int>() : FilterOverlayTris(fbUvs);
                    string fbReject =
                        fbUvs == null ? "decode failed" :
                        fbKept.Length == 0 ? "no in-range triangles" :
                        ValidateOverlayUvs(fbUvs, fbKept, 2.0, out _, out _, out _, out _);
                    // AUDIT: a driver number needs a real atlas island.
                    // The generic 0.002 single-point-park threshold let
                    // vestigial vb4 slivers through (AlphaTauri right
                    // sidepod: 0.064 x 0.011 -- renders as a garbage
                    // strip). For NUMBER overlays require ~0.07 span in
                    // both axes (the UniversalExporter's number-degeneracy
                    // heuristic); anything thinner is authored-dead and
                    // the side legitimately has no number (the game
                    // mirrors the opposite part at render time).
                    if (fbReject == null && isNumberOverlay)
                    {
                        double nuMin = double.PositiveInfinity, nuMax = double.NegativeInfinity;
                        double nvMin = double.PositiveInfinity, nvMax = double.NegativeInfinity;
                        foreach (int vi in fbKept)
                        {
                            double u = fbUvs[vi].X, v = fbUvs[vi].Y;
                            if (u < nuMin) nuMin = u; if (u > nuMax) nuMax = u;
                            if (v < nvMin) nvMin = v; if (v > nvMax) nvMax = v;
                        }
                        if (nuMax - nuMin < 0.07 || nvMax - nvMin < 0.07)
                            fbReject = $"vestigial number strip " +
                                       $"({nuMax - nuMin:F3}x{nvMax - nvMin:F3} span)";
                    }
                    if (fbReject == null)
                    {
                        sponsorUvs = fbUvs;
                        overlayIndices = fbKept;
                        pickedUvSource = "vb4-decal";
                        Log($"     overlay UV fallback '{sm.Name}': vb4-decal " +
                            $"kept {fbKept.Length / 3}/{rawIndices.Length / 3} triangles " +
                            $"— primary rejected: {priReject}");
                    }
                    else
                    {
                        Log($"     ~ overlay SKIP '{sm.Name}': vb3-pair2: {priReject}; vb4-decal: {fbReject}");
                        StashParkedExportUv(priUvs);
                        return null;
                    }
                }
                else
                {
                    Log($"     ~ overlay SKIP '{sm.Name}': vb3-pair2: {priReject} (no vb4 buffer)");
                    StashParkedExportUv(priUvs);
                    return null;
                }
            }
            else if (sponsorUvs == null)
            {
                Log($"     ~ overlay SKIP '{sm.Name}': vb3-pair2: {priReject}");
                Log($"       {DescribeUvTiles(priUvs, rawIndices)}");
                StashParkedExportUv(priUvs);
                return null;
            }

            // 4. Use the pre-decoded overlay brush from the resolver. No
            //    filename sniffing, no second material lookup.
            // Typed as Brush (not ImageBrush) so the diagnostic override
            // below can substitute a SolidColorBrush; DiffuseMaterial
            // accepts any Brush subclass.
            //
            // Substitute with the hi-res mipmaps-sidecar brush when one
            // exists for this overlay texture (e.g. the high-res driver
            // number atlas at 1024x512 instead of the embedded 64x32
            // tail mip). Falls through to resolved.OverlayBrush untouched
            // when no sidecar is present.
            System.Windows.Media.Brush brush = HighResBrushProvider.WrapBrush(
                resolved.OverlayTextureEaid, resolved.OverlayBrush, _textureCache, Log);
            if (brush == null) { Log($"     ~ overlay SKIP '{sm.Name}': OverlayBrush is null (texture decode failed?)"); return null; }

            // FIX 34 wrap: emulate GPU WRAP sampling for vb=4 UVs that
            // extend past [0,1]. WPF stretches a material brush across the
            // 2D bounding box of the mesh's ENTIRE TextureCoordinates
            // collection (not [0,1]!), so one atlas tile in brush space is
            // 1/span of that bbox. Setting the viewport to exactly one UV
            // unit with TileMode=Tile makes u=2.545 sample atlas u=0.545,
            // matching the game's sampler.
            if (overlayNeedsWrap && brush is System.Windows.Media.TileBrush tileSrc)
            {
                double fuMin = double.PositiveInfinity, fuMax = double.NegativeInfinity;
                double fvMin = double.PositiveInfinity, fvMax = double.NegativeInfinity;
                for (int k = 0; k < sponsorUvs.Length; k++)
                {
                    double u = sponsorUvs[k].X, v = sponsorUvs[k].Y;
                    if (u < fuMin) fuMin = u; if (u > fuMax) fuMax = u;
                    if (v < fvMin) fvMin = v; if (v > fvMax) fvMax = v;
                }
                double spanU = fuMax - fuMin, spanV = fvMax - fvMin;
                if (spanU > 1e-6 && spanV > 1e-6)
                {
                    var tiled = (System.Windows.Media.TileBrush)tileSrc.Clone();
                    tiled.TileMode = System.Windows.Media.TileMode.Tile;
                    tiled.ViewportUnits = System.Windows.Media.BrushMappingMode.RelativeToBoundingBox;
                    tiled.Viewport = new System.Windows.Rect(
                        -fuMin / spanU, -fvMin / spanV, 1.0 / spanU, 1.0 / spanV);
                    tiled.Freeze();
                    brush = tiled;
                    Log($"     overlay wrap-brush: full bbox=[{fuMin:F3},{fuMax:F3}]x[{fvMin:F3},{fvMax:F3}] viewport=1/{spanU:F3}x1/{spanV:F3}");
                }
            }

            // =================================================================
            // DIAGNOSTIC MODE (temporary): force number overlays to render as
            // solid opaque red, bypassing the driver atlas brush. Flip
            // DebugSolidRedOverlays to false to restore normal rendering.
            //
            // Purpose: the number isn't visible on the car despite every log
            // signal saying the overlay pipeline is working. Rendering the
            // overlay with an unambiguous solid colour answers the binary
            // question "is the overlay mesh actually being rasterised?":
            //
            //   * Red patches appear where numbers should be → overlay IS
            //     rendering. The invisibility is caused by the driver atlas's
            //     mid-grey RGB (133,133,133) blending into the lit paint.
            //     Fix path: restore DecalCompositor-style bake onto an opaque
            //     paint/carbon sample so there's no alpha at render time.
            //
            //   * No red patches → overlay is NOT being rasterised. The
            //     problem is in WPF scene graph / depth / material setup,
            //     below the texture pipeline we've been editing.
            //
            // Remove this block (and set brush back to resolved.OverlayBrush)
            // once the diagnosis is confirmed.
            // =================================================================
            if (DebugSolidRedOverlays)
            {
                var redBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Colors.Red);
                redBrush.Freeze();
                brush = redBrush;
                Log($"     DEBUG: overriding '{sm.Name}' overlay brush with solid RED");
            }
            else if (brush is ImageBrush dbgIb && dbgIb.ImageSource is System.Windows.Media.Imaging.BitmapSource dbgBs)
            {
                // Record what WrapBrush actually handed over. If the overlay
                // never appears, the next question is whether this buffer still
                // carries per-pixel alpha and at what size -- both are visible
                // here without a rebuild.
                Log($"     overlay brush '{sm.Name}': {dbgBs.PixelWidth}x{dbgBs.PixelHeight} " +
                    $"{dbgBs.Format} opacity={dbgIb.Opacity:F2} tile={dbgIb.TileMode} " +
                    $"viewportUnits={dbgIb.ViewportUnits} viewport={dbgIb.Viewport}");
            }

            // 5. Build offset positions: pos + normal * epsilon. F1 cars are
            //    roughly 5 model units long so 0.0005 is sub-millimetre --
            //    invisible but enough to win the depth test cleanly.
            //    FIX 21: number overlays use a larger offset (0.002) because
            //    the number base mesh z-fights with the body underneath.
            // ---- DEPTH CLEARANCE IS DEFERRED -------------------------------
            //
            // No displacement is applied here. The overlay is built coincident
            // with the surface it was copied from, tagged with the direction it
            // should later move in, and displaced once by
            // ApplyOverlayDepthOffset after the whole model exists.
            //
            // It has to work that way because the right distance is not a
            // property of this submesh. Whether an overlay wins its depth test
            // depends on how finely the depth buffer resolves the scene the
            // camera is framing, and ZoomExtents frames the whole model -- so
            // the clearance scales with the model, which is not known until the
            // last part is loaded. A literal distance baked in here is correct
            // for exactly one content scale and silently wrong for every other:
            // a classic car authored in different units, a single wing loaded
            // on its own, anything that is not a full modern chassis.

            // ---- WHICH WAY IS OUT? (the one-sided decal) -------------------
            //
            // Pushing along +N is the only direction-sensitive step in this
            // whole method, and it is the only thing that can make a decal
            // appear on one side of the car and not the other. Everything
            // upstream is provably side-blind: for the SF-25, suspension_fl
            // and suspension_fr resolve to the same streams (pos=vb5, uv2=vb9
            // Fvf29), the same decal bbox [0.030,0.132]x[0.427,0.462], the
            // same 293/293 triangles and the same 4096x4096 brush. Ditto
            // wing_mirror_l / _r on [0.467,0.591]x[0.490,0.660]. The loader
            // cannot tell the sides apart, so the asymmetry has to be here.
            //
            // Codemasters mirrors a part by negating one position axis. If the
            // stored normals are not flipped to match -- and on mirrored parts
            // they frequently are not -- the normal points INTO the body on the
            // mirrored side. The overlay then sinks half a millimetre below the
            // panel instead of sitting above it, loses the depth test to the
            // base submesh it was copied from, and vanishes. Same UVs, same
            // texture, same triangles: visible on one side, gone on the other.
            //
            // So measure the direction rather than assume it. This moves the
            // overlay along its own surface normal only -- it cannot change
            // which texels are sampled, so no UV, no atlas region and no
            // placement is touched.
            //
            // v17.2: the measurement is now an area-weighted vote of each
            // face's winding normal against its authored vertex normals.
            //
            // The previous test -- mean dot(N, P - centroid) over the used
            // vertices -- only answers "outward?" for a convex, closed shell.
            // Decal overlays are rarely that: they are strips, open panels and
            // tubes. On a tube the centroid sits in empty space, the dot
            // products cancel around the cross-section, and the sign of the
            // (near-zero) mean is noise. On the F1 25 Ferrari it flipped 17
            // overlays inward, including safetycell.carbon_tile_2 -- the HALO
            // RING, mean dot -0.0606 -- whose aws / Shell / IBM / Richard
            // Mille / Bitdefender decals were then lifted ~2 mm INTO the halo
            // tube and lost the depth test on every view. Also flipped:
            // frontwing_fl/fr_01.body, driver_surroun.body, rearwing_top.detail,
            // rearwingmoving_top.detail, floor_damage_l/r.detail, ...
            //
            // Measured on all 157 LOD0 submeshes of that car, the authored
            // normals agree with the triangle winding on >= 99.8% of the
            // surface area, every one of them. The winding vote reads that
            // agreement directly and is shape-independent, and it still
            // catches the case this block exists for: a part mirrored by
            // negating one position axis reverses its winding, so if its
            // normals were not flipped to match, the vote comes out negative
            // and the offset is flipped exactly as before. The centroid test is
            // kept only as a fallback when the vote has no area to go on.
            double offsetSign = 1.0;
            {
                double agreeArea = 0, disagreeArea = 0;
                foreach (int[] set in new[] { overlayIndices, seamFaces })
                {
                    for (int t = 0; t + 2 < set.Length; t += 3)
                    {
                        int a = set[t], b = set[t + 1], c = set[t + 2];
                        if (a < 0 || b < 0 || c < 0 ||
                            a >= positions.Length || b >= positions.Length || c >= positions.Length ||
                            a >= normals.Length || b >= normals.Length || c >= normals.Length)
                            continue;
                        var pa = positions[a]; var pb = positions[b]; var pc = positions[c];
                        double e1x = pb.X - pa.X, e1y = pb.Y - pa.Y, e1z = pb.Z - pa.Z;
                        double e2x = pc.X - pa.X, e2y = pc.Y - pa.Y, e2z = pc.Z - pa.Z;
                        double fx = e1y * e2z - e1z * e2y;
                        double fy = e1z * e2x - e1x * e2z;
                        double fz = e1x * e2y - e1y * e2x;
                        double area = Math.Sqrt(fx * fx + fy * fy + fz * fz);
                        if (!(area > 0)) continue;
                        var na = normals[a]; var nb = normals[b]; var nc = normals[c];
                        double d = fx * (na.X + nb.X + nc.X) +
                                   fy * (na.Y + nb.Y + nc.Y) +
                                   fz * (na.Z + nb.Z + nc.Z);
                        if (d > 0) agreeArea += area;
                        else if (d < 0) disagreeArea += area;
                    }
                }

                double voteTotal = agreeArea + disagreeArea;
                if (voteTotal > 0)
                {
                    double agreeFrac = agreeArea / voteTotal;
                    bool inward = agreeFrac < 0.5;
                    if (inward && OverlayAutoFlipOffset) offsetSign = -1.0;
                    Log($"     overlay normal-check '{sm.Name}': " +
                        $"winding agreement {agreeFrac:P1} by area -> " +
                        (inward
                            ? (OverlayAutoFlipOffset
                                ? "normals oppose winding (mirrored part), offset FLIPPED"
                                : "normals oppose winding, offset NOT flipped (auto-flip off)")
                            : "normals match winding, offset unchanged"));
                }
                else
                {
                    var usedVerts = new HashSet<int>(overlayIndices);
                    foreach (int vi in seamFaces) usedVerts.Add(vi);
                    double cx = 0, cy = 0, cz = 0;
                    int cn = 0;
                    foreach (int vi in usedVerts)
                    {
                        if (vi < 0 || vi >= positions.Length) continue;
                        cx += positions[vi].X; cy += positions[vi].Y; cz += positions[vi].Z;
                        cn++;
                    }
                    if (cn > 0)
                    {
                        cx /= cn; cy /= cn; cz /= cn;
                        double dotSum = 0;
                        int dn = 0;
                        foreach (int vi in usedVerts)
                        {
                            if (vi < 0 || vi >= positions.Length || vi >= normals.Length) continue;
                            var pv = positions[vi];
                            var nv = normals[vi];
                            dotSum += (pv.X - cx) * nv.X + (pv.Y - cy) * nv.Y + (pv.Z - cz) * nv.Z;
                            dn++;
                        }
                        if (dn > 0)
                        {
                            double meanDot = dotSum / dn;
                            bool inward = meanDot < 0;
                            if (inward && OverlayAutoFlipOffset) offsetSign = -1.0;
                            Log($"     overlay normal-check '{sm.Name}': " +
                                $"mean dot(N,P-centroid)={meanDot:F6} over {dn} vert(s) -> " +
                                (inward
                                    ? (OverlayAutoFlipOffset
                                        ? "normals point INWARD, offset FLIPPED"
                                        : "normals point INWARD, offset NOT flipped (auto-flip off)")
                                    : "normals point outward, offset unchanged") +
                                " [centroid fallback: no face area to vote on]");
                        }
                    }
                }
            }
            // Coincident for now; ApplyOverlayDepthOffset moves it later.
            int posCount = positions.Length;
            var offsetPositions = new Point3D[posCount];
            Array.Copy(positions, offsetPositions, posCount);

            // FIX 20e: use raw pair-2 UVs with NO modifications.
            //
            // The vb=4 decal UVs are authored by Codemasters' pipeline to
            // sample me_logoMap directly — each vertex's UV points at the
            // exact pixel on the decal atlas where its sponsor content sits.
            // The shader does NOT apply scale/bias or me_uOffset to the
            // pair-2 stream; those parameters affect the pair-1 paint UVs
            // or other shader passes. Applying any transform here shifted
            // every sponsor to the wrong atlas region.
            if (overlayIndices.Length == 0 && seamFaces.Length == 0)
            {
                Log($"     ~ overlay SKIP '{sm.Name}': no faces at all");
                return null;
            }

            System.Windows.Point[] shiftedUvs = sponsorUvs;

            // 6. Assemble the overlay mesh.
            // FIX 20: log the final UV bbox for the overlay (USED verts only)
            {
                double fUmin = double.PositiveInfinity, fUmax = double.NegativeInfinity;
                double fVmin = double.PositiveInfinity, fVmax = double.NegativeInfinity;
                foreach (int[] set in new[] { overlayIndices, seamFaces })
                    for (int ri = 0; ri < set.Length; ri++)
                    {
                        int vi = set[ri];
                        if (vi >= 0 && vi < shiftedUvs.Length)
                        {
                            double u = shiftedUvs[vi].X, v = shiftedUvs[vi].Y;
                            if (u < fUmin) fUmin = u; if (u > fUmax) fUmax = u;
                            if (v < fVmin) fVmin = v; if (v > fVmax) fVmax = v;
                        }
                    }
                Log($"     overlay OK '{sm.Name}' [{pickedUvSource}]: final UV=[{fUmin:F3},{fUmax:F3}]x[{fVmin:F3},{fVmax:F3}]");

                // Probe what is actually sampled. When faces were recovered the
                // raw bbox runs past 1 and describes tile space, not the sheet,
                // so measure the wrapped bounds instead -- otherwise the probe
                // reports a region the overlay never reads.
                double pUmin = fUmin, pUmax = fUmax, pVmin = fVmin, pVmax = fVmax;
                if (overlayWrapUvs)
                {
                    pUmin = double.PositiveInfinity; pUmax = double.NegativeInfinity;
                    pVmin = double.PositiveInfinity; pVmax = double.NegativeInfinity;
                    foreach (int[] set in new[] { overlayIndices, seamFaces })
                        for (int ri = 0; ri < set.Length; ri++)
                        {
                            int vi = set[ri];
                            if (vi < 0 || vi >= shiftedUvs.Length) continue;
                            double u = WrapUv(shiftedUvs[vi].X), v = WrapUv(shiftedUvs[vi].Y);
                            if (u < pUmin) pUmin = u; if (u > pUmax) pUmax = u;
                            if (v < pVmin) pVmin = v; if (v > pVmax) pVmax = v;
                        }
                    Log($"     overlay wrapped UV '{sm.Name}': " +
                        $"[{pUmin:F3},{pUmax:F3}]x[{pVmin:F3},{pVmax:F3}]");
                }
                ProbeOverlayAlpha(brush, sm.Name, pUmin, pUmax, pVmin, pVmax);
            }

            // FIX 34c: WPF stretches a 3D material brush across the
            // bounding box of the ENTIRE TextureCoordinates collection,
            // not [0,1]. Historically this worked by luck: corner-parked
            // verts near (0.99,0.99) plus content near 0 kept the bbox
            // ≈[0,1]. With triangle filtering, DROPPED verts' tiled UVs
            // (u up to 2.545) would blow the bbox up and squeeze the
            // decal. Clamp every UV into [0,1] (kept tris are within the
            // ±0.05 tolerance anyway) and pin the bbox to exactly [0,1]
            // with two sentinel vertices no triangle references.
            // Seam faces get their own private copies of their three vertices,
            // appended past the sentinels. A seam face cannot be folded per
            // vertex -- its corners belong to different tiles, and a shared
            // vertex cannot hold two answers at once -- but with its own copies
            // it can be folded per FACE: shift all three corners by the same
            // tile, which keeps the triangle intact and lands it on the right
            // patch of atlas. Duplicating rather than editing in place is what
            // keeps the neighbouring faces that share those vertices unaffected.
            int uvCount = shiftedUvs.Length;
            bool canDupSeams = seamFaces.Length > 0 &&
                               offsetPositions.Length == uvCount &&
                               normals.Length >= uvCount;
            int seamVerts = canDupSeams ? seamFaces.Length : 0;

            var uvFixed = new System.Windows.Point[uvCount + 2 + seamVerts];
            for (int k = 0; k < uvCount; k++)
            {
                double cu = shiftedUvs[k].X, cv = shiftedUvs[k].Y;

                // Recovered faces live one tile over -- topcanopy_bm.body runs
                // out to u=1.928 once its 409 faces are back. Clamping pins
                // every one of them to the right edge of the sheet, which is
                // why restoring the geometry alone changed nothing on screen.
                // Fold them back into the tile the way the game's WRAP sampler
                // does. Whole-tile translation only: no flip, no rotation, no
                // scaling, and values already inside [0,1] are untouched.
                if (overlayWrapUvs)
                {
                    cu = WrapUv(cu);
                    cv = WrapUv(cv);
                }
                else
                {
                    if (cu < 0) cu = 0; else if (cu > 1) cu = 1;
                    if (cv < 0) cv = 0; else if (cv > 1) cv = 1;
                }
                uvFixed[k] = new System.Windows.Point(cu, cv);
            }
            uvFixed[uvCount] = new System.Windows.Point(0, 0);
            uvFixed[uvCount + 1] = new System.Windows.Point(1, 1);

            var posFixed = new Point3D[offsetPositions.Length + 2 + seamVerts];
            Array.Copy(offsetPositions, posFixed, offsetPositions.Length);
            posFixed[offsetPositions.Length] = offsetPositions.Length > 0 ? offsetPositions[0] : new Point3D();
            posFixed[offsetPositions.Length + 1] = posFixed[offsetPositions.Length];

            var nrmFixed = new Vector3D[normals.Length + 2 + seamVerts];
            Array.Copy(normals, nrmFixed, normals.Length);

            int[] meshIndices = overlayIndices;
            if (canDupSeams)
            {
                var withSeams = new List<int>(overlayIndices.Length + seamFaces.Length);
                withSeams.AddRange(overlayIndices);

                int next = uvCount + 2;
                for (int f = 0; f + 2 < seamFaces.Length; f += 3)
                {
                    int a = seamFaces[f], b = seamFaces[f + 1], c = seamFaces[f + 2];

                    // One tile for the whole face: the lowest its corners reach.
                    int tu = Math.Min(TileIndex(shiftedUvs[a].X),
                             Math.Min(TileIndex(shiftedUvs[b].X), TileIndex(shiftedUvs[c].X)));
                    int tv = Math.Min(TileIndex(shiftedUvs[a].Y),
                             Math.Min(TileIndex(shiftedUvs[b].Y), TileIndex(shiftedUvs[c].Y)));

                    foreach (int src in new[] { a, b, c })
                    {
                        double cu = shiftedUvs[src].X - tu;
                        double cv = shiftedUvs[src].Y - tv;
                        if (cu < 0) cu = 0; else if (cu > 1) cu = 1;
                        if (cv < 0) cv = 0; else if (cv > 1) cv = 1;

                        uvFixed[next] = new System.Windows.Point(cu, cv);
                        posFixed[next] = offsetPositions[src];
                        nrmFixed[next] = normals[src];
                        withSeams.Add(next);
                        next++;
                    }
                }
                meshIndices = withSeams.ToArray();
                Log($"     overlay seam-faces '{sm.Name}': recovered {seamFaces.Length / 3} " +
                    $"face(s) via {seamVerts} duplicated vertices");
            }

            var overlayMesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(posFixed),
                Normals = new Vector3DCollection(nrmFixed),
                TriangleIndices = new Int32Collection(meshIndices),
                TextureCoordinates = new PointCollection(uvFixed),
            };

            // Register the overlay mesh with a slot name suffixed "_overlay".
            // This is the key BuildDecalUvSidecar (in F1FbxExporter) uses to
            // pair an overlay's UV0 with its base submesh and emit it as
            // TEXCOORD_1 on the BASE primitive in the exported FBX. Without
            // this registration the modern loader's sponsor/number overlay
            // path produced overlay geometry that the exporter could see as
            // a separate mesh but couldn't ATTACH back onto the paint base,
            // so the FBX shipped paint materials with only one UV channel
            // (no decal UV) -- the symptom the user reported.
            //
            // Also wire the DECAL TEXTURE EAID through MeshSubmeshRegistry.
            // The exporter's EnsureTexturePng pipeline looks the texture EAID
            // up off the mesh, decodes it to a PNG sidecar next to the FBX,
            // and binds it to the Assimp material's diffuse slot. Without
            // SetTextureEaid the overlay primitive shipped with no diffuse
            // texture at all -- the decal PNG never made it to disk.
            MeshSubmeshRegistry.Register(overlayMesh, sm.Name + "_overlay", sm.DescriptorHint);
            if (!string.IsNullOrEmpty(resolved.OverlayTextureEaid))
                MeshSubmeshRegistry.SetTextureEaid(overlayMesh, resolved.OverlayTextureEaid);

            // FIX 34d: hand the exporter the AUTHENTIC decal UVs. The
            // uvFixed collection above is preview-only (clamped + two
            // sentinel verts for WPF brush mapping); the FBX decal
            // sidecar pairs UVs to the BASE mesh by vertex count and
            // must see the raw stream values, one per original vertex.
            //
            // FIX 34e: ...with one exception. The BASE mesh keeps ALL
            // triangles, including the ones the per-triangle filter
            // dropped from the overlay (carbon faces whose pair-2 is a
            // tiled copy, u up to 2.5+). Passing their raw tiled UVs
            // through TEXCOORD_1 makes those faces sample random atlas
            // content in Blender ("messed up decal" on 2026 Mercedes
            // carbon) even though the preview looks right. Park every
            // vertex that no KEPT triangle references at the transparent
            // atlas corner Codemasters themselves park unused decal
            // verts at (observed ~(0.002, 0.998) across 2026 content),
            // so dropped faces sample transparent pixels downstream.
            var exportUvs = (System.Windows.Point[])sponsorUvs.Clone();
            var keptVerts = new HashSet<int>(overlayIndices);
            // Seam faces reach the mesh through duplicates, but their ORIGINAL
            // vertices are still live geometry -- don't park them in the export
            // sidecar as if their faces had been dropped.
            foreach (int vi in seamFaces) keptVerts.Add(vi);
            var parkPoint = new System.Windows.Point(0.002, 0.998);
            int parked = 0;
            for (int k = 0; k < exportUvs.Length; k++)
            {
                if (!keptVerts.Contains(k))
                {
                    exportUvs[k] = parkPoint;
                    parked++;
                }
            }
            if (parked > 0 && overlayIndices.Length < rawIndices.Length)
                Log($"     export decal-UV: parked {parked} dropped-face vert(s) at transparent corner");
            MeshSubmeshRegistry.SetRawDecalUv(overlayMesh,
                new System.Windows.Media.PointCollection(exportUvs));

            // UV VIEW: stash the AUTHORED pair-2 layout -- every base
            // triangle with the unfiltered decal UVs. The overlay mesh
            // only carries the triangles that overlap decal content, so
            // its wireframe hugs the sticker pixels; the authored layout
            // (full box panels etc.) is what the UV view should draw.
            MeshSubmeshRegistry.SetUvViewDecal(overlayMesh,
                new System.Windows.Media.PointCollection(sponsorUvs),
                new System.Windows.Media.Int32Collection(rawIndices));

            var overlayMat = new DiffuseMaterial(brush);
            overlayMat.Freeze();

            var overlayModel = new GeometryModel3D(overlayMesh, overlayMat)
            {
                BackMaterial = overlayMat,
            };
            SetIsOverlay(overlayModel, true);
            SetOverlayOffsetSign(overlayModel, offsetSign);
            sponsorTexShort = ShortenEaid(resolved.OverlayTextureEaid);
            return overlayModel;
        }

        /// <summary>
        /// Tolerance band the strict filter already treated as "on the edge".
        /// Overshoots this small are float noise on a boundary vertex, not a
        /// neighbouring tile, and both helpers below must agree on that or a
        /// face gets flagged as straddling when it is flat on the seam.
        /// </summary>
        private const double kUvEdgeSlack = 0.05;

        /// <summary>
        /// One-line census of where a submesh's decal UVs actually sit.
        ///
        /// A skip currently ends the story: the submesh parks, nothing is built,
        /// and wrap recovery stays silent because it found no extra faces --
        /// which is indistinguishable in the log from recovery never having run.
        /// This closes that gap. If a skipped submesh reports tiles=1 and a
        /// pinpoint bbox, its decal UVs really are parked and no face recovery
        /// can reach them; if it reports several tiles, there is something to go
        /// after and the recovery path needs to explain why it declined.
        /// </summary>
        private static string DescribeUvTiles(System.Windows.Point[] uvs, int[] indices)
        {
            if (uvs == null || indices == null || indices.Length < 3)
                return "uv-census: no data";

            var tiles = new Dictionary<long, int>();
            double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
            double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
            int faces = 0, coherent = 0, inRange = 0;

            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                if (a < 0 || a >= uvs.Length || b < 0 || b >= uvs.Length ||
                    c < 0 || c >= uvs.Length) continue;
                faces++;

                bool near = true;
                foreach (int vi in new[] { a, b, c })
                {
                    double u = uvs[vi].X, v = uvs[vi].Y;
                    if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                    if (v < vMin) vMin = v; if (v > vMax) vMax = v;
                    if (u < -kUvEdgeSlack || u > 1.0 + kUvEdgeSlack ||
                        v < -kUvEdgeSlack || v > 1.0 + kUvEdgeSlack) near = false;
                }
                if (near) inRange++;

                int tu = TileIndex(uvs[a].X), tv = TileIndex(uvs[a].Y);
                if (TileIndex(uvs[b].X) == tu && TileIndex(uvs[c].X) == tu &&
                    TileIndex(uvs[b].Y) == tv && TileIndex(uvs[c].Y) == tv)
                {
                    coherent++;
                    long key = ((long)tu << 32) ^ (uint)tv;
                    tiles.TryGetValue(key, out int n);
                    tiles[key] = n + 1;
                }
            }

            if (faces == 0) return "uv-census: no faces";

            var parts = new List<string>();
            foreach (var kv in tiles)
                parts.Add($"({(int)(kv.Key >> 32)},{(int)(uint)kv.Key})x{kv.Value}");

            return $"uv-census: {faces} face(s), {inRange} in-range, {coherent} tile-coherent, " +
                   $"raw bbox=[{uMin:F3},{uMax:F3}]x[{vMin:F3},{vMax:F3}], " +
                   $"tiles={{{string.Join(" ", parts)}}}";
        }

        /// <summary>Which integer tile a UV coordinate falls in.</summary>
        private static int TileIndex(double x)
        {
            if (x > -kUvEdgeSlack && x < 1.0 + kUvEdgeSlack) return 0;
            double f = Math.Floor(x);
            // An exact integer above zero closes the tile below it rather than
            // opening the next one, so a face edge landing on the seam is not
            // split across two tiles.
            if (x > 0.0 && Math.Abs(x - f) < 1e-6) f -= 1;
            return (int)f;
        }

        /// <summary>
        /// Folds a UV coordinate into [0,1] by whole tiles, as a WRAP sampler
        /// does. Values inside the tile, and edge overshoots within the slack
        /// band, keep the old clamp behaviour untouched.
        /// </summary>
        private static double WrapUv(double x)
        {
            if (x >= 0.0 && x <= 1.0) return x;
            if (x > -kUvEdgeSlack && x < 0.0) return 0.0;
            if (x > 1.0 && x < 1.0 + kUvEdgeSlack) return 1.0;
            double w = x - Math.Floor(x);
            if (w <= 0.0) w = x > 0.0 ? 1.0 : 0.0;
            return w;
        }

        /// <summary>
        /// Reads the atlas pixels an overlay actually samples and reports how
        /// much of that region is opaque.
        ///
        /// Every load-time signal can be green while nothing shows on the car,
        /// and there are only two explanations: the UV region is real but the
        /// overlay isn't rasterising, or the overlay is rasterising a region of
        /// the atlas that happens to be empty. The logs cannot tell those
        /// apart, because a plausible-looking bbox says nothing about what is
        /// printed inside it. This looks.
        ///
        ///   high % opaque  -> the brush and the UVs are both right, and the
        ///                     failure is downstream in rendering. Flip
        ///                     DebugSolidRedOverlays next.
        ///   ~0% opaque     -> the overlay is faithfully painting empty atlas.
        ///                     The UV stream is wrong even though its bbox
        ///                     looked sane, and no renderer change will help.
        ///
        /// Read-only and best-effort: the region is cropped and downsampled to
        /// at most 192px before any pixels are copied, so the cost is trivial
        /// even against the 4096x4096 sidecar, and any failure is logged rather
        /// than thrown.
        /// </summary>
        private static void ProbeOverlayAlpha(System.Windows.Media.Brush brush, string slotName,
            double uMin, double uMax, double vMin, double vMax)
        {
            try
            {
                if (!(brush is ImageBrush ib) ||
                    !(ib.ImageSource is System.Windows.Media.Imaging.BitmapSource src))
                    return;

                int w = src.PixelWidth, h = src.PixelHeight;
                if (w <= 0 || h <= 0) return;

                double cuMin = Math.Max(0, Math.Min(1, uMin)), cuMax = Math.Max(0, Math.Min(1, uMax));
                double cvMin = Math.Max(0, Math.Min(1, vMin)), cvMax = Math.Max(0, Math.Min(1, vMax));

                int x0 = (int)Math.Floor(cuMin * (w - 1));
                int y0 = (int)Math.Floor(cvMin * (h - 1));
                int rw = (int)Math.Ceiling(cuMax * (w - 1)) - x0;
                int rh = (int)Math.Ceiling(cvMax * (h - 1)) - y0;
                if (rw < 1) rw = 1;
                if (rh < 1) rh = 1;
                if (x0 + rw > w) rw = w - x0;
                if (y0 + rh > h) rh = h - y0;
                if (rw < 1 || rh < 1) return;

                System.Windows.Media.Imaging.BitmapSource region =
                    new System.Windows.Media.Imaging.CroppedBitmap(
                        src, new System.Windows.Int32Rect(x0, y0, rw, rh));

                const int kMaxSample = 192;
                if (rw > kMaxSample || rh > kMaxSample)
                {
                    double s = Math.Min((double)kMaxSample / rw, (double)kMaxSample / rh);
                    region = new System.Windows.Media.Imaging.TransformedBitmap(
                        region, new ScaleTransform(s, s));
                }

                var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    region, PixelFormats.Bgra32, null, 0);
                int pw = conv.PixelWidth, ph = conv.PixelHeight;
                if (pw <= 0 || ph <= 0) return;

                var buf = new byte[pw * ph * 4];
                conv.CopyPixels(buf, pw * 4, 0);

                long opaque = 0, partial = 0, r = 0, g = 0, b = 0, aMax = 0;
                for (int i = 0; i + 3 < buf.Length; i += 4)
                {
                    byte a = buf[i + 3];
                    if (a > aMax) aMax = a;
                    if (a >= 128) { opaque++; b += buf[i]; g += buf[i + 1]; r += buf[i + 2]; }
                    else if (a > 8) partial++;
                }

                long total = (long)pw * ph;
                double pct = total > 0 ? 100.0 * opaque / total : 0;
                if (opaque > 0)
                    Log($"     overlay alpha-probe '{slotName}': {rw}x{rh}px region, " +
                        $"{pct:F1}% opaque ({partial} partial, maxA={aMax}), " +
                        $"mean RGB=({r / opaque},{g / opaque},{b / opaque})");
                else
                    Log($"     overlay alpha-probe '{slotName}': {rw}x{rh}px region, " +
                        $"0% opaque ({partial} partial, maxA={aMax}) " +
                        $"-- sampled atlas area carries no decal content");
            }
            catch (Exception ex)
            {
                Log($"     overlay alpha-probe '{slotName}': failed {ex.Message}");
            }
        }

        // =====================================================================
        // ATTRIBUTE DECODERS
        // =====================================================================

        private static Vector3D[] DecodeNormalStream(
            List<VertexBufferInfo> vbufs, int nrmIdx, int baseVertex, int maxIndex)
        {
            if (nrmIdx < 0 || nrmIdx >= vbufs.Count) return null;

            var nvb = vbufs[nrmIdx];
            if (!File.Exists(nvb.DataPath)) return null;

            byte[] data = File.ReadAllBytes(nvb.DataPath);
            long start = (long)baseVertex * nvb.Stride;
            long needed = start + (long)(maxIndex + 1) * nvb.Stride;
            if (needed > data.LongLength) return null;

            var normals = new Vector3D[maxIndex + 1];

            // v17.4: a declared f16x4 normal wins over the header usage value.
            uint normalFmt = nvb.DeclFormat == 35 ? 35u : nvb.Fvf;
            switch (normalFmt)
            {
                case 35:
                    for (int vi = 0; vi <= maxIndex; vi++)
                    {
                        long o = start + (long)vi * nvb.Stride;
                        float nx = ReadHalfFloat(data, (int)o + 0);
                        float ny = ReadHalfFloat(data, (int)o + 2);
                        float nz = ReadHalfFloat(data, (int)o + 4);
                        normals[vi] = Normalize(nx, ny, nz);
                    }
                    break;

                case 29:
                    for (int vi = 0; vi <= maxIndex; vi++)
                    {
                        long o = start + (long)vi * nvb.Stride;
                        uint packed = BitConverter.ToUInt32(data, (int)o);
                        int x = (int)((packed >> 0) & 0x3FF); if (x > 0x1FF) x -= 0x400;
                        int y = (int)((packed >> 10) & 0x3FF); if (y > 0x1FF) y -= 0x400;
                        int z = (int)((packed >> 20) & 0x3FF); if (z > 0x1FF) z -= 0x400;
                        normals[vi] = Normalize(x / 511f, y / 511f, z / 511f);
                    }
                    break;

                default:
                    return null;
            }

            return normals;
        }

        /// <summary>
        /// Vertical UV flip toggle. F1 2021 is a DirectX title and WPF uses
        /// DirectX texture conventions, so the raw UVs should NOT need to
        /// be flipped -- both coordinate systems put (0,0) at top-left of
        /// the texture. An earlier version of this file flipped V on load,
        /// which produced the "textures land in wrong areas of each panel"
        /// symptom (paint sponsors on the wrong face, number showing its
        /// opposite side of the atlas, etc.).
        ///
        /// Flip default: FALSE (no flip, DirectX-native).
        /// Set to TRUE if a future build turns out to have UV data stored
        /// bottom-left instead of top-left.
        /// </summary>
        public static bool FlipTextureV { get; set; } = false;

        /// <summary>
        /// Maximum tile rate (in either U or V) before a submesh's UVs are
        /// scaled down. Codemasters carbon panels store UVs going from -50
        /// to +60 because the real game shader applies its own per-material
        /// scale (me_carbonTile etc.) plus a lacquer/anisotropy pass. Without
        /// that shader, raw 170-tile UVs render as fine visual noise rather
        /// than recognisable carbon. Capping makes the carbon weave readable
        /// in a basic preview while leaving body/detail/number (which already
        /// use [0,1] atlas UVs) untouched.
        ///
        /// FIX 12: raised from 16.0 to 64.0. With Fix 11's ViewportUnits=
        /// Absolute now in effect, UVs sample the cf_pattern atlas at literal
        /// (u, v) -> pixel (u*W, v*H) coordinates, so the cap directly
        /// controls how many tile-repeats are visible. Cap=16 produced a
        /// visible weave but the user wanted finer/more-repeated carbon to
        /// better match the in-game look. Cap=64 quadruples the tile density
        /// (now ~32 tiles across a typical panel instead of ~8) without
        /// pushing into the "fine noise" regime that uncapped UVs produce.
        ///
        /// Set to a large number (e.g. 1000) to disable the cap and see
        /// the raw mesh UVs as Codemasters stored them.
        /// </summary>
        public static double MaxUvTileRate { get; set; } = 64.0;

        /// <summary>
        /// F1 2021 decal-compatibility toggle.
        ///
        /// The F1 2021 decal atlas (mclaren_decal_da.tif et al.) has sponsor
        /// UVs authored with occasional out-of-range values and tile-rate
        /// overshoots. The earlier "F1 2021 preview done" build applied two
        /// UV modifications to make those decals render correctly:
        ///
        ///   1. Clamp decal UVs to [0,1] whenever the shader picked a logo /
        ///      sponsor slot (useSecondaryUvPair=true).
        ///
        ///   2. Cap excessive UV tiling: if the UV buffer's bbox (not
        ///      just used verts) had any component with |value| &gt;
        ///      MaxUvTileRate, scale every UV in the buffer down so the
        ///      bbox fits. This is the pre-FIX-32 buffer-bbox version,
        ///      which matches the earlier build's behaviour byte-for-byte.
        ///
        /// Both modifications were removed when the user said "remove any
        /// UV modifications". That's correct for F1 22 (where those mods
        /// disrupted paint colour and number rendering) but regressed F1
        /// 2021 decals because F1 2021 atlases rely on the clamp + cap.
        ///
        /// This flag is the ADD-ON the user requested: set to true before
        /// loading an F1 2021 ERP to re-enable both mods. Default false
        /// leaves the F1 22 path untouched -- raw UVs reach the sampler
        /// exactly as decoded, which is what F1 22 needs.
        /// </summary>
        public static bool ApplyF1_2021_DecalCompatibility { get; set; } = false;

        // =====================================================================
        // F1 23 / 24 / 25 ERA TOGGLES
        // =====================================================================
        //
        // Structural analysis of F1 23 ERPs (ERPK v3.0, identical resource
        // types + URI shapes to F1 22, GfxSurfaceRes fmtVer=9 as in F1 22,
        // shared-generics/materials layout unchanged) says that loading F1 23
        // should follow the exact F1 22 code path by default -- raw UVs to
        // the sampler, no decal clamp, no tile cap override. That's the
        // reason each of these flags defaults to FALSE: the default does
        // the right thing for 23, 24, and 25 today.
        //
        // The flags exist so that per-year patches can be added without
        // another round of plumbing when a specific F1 23/24/25 atlas or
        // mesh encoding needs a targeted fix. The auto-configure logic in
        // LoadFolder reads the detected game-root year (F1 23 / F1 24 /
        // F1 25) and sets exactly one of these flags to true; callers that
        // set a flag manually before LoadFolder keep that value even if
        // auto-detection later disagrees.

        /// <summary>
        /// F1 23-era compatibility toggle. Default false. Currently a
        /// placeholder -- the loader does nothing differently when true --
        /// but the flag is wired into auto-configure so that when an F1 23
        /// game root is detected, downstream code can opt into era-specific
        /// behaviour by reading this property. Kept symmetric with
        /// ApplyF1_2021_DecalCompatibility so the pattern is obvious.
        /// </summary>
        public static bool ApplyF1_2023_Compat { get; set; } = false;

        /// <summary>
        /// F1 24-era compatibility toggle. See ApplyF1_2023_Compat notes.
        /// </summary>
        public static bool ApplyF1_2024_Compat { get; set; } = false;

        /// <summary>
        /// Build the overlay even when the UV set fails validation. A parked or
        /// degenerate set samples a transparent corner and draws nothing, so the
        /// cost of keeping it is an empty quad; the cost of rejecting it is a
        /// sponsor that never appears. Set false to let the validator veto again.
        public static bool OverlayRejectNothing { get; set; } = true;

        /// Keep overlay faces whose UVs fall outside [0,1] instead of
        /// discarding them, so the brush's existing WRAP tiling resolves them
        /// the way the game's sampler does. Only applied when it recovers faces
        /// and the result still validates. Set false for the old strict filter.
        public static bool OverlayWrapRecovery { get; set; } = true;

        /// How far a decal overlay is lifted off its panel, as a fraction of the
        /// loaded model's bounding diagonal. Dimensionless, so it holds across
        /// content scales instead of assuming one. Raise if decals flicker or
        /// drop out at distance, lower if they visibly float off curved panels.
        public static double OverlayDepthRatio { get; set; } = 1.0 / 3000.0;

        /// When an overlay submesh's normals face inward (a mirrored part
        /// whose normals were not flipped with its positions), push the overlay
        /// along -N instead of +N so it still sits above the panel rather than
        /// sinking below it. Direction only -- never touches UVs. Set false to
        /// keep the unconditional +N behaviour.
        /// v17.2: "inward" is decided by an area-weighted vote of the face
        /// winding against the authored normals, not by a centroid test.
        public static bool OverlayAutoFlipOffset { get; set; } = true;

        /// F1 25-era compatibility toggle. See ApplyF1_2023_Compat notes.
        /// </summary>
        public static bool ApplyF1_2025_Compat { get; set; } = false;

        /// <summary>
        /// Year number of the currently-loaded game, as inferred by the
        /// LoadFolder auto-configure step from the F1PackageLocator.Last
        /// game-root folder name. Zero when detection hasn't run yet or
        /// when the ERP sits outside a recognised install layout.
        ///
        /// Exposed as a read-only snapshot so that post-load code
        /// (exporters, texture pipeline, future per-year tweaks) can
        /// branch without re-running the locator. The value survives
        /// across LoadFolder calls until the next successful auto-detect.
        /// </summary>
        public static int DetectedGameYear { get; private set; } = 0;

        // FIX 6: _lastUvRange was a process-static mutable field that leaked UV
        // range data from one submesh into the next whenever the FP32 fallback
        // path was taken (because that path never updated it). Replaced by an
        // `out` parameter on DecodeUvStream/DecodeSecondaryUvStream and a local
        // variable inside BuildGeometryFromSubMeshes.

        /// <summary>
        /// FIX 25 helper: for a set of decoded UVs plus the submesh's
        /// index buffer, count how many USED vertex UVs land on a pixel
        /// of the source driver-atlas alpha channel with alpha &gt;= 128
        /// (i.e. "digit content" rather than transparent background).
        ///
        /// Returns 0 when no comparison is possible (null UV array, null
        /// alpha buffer from the pipeline, or empty index list). The
        /// caller uses this to pick between vb=4 and vb=3[4,6] for
        /// number overlays: whichever source actually aims the mesh's
        /// triangles at the digit is the correct one.
        ///
        /// Visits each used index exactly once -- we don't need triangle
        /// barycenters here because we're scoring UV sources against a
        /// ~50%-density alpha mask; vertex-level sampling is sufficient
        /// and an order of magnitude cheaper than per-pixel rasterisation.
        /// Wraps UVs into [0,1) via IEEERemainder so tiled or negative
        /// UVs (e.g. steering_wheel carbon at v=-0.017) still sample the
        /// correct cell of the atlas.
        /// </summary>
        private static int CountDigitCoverage(
            System.Windows.Point[] uvs,
            int[] rawIndices,
            TextureCache.PreviewResolution resolved)
        {
            if (uvs == null) return 0;
            if (rawIndices == null || rawIndices.Length == 0) return 0;
            byte[] alpha = resolved.OverlaySourceAlpha;
            if (alpha == null) return 0;
            int W = resolved.OverlaySourceWidth;
            int H = resolved.OverlaySourceHeight;
            if (W <= 0 || H <= 0 || alpha.Length != W * H) return 0;

            int hits = 0;
            for (int ri = 0; ri < rawIndices.Length; ri++)
            {
                int vi = rawIndices[ri];
                if (vi < 0 || vi >= uvs.Length) continue;

                // Wrap UVs to [0,1) -- matches GPU clamp/repeat semantics
                // well enough for coverage scoring. Negative values from
                // Math.IEEERemainder are shifted back into [0,1).
                double u = uvs[vi].X - Math.Floor(uvs[vi].X);
                double v = uvs[vi].Y - Math.Floor(uvs[vi].Y);

                int x = (int)(u * W);
                int y = (int)(v * H);
                if (x < 0) x = 0; else if (x >= W) x = W - 1;
                if (y < 0) y = 0; else if (y >= H) y = H - 1;

                if (alpha[y * W + x] >= 128) hits++;
            }
            return hits;
        }

        /// <summary>
        /// Decode a stream of UV coordinates from a vertex buffer with
        /// the expected Fvf 0x23 (0x03) structure. See DecodeUvStream for
        /// the full writeup. The `useSecondaryUvPair` flag picks between
        /// the two UV pairs that Ego engine stores in a stride-8 buffer:
        /// offsets 0,2 (primary) vs 4,6 (secondary / decal).
        /// </summary>
        /// <param name="useSecondaryUvPair">
        /// Most Ego-engine geometry stores its UVs in a single
        /// (stride=8, fmt=35) buffer that holds TWO UV pairs packed back
        /// to back: (u0,v0,u1,v1) per vertex. The first pair is the
        /// "primary" UV used by paint / carbon-weave / detail / wheel
        /// shaders. The second pair (offset 4,6) is the "logo / atlas"
        /// UV used specifically by me_logoMap (number decals, sponsor
        /// stickers). For a number panel the primary pair is a tiny
        /// degenerate slice (e.g. U=[0.80,0.81], V=[0.027,0.033]) that
        /// produces a 3-pixel-tall rendering, while the secondary pair
        /// is a proper rectangular decal region (e.g. U=[0.05,0.46],
        /// V=[0.56,1.01]).
        /// Pass true for me_logoMap submeshes; false (default) otherwise.
        /// </param>
        private static System.Windows.Point[] DecodeUvStream(
            List<VertexBufferInfo> vbufs, int uvIdx, int baseVertex, int maxIndex,
            bool useSecondaryUvPair,
            out (double uMin, double uMax, double vMin, double vMax) range)
        {
            range = (0, 0, 0, 0);
            if (uvIdx < 0 || uvIdx >= vbufs.Count) return null;

            var uvb = vbufs[uvIdx];
            if (!File.Exists(uvb.DataPath)) return null;

            byte[] data = File.ReadAllBytes(uvb.DataPath);
            long start = (long)baseVertex * uvb.Stride;
            long needed = start + (long)(maxIndex + 1) * uvb.Stride;
            if (needed > data.LongLength) return null;

            var uvs = new System.Windows.Point[maxIndex + 1];
            double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
            double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;

            switch (UvStreamFormat(uvb))
            {
                case 29:
                case 13:  // Character-cloth logo UV channel (Fvf=13, stride-4 half2).
                          // Same binary layout as Fvf=29; the format code differs
                          // because the shader pipeline treats this as a "logo
                          // overlay" channel rather than a generic UV1, but the
                          // bytes-per-vertex contract is identical.
                    {
                        for (int vi = 0; vi <= maxIndex; vi++)
                        {
                            long o = start + (long)vi * uvb.Stride;
                            float u = ReadHalfFloat(data, (int)o + 0);
                            float v = ReadHalfFloat(data, (int)o + 2);
                            double vOut = FlipTextureV ? (1.0 - v) : v;
                            uvs[vi] = new System.Windows.Point(u, vOut);
                            if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                            if (v < vMin) vMin = v; if (v > vMax) vMax = v;
                        }
                    }
                    break;

                case 35:
                    {
                        bool canUsePair2 = useSecondaryUvPair && uvb.Stride >= 8;
                        int uOff = canUsePair2 ? 4 : 0;
                        int vOff = canUsePair2 ? 6 : 2;
                        for (int vi = 0; vi <= maxIndex; vi++)
                        {
                            long o = start + (long)vi * uvb.Stride;
                            float u = ReadHalfFloat(data, (int)o + uOff);
                            float v = ReadHalfFloat(data, (int)o + vOff);
                            double vOut = FlipTextureV ? (1.0 - v) : v;
                            uvs[vi] = new System.Windows.Point(u, vOut);
                            if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                            if (v < vMin) vMin = v; if (v > vMax) vMax = v;
                        }
                    }
                    break;

                default:
                    if (uvb.Stride >= 8)
                    {
                        bool canUsePair2 = useSecondaryUvPair && uvb.Stride >= 16;
                        int uOff = canUsePair2 ? 8 : 0;
                        int vOff = canUsePair2 ? 12 : 4;
                        for (int vi = 0; vi <= maxIndex; vi++)
                        {
                            long o = start + (long)vi * uvb.Stride;
                            float u = BitConverter.ToSingle(data, (int)o + uOff);
                            float v = BitConverter.ToSingle(data, (int)o + vOff);
                            double vOut = FlipTextureV ? (1.0 - v) : v;
                            uvs[vi] = new System.Windows.Point(u, vOut);
                            if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                            if (v < vMin) vMin = v; if (v > vMax) vMax = v;
                        }
                    }
                    else return null;
                    break;
            }

            // FIX 6: UV range is returned via `out` parameter instead of being
            // written to a process-static field that leaked across submeshes.
            range = (uMin, uMax, vMin, vMax);
            return uvs;
        }

        /// <summary>
        /// Decode the pair-2 (decal / logo atlas) UV stream.
        ///
        /// Originally this loader assumed the pair-2 UVs were packed into the
        /// upper half of the pair-1 vertex buffer (vb=3 offsets 4,6). That
        /// assumption held on some older F1 builds but is WRONG for the 2021
        /// data: vb=3 is a tangent/bitangent buffer (Fvf=35 = R16G16B16A16_FLOAT,
        /// 4 FP16s per vertex). The first 2 FP16s happen to sit in plausible
        /// [0..1] ranges because they're the xy of a unit tangent vector,
        /// which is why reading them as a "UV" occasionally produced roughly
        /// correct results, but the 3rd and 4th FP16s are the tangent's z
        /// and w components and reading them as a UV just samples the decal
        /// atlas at near-random positions.
        ///
        /// The REAL secondary UV stream is a separate Fvf=29 buffer at
        /// slots[3] (typically vb=4 for body / number / carbon panels). This
        /// method reads it directly. It has exactly 4 bytes per vertex (2
        /// FP16s = one UV), and the very same UV drives both me_albedoMap
        /// sampling and me_logoMap sampling at render time -- the difference
        /// between those two is WHICH ATLAS gets sampled, not WHICH UV is used.
        ///
        /// Verified by sampling mclaren_decal_da.tif against body submeshes:
        /// reading from vb=3[offsets 4,6] gave 0-3% opaque sponsor hits,
        /// while reading from vb=4 gave 13-35% -- matching where actual
        /// McLaren sponsors sit on the decal sheet.
        /// </summary>
        private static System.Windows.Point[] DecodeSecondaryUvStream(
            List<VertexBufferInfo> vbufs, int uvIdx2, int baseVertex, int maxIndex,
            out (double uMin, double uMax, double vMin, double vMax) range)
        {
            range = (0, 0, 0, 0);
            if (uvIdx2 < 0 || uvIdx2 >= vbufs.Count) return null;

            var uvb = vbufs[uvIdx2];
            if (!File.Exists(uvb.DataPath)) return null;

            if (UvStreamFormat(uvb) != 29)
                return DecodeUvStream(vbufs, uvIdx2, baseVertex, maxIndex,
                                      useSecondaryUvPair: true, out range);

            byte[] data = File.ReadAllBytes(uvb.DataPath);
            long start = (long)baseVertex * uvb.Stride;
            long needed = start + (long)(maxIndex + 1) * uvb.Stride;
            if (needed > data.LongLength) return null;

            var uvs = new System.Windows.Point[maxIndex + 1];
            double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
            double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;

            for (int vi = 0; vi <= maxIndex; vi++)
            {
                long o = start + (long)vi * uvb.Stride;
                float u = ReadHalfFloat(data, (int)o + 0);
                float v = ReadHalfFloat(data, (int)o + 2);
                double vOut = FlipTextureV ? (1.0 - v) : v;
                uvs[vi] = new System.Windows.Point(u, vOut);
                if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                if (v < vMin) vMin = v; if (v > vMax) vMax = v;
            }

            range = (uMin, uMax, vMin, vMax);
            return uvs;
        }

        /// <summary>
        /// v17.4: format used to decode a UV stream -- the descriptor's
        /// declared format when it is a known UV encoding (35 = two FP16
        /// pairs, 29 = one FP16 pair), otherwise the buffer header value.
        /// </summary>
        private static uint UvStreamFormat(VertexBufferInfo vb)
        {
            if (vb == null) return 0;
            if (vb.DeclFormat == 35 || vb.DeclFormat == 29) return (uint)vb.DeclFormat;
            return vb.Fvf;
        }

        private static int AutoPickPositionVb(List<VertexBufferInfo> vbufs, int baseVertex, int maxIndex)
        {
            int chosen = -1;
            int chosenCount = int.MaxValue;

            for (int i = 0; i < vbufs.Count; i++)
            {
                var c = vbufs[i];
                if (c.Stride < 12) continue;
                long needed = (long)baseVertex + maxIndex + 1;
                if (needed <= c.Count && c.Count < chosenCount)
                {
                    chosen = i;
                    chosenCount = c.Count;
                }
            }
            return chosen;
        }

        /// <summary>
        /// Trims an EAID URI to just its filename for log readability.
        /// Keeps the full EAID only if the filename alone would be ambiguous
        /// (same basename appears under shared/ and teams/).
        /// </summary>
        private static string ShortenEaid(string eaid)
        {
            if (string.IsNullOrEmpty(eaid)) return "-";
            int lastSlash = eaid.LastIndexOf('/');
            return lastSlash >= 0 && lastSlash + 1 < eaid.Length
                ? eaid.Substring(lastSlash + 1)
                : eaid;
        }

        // =====================================================================
        // BUFFER PAIR LOADERS
        // =====================================================================

        private static VertexBufferInfo LoadBufVertex(string folder, string teamPrefix, string suffix)
        {
            string temp000 = Path.Combine(folder, $"{teamPrefix}!!!temp000{suffix}");
            string temp001 = Path.Combine(folder, $"{teamPrefix}!!!temp001{suffix}");

            var info = new VertexBufferInfo { Suffix = suffix, DataPath = temp001 };
            if (!File.Exists(temp000)) return info;

            byte[] header = File.ReadAllBytes(temp000);
            if (header.Length < 16) return info;

            info.Type = BitConverter.ToUInt32(header, 0);
            info.Stride = BitConverter.ToInt32(header, 4);
            info.Count = BitConverter.ToInt32(header, 8);
            info.Fvf = BitConverter.ToUInt32(header, 12);

            return info;
        }

        private static IndexBufferInfo LoadBufIndex(string folder, string teamPrefix, string suffix)
        {
            string temp000 = Path.Combine(folder, $"{teamPrefix}!!!temp000{suffix}");
            string temp001 = Path.Combine(folder, $"{teamPrefix}!!!temp001{suffix}");

            var info = new IndexBufferInfo { Suffix = suffix, DataPath = temp001 };
            if (!File.Exists(temp000)) return info;

            byte[] header = File.ReadAllBytes(temp000);
            if (header.Length < 16) return info;

            info.Type = BitConverter.ToUInt32(header, 0);
            info.IndexSize = BitConverter.ToInt32(header, 4);
            info.Count = BitConverter.ToInt32(header, 8);
            info.Fvf = BitConverter.ToUInt32(header, 12);

            if (info.IndexSize != 2 && info.IndexSize != 4) info.IndexSize = 4;
            return info;
        }

        /// <summary>
        /// Enumerates candidate texture EAIDs to try, in priority order, when
        /// a helmet submesh's .material file carries no sampler bindings.
        ///
        ///   1. Per-livery bespoke texture (the visible "skin"):
        ///      eaid://livery_package_shared/replica/&lt;context&gt;/textures/&lt;context&gt;_&lt;slot&gt;_&lt;channel&gt;.tif
        ///
        ///   2. Shared character-package template (visor / generic bits the
        ///      livery doesn't override):
        ///      eaid://character_package_shared/drivers/gender/teamname_charname/textures/charname_&lt;slot&gt;_&lt;channel&gt;.tif
        ///
        /// First match wins. Empty enumerable when slot is missing.
        /// </summary>
        /// <summary>
        /// Pulls the leaf basename from a .material EAID -- e.g.
        ///   eaid://.../materials/helmet_wing.material -> "helmet_wing"
        /// Returns null when the EAID doesn't end in ".material" or has no
        /// leaf segment.
        /// </summary>
        private static string ExtractMaterialStem(string materialEaid)
        {
            if (string.IsNullOrEmpty(materialEaid)) return null;
            int lastSlash = materialEaid.LastIndexOf('/');
            string leaf = lastSlash >= 0 ? materialEaid.Substring(lastSlash + 1) : materialEaid;
            const string suffix = ".material";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf.Substring(0, leaf.Length - suffix.Length);
            return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
        }

        /// <summary>
        /// Enumerates candidate texture EAIDs to try, in priority order, when
        /// a helmet submesh's .material file carries no sampler bindings.
        /// F1 25 ships helmets under two distinct layouts:
        ///
        ///   REPLICA (livery editor / pre-built skins) -- context like
        ///     "replica_zenith25a":
        ///     eaid://livery_package_shared/replica/&lt;ctx&gt;/textures/&lt;ctx&gt;_&lt;slot&gt;_&lt;ch&gt;.tif
        ///
        ///   STOCK DRIVER (career roster) -- context like
        ///     "2025-sauber_nico_hulkenberg":
        ///     eaid://character_package/drivers/&lt;gender&gt;/&lt;ctx&gt;/textures/&lt;driver&gt;_&lt;slot&gt;_&lt;ch&gt;.tif
        ///   where &lt;driver&gt; strips the leading "&lt;year&gt;-&lt;team&gt;_" from &lt;ctx&gt;.
        ///
        ///   SHARED template (visor textures and other generic bits both
        ///     layouts share):
        ///     eaid://character_package_shared/drivers/gender/teamname_charname/textures/charname_&lt;slot&gt;_&lt;ch&gt;.tif
        ///
        /// First disk hit wins.
        /// </summary>
        private static IEnumerable<(string eaid, string label)> EnumerateHelmetFallbackTextureEaids(
            string contextName, string slot, string channel)
        {
            if (string.IsNullOrEmpty(slot) || string.IsNullOrEmpty(channel))
                yield break;

            if (!string.IsNullOrEmpty(contextName))
            {
                // Replica helmet path.
                yield return (
                    $"eaid://livery_package_shared/replica/{contextName}/textures/{contextName}_{slot}_{channel}.tif",
                    "helmet_livery_replica");

                // Stock-driver helmet paths. Try the driver-prefix filename
                // first, then fall back to the full context as a filename
                // prefix in case that's how a particular asset stores it.
                string driverPrefix = ExtractDriverPrefixFromContext(contextName);
                foreach (var gender in new[] { "male", "female" })
                {
                    yield return (
                        $"eaid://character_package/drivers/{gender}/{contextName}/textures/{driverPrefix}_{slot}_{channel}.tif",
                        $"helmet_driver_{gender}");
                }
                if (!string.Equals(driverPrefix, contextName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var gender in new[] { "male", "female" })
                    {
                        yield return (
                            $"eaid://character_package/drivers/{gender}/{contextName}/textures/{contextName}_{slot}_{channel}.tif",
                            $"helmet_driver_{gender}_fullctx");
                    }
                }
            }

            // Shared character template fallback (visor / generic bits).
            yield return (
                $"eaid://character_package_shared/drivers/gender/teamname_charname/textures/charname_{slot}_{channel}.tif",
                "helmet_shared");
        }

        /// <summary>
        /// Strips the leading "&lt;year&gt;-&lt;team&gt;_" prefix from a stock-driver
        /// context name.
        ///   "2025-sauber_nico_hulkenberg" -&gt; "nico_hulkenberg"
        ///   "replica_zenith25a"            -&gt; "replica_zenith25a" (unchanged)
        /// </summary>
        private static string ExtractDriverPrefixFromContext(string contextName)
        {
            if (string.IsNullOrEmpty(contextName)) return contextName;
            var m = System.Text.RegularExpressions.Regex.Match(
                contextName, @"^\d{2,4}-[A-Za-z0-9]+_(.+)$");
            return m.Success ? m.Groups[1].Value : contextName;
        }

        private static bool MatchesAscii(byte[] data, int offset, string ascii)
        {
            if (offset < 0 || offset + ascii.Length > data.Length) return false;
            for (int i = 0; i < ascii.Length; i++)
                if (data[offset + i] != (byte)ascii[i]) return false;
            return true;
        }

        private static int IndexOfAscii(byte[] data, int startOffset, string ascii)
        {
            if (startOffset < 0) startOffset = 0;
            int last = data.Length - ascii.Length;
            for (int i = startOffset; i <= last; i++)
            {
                bool ok = true;
                for (int j = 0; j < ascii.Length; j++)
                {
                    if (data[i + j] != (byte)ascii[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        // =====================================================================
        // LOW-LEVEL UTILITIES
        // =====================================================================

        private static uint ReadU32(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
        private static float ReadF32(byte[] data, int offset) => BitConverter.ToSingle(data, offset);

        private static bool IsAllZero(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
                if (data[i] != 0) return false;
            return true;
        }

        private static List<string> ReadNullTerminatedStrings(byte[] data, int offset, int count)
        {
            var result = new List<string>(count);
            int p = offset;
            while (p < data.Length && result.Count < count)
            {
                int start = p;
                while (p < data.Length && data[p] != 0) p++;
                result.Add(System.Text.Encoding.ASCII.GetString(data, start, p - start));
                if (p < data.Length) p++;
            }
            return result;
        }

        private static Vector3D Normalize(float x, float y, float z)
        {
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (len < 1e-8) return new Vector3D(0, 0, 1);
            return new Vector3D(x / len, y / len, z / len);
        }

        // FIX 1: previously applied the sign bit twice. The sign is already
        // baked into `bits` via `(sign << 31)`, so BitConverter.ToSingle returns
        // an already-signed float � the old `return sign == 1 ? -result : result`
        // then flipped it again, making every negative FP16 come out positive.
        // Symptoms: flipped normals on half of each body panel, tiled carbon UVs
        // folded about zero, and any negative UV silently mirrored to positive.
        //
        // This implementation is lifted from F1ModelLoaderLegacy.HalfToFloat,
        // which has handled half-floats correctly in this project for years.
        private static float ReadHalfFloat(byte[] data, int offset)
        {
            if (offset + 1 >= data.Length) return 0f;
            ushort h = BitConverter.ToUInt16(data, offset);

            int sign = (h >> 15) & 0x00000001;
            int exp = (h >> 10) & 0x0000001F;
            int mant = h & 0x000003FF;

            int varExp = exp + (127 - 15);
            if (exp == 0)
            {
                if (mant == 0) return sign == 1 ? -0.0f : 0.0f;
                return (sign == 1 ? -1.0f : 1.0f) * mant / 1024.0f * (float)Math.Pow(2, -14);
            }
            else if (exp == 31)
            {
                if (mant == 0) return sign == 1 ? float.NegativeInfinity : float.PositiveInfinity;
                return float.NaN;
            }

            int bits = (sign << 31) | (varExp << 23) | (mant << 13);
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        // =====================================================================
        // v17.1: DESCRIPTOR VERTEX-ELEMENT TABLE
        // =====================================================================
        //
        // Header (relative offsets are from 0x4C):
        //   +0x24  u32 elementCount   (== vbRef count; one element per vbRef)
        //   +0x28  u32 elementOffset  (20 bytes each: slot, byteOffset,
        //                              semantic, semanticIndex, format)
        //   +0x2C  u32 vbRefOffset    (8 bytes each: kind(=1), vbNameIndex)
        // Submesh block:
        //   +0x00  u32 streamCount
        //   +0x38  u32 vbRef[8]       (first streamCount valid)
        // Semantics: 1 POSITION, 2 NORMAL, 3 TANGENT, 5 TEXCOORD, 7 PARTINFO.
        // Formats:   47 f32x3, 35 f16x4, 29 f16x2, 31 16-bit x4, 13 8-bit x4.

        private struct StreamRoles
        {
            public int Position, Normal, Tangent, Uv, Uv2, UvExtra, PartInfo;
            public bool UvHasPair2;

            // v17.4: vbuf index -> declared element format, for every
            // stream the submesh references.
            public Dictionary<int, int> Formats;
        }

        private sealed class DescriptorDecl
        {
            public int Count;
            public int[] Semantic, SemanticIndex, Format, VbName;

            private static int ExpectedStride(int fmt)
            {
                switch (fmt)
                {
                    case 47: return 12;
                    case 35: return 8;
                    case 29: return 4;
                    case 31: return 8;
                    case 13: return 4;
                    default: return -1;
                }
            }

            public static DescriptorDecl TryRead(byte[] b, int vbNameCount, List<VertexBufferInfo> vbufs)
            {
                try
                {
                    if (b.Length < 0x4C) return null;
                    int count = (int)ReadU32(b, 0x24);
                    long elemOff = 0x4C + (long)ReadU32(b, 0x28);
                    long refOff = 0x4C + (long)ReadU32(b, 0x2C);
                    if (count <= 0 || count > 64) return null;
                    if (elemOff + count * 20L > b.Length) return null;
                    if (refOff + count * 8L > b.Length) return null;

                    var d = new DescriptorDecl
                    {
                        Count = count,
                        Semantic = new int[count],
                        SemanticIndex = new int[count],
                        Format = new int[count],
                        VbName = new int[count],
                    };
                    for (int i = 0; i < count; i++)
                    {
                        int e = (int)(elemOff + i * 20L);
                        int sem = (int)ReadU32(b, e + 8);
                        int semIdx = (int)ReadU32(b, e + 12);
                        int fmt = (int)ReadU32(b, e + 16);
                        if (sem < 1 || sem > 16 || semIdx > 8 || fmt <= 0 || fmt > 255) return null;

                        int r = (int)(refOff + i * 8L);
                        uint kind = ReadU32(b, r);
                        int vbn = (int)ReadU32(b, r + 4);
                        if (kind != 1 || vbn < 0 || vbn >= vbNameCount || vbn >= vbufs.Count) return null;

                        int want = ExpectedStride(fmt);
                        if (want > 0 && vbufs[vbn].Stride > 0 && vbufs[vbn].Stride != want) return null;

                        d.Semantic[i] = sem;
                        d.SemanticIndex[i] = semIdx;
                        d.Format[i] = fmt;
                        d.VbName[i] = vbn;
                    }
                    return d;
                }
                catch
                {
                    return null;
                }
            }

            public bool TryResolve(byte[] b, int blockOff, List<VertexBufferInfo> vbufs,
                                   out StreamRoles roles, out string why)
            {
                roles = new StreamRoles
                {
                    Position = -1,
                    Normal = -1,
                    Tangent = -1,
                    Uv = -1,
                    Uv2 = -1,
                    UvExtra = -1,
                    PartInfo = -1,
                    Formats = new Dictionary<int, int>(),
                };
                why = null;

                int n = (int)ReadU32(b, blockOff + 0x00);
                if (n < 1 || n > 8) { why = $"streamCount={n}"; return false; }

                for (int k = 0; k < n; k++)
                {
                    int r = (int)ReadU32(b, blockOff + 0x38 + k * 4);
                    if (r < 0 || r >= Count) { why = $"vbRef[{k}]={r} out of range"; return false; }
                    int vb = VbName[r];
                    int fmt = Format[r];
                    if (!roles.Formats.ContainsKey(vb)) roles.Formats[vb] = fmt;
                    switch (Semantic[r])
                    {
                        case 1:
                            if (roles.Position < 0) roles.Position = vb;
                            break;
                        case 2:
                            if (roles.Normal < 0) roles.Normal = vb;
                            break;
                        case 3:
                            if (roles.Tangent < 0) roles.Tangent = vb;
                            break;
                        case 5:
                            if (roles.Uv < 0)
                            {
                                roles.Uv = vb;
                                roles.UvHasPair2 = fmt == 35;
                            }
                            else if (roles.Uv2 < 0 && fmt == 29 && SemanticIndex[r] >= 1)
                                roles.Uv2 = vb;
                            else if (roles.UvExtra < 0)
                                roles.UvExtra = vb;
                            break;
                        case 7:
                            if (roles.PartInfo < 0) roles.PartInfo = vb;
                            break;
                    }
                }

                if (roles.Position < 0) { why = "no POSITION element"; return false; }
                if (vbufs[roles.Position].Stride < 12)
                {
                    why = $"POSITION stride {vbufs[roles.Position].Stride}";
                    return false;
                }
                return true;
            }
        }

        // =====================================================================
        // v17.4: DATA-DRIVEN CAR LAYOUT
        // =====================================================================

        /// <summary>
        /// The car's ?render node graph: node names plus the part-slot table
        /// that PARTINFO byte 0 indexes into. Layout (same reader offsets as
        /// F1FbxExporter.TryParseNodeGraph):
        ///   u16 nodeCount, u16 partCount, u32 nameBlobSize, u32 (node 0 unk)
        ///   nodeCount x 120-byte records read from 0x0C (u32 nameOffset first)
        ///   u16 partSlotToNode[partCount]
        ///   name blob
        /// Node order, node count and part count all differ between teams
        /// (Ferrari 130/67, McLaren 140/73, Red Bull 135/70 ...), so nothing
        /// here is fixed; every value is validated against the file.
        /// </summary>
        private sealed class CarRenderGraph
        {
            public string SourceFile;
            public List<string> NodeNames = new List<string>();
            public int[] PartSlotToNode = Array.Empty<int>();

            public string NodeNameForSlot(int slot)
            {
                if (slot < 0 || slot >= PartSlotToNode.Length) return null;
                int n = PartSlotToNode[slot];
                return (n >= 0 && n < NodeNames.Count) ? NodeNames[n] : null;
            }

            public static CarRenderGraph TryLoad(string folder, Action<string> log)
            {
                try
                {
                    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;

                    // The car's own graph: largest ?render descriptor that is
                    // not the merged shared-tyres one.
                    string best = null;
                    long bestLen = -1;
                    foreach (var f in Directory.GetFiles(folder, "*temp000*render*", SearchOption.TopDirectoryOnly))
                    {
                        string leaf = Path.GetFileName(f);
                        string dec = Uri.UnescapeDataString(leaf);
                        if (dec.IndexOf("?render", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (leaf.StartsWith("tyres!!!", StringComparison.OrdinalIgnoreCase)) continue;
                        long len = new FileInfo(f).Length;
                        if (len > bestLen) { best = f; bestLen = len; }
                    }
                    if (best == null)
                    {
                        log?.Invoke("  render graph: no ?render descriptor found -- parts keep file-name matching.");
                        return null;
                    }

                    byte[] d = File.ReadAllBytes(best);
                    if (d.Length < 0x0C) return null;
                    int nodeCount = BitConverter.ToUInt16(d, 0);
                    int partCount = BitConverter.ToUInt16(d, 2);
                    const int nodesStart = 0x0C, stride = 120;
                    long tableStart = nodesStart + (long)nodeCount * stride;
                    long poolStart = tableStart + (long)partCount * 2;
                    if (nodeCount == 0 || partCount == 0 || poolStart > d.Length)
                    {
                        log?.Invoke($"  render graph: {Path.GetFileName(best)} failed validation " +
                                    $"(nodes={nodeCount}, parts={partCount}, size={d.Length}).");
                        return null;
                    }

                    var g = new CarRenderGraph { SourceFile = best };
                    for (int i = 0; i < nodeCount; i++)
                    {
                        uint nameOfs = BitConverter.ToUInt32(d, nodesStart + i * stride);
                        long np = poolStart + nameOfs;
                        if (np < poolStart || np >= d.Length) return null;
                        int e = (int)np;
                        while (e < d.Length && d[e] != 0) e++;
                        g.NodeNames.Add(System.Text.Encoding.ASCII.GetString(d, (int)np, e - (int)np));
                    }

                    g.PartSlotToNode = new int[partCount];
                    for (int i = 0; i < partCount; i++)
                    {
                        int n = BitConverter.ToUInt16(d, (int)tableStart + i * 2);
                        if (n >= nodeCount) return null;
                        g.PartSlotToNode[i] = n;
                    }

                    log?.Invoke($"  render graph: {nodeCount} node(s), {partCount} part slot(s) " +
                                $"from {Path.GetFileName(best)}");
                    return g;
                }
                catch (Exception ex)
                {
                    log?.Invoke("  render graph: read failed (" + ex.Message + ") -- parts keep file-name matching.");
                    return null;
                }
            }
        }

        /// <summary>
        /// Part slot of a descriptor: PARTINFO byte 0 of the first vertex its
        /// first PARTINFO-bearing submesh draws. -1 when unavailable.
        /// </summary>
        private static int TryReadPartSlot(List<VertexBufferInfo> vbufs, IndexBufferInfo ibuf,
                                           List<SubMeshInfo> subMeshes)
        {
            try
            {
                if (ibuf == null || !File.Exists(ibuf.DataPath) || subMeshes == null) return -1;
                foreach (var sm in subMeshes)
                {
                    int pi = sm.PartInfoBufferIndex;
                    if (pi < 0 || pi >= vbufs.Count || sm.IndexCount <= 0) continue;
                    var pb = vbufs[pi];
                    if (pb.Stride < 1 || !File.Exists(pb.DataPath)) continue;

                    int isz = ibuf.IndexSize == 2 ? 2 : 4;
                    long io = (long)sm.IOffset * isz;
                    int vid;
                    using (var fs = File.OpenRead(ibuf.DataPath))
                    {
                        if (io + isz > fs.Length) continue;
                        var buf = new byte[isz];
                        fs.Position = io;
                        if (fs.Read(buf, 0, isz) != isz) continue;
                        vid = isz == 2 ? BitConverter.ToUInt16(buf, 0) : BitConverter.ToInt32(buf, 0);
                    }
                    long vo = ((long)vid + sm.VOffset) * pb.Stride;
                    using (var fs = File.OpenRead(pb.DataPath))
                    {
                        if (vo < 0 || vo >= fs.Length) continue;
                        fs.Position = vo;
                        int b = fs.ReadByte();
                        if (b >= 0) return b;
                    }
                }
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// Tags a loaded part group with the exact node it is bound to, so the
        /// assembler matches it without guessing from the file name. Only
        /// applied when the node name is compatible with the descriptor's part
        /// name (equal, or one a prefix of the other once trimmed -- covers
        /// "hal"/"halo", "driver_surroun"/"driver_surround" and McLaren's
        /// "driver_surround " node); anything else is logged and left to the
        /// existing name-based matching, so an unfamiliar PARTINFO encoding
        /// can never misplace a part.
        /// </summary>
        private static void RegisterPartNode(Model3DGroup partGroup, string meshFile, int partSlot,
                                             CarRenderGraph graph)
        {
            if (partGroup == null || graph == null || partSlot < 0) return;
            string nodeName = graph.NodeNameForSlot(partSlot);
            if (string.IsNullOrWhiteSpace(nodeName)) return;

            string fileName = ResolveDescriptorPartName(Path.GetFileName(meshFile));
            string a = (fileName ?? "").Trim();
            string b = nodeName.Trim();
            bool compatible = a.Length > 0 &&
                (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
                 b.StartsWith(a, StringComparison.OrdinalIgnoreCase) ||
                 a.StartsWith(b, StringComparison.OrdinalIgnoreCase));
            if (!compatible)
            {
                Log($"  part node: '{fileName}' slot {partSlot} -> node '{nodeName}' does not match " +
                    "the file name; keeping name-based assembly for this part.");
                return;
            }

            ModelPartNameRegistry.Register(partGroup, nodeName);
            if (!string.Equals(fileName, nodeName, StringComparison.Ordinal))
                Log($"  part node: '{fileName}' -> '{nodeName}' (slot {partSlot})");
        }

        /// <summary>
        /// Season from the package segment of the EAIDs inside the folder's
        /// mesh descriptors ("eaid://f1_2025_story_vehicle_package/..." ->
        /// 2025). Returns 0 when nothing recognisable is found.
        /// </summary>
        internal static int TryDetectYearFromDescriptors(string folder, out string packageName)
        {
            packageName = null;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return 0;
            var rx = new System.Text.RegularExpressions.Regex(
                @"eaid://((?:f1|f2)_(20\d\d)_[a-z0-9_]*package)/",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int checkedFiles = 0;
            foreach (var f in Directory.EnumerateFiles(folder, "*temp000*mesh*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(f).StartsWith("tyres!!!", StringComparison.OrdinalIgnoreCase)) continue;
                if (++checkedFiles > 8) break;
                byte[] b;
                try { b = File.ReadAllBytes(f); } catch { continue; }
                string text = System.Text.Encoding.ASCII.GetString(b);
                var m = rx.Match(text);
                if (!m.Success) continue;
                if (int.TryParse(m.Groups[2].Value, out int y) && y >= 2017 && y <= 2030)
                {
                    packageName = m.Groups[1].Value;
                    return y;
                }
            }
            return 0;
        }

        /// <summary>
        /// v17.5: WPF draws a Model3DGroup's children in order and alpha-blended
        /// geometry still writes depth. A decal overlay is a lifted copy of its
        /// base panel that is mostly transparent, so when it is drawn BEFORE a
        /// neighbouring opaque submesh of the same part, its transparent texels
        /// stamp depth in front of that submesh and it is never drawn there --
        /// the screen shows whatever was drawn earlier behind it instead.
        ///
        /// Seen on the 2026 McLaren mirror lights: wing_mirror_l/r submesh order
        /// is body, detail, carbon_tile_1, mirror, rear light, and the
        /// carbon_tile_1 overlay (lifted 1.5 mm along its normals) includes the
        /// pocket walls around the recessed LED strip. Those walls lean over
        /// the strip once lifted; their UVs are parked on a fully transparent
        /// atlas texel, yet they hid ~16% of the light (measured from the ERP),
        /// showing the housing interior and the car body behind as a teal /
        /// white "gap" between the light and its bezel.
        ///
        /// Drawing every overlay after all opaque submeshes of its part lets
        /// the transparent texels blend over geometry that is already there.
        /// Only the child order changes: no geometry, material or registry
        /// entry is touched, and the exporter pairs overlays with their bases
        /// by (part, slot), not by position. The DX showroom already routes
        /// overlays through its transparent pass (IsTransparent).
        /// </summary>
        private static int MoveOverlaysAfterBase(Model3DGroup partGroup)
        {
            if (partGroup == null || partGroup.IsFrozen || partGroup.Children.Count < 2) return 0;

            // v17.6: three bands -- opaque, see-through bases (glass), then
            // decal overlays. On the 2026 Cadillac, FOM car and Sauber the
            // mirror submesh order is body, GLASS, carbon, mirror, rear light:
            // the glass lens (1-2 mm over the LED strip, ~25% alpha) was drawn
            // before the light and hid it the same way the carbon overlay did
            // on the McLaren.
            var opaque = new List<Model3D>();
            var seeThrough = new List<Model3D>();
            var overlays = new List<Model3D>();
            foreach (var child in partGroup.Children)
            {
                bool isOverlay = false, isGlass = false;
                if (child is GeometryModel3D gm)
                {
                    isOverlay = GetIsOverlay(gm);
                    if (!isOverlay && gm.Geometry is MeshGeometry3D mg)
                    {
                        string nm = MeshSubmeshRegistry.GetSubmeshName(mg);
                        isOverlay = nm != null && nm.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase);
                    }
                    if (!isOverlay) isGlass = GetIsTransparentBase(gm);
                }
                (isOverlay ? overlays : isGlass ? seeThrough : opaque).Add(child);
            }
            if (overlays.Count + seeThrough.Count == 0 || opaque.Count == 0) return 0;

            var wanted = new List<Model3D>(partGroup.Children.Count);
            wanted.AddRange(opaque);
            wanted.AddRange(seeThrough);
            wanted.AddRange(overlays);

            // Already in the right order? Leave the collection alone.
            bool ordered = true;
            for (int i = 0; i < wanted.Count; i++)
                if (!ReferenceEquals(partGroup.Children[i], wanted[i])) { ordered = false; break; }
            if (ordered) return 0;

            partGroup.Children.Clear();
            foreach (var m in wanted) partGroup.Children.Add(m);
            return overlays.Count + seeThrough.Count;
        }

        /// <summary>
        /// v17.4: canonical form of a submesh / material slot name. Teams
        /// spell the same slot differently ("carbon tile 4" on Red Bull and
        /// Toro Rosso, "carbon_tile_4" elsewhere, stray padding spaces), so
        /// comparisons go through this: trimmed, lower-case, runs of spaces,
        /// hyphens and underscores collapsed to one underscore. The name
        /// itself is never rewritten -- livery-context lookups still use the
        /// authored string.
        /// </summary>
        internal static string NormalizeSlotName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var sb = new System.Text.StringBuilder(name.Length);
            bool sep = false;
            foreach (char ch in name.Trim().ToLowerInvariant())
            {
                if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_')
                {
                    if (!sep) { sb.Append('_'); sep = true; }
                }
                else
                {
                    sb.Append(ch);
                    sep = false;
                }
            }
            return sb.ToString();
        }

        internal static bool SlotIs(string name, string canonical) =>
            string.Equals(NormalizeSlotName(name), canonical, StringComparison.Ordinal);

        internal static bool IsNumberSlot(string name) =>
            SlotIs(name, "number") || SlotIs(name, "carbon_number");

        private static bool LooksLikeStreamName(byte[] data, int pos)
        {
            if (pos < 0 || pos >= data.Length) return false;
            var s = ReadNullTerminatedStrings(data, pos, 1);
            if (s.Count != 1 || string.IsNullOrEmpty(s[0])) return false;
            string v = s[0];
            return v.StartsWith("eaid:", StringComparison.OrdinalIgnoreCase) ||
                   v.IndexOf("vb=", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   v.IndexOf("ib=", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// v17.1: decode bytes 4/6 of a 4-component FP16 primary UV stream
        /// as the secondary UV. Returns false when the stream is missing or
        /// the used-vertex span is degenerate in either axis (the FIX 7
        /// constant-bytes case), so callers keep their existing fallback.
        /// </summary>
        private static bool TryDecodePrimaryPair2(
            List<VertexBufferInfo> vbufs, SubMeshInfo sm, int maxIndex, int[] rawIndices,
            out System.Windows.Point[] uvs,
            out (double uMin, double uMax, double vMin, double vMax) range)
        {
            uvs = null;
            range = (0, 0, 0, 0);
            if (sm.UvBufferIndex < 0 || sm.UvBufferIndex >= vbufs.Count) return false;
            if (vbufs[sm.UvBufferIndex].Stride < 8) return false;

            var cand = DecodeUvStream(vbufs, sm.UvBufferIndex, sm.VOffset, maxIndex,
                                      useSecondaryUvPair: true, out var candRange);
            if (cand == null || rawIndices == null || rawIndices.Length == 0) return false;

            double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
            double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
            for (int i = 0; i < rawIndices.Length; i++)
            {
                int vi = rawIndices[i];
                if (vi < 0 || vi >= cand.Length) continue;
                double u = cand[vi].X, v = cand[vi].Y;
                if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                if (v < vMin) vMin = v; if (v > vMax) vMax = v;
            }
            const double kMinSpan = 1e-3;
            if (double.IsPositiveInfinity(uMin) ||
                (uMax - uMin) < kMinSpan || (vMax - vMin) < kMinSpan)
                return false;

            uvs = cand;
            range = candRange;
            return true;
        }

        private static int DetectUvByteOffset(int stride)
        {
            if (stride >= 32) return 24;
            if (stride == 20) return 12;
            return 0;
        }

        private static string BuildSuffixFromName(string name, string teamPrefix)
        {
            string baseName = Path.GetFileName(name);
            baseName = baseName.Replace("?", "%3F");

            int dotIdf = baseName.IndexOf(".idf", StringComparison.OrdinalIgnoreCase);
            if (dotIdf > 0)
            {
                string stem = baseName.Substring(0, dotIdf);
                baseName = baseName.Replace(stem, "");
            }
            else if (!string.IsNullOrEmpty(teamPrefix))
            {
                baseName = baseName.Replace(teamPrefix, "");
            }

            return baseName;
        }

        // =====================================================================
        // PART NAMING & PREVIEW COLOURS
        // =====================================================================

        private static string ResolveDescriptorPartName(string meshFileName)
        {
            if (string.IsNullOrWhiteSpace(meshFileName)) return "part";

            string decoded = Uri.UnescapeDataString(meshFileName);

            // Capture EVERYTHING between "lod=N&" and "&mesh", non-greedy.
            // This intentionally accepts multi-segment patterns like
            //   lod=0&safetycell&water&mesh
            //   lod=0&hub_caliper_fl&damage&mesh
            // The captured segments are joined with underscore so the part
            // names stay distinct from the base variant ("safetycell" vs
            // "safetycell_water"). Without this, water/damage/glass etc.
            // variants would all collapse into the base name and either
            // get dropped by the consolidated-mesh filter or render on top
            // of each other in the same Model3DGroup.
            var rx = new System.Text.RegularExpressions.Regex(
                @"lod=\d+&(.+?)&mesh\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var m = rx.Match(decoded);
            if (m.Success)
            {
                string captured = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(captured))
                {
                    // "safetycell&water" -> "safetycell_water"
                    return captured.Replace('&', '_');
                }
            }

            // HELMET FIX: consolidated whole-asset descriptors have no part
            // name between "lod=N&" and "&mesh" -- the whole helmet is one
            // mesh file. Falling back to "part" leaves the geometry under a
            // generic name that the FBX exporter cannot match against the
            // render-graph node names (e.g. "helmet_bell_hp7_x0"). Use the
            // asset name (the "<asset>!!!" prefix from the file name) as the
            // part name so downstream prefix-matching can attach the loaded
            // geometry to the corresponding render-graph LOD-group node.
            int excl = decoded.IndexOf("!!!", StringComparison.Ordinal);
            if (excl > 0)
                return decoded.Substring(0, excl);

            return "part";
        }

        // Single shared fallback material. Frozen so WPF can reuse it across
        // thousands of submeshes with zero per-instance overhead. Neutral
        // off-white reads as "matte plastic" under default HelixToolkit lighting.
        //
        // Used whenever the real .material file isn't on disk (typically
        // shared/generics materials that live in a separate ERP). To get
        // the proper textures for those slots, extract the matching shared
        // ERP and drop the .material files into
        //   <package_root>/shared/generics/materials/
        // -- the loader will pick them up automatically on the next load.
        private static readonly Material _neutralMaterial = CreateNeutralMaterial();

        private static Material CreateNeutralMaterial()
        {
            var brush = new SolidColorBrush(Color.FromRgb(220, 220, 220));
            brush.Freeze();
            var mat = new DiffuseMaterial(brush);
            mat.Freeze();
            return mat;
        }

        private static int? ExtractLodValueFromPath(string path)
        {
            const string token = "lod=";
            int idx = path.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            idx += token.Length;
            int start = idx;
            while (idx < path.Length && char.IsDigit(path[idx])) idx++;
            if (idx == start) return null;

            string numStr = path.Substring(start, idx - start);
            if (int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                return val;
            return null;
        }
    }
}