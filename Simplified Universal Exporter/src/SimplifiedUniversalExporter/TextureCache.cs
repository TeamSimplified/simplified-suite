using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimplifiedUniversalExporter
{
    /// <summary>
    /// Caches decoded texture ImageBrushes keyed by texture EAID. Load-on-demand
    /// and reuse -- one brush can be applied to many meshes without re-decoding.
    ///
    /// WPF note: each BitmapSource is Frozen for cross-thread safety and
    /// lower GPU overhead. ImageBrushes are also Frozen. This is essential
    /// for HelixToolkit performance when many submeshes share a material.
    /// </summary>
    public sealed class TextureCache
    {
        /// <summary>Set by the modern (2021+) loader while it resolves
        /// the shared tyres descriptor: that pipeline bakes the compound
        /// colour into the wall via the sidewall LOGO mask, so the
        /// classic-era whole-brush tint below must not double-apply (it
        /// washes the entire modern sidewall red).</summary>
        public static bool SuppressTyreTint = false;

        public string PackageRoot { get; }

        private readonly Dictionary<string, ImageBrush> _brushCache
            = new Dictionary<string, ImageBrush>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, MaterialFile> _materialCache
            = new Dictionary<string, MaterialFile>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Raw BGRA pixel cache. Populated lazily by GetPixelsForTexture.
        /// Separate from _brushCache because the compositor needs direct
        /// access to the decoded byte array (which ImageBrush hides).
        /// </summary>
        private readonly Dictionary<string, DecodedTexture> _pixelCache
            = new Dictionary<string, DecodedTexture>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Cache of composited number-atlas brushes, keyed by
        /// "{numberEaid}|{backgroundEaid}" so the same team+driver pair
        /// across multiple submeshes reuses one composite.
        /// </summary>
        private readonly Dictionary<string, ImageBrush> _compositeCache
            = new Dictionary<string, ImageBrush>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// FIX 28: dedicated cache for high-res sidecar brushes used by
        /// vehicle_generic submeshes (generic_main_d.tif and friends in
        /// shared/generics/textures). Kept strictly separate from
        /// _brushCache so the sidecar upgrade ONLY applies on the generic-
        /// submesh render path -- never on the paint/carbon body, never
        /// on sponsor/number overlay atlases. This preserves every existing
        /// decal behaviour exactly while still giving generics a crisp
        /// 1024x512-class texture instead of the ERP tail.
        ///
        /// Null-sentinel semantics match _brushCache: a stored null means
        /// "confirmed no sidecar available", so we don't re-probe on
        /// every submesh that shares the same generic texture.
        /// </summary>
        private readonly Dictionary<string, ImageBrush> _genericSidecarBrushCache
            = new Dictionary<string, ImageBrush>(StringComparer.OrdinalIgnoreCase);

        private readonly Action<string> _log;

        public TextureCache(string packageRoot, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(packageRoot))
                throw new ArgumentNullException(nameof(packageRoot));
            PackageRoot = Path.GetFullPath(packageRoot);
            _log = log ?? (_ => { });
        }

        public int DecodedTextureCount => _brushCache.Count;

        /// <summary>
        /// <summary>
        /// Resolve a material EAID -> ImageBrush of its preview texture.
        /// The material EAID is also passed to the picker as a hint -- for
        /// "number_N" materials this steers the picker toward me_logoMap
        /// (the driver decal) instead of me_albedoMap (which would be the
        /// same body paint everything else uses).
        /// Returns null if no suitable texture is available.
        /// </summary>
        public ImageBrush GetDiffuseBrushForMaterial(string materialEaid)
        {
            var r = ResolveDiffuseForMaterial(materialEaid);
            return r.Brush;
        }

        /// <summary>
        /// Carrier for a fully-resolved material-to-texture decision, so the
        /// loader can log exactly what landed on each submesh.
        /// </summary>
        public struct PreviewResolution
        {
            public string MaterialEaid;    // input
            public string PickedSlot;      // e.g. "me_albedoMap", or null
            public string TextureEaid;     // e.g. "eaid://.../mclaren_paint_d.tif"
            public ImageBrush Brush;       // decoded brush, or null
            public string FailureReason;   // set when Brush is null

            // Overlay layer (logo / sponsor / driver number decal). Populated
            // by the shader-aware MaterialSlotResolver when the material uses
            // a shader family that defines an overlay pass (paint, carbon,
            // steering wheel). The loader uses this to decide whether to
            // spawn a second GeometryModel3D that samples this texture via
            // the submesh's pair-2 UV on top of the base. When HasOverlay
            // is false, no overlay pass is rendered.
            public string OverlaySlot;
            public string OverlayTextureEaid;
            public ImageBrush OverlayBrush;
            public bool HasOverlay => OverlayBrush != null;

            // Source alpha channel of the driver/digit atlas behind
            // OverlayBrush, populated only for number submeshes. The
            // loader uses this to decide whether the submesh's vb=4
            // UVs actually cover digit content -- on some parts vb=4
            // points at a flat region of the atlas and the "real" UVs
            // for that mesh live in vb=3 offsets 4,6 instead. Without
            // this the loader would trust the UV bbox span, which
            // fails when a handful of outlier vertices widen the bbox
            // into digit territory while the bulk of triangles stay
            // in a blank area (observed on safetycell.number).
            public byte[] OverlaySourceAlpha;
            public int OverlaySourceWidth;
            public int OverlaySourceHeight;

            // FIX 17v2: shader UV translation that the game applies to
            // me_logoMap before sampling (additive, in atlas-UV units).
            // Read from the material's me_uOffset / me_vOffset float
            // parameters via MaterialParameterReader. Defaults to (0,0)
            // on any parse failure -- callers that don't read these fields
            // see no behaviour change.
            public float LogoUOffset;
            public float LogoVOffset;

            public MaterialSlotResolver.ShaderFamily Shader;
        }

        /// <summary>
        /// Resolve a material EAID end-to-end: parse the material file, pick
        /// the preview sampler slot and (if the shader defines one) the
        /// overlay sampler slot, decode both textures. Returns a record so
        /// the loader can log the whole chain per-submesh.
        ///
        /// Slot selection is delegated to MaterialSlotResolver, which uses the
        /// material's shader EAID and the supplied mesh slot name to index
        /// into a table-driven rule set. The slot name is critical for the
        /// paint and carbon shader families because both have multiple
        /// distinct uses sharing the same shader -- e.g. a number_N material
        /// applied to a "number" submesh wants to render the driver decal as
        /// the base, but applied to a "body" submesh would want the paint
        /// atlas. Older callers that don't pass a slot fall through to a
        /// material-name heuristic (looks for "number_" in the EAID).
        /// </summary>
        public PreviewResolution ResolveDiffuseForMaterial(string materialEaid, string slotName = null)
        {
            var r = new PreviewResolution { MaterialEaid = materialEaid };

            if (string.IsNullOrEmpty(materialEaid))
            {
                r.FailureReason = "material EAID empty";
                return r;
            }

            var mat = GetMaterial(materialEaid);
            if (mat == null)
            {
                r.FailureReason = "material file not found or unparseable";
                return r;
            }
            if (mat.TextureEaids == null || mat.TextureEaids.Count == 0)
            {
                r.FailureReason = "material has no texture bindings (parameter-only shader)";
                return r;
            }

            var picked = MaterialSlotResolver.Resolve(mat, materialEaid, slotName);
            r.Shader = picked.Shader;

            if (string.IsNullOrEmpty(picked.BaseTextureEaid))
            {
                r.FailureReason = string.IsNullOrEmpty(picked.FailureReason)
                    ? "shader-aware resolver returned no base texture"
                    : picked.FailureReason;
                return r;
            }

            r.PickedSlot = picked.BaseSlot;
            r.TextureEaid = picked.BaseTextureEaid;

            // === Overlay path (numbers use the decal path per general rule) ===
            //
            // Previous revisions had a separate number-submesh branch here
            // that loaded the driver atlas via a sidecar fetch, composited
            // the digit onto a sampled paint background (DecalCompositor.
            // CompositeNumberOnBackground), and swapped the base/overlay slots
            // so the body paint became the base and the driver atlas became
            // the overlay. It was trying to work around a WPF alpha-blending
            // quirk on heavily-transparent overlays, but the resulting
            // opaque-bake produced a coloured rectangle that looked like
            // the body paint with only a sliver of digit visible.
            //
            // Per user rule ("if the number does NOT load like the decals,
            // then it MUST load like the decals"), that entire branch is
            // removed. Numbers now take the same code path as every other
            // submesh: base is the shader-resolved BaseSlot / BaseTextureEaid
            // (for a number submesh that is me_logoMap -> driver_N.tif),
            // overlay is whatever picked.OverlayTextureEaid names (usually
            // nothing extra), and the brush is decoded via the normal
            // GetBrushForTexture path with its authored alpha preserved.
            //
            // Consequence: number overlays are rendered with the driver
            // atlas as an alpha-preserved ImageBrush -- same treatment as
            // sponsor decals get against mclaren_decal_da.tif. Whatever
            // rendering characteristics sponsors have on the current
            // build, numbers will have too.
            // FIX 28: for vehicle_generic submeshes (detail, generics,
            // rear_light, indicator_lights, generic_lcd, revs) try the
            // .mipmaps sidecar first so generic_main_d.tif renders at
            // its true 1024x512-class resolution instead of the ERP
            // tail. If no sidecar exists (helper returns null) or the
            // submesh is any other shader (paint, carbon, steering,
            // rainbeads, mirror, shadow, unknown), fall straight
            // through to the original GetBrushForTexture path --
            // which is critical: decal/overlay atlases depend on
            // that path being untouched.
            //
            // FIX 28b: some shared materials (notably generics.material)
            // appear to use a shader EAID that ClassifyShader doesn't
            // currently recognise as VehicleGeneric (it returned
            // Unknown in a validation run, even though the material
            // picks me_diffuseMap just like any other generic). So
            // in addition to the shader-family gate, fire the sidecar
            // attempt whenever the BASE texture EAID lives under a
            // "/generics/textures/" path -- that's where
            // generic_main_d.tif and its siblings live. Textures
            // outside that folder are only gated by shader family,
            // so paint/carbon body textures remain untouched.
            //
            // FIX 28c (F1 22 universal sidecar detection):
            //
            // F1 22 changed HOW the game signals "this texture has a
            // streamed high-res sidecar". In F1 2021 only driver
            // atlases shipped a temp002.image descriptor; in F1 22
            // EVERY high-res-enabled texture ships one (paint, detail,
            // damage, decals, drivers, steering wheel, wheel,
            // generics, disc, rear_light, glass, etc. -- we saw 116
            // such files in one team's F1 22 cache).
            //
            // The F1 22 game's "does this texture have a sidecar?"
            // answer is exactly "does temp002.image exist next to
            // temp001.image?" -- we mimic that by probing the
            // extracted cache with HasSidecarDescriptor.
            //
            // OR-ed with the existing two conditions so F1 2021
            // behaviour is strictly preserved:
            //   * F1 2021 generics hit genericShader / eaidInGenericsFolder
            //     as they already did -- unchanged path.
            //   * F1 2021 paint/carbon/detail don't ship temp002.image
            //     and their shader isn't VehicleGeneric, so NONE of
            //     the three conditions fire -- ERP-tail fallback as
            //     before.
            //   * F1 22 paint/detail/damage/etc. DO ship temp002.image,
            //     so the new condition fires and we try the sidecar.
            //
            // Scope guard: only applies to the non-number-submesh
            // base texture path (this else branch). Overlay atlases
            // (sponsor/number decals) keep going through
            // GetBrushForTexture unchanged -- user's explicit rule,
            // and necessary to avoid decal regressions.
            //
            // Diagnostic: log the gate decision for each submesh so
            // we can see at a glance why a sidecar was or wasn't
            // attempted. Cheap -- one line per non-number submesh.
            bool eaidInGenericsFolder =
                !string.IsNullOrEmpty(picked.BaseTextureEaid) &&
                picked.BaseTextureEaid.IndexOf(
                    "/generics/textures/", StringComparison.OrdinalIgnoreCase) >= 0;
            bool genericShader =
                picked.Shader == MaterialSlotResolver.ShaderFamily.VehicleGeneric;
            bool hasSidecarDescriptor =
                HasSidecarDescriptor(picked.BaseTextureEaid);
            bool trySidecar = genericShader || eaidInGenericsFolder || hasSidecarDescriptor;

            _log($"  [fix28-gate] shader={picked.Shader} " +
                 $"inGenericsFolder={eaidInGenericsFolder} " +
                 $"hasTemp002={hasSidecarDescriptor} " +
                 $"trySidecar={trySidecar} " +
                 $"base={Path.GetFileName(picked.BaseTextureEaid ?? "")}");

            if (trySidecar)
            {
                // FIX 30: only paint atlases get the badge-corner patch.
                // F1 22+ paint atlases ship with a "COMPETITOR / 2022 /
                // MCL36 / Signature" metadata badge in the top-left
                // corner; body panels with tile-wrap UVs land there
                // and render the badge instead of team paint. The
                // patcher substitutes that specific corner with a
                // sampled primary paint colour. Does nothing on
                // F1 2021 atlases (their top-left is already clean
                // paint) and nothing on non-paint textures (generics,
                // carbon, wheel, decal atlases, driver atlases).
                bool isPaintShader =
                    picked.Shader == MaterialSlotResolver.ShaderFamily.VehicleMetallicPaint;
                r.Brush = TryGetGenericSidecarBrush(
                    picked.BaseTextureEaid,
                    patchPaintBadge: isPaintShader);
            }

            if (r.Brush == null)
                r.Brush = GetBrushForTexture(picked.BaseTextureEaid);

            if (r.Brush == null)
            {
                r.FailureReason = "base texture decode failed (missing file or unsupported format)";
                return r;
            }

            // Material-parameter tinting. Tyre sidewall materials carry their
            // compound BAND COLOR as a vec3 cbuf parameter (me_logoColour) --
            // the diffuse texture is a SHARED greyscale Pirelli pattern; the
            // game multiplies it by the per-compound colour at runtime. Without
            // this step every sidewall renders monochrome, dropping the
            // visual distinction between soft / medium / hard / etc.
            //
            // Verified ground truth: the 9 sidewall_*.material files in
            // shared/tyres/materials encode each Pirelli colour band as
            // me_logoColour and the decoded vec3 values match the published
            // 2018 palette (see References/MaterialFormatNotes/05_*.md).
            //
            // Scope: tyre sidewall family only for this pass. The same
            // pattern (texture × parameter colour) applies to car paint
            // (me_lacquerColour, me_dirtAlbedo) and character skin
            // (me_baseColour, me_fuzzColour), but tyres are the cleanest
            // demonstration -- they're solid colour bands with no other
            // colour input on the model, so the tint is unambiguously
            // visible.
            // Tyre sidewall band colour: applied to the in-memory WPF
            // brush as a pixel tint so the preview viewport shows the
            // correct band colour (yellow Soft, white Medium, etc.).
            //
            // This is an in-memory ImageBrush modification, NOT a
            // texture file we write -- the FBX export deliberately
            // ships the un-tinted greyscale Pirelli diffuse and carries
            // me_logoColour through Material.ColorDiffuse so Blender's
            // renderer multiplies them at draw time. So the "no making
            // up textures" rule is honoured on disk; the WPF preview
            // is a different scope (it doesn't have a clean way to
            // bind a per-material multiplier separate from the brush,
            // so the brush itself gets the tint baked in).
            if (!SuppressTyreTint &&
                picked.Shader == MaterialSlotResolver.ShaderFamily.VehicleTyre &&
                mat.Parameters != null)
            {
                float[] logoColour = mat.GetVec3("me_logoColour");
                if (logoColour != null &&
                    (logoColour[0] > 0f || logoColour[1] > 0f || logoColour[2] > 0f))
                {
                    var tinted = TryTintBrushByColor(picked.BaseTextureEaid, logoColour);
                    if (tinted != null)
                    {
                        r.Brush = tinted;
                        _log($"  [tyre-tint] {Path.GetFileName(materialEaid)}: " +
                             $"me_logoColour=({logoColour[0]:F3},{logoColour[1]:F3},{logoColour[2]:F3}) " +
                             $"applied to preview brush (FBX uses ColorDiffuse).");
                    }
                }
            }

            // Scalar transparency. char_visor_skinned (and the related
            // char_visor_tear_skinned) carry a me_alpha cbuf parameter
            // controlling uniform opacity. When < 1.0 we apply it to
            // the ImageBrush via its Opacity property so the WPF preview
            // shows the visor as the semi-transparent surface it is in
            // the game. Brush.Opacity multiplies the per-pixel alpha
            // straight through to the final composite, exactly matching
            // the shader's uniform-alpha multiply behaviour.
            //
            // Caveat: WPF's DiffuseMaterial still writes to the depth
            // buffer for all rasterised pixels, so the visor can occlude
            // the helmet beneath it in some camera angles -- that's a
            // WPF rendering limitation, not a material-data issue.
            // For FBX export the Material.Opacity is plumbed through
            // independently (see F1FbxExporter.EnsureMaterial).
            if (mat.Parameters != null && r.Brush != null)
            {
                float? alphaParam = mat.GetFloat("me_alpha");
                if (alphaParam.HasValue && alphaParam.Value < 0.999f && alphaParam.Value >= 0f)
                {
                    // Brushes are frozen by the texture decoder before
                    // returning, so we can't mutate the cached one. Clone
                    // with the new opacity baked in, freeze that, and
                    // keep using the clone for the rest of this
                    // resolution.
                    var transparentBrush = r.Brush.CloneCurrentValue();
                    transparentBrush.Opacity = alphaParam.Value;
                    transparentBrush.Freeze();
                    r.Brush = transparentBrush;
                    _log($"  [me_alpha] {Path.GetFileName(materialEaid)}: opacity={alphaParam.Value:F3}");
                }
            }

            // Overlay is best-effort: if the resolver named an overlay slot
            // we try to decode its texture, but a missing overlay doesn't
            // fail the whole resolution -- the base still renders fine.
            //
            // FIX 28c extended to overlays: mirror the sidecar-first logic
            // used for the base brush above. Without this the overlay
            // texture (e.g. mclaren_driver_N.tif as the overlay for a
            // number submesh after the general-rule unification) loaded
            // from the 64x32 ERP tail instead of the 1024x512 streamed
            // sidecar, so the digit rendered blurry and pixelated. Probing
            // HasSidecarDescriptor on the OVERLAY EAID triggers
            // TryGetGenericSidecarBrush to fetch the high-res mip, matching
            // the behaviour of the previous opaque-bake path that did
            // MipmapsSidecarLoader.TryLoadTopMip on the driver atlas
            // explicitly.
            //
            // 2021-compat scope guard -- NARROWED to sponsor-decal atlases
            // only:
            //
            // The earlier "F1 2021 preview_done" build fetched overlay
            // atlases via GetBrushForTexture (ERP tail, low-res). When
            // sidecars were later enabled for overlays, F1 2021 SPONSOR
            // decal atlases (mclaren_decal_da.tif et al.) regressed --
            // their 4096x4096 sidecars have a different sponsor layout
            // than the ERP tail the UVs were authored against, so
            // sponsors landed in the wrong places.
            //
            // But F1 2021 driver NUMBER atlases (mclaren_driver_N.tif) do
            // NOT have that problem -- their ERP tail and sidecar carry
            // the same digit layout at different resolutions, so loading
            // the sidecar just makes the number sharper. Previously this
            // guard was too broad -- it suppressed the sidecar for ALL
            // F1 2021 overlay textures, which meant numbers were stuck
            // at 64x32 blurry.
            //
            // Narrow rule: on F1 2021 (flag=true), suppress the overlay
            // sidecar ONLY for sponsor decal atlases, identified by the
            // "_decal_" infix in the EAID (mclaren_decal_da, etc.).
            // Anything else overlay (driver atlases, and any future
            // overlay kind that ships a sidecar) goes through the
            // sidecar path as normal. F1 22 is unchanged: flag=false so
            // the 2021 guard is inert, every overlay that has a
            // sidecar gets it.
            if (picked.HasOverlay)
            {
                r.OverlaySlot = picked.OverlaySlot;
                r.OverlayTextureEaid = picked.OverlayTextureEaid;

                bool is2021SponsorDecal =
                    F1ModelLoader.ApplyF1_2021_DecalCompatibility &&
                    !string.IsNullOrEmpty(picked.OverlayTextureEaid) &&
                    picked.OverlayTextureEaid.IndexOf(
                        "_decal_", StringComparison.OrdinalIgnoreCase) >= 0;

                bool tryOverlaySidecar =
                    !is2021SponsorDecal &&
                    HasSidecarDescriptor(picked.OverlayTextureEaid);

                if (tryOverlaySidecar)
                {
                    r.OverlayBrush = TryGetGenericSidecarBrush(
                        picked.OverlayTextureEaid,
                        patchPaintBadge: false);

                    if (r.OverlayBrush != null)
                        _log($"  overlay sidecar: {Path.GetFileName(picked.OverlayTextureEaid)} " +
                             $"-> hi-res");
                }

                if (r.OverlayBrush == null)
                    r.OverlayBrush = GetBrushForTexture(picked.OverlayTextureEaid);

                if (r.OverlayBrush == null)
                    _log($"  WARNING: overlay texture decode FAILED for {picked.OverlayTextureEaid}");
            }

            // FIX 17v2: pull (me_uOffset, me_vOffset) directly from the
            // .material bytes via the .bt-template-validated reader. We
            // re-read the file rather than plumbing the values through
            // MaterialFile so this addition is fully hermetic (no impact
            // on bindings, no change to the existing string-scanner
            // Parse path). The file is small and the file system caches
            // it, so the cost is negligible.
            try
            {
                string matPath = EaidPathResolver.ResolveDescriptor(materialEaid, PackageRoot);
                if (matPath != null && File.Exists(matPath))
                {
                    var matBytes = File.ReadAllBytes(matPath);
                    var (uOff, vOff) = MaterialParameterReader.ReadLogoUvOffset(matBytes);
                    r.LogoUOffset = uOff;
                    r.LogoVOffset = vOff;
                }
            }
            catch { /* leave (0,0) on any I/O failure */ }

            return r;
        }

        public MaterialFile GetMaterial(string materialEaid)
        {
            if (_materialCache.TryGetValue(materialEaid, out var cached))
                return cached;

            string path = EaidPathResolver.ResolveDescriptor(materialEaid, PackageRoot);
            if (path == null || !File.Exists(path))
            {
                _log($"  Material file not found on disk: {path}");
                _materialCache[materialEaid] = null;
                return null;
            }

            try
            {
                var mat = MaterialReader.FromFile(path);
                _materialCache[materialEaid] = mat;
                return mat;
            }
            catch (Exception ex)
            {
                _log($"  Failed to parse material {path}: {ex.Message}");
                _materialCache[materialEaid] = null;
                return null;
            }
        }

        /// <summary>
        /// Estimate the bytes-per-slice for a Codemasters texture descriptor:
        /// sum the BC-block size of each mip level from base down to 1x1
        /// (or to the descriptor's stated MipCount, whichever is smaller).
        ///
        /// Used by GetBrushForTexture's texarray-slicing branch to figure
        /// out where slice 0 ends and slice 1 begins in a stacked payload.
        /// Returns 0 for unknown / unsupported formats so the caller can
        /// fall back to "decode as single image" rather than slice the
        /// payload incorrectly.
        ///
        /// Block-compressed sizes:
        ///   BC1 / BC1_SRGB    8 bytes per 4x4 block  -> 0.5 bytes/pixel
        ///   BC2 / BC3         16 bytes per 4x4 block -> 1.0 bytes/pixel
        ///   BC5 / BC5_SNORM   16 bytes per 4x4 block -> 1.0 bytes/pixel
        ///
        /// Each mip level rounds dimensions UP to a multiple of 4 (the BC
        /// block size) before computing block count -- a 5x5 mip stores as
        /// 2x2 blocks, not 1x1.
        /// </summary>
        private static int ComputeMipChainBytesForFormat(
            uint formatCode, int width, int height, int mipCount)
        {
            int bytesPerBlock;
            if (formatCode == 52 || formatCode == 54)
                bytesPerBlock = 8;          // BC1
            else if (formatCode == 55 || formatCode == 57 ||
                     formatCode == 65 || formatCode == 69 ||
                     formatCode == 70)
                bytesPerBlock = 16;         // BC2 / BC3 / BC5 / BC7
            else
                return 0;                   // unknown -- caller falls back

            if (width <= 0 || height <= 0 || mipCount <= 0) return 0;

            int total = 0;
            int w = width, h = height;
            for (int i = 0; i < mipCount; i++)
            {
                int blocksW = Math.Max(1, (w + 3) / 4);
                int blocksH = Math.Max(1, (h + 3) / 4);
                total += blocksW * blocksH * bytesPerBlock;
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
            return total;
        }

        public ImageBrush GetBrushForTexture(string textureEaid)
        {
            if (_brushCache.TryGetValue(textureEaid, out var cached))
                return cached;

            var (_, metaPath, payloadPath) =
                EaidPathResolver.ResolveTexturePaths(textureEaid, PackageRoot);

            if (metaPath == null || !File.Exists(metaPath) || !File.Exists(payloadPath))
            {
                // FIX 23: try DDS override when ERP texture files are missing.
                // Shared/generic textures often live in a separate ERP that
                // isn't extracted with the team package. Users can provide
                // standalone .dds files as replacements.
                var ddsBrush = TryLoadDdsOverride(textureEaid);
                if (ddsBrush != null)
                {
                    _brushCache[textureEaid] = ddsBrush;
                    return ddsBrush;
                }
                _log($"  Texture files missing for {textureEaid}");
                _brushCache[textureEaid] = null;
                return null;
            }

            try
            {
                var desc = ImageDescriptor.FromFile(metaPath);
                byte[] payload = File.ReadAllBytes(payloadPath);

                // Texture-array slicing: when the descriptor announces multiple
                // slices, the payload is N mip-chains stacked back-to-back. We
                // render slice 0 only -- it's the canonical entry the engine
                // would show before any compound/runtime selection has happened.
                //
                // Slice count is computed empirically rather than reading
                // ArraySize directly: F1 2018 classic texarrays sometimes
                // store ArraySize as half the actual stored slice count
                // (verified on c_03_sidewall_diffuse: ArraySize=2, payload
                // contains 4 mip-chains). We compute the per-slice mip-chain
                // size and divide. This is robust for both "ArraySize is
                // exact" and "ArraySize is misleading" texarrays.
                if (desc.IsTextureArray && payload.Length > 0)
                {
                    int sliceMipBytes = ComputeMipChainBytesForFormat(
                        desc.FormatCode, desc.Width, desc.Height, desc.MipCount);

                    if (sliceMipBytes > 0 && sliceMipBytes < payload.Length)
                    {
                        int derivedSliceCount = payload.Length / sliceMipBytes;
                        _log($"  texarray: ArraySize={desc.ArraySize} hdr, derived " +
                             $"sliceCount={derivedSliceCount} from payload " +
                             $"(payload={payload.Length}, perSlice={sliceMipBytes}); " +
                             $"rendering slice 0.");

                        // Take only slice 0's mip chain as the new "payload"
                        // so the existing single-image decoders below operate
                        // on a slice-shaped buffer without further changes.
                        byte[] slice0 = new byte[sliceMipBytes];
                        Buffer.BlockCopy(payload, 0, slice0, 0, sliceMipBytes);
                        payload = slice0;
                    }
                    else
                    {
                        _log($"  texarray: per-slice size compute failed " +
                             $"(format={desc.FormatCode}, W={desc.Width}, H={desc.Height}, " +
                             $"mips={desc.MipCount}); decoding payload as-is.");
                    }
                }

                byte[] bgra;
                string decoderName;
                if (desc.IsBC1)
                {
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC1(payload, desc.Width, desc.Height);
                    decoderName = "BC1";
                }
                else if (desc.IsBC3)
                {
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC3(payload, desc.Width, desc.Height);
                    decoderName = "BC3";
                }
                else if (desc.IsBC7)
                {
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC7(payload, desc.Width, desc.Height);
                    decoderName = "BC7";
                }
                else if (desc.IsBC5)
                {
                    // BC5 holds two independent unsigned channels per
                    // pixel (RG, with B implied). Codemasters uses BC5 for
                    // two distinct purposes:
                    //
                    //   * Tangent-space normal maps (.n.tif) -- R = X, G = Y,
                    //     Z reconstructed in shader as sqrt(1-X^2-Y^2). This
                    //     is the common DirectX use of BC5.
                    //   * Tread / detail diffuse atlases (e.g.
                    //     c_03_slick_diffuse, c_03_inter_diffuse) -- R holds
                    //     the dominant tread-pattern luminance and G holds
                    //     a secondary detail mask. The game shader composes
                    //     these with a base rubber colour at draw time.
                    //
                    // For the preview we render BC5 as **grayscale-R** in
                    // both cases: copy the R channel into all three colour
                    // bytes, then apply a 1.5x luminance gain. Why this
                    // works for both:
                    //
                    //   * For tread diffuse: R IS the visible tread
                    //     pattern. Grayscale-R produces clean dark grooves
                    //     on lighter rubber, exactly what you'd want for
                    //     a tread preview. The game's compositing with the
                    //     rubber base colour is cosmetic; the shape is in
                    //     the R channel.
                    //   * For normal maps: R-as-grayscale shows the
                    //     surface shape (height-ish) without the
                    //     purplish-blue artefacts that a true normal-map
                    //     decode would produce. Not photorealistic but
                    //     visually stable; previewing a normal map as
                    //     colour was always going to be a compromise.
                    //
                    // Decoder reuses the existing DecodeBC3 path: BC5's
                    // bytes 0-7 are a BC4 alpha-style block for the R
                    // channel, identical in layout to BC3's alpha block.
                    // We extract R from that block and discard the second
                    // 8 bytes (the G-channel BC4 block) for this preview.
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC5AsGrayscaleR(
                        payload, desc.Width, desc.Height);
                    decoderName = "BC5/grayscale-R";
                }
                else if (desc.IsBC2)
                {
                    // cf_pattern_*_d carbon weaves (fmt 55, BC2 / DXT3).
                    //
                    // The raw BC2 colour block stores R and B as a strongly-
                    // correlated pair (R ~= B) with G encoding fine detail.
                    // This is Codemasters packing data so the NEFX2 carbon
                    // shader can reconstruct proper anisotropic fibres from
                    // (R, G, B) via a lookup. Decoded straight as RGB565 the
                    // texture reads as a saturated magenta pattern -- true to
                    // what the file holds, but not useful as a preview.
                    //
                    // For the WPF preview: use the GREEN channel only as the
                    // luminance source, then gamma-stretch it. Earlier code
                    // averaged R+G+B which drowned the useful G signal in the
                    // R/B mask noise -- producing a passable grey for
                    // cf_pattern_1_d (which has a relatively benign R/B
                    // pattern) but visible chequerboard/noise on cf_pattern_3_d
                    // and cf_pattern_4_d (whose R/B pattern is a different
                    // shape that adds high-frequency artefacts to the average).
                    //
                    // Pure-G plus a 1.5x gain gives a clean carbon look on
                    // every pattern in the 2021 set, and approximates how the
                    // game's shader brightens the weave under its lacquer
                    // pass. The alpha channel is forced opaque (the BC2 alpha
                    // is anisotropy mask data, not transparency).
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC2(
                        payload, desc.Width, desc.Height);
                    for (int i = 0; i + 3 < bgra.Length; i += 4)
                    {
                        int g = bgra[i + 1];
                        // 1.5x gain with saturation -- carbon weave G channel
                        // typically peaks around 170/255, so scaling lifts it
                        // to the 220-255 range without clipping low detail.
                        int boosted = (g * 3) >> 1;
                        if (boosted > 255) boosted = 255;
                        byte lum = (byte)boosted;
                        bgra[i] = lum; bgra[i + 1] = lum; bgra[i + 2] = lum;
                        bgra[i + 3] = 255;
                    }
                    decoderName = "BC2/carbon";
                }
                else
                {
                    // BC5 (normal maps) and unknowns -- skip for WPF preview.
                    _log($"  Unsupported image format {desc.FormatCode} for preview: {textureEaid}");
                    _brushCache[textureEaid] = null;
                    return null;
                }

                var bitmap = BitmapSource.Create(
                    desc.Width, desc.Height,
                    96, 96,
                    PixelFormats.Bgra32, null,
                    bgra, desc.Width * 4);
                bitmap.Freeze();

                // Correct brush setup for WPF 3D DiffuseMaterial texture
                // mapping:
                //
                //   * ViewportUnits = BrushMappingMode.Absolute. THIS IS
                //     CRITICAL. The default (RelativeToBoundingBox) makes
                //     WPF auto-normalize the brush's Viewport rect to the
                //     bounding box of the geometry's TextureCoordinates,
                //     which means UV (0.81, 0.03) doesn't sample texel
                //     (0.81, 0.03) of the atlas -- it samples
                //     (0.81, 0.03) RELATIVE TO whatever min/max UVs
                //     happen to be on this submesh. Verified failure on
                //     2021 McLaren safetycell body: real UVs cluster at
                //     U=[0.5, 0.95] V=[0.0, 0.06] (orange paint stripe at
                //     atlas top), but two outlier verts at U=0.06 V=0.85
                //     and V=-0.124 stretch the bbox by ~10x vertically.
                //     Auto-normalization then compresses the body's V=0.03
                //     to V'=0.15, sampling pixel y=76 which is the dark
                //     #181818 background instead of pixel y=15 (orange).
                //     Result: body renders as solid black with a few
                //     coloured streaks instead of the team's livery.
                //
                //     With Absolute, the brush's default Viewport (0,0,1,1)
                //     is interpreted as a fixed (0,0)-(1,1) box in UV
                //     space, so UV (u,v) maps directly to texel (u*W, v*H)
                //     of the source image, exactly as authored. Tile mode
                //     handles UVs outside [0,1] by wrapping. This matches
                //     the documented WPF3D recipe for "use texture
                //     coordinates outside [0,1]" -- see
                //     https://learn.microsoft.com/.../texture-coordinates-in-wpf3d
                //
                //   * TileMode = Tile. F1 materials *do* use UVs outside
                //     [0,1] -- carbon weaves, rain beads, tread patterns
                //     are all tiled, and the decal sheet UVs can land in
                //     adjacent tile quadrants. Tile mode is correct; None
                //     would leave those faces transparent.
                //
                //   * Viewport / Viewbox / Stretch are left at their
                //     defaults (0,0,1,1 / 0,0,1,1 / Fill).
                var brush = new ImageBrush(bitmap)
                {
                    TileMode = TileMode.Tile,
                    ViewportUnits = BrushMappingMode.Absolute,
                };
                brush.Freeze();

                _brushCache[textureEaid] = brush;
                _log($"  Decoded {desc.Width}x{desc.Height} " +
                     $"{decoderName} texture: {Path.GetFileName(payloadPath)}");
                return brush;
            }
            catch (Exception ex)
            {
                _log($"  Failed to decode texture {textureEaid}: {ex.Message}");
                _brushCache[textureEaid] = null;
                return null;
            }
        }

        /// <summary>
        /// FIX 28: sidecar-brush loader used exclusively by the
        /// vehicle_generic render path in ResolveSubmesh. Analogous to
        /// what the number-submesh block does with
        /// <see cref="MipmapsSidecarLoader.TryLoadTopMip"/> for driver
        /// atlases, but the shared-generics .mipmaps format is different
        /// enough that it gets its own loader --
        /// <see cref="GenericMipmapsSidecarLoader.TryLoadTopMip"/>.
        ///
        /// Differences between the two sidecar flavours (why we need
        /// two loaders instead of one):
        ///   * driver atlas:   has a temp002.image descriptor, tail is
        ///                     BC3, file is LZ4-compressed BC3 blocks
        ///                     per mip (tiny, ~35 KB).
        ///   * shared generic: NO temp002.image (shared-generics ERP
        ///                     doesn't emit the sidecar block), tail is
        ///                     BC1, file is RAW concat BC1 blocks per
        ///                     mip (multi-MB). File size exactly equals
        ///                     sum_{i=1..N} BC1(tailW&lt;&lt;i, tailH&lt;&lt;i),
        ///                     which is how the generic loader infers N
        ///                     without a descriptor.
        ///
        /// Scope (unchanged from the prior revision):
        ///   * Only called from the <c>picked.Shader ==
        ///     ShaderFamily.VehicleGeneric</c> branch in ResolveSubmesh,
        ///     never from paint/carbon base paths, never from the overlay
        ///     path. Decals (sponsors, numbers) keep loading through the
        ///     untouched <see cref="GetBrushForTexture"/> code.
        ///   * Uses its own cache (<see cref="_genericSidecarBrushCache"/>)
        ///     so hits here don't leak into <see cref="_brushCache"/>.
        ///
        /// Silent null return on any failure lets the caller fall through
        /// to <see cref="GetBrushForTexture"/>'s ERP-tail path unchanged.
        /// </summary>
        private ImageBrush TryGetGenericSidecarBrush(
            string textureEaid,
            bool patchPaintBadge = false)
        {
            if (string.IsNullOrEmpty(textureEaid))
                return null;

            // FIX 30: cache key includes the patch flag so a "patched"
            // and "unpatched" variant of the same texture don't collide.
            // In practice a paint texture is only ever loaded via the
            // paint-shader path (patched=true) and every other texture
            // via the non-paint path (patched=false), but keying on the
            // flag keeps the cache honest if the caller pattern changes.
            string cacheKey = patchPaintBadge
                ? (textureEaid + "#paint-patched")
                : textureEaid;

            if (_genericSidecarBrushCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var sidecarPx = GenericMipmapsSidecarLoader.TryLoadTopMip(
                textureEaid, PackageRoot, _log);
            if (sidecarPx == null || sidecarPx.Bgra == null ||
                sidecarPx.Width <= 0 || sidecarPx.Height <= 0)
            {
                // Null-sentinel: confirmed no sidecar for this texture.
                // Next lookup returns immediately from cache instead of
                // re-probing the install on every generic submesh.
                _genericSidecarBrushCache[cacheKey] = null;
                return null;
            }

            try
            {
                byte[] pixels = sidecarPx.Bgra;
                int w = sidecarPx.Width;
                int h = sidecarPx.Height;

                // FIX 30: paint-shader textures get their top-left corner
                // patched if it looks like a badge (F1 22+ authoring).
                // Non-paint textures bypass entirely -- zero effect.
                if (patchPaintBadge)
                {
                    var (maybePatched, wasPatched) =
                        PaintAtlasBadgePatcher.MaybePatchBadgeCorner(
                            pixels, w, h, _log,
                            Path.GetFileName(textureEaid));
                    if (wasPatched)
                        pixels = maybePatched;
                }

                var brush = CreateBrushFromPixels(pixels, w, h);
                _genericSidecarBrushCache[cacheKey] = brush;
                _log($"  generic sidecar hi-res {w}x{h} " +
                     $"for {Path.GetFileName(textureEaid)}" +
                     (patchPaintBadge ? " (paint-patch candidate)" : ""));
                return brush;
            }
            catch (Exception ex)
            {
                _log($"  generic sidecar brush wrap failed for {textureEaid}: " +
                     $"{ex.Message} -- falling back to ERP tail");
                _genericSidecarBrushCache[cacheKey] = null;
                return null;
            }
        }

        /// <summary>
        /// FIX 28c: probe whether the extracted cache has a temp002.image
        /// descriptor sitting next to this texture's temp001.image payload.
        ///
        /// This is the GAME'S OWN SIGNAL for "this texture has a streamed
        /// high-res sidecar you should load". The tool's extractor already
        /// writes temp002.image to the cache unmodified when it's present
        /// in the ERP (see ErpIdfExtractor.WriteTextureImage), so this
        /// check is a one-shot filesystem probe: file-exists or not.
        ///
        /// Used by the fix28 gate in ResolveSubmesh as a THIRD condition
        /// (OR-ed with genericShader and eaidInGenericsFolder) so that
        /// F1 22's universally-signalled sidecars get picked up without
        /// needing shader-family or folder-path heuristics, while F1 2021
        /// behaviour is preserved exactly (its non-driver textures don't
        /// ship temp002.image, so this returns false and the gate falls
        /// back to the old conditions).
        ///
        /// Result cached per-EAID so the filesystem hit is paid at most
        /// once per texture per TextureCache lifetime.
        /// </summary>
        private bool HasSidecarDescriptor(string textureEaid)
        {
            if (string.IsNullOrEmpty(textureEaid)) return false;

            if (_sidecarDescriptorPresence.TryGetValue(textureEaid, out var cached))
                return cached;

            // temp002.image lives at the SAME path as temp001.image, just
            // with the suffix swapped. Reuse ResolveTexturePaths to get
            // the payload path and transform it.
            bool present = false;
            try
            {
                var (_, _, payloadPath) =
                    EaidPathResolver.ResolveTexturePaths(textureEaid, PackageRoot);
                if (!string.IsNullOrEmpty(payloadPath))
                {
                    // payloadPath ends in "...!!!temp001.image"; swap to "!!!temp002.image".
                    const string temp001 = "!!!temp001.image";
                    const string temp002 = "!!!temp002.image";
                    if (payloadPath.EndsWith(temp001, StringComparison.OrdinalIgnoreCase))
                    {
                        string descPath = payloadPath.Substring(0, payloadPath.Length - temp001.Length) + temp002;
                        present = File.Exists(descPath);
                    }
                }
            }
            catch
            {
                // Any I/O or path failure just means "don't enable the
                // sidecar gate path" -- fall back to the old conditions.
                present = false;
            }

            _sidecarDescriptorPresence[textureEaid] = present;
            return present;
        }

        /// <summary>
        /// Cache for HasSidecarDescriptor results. Populated lazily; entries
        /// are boolean "temp002.image exists" answers and don't change
        /// during a single load session, so a hit is free.
        /// </summary>
        private readonly Dictionary<string, bool> _sidecarDescriptorPresence
            = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Searches for a high-res DDS override file matching the given
        /// texture EAID. Looks in the package textures folder for a .dds
        /// file with a matching stem (e.g. mclaren_driver_1.dds for
        /// mclaren_driver_1.tif). Supports DXT5 (BC3) and DXT1 (BC1).
        /// Returns null if no DDS file exists or decoding fails.
        /// </summary>
        public ImageBrush TryLoadDdsOverride(string textureEaid)
        {
            if (string.IsNullOrEmpty(textureEaid) || string.IsNullOrEmpty(PackageRoot))
                return null;

            // Extract filename stem from EAID
            string rel = EaidPathResolver.StripEaidPrefix(textureEaid);
            if (rel == null) return null;

            int lastSlash = Math.Max(rel.LastIndexOf('/'), rel.LastIndexOf('\\'));
            string dir = (lastSlash >= 0) ? rel.Substring(0, lastSlash) : "";
            string file = (lastSlash >= 0) ? rel.Substring(lastSlash + 1) : rel;
            int dot = file.LastIndexOf('.');
            string stem = (dot >= 0) ? file.Substring(0, dot) : file;

            // Search for .dds file in same folder
            string texDir = Path.Combine(PackageRoot, dir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(texDir)) return null;

            string ddsPath = Path.Combine(texDir, stem + ".dds");
            if (!File.Exists(ddsPath))
            {
                // Also search parent and sibling folders
                string parentDir = Path.GetDirectoryName(texDir);
                if (parentDir != null)
                {
                    foreach (var candidate in Directory.EnumerateFiles(
                        parentDir, stem + ".dds", SearchOption.AllDirectories))
                    {
                        ddsPath = candidate;
                        break;
                    }
                }
                if (!File.Exists(ddsPath)) return null;
            }

            try
            {
                byte[] dds = File.ReadAllBytes(ddsPath);
                if (dds.Length < 128 || dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S' || dds[3] != ' ')
                {
                    _log($"  DDS: invalid header in {ddsPath}");
                    return null;
                }

                int height = BitConverter.ToInt32(dds, 12);
                int width = BitConverter.ToInt32(dds, 16);
                string fourCC = System.Text.Encoding.ASCII.GetString(dds, 84, 4);

                byte[] payload = new byte[dds.Length - 128];
                Array.Copy(dds, 128, payload, 0, payload.Length);

                byte[] bgra;
                if (fourCC == "DXT5")
                {
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC3(payload, width, height);
                }
                else if (fourCC == "DXT1")
                {
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC1(payload, width, height);
                }
                else if (fourCC == "DXT3")
                {
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC2(payload, width, height);
                }
                else
                {
                    _log($"  DDS: unsupported format {fourCC} in {ddsPath}");
                    return null;
                }

                var brush = CreateBrushFromPixels(bgra, width, height);
                _log($"  DDS: loaded {width}x{height} {fourCC} from {Path.GetFileName(ddsPath)}");
                return brush;
            }
            catch (Exception ex)
            {
                _log($"  DDS: failed to load {ddsPath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clears every cached brush. Call when switching cars or livery contexts
        /// so memory doesn't accumulate across loads.
        /// </summary>
        public void Clear()
        {
            _brushCache.Clear();
            _materialCache.Clear();
            _pixelCache.Clear();
            _compositeCache.Clear();
            _genericSidecarBrushCache.Clear();
            _sidecarDescriptorPresence.Clear();
        }

        /// <summary>
        /// Decode a texture EAID to its raw BGRA32 pixel buffer plus
        /// dimensions. Used by the compositor to alpha-blend the driver-
        /// number atlas onto the paint/carbon colour source. Results are
        /// cached.  Returns null on any decode failure (missing file,
        /// unsupported format, malformed payload).
        ///
        /// The returned byte[] is ALWAYS exclusively owned by the cache --
        /// callers must not mutate it. If you need to transform the pixels
        /// (e.g. compositing), allocate a new array and copy first.
        /// </summary>
        /// <summary>
        /// Tints a diffuse texture by a per-component RGB multiplier (the
        /// material's me_logoColour / me_baseColour / etc.). The math is
        /// straight multiply blend in 8-bit space:
        ///   out_r = src_r * tint_r / 255
        ///   out_g = src_g * tint_g / 255
        ///   out_b = src_b * tint_b / 255
        ///   out_a = src_a   (alpha preserved)
        ///
        /// Used for tyre sidewalls where the diffuse texture is a shared
        /// greyscale-ish pattern and the per-compound colour lives in a
        /// cbuf parameter rather than in the texture itself. Returns null
        /// if the source texture can't be decoded -- caller falls back to
        /// the un-tinted brush (which still looks better than a hard
        /// failure even if the colour is wrong).
        ///
        /// The result is cached so multiple submeshes that share the same
        /// (texture, colour) pair don't re-pay the per-pixel multiply.
        /// </summary>
        private ImageBrush TryTintBrushByColor(string textureEaid, float[] colorRgb)
        {
            if (string.IsNullOrEmpty(textureEaid) || colorRgb == null || colorRgb.Length < 3)
                return null;

            // Cache key: texture EAID + the 3 colour bytes (quantised to
            // 0..255 since the brush ultimately is 8-bit). Two materials
            // with the same texture + colour share the cached brush.
            byte tintR = ToByte(colorRgb[0]);
            byte tintG = ToByte(colorRgb[1]);
            byte tintB = ToByte(colorRgb[2]);
            string cacheKey = $"{textureEaid}|tint:{tintR:X2}{tintG:X2}{tintB:X2}";
            if (_tintedBrushCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var decoded = GetPixelsForTexture(textureEaid);
            if (decoded == null || decoded.Bgra == null) { _tintedBrushCache[cacheKey] = null; return null; }

            int w = decoded.Width, h = decoded.Height;
            byte[] src = decoded.Bgra;
            int n = src.Length;
            byte[] dst = new byte[n];
            for (int i = 0; i + 3 < n; i += 4)
            {
                // BGRA32: B at +0, G at +1, R at +2, A at +3.
                dst[i + 0] = (byte)((src[i + 0] * tintB) / 255);
                dst[i + 1] = (byte)((src[i + 1] * tintG) / 255);
                dst[i + 2] = (byte)((src[i + 2] * tintR) / 255);
                dst[i + 3] = src[i + 3];
            }

            var bitmap = BitmapSource.Create(
                w, h, 96, 96, PixelFormats.Bgra32, null,
                dst, w * 4);
            bitmap.Freeze();
            var brush = new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
            };
            brush.Freeze();
            _tintedBrushCache[cacheKey] = brush;
            return brush;
        }

        private static byte ToByte(float f)
        {
            if (float.IsNaN(f) || f <= 0f) return 0;
            if (f >= 1f) return 255;
            return (byte)(f * 255f + 0.5f);
        }

        /// <summary>Cache of (textureEaid + tint colour) -> tinted ImageBrush.</summary>
        private readonly Dictionary<string, ImageBrush> _tintedBrushCache
            = new Dictionary<string, ImageBrush>(StringComparer.Ordinal);

        /// <summary>
        /// Composites a diffuse texture's RGB with a separate mask
        /// texture's R channel as alpha. Used by ResolveDiffuseForMaterial
        /// for the helmet / cloth / attachment shaders that drive per-
        /// pixel alpha test via a sibling me_maskMap binding.
        ///
        /// Sampling: nearest-neighbour from the mask's pixel grid to
        /// the diffuse's, since masks are binary-ish (mostly 0 or 255)
        /// and bilinear would soften the cutout edges.
        ///
        /// Result is cached by (diffuseEaid|maskEaid) so multiple
        /// submeshes sharing the same composition reuse the work.
        /// Returns null when either texture can't be decoded -- caller
        /// falls back to the un-masked brush rather than failing the
        /// whole resolution.
        /// </summary>
        /// <summary>
        /// Derives an alpha channel from a diffuse texture's RGB
        /// luminance, used for alpha-dither shaders (hair, frays,
        /// visor tear) whose BC1 diffuse has no real alpha channel.
        ///
        /// alpha = max(R, G, B)  -- pure-black pixels become fully
        /// transparent, fully-saturated pixels stay fully opaque,
        /// intermediate luminance gets a proportional alpha. This is a
        /// crude approximation of the game's dither-based alpha test
        /// but gives a clean cutout in WPF and Blender without any
        /// shader-side help.
        ///
        /// Result is cached so multiple submeshes sharing the same
        /// diffuse reuse the work.
        /// </summary>
        private ImageBrush TryBuildLuminanceAlphaBrush(string diffuseEaid)
        {
            if (string.IsNullOrEmpty(diffuseEaid)) return null;

            string cacheKey = $"{diffuseEaid}|lumAlpha";
            if (_tintedBrushCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var decoded = GetPixelsForTexture(diffuseEaid);
            if (decoded == null || decoded.Bgra == null ||
                decoded.Width <= 0 || decoded.Height <= 0)
            {
                _tintedBrushCache[cacheKey] = null;
                return null;
            }

            int w = decoded.Width, h = decoded.Height;
            byte[] src = decoded.Bgra;
            byte[] dst = new byte[src.Length];
            for (int p = 0; p < w * h; p++)
            {
                int o = p * 4;
                byte b = src[o + 0];
                byte g = src[o + 1];
                byte r = src[o + 2];
                // max(R, G, B) -- catches any colour channel that's
                // visibly bright. Luminance (0.21R + 0.72G + 0.07B)
                // also works but underweights red wisps on a black
                // background, which is exactly what hair textures
                // tend to be.
                byte alpha = r > g ? r : g;
                if (b > alpha) alpha = b;
                dst[o + 0] = b;
                dst[o + 1] = g;
                dst[o + 2] = r;
                dst[o + 3] = alpha;
            }

            var bitmap = BitmapSource.Create(
                w, h, 96, 96, PixelFormats.Bgra32, null, dst, w * 4);
            bitmap.Freeze();
            var brush = new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
            };
            brush.Freeze();
            _tintedBrushCache[cacheKey] = brush;
            return brush;
        }

        private ImageBrush TryBuildMaskedBrush(string diffuseEaid, string maskEaid)
        {
            if (string.IsNullOrEmpty(diffuseEaid) || string.IsNullOrEmpty(maskEaid))
                return null;

            string cacheKey = $"{diffuseEaid}|mask:{maskEaid}";
            if (_tintedBrushCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var diffuse = GetPixelsForTexture(diffuseEaid);
            if (diffuse == null || diffuse.Bgra == null ||
                diffuse.Width <= 0 || diffuse.Height <= 0)
            {
                _tintedBrushCache[cacheKey] = null;
                return null;
            }

            var mask = GetPixelsForTexture(maskEaid);
            if (mask == null || mask.Bgra == null ||
                mask.Width <= 0 || mask.Height <= 0)
            {
                _tintedBrushCache[cacheKey] = null;
                return null;
            }

            int dw = diffuse.Width, dh = diffuse.Height;
            int mw = mask.Width, mh = mask.Height;

            // Triviality check: skip the composite when the mask is
            // effectively all-opaque. The shared template masks
            // (charname_*_m.tif) ship the ERP-tail mip at 8x8 with all
            // pixels ~255 -- the real cutout data is in the streamed
            // .mipmaps file. Applying the placeholder over the diffuse
            // would just slightly wash out the colour for no benefit.
            //
            // Threshold: if fewer than 1% of mask pixels have R < 200,
            // treat the mask as "no cutout needed" and return null so
            // the caller keeps using the un-masked diffuse brush.
            {
                int meaningful = 0;
                int total = mw * mh;
                for (int p = 0; p < total; p++)
                {
                    byte r = mask.Bgra[p * 4 + 2];
                    if (r < 200) meaningful++;
                }
                if (meaningful * 100 < total)
                {
                    _tintedBrushCache[cacheKey] = null;
                    return null;
                }
            }

            byte[] dst = new byte[diffuse.Bgra.Length];
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
                    // Multiply-blend the diffuse's authored alpha with
                    // the mask's R channel rather than replacing. See
                    // F1FbxExporter.TryWriteMaskedDiffusePng for the
                    // full reasoning; the short version is that the
                    // diffuse already encodes selective transparency
                    // for things like glove-fray wisps, and replacing
                    // its alpha with the mask drops that authored
                    // cutout.
                    dst[dOff + 0] = diffuse.Bgra[dOff + 0];
                    dst[dOff + 1] = diffuse.Bgra[dOff + 1];
                    dst[dOff + 2] = diffuse.Bgra[dOff + 2];
                    dst[dOff + 3] = (byte)(
                        (diffuse.Bgra[dOff + 3] * mask.Bgra[mOff + 2]) / 255);
                }
            }

            var bitmap = BitmapSource.Create(
                dw, dh, 96, 96, PixelFormats.Bgra32, null,
                dst, dw * 4);
            bitmap.Freeze();
            var brush = new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
            };
            brush.Freeze();
            _tintedBrushCache[cacheKey] = brush;
            return brush;
        }

        public DecodedTexture GetPixelsForTexture(string textureEaid)
        {
            if (_pixelCache.TryGetValue(textureEaid, out var cached))
                return cached;

            var (_, metaPath, payloadPath) =
                EaidPathResolver.ResolveTexturePaths(textureEaid, PackageRoot);

            if (metaPath == null || !File.Exists(metaPath) || !File.Exists(payloadPath))
            {
                _log($"  [pixels] Texture files missing for {textureEaid}");
                _pixelCache[textureEaid] = null;
                return null;
            }

            try
            {
                var desc = ImageDescriptor.FromFile(metaPath);
                byte[] payload = File.ReadAllBytes(payloadPath);

                // Texarray slicing: same logic as GetBrushForTexture. Decode
                // slice 0 only; ignore subsequent compound/variant slices.
                if (desc.IsTextureArray && payload.Length > 0)
                {
                    int sliceMipBytes = ComputeMipChainBytesForFormat(
                        desc.FormatCode, desc.Width, desc.Height, desc.MipCount);
                    if (sliceMipBytes > 0 && sliceMipBytes < payload.Length)
                    {
                        byte[] slice0 = new byte[sliceMipBytes];
                        Buffer.BlockCopy(payload, 0, slice0, 0, sliceMipBytes);
                        payload = slice0;
                    }
                }

                byte[] bgra;
                string fmt;
                if (desc.IsBC1)
                {
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC1(payload, desc.Width, desc.Height);
                    fmt = "BC1";
                }
                else if (desc.IsBC3)
                {
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC3(payload, desc.Width, desc.Height);
                    fmt = "BC3";
                }
                else if (desc.IsBC7)
                {
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC7(payload, desc.Width, desc.Height);
                    fmt = "BC7";
                }
                else if (desc.IsBC5)
                {
                    // Same R-channel grayscale interpretation as the
                    // GetBrushForTexture path -- see the long comment
                    // there. Used for tread diffuse atlases and any
                    // other BC5-stored content the compositor asks for.
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC5AsGrayscaleR(
                        payload, desc.Width, desc.Height);
                    fmt = "BC5/grayscale-R";
                }
                else if (desc.IsBC2)
                {
                    // Raw BC2 decode -- no carbon-weave luminance remap.
                    // The compositor wants the real pixel data, not the
                    // preview-hack grayscale. If a future code path needs
                    // the carbon-preview variant it can call
                    // GetBrushForTexture which applies the remap.
                    bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC2(
                        payload, desc.Width, desc.Height);
                    fmt = "BC2";
                }
                else
                {
                    _log($"  [pixels] Unsupported format {desc.FormatCode} for {textureEaid}");
                    _pixelCache[textureEaid] = null;
                    return null;
                }

                var result = new DecodedTexture
                {
                    Bgra = bgra,
                    Width = desc.Width,
                    Height = desc.Height,
                    FormatName = fmt,
                };
                _pixelCache[textureEaid] = result;
                return result;
            }
            catch (Exception ex)
            {
                _log($"  [pixels] Failed to decode {textureEaid}: {ex.Message}");
                _pixelCache[textureEaid] = null;
                return null;
            }
        }

        /// <summary>
        /// Diagnostic dump of an 8x8 density grid of the driver atlas --
        /// percentage of pixels with source alpha >= 128 in each cell. The
        /// grid is indexed by normalised (u, v) so it can be read directly
        /// against the per-submesh 'final UV=[uMin,uMax]x[vMin,vMax]' log
        /// lines: a submesh whose UV rect falls entirely over 0.0% cells
        /// won't show any digit, no matter what colour the bake uses.
        ///
        /// Cached by EAID so a car with ten number submeshes emits the grid
        /// exactly once per unique driver texture.
        /// </summary>
        private void LogDriverAtlasDensityGrid(DecodedTexture tex, string textureEaid)
        {
            if (tex == null || tex.Bgra == null || tex.Width <= 0 || tex.Height <= 0)
                return;
            if (_densityLogged.Contains(textureEaid)) return;
            _densityLogged.Add(textureEaid);

            const int N = 8; // 8x8 grid
            int W = tex.Width, H = tex.Height;
            byte[] p = tex.Bgra;
            int[,] visible = new int[N, N];
            int[,] total = new int[N, N];

            for (int y = 0; y < H; y++)
            {
                int cy = Math.Min(N - 1, (y * N) / H);
                for (int x = 0; x < W; x++)
                {
                    int cx = Math.Min(N - 1, (x * N) / W);
                    total[cx, cy]++;
                    byte a = p[(y * W + x) * 4 + 3];
                    if (a >= 128) visible[cx, cy]++;
                }
            }

            _log($"  DRIVER-ATLAS DENSITY GRID {Path.GetFileName(textureEaid)} {W}x{H}");
            _log($"    U increases rightward, V increases downward. Values are % visible.");
            _log($"                  u=[0,1/8) u=[1/8,2/8) ... u=[7/8,1]");
            for (int cy = 0; cy < N; cy++)
            {
                double vMin = (double)cy / N, vMax = (double)(cy + 1) / N;
                var sb = new StringBuilder();
                sb.Append($"    v=[{vMin:F3},{vMax:F3}) ");
                for (int cx = 0; cx < N; cx++)
                {
                    double pct = total[cx, cy] > 0
                        ? 100.0 * visible[cx, cy] / total[cx, cy]
                        : 0.0;
                    sb.Append($"{pct,5:F1}% ");
                }
                _log(sb.ToString());
            }
        }

        private readonly HashSet<string> _densityLogged = new HashSet<string>();
        /// <summary>
        /// True when the currently-loaded ERP lives under an "F1 2021"
        /// game install (as populated by F1PackageLocator.Last in MainWindow).
        /// Excludes F1 2022/2023/2024 installs defensively.
        /// </summary>
        private static bool IsF1_2021_Install()
        {
            var located = F1PackageLocator.Last;
            if (located == null || string.IsNullOrEmpty(located.GameRoot))
                return false;
            string rootName = Path.GetFileName(
                located.GameRoot.TrimEnd(Path.DirectorySeparatorChar,
                                         Path.AltDirectorySeparatorChar)) ?? "";
            return rootName.IndexOf("F1 2021", StringComparison.OrdinalIgnoreCase) >= 0
                && rootName.IndexOf("F1 2022", StringComparison.OrdinalIgnoreCase) < 0
                && rootName.IndexOf("F1 2023", StringComparison.OrdinalIgnoreCase) < 0
                && rootName.IndexOf("F1 2024", StringComparison.OrdinalIgnoreCase) < 0;
        }
        /// <summary>
        /// Build a frozen ImageBrush from a raw BGRA32 pixel buffer. Used
        /// by the compositor to turn a freshly-blended number texture into
        /// something WPF can render. Matches the brush settings
        /// (TileMode.Tile + Absolute viewport units) used by GetBrushForTexture
        /// so behaviour stays consistent.
        /// </summary>
        public static ImageBrush CreateBrushFromPixels(byte[] bgra, int width, int height)
        {
            var bitmap = BitmapSource.Create(
                width, height,
                96, 96,
                PixelFormats.Bgra32, null,
                bgra, width * 4);
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
            };
            brush.Freeze();
            return brush;
        }
    }
}
