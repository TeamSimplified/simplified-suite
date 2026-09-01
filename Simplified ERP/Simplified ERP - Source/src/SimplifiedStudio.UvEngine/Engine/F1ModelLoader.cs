using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;

namespace F1modding3dtoolV1
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
    /// </summary>
    public static class F1ModelLoader
    {
        // =====================================================================
        // LOGGING
        // =====================================================================

        private static string _logPath = "";

        private static void Log(string message)
        {
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
            // `…\F1modding3dtoolV1`) contains sibling `erp_extract` /
            // `erp_cache` folders with unrelated classic-car descriptors
            // from earlier sessions -- those ARE classic, but they
            // belong to a DIFFERENT car, not the one being loaded right
            // now. Observed in the field: a 2025 McLaren load got
            // bounced to F1ModelLoaderClassic because hop 5 reached
            // `…\F1modding3dtoolV1` and the descendant sniff found a
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

        /// <summary>
        /// Gender filter for race-suit / driver / character ERPs that ship
        /// parallel male+female mesh files. Set to "male", "female", or
        /// null/empty (= load both, original behaviour). See the per-file
        /// drop logic where this is applied.
        /// </summary>
        public static string GenderFilter = null;

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
        private static bool ContainsGenderToken(string lowercaseName, string token)
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

        public static void SetActiveContext(EmbContextReader ctx) => _activeContext = ctx;
        public static void ClearActiveContext() => _activeContext = null;

        /// <summary>
        /// UV-channel override for "number" / "carbon number" submeshes
        /// only. Set by the MainWindow's Number UV combobox.
        ///
        /// Default is Pair-2 (vb=3 pair-2). Verified by the user to
        /// produce correct digit placement across every 2025 team
        /// AND the FOM car -- the loader's earlier vb=4-first
        /// heuristic was wrong on Ferrari's front number and on both
        /// FOM number submeshes (it picked a pixel-strip on the
        /// driver atlas instead of the actual digit region). Pair-2
        /// is now the safe default and the UI surfaces Primary /
        /// Secondary / Auto as explicit overrides for any future car
        /// that turns out to need a different channel.
        /// </summary>
        public enum NumberUvSource { Auto, Primary, Pair2, Secondary }
        public static NumberUvSource NumberUvOverride = NumberUvSource.Pair2;
        public static void SetNumberUvOverride(NumberUvSource src) =>
            NumberUvOverride = src;

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
        private static readonly bool _debugSolidRedNumberOverlays = false;

        public static void SetTextureCache(TextureCache cache) => _textureCache = cache;
        public static void ClearTextureCache() => _textureCache = null;

        // Exposed so the FBX exporter's EnsureTexturePng can decode textures
        // from this loader's cache. Mirrors the F1ModelLoaderClassic /
        // F1ModelLoaderLegacy.ActiveTextureCache property the exporter already
        // checks. Without this, helmet exports (which always go through this
        // 2021+ loader) see cache==null in the exporter and silently skip the
        // PNG decode, so the FBX ships textureless.
        public static TextureCache ActiveTextureCache => _textureCache;

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

            // FIX 19: globalSlotUVScaleAndBias — four floats at mesh-descriptor
            // offset 0x18 (dwords [6..9]).  vehicle_metallic_paint.nefx2 applies
            // this per-slot transform to the decal/logo UV stream (UV2UV):
            //   decal_uv' = decal_uv * float2(Scale) + float2(Bias)
            // The float layout in the descriptor is (biasU, biasV, scaleU, scaleV).
            public float SlotUVBiasU;
            public float SlotUVBiasV;
            public float SlotUVScaleU;
            public float SlotUVScaleV;
            public bool HasSlotUVScaleBias;
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
            Log("F1ModelLoader.LoadFolder (NEFX2 / F1 2017-2021)");
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
            Log($"Final mesh descriptor count after LOD filter: {meshFilesToUse.Length}");

            int totalParts = 0;
            int totalSubmeshes = 0;
            int texturedSubmeshes = 0;
            int skippedPlaceholders = 0;
            _stats.Reset();

            foreach (var meshFile in meshFilesToUse)
            {
                Log("--------------------------------------------------");
                Log($"Loading mesh descriptor: {meshFile}");

                // Per-descriptor context switch. Track ERPs pack many
                // descriptors into one folder (e.g. `pit_perches/`
                // contains pit_perch_cadillac.idf, pit_perch_ferrari.idf,
                // etc.). Each ships its OWN .emb?context=<team>
                // binding that maps the descriptor's material slots
                // to that team's .material files. Without swapping
                // per descriptor, AutoConfigure's single _activeContext
                // is used across the board and every descriptor except
                // the one matching that context resolves to the wrong
                // team's materials (visible symptom: only one or two
                // team names + textures land correctly on a Mexico
                // track load; everything else is generic / white).
                //
                // Snapshot the folder-level context, look up the
                // matching per-descriptor .emb, swap it in, and restore
                // in a finally so unrelated descriptors keep their
                // folder-level fallback if lookup fails.
                var savedContext = _activeContext;
                var descContext = TryLoadPerDescriptorContext(meshFile);
                if (descContext != null)
                {
                    _activeContext = descContext;
                    Log($"  per-descriptor context: ctxId={descContext.ContextId} " +
                        $"({descContext.EntryCount} slot bindings)");
                }

                try
                {
                    var (partGroup, tex) = LoadOneMeshFile(meshFile);

                    if (partGroup == null)
                    {
                        Log("  -> (skipped: placeholder / all-zero LOD record)");
                        skippedPlaceholders++;
                        continue;
                    }

                    if (partGroup.Children.Count > 0)
                    {
                        group.Children.Add(partGroup);
                        totalParts++;
                        totalSubmeshes += partGroup.Children.Count;
                        texturedSubmeshes += tex;
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
                    // (whichever context AutoConfigure picked). Failing
                    // to restore would let one descriptor's context
                    // silently apply to all subsequent descriptors that
                    // don't have their own .emb sibling.
                    _activeContext = savedContext;
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
            //
            // HELMET FIX: helmet IDFs ship ONLY a consolidated mesh per LOD
            // (no per-part subdivision -- the whole helmet is one mesh file).
            // Blindly applying the drop-consolidated filter empties the list
            // and produces a geometry-free FBX. Only drop consolidated files
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

            // Already configured? Respect the caller's choice.
            if (_textureCache != null && _activeContext != null)
            {
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
                    // Fall back to whatever EnumerateContexts returns (numeric
                    // contexts first, default at the tail). This is the
                    // pre-helmet behaviour and is what car ERPs still use.
                    var contexts = EmbContextReader.EnumerateContexts(idfFolder).ToList();
                    if (contexts.Count > 0)
                    {
                        var (id, path) = contexts[0];
                        try
                        {
                            _activeContext = EmbContextReader.FromFile(path);
                            Log($"AutoConfigure: default livery context loaded (context={id}). " +
                                $"Call SetActiveContext(...) with another context to change driver.");
                        }
                        catch (Exception ex)
                        {
                            Log($"AutoConfigure: failed to load default context {path}: {ex.Message}");
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
                // Fast-path the canonical car layout first to avoid walking
                // the whole tree for stock content.
                string teams = Path.Combine(path, "teams");
                if (Directory.Exists(teams))
                {
                    foreach (var team in Directory.EnumerateDirectories(teams))
                    {
                        string mats = Path.Combine(team, "materials");
                        string texs = Path.Combine(team, "textures");
                        if (Directory.Exists(mats) && Directory.Exists(texs))
                            return true;
                    }
                }

                // Shared-generics car convention.
                string shared = Path.Combine(path, "shared");
                if (Directory.Exists(Path.Combine(shared, "generics", "textures")))
                    return true;

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
            catch { }
            return false;
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

        // =====================================================================
        // CORE: PARSE ONE MESH FILE (which may contain N submeshes)
        // =====================================================================
        // See comments on the sub-mesh 88-byte block layout in the previous
        // version; unchanged here.
        // =====================================================================

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

        private static (Model3DGroup group, int texturedCount) LoadOneMeshFile(string meshTemp000Path)
        {
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

            if (IsAllZero(meshBytes))
            {
                Log("  All-zero placeholder -- this LOD has no geometry for this part.");
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

            if (submeshCount == 0 || submeshCount > 32)
            {
                Log($"  ERROR: implausible submeshCount={submeshCount}.");
                return (null, 0);
            }

            int eaidStart = (int)eaidOffsetM76 + 76;
            int calcEaidStart = 0x4C + (int)submeshCount * 88;
            if (eaidStart != calcEaidStart)
                eaidStart = calcEaidStart;

            if (eaidStart < 0 || eaidStart >= meshBytes.Length)
            {
                Log($"  ERROR: EAID region (0x{eaidStart:X}) out of file bounds.");
                return (null, 0);
            }

            // HELMET FIX: consolidated whole-asset descriptors (helmet IDFs,
            // possibly other character kits) use larger per-submesh blocks
            // than per-part car descriptors -- the "88 bytes per submesh"
            // assumption embedded in calcEaidStart lands in the middle of
            // those blocks. The descriptor's own eaidOffsetM76 field also
            // points to a SECOND region (IB + submesh labels), not the VB
            // EAIDs. When the chosen offset doesn't begin with the canonical
            // "eaid://" ASCII prefix, scan forward to find it.
            //
            // For car per-part descriptors the formula already lands on
            // "eaid://" exactly, so this scan is a no-op and behaviour is
            // unchanged.
            if (!MatchesAscii(meshBytes, eaidStart, "eaid://"))
            {
                int scan = IndexOfAscii(meshBytes, eaidStart, "eaid://");
                if (scan > eaidStart && scan < meshBytes.Length)
                {
                    Log($"  EAID region: formula offset 0x{eaidStart:X} did not begin with 'eaid://'; scanned forward to 0x{scan:X}.");
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

            string ibEaid = eaids[eaids.Count - 1];
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

                // FIX 20e: log slots and VB properties for debugging
                {
                    string _vb(int idx)
                    {
                        if (idx < 0 || idx >= vbufs.Count) return "NONE";
                        var b = vbufs[idx];
                        return $"vb{idx}(Fvf={b.Fvf},stride={b.Stride},n={b.Count})";
                    }
                    Log($"  submesh[{si}] '{indexedLabel ?? "?"}' slots=[{slots[0]},{slots[1]},{slots[2]},{slots[3]},{slots[4]}] " +
                        $"pos={_vb(posIdx)} uv1={_vb(uvIdx)} uv2={_vb(uvIdx2)} uv3={_vb(uvIdx3)}");
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
                // 2026 GATE: only apply the 2020 mirror-glass reroute when
                // the slot-derived posIdx points at something that is NOT a
                // valid position buffer. F1 2020 mirror glass had its slot
                // resolve to a stride-8 packed-normals VB or an out-of-range
                // index, so the reroute was necessary. F1 2026 Ferrari ships
                // mirror_left/right with a perfectly valid stride-12 position
                // buffer at the slot-derived index (e.g. vb=12, FVF=39,
                // stride=12, n=214 matching the UV stream's vertex count) --
                // forcing the reroute to vb=11 there pulls in geometry from
                // an unrelated stream and produces visible "long strips" /
                // stretched vertices in the preview. Position buffers in this
                // format are universally stride 12 (float3 XYZ); anything
                // else is normals / weights / aux data and is what the 2020
                // reroute was meant to bypass.
                bool slotPosLooksValid =
                    posIdx >= 0 && posIdx < vbufs.Count &&
                    vbufs[posIdx].Stride == 12;

                if ((indexedLabel == "mirror_left" || indexedLabel == "mirror_right") &&
                    idxVb11 >= 0 && idxVb11 < vbufs.Count &&
                    posIdx != idxVb11 &&
                    !slotPosLooksValid)
                {
                    Log($"  [mirror-glass-reroute] '{indexedLabel}': slot-derived posIdx={posIdx} " +
                        $"-> vb=11 ({idxVb11}) (2020 mirror-glass routing)");
                    posIdx = idxVb11;
                    nrmIdx = -1;
                }
                else if ((indexedLabel == "mirror_left" || indexedLabel == "mirror_right") &&
                         idxVb11 >= 0 && slotPosLooksValid)
                {
                    Log($"  [mirror-glass-skip-reroute] '{indexedLabel}': slot-derived posIdx={posIdx} " +
                        $"is already a valid stride-12 position VB; ignoring 2020 vb=11 reroute.");
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

                    // FIX 19: globalSlotUVScaleAndBias lives at submesh-block
                    // offset 0x18 (four LE float32s = dwords [6..9]).  The
                    // vehicle_metallic_paint shader uses this to transform
                    // the secondary UV (me_logoMap) before sampling:
                    //   uv' = uv * float2(scale) + float2(bias)
                    // Layout in descriptor: (biasU, biasV, scaleU, scaleV).
                    SlotUVBiasU = ReadF32(meshBytes, blockOff + 0x18),
                    SlotUVBiasV = ReadF32(meshBytes, blockOff + 0x1C),
                    SlotUVScaleU = ReadF32(meshBytes, blockOff + 0x20),
                    SlotUVScaleV = ReadF32(meshBytes, blockOff + 0x24),
                    HasSlotUVScaleBias = true,
                });
            }

            if (subMeshes.Count == 0)
            {
                Log("  No usable submeshes -- nothing to draw.");
                return (null, 0);
            }

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
                if (string.Equals(sm.MaterialSlot, "rainbeads", StringComparison.OrdinalIgnoreCase))
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
                if (string.Equals(sm.MaterialSlot, "rainbeads", StringComparison.OrdinalIgnoreCase) &&
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
                bool isNumberSubmesh =
                    sm.Name.Equals("number", StringComparison.OrdinalIgnoreCase) ||
                    sm.Name.Equals("carbon number", StringComparison.OrdinalIgnoreCase) ||
                    sm.Name.Equals("carbon_number", StringComparison.OrdinalIgnoreCase);
                if (earlyPick.HasValue && !string.IsNullOrEmpty(earlyPick.Value.PickedSlot))
                {
                    string s = earlyPick.Value.PickedSlot;
                    bool slotMentionsLogo =
                        s.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        s.IndexOf("sponsor", StringComparison.OrdinalIgnoreCase) >= 0;
                    // Number submeshes ALSO need the secondary UV pair. Their
                    // resolved base sampler is me_albedoMap (which on the
                    // <team>_number_N.material binds to the driver-N digit
                    // atlas, NOT the paint atlas), so the slot name itself
                    // doesn't mention "logo" -- but the digit on the atlas
                    // lives in a small UV-panel region that the IDF authors
                    // on vb=4 (or vb=3 pair-2 as fallback), not on the
                    // body's vb=3 pair-1. Without this branch, Ferrari's
                    // unbounded body UV (-72..+32) tiled the digit into
                    // strips, and other teams' UV0 landed on transparent
                    // regions of the atlas making the digit invisible.
                    if (slotMentionsLogo || isNumberSubmesh)
                    {
                        // Both car paint/carbon shaders AND character cloth-
                        // logo shaders (char_logo_skinned*) put their logo /
                        // sponsor diffuse on a SEPARATE UV channel from the
                        // base body. Cars author it on the secondary UV pair
                        // (vb=4 / Fvf=29 stride=4); cloth-logo shaders author
                        // it on a parallel Fvf=13 stride=4 buffer the loader
                        // assigns to UvBufferIndex2 the same way. Treat both
                        // as "secondary UV pair" so the decoder picks the
                        // right channel; the only exception is character
                        // BODY shaders (cloth-skinned, helmet, skin), where
                        // there IS no separate logo geometry -- those are
                        // authored against UV1.
                        var fam = earlyPick.Value.Shader;
                        bool isCharacterBody =
                            (fam == MaterialSlotResolver.ShaderFamily.CharacterCloth ||
                             fam == MaterialSlotResolver.ShaderFamily.CharacterSkin ||
                             fam == MaterialSlotResolver.ShaderFamily.CharacterHelmet) &&
                            s.StartsWith("me_baseDiffuse", StringComparison.OrdinalIgnoreCase);
                        if (!isCharacterBody)
                            useSecondaryUvPair = true;
                    }
                }

                // FIX 6: UV range is now a per-submesh local, not a static field.
                var uvRange = (uMin: 0.0, uMax: 0.0, vMin: 0.0, vMax: 0.0);

                // USER OVERRIDE for number-submesh UV source. The
                // MainWindow exposes a "Number UV" dropdown so the user
                // can preview Primary / Pair-2 / Secondary directly
                // when the heuristic above picks wrong (Ferrari front
                // number, FOM car, etc.). When the override is in
                // effect we decode the requested channel below and
                // skip the heuristic's own try-and-fallback chain.
                bool overrideActive = isNumberSubmesh &&
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
                                // Fall back to primary if vb=4 absent.
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
                                bool altWraparound = aUMax > 1.05 || aUMin < -0.05 ||
                                                     aVMax > 1.05 || aVMin < -0.05;

                                // For CHARACTER CLOTH-LOGO shaders the
                                // full-atlas / wraparound rejection rules
                                // don't apply: cloth-logo geometry routinely
                                // covers most of the suit / glove and its
                                // valid UV2 channel maps across the entire
                                // logo atlas, which would normally trip the
                                // full-atlas guard. The guard is car-decal
                                // logic where logos should land in a small
                                // atlas region. Relax it for cloth so
                                // body_logos (currently invisible) can
                                // adopt vb3 pair-2 when vb4 is degenerate.
                                bool isClothLogoShader =
                                    earlyPick.HasValue &&
                                    earlyPick.Value.Shader == MaterialSlotResolver.ShaderFamily.CharacterCloth &&
                                    !string.IsNullOrEmpty(earlyPick.Value.PickedSlot) &&
                                    earlyPick.Value.PickedSlot.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0;
                                bool acceptAlt = isClothLogoShader
                                    ? !altDegenerate
                                    : (!altDegenerate && !altFullAtlas && !altWraparound);
                                if (acceptAlt)
                                {
                                    Log($"  NOTE: submesh '{sm.Name}' vb=4 decal UV span " +
                                        $"({uMax - uMin:0.000} x {vMax - vMin:0.000}) is " +
                                        $"degenerate; falling back to vb=3[4,6] (" +
                                        $"{aUMax - aUMin:0.000} x {aVMax - aVMin:0.000})" +
                                        (isClothLogoShader ? " [cloth-logo, full-atlas allowed]" : "") + ".");
                                    uvs = altUvs;
                                    uvRange = altRange;
                                }
                            }
                        }
                    }
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

                    // (2) Cap excessive UV tiling -- buffer-bbox variant
                    // (earlier build's exact logic, pre-FIX-32).
                    if (uvs != null)
                    {
                        var r = uvRange;
                        double maxAbs = Math.Max(
                            Math.Max(Math.Abs(r.uMin), Math.Abs(r.uMax)),
                            Math.Max(Math.Abs(r.vMin), Math.Abs(r.vMax)));
                        if (maxAbs > MaxUvTileRate)
                        {
                            double scale = MaxUvTileRate / maxAbs;
                            for (int k = 0; k < uvs.Length; k++)
                                uvs[k] = new System.Windows.Point(uvs[k].X * scale, uvs[k].Y * scale);
                            uvRange = (r.uMin * scale, r.uMax * scale,
                                            r.vMin * scale, r.vMax * scale);
                        }
                    }
                }

                // For NUMBER submeshes the BASE mesh now carries the digit
                // atlas itself (me_albedoMap on <team>_number_N.material
                // binds to <team>_driver_N.tif on 2025/2026 ERPs). Push
                // these vertices a hair out along their normals so the
                // number mesh sits in front of the body underneath -- without
                // an offset it z-fights with the body submesh and disappears
                // under WPF's depth test in the preview viewport.
                //
                // The original FIX 21 reasoning ("base sits flush, overlay
                // carries the digit") doesn't apply on current ERPs because
                // the overlay system doesn't fire for these number materials
                // (their me_logoMap binds to nothing useful), so HasOverlay
                // is false and the base IS the digit pass.
                Point3D[] meshPositions = positions;
                if (isNumberSubmesh && normals != null && normals.Length == positions.Length)
                {
                    const double kNumberBaseOffset = 0.005; // ~5mm in game units

                    // Offset DIRECTION needs to point AWAY from the part
                    // body. Per-vertex normals usually do that, but some
                    // submeshes (notably the Ferrari topcanopy_bm.number)
                    // ship with inverted normals -- the per-vertex normal
                    // points INTO the body, so +normal pushes the number
                    // mesh INSIDE the airbox shell and the body submesh
                    // occludes it.
                    //
                    // Robust fix: compute the centroid of THIS submesh's
                    // used vertices, then for each vertex check whether
                    // the per-vertex normal points roughly the same way
                    // as (vertex - centroid) -- the "outward" direction
                    // for a roughly-convex shell. If it points the wrong
                    // way (dot < 0), negate it before applying the
                    // offset. This leaves the well-authored case
                    // (Mercedes, McLaren, Red Bull etc.) untouched and
                    // fixes the inverted Ferrari case automatically
                    // without needing a per-team table.
                    double cx = 0, cy = 0, cz = 0;
                    int cn = 0;
                    if (rawIndices != null)
                    {
                        for (int ri = 0; ri < rawIndices.Length; ri++)
                        {
                            int vi = rawIndices[ri];
                            if (vi < 0 || vi >= positions.Length) continue;
                            cx += positions[vi].X;
                            cy += positions[vi].Y;
                            cz += positions[vi].Z;
                            cn++;
                        }
                    }
                    if (cn > 0)
                    {
                        cx /= cn; cy /= cn; cz /= cn;
                    }

                    meshPositions = new Point3D[positions.Length];
                    for (int i = 0; i < positions.Length; i++)
                    {
                        var n = normals[i];
                        double nx = n.X, ny = n.Y, nz = n.Z;
                        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (len > 1e-6) { nx /= len; ny /= len; nz /= len; }

                        var p = positions[i];
                        // Outward direction relative to the submesh
                        // centroid. If the per-vertex normal opposes it,
                        // the geometry was authored with inverted normals
                        // -- flip the offset for this vertex.
                        double ox = p.X - cx, oy = p.Y - cy, oz = p.Z - cz;
                        double dot = nx * ox + ny * oy + nz * oz;
                        double sign = (dot < 0) ? -1.0 : 1.0;

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
                // Register with material EAID too so the FBX exporter can name
                // the Assimp Material after the game's actual .material file.
                MeshSubmeshRegistry.Register(mesh, sm.Name, sm.DescriptorHint, materialEaid);

                // ---- Build the material for this submesh ----
                //
                // Every submesh gets ONE log line in the form
                //   [PART.N] slot=<SLOT> mat=<MATERIAL> pick=<SAMPLER> tex=<TEXTURE>  [OK|WHITE: <reason>]
                // This is the primary diagnostic tool when textures land
                // on the wrong geometry: compare the per-submesh line to
                // where that submesh sits in 3D space.
                Material material = null;
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
                        // Diagnostic: pixel dimensions of the brush actually
                        // about to be bound. If this shows the hi-res numbers
                        // but the viewport still appears low-res, the issue
                        // lies in WPF rendering / bitmap scaling, not in the
                        // sidecar lookup.
                        if (diffuseBrush is System.Windows.Media.ImageBrush diagIb &&
                            diagIb.ImageSource is System.Windows.Media.Imaging.BitmapSource diagBs)
                        {
                            Log($"    bound brush dims: {diagBs.PixelWidth}x{diagBs.PixelHeight}");
                        }
                        material = new DiffuseMaterial(diffuseBrush);
                        texturedCount++;
                        logStatus = "OK";

                        // Persist the resolved texture EAID for the FBX
                        // exporter -- it calls EnsureTexturePng with this EAID
                        // to decode the texture into a PNG sidecar next to
                        // the .fbx. Without this registration the exporter
                        // has no EAID to look up, EnsureTexturePng never runs,
                        // and the .fbx ships textureless even though we
                        // successfully bound the brush in the WPF viewport.
                        if (!string.IsNullOrEmpty(res.TextureEaid))
                            MeshSubmeshRegistry.SetTextureEaid(mesh, res.TextureEaid);
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
                         sm.Name.Equals("detail", StringComparison.OrdinalIgnoreCase))
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
                        sm.Name.Equals("number", StringComparison.OrdinalIgnoreCase) ? "number submesh" :
                        sm.Name.Equals("carbon number", StringComparison.OrdinalIgnoreCase) ||
                        sm.Name.Equals("carbon_number", StringComparison.OrdinalIgnoreCase) ? "carbon_number submesh" :
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
                sm.Name.Equals("number", StringComparison.OrdinalIgnoreCase) ||
                sm.Name.Equals("carbon number", StringComparison.OrdinalIgnoreCase) ||
                sm.Name.Equals("carbon_number", StringComparison.OrdinalIgnoreCase);

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
            int[] FilterOverlayTris(System.Windows.Point[] uvArr)
            {
                const double kLo = -0.05, kHi = 1.05;
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

            var priUvs = DecodeUvStream(
                vbufs, sm.UvBufferIndex, sm.VOffset, maxIndex,
                useSecondaryUvPair: true, out _);
            string priReject;
            if (priUvs == null)
            {
                priReject = "decode failed";
            }
            else
            {
                int[] kept = FilterOverlayTris(priUvs);
                if (kept.Length == 0)
                {
                    priReject = "no in-range triangles";
                }
                else
                {
                    priReject = ValidateOverlayUvs(priUvs, kept, 2.0,
                        out _, out _, out _, out _);
                    if (priReject == null)
                    {
                        sponsorUvs = priUvs;
                        overlayIndices = kept;
                        pickedUvSource = "vb3-pair2";
                        if (kept.Length < rawIndices.Length)
                            Log($"     overlay tri-filter '{sm.Name}': kept " +
                                $"{kept.Length / 3}/{rawIndices.Length / 3} triangles (rest tiled/out-of-range)");
                    }
                }
            }

            if (sponsorUvs == null &&
                (priReject == "no in-range triangles" || priReject == "decode failed"))
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
                    int[] fbKept = fbUvs == null ? Array.Empty<int>() : FilterOverlayTris(fbUvs);
                    string fbReject =
                        fbUvs == null ? "decode failed" :
                        fbKept.Length == 0 ? "no in-range triangles" :
                        ValidateOverlayUvs(fbUvs, fbKept, 2.0, out _, out _, out _, out _);
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

                    // TEMP DEBUG: dump the overlay atlas to PNG so the
                    // sampled region can be inspected by eye. Remove once
                    // the rear wing banner mapping is confirmed.
                    try
                    {
                        if (tileSrc is System.Windows.Media.ImageBrush dbgIb &&
                            dbgIb.ImageSource is System.Windows.Media.Imaging.BitmapSource dbgBs)
                        {
                            string dbgPath = Path.Combine(Path.GetTempPath(),
                                "F1modding3dtoolV1", "debug_decal_atlas.png");
                            using (var dbgFs = File.Create(dbgPath))
                            {
                                var dbgEnc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                dbgEnc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(dbgBs));
                                dbgEnc.Save(dbgFs);
                            }
                            Log($"     DEBUG: dumped overlay atlas -> {dbgPath}");
                        }
                    }
                    catch { }
                }
            }

            // =================================================================
            // DIAGNOSTIC MODE (temporary): force number overlays to render as
            // solid opaque red, bypassing the driver atlas brush. Flip
            // _debugSolidRedNumberOverlays to false to restore normal rendering.
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
            if (_debugSolidRedNumberOverlays && isNumberOverlay)
            {
                var redBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Colors.Red);
                redBrush.Freeze();
                brush = redBrush;
                Log($"     DEBUG: overriding '{sm.Name}' overlay brush with solid RED");
            }

            // 5. Build offset positions: pos + normal * epsilon. F1 cars are
            //    roughly 5 model units long so 0.0005 is sub-millimetre --
            //    invisible but enough to win the depth test cleanly.
            //    FIX 21: number overlays use a larger offset (0.002) because
            //    the number base mesh z-fights with the body underneath.
            double offsetAmount = isNumberOverlay ? 0.002 : 0.0005;
            int posCount = positions.Length;
            var offsetPositions = new Point3D[posCount];
            for (int k = 0; k < posCount; k++)
            {
                if (k < normals.Length)
                {
                    var n = normals[k];
                    offsetPositions[k] = new Point3D(
                        positions[k].X + n.X * offsetAmount,
                        positions[k].Y + n.Y * offsetAmount,
                        positions[k].Z + n.Z * offsetAmount);
                }
                else
                {
                    offsetPositions[k] = positions[k];
                }
            }

            // FIX 20e: use raw pair-2 UVs with NO modifications.
            //
            // The vb=4 decal UVs are authored by Codemasters' pipeline to
            // sample me_logoMap directly — each vertex's UV points at the
            // exact pixel on the decal atlas where its sponsor content sits.
            // The shader does NOT apply scale/bias or me_uOffset to the
            // pair-2 stream; those parameters affect the pair-1 paint UVs
            // or other shader passes. Applying any transform here shifted
            // every sponsor to the wrong atlas region.
            System.Windows.Point[] shiftedUvs = sponsorUvs;

            // 6. Assemble the overlay mesh.
            // FIX 20: log the final UV bbox for the overlay (USED verts only)
            {
                double fUmin = double.PositiveInfinity, fUmax = double.NegativeInfinity;
                double fVmin = double.PositiveInfinity, fVmax = double.NegativeInfinity;
                for (int ri = 0; ri < overlayIndices.Length; ri++)
                {
                    int vi = overlayIndices[ri];
                    if (vi >= 0 && vi < shiftedUvs.Length)
                    {
                        double u = shiftedUvs[vi].X, v = shiftedUvs[vi].Y;
                        if (u < fUmin) fUmin = u; if (u > fUmax) fUmax = u;
                        if (v < fVmin) fVmin = v; if (v > fVmax) fVmax = v;
                    }
                }
                Log($"     overlay OK '{sm.Name}' [{pickedUvSource}]: final UV=[{fUmin:F3},{fUmax:F3}]x[{fVmin:F3},{fVmax:F3}]");
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
            int uvCount = shiftedUvs.Length;
            var uvFixed = new System.Windows.Point[uvCount + 2];
            for (int k = 0; k < uvCount; k++)
            {
                double cu = shiftedUvs[k].X, cv = shiftedUvs[k].Y;
                if (cu < 0) cu = 0; else if (cu > 1) cu = 1;
                if (cv < 0) cv = 0; else if (cv > 1) cv = 1;
                uvFixed[k] = new System.Windows.Point(cu, cv);
            }
            uvFixed[uvCount] = new System.Windows.Point(0, 0);
            uvFixed[uvCount + 1] = new System.Windows.Point(1, 1);

            var posFixed = new Point3D[offsetPositions.Length + 2];
            Array.Copy(offsetPositions, posFixed, offsetPositions.Length);
            posFixed[offsetPositions.Length] = offsetPositions.Length > 0 ? offsetPositions[0] : new Point3D();
            posFixed[offsetPositions.Length + 1] = posFixed[offsetPositions.Length];

            var nrmFixed = new Vector3D[normals.Length + 2];
            Array.Copy(normals, nrmFixed, normals.Length);

            var overlayMesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(posFixed),
                Normals = new Vector3DCollection(nrmFixed),
                TriangleIndices = new Int32Collection(overlayIndices),
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

            var overlayMat = new DiffuseMaterial(brush);
            overlayMat.Freeze();

            var overlayModel = new GeometryModel3D(overlayMesh, overlayMat)
            {
                BackMaterial = overlayMat,
            };
            sponsorTexShort = ShortenEaid(resolved.OverlayTextureEaid);
            return overlayModel;
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

            switch (nvb.Fvf)
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

            switch (uvb.Fvf)
            {
                case 29:
                case 13:  // Character-cloth logo UV channel (Fvf=13, stride=4 half2).
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

            if (uvb.Fvf != 29)
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

        // =====================================================================
        // LOW-LEVEL UTILITIES
        // =====================================================================

        private static uint ReadU32(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
        private static float ReadF32(byte[] data, int offset) => BitConverter.ToSingle(data, offset);

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

        private static bool IsAllZero(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
                if (data[i] != 0) return false;
            return true;
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