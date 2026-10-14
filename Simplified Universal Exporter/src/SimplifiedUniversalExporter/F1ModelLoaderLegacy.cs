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
    public static class F1ModelLoaderLegacy
    {
        // =====================================================================
        // LOGGING
        // =====================================================================

        private static string _logPath = "";
        private static int _totalSubmeshes = 0;
        private static int _totalIndices = 0;
        private static int _totalTriangles = 0;
        private static int _totalVertices = 0;

        // Game version detection
        // 0  = unknown (treated as 2019)
        // 2019 = F1 2019
        // 2020 = F1 2020
        private static int _currentGameYear = 0;

        // Used only in 2020 mode to cull huge / broken “ball” meshes
        private const double MaxReasonableExtent2020 = 50.0;

        // Explicit-LOD override for <see cref="LoadFolderExplicitLod"/>.
        // When non-null, the LOD-selection block in
        // <see cref="LoadFolder(string, string)"/> bypasses its usual
        // "pick lod=0 > pick lod=all > pick smallest-numeric" heuristic
        // and instead uses ONLY descriptor files whose filename contains
        // "lod={value}". Mirror of F1ModelLoaderClassic._requestedExplicitLod.
        // Without this, the glTF exporter's foreach-LOD loop produced
        // {team}_LOD0.gltf .. {team}_LOD7.gltf files all containing
        // identical lod=0 geometry on 2019/2020 cars, because
        // LoadGroupForYearAndLod called Legacy.LoadFolder(folder, log)
        // with no lod argument and the loader hardcoded "lod=0" at
        // its filename filter. With this field set the loader honours
        // the requested LOD and returns an empty group if no
        // descriptors match (preferred over silently writing lod=0
        // data into a file named for a different LOD).
        //
        // Always set from inside LoadFolderExplicitLod's try/finally so
        // a throw inside LoadFolder can't leak this state into a
        // subsequent call on the same thread.
        private static int? _requestedExplicitLod = null;

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
                File.WriteAllText(_logPath, "F1ModelLoaderLegacy Log\r\n");
            }
            catch
            {
                // ignore
            }

            _totalSubmeshes = 0;
            _totalIndices = 0;
            _totalTriangles = 0;
            _totalVertices = 0;

            // Reset game year detection for this load
            _currentGameYear = 0;
        }

        // =====================================================================
        // TEXTURE PIPELINE STATE (Phase 2)
        // =====================================================================
        //
        // Mirrors F1ModelLoaderClassic's texture-pipeline state. Populated
        // by AutoConfigureTextureCache at LoadFolder-entry, cleared on
        // re-entry so a second LoadFolder can't leak state from the first.
        //
        // _textureCache        - decoded-brush cache for *.material /
        //                        *.tif pairs under the package root
        // _activeContext       - loaded GfxMatBindRes ("emb?context=N")
        //                        which the slot -> material mapping uses
        //                        as the authoritative binding table;
        //                        falls back to the hardcoded per-slot
        //                        table below when a slot isn't bound
        //                        in the current context
        // _detectedPackageName - e.g. "f1_2019_vehicle_package", used
        //                        when building material EAIDs
        private static TextureCache _textureCache;
        private static string _detectedPackageName;
        private static EmbContextReader _activeContext;

        /// <summary>
        /// Which driver's livery context to prefer when a descriptor has
        /// multiple emb?context=N files. F1 2019 McLaren drivers are
        /// Norris (4) and Sainz (55); default 4 renders Norris. Any
        /// number not present in this ERP falls back to context=1
        /// (first available) so the loader doesn't crash on a wrong
        /// default. Only affects the 'number' slot visually -- every
        /// other slot (body, generic, carbon, etc.) binds to the same
        /// material across all contexts, so picking the "wrong" driver
        /// only changes which number atlas renders.
        /// </summary>
        public static int PreviewDriverNumber { get; set; } = 4;

        /// <summary>
        /// Inject a pre-built TextureCache. When set before LoadFolder,
        /// AutoConfigureTextureCache honours it and skips its own
        /// TextureCache construction. Useful for tests / batch renders
        /// that share a cache across multiple cars.
        /// </summary>
        public static void SetTextureCache(TextureCache cache) => _textureCache = cache;

        /// <summary>
        /// Read-only accessor for the currently-loaded TextureCache.
        /// Mirrors F1ModelLoaderClassic.ActiveTextureCache so callers
        /// can resolve textures for whichever loader populated them.
        ///
        /// <para>F1FbxExporter.EnsureTexturePng consults both loaders'
        /// caches (Classic first, then Legacy). Before this accessor
        /// existed, a 2019/2020 export had nowhere to fetch decoded
        /// pixels from -- the Classic cache was always null on those
        /// years, the Legacy cache was unreachable, and every material
        /// shipped to the exported glTF without its diffuse texture
        /// (visible to the user as flat-gray paint, carbon, generic,
        /// number, wheel, etc.).</para>
        /// </summary>
        public static TextureCache ActiveTextureCache => _textureCache;

        /// <summary>
        /// Override PreviewDriverNumber programmatically.
        /// </summary>
        public static void SetPreviewDriverNumber(int n) => PreviewDriverNumber = n;

        // =====================================================================
        // UV-TRANSFORM KNOBS (ported from F1ModelLoaderClassic, Phase 2.5)
        // =====================================================================
        //
        // F1 2018/2019/2020 ERPs author carbon-weave UVs at tile rates in
        // the ±50..+70 range so the live game shader can scale them again
        // by a per-material factor. A raw preview that samples mclaren_
        // carbon.tif at those rates produces visual noise instead of a
        // readable weave. These knobs let the preview approximate the
        // missing shader-side scale.
        //
        // All knobs mirror their F1ModelLoaderClassic counterparts one-
        // for-one so behaviour is consistent with 2018.

        /// <summary>
        /// Maximum absolute UV value before a submesh's UVs are scaled down.
        /// Safetycell's "carbon_tile_1" in 2019 McLaren hits max|UV| ≈ 335
        /// (observed -308..+325 across both axes), which renders as pure
        /// visual noise; capping at 256 brings it to ~64 effective tile
        /// repeats per panel, matching what the game's shader shows.
        /// </summary>
        public static double MaxUvTileRate { get; set; } = 256.0;

        /// <summary>
        /// When a carbon-slot submesh ships with its UVs still in atlas
        /// range (max|UV| at or below this threshold), we multiply by
        /// <see cref="CarbonTileMultiplier"/> to fake the shader's per-
        /// material scale. Above this threshold the multiplier is skipped
        /// (the normal <see cref="MaxUvTileRate"/> cap takes over). A
        /// value of 2 lets through UVs that slightly overflow [0,1] (as
        /// body/detail commonly do for edge padding) without false-
        /// triggering on them.
        /// </summary>
        public static double CarbonAtlasThreshold { get; set; } = 2.0;

        /// <summary>
        /// Multiplier applied to carbon-slot UVs that are still in atlas
        /// range, gated by <see cref="CarbonAtlasThreshold"/>. The 2021+
        /// loader applies a similar multiplier through its shader
        /// classifier.
        ///
        /// <para>Default 128.0, locked in lockstep with
        /// <see cref="F1ModelLoaderClassic.CarbonTileMultiplier"/>.
        /// The 2018 loader uses 128 and the user has confirmed the
        /// 2018 carbon look is correct, so the 2019/2020 path here
        /// runs the byte-identical transform: same threshold, same
        /// multiplier value, same plain <c>p.X * k, p.Y * k</c>
        /// scale (no recentre, no asymmetric-bbox compensation).
        /// If the multiplier ever needs to change for a year-specific
        /// reason, it must change in both files together.</para>
        ///
        /// <para>For the 2019 McLaren ERP this knob fires on the
        /// fallback-path carbon submeshes (camera_wing,
        /// frontspoiler_fm, hub_caliper_f*, sidewing_*, diffuser_bm,
        /// suspension*_fl/fr -- the 15 submeshes whose
        /// vertexFileOffset lands on a stride-8 secondary-group VB)
        /// because their decoded pair-1 UVs span [0, 1.033], well
        /// below CarbonAtlasThreshold=2.0. The remaining 27 carbon
        /// submeshes (warm path) decode UVs already authored at ~300
        /// tile-rep, fail the threshold gate, and skip the multiplier
        /// -- exactly as Classic does for any carbon submesh that
        /// happens to have pre-multiplied UVs.</para>
        ///
        /// <para>The downstream <see cref="MaxUvTileRate"/> cap of
        /// 256 still applies to both paths and clamps anything above
        /// 256 back down. After the multiplier and cap, fallback-path
        /// final span is ~132 weave reps; warm-path final span is
        /// ~470 reps. The 2018 carbon mat has historically rendered
        /// at ~132 reps everywhere because Classic's UV layout is
        /// uniform across all 2018 carbon submeshes. Mirroring that
        /// algorithm exactly is the user's stated success
        /// criterion.</para>
        /// </summary>
        public static double CarbonTileMultiplier { get; set; } = 128.0;

        /// <summary>
        /// Additive U offset applied to pair-1 UVs of the "generic" slot
        /// (and variants: underscore / case-insensitive).
        ///
        /// F1 2019 McLaren safetycell's generic submesh has its authored
        /// pair-1 UVs confined to U=[0.002, 0.492] (left half of the
        /// atlas), sampled against shared/generics/textures/generic_main_d.tif.
        /// The live game's generics shader likely adds a per-material
        /// uOffset to pull the sample into the right half of the atlas;
        /// without that translation, our preview samples the left half
        /// and the user sees "wrong atlas region" on every generic-slot
        /// mesh. Default 0 keeps old behaviour; set to 0.5 to test the
        /// half-atlas-shift hypothesis before we ship a material-derived
        /// value. Publicly settable so it can be iterated at runtime
        /// without a rebuild.
        /// </summary>
        public static double GenericUOffset { get; set; } = 0.0;

        /// <summary>
        /// Additive V offset counterpart to <see cref="GenericUOffset"/>.
        /// Default 0; flip to a non-zero value only if U-shift alone
        /// doesn't land the right atlas region.
        /// </summary>
        public static double GenericVOffset { get; set; } = 0.0;

        /// <summary>
        /// Override the material EAID used for the "generic" slot, bypassing
        /// both the hardcoded BuildMaterialEaidForSlot table and the
        /// GfxMatBindRes context lookup. A value of null (the default)
        /// disables the override.
        ///
        /// Example values to iterate on for F1 2019 McLaren when
        /// generics.material / generic_main_d.tif looks wrong:
        ///   "eaid://f1_2019_vehicle_package/teams/mclaren/materials/mclaren_detail.material"
        ///   "eaid://f1_2019_vehicle_package/teams/mclaren/materials/mclaren_paint.material"
        ///
        /// The 2018 Classic loader empirically uses mclaren_detail.material
        /// for the generic slot (see 2018 Classic log), but the 2019
        /// context genuinely binds generic -> shared/generics.material.
        /// This knob exists so users who prefer the 2018-style visual
        /// can opt in without a rebuild.
        /// </summary>
        public static string GenericMaterialOverride { get; set; } = null;

        /// <summary>
        /// Enable verbose dumping of all parameters in every resolved
        /// .material file (parameter name, type, and for floats also the
        /// value). Useful for discovering UV-transform parameters like
        /// me_uOffset / me_uScale / me_tileRate that differ between
        /// 2018 and 2019 materials and that our preview might not be
        /// applying. Default off because the output is large on cars
        /// with 80+ submeshes.
        /// </summary>
        public static bool VerboseMaterialParamDump { get; set; } = false;

        /// <summary>
        /// Skip the "unioned" root descriptor (e.g.
        /// <c>mclaren!!!temp000.idf?lod=0&amp;mesh</c> with no leaf-part
        /// segment) that contains every submesh of the car at once.
        ///
        /// 2019 McLaren ships both a root descriptor (16 meshes covering
        /// the whole car) AND ~60 per-part leaf descriptors
        /// (<c>&amp;safetycell&amp;mesh</c>, <c>&amp;frontwing_fl_01&amp;mesh</c>, ...).
        /// Each leaf's mesh records reference the same shared IB/VBs at
        /// the same <c>iofs</c> values as the root — so processing both
        /// draws every triangle twice. For F1 2019 McLaren safetycell's
        /// carbon_tile_1 this meant drawing 78 789 indices from the leaf
        /// AND 188 385 indices from the root, with the leaf's triangles
        /// wholly contained in the root's set.
        ///
        /// Default true to match the leaf-only intent of the existing
        /// descriptor loop. Set false to re-enable root processing for
        /// comparison (e.g. when a car ships ONLY a root descriptor and
        /// no leaves, which we haven't seen in 2019 but might appear in
        /// older packages).
        /// </summary>
        public static bool SkipRootDescriptor { get; set; } = true;

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

        private static bool IsGenericSlotName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim().ToLowerInvariant().Replace('_', ' ');
            return n == "generic";
        }

        /// <summary>
        /// Slot-name predicate for the 'body' slot. Used by the 2020
        /// tile-rate-cap bypass at the pair-1 transform site. Matches the
        /// stock label string 'body' exactly after standard
        /// trim/lower/replace-underscore-with-space normalisation, the
        /// same convention IsGenericSlotName uses. Does NOT match
        /// 'something_body' or 'body_something' or any compound -- the
        /// bypass should only apply to the literal 'body' submesh slot.
        /// </summary>
        private static bool IsBodySlotName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim().ToLowerInvariant().Replace('_', ' ');
            return n == "body";
        }

        /// <summary>
        /// Detect the "unioned" root mesh descriptor that covers the whole
        /// car in a single file, as opposed to a leaf descriptor scoped to
        /// a single part like safetycell or frontwing_fl_01.
        ///
        /// Accepts both the URL-encoded "%26" and plain "&amp;" separators
        /// because ERP cache path encoding varies between extractor
        /// versions. Case-insensitive.
        ///
        /// Root shape:  ".../mclaren!!!temp000.idf%3Flod=0&mesh"
        /// Leaf shape:  ".../mclaren!!!temp000.idf%3Flod=0&safetycell&mesh"
        /// (the marker is whether there's ANY character between the
        /// "lod=N&" segment and the trailing "&mesh").
        /// </summary>
        private static bool IsRootDescriptorPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) return false;

            StringComparison ci = StringComparison.OrdinalIgnoreCase;

            // Find the "lod=" marker first, then look at what immediately
            // follows the digits+separator. If it's just "mesh" with no
            // part name in between, it's a root.
            int lodAt = name.IndexOf("lod=", ci);
            if (lodAt < 0) return false;

            // Walk past "lod=" and skip the digits that follow
            int cursor = lodAt + 4;
            while (cursor < name.Length && (char.IsDigit(name[cursor]) || name[cursor] == 'a'
                                             || name[cursor] == 'l'))
            {
                // "lod=all" is also acceptable; we're skipping [0-9a-z] until
                // we hit a separator
                cursor++;
            }
            if (cursor >= name.Length) return false;

            // Next must be a separator: plain "&" or URL-encoded "%26"
            string rest;
            if (name[cursor] == '&')
                rest = name.Substring(cursor + 1);
            else if (cursor + 3 <= name.Length &&
                     string.Equals(name.Substring(cursor, 3), "%26", ci))
                rest = name.Substring(cursor + 3);
            else
                return false;

            // If `rest` is exactly "mesh" (case-insensitive), there was no
            // leaf segment between lod=N and mesh → root descriptor.
            return string.Equals(rest, "mesh", ci);
        }

        /// <summary>
        /// Diagnostic: dump every float parameter in a .material file to the
        /// log. Uses the GfxMaterialRes binary layout documented in
        /// EgoTexturePipeline.MaterialParameterReader. Silent on parse
        /// failure so a broken material file never crashes geometry load.
        ///
        /// Specifically looking for UV-transform parameters the game's
        /// shader applies but our preview ignores:
        ///   me_uOffset, me_vOffset       - additive UV translation
        ///   me_uScale,  me_vScale        - multiplicative UV scale
        ///   me_tileRate, me_uTile, me_vTile - tile-rate variants
        /// Any non-zero value for these explains why a "correct" texture
        /// binding still produces wrong visuals at raw UV sample positions.
        /// </summary>
        private static void DumpMaterialParameters(string slotTag, byte[] data)
        {
            try
            {
                if (data == null || data.Length < 16) return;
                int parameterCount = BitConverter.ToInt32(data, 0);
                int paramNameOffset = BitConverter.ToInt32(data, 8);
                if (parameterCount <= 0 || parameterCount > 256)
                {
                    Log($"  [{slotTag}] params: count out of range ({parameterCount})");
                    return;
                }
                int dataPoolBase = 16 + 16 * parameterCount;
                if (dataPoolBase > data.Length) return;
                int nameBase = dataPoolBase + paramNameOffset;
                if (nameBase < 0 || nameBase >= data.Length) return;

                var parts = new List<string>(parameterCount);
                for (int i = 0; i < parameterCount; i++)
                {
                    int eo = 16 + i * 16;
                    if (eo + 16 > data.Length) break;
                    uint nameOfs = BitConverter.ToUInt32(data, eo + 0);
                    uint type = BitConverter.ToUInt32(data, eo + 4);
                    uint size = BitConverter.ToUInt32(data, eo + 8);
                    uint dataOfs = BitConverter.ToUInt32(data, eo + 12);

                    // Name lookup
                    int np = nameBase + (int)nameOfs;
                    if (np < 0 || np >= data.Length) continue;
                    int ne = np;
                    while (ne < data.Length && data[ne] != 0) ne++;
                    if (ne <= np) continue;
                    string name = System.Text.Encoding.ASCII.GetString(data, np, ne - np);

                    // Value: type 1 (CONSTANT_BUFFER) with size 4/12/16 → float/vec3/vec4
                    string val;
                    if (type == 1)
                    {
                        int vp = dataPoolBase + (int)dataOfs;
                        if (vp < 0 || vp >= data.Length) { val = "<oob>"; }
                        else if (size == 4 && vp + 4 <= data.Length)
                        {
                            float f = BitConverter.ToSingle(data, vp);
                            val = float.IsFinite(f) ? f.ToString("G6", CultureInfo.InvariantCulture) : "<nan>";
                        }
                        else if (size == 12 && vp + 12 <= data.Length)
                        {
                            float a = BitConverter.ToSingle(data, vp + 0);
                            float b = BitConverter.ToSingle(data, vp + 4);
                            float c = BitConverter.ToSingle(data, vp + 8);
                            val = $"({a:G4},{b:G4},{c:G4})";
                        }
                        else if (size == 16 && vp + 16 <= data.Length)
                        {
                            float a = BitConverter.ToSingle(data, vp + 0);
                            float b = BitConverter.ToSingle(data, vp + 4);
                            float c = BitConverter.ToSingle(data, vp + 8);
                            float d = BitConverter.ToSingle(data, vp + 12);
                            val = $"({a:G4},{b:G4},{c:G4},{d:G4})";
                        }
                        else val = $"<size={size}>";
                    }
                    else if (type == 2) val = "<tex>";
                    else if (type == 3) val = "<sampler>";
                    else val = $"<type={type} size={size}>";

                    parts.Add($"{name}={val}");
                }

                if (parts.Count > 0)
                    Log($"  [{slotTag}] material params ({parts.Count}): " + string.Join(" | ", parts));
            }
            catch (Exception ex)
            {
                Log($"  [{slotTag}] param-dump error: {ex.Message}");
            }
        }

        // Walk up from the idf folder looking for teams/<team>/{materials,
        // textures} siblings -- the shape of an extracted ERP cache. Capped
        // at 8 hops so we don't walk the whole drive on a mis-configured
        // path. Returns null when no plausible root is found; callers
        // degrade to gray materials in that case.
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
        /// Byte-scan one mesh descriptor for the package name EAID.
        /// Checks F1 2019 first (matching this loader's primary target),
        /// then 2020 (same format, zstd-compressed sections that the ERP
        /// reader decompresses before we see them), then 2018 as a
        /// safety net. Defaults to f1_2019_vehicle_package if every
        /// scan fails -- better to build a slightly wrong EAID and have
        /// the TextureCache log a miss than to refuse texture resolution
        /// entirely.
        ///
        /// v11.66: ALSO recognise the classic-vehicle packages used by
        /// 2019/2020 for pre-2018 chassis (c_ferrari_90, c_mclaren_88,
        /// etc.). The 2019 game ships these inside
        /// f1_2019_classic_vehicle_package (and 2020 inside
        /// f1_2020_classic_vehicle_package); same Pool A/B/C/D 20-VB
        /// layout as modern 2019 stock, but the EAID prefix is
        /// "_classic_vehicle_package" rather than "_vehicle_package".
        /// Without recognising this prefix every texture EAID built
        /// downstream pointed at the modern package and missed in the
        /// TextureCache, and every classic-2019 submesh rendered as a
        /// neutral-gray gradient (geometry fine, materials gone). The
        /// classic candidates are checked FIRST so a 2019/2020 classic
        /// car can't be misclassified as a modern one if the descriptor
        /// happens to embed a cross-package eaid:// reference into a
        /// modern resource (e.g. shared/tyres EAIDs sometimes do).
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
                    foreach (var candidate in new[]
                    {
                        "f1_2019_classic_vehicle_package",
                        "f1_2020_classic_vehicle_package",
                        "f1_2019_vehicle_package",
                        "f1_2020_vehicle_package",
                        "f1_2018_vehicle_package",
                    })
                    {
                        if (DescriptorContainsAscii(bytes, candidate))
                            return candidate;
                    }
                }
            }
            catch { }
            return "f1_2019_vehicle_package";
        }

        /// <summary>
        /// One-shot TextureCache + livery-context setup. Called at
        /// LoadFolder entry. Mirrors F1ModelLoaderClassic.
        /// AutoConfigureTextureCache: discover a package root, detect
        /// the ERP's package name from descriptor bytes, build a
        /// TextureCache rooted there, load the authoritative GfxMatBindRes
        /// slot->material binding. Any step that fails leaves the
        /// corresponding field null and the per-submesh loop falls back
        /// to the hardcoded slot table (BuildMaterialEaidForSlot) or
        /// ultimately to a neutral-gray DiffuseMaterial.
        /// </summary>
        private static void AutoConfigureTextureCache(string idfFolder)
        {
            // Honour a caller-injected cache from SetTextureCache().
            if (_textureCache != null)
            {
                Log($"AutoConfigure: using pre-set TextureCache (packageRoot={_textureCache.PackageRoot})");
                if (string.IsNullOrEmpty(_detectedPackageName))
                    _detectedPackageName = DetectPackageNameFromDescriptors(idfFolder);
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
            // Preferred over the hardcoded per-slot table because it's
            // the game's own slot -> material binding, and because F1
            // 2019 introduces slot-name quirks (mirror_left with
            // underscore but "mirror right" with space, carbon_tile_1
            // with underscore but "carbon tile 4" with space) that
            // the hardcoded table would need a normalization layer to
            // match but the context table already gets right.
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

        // =====================================================================
        // MATERIAL RESOLUTION (Phase 2)
        // =====================================================================

        /// <summary>
        /// Map a submesh slot name to a material EAID for F1 2019-2020.
        /// Prefers the GfxMatBindRes context when one is loaded -- that's
        /// the game's own authoritative binding and correctly handles
        /// 2019's per-driver materials (e.g. mclaren_carbon_nor vs
        /// mclaren_carbon_sai) as well as shared materials (generics,
        /// indicator_lights, rainbeads, etc.).
        ///
        /// Falls through to a hardcoded per-slot table, normalised for
        /// 2019's mixed underscore/space slot naming: the McLaren context
        /// ships both "mirror_left"/"mirror right" and "carbon_tile_1"/
        /// "carbon tile 4" variants, so we treat underscore and space as
        /// equivalent when matching against the table.
        /// </summary>
        private static string BuildMaterialEaidForSlot(string packageName, string teamPrefix, string slotName)
        {
            if (string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(slotName))
                return null;

            // Highest-priority override: if the caller has set
            // GenericMaterialOverride and this is the generic slot,
            // use the override verbatim. Lets users test alternative
            // generic-slot materials (e.g. mclaren_detail.material for
            // 2018-style visuals) without rebuilding the app.
            if (!string.IsNullOrEmpty(GenericMaterialOverride))
            {
                string normalized = slotName.Trim().ToLowerInvariant().Replace('_', ' ');
                if (normalized == "generic")
                    return GenericMaterialOverride;
            }

            // Preferred path: ask the loaded GfxMatBindRes. This is how
            // we correctly bind "generic" -> shared/generics/materials/
            // generics.material, "digi revs" -> shared/generics/materials/
            // vehicle_revs_type2.material, etc., instead of guessing via
            // the per-slot table.
            if (_activeContext != null)
            {
                string ctxEaid = _activeContext.Resolve(slotName);
                if (!string.IsNullOrEmpty(ctxEaid))
                    return ctxEaid;
            }

            // Normalise for the hardcoded table: lower-case, trim, and
            // treat underscore and space as the same character. That way
            // "mirror_left" and "mirror left" both hit the same table
            // entry, and ditto for "carbon_tile_1" vs "carbon tile 1".
            string slotRaw = slotName.Trim().ToLowerInvariant();
            string slot = slotRaw.Replace('_', ' ');
            string team = (teamPrefix ?? string.Empty).ToLowerInvariant();

            string teamPath = string.IsNullOrEmpty(team) ? null :
                $"eaid://{packageName}/teams/{team}/materials";
            string sharedPath = $"eaid://{packageName}/shared/generics/materials";

            if (slot == "body")
                return teamPath != null ? $"{teamPath}/{team}_paint.material" : null;

            // "carbon tile N" -> {team}_carbonN.material
            // Covers both "carbon tile 1" and the underscore-normalized
            // "carbon tile 1" (input "carbon_tile_1"), same target.
            if (slot.StartsWith("carbon tile "))
            {
                string tail = slot.Substring("carbon tile ".Length).Trim();
                if (int.TryParse(tail, out int n) && teamPath != null)
                    return $"{teamPath}/{team}_carbon{n}.material";
            }

            // "carbon number" -> carbon backing under the driver number.
            // Present as a distinct slot in 2019 GfxMatBindRes (both
            // "carbon number" and "carbon_number" variants) but missing
            // from 2018's table. Bind to the first carbon tile so it
            // shares the carbon weave texture.
            if (slot == "carbon number")
                return teamPath != null ? $"{teamPath}/{team}_carbon1.material" : null;

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

            // shared/generics/materials/* for the slots below. Same
            // routing as F1 2018 Classic's table since the 2019 context
            // maps them identically.
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

        /// <summary>
        /// Produce a DiffuseMaterial for a submesh, or null if any step
        /// in the chain fails (no TextureCache, no slot->material
        /// mapping, material file missing, brush decode error). The
        /// caller treats null as "use neutral gray".
        /// </summary>
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
                _detectedPackageName ?? "f1_2019_vehicle_package",
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

                // Diagnostic: when the slot is "generic", enumerate every
                // texture binding on the resolved material file. Gives us
                // the full list of candidate textures that MaterialSlotResolver
                // / fix28-gate could have picked, so we can see whether the
                // "wrong atlas region" symptom comes from picking the wrong
                // binding OR from a material-level issue (no binding matches
                // what the mesh expects).
                //
                // Additionally, when VerboseMaterialParamDump is enabled
                // (or whenever slot is "generic"), dump every float/texture
                // parameter in the material file. This surfaces UV-transform
                // parameters like me_uOffset, me_uScale, me_tileRate that
                // might differ between 2018 and 2019 materials and that
                // our preview isn't yet applying.
                string slotLower = (slotName ?? string.Empty).Trim().ToLowerInvariant().Replace('_', ' ');
                bool isGeneric = (slotLower == "generic");
                if (isGeneric || VerboseMaterialParamDump)
                {
                    try
                    {
                        var matFile = _textureCache.GetMaterial(materialEaid);
                        if (matFile != null && matFile.BindingsBySlot != null &&
                            matFile.BindingsBySlot.Count > 0)
                        {
                            Log($"  [{slotLower}] material bindings ({matFile.BindingsBySlot.Count}): " +
                                string.Join(" | ",
                                    matFile.BindingsBySlot.Select(kv => $"{kv.Key}={kv.Value}")));
                        }
                        else if (isGeneric)
                        {
                            Log($"  [generic] material has null/empty bindings for {materialEaid}");
                        }

                        // Raw-byte parameter dump. We bypass MaterialFile because
                        // that class only exposes sampler bindings, not the float
                        // parameters the shader actually consumes. The
                        // MaterialParameterReader in EgoTexturePipeline knows the
                        // .material binary layout; we reuse its offset math here
                        // and stream every parameter name + float value into the log.
                        string matPath = EaidPathResolver.ResolveDescriptor(
                            materialEaid, _textureCache.PackageRoot);
                        if (!string.IsNullOrEmpty(matPath) && File.Exists(matPath))
                        {
                            byte[] matBytes = File.ReadAllBytes(matPath);
                            DumpMaterialParameters(slotLower, matBytes);
                        }
                    }
                    catch (Exception diag)
                    {
                        Log($"  [{slotLower}] param dump threw: {diag.Message}");
                    }
                }

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
                return new DiffuseMaterial(res.Brush);
            }
            catch (Exception ex)
            {
                logStatus = $"material-resolve threw ({ex.Message}) for {materialEaid}";
                return null;
            }
        }

        /// <summary>
        /// Overlay-brush fallback. F1 2019-2020 paint/carbon/steering
        /// shaders use EAIDs like "vehicle_paint.nefx2" (without the
        /// 2021+ "_metallic_" prefix), which MaterialSlotResolver.
        /// ClassifyShader falls through to ResolveUnknownByHeuristic
        /// for -- and that path deliberately refuses to pick an overlay
        /// slot because it can't guarantee the shader's compositing
        /// rules. Result: the base paint loads but sponsor decals and
        /// driver numbers never render.
        ///
        /// Mirrors F1ModelLoaderClassic.TryResolveOverlayBrushClassic:
        /// read the material file directly, look for me_logoMap /
        /// me_decalMap / me_sponsorMap in its bindings, decode the
        /// bound texture into a brush. Gated to body / number / carbon-
        /// tile slots so we don't paint sponsors on mirror glass or
        /// brake discs.
        /// </summary>
        private static System.Windows.Media.Brush TryResolveOverlayBrushLegacy(
            string slotName, string teamPrefix, out string logInfo)
        {
            logInfo = null;
            if (_textureCache == null) { logInfo = "no TextureCache"; return null; }

            string slotRaw = (slotName ?? string.Empty).Trim().ToLowerInvariant();
            string slot = slotRaw.Replace('_', ' ');

            // Same allowlist as Classic. Everything else declines an
            // overlay pass to keep random me_logoMap bindings off
            // mirrors / brake discs / interior plastics.
            bool wantsOverlay = slot == "body" || slot == "number" ||
                                slot == "carbon number" ||
                                slot.StartsWith("carbon tile ");
            if (!wantsOverlay)
            {
                logInfo = $"slot '{slot}' not in body/number/carbon-tile allowlist";
                return null;
            }

            string materialEaid = BuildMaterialEaidForSlot(
                _detectedPackageName ?? "f1_2019_vehicle_package",
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

            string[] overlaySlots = { "me_logoMap", "me_decalMap", "me_sponsorMap" };
            foreach (var overlaySlotName in overlaySlots)
            {
                if (matFile.BindingsBySlot.TryGetValue(overlaySlotName, out var overlayEaid) &&
                    !string.IsNullOrEmpty(overlayEaid))
                {
                    try
                    {
                        var brush = _textureCache.GetBrushForTexture(overlayEaid);
                        if (brush != null)
                        {
                            logInfo = $"legacy-fallback via {overlaySlotName} -> {overlayEaid}";
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

        // =====================================================================
        // UV / NORMAL DECODING (copied verbatim from Classic's helpers so the
        // two loaders agree on half-float edge cases, stride layouts, etc.)
        // =====================================================================

        /// <summary>
        /// Find the Nth stride-matching VB following posVbIdx whose vertex
        /// count matches. Used to pick the "UV VB" (the second stride-8
        /// after the position VB, after the normal and tangent streams)
        /// and the "normal VB" (the first stride-8 after position).
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
                    result.Add(new Vector3D(0, 1, 0));
                else
                    result.Add(new Vector3D(nx / len, ny / len, nz / len));
            }
            return result;
        }

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

        private class VertexBufferInfo
        {
            public string Name;
            public string Suffix;
            public string DataPath;
            public uint Type;
            public int Stride;
            public int Count;
            public uint Fvf;
            public long DataLength; // actual file size for checks
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
            public long DataLength; // actual file size for checks
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

        // =====================================================================
        // PUBLIC ENTRY POINTS
        // =====================================================================
        public static class F1CoordinateSystem
        {
            // Adjust these numbers until the car looks “straightforward” for *all* years.
            public static readonly Transform3D Orientation =
                new RotateTransform3D(
                    new AxisAngleRotation3D(new Vector3D(1, 0, 0), -90));
        }

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

            string logFile = Path.Combine(folder, "F1_LegacyLoader_Log.txt");
            return LoadFolder(inputPath, logFile);
        }

        /// <summary>
        /// Explicit-LOD entry point used by the glTF exporter to emit a
        /// separate <c>{team}_LOD{N}.gltf</c> for every LOD present in
        /// the cache. Sets <see cref="_requestedExplicitLod"/> for the
        /// duration of the call so the LOD-selection block inside
        /// <see cref="LoadFolder(string, string)"/> picks descriptors
        /// whose filename contains <c>lod={lod}</c> instead of the
        /// default <c>lod=0</c>. Mirrors
        /// F1ModelLoaderClassic.LoadFolderExplicitLod exactly.
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
            Log("==================================================");
            Log($"F1ModelLoaderLegacy.LoadFolder START");
            Log($"Input: {inputPath}");

            var group = new Model3DGroup();



            if (string.IsNullOrWhiteSpace(inputPath))
            {
                Log("ERROR: inputPath is null/empty. Returning empty group.");
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
                Log($"ERROR: Folder does not exist: {folder}. Returning empty group.");
                return group;
            }

            // ---------------------------------------------------------
            // v12.05: CLASSIC-CONTENT PRE-FLIGHT DISPATCH
            // ---------------------------------------------------------
            // Some ERPs land here that aren't really "legacy" content
            // -- they're modern-NEFX1 classic-car descriptors (F1 2018
            // numbered classics: f1_classic_01/02/03_vehicle_package,
            // AND F1 2020's bare unnumbered f1_classic_vehicle_package
            // shipping Benetton 94 / McLaren 98 / etc.). Their VB
            // headers emit the 9-int classic magic, their material
            // bindings use shared/generics paths, and their mesh
            // records are the 88-byte modern form. F1ModelLoaderClassic
            // is the correct parser for them.
            //
            // Without this pre-flight, the loader walks every
            // descriptor through the 2016/2017-era LegacyMesh
            // fallback path, which has hardcoded VOffset=0 and emits
            // a single per-descriptor "<id>!!!temp000" placeholder
            // submesh with no slot-name routing. Symptoms in the
            // field: every submesh logged as "c_benetton_94!!!temp000"
            // with "no material-EAID mapping" -> gray fallback,
            // distorted geometry from misread half-float aux VBs.
            //
            // The detection predicate is F1ModelLoader.LooksLikeClassicContent
            // (made internal in v12.05 -- single source of truth shared
            // with the hybrid dispatcher higher up in the call chain).
            // The same logFile is plumbed through so diagnostics from
            // the classic loader land in the same file the user is
            // already inspecting -- no second log to chase.
            //
            // _requestedExplicitLod is set by LoadFolderExplicitLod
            // (the LoadFolder(inputPath, lod) entry point) immediately
            // before the unified LoadFolder(inputPath, logFile) is
            // called. Forward it through to the classic loader's
            // matching explicit-LOD entry point so a user picking
            // LOD=3 from the UI gets LOD=3 in the classic dispatch.
            try
            {
                if (F1ModelLoader.LooksLikeClassicContent(inputPath, out string dispatchReason))
                {
                    Log("[CLASSIC-DISPATCH] CLASSIC CONTENT DETECTED -- " +
                        $"forwarding to F1ModelLoaderClassic. Reason: {dispatchReason}");
                    if (_requestedExplicitLod.HasValue)
                    {
                        int reqLod = _requestedExplicitLod.Value;
                        Log($"[CLASSIC-DISPATCH] preserving explicit LOD request: {reqLod}");
                        return F1ModelLoaderClassic.LoadFolderExplicitLod(
                            inputPath, reqLod, logFile);
                    }
                    return F1ModelLoaderClassic.LoadFolder(inputPath, logFile);
                }
            }
            catch (Exception ex)
            {
                // Detection or dispatch threw -- log and fall through
                // to the legacy path. Better to render badly than
                // crash the whole load.
                Log("[CLASSIC-DISPATCH] pre-flight threw " +
                    $"({ex.Message}); continuing on the legacy path.");
            }

            // Reset texture-pipeline state so a second LoadFolder on a
            // different car doesn't leak the first car's TextureCache /
            // livery context / package name. AutoConfigureTextureCache
            // honours a pre-set _textureCache injected via
            // SetTextureCache() (for batch renders sharing one cache),
            // so callers that want that pattern set it AFTER the clear
            // here and BEFORE LoadFolder.
            _textureCache = null;
            _detectedPackageName = null;
            _activeContext = null;

            // Discover package root + detect the package name from a
            // descriptor + build TextureCache + load GfxMatBindRes.
            // Each step logs its own success/failure; if any fail the
            // per-submesh loop falls back to neutral gray.
            AutoConfigureTextureCache(folder);

            Log($"Scanning folder for LEGACY mesh descriptors (temp000*mesh*) in: {folder}");
            string[] allMeshFiles = Directory.GetFiles(folder, "*temp000*mesh*", SearchOption.TopDirectoryOnly);

            if (allMeshFiles.Length == 0)
            {
                Log("ERROR: No temp000 mesh files (*temp000*mesh*) found in folder. Returning empty group.");
                return group;
            }

            Array.Sort(allMeshFiles, StringComparer.OrdinalIgnoreCase);
            Log($"Found {allMeshFiles.Length} legacy mesh descriptor file(s) (all LODs).");

            // LOD selection
            StringComparison cmp = StringComparison.OrdinalIgnoreCase;
            string[] meshFilesToUse;

            if (_requestedExplicitLod.HasValue)
            {
                // Explicit-LOD path -- caller is the glTF exporter asking
                // for a specific {team}_LOD{N}.gltf output. Restrict to
                // descriptors whose filename contains "lod={requested}".
                // Returns an empty group on no match rather than silently
                // falling through to lod=0: writing lod=0 data into a
                // file named "_LOD{N}.gltf" for N!=0 was the bug this
                // path exists to fix. Mirror of F1ModelLoaderClassic's
                // _requestedExplicitLod path at line 3001.
                int reqLod = _requestedExplicitLod.Value;
                string tag = "lod=" + reqLod.ToString(System.Globalization.CultureInfo.InvariantCulture);
                meshFilesToUse = allMeshFiles.Where(f => f.IndexOf(tag, cmp) >= 0).ToArray();
                if (meshFilesToUse.Length > 0)
                {
                    Log($"LOD Filter: EXPLICIT lod={reqLod} -> using {meshFilesToUse.Length} descriptor(s).");
                }
                else
                {
                    Log($"LOD Filter: EXPLICIT lod={reqLod} requested but NO matching descriptors in this cache; returning empty group.");
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
                    Log($"LOD Filter: Using {meshFilesToUse.Length} file(s) with 'lod=0' (highest detail).");
                }
                else
                {
                    var lodAllFiles = allMeshFiles.Where(f => f.IndexOf("lod=all", cmp) >= 0).ToArray();
                    if (lodAllFiles.Length > 0)
                    {
                        meshFilesToUse = lodAllFiles;
                        Log($"LOD Filter: No 'lod=0' files. Using {meshFilesToUse.Length} file(s) with 'lod=all'.");
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
                            Log($"LOD Filter: Using {meshFilesToUse.Length} file(s) with smallest lod={minLod}.");
                        }
                        else
                        {
                            meshFilesToUse = allMeshFiles;
                            Log("LOD Filter: No 'lod=' pattern found in filenames. Using ALL mesh files.");
                        }
                    }
                }
            }

            Log($"Final legacy mesh descriptor count after LOD filter: {meshFilesToUse.Length}");

            // Remove the whole-car "root" descriptor if SkipRootDescriptor
            // is on. A descriptor is a root if its filename ends with
            // "lod=N&mesh" with no part-name segment between the LOD and
            // "&mesh" (both "%26" URL-encoded and plain "&" are accepted
            // because ERP cache paths can use either encoding depending
            // on extractor version). Leaves always have the form
            // "lod=N&{part}&mesh" and survive this filter.
            if (SkipRootDescriptor && meshFilesToUse.Length > 0)
            {
                int before = meshFilesToUse.Length;
                meshFilesToUse = meshFilesToUse
                    .Where(p => !IsRootDescriptorPath(p))
                    .ToArray();
                int skipped = before - meshFilesToUse.Length;
                if (skipped > 0)
                    Log($"SkipRootDescriptor: removed {skipped} whole-car root descriptor(s) " +
                        $"to avoid double-drawing geometry the leaves already cover. " +
                        $"New descriptor count: {meshFilesToUse.Length}.");
            }

            int totalParts = 0;

            foreach (var meshFile in meshFilesToUse)
            {
                Log("--------------------------------------------------");
                Log($"[LEGACY MESH DESCRIPTOR] START: {meshFile}");

                // Skip rain-effect (_water) descriptors entirely. F1 2018-2020
                // ship a parallel set of "*_water" descriptors -- safetycell_water,
                // nosecone_water, frontflap_fl_water, etc. -- whose only submesh
                // is a "rainbeads" overlay that the wet-weather shader uses to
                // draw water droplets. In a dry-weather static preview they just
                // double-layer over the main body with a transparent rainbeads
                // atlas that adds nothing and inflates the submesh count by
                // ~13 per car. Matches the Classic loader's descriptor-level
                // skip, same rationale.
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
                        Log("  -> LoadLegacyMeshDescriptor returned null/empty group for this descriptor.");
                        continue;
                    }

                    foreach (var child in partGroup.Children)
                    {
                        group.Children.Add(child);
                        totalParts++;
                    }

                    Log($"[LEGACY MESH DESCRIPTOR] END: Added {partGroup.Children.Count} legacy submesh(es) from this descriptor.");
                }
                catch (Exception ex)
                {
                    Log($"[LEGACY MESH DESCRIPTOR] CRITICAL EXCEPTION while loading '{meshFile}':");
                    Log(ex.ToString());
                }
            }

            Log("==================================================");
            Log($"F1ModelLoaderLegacy.LoadFolder END. Total parts added: {totalParts}");
            Log("[LEGACY GLOBAL STATS]");
            Log($"    Total submeshes  : {_totalSubmeshes}");
            Log($"    Total indices    : {_totalIndices}");
            Log($"    Total triangles  : {_totalTriangles}");
            Log($"    Total vertices   : {_totalVertices}");
            Log("==================================================");

            return group;
        }

        // =====================================================================
        // CORE: LEGACY MESH DESCRIPTOR
        // =====================================================================

        private static Model3DGroup LoadLegacyMeshDescriptor(string meshTemp000Path)
        {
            Log($"LoadLegacyMeshDescriptor: {meshTemp000Path}");

            if (!File.Exists(meshTemp000Path))
            {
                Log("ERROR: Legacy mesh file not found.");
                return null;
            }

            // Detect game version from the path itself (folder names etc.)
            UpdateGameYearFromString(meshTemp000Path);

            string folder = Path.GetDirectoryName(meshTemp000Path)!;
            string meshFileName = Path.GetFileName(meshTemp000Path);

            string teamPrefix = meshFileName.Contains("!!!")
                ? meshFileName.Substring(0, meshFileName.IndexOf("!!!", StringComparison.Ordinal))
                : "mclaren";

            // Is this one of the safetycell IDFs?
            bool isSafetyCellDescriptor =
                meshFileName.Contains("safetycell", StringComparison.OrdinalIgnoreCase) ||
                meshFileName.Contains("safetycell_water", StringComparison.OrdinalIgnoreCase);

            // 2018+ multi-mesh descriptor path (also used for 2019 and 2020).
            //
            // Original heuristic was path-based (IndexOf "2018"/"2019"/"2020"
            // in the on-disk path), which only works when the user loads from
            // an install folder. It silently fails for ERPs extracted to a
            // hash-named cache directory like
            //   erp_cache\mclaren_E2828C83719BB215\teams\mclaren\idf
            // because that path has no year in it -- which is where every
            // ERP the user picks ends up first. Falling through to the
            // [LEGACY_SINGLE] branch meant we read only mesh[0] from the
            // header's h[12]/h[13] range and ignored every label + mesh
            // record, producing the "car partially loads" symptom.
            //
            // Classic solved this by scanning the descriptor's raw bytes for
            // the ASCII package name (the EAID prefix stored in every mesh
            // record's embedded buffer references). We mirror that here --
            // content-based is the only reliable signal when the on-disk
            // path has been anonymised.
            bool is2018Plus =
                meshTemp000Path.IndexOf("2018", StringComparison.OrdinalIgnoreCase) >= 0 ||
                meshTemp000Path.IndexOf("2019", StringComparison.OrdinalIgnoreCase) >= 0 ||
                meshTemp000Path.IndexOf("2020", StringComparison.OrdinalIgnoreCase) >= 0;

            byte[] bytes = File.ReadAllBytes(meshTemp000Path);

            // -------------------------------------------------
            // EMPTY-STUB SHORT-CIRCUIT (F1 2018+ multi-LOD)
            // -------------------------------------------------
            //
            // F1 2018-2020 vehicle packages publish one mesh descriptor
            // per (LOD, pool) combination. For LODs where a given pool has
            // no authored geometry, the publisher emits a 36-byte
            // ALL-ZERO descriptor instead of just omitting the entry --
            // this keeps the per-LOD pool list shape-stable across LODs
            // so the engine can index by pool without bounds-checking.
            // 36 bytes is exactly one 9-int 2018+ header with every
            // field zero (numMeshes=0, numVBuf=0, numIBuf=0, labelCount=0,
            // numNodeLinks=0, vbufOffset=0, ibufOffset=0,
            // meshNameOffset=0, nodeLinkOffset=0).
            //
            // Population per LOD in the McLaren 2020 ERP (verified by
            // walking the .erp TOC):
            //   LOD0: 56 of 56 pools full,   0 stubs  (highest detail)
            //   LOD1: 56 of 56 pools full,   0 stubs
            //   LOD2: 51 of 56 pools full,   5 stubs
            //   LOD3: 46 of 54 pools full,   8 stubs
            //   LOD4: 44 of 53 pools full,   9 stubs
            //   LOD5: 25 of 45 pools full,  20 stubs
            //   LOD6:  1 of 39 pools full,  38 stubs  (mostly culled)
            //   LOD7: 10 of 39 pools full,  29 stubs  (shadow-distance
            //                                          silhouette only)
            //   LOD8: 37 of 37 pools full,   0 stubs  (shadow LOD;
            //                                          re-detailed for
            //                                          collision / cast
            //                                          shadows)
            //
            // The previous behaviour was a hard 19-int (76-byte) size
            // cap that rejected every stub with
            // "ERROR: Mesh file too small for 19-int header" and
            // returned an empty Model3DGroup. The result -- empty group
            // -- is what we want, but the ERROR log was misleading
            // (the caller would interpret the LOD as broken when
            // actually the descriptor was a legitimate
            // "no-geometry-for-this-part-at-this-LOD" sentinel).
            //
            // Worse, gltf viewers downstream that render empty
            // hierarchy leaves by falling back to a previous load's
            // geometry would cause LOD3..LOD7 exports to LOOK like
            // LOD0 even though the ERROR log said the geometry was
            // absent. Treating stubs as a clean empty-result eliminates
            // the misleading log, and the empty-leaf rendering fallback
            // becomes a problem only at the viewer layer, where a
            // legitimately empty node now reads as authored-empty
            // rather than parse-failed.
            //
            // The check is conservative -- 36 bytes exactly, all zeros.
            // Any other small file falls through to the original 19-int
            // sanity check below, so non-stub corrupt descriptors still
            // hit the ERROR path.
            if (bytes.Length == 36)
            {
                bool isAllZeroStub = true;
                for (int i = 0; i < 36; i++)
                {
                    if (bytes[i] != 0)
                    {
                        isAllZeroStub = false;
                        break;
                    }
                }
                if (isAllZeroStub)
                {
                    Log("[HEADER] empty 36-byte all-zero stub descriptor " +
                        "(this LOD authors no geometry for this part); " +
                        "returning empty group cleanly.");
                    return new Model3DGroup();
                }
            }

            if (bytes.Length < 19 * 4)
            {
                Log($"[HEADER] ERROR: Mesh file too small for 19-int header (size={bytes.Length}, expected >= 76).");
                return new Model3DGroup();
            }

            // Content-based fallback: even if the path doesn't expose the
            // year, the descriptor itself embeds the package name. Any of
            // f1_2018, f1_2019, f1_2020 tells us we're in the multi-mesh
            // format. v11.66: the classic 2019/2020 packages
            // (f1_2019_classic_vehicle_package and 2020 equivalent) use
            // the same multi-mesh layout, so they belong on the is2018Plus
            // path too -- without their tokens here, an anonymised classic
            // ERP folder would be mistaken for a 2017-vintage single-mesh
            // package and the loader would route it through the wrong
            // descriptor parser.
            if (!is2018Plus)
            {
                if (DescriptorContainsAscii(bytes, "f1_2018_vehicle_package") ||
                    DescriptorContainsAscii(bytes, "f1_2019_vehicle_package") ||
                    DescriptorContainsAscii(bytes, "f1_2020_vehicle_package") ||
                    DescriptorContainsAscii(bytes, "f1_2019_classic_vehicle_package") ||
                    DescriptorContainsAscii(bytes, "f1_2020_classic_vehicle_package"))
                {
                    is2018Plus = true;
                    Log("[HEADER] is2018Plus auto-enabled via content-based detection " +
                        "(descriptor bytes contain a known f1_20xx_vehicle_package EAID).");
                }
            }

            using (var ms = new MemoryStream(bytes))
            using (var br = new BinaryReader(ms))
            {
                // ---- 19-int descriptor header ----
                int[] h = new int[19];
                for (int i = 0; i < 19; i++)
                    h[i] = br.ReadInt32();

                int numMeshes = h[0];
                int numVBuf = h[1];
                int numIBuf = h[2];
                int labelCount = h[3];

                Log($"[HDR] h[0..18] = {string.Join(", ", h)}");
                Log($"[HDR] numMeshes={numMeshes}, numVBuf={numVBuf}, numIBuf={numIBuf}, labelCount={labelCount}, " +
                    $"vbufOfs=h[5]={h[5]}, ibufOfs=h[6]={h[6]}, headerIOffset=h[12]={h[12]}, headerIndexCount=h[13]={h[13]}");

                // These offsets are relative to the end of this 19-int header.
                const int LegacyHeaderSize = 19 * 4;

                int vbufOfs = h[5];
                int ibufOfs = h[6];
                int headerIOffset = h[12];
                int headerIndexCount = h[13];

                // ---- VBUFs ----
                var vbufs = new List<VertexBufferInfo>();
                int idxVb0 = -1;
                int idxVb6 = -1; // track vb=6 for generic antennas / tech bits
                int idxVb11 = -1; // track vb=11: F1 2020 mirror-glass position
                                  // buffer (stride 40, FVF 0x27, 156 verts in
                                  // McLaren ERP). Mirrors live in this VB
                                  // exclusively -- not in vb=6 -- so the
                                  // mirror reroute below targets it directly.

                // ADDITIVE (mirror-glass fallback): same idea as idxVb11 but
                // identifies the dedicated mirror-glass VB by its CHARACTER-
                // ISTICS instead of by hardcoded vb=N name. Some descriptors
                // (e.g. F1 2019 classic Ferrari wing_mirror_l) place the
                // mirror glass in vb=17 instead of vb=11, and on those the
                // existing idxVb11-based reroute picks up the BODY position
                // VB (which happens to be the one named "vb=11" in that
                // descriptor) and the mirror geometry reads the first
                // ~50 vertices of the body instead of the mirror mesh.
                //
                // The dedicated mirror-glass VB across every observed case
                // is Stride=40, FVF=0x27 -- a unique signature: body position
                // VBs are Stride=12, FVF=0x27, and aux VBs use other FVFs
                // (0x23 for stride-8 normals/UVs, 0xD/0x1D for stride-4 aux).
                // First-match-wins. Pools with no Stride=40 position buffer
                // leave this at -1 and the override block below is a no-op,
                // preserving every prior load's behaviour byte-for-byte.
                int idxMirrorGlass = -1;

                ms.Position = LegacyHeaderSize + vbufOfs;
                for (int i = 0; i < numVBuf; i++)
                {
                    string name = ReadCString(br);

                    // Detect game year from the EAID path for this VBUF
                    UpdateGameYearFromString(name);

                    string suffix = BuildSuffixFromName(name, teamPrefix);
                    var vInfo = LoadBufVertex(folder, teamPrefix, suffix);
                    vInfo.Name = name;
                    vbufs.Add(vInfo);

                    Log($"  [VBUF {i}] name='{name}', suffix='{suffix}', DataPath='{vInfo.DataPath}', " +
                        $"Stride={vInfo.Stride}, Count={vInfo.Count}, FVF=0x{vInfo.Fvf:X}");

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

                    if (name.Contains("vb=11", StringComparison.OrdinalIgnoreCase) ||
                        suffix.Contains("vb=11", StringComparison.OrdinalIgnoreCase))
                    {
                        idxVb11 = i;
                    }

                    // ADDITIVE: dedicated-mirror-glass-VB detector. Stride=40
                    // + FVF=0x27 is the unique signature of the small dedi-
                    // cated mirror-glass position buffer (148 verts on F1
                    // 2019 Ferrari, 156 verts on F1 2020 McLaren). Body
                    // position VBs are Stride=12 with the same FVF, so the
                    // stride check disambiguates without false-matching.
                    // First-match-wins so the front-end position pool gets
                    // the slot if any second Stride=40 buffer ever appears.
                    if (idxMirrorGlass < 0 && vInfo.Stride == 40 && vInfo.Fvf == 0x27)
                    {
                        idxMirrorGlass = i;
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

                    // Detect game year from the EAID path for this IBUF as well
                    UpdateGameYearFromString(name);

                    string suffix = BuildSuffixFromName(name, teamPrefix);
                    var iInfo = LoadBufIndex(folder, teamPrefix, suffix);
                    iInfo.Name = name;
                    ibufs.Add(iInfo);

                    Log($"  [IBUF {i}] name='{name}', suffix='{suffix}', DataPath='{iInfo.DataPath}', " +
                        $"IndexSize={iInfo.IndexSize}, Count={iInfo.Count}, FVF=0x{iInfo.Fvf:X}");
                }

                // ---- Labels ----
                var labels = new List<string>();
                for (int i = 0; i < labelCount; i++)
                {
                    string label = ReadCString(br);
                    labels.Add(label);
                    Log($"  [LABEL {i}] '{label}'");
                }

                // ---------------------------------------------------------
                // F1 2020 ONLY: repair the off-by-one label-table corruption.
                // ---------------------------------------------------------
                // EVERY 2020 stock mesh descriptor (verified across
                // safetycell, nosecone, frontwing_fl_01, sidepod_fl,
                // cockpit, wheel_fl, topcanopy_bm, driver_surroun on
                // mclaren.erp 2026-05-09) has a malformed first entry in
                // its label table -- a 36-byte stale IDF reference of the
                // form 'claren/idf/mclaren.idf?lod=all&ib=0' that sits in
                // front of the real slot names ('body', 'generic',
                // 'detail', 'carbon_tile_1', ..., 'rainbeads'). It looks
                // like a leaked substring of the team's IB EAID that
                // Codemasters' build process accidentally inlined ahead
                // of the real labels.
                //
                // The descriptor's labelCount counts ALL entries
                // including the junk one, so the null-walk loop above
                // reads the junk as labels[0] and stops one short of the
                // last real label ('rainbeads' on safetycell). The
                // existing per-submesh loop assigns slot names by
                // labels[mIndex], so every mesh's slot ends up shifted:
                //   mesh[0] real='body'      -> seen as the junk string
                //   mesh[1] real='generic'   -> seen as 'body'
                //   mesh[2] real='detail'    -> seen as 'generic'
                //   ... (all shifted by one)
                //   mesh[N-1] real='rainbeads' -> seen as 'rear light'
                //
                // Visible symptoms before this fix, all on 2020 only:
                //   * paint texture lands on the wrong submesh (the
                //     reported "paint UV is wrong"). The real body
                //     submesh gets the junk slot string, which falls
                //     out of every slot->material mapping path and
                //     resolves to neutral gray or whatever fallback;
                //     meanwhile the real generic-interior mesh wears
                //     the paint atlas via its (shifted) 'body' slot.
                //   * decals on body/number/carbon-tile composite onto
                //     the wrong panels -- same off-by-one applied to
                //     the overlay pipeline.
                //   * the rainbeads-on-dry-parts skip from the 2020
                //     fix block above NEVER fires because the real
                //     rainbeads submesh is labelled 'rear light'.
                //
                // F1 2019 does NOT exhibit this corruption: 2019 stock
                // descriptors verified clean against past sessions
                // (safetycell mesh[0]='body' / mesh[1]='carbon_tile_1' /
                // mesh[2]='detail' as authored, no leading junk entry).
                // Strictly gated to 2020 so 2019 byte-identically
                // round-trips its labels unchanged.
                //
                // Repair shape: if the first label is an IDF/EAID
                // fragment (contains '/' or '?' or '&' or '.idf'),
                // drop it from the head of the list and read ONE
                // more null-terminated string from the binary reader.
                // That gives us labelCount real slot names again, in
                // the same array index space the per-submesh loop
                // already consumes. No change to the per-submesh
                // loop itself -- it keeps using labels[mIndex] and
                // now sees the correct value at every index.
                if (_currentGameYear == 2020 && labels.Count > 0)
                {
                    string first = labels[0] ?? string.Empty;
                    bool looksLikeIdfFragment =
                        first.IndexOf('/') >= 0 ||
                        first.IndexOf('?') >= 0 ||
                        first.IndexOf('&') >= 0 ||
                        first.IndexOf(".idf", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (looksLikeIdfFragment)
                    {
                        labels.RemoveAt(0);
                        try
                        {
                            string extra = ReadCString(br);
                            labels.Add(extra);
                            Log($"  [LABEL FIX 2020] dropped malformed first label '{first}', " +
                                $"appended trailing label '{extra}'. " +
                                $"Real slots now indexed labels[0..{labels.Count - 1}]: " +
                                $"[{string.Join(", ", labels)}]");
                        }
                        catch (Exception ex)
                        {
                            // If the trailing read fails (truncated descriptor
                            // / unexpected EOF) the labels list is one entry
                            // short; downstream slot lookups for the highest
                            // mIndex would fall back to "mesh_{mIndex}". Logged
                            // and tolerated so the rest of the car still loads.
                            Log($"  [LABEL FIX 2020] dropped malformed first label '{first}', " +
                                $"trailing ReadCString threw ({ex.Message}); labels list is " +
                                $"now {labels.Count} entries (one short of original {labelCount}).");
                        }
                    }
                }

                var subMeshes = new List<SubMeshInfo>();

                if (is2018Plus)
                {
                    // ------------------------------------------------------------------
                    // 2018+ layout
                    //   - 9 * 4-byte header at offset 0
                    //   - submesh records (88 bytes each) immediately after that
                    //   - vertexBufferOffset points to vbOffsets[numVBuf]
                    // ------------------------------------------------------------------
                    using (var ms2 = new MemoryStream(bytes))
                    using (var br2 = new BinaryReader(ms2))
                    {
                        int numMeshes2018 = br2.ReadInt32();
                        int numVBuf2018 = br2.ReadInt32();
                        int numIBuf2018 = br2.ReadInt32();
                        int labelCount2018 = br2.ReadInt32();
                        int numNodeLinks = br2.ReadInt32();
                        int vbufOffset2018 = br2.ReadInt32();
                        int ibufOffset2018 = br2.ReadInt32();
                        int meshNameOffset2018 = br2.ReadInt32();
                        int nodeLinkOffset2018 = br2.ReadInt32();

                        Log($"[2018+] header: numMeshes={numMeshes2018}, numVBuf={numVBuf2018}, numIBuf={numIBuf2018}, " +
                            $"labelCount={labelCount2018}, numNodeLinks={numNodeLinks}, " +
                            $"vbufOffset={vbufOffset2018}, ibufOffset={ibufOffset2018}, " +
                            $"meshNameOffset={meshNameOffset2018}, nodeLinkOffset={nodeLinkOffset2018}");

                        // Sanity checks vs original 19-int header
                        if (numMeshes2018 != numMeshes)
                            Log($"  [2018+] Header numMeshes mismatch: {numMeshes2018} vs {numMeshes}");
                        if (numVBuf2018 != numVBuf)
                            Log($"  [2018+] Header numVBuf mismatch: {numVBuf2018} vs {numVBuf}");
                        if (numIBuf2018 != numIBuf)
                            Log($"  [2018+] Header numIBuf mismatch: {numIBuf2018} vs {numIBuf}");

                        // VB offset table
                        var vbOffsets = new uint[numVBuf2018];
                        ms2.Position = vbufOffset2018;
                        for (int i = 0; i < numVBuf2018; i++)
                            vbOffsets[i] = br2.ReadUInt32();

                        Log($"[2018+] VB offsets (vertexBufferOffset={vbufOffset2018}): " +
                            string.Join(", ", vbOffsets.Select((ofs, idx) => $"VB[{idx}]=0x{ofs:X}")));

                        // -------------------------------------------------
                        // VB-INFO-BLOCK PARSE  (descriptor's authoritative
                        // submesh -> vbuf chain)
                        // -------------------------------------------------
                        //
                        // The descriptor lays out, contiguously after the
                        // vbOffsets table, one VB-info-block per VBuf. Each
                        // block is a null-terminated EAID of the form
                        // "eaid://...&vb=N". A submesh's vertexFileOffset
                        // (and each entry of maps[]) is a BYTE-OFFSET into
                        // this block area; reading the EAID at that offset
                        // gives the integer N of the VBuf the submesh wants
                        // for its position (or per-channel) data.
                        //
                        // Why this exists alongside vbOffsets[]: the actual
                        // vbOffsets[] table only contains numMeshes valid
                        // entries; the loader reads numVBuf entries because
                        // that is the count the format header advertises,
                        // but bytes past numMeshes*4 are the start of the
                        // EAID strings reinterpreted as u32 (the McLaren 2020
                        // safetycell shows 0x64696165 ='eaid' at vbOffsets[9]
                        // for example -- garbage, not a real offset). The
                        // picker's "exact / segment match" against this
                        // tail is unreliable and lands many submeshes on a
                        // stride-8 packed-normals VB whose first 3 floats
                        // can't be decoded as positions. The VB-info-block
                        // chain is the descriptor's authoritative source --
                        // following it gives the same routing the engine
                        // itself would use.
                        //
                        // The map and the loader-VBUF lookup built below are
                        // consumed by a fallback gate after the existing
                        // STRIDE-12 RETARGET; existing pickers, generic /
                        // mirror reroutes, and STRIDE-12 RETARGET still run
                        // first, and the fallback is a no-op when they have
                        // already routed to a stride>=12 buffer.
                        var vbInfoMap = new Dictionary<uint, int>();
                        {
                            // The vbOffsets[] table is followed immediately
                            // by the VB-info-block EAID strings. The table
                            // size varies per pool (we have observed pools
                            // with 3, 4, 9 entries even when numVBuf is much
                            // larger -- the loader reads numVBuf entries to
                            // match the format header but the descriptor
                            // only authors a smaller number, padding with
                            // zeros that abut the EAID bytes). Rather than
                            // guess the table size, we scan forward from
                            // vbufOffset2018 for the first 'eaid://' marker;
                            // that byte position IS the VB-info-block area
                            // start (and corresponds to vfo=0 in the
                            // submesh records).
                            int vbInfoAreaStart = -1;
                            int sEnd = bytes.Length - 6;
                            for (int q = vbufOffset2018; q < sEnd; q++)
                            {
                                if (bytes[q] == (byte)'e' && bytes[q + 1] == (byte)'a' &&
                                    bytes[q + 2] == (byte)'i' && bytes[q + 3] == (byte)'d' &&
                                    bytes[q + 4] == (byte)':' && bytes[q + 5] == (byte)'/' &&
                                    bytes[q + 6] == (byte)'/')
                                {
                                    vbInfoAreaStart = q;
                                    break;
                                }
                            }
                            if (vbInfoAreaStart < 0)
                            {
                                Log("[2018+] No 'eaid://' marker found after vbOffsets; vbinfo-reroute fallback inactive.");
                            }
                            else
                            {
                                int p = vbInfoAreaStart;
                                int safetyCap = 256; // never expect more than this many blocks
                                while (p < bytes.Length - 7 && vbInfoMap.Count < safetyCap)
                                {
                                    // scan forward for next 'eaid://'
                                    int e = -1;
                                    int qEnd = bytes.Length - 6;
                                    for (int q = p; q < qEnd; q++)
                                    {
                                        if (bytes[q] == (byte)'e' && bytes[q + 1] == (byte)'a' &&
                                            bytes[q + 2] == (byte)'i' && bytes[q + 3] == (byte)'d' &&
                                            bytes[q + 4] == (byte)':' && bytes[q + 5] == (byte)'/' &&
                                            bytes[q + 6] == (byte)'/')
                                        {
                                            e = q;
                                            break;
                                        }
                                    }
                                    if (e < 0) break;

                                    // read until null
                                    int strEnd = e;
                                    while (strEnd < bytes.Length && bytes[strEnd] != 0) strEnd++;
                                    if (strEnd <= e) break;
                                    string s = System.Text.Encoding.UTF8.GetString(bytes, e, strEnd - e);

                                    // boundary: IBUF EAIDs (which contain "&ib=")
                                    // mark the end of the VB-info-block area.
                                    if (s.IndexOf("&ib=", StringComparison.OrdinalIgnoreCase) >= 0)
                                        break;

                                    // extract vb=N (only the LAST occurrence in
                                    // the EAID -- the prefix never contains "vb=")
                                    int vbIdx = s.LastIndexOf("vb=", StringComparison.OrdinalIgnoreCase);
                                    if (vbIdx >= 0)
                                    {
                                        int ns = vbIdx + 3, ne = ns;
                                        while (ne < s.Length && s[ne] >= '0' && s[ne] <= '9') ne++;
                                        if (ne > ns)
                                        {
                                            int vbN;
                                            if (int.TryParse(s.Substring(ns, ne - ns), out vbN))
                                            {
                                                uint relOff = (uint)(e - vbInfoAreaStart);
                                                vbInfoMap[relOff] = vbN;
                                            }
                                        }
                                    }
                                    p = strEnd + 1;
                                }
                            }

                            if (vbInfoMap.Count > 0)
                            {
                                Log($"[2018+] Parsed {vbInfoMap.Count} VB-info-blocks " +
                                    $"(vbInfoAreaStart=0x{vbInfoAreaStart:X}, " +
                                    $"vbOffsets table = {vbInfoAreaStart - vbufOffset2018} bytes / " +
                                    $"{(vbInfoAreaStart - vbufOffset2018) / 4} u32s) " +
                                    $"(vfo -> vb=N): " +
                                    string.Join(", ", vbInfoMap.OrderBy(kv => kv.Key)
                                        .Select(kv => $"+0x{kv.Key:X}=vb{kv.Value}")));
                            }
                        }

                        // Build vb_num -> loader VBUF index map by parsing
                        // each loaded vbuf's EAID name. The loader already
                        // populated vbufs[i].Name from the descriptor's
                        // VBUF EAID list, so this is a free name-based
                        // lookup -- the same machinery the existing
                        // generic/mirror reroutes use to resolve idxVb6 /
                        // idxVb11.
                        var vbNumToLoaderIdx = new Dictionary<int, int>();
                        for (int _vi = 0; _vi < vbufs.Count; _vi++)
                        {
                            string _nm = vbufs[_vi]?.Name ?? "";
                            int _vbIdx = _nm.LastIndexOf("vb=", StringComparison.OrdinalIgnoreCase);
                            if (_vbIdx >= 0)
                            {
                                int _ns = _vbIdx + 3, _ne = _ns;
                                while (_ne < _nm.Length && _nm[_ne] >= '0' && _nm[_ne] <= '9') _ne++;
                                if (_ne > _ns)
                                {
                                    int _vbN;
                                    if (int.TryParse(_nm.Substring(_ns, _ne - _ns), out _vbN))
                                    {
                                        // Last writer wins on duplicates.
                                        // Duplicates shouldn't occur for
                                        // well-formed descriptors but the
                                        // map-build is defensive.
                                        vbNumToLoaderIdx[_vbN] = _vi;
                                    }
                                }
                            }
                        }

                        const int MeshHeaderSizeBytes = 9 * 4;

                        for (int mIndex = 0; mIndex < numMeshes2018; mIndex++)
                        {
                            // NO mesh[i>0] allowlist for 2018+ Legacy.
                            //
                            // An earlier revision filtered mesh[i>0] to a
                            // short allowlist (number + generic + carbon
                            // number + carbon_number) on the theory that
                            // mesh[1..n] records are LOD / shadow-pass /
                            // rainbead duplicates of mesh[0] -- which is
                            // how F1 2018 Classic descriptors are
                            // organised. That filter is wrong for F1
                            // 2019-2020 Legacy: the log for a 2019
                            // McLaren shows safetycell carrying mesh[0]=
                            // 'body', mesh[1]='carbon_tile_1', mesh[2]=
                            // 'detail' as three DISTINCT rendered
                            // submeshes (paint on top of carbon backing,
                            // detail layer on top of paint), and other
                            // descriptors similarly carry mesh[i>0]
                            // records labelled disc / rear light /
                            // steering wheel / mirror_left / mirror_right
                            // / lcd / digi revs / flag / rainbeads --
                            // every one of those is legitimate visible
                            // geometry that went missing while the
                            // allowlist was active ("many submeshes are
                            // skipped" symptom reported 2026-04-19).
                            //
                            // F1 2019 McLaren legacy descriptors are
                            // simply authored differently than F1 2018:
                            // the label array carries a real per-submesh
                            // slot, not a tag for LOD/pass variants. We
                            // emit every mesh record. Duplicates, if any,
                            // are the descriptor's authored intent
                            // (e.g. four 'wheel' records = four wheels).
                            long meshOffset = MeshHeaderSizeBytes + (long)mIndex * 88;
                            if (meshOffset + 88 > bytes.Length)
                            {
                                Log($"  [SUBMESH {mIndex}] meshOffset={meshOffset} would exceed file size, stopping.");
                                break;
                            }

                            ms2.Position = meshOffset;

                            // -----------------------------
                            // First block of 6 uints
                            // -----------------------------
                            uint numVBsForMesh = br2.ReadUInt32();
                            uint unk2 = br2.ReadUInt32();
                            uint unk3 = br2.ReadUInt32();
                            uint firstIndex = br2.ReadUInt32();        // index offset (iofs)
                            uint indexCount = br2.ReadUInt32();        // index count (inum)
                            uint vertexOffsetIndex = br2.ReadUInt32(); // base vertex index (vofs)

                            // -----------------------------
                            // Bounding sphere block (4 floats)
                            // -----------------------------
                            float cx = br2.ReadSingle();
                            float cy = br2.ReadSingle();
                            float cz = br2.ReadSingle();
                            float radius = br2.ReadSingle();

                            // -----------------------------
                            // Remaining ints (3 + numATTR in original fmt)
                            // -----------------------------
                            uint nodeLinkOffsetForMesh = br2.ReadUInt32();
                            uint unk9 = br2.ReadUInt32();
                            uint nameIndex = br2.ReadUInt32();
                            uint unk11 = br2.ReadUInt32();
                            uint vertexFileOffset = br2.ReadUInt32(); // used with vbOffsets
                            uint map0 = br2.ReadUInt32();
                            uint map1 = br2.ReadUInt32();
                            uint map2 = br2.ReadUInt32();
                            uint map3 = br2.ReadUInt32();
                            uint map4 = br2.ReadUInt32();
                            uint map5 = br2.ReadUInt32();
                            uint map6 = br2.ReadUInt32();

                            Log($"  [SUBMESH_HDR {mIndex}] raw: numVBsForMesh={numVBsForMesh}, unk2={unk2}, unk3={unk3}, " +
                                $"firstIndex(iofs)={firstIndex}, indexCount(inum)={indexCount}, vertexOffsetIndex(vofs)={vertexOffsetIndex}, " +
                                $"center=({cx:F3},{cy:F3},{cz:F3}), radius={radius:F3}, " +
                                $"nodeLinkOffset={nodeLinkOffsetForMesh}, unk9={unk9}, nameIndex={nameIndex}, unk11={unk11}, " +
                                $"vertexFileOffset=0x{vertexFileOffset:X}, maps=[{map0},{map1},{map2},{map3},{map4},{map5},{map6}]");

                            int idxOffset = unchecked((int)firstIndex);
                            int idxCount = unchecked((int)indexCount);
                            int vOffset = unchecked((int)vertexOffsetIndex);

                            // Classic 2018 descriptors generally have a single IB
                            int ibufID = 0;
                            if (ibufID < 0 || ibufID >= ibufs.Count)
                            {
                                Log($"  [SUBMESH {mIndex}] Skipping mesh: invalid ibufID={ibufID} (ibufs.Count={ibufs.Count})");
                                continue;
                            }

                            // Choose VB exactly like the game data describes it:
                            // use vertexFileOffset + vbOffsets, with VB0 as fallback.
                            int preferredVBuf = PickPositionVBufferIndex(
                                vbufs,
                                vbOffsets,
                                vertexFileOffset,
                                idxVb0,
                                vertexOffsetIndex);

                            if (preferredVBuf < 0 || preferredVBuf >= vbufs.Count)
                            {
                                Log($"  [SUBMESH {mIndex}] Skipping mesh: invalid VBuf={preferredVBuf} (vbufs.Count={vbufs.Count})");
                                continue;
                            }

                            // Snapshot of the picker's first-pass choice so the
                            // vbinfo-reroute gate downstream can tell whether
                            // any prior reroute (POS-RETARGET, generic-reroute,
                            // mirror-reroute, STRIDE-12 RETARGET) changed it.
                            // The vbinfo-reroute is the most aggressive
                            // override and must NOT step on a routing decision
                            // an earlier authoritative path already made --
                            // see comment at the gate for the LOD8 rainbeads
                            // regression this guards against.
                            int initialPickerVBuf = preferredVBuf;

                            // Label selection: what we *use* versus what the header pointed at
                            string nameFromIndexHeader = (nameIndex < labels.Count)
                                ? labels[(int)nameIndex]
                                : $"nameIdx_{nameIndex}";
                            string nameFromMeshIndex = (mIndex < labels.Count)
                                ? labels[mIndex]
                                : $"mesh_{mIndex}";

                            string name = nameFromMeshIndex; // keep current behavior
                            string lowerName = name.ToLowerInvariant();

                            Log($"  [SUBMESH_NAME {mIndex}] nameIndex={nameIndex}, label[nameIndex]='{nameFromIndexHeader}', " +
                                $"label[meshIndex={mIndex}]='{nameFromMeshIndex}', using name='{name}'");

                            // Generic-slot force-reroute to vb=6.
                            //
                            // Briefly removed on the theory that
                            // descriptor-authored VBs should always be
                            // trusted (per the 2018 Classic fix where the
                            // same force was removed). That was wrong for
                            // F1 2019 Legacy: the 2019 VB layout has two
                            // vertex groups -- main (vb=0..5, 183042
                            // verts) and secondary (vb=6..10, 86817
                            // verts) -- and safetycell's generic
                            // submeshes belong in the secondary group
                            // (interior tech bits, antennas, cockpit
                            // internals) even though their mesh records
                            // carry a vertexFileOffset that Legacy's
                            // vbOffsets-match routes to the MAIN group's
                            // stride-8 normal VB.
                            //
                            // Empirical: for safetycell's mesh[1]=generic,
                            // vertexFileOffset=0x1BC=444. 444 / 74 = 6
                            // exactly (each VB name-table entry is ~74
                            // bytes), so the descriptor's own name-table
                            // index points at vb=6 = secondary-group
                            // position. The force-reroute makes our
                            // routing match what the descriptor actually
                            // authored.
                            //
                            // Generic ONLY. Detail routes fine via the
                            // normal picker; the pre-fix version that
                            // also forced detail to vb=6 was incorrect
                            // and produced displaced detail geometry.
                            if (lowerName.Contains("generic") &&
                                idxVb6 >= 0 && idxVb6 < vbufs.Count &&
                                preferredVBuf != idxVb6)
                            {
                                Log($"  [generic-reroute {mIndex}] '{name}': VBuf {preferredVBuf} -> vb=6 ({idxVb6}) " +
                                    $"(2019 secondary group routing)");
                                preferredVBuf = idxVb6;
                            }

                            // 2020 ADDENDUM: mirror_left / mirror_right reroute
                            // to vb=11.
                            //
                            // In F1 2020 mirror glass got folded into the
                            // safetycell mesh as two extra submeshes that
                            // carry numVBsForMesh=1 and a single VB-info-block
                            // selector at vertexFileOffset=0x32F (=815). That
                            // block's EAID payload is "...&vb=11" -- i.e. the
                            // mirrors live in vbuf 11 (stride 40, FVF 0x27,
                            // 156 verts in the McLaren ERP), NOT in vb=6.
                            // The picker doesn't follow the VB-info-block
                            // pointer for size-1 submeshes and falls back to
                            // a segment match that lands on a stride-8
                            // packed-normals buffer; without this gate the
                            // GEOMETRY stage drops the mirrors with
                            // "stride 8 too small for 3 floats" and the
                            // mirror glass disappears from the export.
                            //
                            // Verified empirically: vb=11[0..19] bounds
                            // (-0.626..0.484, 0.396..0.447, 0.664..0.718)
                            // with ctr (+0.555, 0.422, 0.691) match the
                            // mirror_left submesh sphere center
                            // (+0.554, 0.429, 0.691) radius 0.080;
                            // vb=11[20..39] mirror those across X for
                            // mirror_right. Position is at byte offset 0
                            // of each 40-byte vertex.
                            //
                            // Pool-narrow: idxVb11 is only set when the pool
                            // actually has an EAID-named "vb=11" stream,
                            // which for the McLaren 2020 ERP is the
                            // safetycell pool only. Pools without a vb=11
                            // (everything outside safetycell) leave
                            // idxVb11 = -1 and this gate is a no-op, so
                            // older ERPs where mirror_* are their own pools
                            // are unaffected.
                            if ((lowerName == "mirror_left" || lowerName == "mirror_right") &&
                                idxVb11 >= 0 && idxVb11 < vbufs.Count &&
                                preferredVBuf != idxVb11)
                            {
                                Log($"  [mirror-reroute {mIndex}] '{name}': VBuf {preferredVBuf} -> vb=11 ({idxVb11}) " +
                                    $"(2020 mirror-glass routing)");
                                preferredVBuf = idxVb11;
                            }

                            // ADDITIVE (mirror-glass fallback override):
                            // The vb=11-by-name reroute above is correct for
                            // F1 2020 stock content where vb=11 IS the dedi-
                            // cated mirror-glass VB (stride 40, ~156 verts).
                            // Some classic descriptors -- F1 2019 c_ferrari_90
                            // wing_mirror_l / wing_mirror_r in particular --
                            // reuse the body position pool as their first VB
                            // and happen to label it vb=11, while the actual
                            // mirror glass lives in vb=17 (stride 40, 148
                            // verts). On those, the vb=11-by-name reroute
                            // above lands on the BODY position buffer
                            // (~100k verts) and the mirror submesh reads the
                            // first ~50 vertices of the body instead of the
                            // mirror mesh.
                            //
                            // Override condition: a Stride=40 / FVF=0x27 VB
                            // was identified (idxMirrorGlass >= 0), AND the
                            // currently-selected VBUF is body-sized (Count
                            // > 1000 -- mirror VBs are always under a few
                            // hundred verts; the smallest body pool I've
                            // logged is ~3k), AND they're different VBUFs.
                            //
                            // For F1 2020 stock McLaren idxMirrorGlass and
                            // idxVb11 resolve to the same VBUF index, so
                            // either the "different VBUFs" gate fails or
                            // the "body-sized" gate fails (the McLaren vb=11
                            // is 156 verts), and this override is a no-op
                            // -- the existing reroute's result stands.
                            //
                            // For descriptors with no Stride=40 VB at all
                            // idxMirrorGlass stays -1 and this override
                            // never fires. So every prior load's behaviour
                            // is preserved; only the broken Ferrari-style
                            // case is rerouted.
                            if ((lowerName == "mirror_left" || lowerName == "mirror_right") &&
                                idxMirrorGlass >= 0 && idxMirrorGlass < vbufs.Count &&
                                preferredVBuf >= 0 && preferredVBuf < vbufs.Count &&
                                preferredVBuf != idxMirrorGlass &&
                                vbufs[preferredVBuf].Count > 1000)
                            {
                                Log($"  [mirror-glass-fallback {mIndex}] '{name}': VBuf {preferredVBuf} " +
                                    $"({vbufs[preferredVBuf].Count} verts, stride={vbufs[preferredVBuf].Stride}) " +
                                    $"-> {idxMirrorGlass} ({vbufs[idxMirrorGlass].Count} verts, " +
                                    $"stride={vbufs[idxMirrorGlass].Stride}) " +
                                    $"(dedicated Stride=40 mirror-glass VB)");
                                preferredVBuf = idxMirrorGlass;
                            }

                            // -------------------------------------------------
                            // STRIDE-12 RETARGET (2019 fallback-path GEOMETRY)
                            // -------------------------------------------------
                            //
                            // PickPositionVBufferIndex's IsPosCandidate filter
                            // rejects only UV VBs (Fvf==29), accepting any
                            // non-UV stride. For fallback-path 2019 carbon
                            // submeshes (frontspoiler_fm, diffuser_bm,
                            // camera_wing, hub_caliper_f*, sidewing_*,
                            // suspension*_fl/fr) the submesh's
                            // vertexFileOffset (0x173 in the McLaren 2019 ERP)
                            // exact-matches a stride-8 secondary-group buffer
                            // -- &vb=7 -- which is NOT a position buffer. The
                            // picker returns it, the position decoder then
                            // reads 12-byte xyz floats from 8-byte stride,
                            // and vertex positions come out misplaced. The
                            // geometry FINALIZEs because the decoder doesn't
                            // crash, but the carbon panel renders at the
                            // wrong physical position -- which is what the
                            // user has been calling "carbon mat partially
                            // loads" since round 5. The previous four
                            // rounds chased this as a UV problem; analyzing
                            // mclaren_carbon1.material confirmed
                            // me_carbonTile=1.0 (shader doesn't scale UVs),
                            // ruling that out and leaving geometry as the
                            // remaining suspect. Inspecting the IDF binary
                            // confirmed: frontspoiler carbon's
                            // vertexFileOffset 0x173 -> VB[1] (stride-8,
                            // picked by lenient filter), but maps[3] = 0x29B
                            // -> VB[5] (stride-12, MAIN positions
                            // count=183042). The MAIN position buffer is
                            // referenced via a map slot, not via
                            // vertexFileOffset, for these submeshes. By
                            // contrast, warm-path carbons (safetycell etc.)
                            // and all body/detail/generic submeshes have
                            // their position offset in vertexFileOffset
                            // directly, so the picker gets them right.
                            //
                            // The retarget: if preferredVBuf is non-stride-12
                            // AND any reference in the submesh's list resolves
                            // to a stride-12 buffer, switch preferredVBuf to
                            // the FIRST such stride-12 reference. This is the
                            // pattern observed in the file: the position VB
                            // is always the first stride-12 in the reference
                            // list (vertexFileOffset, map0, map1, map2, ...
                            // up to numVBsForMesh entries).
                            //
                            // No-op for every case the picker already gets
                            // right -- if preferredVBuf is stride-12 the gate
                            // stops the block, and if no reference is
                            // stride-12 there's nothing to switch to. Safe
                            // for the warm path (preferredVBuf=stride-12),
                            // safe for body/detail/generic (preferredVBuf=
                            // stride-12), only triggers on the fallback-path
                            // carbon submeshes that have been broken since
                            // round 5.
                            if (preferredVBuf >= 0 && preferredVBuf < vbufs.Count &&
                                vbufs[preferredVBuf] != null &&
                                vbufs[preferredVBuf].Stride != 12)
                            {
                                uint[] refs = {
                                    vertexFileOffset,
                                    map0, map1, map2, map3, map4, map5, map6
                                };
                                int nRefs = (int)Math.Min((uint)refs.Length, numVBsForMesh);
                                int retargetPick = -1;
                                int retargetRefIdx = -1;
                                for (int r = 0; r < nRefs; r++)
                                {
                                    uint refOff = refs[r];
                                    for (int i = 0;
                                         i < vbOffsets.Length && i < vbufs.Count;
                                         i++)
                                    {
                                        if (vbOffsets[i] == refOff &&
                                            vbufs[i] != null &&
                                            vbufs[i].Stride == 12 &&
                                            vbufs[i].Fvf != 29)
                                        {
                                            retargetPick = i;
                                            retargetRefIdx = r;
                                            break;
                                        }
                                    }
                                    if (retargetPick >= 0) break;
                                }
                                if (retargetPick >= 0 && retargetPick != preferredVBuf)
                                {
                                    Log($"  [POS-RETARGET {mIndex}] '{name}' VB {preferredVBuf} " +
                                        $"(stride={vbufs[preferredVBuf].Stride}, Fvf=0x{vbufs[preferredVBuf].Fvf:X}) " +
                                        $"-> VB {retargetPick} (stride=12, Fvf=0x{vbufs[retargetPick].Fvf:X}) " +
                                        $"via reference[{retargetRefIdx}] offset 0x{refs[retargetRefIdx]:X}");
                                    preferredVBuf = retargetPick;
                                }
                            }

                            // -------------------------------------------------
                            // VB-INFO-BLOCK FALLBACK
                            // -------------------------------------------------
                            //
                            // Last-resort routing for submeshes whose final
                            // preferredVBuf disagrees with the descriptor's
                            // authoritative VB-info-block chain.
                            //
                            // The submesh's vertexFileOffset is a byte-offset
                            // into the descriptor's VB-info-block area; the
                            // EAID at that offset names the VBuf the engine
                            // would use. We resolve vfo -> vb=N (via
                            // vbInfoMap, parsed once per descriptor above)
                            // and vb=N -> loader VBUF index (via
                            // vbNumToLoaderIdx, built from each vbuf's EAID
                            // name). If the resolved VBUF is a valid
                            // position buffer (stride>=12, Fvf!=29) and is
                            // DIFFERENT from preferredVBuf, route to it.
                            //
                            // This covers two distinct failure modes:
                            //
                            // (1) Picker landed on a stride<12 packed-normals
                            //     buffer that the GEOMETRY stage would SKIP
                            //     with "VBUF stride 8 too small for 3 floats".
                            //     Without this gate the submesh disappears
                            //     from the export. Examples (McLaren 2020):
                            //     LOD0 'detail' in 11 pools (vfo=0x1BC -> vb=6),
                            //     LOD0 'carbon_tile_1' in rearwing_top /
                            //     sidewing_* (vfo=0x173 -> vb=0),
                            //     LOD0 'rear light' in safetycell (vfo=0x1BC -> vb=6),
                            //     LOD1 'body' / 'carbon_tile_1' / 'number'
                            //     in safetycell (vfo=0x1C2 -> vb=0),
                            //     LOD1 'detail' in camera_wing /
                            //     rearwing_top / sidewing_* (vfo=0x37E -> vb=6).
                            //
                            // (2) Picker landed on a stride-12 buffer but
                            //     the WRONG one. This happens when the
                            //     descriptor stores its VBUF EAIDs in a
                            //     non-natural order (e.g. McLaren 2020 LOD1
                            //     non-safetycell pools list vb=12..17
                            //     before vb=0..10, so vbufs[6] is vb=0 not
                            //     vb=6) and the picker's vbOffsets[]-based
                            //     "segment match" returns a stride-12 hit
                            //     that happens to be a different vb=N from
                            //     what the submesh's vfo actually points to.
                            //     The submesh FINALIZEs with no warning but
                            //     reads vertex data from the wrong buffer,
                            //     producing visually correct-looking but
                            //     positionally distorted geometry. Examples:
                            //     LOD1 'detail' in 9 pools (frontflap_*,
                            //     frontspoiler_fm, frontwing_*, nosecone,
                            //     sidepod_*, topcanopy_bm) where picker
                            //     picks VB 6=vb=0 but descriptor says vb=6
                            //     (= VBUF 12 in those pools);
                            //     LOD1 'carbon_tile_1' in rearwing_top /
                            //     sidewing_* where picker picks VB 6=vb=6
                            //     but descriptor says vb=0 (= VBUF 11 in
                            //     those pools).
                            //
                            // The target-side guard `Stride >= 12 && Fvf
                            // != 29` keeps the 2019 fallback-path carbon
                            // case (POS-RETARGET) untouched: those
                            // submeshes have a vfo that points to a
                            // stride-8 buffer in the descriptor, so
                            // vbInfoMap[vfo] resolves to a stride<12 vbuf
                            // and the gate skips. POS-RETARGET's correct
                            // routing (via maps[]) stands.
                            //
                            // The pre-existing reroutes (generic, mirror,
                            // POS-RETARGET) and the picker run FIRST. If
                            // any of them already routed preferredVBuf to
                            // the same loader index this gate would have
                            // chosen, `correctLoaderIdx != preferredVBuf`
                            // makes this a no-op.
                            //
                            // PRIOR-REROUTE GUARD: if the picker's choice has
                            // already been changed by POS-RETARGET / generic-
                            // reroute / mirror-reroute / STRIDE-12 RETARGET
                            // (preferredVBuf != initialPickerVBuf), the
                            // vbinfo-reroute MUST NOT step on it -- those
                            // reroutes follow the descriptor's per-submesh
                            // maps[] / reference[] chain, which is the
                            // engine-authoritative routing for any submesh
                            // whose vbuf set is reached via maps (notably
                            // 'rainbeads', whose POS-RETARGET via maps[5]
                            // lands on the secondary group's stride-12 buffer
                            // and must NOT then be overridden to vb=12 by the
                            // global vfo->vb chain).
                            //
                            // The original purpose of the vbinfo-reroute --
                            // rescuing picker choices that landed on stride<12
                            // packed-normals buffers -- is preserved because
                            // the picker's stride-8 choice is the picker's
                            // FINAL choice (no prior reroute fired), so
                            // preferredVBuf == initialPickerVBuf and the gate
                            // fires normally.
                            //
                            // The LOD1 distortion fix is also preserved
                            // because in those cases the picker lands on a
                            // stride-12 buffer DIRECTLY (no POS-RETARGET
                            // needed), so again preferredVBuf ==
                            // initialPickerVBuf and the gate fires.
                            if (preferredVBuf >= 0 && preferredVBuf < vbufs.Count &&
                                vbufs[preferredVBuf] != null &&
                                preferredVBuf == initialPickerVBuf)
                            {
                                int correctVbN;
                                int correctLoaderIdx;
                                if (vbInfoMap.TryGetValue(vertexFileOffset, out correctVbN) &&
                                    vbNumToLoaderIdx.TryGetValue(correctVbN, out correctLoaderIdx) &&
                                    correctLoaderIdx >= 0 && correctLoaderIdx < vbufs.Count &&
                                    vbufs[correctLoaderIdx] != null &&
                                    vbufs[correctLoaderIdx].Stride >= 12 &&
                                    vbufs[correctLoaderIdx].Fvf != 29 &&
                                    correctLoaderIdx != preferredVBuf)
                                {
                                    Log($"  [vbinfo-reroute {mIndex}] '{name}' VB {preferredVBuf} " +
                                        $"(stride={vbufs[preferredVBuf].Stride}, Fvf=0x{vbufs[preferredVBuf].Fvf:X}) " +
                                        $"-> VB {correctLoaderIdx} (vb={correctVbN}, " +
                                        $"stride={vbufs[correctLoaderIdx].Stride}, " +
                                        $"Fvf=0x{vbufs[correctLoaderIdx].Fvf:X}) " +
                                        $"via VB-info-block chain at vfo=0x{vertexFileOffset:X}");
                                    preferredVBuf = correctLoaderIdx;
                                }
                            }

                            // Extra logging for safetycell parts (diagnostic only)
                            if (isSafetyCellDescriptor &&
                                (lowerName.Contains("generic") || lowerName.Contains("carbon tile 1")))
                            {
                                Log($"  [safetycell/{name} {mIndex}] BEFORE clamp: VB={preferredVBuf}, ibufID={ibufID}, " +
                                    $"idxOffset={idxOffset}, idxCount={idxCount}, vOffset={vOffset}, vertexFileOffset=0x{vertexFileOffset:X}");
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
                                    Log($"  [SUBMESH {mIndex}] '{name}' idxOffset {idxOffset} >= IB.Count {ibInfo.Count}, skipping mesh.");
                                    continue;
                                }

                                if (idxOffset + idxCount > ibInfo.Count)
                                {
                                    int maxCount = ibInfo.Count - idxOffset;
                                    Log($"  [SUBMESH {mIndex}] '{name}' Clamp indexCount {idxCount} -> {maxCount} (IB.Count={ibInfo.Count}).");
                                    idxCount = maxCount;
                                }
                            }

                            // ---------- VERTEX OFFSET SANITY ----------
                            if (vbInfo.Count > 0)
                            {
                                if (vOffset < 0 || vOffset >= vbInfo.Count)
                                {
                                    Log($"  [SUBMESH {mIndex}] '{name}' VOffset {vOffset} out of VB[{preferredVBuf}] range (Count={vbInfo.Count}), forcing 0.");
                                    vOffset = 0;
                                }
                            }
                            else
                            {
                                if (vOffset < 0)
                                    vOffset = 0;
                            }

                            Log($"  [SUBMESH_PARSED {mIndex}] '{name}': iofs={idxOffset}, inum={idxCount}, vofs={vOffset}, " +
                                $"IBUF={ibufID} (Count={ibInfo.Count}, IndexSize={ibInfo.IndexSize}), " +
                                $"VBUF={preferredVBuf} (Count={vbInfo.Count}, Stride={vbInfo.Stride}, FVF=0x{vbInfo.Fvf:X}).");

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
                    // Non-2018+: single big mesh from header range
                    if (headerIndexCount > 0)
                    {
                        string name = DeriveLegacyMeshName(meshFileName);

                        Log($"  [LEGACY_SINGLE] '{name}': iofs={headerIOffset}, inum={headerIndexCount}, vofs=0, " +
                            $"IBUF=0, VBUF={idxVb0}");

                        subMeshes.Add(new SubMeshInfo
                        {
                            Name = name,
                            IOffset = headerIOffset,
                            IndexCount = headerIndexCount,
                            VOffset = 0,
                            VBufferIndex = idxVb0,
                            IBufferIndex = 0
                        });
                    }
                    else
                    {
                        Log("  [LEGACY_SINGLE] No headerIndexCount, nothing to build.");
                    }
                }

                // Single exit point. Thread the descriptor's part name
                // (e.g. "safetycell") and full path through to the
                // geometry builder so each submesh registers under the
                // descriptor's actual part name rather than the slot
                // label, and the exporter sees a proper slot/material
                // EAID per submesh instead of falling back to "default".
                string descriptorPartName = ExtractPartNameFromDescriptorFilename(meshTemp000Path);
                return BuildGeometryFromSubMeshes(
                    vbufs, ibufs, subMeshes, teamPrefix,
                    descriptorPartName, meshTemp000Path);
            }
        }


        // =====================================================================
        // SHARED GEOMETRY BUILDER (positions + normals + pair-1 UV + pair-2 UV)
        // =====================================================================

        private static Model3DGroup BuildGeometryFromSubMeshes(
            List<VertexBufferInfo> vbufs,
            List<IndexBufferInfo> ibufs,
            List<SubMeshInfo> subMeshes,
            string teamPrefix,
            string descriptorPartName,
            string descriptorPath)
        {
            var group = new Model3DGroup();
            Log("--- BUILDING MESH GEOMETRY ---");

            foreach (var sm in subMeshes)
            {
                Log($"[GEOMETRY] Starting build for SubMesh '{sm.Name}'.");

                // v12.10 (additive filter): rainbeads NEVER loads on ANY
                // model. Single-line filter at the top of the loop; the
                // original "F1 2020 ONLY" gate further below remains
                // intact and untouched -- it just never fires anymore
                // because this filter catches every rainbeads-slot
                // submesh first. No existing geometry-parsing logic
                // touched; this is a pure filter.
                if (string.Equals(sm.Name, "rainbeads", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[GEOMETRY] SubMesh '{sm.Name}' SKIPPED by v12.10 filter.");
                    continue;
                }

                // ---------------------------------------------------------
                // F1 2020 ONLY: skip rainbeads-slot submeshes.
                // ---------------------------------------------------------
                // F1 2019 ships a parallel set of "*_water" mesh descriptors
                // (safetycell_water, nosecone_water, frontflap_fl_water, ...)
                // whose only submeshes are the wet-weather rainbeads layer.
                // Those are skipped at the descriptor-level filter in
                // LoadFolder (the "_water in path -> skip" block above
                // the per-descriptor try/catch).
                //
                // F1 2020 dropped the per-mesh _water variants entirely --
                // rain became a screen-space effect at the renderer
                // level -- so there are no "_water" descriptors for the
                // descriptor-level skip to catch. But the rainbeads
                // material slot still appears INSIDE regular dry
                // descriptors (body, safetycell, frontwing, ...) as a
                // separate mesh record, intended to be enabled by the
                // wet-weather shader's wetness-intensity uniform.
                //
                // The static preview can't read that uniform, so a
                // rainbeads submesh renders unconditionally on top of
                // the body. Because the rainbeads BC1 texture has no
                // alpha and tiles 16x across the surface, the result
                // is opaque grey droplet patterns spread over every
                // body and carbon panel -- the "distortion" symptom
                // reported on the F1 2020 McLaren preview (2026-05-09).
                //
                // Mirrors F1ModelLoader.cs:1651 (the 2021+ loader's
                // identical rainbeads-on-dry-parts skip). Gated strictly
                // on _currentGameYear == 2020 so F1 2019 stays on the
                // descriptor-level _water skip path it already uses;
                // anything reading rainbeads on 2019 is intentional
                // _water-descriptor geometry that the upstream filter
                // already drops, so 2019 behaviour is byte-identical
                // to before this change.
                //
                // _currentGameYear is set by UpdateGameYearFromString
                // earlier in the descriptor walk (it sniffs path /
                // name strings for "f1_2020" / "2020" / "f1_2019" /
                // "2019" markers). When neither was seen the gate
                // stays 0 and this skip never fires -- safe default
                // for any unknown-year load.
                if (_currentGameYear == 2020 &&
                    string.Equals(sm.Name, "rainbeads", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[GEOMETRY] (2020) SubMesh '{sm.Name}' SKIPPED: " +
                        "rainbeads slot on dry body part (2020 wet-weather " +
                        "is screen-space; rendering it would distort the preview).");
                    continue;
                }

                // 1. IBUF Data Load and Range Check
                if (sm.IBufferIndex < 0 || sm.IBufferIndex >= ibufs.Count)
                {
                    Log($"[GEOMETRY] SubMesh '{sm.Name}' SKIPPED: References invalid IBUF index {sm.IBufferIndex}.");
                    continue;
                }

                var ib = ibufs[sm.IBufferIndex];
                if (!File.Exists(ib.DataPath))
                {
                    Log($"[GEOMETRY] SubMesh '{sm.Name}' SKIPPED: IBUF data file not found: {ib.DataPath}");
                    continue;
                }

                byte[] iData = File.ReadAllBytes(ib.DataPath);
                long iByteOffset = (long)sm.IOffset * ib.IndexSize;
                long iByteCount = (long)sm.IndexCount * ib.IndexSize;

                Log($"[GEOMETRY] IBUF[{sm.IBufferIndex}] Checks: IndexSize={ib.IndexSize}, IOffset={sm.IOffset} (ByteOffset={iByteOffset}), IndexCount={sm.IndexCount} (ByteCount={iByteCount}), IBUF Data File Length={iData.LongLength}");

                if (iByteOffset + iByteCount > iData.LongLength)
                {
                    Log($"[GEOMETRY] SubMesh '{sm.Name}' SKIPPED: Index range out of bounds. (Offset+Count)={iByteOffset + iByteCount} > FileLength={iData.LongLength}.");
                    continue;
                }

                var indices = new int[sm.IndexCount];
                int maxIndex = 0;

                for (int k = 0; k < sm.IndexCount; k++)
                {
                    long basePos = iByteOffset + (long)k * ib.IndexSize;
                    int idx;
                    if (ib.IndexSize == 2)
                    {
                        idx = BitConverter.ToUInt16(iData, (int)basePos);
                    }
                    else
                    {
                        idx = BitConverter.ToInt32(iData, (int)basePos);
                    }

                    indices[k] = idx;
                    if (idx > maxIndex) maxIndex = idx;
                }

                Log($"[GEOMETRY] SubMesh '{sm.Name}': Decoded {indices.Length} indices. Max Index Value={maxIndex}. VOffset={sm.VOffset}.");

                // 2. VBUF Auto-Selection and Range Check
                int vIndex = sm.VBufferIndex;
                bool vbufNeedsFinder = false;
                long neededVerts = (long)sm.VOffset + maxIndex + 1;

                Log($"[GEOMETRY] Needed Vertices Calculation: VOffset ({sm.VOffset}) + MaxIndex ({maxIndex}) + 1 = {neededVerts}.");

                if (vIndex >= 0 && vIndex < vbufs.Count)
                {
                    var testVb = vbufs[vIndex];
                    if (neededVerts > testVb.Count)
                    {
                        Log($"[GEOMETRY] VBUF[{vIndex}] WARN: Requested buffer Count ({testVb.Count}) is too small for Needed Vertices ({neededVerts}). Falling back to auto-picker.");
                        vbufNeedsFinder = true;
                    }
                    else
                    {
                        Log($"[GEOMETRY] VBUF[{vIndex}] OK: Requested buffer Count ({testVb.Count}) is sufficient.");
                    }
                }
                else
                {
                    Log($"[GEOMETRY] VBUF Finder Triggered: Invalid initial index ({vIndex}) or explicit request.");
                    vbufNeedsFinder = true;
                }

                if (vbufNeedsFinder)
                {
                    int chosen = -1;
                    int chosenCount = int.MaxValue;

                    Log("[GEOMETRY] Starting Auto-Selection Search...");
                    List<string> candidates = new List<string>();

                    for (int i = 0; i < vbufs.Count; i++)
                    {
                        var candidate = vbufs[i];
                        if (candidate.Stride < 8)
                        {
                            candidates.Add($"VBUF[{i}] (Stride {candidate.Stride}) - SKIPPED (Stride too small).");
                            continue;
                        }

                        if (candidate.Count >= neededVerts)
                        {
                            candidates.Add($"VBUF[{i}] (Stride {candidate.Stride}, Count {candidate.Count}) - FIT.");
                            if (candidate.Count < chosenCount)
                            {
                                chosen = i;
                                chosenCount = candidate.Count;
                            }
                        }
                        else
                        {
                            candidates.Add($"VBUF[{i}] (Stride {candidate.Stride}, Count {candidate.Count}) - TOO SMALL (Needed {neededVerts}).");
                        }
                    }
                    Log("[GEOMETRY] VBUF Candidate List:\r\n" + string.Join("\r\n", candidates.Select(s => $"  -> {s}")));

                    if (chosen < 0)
                    {
                        Log($"[GEOMETRY] SubMesh '{sm.Name}' SKIPPED: Auto-picker failed. No suitable VBUF found that fits {neededVerts} vertices.");
                        continue;
                    }

                    vIndex = chosen;
                    Log($"[GEOMETRY] Auto-Selected VBUF[{vIndex}] (count={vbufs[vIndex].Count}, stride={vbufs[vIndex].Stride}) for submesh '{sm.Name}'.");
                }

                var vb = vbufs[vIndex];
                if (!File.Exists(vb.DataPath))
                {
                    Log($"[GEOMETRY] SubMesh '{sm.Name}' SKIPPED: VBUF data file not found: {vb.DataPath}");
                    continue;
                }

                byte[] vData = File.ReadAllBytes(vb.DataPath);
                if (sm.VOffset < 0)
                    sm.VOffset = 0;

                long vStartByte = (long)sm.VOffset * vb.Stride;
                long neededBytes = vStartByte + (long)(maxIndex + 1) * vb.Stride;

                Log($"[GEOMETRY] VBUF[{vIndex}] Checks: Stride={vb.Stride}, VOffset={sm.VOffset} (StartByte={vStartByte}), MaxIndex={maxIndex}, NeededBytes={neededBytes}, VBUF Data File Length={vData.LongLength}");

                if (neededBytes > vData.LongLength)
                {
                    Log($"[GEOMETRY] SubMesh '{sm.Name}' SKIPPED: Vertex range out of bounds. (StartByte + NeededBytes)={neededBytes} > FileLength={vData.LongLength}.");
                    continue;
                }

                // 3. Position Extraction (version-aware)
                var positions = new Point3D[maxIndex + 1];

                // Default unknown -> treat as 2019 behaviour
                int gameYear = _currentGameYear == 0 ? 2019 : _currentGameYear;
                Log($"[GEOMETRY] Using game year mode: {gameYear} for submesh '{sm.Name}'.");

                if (gameYear == 2020)
                {
                    // ------------------------------------------------------------
                    // F1 2020 path:
                    //   - Positions as floats (like Noesis)
                    //   - Guard: don't read 3 floats from a stride < 12
                    //   - Extra bounding box sanity check to kill giant balls
                    // ------------------------------------------------------------
                    bool useHalfFloat = false;
                    int posOffset = 0;

                    if (vb.Stride == 16)
                    {
                        posOffset = 4;
                    }

                    Log($"[GEOMETRY] (2020) Position Parsing Config: Stride={vb.Stride}, HalfFloat={useHalfFloat}, Position Offset={posOffset}.");

                    if (vb.Stride < 12)
                    {
                        Log($"[GEOMETRY] (2020) SubMesh '{sm.Name}' SKIPPED: VBUF stride {vb.Stride} too small for 3 floats.");
                        continue;
                    }

                    for (int vi = 0; vi <= maxIndex; vi++)
                    {
                        long off = vStartByte + (long)vi * vb.Stride;

                        float x = BitConverter.ToSingle(vData, (int)off + posOffset + 0);
                        float y = BitConverter.ToSingle(vData, (int)off + posOffset + 4);
                        float z = BitConverter.ToSingle(vData, (int)off + posOffset + 8);

                        positions[vi] = new Point3D(x, y, z);
                    }

                    // 3.5 – bounding-box sanity check
                    double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

                    for (int i = 0; i < positions.Length; i++)
                    {
                        var p = positions[i];
                        if (p.X < minX) minX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.Z < minZ) minZ = p.Z;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y > maxY) maxY = p.Y;
                        if (p.Z > maxZ) maxZ = p.Z;
                    }

                    double extentX = maxX - minX;
                    double extentY = maxY - minY;
                    double extentZ = maxZ - minZ;
                    double maxExtent = Math.Max(extentX, Math.Max(extentY, extentZ));

                    Log($"[GEOMETRY] (2020) SubMesh '{sm.Name}' bounds: " +
                        $"min=({minX:F3},{minY:F3},{minZ:F3}), " +
                        $"max=({maxX:F3},{maxY:F3},{maxZ:F3}), " +
                        $"extents=({extentX:F3},{extentY:F3},{extentZ:F3}), maxExtent={maxExtent:F3}.");

                    if (maxExtent > MaxReasonableExtent2020 || double.IsNaN(maxExtent) || double.IsInfinity(maxExtent))
                    {
                        Log($"[GEOMETRY] (2020) SubMesh '{sm.Name}' SKIPPED: unreasonable size (maxExtent={maxExtent:F3} > {MaxReasonableExtent2020}).");
                        continue;
                    }
                }
                else
                {
                    // ------------------------------------------------------------
                    // F1 2019 path (and default):
                    //   - This is your existing behaviour that works for 2019
                    // ------------------------------------------------------------
                    bool useHalfFloat = false;
                    int posOffset = 0;

                    // Only apply 4-byte offset for known 16-byte layout
                    if (vb.Stride == 16)
                    {
                        posOffset = 4;
                    }

                    Log($"[GEOMETRY] (2019) Position Parsing Config: Stride={vb.Stride}, HalfFloat={useHalfFloat}, Position Offset={posOffset}.");

                    for (int vi = 0; vi <= maxIndex; vi++)
                    {
                        long off = vStartByte + (long)vi * vb.Stride;
                        float x, y, z;

                        if (useHalfFloat)
                        {
                            // Half float positions (currently unused, but kept for compatibility)
                            ushort hx = BitConverter.ToUInt16(vData, (int)off + 0);
                            ushort hy = BitConverter.ToUInt16(vData, (int)off + 2);
                            ushort hz = BitConverter.ToUInt16(vData, (int)off + 4);

                            x = HalfToFloat(hx);
                            y = HalfToFloat(hy);
                            z = HalfToFloat(hz);
                        }
                        else
                        {
                            x = BitConverter.ToSingle(vData, (int)off + posOffset + 0);
                            y = BitConverter.ToSingle(vData, (int)off + posOffset + 4);
                            z = BitConverter.ToSingle(vData, (int)off + posOffset + 8);
                        }

                        positions[vi] = new Point3D(x, y, z);
                    }
                }

                // 4. Finalization and Stats
                int triangleCount = indices.Length / 3;
                int vertexCount = positions.Length;

                _totalSubmeshes++;
                _totalIndices += indices.Length;
                _totalTriangles += triangleCount;
                _totalVertices += vertexCount;

                Log($"[GEOMETRY] SubMesh '{sm.Name}' FINALIZED. Indices={indices.Length}, Triangles={triangleCount}, Vertices={vertexCount}.");

                // -----------------------------------------------------
                // NORMAL DECODE (first stride-8 companion VB after pos)
                // -----------------------------------------------------
                // The normal VB follows the position VB in descriptor
                // order. F1 2019 packs xyz as three half-floats (plus a
                // 4th half-float w we ignore) in a stride-8 block.
                // Normals are only consumed for the overlay offset so
                // sponsors/numbers sit above the base without z-fight;
                // gracefully skipped on any failure.
                Vector3DCollection pendingNormals = null;
                int normalVbIdx = FindCompanionStreamIndex(
                    vbufs, vIndex, expectedStride: 8, skipMatches: 0);
                if (normalVbIdx >= 0)
                {
                    var nvb = vbufs[normalVbIdx];
                    if (File.Exists(nvb.DataPath))
                    {
                        try
                        {
                            byte[] nData = File.ReadAllBytes(nvb.DataPath);
                            long nStart = (long)sm.VOffset * nvb.Stride;
                            long nEnd = nStart + (long)(maxIndex + 1) * nvb.Stride;
                            if (nEnd <= nData.Length)
                            {
                                pendingNormals = DecodeHalf4NormalsBlock(
                                    nData, nStart, nvb.Stride, maxIndex);
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

                // -----------------------------------------------------
                // PAIR-1 UV DECODE (third stride-8 companion: pos, norm,
                // tangent, [UV])
                // -----------------------------------------------------
                // FindCompanionStreamIndex with skipMatches=2 skips the
                // normal + tangent streams and lands on the UV stream.
                // Each vertex's stride-8 window holds 4 half-floats
                // encoding two UV pairs packed together:
                //   [0..3]: pair-1 (u1, v1)  -- base colour atlas
                //   [4..7]: pair-2 (u2, v2)  -- overlay / decal atlas
                // Pair-1 goes into mesh.TextureCoordinates below;
                // pair-2 is decoded further down for the overlay pass.
                PointCollection pendingUvs = null;
                int uvVbIdx = FindCompanionStreamIndex(
                    vbufs, vIndex, expectedStride: 8, skipMatches: 2);

                // FALLBACK: stride-8-primary submeshes.
                //
                // The skipMatches=2 search above assumes the submesh's
                // primary is a stride-12 position VB and counts past
                // [normals, tangents] to land on the UV stream. That
                // assumption fails when the submesh header points its
                // primary at a stride-8 VB instead -- F1 2019 McLaren's
                // camera_wing carbon_tile_1 submesh does exactly that
                // (primary=VB 1, stride=8), and the after-primary walk
                // only sees 2 more stride-8 companions before running
                // out of VBs, so the search returns -1, pendingUvs
                // stays null, and the carbon mesh ships without UVs.
                // Visible to the user as the carbon weave texture
                // collapsing to a single texel-(0,0) sample (a flat
                // black or single-colour patch) on every part where
                // carbon_tile_1's submesh references the stride-8 VB
                // directly.
                //
                // The UV VB is at a FIXED LIST POSITION in the
                // descriptor's VB table (by descriptor-build convention,
                // the 3rd stride-8 buffer counting from the start of
                // the list, regardless of which VB any individual
                // submesh declares as its primary). A list-anchored
                // search counts stride-8 buffers from index 0 and
                // skipMatches=2 lands on that same UV VB whether the
                // submesh's primary is stride-12 or stride-8 -- the
                // primary itself is just one of the buffers we count
                // through.
                //
                // Gated by "primary is stride-8" so the warm path
                // (stride-12 primary, after-primary search finds VB 3
                // directly) is unchanged; only the previously-broken
                // case retries.
                if (uvVbIdx < 0 &&
                    vIndex >= 0 && vIndex < vbufs.Count &&
                    vbufs[vIndex] != null && vbufs[vIndex].Stride == 8)
                {
                    int targetCount = vbufs[vIndex].Count;
                    int matchesSeen = 0;
                    for (int i = 0; i < vbufs.Count; i++)
                    {
                        // Renamed from 'vb' to 'vbAt' to avoid CS0136 with
                        // the enclosing-scope 'vb' (vbufs[vIndex] alias
                        // declared at the top of this submesh-loop
                        // iteration). C# rejects the inner declaration
                        // even though the inner scope is strictly
                        // narrower; renaming is the cheapest fix and
                        // doesn't change semantics.
                        var vbAt = vbufs[i];
                        if (vbAt == null) continue;
                        if (vbAt.Count != targetCount) continue;
                        if (vbAt.Stride != 8) continue;

                        if (matchesSeen == 2)
                        {
                            uvVbIdx = i;
                            break;
                        }
                        matchesSeen++;
                    }

                    if (uvVbIdx >= 0)
                    {
                        Log($"  [{sm.Name}] UV VB list-anchored fallback: " +
                            $"primary=VB{vIndex} (stride=8), uvVB={uvVbIdx} (was -1).");
                    }
                }
                if (uvVbIdx >= 0)
                {
                    var uvb = vbufs[uvVbIdx];
                    if (File.Exists(uvb.DataPath))
                    {
                        try
                        {
                            byte[] uData = File.ReadAllBytes(uvb.DataPath);
                            long uStart = (long)sm.VOffset * uvb.Stride;
                            long uEnd = uStart + (long)(maxIndex + 1) * uvb.Stride;
                            if (uEnd <= uData.Length)
                            {
                                pendingUvs = DecodeHalf2UvsBlock(
                                    uData, uStart, uvb.Stride, maxIndex);
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

                // -----------------------------------------------------
                // PAIR-1 UV TRANSFORMS (carbon upscale + tile-rate cap
                // + generic U/V offset). Ported from F1ModelLoaderClassic,
                // with a new generic-offset path specific to 2019-2020.
                // -----------------------------------------------------
                if (pendingUvs != null && pendingUvs.Count > 0)
                {
                    // 1) Carbon atlas-UV upscale. Carbon submeshes whose
                    // UVs are still atlas-shaped (max|UV| <= threshold)
                    // get multiplied by CarbonTileMultiplier so the
                    // carbon weave tiles at a readable density. UVs
                    // already authored at >threshold fall through to
                    // the cap below untouched.
                    //
                    // Byte-identical to F1ModelLoaderClassic's carbon
                    // atlas-UV upscale (line ~4501). Same gate, same
                    // single-pass max scan, same threshold check,
                    // same plain (p.X*k, p.Y*k) multiply, same log
                    // line format. Anything that diverges from
                    // Classic here is a regression, not an
                    // improvement -- 2018's carbon look is the
                    // reference and the user has it locked in.
                    if (CarbonTileMultiplier > 1.0 && IsCarbonSlotName(sm.Name))
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

                    // 2) Generic slot U/V offset. Observed 2019 McLaren
                    // safetycell generic submesh UVs at U=[0.002, 0.492]
                    // suggest the live shader additively shifts the
                    // sample position. Knobs default to 0 (no shift);
                    // see GenericUOffset / GenericVOffset docs.
                    if ((GenericUOffset != 0.0 || GenericVOffset != 0.0) &&
                        IsGenericSlotName(sm.Name))
                    {
                        var shifted = new PointCollection(pendingUvs.Count);
                        foreach (var p in pendingUvs)
                            shifted.Add(new System.Windows.Point(
                                p.X + GenericUOffset,
                                p.Y + GenericVOffset));
                        Log($"  [{sm.Name}] generic UV shift: (+{GenericUOffset:F3}, +{GenericVOffset:F3})");
                        pendingUvs = shifted;
                    }

                    // 3) UV tile-rate cap. Catches carbon / detail / etc.
                    // meshes whose UVs are authored at high tile rates
                    // (±50..±300+) and scales them back to a visible
                    // weave density. No-op for UVs already in [0,1].
                    //
                    // F1 2020 ONLY, BODY SLOT ONLY: SKIP this cap.
                    //
                    // Direct decode of the 2020 stock McLaren temp001
                    // vb=3 over every body-submesh's IB-referenced
                    // vertex set shows that of 14237 unique body
                    // vertices, EXACTLY 14234 have pair-1 UVs in
                    // [0,1]. Three modelling-outlier vertices --
                    // v[12089], v[12090], v[12091], all near the
                    // rear-bottom of the car at positions like
                    // (-0.010, -0.063, -0.841) -- carry pair-1
                    // UV=(143.12, -525.00), (142.00, -524.50),
                    // (142.62, -538.50). These three vertices set
                    // max|UV| = 538.50 > MaxUvTileRate=256, which
                    // DOES trip the cap below for any body submesh
                    // whose IB references them (per-submesh testing
                    // identifies the master body submesh at
                    // iofs=0 inum=58095 covering all 14237 body
                    // verts, plus iofs=45474 inum=4197 -- 2 of the
                    // 16 body submeshes in a stock load). The cap
                    // then computes scale = 256/538.50 = 0.4754
                    // and multiplies EVERY UV in those submeshes
                    // by that, collapsing the body's clean [0,1]
                    // paint UVs into the upper-left 47% × 47%
                    // sub-region of mclaren_paint_d.tif.
                    //
                    // The visual result is exactly the symptom the
                    // user reported: the larger body panels (those
                    // 2 submeshes covering the bulk of the shell)
                    // sample a wrong region of paint_d so their
                    // colours come out swapped (blue regions of the
                    // livery painted onto orange-coloured panels and
                    // vice versa), while the smaller body submeshes
                    // (the other 14, with maxIdx < 12089 so they
                    // never reference the 3 outlier verts) sample
                    // the atlas correctly.
                    //
                    // Three outliers should not override 14234 clean
                    // UVs. The fix is to SKIP the tile-rate cap for
                    // 2020 body submeshes -- the 3 outlier verts'
                    // UVs are then sampled with WPF's TileMode.Tile
                    // wrap on those 3 individual vertices, but the
                    // remaining 14234 body UVs render at their
                    // authored [0,1] positions and the body shows
                    // the proper McLaren 2020 livery. The 3 wrap
                    // points are too small to be visually
                    // identifiable on the rendered model (they
                    // span maybe 1 cm of geometry between them
                    // near the underbody) and the legacy loader's
                    // existing TileMode.Tile setting in the brush
                    // pipeline handles them gracefully.
                    //
                    // GATING:
                    //   * _currentGameYear == 2020 -- 2018/2019 keep
                    //     their existing cap behaviour. The 3
                    //     outlier-UV pattern is specific to 2020
                    //     stock McLaren (and possibly other 2020
                    //     liveries with similar modelling artefacts);
                    //     2019 stock packages don't have these
                    //     outliers per past sessions and the cap
                    //     hasn't caused them visible problems.
                    //   * IsBodySlotName(sm.Name) -- only body
                    //     submeshes get the bypass. Carbon /
                    //     generic / detail / etc. all keep the cap
                    //     so genuine tile-rate UVs (±300+ on rare
                    //     carbon weaves, etc.) still get scaled
                    //     down. Skipping the cap globally would
                    //     regress those.
                    //
                    // SIDE EFFECTS:
                    //   * Body's pendingUvs now keeps its clean
                    //     [0,1] values for 14234 verts and its
                    //     wonky values (143, -538 etc.) for the
                    //     3 outliers. WPF samples paint_d at all
                    //     14237 UVs with TileMode.Tile; the 14234
                    //     correct verts produce the proper livery,
                    //     the 3 wrap-around verts produce a tiny
                    //     visual artefact at the underbody too
                    //     small to identify in normal preview.
                    //   * No other slot is affected -- all 35
                    //     detail / 35 carbon_tile_1 / 8 generic /
                    //     etc. submeshes still hit the cap as
                    //     before.
                    //   * 2018/2019 unchanged.
                    bool skipCapForBody2020 =
                        _currentGameYear == 2020 &&
                        IsBodySlotName(sm.Name);

                    if (MaxUvTileRate > 0.0 && !skipCapForBody2020)
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
                                scaled.Add(new System.Windows.Point(
                                    p.X * scale, p.Y * scale));
                            Log($"  [{sm.Name}] UV tile-rate cap: max|UV|={maxAbs:F2} > {MaxUvTileRate:F2}, scaling by {scale:F4}");
                            pendingUvs = scaled;
                        }
                    }
                    else if (skipCapForBody2020)
                    {
                        // Diagnostic: surface that we skipped the cap.
                        // Compute max|UV| for the log so the user can
                        // see which body submeshes had outliers.
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
                            Log($"  [{sm.Name}] (2020) BODY tile-rate cap SKIP: max|UV|={maxAbs:F2} > {MaxUvTileRate:F2} -- not scaling, " +
                                $"keeping raw [0,1] UVs for the 99.98% of body verts that are clean. Three outlier UVs wrap via TileMode.Tile.");
                        }
                    }
                }

                // -----------------------------------------------------
                // PAIR-2 UV DECODE (same VB as pair-1, offset +4 bytes)
                // -----------------------------------------------------
                // No tile-rate cap, no V-flip, no scale/bias -- raw
                // pair-2 UVs straight into TextureCoordinates, matching
                // the 2021+ F1ModelLoader's "use raw pair-2 UVs with NO
                // modifications" stance.
                PointCollection pair2Uvs = null;
                if (uvVbIdx >= 0)
                {
                    var uvb2 = vbufs[uvVbIdx];
                    if (uvb2.Stride >= 8 && File.Exists(uvb2.DataPath))
                    {
                        try
                        {
                            byte[] pData = File.ReadAllBytes(uvb2.DataPath);
                            long pStart = (long)sm.VOffset * uvb2.Stride;
                            long pEnd = pStart + (long)(maxIndex + 1) * uvb2.Stride;
                            if (pEnd <= pData.Length)
                            {
                                // Shift start by +4 bytes so the
                                // decoder's internal [0,2] reads land
                                // on [4,6] of each stride-8 window --
                                // the second packed UV pair.
                                pair2Uvs = DecodeHalf2UvsBlock(
                                    pData, pStart + 4, uvb2.Stride, maxIndex);
                            }
                            else
                            {
                                Log($"  [{sm.Name}] pair-2 UV OOB (vb={uvVbIdx}); skipping overlay UVs.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"  [{sm.Name}] pair-2 UV decode threw ({ex.Message}); skipping overlay UVs.");
                        }
                    }
                }

                // ---------------------------------------------------------
                // F1 2020 ONLY: decal-UV clamp + tile-rate cap.
                // ---------------------------------------------------------
                // F1 2020 ships sponsor / number / carbon-tile decal atlases
                // (mclaren_decal_da.tif and equivalents) authored with
                // occasional out-of-range UVs and tile-rate overshoots --
                // identical authoring shape to F1 2021's decal atlases.
                //
                // F1 2019 does NOT need this transform: its 2019 decal
                // atlases were verified end-to-end in earlier sessions
                // and matched the in-game look without modification, so
                // running clamp/cap on 2019 pair-2 UVs would silently
                // change the body/sponsor compositing the user already
                // has locked in. Strictly gated to 2020.
                //
                // Mirrors F1ModelLoader.cs:1990-2031 (the 2021+ modern
                // loader's ApplyF1_2021_DecalCompatibility block) byte-
                // for-byte:
                //
                //   1. Clamp pair-2 UVs to [0,1] to kill wrap-around
                //      (UVs at u=1.07 sample the texture at u=0.07
                //      after Tile mode wraps -- decal lands shifted).
                //
                //   2. Cap UVs to MaxUvTileRate using the buffer's
                //      post-clamp bbox -- guards against authored
                //      tile-rep overshoot. With clamp applied first
                //      this rarely fires, but kept for parity with
                //      the modern loader's proven 2021 fix.
                //
                // Applied to pair2Uvs ONLY -- pendingUvs (the paint
                // atlas pair-1 UVs) is the body / carbon / detail
                // channel and is left strictly untouched. Body and
                // carbon UV maps therefore round-trip the legacy
                // loader unmodified for both 2019 and 2020; only the
                // decal channel gets the 2020 transform. Matches
                // your "no other UV map should be changed" instruction.
                if (_currentGameYear == 2020 && pair2Uvs != null && pair2Uvs.Count > 0)
                {
                    // (1) Clamp to [0,1].
                    bool clamped = false;
                    double cuMin = double.PositiveInfinity, cuMax = double.NegativeInfinity;
                    double cvMin = double.PositiveInfinity, cvMax = double.NegativeInfinity;
                    var clampedPts = new PointCollection(pair2Uvs.Count);
                    foreach (var p in pair2Uvs)
                    {
                        double u = p.X, v = p.Y;
                        double uClamp = u < 0 ? 0 : (u > 1 ? 1 : u);
                        double vClamp = v < 0 ? 0 : (v > 1 ? 1 : v);
                        if (uClamp != u || vClamp != v) clamped = true;
                        clampedPts.Add(new System.Windows.Point(uClamp, vClamp));
                        if (uClamp < cuMin) cuMin = uClamp;
                        if (uClamp > cuMax) cuMax = uClamp;
                        if (vClamp < cvMin) cvMin = vClamp;
                        if (vClamp > cvMax) cvMax = vClamp;
                    }
                    if (clamped)
                    {
                        Log($"  [{sm.Name}] (2020) pair-2 decal-UV clamp to [0,1]: " +
                            $"bbox now U=[{cuMin:F3},{cuMax:F3}] V=[{cvMin:F3},{cvMax:F3}]");
                        pair2Uvs = clampedPts;
                    }
                    else
                    {
                        // No clamp fired but we still need a populated
                        // bbox for the tile-cap step below. Compute it
                        // once over the unmodified buffer.
                        cuMin = double.PositiveInfinity; cuMax = double.NegativeInfinity;
                        cvMin = double.PositiveInfinity; cvMax = double.NegativeInfinity;
                        foreach (var p in pair2Uvs)
                        {
                            if (p.X < cuMin) cuMin = p.X;
                            if (p.X > cuMax) cuMax = p.X;
                            if (p.Y < cvMin) cvMin = p.Y;
                            if (p.Y > cvMax) cvMax = p.Y;
                        }
                    }

                    // (2) Tile-rate cap, buffer-bbox variant. Same
                    // formula and semantics as the 2021+ modern path:
                    // if the post-clamp bbox has any component whose
                    // |value| > MaxUvTileRate, scale every UV in the
                    // buffer down by MaxUvTileRate / maxAbs.
                    if (MaxUvTileRate > 0.0)
                    {
                        double maxAbs = Math.Max(
                            Math.Max(Math.Abs(cuMin), Math.Abs(cuMax)),
                            Math.Max(Math.Abs(cvMin), Math.Abs(cvMax)));
                        if (maxAbs > MaxUvTileRate)
                        {
                            double scale = MaxUvTileRate / maxAbs;
                            var scaledPts = new PointCollection(pair2Uvs.Count);
                            foreach (var p in pair2Uvs)
                                scaledPts.Add(new System.Windows.Point(p.X * scale, p.Y * scale));
                            Log($"  [{sm.Name}] (2020) pair-2 decal-UV tile-cap: " +
                                $"max|UV|={maxAbs:F2} > {MaxUvTileRate:F2}, scaling by {scale:F4}");
                            pair2Uvs = scaledPts;
                        }
                    }
                }

                // -----------------------------------------------------
                // VB=4 DAMAGE-ATLAS UV DECODE  (TEXCOORD_2 round-trip)
                // -----------------------------------------------------
                // F1 2019/2020 IDFs reserve a stride-4 half2 channel for
                // the per-vertex damage atlas UV (mclaren_main_damage.tif
                // and equivalents), sampled by both vehicle_metallic_
                // paint.nefx2 (body) and vehicle_carbon.nefx2 (carbon)
                // shaders. The bytes we want are two 16-bit half-floats
                // per vertex, identical to the 2018 layout.
                //
                // VB POOL LAYOUTS (verified against stock 2019 mclaren.erp).
                // Each per-part submesh's primary VB selects which pool
                // its companion VBs come from:
                //
                //   Pool A (vb=0..5):   12, 8, 8, 8, 4, 4
                //                       pos, norm, tan, uv, DAMAGE, bone
                //                       -> 2 stride-4 companions
                //   Pool B (vb=6..10):  12, 8, 8, 8, 4
                //                       pos, norm, tan, uv, bone
                //                       -> 1 stride-4 companion (no damage)
                //   Pool C (vb=12..17): 12, 8, 8, 8, 4, 4   (Pool B+damage)
                //                       pos, norm, tan, uv, DAMAGE, bone
                //                       -> 2 stride-4 companions
                //   Pool D (vb=18..19): 12, 4
                //                       pos, bone
                //                       -> 1 stride-4 companion (no damage)
                //   Mirror (vb=11):     stride 40 (separate path)
                //
                // The gate below is "post-position stride-4 companion
                // count >= 2". When that holds (Pool A and Pool C
                // submeshes) the FIRST stride-4 companion is the damage
                // UV channel; the LAST is the bone palette. When only
                // one stride-4 companion exists (Pool B / Pool D) it is
                // the bone palette and damage decode is skipped --
                // decoding the bone palette as half-floats would yield
                // garbage in TEXCOORD_2 (verified: vb=10 is uniform
                // 0x80808080, decoding to subnormal halves; vb=19 is
                // uniform 0x23000000).
                //
                // The decoded PointCollection is registered against the
                // built mesh via F1ModelLoaderClassic.RegisterMeshDamageUvs
                // -- a public static sidecar shared with the Classic
                // loader. F1FbxExporter looks the value up by mesh
                // reference (GetMeshDamageUvs) and emits it as
                // TEXCOORD_2; GltfLodFolderImporter reads TEXCOORD_2
                // into SubMesh.Uvs2; LegacyIdfWriter2019 v11.50+ routes
                // it back into vb=4 verbatim. End-to-end stock round
                // trip on the damage channel is byte-identical for 2019
                // packages, matching the existing 2018 behaviour. No
                // exporter-side change is required.
                //
                // Mirrors the same windowing convention as the normal
                // and pair-1 UV decodes above: start = sm.VOffset *
                // stride, count = maxIndex + 1. If the gate fails, the
                // file is missing, or the per-vertex window goes past
                // EOF, damageUvs stays null and no sidecar entry is
                // registered -- exporter then drops back to its v11.49
                // synth / UV0 fallbacks exactly as before, so the
                // absence-of-data case is a strict superset of prior
                // behaviour.
                PointCollection damageUvs = null;

                // Count post-position stride-4 companions (Count must
                // match the primary VB's vertex count -- same constraint
                // FindCompanionStreamIndex applies). Only when there are
                // 2+ does the FIRST one carry damage-UV bytes; otherwise
                // the single stride-4 entry is the bone palette and we
                // skip damage decoding for this submesh.
                int stride4CompCount = 0;
                if (vIndex >= 0 && vIndex < vbufs.Count && vbufs[vIndex] != null)
                {
                    int target = vbufs[vIndex].Count;
                    for (int j = vIndex + 1; j < vbufs.Count; j++)
                    {
                        var v4 = vbufs[j];
                        if (v4 == null) continue;
                        if (v4.Stride != 4) continue;
                        if (v4.Count != target) continue;
                        stride4CompCount++;
                    }
                }

                int dmgVbIdx = (stride4CompCount >= 2)
                    ? FindCompanionStreamIndex(vbufs, vIndex, expectedStride: 4, skipMatches: 0)
                    : -1;
                if (dmgVbIdx >= 0)
                {
                    var dvb = vbufs[dmgVbIdx];
                    if (File.Exists(dvb.DataPath))
                    {
                        try
                        {
                            byte[] dData = File.ReadAllBytes(dvb.DataPath);
                            long dStart = (long)sm.VOffset * dvb.Stride;
                            long dEnd = dStart + (long)(maxIndex + 1) * dvb.Stride;
                            if (dEnd <= dData.LongLength)
                            {
                                damageUvs = DecodeHalf2UvsBlock(
                                    dData, dStart, dvb.Stride, maxIndex);
                            }
                            else
                            {
                                Log($"  [{sm.Name}] vb=4 damage UV OOB (vb={dmgVbIdx}, stride={dvb.Stride}, " +
                                    $"need={dEnd}, have={dData.LongLength}); skipping damage UVs.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"  [{sm.Name}] vb=4 damage UV decode threw ({ex.Message}); skipping damage UVs.");
                        }
                    }
                }
                else if (stride4CompCount == 1)
                {
                    Log($"  [{sm.Name}] no damage-UV channel for this pool " +
                        $"(only 1 stride-4 companion, treated as bone palette); skipping.");
                }

                // -----------------------------------------------------
                // BASE MESH BUILD
                // -----------------------------------------------------
                var mesh = new MeshGeometry3D
                {
                    Positions = new Point3DCollection(positions),
                    TriangleIndices = new Int32Collection(indices)
                };

                if (pendingNormals != null && pendingNormals.Count == positions.Length)
                    mesh.Normals = pendingNormals;
                if (pendingUvs != null && pendingUvs.Count == positions.Length)
                    mesh.TextureCoordinates = pendingUvs;

                // ---------------------------------------------------------
                // UV-PRESERVATION DIAGNOSTIC
                // ---------------------------------------------------------
                // F1FbxExporter.ConvertToAssimpMesh emits TEXCOORD_0 only
                // when wpf.TextureCoordinates is non-null AND its Count
                // matches the position count (line ~2998 of that file).
                // When either gate fails the glTF primitive ships with
                // NO TEXCOORD_0 accessor at all -- Blender then imports
                // the part with no UV map and the modder has to
                // re-author it from scratch. The legacy loader has
                // several decode paths where pendingUvs can come back
                // null (the primary FindCompanionStreamIndex returning
                // -1 on an unexpected pool layout, the count-match
                // skip filtering out a same-pool stride-8 with a
                // mismatched count, the file-read OOB / IO-throw
                // branches at the decode site) and the existing logs
                // for those failures are spread out and worded
                // inconsistently. This single line surfaces every
                // failure-to-attach in one greppable form so the
                // exact submeshes losing UVs in a given car are easy
                // to enumerate from the loader log.
                //
                // Behaviour-only diagnostic -- doesn't change which UVs
                // get written or what the exporter sees. The real fix
                // for any specific submesh listed here lives in the
                // pendingUvs decode path above; this just makes the
                // gap visible.
                if (mesh.TextureCoordinates == null || mesh.TextureCoordinates.Count == 0)
                {
                    string reason;
                    if (pendingUvs == null)
                        reason = "no UV companion VB found (FindCompanionStreamIndex -> -1)";
                    else if (pendingUvs.Count != positions.Length)
                        reason = $"UV count mismatch (got {pendingUvs.Count}, expected {positions.Length})";
                    else
                        reason = "unknown (pendingUvs populated but mesh.TextureCoordinates not assigned)";
                    Log($"  [NO-UV] '{sm.Name}' (part='{descriptorPartName}'): " +
                        $"will export to glTF without TEXCOORD_0 -- {reason}.");
                }

                // Register vb=4 damage UVs on the mesh so F1FbxExporter
                // surfaces them as glTF TEXCOORD_2. Length-gated against
                // positions so a partial decode doesn't poison the
                // lookup; mirrors the Classic loader's safeguard.
                if (damageUvs != null && damageUvs.Count == positions.Length)
                {
                    F1ModelLoaderClassic.RegisterMeshDamageUvs(mesh, damageUvs);
                    Log($"  [{sm.Name}] vb=4 damage UVs registered ({damageUvs.Count} verts) " +
                        $"-> exporter TEXCOORD_2.");
                }

                // Per-submesh UV bbox log. Lets us diagnose "wrong atlas
                // region" symptoms (where texture resolves but UVs are
                // authored for a different texture or VB) by comparing
                // pair-1 / pair-2 ranges across slots. Known-good body
                // submeshes should have pair-1 UVs roughly in [0,1]x[0,1]
                // (paint atlas); a slot whose pair-1 bbox is obviously
                // off -- e.g. an all-zero range, negative values, or a
                // range that's wildly different from the known-good
                // slots -- is reading from the wrong companion VB.
                if (pendingUvs != null && pendingUvs.Count > 0)
                {
                    double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
                    double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
                    foreach (var p in pendingUvs)
                    {
                        if (p.X < uMin) uMin = p.X;
                        if (p.X > uMax) uMax = p.X;
                        if (p.Y < vMin) vMin = p.Y;
                        if (p.Y > vMax) vMax = p.Y;
                    }
                    Log($"  [{sm.Name}] pair-1 UV bbox: U=[{uMin:F3},{uMax:F3}] V=[{vMin:F3},{vMax:F3}] " +
                        $"(uvVB={uvVbIdx}, {pendingUvs.Count} UVs)");
                }
                if (pair2Uvs != null && pair2Uvs.Count > 0)
                {
                    double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
                    double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
                    foreach (var p in pair2Uvs)
                    {
                        if (p.X < uMin) uMin = p.X;
                        if (p.X > uMax) uMax = p.X;
                        if (p.Y < vMin) vMin = p.Y;
                        if (p.Y > vMax) vMax = p.Y;
                    }
                    Log($"  [{sm.Name}] pair-2 UV bbox: U=[{uMin:F3},{uMax:F3}] V=[{vMin:F3},{vMax:F3}]");
                }

                // Family-tag this submesh.
                //
                // Prefer the descriptor's PART name (e.g. "safetycell"
                // parsed from "mclaren!!!temp000.idf%3Flod=0&safetycell&mesh")
                // because the exporter's parsedNodes tree is keyed on
                // those exact names. The previous fallback (the slot
                // label, via ResolvePartNameForSubmesh's GetSmartPartName)
                // produced families like "body" / "carbon_tile_1" /
                // "wheel" that never matched a real part node, so the
                // exporter dumped that geometry into synthesised
                // top-level nodes named after the slot or fell through
                // to the "unresolved_part_N" fallback. The fallback
                // path is preserved for descriptors whose filename
                // doesn't expose a part name (e.g. the root-union
                // descriptor that has only "&mesh" with no part
                // segment) so behaviour for those is unchanged.
                string partName = !string.IsNullOrEmpty(descriptorPartName)
                    ? descriptorPartName
                    : ResolvePartNameForSubmesh(sm, vbufs, vIndex);
                MeshFamilyRegistry.Register(mesh, partName);

                // Slot + descriptor hint + material EAID. The exporter's
                // GroupPartModelGeometryBySlot reads MeshSubmeshRegistry
                // .GetSubmeshName to bucket per-part submeshes by
                // material; without this registration every legacy-loaded
                // mesh collapsed into a single "default" slot bucket
                // (the symptom: every part has 1 merged primitive
                // instead of separate body/carbon/decal/number primitives,
                // and EVERY mesh prints "had a mesh with no submesh/
                // material-slot name. Fell back to 'default'" warnings).
                // The material EAID lets the exporter emit a glTF
                // Material entry named after the actual game material
                // rather than a generic "default" placeholder. Mirrors
                // F1ModelLoaderClassic.cs ~line 4781 which does the
                // same registration.
                {
                    string slotMaterialEaid = BuildMaterialEaidForSlot(
                        _detectedPackageName ?? "f1_2019_vehicle_package",
                        teamPrefix, sm.Name);
                    MeshSubmeshRegistry.Register(
                        mesh, sm.Name, descriptorPath, slotMaterialEaid);
                }

                // -----------------------------------------------------
                // MATERIAL RESOLUTION
                // -----------------------------------------------------
                // When AutoConfigureTextureCache found a package root
                // AND we have a slot -> material mapping for this
                // submesh, the TextureCache decodes the .tif pair on
                // disk into a frozen ImageBrush and we wrap it in a
                // DiffuseMaterial. Any failure in that chain (no cache,
                // no mapping, missing file, decode error) falls back to
                // the neutral-gray material that was the sole behaviour
                // before Phase 2 -- so the ERP-loaded-without-package-
                // cache path is bit-for-bit identical to the pre-Phase-2
                // build.
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
                // exporter can decode its pixels via TextureCache and
                // embed a PNG data URI in the output. Mirrors
                // F1ModelLoaderClassic.cs ~line 4831; without this the
                // exporter falls back to the gray placeholder material
                // for every submesh on a legacy-loaded car.
                if (resolution.HasValue && !string.IsNullOrEmpty(resolution.Value.TextureEaid))
                {
                    MeshSubmeshRegistry.SetTextureEaid(mesh, resolution.Value.TextureEaid);
                }

                var model = new GeometryModel3D(mesh, mat)
                {
                    BackMaterial = mat
                };

                group.Children.Add(model);

                // -----------------------------------------------------
                // OVERLAY MESH (DECAL SPONSORS + DRIVER NUMBERS)
                // -----------------------------------------------------
                // Paint / carbon / number shaders carry an overlay
                // texture (me_logoMap / me_decalMap) that composites
                // on top of the base colour at draw time. When the
                // MaterialSlotResolver populates resolution.OverlayBrush
                // (rare for 2019 since its shaders don't carry the
                // 2021+ "_metallic_" prefix), use that. Otherwise fall
                // back to TryResolveOverlayBrushLegacy, which reads
                // the material file directly and picks me_logoMap /
                // me_decalMap / me_sponsorMap regardless of shader
                // classification.
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
                    overlayBrush = TryResolveOverlayBrushLegacy(sm.Name, teamPrefix,
                        out string legacyFallbackLog);
                    if (overlayBrush != null)
                    {
                        overlaySourceNote = legacyFallbackLog;
                    }
                    else if (!string.IsNullOrEmpty(legacyFallbackLog))
                    {
                        // Log only when the fallback engaged past the
                        // slot-name gate and then failed. Silent miss
                        // for mirror / wheel / etc. keeps the log
                        // focused on real failures.
                        if (!legacyFallbackLog.Contains("not in body/number/carbon-tile allowlist"))
                            Log($"  [{sm.Name}] overlay-legacy: {legacyFallbackLog}");
                    }
                }

                if (overlayBrush != null &&
                    pair2Uvs != null && pair2Uvs.Count == positions.Length &&
                    pendingNormals != null && pendingNormals.Count == positions.Length)
                {
                    try
                    {
                        // Offset positions along the normal so the
                        // overlay sits on top of the base without
                        // z-fighting. 0.0005 works for paint/carbon;
                        // numbers need a touch more (0.002) because
                        // the number slot uses a nearly co-planar
                        // submesh with the body.
                        double offsetAmount = string.Equals(sm.Name, "number",
                            StringComparison.OrdinalIgnoreCase) ? 0.002 : 0.0005;

                        var offsetPositions = new Point3DCollection(positions.Length);
                        for (int k = 0; k < positions.Length; k++)
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
                            TriangleIndices = new Int32Collection(indices),
                            TextureCoordinates = pair2Uvs,
                        };

                        // Family + slot + overlay material EAID.
                        //
                        // Family matches the base submesh's so the
                        // exporter attaches the overlay under the real
                        // part node rather than synthesising an
                        // "unresolved_part_N" orphan under x0_mesh.
                        //
                        // Slot is the base slot suffixed with "_overlay"
                        // -- F1FbxExporter.GroupPartModelGeometryBySlot
                        // routes "_overlay"-suffixed slots through the
                        // decal sidecar pairing pass so the overlay's
                        // pair-2 UVs end up on the base primitive's
                        // TEXCOORD_1 channel rather than as a separate
                        // standalone primitive. Without the suffix the
                        // overlay would be emitted as its own primitive,
                        // losing the decal compositing hint the exporter
                        // is set up to handle.
                        //
                        // Overlay material EAID comes from the same
                        // PreviewResolution the base mesh's resolution
                        // came from; falls through to null when the
                        // overlay was synthesised via the legacy
                        // me_logoMap fallback (TryResolveOverlayBrushLegacy)
                        // which doesn't carry an EAID.
                        //
                        // Mirrors F1ModelLoaderClassic.cs ~line 4979.
                        if (!string.IsNullOrEmpty(partName))
                            MeshFamilyRegistry.Register(overlayMesh, partName);

                        string overlaySlot = sm.Name + "_overlay";
                        string overlayEaid = (resolution.HasValue && resolution.Value.HasOverlay)
                            ? resolution.Value.OverlayTextureEaid
                            : null;
                        MeshSubmeshRegistry.Register(
                            overlayMesh, overlaySlot, descriptorPath, overlayEaid);

                        var overlayMat = new DiffuseMaterial(overlayBrush);
                        group.Children.Add(new GeometryModel3D(overlayMesh, overlayMat)
                        {
                            BackMaterial = overlayMat,
                        });

                        Log($"  [{sm.Name}] overlay: attached [{overlaySourceNote}] " +
                            $"({pair2Uvs.Count} pair-2 UVs, offset={offsetAmount:F4})");
                    }
                    catch (Exception ex)
                    {
                        Log($"  [{sm.Name}] overlay: build threw ({ex.Message}); skipping overlay");
                    }
                }
                else if (overlayBrush != null)
                {
                    // Brush but missing UVs or normals -- diagnose so
                    // it's traceable without debugger.
                    Log($"  [{sm.Name}] overlay: HAVE brush but skipped " +
                        $"(pair2Uvs={(pair2Uvs != null ? pair2Uvs.Count : 0)}, " +
                        $"positions={positions.Length}, " +
                        $"normals={(pendingNormals != null ? pendingNormals.Count : 0)})");
                }

            }

            Log("--- MESH GEOMETRY BUILD END ---");
            return group;
        }

        /// <summary>
        /// Extract the canonical part name from a descriptor filename of the
        /// form <c>{team}!!!temp000.idf%3Flod=N&amp;{partName}&amp;mesh</c>
        /// (e.g. <c>"mclaren!!!temp000.idf%3Flod=0&amp;safetycell&amp;mesh"</c>
        /// → <c>"safetycell"</c>).
        ///
        /// <para>Ported verbatim from F1ModelLoaderClassic.ExtractPartNameFromDescriptorFilename
        /// (~line 5067). The legacy loader didn't have an equivalent, which
        /// is why every legacy submesh ended up family-tagged with the
        /// MATERIAL SLOT name (e.g. "body", "carbon tile 1") via
        /// ResolvePartNameForSubmesh's GetSmartPartName fallback, instead
        /// of the descriptor's actual part name. The exporter's
        /// parsedNodes tree is keyed on real part names (safetycell,
        /// frontwing_fl_01, ...), so slot-named families never matched
        /// any node and dumped the geometry into synthesised top-level
        /// nodes named after the slot ("body", "carbon_tile_1", ...) or
        /// fell through to the "unresolved_part_N" fallback.</para>
        ///
        /// <para>Returns an empty string on any failure (root descriptor
        /// with no part name segment, malformed URL encoding, missing
        /// "&amp;mesh" tail). Callers treat empty as "use the existing
        /// slot-heuristic fallback" so the function is safe to call on
        /// every legacy descriptor regardless of shape.</para>
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
            // aren't real mesh descriptors.
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

            // USE NEW SMART METHOD:
            return UniversalIdfNaming.GetSmartPartName(label, vbufName);
        }

        // =====================================================================
        // HELPERS
        // =====================================================================

        private static float HalfToFloat(ushort h)
        {
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

        /// <summary>
        /// Build the suffix used to locate temp000/temp001 files.
        /// For names like "mclaren!!!temp000_vb=0_lod=0.idf?0x0000" we want
        /// "_vb=0_lod=0.idf%3F0x0000" so that:
        ///   teamPrefix + "!!!temp000" + suffix
        /// matches the on-disk file.
        /// </summary>
        private static string BuildSuffixFromName(string name, string teamPrefix)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            // Example incoming name for F1 2019:
            // "eaid://f1_2019_vehicle_package/teams/mclaren/idf/mclaren.idf?lod=all&vb=6"
            //
            // We *only* care about the ".idf?lod=...&vb=..." part, because on disk
            // the files are:
            //   mclaren!!!temp000.idf%3Flod=all&vb=6
            //   mclaren!!!temp001.idf%3Flod=all&vb=6

            string baseName = System.IO.Path.GetFileName(name); // "mclaren.idf?lod=all&vb=6"
            int dotIdf = baseName.IndexOf(".idf", StringComparison.OrdinalIgnoreCase);

            string suffix;

            if (dotIdf >= 0)
            {
                // Take everything from ".idf" onwards -> ".idf?lod=all&vb=6"
                suffix = baseName.Substring(dotIdf);
            }
            else
            {
                // Fallback for weird/older names: trim team prefix if it’s at the front
                suffix = baseName;
                if (!string.IsNullOrEmpty(teamPrefix) &&
                    suffix.StartsWith(teamPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    suffix = suffix.Substring(teamPrefix.Length);
                }
            }

            // Match the encoded filenames in the IDF folder
            return suffix.Replace("?", "%3F");
        }

        /// <summary>
        /// Peek strings (paths / EAID names) to auto-detect which game year
        /// this descriptor comes from.
        ///
        /// <para>STRICT MATCHING. Each year requires the exact
        /// <c>f1_YYYY_vehicle_package</c> token, with the classic-DLC
        /// variant <c>f1_YYYY_classic_vehicle_package</c> explicitly
        /// excluded. The previous looser check (any occurrence of
        /// "2019" / "2020" anywhere in the string) caused
        /// false-positive 2020 detection on F1 2019 ERPs because F1
        /// 2019 references the shared 2020 tyre EAID base
        /// <c>eaid://f1_2020_vehicle_package/shared/tyres/idf/</c>;
        /// scanning a 2019 mclaren.erp would hit that string from a
        /// tyre descriptor before the team's own
        /// <c>f1_2019_vehicle_package</c> path was seen, lock to 2020,
        /// and never re-evaluate -- which then caused the 2020-gated
        /// fix blocks (rainbeads skip, decal pair-2 clamp/cap) to
        /// fire on a 2019 car. The user's explicit "make sure these
        /// fixes ONLY apply to 2020" instruction is enforced here at
        /// the detection level so every downstream
        /// <c>_currentGameYear == 2020</c> gate sees a year value
        /// that matches the actual ERP package, regardless of
        /// descriptor walk order.</para>
        ///
        /// <para>UPGRADE PATH. If a string locks the year to 2020
        /// speculatively and a later string carries the strict
        /// <c>f1_2019_vehicle_package</c> token, the year is
        /// upgraded back to 2019 (one-way -- 2019 is never
        /// downgraded). For a true 2020 stock ERP no
        /// <c>f1_2019_vehicle_package</c> string ever appears, so
        /// the upgrade path is dormant; for a 2019 stock ERP the
        /// upgrade fires whenever the team package descriptor is
        /// scanned, correcting any speculative 2020 lock from a
        /// shared-tyre descriptor that was scanned first.</para>
        /// </summary>
        private static void UpdateGameYearFromString(string s)
        {
            if (string.IsNullOrEmpty(s))
                return;

            // Strict per-year tokens. Each requires the
            // f1_YYYY_vehicle_package suffix; the classic-DLC
            // variants are explicitly subtracted out so a string
            // like f1_2019_classic_vehicle_package doesn't satisfy
            // the 2019 stock check (that's a different package
            // family loaded by the same loader, but it shouldn't
            // trigger the 2020-gated stock fixes on its own).
            bool is2019Stock =
                s.IndexOf("f1_2019_vehicle_package", StringComparison.OrdinalIgnoreCase) >= 0 &&
                s.IndexOf("f1_2019_classic_vehicle_package", StringComparison.OrdinalIgnoreCase) < 0;
            bool is2020Stock =
                s.IndexOf("f1_2020_vehicle_package", StringComparison.OrdinalIgnoreCase) >= 0 &&
                s.IndexOf("f1_2020_classic_vehicle_package", StringComparison.OrdinalIgnoreCase) < 0;
            bool is2018Stock =
                s.IndexOf("f1_2018_vehicle_package", StringComparison.OrdinalIgnoreCase) >= 0;

            // Upgrade-or-lock path for 2019. 2019 hits ALWAYS take
            // precedence (over a speculative 2020 lock from an
            // earlier shared-tyre descriptor) and 2019 is never
            // overwritten by anything else.
            if (is2019Stock)
            {
                if (_currentGameYear != 2019)
                {
                    if (_currentGameYear == 0)
                        Log("[VERSION] Detected F1 2019 stock layout from string: " + s);
                    else
                        Log($"[VERSION] Re-evaluated to F1 2019 (was {_currentGameYear}, " +
                            $"speculative lock from earlier shared-package string): " + s);
                    _currentGameYear = 2019;
                }
                return;
            }

            if (_currentGameYear != 0)
                return;

            if (is2020Stock)
            {
                _currentGameYear = 2020;
                Log("[VERSION] Detected F1 2020 stock layout from string: " + s);
            }
            else if (is2018Stock)
            {
                _currentGameYear = 2018;
                Log("[VERSION] Detected F1 2018+ layout from string: " + s);
            }
        }

        private static VertexBufferInfo LoadBufVertex(string folder, string teamPrefix, string suffix)
        {
            string temp000 = Path.Combine(folder, $"{teamPrefix}!!!temp000{suffix}");
            string temp001 = Path.Combine(folder, $"{teamPrefix}!!!temp001{suffix}");

            var info = new VertexBufferInfo
            {
                Suffix = suffix,
                DataPath = temp001,
                DataLength = 0
            };

            if (File.Exists(temp001))
            {
                info.DataLength = new FileInfo(temp001).Length;
            }

            if (!File.Exists(temp000))
            {
                Log($"    LoadBufV ERROR: VB temp000 not found: {temp000}");
                return info;
            }

            byte[] header = File.ReadAllBytes(temp000);
            if (header.Length < 16)
            {
                Log("    LoadBufV ERROR: VB temp000 header too small.");
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
            string temp000 = Path.Combine(folder, $"{teamPrefix}!!!temp000{suffix}");
            string temp001 = Path.Combine(folder, $"{teamPrefix}!!!temp001{suffix}");

            var info = new IndexBufferInfo
            {
                Suffix = suffix,
                DataPath = temp001,
                DataLength = 0
            };

            if (File.Exists(temp001))
            {
                info.DataLength = new FileInfo(temp001).Length;
            }

            if (!File.Exists(temp000))
            {
                Log($"    LoadBufI ERROR: IB temp000 not found: {temp000}");
                return info;
            }

            byte[] header = File.ReadAllBytes(temp000);
            if (header.Length < 16)
            {
                Log("    LoadBufI ERROR: IB temp000 header too small.");
                return info;
            }

            info.Type = BitConverter.ToUInt32(header, 0);
            info.IndexSize = BitConverter.ToInt32(header, 4);
            info.Count = BitConverter.ToInt32(header, 8);
            info.Fvf = BitConverter.ToUInt32(header, 12);

            if (info.IndexSize != 2 && info.IndexSize != 4)
            {
                Log($"    LoadBufI WARNING: Unusual indexSize={info.IndexSize}, forcing 4 for compatibility.");
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
            // POSITION VB DISCRIMINATION (2019 "VBUF=1 Stride=8" FIX)
            // -----------------------------------------------------------
            //
            // The previous filter only rejected Fvf==29 (UV). That isn't
            // enough for F1 2019: a descriptor's stride-8 normal and
            // tangent VBs carry Fvf=0x23, which the filter happily
            // accepts. When the mesh record's vertexFileOffset matches
            // vbOffsets[1] (the normal VB), the exact-match loop picks
            // VB1 -- a stride-8 half-float buffer -- as the "position"
            // source. The position decoder then reads half-floats as
            // unit vectors in [-1, +1] and every vertex collapses into
            // a unit sphere around the origin. The "some parts render
            // correctly and some look like tiny balls on top of the car"
            // symptom is exactly this: mesh[0] records have
            // vertexFileOffset matching VB0 (the real position stream,
            // stride 12) and render fine, while mesh[i>0] generic/number
            // records have vertexFileOffset matching an adjacent offset
            // that happens to land on the normal VB.
            //
            // The fix ports Classic's stride-12 preference: if any
            // stride-12 VB exists in the descriptor (position streams
            // are ALWAYS stride 12 in 2018-2020), only stride-12 VBs
            // qualify as position candidates. Stride 4/8/40 VBs are
            // companion streams (decal UVs, normals, bone weights),
            // never position sources. When no stride-12 VB is present
            // (tiny accessory descriptors, or hypothetical very-old
            // layouts), the filter degrades back to the old "not UV"
            // behaviour so we don't break pre-existing working cases.
            //
            // Also adds base-vertex sufficiency: if the mesh's
            // vertexOffsetIndex is non-zero, the chosen VB must contain
            // at least that many vertices or the VOffset would point
            // outside the buffer and the downstream sanity check would
            // clamp it to 0 -- yanking triangles from the wrong place.
            // Position VB candidate filter. Historically this rejected
            // only Fvf==29 (UV). A brief experiment added a stride-12
            // requirement on the theory that stride-8 buffers are always
            // normals, but that broke F1 2019 "generic" routing: when
            // the descriptor's vertexFileOffset pointed at a stride-8
            // VB (interpreted by Legacy's vbOffsets-match as "this is
            // the position VB for this mesh"), rejecting it and
            // segment-falling to the nearest stride-12 VB landed on
            // VB[0] (main-group position, stride-12), but the mesh
            // actually belonged to the secondary group. Vertices were
            // then sourced from the wrong group → "super distorted".
            //
            // The combination that WORKS for 2019 is: keep the lenient
            // filter here, AND re-apply the generic->vb=6 force-reroute
            // at the caller so safetycell's generic submeshes go to
            // the secondary-group position buffer where they actually
            // belong. See the note at the caller.
            bool IsPosCandidate(int i)
            {
                if (i < 0 || i >= vbufs.Count) return false;
                var vb = vbufs[i];
                if (vb == null) return false;
                if (vb.Fvf == 29) return false; // 29 = UV (2xHALF) in the Noesis plugin
                // Base-vertex sufficiency: when the submesh's
                // vertexOffsetIndex is non-zero, the chosen VB must
                // contain at least that many vertices, otherwise the
                // downstream VOffset clamp zeroes and the submesh's
                // triangles land on the wrong vertices. Harmless in
                // the common vertexOffsetIndex=0 path.
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
                        Log($"[VB-PICK] vertexFileOffset=0x{vertexFileOffset:X}, exact match -> VB {i} (Fvf=0x{vbufs[i].Fvf:X})");
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
                    Log($"[VB-PICK] vertexFileOffset=0x{vertexFileOffset:X}, segment match -> VB {bestIndex} (Fvf=0x{vbufs[bestIndex].Fvf:X})");
                    return bestIndex;
                }
            }

            // 3) Fallback: VB0 if it looks like a position stream
            if (IsPosCandidate(idxVb0))
            {
                Log($"[VB-PICK] Fallback to VB0={idxVb0} (Fvf=0x{vbufs[idxVb0].Fvf:X})");
                return idxVb0;
            }

            // 4) Final fallback: first non-UV VB
            for (int i = 0; i < vbufs.Count; i++)
            {
                if (IsPosCandidate(i))
                {
                    Log($"[VB-PICK] Fallback to first non-UV VB={i} (Fvf=0x{vbufs[i].Fvf:X})");
                    return i;
                }
            }

            // 5) Absolute last resort: VB0 even if it's UV, just so we don't crash
            Log("[VB-PICK] WARNING: no non-UV VB found, forcing VB0");
            return (idxVb0 >= 0 && idxVb0 < vbufs.Count) ? idxVb0 : 0;
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

        /// <summary>
        /// Byte-literal ASCII substring match inside a descriptor payload.
        /// Used by the content-based is2018Plus gate so year detection
        /// doesn't depend on the on-disk path (ERPs extracted to a
        /// hash-named cache lose the year from their path, but every
        /// descriptor's buffer/name references still embed the package
        /// EAID as plain ASCII). Copied from F1ModelLoaderClassic so both
        /// loaders use the identical search predicate.
        /// </summary>
        private static bool DescriptorContainsAscii(byte[] data, string needle)
        {
            if (data == null || data.Length == 0 || string.IsNullOrEmpty(needle))
                return false;
            int nLen = needle.Length;
            if (data.Length < nLen) return false;

            byte[] nb = new byte[nLen];
            for (int i = 0; i < nLen; i++) nb[i] = (byte)needle[i];

            int last = data.Length - nLen;
            for (int i = 0; i <= last; i++)
            {
                bool match = true;
                for (int j = 0; j < nLen; j++)
                {
                    if (data[i + j] != nb[j]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }
    }
}