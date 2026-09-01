using System;
using System.Collections.Generic;

namespace F1modding3dtoolV1
{
    /// <summary>
    /// Shader-aware texture picker for Codemasters NEFX2 materials.
    ///
    /// Replaces the old heuristic-based picker and overlay code in
    /// F1ModelLoader / EgoTexturePipeline with a table-driven approach:
    /// given the shader EAID of a material, look up which sampler slot
    /// provides the visible base texture and which provides the overlay
    /// (logo / sponsor / number) that composites on top.
    ///
    /// Why: the old code layered three brittle heuristics on top of each
    /// other (material-name contains "carbon", preview-picker-returned-slot
    /// contains "logo", vb=4 UV range fits inside [0,1]x[0,1]). That chain
    /// produced inconsistent results -- e.g. bargeboard_fr getting a
    /// sponsor overlay while bargeboard_fl didn't, despite identical
    /// submesh slot data. Swapping to a shader-indexed table makes the
    /// decision deterministic: if a material's shader is vehicle_metallic_paint,
    /// its base is always me_albedoMap and its overlay is always me_logoMap,
    /// regardless of which mesh part it's applied to.
    ///
    /// Verified against every .material file in the 2021 McLaren package:
    ///   shader_package/vehicle_paint_v2/vehicle_metallic_paint.nefx2
    ///   shader_package/vehicle_carbon/vehicle_carbon.nefx2
    ///   shader_package/vehicle_generic/vehicle_generic.nefx2
    ///   shader_package/vehicle_generic/vehicle_wheel.nefx2
    ///   shader_package/vehicle_steering/vehicle_steering.nefx2
    ///   shader_package/f1_car_mirror/f1_car_mirror_left.nefx2  (and _right)
    ///   shader_package/vehicle_rainbeads/vehicle_rainbeads.nefx2
    ///   shader_package/vehicle_generic/vehicle_shadow.nefx2
    /// </summary>
    public static class MaterialSlotResolver
    {
        /// <summary>
        /// What we recovered from a material: which sampler slot to read as the
        /// base diffuse texture, and (optionally) which to read as an overlay
        /// atlas composited on top of the base via the submesh's secondary UV.
        /// </summary>
        public struct Resolution
        {
            /// <summary>Sampler slot chosen as the base colour. Never null if any resolution succeeded.</summary>
            public string BaseSlot;
            /// <summary>Texture EAID bound to BaseSlot.</summary>
            public string BaseTextureEaid;

            /// <summary>Sampler slot chosen as the overlay, or null if this shader has no overlay pass.</summary>
            public string OverlaySlot;
            /// <summary>Texture EAID bound to OverlaySlot, or null.</summary>
            public string OverlayTextureEaid;

            /// <summary>True when the submesh should be rendered with a second,
            /// z-offset pass that reads OverlayTextureEaid via UV pair 2.</summary>
            public bool HasOverlay => !string.IsNullOrEmpty(OverlayTextureEaid);

            /// <summary>Which shader family the material uses, purely for diagnostics.</summary>
            public ShaderFamily Shader;

            /// <summary>When resolution fails, a one-line explanation for the log.</summary>
            public string FailureReason;

            /// <summary>
            /// True when this is a "number" / "carbon number" submesh whose
            /// visible content is a driver-digit decal. The TextureCache uses
            /// this to trigger CPU-side compositing: the number atlas
            /// (me_logoMap, e.g. mclaren_driver_N.tif) is alpha-blended onto
            /// the paint/carbon colour from me_albedoMap / me_diffuseMap,
            /// producing a single fully-opaque brush that renders without
            /// WPF's depth-hole artefact.
            /// </summary>
            public bool IsNumberSubmesh;

            /// <summary>
            /// For number submeshes: the EAID of the texture whose colour
            /// should fill the transparent areas of the number atlas during
            /// compositing (me_albedoMap for paint-family numbers,
            /// me_diffuseMap for carbon-family numbers). Null for non-number
            /// submeshes.
            /// </summary>
            public string BackgroundTextureEaid;
        }

        public enum ShaderFamily
        {
            Unknown,
            VehicleMetallicPaint,   // paint, number
            VehicleCarbon,          // carbon*, carbon_number_*
            VehicleGeneric,         // detail, generics, rear_light, indicator_lights, generic_lcd, revs
            VehicleWheel,           // wheel_hub, disc
            VehicleSteering,        // steeringwheel
            F1CarMirror,            // mirror_left, mirror_right
            VehicleRainbeads,       // rainbeads
            VehicleShadow,          // shadow
            VehicleTyre,            // tyre_sidewall*, tyre_tread*, tyre_slick*, tyre_wet*, tyre_inter*
            CharacterHelmet,        // char_helmet_skinned + every helmet-family shader (visor, attachments, ...)
            CharacterCloth,         // char_cloth_skinned* (suit, gloves, harness, hans, livery_v2, incar_livery_v2)
            CharacterSkin,          // skin_detail_* (arm, exposed skin)
        }

        /// <summary>
        /// Primary entry point: given a parsed material AND the mesh slot name
        /// the submesh declares ("body", "number", "carbon_tile_1", "carbon number",
        /// etc.), figure out the base + overlay texture bindings for the shader
        /// and slot in use. Both returned slots are real keys in mat.BindingsBySlot
        /// when non-null.
        ///
        /// The slot name carries the mesh's intent and is essential for the
        /// paint and carbon shader families because both have multiple
        /// distinct uses sharing the same shader:
        ///
        ///   vehicle_metallic_paint.nefx2 is used by:
        ///     "body"   submeshes -> base = paint atlas,  overlay = sponsor decal
        ///     "number" submeshes -> base = DRIVER NUMBER, overlay = none
        ///                           (the number IS the base; the paint behind
        ///                           it is what the driver number is plastered
        ///                           on top of in the game shader, but the
        ///                           visible thing on this submesh is the
        ///                           number itself)
        ///
        ///   vehicle_carbon.nefx2 is used by:
        ///     "carbon_tile_N" / "carbon tile N" -> base = cf_pattern weave,
        ///                                          overlay = sponsor decal where
        ///                                          authored
        ///     "carbon number"                    -> base = DRIVER NUMBER,
        ///                                          overlay = none
        ///
        /// Without the slot name we couldn't tell those apart from the material
        /// alone, and number panels would render as plain paint with no number
        /// visible (the symptom you saw before this overload was added).
        ///
        /// The single-arg overload is kept for callers that haven't been
        /// updated yet; it routes through the same logic but assumes the
        /// material is being applied to its primary intent (body/carbon_tile).
        /// </summary>
        public static Resolution Resolve(MaterialFile mat, string materialEaidHint = null,
                                         string slotName = null)
        {
            var r = new Resolution();

            if (mat == null || mat.BindingsBySlot == null || mat.BindingsBySlot.Count == 0)
            {
                r.FailureReason = "material has no sampler bindings";
                return r;
            }

            r.Shader = ClassifyShader(mat.ShaderEaid);

            // Identify the slot's role from its name. Mesh slot names are
            // authored consistently by the game's pipeline -- "number" and
            // "carbon number" always mean the driver-decal layer, "body"
            // means the painted bodywork, "carbon_tile_N" means a carbon
            // weave panel.
            bool isNumberSlot = SlotIsNumber(slotName);
            bool isBodySlot = SlotIs(slotName, "body");
            bool isCarbonSlot = SlotIsCarbonTile(slotName);

            // Older callers that don't pass a slot fall back to the material-
            // name hint heuristic. If the material EAID contains "number_"
            // and we have no explicit slot, treat as a number submesh -- this
            // matches what mclaren_number_N / mclaren_carbon_number_N
            // materials would intend.
            if (slotName == null)
            {
                isNumberSlot = !string.IsNullOrEmpty(materialEaidHint) &&
                               materialEaidHint.IndexOf("number_", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            switch (r.Shader)
            {
                case ShaderFamily.VehicleMetallicPaint:
                    ResolvePaint(mat, isNumberSlot, ref r);
                    break;
                case ShaderFamily.VehicleCarbon:
                    ResolveCarbon(mat, isNumberSlot, isCarbonSlot, ref r);
                    break;
                case ShaderFamily.VehicleGeneric:
                    ResolveGeneric(mat, ref r);
                    break;
                case ShaderFamily.VehicleWheel:
                    ResolveGeneric(mat, ref r);
                    break;
                case ShaderFamily.VehicleSteering:
                    ResolveSteering(mat, ref r);
                    break;
                case ShaderFamily.VehicleRainbeads:
                    ResolveRainbeads(mat, ref r);
                    break;
                case ShaderFamily.VehicleTyre:
                    ResolveTyre(mat, ref r);
                    break;
                case ShaderFamily.CharacterHelmet:
                    // Helmet / visor / attachments / wing / character body
                    // family all use the same me_diffuseTex base binding
                    // (aliased to me_diffuseMap by MaterialReader). Generic
                    // picker does the right thing: pick whichever diffuse
                    // map binding is present.
                    ResolveGeneric(mat, ref r);
                    break;
                case ShaderFamily.CharacterCloth:
                    ResolveCharacterCloth(mat, ref r);
                    break;
                case ShaderFamily.CharacterSkin:
                    ResolveGeneric(mat, ref r);
                    break;
                case ShaderFamily.F1CarMirror:
                case ShaderFamily.VehicleShadow:
                    ResolveUnknownByHeuristic(mat, ref r);
                    break;
                default:
                    ResolveUnknownByHeuristic(mat, ref r);
                    break;
            }

            if (string.IsNullOrEmpty(r.BaseTextureEaid) && string.IsNullOrEmpty(r.FailureReason))
                r.FailureReason = $"shader={r.Shader} has no resolvable base slot in this material";

            return r;
        }

        // Slot-name predicates. The game uses both underscore and space
        // variants ("carbon_tile_1" vs "carbon tile 4"); accept either.

        private static bool SlotIs(string slot, string token)
        {
            if (string.IsNullOrEmpty(slot)) return false;
            return string.Equals(slot.Trim(), token, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SlotIsNumber(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return false;
            string s = slot.Trim();
            // Matches "number" and "carbon number" / "carbon_number".
            return s.Equals("number", StringComparison.OrdinalIgnoreCase) ||
                   s.Equals("carbon number", StringComparison.OrdinalIgnoreCase) ||
                   s.Equals("carbon_number", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SlotIsCarbonTile(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return false;
            string s = slot.Trim().ToLowerInvariant();
            return (s.StartsWith("carbon_tile") || s.StartsWith("carbon tile"));
        }

        // =====================================================================
        // SHADER CLASSIFICATION
        // =====================================================================

        /// <summary>
        /// Shader EAIDs look like:
        ///   eaid://shader_package/vehicle_paint_v2/vehicle_metallic_paint.nefx2
        ///   eaid://shader_package/vehicle_carbon/vehicle_carbon.nefx2
        /// etc. We only need to pattern-match the last path segment to know
        /// which family the material belongs to.
        /// </summary>
        private static ShaderFamily ClassifyShader(string shaderEaid)
        {
            if (string.IsNullOrEmpty(shaderEaid)) return ShaderFamily.Unknown;

            // Case-insensitive contains, keyed on the shader's unique leaf name.
            //
            // The body-paint family covers EVERY variant Codemasters has
            // shipped under "vehicle_*paint" in the v2 shader package:
            //   * vehicle_metallic_paint  (McLaren, Red Bull, Williams, ...)
            //   * vehicle_gloss_paint     (Ferrari, Haas on F1 25/26)
            //   * vehicle_matte_paint     (occasional matte liveries)
            //   * vehicle_neon_paint      (special-event liveries)
            // They all share the same sampler layout (me_albedoMap base +
            // me_logoMap overlay for the sponsor sheet); only the surface-
            // material shading model differs. Treating just the "metallic"
            // leaf as the paint family was a 2021-era assumption that left
            // Ferrari / Haas body sponsor decals silently un-rendered on
            // 2025/2026 ERPs because their paint shader is "gloss_paint",
            // not "metallic_paint", so the family fell through to Unknown
            // and ResolvePaint never ran.
            if (Has(shaderEaid, "vehicle_metallic_paint") ||
                Has(shaderEaid, "vehicle_gloss_paint") ||
                Has(shaderEaid, "vehicle_matte_paint") ||
                Has(shaderEaid, "vehicle_neon_paint") ||
                Has(shaderEaid, "vehicle_paint"))
                return ShaderFamily.VehicleMetallicPaint;
            if (Has(shaderEaid, "vehicle_carbon")) return ShaderFamily.VehicleCarbon;
            if (Has(shaderEaid, "vehicle_steering")) return ShaderFamily.VehicleSteering;
            if (Has(shaderEaid, "vehicle_wheel")) return ShaderFamily.VehicleWheel;
            if (Has(shaderEaid, "vehicle_rainbeads")) return ShaderFamily.VehicleRainbeads;
            if (Has(shaderEaid, "vehicle_shadow")) return ShaderFamily.VehicleShadow;
            if (Has(shaderEaid, "f1_car_mirror")) return ShaderFamily.F1CarMirror;

            // Classic-tyre shader family covers every "tyre_*" leaf name we've
            // seen in F1 2018: tyre_sidewall, tyre_sidewall_no_logo, tyre_tread,
            // tyre_slick, tyre_wet, tyre_intermediate. The shader EAID format is
            // shader_package/tyre_<role>/tyre_<role>[_variant].nefx2 so a single
            // "tyre_" substring match covers them all without needing a per-leaf
            // table. ResolveTyre keys off whichever me_*Map binding is present
            // -- diffuse first, normal/surface as fallbacks.
            if (Has(shaderEaid, "tyre_") || Has(shaderEaid, "/tyre/")) return ShaderFamily.VehicleTyre;

            // Character cloth family (race-suits, gloves, harness, HANS,
            // livery_v2, incar_livery_v2). Shader EAIDs look like
            // shader_package/character_cloth/char_cloth_skinned[_8weights|_in_car].nefx2.
            // Base diffuse lives on me_baseDiffuseTex (NOT me_diffuseTex),
            // which is what makes this family distinct from the helmet
            // family below.
            if (Has(shaderEaid, "char_cloth") || Has(shaderEaid, "character_cloth"))
                return ShaderFamily.CharacterCloth;

            // Character skin family (arm, exposed skin). Shader EAIDs look
            // like shader_package/skin/skin_detail_*.nefx2. Diffuse on bare
            // me_diffuseTex, same as helmet -- routed through the generic
            // picker.
            if (Has(shaderEaid, "/skin/") || Has(shaderEaid, "skin_detail"))
                return ShaderFamily.CharacterSkin;

            // Character helmet family. F1 25 helmet materials reference
            // shaders like shader_package/character_helmet/char_helmet_skinned.nefx2
            // (also char_helmet_visor, char_helmet_transparent, char_helmet_decal,
            // char_body_skinned etc.). All of them carry their diffuse on
            // me_diffuseTex (aliased to me_diffuseMap by MaterialReader), so
            // a single substring match covers the whole family without an
            // exhaustive table.
            if (Has(shaderEaid, "char_helmet") || Has(shaderEaid, "char_body") ||
                Has(shaderEaid, "character_helmet") || Has(shaderEaid, "character_body") ||
                Has(shaderEaid, "/character_"))
                return ShaderFamily.CharacterHelmet;

            // vehicle_generic covers detail/generics/rear_light/indicator/lcd/revs
            // and should be the catch-all for other "vehicle_*" shaders.
            if (Has(shaderEaid, "vehicle_generic")) return ShaderFamily.VehicleGeneric;

            return ShaderFamily.Unknown;
        }

        // =====================================================================
        // SHADER-SPECIFIC RESOLVERS
        // =====================================================================

        /// <summary>
        /// vehicle_metallic_paint.nefx2:
        ///
        /// For a "body" submesh the visible base is the team paint atlas
        /// (me_albedoMap) and the sponsor sheet rides on top through
        /// me_logoMap as a UV-pair-2 overlay.
        ///
        /// For a "number" submesh the visible base is the DRIVER NUMBER
        /// itself (me_logoMap, which on a number_N material binds to
        /// mclaren_driver_N.tif). The body paint behind it (me_albedoMap)
        /// is what the number sits on in the game shader, but the geometry
        /// for the number submesh is a tiny patch UV-mapped to a small
        /// strip of the paint atlas; rendering paint there would leak the
        /// underlying body paint colour through, hiding the number entirely.
        /// We therefore promote me_logoMap to base and skip the overlay
        /// pass on number submeshes.
        ///
        /// Slot-name predicates make this decision -- not the material name.
        /// Looking only at the material EAID we'd see "number_1" but couldn't
        /// distinguish it from a (hypothetical) other use; the mesh slot
        /// declares the geometry's intent unambiguously.
        /// </summary>
        private static void ResolvePaint(MaterialFile mat, bool isNumberSlot, ref Resolution r)
        {
            if (isNumberSlot)
            {
                // Per general rule: "if the number does NOT load like the
                // decals, then it MUST load like the decals."
                //
                // Previously this branch swapped base and overlay:
                //   BaseSlot = me_logoMap (driver atlas)
                //   OverlaySlot = null    (no overlay pass)
                //   BackgroundTextureEaid = paint for CPU-side compositing
                //
                // That made number submeshes go through a custom opaque-bake
                // path in EgoTexturePipeline to sidestep WPF alpha quirks.
                // The bake produced a coloured rectangle with a sliver of
                // digit visible -- not what we want.
                //
                // Unified with the body path below: paint is the base, the
                // driver atlas is the overlay. Numbers now share the exact
                // two-pass pipeline that sponsors use: main pass samples
                // paint via vb=3 pair-1 UVs, overlay pass samples the
                // driver atlas via vb=3 pair-2 UVs, overlay is alpha-
                // blended on top. Whatever rendering characteristics
                // sponsors have on the current build, numbers now have too.
                Pick(mat, "me_albedoMap", ref r.BaseSlot, ref r.BaseTextureEaid);
                PickAnyOverlay(mat, ref r.OverlaySlot, ref r.OverlayTextureEaid);
                r.IsNumberSubmesh = true;
                return;
            }

            // Default: body submesh.
            Pick(mat, "me_albedoMap", ref r.BaseSlot, ref r.BaseTextureEaid);
            PickAnyOverlay(mat, ref r.OverlaySlot, ref r.OverlayTextureEaid);
        }

        /// <summary>
        /// vehicle_carbon.nefx2:
        ///
        /// Samplers (in 2021 McLaren pkg):
        ///   me_diffuseMap  -> cf_pattern_{N}_d.tif (carbon weave diffuse)
        ///   me_logoMap     -> overlay atlas (sponsor sheet or driver number)
        ///   me_normalMap   -> cf_pattern_{N}_n.tif (skipped; normal only)
        ///   me_dirtMap, me_damageMap -> skipped
        ///
        /// Slot-specific behaviour:
        ///
        ///   "carbon number" / "carbon_number" submesh:
        ///     base = me_logoMap (-> mclaren_driver_N.tif). Same reasoning as
        ///     number paint: the carbon-number geometry is a small overlay
        ///     panel whose UVs index a number, not the carbon weave. Rendering
        ///     the cf_pattern weave at those UVs (which often span 0..16)
        ///     produces the noisy garbled carbon you see when this is
        ///     misclassified.
        ///     No overlay -- the number IS the layer.
        ///
        ///   "carbon_tile_N" submesh:
        ///     base = me_diffuseMap (-> cf_pattern weave). NO overlay -- in
        ///     2021 the carbon panels in plain "tile" slots are dry weave with
        ///     no decals on top. Adding a sponsor overlay here put sponsors on
        ///     bargeboards, undertrays and other dry-carbon surfaces, which
        ///     was a visible regression. The actual sponsor placements live
        ///     on the "body" submeshes that have me_logoMap-friendly UVs.
        ///
        /// Older pre-slot-aware caller (slotName=null) falls through to the
        /// material-name hint -- if mat name contains "number_" it's treated
        /// as the number case, otherwise as a tile.
        /// </summary>
        private static void ResolveCarbon(MaterialFile mat, bool isNumberSlot, bool isCarbonTileSlot,
                                          ref Resolution r)
        {
            if (isNumberSlot)
            {
                // Per general rule (see paint-number branch): numbers
                // load like decals. For carbon-family materials that
                // means: carbon weave is the base, driver atlas is the
                // overlay -- same setup as a regular carbon body panel
                // with sponsors.
                Pick(mat, "me_diffuseMap", ref r.BaseSlot, ref r.BaseTextureEaid);
                PickAnyOverlay(mat, ref r.OverlaySlot, ref r.OverlayTextureEaid);
                r.IsNumberSubmesh = true;
                return;
            }

            // Base is always the carbon weave.
            Pick(mat, "me_diffuseMap", ref r.BaseSlot, ref r.BaseTextureEaid);

            // FIX 20: provide me_logoMap as overlay for carbon submeshes.
            // An earlier version unconditionally suppressed carbon overlays
            // because it placed sponsors on undertrays and dry-carbon
            // panels. The real guard is the UV range check in
            // TryBuildSponsorOverlay (full-atlas / tiling rejection):
            // undertray panels whose vb=4 UVs tile or span the full atlas
            // are rejected there. Panels with proper decal-window UVs
            // (authored sponsor placements) pass through. Suppressing the
            // overlay here rather than letting the UV gate decide caused
            // sponsors to be missing on carbon body panels that legitimately
            // carry them.
            PickAnyOverlay(mat, ref r.OverlaySlot, ref r.OverlayTextureEaid);
        }

        /// <summary>
        /// vehicle_generic.nefx2 and vehicle_wheel.nefx2:
        ///   Samplers:
        ///     me_diffuseMap  -> diffuse                                ** BASE
        ///     me_normalMap   -> normal (skipped)
        ///     me_surfaceMap  -> roughness/metallic (skipped)
        ///
        /// No overlay pass on generic/wheel shaders.
        /// </summary>
        private static void ResolveGeneric(MaterialFile mat, ref Resolution r)
        {
            Pick(mat, "me_diffuseMap", ref r.BaseSlot, ref r.BaseTextureEaid);
        }

        /// <summary>
        /// char_cloth_skinned*.nefx2 (race-suit / gloves / harness / HANS /
        /// livery_v2 / incar_livery_v2 / suit_attachments) AND the related
        /// char_logo_skinned*.nefx2 family (livery_v2_logo, gloves_logo,
        /// suit_attachments_logo). Authored sampler slot names per shader:
        ///
        ///   cloth body samplers:
        ///     me_baseDiffuseTex / me_baseDiffuseMap   ** BASE
        ///     me_baseNormalTex   (skipped for preview)
        ///     me_detailNormalTex (skipped)
        ///     me_maskTex         (livery mask, skipped)
        ///     me_surfaceTex      (PBR surface, skipped)
        ///
        ///   logo overlay samplers:
        ///     me_logoDiffuseTex / me_logoDiffuseMap   ** BASE
        ///     me_logoNormalTex / me_logoSurfaceTex  (skipped)
        ///     me_msdfTex                            (text MSDF, skipped)
        ///
        /// Try the body form, the logo form, then bare diffuse in priority
        /// order. First binding present wins -- works regardless of which
        /// specific cloth shader variant the material targets.
        /// </summary>
        private static void ResolveCharacterCloth(MaterialFile mat, ref Resolution r)
        {
            Pick(mat, "me_baseDiffuseMap", ref r.BaseSlot, ref r.BaseTextureEaid);
            if (string.IsNullOrEmpty(r.BaseTextureEaid))
                Pick(mat, "me_logoDiffuseMap", ref r.BaseSlot, ref r.BaseTextureEaid);
            if (string.IsNullOrEmpty(r.BaseTextureEaid))
                Pick(mat, "me_diffuseMap", ref r.BaseSlot, ref r.BaseTextureEaid);
        }

        /// <summary>
        /// vehicle_steering.nefx2:
        ///   Samplers:
        ///     me_diffuseMap  -> steering wheel diffuse                 ** BASE
        ///     me_logoMap     -> steering_wheel_decal_da.tif            ** OVERLAY
        ///     me_normalMap   -> normal (skipped)
        ///     me_surfaceMap  -> roughness/metallic (skipped)
        ///
        /// Steering wheel does carry an overlay (the team logo in the middle
        /// of the wheel), so we expose it the same way paint does.
        /// </summary>
        private static void ResolveSteering(MaterialFile mat, ref Resolution r)
        {
            Pick(mat, "me_diffuseMap", ref r.BaseSlot, ref r.BaseTextureEaid);
            Pick(mat, "me_logoMap", ref r.OverlaySlot, ref r.OverlayTextureEaid);
        }

        /// <summary>
        /// vehicle_rainbeads.nefx2:
        ///   Samplers (in 2021 McLaren pkg):
        ///     me_rainBeadsIntensityMap -> rainbeads_large_i.tif        ** BASE
        ///     me_normalMap             -> skipped
        ///
        /// The rainbeads shader is what draws the water droplets on bodywork
        /// when wet. For a dry-weather preview it's a near-transparent layer
        /// on top of every paint / carbon panel. We expose it as a base
        /// layer with no overlay; the caller can choose to skip rendering
        /// this submesh entirely if it wants a clean dry preview.
        /// </summary>
        private static void ResolveRainbeads(MaterialFile mat, ref Resolution r)
        {
            Pick(mat, "me_rainBeadsIntensityMap", ref r.BaseSlot, ref r.BaseTextureEaid);

            // Older packages use me_diffuseMap; fall back for safety.
            if (string.IsNullOrEmpty(r.BaseTextureEaid))
                Pick(mat, "me_diffuseMap", ref r.BaseSlot, ref r.BaseTextureEaid);
        }

        /// <summary>
        /// Classic-tyre shader resolver. The F1 2018 tyre shader family
        /// (tyre_sidewall, tyre_sidewall_no_logo, tyre_tread, tyre_slick,
        /// tyre_wet, tyre_intermediate) uses these texture sampler slots:
        ///
        ///   me_diffuseMap  -> c_NN_<role>_diffuse.texarray  (multi-slice
        ///                     compound-keyed colour atlas; what we want)
        ///   me_normalMap   -> c_NN_<role>_n.tif             (BC5 normal map;
        ///                     can't be previewed sensibly as colour and
        ///                     would render purplish-blue if mistakenly used)
        ///   me_surfaceMap  -> c_NN_<role>_s.tif             (specular/surface
        ///                     mask; same issue as the normal map)
        ///
        /// We always pick me_diffuseMap. The texarray decoder in
        /// TextureCache.GetBrushForTexture handles the multi-slice payload
        /// and decodes slice 0 (the canonical compound) to produce the
        /// preview brush.
        ///
        /// Why we DON'T fall back to normal/surface: the old generic
        /// "Unknown shader" path tried me_albedoMap, me_diffuseMap,
        /// me_rainBeadsIntensityMap, me_surfaceMap in order, picking the
        /// first that resolved. For tyre materials before the .texarray
        /// parser fix, me_diffuseMap was silently dropped, so the picker
        /// fell through to me_surfaceMap (the BC5 specular) -- which the
        /// decoder then refused as "Unsupported image format 65" and the
        /// tyres rendered as gray. Limiting the tyre resolver to ONLY
        /// me_diffuseMap means tyres with no diffuse binding (none seen in
        /// the wild, but defensively) will report a clean "no diffuse
        /// binding" failure rather than incorrectly rendering the normal
        /// map's purple as a colour.
        /// </summary>
        private static void ResolveTyre(MaterialFile mat, ref Resolution r)
        {
            Pick(mat, "me_diffuseMap", ref r.BaseSlot, ref r.BaseTextureEaid);

            if (string.IsNullOrEmpty(r.BaseTextureEaid))
                r.FailureReason = "tyre material has no me_diffuseMap binding";

            // Tyres get no overlay pass -- the sidewall logo is part of the
            // diffuse atlas in this package layout, not a separate decal.
        }

        /// <summary>
        /// Unknown-shader fallback: pick something sensible without knowing
        /// the shader semantics. Follows the same priority as the old
        /// EgoTexturePipeline picker so behaviour for un-classified shaders
        /// is identical to before. Used for mirror/shadow/unknown shaders.
        /// </summary>
        private static void ResolveUnknownByHeuristic(MaterialFile mat, ref Resolution r)
        {
            string[] preferredBase = {
                "me_albedoMap", "me_diffuseMap", "me_rainBeadsIntensityMap",
                "me_surfaceMap",
            };
            foreach (var slot in preferredBase)
            {
                Pick(mat, slot, ref r.BaseSlot, ref r.BaseTextureEaid);
                if (!string.IsNullOrEmpty(r.BaseTextureEaid)) break;
            }

            // Unknown shaders don't get overlays -- we can't guarantee a
            // decal-style sampler would be correctly composited without
            // knowing the shader's blend rules.
        }

        // =====================================================================
        // HELPERS
        // =====================================================================

        /// <summary>
        /// Case-insensitive dictionary fetch, writes (slot, tex) to the refs only
        /// if both are non-null. No-op if the binding is absent or empty.
        /// </summary>
        private static void Pick(MaterialFile mat, string slotName,
                                 ref string outSlot, ref string outEaid)
        {
            if (mat.BindingsBySlot.TryGetValue(slotName, out var eaid) &&
                !string.IsNullOrEmpty(eaid))
            {
                outSlot = slotName;
                outEaid = eaid;
            }
        }

        // Probe several sampler slot names in priority order, accepting the
        // first one that's actually bound on the material. Used for the
        // car overlay (sponsor / decal / logo) pick, where the sampler name
        // varies by team and game year:
        //
        //   me_logoMap       -- McLaren, Red Bull, most 2021-2025 teams
        //   me_decalMap      -- some teams ship sponsor sheets under this name
        //   me_sponsorMap    -- pre-2021 / classic naming, occasionally seen
        //                       in modern packages
        //
        // 2026 ERPs revealed Ferrari and Haas paint materials lack me_logoMap
        // entirely. Their sponsor sheet ships under one of the other slot
        // names, so the old single-name Pick("me_logoMap") returned null and
        // the overlay pass was silently skipped (visible as: "no body decals
        // on Ferrari / Haas while carbon decals still work" -- carbon uses a
        // different material path that happened to bind the same way as the
        // 2021 cars). The Legacy loader already has a multi-name probe of
        // this form (F1ModelLoaderLegacy.cs line 967); mirror it here for
        // the modern shader-aware resolver.
        private static void PickAnyOverlay(MaterialFile mat,
                                           ref string outSlot, ref string outEaid)
        {
            string[] overlaySlots = { "me_logoMap", "me_decalMap", "me_sponsorMap" };
            foreach (var slot in overlaySlots)
            {
                if (mat.BindingsBySlot.TryGetValue(slot, out var eaid) &&
                    !string.IsNullOrEmpty(eaid))
                {
                    outSlot = slot;
                    outEaid = eaid;
                    return;
                }
            }
        }

        private static bool Has(string s, string needle)
            => s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}