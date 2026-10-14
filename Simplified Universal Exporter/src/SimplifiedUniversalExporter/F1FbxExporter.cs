using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media.Media3D;
using Assimp;

// Both `Assimp` and `System.Windows.Media.Media3D` define a type named
// Vector3D, so any unqualified use of the bare `Vector3D` identifier
// inside this file is an ambiguity error (CS0104). The existing code
// worked around that by always spelling Assimp's version out as
// `Assimp.Vector3D`. The smooth-normal helpers below need the WPF one
// for all of the accumulator / cross-product / angle math, so we alias
// it here rather than repeating `System.Windows.Media.Media3D.Vector3D`
// at every call site.
using MediaVector3D = System.Windows.Media.Media3D.Vector3D;

namespace SimplifiedUniversalExporter
{
    internal static class F1FbxExporter
    {
        // ----- Helmet exporter FBX patch ---------------------------------
        // The car program writes glTF 2 only. The helmet build needs FBX
        // output so users can edit in Blender / Maya directly. Rather than
        // duplicate the 3500-line export pipeline, the format is toggled via
        // CurrentExportFormatKind: callers set it to Fbx around the call to
        // ExportCarAllLodsFromErp / ExportCarAllLodsFromExtractedIdfFolder
        // and the internal format-id selection, output-extension, and post-
        // process steps branch on this field. Stays Gltf2 by default so any
        // future caller that wants the original behaviour is unaffected.
        internal enum ExportFormatKind { Gltf2, Fbx }
        internal static ExportFormatKind CurrentExportFormatKind = ExportFormatKind.Gltf2;

        // Uniform scale applied to the FBX scene's root node. The car exporter
        // produces output at game-native units which import small into Blender
        // (Blender defaults to 1 unit = 1 m; game units are sub-meter for a
        // helmet). 100 turns game-cm into Blender-cm-equivalent and matches
        // what users expect when importing into a scene scaled in cm. Tunable
        // at call site so we can iterate if the user wants different units.
        internal static float HelmetRootScale = 100.0f;

        // Helmet export option: emit only LOD0 (the highest-detail mesh)
        // and skip LOD1..LOD4. Cuts export time roughly 5x and leaves the
        // user with one .fbx instead of five.
        internal static bool ExportOnlyLod0 = false;

        // Z-fighting fix for decals / sponsor logos / livery overlays. The
        // game's runtime applies a GPU polygon-offset to the decal layer
        // (so it draws just in front of the base body without re-authoring
        // geometry). That bias doesn't survive export, so the decal mesh
        // ends up coincident with the base mesh and the two flicker against
        // each other in any DCC viewport. When enabled, push every decal-
        // mesh vertex along its normal by DecalOffsetMeters at export time
        // so the decal sits a hair in front of the base. Default: on, 0.5 mm
        // which is invisible on a 1.8 m suit but enough to remove flicker.
        internal static bool OffsetDecalsToFixZFighting = true;
        internal static float DecalOffsetMeters = 0.0005f;

        /// <summary>
        /// When non-null, ExportCarAllLodsFromExtractedIdfFolder skips
        /// any part whose descriptor name isn't in this set (case-
        /// insensitive). Lets the UI export only specific parts from
        /// large track IDFs without having to slice the source files.
        /// Set immediately before each call and restore (to null) in a
        /// finally block so unrelated callers aren't affected.
        /// Matched against the same partName the loader reports via
        /// MeshFamilyRegistry, which is what the user sees in the
        /// sidebar tree, so the round-trip is direct.
        /// </summary>
        public static HashSet<string> PartNameFilter = null;

        /// <summary>
        /// Per-slot whitelist keyed by descriptor part name. The
        /// value is the set of material-leaf names allowed inside
        /// that part -- e.g. for the driver suit, the "body"
        /// descriptor part might map to {"boots", "gloves"} to drop
        /// the harness / undershirt / etc. Empty/null = "include
        /// every slot in this part" (default).
        /// Keyed case-insensitively. Matched against the material
        /// EAID's leaf name (sans .material) and the literal slot
        /// name as a fallback, so callers can use whichever the
        /// sidebar built.
        /// </summary>
        public static Dictionary<string, HashSet<string>> SlotNameFilterByPart = null;

        // Helmet IDF render graphs share a body-template skeleton that
        // describes every helmet brand variant + harness + HANS + hands.
        // Loading helmet_bell_hp7 fills only its own subtree; the rest stay
        // empty. The car exporter pads empty leaves with a zero-area keep-
        // alive mesh so Assimp's glTF writer doesn't drop them -- which is
        // exactly what we want for cars (every chassis part matters), but
        // for a single-helmet export those keep-alives turn into visible
        // empty nodes in Blender. Toggle on for helmet exports to prune the
        // empty branches instead.
        internal static bool PruneEmptySubtrees = false;

        private static string ExportFileExtension()
            => CurrentExportFormatKind == ExportFormatKind.Fbx ? ".fbx" : ".gltf";

        // ----- Binary-FBX UnitScaleFactor patch ---------------------------
        // Assimp's FBX writer always emits UnitScaleFactor=1.0 (i.e. 1 unit =
        // 1 cm) regardless of the source scene's units. Our pipeline writes
        // geometry in game-native units that are 1:1 with meters -- the same
        // data exports correctly out of the glTF path with no scale factor
        // applied. Patching UnitScaleFactor to 100.0 declares "1 unit = 100
        // cm = 1 m", which is what Blender / Maya read at import to scale
        // the file into their working units. This lets us drop the empirical
        // 125x root-node scale hack and instead emit geometry at native
        // scale + correct unit metadata.
        //
        // Binary-FBX property layout for a "P:" node entry is:
        //   <name>            (raw ASCII, length-prefixed earlier in the file)
        //   "S\x06\0\0\0double"   (11 bytes: type tag)
        //   "S\x06\0\0\0Number"   (11 bytes: subtype tag)
        //   "S\x00\0\0\0"         ( 5 bytes: empty flag string)
        //   'D' + 8-byte little-endian IEEE-754 double  (the value)
        //
        // The 8-byte value lives at name_offset + name_length + 11 + 11 + 5 + 1.
        // The byte immediately before is 'D' (0x44), which we sanity-check.
        private static void PatchFbxUnitScale(string fbxPath, double newScale)
        {
            if (!File.Exists(fbxPath)) return;

            byte[] bytes = File.ReadAllBytes(fbxPath);

            // Must be binary FBX -- ASCII FBX uses a totally different layout
            // and the helmet/car pipeline only writes binary. Magic is the
            // 23-byte string "Kaydara FBX Binary  \0\x1A\0".
            if (bytes.Length < 32) return;
            string magic = Encoding.ASCII.GetString(bytes, 0, 21);
            if (!magic.StartsWith("Kaydara FBX Binary")) return;

            byte[] newDouble = BitConverter.GetBytes(newScale);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(newDouble);

            PatchOneFbxDoubleProperty(bytes, "UnitScaleFactor", newDouble);
            PatchOneFbxDoubleProperty(bytes, "OriginalUnitScaleFactor", newDouble);

            File.WriteAllBytes(fbxPath, bytes);
        }

        private static void PatchOneFbxDoubleProperty(byte[] bytes, string propName, byte[] newDoubleLE)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(propName);
            int idx = IndexOfBytes(bytes, nameBytes, 0);
            if (idx < 0) return;

            // 11 (double-tag) + 11 (Number-tag) + 5 (empty-flag) + 1 ('D')
            int valueOffset = idx + nameBytes.Length + 11 + 11 + 5 + 1;
            if (valueOffset + 8 > bytes.Length) return;

            // Sanity check: the byte right before the 8-byte payload should be
            // 'D' (0x44). If layout is different (other Assimp version, ascii
            // FBX, etc.) bail rather than corrupt the file.
            if (bytes[valueOffset - 1] != 0x44) return;

            Buffer.BlockCopy(newDoubleLE, 0, bytes, valueOffset, 8);
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle, int start)
        {
            if (needle.Length == 0) return -1;
            int end = haystack.Length - needle.Length;
            for (int i = start; i <= end; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        /// <summary>
        /// Helmet-export utility: merges several already-written FBX files into
        /// a single FBX at <paramref name="outputPath"/>. Used to fold the
        /// attachments-IDF FBX (visor strip / wings / replica decals) into the
        /// base-helmet FBX so the user gets one cohesive file. Re-uses Assimp
        /// for import + export; meshes and materials are appended with index
        /// remapping, and each input scene's root-node children are reparented
        /// onto the merged scene's root.
        ///
        /// The first input becomes the base scene (it owns the original root
        /// transform). Subsequent inputs contribute their root's children and
        /// their meshes / materials.
        /// </summary>
        internal static void MergeFbxFiles(string[] inputs, string outputPath)
        {
            if (inputs == null || inputs.Length == 0)
                throw new ArgumentException("MergeFbxFiles: empty input list.");

            var ctx = new AssimpContext();
            try
            {
                Scene merged = ctx.ImportFile(inputs[0], PostProcessSteps.None)
                    ?? throw new InvalidOperationException("MergeFbxFiles: failed to import " + inputs[0]);

                for (int i = 1; i < inputs.Length; i++)
                {
                    var added = ctx.ImportFile(inputs[i], PostProcessSteps.None);
                    if (added == null) continue;

                    int matOffset = merged.Materials.Count;
                    foreach (var m in added.Materials)
                        merged.Materials.Add(m);

                    int meshOffset = merged.Meshes.Count;
                    foreach (var m in added.Meshes)
                    {
                        m.MaterialIndex += matOffset;
                        merged.Meshes.Add(m);
                    }

                    // Reparent each top-level child of the added scene under
                    // merged scene's root. Children.Add works on AssimpNet
                    // even when RemoveAt / root-transform setter don't.
                    foreach (var topChild in added.RootNode.Children)
                    {
                        RemapMeshIndicesRecursive(topChild, meshOffset);
                        merged.RootNode.Children.Add(topChild);
                    }
                }

                string formatId = CurrentExportFormatKind == ExportFormatKind.Fbx ? "fbx" : "gltf2";
                ctx.ExportFile(merged, outputPath, formatId);

                // Re-stamp UnitScaleFactor=100 on the merged FBX. Assimp's
                // ImportFile/ExportFile round-trip resets the global settings
                // back to its default (UnitScaleFactor=1), undoing the patch
                // we wrote on each individual per-IDF FBX before the merge.
                // Apply once more on the final merged output so Blender /
                // Maya see "1 unit = 1 m" on the file users actually open.
                if (CurrentExportFormatKind == ExportFormatKind.Fbx)
                {
                    try { PatchFbxUnitScale(outputPath, 100.0); }
                    catch { /* best-effort; non-fatal if FBX layout differs */ }
                }
            }
            finally
            {
                ctx.Dispose();
            }
        }

        private static void RemapMeshIndicesRecursive(Node node, int meshOffset)
        {
            if (node == null) return;
            if (node.MeshIndices != null)
            {
                for (int i = 0; i < node.MeshIndices.Count; i++)
                    node.MeshIndices[i] = node.MeshIndices[i] + meshOffset;
            }
            if (node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    RemapMeshIndicesRecursive(node.Children[i], meshOffset);
            }
        }

        internal sealed class NodeInfo
        {
            public int Index;
            public int ParentIndex;
            public string Name = "";

            public float Tx, Ty, Tz;
            public float Qx, Qy, Qz, Qw;
            public float Sx = 1, Sy = 1, Sz = 1;

            // kind=0: transform / emitter / IK helper (no geometry)
            // kind=1: LOD-group marker (x0_mesh...x7_mesh)
            // kind=2: renderable leaf (safetycell, nosecone, wheels, ...)
            // Read from offset 0x70 of the 120-byte node record in ?render.
            public int Kind;
        }

        public static void ExportCarAllLodsFromErp(string erpPath, string outPath, int detectedGameYear)
        {
            if (string.IsNullOrWhiteSpace(erpPath) || !File.Exists(erpPath))
                throw new FileNotFoundException("ERP not found: " + erpPath);

            string extractedFolder = ErpIdfExtractor.ExtractEmbeddedIdfToTempFolder(erpPath);
            if (string.IsNullOrWhiteSpace(extractedFolder) || !Directory.Exists(extractedFolder))
                throw new InvalidOperationException("Failed to extract IDF resources from ERP: " + erpPath);

            ExportCarAllLodsFromExtractedIdfFolder(extractedFolder, outPath, detectedGameYear);
        }

        public static void ExportCarAllLodsFromExtractedIdfFolder(string extractedIdfFolder, string outPath, int detectedGameYear)
        {
            if (string.IsNullOrWhiteSpace(extractedIdfFolder) || !Directory.Exists(extractedIdfFolder))
                throw new DirectoryNotFoundException("IDF folder not found: " + extractedIdfFolder);

            if (string.IsNullOrWhiteSpace(outPath))
                throw new ArgumentException("Output path is empty.");

            // -------------------------------------------------------------
            // SELF-HEAL: redirect from a thin "shared/tyres/idf" folder to
            // the richer "teams/<X>/idf" folder for classic ERPs.
            //
            // The IDF picker in ErpIdfExtractor returns the FIRST IDF
            // resource it sees during ERP-walk-and-extract. For F1 2018
            // classic content (f1_classic_0[123]_vehicle_package),
            // tyres come before the team in TOC order, so the picker
            // hands us "<cache>\shared\tyres\idf" -- which contains 7
            // tyre LODs and nothing else. The downstream code then
            // reads tyre render/model files and produces output named
            // after the tyre asset (e.g. "c_07_tyres_LOD0.gltf") with
            // the car's actual node graph completely missing.
            //
            // The model loader (F1ModelLoaderClassic) already self-
            // heals this same picker mistake at LoadFolder time. The
            // exporter needs the equivalent fix because TryParseNodeGraph,
            // FindMainRenderFile, FindMainModelFile, and ResolveTeamName
            // are called with the ORIGINAL extractedIdfFolder before
            // LoadFolder runs.
            //
            // For stock 2018 cars (no tyres in the team ERP) the picker
            // already returns the correct teams/<X>/idf folder, the
            // redirect finds no richer alternative, and the function
            // is a no-op. Same for non-classic eras: their cache layout
            // has no shared/tyres branch alongside the team idf.
            string redirected = RedirectToRichestTeamIdfFolder(extractedIdfFolder);
            if (!string.Equals(redirected, extractedIdfFolder, StringComparison.OrdinalIgnoreCase))
            {
                // No log sink here -- this runs before per-LOD logs are
                // opened. The model loader's own log will reflect the
                // redirected folder when it runs LoadFolder below.
                extractedIdfFolder = redirected;
            }

            string outDir = ResolveOutputDirectory(outPath, extractedIdfFolder);
            Directory.CreateDirectory(outDir);

            string teamName = ResolveTeamName(extractedIdfFolder, outPath);
            var lods = DetectAvailableLods(extractedIdfFolder);
            if (lods.Count == 0)
                lods.Add(0);

            // Helmet option: drop all LODs except 0.
            if (ExportOnlyLod0)
                lods = lods.Where(l => l == 0).ToList();
            if (lods.Count == 0)
                lods.Add(0);

            var parsedNodes = TryParseNodeGraph(extractedIdfFolder);
            var x0ControlNodes = GetExportedX0ControlNodes(parsedNodes);
            var renderPartRootIndices = TryParseRenderPartRootIndices(extractedIdfFolder);
            var renderPartRootNameToIndex = BuildRenderPartRootNameToIndex(parsedNodes, renderPartRootIndices);

            var ctx = new AssimpContext();
            string formatId = ChooseGltf2FormatId(ctx);

            foreach (int lod in lods)
            {
                // Reset the EAID -> PNG-path cache before each LOD. The cache
                // is keyed only by texture EAID, but the resolved PNG paths
                // get DELETED by EmbedExternalResourcesIntoGltfAndDeleteSidecars
                // at the end of every LOD's export (the PNGs are sidecars
                // staged for the inline-as-data-URI pass). Without this
                // reset, LOD1..LOD7 (and LOD8 on F1 2020) would re-use the
                // cached path from LOD0, EmbedExternalResources would hit
                // File.Exists(...) == false and silently skip, and the
                // resulting LOD1+ .gltf files would ship with image URIs
                // pointing at deleted files -- i.e. textureless in Blender.
                // Clearing here forces a fresh decode + re-encode per LOD,
                // which is cheap (the BC1/BC2/BC3 decode is cached upstream
                // in F1ModelLoaderClassic/Legacy.ActiveTextureCache and the
                // PNG encode is fast).
                ClearTextureEmbedCache();

                string finalOut = Path.Combine(outDir, $"{teamName}_LOD{lod}{ExportFileExtension()}");
                string diagPath = finalOut + ".diag.txt";
                string lodLogPath = finalOut + ".loader.log";

                var warnings = new List<string>();

                var scene = new Scene();
                scene.RootNode = CreateSceneRootNode(parsedNodes, teamName);

                var materialIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
                EnsureMaterial(scene, materialIndexByName, "default");

                var exportNodeByIndex = new Dictionary<int, Node>();
                BuildRenderableNodeTree(scene.RootNode, parsedNodes, exportNodeByIndex);

                // Clear per-mesh registries before every LOD load so the
                // slot / material / family maps don't cross-pollute between
                // iterations. (Registries are keyed by MeshGeometry3D
                // instance so stale entries from LOD N-1 would never
                // actively mis-attribute, but clearing is cheap and keeps
                // the per-LOD diag output clean.)
                F1ModelLoader.ClearRegistries();

                Model3DGroup group = LoadGroupForYearAndLod(
                    extractedIdfFolder, lod, lodLogPath, detectedGameYear);
                var loadedPartModels = EnumerateTopLevelPartModels(group);

                if (loadedPartModels.Count == 0)
                {
                    warnings.Add("LOD " + lod.ToString(CultureInfo.InvariantCulture) +
                        ": loader returned 0 top-level parts. Output file for this LOD will contain hierarchy but no geometry.");
                }

                List<string> orderedPartNames = ResolveLoadedPartNamesFromSuccessfulGroups(
                    loadedPartModels,
                    warnings);

                // Pre-extract every overlay's UV0 into a sidecar dict
                // keyed by (partName, baseSlot). Both the main per-part
                // emit loop below AND the root-union redistribution pass
                // further down consult this sidecar at the moment they
                // emit a base mesh, looking up by the SAME key to
                // attach the overlay's UV0 as TEXCOORD_1 on the base
                // primitive. This bridges the data-flow gap that
                // showed up in the McLaren 2018 export: per-part
                // Model3Ds carry only overlays; their bases live in
                // the root-union pseudopart and arrive at per-part
                // target nodes via redistribution, on a totally
                // separate code path. Pairing within a single Model3D
                // (which an earlier attempt did) couldn't see across
                // that boundary.
                var decalUvSidecar = BuildDecalUvSidecar(loadedPartModels, orderedPartNames);

                // Decal-texture sidecar: same key (partName, baseSlot) as the
                // UV sidecar above, with the OVERLAY mesh's texture EAID as
                // the value. The overlay's MESH is dropped by
                // GroupPartModelGeometryBySlot (decal data rides the base
                // primitive's TEXCOORD_1) so its texture EAID would never
                // reach EnsureMaterial otherwise -- the decal PNG would
                // never land on disk and the user can't wire it up in
                // Blender. We collect every unique overlay texture EAID
                // here and ensure each one decodes to a PNG sidecar.
                var decalTextureEaids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var loadedPart in loadedPartModels)
                {
                    if (loadedPart == null) continue;
                    foreach (var mesh in EnumerateGeometry(loadedPart))
                    {
                        string slot = MeshSubmeshRegistry.GetSubmeshName(mesh);
                        if (string.IsNullOrEmpty(slot)) continue;
                        if (!slot.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase)) continue;
                        string overlayTexEaid = MeshSubmeshRegistry.GetTextureEaid(mesh);
                        if (!string.IsNullOrEmpty(overlayTexEaid))
                            decalTextureEaids.Add(overlayTexEaid);
                    }
                }
                foreach (var eaid in decalTextureEaids)
                {
                    // EnsureTexturePng writes the PNG sidecar into the
                    // textures/ subdirectory next to the FBX and caches
                    // the result so a later EnsureMaterial call binding
                    // the same EAID is a no-op. We don't attach the PNG
                    // to any material here (no base primitive exists for
                    // the overlay by design), but the file lands next to
                    // the FBX's sibling sidecars and the user can drop
                    // it onto the paint material in Blender to recreate
                    // the decal pass.
                    EnsureTexturePng(eaid, Path.Combine(outDir, "textures"), warnings);
                }

                // Load vb=5 (Pool A bone-byte buffer) once for the
                // redistribution pass below. Null = file missing /
                // malformed; redistribution becomes a no-op and the
                // pseudo-part falls through to its top-level emission.
                byte[] poolABoneBytes = LoadPoolABoneBytes(extractedIdfFolder, warnings);

                // Build the carbon-tile-1 dedup set DYNAMICALLY from the
                // actual loaded parts.
                //
                // The redistribute pass below skips triangles whose
                // dominant vb=5 bone byte resolves to a per-part that
                // already emits its own carbon geometry natively --
                // otherwise the same triangles would appear twice
                // (once via the per-part, once via the root-union
                // pseudo-part), causing visible Z-fighting on the
                // duplicated panels.
                //
                // The historical implementation used a static set
                // (POOL_A_NATIVE_CARBON_BONES) hand-tuned by inspecting
                // a stock 2018 McLaren loader log. That works for the
                // car it was tuned against, but for c_mclaren_98 (and
                // every other classic / non-stock 2018 chassis) the
                // per-part shader bindings differ -- some parts that
                // emit native carbon-tile-1 in stock 2018 don't emit
                // anything carbon-shaped on the classic, so the
                // hardcoded skip drops geometry that has no duplicate
                // anywhere else. Visible to the user as "less than 1%
                // distortion" -- the missing carbon panels.
                //
                // Computing the set from the LIVE per-parts of the
                // current load fixes both directions: stock 2018
                // recovers exactly the same set the static list had
                // (every part that natively emits carbon is detected
                // here too), and classic content gets a set that
                // matches its own shader bindings.
                //
                // Falls back to the static set if the dynamic
                // detection comes up empty (defensive: a future
                // refactor of slot naming shouldn't be able to silently
                // turn the dedup off).
                var dynamicNativeCarbonBones = BuildDynamicNativeCarbonBoneSet(
                    loadedPartModels, orderedPartNames, warnings);
                if (dynamicNativeCarbonBones.Count == 0)
                {
                    warnings.Add("RootUnionRedistribution: dynamic carbon-bone " +
                        "detection found no parts; falling back to static set " +
                        "(" + POOL_A_NATIVE_CARBON_BONES.Count.ToString(CultureInfo.InvariantCulture) +
                        " bones).");
                    dynamicNativeCarbonBones = new HashSet<byte>(POOL_A_NATIVE_CARBON_BONES);
                }
                else
                {
                    warnings.Add("RootUnionRedistribution: dynamic carbon-bone set " +
                        "size=" + dynamicNativeCarbonBones.Count.ToString(CultureInfo.InvariantCulture) +
                        " (static reference set size=" +
                        POOL_A_NATIVE_CARBON_BONES.Count.ToString(CultureInfo.InvariantCulture) +
                        ").");
                }

                // Track pseudo-part loader-indices so the redistribute
                // pass can iterate them after the main emit loop.
                var pseudoPartIndices = new List<int>();

                // ResolveLoadedPartNamesFromSuccessfulGroups now always
                // returns one name per loaded part model (placeholder
                // fallback for unresolvable ones), so the earlier hard
                // throw on count mismatch is gone. Geometry for parts
                // whose names can't be mapped to the ?render graph still
                // gets exported -- just attached under x0_mesh instead of
                // being dropped on the floor along with the whole LOD.

                // Walk the loaded part models and for each submesh emit one
                // Assimp mesh attached to the right target node. Two submeshes
                // that resolve to the same target node AND use the same slot
                // name deliberately share a material INDEX (via EnsureMaterial's
                // de-dupe by name) so that when Assimp's glTF2 writer serialises
                // the scene it can group same-material primitives under one
                // node's mesh -- that's the "one object with material slots"
                // structure the modder wants.
                //
                // The previous iteration of this code tried to merge the
                // MeshGeometry3D instances per (node, slot) into a single
                // fused mesh before handing to Assimp. It produced an
                // AccessViolationException deep in the native glTF2
                // exporter on at least one 2018 McLaren cache -- a fused
                // mesh with ~100k verts across safetycell's body + root-
                // union's body + overlays was apparently tripping
                // something in the C++ writer. Reverted to the
                // per-submesh emission below; the structural
                // consolidation will have to happen in a later switch
                // to a pure-managed SharpGLTF output path rather than in
                // pre-merge fed to Assimp.
                for (int i = 0; i < loadedPartModels.Count; i++)
                {
                    string partName = orderedPartNames[i];

                    // Overlay-only models produced by the Classic loader
                    // (0.5mm decal-pass duplicates) carry no geometry the
                    // exporter emits -- GroupPartModelGeometryBySlot filters
                    // them -- and we don't want a placeholder part either.
                    // Skip before target-node resolution so they produce
                    // neither a warning nor an empty x0_mesh orphan.
                    if (string.Equals(partName, SKIP_OVERLAY_ONLY_MODEL, StringComparison.Ordinal))
                        continue;

                    // Skip the literal &part&mesh descriptor if any team's
                    // IDF contains one. It's the unanimated / unassembled
                    // form of the car as a single object and the modding
                    // workflow doesn't want it shipped alongside the real
                    // per-parts. (The unnamed &mesh root-union descriptor
                    // is handled separately by the redistribution pass
                    // below.)
                    if (string.Equals(partName, "part", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Per-part user filter. Empty/null filter == "export
                    // everything" (default). Set by the MainWindow before
                    // each ExportCarAllLodsFromExtractedIdfFolder call so
                    // track ERPs with dozens of IDF parts can ship only
                    // the ones the user ticked in the sidebar tree.
                    //
                    // A part that's missing from PartNameFilter still
                    // survives if it carries a SlotNameFilterByPart
                    // entry: the user ticked a specific slot inside it
                    // (e.g. "boots" inside the otherwise-excluded
                    // "body" descriptor) and we need to descend into
                    // the slot loop to filter there.
                    bool partInExplicitFilter =
                        PartNameFilter != null && PartNameFilter.Contains(partName);
                    bool partHasSlotWhitelist =
                        SlotNameFilterByPart != null &&
                        SlotNameFilterByPart.TryGetValue(partName, out var probeWhitelist) &&
                        probeWhitelist != null && probeWhitelist.Count > 0;
                    if (PartNameFilter != null && PartNameFilter.Count > 0 &&
                        !partInExplicitFilter && !partHasSlotWhitelist)
                    {
                        warnings.Add($"Skipped part '{partName}' (excluded by user filter)");
                        continue;
                    }

                    // Root-union pseudo-parts (partName == material name)
                    // are handled by the bone-binding redistribution pass
                    // below ONLY if vb=5 loaded successfully. Without it
                    // we have no authoritative way to slice the geometry,
                    // so we let the pseudo-part fall through to its
                    // legacy top-level emission rather than silently
                    // dropping the geometry.
                    //
                    // No triangle-count threshold here: the only Model3Ds
                    // whose partName resolves to a stock-material name
                    // are the unnamed root-union descriptors (the loader
                    // registers MeshFamilyRegistry for every named
                    // per-part, so left_paddle / mix_dial / steering_wheel
                    // / etc. now resolve to their real family name and
                    // never reach the slot-name fallback). The earlier
                    // 5 000-triangle floor was therefore excluding LOD4..
                    // LOD7 root-union submeshes (3 129 / 894 / 517 / 98
                    // triangles respectively) from redistribution and
                    // letting them surface as top-level orphan nodes
                    // named after their slot ("carbon tile 1" /
                    // "body" / "body_decal").
                    if (poolABoneBytes != null && IsRootUnionPseudoPart(partName)
                        && IsModelFromRootUnionDescriptor(loadedPartModels[i]))
                    {
                        pseudoPartIndices.Add(i);
                        continue;
                    }

                    Model3D partModel = loadedPartModels[i];

                    Node targetNode = ResolveTargetExportNodeForPart(
                        partName,
                        renderPartRootNameToIndex,
                        exportNodeByIndex,
                        scene.RootNode,
                        parsedNodes,
                        warnings);

                    var slotGroups = GroupPartModelGeometryBySlot(partModel, partName, warnings);

                    // Look up the per-part slot whitelist once -- only
                    // applies when the MainWindow's sidebar narrowed
                    // the selection inside this descriptor part. Null
                    // means "no per-slot filtering for this part" so
                    // every slot below ships unchanged.
                    HashSet<string> slotWhitelist = null;
                    if (SlotNameFilterByPart != null)
                        SlotNameFilterByPart.TryGetValue(partName, out slotWhitelist);

                    foreach (var slotEntry in slotGroups)
                    {
                        string slotName = slotEntry.Key;
                        List<MeshGeometry3D> slotMeshes = slotEntry.Value;

                        // Per-slot filter. Check the EAID leaf of any
                        // mesh in this slot first (matches what the
                        // sidebar shows), then the literal slot name
                        // as a backup. If neither hits the whitelist
                        // we skip the entire slot.
                        if (slotWhitelist != null && slotWhitelist.Count > 0)
                        {
                            string matLeaf = null;
                            foreach (var probe in slotMeshes)
                            {
                                if (probe == null) continue;
                                string eaid = MeshSubmeshRegistry.GetMaterialEaid(probe);
                                matLeaf = ExtractMaterialLeafForFilter(eaid);
                                if (!string.IsNullOrWhiteSpace(matLeaf)) break;
                            }
                            bool matMatch = matLeaf != null && slotWhitelist.Contains(matLeaf);
                            bool slotMatch = !string.IsNullOrWhiteSpace(slotName) &&
                                             slotWhitelist.Contains(slotName);
                            if (!matMatch && !slotMatch)
                            {
                                warnings.Add($"Skipped slot '{partName}/{slotName}' " +
                                             $"(material '{matLeaf ?? "?"}' excluded by user filter)");
                                continue;
                            }
                        }

                        for (int sm = 0; sm < slotMeshes.Count; sm++)
                        {
                            var src = slotMeshes[sm];
                            if (src == null)
                                continue;

                            // Defensive pre-Assimp validation. Zero-vertex or
                            // zero-index meshes would produce an aMesh with
                            // no faces and we'd skip below anyway; catching
                            // them here keeps the diag output tidy.
                            if (src.Positions == null || src.Positions.Count == 0)
                                continue;
                            if (src.TriangleIndices == null || src.TriangleIndices.Count < 3)
                                continue;

                            // Name each Assimp.Mesh with a unique
                            // "<part>_<slot>" identifier so Assimp's glTF2
                            // writer emits a distinct child node per
                            // submesh under the parent part node. Without
                            // this every submesh attached to the same
                            // part is named after that part (ConvertToAssimpMesh
                            // used to receive `partName` as meshName), and
                            // Blender's outliner displays several
                            // indistinguishable "<part>" entries that
                            // the user perceives as merged. Using
                            // "<part>_<slot>" -- slot-name stripped of
                            // whitespace so "carbon tile 1" becomes
                            // "carbon_tile_1" -- keeps every submesh
                            // visually and structurally separate in the
                            // final glTF, without changing node parenting
                            // or material assignment.
                            string meshNameForPrim = BuildPrimitiveMeshName(partName, slotName);
                            // Look up paired decal UV from the sidecar
                            // keyed by (partName, slotName). When found
                            // and the vertex count matches, ConvertToAssimpMesh
                            // emits it as TEXCOORD_1 on the base primitive.
                            // null = no overlay authored for this base
                            // (or the overlay's vertex count drifted from
                            // the base's, which is treated as no-merge).
                            var decalUv = TryGetDecalUv(decalUvSidecar, partName, slotName, src.Positions.Count);
                            var damageUv = F1ModelLoaderClassic.GetMeshDamageUvs(src);
                            if (damageUv != null && damageUv.Count != src.Positions.Count) damageUv = null;
                            var aMesh = ConvertToAssimpMesh(src, meshNameForPrim, decalUv, damageUv);
                            if (aMesh.Vertices.Count == 0 || aMesh.Faces.Count == 0)
                                continue;

                            // EnsureMaterial is keyed by display-name + slot,
                            // so multiple submeshes with the same derived
                            // material name and slot reuse the same material
                            // INDEX. This is what lets Assimp's glTF2 writer
                            // group them under shared primitives per node.
                            string materialEaid = MeshSubmeshRegistry.GetMaterialEaid(src);
                            string textureEaid = MeshSubmeshRegistry.GetTextureEaid(src);
                            string materialDisplayName = DeriveMaterialDisplayName(materialEaid, slotName);
                            aMesh.MaterialIndex = EnsureMaterial(
                                scene, materialIndexByName, materialDisplayName, slotName, materialEaid,
                                textureEaid, Path.Combine(outDir, "textures"), warnings);
                            ValidateAssimpMeshOrThrow(aMesh, aMesh.Name);

                            int meshIndex = scene.Meshes.Count;
                            scene.Meshes.Add(aMesh);
                            targetNode.MeshIndices.Add(meshIndex);
                        }
                    }
                }

                // ----------------------------------------------------------
                // Hierarchy-preservation pass.
                //
                // The ?render / ?model files lay out ~135 nodes for a 2018
                // McLaren-tier car. Around 60 of them are EMPTY leaves:
                //   - every x0_*_emitter / x0_*_fixed_* attachment
                //   - every *_water simplified-geometry variant that the
                //     previewer ignores
                //   - driver / IK / tether dummies (d_*, ik_*)
                //   - the xN_mesh LOD-grouping markers
                // These nodes carry no MeshIndices. Assimp's glTF2 writer
                // treats a node with no mesh AND no children as redundant
                // and DROPS it at serialisation time, which is what makes
                // the exported .gltf look like the hierarchy was flattened
                // (only the ~69 mesh-bearing nodes survive, all parented
                // directly to the team-name root).
                //
                // The workaround is to give every empty leaf a trivial
                // keep-alive mesh -- a single zero-area triangle whose
                // three vertices all sit at the node's local origin.
                // The triangle renders as nothing in every DCC (no pixels
                // to shade) but forces Assimp to emit the node, which
                // lets modders see and snap to all 135 attachment points.
                //
                // We share ONE keep-alive mesh across every empty leaf so
                // the .gltf grows by exactly one MeshPrimitive regardless
                // of how many leaves need preserving.
                // ----------------------------------------------------------
                // ----------------------------------------------------------
                // Bone-binding pseudo-part redistribution.
                //
                // For each queued pseudo-part, slice its triangles by the
                // dominant bone byte of their three vertices in vb=5 and
                // graft each slice onto the per-part Node owning that
                // bone. Triangles whose bone is already covered by a
                // per-part native carbon-tile-1 slot are skipped to
                // avoid Z-fighting; they're visible via the per-part's
                // own emission. The remaining slices populate carbon-
                // tile-1 material slots on safetycell, nosecone, hal,
                // driver_surroun, frontspoiler_fm, frontwing_*_02, etc.
                // -- parts whose carbon-tile-1 geometry only exists in
                // the root-union descriptor (~80% of all carbon-tile-1
                // indices in mclaren 2018).
                // ----------------------------------------------------------
                int redistributedTriangleCount = 0;
                int redistributedDuplicatesSkipped = 0;
                int redistributedPartCount = 0;
                if (pseudoPartIndices.Count > 0 && poolABoneBytes != null)
                {
                    foreach (int pi in pseudoPartIndices)
                    {
                        if (pi < 0 || pi >= loadedPartModels.Count) continue;
                        int placed = RedistributeRootUnionPseudoPartByBone(
                            scene,
                            loadedPartModels[pi],
                            orderedPartNames[pi],
                            poolABoneBytes,
                            renderPartRootNameToIndex,
                            exportNodeByIndex,
                            parsedNodes,
                            materialIndexByName,
                            outDir,
                            warnings,
                            decalUvSidecar,
                            dynamicNativeCarbonBones,
                            out int dupSkipped);
                        redistributedTriangleCount += placed;
                        redistributedDuplicatesSkipped += dupSkipped;
                        if (placed > 0) redistributedPartCount++;
                    }
                }
                warnings.Add(
                    "RootUnionRedistribution: pseudoparts=" +
                    redistributedPartCount.ToString(CultureInfo.InvariantCulture) +
                    " placed_triangles=" +
                    redistributedTriangleCount.ToString(CultureInfo.InvariantCulture) +
                    " duplicates_skipped=" +
                    redistributedDuplicatesSkipped.ToString(CultureInfo.InvariantCulture));

                int defaultMatIdx = EnsureMaterial(scene, materialIndexByName, "default");

                if (PruneEmptySubtrees)
                {
                    // Helmet export: drop nodes that don't contribute geometry
                    // instead of padding them with keep-alive meshes. The
                    // body-template render graph carries placeholder bones
                    // for every helmet variant + harness + HANS + hands;
                    // anything outside the loaded asset's subtree is empty.
                    int beforeRoot = scene.RootNode?.ChildCount ?? 0;
                    int deepPruned = PruneEmptyBranches(scene.RootNode);

                    // AssimpNet quirk: Children.RemoveAt on scene.RootNode is
                    // a no-op (the collection accepts the call but the FBX
                    // writer still sees the original list). Children.Add does
                    // work, though -- so rebuild scene.RootNode from scratch
                    // and re-add only the children that contribute geometry.
                    var keepers = new List<Node>();
                    foreach (var child in scene.RootNode.Children)
                    {
                        if (HasAnyMeshInSubtree(child)) keepers.Add(child);
                    }
                    var newRoot = new Node(scene.RootNode.Name) { Transform = scene.RootNode.Transform };
                    foreach (var k in keepers) newRoot.Children.Add(k);
                    foreach (var meshIdx in scene.RootNode.MeshIndices) newRoot.MeshIndices.Add(meshIdx);
                    scene.RootNode = newRoot;

                    int afterRoot = scene.RootNode.ChildCount;
                    warnings.Add($"PrunedEmptyBranches: deepDropped={deepPruned} rootBefore={beforeRoot} rootAfter={afterRoot}");
                }
                else
                {
                    int keepAliveMeshIndex = EnsureKeepAliveMesh(scene, defaultMatIdx);
                    int keepAliveAttached = AttachKeepAliveToEmptyLeaves(scene.RootNode, keepAliveMeshIndex);
                    warnings.Add("KeepAliveEmptyLeavesPreserved=" + keepAliveAttached.ToString(CultureInfo.InvariantCulture));
                }

                // Apply uniform scale so helmets land at a sensible size in
                // Blender / Maya. No-op when scale == 1.
                //
                // We can't set this on scene.RootNode.Transform -- AssimpNet's
                // root-node Transform setter is the same kind of silent no-op
                // we hit with Children.RemoveAt -- so apply it to each direct
                // child of the (rebuilt) root instead. After the prune pass
                // there's only one direct child (the helmet asset's node) so
                // this scales the whole exported model uniformly.
                if (Math.Abs(HelmetRootScale - 1.0f) > 1e-6f && PruneEmptySubtrees)
                {
                    var s = HelmetRootScale;
                    foreach (var child in scene.RootNode.Children)
                    {
                        var t = child.Transform;
                        t.A1 *= s; t.A2 *= s; t.A3 *= s; t.A4 *= s;
                        t.B1 *= s; t.B2 *= s; t.B3 *= s; t.B4 *= s;
                        t.C1 *= s; t.C2 *= s; t.C3 *= s; t.C4 *= s;
                        child.Transform = t;
                    }
                    warnings.Add("HelmetRootScaleApplied=" + s.ToString("0.###", CultureInfo.InvariantCulture));
                }

                WriteDiagnostics(
                    diagPath,
                    extractedIdfFolder,
                    finalOut,
                    detectedGameYear,
                    teamName,
                    lod,
                    parsedNodes,
                    x0ControlNodes,
                    scene,
                    formatId,
                    warnings);

                if (scene.Meshes.Count == 0)
                    throw new InvalidOperationException("Export aborted: scene contains 0 meshes. See: " + diagPath);

                // Assimp's gltf2 exporter always writes two files: the
                // .gltf JSON and a .bin sidecar containing the raw vertex
                // / index buffer bytes. The user has been explicit that
                // NO .bin file should ever appear at the output location
                // -- large LODs can be tens of MB, they clutter the
                // folder, and glTF viewers happily accept the self-
                // contained form where the buffer is inlined as a
                // base64 data URI instead.
                //
                // Strategy: write Assimp's output into a fresh per-export
                // temp directory (unique GUID so concurrent exports can't
                // collide), then post-process the .gltf to inline the
                // .bin as a data URI (via
                // EmbedExternalResourcesIntoGltfAndDeleteSidecars, which
                // also inlines any external texture images while it's at
                // it), then move ONLY the now-self-contained .gltf out
                // to the real destination. The temp directory and its
                // .bin get torn down in the finally block so a throw
                // halfway through export can't leave a stale .bin
                // orphaned anywhere on disk -- including the user's
                // output folder, where it would otherwise be visible
                // for the entire duration of the Assimp call.
                string tempExportDir = Path.Combine(
                    Path.GetTempPath(),
                    "export-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempExportDir);
                string tempGltfPath = Path.Combine(tempExportDir, Path.GetFileName(finalOut));

                try
                {
                    ValidateSceneOrThrow(scene);

                    if (CurrentExportFormatKind == ExportFormatKind.Fbx)
                    {
                        // FBX self-contains its geometry and references
                        // textures by relative path; no .bin sidecar is
                        // produced and the EmbedExternalResources pass is
                        // glTF-specific. Write straight to the final path
                        // and leave the texture PNG sidecars next to it so
                        // Blender / Maya resolve them on import.
                        if (File.Exists(finalOut))
                            File.Delete(finalOut);
                        ctx.ExportFile(scene, finalOut, formatId);

                        // Assimp's binary-FBX writer hardcodes UnitScaleFactor=1
                        // (1 unit = 1 cm). Our geometry is in game-native units
                        // that match meters -- the same data exports correctly
                        // out of the glTF path without any scale factor. Patch
                        // the FBX global settings post-write so Blender / Maya
                        // import at the correct scale natively, without us
                        // having to inflate the geometry by 100x at the root.
                        try { PatchFbxUnitScale(finalOut, 100.0); }
                        catch { /* best-effort; non-fatal if FBX layout differs */ }

                        // Write the material manifest alongside the FBX.
                        // The Blender post-import script reads this to map
                        // material names -> texture filenames without
                        // string-matching, fixing cases like the suit
                        // "gloves" material binding to a "<team>_glove_d.png"
                        // texture (plural vs singular).
                        WriteMaterialManifest(finalOut, warnings);
                    }
                    else
                    {
                        ctx.ExportFile(scene, tempGltfPath, formatId);

                        if (!File.Exists(tempGltfPath) || new FileInfo(tempGltfPath).Length == 0)
                            throw new InvalidOperationException("Export returned without an output file. See: " + diagPath);

                        // Inline the .bin (and any external images) into the
                        // .gltf as data URIs, then delete the now-orphaned
                        // sidecars inside the temp dir. After this call, the
                        // temp .gltf is fully self-contained.
                        EmbedExternalResourcesIntoGltfAndDeleteSidecars(tempGltfPath);

                        // Promote the self-contained .gltf to the user's
                        // output folder. File.Move in .NET 8 throws if the
                        // destination exists, so pre-delete any stale output
                        // from a previous export to the same filename.
                        if (File.Exists(finalOut))
                            File.Delete(finalOut);
                        File.Move(tempGltfPath, finalOut);
                    }
                }
                catch (AccessViolationException ex)
                {
                    try
                    {
                        File.AppendAllText(
                            diagPath,
                            Environment.NewLine +
                            "ACCESS VIOLATION DURING EXPORT" + Environment.NewLine +
                            ex.ToString() + Environment.NewLine,
                            new UTF8Encoding(false));
                    }
                    catch
                    {
                    }

                    throw;
                }
                finally
                {
                    // Wipe the entire temp directory. Best-effort; if the
                    // OS has a handle on one of the files (rare) we let
                    // the runtime's temp-cleanup pick it up later.
                    try
                    {
                        if (Directory.Exists(tempExportDir))
                            Directory.Delete(tempExportDir, recursive: true);
                    }
                    catch
                    {
                    }
                }

                if (!File.Exists(finalOut) || new FileInfo(finalOut).Length == 0)
                    throw new InvalidOperationException("Export returned without an output file. See: " + diagPath);
            }
        }

        /// <summary>
        /// Per-LOD loader dispatch. Mirrors the year-branched preview
        /// path in MainWindow so the exporter pulls geometry from the
        /// SAME loader that authored the preview the user is looking at:
        ///
        ///   2016-2018  -> F1ModelLoaderClassic   (NEFX1, hardcoded slot table +
        ///                                         EmbContext-driven material EAIDs)
        ///   2019-2020  -> F1ModelLoaderLegacy    (NEFX2 intermediate,
        ///                                         half-float positions on some VBs)
        ///   2021+      -> F1ModelLoader          (NEFX2 full-float, explicit LOD)
        ///
        /// Previously the exporter called F1ModelLoader.LoadFolderExplicitLod
        /// unconditionally, which is the 2021+ loader — handing it a 2018
        /// cache produces zero top-level parts because the descriptor
        /// layout, vertex-stream stride convention, and texture-EAID
        /// mapping all differ between those loader families. That's why
        /// 2018 exports were arriving with the hierarchy but no car
        /// geometry inside it.
        ///
        /// Classic now has its own <see cref="F1ModelLoaderClassic.LoadFolderExplicitLod"/>
        /// so we can iterate every LOD the cache contains. Legacy doesn't
        /// yet, so 2019/2020 exports fall back to its LoadFolder (which
        /// picks the best available detail level) and emit the same
        /// geometry for every LOD filename until that loader gets the
        /// same refactor.
        /// </summary>
        private static Model3DGroup LoadGroupForYearAndLod(string folder, int lod, string logPath, int year)
        {
            if (year >= 2016 && year <= 2018)
                return F1ModelLoaderClassic.LoadFolderExplicitLod(folder, lod, logPath);

            if (year == 2019 || year == 2020)
                return F1ModelLoaderLegacy.LoadFolderExplicitLod(folder, lod, logPath);

            // 2021+ and unknown/zero years use the modern loader.
            return F1ModelLoader.LoadFolderExplicitLod(folder, lod, logPath);
        }

        private static string ResolveOutputDirectory(string outPath, string fallbackFolder)
        {
            string ext = Path.GetExtension(outPath);
            if (!string.IsNullOrWhiteSpace(ext))
            {
                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }

            if (Directory.Exists(outPath))
                return outPath;

            if (!string.IsNullOrWhiteSpace(ext))
                return Path.GetDirectoryName(outPath) ?? fallbackFolder;

            return outPath;
        }

        /// <summary>
        /// Walk up at most six directory levels from
        /// <paramref name="folder"/> to find the cache root (the nearest
        /// ancestor with a "teams" subdirectory), then enumerate every
        /// teams/&lt;X&gt;/idf folder under it and return the one with
        /// the most "*temp000*mesh*" descriptors.
        ///
        /// <para>If the supplied folder is already the richest one (or
        /// no "teams" ancestor exists at all), returns the input
        /// unchanged. So this is a no-op when the picker already chose
        /// correctly -- only fires for the broken classic-ERP case
        /// where the picker handed us "shared/tyres/idf" and the real
        /// car geometry sits in "teams/&lt;X&gt;/idf" elsewhere in the
        /// same cache.</para>
        ///
        /// <para>Mirrors F1ModelLoaderClassic's redirect of the same
        /// name. Both layers run because the loader and the exporter
        /// each receive the picker's output independently; without
        /// the exporter-side redirect, FindMainRenderFile and
        /// ResolveTeamName get the wrong folder and produce
        /// tyre-named output files even when the loader correctly
        /// rendered the car.</para>
        /// </summary>
        private static string RedirectToRichestTeamIdfFolder(string folder)
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

            // Locate the cache root: nearest ancestor that has a "teams" subfolder.
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

            // Enumerate teams/<X>/idf and pick the richest by descriptor count.
            string teamsRoot = Path.Combine(cacheRoot, "teams");
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
            catch { /* fall through with whatever we found so far */ }

            return bestFolder;
        }

        private static string ResolveTeamName(string extractedIdfFolder, string outPath)
        {
            string modelFile = FindMainModelFile(extractedIdfFolder);
            string renderFile = FindMainRenderFile(extractedIdfFolder);
            string source = !string.IsNullOrWhiteSpace(modelFile) ? modelFile : renderFile;

            if (!string.IsNullOrWhiteSpace(source))
            {
                string fileName = Path.GetFileName(source);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    int bang = fileName.IndexOf("!!!", StringComparison.Ordinal);
                    if (bang > 0)
                    {
                        string raw = fileName.Substring(0, bang);
                        raw = Uri.UnescapeDataString(raw).Trim();
                        if (!string.IsNullOrWhiteSpace(raw))
                            return raw;
                    }
                }
            }

            string stem = Path.GetFileNameWithoutExtension(outPath);
            if (!string.IsNullOrWhiteSpace(stem))
                return stem;

            return "Team";
        }

        private static Node CreateSceneRootNode(List<NodeInfo> parsedNodes, string teamName)
        {
            // Use the literal string "Scene Root" -- the exact name the
            // game's ?render / ?model files give to their root node
            // (parsedNodes[0].Name == "Scene Root" on every 2016-2020
            // car). Preserving this instead of substituting the team
            // name keeps parity with the source file hierarchy the
            // modder is working against.
            const string rootLabel = "Scene Root";

            var sceneRoot = parsedNodes.FirstOrDefault(n => string.Equals(n.Name, "Scene Root", StringComparison.Ordinal));
            if (sceneRoot != null)
            {
                return new Node(rootLabel)
                {
                    Transform = BuildTransform(sceneRoot)
                };
            }

            return new Node(rootLabel);
        }

        private static List<NodeInfo> GetExportedX0ControlNodes(List<NodeInfo> parsedNodes)
        {
            var result = new List<NodeInfo>();

            foreach (var n in parsedNodes)
            {
                if (string.IsNullOrWhiteSpace(n.Name))
                    continue;

                if (!n.Name.StartsWith("x0_", StringComparison.Ordinal))
                    continue;

                if (string.Equals(n.Name, "x0_mesh", StringComparison.Ordinal))
                    continue;

                result.Add(n);
            }

            return result
                .OrderBy(n => n.Name, StringComparer.Ordinal)
                .ToList();
        }

        private static List<int> TryParseRenderPartRootIndices(string folder)
        {
            try
            {
                string renderFile = FindMainRenderFile(folder);
                if (string.IsNullOrWhiteSpace(renderFile) || !File.Exists(renderFile))
                    return new List<int>();

                byte[] data = File.ReadAllBytes(renderFile);
                if (data.Length < 0x0C)
                    return new List<int>();

                int nodeCount = ReadU16(data, 0);
                int tableCount = ReadU16(data, 2);

                const int nodesStart = 0x0C;
                const int nodeStride = 120;
                int tableStart = nodesStart + (nodeCount * nodeStride);
                if (tableStart < 0 || tableStart + (tableCount * 2) > data.Length)
                    return new List<int>();

                var result = new List<int>(tableCount);
                for (int i = 0; i < tableCount; i++)
                {
                    int idx = ReadU16(data, tableStart + (i * 2));
                    result.Add(idx);
                }

                return result;
            }
            catch
            {
                return new List<int>();
            }
        }

        private static Dictionary<string, int> BuildRenderPartRootNameToIndex(List<NodeInfo> parsedNodes, List<int> renderPartRootIndices)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (int idx in renderPartRootIndices)
            {
                if ((uint)idx >= (uint)parsedNodes.Count)
                    continue;

                string name = parsedNodes[idx].Name ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!map.ContainsKey(name))
                    map[name] = idx;
            }

            return map;
        }

        // Builds the complete Assimp node tree mirroring the ?render file exactly.
        // Every node in parsedNodes (except Scene Root itself) is created and parented
        // according to its ParentIndex. Nodes whose parent IS Scene Root attach directly
        // to sceneRootNode. All x0_ control nodes, x*_mesh grouping nodes, and every
        // render part are included with their correct transforms and names.
        private static void BuildRenderableNodeTree(
            Node sceneRootNode,
            List<NodeInfo> parsedNodes,
            Dictionary<int, Node> exportNodeByIndex)
        {
            if (parsedNodes == null || parsedNodes.Count == 0)
                return;

            int sceneRootIndex = parsedNodes.FindIndex(
                n => string.Equals(n.Name, "Scene Root", StringComparison.Ordinal));
            if (sceneRootIndex < 0)
                return;

            // Register the Scene Root Assimp node so recursive calls can find it.
            exportNodeByIndex[sceneRootIndex] = sceneRootNode;

            for (int i = 0; i < parsedNodes.Count; i++)
            {
                if (parsedNodes[i].Index == sceneRootIndex)
                    continue;

                CreateNodeRecursive(i, parsedNodes, sceneRootIndex, sceneRootNode, exportNodeByIndex);
            }
        }

        // Recursively creates an Assimp node for the given render-file node and all
        // of its ancestors, following ParentIndex until Scene Root is reached.
        private static Node CreateNodeRecursive(
            int nodeIndex,
            List<NodeInfo> parsedNodes,
            int sceneRootIndex,
            Node sceneRootNode,
            Dictionary<int, Node> exportNodeByIndex)
        {
            if (exportNodeByIndex.TryGetValue(nodeIndex, out var existing))
                return existing;

            var info = parsedNodes[nodeIndex];
            var newNode = new Node(info.Name) { Transform = BuildTransform(info) };
            exportNodeByIndex[nodeIndex] = newNode;

            // Find the Assimp parent: if parent IS Scene Root use sceneRootNode directly;
            // otherwise recurse so the parent node is created first.
            Node parentAssimp;
            int parentIndex = info.ParentIndex;

            if (parentIndex == sceneRootIndex || parentIndex < 0 || parentIndex >= parsedNodes.Count)
            {
                parentAssimp = sceneRootNode;
            }
            else
            {
                parentAssimp = CreateNodeRecursive(
                    parentIndex, parsedNodes, sceneRootIndex, sceneRootNode, exportNodeByIndex);
            }

            parentAssimp.Children.Add(newNode);
            return newNode;
        }

        private static List<Model3D> EnumerateTopLevelPartModels(Model3DGroup group)
        {
            var list = new List<Model3D>();
            if (group == null)
                return list;

            foreach (var child in group.Children)
            {
                if (child != null)
                    list.Add(child);
            }

            return list;
        }

        /// <summary>
        /// Sentinel returned by <see cref="TryResolveLoadedPartNameFromModel"/>
        /// for part models whose ONLY geometry is overlay submeshes (slot
        /// names ending in "_overlay"). Callers treat this as "skip this
        /// model entirely": no target node is created, no geometry is
        /// attached, and no unresolved-part-N warning is logged. The
        /// overlay meshes themselves were already filtered out by
        /// GroupPartModelGeometryBySlot, so there's no geometry to lose.
        /// </summary>
        private const string SKIP_OVERLAY_ONLY_MODEL = "__skip_overlay_only__";

        // ---------------------------------------------------------------
        // Root-union pseudo-part bone-binding redistribution.
        //
        // Background: the IDF root-union descriptor packs ~17 material-
        // keyed submeshes ("body", "carbon tile 1", "generic", ...) into
        // one nameless mesh shared across the whole car. The Classic
        // loader emits each as a Model3D whose partName is the slot
        // label; the exporter sees e.g. a "carbon tile 1" Model3D
        // alongside real per-part Models. Without redistribution it
        // becomes a top-level "carbon tile 1" object containing 80% of
        // the car's carbon geometry as one conjoined mesh, which is
        // not the per-part-with-shared-material structure modders want.
        //
        // Empirically (mclaren 2018 IDF decode): the carbon-tile-1
        // vertices in vb=0[0..81211] are stored in BONE-LOCAL space --
        // each part's verts cluster around origin in raw vb=0, with the
        // bone-byte in vb=5 carrying the part identity. That makes
        // spatial bbox lookups meaningless (every per-part's "tight"
        // carbon bbox collapses to an overlapping ±0.15 cube at origin),
        // so we redistribute by reading vb=5 directly and using the bone
        // byte as the authoritative part identifier.
        //
        // Mapping was derived by, for each per-part .idf descriptor,
        // walking its first submesh's IB segment + VOffset and counting
        // the dominant bone byte across the referenced vertices in vb=5.
        // Confidence ≥ 80% in every entry below; values are stable across
        // F1 2018 teams because they live in the global vehicle-package
        // bone palette, not per-team data.
        //
        // For shared-bone cases (a few bones list multiple parts in the
        // empirical analysis), the entry below is the part that owns
        // the carbon-tile-1 share of that bone. That's typically the
        // suspension arm or trim-bearing part, not the brake disc or
        // camera mount, because only the former has a carbon-tile-1
        // slot in its native descriptor.
        // ---------------------------------------------------------------
        private static readonly HashSet<string> ROOT_UNION_MATERIAL_NAMES =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "carbon tile 1", "generic", "body", "detail",
            "mirror left", "wheel", "disc", "mirror right",
            "cockpit", "number", "rear light", "steering wheel",
            "carbon tile 4", "flag", "lcd", "digi revs", "rainbeads",
        };

        private static bool IsRootUnionPseudoPart(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return false;
            string trimmed = partName.Trim();
            if (ROOT_UNION_MATERIAL_NAMES.Contains(trimmed)) return true;

            // Decal variant: "<stock-material>_decal" is a root-union
            // pseudo-part too. The Classic loader emits an overlay
            // sibling for every decal-bearing slot of the root-union
            // descriptor; that sibling's slot is renamed from
            // "<material>_overlay" to "<material>_decal" by
            // GroupPartModelGeometryBySlot, and it propagates back
            // up through TryResolveLoadedPartNameFromModel as the
            // resolved partName for an overlay-only Model3D. Without
            // this branch, "carbon tile 1_decal" failed the exact-
            // match check, fell through to the normal emit path,
            // and ResolveTargetExportNodeForPart's path-3 fallback
            // synthesised a top-level scene-root node named
            // "carbon tile 1_decal" -- visible in the diag's
            // RootChildren list as an orphan with no real parent
            // semantics. Catching it here routes the geometry
            // through the bone-byte redistribution pass instead,
            // so the decal triangles attach to the right per-part
            // (safetycell, nosecone, etc.) as <part>_<material>_
            // decal material slots -- the material survives, no
            // orphan node, no skipped data.
            const string decalSuffix = "_decal";
            if (trimmed.EndsWith(decalSuffix, StringComparison.OrdinalIgnoreCase))
            {
                string stripped = trimmed.Substring(0, trimmed.Length - decalSuffix.Length).TrimEnd();
                if (ROOT_UNION_MATERIAL_NAMES.Contains(stripped)) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when <paramref name="model"/> came from a 2018-style
        /// root-union <c>&amp;mesh</c> descriptor (URL of the shape
        /// <c>...lod=N&amp;mesh</c>, no per-part name segment) and false
        /// when it came from a per-part <c>&amp;&lt;partname&gt;&amp;mesh</c>
        /// descriptor (e.g. <c>...lod=0&amp;safetycell&amp;mesh</c>,
        /// <c>...lod=0&amp;cockpit&amp;mesh</c>).
        ///
        /// <para>This exists because <see cref="ROOT_UNION_MATERIAL_NAMES"/>
        /// contains slot labels that double as legitimate per-part
        /// descriptor names on 2019/2020 ERPs -- "cockpit", "rear light",
        /// "steering wheel", "wheel", "disc", etc. On a 2018 ERP those
        /// names only ever appear as slot labels inside the unnamed
        /// root-union descriptor (no real <c>&amp;cockpit&amp;mesh</c>
        /// exists), so catching them via partName alone was correct.
        /// On a 2019/2020 ERP the smart-scanner port at
        /// F1ModelLoaderLegacy.BuildGeometryFromSubMeshes gives every
        /// per-part descriptor's submeshes a partName equal to the
        /// descriptor's own part name, so a real per-part cockpit
        /// (3482 triangles: 10 body + 3472 generic) arrived at the
        /// exporter's main loop with partName="cockpit" and got
        /// misrouted into <see cref="RedistributeRootUnionPseudoPartByBone"/>.
        /// With no carbon-bone signature in any of those triangles
        /// (the cockpit body is paint, the cockpit generic is
        /// generic_main_d-textured interior), every triangle was
        /// classified as a "duplicate" by the dynamic native-carbon-bone
        /// filter and silently skipped. The diag's
        /// <c>duplicates_skipped=3482, placed_triangles=0</c> is
        /// exactly the cockpit's full triangle count vanishing into
        /// that pass -- visible to the user as a missing cockpit
        /// node-with-geometry in the exported glTF, even though
        /// "cockpit" still appears in RootChildren (the parsed-scene
        /// node is created by ResolveTargetExportNodeForPart's
        /// path-3 fallback, but it ends up empty).</para>
        ///
        /// <para>The discriminator is the descriptor URL stored at
        /// registration time in
        /// <see cref="MeshSubmeshRegistry.GetDescriptorHint"/>.
        /// F1ModelLoaderClassic populates this from
        /// <c>_currentDescriptorPath</c> at line 4784; the Legacy
        /// smart-scanner port populates it from <c>descriptorPath</c>
        /// at the matching site. The parser below mirrors
        /// F1ModelLoaderClassic.ExtractPartNameFromDescriptorFilename
        /// (line 5067) so the two routines agree on what counts as a
        /// per-part URL.</para>
        ///
        /// <para>Default for a model whose meshes have no descriptor
        /// hint stored is <c>true</c> -- preserve the pre-fix
        /// behavior where every pseudo-part-named model went through
        /// redistribution. This change can only NARROW the
        /// redistribution scope (skip the pass for confirmed per-part
        /// 2019 models), never widen it.</para>
        /// </summary>
        private static bool IsModelFromRootUnionDescriptor(Model3D model)
        {
            if (model == null) return true;

            foreach (var mesh in EnumerateGeometry(model))
            {
                string hint = MeshSubmeshRegistry.GetDescriptorHint(mesh);
                if (string.IsNullOrEmpty(hint)) continue;

                // Mirror of F1ModelLoaderClassic.ExtractPartNameFromDescriptorFilename
                // (line 5067). Intentionally duplicated rather than
                // referenced because that helper is private and
                // adding a wider accessor would touch a file we
                // don't otherwise need to modify in this fix.
                string fn;
                try { fn = System.IO.Path.GetFileName(hint) ?? ""; }
                catch { fn = hint; }
                if (fn.Length == 0) continue;

                string dec;
                try { dec = System.Uri.UnescapeDataString(fn); }
                catch { dec = fn; }

                int lodIdx = dec.IndexOf("lod=", StringComparison.OrdinalIgnoreCase);
                if (lodIdx < 0) continue;

                int firstAmp = dec.IndexOf('&', lodIdx);
                if (firstAmp < 0) continue;

                int nextAmp = dec.IndexOf('&', firstAmp + 1);
                if (nextAmp < 0)
                {
                    // No second '&' -- URL is "...lod=N&mesh" with no part
                    // name segment. 2018 root-union shape.
                    int tailStart = firstAmp + 1;
                    if (tailStart < dec.Length &&
                        string.Compare(dec, tailStart, "mesh", 0, 4,
                            StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        return true;
                    }
                    continue;
                }

                // Second '&' found -- confirm the tail is "mesh" and that
                // the wedged segment is non-empty before declaring this
                // a per-part descriptor.
                int tailStart2 = nextAmp + 1;
                if (tailStart2 < dec.Length &&
                    string.Compare(dec, tailStart2, "mesh", 0, 4,
                        StringComparison.OrdinalIgnoreCase) == 0)
                {
                    string candidate = dec.Substring(firstAmp + 1,
                        nextAmp - firstAmp - 1).Trim();
                    if (!string.IsNullOrEmpty(candidate))
                        return false; // per-part descriptor
                }
            }

            // No mesh produced a usable hint -- preserve old behavior.
            return true;
        }

        private static readonly Dictionary<byte, string> POOL_A_BONE_TO_PART_NAME =
            new Dictionary<byte, string>
        {
            { 0x00, "safetycell"          },
            { 0x01, "frontflap_fl"        },
            { 0x02, "suspension_bl"       },
            { 0x03, "hub_caliper_fr"      },
            { 0x04, "hub_caliper_br"      },
            { 0x05, "suspension_br"       },
            { 0x06, "hub_caliper_bl"      },
            { 0x07, "rearwingmoving_top"  },
            { 0x08, "rearwing_top"        },
            { 0x09, "rearwing_br"         },
            { 0x0a, "rearwing_bl"         },
            { 0x0b, "suspension_fr"       },
            { 0x0c, "frontspoiler_fm"     },
            { 0x0d, "frontflap_fr"        },
            { 0x0e, "suspension_fl"       },
            { 0x0f, "hub_caliper_fl"      },
            { 0x10, "sidewing_fl"         },
            { 0x11, "sidewing_fr"         },
            { 0x12, "wing_mirror_l"       },
            { 0x13, "nosecone"            },
            { 0x14, "camera_wing"         },
            { 0x15, "wing_mirror_r"       },
            { 0x16, "suspension2_br"      },
            { 0x17, "suspension2_bl"      },
            { 0x18, "suspension2_fr"      },
            { 0x19, "suspension2_fl"      },
            { 0x1a, "diffuser_bm"         },
            { 0x1e, "suspension3_fr"      },
            { 0x1f, "suspension3_fl"      },
            { 0x26, "rearfin_bm"          },
            { 0x2c, "tether_fr"           },
            { 0x2d, "tether_bl"           },
            { 0x2e, "tether_br"           },
            { 0x2f, "tether_fl"           },
            { 0x30, "hal"                 },
            { 0x31, "driver_surroun"      },
            { 0x32, "frontwing_fl_02"     },
            { 0x33, "frontwing_fr_02"     },
            { 0x34, "frontwing_fl_01"     },
            { 0x35, "frontwing_fr_01"     },
        };

        // Bones whose vertices are already covered by some per-part's
        // native carbon-tile-1 submesh (per loader-log inspection of
        // mclaren 2018). Triangles in the pseudo-part with these bones
        // would duplicate geometry the per-part already emits, causing
        // Z-fighting; skip them during redistribution.
        private static readonly HashSet<byte> POOL_A_NATIVE_CARBON_BONES =
            new HashSet<byte>
        {
            0x02, 0x04, 0x05, 0x06, 0x0b, 0x0e, 0x10, 0x11,
            0x16, 0x17, 0x18, 0x19, 0x1e, 0x1f, 0x26,
            0x2c, 0x2d, 0x2e, 0x2f, 0x34, 0x35,
        };

        /// <summary>
        /// Reverse of <see cref="POOL_A_BONE_TO_PART_NAME"/>: maps a part's
        /// canonical name back to its Pool A bone byte. Built once per
        /// exporter call (not memoised statically) because the table is
        /// tiny -- 41 entries -- and a static cache would just be one
        /// more thing to invalidate when the bone map gets edited.
        ///
        /// Used by <see cref="BuildDynamicNativeCarbonBoneSet"/> to
        /// translate "this loaded per-part has a carbon-tile slot" into
        /// "the bone byte for that part is natively covered, dedup
        /// against it during root-union redistribution".
        /// </summary>
        private static Dictionary<string, byte> BuildPartNameToBoneReverseMap()
        {
            var d = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in POOL_A_BONE_TO_PART_NAME)
            {
                // Last write wins on the (extremely unlikely) event of a
                // duplicate part name in the forward table. Keys in the
                // forward table have been unique by inspection for every
                // car we've shipped, so this is just defensive.
                d[kv.Value] = kv.Key;
            }
            return d;
        }

        /// <summary>
        /// Walk the loaded per-parts and build the set of Pool A bone
        /// bytes whose owning per-part natively emits a "carbon"-named
        /// material slot. That set is the correct dedup target for the
        /// root-union redistribution pass: triangles whose dominant
        /// bone is in the set are duplicates that the per-part already
        /// emits, so the redistribution pass skips them.
        ///
        /// <para>Why dynamic instead of static: the previous
        /// implementation used a hand-tuned static set
        /// (POOL_A_NATIVE_CARBON_BONES) calibrated on stock 2018
        /// McLaren. Other cars with the same bone numbering but
        /// different shader bindings -- notably classic-content
        /// chassis like c_mclaren_98, c_brawn_09 -- have a different
        /// subset of parts that emit native carbon-tile geometry. The
        /// static set then over-skips, dropping carbon panels that
        /// have no duplicate to dedup against, visible to the user as
        /// missing geometry on the order of 1-2% of the car's
        /// surface.</para>
        ///
        /// <para>Detection rule: a part's slot is "carbon" if the slot
        /// name contains the case-insensitive substring "carbon"
        /// (catches "carbon tile 1" through "carbon tile 4", any
        /// future "carbon detail" variants, and the 2019+ legacy
        /// loader's "carbon" generic). False positives are negligible
        /// in practice -- no non-carbon slot names contain that
        /// substring in any 2016-2025 chassis we've inspected.</para>
        /// </summary>
        private static HashSet<byte> BuildDynamicNativeCarbonBoneSet(
            List<Model3D> loadedPartModels,
            List<string> orderedPartNames,
            List<string> warnings)
        {
            var dynamicSet = new HashSet<byte>();
            if (loadedPartModels == null || orderedPartNames == null) return dynamicSet;

            var partNameToBone = BuildPartNameToBoneReverseMap();
            var sink = new List<string>(); // discard slot-grouping warnings;
                                           // the main per-part loop below
                                           // calls the same function and
                                           // surfaces them once there.

            int n = Math.Min(loadedPartModels.Count, orderedPartNames.Count);
            for (int i = 0; i < n; i++)
            {
                string partName = orderedPartNames[i];
                if (string.IsNullOrEmpty(partName)) continue;
                if (string.Equals(partName, SKIP_OVERLAY_ONLY_MODEL, StringComparison.Ordinal))
                    continue;
                if (!partNameToBone.TryGetValue(partName, out byte bone)) continue;

                var slotGroups = GroupPartModelGeometryBySlot(loadedPartModels[i], partName, sink);
                if (slotGroups == null) continue;

                foreach (var slotName in slotGroups.Keys)
                {
                    if (slotName == null) continue;
                    if (slotName.IndexOf("carbon", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        dynamicSet.Add(bone);
                        break;
                    }
                }
            }
            return dynamicSet;
        }

        // Read vb=5 from the IDF folder and extract byte 0 (the bone
        // index) per 4-byte stride. Returns null with a warning if the
        // file is missing or malformed; callers fall through to a no-op
        // redistribution and the pseudo-part is then attached to its own
        // top-level node (the pre-redistribution behaviour). All known
        // F1 2018 packages ship vb=5 next to vb=0..4, so the fallback
        // path is for hardened error handling, not the common case.
        private static byte[] LoadPoolABoneBytes(string idfFolder, List<string> warnings)
        {
            if (string.IsNullOrEmpty(idfFolder) || !Directory.Exists(idfFolder))
            {
                warnings.Add("Bone-binding redistribution unavailable: idf folder missing.");
                return null;
            }

            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(idfFolder, "*temp001*vb=5*");
            }
            catch (Exception ex)
            {
                warnings.Add("Bone-binding redistribution unavailable: cannot enumerate idf folder (" + ex.Message + ").");
                return null;
            }

            if (candidates.Length == 0)
            {
                warnings.Add("Bone-binding redistribution unavailable: vb=5 not found in idf folder.");
                return null;
            }

            // Prefer the lod=all variant; vb=5 should be identical across
            // LODs but lod=all is the canonical form in vanilla packages.
            string vb5Path = candidates[0];
            foreach (string c in candidates)
            {
                if (c.IndexOf("lod=all", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    vb5Path = c;
                    break;
                }
            }

            byte[] raw;
            try
            {
                raw = File.ReadAllBytes(vb5Path);
            }
            catch (Exception ex)
            {
                warnings.Add("Bone-binding redistribution unavailable: cannot read " + vb5Path + " (" + ex.Message + ").");
                return null;
            }

            if (raw.Length < 4 || (raw.Length & 3) != 0)
            {
                warnings.Add("Bone-binding redistribution unavailable: vb=5 has malformed length " +
                    raw.Length.ToString(CultureInfo.InvariantCulture) + ".");
                return null;
            }

            int count = raw.Length / 4;
            byte[] bones = new byte[count];
            for (int i = 0; i < count; i++)
            {
                bones[i] = raw[i * 4];
            }
            return bones;
        }

        // Build a compact MeshGeometry3D from a subset of triangles in a
        // source mesh. Vertices are remapped so only those referenced by
        // the kept triangles end up in the result -- otherwise a 1089-tri
        // chunk routed to nosecone would carry every vertex of the
        // 81212-vert pseudo-part, blowing the .gltf size up.
        private static MeshGeometry3D BuildSubMeshFromTriangles(
            MeshGeometry3D src, List<int> triStartIndices)
        {
            var positions = new Point3DCollection();
            bool hasNormals = src.Normals != null && src.Normals.Count == src.Positions.Count;
            bool hasUvs = src.TextureCoordinates != null && src.TextureCoordinates.Count == src.Positions.Count;
            var normals = hasNormals ? new Vector3DCollection() : null;
            var uvs = hasUvs ? new System.Windows.Media.PointCollection() : null;
            var remap = new Dictionary<int, int>();

            var tri = src.TriangleIndices;
            var indices = new System.Windows.Media.Int32Collection(triStartIndices.Count * 3);
            int posCount = src.Positions.Count;

            int Map(int srcIdx)
            {
                if (remap.TryGetValue(srcIdx, out int existing)) return existing;
                int newIdx = positions.Count;
                positions.Add(src.Positions[srcIdx]);
                if (normals != null) normals.Add(src.Normals[srcIdx]);
                if (uvs != null) uvs.Add(src.TextureCoordinates[srcIdx]);
                remap[srcIdx] = newIdx;
                return newIdx;
            }

            foreach (int triStart in triStartIndices)
            {
                int i0 = tri[triStart];
                int i1 = tri[triStart + 1];
                int i2 = tri[triStart + 2];
                if ((uint)i0 >= (uint)posCount ||
                    (uint)i1 >= (uint)posCount ||
                    (uint)i2 >= (uint)posCount) continue;
                indices.Add(Map(i0));
                indices.Add(Map(i1));
                indices.Add(Map(i2));
            }

            var result = new MeshGeometry3D
            {
                Positions = positions,
                TriangleIndices = indices,
            };
            if (normals != null) result.Normals = normals;
            if (uvs != null) result.TextureCoordinates = uvs;
            return result;
        }

        // Sum triangles across every MeshGeometry3D inside a Model3D
        // tree. Used by the pseudo-part filter to distinguish a real
        // root-union pseudo-part (54 207 triangles for "carbon tile 1"
        // in mclaren 2018) from a real per-part whose loader-resolved
        // partName happens to fall back to a stock-material slot name
        // (e.g. left_paddle / mix_dial / right_paddle / tyre_dial each
        // have a single 32-180 tri "steering wheel" submesh and would
        // otherwise be wrongly redistributed away into safetycell).
        private static int CountTrianglesInModel(Model3D model)
        {
            int total = 0;
            var stack = new Stack<Model3D>();
            stack.Push(model);
            while (stack.Count > 0)
            {
                var m = stack.Pop();
                if (m is Model3DGroup g)
                {
                    foreach (var c in g.Children) stack.Push(c);
                    continue;
                }
                if (m is GeometryModel3D gm &&
                    gm.Geometry is MeshGeometry3D mg &&
                    mg.TriangleIndices != null)
                {
                    total += mg.TriangleIndices.Count / 3;
                }
            }
            return total;
        }

        // Slice a pseudo-part Model3D into triangle batches owned by
        // real per-parts based on the bone byte recorded in vb=5 for
        // each vertex. Each batch becomes its own Assimp.Mesh attached
        // to the matching per-part's scene-graph node, so the carbon-
        // tile-1 material ends up as a slot on safetycell, nosecone,
        // hal, driver_surroun, frontspoiler_fm, etc. (parts that don't
        // have a native carbon-tile-1 slot but whose bone the carbon
        // verts are bound to). Triangles whose dominant bone is already
        // covered by a per-part native carbon-tile-1 submesh are SKIPPED
        // to avoid Z-fighting with the duplicate geometry the per-part
        // already emits. Returns the total triangle count actually
        // placed (excludes skipped duplicates).
        private static int RedistributeRootUnionPseudoPartByBone(
            Scene scene,
            Model3D pseudoPart,
            string pseudoPartName,
            byte[] vb5Bones,
            Dictionary<string, int> renderPartRootNameToIndex,
            Dictionary<int, Node> exportNodeByIndex,
            List<NodeInfo> parsedNodes,
            Dictionary<string, int> materialIndexByName,
            string outDir,
            List<string> warnings,
            Dictionary<(string PartName, string BaseSlot), System.Windows.Media.PointCollection> decalUvSidecar,
            HashSet<byte> nativeCarbonBones,
            out int duplicatesSkipped)
        {
            duplicatesSkipped = 0;
            if (vb5Bones == null || vb5Bones.Length == 0) return 0;
            // Defensive: if no dedup set is supplied (shouldn't happen
            // with the new dynamic-set call site), use the static set
            // so behaviour is identical to the pre-fix codepath.
            if (nativeCarbonBones == null) nativeCarbonBones = POOL_A_NATIVE_CARBON_BONES;

            var slotGroups = GroupPartModelGeometryBySlot(pseudoPart, pseudoPartName, warnings);
            int trianglesPlaced = 0;
            int boneCount = vb5Bones.Length;

            foreach (var slotEntry in slotGroups)
            {
                string slotName = slotEntry.Key;
                var slotMeshes = slotEntry.Value;
                if (slotMeshes == null) continue;

                for (int sm = 0; sm < slotMeshes.Count; sm++)
                {
                    var src = slotMeshes[sm];
                    if (src == null) continue;
                    if (src.Positions == null || src.Positions.Count == 0) continue;
                    if (src.TriangleIndices == null || src.TriangleIndices.Count < 3) continue;

                    // The pseudo-part's VOffset against the global vb=5.
                    // Earlier code assumed VOffset=0 (true for LOD0's
                    // root-union). For LOD1 the root-union has
                    // VOffset=145118; for LOD2 it's 249102. Without
                    // adding the offset, every triangle's bone byte
                    // gets read from the WRONG slice of vb=5 and the
                    // redistribution maps the geometry onto the
                    // wrong owning per-part -- visible as parts
                    // "confused with each other" at non-zero LODs.
                    int vOffset = F1ModelLoaderClassic.GetMeshVOffset(src);

                    // Group triangles by the part name their dominant bone
                    // resolves to. WPF TriangleIndices values are 0-based
                    // local indices; the corresponding global vb=5 index
                    // is wpf_index + vOffset.
                    var byPartName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                    var tri = src.TriangleIndices;
                    int posCount = src.Positions.Count;
                    int triCount = tri.Count / 3;

                    for (int t = 0; t < triCount; t++)
                    {
                        int triStart = t * 3;
                        int v0 = tri[triStart];
                        int v1 = tri[triStart + 1];
                        int v2 = tri[triStart + 2];
                        if ((uint)v0 >= (uint)posCount ||
                            (uint)v1 >= (uint)posCount ||
                            (uint)v2 >= (uint)posCount) continue;

                        // Look up bone byte for each vertex through the
                        // VOffset-shifted global index. Out-of-range
                        // verts (shouldn't happen for a valid pseudo-
                        // part descriptor) get bone 0 (safetycell) as
                        // a safe default.
                        int g0 = v0 + vOffset;
                        int g1 = v1 + vOffset;
                        int g2 = v2 + vOffset;
                        byte b0 = (uint)g0 < (uint)boneCount ? vb5Bones[g0] : (byte)0;
                        byte b1 = (uint)g1 < (uint)boneCount ? vb5Bones[g1] : (byte)0;
                        byte b2 = (uint)g2 < (uint)boneCount ? vb5Bones[g2] : (byte)0;

                        // Majority of three: if two agree, use them; else
                        // pick v0's bone (deterministic, no spatial bias).
                        byte dom = b0;
                        if (b1 == b2) dom = b1;
                        else if (b0 == b1 || b0 == b2) dom = b0;
                        // else b0,b1,b2 all distinct -> dom = b0 already.

                        // Skip triangles whose owner already emits this
                        // carbon natively (avoids visible Z-fighting on
                        // duplicated geometry). Set comes from caller --
                        // dynamically computed from the actual loaded
                        // per-parts, so the dedup matches the cars
                        // shader bindings rather than a hand-tuned
                        // stock-2018 reference set.
                        if (nativeCarbonBones.Contains(dom))
                        {
                            duplicatesSkipped++;
                            continue;
                        }

                        if (!POOL_A_BONE_TO_PART_NAME.TryGetValue(dom, out string targetName))
                            targetName = "safetycell";  // unknown bone -> chassis fallback

                        if (!byPartName.TryGetValue(targetName, out var list))
                        {
                            list = new List<int>();
                            byPartName[targetName] = list;
                        }
                        list.Add(triStart);
                    }

                    string materialEaid = MeshSubmeshRegistry.GetMaterialEaid(src);
                    string textureEaid = MeshSubmeshRegistry.GetTextureEaid(src);
                    string materialDisplayName = DeriveMaterialDisplayName(materialEaid, slotName);

                    foreach (var kvp in byPartName)
                    {
                        string targetPartName = kvp.Key;
                        var triList = kvp.Value;
                        if (triList.Count == 0) continue;

                        Node targetNode = ResolveTargetExportNodeForPart(
                            targetPartName,
                            renderPartRootNameToIndex,
                            exportNodeByIndex,
                            scene.RootNode,
                            parsedNodes,
                            warnings);
                        if (targetNode == null) continue;

                        var subMesh = BuildSubMeshFromTriangles(src, triList);
                        if (subMesh.Positions.Count == 0) continue;
                        if (subMesh.TriangleIndices == null || subMesh.TriangleIndices.Count < 3) continue;

                        string slotSafe = (slotName ?? "slot").Replace(' ', '_');
                        string meshName = targetPartName + "_" + slotSafe;
                        // Look up the matching overlay's UV0 from the
                        // sidecar by (targetPartName, slotName). The
                        // overlay was authored on the per-part Model3D
                        // for this same target part with the same slot
                        // name (just suffixed "_overlay"), so the keys
                        // line up. Vertex-count check inside TryGetDecalUv
                        // ensures we don't mismatch the redistributed
                        // sub-mesh against an overlay whose vertex layout
                        // diverged.
                        var decalUv = TryGetDecalUv(decalUvSidecar, targetPartName, slotName, subMesh.Positions.Count);
                        var damageUv = F1ModelLoaderClassic.GetMeshDamageUvs(subMesh);
                        if (damageUv != null && damageUv.Count != subMesh.Positions.Count) damageUv = null;
                        var aMesh = ConvertToAssimpMesh(subMesh, meshName, decalUv, damageUv);
                        if (aMesh.Vertices.Count == 0 || aMesh.Faces.Count == 0) continue;

                        aMesh.MaterialIndex = EnsureMaterial(
                            scene, materialIndexByName, materialDisplayName, slotName,
                            materialEaid, textureEaid, Path.Combine(outDir, "textures"), warnings);
                        ValidateAssimpMeshOrThrow(aMesh, aMesh.Name);

                        int meshIndex = scene.Meshes.Count;
                        scene.Meshes.Add(aMesh);
                        targetNode.MeshIndices.Add(meshIndex);
                        trianglesPlaced += triList.Count;
                    }
                }
            }

            return trianglesPlaced;
        }

        private static List<string> ResolveLoadedPartNamesFromSuccessfulGroups(List<Model3D> loadedPartModels, List<string> warnings)
        {
            // Always emit exactly one entry per loaded part model so the
            // caller can zip the two lists 1:1. For overlay-only models
            // we emit the SKIP_OVERLAY_ONLY_MODEL sentinel (the caller
            // skips them); for real unresolved parts we emit
            // "unresolved_part_N" and attach under x0_mesh so the modder
            // still sees the geometry.
            var result = new List<string>();

            for (int i = 0; i < loadedPartModels.Count; i++)
            {
                if (TryResolveLoadedPartNameFromModel(loadedPartModels[i], out string partName))
                {
                    result.Add(partName);
                }
                else if (IsOverlayOnlyModel(loadedPartModels[i]))
                {
                    // Overlay-only models arise when the Classic loader emits
                    // an overlay sibling for a root/union submesh (no family
                    // registered, slot ends in "_overlay"). The exporter has
                    // nothing to do with these -- geometry gets filtered in
                    // GroupPartModelGeometryBySlot -- so skip cleanly with
                    // no warning and no x0_mesh placeholder.
                    result.Add(SKIP_OVERLAY_ONLY_MODEL);
                }
                else
                {
                    string fallback = "unresolved_part_" + i.ToString(CultureInfo.InvariantCulture);
                    warnings.Add("Could not resolve part name for loaded group at index " +
                        i.ToString(CultureInfo.InvariantCulture) +
                        "; using fallback name '" + fallback + "' and attaching geometry under x0_mesh.");
                    result.Add(fallback);
                }
            }

            return result;
        }

        /// <summary>
        /// True when every MeshGeometry3D inside the given model carries a
        /// "_overlay"-suffixed slot name (e.g. "carbon tile 1_overlay",
        /// "body_overlay"). Overlay submeshes are the Classic loader's
        /// half-millimetre-offset decal pass; the exporter has no use for
        /// them and already filters them at the slot-grouping step.
        /// </summary>
        private static bool IsOverlayOnlyModel(Model3D model)
        {
            var geoms = EnumerateGeometry(model);
            if (geoms.Count == 0) return false;
            foreach (var mesh in geoms)
            {
                string slot = MeshSubmeshRegistry.GetSubmeshName(mesh);
                if (string.IsNullOrEmpty(slot)) return false;
                if (!slot.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static bool TryResolveLoadedPartNameFromModel(Model3D model, out string partName)
        {
            partName = "";

            var geometries = EnumerateGeometry(model);
            foreach (var mesh in geometries)
            {
                string family = MeshFamilyRegistry.GetFamily(mesh);
                if (!string.IsNullOrWhiteSpace(family))
                {
                    partName = family;
                    return true;
                }
            }

            foreach (var mesh in geometries)
            {
                string hint = MeshSubmeshRegistry.GetDescriptorHint(mesh);
                string fromHint = ExtractDescriptorPartNameFromHint(hint);
                if (!string.IsNullOrWhiteSpace(fromHint) &&
                    !string.Equals(fromHint, "mesh", StringComparison.Ordinal))
                {
                    partName = fromHint;
                    return true;
                }
            }

            // Last-resort fallback: use the submesh's SLOT NAME as the part
            // name. Applies to submeshes with no family and no descriptor
            // hint -- these only arise from exotic edge cases now that
            // the Classic loader splits the root/union carbon by
            // connected components before it ever reaches the exporter
            // (each island gets its own family at load time). Kept as a
            // safety net.
            // First pass: prefer non-overlay slots so a descriptor
            // that has both primary and overlay submeshes resolves
            // to the primary slot name.
            foreach (var mesh in geometries)
            {
                string slot = MeshSubmeshRegistry.GetSubmeshName(mesh);
                if (string.IsNullOrWhiteSpace(slot)) continue;
                if (slot.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase))
                {
                    slot = slot.Substring(0, slot.Length - "_overlay".Length) + "_decal";
                }

                partName = slot;
                return true;
            }

            // Fallback: every submesh in this Model3D is overlay-only
            // (the loader split the descriptor into a primary Model3D
            // and an overlay-only sibling Model3D). Strip the
            // "_overlay" suffix so we resolve to the same partName
            // as the primary sibling -- both Model3Ds will then
            // attach to the same per-part target node, and the
            // overlay submeshes will appear as `_decal`-suffixed
            // glTF primitives alongside the primary ones.
            foreach (var mesh in geometries)
            {
                string slot = MeshSubmeshRegistry.GetSubmeshName(mesh);
                if (string.IsNullOrWhiteSpace(slot)) continue;
                if (slot.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase))
                {
                    partName = slot.Substring(0, slot.Length - "_overlay".Length);
                    return true;
                }
            }

            return false;
        }

        private static string ExtractDescriptorPartNameFromHint(string descriptorHint)
        {
            if (string.IsNullOrWhiteSpace(descriptorHint))
                return "";

            string decoded = Uri.UnescapeDataString(descriptorHint);

            var rxNormal = new Regex(@"(?:\?|%3F)lod=\d+&([^&]+)&mesh\b", RegexOptions.IgnoreCase);
            var mNormal = rxNormal.Match(decoded);
            if (mNormal.Success)
            {
                string partName = mNormal.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(partName))
                    return partName;
            }

            var rxBareMesh = new Regex(@"(?:\?|%3F)lod=\d+&mesh\b", RegexOptions.IgnoreCase);
            var mBareMesh = rxBareMesh.Match(decoded);
            if (mBareMesh.Success)
                return "mesh";

            return "";
        }

        private static Node GetOrCreateDirectChildNode(Node parent, string childName)
        {
            foreach (var child in parent.Children)
            {
                if (string.Equals(child.Name, childName, StringComparison.Ordinal))
                    return child;
            }

            var created = new Node(childName);
            parent.Children.Add(created);
            return created;
        }

        private static Node ResolveTargetExportNodeForPart(
            string partName,
            Dictionary<string, int> renderPartRootNameToIndex,
            Dictionary<int, Node> exportNodeByIndex,
            Node sceneRootNode,
            List<NodeInfo> parsedNodes,
            List<string> warnings)
        {
            // 1. Exact match against the render-part table (keyed by full IDF node names).
            //    This is the path that catches steering_wheel / left_paddle /
            //    right_paddle / mix_dial / tyre_dial -- they live under
            //    steer_master in the ?render hierarchy, so renderPartRootName
            //    ToIndex maps them to the correct nested Assimp node which
            //    already has the cumulative parent transform applied by
            //    BuildRenderableNodeTree (steer_master->steering_wheel
            //    etc.). Attaching the mesh here puts the geometry exactly
            //    where the cockpit actually sits in the car. Bypassing
            //    this path -- as the previous flat-mode did -- left the
            //    new top-level node at identity transform, which is why
            //    the steering wheel was clipping through the middle of
            //    the chassis instead of being in the driver's hands.
            if (renderPartRootNameToIndex.TryGetValue(partName, out int rootIdx) &&
                exportNodeByIndex.TryGetValue(rootIdx, out Node exactNode))
                return exactNode;

            // 2. Prefix match for ERP-truncated names.
            //    The ERP EAID segment is limited to 14 chars, so "driver_surround" (15)
            //    becomes "driver_surroun" and "halo" (4) becomes "hal" (3) in the loader.
            //    Match when the ERP name is a prefix of the IDF node name OR vice-versa,
            //    but only when the result is unambiguous (exactly one candidate).
            if (partName.Length >= 3)
            {
                var prefixMatches = renderPartRootNameToIndex
                    .Where(kv => kv.Key.StartsWith(partName, StringComparison.Ordinal) ||
                                 partName.StartsWith(kv.Key, StringComparison.Ordinal))
                    .ToList();

                if (prefixMatches.Count == 1 &&
                    exportNodeByIndex.TryGetValue(prefixMatches[0].Value, out Node prefixNode))
                    return prefixNode;
            }

            // 3. Fallback: create a new top-level node named after the part
            //    and parent it directly under Scene Root. This is the path
            //    used by the root-union redistribution's synthesised submeshes
            //    when their target part name doesn't exist in the render-part
            //    table at all (which only happens for slot-name fallbacks like
            //    "carbon tile 1_decal" produced by the redistribution pass).
            _ = parsedNodes;
            _ = warnings;
            for (int i = 0; i < sceneRootNode.Children.Count; i++)
            {
                if (string.Equals(sceneRootNode.Children[i].Name, partName, StringComparison.Ordinal))
                    return sceneRootNode.Children[i];
            }
            var newTopLevel = new Node(partName);
            sceneRootNode.Children.Add(newTopLevel);
            return newTopLevel;
        }

        /// <summary>
        /// Concatenates a list of WPF <see cref="MeshGeometry3D"/> instances
        /// into a single merged mesh. Positions / Normals / TextureCoordinates
        /// are appended in order; TriangleIndices are rebased so indices from
        /// the second mesh onward point into the merged vertex array.
        ///
        /// Normals and UVs are padded when a contributing mesh doesn't carry
        /// them so the three attribute arrays always stay length-aligned with
        /// Positions -- required by the downstream Assimp conversion and by
        /// glTF2's per-primitive accessor-length constraint. Padding uses a
        /// zero vector (3D) / zero point (2D), matching the defensive
        /// defaulting ConvertToAssimpMesh already applies in the single-mesh
        /// path.
        ///
        /// Caller guarantees at least one non-null mesh; a single-element list
        /// is handled by the caller before this helper is invoked.
        /// </summary>
        private static MeshGeometry3D MergeMeshGeometries(List<MeshGeometry3D> meshes)
        {
            var merged = new MeshGeometry3D
            {
                Positions = new System.Windows.Media.Media3D.Point3DCollection(),
                Normals = new System.Windows.Media.Media3D.Vector3DCollection(),
                TextureCoordinates = new System.Windows.Media.PointCollection(),
                TriangleIndices = new System.Windows.Media.Int32Collection(),
            };

            foreach (var mesh in meshes)
            {
                if (mesh == null || mesh.Positions == null || mesh.Positions.Count == 0)
                    continue;

                int baseIndex = merged.Positions.Count;
                int posCount = mesh.Positions.Count;

                // Positions
                for (int p = 0; p < posCount; p++)
                    merged.Positions.Add(mesh.Positions[p]);

                // Normals -- pad with zero when missing/mismatched so the
                // merged attribute array stays aligned with Positions.
                if (mesh.Normals != null && mesh.Normals.Count == posCount)
                {
                    for (int n = 0; n < posCount; n++)
                        merged.Normals.Add(mesh.Normals[n]);
                }
                else
                {
                    for (int n = 0; n < posCount; n++)
                        merged.Normals.Add(new MediaVector3D(0, 0, 0));
                }

                // UVs -- pad with zero when missing/mismatched.
                if (mesh.TextureCoordinates != null && mesh.TextureCoordinates.Count == posCount)
                {
                    for (int u = 0; u < posCount; u++)
                        merged.TextureCoordinates.Add(mesh.TextureCoordinates[u]);
                }
                else
                {
                    for (int u = 0; u < posCount; u++)
                        merged.TextureCoordinates.Add(new System.Windows.Point(0, 0));
                }

                // Triangle indices, rebased into the merged vertex array.
                if (mesh.TriangleIndices != null)
                {
                    for (int t = 0; t < mesh.TriangleIndices.Count; t++)
                        merged.TriangleIndices.Add(mesh.TriangleIndices[t] + baseIndex);
                }
            }

            return merged;
        }

        private static SortedDictionary<string, List<MeshGeometry3D>> GroupPartModelGeometryBySlot(
            Model3D partModel,
            string partName,
            List<string> warnings)
        {
            // Walks the Model3D's geometries and groups base submeshes by
            // slot name. Overlay submeshes (slot ending in "_overlay")
            // are SILENTLY DROPPED here -- they were already extracted
            // into the decal-UV sidecar by the pre-pass at the top of
            // ExportLodToFile, and will be looked up by partName + slot
            // at the per-base emit site (both the main per-part path
            // and the root-union redistribution path consult the same
            // sidecar). This keeps the decal-merge logic in one place
            // instead of trying to pair within a single Model3D, which
            // is where the previous attempt failed for cars whose body
            // bases come from a root-union pseudopart while overlays
            // come from per-part Model3Ds.
            //
            // The exporter NEVER emits a separate "_decal" glTF
            // primitive: decal data either rides the base primitive's
            // TEXCOORD_1 channel (paired via the sidecar at emit time)
            // or it is dropped with a warning.
            var grouped = new SortedDictionary<string, List<MeshGeometry3D>>(StringComparer.Ordinal);
            var geometries = EnumerateGeometry(partModel);

            foreach (var mesh in geometries)
            {
                string slot = MeshSubmeshRegistry.GetSubmeshName(mesh);

                if (!string.IsNullOrEmpty(slot) &&
                    slot.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase))
                {
                    // Overlay -- handled via sidecar at emit time.
                    continue;
                }

                if (string.IsNullOrWhiteSpace(slot))
                {
                    slot = "default";
                    warnings.Add("Part '" + partName + "' had a mesh with no submesh/material-slot name. Fell back to 'default'.");
                }

                if (!grouped.TryGetValue(slot, out var list))
                {
                    list = new List<MeshGeometry3D>();
                    grouped[slot] = list;
                }
                list.Add(mesh);
            }

            return grouped;
        }

        /// <summary>
        /// Pre-pass over all per-part Model3Ds to extract every overlay
        /// submesh's UV0 into a sidecar dictionary keyed by
        /// (resolvedPartName, baseSlot). Both the main per-part emit
        /// loop and the root-union redistribution path consult this
        /// sidecar at the moment they emit a base mesh, looking up by
        /// the SAME (partName, slot) key to attach the overlay's UV0
        /// as TEXCOORD_1 on the base primitive.
        ///
        /// Why a sidecar rather than per-Model3D pairing: the Classic
        /// loader splits geometry across two complementary structures
        /// for cars with root-union descriptors (the McLaren 2018 and
        /// almost every other 2018-vintage car):
        ///
        ///   * Per-part Model3Ds (safetycell, nosecone, frontflap_*,
        ///     etc.) carry ONLY overlay submeshes -- the 0.5mm-nudged
        ///     decal-pass duplicates with the decal atlas UV in their
        ///     UV0. They have no base submeshes of their own.
        ///
        ///   * A single root-union pseudopart (named after one of the
        ///     stock material slots, e.g. "carbon tile 1") carries
        ///     ALL the base submeshes for body / carbon / number
        ///     across every per-part target -- their triangles get
        ///     redistributed to the right per-part target node by
        ///     RedistributeRootUnionPseudoPartByBone using vb=5's
        ///     bone-byte ownership.
        ///
        /// A pairing that only operates within one Model3D's geometry
        /// list cannot match overlays in per-part Model3Ds with bases
        /// in the root-union pseudopart -- they're not in the same
        /// list. The sidecar bridges that gap by using the
        /// (partName, slot) tuple as the matching key, which both
        /// paths know unambiguously at emit time.
        ///
        /// Vertex-count match is verified at lookup time, not here.
        /// </summary>
        private static Dictionary<(string PartName, string BaseSlot), System.Windows.Media.PointCollection> BuildDecalUvSidecar(
            List<Model3D> loadedPartModels,
            List<string> orderedPartNames)
        {
            var sidecar = new Dictionary<(string, string), System.Windows.Media.PointCollection>();
            if (loadedPartModels == null || orderedPartNames == null) return sidecar;

            for (int i = 0; i < loadedPartModels.Count; i++)
            {
                if (i >= orderedPartNames.Count) break;
                string partName = orderedPartNames[i];
                if (string.IsNullOrWhiteSpace(partName)) continue;
                if (string.Equals(partName, SKIP_OVERLAY_ONLY_MODEL, StringComparison.Ordinal))
                {
                    // The "overlay-only" sentinel marks Model3Ds whose
                    // geometries are ALL overlays; they were skipped
                    // upstream because they couldn't resolve to a
                    // target node. We still want their UVs in the
                    // sidecar so the redistribution path can pick
                    // them up. Resolve the partName ourselves from
                    // the model's first overlay slot.
                    if (!TryResolveOverlayOnlyModelPartName(loadedPartModels[i], out partName))
                        continue;
                }

                var partModel = loadedPartModels[i];
                if (partModel == null) continue;

                foreach (var mesh in EnumerateGeometry(partModel))
                {
                    string slot = MeshSubmeshRegistry.GetSubmeshName(mesh);
                    if (string.IsNullOrEmpty(slot)) continue;
                    if (!slot.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase))
                    {
                        // FIX 34f: BASE meshes whose overlay was skipped
                        // (parked / tiled decal UVs) carry a parked raw
                        // stash so the FBX still gets a TEXCOORD_1 — the
                        // Blender addon wires the decal mix per material
                        // and a missing UV layer falls back to the tiling
                        // UV (smeared decals). Overlay-derived entries
                        // (added below) take precedence over these.
                        var parkedUv = MeshSubmeshRegistry.GetRawDecalUv(mesh);
                        if (parkedUv != null && mesh.Positions != null &&
                            parkedUv.Count == mesh.Positions.Count)
                        {
                            var baseKey = (partName, slot);
                            if (!sidecar.ContainsKey(baseKey))
                                sidecar[baseKey] = parkedUv;
                        }
                        continue;
                    }

                    string baseSlot = slot.Substring(0, slot.Length - "_overlay".Length);
                    if (string.IsNullOrWhiteSpace(baseSlot)) continue;

                    // Prefer the RAW decal UVs stashed by the loader (FIX
                    // 34d): the overlay mesh's own TextureCoordinates are
                    // preview-massaged (clamped to [0,1] + two sentinel
                    // vertices appended to pin WPF's brush bbox), which
                    // corrupts values and breaks the vertex-count pairing
                    // below. Raw UVs are one-per-original-vertex.
                    var uv = MeshSubmeshRegistry.GetRawDecalUv(mesh)
                             ?? mesh.TextureCoordinates;
                    int posCount = mesh.Positions?.Count ?? 0;
                    int uvCount = uv?.Count ?? 0;
                    if (uv == null || posCount == 0 || uvCount == 0) continue;
                    // Accept either exact match (legacy overlays) or the
                    // preview's +2-sentinel layout paired with raw UVs.
                    if (uvCount != posCount && uvCount != posCount - 2) continue;

                    var key = (partName, baseSlot);
                    // Last-writer-wins on duplicate keys (typical
                    // case: a single overlay per (part, slot) tuple,
                    // so duplicates are an edge case anyway).
                    sidecar[key] = uv;
                }
            }

            return sidecar;
        }

        /// <summary>
        /// Helper to identify the partName of a Model3D that consists
        /// entirely of overlay submeshes. Used by BuildDecalUvSidecar
        /// when a Model3D was tagged with the SKIP_OVERLAY_ONLY_MODEL
        /// sentinel (because it had no geometry to emit as a primitive)
        /// but its UVs are still useful for sidecar pairing.
        /// </summary>
        private static bool TryResolveOverlayOnlyModelPartName(Model3D model, out string partName)
        {
            partName = "";
            if (model == null) return false;
            foreach (var mesh in EnumerateGeometry(model))
            {
                string slot = MeshSubmeshRegistry.GetSubmeshName(mesh);
                if (string.IsNullOrEmpty(slot)) continue;
                if (!slot.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase)) continue;
                // The submesh's slot is "<basepart>_overlay" or
                // "<material>_overlay" depending on the loader path.
                // Strip and use as the key. This must align with how
                // the redistribution path resolves the targetPartName,
                // which is derived from vb=5's bone-byte mapping
                // (POOL_A_BONE_TO_PART_NAME). For the McLaren 2018
                // case, the per-part Model3D's overlays use the part
                // name (e.g. "safetycell_overlay" -> "safetycell"),
                // which IS the right key.
                partName = slot.Substring(0, slot.Length - "_overlay".Length);
                return !string.IsNullOrWhiteSpace(partName);
            }
            return false;
        }

        /// <summary>
        /// Look up a paired decal UV in the sidecar. Returns null when
        /// no entry exists for the (partName, slot) tuple, or when the
        /// stored UV count doesn't match <paramref name="expectedVertexCount"/>.
        /// The vertex-count guard catches data anomalies where an
        /// overlay's vertex layout drifted from its base's; rather than
        /// silently emit garbled UVs, we log nothing here and the
        /// caller emits the base without TEXCOORD_1.
        /// </summary>
        private static System.Windows.Media.PointCollection TryGetDecalUv(
            Dictionary<(string PartName, string BaseSlot), System.Windows.Media.PointCollection> sidecar,
            string partName,
            string slot,
            int expectedVertexCount)
        {
            if (sidecar == null) return null;
            if (string.IsNullOrEmpty(partName) || string.IsNullOrEmpty(slot)) return null;
            if (!sidecar.TryGetValue((partName, slot), out var uv)) return null;
            if (uv == null) return null;
            if (uv.Count != expectedVertexCount) return null;
            return uv;
        }

        /// <summary>
        /// Turns a full material EAID like
        /// "eaid://f1_2018_vehicle_package/teams/mclaren/materials/mclaren_paint.material"
        /// into a short, modder-friendly display name ("mclaren_paint") by
        /// pulling out the file stem. Falls back to the slot name when the
        /// EAID is missing / unparseable, which preserves the exporter's
        /// prior behaviour for textureless or out-of-mapping-table submeshes.
        /// </summary>
        /// <summary>
        /// Produces the glTF Material display name for a (material EAID, slot)
        /// pair. The game's own .emb?context_N livery-binding files are the
        /// authoritative name source: they map each slot ("body", "carbon
        /// tile 1", "generic", ...) to a material EAID whose filename leaf
        /// is the exact display name the modder expects in Blender / Maya /
        /// 3ds Max. For the 2018 McLaren:
        ///
        ///   body            -> mclaren_paint.material         -> "mclaren_paint"
        ///   carbon tile 1   -> mclaren_carbon1.material       -> "mclaren_carbon1"
        ///   carbon tile 3   -> mclaren_carbon3.material       -> "mclaren_carbon3"
        ///   generic         -> generics.material              -> "generics"
        ///   generic cockpit -> generics_cockpit.material      -> "generics_cockpit"
        ///   rear light      -> rear_light.material            -> "rear_light"
        ///   steering wheel  -> mclaren_steeringwheel.material -> "mclaren_steeringwheel"
        ///   ...
        ///
        /// This preserves the game's own spelling conventions -- no
        /// title-casing, no space-stripping, no slot-name fallback
        /// heuristics. The loader already resolved each submesh's EAID
        /// via BuildMaterialEaidForSlot (which reads the .emb context)
        /// and stashed it on MeshSubmeshRegistry; we just pull the
        /// filename stem out here.
        ///
        /// When the loader couldn't resolve an EAID -- e.g. for slots
        /// not in the livery-binding table, or for the overlay _overlay
        /// siblings that are filtered out of the exporter anyway --
        /// we fall back to the raw slot name. That keeps the output
        /// deterministic even if the .emb context is missing a binding.
        /// </summary>
        /// <summary>
        /// Small EAID->leaf helper used by the per-slot filter path so
        /// the matching logic doesn't have to assemble the full glTF-
        /// style display name. Mirrors what
        /// MainWindow.ExtractMaterialLeaf does on the UI side, so both
        /// halves of the filter use the same string.
        /// </summary>
        private static string ExtractMaterialLeafForFilter(string materialEaid)
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

        private static string DeriveMaterialDisplayName(string materialEaid, string slotName)
        {
            if (!string.IsNullOrWhiteSpace(materialEaid))
            {
                // Strip trailing query-like segments ("?foo=bar") defensively.
                int q = materialEaid.IndexOfAny(new[] { '?', '#' });
                string eaid = q >= 0 ? materialEaid.Substring(0, q) : materialEaid;

                // Grab the last path segment.
                int slash = eaid.LastIndexOf('/');
                string leaf = slash >= 0 ? eaid.Substring(slash + 1) : eaid;

                // Drop known resource suffixes (.material, .tif, etc).
                string[] knownSuffixes = { ".material", ".tif", ".png", ".tga", ".dds", ".jpg", ".jpeg", ".bmp" };
                foreach (string suffix in knownSuffixes)
                {
                    if (leaf.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase))
                    {
                        leaf = leaf.Substring(0, leaf.Length - suffix.Length);
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(leaf))
                {
                    // Decal slot -> distinct material name so Blender
                    // shows it as a separate material slot on the
                    // same object (e.g. "mclaren_paint" + 
                    // "mclaren_paint_decal"). The materialEaid for
                    // a decal slot may be the same as the primary
                    // slot's EAID -- the texture EAID is the
                    // discriminator (mclaren_main_paint_d.tif vs
                    // mclaren_main_decal_da.tif) -- so we don't
                    // rely on the EAID alone for naming.
                    if (!string.IsNullOrEmpty(slotName) &&
                        slotName.EndsWith("_decal", StringComparison.OrdinalIgnoreCase) &&
                        !leaf.EndsWith("_decal", StringComparison.OrdinalIgnoreCase))
                    {
                        leaf = leaf + "_decal";
                    }
                    return leaf;
                }
            }

            // No EAID -> raw slot name as-is (no transformation).
            return string.IsNullOrWhiteSpace(slotName) ? "default" : slotName;
        }

        /// <summary>
        /// Three-arg legacy overload. Kept so existing call sites (e.g. the
        /// keep-alive wiring in the main export loop and the pre-populated
        /// "default" material) don't need to thread a fake EAID through.
        /// </summary>
        private static int EnsureMaterial(Scene scene, Dictionary<string, int> materialIndexByName, string slotName)
        {
            return EnsureMaterial(scene, materialIndexByName, slotName, slotName, null, null, null, null);
        }

        /// <summary>
        /// Legacy five-arg overload. Some call sites (e.g. the
        /// "default" bootstrap material before the main loop) don't have
        /// texture info available; routed through the full overload
        /// with texture-related args null so the PNG-embedding logic
        /// short-circuits cleanly.
        /// </summary>
        private static int EnsureMaterial(
            Scene scene,
            Dictionary<string, int> materialIndexByName,
            string materialDisplayName,
            string slotName,
            string materialEaid)
        {
            return EnsureMaterial(scene, materialIndexByName, materialDisplayName, slotName, materialEaid,
                null, null, null);
        }

        // Cache of texture-EAID -> PNG file path for the current export
        // run. Populated lazily the first time a given texture is needed
        // so a single decode + PNG encode is shared across every material
        // that binds the same texture (carbon goes on every carbon-fiber
        // slot; paint is reused between safetycell body / driver_surroun
        // body / wing_mirror body / etc.). Reset per ExportCarAllLodsFromErp
        // call via ClearTextureEmbedCache below.
        private static readonly Dictionary<string, string> _textureEaidToPngPath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Material manifest collected during the export. Keyed by the
        // FBX material name (i.e. the game's .material leaf, e.g.
        // "ferrari_paint"). Values record the PNG sidecar filenames per
        // role -- the Blender post-import script reads this JSON to know
        // which texture goes with which material WITHOUT having to
        // string-match (the textures don't always share a name root with
        // the material, e.g. "gloves" material binds to "<team>_glove_d
        // .png" with the trailing 's' missing). Written to disk after
        // FBX export as <fbx_basename>.materials.json.
        private static readonly Dictionary<string, MaterialManifestEntry> _materialManifest =
            new Dictionary<string, MaterialManifestEntry>(StringComparer.OrdinalIgnoreCase);

        private sealed class MaterialManifestEntry
        {
            public string Diffuse;
            public string Normal;
            public string Surface;
            public string Mask;
            public string Alpha;
            public string Decal;          // me_logoMap PNG sidecar
                                          // (per-team sponsor decals
                                          // for paint + carbon).
            public string DecalSurface;   // me_logoSurfaceMap PNG (G=rough,
                                          // B=metallic, masked by decal
                                          // alpha on the Blender side).
            public float Opacity = 1.0f;
            public string ShaderEaid;
            public float[] ColorDiffuse;  // R,G,B from the material's
                                          // parameter-driven diffuse colour
                                          // (tyre band, skin tone) when
                                          // applicable; null otherwise.
            public float[] HairColour;    // R,G,B from me_MatBaseColour on
                                          // char_hair_alpha_dither_* shaders
                                          // (hair card tint). Distinct from
                                          // ColorDiffuse because hair uses a
                                          // tinted grayscale strand sheet
                                          // rather than a recolour atlas.
            public float AlphaThreshold = -1f; // me_alphaThreshold on alpha-
                                               // dither shaders; -1 means
                                               // "not set" (don't emit).

            /// <summary>
            /// Parameter block stamped for materials whose shader is
            /// vehicle_carbon (or its no_damage variant). Carries the
            /// procedural shader parameters the game uses to synthesise
            /// the visible carbon weave from a pattern-direction texture
            /// rather than from a literal "carbon-coloured" diffuse. The
            /// Blender addon reads this and builds an anisotropic +
            /// clearcoat node graph instead of plugging the raw pattern
            /// into Base Color (which produces the pink/iridescent
            /// noise you'd otherwise see).
            /// </summary>
            public CarbonParams Carbon;
        }

        /// <summary>
        /// Mirrors the subset of vehicle_carbon.nefx2 parameters that
        /// the Blender addon needs to reconstruct a faithful-looking
        /// carbon material. All values are in the game's units (e.g.
        /// CarbonTile = 300 means 300x UV tiling, not 30000%).
        /// </summary>
        public sealed class CarbonParams
        {
            public float CarbonTile;            // me_carbonTile -- UV scale
            public float CarbonWidth;           // me_carbonWidth -- fibre width
            public float CarbonAnisotropyX;     // me_carbonAnisotropyX -- shimmer along weave
            public float CarbonAnisotropyY;     // me_carbonAnisotropyY -- shimmer across weave
            public float CarbonEffect;          // me_carbonEffect -- procedural fibre strength
            public float CarbonIor;             // me_carbonIor
            public float LacquerLevel;          // me_lacquerLevel -- clear-coat thickness
            public float LacquerRoughness;      // me_lacquerRoughness
            public float LacquerIor;            // me_lacquerIor
            public float[] DirtColour;          // me_dirtColour (vec3)
            public float DirtRoughness;         // me_dirtRoughness
            public float UOffset;               // me_uOffset
            public float VOffset;               // me_vOffset
        }

        private static void ClearTextureEmbedCache()
        {
            _textureEaidToPngPath.Clear();
            _materialManifest.Clear();
        }

        /// <summary>
        /// Records (or updates) the manifest entry for a material so the
        /// Blender post-import script has authoritative material->texture
        /// mappings instead of relying on substring filename heuristics.
        /// Called from EnsureMaterial as each texture role is discovered.
        ///
        /// The 'role' parameter is the JSON key: "diffuse" / "normal" /
        /// "surface" / "mask" / "alpha". 'pngPath' is the full path to
        /// the PNG sidecar that EnsureTexturePng (or one of its variants)
        /// just wrote; we record only its leaf filename in the manifest
        /// because the Blender script always reads from <fbx_dir>/textures/.
        /// </summary>
        private static void RecordManifestTexture(
            string materialName, string role, string pngPath)
        {
            if (string.IsNullOrEmpty(materialName) || string.IsNullOrEmpty(pngPath))
                return;
            if (!_materialManifest.TryGetValue(materialName, out var entry))
            {
                entry = new MaterialManifestEntry();
                _materialManifest[materialName] = entry;
            }
            string leaf = Path.GetFileName(pngPath);
            switch (role)
            {
                case "diffuse": entry.Diffuse = leaf; break;
                case "normal": entry.Normal = leaf; break;
                case "surface": entry.Surface = leaf; break;
                case "mask": entry.Mask = leaf; break;
                case "alpha": entry.Alpha = leaf; break;
                case "decal": entry.Decal = leaf; break;
                case "decal_surface": entry.DecalSurface = leaf; break;
            }
        }

        /// <summary>Stamp non-texture data (opacity, shader, diffuse colour)
        /// onto the manifest entry. Called once per material as soon as
        /// its name + materialEaid are known.</summary>
        private static void RecordManifestMeta(
            string materialName, string materialEaid,
            float opacity, float[] colorDiffuse)
        {
            if (string.IsNullOrEmpty(materialName)) return;
            if (!_materialManifest.TryGetValue(materialName, out var entry))
            {
                entry = new MaterialManifestEntry();
                _materialManifest[materialName] = entry;
            }
            entry.Opacity = opacity;
            entry.ColorDiffuse = colorDiffuse;
            // Resolve shader EAID via the active TextureCache so the
            // manifest carries enough context for the post-import script
            // to switch wiring per shader family if it wants to.
            var cache = F1ModelLoader.ActiveTextureCache
                        ?? F1ModelLoaderClassic.ActiveTextureCache
                        ?? F1ModelLoaderLegacy.ActiveTextureCache;
            if (cache != null && !string.IsNullOrEmpty(materialEaid))
            {
                try
                {
                    var mf = cache.GetMaterial(materialEaid);
                    if (mf != null)
                    {
                        entry.ShaderEaid = mf.ShaderEaid;

                        // Hair-family shaders pack the visible tint into
                        // me_MatBaseColour (vec3) and dither cutoff into
                        // me_alphaThreshold (f32) -- decoded for both Hulkenberg
                        // (blonde) and Bearman (dark) and verified to match
                        // the per-driver hair colour seen in-game. Surface this
                        // to the Blender side so it can multiply the strand
                        // sheet with the tint and binarise alpha at the
                        // game-authored cutoff.
                        // vehicle_carbon family: faithful reproduction in
                        // Blender needs the procedural params (tile rate,
                        // anisotropy, lacquer) -- the raw pattern texture
                        // alone is meaningless without them. Covers both
                        // vehicle_carbon.nefx2 and vehicle_carbon_no_damage.nefx2.
                        if (!string.IsNullOrEmpty(mf.ShaderEaid) &&
                            mf.ShaderEaid.IndexOf(
                                "vehicle_carbon",
                                StringComparison.OrdinalIgnoreCase) >= 0 &&
                            mf.Parameters != null)
                        {
                            entry.Carbon = new CarbonParams
                            {
                                CarbonTile        = mf.GetFloat("me_carbonTile")        ?? 1f,
                                CarbonWidth       = mf.GetFloat("me_carbonWidth")       ?? 0.076f,
                                CarbonAnisotropyX = mf.GetFloat("me_carbonAnisotropyX") ?? 1f,
                                CarbonAnisotropyY = mf.GetFloat("me_carbonAnisotropyY") ?? 1f,
                                CarbonEffect      = mf.GetFloat("me_carbonEffect")      ?? 0f,
                                CarbonIor         = mf.GetFloat("me_carbonIor")         ?? 1.5f,
                                LacquerLevel      = mf.GetFloat("me_lacquerLevel")      ?? 0f,
                                LacquerRoughness  = mf.GetFloat("me_lacquerRoughness")  ?? 0.5f,
                                LacquerIor        = mf.GetFloat("me_lacquerIor")        ?? 1.4f,
                                DirtColour        = mf.GetVec3("me_dirtColour"),
                                DirtRoughness     = mf.GetFloat("me_dirtRoughness")     ?? 0.98f,
                                UOffset           = mf.GetFloat("me_uOffset")           ?? 0f,
                                VOffset           = mf.GetFloat("me_vOffset")           ?? 0f,
                            };
                        }

                        if (!string.IsNullOrEmpty(mf.ShaderEaid) &&
                            mf.ShaderEaid.IndexOf(
                                "char_hair_alpha_dither",
                                StringComparison.OrdinalIgnoreCase) >= 0 &&
                            mf.Parameters != null)
                        {
                            var hair = mf.GetVec3("me_MatBaseColour");
                            if (hair != null && hair.Length >= 3 &&
                                !(hair[0] <= 0f && hair[1] <= 0f && hair[2] <= 0f))
                            {
                                entry.HairColour = new[] {
                                    Math.Max(0f, hair[0]),
                                    Math.Max(0f, hair[1]),
                                    Math.Max(0f, hair[2]),
                                };
                            }
                            var thresh = mf.GetFloat("me_alphaThreshold");
                            if (thresh.HasValue && thresh.Value > 0f && thresh.Value < 1f)
                                entry.AlphaThreshold = thresh.Value;
                        }
                    }
                }
                catch { /* manifest entry stays without shader */ }
            }
        }

        /// <summary>
        /// Serialise the collected manifest as JSON next to the FBX. The
        /// file lives at <fbxPathWithoutExt>.materials.json so multiple
        /// FBXs in the same folder don't collide. Best-effort: failures
        /// are logged via the warnings list and don't abort export.
        /// </summary>
        private static void WriteMaterialManifest(string fbxPath, List<string> warnings)
        {
            if (_materialManifest.Count == 0) return;
            try
            {
                string dir = Path.GetDirectoryName(fbxPath) ?? "";
                string stem = Path.GetFileNameWithoutExtension(fbxPath) ?? "manifest";
                string outPath = Path.Combine(dir, stem + ".materials.json");

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"fbx_basename\": " + JsonString(stem) + ",");
                sb.AppendLine("  \"texture_subdir\": \"textures\",");
                sb.AppendLine("  \"materials\": {");
                bool first = true;
                foreach (var kv in _materialManifest)
                {
                    if (!first) sb.AppendLine(",");
                    first = false;
                    sb.Append("    ").Append(JsonString(kv.Key)).AppendLine(": {");
                    var e = kv.Value;
                    var fields = new List<string>();
                    if (!string.IsNullOrEmpty(e.Diffuse)) fields.Add("      \"diffuse\": " + JsonString(e.Diffuse));
                    if (!string.IsNullOrEmpty(e.Normal)) fields.Add("      \"normal\": " + JsonString(e.Normal));
                    if (!string.IsNullOrEmpty(e.Surface)) fields.Add("      \"surface\": " + JsonString(e.Surface));
                    if (!string.IsNullOrEmpty(e.Mask)) fields.Add("      \"mask\": " + JsonString(e.Mask));
                    if (!string.IsNullOrEmpty(e.Alpha)) fields.Add("      \"alpha\": " + JsonString(e.Alpha));
                    if (!string.IsNullOrEmpty(e.Decal)) fields.Add("      \"decal\": " + JsonString(e.Decal));
                    if (!string.IsNullOrEmpty(e.DecalSurface)) fields.Add("      \"decal_surface\": " + JsonString(e.DecalSurface));
                    if (e.Opacity < 0.999f) fields.Add("      \"opacity\": " + e.Opacity.ToString("0.###", CultureInfo.InvariantCulture));
                    if (e.ColorDiffuse != null && e.ColorDiffuse.Length >= 3)
                        fields.Add($"      \"diffuse_color\": [{e.ColorDiffuse[0].ToString("0.######", CultureInfo.InvariantCulture)}, {e.ColorDiffuse[1].ToString("0.######", CultureInfo.InvariantCulture)}, {e.ColorDiffuse[2].ToString("0.######", CultureInfo.InvariantCulture)}]");
                    if (e.HairColour != null && e.HairColour.Length >= 3)
                        fields.Add($"      \"hair_color\": [{e.HairColour[0].ToString("0.######", CultureInfo.InvariantCulture)}, {e.HairColour[1].ToString("0.######", CultureInfo.InvariantCulture)}, {e.HairColour[2].ToString("0.######", CultureInfo.InvariantCulture)}]");
                    if (e.AlphaThreshold >= 0f)
                        fields.Add("      \"alpha_threshold\": " + e.AlphaThreshold.ToString("0.###", CultureInfo.InvariantCulture));
                    if (e.Carbon != null)
                    {
                        var c = e.Carbon;
                        string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
                        var carbonFields = new List<string>
                        {
                            "        \"tile\": "       + F(c.CarbonTile),
                            "        \"width\": "      + F(c.CarbonWidth),
                            "        \"anisotropy\": [" + F(c.CarbonAnisotropyX) + ", " + F(c.CarbonAnisotropyY) + "]",
                            "        \"effect\": "     + F(c.CarbonEffect),
                            "        \"ior\": "        + F(c.CarbonIor),
                            "        \"lacquer_level\": "     + F(c.LacquerLevel),
                            "        \"lacquer_roughness\": " + F(c.LacquerRoughness),
                            "        \"lacquer_ior\": "       + F(c.LacquerIor),
                            "        \"dirt_roughness\": "    + F(c.DirtRoughness),
                            "        \"uv_offset\": [" + F(c.UOffset) + ", " + F(c.VOffset) + "]",
                        };
                        if (c.DirtColour != null && c.DirtColour.Length >= 3)
                            carbonFields.Add("        \"dirt_colour\": [" +
                                F(c.DirtColour[0]) + ", " + F(c.DirtColour[1]) + ", " + F(c.DirtColour[2]) + "]");
                        fields.Add("      \"carbon\": {" + Environment.NewLine +
                                   string.Join("," + Environment.NewLine, carbonFields) +
                                   Environment.NewLine + "      }");
                    }
                    if (!string.IsNullOrEmpty(e.ShaderEaid)) fields.Add("      \"shader\": " + JsonString(e.ShaderEaid));
                    sb.AppendLine(string.Join("," + Environment.NewLine, fields));
                    sb.Append("    }");
                }
                sb.AppendLine();
                sb.AppendLine("  }");
                sb.AppendLine("}");

                File.WriteAllText(outPath, sb.ToString());
                warnings.Add($"Wrote material manifest: {Path.GetFileName(outPath)} " +
                             $"({_materialManifest.Count} materials)");
            }
            catch (Exception ex)
            {
                warnings.Add($"Material manifest write failed: {ex.Message}");
            }
        }

        // Minimal JSON string escaper -- handles backslash, quote, and
        // control chars. Adequate for our texture filenames + EAIDs
        // which are ASCII-only.
        private static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new System.Text.StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.AppendFormat("\\u{0:X4}", (int)c);
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Full overload. Creates (once) a glTF Material named after the
        /// resolved slot, and when a diffuse-texture EAID is available
        /// decodes that texture's raw pixels via
        /// <see cref="F1ModelLoaderClassic.ActiveTextureCache"/>, writes
        /// them as a PNG to the temp export directory, and attaches the
        /// file path as the material's diffuse texture slot. The PNG
        /// then gets inlined as a base64 data URI by
        /// <see cref="EmbedExternalResourcesIntoGltfAndDeleteSidecars"/>
        /// on its way to the user's output folder, so the final .gltf
        /// is fully self-contained with no sidecar files.
        /// </summary>
        private static int EnsureMaterial(
            Scene scene,
            Dictionary<string, int> materialIndexByName,
            string materialDisplayName,
            string slotName,
            string materialEaid,
            string textureEaid,
            string textureWorkDir,
            List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(materialDisplayName))
                materialDisplayName = "default";
            if (string.IsNullOrWhiteSpace(slotName))
                slotName = "default";

            // The material NAME is the display name exactly -- no
            // "__slot" suffix, no EAID-derived prefix. The user's
            // requirement is that a "generic"-slot submesh produces a
            // material literally named "Generic", not "generic__generic"
            // or "mclaren_generic__generic". DeriveMaterialDisplayName
            // has already taken care of title-casing and of picking
            // the right source string; EnsureMaterial just trusts it.
            string matName = materialDisplayName;

            // Dedup key includes the bound diffuse texture EAID so that
            // two submeshes whose loader-resolved materials share a leaf
            // name but bind DIFFERENT actual textures stay as separate
            // material entries. The previous implementation keyed by
            // matName alone; in classic content (2016-2018) that silently
            // collapses "minor faulty" cases where the .emb context
            // routes two slots to the same .material file but the texture
            // pipeline lands on different `me_albedoMap` bindings (e.g.
            // a body slot resolved to mclaren_paint_d.tif vs a fallback
            // resolved to mclaren_main_paint_d.tif). Under the old key,
            // whichever submesh ran first won the texture and every
            // subsequent submesh that produced the same matName silently
            // inherited it.
            //
            // Key format:
            //   "<matName>"                      -- when no textureEaid is
            //                                       known (textureless
            //                                       fallback paths, the
            //                                       bootstrap "default"
            //                                       entries, the keep-alive
            //                                       material). Behaviour
            //                                       identical to the legacy
            //                                       single-string key in
            //                                       these cases, so the
            //                                       three-arg / five-arg
            //                                       overloads still work
            //                                       unchanged.
            //   "<matName>|tex=<textureEaid>"    -- when a texture EAID is
            //                                       bound. Two submeshes
            //                                       sharing matName AND
            //                                       textureEaid still
            //                                       collapse to one
            //                                       material (correct: it
            //                                       really is the same
            //                                       binding). Different
            //                                       texture EAIDs produce
            //                                       different keys, so each
            //                                       gets its own Assimp
            //                                       Material with its own
            //                                       diffuse slot. Assimp's
            //                                       glTF2 exporter is happy
            //                                       to emit two materials
            //                                       with the same `name` --
            //                                       Blender's importer
            //                                       suffixes the second one
            //                                       (".001") on its end so
            //                                       the modder still sees
            //                                       both distinctly.
            //
            // The texture EAID is preferred over the material EAID for
            // the discriminator because (a) the user's described problem
            // is about TEXTURE divergence, and (b) two different material
            // EAIDs that share a leaf name AND a texture EAID really are
            // the same render target -- merging them is the right call
            // there.
            string dedupKey = string.IsNullOrWhiteSpace(textureEaid)
                ? matName
                : matName + "|tex=" + textureEaid;

            if (materialIndexByName.TryGetValue(dedupKey, out int existing))
                return existing;

            var mat = new Assimp.Material
            {
                Name = matName,
                ColorDiffuse = new Color4D(0.8f, 0.8f, 0.8f, 1.0f)
            };

            // Stash the raw EAID + the binding slot on the material itself
            // so the .diag.txt and any Assimp-aware debug tooling can see
            // provenance even if the glTF2 exporter doesn't round-trip
            // custom properties.
            if (!string.IsNullOrWhiteSpace(materialEaid))
                mat.AddProperty(new MaterialProperty("$raw.MaterialEAID", materialEaid));
            if (!string.IsNullOrWhiteSpace(textureEaid))
                mat.AddProperty(new MaterialProperty("$raw.DiffuseTextureEAID", textureEaid));
            mat.AddProperty(new MaterialProperty("$raw.MaterialSlot", slotName));

            // Material.ColorDiffuse from the actual game material's
            // parameter values. This is how parameter-driven colours
            // (tyre band colour me_logoColour, character skin
            // me_baseColour, etc.) make it into the exported FBX
            // WITHOUT modifying the diffuse texture itself. Blender's
            // FBX importer reads this into the principled BSDF's Base
            // Color multiplier, so the renderer multiplies the
            // (greyscale) Pirelli pattern × this colour at draw time --
            // matching the game shader one-to-one.
            //
            // For materials without a parameter-driven colour, the
            // default (0.8, 0.8, 0.8, 1.0) leaves the texture
            // colourimetrically unchanged in most viewers.
            ApplyParameterDrivenDiffuseColor(mat, materialEaid);

            // Decode the diffuse texture via the active TextureCache and
            // attach it to the material's diffuse slot. Without this step
            // every material ships to Assimp as flat gray -- paint, carbon,
            // generic, wheel, steering wheel, all of them -- because the
            // materialEaid/textureEaid arguments were previously unused.
            // The user sees this as "the carbon material isn't loading":
            // the material name is correct ("mclaren_carbon1") but there's
            // no texture on it, so the surface renders as a gray blob
            // instead of the woven carbon pattern.
            //
            // The decoded pixels come from F1ModelLoaderClassic's active
            // TextureCache (populated during LoadFolder). We write a PNG
            // sidecar into the temp work dir and attach its path; the
            // post-export step EmbedExternalResourcesIntoGltfAndDeleteSidecars
            // then inlines that PNG as a base64 data URI so the final
            // .gltf is self-contained.
            // Diffuse PNG sidecar: written to disk for the post-import
            // script to wire, NOT attached to the Assimp material. The
            // FBX ships with material NAMES that match the game and zero
            // texture references -- the post-import script reads the
            // material names, finds <material>_d.png / _n.png / _s.png
            // / _m.png / _alpha.png next to the FBX, and connects them
            // explicitly in Blender. This avoids the half-broken auto-
            // wiring Blender's FBX importer does for non-Diffuse channels.
            // Diffuse PNG sidecar.
            if (!string.IsNullOrWhiteSpace(textureEaid) &&
                !string.IsNullOrWhiteSpace(textureWorkDir))
            {
                string p = EnsureTexturePng(textureEaid, textureWorkDir, warnings);
                if (!string.IsNullOrEmpty(p))
                    RecordManifestTexture(matName, "diffuse", p);
            }

            // Companion textures: mask, alpha-dither derivative, normal,
            // surface. None are attached to the Assimp material; all are
            // recorded in the manifest by material name + role so the
            // Blender post-import script knows EXACTLY which PNG goes
            // with each material (no filename heuristics).
            if (!string.IsNullOrWhiteSpace(materialEaid) &&
                !string.IsNullOrWhiteSpace(textureWorkDir))
            {
                // Mask. We still write the PNG (cheap, in case the user
                // wants to pull it in manually later) but the Blender
                // script no longer auto-wires it -- masks were making
                // helmet / suit geometry disappear in Blender.
                string maskEaid = TryResolveMaskMapEaid(materialEaid);
                if (!string.IsNullOrEmpty(maskEaid))
                {
                    string p = EnsureTexturePng(maskEaid, textureWorkDir, warnings, asNormalMap: false);
                    if (!string.IsNullOrEmpty(p))
                        RecordManifestTexture(matName, "mask", p);
                    mat.AddProperty(new MaterialProperty(
                        "$raw.MaskTextureEAID", maskEaid));
                }

                // Alpha-dither luminance derivative (frays, hair, visor tear).
                if (!string.IsNullOrWhiteSpace(textureEaid) &&
                    IsAlphaDitherShader(materialEaid))
                {
                    string p = TryWriteLuminanceAlphaPng(textureEaid, textureWorkDir, warnings);
                    if (!string.IsNullOrEmpty(p))
                        RecordManifestTexture(matName, "alpha", p);
                    mat.AddProperty(new MaterialProperty(
                        "$raw.AlphaSource", "diffuse-luminance"));
                }

                // Scalar opacity (visor me_alpha).
                float opacityValue = TryResolveScalarAlphaParam(materialEaid);
                if (opacityValue < 0.999f)
                {
                    mat.AddProperty(new MaterialProperty(
                        "$raw.Opacity", opacityValue));
                }

                // Normal map.
                string normalEaid = TryResolveNormalMapEaid(materialEaid);
                if (!string.IsNullOrEmpty(normalEaid))
                {
                    string p = EnsureTexturePng(normalEaid, textureWorkDir, warnings, asNormalMap: true);
                    if (!string.IsNullOrEmpty(p))
                        RecordManifestTexture(matName, "normal", p);
                    mat.AddProperty(new MaterialProperty(
                        "$raw.NormalTextureEAID", normalEaid));
                }

                // Surface (PBR finish) map.
                string surfaceEaid = TryResolveSurfaceMapEaid(materialEaid);
                if (!string.IsNullOrEmpty(surfaceEaid))
                {
                    string p = EnsureTexturePng(surfaceEaid, textureWorkDir, warnings, asNormalMap: false);
                    if (!string.IsNullOrEmpty(p))
                        RecordManifestTexture(matName, "surface", p);
                    mat.AddProperty(new MaterialProperty(
                        "$raw.SurfaceTextureEAID", surfaceEaid));
                }

                // Logo / decal map. me_logoMap carries the per-team
                // sponsor decals as a diffuse+alpha texture (alpha is
                // the decal mask). Both vehicle_carbon and
                // vehicle_paint_v2 use this slot so the Blender side
                // can composite the same decal layer on top of either
                // base. EnsureTexturePng decodes alpha-bearing
                // formats end-to-end, so the resulting PNG keeps the
                // mask intact.
                string logoEaid = TryResolveLogoMapEaid(materialEaid);
                if (!string.IsNullOrEmpty(logoEaid))
                {
                    string p = EnsureTexturePng(logoEaid, textureWorkDir, warnings, asNormalMap: false);
                    if (!string.IsNullOrEmpty(p))
                        RecordManifestTexture(matName, "decal", p);
                    mat.AddProperty(new MaterialProperty(
                        "$raw.DecalTextureEAID", logoEaid));
                }

                // Per-decal surface (PBR finish) map -- same G=rough /
                // B=metallic encoding as the body surface, but masked
                // by the decal's alpha on the Blender side so only the
                // decal area picks up its values.
                string logoSurfaceEaid = TryResolveLogoSurfaceMapEaid(materialEaid);
                if (!string.IsNullOrEmpty(logoSurfaceEaid))
                {
                    string p = EnsureTexturePng(logoSurfaceEaid, textureWorkDir, warnings, asNormalMap: false);
                    if (!string.IsNullOrEmpty(p))
                        RecordManifestTexture(matName, "decal_surface", p);
                    mat.AddProperty(new MaterialProperty(
                        "$raw.DecalSurfaceTextureEAID", logoSurfaceEaid));
                }

                // Manifest meta (opacity, shader, parameter-driven color).
                // Captures the ColorDiffuse the FBX already carries so the
                // Blender script can apply it without re-reading the .material.
                float[] colVec = null;
                if (mat.HasProperty("$raw.ColorDiffuseSource"))
                {
                    // ApplyParameterDrivenDiffuseColor already stamped
                    // mat.ColorDiffuse; reuse that.
                    var c = mat.ColorDiffuse;
                    colVec = new[] { c.R, c.G, c.B };
                }
                RecordManifestMeta(matName, materialEaid, opacityValue, colVec);
            }

            int newIndex = scene.Materials.Count;
            scene.Materials.Add(mat);
            materialIndexByName[dedupKey] = newIndex;
            return newIndex;
        }

        /// <summary>
        /// Reads the material's parameter-driven diffuse colour from the
        /// parsed cbuf values and writes it to Material.ColorDiffuse so
        /// the FBX consumer (Blender's principled BSDF Base Color) can
        /// multiply it with the diffuse texture at draw time. This is
        /// the export-side counterpart to baking the colour into the
        /// texture -- we keep the texture as the raw game file and pass
        /// the colour through the material properties instead.
        ///
        /// Per-shader picks (in priority order):
        ///   - tyre shaders            -> me_logoColour (band colour)
        ///   - skin/HSL shaders        -> me_baseColour
        ///   - character cloth-base    -> not parameter-driven, leave default
        ///
        /// Adds nothing when the material has no relevant parameter,
        /// leaving the default (0.8, 0.8, 0.8, 1.0) which is the
        /// "white-ish" base most importers treat as identity tint.
        /// </summary>
        private static void ApplyParameterDrivenDiffuseColor(
            Assimp.Material mat, string materialEaid)
        {
            if (string.IsNullOrEmpty(materialEaid)) return;

            var cache = F1ModelLoader.ActiveTextureCache
                        ?? F1ModelLoaderClassic.ActiveTextureCache
                        ?? F1ModelLoaderLegacy.ActiveTextureCache;
            if (cache == null) return;

            MaterialFile mf;
            try { mf = cache.GetMaterial(materialEaid); }
            catch { return; }
            if (mf == null || mf.Parameters == null ||
                string.IsNullOrEmpty(mf.ShaderEaid)) return;

            string shader = mf.ShaderEaid;

            // Tyre sidewall: me_logoColour holds the per-compound band
            // colour (yellow / red / white / etc -- see References/
            // MaterialFormatNotes/05_*.md).
            float[] colour = null;
            string colourSource = null;
            if (shader.IndexOf("tyre_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shader.IndexOf("/tyre/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                colour = mf.GetVec3("me_logoColour");
                if (colour != null) colourSource = "me_logoColour";
            }
            // Character skin (HSL recolour system): me_baseColour drives
            // the skin tone.
            else if (shader.IndexOf("skin", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                colour = mf.GetVec3("me_baseColour");
                if (colour != null) colourSource = "me_baseColour";
            }

            if (colour == null || colour.Length < 3) return;
            // Reject (0,0,0) -- pure-black diffuse multipliers would make
            // every render output black; treat that as "no colour" and
            // leave the default. Defensive against materials that ship
            // a placeholder zero value.
            if (colour[0] <= 0f && colour[1] <= 0f && colour[2] <= 0f) return;

            mat.ColorDiffuse = new Color4D(
                Math.Clamp(colour[0], 0f, 1f),
                Math.Clamp(colour[1], 0f, 1f),
                Math.Clamp(colour[2], 0f, 1f),
                1.0f);
            mat.AddProperty(new MaterialProperty(
                "$raw.ColorDiffuseSource",
                $"{colourSource}=({colour[0]:F3},{colour[1]:F3},{colour[2]:F3})"));
        }

        /// <summary>
        /// Detects materials that use a shader whose transparency comes
        /// from runtime alpha-dither / alpha-test against the diffuse's
        /// luminance (rather than a real alpha channel or a separate
        /// mask). For these we derive alpha = max(R, G, B) and bake it
        /// into the PNG so Blender's principled BSDF can render the
        /// cutout correctly without shader-side help.
        ///
        /// Currently covers:
        ///   - char_hair_alpha_dither_skinned  (hair, glove frays)
        ///   - char_visor_tear_skinned         (tear-off strips)
        /// </summary>
        private static bool IsAlphaDitherShader(string materialEaid)
        {
            if (string.IsNullOrEmpty(materialEaid)) return false;

            var cache = F1ModelLoader.ActiveTextureCache
                        ?? F1ModelLoaderClassic.ActiveTextureCache
                        ?? F1ModelLoaderLegacy.ActiveTextureCache;
            if (cache == null) return false;

            MaterialFile mat;
            try { mat = cache.GetMaterial(materialEaid); }
            catch { return false; }
            if (mat == null || string.IsNullOrEmpty(mat.ShaderEaid)) return false;

            return mat.ShaderEaid.IndexOf(
                       "char_hair_alpha_dither", StringComparison.OrdinalIgnoreCase) >= 0
                || mat.ShaderEaid.IndexOf(
                       "char_visor_tear", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Re-decodes a diffuse texture, derives a per-pixel alpha from
        /// its RGB luminance (alpha = max R/G/B), and writes the result
        /// as a fresh PNG. Used for alpha-dither shaders -- see
        /// IsAlphaDitherShader for the gating logic.
        ///
        /// Output filename is the diffuse's leaf + "_alpha" so it
        /// doesn't collide with the plain-diffuse PNG that EnsureTexture
        /// Png already wrote for this same EAID earlier in the export
        /// (the plain version stays on disk in case other materials use
        /// the same texture without alpha-dither semantics).
        /// </summary>
        private static string TryWriteLuminanceAlphaPng(
            string textureEaid, string workDir, List<string> warnings)
        {
            try
            {
                // Trigger the standard diffuse decode so EnsureTexturePng
                // populates _textureEaidToPngPath if needed -- we then
                // re-read its bytes for the alpha derivation.
                string plainPng = EnsureTexturePng(
                    textureEaid, workDir, warnings, asNormalMap: false);
                if (string.IsNullOrEmpty(plainPng) || !File.Exists(plainPng))
                    return null;

                System.Windows.Media.Imaging.BitmapSource diffuseBmp;
                using (var fs = File.OpenRead(plainPng))
                {
                    var dec = new System.Windows.Media.Imaging.PngBitmapDecoder(
                        fs,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    diffuseBmp = dec.Frames[0];
                }
                int w = diffuseBmp.PixelWidth;
                int h = diffuseBmp.PixelHeight;

                var bgra32 = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    diffuseBmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                byte[] src = new byte[w * h * 4];
                bgra32.CopyPixels(src, w * 4, 0);

                byte[] dst = new byte[src.Length];
                for (int p = 0; p < w * h; p++)
                {
                    int o = p * 4;
                    byte b = src[o + 0];
                    byte g = src[o + 1];
                    byte r = src[o + 2];
                    byte alpha = r > g ? r : g;
                    if (b > alpha) alpha = b;
                    dst[o + 0] = b;
                    dst[o + 1] = g;
                    dst[o + 2] = r;
                    dst[o + 3] = alpha;
                }

                string leaf = textureEaid;
                int slash = leaf.LastIndexOf('/');
                if (slash >= 0) leaf = leaf.Substring(slash + 1);
                int dotIx = leaf.LastIndexOf('.');
                if (dotIx > 0) leaf = leaf.Substring(0, dotIx);
                foreach (char bad in Path.GetInvalidFileNameChars())
                    leaf = leaf.Replace(bad, '_');
                if (string.IsNullOrWhiteSpace(leaf)) leaf = "tex";

                string pngPath = Path.Combine(workDir, leaf + "_alpha.png");
                Directory.CreateDirectory(workDir);

                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                    w, h, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null,
                    dst, w * 4);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var fs = File.Create(pngPath))
                    encoder.Save(fs);

                warnings.Add(
                    $"Alpha-dither '{Path.GetFileName(textureEaid)}' -> derived " +
                    $"alpha from luminance ({w}x{h}) -> {Path.GetFileName(pngPath)}");
                return pngPath;
            }
            catch (Exception ex)
            {
                warnings.Add(
                    $"Luminance-alpha decode failed for '{textureEaid}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolves the mask-texture EAID for a material. Mask textures
        /// (typically charname_*_m.tif, BC5 with the mask in the R
        /// channel) drive per-pixel alpha test on character helmet /
        /// cloth / attachment shaders. Returns null when the material
        /// doesn't bind a mask, leaving the alpha-test branch inert.
        /// </summary>
        private static string TryResolveMaskMapEaid(string materialEaid)
        {
            if (string.IsNullOrEmpty(materialEaid)) return null;

            var cache = F1ModelLoader.ActiveTextureCache
                        ?? F1ModelLoaderClassic.ActiveTextureCache
                        ?? F1ModelLoaderLegacy.ActiveTextureCache;
            if (cache == null) return null;

            MaterialFile mat;
            try { mat = cache.GetMaterial(materialEaid); }
            catch { return null; }
            if (mat == null || mat.BindingsBySlot == null) return null;

            string[] preferred = { "me_maskMap", "me_maskTex" };
            foreach (var slot in preferred)
            {
                if (mat.BindingsBySlot.TryGetValue(slot, out var eaid) &&
                    !string.IsNullOrEmpty(eaid))
                    return eaid;
            }
            return null;
        }

        /// <summary>
        /// Composites a diffuse texture (RGB) with a separate mask texture
        /// (R channel = opacity) into a single RGBA PNG suitable as the
        /// material's diffuse + alpha source. The output filename mirrors
        /// the diffuse EAID's leaf with a "_masked" suffix so it doesn't
        /// collide with the plain-diffuse PNG that EnsureTexturePng wrote
        /// earlier in this material's setup.
        ///
        /// Both textures are decoded via the highest-resolution source
        /// available (hi-res sidecar where present, ERP tail otherwise).
        /// If they differ in resolution, the mask is nearest-neighbour
        /// resampled to the diffuse's dimensions -- masks are binary-ish
        /// (mostly 0 or 255 with thin edge transition) so bilinear
        /// resampling would soften edges that should stay crisp.
        ///
        /// Returns null on any decode / write failure -- the caller falls
        /// back to the un-masked diffuse PNG that was already attached
        /// before this helper ran.
        /// </summary>
        private static string TryWriteMaskedDiffusePng(
            string diffuseEaid, string maskEaid, string workDir, List<string> warnings)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(diffuseEaid) ||
                    string.IsNullOrWhiteSpace(maskEaid)) return null;

                // Reuse EnsureTexturePng to get the diffuse PNG path
                // first; we'll decode it back from disk to get fresh
                // pixels. This guarantees the diffuse uses the same
                // hi-res / ERP-tail ladder as every other diffuse in the
                // export (no risk of getting a different mip than what
                // a sibling material got).
                string diffusePng = EnsureTexturePng(
                    diffuseEaid, workDir, warnings, asNormalMap: false);
                if (string.IsNullOrEmpty(diffusePng) || !File.Exists(diffusePng))
                    return null;

                // Decode the just-written diffuse PNG back into BGRA so
                // we can mutate the alpha channel.
                System.Windows.Media.Imaging.BitmapSource diffuseBmp;
                using (var fs = File.OpenRead(diffusePng))
                {
                    var dec = new System.Windows.Media.Imaging.PngBitmapDecoder(
                        fs,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    diffuseBmp = dec.Frames[0];
                }
                int dw = diffuseBmp.PixelWidth;
                int dh = diffuseBmp.PixelHeight;

                // BitmapSource may return whatever pixel format the PNG
                // had; force a BGRA32 copy so the alpha plane lives in a
                // predictable spot for the composite loop below.
                var bgra32 = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    diffuseBmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                byte[] diffuseBgra = new byte[dw * dh * 4];
                bgra32.CopyPixels(diffuseBgra, dw * 4, 0);

                // Decode the mask: we want the R channel as opacity. The
                // existing decoders happen to put R in BGRA byte +2 for
                // BC5-as-grayscale-R, so reading the R byte from each
                // pixel of the mask's BGRA buffer gives us what we want
                // regardless of the underlying compression.
                var cache = F1ModelLoader.ActiveTextureCache
                            ?? F1ModelLoaderClassic.ActiveTextureCache
                            ?? F1ModelLoaderLegacy.ActiveTextureCache;
                if (cache == null) return null;

                DecodedTexture maskTex = null;
                try
                {
                    var hi = GenericMipmapsSidecarLoader.TryLoadTopMip(
                        maskEaid, cache.PackageRoot, _ => { });
                    if (hi != null && hi.Bgra != null && hi.Width > 0 && hi.Height > 0)
                        maskTex = hi;
                }
                catch { }
                if (maskTex == null)
                    maskTex = cache.GetPixelsForTexture(maskEaid);
                if (maskTex == null || maskTex.Bgra == null ||
                    maskTex.Width <= 0 || maskTex.Height <= 0)
                    return null;

                // Triviality check. The shared template masks
                // (charname_*_m.tif) often ship at 8x8 -- this is the
                // ERP-tail mip and the game streams the full-resolution
                // copy from a separate .mipmaps file at runtime. When
                // the mask we have IS the tail AND every pixel is
                // effectively opaque (R >= ~200), it's a "no cutout"
                // placeholder and applying it would just wash out the
                // diffuse for no reason. Skip the composite and let the
                // caller fall back to the un-masked diffuse.
                //
                // We deliberately do NOT skip on "small" alone -- if the
                // tail has genuine cutout regions (any pixel < ~50) the
                // composite stays useful even at 8x8, blocky alpha and
                // all. The discriminator is content, not resolution.
                {
                    int meaningfulCutoutPixels = 0;
                    int totalPixels = maskTex.Width * maskTex.Height;
                    for (int p = 0; p < totalPixels; p++)
                    {
                        // BGRA32: R byte at +2.
                        byte r = maskTex.Bgra[p * 4 + 2];
                        if (r < 200) meaningfulCutoutPixels++;
                    }
                    // < 1% of pixels show meaningful transparency → mask is
                    // effectively all-opaque; not worth applying.
                    if (meaningfulCutoutPixels * 100 < totalPixels)
                    {
                        warnings.Add(
                            $"Mask '{Path.GetFileName(maskEaid)}' " +
                            $"({maskTex.Width}x{maskTex.Height}) is effectively " +
                            $"all-opaque (only {meaningfulCutoutPixels}/{totalPixels} " +
                            $"pixels < 200); skipping composite.");
                        return null;
                    }
                }

                // Composite. The output alpha is the MULTIPLY blend of
                // the diffuse's existing alpha and the mask's R channel
                // (both in 8-bit 0..255 with 255 = fully opaque):
                //
                //   out_alpha = (diffuse_alpha * mask_R) / 255
                //
                // This preserves the diffuse's authored alpha (e.g.
                // glove-fray strands cut out via BC3 alpha) AND layers
                // the mask's per-pixel cutouts on top. Only regions
                // opaque in BOTH end up opaque in the composite.
                //
                // Previously the mask alpha REPLACED the diffuse alpha
                // outright, which dropped the existing transparency on
                // BC3-with-alpha textures like glove_frays_d.tif --
                // their wispy fray strands turned solid against the
                // mask, then re-cut by the mask which mostly didn't
                // overlap. Multiply-blend is the standard alpha-stack
                // operator and matches the game shader's behaviour.
                int mw = maskTex.Width;
                int mh = maskTex.Height;
                byte[] composited = new byte[diffuseBgra.Length];
                for (int y = 0; y < dh; y++)
                {
                    int my = (int)((long)y * mh / dh);
                    if (my >= mh) my = mh - 1;
                    for (int x = 0; x < dw; x++)
                    {
                        int mx = (int)((long)x * mw / dw);
                        if (mx >= mw) mx = mw - 1;
                        int dOff = (y * dw + x) * 4;
                        int mOff = (my * mw + mx) * 4;
                        composited[dOff + 0] = diffuseBgra[dOff + 0];
                        composited[dOff + 1] = diffuseBgra[dOff + 1];
                        composited[dOff + 2] = diffuseBgra[dOff + 2];
                        composited[dOff + 3] = (byte)(
                            (diffuseBgra[dOff + 3] * maskTex.Bgra[mOff + 2]) / 255);
                    }
                }

                // Derive output filename: diffuse leaf + "_masked".
                string leaf = diffuseEaid;
                int slash = leaf.LastIndexOf('/');
                if (slash >= 0) leaf = leaf.Substring(slash + 1);
                int dot = leaf.LastIndexOf('.');
                if (dot > 0) leaf = leaf.Substring(0, dot);
                foreach (char bad in Path.GetInvalidFileNameChars())
                    leaf = leaf.Replace(bad, '_');
                if (string.IsNullOrWhiteSpace(leaf)) leaf = "tex";
                string pngPath = Path.Combine(workDir, leaf + "_masked.png");

                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                    dw, dh, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null,
                    composited, dw * 4);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var fs = File.Create(pngPath))
                    encoder.Save(fs);

                warnings.Add(
                    $"Composite masked diffuse '{Path.GetFileName(diffusePng)}' " +
                    $"+ mask '{Path.GetFileName(maskEaid)}' ({mw}x{mh}) " +
                    $"-> {Path.GetFileName(pngPath)}");
                return pngPath;
            }
            catch (Exception ex)
            {
                warnings.Add(
                    $"Masked diffuse composite failed (diffuse='{diffuseEaid}', " +
                    $"mask='{maskEaid}'): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads the material's scalar opacity parameter (me_alpha) and
        /// returns it, or 1.0 if absent / unreadable. Only the
        /// char_visor_skinned and char_visor_tear_skinned shaders
        /// declare me_alpha in their cbuf -- on other shaders the value
        /// stays at the 1.0 default, leaving the caller's "if < 0.999"
        /// guard inert.
        ///
        /// Kept as a separate helper (instead of inlining a single
        /// Parameters.TryGetValue call) so future transparency-bearing
        /// shaders can be added in one place without touching the
        /// EnsureMaterial body.
        /// </summary>
        private static float TryResolveScalarAlphaParam(string materialEaid)
        {
            if (string.IsNullOrEmpty(materialEaid)) return 1.0f;

            var cache = F1ModelLoader.ActiveTextureCache
                        ?? F1ModelLoaderClassic.ActiveTextureCache
                        ?? F1ModelLoaderLegacy.ActiveTextureCache;
            if (cache == null) return 1.0f;

            MaterialFile mat;
            try { mat = cache.GetMaterial(materialEaid); }
            catch { return 1.0f; }
            if (mat == null || mat.Parameters == null) return 1.0f;

            // me_alpha is the canonical name on char_visor_skinned. Other
            // shaders that ship a scalar opacity (none observed yet, but
            // the 105-file corpus is small) can be added here.
            float? a = mat.GetFloat("me_alpha");
            if (a.HasValue && a.Value >= 0f && a.Value <= 1f)
                return a.Value;
            return 1.0f;
        }

        /// <summary>
        /// Look up the surface-map (PBR finish) texture EAID for a
        /// material. The "_s" texture encodes roughness / metallic /
        /// specular intensity per pixel; the game shader uses it to
        /// give per-pixel variation in glossiness on top of the
        /// material's uniform scalar IOR / roughness parameters.
        ///
        /// Tries each canonical sampler slot in priority order:
        ///   1. me_surfaceMap         -- character + general car shaders
        ///   2. me_surfaceTex         -- character/helmet (Tex-suffix variant)
        ///   3. me_paintSurfaceMap    -- vehicle_metallic_paint family
        ///   4. me_logoSurfaceMap     -- secondary surface map used by some
        ///                                paint shaders for the lacquer
        ///                                layer (e.g. ferrari_decal_s.tif)
        ///
        /// Returns null when no surface binding is present (mirrors / lights
        /// / interior materials don't carry one).
        /// </summary>
        /// <summary>
        /// Look up the logo / decal texture EAID for a material. Both
        /// vehicle_paint_v2 and vehicle_carbon bind their per-team
        /// decal layer through `me_logoMap` (the texture itself is
        /// usually named `<team>_decal_da.tif` -- the trailing `da`
        /// = diffuse + alpha, alpha being the decal mask). Returns
        /// null when the material has no logo binding so the export
        /// path skips a decal PNG cleanly. Also handles the
        /// `_logoSurfaceMap` (gloss/metallic-for-the-decal) which we
        /// don't currently consume but is documented here for
        /// completeness.
        /// </summary>
        private static string TryResolveLogoMapEaid(string materialEaid)
        {
            if (string.IsNullOrEmpty(materialEaid)) return null;

            var cache = F1ModelLoader.ActiveTextureCache
                        ?? F1ModelLoaderClassic.ActiveTextureCache
                        ?? F1ModelLoaderLegacy.ActiveTextureCache;
            if (cache == null) return null;

            MaterialFile mat;
            try { mat = cache.GetMaterial(materialEaid); }
            catch { return null; }
            if (mat == null || mat.BindingsBySlot == null) return null;

            string[] preferred = {
                "me_logoMap",
                "me_logoTex",
                "me_decalMap",
                "me_decalTex",
            };
            foreach (var slot in preferred)
            {
                if (mat.BindingsBySlot.TryGetValue(slot, out var eaid) &&
                    !string.IsNullOrEmpty(eaid))
                    return eaid;
            }
            return null;
        }

        /// <summary>
        /// Surface (PBR finish) map specifically for the decal layer.
        /// Stored under `me_logoSurfaceMap` -- the texture is usually
        /// `<team>_decal_s.tif` and follows the same R/G/B encoding as
        /// the base surface map (G=roughness, B=metallic). Returns null
        /// when the material has no per-decal surface; in that case the
        /// addon just leaves the underlying surface controlling the
        /// whole panel.
        /// </summary>
        private static string TryResolveLogoSurfaceMapEaid(string materialEaid)
        {
            if (string.IsNullOrEmpty(materialEaid)) return null;

            var cache = F1ModelLoader.ActiveTextureCache
                        ?? F1ModelLoaderClassic.ActiveTextureCache
                        ?? F1ModelLoaderLegacy.ActiveTextureCache;
            if (cache == null) return null;

            MaterialFile mat;
            try { mat = cache.GetMaterial(materialEaid); }
            catch { return null; }
            if (mat == null || mat.BindingsBySlot == null) return null;

            string[] preferred = {
                "me_logoSurfaceMap",
                "me_logoSurfaceTex",
                "me_decalSurfaceMap",
                "me_decalSurfaceTex",
            };
            foreach (var slot in preferred)
            {
                if (mat.BindingsBySlot.TryGetValue(slot, out var eaid) &&
                    !string.IsNullOrEmpty(eaid))
                    return eaid;
            }
            return null;
        }

        private static string TryResolveSurfaceMapEaid(string materialEaid)
        {
            if (string.IsNullOrEmpty(materialEaid)) return null;

            var cache = F1ModelLoader.ActiveTextureCache
                        ?? F1ModelLoaderClassic.ActiveTextureCache
                        ?? F1ModelLoaderLegacy.ActiveTextureCache;
            if (cache == null) return null;

            MaterialFile mat;
            try { mat = cache.GetMaterial(materialEaid); }
            catch { return null; }
            if (mat == null || mat.BindingsBySlot == null) return null;

            string[] preferred = {
                "me_surfaceMap",
                "me_surfaceTex",
                "me_paintSurfaceMap",
                "me_paintSurfaceTex",
                "me_logoSurfaceMap",
                "me_logoSurfaceTex",
            };
            foreach (var slot in preferred)
            {
                if (mat.BindingsBySlot.TryGetValue(slot, out var eaid) &&
                    !string.IsNullOrEmpty(eaid))
                    return eaid;
            }
            return null;
        }

        /// <summary>
        /// Look up the normal-map texture EAID for a material by querying
        /// the active TextureCache for the parsed material bindings. Tries
        /// each known sampler-slot name in priority order:
        ///   1. me_normalMap     -- car shaders (paint, carbon, detail, ...)
        ///   2. me_normalTex     -- character/helmet shaders
        ///   3. me_baseNormalMap -- variant naming
        ///   4. me_baseNormalTex -- character cloth (base layer normal)
        /// Returns null if the material has no normal-map binding (e.g.
        /// mirror materials, lcd screens). Caller treats null as "no normal
        /// map to add" and continues without it.
        /// </summary>
        private static string TryResolveNormalMapEaid(string materialEaid)
        {
            if (string.IsNullOrEmpty(materialEaid)) return null;

            var cache = F1ModelLoader.ActiveTextureCache
                        ?? F1ModelLoaderClassic.ActiveTextureCache
                        ?? F1ModelLoaderLegacy.ActiveTextureCache;
            if (cache == null) return null;

            MaterialFile mat;
            try { mat = cache.GetMaterial(materialEaid); }
            catch { return null; }
            if (mat == null || mat.BindingsBySlot == null) return null;

            string[] preferred = {
                "me_normalMap",
                "me_normalTex",
                "me_baseNormalMap",
                "me_baseNormalTex",
            };
            foreach (var slot in preferred)
            {
                if (mat.BindingsBySlot.TryGetValue(slot, out var eaid) &&
                    !string.IsNullOrEmpty(eaid))
                    return eaid;
            }
            return null;
        }

        /// <summary>
        /// Decodes <paramref name="textureEaid"/> into BGRA pixels and encodes
        /// the result as a PNG inside <paramref name="workDir"/>, returning
        /// the path of the resulting file. Results are memoised by EAID so
        /// multiple materials that bind the same texture share a single
        /// decode + a single PNG on disk. Returns null if no decoder yields
        /// a usable buffer, or the PNG encode throws.
        ///
        /// <para>Source order (highest resolution first):</para>
        /// <list type="number">
        ///   <item>
        ///     <description>
        ///       <see cref="MipmapsSidecarLoader.TryLoadTopMip"/> — the
        ///       streamed driver-atlas / team-textures sidecar that ships
        ///       next to the ERP on disk (<c>*_tif.mipmaps</c>). This is the
        ///       same path the WPF preview takes through
        ///       <see cref="HighResBrushProvider.WrapBrush"/>; calling it
        ///       here gives the exporter the full top-mip (e.g. 1024x512,
        ///       2048x1024) instead of the 64x32-class tail. CRITICAL for
        ///       classic content (2016–2018), where the ERP-embedded tail
        ///       is so coarse that interior decals dissolve into mush.
        ///     </description>
        ///   </item>
        ///   <item>
        ///     <description>
        ///       <see cref="GenericMipmapsSidecarLoader.TryLoadTopMip"/> —
        ///       the shared/generics flavour of the same sidecar format.
        ///       Different layout (BC1 raw-concat, no <c>temp002.image</c>
        ///       descriptor) so it lives in its own loader; semantics are
        ///       identical from the caller's perspective.
        ///     </description>
        ///   </item>
        ///   <item>
        ///     <description>
        ///       <see cref="TextureCache.GetPixelsForTexture"/> — the legacy
        ///       fallback. Decodes the tail mip embedded in the ERP itself.
        ///       Used when no on-disk install root is known
        ///       (<see cref="F1PackageLocator.Last"/> null), when the texture
        ///       has no shipped sidecar, or when both sidecar loaders fail
        ///       on a layout we haven't catalogued. Preserves the previous
        ///       gray-or-low-res behaviour rather than producing nothing.
        ///     </description>
        ///   </item>
        /// </list>
        ///
        /// <para>The sidecar loaders both consult
        /// <see cref="F1PackageLocator.Last"/> internally and silently
        /// return null when no install was located -- which is exactly the
        /// "stray ERP loaded outside its install" case where the ERP-tail
        /// fallback is the only source we have. Throws inside either
        /// loader are caught and logged into <paramref name="warnings"/>
        /// so a malformed sidecar can never abort the whole export.</para>
        /// </summary>
        private static string EnsureTexturePng(string textureEaid, string workDir, List<string> warnings)
            => EnsureTexturePng(textureEaid, workDir, warnings, asNormalMap: false);

        // Overload that lets the caller request normal-map encoding. Normal-
        // map BC5 textures must NOT go through the grayscale-R preview
        // decoder (which is what TextureCache.GetPixelsForTexture uses) --
        // that collapses the Y channel and produces a near-black PNG.
        // Instead this path re-reads the raw block-compressed bytes off
        // disk and decodes them via BlockCompressionDecoder.DecodeBC5As-
        // NormalMap so the output PNG has the canonical (R=X, G=Y, B=Z)
        // tangent-space encoding Blender's principled BSDF expects.
        private static string EnsureTexturePng(string textureEaid, string workDir,
                                               List<string> warnings, bool asNormalMap)
        {
            // Cache key splits by normal-vs-diffuse so the same EAID can
            // legitimately produce a different PNG depending on requested
            // encoding. Without this, the first caller would win the cache
            // and a later normal-map request would get a stale grayscale.
            string cacheKey = asNormalMap ? textureEaid + "|normal" : textureEaid;
            if (_textureEaidToPngPath.TryGetValue(cacheKey, out var cached))
                return cached;

            // For normal maps, try a direct re-decode path first that
            // preserves the BC5 X/Y channels and reconstructs Z. Falls
            // through to the legacy ladder below if that fails -- we'd
            // rather ship a grayscale-R PNG than skip the normal map
            // entirely.
            if (asNormalMap)
            {
                string p = TryWriteNormalMapPng(textureEaid, workDir, warnings);
                if (!string.IsNullOrEmpty(p))
                {
                    _textureEaidToPngPath[cacheKey] = p;
                    return p;
                }
            }

            try
            {
                // Pick whichever loader's TextureCache is currently
                // populated. Order: modern F1ModelLoader (2021+, including
                // F1 25 helmets) first, then F1ModelLoaderClassic (2016-2018),
                // then F1ModelLoaderLegacy (2019-2020). Without the modern
                // entry, helmet exports see cache==null and silently skip
                // the PNG decode, shipping the FBX textureless.
                var cache = F1ModelLoader.ActiveTextureCache
                            ?? F1ModelLoaderClassic.ActiveTextureCache
                            ?? F1ModelLoaderLegacy.ActiveTextureCache;
                if (cache == null)
                {
                    _textureEaidToPngPath[textureEaid] = null;
                    return null;
                }

                // Resolve the highest-resolution decoded buffer we can
                // obtain for this EAID. See the XML doc above for why this
                // ladder exists and the contract each step preserves.
                DecodedTexture decoded = null;
                string sourceLabel = null;

                // Step 1: streamed driver-atlas / team-textures sidecar.
                try
                {
                    var hi = MipmapsSidecarLoader.TryLoadTopMip(
                        textureEaid, cache.PackageRoot, _ => { });
                    if (hi != null && hi.Bgra != null && hi.Bgra.Length > 0 &&
                        hi.Width > 0 && hi.Height > 0)
                    {
                        decoded = hi;
                        sourceLabel = "MipmapsSidecarLoader (" +
                                      (hi.FormatName ?? "BC?") + ", " +
                                      hi.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                                      hi.Height.ToString(CultureInfo.InvariantCulture) + ")";
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add(
                        "MipmapsSidecarLoader threw for '" + textureEaid +
                        "': " + ex.Message + " -- trying next source");
                }

                // Step 2: shared/generics flavour of the sidecar.
                if (decoded == null)
                {
                    try
                    {
                        var hi = GenericMipmapsSidecarLoader.TryLoadTopMip(
                            textureEaid, cache.PackageRoot, _ => { });
                        if (hi != null && hi.Bgra != null && hi.Bgra.Length > 0 &&
                            hi.Width > 0 && hi.Height > 0)
                        {
                            decoded = hi;
                            sourceLabel = "GenericMipmapsSidecarLoader (" +
                                          (hi.FormatName ?? "BC?") + ", " +
                                          hi.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                                          hi.Height.ToString(CultureInfo.InvariantCulture) + ")";
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add(
                            "GenericMipmapsSidecarLoader threw for '" + textureEaid +
                            "': " + ex.Message + " -- falling back to ERP tail");
                    }
                }

                // Step 2.5: direct BC5 raw decode from the temp001.image
                // payload. The general cache path (cache.GetPixelsForTexture,
                // step 3) routes BC5 through DecodeBC5AsGrayscaleR -- a
                // decoder tuned for tyre-tread previews that clamps the R
                // channel into a narrow 14..27 band to look like rubber.
                // That's catastrophic for character surface (_s) and mask
                // (_m) textures where the actual roughness / opacity
                // values matter (the helmet_s on Hulkenberg came out as
                // flat dark grey). Decode raw here ourselves before
                // falling through to the cache.
                if (decoded == null)
                {
                    try
                    {
                        var (_, metaPath, payloadPath) =
                            EaidPathResolver.ResolveTexturePaths(textureEaid, cache.PackageRoot);
                        if (metaPath != null && File.Exists(metaPath) &&
                            payloadPath != null && File.Exists(payloadPath))
                        {
                            var desc = ImageDescriptor.FromFile(metaPath);
                            if (desc.IsBC5 && desc.Width > 0 && desc.Height > 0)
                            {
                                byte[] payload = File.ReadAllBytes(payloadPath);
                                if (payload.Length > 0)
                                {
                                    byte[] bgra = BlockCompressionDecoder.DecodeBC5AsRawR(
                                        payload, desc.Width, desc.Height);
                                    if (bgra != null && bgra.Length > 0)
                                    {
                                        decoded = new DecodedTexture
                                        {
                                            Bgra = bgra,
                                            Width = desc.Width,
                                            Height = desc.Height,
                                            FormatName = "BC5/raw-R",
                                        };
                                        sourceLabel = $"ERP tail (BC5/raw-R, " +
                                                      $"{desc.Width}x{desc.Height})";
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add(
                            $"BC5 raw decode threw for '{textureEaid}': {ex.Message} " +
                            "-- falling back to cache");
                    }
                }

                // Step 3: ERP-embedded tail mip (legacy behaviour, used
                // for non-BC5 textures).
                if (decoded == null)
                {
                    var lo = cache.GetPixelsForTexture(textureEaid);
                    if (lo != null && lo.Bgra != null && lo.Bgra.Length > 0 &&
                        lo.Width > 0 && lo.Height > 0)
                    {
                        decoded = lo;
                        sourceLabel = "ERP tail (" +
                                      (lo.FormatName ?? "BC?") + ", " +
                                      lo.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                                      lo.Height.ToString(CultureInfo.InvariantCulture) + ")";
                    }
                }

                if (decoded == null)
                {
                    _textureEaidToPngPath[textureEaid] = null;
                    return null;
                }

                // Build safe filename from the EAID leaf.
                string leaf = textureEaid;
                int slash = leaf.LastIndexOf('/');
                if (slash >= 0) leaf = leaf.Substring(slash + 1);
                int dot = leaf.LastIndexOf('.');
                if (dot > 0) leaf = leaf.Substring(0, dot);
                foreach (char bad in System.IO.Path.GetInvalidFileNameChars())
                    leaf = leaf.Replace(bad, '_');
                if (string.IsNullOrWhiteSpace(leaf)) leaf = "tex";

                string pngPath = System.IO.Path.Combine(workDir, leaf + ".png");

                // Ensure dir exists.
                System.IO.Directory.CreateDirectory(workDir);

                // Convert BGRA -> BGRA32 WPF BitmapSource and encode as PNG.
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                    decoded.Width,
                    decoded.Height,
                    96, 96,
                    System.Windows.Media.PixelFormats.Bgra32,
                    null,
                    decoded.Bgra,
                    decoded.Width * 4);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var fs = System.IO.File.Create(pngPath))
                {
                    encoder.Save(fs);
                }

                // Audit breadcrumb so the .diag.txt records which source
                // each texture actually came from. Useful for verifying
                // that classic-car exports are picking up the high-res
                // sidecar rather than silently degrading to the tail.
                if (sourceLabel != null)
                {
                    warnings.Add(
                        "Texture '" + textureEaid + "' decoded from " + sourceLabel +
                        " -> " + System.IO.Path.GetFileName(pngPath));
                }

                _textureEaidToPngPath[textureEaid] = pngPath;
                return pngPath;
            }
            catch (Exception ex)
            {
                warnings.Add($"Texture decode/encode failed for '{textureEaid}': {ex.Message}");
                _textureEaidToPngPath[textureEaid] = null;
                return null;
            }
        }

        /// <summary>
        /// Decodes a BC5 (or BC3-via-RGB) tangent-space normal map into a
        /// proper RGB PNG suitable for Blender / Maya import. Unlike
        /// EnsureTexturePng's standard path (which routes BC5 textures
        /// through the grayscale-R preview decoder used for the WPF
        /// viewport), this re-reads the raw block-compressed payload off
        /// disk and decodes the X / Y channels into R / G with Z
        /// reconstructed in B via the canonical normal-vector formula.
        ///
        /// Returns the written PNG path, or null on any failure (missing
        /// texture descriptor, format we don't recognise, decode threw).
        /// On null, EnsureTexturePng falls back to the grayscale path
        /// rather than dropping the normal map entirely -- a grayscale
        /// normal map is wrong but at least leaves a sidecar the user
        /// can swap manually.
        /// </summary>
        private static string TryWriteNormalMapPng(
            string textureEaid, string workDir, List<string> warnings)
        {
            try
            {
                var cache = F1ModelLoader.ActiveTextureCache
                            ?? F1ModelLoaderClassic.ActiveTextureCache
                            ?? F1ModelLoaderLegacy.ActiveTextureCache;
                if (cache == null) return null;

                // Resolution ladder (high → low). For normal maps we want
                // the highest-resolution source we can find:
                //   1. GenericMipmapsSidecarLoader -- pulls the streamed
                //      .mipmaps top-mip via the descriptor-driven
                //      (temp002.image) path. Now BC5-aware (2026), so
                //      we get the full-res hi-res normal map when the
                //      game install ships one. This is the same path
                //      the diffuse normal exporter uses, just routed
                //      through DecodeBC5AsNormalMap on the loader side.
                //   2. ERP tail (temp001.image + temp000.image) -- the
                //      low-res copy embedded in the ERP itself. Always
                //      present, but only ~64x64 for normal maps.
                //
                // Both produce the same RGBA bytes (R=X, G=Y, B=Z) so the
                // PNG writer below doesn't care which source won.
                DecodedTexture decoded = null;
                string sourceLabel = null;

                try
                {
                    var hi = GenericMipmapsSidecarLoader.TryLoadTopMip(
                        textureEaid, cache.PackageRoot, _ => { },
                        GenericMipmapsSidecarLoader.BC5DecodeMode.NormalMap);
                    if (hi != null && hi.Bgra != null && hi.Bgra.Length > 0 &&
                        hi.Width > 0 && hi.Height > 0)
                    {
                        decoded = hi;
                        sourceLabel = $"hi-res sidecar ({hi.Width}x{hi.Height})";
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add(
                        $"Normal-map sidecar lookup threw for '{textureEaid}': {ex.Message} " +
                        "-- falling back to ERP tail");
                }

                if (decoded == null)
                {
                    // ERP-tail fallback: re-decode the temp001.image payload
                    // ourselves to avoid the cache's grayscale-R preview
                    // decoder.
                    var (_, metaPath, payloadPath) =
                        EaidPathResolver.ResolveTexturePaths(textureEaid, cache.PackageRoot);
                    if (metaPath == null || !File.Exists(metaPath) ||
                        payloadPath == null || !File.Exists(payloadPath))
                        return null;

                    ImageDescriptor desc;
                    try { desc = ImageDescriptor.FromFile(metaPath); }
                    catch { return null; }
                    if (!desc.IsBC5) return null;
                    if (desc.Width <= 0 || desc.Height <= 0) return null;

                    byte[] payload = File.ReadAllBytes(payloadPath);
                    if (payload.Length == 0) return null;

                    byte[] bgraTail = BlockCompressionDecoder.DecodeBC5AsNormalMap(
                        payload, desc.Width, desc.Height);
                    if (bgraTail == null || bgraTail.Length == 0) return null;

                    decoded = new DecodedTexture
                    {
                        Bgra = bgraTail,
                        Width = desc.Width,
                        Height = desc.Height,
                        FormatName = "BC5",
                    };
                    sourceLabel = $"ERP tail ({desc.Width}x{desc.Height})";
                }

                byte[] bgra = decoded.Bgra;

                // Derive output filename from the EAID's leaf (same
                // convention as the diffuse path -- e.g. "cf_pattern_1_n").
                string leaf = textureEaid ?? "tex";
                int slash = leaf.LastIndexOf('/');
                if (slash >= 0) leaf = leaf.Substring(slash + 1);
                int dot = leaf.LastIndexOf('.');
                if (dot > 0) leaf = leaf.Substring(0, dot);
                foreach (char bad in Path.GetInvalidFileNameChars())
                    leaf = leaf.Replace(bad, '_');
                if (string.IsNullOrWhiteSpace(leaf)) leaf = "tex_n";

                string pngPath = Path.Combine(workDir, leaf + ".png");
                Directory.CreateDirectory(workDir);

                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                    decoded.Width, decoded.Height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null,
                    bgra, decoded.Width * 4);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var fs = File.Create(pngPath))
                    encoder.Save(fs);

                warnings.Add(
                    $"Normal-map '{textureEaid}' decoded as BC5 tangent-space " +
                    $"from {sourceLabel} -> {Path.GetFileName(pngPath)}");
                return pngPath;
            }
            catch (Exception ex)
            {
                warnings.Add(
                    $"Normal-map decode failed for '{textureEaid}': {ex.Message} " +
                    "-- falling back to grayscale path");
                return null;
            }
        }

        /// <summary>
        /// Creates (once per scene) a single zero-area triangle whose three
        /// vertices all sit at the local origin, registers it in
        /// <c>scene.Meshes</c>, and returns its index. Every empty leaf node
        /// later shares this mesh via <see cref="AttachKeepAliveToEmptyLeaves"/>
        /// so Assimp's glTF2 writer doesn't prune them during serialisation.
        ///
        /// The mesh carries its own distinctive name ("__node_keepalive__")
        /// and a matching material name so modders can filter / delete it
        /// with one script pass after opening the .gltf. Per-vertex normals
        /// are emitted on the keep-alive as well, keeping it consistent with
        /// the rest of the exporter's no-STL-surfaces contract -- not that
        /// a zero-area triangle is ever shaded, but leaving normals off
        /// would trip downstream glTF validators that expect every mesh
        /// to carry the same accessor set.
        /// </summary>
        private static int EnsureKeepAliveMesh(Scene scene, int defaultMaterialIndex)
        {
            const string keepAliveMeshName = "__node_keepalive__";

            for (int i = 0; i < scene.Meshes.Count; i++)
            {
                if (string.Equals(scene.Meshes[i].Name, keepAliveMeshName, StringComparison.Ordinal))
                    return i;
            }

            var m = new Assimp.Mesh(keepAliveMeshName, PrimitiveType.Triangle);

            m.Vertices.Add(new Assimp.Vector3D(0f, 0f, 0f));
            m.Vertices.Add(new Assimp.Vector3D(0f, 0f, 0f));
            m.Vertices.Add(new Assimp.Vector3D(0f, 0f, 0f));

            m.Normals.Add(new Assimp.Vector3D(0f, 1f, 0f));
            m.Normals.Add(new Assimp.Vector3D(0f, 1f, 0f));
            m.Normals.Add(new Assimp.Vector3D(0f, 1f, 0f));

            var face = new Face();
            face.Indices.Add(0);
            face.Indices.Add(1);
            face.Indices.Add(2);
            m.Faces.Add(face);

            m.MaterialIndex = defaultMaterialIndex;

            int newIndex = scene.Meshes.Count;
            scene.Meshes.Add(m);
            return newIndex;
        }

        /// <summary>
        /// Depth-first walk of the Assimp scene graph. For every node that
        /// has neither children nor mesh references, appends
        /// <paramref name="keepAliveMeshIndex"/> to its
        /// <c>MeshIndices</c>. Interior empty nodes are left alone because
        /// Assimp preserves them naturally via their child pointers.
        ///
        /// Returns the number of leaves that were given the keep-alive
        /// mesh, which we write into the diagnostics file so any change
        /// in leaf-preservation behaviour (new game year, different team
        /// layout, etc.) is auditable after the fact.
        /// </summary>
        /// <summary>
        /// Inverse of <see cref="AttachKeepAliveToEmptyLeaves"/>. Recursively
        /// removes nodes that contribute no geometry to the scene -- either
        /// directly (no <c>MeshIndices</c>) or transitively (no descendant
        /// carries any either). Returns the number of pruned subtree roots.
        ///
        /// Used for helmet export, where the IDF render graph references a
        /// shared body-template skeleton with placeholders for every helmet
        /// brand, harness variant, HANS device and hands; only the loaded
        /// asset's own subtree has real meshes. Without pruning those
        /// placeholders surface in Blender as visible empty nodes.
        /// </summary>
        private static int PruneEmptyBranches(Node node)
        {
            if (node?.Children == null) return 0;
            int pruned = 0;
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                var child = node.Children[i];
                pruned += PruneEmptyBranches(child);
                if (!HasAnyMeshInSubtree(child))
                {
                    node.Children.RemoveAt(i);
                    pruned++;
                }
            }
            return pruned;
        }

        private static bool HasAnyMeshInSubtree(Node n)
        {
            if (n == null) return false;
            if (n.MeshIndices != null && n.MeshIndices.Count > 0) return true;
            if (n.Children != null)
            {
                for (int i = 0; i < n.Children.Count; i++)
                    if (HasAnyMeshInSubtree(n.Children[i])) return true;
            }
            return false;
        }

        private static int AttachKeepAliveToEmptyLeaves(Node node, int keepAliveMeshIndex)
        {
            if (node == null)
                return 0;

            int count = 0;

            bool hasChildren = node.Children != null && node.Children.Count > 0;
            bool hasMesh = node.MeshIndices != null && node.MeshIndices.Count > 0;

            if (!hasChildren && !hasMesh)
            {
                node.MeshIndices.Add(keepAliveMeshIndex);
                count++;
            }
            else if (hasChildren)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    count += AttachKeepAliveToEmptyLeaves(node.Children[i], keepAliveMeshIndex);
            }

            return count;
        }

        internal static List<int> DetectAvailableLods(string folder)
        {
            var result = new HashSet<int>();
            var rx = new Regex(@"lod=(\d+)", RegexOptions.IgnoreCase);

            foreach (var f in Directory.GetFiles(folder, "*temp000*mesh*", SearchOption.TopDirectoryOnly))
            {
                var m = rx.Match(f);
                if (!m.Success)
                    continue;

                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lod))
                    result.Add(lod);
            }

            var list = result.ToList();
            list.Sort();
            return list;
        }

        internal static List<NodeInfo> TryParseNodeGraph(string folder)
        {
            try
            {
                string renderFile = FindMainRenderFile(folder);
                if (string.IsNullOrWhiteSpace(renderFile) || !File.Exists(renderFile))
                    return new List<NodeInfo>();

                byte[] data = File.ReadAllBytes(renderFile);
                if (data.Length < 0x0C)
                    return new List<NodeInfo>();

                int nodeCount = ReadU16(data, 0);
                int tableCount = ReadU16(data, 2);

                const int nodesStart = 0x0C;
                const int nodeStride = 120;

                int stringPoolStart = nodesStart + (nodeCount * nodeStride) + (tableCount * 2);
                if (stringPoolStart < 0 || stringPoolStart >= data.Length)
                    return new List<NodeInfo>();

                int nodesBytes = nodeCount * nodeStride;
                if (nodesStart + nodesBytes > data.Length)
                    return new List<NodeInfo>();

                var nodes = new List<NodeInfo>(nodeCount);

                for (int i = 0; i < nodeCount; i++)
                {
                    int off = nodesStart + (i * nodeStride);

                    uint nameOfs = ReadU32(data, off + 0x00);
                    int parent = ReadI32(data, off + 0x04);

                    float px = ReadF32(data, off + 0x08);
                    float py = ReadF32(data, off + 0x0C);
                    float pz = ReadF32(data, off + 0x10);

                    float qx = ReadF32(data, off + 0x14);
                    float qy = ReadF32(data, off + 0x18);
                    float qz = ReadF32(data, off + 0x1C);
                    float qw = ReadF32(data, off + 0x20);

                    float sx = ReadF32(data, off + 0x24);
                    float sy = ReadF32(data, off + 0x28);
                    float sz = ReadF32(data, off + 0x2C);

                    int kind = (int)ReadU32(data, off + 0x70);

                    string name = ReadCString(data, stringPoolStart + (int)nameOfs, 256);

                    nodes.Add(new NodeInfo
                    {
                        Index = i,
                        ParentIndex = parent,
                        Name = name ?? "",
                        Tx = px,
                        Ty = py,
                        Tz = pz,
                        Qx = qx,
                        Qy = qy,
                        Qz = qz,
                        Qw = qw,
                        Sx = sx,
                        Sy = sy,
                        Sz = sz,
                        Kind = kind
                    });
                }

                return nodes;
            }
            catch
            {
                return new List<NodeInfo>();
            }
        }

        /// <summary>
        /// Combines a part name and slot name into a single glTF mesh-name
        /// token that stays unique per (part, slot) pair. Whitespace in
        /// the slot (for example "carbon tile 1") is collapsed to
        /// underscores ("carbon_tile_1") so the resulting identifier is
        /// safe to use as a glTF node / mesh name and so downstream DCC
        /// tools that outline by mesh-name (Blender's outliner in
        /// particular) display one distinct entry per submesh rather
        /// than collapsing several same-named siblings together.
        ///
        /// Part and slot are both lowered slightly -- we don't force
        /// casing, but we strip newlines / tabs and trim whitespace
        /// defensively so a rogue control char in either can't smuggle
        /// into the glTF's text. Empty inputs fall back to "part" /
        /// "slot" so the final name is always valid.
        /// </summary>
        private static string BuildPrimitiveMeshName(string partName, string slotName)
        {
            string p = (partName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(p)) p = "part";

            string s = (slotName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) s = "slot";

            // Replace any whitespace run with a single underscore so
            // "carbon tile 1" -> "carbon_tile_1". Strip control chars.
            var sb = new System.Text.StringBuilder(p.Length + 1 + s.Length);
            sb.Append(p);
            sb.Append('_');
            bool lastWasSpace = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace)
                    {
                        sb.Append('_');
                        lastWasSpace = true;
                    }
                }
                else if (c < 0x20 || c == 0x7F)
                {
                    // skip control char
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            return sb.ToString();
        }

        internal static List<MeshGeometry3D> EnumerateGeometry(Model3D model)
        {
            var list = new List<MeshGeometry3D>();
            if (model == null)
                return list;

            if (model is GeometryModel3D gm && gm.Geometry is MeshGeometry3D mg)
                list.Add(mg);

            if (model is Model3DGroup g)
            {
                foreach (var c in g.Children)
                    list.AddRange(EnumerateGeometry(c));
            }

            return list;
        }

        // Heuristic: does this mesh name belong to a decal / overlay layer
        // that's known to coincide with a base body mesh? The exporter's
        // own upstream passes (GroupPartModelGeometryBySlot, BuildPrimitive-
        // MeshName, TryResolveLoadedPartNameFromModel) drive the suffix
        // conventions; this is the single point of truth for "should I
        // apply the z-fight offset to this mesh?"
        private static bool IsDecalLikeMeshName(string meshName)
        {
            if (string.IsNullOrEmpty(meshName)) return false;
            // Strip a numeric primitive-index suffix like "_3" Assimp can
            // append to disambiguate primitives sharing a name. Match the
            // semantic suffix that lives just before that.
            string m = meshName;
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
                || m.EndsWith("_logos", StringComparison.OrdinalIgnoreCase);
        }

        private static Assimp.Mesh ConvertToAssimpMesh(
            MeshGeometry3D wpf,
            string meshName,
            System.Windows.Media.PointCollection decalUv = null,
            System.Windows.Media.PointCollection damageUv = null)
        {
            var a = new Assimp.Mesh(meshName, PrimitiveType.Triangle);

            int vCount = wpf.Positions?.Count ?? 0;
            if (vCount == 0)
                return a;

            // Positions straight through.
            for (int i = 0; i < vCount; i++)
            {
                var p = wpf.Positions[i];
                a.Vertices.Add(new Assimp.Vector3D((float)p.X, (float)p.Y, (float)p.Z));
            }

            // Triangles. We collect the accepted-index triples into a reusable
            // list so that if we have to compute smooth normals ourselves
            // below, we do it over the EXACT same face set Assimp sees --
            // no chance of a normal being influenced by a triangle that was
            // rejected for out-of-range indexing.
            int idxCount = wpf.TriangleIndices?.Count ?? 0;
            var acceptedTris = new List<(int i0, int i1, int i2)>(idxCount / 3);
            for (int i = 0; i + 2 < idxCount; i += 3)
            {
                int i0 = wpf.TriangleIndices[i + 0];
                int i1 = wpf.TriangleIndices[i + 1];
                int i2 = wpf.TriangleIndices[i + 2];

                if ((uint)i0 >= (uint)vCount || (uint)i1 >= (uint)vCount || (uint)i2 >= (uint)vCount)
                    continue;

                acceptedTris.Add((i0, i1, i2));

                var face = new Face();
                face.Indices.Add(i0);
                face.Indices.Add(i1);
                face.Indices.Add(i2);
                a.Faces.Add(face);
            }

            // Normals: ALWAYS emit, even when the loader didn't supply them.
            // A glTF primitive without a NORMAL accessor forces every
            // downstream client (Blender, 3ds Max, Maya, glTF-Viewer, Three.js,
            // etc.) to synthesise flat per-face normals at draw time, which
            // is exactly the faceted / "STL-like" look we want to avoid for
            // modding work. Prefer the loader's per-vertex normals when
            // available; otherwise compute our own smooth ones from the
            // accepted-triangle set.
            bool hasLoaderNormals = wpf.Normals != null && wpf.Normals.Count == vCount;
            if (hasLoaderNormals)
            {
                for (int i = 0; i < vCount; i++)
                {
                    var n = wpf.Normals[i];
                    a.Normals.Add(new Assimp.Vector3D((float)n.X, (float)n.Y, (float)n.Z));
                }
            }
            else
            {
                EmitComputedSmoothNormals(a, wpf.Positions, acceptedTris, vCount);
            }

            // Decal z-fighting fix. Recognise decal / overlay meshes by the
            // mesh-name suffix produced upstream by GroupPartModelGeometry-
            // BySlot (which renames overlay slots from "<slot>_overlay" to
            // "<slot>_decal" before the mesh-name is built) and by the raw
            // "_overlay" suffix used on the per-IDF branch that still emits
            // the overlay as its own mesh. For every flagged mesh, shift
            // each vertex outward along its (now-final) normal by a small
            // fixed offset so the decal sits a hair in front of the base
            // body in viewport / render. Normals themselves stay unit-
            // length and unmodified -- only positions move.
            if (OffsetDecalsToFixZFighting && DecalOffsetMeters > 0f && IsDecalLikeMeshName(meshName))
            {
                float offs = DecalOffsetMeters;
                for (int i = 0; i < a.Vertices.Count && i < a.Normals.Count; i++)
                {
                    var n = a.Normals[i];
                    float nx = n.X, ny = n.Y, nz = n.Z;
                    // Re-normalise defensively; computed-smooth-normals are
                    // unit-length but loader-supplied normals occasionally
                    // arrive un-normalised.
                    float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 1e-6f)
                    {
                        nx /= len; ny /= len; nz /= len;
                    }
                    var v = a.Vertices[i];
                    a.Vertices[i] = new Assimp.Vector3D(
                        v.X + nx * offs,
                        v.Y + ny * offs,
                        v.Z + nz * offs);
                }
            }

            // UV0 = paint atlas UV (TEXCOORD_0). The Classic loader emits
            // the base submesh with its primary UV here.
            //
            // V-FLIP ON EXPORT. WPF, DirectX, and the glTF 2.0 spec all
            // use (0, 0) at the TOP-LEFT of the texture. Assimp however
            // uses OpenGL's convention internally, (0, 0) at BOTTOM-LEFT,
            // and its glTF2 exporter converts from internal-to-glTF by
            // writing out `1 - v` for every vertex. That's correct when
            // the caller feeds Assimp UVs in OpenGL convention, but we
            // feed WPF UVs (DirectX / glTF native), so the exporter's
            // auto-flip puts every vertex on the wrong island of its
            // texture atlas -- a vertex authored at V=0.915 ends up
            // sampling V=0.085 of the image, "textures placed totally
            // differently" as the modder reported (2026-04-21).
            //
            // Pre-flip V here so Assimp's flip-on-write cancels out
            // and the exported glTF carries the same V values the
            // viewport and the source VBs have. Verified by byte-
            // comparing vb=3's pair-1 UVs against the glTF accessor
            // output: without this pre-flip every row satisfies
            // exported_v == 1 - source_v exactly; with the pre-flip
            // they match source_v.
            bool hasUV0 = wpf.TextureCoordinates != null && wpf.TextureCoordinates.Count == vCount;
            if (hasUV0)
            {
                a.TextureCoordinateChannels[0] = new List<Assimp.Vector3D>(vCount);
                for (int i = 0; i < vCount; i++)
                {
                    var uv = wpf.TextureCoordinates[i];
                    a.TextureCoordinateChannels[0].Add(new Assimp.Vector3D(
                        (float)uv.X,
                        1.0f - (float)uv.Y,
                        0f));
                }
                a.UVComponentCount[0] = 2;
            }

            // UV1 = sponsor decal atlas UV (TEXCOORD_1). Populated only
            // when GroupPartModelGeometryBySlot paired this base mesh
            // with an overlay sibling carrying the decal atlas UV in
            // its TEXCOORD_0. The pair-up requires identical vertex
            // counts (verified caller-side) so we can write index-by-
            // index here with no remapping. Same V-flip as UV0 -- the
            // overlay's UV came from the same WPF/DirectX origin and
            // needs the same Assimp pre-flip to round-trip cleanly.
            //
            // Downstream: ClassicIdfWriter v11.41+ already reads
            // TEXCOORD_1 via SubMesh.Uvs1 and writes it into vb=3
            // halves (2, 3) -- F1 2018's body shader's decal atlas
            // sample. So a body primitive emitted here with
            // TEXCOORD_0 + TEXCOORD_1 lands in-game with paint AND
            // decals composited correctly through the body shader,
            // no separate decal primitive required.
            if (decalUv != null && decalUv.Count == vCount)
            {
                a.TextureCoordinateChannels[1] = new List<Assimp.Vector3D>(vCount);
                for (int i = 0; i < vCount; i++)
                {
                    var uv = decalUv[i];
                    a.TextureCoordinateChannels[1].Add(new Assimp.Vector3D(
                        (float)uv.X,
                        1.0f - (float)uv.Y,
                        0f));
                }
                a.UVComponentCount[1] = 2;
            }

            // UV2 = damage atlas UV (TEXCOORD_2). Round-trips stock IDF
            // vb=4 -- the channel mclaren_main_damage.tif is sampled
            // with by both vehicle_metallic_paint.nefx2 (body) and
            // vehicle_carbon.nefx2 (carbon) shaders.
            //
            // F1ModelLoaderClassic decodes stock vb=4 (stride-4 half2)
            // into a per-mesh PointCollection registered against the
            // MeshGeometry3D. Caller looks it up via
            // F1ModelLoaderClassic.GetMeshDamageUvs(src) and passes it
            // here; we emit it as TEXCOORD_2 with the same V-flip
            // applied as UV0/UV1 so Assimp's flip-on-write cancels and
            // the glTF accessor carries the source values verbatim.
            //
            // Downstream: GltfLodFolderImporter reads TEXCOORD_2 into
            // SubMesh.Uvs2; ClassicIdfWriter2018 v11.50+ routes it
            // back into vb=4. Stock parts then round-trip the damage
            // channel byte-identically; modders authoring a third UV
            // map in Blender for new geometry get explicit control of
            // damage-atlas placement on their custom parts.
            //
            // Body / number / generic stock vbuf=4 has per-vertex
            // authored values with no closed-form relation to UV0 --
            // this channel is the only path that preserves them.
            if (damageUv != null && damageUv.Count == vCount)
            {
                a.TextureCoordinateChannels[2] = new List<Assimp.Vector3D>(vCount);
                for (int i = 0; i < vCount; i++)
                {
                    var uv = damageUv[i];
                    a.TextureCoordinateChannels[2].Add(new Assimp.Vector3D(
                        (float)uv.X,
                        1.0f - (float)uv.Y,
                        0f));
                }
                a.UVComponentCount[2] = 2;
            }

            CompactMeshDropLooseVertices(a);
            return a;
        }

        /// <summary>
        /// Removes vertices not referenced by any face and rebuilds the
        /// face indices in lock-step. Defends against Blender 3.5's
        /// glTF exporter, which crashes with
        /// `IndexError: index N is out of bounds for axis 0 with size N`
        /// in <c>gltf2_blender_gather_primitives_extract.py</c>
        /// when an imported primitive carries vertices that aren't
        /// referenced by any loop -- the exporter's loop accounting
        /// then walks one slot off the end of its <c>dots</c> array.
        ///
        /// Loose vertices arise naturally in our pipeline: the index-
        /// range filter inside <see cref="ConvertToAssimpMesh"/> drops
        /// triangles whose three indices aren't all in [0, vCount), and
        /// any vertex referenced exclusively by such a dropped triangle
        /// is now orphaned. They render harmlessly in viewers but
        /// trigger the Blender bug on round-trip export, so we strip
        /// them before handing the mesh to Assimp.
        ///
        /// Also enforces stream-length parity: Normals and every UV
        /// channel must have exactly Vertices.Count entries afterwards;
        /// any drift is corrected by trimming or extending with a
        /// neutral default.
        /// </summary>
        private static void CompactMeshDropLooseVertices(Assimp.Mesh m)
        {
            if (m == null) return;
            int vCount = m.Vertices?.Count ?? 0;
            if (vCount == 0) return;
            int fCount = m.Faces?.Count ?? 0;
            if (fCount == 0) return;

            // Pass 1: mark referenced vertices.
            var used = new bool[vCount];
            for (int f = 0; f < fCount; f++)
            {
                var face = m.Faces[f];
                if (face == null) continue;
                for (int k = 0; k < face.IndexCount; k++)
                {
                    int idx = face.Indices[k];
                    if ((uint)idx < (uint)vCount) used[idx] = true;
                }
            }

            // Fast path: every vertex is referenced -- nothing to compact,
            // just enforce stream-length parity and bail.
            int usedCount = 0;
            for (int i = 0; i < vCount; i++) if (used[i]) usedCount++;
            if (usedCount == vCount)
            {
                EnforceStreamLengthParity(m, vCount);
                return;
            }

            // Pass 2: build old->new index map.
            var remap = new int[vCount];
            int next = 0;
            for (int i = 0; i < vCount; i++)
                remap[i] = used[i] ? next++ : -1;

            // Pass 3: rebuild Vertices, Normals, and UV channels in
            // lock-step using the remap.
            var newVerts = new List<Assimp.Vector3D>(usedCount);
            for (int i = 0; i < vCount; i++)
                if (used[i]) newVerts.Add(m.Vertices[i]);

            List<Assimp.Vector3D> newNormals = null;
            if (m.Normals != null && m.Normals.Count == vCount)
            {
                newNormals = new List<Assimp.Vector3D>(usedCount);
                for (int i = 0; i < vCount; i++)
                    if (used[i]) newNormals.Add(m.Normals[i]);
            }

            var newUVChannels = new Dictionary<int, List<Assimp.Vector3D>>();
            for (int ch = 0; ch < m.TextureCoordinateChannelCount; ch++)
            {
                var src = m.TextureCoordinateChannels[ch];
                if (src == null || src.Count != vCount) continue;
                var dst = new List<Assimp.Vector3D>(usedCount);
                for (int i = 0; i < vCount; i++)
                    if (used[i]) dst.Add(src[i]);
                newUVChannels[ch] = dst;
            }

            // Pass 4: rewrite face indices through the remap. Faces
            // whose indices include an unmapped slot (-1) are skipped
            // -- the index-range filter upstream should have caught
            // these already, but we keep the guard for safety.
            var newFaces = new List<Assimp.Face>(fCount);
            for (int f = 0; f < fCount; f++)
            {
                var face = m.Faces[f];
                if (face == null || face.IndexCount != 3) continue;
                int a0 = remap[face.Indices[0]];
                int a1 = remap[face.Indices[1]];
                int a2 = remap[face.Indices[2]];
                if (a0 < 0 || a1 < 0 || a2 < 0) continue;
                var nf = new Assimp.Face();
                nf.Indices.Add(a0);
                nf.Indices.Add(a1);
                nf.Indices.Add(a2);
                newFaces.Add(nf);
            }

            // Commit. Clear-and-fill rather than reassign so we don't
            // surprise any caller still holding a reference to one of
            // the inner lists (the Assimp.Mesh API exposes them by
            // ref-property).
            m.Vertices.Clear();
            foreach (var v in newVerts) m.Vertices.Add(v);
            if (newNormals != null)
            {
                m.Normals.Clear();
                foreach (var n in newNormals) m.Normals.Add(n);
            }
            foreach (var kvp in newUVChannels)
            {
                var src = m.TextureCoordinateChannels[kvp.Key];
                src.Clear();
                foreach (var uv in kvp.Value) src.Add(uv);
            }
            m.Faces.Clear();
            foreach (var face in newFaces) m.Faces.Add(face);

            EnforceStreamLengthParity(m, m.Vertices.Count);
        }

        /// <summary>
        /// Trims/extends Normals and every TextureCoordinateChannel so
        /// each has exactly <paramref name="target"/> entries -- the
        /// stream-length parity glTF and Blender both require. Excess
        /// entries are dropped from the tail, shortfalls are filled with
        /// (0, 1, 0) for normals (matches our orphan default) and
        /// (0, 0, 0) for UVs.
        /// </summary>
        private static void EnforceStreamLengthParity(Assimp.Mesh m, int target)
        {
            if (m == null || target < 0) return;
            if (m.Normals != null)
            {
                while (m.Normals.Count > target) m.Normals.RemoveAt(m.Normals.Count - 1);
                while (m.Normals.Count < target) m.Normals.Add(new Assimp.Vector3D(0f, 1f, 0f));
            }
            for (int ch = 0; ch < m.TextureCoordinateChannelCount; ch++)
            {
                var s = m.TextureCoordinateChannels[ch];
                if (s == null) continue;
                while (s.Count > target) s.RemoveAt(s.Count - 1);
                while (s.Count < target) s.Add(new Assimp.Vector3D(0f, 0f, 0f));
            }
        }

        /// <summary>
        /// Computes angle-weighted per-vertex smooth normals and appends
        /// exactly <paramref name="vCount"/> entries to
        /// <c>assimpMesh.Normals</c>, in POSITION order.
        ///
        /// Algorithm: for every accepted triangle, compute the unit face
        /// normal, then for each of its three vertices accumulate
        /// (face_normal * interior_angle_at_that_vertex). After all triangles
        /// are processed, normalise each accumulator. Angle-weighting (vs
        /// area-weighting) gives visually consistent shading across
        /// tessellation changes and matches what Blender's Shade Smooth
        /// and most DCCs produce by default.
        ///
        /// Vertices that aren't referenced by any accepted triangle (orphans
        /// from index culling) receive a benign (0, 1, 0) unit normal so the
        /// glTF NORMAL accessor stays well-formed.
        /// </summary>
        private static void EmitComputedSmoothNormals(
            Assimp.Mesh assimpMesh,
            Point3DCollection positions,
            List<(int i0, int i1, int i2)> acceptedTris,
            int vCount)
        {
            var accum = new MediaVector3D[vCount];

            for (int f = 0; f < acceptedTris.Count; f++)
            {
                var (i0, i1, i2) = acceptedTris[f];

                var p0 = positions[i0];
                var p1 = positions[i1];
                var p2 = positions[i2];

                var e01 = p1 - p0;
                var e02 = p2 - p0;
                var faceN = MediaVector3D.CrossProduct(e01, e02);

                double faceLen = faceN.Length;
                if (faceLen <= 1e-20 || double.IsNaN(faceLen) || double.IsInfinity(faceLen))
                    continue;

                // Normalise in-place without relying on Vector3D.Normalize()
                // (which throws on a zero-length vector; we've already guarded
                // above but this stays explicit and allocation-free).
                double invLen = 1.0 / faceLen;
                faceN.X *= invLen; faceN.Y *= invLen; faceN.Z *= invLen;

                double w0 = AngleBetweenVectors(p1 - p0, p2 - p0);
                double w1 = AngleBetweenVectors(p0 - p1, p2 - p1);
                double w2 = AngleBetweenVectors(p0 - p2, p1 - p2);

                accum[i0].X += faceN.X * w0; accum[i0].Y += faceN.Y * w0; accum[i0].Z += faceN.Z * w0;
                accum[i1].X += faceN.X * w1; accum[i1].Y += faceN.Y * w1; accum[i1].Z += faceN.Z * w1;
                accum[i2].X += faceN.X * w2; accum[i2].Y += faceN.Y * w2; accum[i2].Z += faceN.Z * w2;
            }

            for (int i = 0; i < vCount; i++)
            {
                var n = accum[i];
                double lenSq = n.X * n.X + n.Y * n.Y + n.Z * n.Z;
                if (lenSq > 1e-20)
                {
                    double inv = 1.0 / Math.Sqrt(lenSq);
                    assimpMesh.Normals.Add(new Assimp.Vector3D(
                        (float)(n.X * inv), (float)(n.Y * inv), (float)(n.Z * inv)));
                }
                else
                {
                    assimpMesh.Normals.Add(new Assimp.Vector3D(0f, 1f, 0f));
                }
            }
        }

        /// <summary>
        /// Interior angle (radians) between two vectors, clamped against
        /// FP drift at the cos(-1)/cos(1) boundaries. Zero-length inputs
        /// return 0 so a degenerate-triangle contribution gets zero weight
        /// in the smooth-normal accumulator instead of a NaN.
        /// </summary>
        private static double AngleBetweenVectors(MediaVector3D a, MediaVector3D b)
        {
            double la = a.Length;
            double lb = b.Length;
            if (la <= 1e-20 || lb <= 1e-20)
                return 0.0;

            double cos = (a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (la * lb);
            if (cos > 1.0) cos = 1.0;
            else if (cos < -1.0) cos = -1.0;
            return Math.Acos(cos);
        }

        private static string ChooseGltf2FormatId(AssimpContext ctx)
        {
            var supported = ctx.GetSupportedExportFormats()
                .Select(f => f.FormatId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            if (CurrentExportFormatKind == ExportFormatKind.Fbx)
            {
                // Prefer binary FBX ("fbx") over ASCII ("fbxa"); both are
                // accepted by Blender but binary is the de-facto standard
                // and produces smaller files.
                string fbxBinary = supported.FirstOrDefault(id => id.Equals("fbx", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(fbxBinary))
                    return fbxBinary;
                string fbxAscii = supported.FirstOrDefault(id => id.Equals("fbxa", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(fbxAscii))
                    return fbxAscii;
                throw new InvalidOperationException("This Assimp build does not advertise an FBX exporter (fbx/fbxa).");
            }

            string gltf2 = supported.FirstOrDefault(id => id.Equals("gltf2", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(gltf2))
                return gltf2;

            if (supported.Any(id => id.Equals("gltf", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This Assimp build only advertises glTF 1 exporter (gltf). glTF 2 is required.");

            throw new InvalidOperationException("This Assimp build does not advertise a glTF 2 exporter (gltf2).");
        }

        private static void EmbedExternalResourcesIntoGltfAndDeleteSidecars(string gltfPath)
        {
            string json = File.ReadAllText(gltfPath, Encoding.UTF8);
            JsonNode root = JsonNode.Parse(json);
            if (root == null)
                return;

            string baseDir = Path.GetDirectoryName(gltfPath) ?? "";
            var filesToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            JsonArray buffers = root["buffers"] as JsonArray;
            if (buffers != null)
            {
                foreach (JsonNode bufferNode in buffers)
                {
                    if (bufferNode == null)
                        continue;

                    string uri = bufferNode["uri"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string filePath = Path.Combine(baseDir, uri.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(filePath))
                        continue;

                    byte[] bytes = File.ReadAllBytes(filePath);
                    string base64 = Convert.ToBase64String(bytes);
                    bufferNode["uri"] = "data:application/octet-stream;base64," + base64;
                    bufferNode["byteLength"] = bytes.Length;

                    filesToDelete.Add(filePath);
                }
            }

            JsonArray images = root["images"] as JsonArray;
            if (images != null)
            {
                foreach (JsonNode imageNode in images)
                {
                    if (imageNode == null)
                        continue;

                    string uri = imageNode["uri"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string filePath = Path.Combine(baseDir, uri.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(filePath))
                        continue;

                    byte[] bytes = File.ReadAllBytes(filePath);
                    string mime = GuessMimeType(filePath);
                    string base64 = Convert.ToBase64String(bytes);
                    imageNode["uri"] = "data:" + mime + ";base64," + base64;

                    filesToDelete.Add(filePath);
                }
            }

            // -----------------------------------------------------------
            // CONVERT KEEP-ALIVE LEAVES INTO EMPTY NODES
            // -----------------------------------------------------------
            //
            // The scene-building step attaches a zero-area
            // __node_keepalive__ triangle mesh to every empty-leaf node
            // so Assimp's glTF2 writer doesn't prune them. That keeps
            // the transform hierarchy intact through serialisation, but
            // in Blender those nodes import as Mesh objects with a
            // single invisible triangle -- which is wrong for the
            // modder's use case: these nodes are attachment points /
            // emitters / IK helpers / camera targets / x0_*_emitter
            // anchors, i.e. conceptual Empties, not geometry.
            //
            // Post-process the written glTF here: find the mesh index
            // whose name is "__node_keepalive__", then strip the `mesh`
            // property from every node that references it. glTF nodes
            // without a `mesh` property import as Blender Empty objects
            // with their transform preserved -- exactly what these
            // nodes semantically are.
            //
            // The orphaned keepalive mesh entry stays in the `meshes`
            // array (removing it would force a renumber of every
            // subsequent mesh index across all nodes and primitives).
            // It's unreferenced after this pass so it carries no
            // content into Blender; it costs ~400 bytes of JSON,
            // acceptable given the alternative churn.
            JsonArray meshesArr = root["meshes"] as JsonArray;
            JsonArray nodesArr = root["nodes"] as JsonArray;
            if (meshesArr != null && nodesArr != null)
            {
                int keepAliveIndex = -1;
                for (int mi = 0; mi < meshesArr.Count; mi++)
                {
                    JsonNode meshNode = meshesArr[mi];
                    if (meshNode == null) continue;
                    string meshName = meshNode["name"]?.GetValue<string>();
                    if (string.Equals(meshName, "__node_keepalive__", StringComparison.Ordinal))
                    {
                        keepAliveIndex = mi;
                        break;
                    }
                }

                if (keepAliveIndex >= 0)
                {
                    int convertedCount = 0;
                    for (int ni = 0; ni < nodesArr.Count; ni++)
                    {
                        JsonNode nodeObj = nodesArr[ni];
                        if (nodeObj == null) continue;
                        JsonNode meshProp = nodeObj["mesh"];
                        if (meshProp == null) continue;
                        int meshIdx;
                        try { meshIdx = meshProp.GetValue<int>(); }
                        catch { continue; }
                        if (meshIdx == keepAliveIndex)
                        {
                            // Remove the `mesh` property entirely -- the
                            // node becomes an empty glTF node, which
                            // Blender imports as an Empty object.
                            nodeObj.AsObject().Remove("mesh");
                            convertedCount++;
                        }
                    }
                    // No log here -- the .diag.txt's existing
                    // "KeepAliveEmptyLeavesPreserved=N" warning already
                    // tells the modder how many nodes were affected,
                    // and they'll see the same count of Empties in
                    // Blender's outliner post-import.
                }
            }

            // -----------------------------------------------------------
            // FORCE "Scene Root" NAME ON THE GLTF SCENE + TOP-LEVEL NODE
            // -----------------------------------------------------------
            //
            // Assimp's glTF2 writer is inconsistent about propagating
            // Scene.RootNode.Name into the JSON it serialises. Even
            // though the in-memory Assimp scene root is correctly
            // named "Scene Root" (see CreateSceneRootNode), the
            // emitted glTF can end up with:
            //   - scenes[0].name absent or set to "defaultScene"
            //   - nodes[scenes[0].nodes[0]].name absent or set to
            //     "RootNode" / "Node_0" / the empty string
            // Blender's glTF importer then surfaces the root as
            // "RootNode" / "Node_0" / "Scene" depending on which
            // field it sees first.
            //
            // Stamp both fields here so the modder always sees
            // "Scene Root" in Blender's outliner regardless of
            // which Assimp version produced the file.
            const string sceneRootName = "Scene Root";
            JsonArray scenesArr = root["scenes"] as JsonArray;
            if (scenesArr != null)
            {
                JsonArray nodesArrForRoot = root["nodes"] as JsonArray;
                foreach (JsonNode sceneNode in scenesArr)
                {
                    if (sceneNode == null) continue;
                    sceneNode["name"] = sceneRootName;

                    if (nodesArrForRoot == null) continue;
                    JsonArray sceneRootList = sceneNode["nodes"] as JsonArray;
                    if (sceneRootList == null) continue;

                    foreach (JsonNode rootRef in sceneRootList)
                    {
                        if (rootRef == null) continue;
                        int rootIdx;
                        try { rootIdx = rootRef.GetValue<int>(); }
                        catch { continue; }
                        if (rootIdx < 0 || rootIdx >= nodesArrForRoot.Count) continue;

                        JsonNode rootNodeJson = nodesArrForRoot[rootIdx];
                        if (rootNodeJson == null) continue;
                        rootNodeJson["name"] = sceneRootName;
                    }
                }
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            File.WriteAllText(gltfPath, root.ToJsonString(options), new UTF8Encoding(false));

            foreach (string file in filesToDelete)
            {
                try
                {
                    if (File.Exists(file))
                        File.Delete(file);
                }
                catch
                {
                }
            }
        }

        private static string GuessMimeType(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
                return "image/png";
            if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                return "image/jpeg";
            if (ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
                return "image/bmp";
            if (ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
                return "image/gif";
            if (ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
                return "image/webp";
            return "application/octet-stream";
        }

        private static void WriteDiagnostics(
            string diagPath,
            string idfFolder,
            string outPath,
            int year,
            string teamName,
            int lod,
            List<NodeInfo> parsedNodes,
            List<NodeInfo> x0ControlNodes,
            Scene scene,
            string formatId,
            List<string> warnings)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("F1 glTF 2 Export Diagnostics");
                sb.AppendLine("IDFFolder=" + idfFolder);
                sb.AppendLine("OutPath=" + outPath);
                sb.AppendLine("DetectedYear=" + year);
                sb.AppendLine("TeamName=" + teamName);
                sb.AppendLine("LOD=" + lod);
                sb.AppendLine("ChosenFormatId=" + formatId);
                sb.AppendLine("ParsedNodes=" + parsedNodes.Count);
                sb.AppendLine("ExportedX0ControlNodes=" + x0ControlNodes.Count);
                sb.AppendLine("SceneMeshes=" + scene.Meshes.Count);
                sb.AppendLine("SceneMaterials=" + scene.Materials.Count);
                sb.AppendLine("SceneRootName=" + (scene.RootNode?.Name ?? ""));
                sb.AppendLine("RootChildren=" + (scene.RootNode?.ChildCount ?? 0));
                sb.AppendLine();

                if (warnings != null && warnings.Count > 0)
                {
                    sb.AppendLine("Warnings:");
                    foreach (string w in warnings)
                        sb.AppendLine("  - " + w);
                    sb.AppendLine();
                }

                sb.AppendLine("Root Children:");
                if (scene.RootNode != null)
                {
                    foreach (var child in scene.RootNode.Children)
                        sb.AppendLine("  " + child.Name);
                }

                File.WriteAllText(diagPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static string FindMainRenderFile(string folder)
        {
            var candidates = Directory.GetFiles(folder, "*temp000*%3Frender*", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(folder, "*temp000*?render*", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(folder, "*%3Frender*", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(folder, "*?render*", SearchOption.AllDirectories))
                .Distinct()
                .ToArray();

            if (candidates.Length == 0)
                return null;

            return candidates
                .OrderByDescending(p => new FileInfo(p).Length)
                .FirstOrDefault();
        }

        private static string FindMainModelFile(string folder)
        {
            var candidates = Directory.GetFiles(folder, "*temp000*%3Fmodel*", SearchOption.TopDirectoryOnly);
            if (candidates.Length == 0)
                candidates = Directory.GetFiles(folder, "*temp001*%3Fmodel*", SearchOption.TopDirectoryOnly);
            if (candidates.Length == 0)
                candidates = Directory.GetFiles(folder, "*%3Fmodel*", SearchOption.TopDirectoryOnly);

            if (candidates.Length == 0)
                return null;

            return candidates
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }

        private static Assimp.Matrix4x4 BuildTransform(NodeInfo n)
        {
            var m = RotationFromQuaternion(n.Qx, n.Qy, n.Qz, n.Qw);

            m.A1 *= n.Sx; m.A2 *= n.Sx; m.A3 *= n.Sx;
            m.B1 *= n.Sy; m.B2 *= n.Sy; m.B3 *= n.Sy;
            m.C1 *= n.Sz; m.C2 *= n.Sz; m.C3 *= n.Sz;

            m.A4 = n.Tx;
            m.B4 = n.Ty;
            m.C4 = n.Tz;

            m.D1 = 0f;
            m.D2 = 0f;
            m.D3 = 0f;
            m.D4 = 1f;
            return m;
        }

        private static Assimp.Matrix4x4 RotationFromQuaternion(float x, float y, float z, float w)
        {
            float xx = x * x;
            float yy = y * y;
            float zz = z * z;
            float xy = x * y;
            float xz = x * z;
            float yz = y * z;
            float wx = w * x;
            float wy = w * y;
            float wz = w * z;

            float m00 = 1f - 2f * (yy + zz);
            float m01 = 2f * (xy + wz);
            float m02 = 2f * (xz - wy);

            float m10 = 2f * (xy - wz);
            float m11 = 1f - 2f * (xx + zz);
            float m12 = 2f * (yz + wx);

            float m20 = 2f * (xz + wy);
            float m21 = 2f * (yz - wx);
            float m22 = 1f - 2f * (xx + yy);

            return new Assimp.Matrix4x4(
                m00, m01, m02, 0,
                m10, m11, m12, 0,
                m20, m21, m22, 0,
                0, 0, 0, 1);
        }

        private static void ValidateAssimpMeshOrThrow(Assimp.Mesh mesh, string name)
        {
            if (mesh == null)
                throw new InvalidOperationException("Null mesh: " + name);

            if (mesh.Vertices == null || mesh.Vertices.Count == 0)
                throw new InvalidOperationException("Mesh has no vertices: " + name);

            if (mesh.Faces == null || mesh.Faces.Count == 0)
                throw new InvalidOperationException("Mesh has no faces: " + name);

            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                var face = mesh.Faces[f];
                if (face == null || face.IndexCount != 3)
                    throw new InvalidOperationException("Mesh has non-triangle face at " + f + ": " + name);

                for (int i = 0; i < face.IndexCount; i++)
                {
                    int idx = face.Indices[i];
                    if (idx < 0 || idx >= mesh.Vertices.Count)
                        throw new InvalidOperationException("Mesh face index out of range in " + name + " face " + f + ": " + idx + " / " + mesh.Vertices.Count);
                }
            }

            if (mesh.MaterialIndex < 0)
                throw new InvalidOperationException("Mesh has invalid material index: " + name);
        }

        private static void ValidateSceneOrThrow(Scene scene)
        {
            if (scene == null)
                throw new InvalidOperationException("Scene is null.");

            if (scene.RootNode == null)
                throw new InvalidOperationException("Scene.RootNode is null.");

            if (scene.Meshes == null || scene.Meshes.Count == 0)
                throw new InvalidOperationException("Scene has no meshes.");

            if (scene.Materials == null || scene.Materials.Count == 0)
                throw new InvalidOperationException("Scene has no materials.");

            for (int i = 0; i < scene.Meshes.Count; i++)
            {
                var mesh = scene.Meshes[i];
                if (mesh == null)
                    throw new InvalidOperationException("Scene mesh is null at index " + i);

                if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= scene.Materials.Count)
                    throw new InvalidOperationException("Mesh has invalid material index at scene mesh " + i + ": " + mesh.MaterialIndex);
            }

            ValidateNodeMeshIndicesRecursive(scene.RootNode, scene.Meshes.Count);
        }

        private static void ValidateNodeMeshIndicesRecursive(Node node, int sceneMeshCount)
        {
            if (node == null)
                throw new InvalidOperationException("Null node in scene graph.");

            if (node.MeshIndices != null)
            {
                for (int i = 0; i < node.MeshIndices.Count; i++)
                {
                    int idx = node.MeshIndices[i];
                    if (idx < 0 || idx >= sceneMeshCount)
                        throw new InvalidOperationException("Node '" + node.Name + "' references invalid mesh index " + idx + " / " + sceneMeshCount);
                }
            }

            if (node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    ValidateNodeMeshIndicesRecursive(node.Children[i], sceneMeshCount);
            }
        }

        private static int ReadU16(byte[] b, int o)
        {
            if ((uint)o + 1u >= (uint)b.Length) return 0;
            return b[o] | (b[o + 1] << 8);
        }

        private static int ReadS32(byte[] b, int o)
        {
            if ((uint)o + 3u >= (uint)b.Length) return 0;
            return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        }

        private static uint ReadU32(byte[] b, int o)
        {
            unchecked
            {
                return (uint)ReadS32(b, o);
            }
        }

        private static int ReadI32(byte[] b, int o)
        {
            return ReadS32(b, o);
        }

        private static float ReadF32(byte[] b, int o)
        {
            if ((uint)o + 3u >= (uint)b.Length) return 0;
            return BitConverter.ToSingle(b, o);
        }

        private static string ReadCStringSafe(byte[] data, int start, int limit)
        {
            if (start < 0) return "";
            if (start >= limit) return "";

            int end = start;
            while (end < limit && data[end] != 0)
                end++;

            int len = end - start;
            if (len <= 0) return "";

            try
            {
                return Encoding.ASCII.GetString(data, start, len);
            }
            catch
            {
                return "";
            }
        }

        private static string ReadCString(byte[] data, int start, int maxLen)
        {
            if (maxLen <= 0) return "";
            int limit = start + maxLen;
            if (limit < start) limit = int.MaxValue;
            if (limit > data.Length) limit = data.Length;
            return ReadCStringSafe(data, start, limit);
        }
    }
}