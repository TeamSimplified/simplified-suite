using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Media3D = System.Windows.Media.Media3D;
// v13.1: namespaces VERIFIED against the HelixToolkit vNext source
// (develop3 -- the generation the installed package comes from; the
// giveaway was System.Numerics math):
//   HelixToolkit             -> Vector3Collection / IntCollection / Vector2Collection
//   HelixToolkit.Maths       -> Color4
//   HelixToolkit.SharpDX     -> MeshGeometry3D, TextureModel, DefaultEffectsManager
//   HelixToolkit.Wpf.SharpDX -> Viewport3DX, MeshGeometryModel3D, PBRMaterial,
//                               lights, GroupModel3D, Element3D, cameras
//   System.Numerics          -> Vector3 / Vector2 (static Normalize etc.)
using System.Numerics;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Shaders;
using HelixToolkit.Wpf.SharpDX;
using Color = System.Windows.Media.Color;

namespace SimplifiedUniversalExporter
{
    /// <summary>
    /// v16.0: REFERENCE-MATCHED SHOWROOM. The whole scene is now derived
    /// from pixel measurements of the F1 2018 pre-rendered showroom shot
    /// (1920x1080 McLaren papaya reference) plus a source-level audit of
    /// the installed HelixToolkit generation (develop3). Three findings
    /// explain every symptom fought through v13-v15:
    ///
    ///   1. EnvironmentMap3D RENDERS ITS SKYBOX by default. The radial
    ///      "reference backdrop" ImageBrush (v13.4) was never visible --
    ///      the viewport background was the cubemap's crude vertical
    ///      gradient the whole time. v16.1: instead of hiding the skybox
    ///      and betting on D3DImage alpha compositing to reveal the WPF
    ///      brush (a mechanism no build has ever actually proven -- the
    ///      skybox was always painting over it), the skybox IS the
    ///      backdrop now: the cubemap sides carry the fitted reference
    ///      field mapped into direction space through the default
    ///      showroom camera, so that camera's view of the sphere
    ///      reproduces the reference frame (validated at 4.4/255 mean
    ///      error -- identical to the screen-space fit). The Background
    ///      brush stays set behind it as a redundant fallback only.
    ///
    ///   2. AmbientLight3D on PBR materials is a FLAT ADDITIVE WASH.
    ///      psMeshPBR.hlsl ends with "color += vLightAmbient.rgb" -- not
    ///      modulated by albedo. That flat add IS the grey film / fog of
    ///      v14.5-v14.9; no dose ever fixes it. Ambient is now near-black
    ///      and soft light comes from the per-material IrradianceMap,
    ///      which the shader multiplies by albedo (physically correct:
    ///      black stays black, papaya glows warm).
    ///
    ///   3. With v15.1 "nothing set", the env map contributed ZERO light:
    ///      c_spec = 0.16 * ReflectanceFactor^2 and the factor defaults
    ///      to 0, so c_spec * Specular_IBL() == 0. "The IBL lights the
    ///      car" was never true. ReflectanceFactor is now 0.75 (F0 ~
    ///      0.09, automotive lacquer) so the paint genuinely reflects
    ///      the cove and the ceiling softbox.
    ///
    /// Reference measurements baked in below (see BuildReferenceBackdrop
    /// for the fitted field, BuildShadowMask for the contact shadow):
    ///   * backdrop = gaussian glow centred (0.41, 0.43), sigma (0.34,
    ///     0.22), amplitude 0.39 over a 0.19->0.57 vertical base ramp
    ///     (rms fit error 3.5%); hotspot #C8741E for the papaya shot
    ///   * hue drifts warmer as it darkens: G ~ L^1.40, B ~ L^1.48
    ///   * contact shadow: ~62% darkening at wheel contact, recovering
    ///     within ~5% of frame height; elevated parts (nose) cast a
    ///     wider ~20% half-shadow -- reproduced by splatting the car's
    ///     own vertices with height-weighted soft blobs
    ///
    /// Requires the NuGet package: HelixToolkit.Wpf.SharpDX (2.25.x).
    /// The WPF-viewport showroom (ShowroomPreview) remains the fallback
    /// when DX initialisation fails.
    ///
    /// v16.5: MAXIMUM FIDELITY. Everything the pipeline exposes, at
    /// its ceiling: a 2x SUPERSAMPLED render target (Viewport3DX
    /// .DpiScale -- the host sizes its buffers at layout x scale and
    /// D3DImage scales back down on display; asserted deferred because
    /// the DP only reaches the canvas on a change made while the
    /// canvas exists), a 512px environment/backdrop cubemap, 2560x1440
    /// fallback backdrop, 1024px shadow catcher over a 384 mask with a
    /// 260k-vertex splat budget, 16px irradiance, and parallelised
    /// generators so none of it costs interactivity. Combined with
    /// v16.4's MSAA Maximum + SSAO High + anisotropic x16 sampling,
    /// the showroom renders at the quality limit of this stack.
    /// </summary>
    internal static class ShowroomDx
    {
        // v16.0: CALIBRATION ANCHORS. All light budgets below are tuned
        // so the scene matches the reference AT THE SHIPPED DEFAULTS
        // (Brightness slider 0.75, Reflection slider 0.45). The sliders
        // therefore scale relative to the reference look: 0.75 -> 1.00x.
        private const double CalBrightness = 0.75;
        private const double CalReflection = 0.45;

        // v14.2/v16.3: ADJUSTABLE LIGHT INTENSITY, live during the
        // preview. The MainWindow 'Light' slider scales the LIGHT only
        // -- key/fill/rim/ambient, the irradiance map, and the white
        // softbox emitter -- while the cove backdrop stays pinned at
        // its calibrated (reference) level, so adjusting intensity
        // never drifts the approved scenery. Rig colours and the tiny
        // 8px irradiance cube update on EVERY slider tick (instant
        // feedback on the car); the 256px environment rebuild (the
        // softbox dose inside the reflections) is debounced ~120 ms so
        // dragging stays smooth.
        public static double Brightness = 0.75;

        // v14.4/v16.0: ADJUSTABLE REFLECTION INTENSITY (0..1, default
        // 0.45 = reference). Drives the white ceiling-softbox weight in
        // the environment cubemap and its share of the irradiance --
        // i.e. how strong the white lacquer streaks on the paint read.
        // 0 removes the white emitter entirely (the v14.8 look).
        public static double Reflection = 0.45;

        private static readonly List<PBRMaterial> _carMats = new List<PBRMaterial>();
        private static AmbientLight3D _ambientRef;
        private static DirectionalLight3D _keyRef, _fillRef, _rimRef;
        private static EnvironmentMap3D _envRef;
        // v17.14: kept so live retints can force a redraw -- swapping
        // EnvironmentMap3D.Texture alone doesn't reliably invalidate.
        private static Viewport3DX _vpLive;
        private static TextureModel _irrTex;
        private static Color _lastTint;

        private static double Bf() => Brightness / CalBrightness;
        private static double Rf() => Math.Min(2.2, Math.Max(0.0, Reflection / CalReflection));

        // v16.4: EXPOSURE LIFT, measured from the reference render's
        // own tones: white livery decals read [221,218,215] (~0.86
        // light on camera-facing panels) and the lit papaya median is
        // [230,148,47] with p90 clipping toward white on the ridges.
        // v16.2's budget landed typical lit panels at ~0.65 -- visibly
        // underexposed against that. New NEUTRAL WHITE budget (at the
        // calibrated defaults, factor = 1):
        //   key  0.66 white  -- diffuse punch + the GGX lacquer streak
        //   fill 0.26 white  -- camera-side lift
        //   rim  0.22 white  -- edge separation from behind
        //   ambient ~0.02    -- effectively off (header note 2: flat
        //        additive fog on PBR, never a light)
        // With the irradiance map below this lands: tops ~1.40 (just
        // clipping, like the reference), camera-side verticals ~0.90,
        // away side ~0.60. The light stays WHITE (v16.2) -- the orange
        // lives only in the scenery and its reflections.
        // v17.10: dramatic top-lit grade -- key brightened and steepened
        // (see KeyDir), fill halved so the flanks fall off into shadow,
        // rim eased. The car now reads as lit by one overhead softbox
        // rather than evenly from all sides.
        private static Color KeyColor() => Scale(0xc8, 0xc8, 0xc8, Bf());
        private static Color FillColorFor() => Scale(0x14, 0x14, 0x14, Bf());
        private static Color RimColor() => Scale(0x1e, 0x1e, 0x1e, Bf());
        private static Color AmbientColorFor() => Scale(0x04, 0x04, 0x04, Bf());

        // v16.0: the key arrives FROM the softbox direction -- azimuth
        // (0.627, -0.779) (the cove hotspot behind the car, matching the
        // camera's look direction) -- so the GGX streak and the env-map
        // softbox reflection sit on the same panel lines and read as one
        // light source. v17.10: elevation raised ~55 -> ~68 deg (same
        // azimuth) for the top-lit dramatic grade.
        private static readonly Media3D.Vector3D KeyDir =
            new Media3D.Vector3D(-0.235, -0.927, 0.292);

        private static Color Scale(byte r, byte g, byte b, double f)
        {
            return Color.FromRgb(
                (byte)Math.Max(0, Math.Min(255, r * f)),
                (byte)Math.Max(0, Math.Min(255, g * f)),
                (byte)Math.Max(0, Math.Min(255, b * f)));
        }

        /// <summary>
        /// v16.3: apply a light-intensity factor to the LIVE showroom.
        /// Safe to call any time (slider fires during window init, long
        /// before a showroom exists); no-op when nothing is built. The
        /// rig colours and the irradiance map change on this call; the
        /// environment (softbox dose) follows via the debounce.
        /// </summary>
        public static void SetBrightness(double b)
        {
            Brightness = Math.Max(0.1, Math.Min(2.0, b));
            if (_ambientRef != null) _ambientRef.Color = AmbientColorFor();
            if (_keyRef != null) _keyRef.Color = KeyColor();
            if (_fillRef != null) _fillRef.Color = FillColorFor();
            if (_rimRef != null) _rimRef.Color = RimColor();
            RebuildIrradiance();
            ScheduleEnvRebuild();
        }

        /// <summary>
        /// v17.14: apply a new scene (backdrop/hotspot) tint to the
        /// LIVE scene: irradiance immediately, environment (skybox +
        /// reflections) via the debounce -- same pattern as the
        /// Light/Reflection sliders.
        /// </summary>
        public static void SetTint(Color tint)
        {
            _lastTint = tint;
            RebuildIrradiance();
            ScheduleEnvRebuild();
            try { _vpLive?.InvalidateRender(); } catch { }
        }

        /// <summary>
        /// v16.3: apply a reflection (softbox) factor to the LIVE
        /// scene: irradiance immediately, environment via the debounce.
        /// </summary>
        public static void SetReflection(double r)
        {
            Reflection = Math.Max(0.0, Math.Min(1.0, r));
            RebuildIrradiance();
            ScheduleEnvRebuild();
        }

        /// <summary>
        /// The 8px irradiance cube is trivial to rebuild, so it tracks
        /// every slider tick -- the car's illumination follows the
        /// 'Light' bar with no perceptible lag.
        /// </summary>
        private static void RebuildIrradiance()
        {
            if (_carMats.Count == 0) return;
            _irrTex = BuildIrradianceCubemap(_lastTint, Brightness, Reflection);
            foreach (var m in _carMats) m.IrradianceMap = _irrTex;
        }

        /// <summary>
        /// v16.3: the 256px environment rebuild (DDS + mip chain +
        /// upload) is too heavy to run on every ValueChanged tick of a
        /// dragged slider, so it coalesces: rebuild fires ~120 ms after
        /// the LAST change. Created lazily on the UI thread (slider
        /// events), which is also where DispatcherTimer must live.
        /// </summary>
        private static DispatcherTimer _envDebounce;

        private static void ScheduleEnvRebuild()
        {
            if (_envRef == null) return;
            if (_envDebounce == null)
            {
                _envDebounce = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(120)
                };
                _envDebounce.Tick += (s, e) =>
                {
                    _envDebounce.Stop();
                    if (_envRef != null)
                        _envRef.Texture = BuildEnvironmentCubemap(_lastTint, Brightness, Reflection);
                    try { _vpLive?.InvalidateRender(); } catch { }
                };
            }
            _envDebounce.Stop();
            _envDebounce.Start();
        }

        // v13.8 diagnostics -- readable after Populate via DiagSummary.
        public static int DiagDegenerateNormalMeshes;
        public static int DiagRecomputedNormalMeshes;
        public static int DiagConvertedMeshes;
        public static string DiagSummary =>
            $"meshes={DiagConvertedMeshes}, recomputedNormals={DiagRecomputedNormalMeshes}, " +
            $"degenerateSets={DiagDegenerateNormalMeshes}";

        // ---------------------------------------------------------------
        // Geometry + material conversion
        // ---------------------------------------------------------------

        /// <summary>
        /// Convert the loader's Model3DGroup tree into DX scene elements.
        /// Transforms are composed down the tree and baked onto each
        /// element (WPF Media3D transforms are accepted directly by
        /// Element3D.Transform). Shared WPF MeshGeometry3D instances are
        /// converted once and cached, so tyre clones stay cheap.
        /// </summary>
        public static List<Element3D> ConvertModelTree(Media3D.Model3DGroup root)
        {
            var outList = new List<Element3D>();
            if (root == null) return outList;
            var meshCache = new Dictionary<Media3D.MeshGeometry3D, MeshGeometry3D>();
            var texCache = new Dictionary<BitmapSource, TextureModel>();
            Walk(root, Media3D.Matrix3D.Identity, outList, meshCache, texCache);
            return outList;
        }

        private static void Walk(Media3D.Model3D model, Media3D.Matrix3D parent,
                                 List<Element3D> outList,
                                 Dictionary<Media3D.MeshGeometry3D, MeshGeometry3D> meshCache,
                                 Dictionary<BitmapSource, TextureModel> texCache)
        {
            Media3D.Matrix3D m = parent;
            if (model.Transform != null && model.Transform != Media3D.Transform3D.Identity)
                m = Media3D.Matrix3D.Multiply(model.Transform.Value, parent);

            if (model is Media3D.Model3DGroup g)
            {
                foreach (var child in g.Children)
                    Walk(child, m, outList, meshCache, texCache);
                return;
            }
            if (!(model is Media3D.GeometryModel3D gm) ||
                !(gm.Geometry is Media3D.MeshGeometry3D wpfMesh))
                return;

            if (!meshCache.TryGetValue(wpfMesh, out var hxMesh))
            {
                hxMesh = ConvertMesh(wpfMesh);
                meshCache[wpfMesh] = hxMesh;
                if (hxMesh != null) DiagConvertedMeshes++;
            }
            if (hxMesh == null) return;

            var seeThrough = FindSeeThrough(gm.Material);

            var element = new MeshGeometryModel3D
            {
                Geometry = hxMesh,
                Material = ConvertMaterial(gm.Material, texCache),
                Transform = new Media3D.MatrixTransform3D(m),
                CullMode = SharpDX.Direct3D11.CullMode.None,
                IsThrowingShadow = true,
            };

            // Decal-named BASE submeshes (a suit's body_logos / *_logo(s)
            // slots) carry a mostly-transparent logo atlas as their
            // diffuse. The opaque pass ignores alpha, so they rendered as
            // solid sheets in the atlas's background colour the moment the
            // depth lift stopped the base geometry from covering them
            // (before the lift they z-fought the body instead). Route them
            // through the transparent pass so the atlas alpha blends --
            // the same treatment the game gives them (polygon offset +
            // blend). Car sponsor decals are unaffected: they ship as
            // overlay meshes whose atlases are opaque-baked (FIX 34), and
            // car parts have no decal-named base slots.
            if (F1ModelLoader.IsDecalLikeSlotName(
                    MeshSubmeshRegistry.GetSubmeshName(wpfMesh)))
                element.IsTransparent = true;
            // Visors, tear-offs and the clear plastic parts blend.
            if (seeThrough != null)
                element.IsTransparent = true;
            _convertedNodes.Add(new ConvertedNode { Node = element, Source = gm.Material });
            outList.Add(element);
        }

        private static MeshGeometry3D ConvertMesh(Media3D.MeshGeometry3D src)
        {
            if (src.Positions == null || src.Positions.Count == 0 ||
                src.TriangleIndices == null || src.TriangleIndices.Count == 0)
                return null;

            var mesh = new MeshGeometry3D
            {
                Positions = new Vector3Collection(src.Positions.Count),
                Indices = new IntCollection(src.TriangleIndices.Count),
            };
            foreach (var p in src.Positions)
                mesh.Positions.Add(new Vector3((float)p.X, (float)p.Y, (float)p.Z));
            foreach (int i in src.TriangleIndices)
                mesh.Indices.Add(i);

            // v13.8: VALIDATE copied normals. Directional/spot lighting
            // is N-dot-L: zero-length or garbage normals kill exactly the
            // directional term while ambient survives -- which matches
            // the 'black car with faint texture' symptom. WPF's viewport
            // silently auto-computes when normals are missing, so the
            // loader's meshes were never held to this standard before.
            bool copiedNormals = false;
            if (src.Normals != null && src.Normals.Count == src.Positions.Count)
            {
                mesh.Normals = new Vector3Collection(src.Normals.Count);
                int degenerate = 0;
                foreach (var n in src.Normals)
                {
                    var v = new Vector3((float)n.X, (float)n.Y, (float)n.Z);
                    if (v.LengthSquared() < 1e-8f) degenerate++;
                    mesh.Normals.Add(v);
                }
                copiedNormals = degenerate <= src.Normals.Count / 10;
                if (!copiedNormals)
                {
                    DiagDegenerateNormalMeshes++;
                    mesh.Normals = null;   // fall through to recompute
                }
            }
            if (!copiedNormals)
            {
                DiagRecomputedNormalMeshes++;
                // v13.0a: manual per-vertex normal accumulation -- no
                // dependency on any helper API that shifted between
                // HelixToolkit versions.
                var acc = new Vector3[mesh.Positions.Count];
                for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
                {
                    int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
                    var n = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a],
                                          mesh.Positions[c] - mesh.Positions[a]);
                    acc[a] += n; acc[b] += n; acc[c] += n;
                }
                mesh.Normals = new Vector3Collection(mesh.Positions.Count);
                for (int i = 0; i < acc.Length; i++)
                {
                    var n = acc[i];
                    // static form: valid for System.Numerics AND SharpDX math
                    n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : new Vector3(0, 1, 0);
                    mesh.Normals.Add(n);
                }
            }
            if (src.TextureCoordinates != null &&
                src.TextureCoordinates.Count == src.Positions.Count)
            {
                mesh.TextureCoordinates = new Vector2Collection(src.TextureCoordinates.Count);
                foreach (var t in src.TextureCoordinates)
                    mesh.TextureCoordinates.Add(new Vector2((float)t.X, (float)t.Y));
            }
            return mesh;
        }

        /// <summary>
        /// v16.0: MEASURED AUTOMOTIVE PAINT. The audit (header note 3)
        /// showed the v15.1 all-defaults material received zero light
        /// from the environment (ReflectanceFactor defaults to 0, and
        /// c_spec scales every env sample). The response is now set
        /// explicitly, at the values that reproduce the reference:
        ///   MetallicFactor    0     -- metallic darkens diffuse and
        ///                              tints reflections orange; the
        ///                              reference streaks are white
        ///   RoughnessFactor   0.32  -- elongated lacquer streak from
        ///                              the key; env sampled ~2 mips
        ///                              down the new mip chain, so cove
        ///                              reflections are soft + coherent
        ///   ReflectanceFactor 0.75  -- F0 ~ 0.09: visible but subtle
        ///                              env reflection, Fresnel-boosted
        ///                              at grazing angles like lacquer
        ///   EmissiveColor     black -- v14.1 rule, kept explicit
        ///   IrradianceMap     shared cube -- the scene's soft light,
        ///                              multiplied by albedo in-shader
        ///   SurfaceMapSampler Ani x16 -- v16.4: the library default is
        ///                              anisotropic x4, which smears
        ///                              texture detail at the grazing
        ///                              angles the FAR half of the car
        ///                              sits at in this near-horizontal
        ///                              framing; x16 keeps distant
        ///                              decals and panel lines legible
        ///                              (mip chains are auto-generated
        ///                              by the loader for 1-mip PNGs,
        ///                              so anisotropy has levels to
        ///                              work with)
        /// Clear coat stays 0: it historically correlated with fog or
        /// blackness (v14.0 note) and the reference doesn't need it.
        /// </summary>
        private static HelixToolkit.Wpf.SharpDX.Material ConvertMaterial(Media3D.Material mat,
            Dictionary<BitmapSource, TextureModel> texCache)
        {
            var pbr = new PBRMaterial
            {
                MetallicFactor = 0.0,
                RoughnessFactor = 0.32,
                ReflectanceFactor = 0.75,
                EmissiveColor = new Color4(0f, 0f, 0f, 1f),
                SurfaceMapSampler = DefaultSamplers.LinearSamplerWrapAni16,
            };
            if (_irrTex != null) pbr.IrradianceMap = _irrTex;
            _carMats.Add(pbr);
            var dm = FindFirstDiffuse(mat);

            // v17.0: GAME MATERIAL RESPONSE. The loader tags each
            // DiffuseMaterial with its decoded GfxMaterialRes (shader
            // family + PerMaterial values + me_* bindings); when a tag
            // is present the guessed constants above are replaced with
            // the game's own numbers per family (see ApplyGameMaterial).
            GameMaterialInfo gm = null;
            if (EnableGameMaterials &&
                !GameMaterialCatalog.TryGet(mat, out gm) && dm != null)
                GameMaterialCatalog.TryGet(dm, out gm);
            if (gm != null) ApplyGameMaterial(pbr, gm, texCache);

            if (dm?.Brush is ImageBrush ib && ib.ImageSource is BitmapSource bs)
            {
                if (!texCache.TryGetValue(bs, out var tex))
                {
                    tex = ToTextureModel(bs);
                    texCache[bs] = tex;
                }
                if (tex != null) pbr.AlbedoMap = tex;
            }
            else if (dm?.Brush is SolidColorBrush sb)
            {
                pbr.AlbedoColor = new Color4(
                    sb.Color.R / 255f, sb.Color.G / 255f, sb.Color.B / 255f, 1f);
            }

            // See-through character materials: the game's opacity (the
            // albedo alpha, which the transparent pass blends by), its
            // smoothness and its index of refraction.
            var seeThrough = FindSeeThrough(mat);
            if (seeThrough != null)
            {
                var a = pbr.AlbedoColor;
                pbr.AlbedoColor = new Color4(a.Red, a.Green, a.Blue, seeThrough.Opacity);
                pbr.RoughnessFactor = Math.Max(0.05, seeThrough.Roughness);
                pbr.ReflectanceFactor = IorToReflectance(seeThrough.Ior);
            }
            return pbr;
        }

        private static SeeThroughMaterial FindSeeThrough(Media3D.Material mat)
        {
            if (GameMaterialCatalog.TryGetSeeThrough(mat, out var st)) return st;
            var dm = FindFirstDiffuse(mat);
            return dm != null && GameMaterialCatalog.TryGetSeeThrough(dm, out st) ? st : null;
        }

        private static Media3D.DiffuseMaterial FindFirstDiffuse(Media3D.Material mat)
        {
            if (mat is Media3D.DiffuseMaterial d) return d;
            if (mat is Media3D.MaterialGroup mg)
            {
                foreach (var child in mg.Children)
                {
                    var f = FindFirstDiffuse(child);
                    if (f != null) return f;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------
        // v17.0: TIER-1 GAME MATERIALS. Per-family response driven by the
        // decoded GfxMaterialRes values (GameMaterialCatalog) instead of
        // one constant set. The mappings and their provenance:
        //
        //   IOR -> ReflectanceFactor: HelixToolkit c_spec = 0.16 * r^2 and
        //   physics F0 = ((n-1)/(n+1))^2, so r = sqrt(F0/0.16). The paint's
        //   me_lacquerIor 1.5 -> F0 0.04 -> r 0.5 (the classic dielectric).
        //
        //   Paint (metallic/gloss/custom): base roughness =
        //   me_lacquerRoughness (MCL33: 0.23); clear coat strength from
        //   me_lacquerThickness (0.2 -> 0.5) with a tighter second lobe
        //   (0.35 x base). Clear coat is SAFE now: the v14.0-era
        //   coat-equals-fog correlation was the flat-ambient bug rooted
        //   out in v16 (header note 2).
        //
        //   Carbon: weave base is rougher than its lacquer (0.45); coat
        //   from me_lacquerRoughness (0.2). The 400x anisotropic weave
        //   itself needs the Tier-2 custom shader -- Helix PBR has no
        //   anisotropy -- so this is the isotropic stand-in.
        //
        //   Generic/Wheel/Steering: the game's me_normalMap is authored
        //   in the SAME UV space as the diffuse for these families
        //   (mclaren_wheel_n, mclaren_detail_n, ...), so it wires
        //   straight into PBRMaterial.NormalMap (tangents are already
        //   loader-provided for these pools). Paint/carbon weave normals
        //   (cf_pattern_*_n) are DELIBERATELY skipped: the game tiles
        //   them 200x/400x via me_normalTile/me_carbonTile in-shader,
        //   which Helix's shared-UV sampling cannot express -- applying
        //   them untiled would smear one weave cell across the car.
        //
        //   Glass/Mirror: near-mirror dielectric. Tyres: rubber via
        //   me_rubberIOR. RainBeads/Unknown: left at the v16 defaults.
        //
        // Kill-switches for A/B testing on real hardware:
        private const bool EnableGameMaterials = true;
        private const bool EnableClearCoat = true;
        private const bool EnableNormalMaps = true;

        private static readonly Dictionary<string, TextureModel> _auxTexCache =
            new Dictionary<string, TextureModel>(StringComparer.OrdinalIgnoreCase);

        // v17.2: which WPF material produced each converted DX node, so a
        // fast in-place swap (driver number) can retarget the copies
        // without repopulating the scene.
        private sealed class ConvertedNode
        {
            public MeshGeometryModel3D Node;
            public Media3D.Material Source;
        }
        private static readonly List<ConvertedNode> _convertedNodes =
            new List<ConvertedNode>();

        private static double IorToReflectance(double ior)
        {
            if (ior <= 1.0) return 0.0;
            double f0 = (ior - 1.0) / (ior + 1.0);
            f0 *= f0;
            return Math.Max(0.0, Math.Min(1.0, Math.Sqrt(f0 / 0.16)));
        }

        private static void ApplyGameMaterial(PBRMaterial pbr, GameMaterialInfo gm,
            Dictionary<BitmapSource, TextureModel> texCache)
        {
            switch (gm.Kind)
            {
                case GameShaderKind.MetallicPaint:
                case GameShaderKind.GlossPaint:
                case GameShaderKind.CustomPaint:
                case GameShaderKind.PaintOther:
                    {
                        double rough = gm.Scalar("me_lacquerRoughness", 0.23f);
                        pbr.RoughnessFactor = Math.Max(0.05, Math.Min(1.0, rough));
                        pbr.ReflectanceFactor = IorToReflectance(gm.Scalar("me_lacquerIor", 1.5f));
                        if (EnableClearCoat)
                        {
                            pbr.ClearCoatStrength = Math.Min(1.0, gm.Scalar("me_lacquerThickness", 0.2f) * 2.5);
                            pbr.ClearCoatRoughness = Math.Max(0.02, rough * 0.35);
                        }
                        break;
                    }
                case GameShaderKind.Carbon:
                    {
                        pbr.RoughnessFactor = 0.45;
                        pbr.ReflectanceFactor = IorToReflectance(gm.Scalar("me_carbonIor", 1.5f));
                        if (EnableClearCoat)
                        {
                            pbr.ClearCoatStrength = 0.4;
                            pbr.ClearCoatRoughness = Math.Max(0.02,
                                gm.Scalar("me_lacquerRoughness", 0.2f) * 0.5);
                        }
                        break;
                    }
                case GameShaderKind.Generic:
                case GameShaderKind.Wheel:
                case GameShaderKind.Steering:
                    {
                        pbr.RoughnessFactor = 0.5;
                        pbr.ReflectanceFactor = 0.5;
                        if (EnableNormalMaps) TryApplyNormalMap(pbr, gm);
                        break;
                    }
                case GameShaderKind.Glass:
                case GameShaderKind.Mirror:
                    {
                        pbr.RoughnessFactor = 0.06;
                        pbr.ReflectanceFactor = 0.85;
                        break;
                    }
                case GameShaderKind.TyreSidewall:
                case GameShaderKind.TyreTread:
                    {
                        pbr.RoughnessFactor = 0.62;
                        pbr.ReflectanceFactor = IorToReflectance(gm.Scalar("me_rubberIOR", 1.45f));
                        break;
                    }
                default:
                    break;   // Unknown / RainBeads: keep the v16 defaults
            }
        }

        private static void TryApplyNormalMap(PBRMaterial pbr, GameMaterialInfo gm)
        {
            try
            {
                if (!gm.Bindings.TryGetValue("me_normalMap", out var eaid) ||
                    string.IsNullOrEmpty(eaid)) return;
                // dummy_normal.tif is the engine's flat placeholder -- skip
                if (eaid.IndexOf("dummy_normal", StringComparison.OrdinalIgnoreCase) >= 0)
                    return;
                if (!_auxTexCache.TryGetValue(eaid, out var tex))
                {
                    var bmp = GameMaterialCatalog.TryGetAuxBitmap(gm, "me_normalMap");
                    tex = bmp != null ? ToTextureModel(bmp) : null;
                    _auxTexCache[eaid] = tex;   // cache nulls too: don't retry per mesh
                }
                if (tex != null) pbr.NormalMap = tex;
            }
            catch { }
        }

        private static TextureModel ToTextureModel(BitmapSource bs)
        {
            try
            {
                // v14.0: plain encode, exactly as the first working
                // build -- no alpha modification.
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bs));
                var ms = new MemoryStream();
                enc.Save(ms);
                ms.Position = 0;
                return new TextureModel(ms);
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------
        // v16.0: the reference colour grade, shared by every generated
        // surface (backdrop brush, env cubemap, irradiance, floor) so
        // the scene is one colour field.
        // ---------------------------------------------------------------

        /// <summary>
        /// The tint hex is defined as the HOTSPOT colour (the brightest
        /// point of the backdrop). Darker regions drift warmer exactly
        /// as the reference does: measured per-channel exponents are
        /// G ~ L^1.40 and B ~ L^1.48 against R ~ L (regressed over ~1500
        /// backdrop samples). #C8741E reproduces the papaya shot.
        /// </summary>
        private static void TintPow(Color tint, double L, out double r, out double g, out double b)
        {
            if (L < 0) L = 0;
            r = tint.R * L;
            g = tint.G * Math.Pow(L, 1.40);
            b = tint.B * Math.Pow(L, 1.48);
        }

        private static double SmoothStep01(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            return t * t * (3 - 2 * t);
        }

        private static byte ClampB(double v) => (byte)Math.Max(0, Math.Min(255, v));

        // ---------------------------------------------------------------
        // Backdrop (the ImageBrush behind the transparent DX viewport --
        // actually visible now that the skybox is SkipRendering, see
        // header note 1)
        // ---------------------------------------------------------------

        /// <summary>
        /// v16.0: FITTED REFERENCE FIELD. Least-squares fit of the
        /// pre-render's backdrop (rms 3.5% of full scale):
        ///   L(x,y) = base(y) + 0.391 * gauss(d)
        ///   base   = 0.194 -> 0.573 smoothstep over y = 0.330..0.505
        ///   d^2    = ((x-0.408)/0.336)^2 + ((y-0.427)/0.219)^2
        /// normalised so the hotspot equals the tint hex, top corners
        /// fall to ~0.27x and the front floor to ~0.70x, with the hue
        /// drifting warm-brown on the way down (TintPow).
        /// </summary>
        public static BitmapSource BuildReferenceBackdrop(Color tint, int w = 2560, int h = 1440)
        {
            byte[] px = new byte[w * h * 4];
            Parallel.For(0, h, y =>
            {
                double fy = y / (double)(h - 1);
                double t = (fy - 0.330) / 0.175;
                double bse = 0.194 + 0.379 * SmoothStep01(t);
                double dy = (fy - 0.427) / 0.219;
                for (int x = 0; x < w; x++)
                {
                    double fx = x / (double)(w - 1);
                    double dx = (fx - 0.408) / 0.336;
                    double L = bse + 0.391 * Math.Exp(-0.5 * (dx * dx + dy * dy));
                    double Ln = Math.Min(1.02, L / 0.805);
                    TintPow(tint, Ln, out double r, out double g, out double b);
                    int o = (y * w + x) * 4;
                    px[o + 0] = ClampB(b);
                    px[o + 1] = ClampB(g);
                    px[o + 2] = ClampB(r);
                    px[o + 3] = 255;
                }
            });
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }

        // ---------------------------------------------------------------
        // v16.1: Environment cubemap -- BOTH the visible backdrop (the
        // skybox renders, header note 1) AND the reflection source. The
        // sides carry the fitted reference field mapped into direction
        // space through the DEFAULT SHOWROOM CAMERA (look azimuth
        // (0.627, -0.779), elevation -6.6 deg, 35 deg vertical FOV /
        // 58.54 deg horizontal at 16:9): what that camera sees of the
        // sphere IS the reference frame, and every other direction
        // extends the same field smoothly (dark cyclorama above, warm
        // floor below) so orbiting stays coherent. The white softbox
        // sits at ~31 deg elevation -- just ABOVE the frustum's +11 deg
        // ceiling, so it lights the paint and appears in reflections
        // but never shows in the backdrop unless the user tilts up.
        // (v14.8 removed every white source because of the film -- but
        // the film was the flat ambient, header note 2; a localised box
        // only appears where panels actually mirror it.)
        // Written as an RGBA32 DDS cubemap WITH A FULL MIP CHAIN so
        // Specular_IBL's roughness-driven mip pick (mip = roughness *
        // mipCount) lands on a softened level. The geometry-dependent
        // terms (field luminance incl. the per-channel gammas, softbox
        // mask) are cached once per run, so the Light/Refl sliders can
        // rebuild the live map with multiply-adds only -- no stutter.
        // ---------------------------------------------------------------
        private const int EnvSize = 512;   // v16.5: was 256 -- smoother backdrop + softbox
        private static float[][] _envLnR, _envLnG, _envLnB, _envSb;

        // v16.5: the render-target supersampling factor pushed onto
        // Viewport3DX.DpiScale (see the header note and Populate).
        private const double SuperSampling = 2.0;

        public static TextureModel BuildEnvironmentCubemap(Color tint)
        {
            return BuildEnvironmentCubemap(tint, Brightness, Reflection);
        }

        public static TextureModel BuildEnvironmentCubemap(Color tint, double brightness)
        {
            return BuildEnvironmentCubemap(tint, brightness, Reflection);
        }

        private static void EnsureEnvFieldCache()
        {
            if (_envLnR != null) return;
            // default showroom camera: normalised xz azimuth of the
            // camPosFor look direction, elevation asin(-0.115) = -6.6
            const double lx = 0.62719, lz = -0.77887;
            const double elevDeg0 = -6.6, fovVDeg = 35.0, fovHDeg = 58.54;
            var lnRs = new float[6][]; var lnGs = new float[6][];
            var lnBs = new float[6][]; var sbs = new float[6][];
            // v16.5: 512px x 6 faces of transcendental math -- one-time
            // cost, parallelised per face (fully independent outputs).
            Parallel.For(0, 6, f =>
            {
                var lnR = new float[EnvSize * EnvSize];
                var lnG = new float[EnvSize * EnvSize];
                var lnB = new float[EnvSize * EnvSize];
                var sbA = new float[EnvSize * EnvSize];
                for (int y = 0; y < EnvSize; y++)
                {
                    double v = y / (double)(EnvSize - 1);
                    for (int x = 0; x < EnvSize; x++)
                    {
                        double u = x / (double)(EnvSize - 1);
                        FaceDir(f, u, v, out double dX, out double dY, out double dZ);

                        // direction -> reference-frame coordinates
                        // (angular mapping; validated sign/scale against
                        // the reference render at 4.4/255 mean error)
                        double theta = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dY))) * 180.0 / Math.PI;
                        double fyS = 0.5 - (theta - elevDeg0) / fovVDeg;
                        double nl = Math.Sqrt(dX * dX + dZ * dZ);
                        double cosp = 1.0, phi = 0.0;
                        if (nl > 1e-6)
                        {
                            cosp = (dX * lx + dZ * lz) / nl;
                            double sinp = (lx * dZ - lz * dX) / nl;
                            phi = Math.Atan2(sinp, cosp) * 180.0 / Math.PI;
                        }
                        double fxS = 0.5 + phi / fovHDeg;

                        // the fitted screen field (see BuildReferenceBackdrop)
                        double bse = 0.194 + 0.379 * SmoothStep01((fyS - 0.330) / 0.175);
                        double gx = (fxS - 0.408) / 0.336;
                        double gy = (fyS - 0.427) / 0.219;
                        double L = bse + 0.391 * Math.Exp(-0.5 * (gx * gx + gy * gy));
                        double Ln = Math.Min(1.02, Math.Max(0.0, L / 0.805));

                        // softbox mask: elevation band ~31 deg (sin 0.52
                        // +/- 0.16) toward the camera azimuth, elongated
                        // ~38 deg -- flat tops (reflecting the horizon
                        // at this near-horizontal camera) stay glowing
                        // papaya while curved shoulders catch the white
                        // streak, exactly the reference behaviour
                        double eT = Math.Exp(-Sq((dY - 0.52) / 0.16));
                        double aT = Math.Exp(-Sq(Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosp))) / 0.66));

                        int o = y * EnvSize + x;
                        lnR[o] = (float)Ln;
                        lnG[o] = (float)Math.Pow(Ln, 1.40);
                        lnB[o] = (float)Math.Pow(Ln, 1.48);
                        sbA[o] = (float)(eT * aT);
                    }
                }
                lnRs[f] = lnR; lnGs[f] = lnG; lnBs[f] = lnB; sbs[f] = sbA;
            });
            _envLnR = lnRs; _envLnG = lnGs; _envLnB = lnBs; _envSb = sbs;
        }

        public static TextureModel BuildEnvironmentCubemap(Color tint, double brightness, double reflection)
        {
            EnsureEnvFieldCache();
            // v16.3: the cove is SCENERY -- pinned at the calibrated
            // (reference) level, deliberately NOT scaled by brightness:
            // the 'Light' slider dims the studio lights over a constant
            // backdrop plate, exactly like the pre-render composite.
            // Only the softbox EMITTER (a light) follows the sliders.
            double bf = brightness / CalBrightness;
            double sbW = 0.95 * Math.Min(2.2, Math.Max(0.0, reflection / CalReflection))
                              * Math.Min(2.0, bf);
            byte[][] faces = new byte[6][];
            Parallel.For(0, 6, f =>
            {
                var lnR = _envLnR[f]; var lnG = _envLnG[f];
                var lnB = _envLnB[f]; var sbA = _envSb[f];
                var face = new byte[EnvSize * EnvSize * 4];
                for (int i = 0; i < EnvSize * EnvSize; i++)
                {
                    double r = tint.R * lnR[i];
                    double g = tint.G * lnG[i];
                    double b = tint.B * lnB[i];
                    double sb = Math.Min(1.0, sbA[i] * sbW);
                    if (sb > 0)
                    {
                        // v16.2: pure white emitter (the light stays white)
                        r += (250 - r) * sb;
                        g += (250 - g) * sb;
                        b += (250 - b) * sb;
                    }
                    int o = i * 4;
                    face[o + 0] = ClampB(r);
                    face[o + 1] = ClampB(g);
                    face[o + 2] = ClampB(b);
                    face[o + 3] = 255;
                }
                faces[f] = face;
            });
            var dds = BuildRgbaCubemapDds(EnvSize, faces, withMips: true);
            return new TextureModel(new MemoryStream(dds));
        }

        /// <summary>
        /// v16.2: WHITE LIGHT. The irradiance map is the scene's soft
        /// light (header note 2: the shader multiplies it by albedo,
        /// unlike the flat-additive ambient).
        /// v16.4 exposure lift (measured): 0.86 from above, 0.58 from
        /// the sides, 0.38 floor bounce -- neutral white throughout
        /// (v16.2), sized so white decals land ~[221] and lit papaya
        /// matches the reference's [230,148,47] median. The tint
        /// parameter no longer colours the light (kept for signature
        /// stability); warmth on the paint comes only from the env
        /// map's mirror reflections of the orange cove.
        /// </summary>
        private static TextureModel BuildIrradianceCubemap(Color tint, double brightness, double reflection)
        {
            const int size = 16;   // v16.5: was 8 -- smoother directional blend
            double bf = brightness / CalBrightness;
            double rf = Math.Min(2.2, Math.Max(0.0, reflection / CalReflection));
            // softbox direction (azimuth of the cove glow, elevation ~31 deg)
            const double sbX = 0.536, sbY = 0.520, sbZ = -0.665;
            // v17.10b: top-lit dramatic grade. The old horizon/floor
            // shares (0.58 / 0.38) washed the flanks and undersides with
            // near-top-level light -- the "lit from all sides" look.
            // Sky stays full; sides fall to a third, floor bounce to a
            // sliver, so form shading rolls off down the bodywork.
            const double topL = 255.0 * 0.90;
            const double hzL = 255.0 * 0.30;
            const double flL = 255.0 * 0.12;

            byte[][] faces = new byte[6][];
            for (int f = 0; f < 6; f++)
            {
                var face = new byte[size * size * 4];
                for (int y = 0; y < size; y++)
                {
                    double v = y / (double)(size - 1);
                    for (int x = 0; x < size; x++)
                    {
                        double u = x / (double)(size - 1);
                        FaceDir(f, u, v, out double dX, out double dY, out double dZ);
                        double up = Math.Max(0, dY);
                        double dn = Math.Max(0, -dY);
                        double hz = 1.0 - Math.Abs(dY);
                        double c = topL * up + flL * dn + hzL * hz;
                        // small directional white share from the softbox
                        double sd = Math.Max(0, dX * sbX + dY * sbY + dZ * sbZ);
                        c += 255.0 * (0.10 * rf * sd * sd);
                        byte cb = ClampB(c * bf);
                        int o = (y * size + x) * 4;
                        face[o + 0] = cb;
                        face[o + 1] = cb;
                        face[o + 2] = cb;
                        face[o + 3] = 255;
                    }
                }
                faces[f] = face;
            }
            var dds = BuildRgbaCubemapDds(size, faces, withMips: false);
            return new TextureModel(new MemoryStream(dds));
        }

        private static double Sq(double v) => v * v;

        /// <summary>
        /// D3D cubemap face texel -> world direction (normalised).
        /// Face order: +X, -X, +Y, -Y, +Z, -Z (the DDS layout order).
        /// </summary>
        private static void FaceDir(int f, double u, double v,
                                    out double x, out double y, out double z)
        {
            double a = 2 * u - 1, b = 2 * v - 1;
            switch (f)
            {
                case 0: x = 1; y = -b; z = -a; break;   // +X
                case 1: x = -1; y = -b; z = a; break;   // -X
                case 2: x = a; y = 1; z = b; break;     // +Y (up)
                case 3: x = a; y = -1; z = -b; break;   // -Y (down)
                case 4: x = a; y = -b; z = 1; break;    // +Z
                default: x = -a; y = -b; z = -1; break; // -Z
            }
            double len = Math.Sqrt(x * x + y * y + z * z);
            x /= len; y /= len; z /= len;
        }

        private static byte[] BuildRgbaCubemapDds(int size, byte[][] faces)
        {
            return BuildRgbaCubemapDds(size, faces, withMips: false);
        }

        /// <summary>
        /// v16.0: optional full mip chain (simple 2x2 box downsample per
        /// face). SkyBoxRenderCore reads the mip count off the texture
        /// view and feeds it to the shader's NumEnvironmentMapMipLevels,
        /// which is what lets roughness soften the reflections.
        /// </summary>
        private static byte[] BuildRgbaCubemapDds(int size, byte[][] faces, bool withMips)
        {
            int mipCount = 1;
            if (withMips)
                for (int s = size; s > 1; s >>= 1) mipCount++;

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(0x20534444);            // 'DDS '
                w.Write(124);                   // header size
                // caps|height|width|pitch|pixelformat (+ mipmapcount)
                w.Write(0x0000100F | (withMips ? 0x20000 : 0));
                w.Write(size); w.Write(size);   // height, width
                w.Write(size * 4);              // pitch
                w.Write(0);                     // depth
                w.Write(withMips ? mipCount : 0); // mips
                for (int i = 0; i < 11; i++) w.Write(0); // reserved
                // pixel format: RGBA32
                w.Write(32);                    // pf size
                w.Write(0x41);                  // ALPHAPIXELS | RGB
                w.Write(0);                     // fourcc
                w.Write(32);                    // bit count
                w.Write(0x000000FFu);           // R mask
                w.Write(0x0000FF00u);           // G mask
                w.Write(0x00FF0000u);           // B mask
                w.Write(0xFF000000u);           // A mask
                // caps: COMPLEX | TEXTURE (| MIPMAP)
                w.Write(0x1008 | (withMips ? 0x400000 : 0));
                w.Write(0xFE00);                // caps2: CUBEMAP | all six faces
                w.Write(0); w.Write(0); w.Write(0);
                foreach (var face in faces)
                {
                    w.Write(face);
                    if (withMips)
                    {
                        var cur = face;
                        int s = size;
                        while (s > 1)
                        {
                            int ns = s >> 1;
                            var nxt = new byte[ns * ns * 4];
                            for (int y = 0; y < ns; y++)
                            {
                                for (int x = 0; x < ns; x++)
                                {
                                    int o00 = ((y * 2) * s + (x * 2)) * 4;
                                    int o01 = o00 + 4;
                                    int o10 = o00 + s * 4;
                                    int o11 = o10 + 4;
                                    int od = (y * ns + x) * 4;
                                    for (int c = 0; c < 4; c++)
                                        nxt[od + c] = (byte)((cur[o00 + c] + cur[o01 + c] +
                                                              cur[o10 + c] + cur[o11 + c] + 2) >> 2);
                                }
                            }
                            w.Write(nxt);
                            cur = nxt;
                            s = ns;
                        }
                    }
                }
                return ms.ToArray();
            }
        }

        // ---------------------------------------------------------------
        // v16.6: TRUE CAST SHADOW. The splat approach (v16.0) hugged
        // the silhouette but was still a density blob -- it could never
        // show the wing's outline, the round wheel patches, or the
        // nose's elongated soft shape. The mask is now a genuine
        // area-light shadow of the car's OWN TRIANGLES: geometry is
        // sliced into 24 height slabs whose XZ footprints are
        // rasterised once, then projected onto the floor along 25
        // sample directions spread over the softbox's solid angle
        // (cone half-angle ~29 deg, sized from the reference's
        // penumbra growth of ~0.5 x height; mean direction
        // near-vertical with a slight key-azimuth tilt). The occluded
        // sample FRACTION is the mask, which is the physically correct
        // soft-shadow profile: razor-sharp at the tyre contact
        // patches, widening and fading with height exactly as the
        // reference does -- and every edge is the car's actual shape.
        // ---------------------------------------------------------------
        private sealed class ShadowMask
        {
            public double[] Data;
            public int N;
            public double MinX, MinZ, SizeX, SizeZ;

            public double Sample(double wx, double wz)
            {
                double u = (wx - MinX) / SizeX * (N - 1);
                double v = (wz - MinZ) / SizeZ * (N - 1);
                if (u < 0 || v < 0 || u > N - 1 || v > N - 1) return 0;
                int x0 = (int)u, z0 = (int)v;
                int x1 = Math.Min(N - 1, x0 + 1), z1 = Math.Min(N - 1, z0 + 1);
                double fu = u - x0, fv = v - z0;
                double a = Data[z0 * N + x0] * (1 - fu) + Data[z0 * N + x1] * fu;
                double b = Data[z1 * N + x0] * (1 - fu) + Data[z1 * N + x1] * fu;
                return a * (1 - fv) + b * fv;
            }
        }

        private static void WalkMeshes(Media3D.Model3D model, Media3D.Matrix3D parent,
                                       Action<Media3D.MeshGeometry3D, Media3D.Matrix3D> visit)
        {
            Media3D.Matrix3D m = parent;
            if (model.Transform != null && model.Transform != Media3D.Transform3D.Identity)
                m = Media3D.Matrix3D.Multiply(model.Transform.Value, parent);
            if (model is Media3D.Model3DGroup g)
            {
                foreach (var child in g.Children) WalkMeshes(child, m, visit);
                return;
            }
            if (model is Media3D.GeometryModel3D gm &&
                gm.Geometry is Media3D.MeshGeometry3D mesh && mesh.Positions != null)
                visit(mesh, m);
        }

        private static ShadowMask BuildShadowMask(Media3D.Model3DGroup carRoot, double floorY)
        {
            try
            {
                var bb = carRoot.Bounds;
                if (bb.IsEmpty || bb.SizeX <= 0 || bb.SizeZ <= 0) return null;

                const int N = 384;
                const int M = 24;    // height slabs
                double padX = bb.SizeX * 0.42, padZ = bb.SizeZ * 0.42;
                double minX = bb.X - padX, minZ = bb.Z - padZ;
                double sizeX = bb.SizeX + 2 * padX, sizeZ = bb.SizeZ + 2 * padZ;
                double carH = Math.Max(0.2, bb.SizeY);
                double cellX = sizeX / (N - 1), cellZ = sizeZ / (N - 1);
                double slabH = carH * 1.05 / M;

                // ---- slice the car's triangles into height slabs and
                //      rasterise each slab's XZ footprint (streaming --
                //      no global triangle list) ----
                var slabs = new byte[M][];
                for (int j = 0; j < M; j++) slabs[j] = new byte[N * N];
                long triCount = 0;
                WalkMeshes(carRoot, Media3D.Matrix3D.Identity, (mesh, m) =>
                {
                    var pos = mesh.Positions;
                    var idx = mesh.TriangleIndices;
                    if (idx == null || idx.Count < 3) return;
                    var wx = new double[pos.Count];
                    var wy = new double[pos.Count];
                    var wz = new double[pos.Count];
                    for (int i = 0; i < pos.Count; i++)
                    {
                        var p = m.Transform(pos[i]);
                        wx[i] = (p.X - minX) / cellX;
                        wy[i] = p.Y - floorY;
                        wz[i] = (p.Z - minZ) / cellZ;
                    }
                    for (int i = 0; i + 2 < idx.Count; i += 3)
                    {
                        int a = idx[i], b = idx[i + 1], c = idx[i + 2];
                        double hy0 = Math.Min(wy[a], Math.Min(wy[b], wy[c]));
                        double hy1 = Math.Max(wy[a], Math.Max(wy[b], wy[c]));
                        if (hy1 < -0.02 || hy0 > carH * 1.05) continue;
                        int j0 = Math.Max(0, (int)(hy0 / slabH));
                        int j1 = Math.Min(M - 1, (int)(Math.Max(0.0, hy1) / slabH));
                        for (int j = j0; j <= j1; j++)
                            RasterizeTri(slabs[j], N, wx[a], wz[a], wx[b], wz[b], wx[c], wz[c]);
                        triCount++;
                    }
                });
                if (triCount < 100) return null;

                // ---- softbox sample directions in slope space: a
                //      point q is shadowed from slope s by geometry at
                //      height h whose footprint covers q - s*h, i.e.
                //      each slab ORs in shifted by +s*h ----
                double msx = KeyDir.X / (-KeyDir.Y) * 0.28;
                double msz = KeyDir.Z / (-KeyDir.Y) * 0.28;
                var slopes = new List<(double sx, double sz)> { (msx, msz) };
                const double coneTan = 0.55;   // ~29 deg source half-angle
                for (int ring = 1; ring <= 2; ring++)
                {
                    int cnt = ring == 1 ? 8 : 16;
                    double r = coneTan * ring / 2.0;
                    for (int i = 0; i < cnt; i++)
                    {
                        double a = 2 * Math.PI * i / cnt + ring * 0.35;
                        slopes.Add((msx + r * Math.Cos(a), msz + r * Math.Sin(a)));
                    }
                }
                int K = slopes.Count;   // 25

                var sampleHits = new byte[K][];
                Parallel.For(0, K, k =>
                {
                    var hit = new byte[N * N];
                    double sx = slopes[k].sx, sz = slopes[k].sz;
                    for (int j = 0; j < M; j++)
                    {
                        double h = (j + 0.5) * slabH;
                        int dx = (int)Math.Round(sx * h / cellX);
                        int dz = (int)Math.Round(sz * h / cellZ);
                        OrShifted(hit, slabs[j], N, dx, dz);
                    }
                    sampleHits[k] = hit;
                });

                // occluded fraction of the source = the shadow mask
                var data = new double[N * N];
                for (int k = 0; k < K; k++)
                {
                    var hit = sampleHits[k];
                    for (int i = 0; i < data.Length; i++) data[i] += hit[i];
                }
                double inv = 1.0 / K;
                for (int i = 0; i < data.Length; i++) data[i] *= inv;
                // one-cell soften hides grid stairs on the umbra edge
                BoxBlur(data, N, 1);

                return new ShadowMask
                {
                    Data = data,
                    N = N,
                    MinX = minX,
                    MinZ = minZ,
                    SizeX = sizeX,
                    SizeZ = sizeZ
                };
            }
            catch { return null; }
        }

        /// <summary>
        /// Fill a triangle (grid coordinates) into a slab footprint.
        /// Sub-cell triangles stamp their bounding cells and long
        /// slivers stamp along their edges, so thin geometry (wing
        /// trailing edges, suspension wires) still occludes -- a
        /// centre-sample test alone would leave pinholes.
        /// </summary>
        private static void RasterizeTri(byte[] grid, int n,
            double x0, double z0, double x1, double z1, double x2, double z2)
        {
            int minx = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))));
            int maxx = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
            int minz = Math.Max(0, (int)Math.Floor(Math.Min(z0, Math.Min(z1, z2))));
            int maxz = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(z0, Math.Max(z1, z2))));
            if (minx > maxx || minz > maxz) return;

            if (maxx - minx <= 1 && maxz - minz <= 1)
            {
                for (int z = minz; z <= maxz; z++)
                    for (int x = minx; x <= maxx; x++)
                        grid[z * n + x] = 1;
                return;
            }

            double area = (x1 - x0) * (z2 - z0) - (x2 - x0) * (z1 - z0);
            if (Math.Abs(area) < 0.5)
            {
                StampEdge(grid, n, x0, z0, x1, z1);
                StampEdge(grid, n, x1, z1, x2, z2);
                StampEdge(grid, n, x2, z2, x0, z0);
                return;
            }

            for (int z = minz; z <= maxz; z++)
            {
                double pz = z;
                int row = z * n;
                for (int x = minx; x <= maxx; x++)
                {
                    double px = x;
                    double e0 = (x1 - x0) * (pz - z0) - (z1 - z0) * (px - x0);
                    double e1 = (x2 - x1) * (pz - z1) - (z2 - z1) * (px - x1);
                    double e2 = (x0 - x2) * (pz - z2) - (z0 - z2) * (px - x2);
                    if ((e0 >= 0 && e1 >= 0 && e2 >= 0) ||
                        (e0 <= 0 && e1 <= 0 && e2 <= 0))
                        grid[row + x] = 1;
                }
            }
        }

        private static void StampEdge(byte[] grid, int n,
            double x0, double z0, double x1, double z1)
        {
            double dx = x1 - x0, dz = z1 - z0;
            int steps = Math.Max(1, (int)(2 * Math.Max(Math.Abs(dx), Math.Abs(dz))));
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps;
                int gx = (int)Math.Round(x0 + dx * t);
                int gz = (int)Math.Round(z0 + dz * t);
                if (gx >= 0 && gz >= 0 && gx < n && gz < n)
                    grid[gz * n + gx] = 1;
            }
        }

        /// <summary>dst |= src shifted by (+dx, +dz) cells, clipped.</summary>
        private static void OrShifted(byte[] dst, byte[] src, int n, int dx, int dz)
        {
            int zStart = Math.Max(0, dz), zEnd = Math.Min(n, n + dz);
            int xStart = Math.Max(0, dx), xEnd = Math.Min(n, n + dx);
            for (int z = zStart; z < zEnd; z++)
            {
                int srcRow = (z - dz) * n - dx;
                int dstRow = z * n;
                for (int x = xStart; x < xEnd; x++)
                {
                    if (src[srcRow + x] != 0) dst[dstRow + x] = 1;
                }
            }
        }

        private static void BoxBlur(double[] a, int n, int r)
        {
            var tmp = new double[n * n];
            // horizontal
            for (int y = 0; y < n; y++)
            {
                int row = y * n;
                double sum = 0;
                int cnt = 0;
                for (int x = 0; x <= r && x < n; x++) { sum += a[row + x]; cnt++; }
                for (int x = 0; x < n; x++)
                {
                    tmp[row + x] = sum / cnt;
                    int add = x + r + 1, rem = x - r;
                    if (add < n) { sum += a[row + add]; cnt++; }
                    if (rem >= 0) { sum -= a[row + rem]; cnt--; }
                }
            }
            // vertical
            for (int x = 0; x < n; x++)
            {
                double sum = 0;
                int cnt = 0;
                for (int y = 0; y <= r && y < n; y++) { sum += tmp[y * n + x]; cnt++; }
                for (int y = 0; y < n; y++)
                {
                    a[y * n + x] = sum / cnt;
                    int add = y + r + 1, rem = y - r;
                    if (add < n) { sum += tmp[add * n + x]; cnt++; }
                    if (rem >= 0) { sum -= tmp[rem * n + x]; cnt--; }
                }
            }
        }

        // ---------------------------------------------------------------
        // v16.1: SHADOW CATCHER. The v16.0 floor carried its own pool-
        // of-light gradient with an alpha rim -- laid over the backdrop
        // that reads as a visible disc around the car, which the
        // reference does NOT have: its ground is nothing but the cove
        // gradient plus the car's shadow. The quad now carries ONLY the
        // projected contact shadow: the colour is the measured shadow
        // tone (the reference's contact reads [68,26,6] over a
        // [177,92,22] pool -- i.e. the floor blending toward TintPow at
        // L = 0.30 at ~95% opacity) and the ALPHA IS THE SHADOW MASK.
        // Everywhere else the quad is fully transparent, so the
        // surrounding area is exactly the skybox cove.
        // ---------------------------------------------------------------
        private static TextureModel BuildFloorTexture(Color tint, ShadowMask shadow, int size = 1024)
        {
            TintPow(tint, 0.30, out double sr, out double sg, out double sb);
            byte[] px = new byte[size * size * 4];
            Parallel.For(0, size, y =>
            {
                double v = y / (double)(size - 1);
                for (int x = 0; x < size; x++)
                {
                    double u = x / (double)(size - 1);
                    double wx = shadow.MinX + u * shadow.SizeX;
                    double wz = shadow.MinZ + v * shadow.SizeZ;
                    double a = 0.95 * shadow.Sample(wx, wz);

                    int o = (y * size + x) * 4;
                    px[o + 0] = ClampB(sb);
                    px[o + 1] = ClampB(sg);
                    px[o + 2] = ClampB(sr);
                    px[o + 3] = ClampB(a * 255.0);
                }
            });
            var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
            bmp.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            var ms = new MemoryStream();
            enc.Save(ms);
            ms.Position = 0;
            return new TextureModel(ms);
        }

        /// <summary>
        /// Populate the Viewport3DX with the showroom: converted car,
        /// floor, lights, environment map, camera. Returns false if
        /// anything throws (caller falls back to the WPF showroom).
        /// </summary>
        public static bool Populate(Viewport3DX vp, Media3D.Model3DGroup carRoot,
                                    Color tint, double floorY,
                                    Media3D.Point3D camPos, Media3D.Vector3D camLook,
                                    double fov)
        {
            try
            {
                vp.Items.Clear();
                _carMats.Clear();   // v14.4: fresh registry per populate
                _convertedNodes.Clear();   // v17.2: fresh node map per populate
                if (vp.EffectsManager == null)
                    vp.EffectsManager = new DefaultEffectsManager();

                _lastTint = tint;
                _vpLive = vp;

                // v16.0: irradiance FIRST -- ConvertMaterial hands it to
                // every car material as it converts.
                _irrTex = BuildIrradianceCubemap(tint, Brightness, Reflection);

                // v16.1: the skybox RENDERS and IS the backdrop (header
                // note 1) -- the cubemap sides are the fitted reference
                // field mapped through the default camera, so no part of
                // the look depends on D3DImage alpha compositing.
                _envRef = new EnvironmentMap3D
                {
                    Texture = BuildEnvironmentCubemap(tint, Brightness, Reflection),
                };
                vp.Items.Add(_envRef);

                // v16.0: near-black ambient (header note 2 -- on PBR it
                // is a flat additive fog, not a light).
                _ambientRef = new AmbientLight3D { Color = AmbientColorFor() };
                vp.Items.Add(_ambientRef);
                _keyRef = new DirectionalLight3D
                {
                    Color = KeyColor(),
                    Direction = KeyDir     // from the softbox: streak + top punch align
                };
                vp.Items.Add(_keyRef);
                _fillRef = new DirectionalLight3D
                {
                    Color = FillColorFor(),
                    Direction = new Media3D.Vector3D(0.65, -0.30, 0.45)
                };
                vp.Items.Add(_fillRef);
                _rimRef = new DirectionalLight3D
                {
                    Color = RimColor(),
                    Direction = new Media3D.Vector3D(0.05, -0.15, 0.95)
                };
                vp.Items.Add(_rimRef);

                var carElements = ConvertModelTree(carRoot);
                var carGroup = new GroupModel3D();
                foreach (var e in carElements) carGroup.Children.Add(e);
                vp.Items.Add(carGroup);

                // ---- the ground: a pure shadow catcher laid over the
                // skybox floor (see the BuildFloorTexture notes) ----
                var shadow = BuildShadowMask(carRoot, floorY);
                if (shadow != null)
                {
                    float by = (float)floorY;
                    float x0 = (float)shadow.MinX, z0 = (float)shadow.MinZ;
                    float x1 = (float)(shadow.MinX + shadow.SizeX);
                    float z1 = (float)(shadow.MinZ + shadow.SizeZ);
                    var floorMesh = new MeshGeometry3D
                    {
                        Positions = new Vector3Collection
                        {
                            new Vector3(x0, by, z0),
                            new Vector3(x1, by, z0),
                            new Vector3(x1, by, z1),
                            new Vector3(x0, by, z1),
                        },
                        Normals = new Vector3Collection
                        {
                            new Vector3(0, 1, 0), new Vector3(0, 1, 0),
                            new Vector3(0, 1, 0), new Vector3(0, 1, 0),
                        },
                        TextureCoordinates = new Vector2Collection
                        {
                            new Vector2(0, 0), new Vector2(1, 0),
                            new Vector2(1, 1), new Vector2(0, 1),
                        },
                        Indices = new IntCollection { 0, 2, 1, 0, 3, 2 },
                    };
                    var floorMat = new DiffuseMaterial
                    {
                        DiffuseColor = new Color4(1f, 1f, 1f, 1f),
                        DiffuseMap = BuildFloorTexture(tint, shadow),
                        EnableUnLit = true, // genuinely unlit (v16.0 fix)
                    };
                    vp.Items.Add(new MeshGeometryModel3D
                    {
                        Geometry = floorMesh,
                        Material = floorMat,
                        IsTransparent = true,   // alpha IS the shadow
                        IsThrowingShadow = false,
                        CullMode = SharpDX.Direct3D11.CullMode.None,
                    });
                }

                vp.Camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera
                {
                    Position = camPos,
                    LookDirection = camLook,
                    UpDirection = new Media3D.Vector3D(0, 1, 0),
                    FieldOfView = fov,
                    // v16.4: 0.05/500 gave a 10000:1 depth range --
                    // most Z precision burnt within centimetres of the
                    // camera, so stacked thin elements at the FAR end
                    // (wing flaps, suspension arms) z-fought and
                    // shimmered. 0.2/300 (1500:1) keeps them stable;
                    // the skybox draws at max depth regardless, and
                    // the scene never legitimately reaches 300.
                    NearPlaneDistance = 0.2,
                    FarPlaneDistance = 300,
                };

                // v16.5: 2x SUPERSAMPLED render target. The DpiScale DP
                // only reaches the render canvas when its value CHANGES
                // while the canvas exists -- and on first activation
                // the canvas is created AFTER Populate returns (the
                // viewport sits Collapsed until the caller flips it,
                // and a collapsed control never applies its template).
                // So the push is deferred to Loaded priority, and
                // toggled through 1.0 when already applied so a
                // recreated canvas (device loss) picks it up again.
                var vpRef = vp;
                vp.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (Math.Abs(vpRef.DpiScale - SuperSampling) < 0.001)
                            vpRef.DpiScale = 1.0;
                        vpRef.DpiScale = SuperSampling;
                    }
                    catch { }
                }), DispatcherPriority.Loaded);

                return true;
            }
            catch
            {
                try { vp.Items.Clear(); } catch { }
                return false;
            }
        }

        /// <summary>
        /// v17.2: FAST DRIVER-NUMBER SWAP. Re-targets the DX copies of the
        /// given WPF materials to a new WPF material without repopulating
        /// the scene -- geometry, lights, env, backdrop and the cast
        /// shadow are all unchanged by a number swap, so rebuilding them
        /// is pure waste. The new material is converted ONCE (tagged
        /// game-material response included, current irradiance applied)
        /// and assigned to every matching node; the node map is updated so
        /// repeated swaps chain. The superseded PBR entries linger in the
        /// brightness registry until the next Populate -- harmless, they
        /// are no longer attached to any node. Returns nodes retargeted.
        /// </summary>
        public static int SwapConvertedMaterials(
            IList<Media3D.Material> oldMaterials, Media3D.Material newMaterial)
        {
            try
            {
                if (oldMaterials == null || oldMaterials.Count == 0 ||
                    newMaterial == null || _convertedNodes.Count == 0)
                    return 0;
                var match = new HashSet<Media3D.Material>(oldMaterials);
                HelixToolkit.Wpf.SharpDX.Material converted = null;
                var texCache = new Dictionary<BitmapSource, TextureModel>();
                int n = 0;
                foreach (var cn in _convertedNodes)
                {
                    if (cn.Node == null || cn.Source == null) continue;
                    if (!match.Contains(cn.Source)) continue;
                    if (converted == null)
                        converted = ConvertMaterial(newMaterial, texCache);
                    cn.Node.Material = converted;
                    cn.Source = newMaterial;
                    n++;
                }
                return n;
            }
            catch { return 0; }
        }

        /// <summary>
        /// UE EXTENSION -- the default-view populate: the same
        /// high-quality PBR conversion, supersampling and MSAA as the
        /// showroom, and Jefry's showroom lighting verbatim (tinted
        /// irradiance + environment cove feeding the reflections, plus
        /// the ambient/key/fill/rim rig). Only the floor/shadow
        /// furniture and the camera are omitted -- the caller owns the
        /// camera.
        /// </summary>
        public static bool PopulateNeutral(Viewport3DX vp, Media3D.Model3DGroup carRoot)
        {
            try
            {
                vp.Items.Clear();
                _carMats.Clear();
                _convertedNodes.Clear();
                if (vp.EffectsManager == null)
                    vp.EffectsManager = new DefaultEffectsManager();
                _vpLive = vp;

                var tint = Color.FromRgb(0x48, 0x48, 0x48);
                _irrTex = BuildIrradianceCubemap(tint, Brightness, Reflection);
                _envRef = new EnvironmentMap3D
                {
                    Texture = BuildEnvironmentCubemap(tint, Brightness, Reflection),
                    // The cubemap feeds the PBR reflections but its
                    // skybox draw is skipped -- the backdrop stays the
                    // viewport's own black.
                    SkipRendering = true,
                };
                vp.Items.Add(_envRef);

                _ambientRef = new AmbientLight3D { Color = AmbientColorFor() };
                vp.Items.Add(_ambientRef);
                _keyRef = new DirectionalLight3D
                {
                    Color = KeyColor(),
                    Direction = KeyDir
                };
                vp.Items.Add(_keyRef);
                _fillRef = new DirectionalLight3D
                {
                    Color = FillColorFor(),
                    Direction = new Media3D.Vector3D(0.65, -0.30, 0.45)
                };
                vp.Items.Add(_fillRef);
                _rimRef = new DirectionalLight3D
                {
                    Color = RimColor(),
                    Direction = new Media3D.Vector3D(0.05, -0.15, 0.95)
                };
                vp.Items.Add(_rimRef);

                var carElements = ConvertModelTree(carRoot);
                var carGroup = new GroupModel3D();
                foreach (var e in carElements) carGroup.Children.Add(e);
                vp.Items.Add(carGroup);

                // 2x supersampled render target (same deferral dance as
                // Populate -- see the notes there).
                var vpRef = vp;
                vp.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (Math.Abs(vpRef.DpiScale - SuperSampling) < 0.001)
                            vpRef.DpiScale = 1.0;
                        vpRef.DpiScale = SuperSampling;
                    }
                    catch { }
                }), DispatcherPriority.Loaded);

                return true;
            }
            catch
            {
                try { vp.Items.Clear(); } catch { }
                return false;
            }
        }

        public static void Clear(Viewport3DX vp)
        {
            try { vp?.Items.Clear(); } catch { }
            try { _envDebounce?.Stop(); } catch { }
            _ambientRef = null; _keyRef = null; _fillRef = null; _rimRef = null;
            _envRef = null; _irrTex = null; _vpLive = null;
            _carMats.Clear();
            _convertedNodes.Clear();   // v17.2
            _auxTexCache.Clear();   // v17.0: aux textures die with the scene
        }
    }
}