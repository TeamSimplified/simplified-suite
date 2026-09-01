using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Xml.Linq;
using HelixToolkit.Wpf;

namespace F1modding3dtoolV1
{
    public static class F1ModelLoaderClassic
    {
        // =====================================================================
        // LOGGING
        // =====================================================================

        private static string _logPath = "";

        private static int _totalSubmeshes = 0;
        private static int _totalIndices = 0;
        private static int _totalTriangles = 0;
        private static int _totalVertices = 0;

        // Explicit-LOD override for <see cref="LoadFolderExplicitLod"/>.
        // When non-null, the LOD-selection block in
        // <see cref="LoadFolder(string, string)"/> bypasses its usual
        // "pick lod=0 > pick lod=all > pick smallest-numeric" heuristic
        // and instead uses ONLY descriptor files whose filename contains
        // "lod={value}". This is what the glTF exporter needs to emit a
        // separate {team}_LOD{N}.gltf for every LOD present in the cache,
        // rather than writing lod=0 geometry into every file.
        //
        // Always set from inside LoadFolderExplicitLod's try/finally so a
        // throw inside LoadFolder can't leak this state into a subsequent
        // call on the same thread.
        private static int? _requestedExplicitLod = null;

        // Full path of the descriptor file currently being processed by
        // the outer foreach loop in LoadFolder. Populated right before
        // each per-descriptor load call, cleared afterwards. Threaded to
        // the per-submesh registration site (2 thousand lines away) as
        // state rather than as a parameter because the intermediate
        // LoadLegacyMeshDescriptor -> ParseHeader -> BuildGeometry chain
        // doesn't otherwise carry the source filename, and plumbing it
        // as a parameter through that chain would touch half a dozen
        // signatures just to move one string.
        //
        // The glTF exporter's TryResolveLoadedPartNameFromModel reads this
        // indirectly via MeshSubmeshRegistry.GetDescriptorHint -- the file
        // path encodes the part name (e.g. "lod=0&safetycell&mesh" in the
        // filename) which the exporter's ExtractDescriptorPartNameFromHint
        // regex pulls cleanly. That's the primary way 2018 parts get
        // named correctly in the output .gltf; the older
        // ResolvePartNameForSubmesh + UniversalIdfNaming.GetPartName
        // combo returns junk like "body|aren/idf/mclaren" for 2018
        // because the VB EAID it uses doesn't carry the part name.
        private static string _currentDescriptorPath = null;

        // Part name parsed from _currentDescriptorPath (e.g. "safetycell"
        // from "...lod=0&safetycell&mesh"). Used at the MeshFamilyRegistry
        // registration site so MeshFamilyRegistry.GetFamily returns a
        // clean canonical name that matches the ?render node graph
        // instead of the composite junk ResolvePartNameForSubmesh
        // synthesises for 2018.
        private static string _currentDescriptorPartName = null;

        // True while LoadLegacyMeshDescriptor is parsing a classic tyre
        // descriptor on behalf of TryLoadClassicTyres. The flag exists to
        // suppress the body-only root-union slot allowlist (see the long
        // comment at line ~2960) when a tyre descriptor passes through
        // the same parser. That allowlist is hard-coded to body slot
        // names like "number", "generic", "carbon 4", "lcd" -- none of
        // which appear in a tyre descriptor. Without this bypass, every
        // tyre mesh record gets `continue`d in the slot-filter loop and
        // the parser emits 0 submeshes from N records.
        //
        // The flag is set true in TryLoadClassicTyres immediately before
        // each LoadLegacyMeshDescriptor call and reset in the finally
        // block. Body loads never touch it so they're unaffected.
        private static bool _inTyreLoadPass = false;

        // Era code ("c_03", "c_07", ...) detected from the body cache's
        // shared/tyres/textures/sidewall folder, set by TryLoadClassicTyres
        // for use by BuildMaterialEaidForSlot when resolving tyre slot
        // materials. Without this we can't know which c_NN_sidewall*.material
        // belongs to the current car (Tyres.erp ships materials for ALL
        // eras, e.g. c_03 + c_07 in a single F1 2018 install).
        //
        // Cleared back to null in the same finally block as _inTyreLoadPass.
        // BuildMaterialEaidForSlot only consults this when _inTyreLoadPass
        // is true, so a body load that happens to use a slot name like
        // "wall" or "tread" (none currently do, but defensively) won't
        // accidentally route to a tyre material.
        private static string _currentTyreEra = null;

        /// <summary>
        /// The folder LoadFolder ended up reading mesh descriptors from
        /// in its most recent call. Differs from the caller-supplied
        /// input when the SELF-HEAL redirect at the top of LoadFolder
        /// picked a richer teams/&lt;X&gt;/idf folder over a thinner
        /// shared/tyres/idf one (typical when the user points at a
        /// classic ERP cache and the tyre folder happens to be picked
        /// by the input path resolver).
        ///
        /// MainWindow's "Assemble car" toggle reads this so the
        /// node-graph lookup ends up walking the body's render file
        /// rather than the tyres' (which only has 29 LOD-marker nodes,
        /// kind=2 count = 0 -- producing 0 matches and leaving every
        /// body part at identity). The original _currentClassicFolder
        /// in MainWindow is still set to the user's input path BEFORE
        /// any self-heal happens, so on its own it can't be trusted
        /// for assembly.
        ///
        /// Null until LoadFolder has run successfully at least once.
        /// </summary>
        public static string LastResolvedFolder { get; private set; }

        // =====================================================================
        // TEXTURE PIPELINE HOOKUP (F1 2018)
        // =====================================================================
        //
        // Mirrors F1ModelLoader.AutoConfigure's TextureCache setup. Populated
        // once per LoadFolder call from the extracted ERP cache that contains
        // teams/<team>/materials and teams/<team>/textures siblings. When
        // this cache is null (e.g. the ERP was loaded standalone without
        // sidecar extraction), every submesh falls back to the neutral
        // gray DiffuseMaterial, same as before the hookup existed -- so
        // adding this path is strictly additive and cannot regress the
        // positions-only preview we had working.
        //
        // _detectedPackageName is sniffed from a mesh descriptor payload
        // (the same "f1_2018_vehicle_package" ASCII that our is2018 check
        // already looks for) so we can reconstruct material EAIDs like
        //   eaid://f1_2018_vehicle_package/teams/mclaren/materials/mclaren_paint.material
        // from submesh slot names. Without this, the TextureCache can't
        // find anything on disk because EaidPathResolver strips the
        // package segment when joining with PackageRoot.
        private static TextureCache _textureCache;
        private static string _detectedPackageName;

        // Livery binding context. F1 2018 ERPs ship 100 GfxMatBindRes
        // resources (one per driver number: mclaren.emb?context=1..99)
        // that contain the authoritative slot -> material-EAID mapping
        // for each livery. Hardcoded slot lookups are a fragile fallback;
        // the context is the real source of truth. Loaded once per
        // LoadFolder call from the same cache folder as the mesh
        // descriptors; used by BuildMaterialEaidForSlot to resolve
        // "generic", "flag", "disc", "lcd", etc. to the correct
        // shared/generics/*.material file (which the hardcoded table
        // had been mapping to wrong team-local materials).
        private static EmbContextReader _activeContext;

        // Driver number used for "number"-slot submeshes. F1 2018 ships 100
        // per-driver number atlases (mclaren_number_1.material ..
        // mclaren_number_99.material); only ONE of them renders at a time.
        // Defaulting to 14 (Alonso at McLaren 2018). Callers can override
        // via SetPreviewDriverNumber before LoadFolder runs.
        public static int PreviewDriverNumber { get; set; } = 14;

        /// <summary>
        /// Optional override: explicitly supply a TextureCache before
        /// LoadFolder runs. Mostly used by tests; end users normally rely
        /// on LoadFolder's auto-configure, which walks up from the idf
        /// cache folder looking for a teams/&lt;team&gt;/materials sibling.
        /// Pass null to clear and let LoadFolder re-auto-configure.
        /// </summary>
        public static void SetTextureCache(TextureCache cache) => _textureCache = cache;

        // -----------------------------------------------------------
        // CACHE FILENAME CONVENTION (auto-detected per load)
        // -----------------------------------------------------------
        // The extracted-IDF cache uses one of two filename styles:
        //
        //   F1 2018 stock (mclaren, etc.):
        //     <team>!!!temp00N.idf%3Flod=0&safetycell&mesh
        //     <team>!!!temp00N.idf%3Flod=all&vb=4
        //   ".idf%3F" prefix, "&" separator, "!!!" between team and temp.
        //
        //   F1 2018 classic content (c_mclaren_98, c_brawn_09, etc.,
        //   from f1_classic_0[123]_vehicle_package):
        //     <team>___temp00N.idf_3Flod_0_safetycell_mesh
        //     <team>___temp00N.idf_3Flod_all_vb_4
        //   ".idf_3F" prefix, "_" separator, "___" between team and temp.
        //
        // Same binary layout in both, only the cache filename differs.
        // We sniff one file in the cache at LoadFolder time and set
        // these statics; BuildSuffixFromName / LoadBufVertex /
        // LoadBufIndex use them to produce filenames the on-disk cache
        // actually has.
        //
        // Defaults match stock convention so a load that fails sniffing
        // (empty folder, exotic naming) behaves the same as before this
        // change.
        // -----------------------------------------------------------
        private static string _cacheSeparator = "!!!";
        private static string _cacheQueryEnc = "%3F";
        private static string _cacheEqualsEnc = "=";
        private static string _cacheAmpEnc = "&";

        /// <summary>
        /// Sniff one filename in <paramref name="folder"/> to decide which
        /// cache-naming convention is in use, and update the static fields
        /// accordingly. Idempotent and cheap; safe to call once per
        /// LoadFolder. Falls back to stock convention when no diagnostic
        /// file is present (which means the directory is empty enough that
        /// the loader's own enumeration will fail later anyway).
        /// </summary>
        private static void DetectCacheConvention(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;

                // Look for any file containing "temp000" or "temp001" --
                // that's the section-index marker shared by both
                // conventions. The character preceding it tells us
                // which separator and URL-encoding style is in use.
                foreach (var path in Directory.EnumerateFiles(
                             folder, "*temp00*", SearchOption.TopDirectoryOnly))
                {
                    string nm = Path.GetFileName(path);
                    if (nm.Contains("___"))
                    {
                        _cacheSeparator = "___";
                        _cacheQueryEnc = "_3F";
                        _cacheEqualsEnc = "_";
                        _cacheAmpEnc = "_";
                        Log("DetectCacheConvention: classic underscore convention " +
                            $"(sep='___', query='_3F', eq='_', amp='_') -- e.g. '{nm}'");
                        return;
                    }
                    if (nm.Contains("!!!"))
                    {
                        _cacheSeparator = "!!!";
                        _cacheQueryEnc = "%3F";
                        _cacheEqualsEnc = "=";
                        _cacheAmpEnc = "&";
                        Log("DetectCacheConvention: stock URL-encoded convention " +
                            $"(sep='!!!', query='%3F', eq='=', amp='&') -- e.g. '{nm}'");
                        return;
                    }
                }
                Log("DetectCacheConvention: no temp00* file with a recognised " +
                    "separator found; defaulting to stock convention.");
            }
            catch (Exception ex)
            {
                Log($"DetectCacheConvention: sniff threw ({ex.Message}); " +
                    "defaulting to stock convention.");
            }
            _cacheSeparator = "!!!";
            _cacheQueryEnc = "%3F";
            _cacheEqualsEnc = "=";
            _cacheAmpEnc = "&";
        }


        // -----------------------------------------------------------
        // PER-MESH VERTEX-OFFSET REGISTRY
        // -----------------------------------------------------------
        // Maps every loaded MeshGeometry3D to the SubMeshInfo.VOffset
        // it was sliced from. The exporter's root-union redistribution
        // pass needs this when looking up the per-vertex bone byte in
        // vb=5: the WPF mesh's TriangleIndices are 0-based local
        // indices, but vb=5 is a global 293988-entry array shared by
        // every LOD, so the correct lookup is vb5[wpf_index + VOffset].
        //
        // For LOD0 the root-union descriptor happens to use VOffset=0,
        // so omitting the offset "worked" by accident. For LOD1
        // (VOffset=145118) and LOD2 (VOffset=249102) the omission
        // misroutes every redistributed triangle to the wrong bone
        // and therefore the wrong owning per-part -- visible to the
        // user as parts "confused with each other" at non-zero LODs.
        // -----------------------------------------------------------
        private static readonly System.Collections.Generic.Dictionary<
            System.Windows.Media.Media3D.MeshGeometry3D, int> _meshVOffsets =
            new System.Collections.Generic.Dictionary<
                System.Windows.Media.Media3D.MeshGeometry3D, int>();
        private static readonly object _meshVOffsetsLock = new object();

        public static void RegisterMeshVOffset(
            System.Windows.Media.Media3D.MeshGeometry3D mesh, int vOffset)
        {
            if (mesh == null) return;
            lock (_meshVOffsetsLock) { _meshVOffsets[mesh] = vOffset; }
        }

        public static int GetMeshVOffset(
            System.Windows.Media.Media3D.MeshGeometry3D mesh)
        {
            if (mesh == null) return 0;
            lock (_meshVOffsetsLock)
            {
                return _meshVOffsets.TryGetValue(mesh, out int v) ? v : 0;
            }
        }

        public static void ClearMeshVOffsets()
        {
            lock (_meshVOffsetsLock) { _meshVOffsets.Clear(); }
        }

        // -----------------------------------------------------------
        // PER-MESH DAMAGE-UV REGISTRY (vb=4 -> TEXCOORD_2)
        // -----------------------------------------------------------
        // Maps every loaded MeshGeometry3D to the per-vertex damage
        // UVs decoded from stock IDF vb=4. The damage atlas texture
        // is mclaren_main_damage.tif, bound to me_damageMap on both
        // vehicle_metallic_paint.nefx2 (body) and vehicle_carbon
        // .nefx2 (carbon); vb=4 is the UV channel that feeds it.
        //
        // Stock body / number / generic verts have per-vertex
        // authored vb=4 values with no closed-form relation to UV0,
        // so the only way the writer can reproduce them byte-
        // identically is to round-trip them through a dedicated
        // glTF channel: stock vb=4 -> this registry -> exporter
        // surfaces as TEXCOORD_2 -> writer's importer reads into
        // SubMesh.Uvs2 -> writer routes back into vb=4. When this
        // registry is empty for a mesh (common for non-classic
        // load paths or non-Pool-A submeshes), the exporter omits
        // TEXCOORD_2 and the writer falls back to its v11.49
        // synth / UV0 paths.
        // -----------------------------------------------------------
        private static readonly System.Collections.Generic.Dictionary<
            System.Windows.Media.Media3D.MeshGeometry3D, PointCollection> _meshDamageUvs =
            new System.Collections.Generic.Dictionary<
                System.Windows.Media.Media3D.MeshGeometry3D, PointCollection>();
        private static readonly object _meshDamageUvsLock = new object();

        public static void RegisterMeshDamageUvs(
            System.Windows.Media.Media3D.MeshGeometry3D mesh, PointCollection damageUvs)
        {
            if (mesh == null) return;
            if (damageUvs == null || damageUvs.Count == 0) return;
            lock (_meshDamageUvsLock) { _meshDamageUvs[mesh] = damageUvs; }
        }

        public static PointCollection GetMeshDamageUvs(
            System.Windows.Media.Media3D.MeshGeometry3D mesh)
        {
            if (mesh == null) return null;
            lock (_meshDamageUvsLock)
            {
                return _meshDamageUvs.TryGetValue(mesh, out var uvs) ? uvs : null;
            }
        }

        public static void ClearMeshDamageUvs()
        {
            lock (_meshDamageUvsLock) { _meshDamageUvs.Clear(); }
        }

        /// <summary>
        /// Read-only accessor for the currently-active TextureCache. The
        /// glTF exporter uses this after <see cref="LoadFolder(string)"/>
        /// returns to pull raw BGRA pixel data for every texture EAID
        /// stored in <see cref="MeshSubmeshRegistry"/>, encode it as PNG,
        /// and embed the bytes into the .gltf as base64 data URIs. The
        /// cache stays resident between LoadFolder and the exporter's
        /// post-processing step; the next LoadFolder call resets it.
        /// </summary>
        public static TextureCache ActiveTextureCache => _textureCache;

        /// <summary>
        /// Walk up from the idf cache folder looking for a directory whose
        /// teams/&lt;any team&gt;/materials + textures siblings exist. That
        /// directory is the package root. Mirrors F1ModelLoader's
        /// DiscoverPackageRoot heuristic so the F1 2018 classic path uses
        /// the same discovery semantics as the 2021+ path.
        /// </summary>
        private static string DiscoverPackageRoot(string startFolder)
        {
            if (string.IsNullOrEmpty(startFolder)) return null;
            string cur;
            try { cur = Path.GetFullPath(startFolder); }
            catch { return null; }

            for (int hop = 0; hop < 8 && !string.IsNullOrEmpty(cur); hop++)
            {
                if (LooksLikePackageRoot(cur)) return cur;
                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) ||
                    string.Equals(parent, cur, StringComparison.Ordinal))
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
                if (!Directory.Exists(teams)) return false;

                foreach (var team in Directory.EnumerateDirectories(teams))
                {
                    string mats = Path.Combine(team, "materials");
                    string texs = Path.Combine(team, "textures");
                    if (Directory.Exists(mats) && Directory.Exists(texs))
                        return true;
                }

                if (Directory.Exists(Path.Combine(path, "shared", "generics", "textures")))
                    return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Walk up at most 6 levels from <paramref name="folder"/> looking
        /// for a directory that contains a "teams" subfolder (the
        /// extractor's cache root). If found, enumerate teams/&lt;X&gt;/idf
        /// folders and return the one with the most *temp000*mesh*
        /// descriptors. Returns <paramref name="folder"/> unchanged if no
        /// cache root is found, no team idf folder beats it, or only one
        /// team idf folder exists and it's the supplied one.
        ///
        /// Defensive helper for classic-content extracts where the
        /// picker sometimes hands us shared/tyres/idf instead of
        /// teams/&lt;team&gt;/idf. Also covers any future picker
        /// regression -- as long as the cache layout has the team
        /// folder in a discoverable position, the loader self-heals.
        /// </summary>
        private static string TryRedirectToRichestTeamIdfFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return folder;

            int currentMeshCount;
            try
            {
                currentMeshCount = Directory.GetFiles(folder, "*temp000*mesh*",
                                                     SearchOption.TopDirectoryOnly).Length;
            }
            catch
            {
                return folder;
            }

            // Find the cache root: the nearest ancestor that contains a
            // "teams" subdirectory.
            string cacheRoot = null;
            string cur;
            try { cur = Path.GetFullPath(folder); }
            catch { return folder; }

            for (int hop = 0; hop < 6 && !string.IsNullOrEmpty(cur); hop++)
            {
                string teamsDir = Path.Combine(cur, "teams");
                if (Directory.Exists(teamsDir))
                {
                    cacheRoot = cur;
                    break;
                }
                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) ||
                    string.Equals(parent, cur, StringComparison.Ordinal))
                    break;
                cur = parent;
            }
            if (cacheRoot == null) return folder;

            string teamsRoot = Path.Combine(cacheRoot, "teams");

            // Enumerate every teams/<X>/idf folder and find the one
            // with the most mesh descriptors.
            string bestFolder = folder;
            int bestCount = currentMeshCount;
            try
            {
                foreach (var teamDir in Directory.EnumerateDirectories(teamsRoot))
                {
                    string idfDir = Path.Combine(teamDir, "idf");
                    if (!Directory.Exists(idfDir)) continue;
                    int n;
                    try
                    {
                        n = Directory.GetFiles(idfDir, "*temp000*mesh*",
                                               SearchOption.TopDirectoryOnly).Length;
                    }
                    catch { continue; }
                    if (n > bestCount)
                    {
                        bestFolder = idfDir;
                        bestCount = n;
                    }
                }
            }
            catch { /* fall through to caller */ }

            return bestFolder;
        }

        // ---------------------------------------------------------------
        // Classic-tyres pass: locate, prompt, and load the bundled tyre
        // model that sits alongside the team body in a classic ERP cache.
        //
        // Why this is hairy:
        //
        // The original implementation hard-coded "<cacheRoot>/shared/tyres/idf"
        // as the tyre folder location. That works for f1_classic_02 cars in
        // F1 2018 (the McLaren 98 case) but misses every other layout: F1
        // 2017 organises classics differently, F1 2019/2020 added more
        // packages, some classics use "tires" (American spelling) instead
        // of "tyres", and a handful of cars place the tyre model under
        // teams/<team>/idf rather than shared/.
        //
        // The replacement uses two layers of metadata:
        //
        //   1. AUTHORITATIVE classic check via the cache's own metadata
        //      files. The extractor writes _cache_ok.txt and _extract_log.txt
        //      into the cache root, both of which preserve the original
        //      ERP path. If that path contains "f1_classic_0X_vehicle_package"
        //      we KNOW this is a classic and should search hard for tyres.
        //      If it contains "f1_20XX_vehicle_package" we KNOW it's modern
        //      and can skip the tyre prompt entirely (modern tyres live in
        //      a separate package referenced via cross-package eaid URIs).
        //
        //   2. MULTI-STRATEGY folder search with scoring. Instead of one
        //      hardcoded path, we walk the cache and rate every candidate
        //      directory against five signals (folder name, era-coded
        //      filenames, generic tyre filenames, sibling materials with
        //      sidewall/tread, presence of vb/ib companions). Whichever
        //      folder scores highest wins; ties broken by mesh count. If
        //      nothing scores positive we log every folder considered so a
        //      future failure case is debuggable from the log alone.
        //
        // This whole region is dead code for modern (2017+) cars because
        // the hybrid dispatcher in F1ModelLoader.LoadFolder only forwards
        // to F1ModelLoaderClassic when classic content is detected, and
        // the metadata check above gives us a second line of defence.
        // ---------------------------------------------------------------

        private const string TyreCacheOkFileName = "_cache_ok.txt";
        private const string TyreExtractLogFileName = "_extract_log.txt";

        /// <summary>
        /// Result of inspecting cache-root metadata files. Captures
        /// whether the source ERP was classic, modern, or undetermined,
        /// plus the original ERP path string for diagnostic logs.
        /// </summary>
        private sealed class ClassicCacheMetadata
        {
            public bool IsClassic;             // path contains f1_classic_0X_vehicle_package
            public bool IsModern;              // path contains f1_20XX_vehicle_package
            public string OriginalErpPath;     // first line of cache_ok / extract_log
            public string PackageName;         // e.g. "f1_classic_02_vehicle_package"
            public string CacheRoot;           // resolved cache root directory
            public string Source;              // which file the metadata came from

            public bool IsKnown => IsClassic || IsModern;
        }

        /// <summary>
        /// Locate the cache root that contains <paramref name="folder"/>.
        /// Definition of "cache root" for this loader: the closest ancestor
        /// containing a "teams" subdirectory. Most caches also have a
        /// "shared" subdirectory and a _cache_ok.txt marker, but neither is
        /// required for this method to return successfully.
        /// </summary>
        private static string FindClassicCacheRoot(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;

            string cur;
            try { cur = Path.GetFullPath(folder); }
            catch { return null; }

            for (int hop = 0; hop < 6 && !string.IsNullOrEmpty(cur); hop++)
            {
                if (Directory.Exists(Path.Combine(cur, "teams")))
                    return cur;

                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) ||
                    string.Equals(parent, cur, StringComparison.Ordinal))
                    break;
                cur = parent;
            }
            return null;
        }

        /// <summary>
        /// Read the cache root's metadata files (_cache_ok.txt and/or
        /// _extract_log.txt) to determine whether the source ERP was a
        /// classic or modern vehicle package, and pull the package name out
        /// of the path string. This is the most reliable classic-detection
        /// signal we have because both files are written by the extractor
        /// using the literal ERP path the user picked, no heuristics
        /// involved.
        ///
        /// Returns a ClassicCacheMetadata with IsClassic/IsModern flags;
        /// IsKnown is false when neither metadata file exists OR neither
        /// contains a recognisable package marker (defensive: a cache built
        /// by an older / third-party extractor might not have these files).
        /// </summary>
        private static ClassicCacheMetadata ReadClassicCacheMetadata(string cacheRoot)
        {
            var meta = new ClassicCacheMetadata { CacheRoot = cacheRoot };
            if (string.IsNullOrEmpty(cacheRoot) || !Directory.Exists(cacheRoot))
                return meta;

            // _cache_ok.txt is single-line and the most compact -- prefer it.
            // Format: "<originalErpPath>|<hash>|<pickedIdfFolder>"
            string okPath = Path.Combine(cacheRoot, TyreCacheOkFileName);
            string firstLine = TryReadFirstLine(okPath);
            if (!string.IsNullOrEmpty(firstLine))
            {
                meta.Source = TyreCacheOkFileName;
                meta.OriginalErpPath = firstLine.Split('|')[0];
            }
            else
            {
                // Fallback to extract log -- multi-line, but the first line
                // also includes the original ERP path in the format
                // "[HH:MM:SS] Full-package extract begin: <path>"
                string logPath = Path.Combine(cacheRoot, TyreExtractLogFileName);
                string logFirst = TryReadFirstLine(logPath);
                if (!string.IsNullOrEmpty(logFirst))
                {
                    meta.Source = TyreExtractLogFileName;
                    int colon = logFirst.IndexOf("extract begin:", StringComparison.OrdinalIgnoreCase);
                    if (colon >= 0)
                        meta.OriginalErpPath = logFirst.Substring(colon + "extract begin:".Length).Trim();
                    else
                        meta.OriginalErpPath = logFirst;
                }
            }

            if (string.IsNullOrEmpty(meta.OriginalErpPath))
                return meta;

            // Classify the package by looking for canonical markers in the
            // path string. The two patterns we care about are:
            //
            //   f1_classic_0X_vehicle_package    (classic, X is 1, 2, or 3)
            //   f1_20XX_vehicle_package          (modern, 2017..2026)
            //
            // We also capture the actual package-folder substring so the
            // log records exactly which package was identified.
            string norm = meta.OriginalErpPath.Replace('\\', '/').ToLowerInvariant();
            int classicIdx = norm.IndexOf("f1_classic_", StringComparison.Ordinal);
            if (classicIdx >= 0)
            {
                meta.IsClassic = true;
                meta.PackageName = ExtractPackageFolderName(norm, classicIdx);
            }
            else
            {
                int modernIdx = FindModernPackageIndex(norm);
                if (modernIdx >= 0)
                {
                    meta.IsModern = true;
                    meta.PackageName = ExtractPackageFolderName(norm, modernIdx);
                }
            }

            return meta;
        }

        /// <summary>
        /// Read the first non-empty line of a text file. Returns null on
        /// any exception (missing file, locked, encoding issue) -- this is
        /// best-effort metadata extraction, never fatal.
        /// </summary>
        private static string TryReadFirstLine(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                foreach (var line in File.ReadLines(path))
                {
                    string trimmed = line == null ? null : line.TrimStart('\uFEFF', ' ', '\t');
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        return trimmed;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Given a normalised forward-slash lowercase path that contains
        /// the package marker at <paramref name="markerIdx"/>, return the
        /// package folder name (everything from the marker to the next
        /// "/"). Falls back to the marker token alone if there's no
        /// trailing slash.
        /// </summary>
        private static string ExtractPackageFolderName(string normPath, int markerIdx)
        {
            int slash = normPath.IndexOf('/', markerIdx);
            return slash > markerIdx
                ? normPath.Substring(markerIdx, slash - markerIdx)
                : normPath.Substring(markerIdx);
        }

        /// <summary>
        /// Search the path for "f1_2017_vehicle_package".."f1_2026_vehicle_package".
        /// Returns the position of the leading "f1_" or -1.
        /// </summary>
        private static int FindModernPackageIndex(string normPath)
        {
            for (int year = 2017; year <= 2026; year++)
            {
                string token = "f1_" + year + "_vehicle_package";
                int idx = normPath.IndexOf(token, StringComparison.Ordinal);
                if (idx >= 0) return idx;
            }
            return -1;
        }

        /// <summary>
        /// Scoring record for a candidate tyre folder. Higher is better.
        /// </summary>
        private sealed class TyreFolderCandidate
        {
            public string Folder;
            public int Score;
            public int MeshCount;     // *temp000*mesh* file count, tiebreaker
            public List<string> Reasons = new List<string>();
        }

        /// <summary>
        /// Walk the cache root looking for any folder that could contain a
        /// classic tyre model, scoring each candidate against five signals.
        /// Returns the highest-scoring folder, or null if no candidate
        /// scored above zero.
        ///
        /// All evaluated candidates -- including rejects -- are appended to
        /// <paramref name="diagnostics"/> so the caller can dump them to the
        /// log when the search fails. That diagnostic trail is the whole
        /// reason this method exists as a separate scoring pass rather than
        /// a returns-the-first-match search: when a classic car silently
        /// produces no tyres, "here are the eight folders we considered and
        /// why each was rejected" is a far more actionable log entry than
        /// "tyre folder not found".
        /// </summary>
        private static TyreFolderCandidate FindBestTyreFolder(
            string cacheRoot, List<TyreFolderCandidate> diagnostics)
        {
            if (string.IsNullOrEmpty(cacheRoot) || !Directory.Exists(cacheRoot))
                return null;

            // Enumerate every directory under the cache root. Classic caches
            // are small (few thousand files at most), so an exhaustive walk
            // is cheap. Capping at depth 5 protects against pathological
            // symlink loops without losing real candidates -- the deepest
            // tyre folder we've seen in the wild is 4 levels deep
            // (cacheRoot / shared / tyres / idf).
            List<string> dirs;
            try
            {
                dirs = EnumerateDirectoriesShallow(cacheRoot, maxDepth: 5);
            }
            catch (Exception ex)
            {
                Log($"[CLASSIC-TYRES] EnumerateDirectoriesShallow failed: {ex.Message}");
                return null;
            }

            TyreFolderCandidate best = null;
            foreach (var dir in dirs)
            {
                var cand = ScoreTyreFolderCandidate(dir);
                if (cand.Score <= 0) continue;

                diagnostics.Add(cand);
                if (best == null ||
                    cand.Score > best.Score ||
                    (cand.Score == best.Score && cand.MeshCount > best.MeshCount))
                {
                    best = cand;
                }
            }

            return best;
        }

        /// <summary>
        /// Iterative breadth-first directory walk capped at the given depth
        /// to avoid Directory.EnumerateDirectories(SearchOption.AllDirectories)
        /// throwing on the first unreadable subdirectory (it bubbles up the
        /// whole walk, which we don't want for an opportunistic search).
        /// </summary>
        private static List<string> EnumerateDirectoriesShallow(string root, int maxDepth)
        {
            var result = new List<string> { root };
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((root, 0));

            while (queue.Count > 0)
            {
                var (cur, depth) = queue.Dequeue();
                if (depth >= maxDepth) continue;

                string[] subs;
                try { subs = Directory.GetDirectories(cur); }
                catch { continue; }

                foreach (var s in subs)
                {
                    result.Add(s);
                    queue.Enqueue((s, depth + 1));
                }
            }
            return result;
        }

        /// <summary>
        /// Apply the five tyre-folder signals to a single directory.
        /// Returns a candidate with the score and the human-readable reason
        /// trail (always populated, even on score-zero candidates -- the
        /// caller filters by score).
        /// </summary>
        private static TyreFolderCandidate ScoreTyreFolderCandidate(string folder)
        {
            var cand = new TyreFolderCandidate { Folder = folder };

            string folderName = Path.GetFileName(folder);
            string folderLower = folderName.ToLowerInvariant();
            string parentName = Path.GetFileName(Path.GetDirectoryName(folder) ?? "");
            string parentLower = parentName.ToLowerInvariant();

            // Signal 1: folder named "tyres"/"tires" (or its idf subfolder).
            // Folder name itself is the strongest single signal -- a folder
            // literally called "tyres" almost certainly holds tyres.
            if (folderLower == "tyres" || folderLower == "tires")
            {
                cand.Score += 5;
                cand.Reasons.Add("+5 folder named 'tyres'/'tires'");
            }
            else if ((folderLower == "idf") &&
                     (parentLower == "tyres" || parentLower == "tires"))
            {
                cand.Score += 5;
                cand.Reasons.Add($"+5 idf subfolder of '{parentName}'");
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                cand.Reasons.Add("(folder unreadable)");
                return cand;
            }

            // Pre-compute the mesh-descriptor count once for tiebreaking.
            int meshCount = 0;
            int eraCodedMeshCount = 0;
            int genericTyreMeshCount = 0;
            int vbIbCount = 0;
            int sidewallTreadMaterials = 0;

            foreach (var f in files)
            {
                string nm = Path.GetFileName(f).ToLowerInvariant();
                bool isMeshDescriptor =
                    nm.IndexOf("temp000", StringComparison.Ordinal) >= 0 &&
                    nm.IndexOf("mesh", StringComparison.Ordinal) >= 0;

                if (isMeshDescriptor)
                {
                    meshCount++;
                    // Era-coded prefix: c_07_tyres, c_03_tires, etc. The
                    // two-digit number is the era group, not the year.
                    if (LooksLikeEraCodedTyreFile(nm)) eraCodedMeshCount++;
                    else if (nm.Contains("tyre") || nm.Contains("tire")) genericTyreMeshCount++;
                }

                if (nm.Contains("temp000") &&
                    (nm.Contains("&vb=") || nm.Contains("&ib=")) &&
                    (nm.Contains("tyre") || nm.Contains("tire")))
                {
                    vbIbCount++;
                }
            }

            cand.MeshCount = meshCount;

            // Signal 2: era-coded tyre filenames (strong).
            if (eraCodedMeshCount > 0)
            {
                cand.Score += 5;
                cand.Reasons.Add($"+5 {eraCodedMeshCount} era-coded c_NN_tyres descriptor(s)");
            }

            // Signal 3: generic tyre/tire filename (medium).
            if (genericTyreMeshCount > 0)
            {
                cand.Score += 3;
                cand.Reasons.Add($"+3 {genericTyreMeshCount} generic tyre/tire mesh descriptor(s)");
            }

            // Signal 4: sibling materials/ folder with sidewall/tread.
            // Path layout: <folder>/.../materials/{sidewall,tread,...}
            // The materials folder is usually a sibling of the idf folder
            // ("../materials") but we also handle a peer-of-team layout.
            try
            {
                string parentDir = Path.GetDirectoryName(folder);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    string sibMaterials = Path.Combine(parentDir, "materials");
                    if (Directory.Exists(sibMaterials))
                    {
                        foreach (var mf in Directory.GetFiles(sibMaterials, "*", SearchOption.TopDirectoryOnly))
                        {
                            string mn = Path.GetFileName(mf).ToLowerInvariant();
                            if (mn.Contains("sidewall") || mn.Contains("tread")) sidewallTreadMaterials++;
                        }
                    }
                }
            }
            catch { /* best-effort */ }

            if (sidewallTreadMaterials > 0)
            {
                cand.Score += 3;
                cand.Reasons.Add($"+3 {sidewallTreadMaterials} sibling sidewall/tread material(s)");
            }

            // Signal 5: tyre-named vb/ib companions. Lowest weight because
            // a folder that contains only buffer files but no descriptors
            // can't actually be loaded -- so this signal helps tiebreak
            // but never carries on its own.
            if (vbIbCount > 0)
            {
                cand.Score += 2;
                cand.Reasons.Add($"+2 {vbIbCount} tyre-named vb/ib file(s)");
            }

            // Hard reject: zero mesh descriptors total. Even if signals
            // 1/3/4 fire on a folder, we can't load it without descriptors.
            if (meshCount == 0)
            {
                cand.Score = 0;
                cand.Reasons.Add("REJECT: 0 *temp000*mesh* descriptors in folder");
            }

            return cand;
        }

        /// <summary>
        /// Does this filename match the "c_NN_tyres" era-coded prefix?
        /// We require a literal underscore-digit-digit-underscore-tyres
        /// sequence at the start so a stray "_tyres_" elsewhere in a file
        /// name doesn't false-positive.
        /// </summary>
        private static bool LooksLikeEraCodedTyreFile(string nameLower)
        {
            // Minimum form: "c_NN_tyres" plus separator -> 11 chars.
            if (nameLower.Length < 11) return false;
            if (nameLower[0] != 'c' || nameLower[1] != '_') return false;
            if (!char.IsDigit(nameLower[2]) || !char.IsDigit(nameLower[3])) return false;
            if (nameLower[4] != '_') return false;
            // Accept "tyres" or "tires" after the era code.
            return nameLower.IndexOf("tyres", 5, StringComparison.Ordinal) == 5 ||
                   nameLower.IndexOf("tires", 5, StringComparison.Ordinal) == 5;
        }

        /// <summary>
        /// Confirm yes/no with the user via a WPF MessageBox, marshalled
        /// onto the UI thread when the loader is running on a background
        /// thread. Returns false (don't load tyres) on any error so a
        /// dispatcher / app shutdown failure can never block the load.
        /// </summary>
        // v12.13 (beta): set to true to skip classic-tyre loading
        // entirely. Returns false from ConfirmLoadClassicTyres
        // unconditionally, which causes the caller in TryLoadClassic
        // Tyres to log "User declined; skipping tyre load." and return
        // without parsing any tyre descriptors. Flip back to false to
        // fall through to the v12.10 prompt-bypass / interactive-prompt
        // logic below.
        //
        // Layered ABOVE _bypassClassicTyrePrompt so the previous v12.10
        // popup-suppression behaviour is preserved verbatim: when this
        // flag is false and _bypassClassicTyrePrompt is true the
        // function still auto-accepts and tyres still auto-load. The
        // interactive MessageBox prompt is only ever reached when BOTH
        // flags are false.
        private static readonly bool _skipClassicTyreLoad = true;

        // BETA TESTING: set to true to skip the "Would you like to load
        // tyres?" MessageBox for classic cars and always auto-accept
        // (tyres will load every time). Flip back to false to restore
        // the original interactive prompt. The MessageBox.Show code
        // below is intentionally preserved so re-enabling is a one-line
        // change.
        private static readonly bool _bypassClassicTyrePrompt = true;

        private static bool ConfirmLoadClassicTyres()
        {
            // v12.13: short-circuit BEFORE the v12.10 bypass so tyres
            // never load for classic cars while the flag is on. Layered
            // (not in place of) so flipping _skipClassicTyreLoad=false
            // restores the previous auto-load-on-bypass behaviour
            // without touching any other code.
            if (_skipClassicTyreLoad)
                return false;

            if (_bypassClassicTyrePrompt)
                return true;

            const string text = "Classic car detected.\r\n\r\n" +
                                "Would you like to load its tyres?";
            const string caption = "F1 Modding 3D Tool";

            try
            {
                var app = System.Windows.Application.Current;
                if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
                {
                    return app.Dispatcher.Invoke(() =>
                        MessageBox.Show(text, caption,
                                        MessageBoxButton.YesNo,
                                        MessageBoxImage.Question)
                            == MessageBoxResult.Yes);
                }

                return MessageBox.Show(text, caption,
                                       MessageBoxButton.YesNo,
                                       MessageBoxImage.Question)
                       == MessageBoxResult.Yes;
            }
            catch (Exception ex)
            {
                Log($"ConfirmLoadClassicTyres: dialog failed ({ex.GetType().Name}: " +
                    $"{ex.Message}). Treating as 'No' so the load can complete.");
                return false;
            }
        }

        /// <summary>
        /// Locate Tyres.erp next to the source ERP's package and extract it
        /// on demand. Returns the cache root the extractor populated, or
        /// null when:
        ///
        ///   - the original ERP path can't be resolved to a package root
        ///   - Tyres.erp doesn't exist where we expect it
        ///   - the extractor itself failed (already logged inside the call)
        ///
        /// Per design: re-extracts on every classic load. We force this by
        /// deleting the cache directory (computed via
        /// ErpIdfExtractor.BuildCacheRoot, which is the same path the
        /// extractor will produce) before invoking the extractor. The
        /// extractor's internal TryReuseCache check then sees an empty
        /// directory and does fresh work.
        ///
        /// Path discovery: walks up from <paramref name="originalErpPath"/>
        /// until it finds a directory that looks like a vehicle-package
        /// root (contains "shared/tyres/Tyres.erp" or any case variant).
        /// The walk caps at 8 hops to handle deep installs without going
        /// off into the filesystem. For F1 2018 classic_02 the layout is:
        ///
        ///   F1 2018/asset_groups/f1_classic_02_vehicle_package/
        ///       teams/c_mclaren_08/wep/c_mclaren_08.erp     (originalErpPath, 3 hops up to package)
        ///       shared/tyres/Tyres.erp                      (sibling we want)
        ///
        /// We tolerate "Tyres.erp" / "tyres.erp" / "Tires.erp" because the
        /// install's actual filename casing varies.
        /// </summary>
        private static string ExtractSiblingTyresErpForClassic(string originalErpPath)
        {
            if (string.IsNullOrEmpty(originalErpPath))
                return null;

            string tyresErpPath = LocateSiblingTyresErp(originalErpPath);
            if (string.IsNullOrEmpty(tyresErpPath))
            {
                Log("[CLASSIC-TYRES] No sibling Tyres.erp found in the install. " +
                    "(Looked under <packageRoot>/shared/tyres/ for Tyres.erp / " +
                    "tyres.erp / Tires.erp.) Continuing with per-car cache only.");
                return null;
            }

            Log($"[CLASSIC-TYRES] Found sibling Tyres.erp: {tyresErpPath}");

            // Compute the cache root the extractor will populate. We use
            // BuildCacheRoot directly so we know exactly where to wipe and
            // exactly where to point our tyre-folder search afterwards.
            // Two callsites compute this same path; keeping both routed
            // through ErpIdfExtractor.BuildCacheRoot guarantees they stay
            // in sync if the extractor's path scheme ever changes.
            string tyresCacheRoot;
            try
            {
                tyresCacheRoot = ErpIdfExtractor.BuildCacheRoot(tyresErpPath);
            }
            catch (Exception ex)
            {
                Log($"[CLASSIC-TYRES] BuildCacheRoot failed: {ex.Message}");
                return null;
            }

            // Force re-extraction every load, per spec. The extractor's own
            // cache-reuse check (TryReuseCache) returns null when _cache_ok
            // is missing, so wiping the directory guarantees fresh work.
            //
            // We catch and log any IO error here rather than aborting --
            // a Directory.Delete failure (sharing violation, antivirus) is
            // not fatal, just means the extractor reuses whatever's there.
            // The extractor itself will overwrite the per-resource files
            // on a fresh extract regardless of whether the directory was
            // wiped first.
            try
            {
                if (Directory.Exists(tyresCacheRoot))
                {
                    Log($"[CLASSIC-TYRES] Wiping existing Tyres.erp cache for fresh " +
                        $"extract: {tyresCacheRoot}");
                    Directory.Delete(tyresCacheRoot, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Log($"[CLASSIC-TYRES] Could not wipe Tyres.erp cache " +
                    $"({ex.GetType().Name}: {ex.Message}). Continuing -- the extractor " +
                    "will overwrite stale files but may not delete obsolete ones.");
            }

            Log($"[CLASSIC-TYRES] Invoking ErpIdfExtractor.ExtractEmbeddedIdfToTempFolder " +
                $"on Tyres.erp...");
            string tyresIdfReturn;
            try
            {
                // The extractor returns the IDF folder of the FIRST team
                // it sees in the ERP. Tyres.erp doesn't have team folders
                // (it's a shared asset), so the return value is generally
                // not useful for our purposes -- it'll be either null or
                // some path we don't care about. We use BuildCacheRoot to
                // find the actual cache root regardless.
                tyresIdfReturn = ErpIdfExtractor.ExtractEmbeddedIdfToTempFolder(tyresErpPath);
            }
            catch (Exception ex)
            {
                Log($"[CLASSIC-TYRES] Extractor threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }

            Log($"[CLASSIC-TYRES] Extractor returned: '{tyresIdfReturn ?? "(null)"}'");

            if (!Directory.Exists(tyresCacheRoot))
            {
                Log($"[CLASSIC-TYRES] Expected Tyres.erp cache root '{tyresCacheRoot}' " +
                    "does not exist after extraction. The extractor probably failed " +
                    "(its own log at " + Path.Combine(tyresCacheRoot, "_extract_log.txt") +
                    " may have details, but the directory itself isn't there). Aborting " +
                    "tyre load.");
                return null;
            }

            Log($"[CLASSIC-TYRES] Tyres.erp extracted to: {tyresCacheRoot}");
            return tyresCacheRoot;
        }

        /// <summary>
        /// Walk up from the original ERP path looking for the package root
        /// (the directory containing shared/tyres/&lt;TyresErpFile&gt;). Returns
        /// the full path to Tyres.erp on success, or null if no candidate
        /// is found within 8 hops. The 8-hop cap is generous -- the
        /// expected layout is ~3 hops up, but we tolerate users who pick
        /// an ERP from a deeper subdirectory or from a non-standard
        /// install layout.
        /// </summary>
        private static string LocateSiblingTyresErp(string originalErpPath)
        {
            string cur;
            try
            {
                cur = Path.GetDirectoryName(Path.GetFullPath(originalErpPath));
            }
            catch
            {
                return null;
            }

            // Filenames we accept, in priority order. F1 2018's vanilla
            // install uses "Tyres.erp" with that exact casing on most
            // file systems, but we tolerate variants because:
            //   - case-insensitive Windows file systems may report a
            //     different casing depending on how the file was created;
            //   - "Tires" (American spelling) shows up in some F1 game
            //     mods even though Codemasters uses "Tyres";
            //   - Linux/proton installs are case-sensitive, so we have to
            //     try multiple specific names rather than rely on Windows
            //     case-folding.
            string[] candidateNames =
            {
                "Tyres.erp", "tyres.erp", "TYRES.ERP",
                "Tires.erp", "tires.erp", "TIRES.ERP",
            };

            for (int hop = 0; hop < 8 && !string.IsNullOrEmpty(cur); hop++)
            {
                // The expected sibling path is <cur>/shared/tyres/Tyres.erp.
                // Some installs nest one extra level (a vehicle_package
                // subfolder), which is why we walk up rather than testing
                // a single relative path -- the loop also catches the case
                // where the user picked the ERP from inside teams/<team>/
                // (3 hops up) AND from inside the package root itself (0
                // hops up, useful when extractor caches are reused).
                string sharedTyres = Path.Combine(cur, "shared", "tyres");
                if (Directory.Exists(sharedTyres))
                {
                    foreach (var name in candidateNames)
                    {
                        string full = Path.Combine(sharedTyres, name);
                        if (File.Exists(full))
                            return full;
                    }
                    // Fallback: any *.erp in shared/tyres/ that has "tyres"
                    // or "tires" in its filename (case-insensitive). This
                    // catches mod installs that renamed the file.
                    try
                    {
                        foreach (var f in Directory.GetFiles(sharedTyres, "*.erp",
                                                             SearchOption.TopDirectoryOnly))
                        {
                            string nm = Path.GetFileName(f).ToLowerInvariant();
                            if (nm.Contains("tyre") || nm.Contains("tire"))
                                return f;
                        }
                    }
                    catch { /* unreadable folder, try parent */ }
                }

                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) ||
                    string.Equals(parent, cur, StringComparison.Ordinal))
                    break;
                cur = parent;
            }

            return null;
        }

        /// <summary>
        /// Inspect the per-car cache's already-extracted body assets to
        /// determine which classic-tyre era code (c_01..c_99) the car uses.
        ///
        /// Why we need this: F1 2018's Tyres.erp contains tyre geometry
        /// for multiple eras (c_03, c_05, c_06, c_07, ...). Each era is
        /// a distinct chassis-width / sidewall-profile / tread-pattern
        /// the game ships, and a given car uses exactly one of them. The
        /// "c_0X" prefix is NOT a year -- it's an era group key.
        ///
        /// The most reliable per-car signal is the body cache's own
        /// sidewall texture filenames: the McLaren 98 cache holds
        /// shared/tyres/textures/sidewall/c_03_sidewall_n.tif etc.,
        /// which uniquely identifies era 03 for that car. We scan that
        /// folder first; if it's missing or empty, we fall back to
        /// scanning the whole shared/tyres/ subtree for c_NN-prefixed
        /// filenames. The dominant prefix (the one with the most file
        /// hits) wins; ties go to the lowest-numbered era because that's
        /// the canonical / non-variant naming Codemasters uses.
        ///
        /// Returns the era code as a 4-char lowercase string suitable
        /// for filename matching ("c_03", "c_07", ...) or null when no
        /// usable signal could be extracted from the cache. Caller is
        /// expected to log diagnostically and skip the tyre load when
        /// null is returned -- loading the wrong era would put visibly
        /// incorrect rubber on the car, which is worse than no rubber
        /// at all.
        /// </summary>
        /// <param name="perCarCacheRoot">
        /// The cache root for the car body, e.g.
        /// "...\erp_cache\c_mclaren_98_E29CDD83870D9AF2". Must be the
        /// per-car cache, NOT the freshly-extracted Tyres.erp cache --
        /// the era hint lives in the BODY's textures because that's
        /// what the game baked in for that specific car.
        /// </param>
        private static string DetectTyreEraFromBodyCache(string perCarCacheRoot)
        {
            if (string.IsNullOrEmpty(perCarCacheRoot) || !Directory.Exists(perCarCacheRoot))
            {
                Log("[CLASSIC-TYRES] DetectTyreEraFromBodyCache: no body cache root supplied.");
                return null;
            }

            // Priority 0 (highest): the BUNDLED tyre-IDF folder. The game
            // ships tyre IDFs in <body>/shared/tyres/idf as
            // c_NN_tyres!!!temp000.* / c_NN_tyres.idf%3F* etc. These are
            // the tyre geometry the game's own loader uses for that
            // specific car -- the most authoritative signal there is,
            // because it's the file the game itself shipped pre-bound
            // to that vehicle's package.
            //
            // The reason this beats the sidewall-texture signal: a car's
            // shared/tyres/textures/sidewall folder is a UNION of every
            // sidewall texture set that any context in the package
            // might bind for that car. For most cars there's only one,
            // and the texture-prefix detector lands on the right answer
            // by accident. For cars that the game pre-bound to a
            // non-default era (e.g. Ferrari 79 -> 1979 -> a 70s tyre
            // shape), the texture folder ends up containing BOTH the
            // bound era AND the c_03 default, producing a tie that the
            // alphabetic tiebreaker resolves in c_03's favour and
            // wrongly puts modern slicks on the historical car.
            //
            // The bundled tyre-IDF folder doesn't have that problem
            // because the game only ships the IDF(s) it actually uses
            // for the car. If we see c_06_tyres there, c_06 is the era.
            // If we see c_07_tyres, c_07. If multiple eras are present
            // (rare; happens when a car has both wet and dry compound
            // sets), the count gives us the dominant one.
            //
            // We still log the texture-based and material-based
            // fallback signals further down for diagnostic purposes,
            // but as soon as the bundled-IDF folder gives us anything,
            // that's the answer.
            var counts0 = new Dictionary<string, int>(StringComparer.Ordinal);
            string bundledTyresIdf = Path.Combine(
                perCarCacheRoot, "shared", "tyres", "idf");
            int bundledHits = AccumulateEraPrefixes(bundledTyresIdf, counts0);

            if (bundledHits > 0)
            {
                string winner = PickDominantEra(counts0);
                Log($"[CLASSIC-TYRES] Era detected from bundled tyre-IDF folder: " +
                    $"'{winner}' ({bundledHits} file hit(s) across " +
                    $"{counts0.Count} candidate era(s)). " +
                    "[Authoritative: this is the era the game pre-bound to " +
                    "this car's package.]");
                LogEraTally(counts0);
                return winner;
            }
            Log("[CLASSIC-TYRES] No bundled tyre-IDF era hits in " +
                $"'{bundledTyresIdf}'; falling back to texture-based signals.");

            // Priority 1: the sidewall folder. Most specific signal -- a
            // car has one sidewall texture set, and its prefix unambiguously
            // names the era.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            string sidewallFolder = Path.Combine(
                perCarCacheRoot, "shared", "tyres", "textures", "sidewall");
            int sidewallHits = AccumulateEraPrefixes(sidewallFolder, counts);

            if (sidewallHits > 0)
            {
                string winner = PickDominantEra(counts);
                Log($"[CLASSIC-TYRES] Era detected from sidewall folder: '{winner}' " +
                    $"({sidewallHits} file hit(s) across {counts.Count} candidate era(s)).");
                LogEraTally(counts);
                return winner;
            }

            // Priority 2: the wider tyres/textures tree (marbles, tread, etc).
            // Some cars may not have a sidewall folder but still carry tyre
            // material/texture references with the era prefix elsewhere.
            counts.Clear();
            string widerTyresTextures = Path.Combine(
                perCarCacheRoot, "shared", "tyres", "textures");
            int widerHits = AccumulateEraPrefixes(widerTyresTextures, counts, recursive: true);

            if (widerHits > 0)
            {
                string winner = PickDominantEra(counts);
                Log($"[CLASSIC-TYRES] Era detected from wider tyres/textures tree: " +
                    $"'{winner}' ({widerHits} file hit(s) across {counts.Count} " +
                    "candidate era(s)).");
                LogEraTally(counts);
                return winner;
            }

            // Priority 3: tyres/materials. A few cars carry the era hint
            // only in the .material filenames. Lowest priority because
            // material names sometimes carry secondary suffixes that
            // could confuse the prefix match.
            counts.Clear();
            string tyresMaterials = Path.Combine(
                perCarCacheRoot, "shared", "tyres", "materials");
            int matHits = AccumulateEraPrefixes(tyresMaterials, counts, recursive: true);

            if (matHits > 0)
            {
                string winner = PickDominantEra(counts);
                Log($"[CLASSIC-TYRES] Era detected from tyres/materials: '{winner}' " +
                    $"({matHits} file hit(s) across {counts.Count} candidate era(s)).");
                LogEraTally(counts);
                return winner;
            }

            Log("[CLASSIC-TYRES] DetectTyreEraFromBodyCache: no c_NN prefixes found in " +
                $"sidewall, wider textures, or materials under '{perCarCacheRoot}'.");
            return null;
        }

        /// <summary>
        /// Walk all files in <paramref name="folder"/> and bump
        /// <paramref name="counts"/> for every "c_NN" prefix found.
        /// Returns the total number of file-level hits (one per file
        /// matched, not per era detected). Returns 0 silently if the
        /// folder doesn't exist -- caller decides whether absence is an
        /// error.
        /// </summary>
        private static int AccumulateEraPrefixes(
            string folder, Dictionary<string, int> counts, bool recursive = false)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return 0;

            int total = 0;
            try
            {
                var opt = recursive
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;

                foreach (var f in Directory.GetFiles(folder, "*", opt))
                {
                    string nm = Path.GetFileName(f).ToLowerInvariant();
                    string era = TryParseEraPrefix(nm);
                    if (era == null) continue;

                    if (counts.TryGetValue(era, out int n))
                        counts[era] = n + 1;
                    else
                        counts[era] = 1;
                    total++;
                }
            }
            catch { /* best-effort */ }
            return total;
        }

        /// <summary>
        /// Pull the era prefix from a lowercase filename, or null if it
        /// doesn't start with "c_NN_" where NN is two digits. The hard
        /// shape requirement keeps things like "c_4d_normal.tif" from
        /// being mistaken for an era prefix.
        /// </summary>
        private static string TryParseEraPrefix(string nameLower)
        {
            if (nameLower == null || nameLower.Length < 5) return null;
            if (nameLower[0] != 'c' || nameLower[1] != '_') return null;
            if (!char.IsDigit(nameLower[2]) || !char.IsDigit(nameLower[3])) return null;
            if (nameLower[4] != '_') return null;
            return nameLower.Substring(0, 4); // "c_03"
        }

        /// <summary>
        /// Choose the dominant era from a tally. Highest count wins;
        /// ties broken by lowest era number (the canonical Codemasters
        /// naming -- earlier numbers tend to be base entries, higher
        /// numbers tend to be variants).
        /// </summary>
        private static string PickDominantEra(Dictionary<string, int> counts)
        {
            string best = null;
            int bestCount = -1;
            foreach (var kv in counts)
            {
                if (kv.Value > bestCount ||
                    (kv.Value == bestCount &&
                     string.CompareOrdinal(kv.Key, best ?? "z_99") < 0))
                {
                    best = kv.Key;
                    bestCount = kv.Value;
                }
            }
            return best;
        }

        /// <summary>
        /// Log the full era-vote tally for diagnostics. Sorted by count
        /// descending, then era code ascending. Useful when a car has
        /// mixed-era files and we want to be transparent about why a
        /// specific era won.
        /// </summary>
        private static void LogEraTally(Dictionary<string, int> counts)
        {
            var ordered = new List<KeyValuePair<string, int>>(counts);
            ordered.Sort((a, b) =>
            {
                int byCount = b.Value.CompareTo(a.Value);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });
            foreach (var kv in ordered)
                Log($"[CLASSIC-TYRES]   tally: {kv.Key} -> {kv.Value} hit(s)");
        }

        /// <summary>
        /// True for any tyre descriptor filename whose stem contains
        /// "_wet" -- e.g. "c_07_tyres_wet!!!temp000.idf%3Flod=0&mesh".
        /// Per design these are skipped; only dry tyres get loaded.
        /// </summary>
        private static bool IsWetTyreDescriptor(string descriptorPath)
        {
            if (string.IsNullOrEmpty(descriptorPath)) return false;
            string nm = Path.GetFileName(descriptorPath).ToLowerInvariant();
            return nm.Contains("_wet");
        }

        /// <summary>
        /// Map a tyre descriptor's submesh slot label to a material-kind
        /// keyword that BuildMaterialEaidForSlot uses to construct the
        /// material EAID. Returns "sidewall" or "tread", or null when
        /// the slot isn't recognised as either.
        ///
        /// Why slot labels need this translation: the descriptor's first
        /// two slots are named "wall" and "tread", and the parser
        /// auto-names the remaining 6 slots "mesh_2".."mesh_7" because
        /// they have no embedded label. The 8 slots correspond to
        /// 4 sidewalls + 4 treads (one of each per wheel), so the
        /// even-indexed mesh_N entries are walls and odd-indexed are
        /// treads. Confirmed against the c_03_tyres descriptor for
        /// c_mclaren_08:
        ///
        ///   slot       VB  -> kind
        ///   wall       0      sidewall   (front-left)
        ///   tread      1      tread      (front-left)
        ///   mesh_2     0      sidewall   (front-right)
        ///   mesh_3     1      tread      (front-right)
        ///   mesh_4     0      sidewall   (rear-left)
        ///   mesh_5     1      tread      (rear-left)
        ///   mesh_6     0      sidewall   (rear-right)
        ///   mesh_7     1      tread      (rear-right)
        ///
        /// VB=0 holds sidewall geometry, VB=1 holds tread geometry; the
        /// VB index alone could classify them, but slot-name routing is
        /// more robust -- if the descriptor format ever changes layout,
        /// the parser still emits "wall"/"tread" for the first pair, so
        /// the explicit string match is the durable signal.
        /// </summary>
        private static string ClassifyTyreSlot(string slotLower)
        {
            if (string.IsNullOrEmpty(slotLower)) return null;

            // Direct slot names emitted by the descriptor's label table
            // for the first wheel.
            if (slotLower == "wall" || slotLower == "sidewall") return "sidewall";
            if (slotLower == "tread") return "tread";

            // Auto-generated names for the remaining 6 slots ("mesh_2"
            // through "mesh_7"). Even index -> sidewall, odd -> tread.
            // Defensive: bail on parse failure rather than guess.
            if (slotLower.StartsWith("mesh_", StringComparison.Ordinal) &&
                slotLower.Length > 5 &&
                int.TryParse(slotLower.Substring(5), out int n))
            {
                return (n % 2 == 0) ? "sidewall" : "tread";
            }
            return null;
        }

        /// <summary>
        /// Top-level entry point for the tyre-load pass. Called once from
        /// LoadFolder after the body is fully built. Responsibilities:
        ///
        ///   1. Find the cache root (walk up from <paramref name="resolvedFolder"/>).
        ///   2. Read cache metadata to confirm classic-vs-modern. Modern
        ///      packages exit immediately (no prompt, no work).
        ///   3. For classics, also extract a sibling Tyres.erp on demand
        ///      so cars whose tyres aren't bundled in the per-car ERP
        ///      (e.g. McLaren 08 in F1 2018) can still get tyres.
        ///   4. Run the multi-strategy folder search across the per-car
        ///      cache AND the freshly-extracted Tyres.erp cache, picking
        ///      the highest-scoring candidate.
        ///   5. Detect the car's era by reading its already-extracted
        ///      sidewall textures. Filter the tyre descriptors so only
        ///      that era's files survive, and skip wet variants.
        ///   6. Show the Yes/No prompt only if at least one descriptor
        ///      remains after filtering.
        ///   7. Load each lod=0 mesh descriptor from that folder via the
        ///      existing LoadLegacyMeshDescriptor pipeline (with the
        ///      _inTyreLoadPass flag set so the body's slot allowlist
        ///      doesn't filter out tyre slot labels); append children
        ///      to <paramref name="group"/>.
        ///
        /// Any failure at any stage is logged and returns gracefully, never
        /// throwing -- the caller has already finished a successful body
        /// load and we don't want a tyre hiccup to invalidate that.
        /// </summary>
        private static void TryLoadClassicTyres(
            string resolvedFolder, string originalFolder, Model3DGroup group)
        {
            Log("--------------------------------------------------");
            Log("[CLASSIC-TYRES] Starting tyre-load pass.");
            Log($"[CLASSIC-TYRES]   resolvedFolder = '{resolvedFolder}'");
            Log($"[CLASSIC-TYRES]   originalFolder = '{originalFolder}'");

            string cacheRoot = FindClassicCacheRoot(resolvedFolder)
                            ?? FindClassicCacheRoot(originalFolder);
            if (cacheRoot == null)
            {
                Log("[CLASSIC-TYRES] No cache root found within 6 hops of either folder. " +
                    "Skipping tyre-load pass.");
                return;
            }
            Log($"[CLASSIC-TYRES] Cache root: {cacheRoot}");

            // Metadata gate. If we can read the cache metadata and it tells
            // us this is a modern package, exit silently -- modern tyres
            // aren't bundled in the per-car ERP. If it tells us this is
            // classic, log it confidently. If we can't determine either way
            // (older / third-party extractor), proceed cautiously: the
            // folder-search step will still find tyres if they exist.
            ClassicCacheMetadata meta = ReadClassicCacheMetadata(cacheRoot);
            if (meta.IsKnown)
            {
                Log($"[CLASSIC-TYRES] Metadata source: {meta.Source}");
                Log($"[CLASSIC-TYRES] Original ERP: {meta.OriginalErpPath}");
                Log($"[CLASSIC-TYRES] Package: {meta.PackageName ?? "(unknown)"} -- " +
                    $"IsClassic={meta.IsClassic}, IsModern={meta.IsModern}");

                if (meta.IsModern)
                {
                    Log("[CLASSIC-TYRES] Source ERP is a MODERN package; modern tyres are " +
                        "shared via cross-package eaid references and aren't bundled per-car. " +
                        "Skipping tyre-load pass and not prompting the user.");
                    return;
                }
            }
            else
            {
                Log("[CLASSIC-TYRES] No cache metadata files found (_cache_ok.txt / " +
                    "_extract_log.txt). Proceeding without metadata confirmation -- the " +
                    "folder-search heuristics below will still locate tyres if any are " +
                    "bundled.");
            }

            // -------------------------------------------------------------
            // Sibling-Tyres.erp extraction step.
            //
            // Some classic ERPs bundle their tyres directly (the McLaren 98
            // case -- shared/tyres/idf appears in the per-car cache). Others
            // don't bundle tyres at all and instead reference a sibling
            // Tyres.erp that lives at <packageRoot>/shared/tyres/Tyres.erp
            // in the F1 2018 install (the McLaren 08 case).
            //
            // The original extractor (ErpIdfExtractor.MergeSiblingErps) only
            // pulls materials and textures from sibling ERPs, never IDFs --
            // its comment explicitly says "We DO NOT pull IDFs from sibling
            // ERPs". That policy is correct for body geometry (we don't
            // want one team's chassis showing up in another's preview) but
            // it means tyre meshes from Tyres.erp never make it into the
            // per-car cache.
            //
            // The fix here: when metadata says we're a classic, look for
            // Tyres.erp next to the source package and extract it on demand
            // into its OWN cache directory. The freshly-populated cache is
            // then passed as a SECOND root to the existing tyre-folder
            // discovery logic. The car's own cache is still consulted too,
            // so cars that bundle their tyres (McLaren 98) keep working
            // exactly as before.
            //
            // Per spec: re-extract on every classic load. We achieve that
            // by deleting the Tyres.erp cache directory (if present) before
            // calling ExtractEmbeddedIdfToTempFolder -- the extractor's
            // own TryReuseCache check sees an empty cache and does fresh
            // work. Slow path is intentional.
            string tyresErpCacheRoot = null;
            if (meta.IsClassic && !string.IsNullOrEmpty(meta.OriginalErpPath))
            {
                try
                {
                    tyresErpCacheRoot = ExtractSiblingTyresErpForClassic(meta.OriginalErpPath);
                }
                catch (Exception ex)
                {
                    Log($"[CLASSIC-TYRES] EXCEPTION while extracting sibling Tyres.erp " +
                        $"({ex.GetType().Name}: {ex.Message}). Continuing with per-car " +
                        "cache only -- if the car's tyres are bundled we'll still find them.");
                }
            }

            var diagnostics = new List<TyreFolderCandidate>();

            // Search the per-car cache first (covers McLaren 98 / bundled-tyres pattern).
            TyreFolderCandidate best = FindBestTyreFolder(cacheRoot, diagnostics);

            // Then search the freshly-extracted Tyres.erp cache, if any
            // (covers McLaren 08 / external-Tyres.erp pattern). Whichever
            // produces a higher score wins; if both miss, we exit cleanly.
            if (!string.IsNullOrEmpty(tyresErpCacheRoot) && Directory.Exists(tyresErpCacheRoot))
            {
                Log($"[CLASSIC-TYRES] Also searching freshly-extracted Tyres.erp cache: " +
                    $"{tyresErpCacheRoot}");
                TyreFolderCandidate fromTyresErp = FindBestTyreFolder(tyresErpCacheRoot, diagnostics);
                if (fromTyresErp != null)
                {
                    if (best == null ||
                        fromTyresErp.Score > best.Score ||
                        (fromTyresErp.Score == best.Score && fromTyresErp.MeshCount > best.MeshCount))
                    {
                        best = fromTyresErp;
                    }
                }
            }


            // Always log every candidate considered, even on success, so a
            // future regression where we pick the wrong folder is debuggable
            // from one log file. Trim if the search blew up to dozens.
            int shown = 0;
            foreach (var cand in diagnostics.OrderByDescending(c => c.Score)
                                            .ThenByDescending(c => c.MeshCount))
            {
                if (shown >= 10)
                {
                    Log($"[CLASSIC-TYRES]   ... and {diagnostics.Count - shown} " +
                        "more lower-scoring candidate(s) suppressed.");
                    break;
                }
                Log($"[CLASSIC-TYRES] candidate score={cand.Score} meshes={cand.MeshCount} " +
                    $"folder='{cand.Folder}'");
                foreach (var r in cand.Reasons)
                    Log($"[CLASSIC-TYRES]     {r}");
                shown++;
            }

            if (best == null)
            {
                if (meta.IsClassic)
                {
                    Log("[CLASSIC-TYRES] METADATA says classic but NO tyre folder candidates " +
                        "scored above zero. This classic ERP may not bundle its own tyres " +
                        "(some classic packages reference a sibling tyre ERP that isn't in " +
                        "this cache). Skipping the prompt.");
                }
                else
                {
                    Log("[CLASSIC-TYRES] No tyre folder candidates scored above zero. " +
                        "Skipping tyre-load pass.");
                }
                return;
            }

            Log($"[CLASSIC-TYRES] WINNER: '{best.Folder}' (score={best.Score}, " +
                $"meshes={best.MeshCount})");

            // Pick the lod=0 mesh descriptors from the winner. If a folder
            // has no lod=0 descriptors, fall back to the full set so we at
            // least try lod=all -- some classics omit the explicit lod=0.
            string[] tyreMeshFiles;
            try
            {
                tyreMeshFiles = Directory.GetFiles(best.Folder, "*temp000*mesh*",
                                                   SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Log($"[CLASSIC-TYRES] Could not enumerate winner '{best.Folder}': {ex.Message}");
                return;
            }

            if (tyreMeshFiles.Length == 0)
            {
                Log("[CLASSIC-TYRES] Winner folder has no descriptors after re-scan " +
                    "(filesystem race?). Aborting tyre-load.");
                return;
            }

            StringComparison cmp = StringComparison.OrdinalIgnoreCase;
            var lod0 = tyreMeshFiles.Where(f => f.IndexOf("lod=0", cmp) >= 0).ToArray();
            string[] tyresAfterLodFilter = lod0.Length > 0 ? lod0 : tyreMeshFiles;

            Log($"[CLASSIC-TYRES] Discovered {tyreMeshFiles.Length} tyre mesh descriptor(s); " +
                $"{tyresAfterLodFilter.Length} survive lod=0 filter.");

            // Era detection. Per design, the era ("c_03", "c_07", ...) is
            // an arbitrary group key, NOT a year. The body cache itself
            // tells us the right answer because the game's per-car
            // sidewall textures are baked with the correct era prefix.
            // Reading the body cache's shared/tyres/textures/sidewall
            // folder and counting c_NN_* file prefixes gives us a
            // deterministic match. cacheRoot here is the per-car cache
            // (e.g. ...\c_mclaren_98_E29CDD83870D9AF2), which is also
            // where the body load just finished writing files.
            string detectedEra = DetectTyreEraFromBodyCache(cacheRoot);
            if (string.IsNullOrEmpty(detectedEra))
            {
                Log("[CLASSIC-TYRES] Could not detect tyre era from the body cache. " +
                    "Skipping tyre-load: loading the wrong era is worse than no tyres. " +
                    "(If this car SHOULD have tyres, check the body extract for a " +
                    "shared/tyres/textures/sidewall folder with c_NN_*.tif files; if " +
                    "it's missing, the body extract may be incomplete.)");
                return;
            }

            // Filter: keep only descriptors that match the detected era,
            // and drop wet variants (per design, dry tyres only). The
            // era prefix check is a startswith match because tyre
            // descriptors all begin with their era code (c_07_tyres,
            // c_07_tyres_wet, etc).
            string eraPrefix = detectedEra + "_"; // "c_03_"
            var afterEraAndWet = tyresAfterLodFilter
                .Where(f =>
                {
                    string nm = Path.GetFileName(f).ToLowerInvariant();
                    if (!nm.StartsWith(eraPrefix, StringComparison.Ordinal))
                        return false;
                    if (IsWetTyreDescriptor(f))
                        return false;
                    return true;
                })
                .ToArray();

            int droppedByEra = tyresAfterLodFilter.Length - afterEraAndWet.Length -
                tyresAfterLodFilter.Count(f => IsWetTyreDescriptor(f) &&
                    Path.GetFileName(f).ToLowerInvariant().StartsWith(eraPrefix, StringComparison.Ordinal));
            int droppedByWet = tyresAfterLodFilter.Count(f => IsWetTyreDescriptor(f));

            Log($"[CLASSIC-TYRES] Filter: era='{detectedEra}', drop wet variants. " +
                $"Result: {afterEraAndWet.Length} descriptor(s) survive " +
                $"(dropped ~{droppedByEra} non-matching era, ~{droppedByWet} wet).");

            string[] tyresToLoad = afterEraAndWet;
            Array.Sort(tyresToLoad, StringComparer.OrdinalIgnoreCase);

            if (tyresToLoad.Length == 0)
            {
                Log($"[CLASSIC-TYRES] After era + wet filter, 0 tyre descriptors remain. " +
                    $"This usually means Tyres.erp doesn't ship the '{detectedEra}' era " +
                    "(e.g. the car uses a custom era code). Dumping all surviving filenames " +
                    "for diagnosis:");
                foreach (var f in tyresAfterLodFilter)
                    Log($"[CLASSIC-TYRES]   discovered: {Path.GetFileName(f)}");
                return;
            }

            // Prompt before doing the actual load. The body is already on
            // screen so the user can see what they're adding tyres to.
            bool consent = ConfirmLoadClassicTyres();
            Log($"[CLASSIC-TYRES] User consent: {(consent ? "YES" : "NO")}");
            if (!consent)
            {
                Log("[CLASSIC-TYRES] User declined; skipping tyre load.");
                return;
            }

            int tyrePartsAdded = 0;
            foreach (var meshFile in tyresToLoad)
            {
                Log("--------------------------------------------------");
                Log($"[CLASSIC-TYRES] Loading tyre mesh descriptor: {meshFile}");

                _currentDescriptorPath = meshFile;
                _currentDescriptorPartName = ExtractPartNameFromDescriptorFilename(meshFile);

                if (string.IsNullOrEmpty(_currentDescriptorPartName))
                    Log("[CLASSIC-TYRES] Descriptor has no part name; per-submesh slot names will be used.");
                else
                    Log($"[CLASSIC-TYRES] Descriptor part name: '{_currentDescriptorPartName}'");

                // Set _inTyreLoadPass so the body-only root-union slot
                // allowlist (the "number/generic/carbon 4/..." filter
                // around line 3050) doesn't drop our tyre slots. Without
                // this flag every tyre slot label was failing the
                // allowlist and the parser was emitting 0 submeshes
                // from 8 mesh records on every tyre descriptor. The
                // flag is reset in the finally regardless of how the
                // descriptor parse goes.
                _inTyreLoadPass = true;
                _currentTyreEra = detectedEra;
                try
                {
                    var partGroup = LoadLegacyMeshDescriptor(meshFile);
                    if (partGroup == null)
                    {
                        Log("  -> LoadLegacyMeshDescriptor returned null for tyre descriptor.");
                        continue;
                    }

                    // Wheel-copy emission. The classic-tyre loader produces
                    // a "partGroup" containing the union of tyre submeshes
                    // (4 sidewalls + 4 treads = 8 GeometryModel3Ds, all
                    // positioned at the tyre-local origin). We need ONE
                    // visible tyre per car wheel (4 wheels: fl, fr, bl, br),
                    // each placed at the body's render-graph wheel position.
                    //
                    // Strategy:
                    //   * Build four Model3DGroup wrappers, one per wheel
                    //     slot. Each wrapper's Children list references the
                    //     SAME GeometryModel3D instances from partGroup --
                    //     WPF composes wrapper.Transform with child geometry
                    //     at render time, so sharing the geometry across
                    //     wrappers correctly produces four independently
                    //     transformed copies without quadrupling memory.
                    //   * Tag each wrapper in ModelPartNameRegistry with
                    //     "wheel_fl" / "wheel_fr" / "wheel_bl" / "wheel_br".
                    //     MainWindow.GetPartNameForGroup will return that
                    //     name, the assembler will look it up in the body's
                    //     render-graph, and the wrapper's Transform3D
                    //     becomes the wheel-position matrix.
                    //   * Add only the FRONT-LEFT wrapper to `group` for the
                    //     "Assemble OFF" view (a single tyre at origin,
                    //     consistent with how non-tyre parts behave when the
                    //     toggle is off). Cache the other three wrappers in
                    //     ClassicTyreCloneRegistry so MainWindow can splice
                    //     them in/out when the toggle is flipped.
                    //
                    // This keeps the OFF view from showing four overlapping
                    // tyres at the origin (which would visually look like
                    // one weirdly-bright tyre), and the ON view places four
                    // distinct tyres at the four wheel positions.
                    // WPF Model3D parentage rule: an unfrozen Model3D can
                    // only belong to ONE Model3DCollection at a time.
                    // Adding it to a second collection silently removes
                    // it from the first. That meant our four wrappers
                    // ended up effectively empty except for whichever
                    // one ran the Add() loop last -- so the user saw
                    // exactly one tyre at the wheel_br position and
                    // nothing at the other three. Freezing each
                    // GeometryModel3D up front makes it shareable: a
                    // frozen Model3D can sit in any number of
                    // Model3DCollections at once because freezing locks
                    // every property and forfeits change notifications,
                    // which is exactly what we want for a read-only
                    // rendered tyre.
                    //
                    // Freezing is safe here because the per-wrapper
                    // Transform is set on the WRAPPER (a fresh
                    // Model3DGroup we own and don't freeze), not on
                    // the children -- WPF composes wrapper.Transform
                    // with each child's own (frozen-identity) transform
                    // at render time, so each wrapper's tyre still
                    // ends up at its correct world position.
                    foreach (var child in partGroup.Children)
                    {
                        if (child is System.Windows.Freezable f && !f.IsFrozen && f.CanFreeze)
                            f.Freeze();
                    }

                    var sharedChildren = new List<Model3D>(partGroup.Children);

                    string[] wheelNames =
                    {
                        "wheel_fl",
                        "wheel_fr",
                        "wheel_bl",
                        "wheel_br",
                    };

                    var clones = new List<Model3DGroup>(4);
                    foreach (var wheelName in wheelNames)
                    {
                        var wrapper = new Model3DGroup();
                        foreach (var child in sharedChildren)
                            wrapper.Children.Add(child);

                        // Register the wrapper's part name so the assembler
                        // can match it to the body render-graph's wheel_*
                        // node and pick up its world transform.
                        ModelPartNameRegistry.Register(wrapper, wheelName);

                        clones.Add(wrapper);
                    }

                    // The first wrapper (wheel_fl) is the "default-visible"
                    // copy: shown when assemble is OFF, also rendered on its
                    // wheel slot when ON. The other three are stored in the
                    // clone registry and only added to the scene when the
                    // toggle goes ON.
                    group.Children.Add(clones[0]);
                    tyrePartsAdded += partGroup.Children.Count;

                    ClassicTyreCloneRegistry.Register(clones[0], clones.GetRange(1, 3));

                    Log($"  -> Added 1 visible tyre wrapper (wheel_fl, with " +
                        $"{partGroup.Children.Count} submesh(es)); cached 3 " +
                        $"additional wrappers (wheel_fr/bl/br) for the " +
                        $"assemble-on toggle.");
                }
                catch (Exception ex)
                {
                    Log($"EXCEPTION while loading tyre descriptor '{meshFile}':");
                    Log(ex.ToString());
                }
                finally
                {
                    _inTyreLoadPass = false;
                    _currentTyreEra = null;
                    _currentDescriptorPath = null;
                    _currentDescriptorPartName = null;
                }
            }

            Log("--------------------------------------------------");
            Log($"[CLASSIC-TYRES] Tyre-load pass complete. Submeshes added: {tyrePartsAdded}");
        }

        /// <summary>
        /// Walk the supplied path's components looking for a
        /// ".../teams/&lt;X&gt;/..." segment and return &lt;X&gt;.
        /// Used when the mesh-descriptor filename doesn't carry a
        /// "&lt;team&gt;!!!" prefix (classic content from
        /// f1_classic_0[123]_vehicle_package, plus any extractor that
        /// rewrites the filename before unpacking).
        ///
        /// Returns null when no "teams/&lt;X&gt;" segment is present
        /// in the path. Caller's responsibility to fall back to a
        /// sensible default.
        ///
        /// Case-insensitive on the literal "teams" directory name to
        /// survive Windows-case extractors. The team folder name
        /// itself is returned verbatim because EAIDs are case-
        /// sensitive and we want to round-trip the cache layout.
        /// </summary>
        private static string TryDeriveTeamPrefixFromPath(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            try
            {
                string cur;
                try { cur = Path.GetFullPath(folder); }
                catch { return null; }

                while (!string.IsNullOrEmpty(cur))
                {
                    string parent = Path.GetDirectoryName(cur);
                    if (string.IsNullOrEmpty(parent)) break;

                    string parentLeaf = Path.GetFileName(parent);
                    if (string.Equals(parentLeaf, "teams", StringComparison.OrdinalIgnoreCase))
                    {
                        // cur is ".../teams/<team>/", return the leaf.
                        return Path.GetFileName(cur);
                    }

                    if (string.Equals(parent, cur, StringComparison.Ordinal)) break;
                    cur = parent;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Scan one mesh descriptor's raw bytes for the package-name ASCII
        /// (e.g. "f1_2018_vehicle_package"). Returns the first match. Used
        /// during auto-configure so we don't have to guess the package name
        /// when building material EAIDs. Re-uses DescriptorContainsAscii's
        /// search pattern but returns the string it found rather than a
        /// boolean; falls back to "f1_2018_vehicle_package" on failure
        /// (the only classic year the new mesh[0]-allowlist path currently
        /// supports; 2016/2017 hit a different parse branch that will
        /// separately need its own package-name resolution).
        /// </summary>
        private static string DetectPackageNameFromDescriptors(string folder)
        {
            try
            {
                foreach (var path in Directory.GetFiles(folder, "*temp000*mesh*",
                                                       SearchOption.TopDirectoryOnly))
                {
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(path); }
                    catch { continue; }
                    // Order matters: most-specific names first so e.g.
                    // "f1_classic_02_vehicle_package" wins over the generic
                    // "vehicle_package" substring it contains. F1 2018's
                    // classic content is split across three asset groups
                    // (01 = 1990s drivers like Senna/Prost, 02 = late-90s
                    // like c_mclaren_98, 03 = early-2000s); each groups
                    // its team folders the same way 2018 stock does, with
                    // identical mesh-descriptor binary layout, so the
                    // loader treats them as a single "modern 9-int header"
                    // family further down in is2018 detection.
                    //
                    // v12.06: F1 2020 introduces a fourth classic-content
                    // packaging -- "f1_classic_vehicle_package" (bare,
                    // unnumbered). It ships the same NEFX1 9-int-header
                    // mesh descriptors as the numbered F1 2018 classics
                    // (verified against c_benetton_94's mesh records),
                    // so a single substring addition to the candidate
                    // list pulls it into the same modern-9-int family.
                    // Listed AFTER the numbered forms to preserve the
                    // "most specific first" precedence rule -- the
                    // numbered names won't substring-collide with the
                    // bare name because the "_NN_" digits separate
                    // "classic" from "vehicle_package".
                    foreach (var candidate in new[]
                    {
                        "f1_classic_01_vehicle_package",
                        "f1_classic_02_vehicle_package",
                        "f1_classic_03_vehicle_package",
                        "f1_classic_vehicle_package",
                        "f1_2018_vehicle_package",
                        "f1_2017_vehicle_package",
                        "f1_2016_vehicle_package",
                        // v12.14: F1 2019 and F1 2020 stock-year packages.
                        // Listed AFTER the F1 2018 entry because the
                        // foreach short-circuits on first match and we
                        // want most-specific names to win; in practice
                        // these don't substring-collide with anything
                        // earlier in the list (the year digits separate
                        // "f1_" from "_vehicle_package"). When a F1
                        // 2019 or F1 2020 stock-content descriptor
                        // happens to embed its own package name in any
                        // EAID, this catches it BEFORE we fall through
                        // to the year-aware fallback below.
                        "f1_2019_vehicle_package",
                        "f1_2020_vehicle_package",
                    })
                    {
                        if (DescriptorContainsAscii(bytes, candidate))
                            return candidate;
                    }
                }
            }
            catch { }

            // v12.14: year + structure-aware fallback.
            //
            // Reached only when the string-scan above found no descriptor
            // containing any recognised "*_vehicle_package" substring.
            // The existing hardcoded "f1_2018_vehicle_package" return
            // immediately below stays as the final safety net; this
            // block layers on top of it to pick a more accurate name
            // when we have enough information to do so.
            //
            // The accurate name matters because _detectedPackageName is
            // used downstream to construct material and texture EAIDs
            // (e.g. "eaid://<package>/teams/<team>/textures/<x>.tif").
            // The on-disk mipmap sidecar pipeline resolves those EAIDs
            // against F1PackageLocator.Last.VehiclePackageRoot, which
            // is the real install path. If the package name we stamp
            // into the EAID doesn't match the real folder name on disk
            // (e.g. we say "f1_2018_vehicle_package" but the ERP lives
            // in "f1_classic_02_vehicle_package"), the resolver's
            // package-folder check rejects the path and the high-res
            // textures don't load. The 2018-19 1990-Ferrari case and
            // the F1 2019 stock case both hit exactly this scenario.
            //
            // We need TWO pieces of information here:
            //   1. What game year did the picked ERP come from?
            //      -> F1PackageLocator.Last.Kind via GetYearForKind,
            //         set in MainWindow before extraction started.
            //   2. Is this a CLASSIC descriptor (so we pick a classic
            //      package name) or a STOCK descriptor (so we pick
            //      "f1_NNNN_vehicle_package")?
            //      -> use the same v12.12 structural detector that
            //         drives is2018 dispatch in LoadLegacyMeshDescriptor.
            //         Re-running it here, on the same temp000 files, is
            //         cheap (one header read per descriptor) and gives
            //         a single source of truth for "modern 36-byte
            //         header" classification.
            //
            // The year-aware mapping itself lives in F1PackageLocator
            // (GetExpectedClassicPackageForGameYear /
            // GetExpectedStockPackageForGameYear) so the rules can be
            // adjusted in one place without touching this file.
            try
            {
                int gameYear = 0;
                var loc = F1PackageLocator.Last;
                if (loc != null)
                    gameYear = F1PackageLocator.GetYearForKind(loc.Kind);

                if (gameYear > 0)
                {
                    bool sawClassicShapedDescriptor = false;
                    bool sawAnyDescriptor = false;
                    foreach (var path in Directory.GetFiles(folder, "*temp000*mesh*",
                                                           SearchOption.TopDirectoryOnly))
                    {
                        byte[] bytes;
                        try { bytes = File.ReadAllBytes(path); }
                        catch { continue; }
                        sawAnyDescriptor = true;
                        if (LooksLikeClassic36ByteHeader(bytes))
                        {
                            sawClassicShapedDescriptor = true;
                            break;
                        }
                    }

                    if (sawAnyDescriptor)
                    {
                        string preferred = sawClassicShapedDescriptor
                            ? F1PackageLocator.GetExpectedClassicPackageForGameYear(gameYear)
                            : F1PackageLocator.GetExpectedStockPackageForGameYear(gameYear);

                        if (!string.IsNullOrEmpty(preferred))
                        {
                            Log($"DetectPackageNameFromDescriptors (v12.14): " +
                                $"no embedded package marker found; gameYear={gameYear}, " +
                                $"classic-shape={sawClassicShapedDescriptor}, " +
                                $"using '{preferred}' so material/texture EAIDs " +
                                $"resolve against the real on-disk install.");
                            return preferred;
                        }
                    }
                }
            }
            catch { /* fall through to existing hardcoded fallback */ }

            return "f1_2018_vehicle_package";
        }

        /// <summary>
        /// (v12.15) Post-process the package name returned by
        /// <see cref="DetectPackageNameFromDescriptors"/>.
        ///
        /// Background: why this layer exists
        /// ----------------------------------
        /// DetectPackageNameFromDescriptors scans descriptor bytes for the
        /// FIRST appearance of any known "*_vehicle_package" substring.
        /// That works correctly for stock content (whose own EAIDs match
        /// the package the ERP ships in) and for self-referential classic
        /// content. It does NOT work for F1 2019's classic-expansion cars
        /// (e.g. 1990 Ferrari), whose descriptor EAIDs reference SHARED
        /// stock assets like glass.material, vehicle_revs_*, and so on
        /// that legitimately live under "f1_2018_vehicle_package" even
        /// when the car itself is shipped in "f1_classic_02_vehicle_package"
        /// inside an F1 2019 install. The scanner sees the stock-asset
        /// reference first and returns "f1_2018_vehicle_package", so
        /// every downstream EAID built for the Ferrari (its OWN textures
        /// and materials, not the shared ones) gets stamped with the
        /// wrong package prefix and the on-disk mipmap pipeline can't
        /// find them.
        ///
        /// The authoritative signal -- what folder the picked ERP file
        /// physically sits in -- is already captured by
        /// <see cref="F1PackageLocator.Last"/> before extraction begins
        /// (set in MainWindow.LoadIdf_Click). When that's a known package
        /// folder we trust it over the descriptor-byte scan, because no
        /// amount of cross-references inside the bytes can override
        /// where the user actually picked the ERP from on disk.
        ///
        /// Override conditions (all required)
        /// ----------------------------------
        ///   1. The scanned name is one of the stock-year forms
        ///      ("f1_2016/2017/2018/2019/2020_vehicle_package"). Other
        ///      names (e.g. "f1_classic_*") are left alone because they
        ///      came from a successful scan that found the actual
        ///      classic package referenced by the descriptor.
        ///   2. F1PackageLocator.Last is non-null and has a non-empty
        ///      PackageFolderName. If the user loaded a loose ERP that
        ///      isn't inside any recognised install, we have no on-disk
        ///      signal and leave the scanned name alone.
        ///   3. PackageFolderName differs from the scanned name. When
        ///      they agree no override is needed.
        ///
        /// When all three conditions hold we return PackageFolderName.
        /// Otherwise we return the scanned name verbatim.
        ///
        /// This function is purely additive: every caller that previously
        /// took DetectPackageNameFromDescriptors's return value unchanged
        /// can wrap the call site with this without altering any
        /// semantics for the cases it doesn't intervene in.
        /// </summary>
        private static string ApplyLocatorOverrideToDetectedPackageName(string scanned)
        {
            // v12.15-diag: log entry unconditionally so the next log run
            // proves the helper is wired in and shows what
            // F1PackageLocator.Last looked like at the moment of the
            // call. Every early-return path also logs below so any
            // failure to override has a corresponding visible reason
            // in the log. (Previous version returned silently on null
            // locator / empty name / agreement, which left the user
            // unable to tell which condition was tripping.)
            string lastSummary;
            try
            {
                var probe = F1PackageLocator.Last;
                lastSummary = (probe == null)
                    ? "<null>"
                    : $"Kind={probe.Kind}, PackageFolderName='{probe.PackageFolderName ?? "<null>"}', VehiclePackageRoot='{probe.VehiclePackageRoot ?? "<null>"}'";
            }
            catch (Exception ex)
            {
                lastSummary = $"<access threw {ex.GetType().Name}: {ex.Message}>";
            }
            try
            {
                Log($"v12.15-diag entry: scanned='{scanned ?? "<null>"}', " +
                    $"F1PackageLocator.Last={lastSummary}");
            }
            catch { /* log failures are non-fatal */ }

            // v12.16: dump the locator's most recent walk trace so when
            // Last is null we can SEE which paths the strict + permissive
            // walks tested and which folder leaves they observed. The
            // trace is a multi-line string built up inside LocateFromErp;
            // we split on newlines and emit each line as its own log
            // entry so timestamps interleave neatly with the surrounding
            // loader output.
            try
            {
                string trace = F1PackageLocator.LastWalkTrace;
                if (!string.IsNullOrEmpty(trace))
                {
                    Log("v12.16-walk: F1PackageLocator.LastWalkTrace follows --");
                    foreach (var line in trace.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                    {
                        if (line.Length == 0) continue;
                        Log("    " + line);
                    }
                    Log("v12.16-walk: end of LastWalkTrace.");
                }
                else
                {
                    Log("v12.16-walk: F1PackageLocator.LastWalkTrace is empty -- " +
                        "LocateFromErp was never called this session, or this build " +
                        "predates the v12.16 trace-capture changes.");
                }
            }
            catch (Exception ex)
            {
                try { Log($"v12.16-walk: reading LastWalkTrace threw {ex.GetType().Name}: {ex.Message}"); }
                catch { }
            }

            if (string.IsNullOrEmpty(scanned))
            {
                try { Log("v12.15-diag: scanned name was null/empty, returning unchanged."); }
                catch { }
                return scanned;
            }

            bool scannedIsStockYear =
                string.Equals(scanned, "f1_2016_vehicle_package", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scanned, "f1_2017_vehicle_package", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scanned, "f1_2018_vehicle_package", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scanned, "f1_2019_vehicle_package", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scanned, "f1_2020_vehicle_package", StringComparison.OrdinalIgnoreCase);
            if (!scannedIsStockYear)
            {
                try
                {
                    Log($"v12.15-diag: scanned='{scanned}' is NOT a stock-year form " +
                        "(scanner already returned a classic name from a direct " +
                        "descriptor match). No override needed; passing through.");
                }
                catch { }
                return scanned;
            }

            F1PackageLocator.Located loc;
            try { loc = F1PackageLocator.Last; }
            catch (Exception ex)
            {
                try
                {
                    Log($"v12.15-diag: F1PackageLocator.Last access threw " +
                        $"{ex.GetType().Name}: {ex.Message}. Cannot override; " +
                        $"passing scanned='{scanned}' through unchanged.");
                }
                catch { }
                return scanned;
            }
            if (loc == null)
            {
                try
                {
                    Log($"v12.15-diag: F1PackageLocator.Last is null -- " +
                        "MainWindow.LoadIdf_Click either didn't call LocateFromErp, " +
                        "or LocateFromErp returned null because the picked ERP path " +
                        "doesn't contain any KnownPackages folder name " +
                        "(stray ERP / loose copy / unrecognised install layout / " +
                        "manually-extracted-then-reloaded). Cannot override; " +
                        $"passing scanned='{scanned}' through unchanged.");
                }
                catch { }
                return scanned;
            }
            string onDiskName = loc.PackageFolderName;
            if (string.IsNullOrEmpty(onDiskName))
            {
                try
                {
                    Log($"v12.15-diag: F1PackageLocator.Last is set (Kind={loc.Kind}) " +
                        "but PackageFolderName is null/empty -- partial locator result. " +
                        $"Cannot override; passing scanned='{scanned}' through unchanged.");
                }
                catch { }
                return scanned;
            }

            if (string.Equals(onDiskName, scanned, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Log($"v12.15-diag: scanned='{scanned}' already agrees with on-disk " +
                        $"PackageFolderName='{onDiskName}'. No override needed.");
                }
                catch { }
                return scanned;
            }

            try
            {
                Log("DetectPackageNameFromDescriptors override (v12.15): " +
                    $"scanner returned '{scanned}' but the picked ERP physically " +
                    $"lives in '{onDiskName}' on disk; preferring the on-disk " +
                    "folder name so this car's OWN material/texture EAIDs resolve " +
                    "against the real install path. (Shared assets that legitimately " +
                    "reference the stock-year package are resolved by the texture " +
                    "pipeline's own per-EAID path walk, not by this name.)");
            }
            catch { /* log failures are non-fatal */ }
            return onDiskName;
        }

        private static void AutoConfigureTextureCache(string idfFolder)
        {
            // If SetTextureCache was called explicitly before LoadFolder,
            // honour that and leave it alone. This matches F1ModelLoader's
            // AutoConfigure behaviour and lets tests / callers override
            // with a stub cache without having to race the discovery path.
            if (_textureCache != null)
            {
                Log($"AutoConfigure: using pre-set TextureCache (packageRoot={_textureCache.PackageRoot})");
                if (string.IsNullOrEmpty(_detectedPackageName))
                    _detectedPackageName = DetectPackageNameFromDescriptors(idfFolder);
                // v12.15: trust the on-disk install folder over the
                // descriptor-byte scan when both are available. Stock-
                // year scanner results get corrected to the real
                // package the picked ERP lives in (no-op when they
                // already agree). See helper docs for details.
                _detectedPackageName = ApplyLocatorOverrideToDetectedPackageName(_detectedPackageName);
                return;
            }

            _detectedPackageName = null;

            string packageRoot = DiscoverPackageRoot(idfFolder);
            if (packageRoot == null)
            {
                Log("AutoConfigure: package root NOT FOUND walking up from idf folder -- " +
                    "textures will not load (all submeshes render as neutral gray). " +
                    "The extracted ERP cache must contain teams/<team>/materials and " +
                    "teams/<team>/textures siblings for the TextureCache to resolve.");
                return;
            }

            _detectedPackageName = DetectPackageNameFromDescriptors(idfFolder);
            // v12.15: trust the on-disk install folder over the
            // descriptor-byte scan when both are available. Helper
            // is a no-op when scanner returned a classic name or when
            // F1PackageLocator.Last is null (stray ERP); otherwise
            // corrects stock-year scanner mistakes (Ferrari 90 in an
            // F1 2019 install gets reported as f1_2018_vehicle_package
            // by the scanner because its descriptor references shared
            // f1_2018 assets -- the on-disk folder is the authoritative
            // answer).
            _detectedPackageName = ApplyLocatorOverrideToDetectedPackageName(_detectedPackageName);
            Log($"AutoConfigure: package root = {packageRoot}");
            Log($"AutoConfigure: package name = {_detectedPackageName}");

            try
            {
                _textureCache = new TextureCache(packageRoot, Log);
                Log("AutoConfigure: TextureCache created.");
            }
            catch (Exception ex)
            {
                Log($"AutoConfigure: failed to construct TextureCache: {ex.Message}. " +
                    "All submeshes will render as neutral gray.");
                _textureCache = null;
            }

            // Load the livery context (GfxMatBindRes / .emb?context=N).
            // Preferred over the hardcoded slot->material table because
            // it's the game's own authoritative binding: "generic" in
            // F1 2018 McLaren binds to shared/generics/materials/
            // generics.material, not the team-local mclaren_detail we
            // were guessing. Also correctly binds flag / disc / lcd /
            // digi revs / rainbeads to their shared/generics/* materials
            // instead of misrouting them to steering-wheel / wheel-hub
            // materials.
            //
            // Context selection: try PreviewDriverNumber first (so the
            // number slot binds to mclaren_number_{driver}.material);
            // fall back to context=1 if the specific driver's context
            // isn't present. Non-number slots are identical across every
            // context -- "generic"/"body"/etc. always map to the same
            // material regardless of which driver's number is active --
            // so fallback to context=1 only changes which number atlas
            // renders, nothing else.
            try
            {
                var preferred = EmbContextReader.LoadFromFolder(idfFolder, PreviewDriverNumber);
                if (preferred != null)
                {
                    _activeContext = preferred;
                    Log($"AutoConfigure: livery context loaded (context={preferred.ContextId}, " +
                        $"{preferred.EntryCount} slot bindings).");
                }
                else
                {
                    var first = EmbContextReader.EnumerateContexts(idfFolder).FirstOrDefault();
                    if (!string.IsNullOrEmpty(first.path))
                    {
                        _activeContext = EmbContextReader.FromFile(first.path);
                        Log($"AutoConfigure: livery context loaded (fallback context={first.id}, " +
                            $"preferred #{PreviewDriverNumber} not found).");
                    }
                    else
                    {
                        Log("AutoConfigure: no .emb?context=* files found -- " +
                            "falling back to hardcoded slot->material table.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"AutoConfigure: failed to load livery context: {ex.Message}. " +
                    "Falling back to hardcoded slot->material table.");
                _activeContext = null;
            }
        }

        /// <summary>
        /// Map a submesh slot name to a candidate material EAID for F1 2018.
        /// The F1 2018 McLaren ERP contains exactly 13 non-livery-variant
        /// materials:
        ///   teams/mclaren/materials/mclaren_paint.material
        ///   teams/mclaren/materials/mclaren_carbon1.material
        ///   teams/mclaren/materials/mclaren_carbon3.material
        ///   teams/mclaren/materials/mclaren_carbon4.material
        ///   teams/mclaren/materials/mclaren_detail.material
        ///   teams/mclaren/materials/mclaren_mirror_left.material
        ///   teams/mclaren/materials/mclaren_mirror_right.material
        ///   teams/mclaren/materials/mclaren_steeringwheel.material
        ///   teams/mclaren/materials/mclaren_wheel_hub.material
        ///   shared/generics/materials/glass.material
        ///   shared/generics/materials/rear_light.material
        ///   shared/generics/materials/vehicle_revs_type2.material
        ///   shared/generics/materials/vehicle_revs_type4.material
        /// plus mclaren_number_1..99 (per-driver livery decal atlases).
        ///
        /// Submesh slot names emitted by our mesh[0]+'number'+'generic'
        /// allowlist: body, generic, rainbeads, carbon tile 1/3/4,
        /// number, mirror left/right, steering wheel, wheel, detail,
        /// rear light, flag, lcd, digi revs, disc. We map each to its
        /// most likely material; anything we can't place returns null and
        /// that submesh falls back to the gray neutral material.
        /// </summary>
        private static string BuildMaterialEaidForSlot(string packageName, string teamPrefix, string slotName)
        {
            if (string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(slotName))
                return null;

            // Classic-tyre fast path: when we're inside the tyre-load pass,
            // route the descriptor's slot names ("wall", "tread", and the
            // numbered duplicates "mesh_2".."mesh_7" for the other three
            // wheels) to the bundled tyre materials in shared/tyres/materials.
            //
            // We DO NOT defer to the active context (.emb GfxMatBindRes) for
            // tyres because the active context was loaded for the body, not
            // the tyre. If a tyre context happened to load and bind 'wall'
            // to a body material (e.g. via slot-name collision) the resulting
            // EAID would point at the wrong file -- the body's material
            // resolver would then fail or, worse, paint the body's logo on
            // the rubber. Hard-routing tyre slots here is more deterministic.
            //
            // Tyres.erp materials are flat in shared/tyres/materials/ and
            // keyed by era + slot + compound, e.g.:
            //   c_03_sidewall_medium.material
            //   c_03_tread_medium.material
            //   c_03_sidewall_soft.material   (compound variants -- ignored
            //   c_03_tread_wet.material        per spec, no compound switch)
            //
            // For the single-default design we always pick "_medium" as the
            // canonical dry compound. (Codemasters ships _hard / _medium /
            // _soft / _super_soft / _ultra_soft / _intermediate / _wet for
            // every era; _medium is the baseline F1 dry slick that matches
            // what the game shows by default.)
            if (_inTyreLoadPass && !string.IsNullOrEmpty(_currentTyreEra))
            {
                string tyreSlot = slotName.Trim().ToLowerInvariant();
                string tyreKind = ClassifyTyreSlot(tyreSlot);
                if (tyreKind != null)
                {
                    return $"eaid://{packageName}/shared/tyres/materials/" +
                           $"{_currentTyreEra}_{tyreKind}_medium.material";
                }
                // Tyre pass but unrecognised slot name -- fall through to
                // the body resolver below. Should be rare; if it fires,
                // the existing "no material-EAID mapping" log line will
                // pick it up and we can extend ClassifyTyreSlot.
            }

            // Preferred path: ask the loaded GfxMatBindRes (.emb?context=N)
            // directly. This is the game's own authoritative slot -> material
            // binding and will correctly distinguish, e.g., "generic" -> shared/
            // generics/materials/generics.material from "detail" -> teams/
            // mclaren/materials/mclaren_detail.material -- something our
            // hardcoded table below had been getting wrong because we
            // originally conflated them. Falls through to the hardcoded table
            // on any of: no context loaded (extraction didn't emit .emb files,
            // or discovery failed), slot not present in this context's binding
            // list, resolved EAID empty. The hardcoded table remains in place
            // as a safety net; anywhere it disagrees with the context, the
            // context wins.
            if (_activeContext != null)
            {
                string ctxEaid = _activeContext.Resolve(slotName);
                if (!string.IsNullOrEmpty(ctxEaid))
                    return ctxEaid;
            }

            string slot = slotName.Trim().ToLowerInvariant();
            string team = (teamPrefix ?? string.Empty).ToLowerInvariant();

            string teamPath = string.IsNullOrEmpty(team) ? null :
                $"eaid://{packageName}/teams/{team}/materials";
            string sharedPath = $"eaid://{packageName}/shared/generics/materials";

            // Body / paint.
            if (slot == "body")
                return teamPath != null ? $"{teamPath}/{team}_paint.material" : null;

            // Carbon tile N -> mclaren_carbonN.material
            if (slot.StartsWith("carbon tile "))
            {
                string tail = slot.Substring("carbon tile ".Length).Trim();
                if (int.TryParse(tail, out int n) && teamPath != null)
                    return $"{teamPath}/{team}_carbon{n}.material";
            }

            // Driver number: use PreviewDriverNumber
            if (slot == "number")
                return teamPath != null
                    ? $"{teamPath}/{team}_number_{PreviewDriverNumber}.material"
                    : null;

            if (slot == "mirror left")
                return teamPath != null ? $"{teamPath}/{team}_mirror_left.material" : null;
            if (slot == "mirror right")
                return teamPath != null ? $"{teamPath}/{team}_mirror_right.material" : null;

            if (slot == "steering wheel")
                return teamPath != null ? $"{teamPath}/{team}_steeringwheel.material" : null;

            if (slot == "wheel")
                return teamPath != null ? $"{teamPath}/{team}_wheel_hub.material" : null;

            if (slot == "detail")
                return teamPath != null ? $"{teamPath}/{team}_detail.material" : null;

            // All of the following are shared/generics in F1 2018 -- confirmed
            // by extracting the GfxMatBindRes (mclaren.emb?context=N) from the
            // ERP and reading the slot -> material table. An earlier version
            // of this table routed them to team-local materials (steeringwheel
            // for flag/lcd/digi revs, wheel_hub for disc, detail for generic)
            // which produced visibly wrong textures -- the generic slot in
            // particular was rendering mclaren_detail_d.tif at UVs authored
            // for generics_d.tif, putting random atlas fragments on interior
            // and cockpit geometry. These are the real bindings.
            if (slot == "generic") return $"{sharedPath}/generics.material";
            if (slot == "flag") return $"{sharedPath}/indicator_lights.material";
            if (slot == "digi revs") return $"{sharedPath}/vehicle_revs_type2.material";
            if (slot == "lcd") return $"{sharedPath}/generic_lcd.material";
            if (slot == "disc") return $"{sharedPath}/discs.material";
            if (slot == "rear light") return $"{sharedPath}/rear_light.material";
            if (slot == "rainbeads") return $"{sharedPath}/rainbeads.material";
            if (slot == "glass") return $"{sharedPath}/glass.material";
            if (slot == "cockpit") return $"{sharedPath}/generics_cockpit.material";
            if (slot == "shadow") return $"eaid://{packageName}/shared/shadows/materials/f1_vehicle_shadow.material";

            return null;
        }

        private static Material ResolveSubmeshMaterial(string slotName, string teamPrefix,
                                                       out TextureCache.PreviewResolution? resolution,
                                                       out string logStatus)
        {
            resolution = null;
            logStatus = null;
            if (_textureCache == null)
            {
                logStatus = "no TextureCache";
                return null;
            }

            string materialEaid = BuildMaterialEaidForSlot(
                _detectedPackageName ?? "f1_2018_vehicle_package",
                teamPrefix, slotName);
            if (string.IsNullOrEmpty(materialEaid))
            {
                logStatus = $"no material-EAID mapping for slot '{slotName}'";
                return null;
            }

            try
            {
                var res = _textureCache.ResolveDiffuseForMaterial(materialEaid, slotName);
                resolution = res;
                if (res.Brush == null)
                {
                    logStatus = $"material-resolve failed ({res.FailureReason ?? "unknown"}) " +
                                $"for {materialEaid}";
                    return null;
                }
                logStatus = $"OK tex={res.TextureEaid} slot={res.PickedSlot}" +
                            (res.HasOverlay
                                ? $" +overlay={res.OverlayTextureEaid} ({res.OverlaySlot})"
                                : "");
                // Substitute with the hi-res mipmap-sidecar brush when
                // one exists for this base diffuse texture; falls
                // through to the original res.Brush otherwise.
                var diffuseBrush = HighResBrushProvider.WrapBrush(
                    res.TextureEaid, res.Brush, _textureCache, Log);
                return new DiffuseMaterial(diffuseBrush);
            }
            catch (Exception ex)
            {
                logStatus = $"material-resolve threw ({ex.Message}) for {materialEaid}";
                return null;
            }
        }

        /// <summary>
        /// Classic-specific overlay resolver. F1 2018's paint/carbon/steering
        /// shaders use EAIDs like "vehicle_paint.nefx2" (no "_metallic_"
        /// prefix), which MaterialSlotResolver.ClassifyShader doesn't
        /// recognise -- it falls through to ResolveUnknownByHeuristic, and
        /// that path deliberately refuses to pick an overlay slot because
        /// it can't guarantee the shader's compositing rules. Result: the
        /// base paint texture loads but sponsor decals and driver numbers
        /// never render.
        ///
        /// We sidestep the shader-name gate entirely here: if the slot is
        /// one that we know wants a decal overlay on top of the base
        /// (body, number, carbon tile N), read the material file directly
        /// via TextureCache.GetMaterial, look for me_logoMap / me_decalMap
        /// / me_sponsorMap in its bindings, and decode the bound texture
        /// into a brush. This keeps all 2021+ behaviour (where the
        /// shader-family path runs before this) unaffected -- if
        /// resolution already has a valid OverlayBrush, the caller uses
        /// that and never invokes this fallback.
        ///
        /// Returns null on any failure (no cache, no mapping, no binding,
        /// decode fail); logInfo is always populated so the log can
        /// diagnose which step missed.
        /// </summary>
        private static System.Windows.Media.Brush TryResolveOverlayBrushClassic(
            string slotName, string teamPrefix, out string logInfo)
        {
            logInfo = null;
            if (_textureCache == null) { logInfo = "no TextureCache"; return null; }

            string slot = (slotName ?? string.Empty).Trim().ToLowerInvariant();
            // Gate: only body/number/carbon slots historically carry decal
            // overlays in F1 2018 packages. Mirrors are fine as plain
            // base, rear lights don't get sponsors, etc. This gate also
            // prevents us from accidentally picking up a random me_logoMap
            // binding on, say, mclaren_mirror_left.material and painting
            // sponsors on the mirror glass.
            bool wantsOverlay = slot == "body" || slot == "number" ||
                                slot.StartsWith("carbon tile ");
            if (!wantsOverlay) { logInfo = $"slot '{slot}' not in body/number/carbon-tile allowlist"; return null; }

            string materialEaid = BuildMaterialEaidForSlot(
                _detectedPackageName ?? "f1_2018_vehicle_package",
                teamPrefix, slotName);
            if (string.IsNullOrEmpty(materialEaid))
            {
                logInfo = $"no material-EAID mapping for slot '{slotName}'";
                return null;
            }

            MaterialFile matFile = null;
            try
            {
                matFile = _textureCache.GetMaterial(materialEaid);
            }
            catch (Exception ex)
            {
                logInfo = $"GetMaterial threw ({ex.Message}) for {materialEaid}";
                return null;
            }
            if (matFile == null || matFile.BindingsBySlot == null ||
                matFile.BindingsBySlot.Count == 0)
            {
                logInfo = $"material not parsed / empty bindings for {materialEaid}";
                return null;
            }

            // Try the known overlay slot names in priority order. me_logoMap
            // is the 2021+ convention (and likely 2018's too based on sampler
            // naming continuity in the Ego engine); me_decalMap and
            // me_sponsorMap cover older and alternate conventions.
            string[] overlaySlots = { "me_logoMap", "me_decalMap", "me_sponsorMap" };
            foreach (var overlaySlotName in overlaySlots)
            {
                if (matFile.BindingsBySlot.TryGetValue(overlaySlotName, out var overlayEaid) &&
                    !string.IsNullOrEmpty(overlayEaid))
                {
                    try
                    {
                        // Route through HighResBrushProvider so the
                        // mipmaps sidecar (if one ships next to the
                        // ERP on disk) gets used in preference to the
                        // small tail mip embedded in the ERP itself.
                        var brush = HighResBrushProvider.GetBrushForTexture(
                            overlayEaid, _textureCache, Log);
                        if (brush != null)
                        {
                            logInfo = $"classic-fallback via {overlaySlotName} -> {overlayEaid}";
                            return brush;
                        }
                        logInfo = $"{overlaySlotName} binding present but brush decode returned null: {overlayEaid}";
                        return null;
                    }
                    catch (Exception ex)
                    {
                        logInfo = $"{overlaySlotName} brush decode threw ({ex.Message}) for {overlayEaid}";
                        return null;
                    }
                }
            }

            logInfo = $"no me_logoMap/me_decalMap/me_sponsorMap in material " +
                      $"(has {matFile.BindingsBySlot.Count} bindings: " +
                      $"{string.Join(",", matFile.BindingsBySlot.Keys)})";
            return null;
        }

        /// <summary>
        /// Maximum tile rate (in either U or V) before a submesh's UVs
        /// are scaled down. Codemasters carbon panels (mclaren_carbon
        /// and friends) store UVs running from roughly -50 to +70 tile
        /// repeats because the real game shader applies its own per-
        /// material scale plus a lacquer / anisotropy pass. Without
        /// that shader, raw 70-tile UVs render as indistinguishable fine
        /// visual noise ("mclaren_carbon is distorted") rather than a
        /// recognisable weave. Capping makes the weave readable in a
        /// basic preview while leaving body / detail / number UVs
        /// (already in [0, 1]) untouched.
        ///
        /// Default matches F1ModelLoader.MaxUvTileRate. Raise this
        /// value (or set it to a very large number) to see the raw
        /// carbon tile rate when debugging textures.
        /// </summary>
        public static double MaxUvTileRate { get; set; } = 256.0;

        /// <summary>
        /// Tile-repeat multiplier applied to carbon slot UVs that are
        /// stored in atlas range ([0, 1]) rather than already-tile-rate
        /// range. Affects submeshes whose name starts with "carbon"
        /// (case-insensitive) AND whose max|UV| is at or below
        /// <see cref="CarbonAtlasThreshold"/>.
        ///
        /// Rationale: F1 2018 stores "carbon tile 1" submeshes' UVs in
        /// the same [0, 1] paint-atlas range as body/detail. The live
        /// game shader (me_carbonTile) multiplies those UVs by a large
        /// per-material scale at draw time to tile mclaren_carbon1.tif
        /// into a visible weave. A basic preview without that shader
        /// samples the carbon texture once across each panel, producing
        /// a smeared / distorted look. Multiplying atlas UVs by the
        /// factor below reproduces the in-shader tiling for preview
        /// purposes.
        ///
        /// Contrast with F1 2018 "carbon tile 4" (paddles, steering
        /// wheel): those submeshes live at vertex offsets where UVs are
        /// ALREADY authored at ~70 tile rate, so multiplying them would
        /// double-apply the tiling. The atlas-threshold guard skips
        /// those automatically.
        ///
        /// Default 32.0 produces a readable weave of comparable density
        /// to the 2021+ loader's capped carbon preview. Set to 1.0 to
        /// disable the multiplier and see the raw atlas-range UVs.
        /// </summary>
        public static double CarbonTileMultiplier { get; set; } = 128.0;

        /// <summary>
        /// Maximum |UV| below which a carbon slot's UVs are considered
        /// to be stored in atlas range and thus eligible for the
        /// <see cref="CarbonTileMultiplier"/> tile-rate upscale. Above
        /// this, UVs are assumed to already be tile-rate and the
        /// multiplier is skipped (the normal <see cref="MaxUvTileRate"/>
        /// cap still applies as usual).
        /// </summary>
        public static double CarbonAtlasThreshold { get; set; } = 2.0;

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
        public static class F1CoordinateSystem
        {
            // Adjust these numbers until the car looks “straightforward” for *all* years.
            public static readonly Transform3D Orientation =
                new RotateTransform3D(
                    new AxisAngleRotation3D(new Vector3D(1, 0, 0), -90));
        }

        private static void ResetLog(string logFile)
        {
            _logPath = logFile ?? "";

            if (!string.IsNullOrEmpty(_logPath))
            {
                try
                {
                    File.WriteAllText(_logPath, "F1ModelLoaderClassic Log\r\n");
                }
                catch
                {
                    // ignore
                }
            }

            _totalSubmeshes = 0;
            _totalIndices = 0;
            _totalTriangles = 0;
            _totalVertices = 0;


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
            public int IBufferIndex;
            public string DescriptorHint;

        }

        private class GfxModelRes
        {
            public uint NumLodDistances;
            public ushort NumNodes;
            public ushort NumBoneNodeIndices;
            public uint NumNames;
            public uint NumFiles;

            public float[] UnkHeaderFloats = new float[4];
            public uint[] Num6 = Array.Empty<uint>();
            public uint Num7;

            public List<GfxLod> LodDistances { get; } = new List<GfxLod>();
            public List<GfxNode> Nodes { get; } = new List<GfxNode>();
            public short[] BoneNodeIndices = Array.Empty<short>();
            public List<string> Files { get; } = new List<string>();
            public List<string> Names { get; } = new List<string>();
        }

        private struct GfxLod
        {
            public float MinDist;
            public float MaxDist;
        }

        private class GfxNode
        {
            public uint StringOffset;
            public uint ParentIndex;

            public Vector3D Position;
            public Quaternion Rotation;
            public Vector3D Scale;
            public Matrix3D Transform;

            public uint Unk6;
            public uint Unk7;

            public string Name;
        }

        // =====================================================================
        // PUBLIC ENTRY POINTS
        private static int PickPositionVBufferIndex(
      List<VertexBufferInfo> vbufs,
      uint[] vbOffsets,
      uint vertexFileOffset,
      int idxVb0,
      uint baseVertexIndex = 0)
        {
            if (vbufs == null || vbufs.Count == 0)
                return -1;

            // -----------------------------------------------------------
            // POSITION VB DISCRIMINATION (F1 2018 FIX: "GIANT BALL")
            // -----------------------------------------------------------
            //
            // In F1 2018 ERPs every VB reports Fvf=1, so the old
            // "Fvf != 29" filter passes EVERY buffer as a position
            // candidate. Unfortunately the vbOffsets table can steer the
            // exact-match or segment-match logic onto a stride-8 buffer,
            // which in 2018 holds half-float NORMALS (unit vectors in
            // [-1, +1]). The position decoder then reads those unit-
            // vector normals as if they were positions and every vertex
            // lands inside a unit sphere at the origin -- the entire
            // submesh collapses into a ~1 metre "ball" around the car's
            // centre (exact symptom reported 2026-04-19).
            //
            // The clean discriminator is stride: position streams in
            // 2018 are always stride-12 (3 x float32). Stride-8 streams
            // are normals / tangents / UVs. So we additionally require
            // stride-12 when ANY stride-12 buffer exists in the list.
            //
            // When no stride-12 VBs are present (some hypothetical
            // 2016/2017 layouts, or tiny accessory descriptors that only
            // ship aux streams), the filter harmlessly degrades to the
            // old behaviour so we don't break any already-working case.
            bool hasAnyStride12 = false;
            for (int i = 0; i < vbufs.Count; i++)
            {
                if (vbufs[i] != null && vbufs[i].Stride == 12)
                {
                    hasAnyStride12 = true;
                    break;
                }
            }

            bool IsPosCandidate(int i)
            {
                if (i < 0 || i >= vbufs.Count) return false;
                var vb = vbufs[i];
                if (vb == null) return false;
                if (vb.Fvf == 29) return false; // 29 = UV (2xHALF) in Noesis plugin
                // Stride-12 preference: if the descriptor ships any
                // stride-12 buffers, only those qualify as position
                // sources. Everything else (stride 4/8/40/etc.) is a
                // companion stream, not a position stream.
                // Allow stride-12 (POS-only) and stride-40 (interleaved
                // POS+NRM+TAN+UV+BONE — the Mirror pool / vb=11 in
                // F1 2018). Both place float32 POSITION at offset 0
                // of each stride, so DecodePosition reads them
                // identically. Stride-8 (half-float aux) and others
                // remain rejected so the giant-ball bug stays fixed.
                if (hasAnyStride12 && vb.Stride != 12 && vb.Stride != 40) return false;
                // Base-vertex sufficiency: when the submesh's
                // vertexOffsetIndex is non-zero, the chosen VB must
                // contain at least that many vertices or the base offset
                // is invalid and the downstream sanity check will clamp
                // it to 0 -- which pulls triangles from the wrong part
                // of the buffer and produces the "carbon-tile panels
                // showing up on top of the car" symptom on ERPs like
                // F1 2018 McLaren, where the paddle descriptors load
                // group-B VBs first (count 82029) even though the
                // paddle's secondary mesh lives in group A (count
                // 293988) at base vertex 129795.
                if (baseVertexIndex > 0 && (uint)vb.Count <= baseVertexIndex) return false;
                return true;
            }

            // 1) Exact match on vbOffsets, but only for non-UV VBs
            if (vbOffsets != null && vbOffsets.Length > 0)
            {
                for (int i = 0; i < vbOffsets.Length && i < vbufs.Count; i++)
                {
                    if (vbOffsets[i] == vertexFileOffset && IsPosCandidate(i))
                    {
                        Log($"[VB-PICK] vertexFileOffset=0x{vertexFileOffset:X}, exact match -> VB {i} (Stride={vbufs[i].Stride}, Fvf=0x{vbufs[i].Fvf:X})");
                        return i;
                    }
                }

                // 2) Fallback: last VB whose offset is <= vertexFileOffset, still only non-UV
                uint bestOffset = 0;
                int bestIndex = -1;

                for (int i = 0; i < vbOffsets.Length && i < vbufs.Count; i++)
                {
                    uint ofs = vbOffsets[i];
                    if (ofs <= vertexFileOffset && ofs >= bestOffset && IsPosCandidate(i))
                    {
                        bestOffset = ofs;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    Log($"[VB-PICK] vertexFileOffset=0x{vertexFileOffset:X}, segment match -> VB {bestIndex} (Stride={vbufs[bestIndex].Stride}, Fvf=0x{vbufs[bestIndex].Fvf:X})");
                    return bestIndex;
                }
            }

            // 3) Fallback: VB0 if it looks like a position stream
            if (IsPosCandidate(idxVb0))
            {
                Log($"[VB-PICK] Fallback to VB0={idxVb0} (Stride={vbufs[idxVb0].Stride}, Fvf=0x{vbufs[idxVb0].Fvf:X})");
                return idxVb0;
            }

            // 4) Final fallback: first stride-12 (or first non-UV if no stride-12)
            for (int i = 0; i < vbufs.Count; i++)
            {
                if (IsPosCandidate(i))
                {
                    Log($"[VB-PICK] Fallback to first position-shaped VB={i} (Stride={vbufs[i].Stride}, Fvf=0x{vbufs[i].Fvf:X})");
                    return i;
                }
            }

            // 5) Absolute last resort: VB0 even if it's UV, just so we don't crash
            Log("[VB-PICK] WARNING: no position-shaped VB found, forcing VB0");
            return (idxVb0 >= 0 && idxVb0 < vbufs.Count) ? idxVb0 : 0;
        }

        // =====================================================================
        // PUBLIC ENTRY POINTS
        // =====================================================================

        public static Model3DGroup LoadFolder(string inputPath)
        {
            string folder;

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                folder = Directory.GetCurrentDirectory();
            }
            else if (File.Exists(inputPath))
            {
                folder = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
            }
            else
            {
                folder = inputPath;
            }

            string logFile = Path.Combine(folder, "F1_ClassicLoader_Log.txt");
            return LoadFolder(inputPath, logFile);
        }

        /// <summary>
        /// Explicit-LOD entry point used by the glTF exporter to iterate
        /// every LOD present in the extracted IDF cache. A 2018 McLaren
        /// cache typically contains descriptors for LODs 0..7; the exporter
        /// calls this once per LOD and writes the result to
        /// <c>{team}_LOD{N}.gltf</c>.
        ///
        /// Implementation is a thin wrapper around <see cref="LoadFolder(string,string)"/>
        /// that sets <see cref="_requestedExplicitLod"/> so the loader's
        /// normal "pick highest-detail LOD" heuristic is bypassed in
        /// favour of the caller's explicit request. The override is
        /// always cleared in a <c>finally</c> so a throw inside the load
        /// can't leak it into a subsequent call.
        /// </summary>
        public static Model3DGroup LoadFolderExplicitLod(string inputPath, int lod, string logFile)
        {
            _requestedExplicitLod = lod;
            try
            {
                return LoadFolder(inputPath, logFile);
            }
            finally
            {
                _requestedExplicitLod = null;
            }
        }

        public static Model3DGroup LoadFolder(string inputPath, string logFile)
        {
            ResetLog(logFile);
            ResetLog(logFile);
            Log("==================================================");
            Log("F1ModelLoaderClassic.LoadFolder");
            Log($"Input: {inputPath}");

            // Reset any carryover texture-cache state so two sequential
            // LoadFolder calls (e.g. user picks mclaren.erp then renault.erp)
            // don't render the second car with the first car's textures.
            // Callers that want to inject a custom cache can do so AFTER
            // LoadFolder via SetTextureCache (for a manual re-render) or
            // before LoadFolder (in which case AutoConfigureTextureCache
            // below will honour it -- the clear here only wipes stale
            // state left over from a previous LoadFolder).
            _textureCache = null;
            _detectedPackageName = null;
            _activeContext = null;

            // Drop any cached high-resolution brushes from a prior load
            // so a fresh ERP doesn't keep brushes whose textures don't
            // exist for this car.
            HighResBrushProvider.Clear();

            var group = new Model3DGroup();

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                Log("ERROR: inputPath is null/empty.");
                return group;
            }

            bool isFile = File.Exists(inputPath);
            string folder;

            if (isFile)
            {
                folder = Path.GetDirectoryName(inputPath)!;
            }
            else if (Directory.Exists(inputPath))
            {
                folder = inputPath;
            }
            else
            {
                folder = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
            }

            if (!Directory.Exists(folder))
            {
                Log($"ERROR: Folder does not exist: {folder}");
                return group;
            }

            // Self-heal: redirect from a thin / wrong cache subfolder to
            // the richest teams/<X>/idf sibling. The extractor's picker
            // sometimes lands on shared/tyres/idf or shared/generics/idf
            // for classic content (f1_classic_0[123]_vehicle_package),
            // which carries only a handful of shared resources -- the
            // actual car body geometry is at teams/<team>/idf and goes
            // unused. F1 2018 stock-McLaren picks teams/mclaren/idf
            // correctly because its tyres aren't bundled into the same
            // ERP, so the redirect is a no-op for that case.
            //
            // NOTE (2026-04-29): the previous comment claimed F1 2018
            // stock tyres weren't bundled. That's incorrect for the
            // f1_classic_02_vehicle_package cars (e.g. c_mclaren_98) --
            // those ERPs DO bundle a c_07_tyres model under
            // shared/tyres/idf. The redirect is still correct (we want
            // the body loaded from teams/<X>/idf), but we now preserve
            // `folder` before redirect into `originalFolder` so the
            // tyre-load step at the end of LoadFolder can find the
            // bundled tyres regardless of whether the picker chose the
            // tyres folder, the body folder, or any other entry point.
            //
            // Algorithm: walk up at most 6 levels from `folder` looking
            // for a directory containing a "teams" subfolder (the cache
            // root). If found, enumerate teams/*/idf and pick the one
            // with the most *temp000*mesh* descriptors. If that count
            // exceeds the current folder's count, redirect.
            string originalFolder = folder;
            string redirected = TryRedirectToRichestTeamIdfFolder(folder);
            if (!string.Equals(redirected, folder, StringComparison.OrdinalIgnoreCase))
            {
                Log($"LoadFolder: SELF-HEAL redirect '{folder}' -> '{redirected}' " +
                    $"(richer teams/<X>/idf folder found in cache).");
                folder = redirected;
            }

            // Publish the resolved folder so MainWindow's assembly toggle
            // can find the body's render file even when the user's input
            // path pointed at a tyre/wep/secondary folder. Without this
            // the assembler walks the wrong folder, finds only the
            // 29-node tyre render file (zero kind=2 renderable nodes),
            // and leaves every body part at identity (origin overlap).
            LastResolvedFolder = folder;

            // Sniff one filename in the resolved folder to determine
            // whether the extractor used the stock 2018 URL-encoded
            // convention ("!!!" + "%3F") or the classic underscore
            // convention ("___" + "_3F"). Sets the static cache-
            // convention fields used by BuildSuffixFromName /
            // LoadBufVertex / LoadBufIndex below.
            DetectCacheConvention(folder);

            // Auto-configure the texture pipeline for F1 2018. Mirrors the
            // 2021+ F1ModelLoader.AutoConfigure behaviour: find a package
            // root by walking up from the idf folder, detect the ERP's
            // package name from descriptor bytes, and construct a
            // TextureCache rooted there. On success, the per-submesh loop
            // in BuildGeometryFromSubMeshes will resolve each slot's
            // material file and hand us back a decoded ImageBrush; on
            // failure the submesh falls back to the neutral gray
            // DiffuseMaterial (the previous behaviour).
            AutoConfigureTextureCache(folder);

            Log($"Scanning folder for CLASSIC mesh descriptors (temp000*mesh*) in: {folder}");
            string[] allMeshFiles = Directory.GetFiles(folder, "*temp000*mesh*", SearchOption.TopDirectoryOnly);

            if (allMeshFiles.Length == 0)
            {
                Log("ERROR: No temp000 mesh files (*temp000*mesh*) found in folder.");

                // Better diagnostic: list what IS in the folder so the next
                // debug round has data to work with. Common failure modes
                // this surfaces:
                //   * User pointed at a wep-only ERP cache (only .layer /
                //     .wep / GfxMaterialRes resources, no geometry).
                //   * User pointed at a folder one level above the cache.
                //   * Extractor used a different filename convention that
                //     doesn't contain the literal "temp000" or "mesh"
                //     tokens we glob for.
                try
                {
                    string[] entries = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly);
                    Log($"  Folder contains {entries.Length} file(s). Sampling up to 30:");
                    int shown = 0;
                    foreach (var entry in entries)
                    {
                        if (shown >= 30) break;
                        Log($"    {Path.GetFileName(entry)}");
                        shown++;
                    }
                    if (entries.Length > 30)
                        Log($"    ... and {entries.Length - 30} more.");

                    // Hint detection: does this look like a wep-only cache?
                    int wepish = 0, idfish = 0;
                    foreach (var entry in entries)
                    {
                        string nm = Path.GetFileName(entry).ToLowerInvariant();
                        if (nm.Contains(".layer") || nm.Contains(".wep")) wepish++;
                        if (nm.Contains("temp000") && nm.Contains("mesh")) idfish++;
                    }
                    if (wepish > 0 && idfish == 0)
                    {
                        Log("  HINT: folder contains .layer / .wep resources but no mesh " +
                            "descriptors. This looks like a WEP scene cache. Verify the " +
                            "ERP you loaded is the geometry package (often the same " +
                            "filename in a sibling folder like /idf/) -- or, if your " +
                            "package bundles geometry into the wep ERP, verify the " +
                            "extractor unpacked all 613+ resources here (mesh resources " +
                            "may appear past the first 50 in the resource table).");
                    }
                }
                catch (Exception ex)
                {
                    Log($"  (Folder enumeration threw: {ex.Message})");
                }
                return group;
            }

            Array.Sort(allMeshFiles, StringComparer.OrdinalIgnoreCase);
            Log($"Found {allMeshFiles.Length} classic mesh descriptor file(s) (all LODs).");

            // LOD selection – same idea as modern loader
            StringComparison cmp = StringComparison.OrdinalIgnoreCase;

            string[] meshFilesToUse;

            if (_requestedExplicitLod.HasValue)
            {
                // Explicit-LOD path — caller is the glTF exporter asking
                // for a specific {team}_LOD{N}.gltf output. Restrict to
                // descriptors whose filename contains "lod={requested}".
                // If none match we return an empty group rather than
                // silently falling through to lod=0: writing lod=0 data
                // into a file named "_LOD{N}.gltf" for N!=0 would be a
                // worse outcome than an empty LOD file that the exporter
                // can flag.
                int reqLod = _requestedExplicitLod.Value;
                string tag = "lod=" + reqLod.ToString(System.Globalization.CultureInfo.InvariantCulture);
                meshFilesToUse = allMeshFiles.Where(f => f.IndexOf(tag, cmp) >= 0).ToArray();
                if (meshFilesToUse.Length > 0)
                {
                    Log($"LOD selection: EXPLICIT lod={reqLod} -> using {meshFilesToUse.Length} descriptor(s).");
                }
                else
                {
                    Log($"LOD selection: EXPLICIT lod={reqLod} requested but NO matching descriptors in this cache; returning empty group.");
                    return group;
                }
            }
            else
            {
                var lod0Files = allMeshFiles.Where(f => f.IndexOf("lod=0", cmp) >= 0).ToArray();
                meshFilesToUse = allMeshFiles;

                if (lod0Files.Length > 0)
                {
                    meshFilesToUse = lod0Files;
                    Log($"LOD selection: Using {meshFilesToUse.Length} file(s) with 'lod=0' (highest detail).");
                }
                else
                {
                    var lodAllFiles = allMeshFiles.Where(f => f.IndexOf("lod=all", cmp) >= 0).ToArray();
                    if (lodAllFiles.Length > 0)
                    {
                        meshFilesToUse = lodAllFiles;
                        Log($"LOD selection: No 'lod=0' files. Using {meshFilesToUse.Length} file(s) with 'lod=all'.");
                    }
                    else
                    {
                        var withParsedLod = allMeshFiles
                            .Select(f => new { File = f, Lod = ExtractLodValueFromPath(f) })
                            .Where(x => x.Lod.HasValue)
                            .ToArray();

                        if (withParsedLod.Length > 0)
                        {
                            int minLod = withParsedLod.Min(x => x.Lod.Value);
                            meshFilesToUse = withParsedLod
                                .Where(x => x.Lod.Value == minLod)
                                .Select(x => x.File)
                                .ToArray();

                            Log($"LOD selection: Using {meshFilesToUse.Length} file(s) with smallest lod={minLod}.");
                        }
                        else
                        {
                            meshFilesToUse = allMeshFiles;
                            Log("LOD selection: No 'lod=' pattern found in filenames. Using ALL mesh files.");
                        }
                    }
                }
            }

            Log($"Final classic mesh descriptor count after LOD filter: {meshFilesToUse.Length}");

            int totalParts = 0;

            foreach (var meshFile in meshFilesToUse)
            {
                Log("--------------------------------------------------");
                Log($"[CLASSIC] Loading mesh descriptor: {meshFile}");

                // Publish the current descriptor's path + parsed part name
                // so the deep-nested submesh-registration code can pick
                // them up without us having to thread them as parameters
                // through half a dozen intermediate calls. The outer try
                // / finally below guarantees we clear these even when a
                // descriptor load throws, so a malformed file can't leak
                // its path into the next iteration.
                _currentDescriptorPath = meshFile;
                _currentDescriptorPartName = ExtractPartNameFromDescriptorFilename(meshFile);

                // Root/union descriptor loads normally, same as every
                // other descriptor in this folder. Its 3 submeshes
                // ('carbon tile 1', 'generic', 'number') become regular
                // entries in MeshSubmeshRegistry keyed by their slot
                // name, and the glTF exporter's slot-name fallback tier
                // surfaces them as top-level nodes named after the slot.
                // No bbox splitting, no post-loop partitioning, no
                // special-case routing -- the carbon material is loaded
                // the same way paint / generic / wheel materials are.
                if (string.IsNullOrEmpty(_currentDescriptorPartName))
                {
                    Log($"[CLASSIC] Descriptor has no part name in filename; per-submesh slot names will be used.");
                }
                else
                {
                    Log($"[CLASSIC] Descriptor part name: '{_currentDescriptorPartName}'");
                }

                // Skip rain-effect (_water) descriptors entirely. F1 2018 ships a
                // parallel set of descriptors -- safetycell_water, nosecone_water,
                // frontflap_fl_water, frontwing_fr_01_water, driver_surround_water,
                // etc. -- that carry "rainbeads" submeshes used by the wet-weather
                // shader to draw water droplets on the bodywork. In-game they're
                // only visible when it's raining; in a static preview they
                // overlay the dry body with a second layer of geometry that
                // reads the same bbox but samples a rainbead texture and
                // adds nothing useful. Drop the descriptor here before parse,
                // same shape as how we drop individual carbon mesh[i] records
                // in the 2018 parse loop: cheaper than letting the parse run
                // to completion and then filtering downstream.
                if (meshFile.IndexOf("_water", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("  -> skipping _water descriptor (rainbeads layer; hidden in preview)");
                    continue;
                }

                try
                {
                    var partGroup = LoadLegacyMeshDescriptor(meshFile);
                    if (partGroup == null)
                    {
                        Log("  -> LoadLegacyMeshDescriptor returned null for this descriptor.");
                        continue;
                    }

                    foreach (var child in partGroup.Children)
                    {
                        group.Children.Add(child);
                        totalParts++;
                    }

                    Log($"  -> Added {partGroup.Children.Count} classic submesh(es) from this descriptor.");
                }
                catch (Exception ex)
                {
                    Log($"EXCEPTION while loading classic '{meshFile}':");
                    Log(ex.ToString());
                }
                finally
                {
                    // Always clear so a malformed descriptor can't leak
                    // its name into the next iteration's submesh-
                    // registration context.
                    _currentDescriptorPath = null;
                    _currentDescriptorPartName = null;
                }
            }

            Log($"TOTAL classic parts added across all mesh descriptors (highest LOD only): {totalParts}");

            if (totalParts == 0)
            {
                Log("WARNING: No classic geometry was added. Check paths / headers / data files.");
            }
            else
            {
                Log("Classic model successfully built from ALL highest-LOD mesh descriptors in folder.");
            }

            // -------------------------------------------------------------
            // Classic-tyres pass.
            //
            // Modern F1 cars (2017+) reference a SHARED tyre model from the
            // global tyre package via cross-package eaid:// URIs, so their
            // tyres aren't in the per-car ERP at all. Classic ERPs
            // (f1_classic_0[123]_vehicle_package) are different: each ERP
            // bundles its own era-specific tyre model -- e.g. the 1998
            // McLaren ERP includes c_07_tyres under shared/tyres/idf with
            // its own LODs (0..6), 2 vertex buffers, and 1 index buffer.
            //
            // The body load above (teams/<X>/idf) doesn't touch this
            // bundled tyre model, so without this pass the assembled car
            // has bare wheel hubs and no rubber on them. We prompt the
            // user before loading because the tyres are visually distinct
            // from the body and some users may prefer a body-only preview
            // (e.g. when checking sponsor decals on the chassis without
            // the wheels in the way).
            //
            // Only runs for classic content -- this whole file is the
            // classic loader, so we're already in the right place. The
            // hybrid dispatcher in F1ModelLoader.LoadFolder forwards to
            // F1ModelLoaderClassic only when classic content is detected,
            // so a modern (NEFX2) load never reaches this pass.
            if (totalParts > 0)
            {
                try
                {
                    TryLoadClassicTyres(folder, originalFolder, group);
                }
                catch (Exception ex)
                {
                    Log("EXCEPTION in TryLoadClassicTyres (body load already succeeded; " +
                        "tyre load failed):");
                    Log(ex.ToString());
                }
            }
            else
            {
                Log("Skipping tyre-load pass because the body load produced 0 parts " +
                    "(no point asking the user to add tyres to a car with no body).");
            }

            Log("==================================================");
            Log("[CLASSIC GLOBAL STATS] (across this LoadFolder call)");
            Log($"    Total submeshes  : {_totalSubmeshes}");
            Log($"    Total indices    : {_totalIndices}");
            Log($"    Total triangles  : {_totalTriangles}");
            Log($"    Total vertices   : {_totalVertices}  (per-submesh counts summed; not de-duplicated across submeshes)");
            Log("==================================================");

            return group;
        }


        // =====================================================================
        // CORE: CLASSIC / LEGACY MESH DESCRIPTOR
        //
        //  - For F1 2018 there is a 9 * 4-byte header, then meshes[numMeshes],
        //    each 88 bytes, plus VB offset table and string blocks.
        //  - For non-2018 we fall back to using the header's global index range.
        // =====================================================================

        private static Model3DGroup LoadLegacyMeshDescriptor(string meshTemp000Path)
        {
            Log($"LoadLegacyMeshDescriptor: {meshTemp000Path}");

            if (!File.Exists(meshTemp000Path)) return null;

            string folder = Path.GetDirectoryName(meshTemp000Path)!;
            string meshFileName = Path.GetFileName(meshTemp000Path);
            string teamPrefix;
            if (meshFileName.Contains("!!!"))
            {
                // Stock 2018 convention: "<team>!!!temp000.idf%3F...&mesh".
                teamPrefix = meshFileName.Substring(0, meshFileName.IndexOf("!!!", StringComparison.Ordinal));
            }
            else if (meshFileName.Contains("___"))
            {
                // Classic content (f1_classic_0[123]_vehicle_package)
                // convention: "<team>___temp000.idf_3Flod_0_<part>_mesh".
                // Same shape as stock, just different separator and
                // URL-encoding style. Team prefix is everything before
                // the first "___".
                teamPrefix = meshFileName.Substring(0, meshFileName.IndexOf("___", StringComparison.Ordinal));
                Log($"teamPrefix derivation: '{teamPrefix}' " +
                    $"(classic '___' separator in descriptor filename '{meshFileName}')");
            }
            else
            {
                // No infix marker. Walk the parent path looking for
                // ".../teams/<X>/...". For the standard cache layout
                // (".../<package>/teams/c_mclaren_98/idf/<descriptor>"),
                // <X> = "c_mclaren_98". Falls back to "mclaren" as last
                // resort to preserve previous behaviour for unusual
                // extractor outputs.
                teamPrefix = TryDeriveTeamPrefixFromPath(folder) ?? "mclaren";
                Log($"teamPrefix derivation: '{teamPrefix}' " +
                    $"(no separator in descriptor filename '{meshFileName}', " +
                    $"derived from parent path)");
            }
            // Is this one of the safetycell IDFs?
            bool isSafetyCellDescriptor =
                meshFileName.Contains("safetycell", StringComparison.OrdinalIgnoreCase) ||
                meshFileName.Contains("safetycell_water", StringComparison.OrdinalIgnoreCase);

            bool is2018 = meshTemp000Path.IndexOf("2018", StringComparison.OrdinalIgnoreCase) >= 0;

            byte[] bytes = File.ReadAllBytes(meshTemp000Path);
            if (bytes.Length < 19 * 4) return new Model3DGroup();


            // --------------------------------------------------------------
            // ROBUST 2018-LAYOUT DETECTION
            // --------------------------------------------------------------
            //
            // The old path-only check above worked when the ERP extractor
            // wrote files into a folder tree like
            // ".../f1_2018_vehicle_package/..." -- the year string then sat
            // in the mesh descriptor's absolute path. The current extractor
            // writes a hashed cache name, e.g.
            // ".../erp_cache/mclaren_E294515E15130D52/teams/mclaren/idf/...",
            // which contains NO year substring, so path-only detection
            // always returns false for 2018 ERPs. That collapses the
            // per-descriptor logic into the non-2018 fallback path below
            // (single "LegacyMesh" submesh, hardcoded VOffset=0), dropping
            // the secondary mesh records that carry the rest of the part
            // AND ignoring the non-zero vertexOffsetIndex values on
            // descriptors like hal/safetycell/wing_mirror/rearwing_top.
            //
            // The mesh descriptor ALWAYS contains a VB name table with
            // full EAIDs like
            // "eaid://f1_2018_vehicle_package/teams/mclaren/idf/mclaren.idf
            //  ?lod=all&vb=0"
            // so scanning the descriptor bytes for "f1_2018" is a reliable
            // year marker that survives any filesystem-cache scheme. We
            // also keep the path check as an additional signal so anything
            // that used to trigger on path still triggers.
            //
            // 2016 and 2017 ERPs use a DIFFERENT internal layout (the old
            // "LegacyMesh" path was tuned for them), so we must NOT enable
            // the 2018 branch for them. They get their own future
            // treatment if needed.
            bool pathHas2018 = meshTemp000Path.IndexOf("2018", StringComparison.OrdinalIgnoreCase) >= 0;
            bool contentHasF12018 = DescriptorContainsAscii(bytes, "f1_2018_vehicle_package");

            // F1 2018's classic content (separate asset groups
            // f1_classic_01/02/03_vehicle_package) ships car geometry
            // with the SAME 9-int header / 88-byte mesh-record / VB-
            // offset-table layout as 2018 stock. Different package
            // name, identical descriptor binary format. Without these
            // markers, classic-car descriptors fall through to the
            // 2016/2017-style legacy fallback parser (single
            // LegacyMesh per descriptor, hardcoded VOffset=0) which
            // silently produces broken geometry.
            //
            // The "is2018" flag here is really "is modern-NEFX1
            // 9-int-header layout", not literally "is the 2018 stock
            // package". Setting it for any modern-classic-family
            // packaging is correct -- the per-descriptor decision is
            // driven by the descriptor's binary `Type=0x109` magic,
            // which is what the loader actually depends on.
            //
            // v12.06: F1 2020 ships pre-2018-chassis cars (Benetton 94,
            // McLaren 98, Ferrari 76, etc. -- the F1 2020 game's
            // classic content, distinct from the F1 2018 game's
            // classic content) under a new bare unnumbered
            // packaging: "f1_classic_vehicle_package" (no _NN_ infix,
            // no year prefix). The VB headers in those descriptors
            // emit `Type=0x109` -- the same modern-NEFX1 magic this
            // parser branch already handles for the F1 2018 numbered
            // classics -- so adding the F1 2020 substring to the
            // marker check below routes the descriptors through the
            // existing 9-int parser. The fix does NOT assume any
            // deeper format equivalence with the F1 2018 classics;
            // it only asserts that the VB header magic is what the
            // 9-int parser branch expects. If F1 2020 classic content
            // turns out to have parser-relevant differences in
            // submesh-record fields, those would be handled by
            // additional refinements inside that branch, not by
            // changing this dispatch.
            bool contentHasF1Classic =
                DescriptorContainsAscii(bytes, "f1_classic_01_vehicle_package") ||
                DescriptorContainsAscii(bytes, "f1_classic_02_vehicle_package") ||
                DescriptorContainsAscii(bytes, "f1_classic_03_vehicle_package") ||
                DescriptorContainsAscii(bytes, "f1_classic_vehicle_package");

            // v12.12 (beta): structural 36-byte-header detection.
            //
            // The string-marker checks above ONLY work when the descriptor
            // bytes contain a recognisable "f1_classic_*_vehicle_package"
            // or "f1_2018_vehicle_package" substring inside its embedded
            // EAID name table. The F1 2019 1990 Ferrari case (and very
            // likely other F1 2019 classic-expansion cars) ships
            // descriptors whose EAID strings reference a package name
            // that does NOT contain either of the above tokens -- the
            // raw VB names show up in the log truncated to
            //   "eams/c_ferrari_90/idf/c_ferrari_90.idf?lod=all&vb=N"
            // which is the trailing portion of an EAID whose package-
            // prefix is something other than the F1 2018-era names the
            // existing whitelist knows about. The earlier v12.11 attempt
            // to enumerate more candidate package-name strings was
            // ineffective for exactly this reason: there is no package
            // name in the bytes to scan FOR.
            //
            // What IS reliable on these descriptors is the BINARY HEADER
            // SHAPE. F1 stock-2018 descriptors carry a 19-int (76-byte)
            // header; F1 classic / F1 2019-expansion descriptors carry a
            // 9-int (36-byte) header. The existing classic-content
            // branch a few lines below already knows how to parse the
            // 36-byte form -- it just isn't being entered because its
            // gate condition is contentHasF1Classic, which the string
            // scan failed to set.
            //
            // The structural detector below reads the first 9 ints AS IF
            // the descriptor were a 36-byte header, sanity-checks the
            // counts and offsets, and verifies that the first VB name
            // string sits exactly where a 36-byte header would put it
            // AND is a plausible EAID (printable ASCII, contains "vb="
            // or ".idf"). For a real stock-2018 descriptor, that probe
            // position lands 40 bytes ahead of the correct name offset
            // -- almost always in non-printable binary -- so this check
            // is heavily biased against false positives on stock 2018.
            //
            // On a positive match we promote contentHasF1Classic to true.
            // That feeds the existing is2018-true gate just below AND
            // the existing classic-content branch at line ~3486, both
            // unchanged. Nothing in this file's previous decision graph
            // is rewritten -- we're just supplying the trigger signal
            // via a second, structural route.
            if (!contentHasF12018 && !contentHasF1Classic &&
                LooksLikeClassic36ByteHeader(bytes))
            {
                Log("v12.12-struct: descriptor bytes have no f1_2018 / " +
                    "f1_classic_* marker, but the 9-int header at offset 0 " +
                    "parses cleanly AND the first VB name at " +
                    "(36 + vbufOffset) reads as a plausible EAID. " +
                    "Promoting contentHasF1Classic=true so the existing " +
                    "36-byte classic-content reader handles this " +
                    "descriptor (fixes F1 2019 1990 Ferrari and similar " +
                    "classic-expansion content whose EAID package prefix " +
                    "is outside the string-marker whitelist).");
                contentHasF1Classic = true;
            }

            if (!is2018 && (contentHasF12018 || contentHasF1Classic))
                is2018 = true;

            if (!is2018)
            {
                // Additional short-form markers that have appeared in the
                // wild for 2018 ERPs: the package-less EAID "f1_2018/"
                // and "vehicle_package_2018".
                if (DescriptorContainsAscii(bytes, "f1_2018/") ||
                    DescriptorContainsAscii(bytes, "vehicle_package_2018"))
                {
                    is2018 = true;
                }
            }

            // v12.11 (beta): extended classic-package marker pass.
            //
            // The hardcoded whitelist above covers F1 2018 stock plus the
            // three F1 2018 numbered classic packages (_01/_02/_03), plus
            // F1 2020's bare unnumbered f1_classic_vehicle_package. That
            // misses the F1 2019 classic expansion (Ferrari 1990, Williams
            // 1992, McLaren 1991, etc.), whose descriptors reference
            // higher-numbered classic packages -- the 1990 Ferrari case
            // produces an all-False is2018 detection and the descriptor
            // falls through to the 2016/2017-style legacy branch (single
            // 'LegacyMesh' submesh, hardcoded VOffset=0, no slot label
            // for material-EAID lookup -> every part renders gray and
            // shape-distorted because multiple 88-byte mesh records get
            // collapsed into one submesh that ignores per-record
            // vertexOffsetIndex).
            //
            // This pass runs ONLY when every preceding check has come
            // back negative, so descriptors that the existing whitelist
            // already matched retain their original code path. It scans
            // the same descriptor byte array using the existing
            // DescriptorContainsAscii helper -- no new pattern-matching
            // machinery, just a generalised candidate list -- and flips
            // is2018=true when any numbered classic package _04..30 or
            // any stock-year package 2019..2025 is found. The "30" /
            // "2025" upper bounds are generous headroom for future
            // titles; the existing detector handles 2018 and below.
            if (!is2018 && DescriptorContainsExtendedClassicMarker(bytes))
            {
                is2018 = true;
                Log("is2018 detection (v12.11-ext): extended marker scan " +
                    "matched a numbered classic _04..30 or stock-year " +
                    "2019..2025 vehicle_package in the descriptor bytes. " +
                    "Routing through the 9-int-header parser branch so " +
                    "per-record slot labels and VOffsets are honoured " +
                    "(fixes F1 2019 1990 Ferrari and similar).");
            }

            Log($"is2018 detection: path-has-2018={pathHas2018}, " +
                $"bytes-have-f1_2018={contentHasF12018}, " +
                $"bytes-have-f1_classic={contentHasF1Classic}, " +
                $"final is2018={is2018}");
            // =====================================================================
            // CLASSIC SECTION  (added 2026-04-28)
            // =====================================================================
            //
            // F1 2018's classic packages (f1_classic_01/02/03_vehicle_package)
            // share the modern 9-int-header / 88-byte-mesh-record / packed-VB-
            // naming binary layout with stock 2018, BUT classic root descriptors
            // trip two long-standing bugs in the legacy reader + 2018 branch
            // below that the stock 2018 path manages to dodge by accident:
            //
            //   1. THE OUTER READER USES LegacyHeaderSize = 19*4 = 76. Classic
            //      descriptors actually carry a 9-int (36-byte) header. VB[0]'s
            //      EAID name is read 40 bytes too late, truncating its first 40
            //      characters; IB names and labels are likewise shifted, which
            //      desynchronises the label list by one slot. Stock 2018 happens
            //      to recover because the resulting truncation still parses a
            //      valid suffix and the 2018 allowlist below mostly ignores
            //      labels[i>0] anyway -- but classic descriptors carry mirror_l,
            //      mirror_r, rainbeads at i=6/7/8 with vertexFileOffset > 360,
            //      and they DO get walked by the loop.
            //
            //   2. THE 2018 BRANCH DOES `ms2.Position = vbufOffset2018; for(...)
            //      vbOffsets[i] = ReadUInt32();`. Classic descriptors HAVE NO
            //      global VB offset table -- vbufOffset (=1320 for c_mclaren_98)
            //      points 52 bytes INTO mesh[N-1]'s record. The loader thus
            //      reads mesh[N-1]'s {unk11, vertexFileOffset, map0..map6} as a
            //      phantom offset table, then keeps reading ASCII bytes from
            //      the trailing EAID strings as further "offsets". The
            //      resulting vbOffsets[] contains only [0, 0, 90, 180, 270,
            //      360, 0, 0, 0, garbage...]. PickPositionVBufferIndex's
            //      segment-match falls back to "largest vbOffsets[i] <=
            //      vertexFileOffset", which routes mesh[6/7/8] (vertexFileOffset
            //      = 991, 991, 1082) to vbOffsets[5] = 360 -> VB index 5 (the
            //      body's stride-12 VB), instead of their actual stride-12
            //      buffers at indices 11 and 12. Result: mirror geometry and
            //      rainbeads geometry render with body vertex positions ->
            //      the "minor disfigurement" symptom on classic cars.
            //
            // The correct vbOffsets[] table is NEVER stored in the descriptor;
            // it has to be RECONSTRUCTED from the (vertexFileOffset, maps[])
            // fields of each mesh record. Each mesh record references nVBs
            // distinct offsets via vfo + maps[0..nVBs-2]. Collecting the union
            // across all mesh records, sorted + deduped, gives exactly numVBuf
            // entries -- the position in this sorted list IS the VB index.
            //
            // This block is ADDITIVE: it intercepts only descriptors whose body
            // proves classic content (and not stock 2018 content), runs its own
            // corrected read with own VB/IB/label lists at offset 36 instead of
            // 76, own reconstructed vbOffsets[], and returns directly. Stock
            // 2018 and 2016/2017 descriptors fall through to the existing
            // reader unchanged.
            if (contentHasF1Classic && !contentHasF12018)
            {
                using (var msC = new MemoryStream(bytes))
                using (var brC = new BinaryReader(msC))
                {
                    // 9-int header
                    int numMeshesC = brC.ReadInt32();   // [0]
                    int numVBufC = brC.ReadInt32();   // [1]
                    int numIBufC = brC.ReadInt32();   // [2]
                    int labelCountC = brC.ReadInt32();   // [3]
                    int numNodeLinksC = brC.ReadInt32();   // [4]
                    int vbufOffsetC = brC.ReadInt32();   // [5]
                    int ibufOffsetC = brC.ReadInt32();   // [6]
                    int labelOffsetC = brC.ReadInt32();   // [7]
                    int nodeLinkOffsetC = brC.ReadInt32();   // [8]

                    const int ClassicHeaderSize = 9 * 4; // 36 bytes
                    const int ClassicMeshRecordSize = 88;    // 22 fields, 4 bytes each

                    Log($"[classic] Header: meshes={numMeshesC}, vbufs={numVBufC}, " +
                        $"ibufs={numIBufC}, labels={labelCountC}, " +
                        $"vbufOff={vbufOffsetC}, ibufOff={ibufOffsetC}, " +
                        $"labelOff={labelOffsetC}");

                    // ---------------------------------------------------------
                    // PASS 1: parse all mesh records (we need their vfo +
                    // maps[] to reconstruct vbOffsets[] before we can walk
                    // them again to emit submeshes).
                    // ---------------------------------------------------------
                    var meshRecords = new List<(uint nVBs, uint firstIdx, uint idxCnt,
                                                 uint vOffIdx, uint vfo, uint nameIdx,
                                                 uint m0, uint m1, uint m2, uint m3,
                                                 uint m4, uint m5, uint m6)>();

                    for (int i = 0; i < numMeshesC; i++)
                    {
                        long off = ClassicHeaderSize + (long)i * ClassicMeshRecordSize;
                        if (off + ClassicMeshRecordSize > bytes.Length) break;
                        msC.Position = off;

                        uint mNVBs = brC.ReadUInt32(); // [0]
                        uint mUnk2 = brC.ReadUInt32(); // [1]
                        uint mUnk3 = brC.ReadUInt32(); // [2]
                        uint mFirstIdx = brC.ReadUInt32(); // [3]
                        uint mIdxCnt = brC.ReadUInt32(); // [4]
                        uint mVOffIdx = brC.ReadUInt32(); // [5]
                        float mCx = brC.ReadSingle(); // [6]
                        float mCy = brC.ReadSingle(); // [7]
                        float mCz = brC.ReadSingle(); // [8]
                        float mRad = brC.ReadSingle(); // [9]
                        uint mNodeOff = brC.ReadUInt32(); // [10]
                        uint mUnk9 = brC.ReadUInt32(); // [11]
                        uint mNameIdx = brC.ReadUInt32(); // [12]
                        uint mUnk11 = brC.ReadUInt32(); // [13]
                        uint mVfo = brC.ReadUInt32(); // [14] vertexFileOffset
                        uint mMap0 = brC.ReadUInt32(); // [15]
                        uint mMap1 = brC.ReadUInt32(); // [16]
                        uint mMap2 = brC.ReadUInt32(); // [17]
                        uint mMap3 = brC.ReadUInt32(); // [18]
                        uint mMap4 = brC.ReadUInt32(); // [19]
                        uint mMap5 = brC.ReadUInt32(); // [20]
                        uint mMap6 = brC.ReadUInt32(); // [21]

                        meshRecords.Add((mNVBs, mFirstIdx, mIdxCnt, mVOffIdx, mVfo, mNameIdx,
                                           mMap0, mMap1, mMap2, mMap3, mMap4, mMap5, mMap6));
                    }

                    // ---------------------------------------------------------
                    // RECONSTRUCT vbOffsets[] FROM MESH RECORDS.
                    //
                    // For mesh i with nVBs = N, the descriptor uses these N
                    // distinct VB offsets:  vfo + maps[0..N-2]. The remaining
                    // maps[N-1..6] are zero-padding and MUST NOT be added to
                    // the set (otherwise a phantom 0 entry would be inserted
                    // for any record where the real vfo is non-zero, and
                    // when the resulting array is indexed positionally that
                    // phantom would shift every subsequent VB index by one).
                    //
                    // Collecting the union across all mesh records, sorted +
                    // deduped, yields exactly numVBufC entries (verified for
                    // c_mclaren_98: 18 entries [0, 90, 180, 270, 360, 450,
                    // 540, 630, 720, 810, 900, 991, 1082, 1173, 1264, 1355,
                    // 1446, 1537] matching numVBuf=18 from the header).
                    // ---------------------------------------------------------
                    var ofsSet = new SortedSet<uint>();
                    foreach (var mr in meshRecords)
                    {
                        ofsSet.Add(mr.vfo);
                        int n = (int)mr.nVBs;
                        uint[] mapArr = { mr.m0, mr.m1, mr.m2, mr.m3, mr.m4, mr.m5, mr.m6 };
                        for (int k = 0; k < n - 1 && k < mapArr.Length; k++)
                            ofsSet.Add(mapArr[k]);
                    }
                    var vbOffsetsC = new uint[ofsSet.Count];
                    ofsSet.CopyTo(vbOffsetsC);

                    if (vbOffsetsC.Length != numVBufC)
                    {
                        Log($"  [classic] WARN: reconstructed vbOffsets length " +
                            $"{vbOffsetsC.Length} != header numVBufs {numVBufC}; " +
                            $"proceeding with reconstructed table.");
                    }
                    else
                    {
                        Log($"  [classic] Reconstructed vbOffsets[{numVBufC}] from " +
                            $"mesh records (spans {vbOffsetsC[0]}..{vbOffsetsC[vbOffsetsC.Length - 1]}).");
                    }

                    // ---------------------------------------------------------
                    // VBUFs -- read from the CORRECT 36-byte-header position.
                    // (The outer reader below uses 76 here, which on classic
                    // causes name #0 to be read 40 bytes mid-string.)
                    // ---------------------------------------------------------
                    var vbufsC = new List<VertexBufferInfo>();
                    int idxVb0C = -1;
                    msC.Position = ClassicHeaderSize + vbufOffsetC;
                    for (int i = 0; i < numVBufC; i++)
                    {
                        string name = ReadCString(brC);
                        string suffix = BuildSuffixFromName(name, teamPrefix);
                        var vInfo = LoadBufVertex(folder, teamPrefix, suffix);
                        vInfo.Name = name;
                        vbufsC.Add(vInfo);

                        if (name.Contains("vb=0", StringComparison.OrdinalIgnoreCase) ||
                            suffix.Contains("vb=0", StringComparison.OrdinalIgnoreCase))
                        {
                            idxVb0C = i;
                        }
                    }
                    if (idxVb0C == -1 && vbufsC.Count > 0) idxVb0C = 0;

                    // ---------------------------------------------------------
                    // IBUFs.
                    // ---------------------------------------------------------
                    var ibufsC = new List<IndexBufferInfo>();
                    msC.Position = ClassicHeaderSize + ibufOffsetC;
                    for (int i = 0; i < numIBufC; i++)
                    {
                        string name = ReadCString(brC);
                        string suffix = BuildSuffixFromName(name, teamPrefix);
                        var iInfo = LoadBufIndex(folder, teamPrefix, suffix);
                        iInfo.Name = name;
                        ibufsC.Add(iInfo);
                    }

                    // ---------------------------------------------------------
                    // Labels (seek explicitly via labelOffset rather than
                    // relying on the IB-name read leaving the cursor at the
                    // right spot -- belt-and-braces against label-table
                    // padding variants).
                    // ---------------------------------------------------------
                    var labelsC = new List<string>();
                    msC.Position = ClassicHeaderSize + labelOffsetC;
                    for (int i = 0; i < labelCountC; i++)
                        labelsC.Add(ReadCString(brC));

                    // ---------------------------------------------------------
                    // PASS 2: emit submeshes. Mirrors the body of the is2018
                    // branch below, but uses the reconstructed vbOffsets table
                    // and the correctly-loaded VB/IB/label lists.
                    // ---------------------------------------------------------
                    var subMeshesC = new List<SubMeshInfo>();
                    for (int i = 0; i < meshRecords.Count; i++)
                    {
                        var mr = meshRecords[i];
                        // CLASSIC ROOT-UNION ALLOWLIST.
                        //
                        // The 2018 branch's allowlist of {number, generic} is
                        // wrong for classic content because classic descriptors
                        // never use those labels. Their root-union-unique slots
                        // are instead {carbon 4, rainbeads, flag, lcd, digi
                        // revs, rear light, steering wheel} -- geometry that
                        // has NO per-part descriptor counterpart, so the only
                        // path it can reach the export is through the root
                        // union. Filtering it out (as the 2018 list does)
                        // drops the rain-bead surface, the cockpit LCD/rev
                        // counter, the rear/brake light, the driver-number
                        // flag area, and the secondary carbon detailing. The
                        // diag's "RootUnionRedistribution: pseudoparts=1" /
                        // "dynamic carbon-bone set size=11 (static reference
                        // set size=21)" warnings are direct readouts of this
                        // over-filtering: only mesh[0] survived.
                        //
                        // Labels that DO duplicate per-part content (body,
                        // detail, carbon tile 1, glass, mirror left/right,
                        // wheel, disc) are still skipped here, because the
                        // duplicate-suppression in BuildGeometryFromSubMeshes
                        // only catches 'generic' and 'number' -- letting
                        // those through would produce overlapping geometry.
                        //
                        // 'number' and 'generic' are kept in the allowlist
                        // for forward-compatibility in case any classic
                        // descriptor variant ever ships with those labels.
                        //
                        // The guard below intentionally does NOT include
                        // an `i > 0` clause. An earlier version had one,
                        // which let mesh-record[0]'s slot bypass the
                        // allowlist completely. On c_mclaren_98 that
                        // record is the 'steering wheel' material slot
                        // (~23,397 tri / 95cm bbox -- the entire cockpit/
                        // interior batch the game lumps under that
                        // material for shader-batching reasons), so
                        // removing 'steering wheel' from the allowlist
                        // alone had no effect: the guard never let the
                        // allowlist run for record 0. Verified against
                        // the parsed root descriptor:
                        //   record[0].nameIdx -> 'steering wheel'
                        //   record[0].firstIdx=0, idxCnt=70191
                        // The allowlist now applies uniformly across
                        // i = 0..numMeshes-1.
                        //
                        // EXCEPTION (2026-04-29): when we're parsing a
                        // classic tyre descriptor on behalf of the
                        // tyre-load pass (TryLoadClassicTyres ->
                        // LoadLegacyMeshDescriptor), the entire allowlist
                        // is bypassed. Tyre descriptors are independent
                        // files (c_03_tyres etc.) with their own
                        // legitimate slot labels (sidewall, tread,
                        // rubber, ...) -- none of which match the
                        // body-specific allowlist below, so without this
                        // bypass every tyre slot was being filtered out
                        // and the parser emitted 0 submeshes from 8
                        // mesh records. The _inTyreLoadPass flag is
                        // scoped strictly to the tyre loop in
                        // TryLoadClassicTyres; body loads never see it
                        // set, so their behaviour is unchanged.
                        if (string.IsNullOrEmpty(_currentDescriptorPartName) &&
                            !_inTyreLoadPass)
                        {
                            string candidateName = (i < labelsC.Count)
                                ? labelsC[i].ToLowerInvariant()
                                : string.Empty;

                            bool allowed =
                                candidateName == "number" ||
                                candidateName == "generic" ||
                                // 'steering wheel' is intentionally NOT
                                // in this allowlist: the per-part
                                // 'steering_wheel' descriptor supplies the
                                // actual rim/spokes geometry (~326 tri,
                                // 6cm bbox). The root-union slot of the
                                // same name is the entire interior /
                                // cockpit batch (~23k tri, 95cm bbox)
                                // that takes its material from this slot
                                // for shader-batching reasons; emitting
                                // it from root paints the cockpit with
                                // the steering-wheel texture, which is
                                // visibly wrong. Skip it here.
                                candidateName == "carbon 4" ||
                                // 'rainbeads' is intentionally NOT in this
                                // allowlist: it's the wet-weather droplet
                                // surface and should never appear in the
                                // dry export. (The _water descriptor skip
                                // upstream already drops rainbeads carried
                                // by per-part rain layers; this entry kept
                                // it alive via the root union.)
                                candidateName == "flag" ||
                                candidateName == "lcd" ||
                                candidateName == "digi revs" ||
                                candidateName == "rear light";

                            if (!allowed)
                                continue;
                        }
                        // Same root-union allowlist as the is2018 branch:


                        int idxOffset = unchecked((int)mr.firstIdx);
                        int idxCount = unchecked((int)mr.idxCnt);
                        int vOffset = unchecked((int)mr.vOffIdx);
                        int ibufID = 0;

                        if (ibufID < 0 || ibufID >= ibufsC.Count)
                        {
                            Log($"  [classic/{i}] Skipping mesh: invalid ibufID={ibufID}");
                            continue;
                        }

                        int preferredVBuf = PickPositionVBufferIndex(
                            vbufsC,
                            vbOffsetsC,
                            mr.vfo,
                            idxVb0C,
                            mr.vOffIdx);

                        if (preferredVBuf < 0 || preferredVBuf >= vbufsC.Count)
                        {
                            Log($"  [classic/{i}] Skipping mesh: invalid VBuf={preferredVBuf}");
                            continue;
                        }

                        string name = (i < labelsC.Count) ? labelsC[i] : $"mesh_{i}";
                        var ibInfo = ibufsC[ibufID];
                        var vbInfo = vbufsC[preferredVBuf];

                        // ---------- INDEX RANGE SANITY ----------
                        if (idxOffset < 0) idxOffset = 0;
                        if (idxCount < 0) idxCount = 0;

                        if (ibInfo.Count > 0)
                        {
                            if (idxOffset >= ibInfo.Count)
                            {
                                Log($"  [classic/{name}] idxOffset {idxOffset} >= " +
                                    $"IB.Count {ibInfo.Count}, skipping mesh.");
                                continue;
                            }
                            if (idxOffset + idxCount > ibInfo.Count)
                            {
                                int maxCount = ibInfo.Count - idxOffset;
                                Log($"  [classic/{name}] Clamp indexCount {idxCount} " +
                                    $"-> {maxCount} (IB.Count={ibInfo.Count})");
                                idxCount = maxCount;
                            }
                        }

                        // ---------- VERTEX OFFSET SANITY ----------
                        if (vbInfo.Count > 0)
                        {
                            if (vOffset < 0 || vOffset >= vbInfo.Count)
                            {
                                Log($"  [classic/{name}] VOffset {vOffset} out of " +
                                    $"VB[{preferredVBuf}] range (Count={vbInfo.Count}), forcing 0.");
                                vOffset = 0;
                            }
                        }
                        else if (vOffset < 0)
                        {
                            vOffset = 0;
                        }

                        Log($"  [classic/{name}] VB={preferredVBuf}, IB={ibufID}, " +
                            $"IOffset={idxOffset}, IndexCount={idxCount}, VOffset={vOffset}, " +
                            $"vfo={mr.vfo}");

                        subMeshesC.Add(new SubMeshInfo
                        {
                            Name = name,
                            IOffset = idxOffset,
                            IndexCount = idxCount,
                            VOffset = vOffset,
                            VBufferIndex = preferredVBuf,
                            IBufferIndex = ibufID
                        });
                    }

                    Log($"  [classic] Emitted {subMeshesC.Count} submesh(es) from " +
                        $"{meshRecords.Count} mesh record(s); routing to BuildGeometryFromSubMeshes.");

                    return BuildGeometryFromSubMeshes(vbufsC, ibufsC, subMeshesC, teamPrefix);
                }
            }
            using (var ms = new MemoryStream(bytes))
            using (var br = new BinaryReader(ms))
            {
                // ---- Header ----
                int[] h = new int[19];
                for (int i = 0; i < 19; i++) h[i] = br.ReadInt32();

                int numMeshes = h[0];
                int numVBuf = h[1];
                int numIBuf = h[2];
                int labelCount = h[3];

                // These offsets are treated as relative to the end of this 19-int header.
                const int LegacyHeaderSize = 19 * 4;

                int vbufOfs = h[5];
                int ibufOfs = h[6];
                int headerIOffset = h[12];
                int headerIndexCount = h[13];

                // ---- VBUFs ----
                var vbufs = new List<VertexBufferInfo>();
                int idxVb0 = -1;
                int idxVb6 = -1; // track vb=6 for generic antennas / tech bits

                ms.Position = LegacyHeaderSize + vbufOfs;
                for (int i = 0; i < numVBuf; i++)
                {
                    string name = ReadCString(br);
                    string suffix = BuildSuffixFromName(name, teamPrefix);
                    var vInfo = LoadBufVertex(folder, teamPrefix, suffix);
                    vInfo.Name = name;
                    vbufs.Add(vInfo);

                    if (name.Contains("vb=0", StringComparison.OrdinalIgnoreCase) ||
                        suffix.Contains("vb=0", StringComparison.OrdinalIgnoreCase))
                    {
                        idxVb0 = i;
                    }

                    if (name.Contains("vb=6", StringComparison.OrdinalIgnoreCase) ||
                        suffix.Contains("vb=6", StringComparison.OrdinalIgnoreCase))
                    {
                        idxVb6 = i;
                    }
                }
                if (idxVb0 == -1 && vbufs.Count > 0)
                    idxVb0 = 0; // Fallback

                // ---- IBUFs ----
                var ibufs = new List<IndexBufferInfo>();

                ms.Position = LegacyHeaderSize + ibufOfs;
                for (int i = 0; i < numIBuf; i++)
                {
                    string name = ReadCString(br);
                    string suffix = BuildSuffixFromName(name, teamPrefix);
                    var iInfo = LoadBufIndex(folder, teamPrefix, suffix);
                    iInfo.Name = name;
                    ibufs.Add(iInfo);
                }

                // ---- Labels ----
                var labels = new List<string>();
                for (int i = 0; i < labelCount; i++)
                    labels.Add(ReadCString(br));

                var subMeshes = new List<SubMeshInfo>();

                if (is2018)
                {
                    // Proper 2018 layout:
                    // - real 9 * 4-byte header at offset 0 in the file
                    // - meshes[numMeshes], each 88 bytes, immediately after that header
                    // - VB offset table at vertexBufferOffset
                    using (var ms2 = new MemoryStream(bytes))
                    using (var br2 = new BinaryReader(ms2))
                    {
                        // 2018 header (override the generic one for this branch)
                        int numMeshes2018 = br2.ReadInt32();
                        int numVBuf2018 = br2.ReadInt32();
                        int numIBuf2018 = br2.ReadInt32();
                        int labelCount2018 = br2.ReadInt32();
                        int numNodeLinks = br2.ReadInt32();
                        int vbufOffset2018 = br2.ReadInt32();
                        int ibufOffset2018 = br2.ReadInt32();
                        int meshNameOffset2018 = br2.ReadInt32();
                        int nodeLinkOffset2018 = br2.ReadInt32();

                        // Sanity checks vs previously read header
                        if (numMeshes2018 != numMeshes)
                            Log($"  [2018] Header numMeshes mismatch: {numMeshes2018} vs {numMeshes}");
                        if (numVBuf2018 != numVBuf)
                            Log($"  [2018] Header numVBs mismatch: {numVBuf2018} vs {numVBuf}");
                        if (numIBuf2018 != numIBuf)
                            Log($"  [2018] Header numIBs mismatch: {numIBuf2018} vs {numIBuf}");

                        // VB offset table at vertexBufferOffset
                        var vbOffsets = new uint[numVBuf2018];
                        ms2.Position = vbufOffset2018;
                        for (int i = 0; i < numVBuf2018; i++)
                            vbOffsets[i] = br2.ReadUInt32();

                        const int MeshHeaderSizeBytes = 9 * 4; // 9-int header

                        for (int i = 0; i < numMeshes2018; i++)
                        {
                            // ---------------------------------------------
                            // MESH[0] + 'number' + 'generic' ALLOWLIST
                            // ---------------------------------------------
                            //
                            // Before the 2018 branch existed, the classic
                            // loader's non-2018 fallback emitted one
                            // submesh per descriptor, reading h[12] /
                            // h[13] out of the 19-int header as IOffset /
                            // IndexCount. Every descriptor I've checked
                            // has those two header fields equal to
                            // mesh[0].firstIndex / mesh[0].indexCount, so
                            // "the old loader's LegacyMesh" and
                            // "mesh[0] via the 2018 layout" are the same
                            // index range. Emitting ONLY mesh[0] by
                            // default reproduces that old-loader geometry
                            // with smooth-shaded normals on top.
                            //
                            // Beyond that baseline, the user asked for
                            // two specific material slots to also emit:
                            // the 'number' slot (driver-number geometry
                            // on the sides of safetycell and in the
                            // aggregate "mesh" descriptor) and the
                            // 'generic' slot (internal-frame / plastic
                            // bracket geometry found at non-mesh[0]
                            // positions in several descriptors --
                            // safetycell mesh[1], frontflap_* mesh[1],
                            // etc.). Everything else (carbon tiles,
                            // cockpit, detail, rear light, flag, lcd,
                            // digi revs, ...) stays skipped because
                            // those are local-space template geometry
                            // that clusters at origin without the
                            // runtime per-part transforms that the game
                            // applies.
                            //
                            // Note that when mesh[0] itself is 'number'
                            // or 'generic' this branch is a no-op -- the
                            // (i > 0) guard never fires for i == 0.
                            // ALLOWLIST IS ROOT-UNION-ONLY (2026-04-26).
                            // Per-part descriptors (safetycell, wing_mirror_l,
                            // steering_wheel, etc.) emit ALL their submeshes;
                            // the root-union (&mesh with no part name in the
                            // filename) retains the original mesh[0]+number+
                            // generic allowlist because its other slots are
                            // in bone-local space across multiple bones and
                            // would render at scene origin without the
                            // exporter-side bone-byte redistribution pass
                            // (which only handles 'carbon tile 1').
                            if (i > 0 && string.IsNullOrEmpty(_currentDescriptorPartName))
                            {
                                string candidateName = (i < labels.Count)
                                    ? labels[i].ToLowerInvariant()
                                    : string.Empty;
                                if (candidateName != "number" &&
                                    candidateName != "generic")
                                    continue;
                            }

                            long meshOffset = MeshHeaderSizeBytes + (long)i * 88;
                            if (meshOffset + 88 > bytes.Length)
                                break;

                            ms2.Position = meshOffset;

                            // First block of 6 uints
                            uint numVBsForMesh = br2.ReadUInt32();
                            uint unk2 = br2.ReadUInt32();
                            uint unk3 = br2.ReadUInt32();
                            uint firstIndex = br2.ReadUInt32();        // index offset
                            uint indexCount = br2.ReadUInt32();        // index count
                            uint vertexOffsetIndex = br2.ReadUInt32(); // base vertex index

                            // Bounding sphere
                            float cx = br2.ReadSingle();
                            float cy = br2.ReadSingle();
                            float cz = br2.ReadSingle();
                            float radius = br2.ReadSingle();

                            // Remaining ints
                            uint nodeLinkOffsetForMesh = br2.ReadUInt32();
                            uint unk9 = br2.ReadUInt32();
                            uint nameIndex = br2.ReadUInt32();
                            uint unk11 = br2.ReadUInt32();
                            uint vertexFileOffset = br2.ReadUInt32(); // maps to vbOffsets[]
                            uint map0 = br2.ReadUInt32();
                            uint map1 = br2.ReadUInt32();
                            uint map2 = br2.ReadUInt32();
                            uint map3 = br2.ReadUInt32();
                            uint map4 = br2.ReadUInt32();
                            uint map5 = br2.ReadUInt32();
                            uint map6 = br2.ReadUInt32();

                            int idxOffset = unchecked((int)firstIndex);
                            int idxCount = unchecked((int)indexCount);
                            int vOffset = unchecked((int)vertexOffsetIndex);

                            // Classic 2018 descriptors generally have a single IB
                            int ibufID = 0;
                            if (ibufID < 0 || ibufID >= ibufs.Count)
                            {
                                Log($"  [{i}] Skipping mesh: invalid ibufID={ibufID}");
                                continue;
                            }

                            // Choose VB exactly like the game data describes it:
                            // use vertexFileOffset + vbOffsets, with VB0 as fallback.
                            // Pass vertexOffsetIndex so the picker can reject
                            // stride-12 VBs whose Count is smaller than the base
                            // vertex this submesh needs to index from -- otherwise
                            // the downstream sanity clamp zeroes the offset and
                            // the submesh's triangles land on the wrong vertices
                            // (the 2018 paddle "carbon tile 4" regression).
                            int preferredVBuf = PickPositionVBufferIndex(
                                vbufs,
                                vbOffsets,
                                vertexFileOffset,
                                idxVb0,
                                vertexOffsetIndex);

                            if (preferredVBuf < 0 || preferredVBuf >= vbufs.Count)
                            {
                                Log($"  [{i}] Skipping mesh: invalid VBuf={preferredVBuf}");
                                continue;
                            }

                            string name = (i < labels.Count) ? labels[i] : $"mesh_{i}";
                            string lowerName = name.ToLowerInvariant();

                            // Note: an earlier revision had a
                            // "carbon-slot skip" here that dropped any
                            // mesh whose label started with "carbon".
                            // That block is now redundant (the
                            // mesh[0]-only guard above already handles
                            // the body-vs-carbon selection per
                            // descriptor) and was harmful for
                            // carbon-only parts where mesh[0] IS the
                            // carbon slot -- specifically tether_*,
                            // suspension*_*, sidewing_*, hub_caliper_bl/
                            // br, frontwing_fl_01 / fr_01, rearfin_bm,
                            // and the standalone "mesh" descriptor. For
                            // those 22 descriptors the skip block fired
                            // on i==0 and made their entire geometry
                            // disappear, which is why the user reported
                            // "the car partially loads" after the
                            // mesh[0]-only patch. Leaving this note
                            // instead of the dead block as a trailhead
                            // if someone wonders why those parts are
                            // now loading again.

                            // Previously this site had a "generic fix"
                            // that unconditionally re-routed every
                            // "generic"-named submesh to vb=6, on the
                            // theory that vb=6 was the antenna/tech-bit
                            // group. That was too aggressive: F1 2018
                            // descriptors also put legitimate "generic"
                            // submeshes on vb=0 (e.g. interior trim on
                            // the safetycell), and the force-reroute
                            // sourced their positions from vb=6's
                            // unrelated vertex data -- hence the "generic
                            // is out of place" symptom. The descriptor's
                            // VB choice is now trusted; submeshes the
                            // game authored on vb=6 still end up there
                            // because preferredVBuf already points at
                            // vb=6 for those records, no override needed.

                            if (isSafetyCellDescriptor &&
                                (lowerName.Contains("generic") || lowerName.Contains("carbon tile 1")))
                            {
                                Log($"  [safetycell/{name}] Using VB={preferredVBuf}, ibufID={ibufID}, " +
                                    $"idxOffset={idxOffset}, idxCount={idxCount}, vOffset={vOffset}");
                            }

                            var ibInfo = ibufs[ibufID];
                            var vbInfo = vbufs[preferredVBuf];

                            // ---------- INDEX RANGE SANITY ----------
                            if (idxOffset < 0) idxOffset = 0;
                            if (idxCount < 0) idxCount = 0;

                            if (ibInfo.Count > 0)
                            {
                                if (idxOffset >= ibInfo.Count)
                                {
                                    Log($"  [{name}] idxOffset {idxOffset} >= IB.Count {ibInfo.Count}, skipping mesh.");
                                    continue;
                                }

                                if (idxOffset + idxCount > ibInfo.Count)
                                {
                                    int maxCount = ibInfo.Count - idxOffset;
                                    Log($"  [{name}] Clamp indexCount {idxCount} -> {maxCount} (IB.Count={ibInfo.Count})");
                                    idxCount = maxCount;
                                }
                            }

                            // ---------- VERTEX OFFSET SANITY ----------
                            if (vbInfo.Count > 0)
                            {
                                if (vOffset < 0 || vOffset >= vbInfo.Count)
                                {
                                    Log($"  [{name}] VOffset {vOffset} out of VB[{preferredVBuf}] range (Count={vbInfo.Count}), forcing 0.");
                                    vOffset = 0;
                                }
                            }
                            else
                            {
                                if (vOffset < 0)
                                    vOffset = 0;
                            }

                            subMeshes.Add(new SubMeshInfo
                            {
                                Name = name,
                                IOffset = idxOffset,
                                IndexCount = idxCount,
                                VOffset = vOffset,
                                VBufferIndex = preferredVBuf,
                                IBufferIndex = ibufID
                            });
                        }
                    }
                }
                else
                {
                    // Non-2018: single big mesh from header range
                    if (headerIndexCount > 0)
                    {
                        subMeshes.Add(new SubMeshInfo
                        {
                            Name = "LegacyMesh",
                            IOffset = headerIOffset,
                            IndexCount = headerIndexCount,
                            VOffset = 0,
                            VBufferIndex = idxVb0,
                            IBufferIndex = 0
                        });
                    }
                }

                // Single exit point
                return BuildGeometryFromSubMeshes(vbufs, ibufs, subMeshes, teamPrefix);
            }
        }

        // =====================================================================
        // SHARED GEOMETRY BUILDER (positions only, from VBUF + IBUF)
        // =====================================================================

        private static Model3DGroup BuildGeometryFromSubMeshes(
            List<VertexBufferInfo> vbufs,
            List<IndexBufferInfo> ibufs,
            List<SubMeshInfo> subMeshes,
            string teamPrefix)
        {
            var group = new Model3DGroup();

            foreach (var sm in subMeshes)
            {
                if (sm.IndexCount <= 0) continue;
                if (sm.IBufferIndex < 0 || sm.IBufferIndex >= ibufs.Count) continue;
                if (sm.VBufferIndex < 0 || sm.VBufferIndex >= vbufs.Count) continue;

                // v12.10: skip the wet-weather rainbeads slot
                // unconditionally on every classic descriptor. The
                // upstream _water-descriptor filter at LoadFolderClassic
                // (~line 3117) already drops the F1 2018 classic
                // *_water descriptors that carry rainbeads as their
                // sole content; this submesh-level filter is the
                // safety net for F1 2020's bare classic packaging
                // (f1_classic_vehicle_package), which inlines a
                // "rainbeads" submesh inside the regular dry
                // descriptor instead of using a separate _water
                // file. Without this, F1 2020 classic Benetton 94 /
                // McLaren 98 / etc. would render a parasitic droplet
                // layer co-planar with the bodywork in the preview
                // and emit a duplicate rainbead surface in any
                // FBX/glTF export. Per user request (v12.10),
                // rainbeads NEVER loads on ANY model.
                if (string.Equals(sm.Name, "rainbeads", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"  -> Skipping submesh '{sm.Name}' (wet-weather " +
                        "rainbeads layer; unconditionally excluded).");
                    continue;
                }

                // Skip the root/union descriptor's 'generic' and 'number'
                // submeshes: cross-referencing each per-part descriptor's
                // IB ranges against the root/union's proved they cover the
                // identical geometry (6,359 tris of 'generic' in ROOT_UNION
                // = sum of 'generic' across per-part descriptors; 174 tris
                // of 'number' likewise). Loading both produces a duplicate
                // object in the output -- Blender surfaces it as a second
                // "generic" blob sitting on top of safetycell's own generic
                // submesh. Drop the root-union copy; the per-part
                // descriptors are the authoritative version that routes
                // each chunk of generic geometry to its proper per-part
                // node (safetycell keeps its interior panels, the discs
                // keep their brake-disc generics, etc.).
                //
                // 'carbon tile 1' is NOT duplicated like this -- the
                // root/union carries 54,207 tris of it while per-part
                // descriptors only total 10,664, so the root-union's
                // copy contributes 43k tris of main-body carbon that
                // exist nowhere else. That slot stays.
                //
                // Detection: the root/union descriptor is the one with
                // no part name in its filename, i.e.
                // _currentDescriptorPartName is null during its iteration.
                if (string.IsNullOrEmpty(_currentDescriptorPartName))
                {
                    if (string.Equals(sm.Name, "generic", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(sm.Name, "number", StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"  -> Skipping root/union '{sm.Name}' submesh (duplicates per-part descriptors' geometry).");
                        continue;
                    }
                }

                var ib = ibufs[sm.IBufferIndex];
                var vb = vbufs[sm.VBufferIndex];

                // Basic per-mesh header logging
                Log(
                    $"[SUBMESH] '{sm.Name}' " +
                    $"VB={sm.VBufferIndex}, IB={sm.IBufferIndex}, " +
                    $"IOffset={sm.IOffset}, IndexCount={sm.IndexCount}, " +
                    $"VOffset={sm.VOffset}");

                if (!File.Exists(ib.DataPath) || !File.Exists(vb.DataPath))
                {
                    Log($"  -> Skipping '{sm.Name}': VB/IB data file missing.");
                    continue;
                }

                byte[] iData = File.ReadAllBytes(ib.DataPath);
                byte[] vData = File.ReadAllBytes(vb.DataPath);

                // --- Read Indices ---
                var indices = new Int32Collection(sm.IndexCount);
                int minIndex = int.MaxValue;
                int maxIndex = -1;

                long iStart = (long)sm.IOffset * ib.IndexSize;
                long iEnd = iStart + (long)sm.IndexCount * ib.IndexSize;

                if (iEnd > iData.Length)
                {
                    Log(
                        $"Skipping {sm.Name}: Index OOB " +
                        $"(IOffset={sm.IOffset}, IndexCount={sm.IndexCount}, " +
                        $"IndexSize={ib.IndexSize}, IBytes={iData.Length}, " +
                        $"IB.Count={ib.Count})");
                    continue;
                }

                // FIRST PASS: scan the IB range to find min / max indices
                // referenced by this submesh. The second pass below builds
                // the final triangle-index collection with each value
                // shifted down by minIndex, so the position / normal / UV
                // collections only need to span [minIndex .. maxIndex] of
                // the original VB instead of [0 .. maxIndex]. Per-part
                // submeshes whose IB starts well above zero (e.g.
                // frontflap_fl on c_mclaren_98 starts at IB-relative 30291)
                // used to drag the prefix [0 .. minIndex-1] in as orphan
                // vertices that no triangle ever touched -- ~87% of every
                // body submesh's emitted positions on c_mclaren_98 LOD0
                // were such orphans, inflating the glTF export size and
                // skewing per-submesh bboxes.
                for (long k = iStart; k < iEnd; k += ib.IndexSize)
                {
                    int idx = (ib.IndexSize == 2)
                        ? BitConverter.ToUInt16(iData, (int)k)
                        : BitConverter.ToInt32(iData, (int)k);
                    if (idx < minIndex) minIndex = idx;
                    if (idx > maxIndex) maxIndex = idx;
                }

                // Defensive clamps. If the IB range was empty (degenerate
                // submesh) keep the legacy "emit nothing meaningful"
                // behaviour by collapsing the window to a single vertex
                // -- the OOB / collection-size guards below still apply.
                if (maxIndex < 0)
                {
                    minIndex = 0;
                    maxIndex = 0;
                }
                if (minIndex < 0) minIndex = 0;

                // SECOND PASS: build the triangle-index collection. Each
                // raw IB value is shifted by -minIndex so it points into
                // the compacted positions / normals / UVs collections we
                // build below. When minIndex == 0 (root-union descriptors,
                // first-VB-vertex per-parts) this is a strict no-op.
                for (long k = iStart; k < iEnd; k += ib.IndexSize)
                {
                    int idx = (ib.IndexSize == 2)
                        ? BitConverter.ToUInt16(iData, (int)k)
                        : BitConverter.ToInt32(iData, (int)k);

                    indices.Add(idx - minIndex);
                }

                // --- Read Vertices ---
                // vStart is shifted forward by (minIndex * stride) so the
                // decode loop below reads positions[minIndex .. maxIndex]
                // of the original VB, not positions[0 .. maxIndex]. The
                // size of the emitted Point3DCollection is therefore
                // (maxIndex - minIndex + 1).
                long vStart = (long)(sm.VOffset + minIndex) * vb.Stride;
                long vEnd = (long)(sm.VOffset + maxIndex + 1) * vb.Stride;

                if (vEnd > vData.Length)
                {
                    Log(
                        $"Skipping {sm.Name}: Vertex OOB " +
                        $"(VOffset={sm.VOffset}, MaxIndex={maxIndex}, " +
                        $"Stride={vb.Stride}, VBytes={vData.Length}, " +
                        $"VB.Count={vb.Count})");
                    continue;
                }

                // Create a decoder for this VB
                var posDecoder = CreatePositionDecoder(vb);
                if (!posDecoder.IsValid)
                {
                    Log($"Skipping {sm.Name}: no valid position decoder for VB '{vb.Name}'");
                    continue;
                }


                var positions = new Point3DCollection(maxIndex - minIndex + 1);

                // BBox accumulators
                double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

                int positionCount = maxIndex - minIndex + 1;
                for (int i = 0; i < positionCount; i++)
                {
                    var p = DecodePosition(posDecoder, vData, vStart, i);
                    positions.Add(p);

                    double x = p.X, y = p.Y, z = p.Z;

                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (z < minZ) minZ = z;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                    if (z > maxZ) maxZ = z;
                }

                // ---------------------------------------------------------
                // F1 2018 COMPANION-STREAM DECODING
                // ---------------------------------------------------------
                //
                // Before this block the mesh shipped to Helix only had
                // Positions + TriangleIndices, so WPF synthesised per-face
                // normals at draw time and the car rendered as a faceted,
                // "STL-like" surface that hid every surface curve.
                //
                // Codemasters' F1 2018 VB layout stores each stream in its
                // own buffer and groups them by matching vertex count:
                //
                //     stride 12  -> POSITION  (3 x float32)     [start of group]
                //     stride  8  -> NORMAL    (4 x half float, xyz used, w=0)
                //     stride  8  -> TANGENT   (4 x half float, w = +/-1)
                //     stride  8  -> UV        (2 x half float + 4 bytes pad)
                //     stride  4  -> packed aux (bone ids / second UV / ...)
                //
                // The VB header's Fvf is identically 1 for every buffer in
                // 2018 so it can't discriminate -- every 2018 ERP we've
                // inspected hits Fvf=1 on all 20 buffers. Stride and the
                // buffer's sibling ordering are reliable instead: the first
                // stride-8 buffer AFTER the picked position VB that has the
                // same vertex count is the normal stream; the third such
                // buffer is the UV stream (normal, tangent, UV in that
                // order). Groups that don't ship normals/UVs (small
                // accessory parts, the stride-40 aux group, the lone
                // stride-12 + stride-4 group D) will simply not find a
                // companion, we skip attaching normals/UVs, and rendering
                // falls back to flat-shaded exactly as before.
                //
                // This is purely additive: if the heuristic fails on any
                // 2016/2017 ERP whose layout differs, the mesh renders
                // exactly how it did before this block was added.
                Vector3DCollection pendingNormals = null;
                PointCollection pendingUvs = null;

                // Note: an earlier revision set a "carbonBypass" flag here
                // that suppressed normal / UV decoding for any submesh
                // whose label began with "carbon". That was defensive
                // scaffolding from when we were trying to make carbon
                // geometry "look exactly like the old loader" (positions-
                // only, flat-shaded). That goal is now served instead by
                // the mesh allowlist at the top of the parse loop (which
                // already filters WHICH submeshes emit), so the bypass
                // became redundant -- and actively harmful: it was the
                // reason the 22 carbon-only descriptors (tether_*,
                // suspension*_*, sidewing_*, hub_caliper_bl/br,
                // frontwing_fl_01 / fr_01, rearfin_bm, "mesh") rendered
                // STL-flat in the previous build. Dropping the bypass
                // lets their companion-stream normals / UVs decode the
                // same way every other submesh does, fixing the
                // faceted look.

                int normalVbIdx = FindCompanionStreamIndex(
                    vbufs, sm.VBufferIndex, expectedStride: 8, skipMatches: 0);
                if (normalVbIdx >= 0)
                {
                    var nvb = vbufs[normalVbIdx];
                    if (File.Exists(nvb.DataPath))
                    {
                        try
                        {
                            byte[] nData = File.ReadAllBytes(nvb.DataPath);
                            long nStart = (long)(sm.VOffset + minIndex) * nvb.Stride;
                            long nEnd = (long)(sm.VOffset + maxIndex + 1) * nvb.Stride;
                            if (nEnd <= nData.Length)
                            {
                                pendingNormals = DecodeHalf4NormalsBlock(
                                    nData, nStart, nvb.Stride, maxIndex - minIndex);
                            }
                            else
                            {
                                Log($"  [{sm.Name}] normal VB OOB (nVB={normalVbIdx}, stride={nvb.Stride}, " +
                                    $"need={nEnd}, have={nData.Length}); skipping normals.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"  [{sm.Name}] normal VB decode threw ({ex.Message}); skipping normals.");
                        }
                    }
                }

                // Classic-tyre interleaved UV path. The body's UV resolver
                // immediately below looks for a SEPARATE VB with stride 8
                // that holds half-float UV pairs (the F1 2018 modern format
                // splits position/normal/UV across multiple VBs). Tyres
                // ship in an older interleaved layout: a single stride-36
                // VB carries position + normal + tangent + 2x UV pairs in
                // one record per vertex.
                //
                // Stride-36 vertex layout, verified empirically against
                // c_03_tyres!!!temp001.idf%3Flod=all&vb=0 (sidewall) and
                // &vb=1 (tread) in F1 2018:
                //
                //   bytes  0-11   position float3 (X, Y, Z)
                //   bytes 12-19   normal as 4 packed shorts (X,Y,Z,pad)
                //                 reading X/Y/Z / 32768.0 -> [-1, 1]
                //   bytes 20-27   tangent as 4 packed shorts (X,Y,Z,sign)
                //   bytes 28-31   UV0 as 2 half-floats
                //   bytes 32-35   UV1 as 2 half-floats (==UV0 for sidewall;
                //                 zero for tread)
                //
                // We use UV0 from offset 28. Since DecodeHalf2UvsBlock reads
                // half-float pairs at offset 0 of each stride-N record, we
                // shift the start pointer by +28 and reuse it as-is.
                //
                // Why this is gated on _inTyreLoadPass rather than just
                // "stride == 36": stride 36 may also occur in body content
                // (non-2018 packages, custom imports), and we don't want to
                // accidentally start reading UVs from interleaved offsets
                // for those if their layout happens to differ. Keeping the
                // tyre path gated to the tyre load pass makes it strictly
                // additive and safe.
                bool tyreUvHandled = false;
                if (_inTyreLoadPass &&
                    sm.VBufferIndex >= 0 && sm.VBufferIndex < vbufs.Count &&
                    vbufs[sm.VBufferIndex].Stride == 36)
                {
                    var posVb = vbufs[sm.VBufferIndex];
                    if (File.Exists(posVb.DataPath))
                    {
                        try
                        {
                            byte[] uData = File.ReadAllBytes(posVb.DataPath);

                            // Same per-vertex stride as positions, but the
                            // half-float UV pair starts 28 bytes in.
                            const int uvOffsetWithinStride = 28;

                            long uStart = (long)(sm.VOffset + minIndex) * posVb.Stride
                                          + uvOffsetWithinStride;
                            long uEnd = (long)(sm.VOffset + maxIndex + 1) * posVb.Stride;

                            if (uEnd <= uData.Length)
                            {
                                pendingUvs = DecodeHalf2UvsBlock(
                                    uData, uStart, posVb.Stride, maxIndex - minIndex);
                                tyreUvHandled = true;
                                Log($"  [{sm.Name}] tyre interleaved UV: " +
                                    $"extracted {pendingUvs.Count} UV(s) from " +
                                    $"VB={sm.VBufferIndex} stride=36 offset=28.");
                            }
                            else
                            {
                                Log($"  [{sm.Name}] tyre interleaved UV: " +
                                    $"VB OOB (stride=36, would need {uEnd} bytes, " +
                                    $"have {uData.Length}); skipping UVs.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"  [{sm.Name}] tyre interleaved UV decode threw " +
                                $"({ex.Message}); skipping UVs.");
                        }
                    }
                }

                // The body's existing companion-VB path. Skipped during the
                // tyre pass when we already extracted interleaved UVs above
                // (otherwise the body resolver runs, can't find a stride-8
                // companion VB, sets uvVbIdx=-1, and would leave the wrongly
                // chosen value in the diagnostic log).
                int uvVbIdx = tyreUvHandled
                    ? sm.VBufferIndex   // for the diagnostic log line at end
                    : FindCompanionStreamIndex(
                        vbufs, sm.VBufferIndex, expectedStride: 8, skipMatches: 2);
                if (!tyreUvHandled && uvVbIdx >= 0)
                {
                    var uvb = vbufs[uvVbIdx];
                    if (File.Exists(uvb.DataPath))
                    {
                        try
                        {
                            byte[] uData = File.ReadAllBytes(uvb.DataPath);
                            long uStart = (long)(sm.VOffset + minIndex) * uvb.Stride;
                            long uEnd = (long)(sm.VOffset + maxIndex + 1) * uvb.Stride;
                            if (uEnd <= uData.Length)
                            {
                                pendingUvs = DecodeHalf2UvsBlock(
                                    uData, uStart, uvb.Stride, maxIndex - minIndex);

                                // -----------------------------------------
                                // F1 2018 CARBON-SLOT UV TILE-RATE UPSCALE
                                // -----------------------------------------
                                //
                                // F1 2018 stores "carbon tile N" submeshes'
                                // UVs in atlas range ([0, 1]) -- same as
                                // the paint atlas -- and relies on the
                                // live game shader (me_carbonTile) to
                                // multiply them by a large tile-rate at
                                // draw time so mclaren_carbonN.tif tiles
                                // into a visible weave. A preview sampling
                                // the carbon texture with raw [0, 1] UVs
                                // maps a single big tile across each panel
                                // ("mclaren_carbon1 is distorted"), since
                                // we don't run the shader.
                                //
                                // We simulate the shader's scale here: when
                                // a submesh name starts with "carbon" AND
                                // its UVs still look atlas-shaped (max|UV|
                                // at or below CarbonAtlasThreshold), we
                                // multiply every UV by CarbonTileMultiplier
                                // to produce a readable weave.
                                //
                                // "carbon tile 4" submeshes (paddles,
                                // steering wheel) have UVs already authored
                                // at ~70 tile rate in the vertex data, so
                                // the threshold guard skips them -- they
                                // fall through to the MaxUvTileRate cap
                                // that scales them back down to a readable
                                // 64-max, exactly as before this block.
                                if (pendingUvs != null && pendingUvs.Count > 0 &&
                                    CarbonTileMultiplier > 1.0 &&
                                    IsCarbonSlotName(sm.Name))
                                {
                                    double atlasMaxAbs = 0.0;
                                    foreach (var p in pendingUvs)
                                    {
                                        double au = Math.Abs(p.X);
                                        double av = Math.Abs(p.Y);
                                        if (au > atlasMaxAbs) atlasMaxAbs = au;
                                        if (av > atlasMaxAbs) atlasMaxAbs = av;
                                    }
                                    if (atlasMaxAbs <= CarbonAtlasThreshold)
                                    {
                                        double k = CarbonTileMultiplier;
                                        var boosted = new PointCollection(pendingUvs.Count);
                                        foreach (var p in pendingUvs)
                                            boosted.Add(new System.Windows.Point(p.X * k, p.Y * k));
                                        Log($"  [{sm.Name}] carbon atlas-UV upscale: max|UV|={atlasMaxAbs:F3} <= {CarbonAtlasThreshold:F2}, multiplying by {k:F2}");
                                        pendingUvs = boosted;
                                    }
                                }

                                // -----------------------------------------
                                // F1 2018 CARBON-WEAVE UV TILE-RATE CAP
                                // -----------------------------------------
                                //
                                // Codemasters authors carbon panels with
                                // UVs running from roughly -50 to +70
                                // tile-repeats because the live game
                                // shader (me_carbonTile) applies its own
                                // per-material scale and an anisotropy/
                                // lacquer pass on top. A basic preview
                                // without that shader samples mclaren_
                                // carbon.tif at the raw UV rate, which
                                // packs ~70 tile-repeats into a panel a
                                // few centimetres wide -- the weave
                                // collapses into indistinguishable fine
                                // visual noise and the user sees
                                // "mclaren_carbon is distorted".
                                //
                                // The 2021+ loader has the exact same
                                // cap for the exact same reason (see
                                // F1ModelLoader.MaxUvTileRate). We
                                // port it here and apply it to every
                                // submesh uniformly: body / detail /
                                // number UVs are already in [0, 1] so
                                // the cap is a no-op for them, while
                                // carbon tiles get scaled down to a
                                // readable weave density. The cap is
                                // exposed as a public static property
                                // so a future caller can raise it to
                                // see the raw carbon tile rate when
                                // debugging.
                                if (pendingUvs != null && pendingUvs.Count > 0 && MaxUvTileRate > 0.0)
                                {
                                    double maxAbs = 0.0;
                                    foreach (var p in pendingUvs)
                                    {
                                        double au = Math.Abs(p.X);
                                        double av = Math.Abs(p.Y);
                                        if (au > maxAbs) maxAbs = au;
                                        if (av > maxAbs) maxAbs = av;
                                    }
                                    if (maxAbs > MaxUvTileRate)
                                    {
                                        double scale = MaxUvTileRate / maxAbs;
                                        var scaled = new PointCollection(pendingUvs.Count);
                                        foreach (var p in pendingUvs)
                                        {
                                            scaled.Add(new System.Windows.Point(
                                                p.X * scale, p.Y * scale));
                                        }
                                        Log($"  [{sm.Name}] UV tile-rate cap: max|UV|={maxAbs:F2} > {MaxUvTileRate:F2}, scaling by {scale:F4}");
                                        pendingUvs = scaled;
                                    }
                                }
                            }
                            else
                            {
                                Log($"  [{sm.Name}] UV VB OOB (uVB={uvVbIdx}, stride={uvb.Stride}); skipping UVs.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"  [{sm.Name}] UV VB decode threw ({ex.Message}); skipping UVs.");
                        }
                    }
                }

                // ---------------------------------------------------------
                // F1 2018 PAIR-2 UV DECODING (DECAL / SPONSOR / NUMBER UV)
                // ---------------------------------------------------------
                //
                // CORRECTED -- previous implementation read a separate
                // stride-4 VB4 thinking that held the decal UV stream.
                // Turns out F1 2018 follows the same packing as F1 2021+:
                // VB3 is stride-8 with FOUR halves per vertex encoding
                // BOTH UV pairs packed together. The 2021+ overlay code
                // reads the exact same stream we read for pair-1
                // (sm.UvBufferIndex) but samples offsets [4,6] of each
                // stride-8 window instead of [0,2]. We now match that.
                //
                // VB4 (stride-4) in F1 2018 ERPs is NOT the decal UV
                // stream -- it's a different auxiliary buffer (bone
                // weights / vertex colour / similar). Its bytes happen
                // to decode as half-floats in [0, 1] by coincidence
                // (those ranges are naturally where weights and 0-255
                // colour encode to as halves), which is why the broken
                // implementation passed the bbox sanity log but produced
                // "decals at garbage UV coordinates" visually. The
                // sponsors were being sampled from whatever part of the
                // decal atlas those weight/colour bytes happened to
                // index. Reading from VB3 [4,6] now gives the actual
                // authored decal UVs, same source 2021+ uses.
                //
                // No tile-rate cap, no corner-park rejection, no
                // V-flip, no scale/bias -- raw pair-2 UVs straight into
                // TextureCoordinates, matching 2021+'s "FIX 20e: use raw
                // pair-2 UVs with NO modifications" stance.
                PointCollection pair2Uvs = null;
                if (uvVbIdx >= 0 && !_inTyreLoadPass)
                {
                    var uvb2 = vbufs[uvVbIdx];
                    if (uvb2.Stride >= 8 && File.Exists(uvb2.DataPath))
                    {
                        try
                        {
                            byte[] pData = File.ReadAllBytes(uvb2.DataPath);
                            long pStart = (long)(sm.VOffset + minIndex) * uvb2.Stride;
                            long pEnd = (long)(sm.VOffset + maxIndex + 1) * uvb2.Stride;
                            if (pEnd <= pData.Length)
                            {
                                // Shift the per-vertex window start by +4
                                // bytes so DecodeHalf2UvsBlock's internal
                                // reads of offsets [0,2] land on the
                                // stride-8 window's bytes [4,5] and [6,7]
                                // -- i.e. the second packed UV pair.
                                pair2Uvs = DecodeHalf2UvsBlock(
                                    pData, pStart + 4, uvb2.Stride, maxIndex - minIndex);
                            }
                            else
                            {
                                Log($"  [{sm.Name}] pair-2 UV OOB (vb={uvVbIdx}, stride={uvb2.Stride}); skipping overlay UVs.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"  [{sm.Name}] pair-2 UV decode threw ({ex.Message}); skipping overlay UVs.");
                        }
                    }
                    else if (uvb2.Stride < 8)
                    {
                        // Stride-4 or smaller pair-1 VB can't contain a
                        // second UV pair. F1 2018 shouldn't hit this
                        // (pair-1 is always stride-8 in the packages we
                        // tested), but guard so we fail cleanly if it
                        // ever does.
                        Log($"  [{sm.Name}] pair-2 UV unavailable: pair-1 VB stride={uvb2.Stride} < 8");
                    }
                }

                // ---------------------------------------------------------
                // DAMAGE UV STREAM (vb=4 -> TEXCOORD_2)
                // ---------------------------------------------------------
                //
                // Stride-4 half2 per vertex: the damage-atlas UV channel
                // that mclaren_main_damage.tif is sampled with by both
                // vehicle_metallic_paint.nefx2 (body) and vehicle_carbon
                // .nefx2 (carbon) shaders. Pool A's layout puts vb=4
                // (UV2/damage, stride 4 half2) immediately before vb=5
                // (bone weights, stride 4 byte4); FindCompanionStreamIndex
                // with expectedStride=4 and skipMatches=0 picks vb=4
                // because it appears first in the descriptor's vbuf table.
                //
                // We decode the per-vertex window [VOffset .. VOffset +
                // maxIndex] using the same DecodeHalf2UvsBlock helper as
                // the primary UV path (vb=4 is half2 just like vb=3 pair-1
                // and pair-2; the only difference is stride 4 vs 8).
                //
                // The decoded PointCollection is registered against the
                // mesh below (after MeshGeometry3D is allocated). The
                // exporter (F1FbxExporter) looks it up via
                // GetMeshDamageUvs and emits it as TEXCOORD_2 in the
                // glTF output. The writer's importer
                // (GltfLodFolderImporter) reads TEXCOORD_2 into
                // SubMesh.Uvs2, and ClassicIdfWriter2018 v11.50+ routes
                // it back into vb=4 verbatim. End-to-end stock round-
                // trip on the damage-atlas channel is byte-identical.
                //
                // If the stride-4 companion isn't present, or the data
                // file is missing, or the per-vertex window goes past
                // the end of the buffer, damageUvs stays null and the
                // writer falls back to its v11.49 synth / UV0 paths.
                // No regression in the absence-of-data case.
                PointCollection damageUvs = null;
                int dmgVbIdx = FindCompanionStreamIndex(
                    vbufs, sm.VBufferIndex, expectedStride: 4, skipMatches: 0);
                if (dmgVbIdx >= 0)
                {
                    var dvb = vbufs[dmgVbIdx];
                    if (File.Exists(dvb.DataPath))
                    {
                        try
                        {
                            byte[] dData = File.ReadAllBytes(dvb.DataPath);
                            long dStart = (long)(sm.VOffset + minIndex) * dvb.Stride;
                            long dEnd = (long)(sm.VOffset + maxIndex + 1) * dvb.Stride;
                            if (dEnd <= dData.Length)
                            {
                                damageUvs = DecodeHalf2UvsBlock(
                                    dData, dStart, dvb.Stride, maxIndex - minIndex);
                            }
                            else
                            {
                                Log($"  [{sm.Name}] vb=4 damage UV OOB (vb={dmgVbIdx}, stride={dvb.Stride}, " +
                                    $"need={dEnd}, have={dData.Length}); skipping damage UVs.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"  [{sm.Name}] vb=4 damage UV decode threw ({ex.Message}); skipping damage UVs.");
                        }
                    }
                }

                var mesh = new MeshGeometry3D
                {
                    Positions = positions,
                    TriangleIndices = indices
                };

                if (pendingNormals != null && pendingNormals.Count == positions.Count)
                    mesh.Normals = pendingNormals;
                if (pendingUvs != null && pendingUvs.Count == positions.Count)
                    mesh.TextureCoordinates = pendingUvs;

                // Register vb=4 damage UVs on the mesh so the exporter
                // can pick them up via GetMeshDamageUvs and surface them
                // as glTF TEXCOORD_2. Length-gated against positions so
                // a partial decode doesn't poison the lookup.
                if (damageUvs != null && damageUvs.Count == positions.Count)
                    RegisterMeshDamageUvs(mesh, damageUvs);

                Log($"  [{sm.Name}] attach: normals={(mesh.Normals != null ? mesh.Normals.Count.ToString() : "none")}  " +
                    $"uvs={(mesh.TextureCoordinates != null ? mesh.TextureCoordinates.Count.ToString() : "none")}  " +
                    $"(posVB={sm.VBufferIndex}, nVB={normalVbIdx}, uvVB={uvVbIdx})");

                // Resolve canonical part name from submesh + VB using your naming helper.
                string partName = ResolvePartNameForSubmesh(sm, vbufs, sm.VBufferIndex);

                // Prefer the part name extracted from the DESCRIPTOR file-
                // name when it's available. For 2018 cars this is the
                // only reliable source: ResolvePartNameForSubmesh above
                // builds "slotName|vbufName" and hands it to
                // UniversalIdfNaming.GetPartName, but the VB EAID on 2018
                // doesn't carry the part name at all -- so that function
                // falls through to GetFamilyName which returns junk like
                // "body|aren/idf/mclaren". That junk propagates as the
                // MeshFamilyRegistry entry and the glTF exporter can no
                // longer match parts back to ?render node indices, with
                // the end result being every LOD emits 0 meshes.
                //
                // The descriptor filename (e.g. "mclaren!!!temp000.idf%3Flod=0&safetycell&mesh")
                // has the clean part name right there. The outer foreach
                // loop in LoadFolder pre-populates _currentDescriptorPartName
                // before calling in here, so we just swap it in when set.
                if (!string.IsNullOrEmpty(_currentDescriptorPartName))
                    partName = _currentDescriptorPartName;

                // Only register a family name when the descriptor filename
                // actually gave us one.
                if (!string.IsNullOrEmpty(_currentDescriptorPartName))
                {
                    MeshFamilyRegistry.Register(mesh, _currentDescriptorPartName);
                }

                // Also register slot name + descriptor path + material EAID
                // for the exporter.
                {
                    string __slotMaterialEaid = BuildMaterialEaidForSlot(
                        _detectedPackageName ?? "f1_2018_vehicle_package",
                        teamPrefix, sm.Name);
                    MeshSubmeshRegistry.Register(mesh, sm.Name, _currentDescriptorPath, __slotMaterialEaid);

                    // The exporter computes vb5[wpf_index + VOffset] when
                    // looking up the per-vertex bone byte for root-union
                    // redistribution. Our triangle indices are now
                    // shifted by -minIndex (positions span [minIndex ..
                    // maxIndex] of the original VB instead of [0 ..
                    // maxIndex]), so we register sm.VOffset + minIndex
                    // here instead of sm.VOffset to keep that lookup
                    // pointing at the same global VB rows. When
                    // minIndex == 0 this is a strict no-op.
                    RegisterMeshVOffset(mesh, sm.VOffset + minIndex);
                }

                // Register for classic patching so we can write edited positions
                // back into the correct vertex buffer slice later.


                // Texture pipeline hookup. When AutoConfigureTextureCache
                // found a package root + we have a mapping from this
                // submesh's slot name to a material EAID, the TextureCache
                // decodes the .tif pair on disk into a frozen ImageBrush
                // and we wrap it in a DiffuseMaterial. When anything in
                // that chain fails (no cache, no mapping, missing file,
                // BC decode error, ...) we log the reason and fall back to
                // the neutral gray material that was the sole behaviour
                // before this hookup existed. In particular this means the
                // textureless case (ERP loaded standalone without a valid
                // on-disk package cache) is bit-for-bit identical to the
                // previous build -- gray everywhere, no surprises.
                Material mat = ResolveSubmeshMaterial(sm.Name, teamPrefix,
                    out TextureCache.PreviewResolution? resolution,
                    out string matLogStatus);
                if (mat == null)
                {
                    if (!string.IsNullOrEmpty(matLogStatus))
                        Log($"  [{sm.Name}] texture: {matLogStatus} -> gray fallback");
                    mat = new DiffuseMaterial(new SolidColorBrush(Colors.LightGray));
                }
                else
                {
                    Log($"  [{sm.Name}] texture: {matLogStatus}");
                }

                // Record the BASE diffuse texture EAID (the one bound to
                // me_albedoMap / me_diffuseMap for this slot) so the glTF
                // exporter can decode its pixels via TextureCache.
                if (resolution.HasValue && !string.IsNullOrEmpty(resolution.Value.TextureEaid))
                {
                    MeshSubmeshRegistry.SetTextureEaid(mesh, resolution.Value.TextureEaid);
                }

                group.Children.Add(new GeometryModel3D(mesh, mat) { BackMaterial = mat });

                // ---------------------------------------------------------
                // OVERLAY MESH (DECAL SPONSORS + DRIVER NUMBERS)
                // ---------------------------------------------------------
                //
                // Paint / carbon / steering shader families carry an
                // overlay texture (me_decalMap / me_logoMap) on top of
                // the base colour. When MaterialSlotResolver populates
                // resolution.OverlayBrush (and pair-2 UVs are available),
                // we emit a second GeometryModel3D for this submesh that
                // samples the overlay texture through pair-2 UVs and
                // sits a sub-millimetre above the base along the normal
                // to win the depth test. Transparent areas of the
                // overlay atlas let the base mesh show through
                // unchanged. This is the same pattern the 2021+
                // F1ModelLoader uses at its per-submesh add site.
                //
                // For F1 2018 specifically, MaterialSlotResolver.
                // ClassifyShader usually returns Unknown for paint/carbon
                // materials (shader EAID is "vehicle_paint.nefx2" rather
                // than 2021+'s "vehicle_metallic_paint.nefx2"), so the
                // resolver's overlay slot is empty. We fall back to
                // TryResolveOverlayBrushClassic, which reads the parsed
                // MaterialFile directly and picks me_logoMap /
                // me_decalMap regardless of shader classification. This
                // is the difference between "base paint loads but
                // sponsors/numbers are missing" and "fully textured".
                System.Windows.Media.Brush overlayBrush = null;
                string overlaySourceNote = null;
                if (resolution.HasValue && resolution.Value.HasOverlay &&
                    resolution.Value.OverlayBrush != null)
                {
                    overlayBrush = resolution.Value.OverlayBrush;
                    overlaySourceNote = $"MaterialSlotResolver ({resolution.Value.OverlaySlot})";
                }
                else
                {
                    overlayBrush = TryResolveOverlayBrushClassic(sm.Name, teamPrefix,
                        out string classicFallbackLog);
                    if (overlayBrush != null)
                    {
                        overlaySourceNote = classicFallbackLog;
                    }
                    else if (!string.IsNullOrEmpty(classicFallbackLog))
                    {
                        // Log the classic-fallback's specific reason only
                        // if it engaged (i.e. got past the slot-name gate)
                        // AND failed. Silently skipping for mirror/wheel
                        // etc. keeps the log focused.
                        if (!classicFallbackLog.Contains("not in body/number/carbon-tile allowlist"))
                            Log($"  [{sm.Name}] overlay-classic: {classicFallbackLog}");
                    }
                }

                if (overlayBrush != null &&
                    pair2Uvs != null && pair2Uvs.Count == positions.Count &&
                    pendingNormals != null && pendingNormals.Count == positions.Count)
                {
                    try
                    {
                        // Pair-2 UV bbox (over the USED indices) computed
                        // purely for the log line below -- we don't gate
                        // on it, don't scale it, don't flip it, don't
                        // wrap it, don't shift it. The pair-2 UV stream
                        // decoded from VB4 flows straight into the
                        // overlay mesh's TextureCoordinates without any
                        // transformation, matching 2021+'s "FIX 20e: use
                        // raw pair-2 UVs with NO modifications" stance.
                        double spUmin = double.PositiveInfinity, spUmax = double.NegativeInfinity;
                        double spVmin = double.PositiveInfinity, spVmax = double.NegativeInfinity;
                        for (int ri = 0; ri < indices.Count; ri++)
                        {
                            int vi = indices[ri];
                            if (vi < 0 || vi >= pair2Uvs.Count) continue;
                            double u = pair2Uvs[vi].X, v = pair2Uvs[vi].Y;
                            if (u < spUmin) spUmin = u; if (u > spUmax) spUmax = u;
                            if (v < spVmin) spVmin = v; if (v > spVmax) spVmax = v;
                        }

                        // Offset positions along the normal so the overlay
                        // sits on top of the base without z-fighting. 0.0005
                        // works for paint/carbon; numbers need a touch more
                        // (0.002) because the number slot uses a nearly
                        // co-planar submesh with the body.
                        double offsetAmount = string.Equals(sm.Name, "number",
                            StringComparison.OrdinalIgnoreCase) ? 0.002 : 0.0005;

                        var offsetPositions = new Point3DCollection(positions.Count);
                        for (int k = 0; k < positions.Count; k++)
                        {
                            var p = positions[k];
                            var n = pendingNormals[k];
                            offsetPositions.Add(new Point3D(
                                p.X + n.X * offsetAmount,
                                p.Y + n.Y * offsetAmount,
                                p.Z + n.Z * offsetAmount));
                        }

                        var overlayMesh = new MeshGeometry3D
                        {
                            Positions = offsetPositions,
                            Normals = pendingNormals,
                            TriangleIndices = indices,
                            TextureCoordinates = pair2Uvs,
                        };

                        var overlayMat = new DiffuseMaterial(overlayBrush);
                        group.Children.Add(new GeometryModel3D(overlayMesh, overlayMat)
                        {
                            BackMaterial = overlayMat,
                        });

                        // Register the overlay mesh in the same registries the
                        // base submesh just got registered in. Without this,
                        // the glTF exporter's TryResolveLoadedPartNameFromModel
                        // can't find a family for the overlay's MeshGeometry3D,
                        // so all ~40 overlay meshes end up as "unresolved_part_N"
                        // children of x0_mesh with the default gray material --
                        // the "duplicate parts with bad materials floating in
                        // the wrong place" the user is seeing in the output.
                        //
                        // We anchor the overlay under the SAME part node as
                        // its base (same partName) so it moves together with
                        // the part, and we stamp a distinct "_overlay" suffix
                        // on the slot so the material bucket is separate:
                        //   body            -> mclaren_paint__body          (base paint)
                        //   body_overlay    -> mclaren_main_decal_da__body_overlay (sponsor decals)
                        //   number          -> mclaren_number_14__number
                        //   number_overlay  -> ...__number_overlay
                        //   carbon_tile_1            -> mclaren_carbon__carbon_tile_1
                        //   carbon_tile_1_overlay    -> ...
                        // Everything the game actually draws is preserved and
                        // named meaningfully; modders can re-texture the base
                        // paint and the decals independently.
                        //
                        // The overlay-texture EAID -- when the resolver gave us
                        // one -- becomes the material's "raw EAID" property so
                        // DeriveMaterialDisplayName can produce a proper file-
                        // stem display name for it. If we only got a brush via
                        // the classic-fallback (decal_da sniffing) and have no
                        // EAID, the fallback slot name itself is used.
                        {
                            string __overlayEaid = resolution.HasValue && resolution.Value.HasOverlay
                                ? resolution.Value.OverlayTextureEaid
                                : null;

                            string __overlaySlot = sm.Name + "_overlay";

                            // Family matches the base so exporter attaches this
                            // overlay under the real part node rather than
                            // synthesising an "unresolved_part_N" orphan. Use
                            // the descriptor-derived name when available for
                            // the same reasons the base registration above does.
                            // Match the base-submesh registration rule: only
                            // register a family when the descriptor filename
                            // gave us one. Overlays from the root/union
                            // descriptor get filtered at the exporter's
                            // slot-grouping step regardless, so whether
                            // we register or not here has no effect on
                            // output -- but it keeps the registry clean.
                            if (!string.IsNullOrEmpty(_currentDescriptorPartName))
                            {
                                MeshFamilyRegistry.Register(overlayMesh, _currentDescriptorPartName);
                            }
                            MeshSubmeshRegistry.Register(
                                overlayMesh, __overlaySlot, _currentDescriptorPath, __overlayEaid);
                            // Same +minIndex shift as the base mesh: the
                            // overlay reuses the base submesh's `indices`
                            // collection (same TriangleIndices reference),
                            // so its per-triangle wpf_index values are
                            // also shifted by -minIndex.
                            RegisterMeshVOffset(overlayMesh, sm.VOffset + minIndex);
                        }

                        Log($"  [{sm.Name}] overlay: attached [{overlaySourceNote}] " +
                            $"bbox=[{spUmin:F3},{spUmax:F3}]x[{spVmin:F3},{spVmax:F3}] " +
                            $"({pair2Uvs.Count} pair-2 UVs, offset={offsetAmount:F4})");
                    }
                    catch (Exception ex)
                    {
                        Log($"  [{sm.Name}] overlay: build threw ({ex.Message}); skipping overlay");
                    }
                }
                else if (overlayBrush != null)
                {
                    // We got a brush but couldn't assemble a mesh for it --
                    // diagnose which input is missing so the issue is
                    // immediately traceable without running a debugger.
                    Log($"  [{sm.Name}] overlay: HAVE brush but skipped " +
                        $"(pair2Uvs={(pair2Uvs != null ? pair2Uvs.Count : 0)}, " +
                        $"positions={positions.Count}, " +
                        $"normals={(pendingNormals != null ? pendingNormals.Count : 0)})");
                }

                // Per-mesh stats log
                if (positions.Count > 0)
                {
                    LogSubMeshStats(
                        sm,
                        vb,
                        ib,
                        positions.Count,
                        sm.IndexCount,
                        minX, minY, minZ,
                        maxX, maxY, maxZ);
                }
                else
                {
                    Log($"[SUBMESH-STATS] '{sm.Name}' has 0 vertices (positions collection empty).");
                }

                // Global stats
                _totalSubmeshes++;
                _totalIndices += sm.IndexCount;
                _totalTriangles += sm.IndexCount / 3;
                _totalVertices += positions.Count;
            }

            return group;
        }
        /// <summary>
        /// Pulls the part name out of a descriptor filename like
        ///   mclaren!!!temp000.idf%3Flod=0&amp;safetycell&amp;mesh
        /// (the "&amp;safetycell&amp;" between "lod=N" and "&amp;mesh"). For
        /// descriptors with no embedded part name -- e.g. the root
        /// "lod=0&amp;mesh" union descriptor -- returns the empty string so
        /// the caller falls back to ResolvePartNameForSubmesh's usual
        /// logic. No regex dependency so we don't have to touch the file's
        /// using directives.
        /// </summary>
        private static string ExtractPartNameFromDescriptorFilename(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "";

            string fn = Path.GetFileName(path) ?? "";
            if (fn.Length == 0)
                return "";

            string dec;
            try { dec = Uri.UnescapeDataString(fn); }
            catch { dec = fn; }

            int lodIdx = dec.IndexOf("lod=", StringComparison.OrdinalIgnoreCase);
            if (lodIdx < 0)
                return "";

            int firstAmp = dec.IndexOf('&', lodIdx);
            if (firstAmp < 0)
                return "";

            int nextAmp = dec.IndexOf('&', firstAmp + 1);
            if (nextAmp < 0)
                return "";

            // Require "mesh" to follow the second "&". This filters out
            // oddly-shaped filenames that happen to have two "&" but
            // aren't real mesh descriptors. Case-insensitive match on
            // the "mesh" tail -- we don't care about any trailing .bin
            // or similar extension after it.
            int tailStart = nextAmp + 1;
            if (tailStart >= dec.Length)
                return "";
            if (string.Compare(dec, tailStart, "mesh", 0, 4, StringComparison.OrdinalIgnoreCase) != 0)
                return "";

            string candidate = dec.Substring(firstAmp + 1, nextAmp - firstAmp - 1).Trim();
            return candidate;
        }

        private static string ResolvePartNameForSubmesh(
    SubMeshInfo sm,
    List<VertexBufferInfo> vbufs,
    int vIndex)
        {
            string label = sm?.Name ?? string.Empty;
            string vbufName = string.Empty;

            if (vIndex >= 0 && vIndex < vbufs.Count)
                vbufName = vbufs[vIndex].Name ?? string.Empty;

            // Combine label + VB name so UniversalIdfNaming can see things like
            // "...safetycell%3Fmodel" even if the label is just "Body".
            string combined = string.IsNullOrEmpty(vbufName)
                ? label
                : label + "|" + vbufName;

            return UniversalIdfNaming.GetPartName(combined);
        }

        // =====================================================================
        // HELPERS
        // =====================================================================
        public static Model3DGroup LoadGfxModelRes(string gfxModelResPath)
        {
            string folder = Path.GetDirectoryName(gfxModelResPath) ?? Directory.GetCurrentDirectory();
            string logFile = Path.Combine(folder, "F1_ClassicLoader_GFX_Log.txt");

            ResetLog(logFile);
            Log("==================================================");
            Log("F1ModelLoaderClassic.LoadGfxModelRes");
            Log($"Input: {gfxModelResPath}");

            var group = LoadFromGfxModelResInternal(gfxModelResPath);
            if (group == null)
                return new Model3DGroup();

            return group;
        }

        private static GfxModelRes ReadGfxModelRes(string path)
        {
            if (!File.Exists(path))
            {
                Log($"[GFX] File not found: {path}");
                return null;
            }

            Log($"[GFX] Reading GFXmodelres: {path}");

            var res = new GfxModelRes();

            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                // --- Header (matches 010 template) ---
                res.NumLodDistances = br.ReadUInt32();
                res.NumNodes = br.ReadUInt16();
                res.NumBoneNodeIndices = br.ReadUInt16();
                res.NumNames = br.ReadUInt32();
                res.NumFiles = br.ReadUInt32();

                for (int i = 0; i < 4; i++)
                    res.UnkHeaderFloats[i] = br.ReadSingle();

                // uint32 num6[numLODDistances];
                res.Num6 = new uint[res.NumLodDistances];
                for (int i = 0; i < res.NumLodDistances; i++)
                    res.Num6[i] = br.ReadUInt32();

                // LOD lodDistances[numLODDistances];
                for (int i = 0; i < res.NumLodDistances; i++)
                {
                    var lod = new GfxLod
                    {
                        MinDist = br.ReadSingle(),
                        MaxDist = br.ReadSingle()
                    };
                    res.LodDistances.Add(lod);
                }

                // uint32 num7;
                res.Num7 = br.ReadUInt32();

                Log($"[GFX] Header: lods={res.NumLodDistances}, nodes={res.NumNodes}, boneIdx={res.NumBoneNodeIndices}, names={res.NumNames}, files={res.NumFiles}");

                // Node nodes[numNodes];
                for (int i = 0; i < res.NumNodes; i++)
                {
                    var node = new GfxNode();

                    node.StringOffset = br.ReadUInt32();
                    node.ParentIndex = br.ReadUInt32();

                    // float pos[3];
                    double px = br.ReadSingle();
                    double py = br.ReadSingle();
                    double pz = br.ReadSingle();
                    node.Position = new Vector3D(px, py, pz);

                    // float rot[4];
                    double rx = br.ReadSingle();
                    double ry = br.ReadSingle();
                    double rz = br.ReadSingle();
                    double rw = br.ReadSingle();
                    node.Rotation = new Quaternion(rx, ry, rz, rw);

                    // float scale[3];
                    double sx = br.ReadSingle();
                    double sy = br.ReadSingle();
                    double sz = br.ReadSingle();
                    node.Scale = new Vector3D(sx, sy, sz);

                    // float transform[16];
                    var m = new Matrix3D();
                    m.M11 = br.ReadSingle();
                    m.M12 = br.ReadSingle();
                    m.M13 = br.ReadSingle();
                    m.M14 = br.ReadSingle();

                    m.M21 = br.ReadSingle();
                    m.M22 = br.ReadSingle();
                    m.M23 = br.ReadSingle();
                    m.M24 = br.ReadSingle();

                    m.M31 = br.ReadSingle();
                    m.M32 = br.ReadSingle();
                    m.M33 = br.ReadSingle();
                    m.M34 = br.ReadSingle();
                    m.OffsetX = br.ReadSingle();
                    m.OffsetY = br.ReadSingle();
                    m.OffsetZ = br.ReadSingle();
                    m.M44 = br.ReadSingle();
                    node.Transform = m;
                    node.Transform = m;

                    node.Unk6 = br.ReadUInt32();
                    node.Unk7 = br.ReadUInt32();

                    res.Nodes.Add(node);
                }

                // int16 boneNodeIndices[numBoneNodeIndices];
                res.BoneNodeIndices = new short[res.NumBoneNodeIndices];
                for (int i = 0; i < res.NumBoneNodeIndices; i++)
                    res.BoneNodeIndices[i] = br.ReadInt16();

                // struct { string meshfile; } files[numFiles];
                for (int i = 0; i < res.NumFiles; i++)
                {
                    string meshfile = ReadCString(br);
                    res.Files.Add(meshfile);
                }

                // strDataOffset = FTell();
                long strDataOffset = fs.Position;

                // struct { string name; } names[numNames];
                for (int i = 0; i < res.NumNames; i++)
                {
                    string name = ReadCString(br);
                    res.Names.Add(name);
                }

                // Try to resolve node names using StringOffset relative to names-block
                for (int i = 0; i < res.Nodes.Count; i++)
                {
                    var node = res.Nodes[i];

                    if (node.StringOffset == 0)
                    {
                        node.Name = string.Empty;
                        res.Nodes[i] = node;
                        continue;
                    }

                    long savePos = fs.Position;
                    try
                    {
                        fs.Position = strDataOffset + node.StringOffset;
                        node.Name = ReadCString(br);
                    }
                    catch
                    {
                        node.Name = string.Empty;
                    }
                    finally
                    {
                        fs.Position = savePos;
                    }

                    res.Nodes[i] = node;
                }
            }

            Log($"[GFX] Read complete: {res.Nodes.Count} nodes, {res.Files.Count} mesh file(s).");
            return res;
        }

        private static Model3DGroup LoadFromGfxModelResInternal(string gfxModelResPath)
        {
            var gfx = ReadGfxModelRes(gfxModelResPath);
            if (gfx == null)
                return null;

            string baseDir = Path.GetDirectoryName(gfxModelResPath) ?? Directory.GetCurrentDirectory();

            var group = new Model3DGroup();
            int totalParts = 0;

            Log($"[GFX] Loading {gfx.Files.Count} mesh descriptor(s) from GFXmodelres.");

            foreach (var file in gfx.Files)
            {
                if (string.IsNullOrWhiteSpace(file))
                    continue;

                string descriptorPath = Path.Combine(baseDir, file);
                if (!File.Exists(descriptorPath))
                {
                    Log($"[GFX] Mesh descriptor not found: {descriptorPath}");
                    continue;
                }

                Log($"[GFX] Loading mesh descriptor from GFX: {descriptorPath}");

                var partGroup = LoadLegacyMeshDescriptor(descriptorPath);
                if (partGroup == null)
                {
                    Log($"[GFX] LoadLegacyMeshDescriptor returned null for {descriptorPath}");
                    continue;
                }

                foreach (var child in partGroup.Children)
                {
                    group.Children.Add(child);
                    totalParts++;
                }
            }

            Log($"[GFX] Finished. Added {totalParts} submesh(es) from GFXmodelres.");
            return group;
        }
        private static string ReadCString(BinaryReader br)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = br.ReadByte()) != 0)
            {
                bytes.Add(b);
            }
            return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static string BuildSuffixFromName(string name, string teamPrefix)
        {
            string baseName = Path.GetFileName(name);

            // Apply the detected cache-encoding convention. EAIDs always
            // hold "?lod=N&...&mesh" / "?lod=all&vb=N", but the on-disk
            // cache uses either URL-encoded ("%3F", "=", "&") names
            // (stock 2018) or underscore-substituted ("_3F", "_", "_")
            // names (classic). Replace each in turn -- when the detected
            // convention happens to match the source character (e.g. eq
            // is "=" for stock), the .Replace is a no-op.
            baseName = baseName.Replace("?", _cacheQueryEnc);
            if (_cacheEqualsEnc != "=")
                baseName = baseName.Replace("=", _cacheEqualsEnc);
            if (_cacheAmpEnc != "&")
                baseName = baseName.Replace("&", _cacheAmpEnc);

            int dotIdf = baseName.IndexOf(".idf", StringComparison.OrdinalIgnoreCase);
            if (dotIdf > 0)
            {
                string stem = baseName.Substring(0, dotIdf);
                baseName = baseName.Replace(stem, "");
            }
            else
            {
                if (!string.IsNullOrEmpty(teamPrefix))
                    baseName = baseName.Replace(teamPrefix, "");
            }

            return baseName;
        }

        private static VertexBufferInfo LoadBufVertex(string folder, string teamPrefix, string suffix)
        {
            // Use the detected separator (!!!  or ___) so this finds
            // the right cache files for both stock and classic content.
            string temp000 = Path.Combine(folder, $"{teamPrefix}{_cacheSeparator}temp000{suffix}");
            string temp001 = Path.Combine(folder, $"{teamPrefix}{_cacheSeparator}temp001{suffix}");

            Log($"  loadBufV: temp000='{temp000}', temp001='{temp001}'");

            var info = new VertexBufferInfo
            {
                Suffix = suffix,
                DataPath = temp001
            };

            if (!File.Exists(temp000))
            {
                Log("    ERROR: VB temp000 not found.");
                return info;
            }

            byte[] header = File.ReadAllBytes(temp000);
            if (header.Length < 16)
            {
                Log("    ERROR: VB temp000 header too small.");
                return info;
            }

            info.Type = BitConverter.ToUInt32(header, 0);
            info.Stride = BitConverter.ToInt32(header, 4);
            info.Count = BitConverter.ToInt32(header, 8);
            info.Fvf = BitConverter.ToUInt32(header, 12);

            return info;
        }

        private static IndexBufferInfo LoadBufIndex(string folder, string teamPrefix, string suffix)
        {
            // Use the detected separator (!!!  or ___) so this finds
            // the right cache files for both stock and classic content.
            string temp000 = Path.Combine(folder, $"{teamPrefix}{_cacheSeparator}temp000{suffix}");
            string temp001 = Path.Combine(folder, $"{teamPrefix}{_cacheSeparator}temp001{suffix}");

            Log($"  loadBufI: temp000='{temp000}', temp001='{temp001}'");

            var info = new IndexBufferInfo
            {
                Suffix = suffix,
                DataPath = temp001
            };

            if (!File.Exists(temp000))
            {
                Log("    ERROR: IB temp000 not found.");
                return info;
            }

            byte[] header = File.ReadAllBytes(temp000);
            if (header.Length < 16)
            {
                Log("    ERROR: IB temp000 header too small.");
                return info;
            }

            info.Type = BitConverter.ToUInt32(header, 0);
            info.IndexSize = BitConverter.ToInt32(header, 4);
            info.Count = BitConverter.ToInt32(header, 8);
            info.Fvf = BitConverter.ToUInt32(header, 12);

            if (info.IndexSize != 2 && info.IndexSize != 4)
            {
                Log($"    WARNING: Unusual indexSize={info.IndexSize}, forcing 4.");
                info.IndexSize = 4;
            }

            return info;
        }

        private static string DeriveLegacyMeshName(string meshFileName)
        {
            string name = Path.GetFileNameWithoutExtension(meshFileName) ?? "LegacyMesh";
            int qIndex = name.IndexOf("?", StringComparison.Ordinal);
            if (qIndex >= 0)
            {
                name = name.Substring(0, qIndex);
            }

            if (string.IsNullOrWhiteSpace(name))
                name = "LegacyMesh";

            return name;
        }

        private static float HalfToFloat(ushort h)
        {
            int sign = (h >> 15) & 0x00000001;
            int exp = (h >> 10) & 0x0000001F;
            int mant = h & 0x000003FF;

            int v = 0;
            if (exp == 0)
            {
                if (mant != 0)
                {
                    exp = 113;
                    while ((mant & 0x00000400) == 0)
                    {
                        mant <<= 1;
                        exp--;
                    }
                    mant &= 0x000003FF;
                    v = (sign << 31) | (exp << 23) | (mant << 13);
                }
                else v = (sign << 31);
            }
            else if (exp == 31) v = (sign << 31) | (0xFF << 23) | (mant << 13);
            else
            {
                exp = exp + (127 - 15);
                v = (sign << 31) | (exp << 23) | (mant << 13);
            }
            return BitConverter.ToSingle(BitConverter.GetBytes(v), 0);
        }

        private static void LogSubMeshStats(
            SubMeshInfo sm,
            VertexBufferInfo vb,
            IndexBufferInfo ib,
            int vertexCount,
            int indexCount,
            double minX, double minY, double minZ,
            double maxX, double maxY, double maxZ)
        {
            int triCount = indexCount / 3;

            Log(
                $"[SUBMESH] '{sm.Name}' " +
                $"VB={sm.VBufferIndex}, IB={sm.IBufferIndex}, " +
                $"IOffset={sm.IOffset}, IndexCount={sm.IndexCount}, " +
                $"VOffset={sm.VOffset}, " +
                $"VBStride={vb.Stride}, VBCount={vb.Count}, " +
                $"IBIndexSize={ib.IndexSize}, IBCount={ib.Count}");

            Log(
                $"[SUBMESH-STATS] '{sm.Name}' " +
                $"Vertices={vertexCount}, Indices={indexCount}, Triangles={triCount}, " +
                $"BBoxMin=({minX:F3}, {minY:F3}, {minZ:F3}), " +
                $"BBoxMax=({maxX:F3}, {maxY:F3}, {maxZ:F3})");
        }

        // =====================================================================
        // VERTEX POSITION DECODER
        // =====================================================================

        private struct PositionDecoder
        {
            public int Stride;   // bytes per vertex
            public int Offset;   // byte offset of position within a vertex
            public bool UseHalf; // true = 3×half, false = 3×float
            public bool IsValid;
        }

        private static PositionDecoder CreatePositionDecoder(VertexBufferInfo vb)
        {
            var dec = new PositionDecoder
            {
                Stride = vb.Stride,
                Offset = 0,
                UseHalf = false,
                IsValid = true
            };

            // 1) Prefer explicit layouts when we know Type/Fvf.
            //    (You can still fill these cases later.)

            // 1.5) Special-case: low-stride (≤8) aux VBs in classic-family
            //      packaging are NOT real position streams. They contain
            //      packed data for LCD, flags, rain beads, decal overlays,
            //      and (on F1 2020's classic Benetton 94 / McLaren 98 /
            //      etc.) the carbon-weave secondary records. Treating them
            //      as XYZ float3 produces the "big ball around the origin"
            //      symptom for aux/LCD streams, and (on carbon) reads
            //      half-float normal/UV bytes as positions -- a recognisable
            //      smeared / spiked distortion sitting on top of the
            //      undistorted carbon primary record.
            //
            //      The packaging-name gate exists so this filter doesn't
            //      false-fire on modern-2019+ (NEFX2) content, where
            //      stride-8 VBs ARE used as compressed position streams.
            //      The list below covers every packaging the modern-NEFX1
            //      9-int-header parser is reachable from: F1 2018 stock,
            //      the three F1 2018 numbered classic packages, and F1
            //      2020's bare unnumbered classic package. v12.07: F1 2020
            //      added; without it, F1 2020 classic content slipped past
            //      this gate and aux VBs got pushed through the position
            //      decoder as half-float XYZ, distorting carbon geometry
            //      on every classic Benetton 94 / McLaren 98 / etc. car
            //      shipped in the 2020 install.
            if (!string.IsNullOrEmpty(vb.DataPath) &&
                vb.Stride <= 8 &&
                (vb.DataPath.IndexOf("f1_2018_vehicle_package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 vb.DataPath.IndexOf("f1_classic_01_vehicle_package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 vb.DataPath.IndexOf("f1_classic_02_vehicle_package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 vb.DataPath.IndexOf("f1_classic_03_vehicle_package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 vb.DataPath.IndexOf("f1_classic_vehicle_package", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Log($"[Decoder] VB '{vb.Name}' (Stride={vb.Stride}) looks like modern-classic aux data, " +
                    "treating as non-position (skip).");
                dec.IsValid = false;
                return dec;
            }

            // 2) If we don't know the FVF yet, fall back to a simple heuristic.
            if (vb.Stride <= 0)
            {
                Log($"[Decoder] Invalid stride {vb.Stride} for VB '{vb.Name}'");
                dec.IsValid = false;
                return dec;
            }

            // Old rule: "small stride => half"
            dec.UseHalf = vb.Stride <= 8;
            dec.Offset = 0;

            Log($"[Decoder] Fallback layout for VB '{vb.Name}': " +
                $"Type=0x{vb.Type:X8}, Fvf=0x{vb.Fvf:X8}, Stride={vb.Stride}, " +
                $"UseHalf={dec.UseHalf}, Offset={dec.Offset}");

            return dec;
        }

        private static Point3D DecodePosition(
            in PositionDecoder dec,
            byte[] vData,
            long vStart,
            int vertexIndex)
        {
            long off = vStart + (long)vertexIndex * dec.Stride;
            float x, y, z;

            if (dec.UseHalf)
            {
                ushort hx = BitConverter.ToUInt16(vData, (int)off + dec.Offset + 0);
                ushort hy = BitConverter.ToUInt16(vData, (int)off + dec.Offset + 2);
                ushort hz = BitConverter.ToUInt16(vData, (int)off + dec.Offset + 4);
                x = HalfToFloat(hx);
                y = HalfToFloat(hy);
                z = HalfToFloat(hz);
            }
            else
            {
                x = BitConverter.ToSingle(vData, (int)off + dec.Offset + 0);
                y = BitConverter.ToSingle(vData, (int)off + dec.Offset + 4);
                z = BitConverter.ToSingle(vData, (int)off + dec.Offset + 8);
            }

            return new Point3D(x, y, z);
        }

        private static int? ExtractLodValueFromPath(string path)
        {
            const string token = "lod=";
            int idx = path.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;

            idx += token.Length;
            int start = idx;
            while (idx < path.Length && char.IsDigit(path[idx]))
            {
                idx++;
            }

            if (idx == start)
                return null;

            string numStr = path.Substring(start, idx - start);
            if (int.TryParse(numStr, out int val))
                return val;

            return null;
        }

        // =====================================================================
        // COMPANION-STREAM DECODERS (F1 2018 NORMAL + UV SUPPORT)
        // =====================================================================
        //
        // These three helpers are consumed by the per-submesh loop's
        // "F1 2018 COMPANION-STREAM DECODING" block. They exist only to
        // populate MeshGeometry3D.Normals / TextureCoordinates from the
        // sibling VBs that the classic loader's previous builds were
        // reading into memory and throwing away.
        //
        // Every helper is self-contained, throws no user-facing exceptions
        // beyond what the caller already try/catches, and returns null /
        // an empty collection / -1 for any case it can't handle. The
        // existing flat-shaded code path is reached whenever a helper
        // returns a "not-applicable" result, so adding these is strictly
        // additive for any classic ERP whose layout doesn't match the
        // 2018 stride-based convention documented above.

        /// <summary>
        /// Tests whether a submesh slot name designates a carbon-fibre
        /// material slot. Codemasters uses names like "carbon tile 1",
        /// "carbon tile 3", "carbon tile 4", "carbon_tile_1",
        /// "carbon number", etc. on F1 cars; they all start with the
        /// word "carbon" (case-insensitive, possibly preceded by
        /// whitespace). Anything else returns false.
        ///
        /// Used by the per-submesh UV attach block to decide whether to
        /// apply the <see cref="CarbonTileMultiplier"/> tile-rate
        /// upscale when the submesh's UVs look atlas-shaped.
        /// </summary>
        private static bool IsCarbonSlotName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int i = 0;
            while (i < name.Length && char.IsWhiteSpace(name[i])) i++;
            return i + 6 <= name.Length &&
                   (name[i] == 'c' || name[i] == 'C') &&
                   (name[i + 1] == 'a' || name[i + 1] == 'A') &&
                   (name[i + 2] == 'r' || name[i + 2] == 'R') &&
                   (name[i + 3] == 'b' || name[i + 3] == 'B') &&
                   (name[i + 4] == 'o' || name[i + 4] == 'O') &&
                   (name[i + 5] == 'n' || name[i + 5] == 'N');
        }

        /// <summary>
        /// Case-insensitive search for an ASCII substring inside a byte
        /// buffer. Used to detect 2018-era ERPs by scanning mesh
        /// descriptor contents for year markers like "f1_2018_vehicle_
        /// package", since the filesystem path no longer reliably carries
        /// the year after recent ERP-cache changes.
        ///
        /// Byte-level search is deliberate: calling
        /// Encoding.ASCII.GetString on an entire descriptor can produce
        /// replacement characters around embedded binary fields and hide
        /// the marker from plain String.IndexOf. A simple Boyer-Moore-ish
        /// byte scan avoids that and is plenty fast for the small (&lt;10
        /// KB) descriptors we're dealing with.
        /// </summary>
        private static bool DescriptorContainsAscii(byte[] data, string needle)
        {
            if (data == null || string.IsNullOrEmpty(needle)) return false;
            if (data.Length < needle.Length) return false;

            // Build lowercase ASCII needle for case-insensitive compare.
            byte[] pat = new byte[needle.Length];
            for (int i = 0; i < needle.Length; i++)
            {
                char c = needle[i];
                if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
                pat[i] = (byte)c;
            }

            int limit = data.Length - pat.Length;
            for (int i = 0; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < pat.Length; j++)
                {
                    byte b = data[i + j];
                    // Lowercase byte b if it's A-Z.
                    if (b >= (byte)'A' && b <= (byte)'Z') b = (byte)(b + 32);
                    if (b != pat[j]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }

        /// <summary>
        /// (v12.11-aug) Extended classic / stock-year package marker
        /// scan. Complements -- never replaces -- the hardcoded
        /// is2018-detection whitelist higher up in
        /// LoadLegacyMeshDescriptor.
        ///
        /// The original whitelist hardcodes a small fixed set
        /// (f1_2018_vehicle_package, f1_classic_01/02/03_vehicle_package,
        /// f1_classic_vehicle_package, plus a couple of short-form
        /// variants). F1 2019 shipped a classic expansion -- 1990
        /// Ferrari, 1985 McLaren, 1979 Ferrari, etc. -- in numbered
        /// classic packages BEYOND _03, so descriptors from those cars
        /// fall through every existing marker and the parser silently
        /// downgrades to its 2016/2017-style fallback branch (one
        /// 'LegacyMesh' submesh per descriptor, hardcoded VOffset=0).
        /// That branch produces the textureless + distorted symptom
        /// the user reported on the F1 2019 1990 Ferrari (no
        /// material-EAID resolution for slot 'LegacyMesh' -> gray; all
        /// 88-byte mesh records inside the descriptor collapsed into
        /// one large mesh with the wrong vertex-offset baseline).
        ///
        /// This helper reuses <see cref="DescriptorContainsAscii"/> for
        /// the actual byte scan -- the candidate list is the only new
        /// surface area. The "30" / "2025" upper bounds are headroom;
        /// the existing whitelist owns 2018 and below.
        /// </summary>
        private static bool DescriptorContainsExtendedClassicMarker(byte[] data)
        {
            if (data == null || data.Length < 16) return false;

            // Numbered classic packages 04..30. Existing whitelist
            // owns 01..03 already, so starting at 04 avoids redundant
            // scans on descriptors the original check would have
            // matched anyway.
            for (int n = 4; n <= 30; n++)
            {
                string candidate = "f1_classic_" + n.ToString("D2") + "_vehicle_package";
                if (DescriptorContainsAscii(data, candidate))
                    return true;
            }

            // Stock-year packages 2019..2025. 2018 is owned by the
            // existing whitelist; we cover the years this tool
            // currently targets and stop there.
            for (int y = 2019; y <= 2025; y++)
            {
                string candidate = "f1_" + y.ToString() + "_vehicle_package";
                if (DescriptorContainsAscii(data, candidate))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// (v12.12-struct) Structural detector for the 36-byte (9-int)
        /// classic mesh-descriptor header layout. Used by
        /// LoadLegacyMeshDescriptor to recognise F1 2019 classic-
        /// expansion content (e.g. the 1990 Ferrari) whose embedded
        /// EAIDs reference a package name that doesn't match any of
        /// the existing string-marker whitelists. Returns true when
        /// the first 36 bytes parse as a sane 9-int header AND the
        /// first VB name string sits where a 36-byte header would
        /// place it, with content that looks like an EAID.
        ///
        /// Designed to be sharply biased against false positives on
        /// real stock-2018 (76-byte header) descriptors: for those,
        /// the probe position (36 + vbufOff) lands 40 bytes ahead of
        /// the correct name offset, which is almost always non-
        /// printable binary content (mesh-record fields, padding) and
        /// is rejected by the printable-ASCII + "vb=" / ".idf" checks
        /// below.
        ///
        /// Bounds were chosen empirically against the descriptor
        /// counts observed across F1 2018-2020 stock and classic
        /// content. They are generous on the high side (e.g.
        /// numMeshes up to 1000) so future titles with larger meshes
        /// per descriptor still match.
        /// </summary>
        private static bool LooksLikeClassic36ByteHeader(byte[] data)
        {
            const int ClassicHeaderSize = 36; // 9 * 4

            if (data == null || data.Length < ClassicHeaderSize + 4) return false;

            int numMeshes;
            int numVBuf;
            int numIBuf;
            int labelCount;
            int numNodeLinks;
            int vbufOff;
            int ibufOff;
            int labelOff;
            int nodeLinkOff;
            try
            {
                numMeshes = BitConverter.ToInt32(data, 0);
                numVBuf = BitConverter.ToInt32(data, 4);
                numIBuf = BitConverter.ToInt32(data, 8);
                labelCount = BitConverter.ToInt32(data, 12);
                numNodeLinks = BitConverter.ToInt32(data, 16);
                vbufOff = BitConverter.ToInt32(data, 20);
                ibufOff = BitConverter.ToInt32(data, 24);
                labelOff = BitConverter.ToInt32(data, 28);
                nodeLinkOff = BitConverter.ToInt32(data, 32);
            }
            catch
            {
                return false;
            }

            // Sanity bounds on count fields. These ranges cover every
            // F1 2018-2025 descriptor we've inspected and reject most
            // 76-byte-header descriptors whose int[1]/int[2] values
            // tend to be either zero or absurdly large when reinterpreted
            // as 9-int header fields.
            if (numMeshes < 1 || numMeshes > 1000) return false;
            if (numVBuf < 1 || numVBuf > 100) return false;
            if (numIBuf < 1 || numIBuf > 50) return false;
            if (labelCount < 0 || labelCount > 500) return false;
            if (numNodeLinks < 0 || numNodeLinks > 1000) return false;

            // Offsets must land somewhere inside the file when added
            // to the 36-byte header base. The labelOff / nodeLinkOff
            // checks are also folded in even though we don't strictly
            // need them, as they catch additional non-classic
            // descriptors cheaply.
            int max = data.Length - ClassicHeaderSize - 1;
            if (vbufOff < 0 || vbufOff > max) return false;
            if (ibufOff < 0 || ibufOff > max) return false;
            if (labelOff < 0 || labelOff > max) return false;
            if (nodeLinkOff < 0 || nodeLinkOff > max + 1) return false;

            // The VB-table region must come BEFORE the IB-table region
            // and the label-table region, with at least one byte of
            // payload before each next offset. Stock-2018 descriptors
            // reinterpreted as 36-byte headers typically violate this
            // ordering because their h[5] / h[6] / h[7] mean something
            // entirely different.
            if (!(vbufOff < ibufOff && ibufOff < labelOff)) return false;

            // Probe the first VB-name string at (36 + vbufOff). For a
            // genuine 36-byte-header descriptor this is the start of a
            // null-terminated UTF-8 EAID. For a misidentified 76-byte
            // descriptor this lands 40 bytes before the real first
            // name and is essentially always non-printable binary.
            int firstNamePos = ClassicHeaderSize + vbufOff;
            if (firstNamePos < 0 || firstNamePos >= data.Length) return false;

            byte first = data[firstNamePos];
            if (first < 0x20 || first > 0x7E) return false;

            // Walk the C-string. Cap the scan length to avoid wandering
            // off into the rest of the descriptor if a null was missed.
            int term = firstNamePos;
            int hardCap = Math.Min(data.Length, firstNamePos + 1024);
            while (term < hardCap && data[term] != 0)
            {
                byte c = data[term];
                // Allow printable ASCII only. EAIDs are always 7-bit
                // ASCII; any high-bit byte indicates we've wandered
                // into binary territory.
                if (c < 0x20 || c > 0x7E) return false;
                term++;
            }
            if (term >= hardCap) return false; // no null found in range -- not a string
            int len = term - firstNamePos;
            if (len < 5 || len > 1000) return false;

            // The string must look like an EAID or VB path -- contain
            // at least one of the canonical substrings the loader's
            // suffix parser depends on.
            string name = System.Text.Encoding.ASCII.GetString(data, firstNamePos, len);
            bool looksLikeEaid =
                name.IndexOf("vb=", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf(".idf", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!looksLikeEaid) return false;

            return true;
        }

        /// <summary>
        /// Walk <paramref name="vbufs"/> starting just after
        /// <paramref name="posVbIdx"/> and return the index of the
        /// <paramref name="skipMatches"/>-th VB whose Stride equals
        /// <paramref name="expectedStride"/> AND whose Count matches the
        /// position VB's Count. Returns -1 when no such buffer is found.
        ///
        /// The "same count" requirement is what enforces stream
        /// co-membership in 2018: every buffer in one vertex group ships
        /// the same number of entries. Different groups have different
        /// counts (e.g. 293988 vs 82029 on the McLaren 2018 body), so the
        /// count check correctly stops the walk before it leaks into a
        /// sibling group.
        ///
        /// skipMatches=0 picks the FIRST match (normals), skipMatches=2
        /// picks the third match (UV: normal, tangent, UV in order).
        /// </summary>
        private static int FindCompanionStreamIndex(
            List<VertexBufferInfo> vbufs,
            int posVbIdx,
            int expectedStride,
            int skipMatches)
        {
            if (vbufs == null) return -1;
            if (posVbIdx < 0 || posVbIdx >= vbufs.Count) return -1;
            if (expectedStride <= 0) return -1;
            if (skipMatches < 0) return -1;

            int targetCount = vbufs[posVbIdx].Count;
            int matchesSeen = 0;

            for (int i = posVbIdx + 1; i < vbufs.Count; i++)
            {
                var vb = vbufs[i];
                if (vb == null) continue;
                if (vb.Count != targetCount) continue;
                if (vb.Stride != expectedStride) continue;

                if (matchesSeen == skipMatches)
                    return i;

                matchesSeen++;
            }

            return -1;
        }

        /// <summary>
        /// Decode (maxIndex + 1) normal vectors from a stride-8 vertex
        /// buffer payload where each vertex holds 4 x IEEE-754 half-floats
        /// (xyz + w). Only xyz is used; w is typically 0 in 2018 but
        /// occasionally carries a sign bit that we don't need at the
        /// normal stage. Each decoded vector is normalised so that WPF's
        /// lighting math produces stable results even when the half-float
        /// round-trip leaves the vector slightly off unit length.
        ///
        /// Returns a Vector3DCollection sized exactly (maxIndex + 1) so
        /// the caller's Count==positions.Count guard passes. Returns an
        /// empty (but non-null) collection when maxIndex &lt; 0 -- there
        /// is no geometry to shade in that case.
        /// </summary>
        private static Vector3DCollection DecodeHalf4NormalsBlock(
            byte[] data, long start, int stride, int maxIndex)
        {
            var result = new Vector3DCollection(Math.Max(0, maxIndex + 1));
            if (data == null || stride < 6 || maxIndex < 0) return result;

            for (int i = 0; i <= maxIndex; i++)
            {
                long o = start + (long)i * stride;
                int oi = checked((int)o);

                float nx = ReadHalfFloat(data, oi + 0);
                float ny = ReadHalfFloat(data, oi + 2);
                float nz = ReadHalfFloat(data, oi + 4);

                double len = Math.Sqrt((double)nx * nx + (double)ny * ny + (double)nz * nz);
                if (len < 1e-6)
                {
                    // Degenerate normal (uncommon but defensive): fall
                    // back to +Y so the vertex contributes a sane shade
                    // rather than a black artefact.
                    result.Add(new Vector3D(0, 1, 0));
                }
                else
                {
                    result.Add(new Vector3D(nx / len, ny / len, nz / len));
                }
            }

            return result;
        }

        /// <summary>
        /// Decode (maxIndex + 1) UV coordinates from a stride-8 vertex
        /// buffer payload where each vertex holds 2 x IEEE-754 half-floats
        /// in the first 4 bytes and 4 bytes of padding / secondary data
        /// after. This is the body/detail/decal atlas UV stream in 2018.
        ///
        /// No V-flip is applied: F1 2018 stores UVs with (0,0) at the
        /// top-left of the texture, which matches WPF's ImageBrush
        /// convention. If a future texture probe shows the atlases
        /// rendering upside-down, wire a FlipTextureV toggle here
        /// analogous to F1ModelLoader.FlipTextureV.
        /// </summary>
        private static PointCollection DecodeHalf2UvsBlock(
            byte[] data, long start, int stride, int maxIndex)
        {
            var result = new PointCollection(Math.Max(0, maxIndex + 1));
            if (data == null || stride < 4 || maxIndex < 0) return result;

            for (int i = 0; i <= maxIndex; i++)
            {
                long o = start + (long)i * stride;
                int oi = checked((int)o);

                float u = ReadHalfFloat(data, oi + 0);
                float v = ReadHalfFloat(data, oi + 2);

                result.Add(new System.Windows.Point(u, v));
            }

            return result;
        }

        /// <summary>
        /// Decode an IEEE-754 binary16 ("half") stored little-endian at
        /// <paramref name="offset"/> within <paramref name="data"/>,
        /// returning a 32-bit float. Copied behaviour-for-behaviour from
        /// F1ModelLoader's half decoder so both loaders agree on edge
        /// cases (subnormals, inf, NaN).
        /// </summary>
        private static float ReadHalfFloat(byte[] data, int offset)
        {
            ushort h = BitConverter.ToUInt16(data, offset);
            int sign = (h >> 15) & 0x1;
            int exp = (h >> 10) & 0x1F;
            int frac = h & 0x3FF;

            if (exp == 0)
            {
                if (frac == 0)
                    return sign == 0 ? 0f : -0f;
                // Subnormal.
                float sub = frac * (float)Math.Pow(2.0, -14 - 10);
                return sign == 0 ? sub : -sub;
            }

            if (exp == 0x1F)
            {
                if (frac == 0) return sign == 0 ? float.PositiveInfinity : float.NegativeInfinity;
                return float.NaN;
            }

            float m = 1.0f + (frac / 1024.0f);
            float val = m * (float)Math.Pow(2.0, exp - 15);
            return sign == 0 ? val : -val;
        }
    }
}