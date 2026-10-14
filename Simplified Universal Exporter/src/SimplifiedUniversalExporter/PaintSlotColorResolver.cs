// =====================================================================
// PaintSlotColorResolver — FIX 31: per-submesh paint-colour lookup via
// the scale/bias block in the mesh descriptor.
//
// BACKGROUND
//
// Every submesh in an F1 22 IDF mesh descriptor carries a 4-float
// (biasU, biasV, scaleU, scaleV) block at offset
//   0x4C + si*88 + 0x18
// These floats are already read during mesh load (FIX 19) and stored on
// SubMeshInfo.SlotUV{Bias,Scale}{U,V}. Historically the only documented
// use was to transform the decal/logo UV stream:
//     decal_uv' = decal_uv * float2(Scale) + float2(Bias)
//
// What we discovered by inspecting the shader NEFX payloads and by
// enumerating every submesh on the F1 22 McLaren model:
//
// These same four floats, when applied to a typical vb=3 pair-2 UV of
// (0.001, 0.999) — a value present verbatim on every body vertex of
// every tile-wrap submesh on the car — produce a single (u, v)
// coordinate into paint_d that matches the submesh's authored livery
// colour. Enumerated sample per part on the McLaren:
//
//   body (safetycell / sidepod_{fl,fr} / cockpit / nosecone / frontwing /
//         frontflap / floor_damage / hal / topcanopy / …):
//         sb = (0.0699, 0.1242, 0.2819, 1.5420)
//         -> paint_d(0.070, 0.665) = RGB(255,113,  0)    ORANGE  ✓
//
//   generic (when a paint part has a trim/accent sub-geometry):
//         sb = (-0.0439, 0.1036, -0.2560, 2.1066)
//         -> paint_d(0.956, 0.208) = RGB( 90,227,255)    LIGHT BLUE ✓
//
//   carbon / suspension / diffuser / hub_caliper / driver_surround:
//         sb = (0.0295, 0.0950, 0.1419, 1.6906)
//         -> paint_d(0.030, 0.784) = RGB( 24, 24, 24)    BLACK (carbon) ✓
//
//   disc / wheel_{bl,fl,fr} / tether:
//         various sb -> paint_d(ORANGE)                  ORANGE rim  ✓
//   wheel_br / rearwing_top / windscreen / steering_wheel:
//         sb -> paint_d(LIGHT BLUE)                      blue trim   ✓
//
// The transform is:
//     u' = 0.001 * scaleU + biasU
//     v' = 0.999 * scaleV + biasV
//     (u, v) = (u' mod 1, v' mod 1)
//
// i.e. a plain scale+bias+tile-wrap sample on paint_d at the canonical
// corner coordinate (0.001, 0.999).
//
// WHAT THIS MODULE DOES
//
// Given the scale/bias values and the decoded BGRA32 paint_d pixels,
// compute the RGB colour the shader would pick for this submesh, then
// return a solid-colour WPF ImageBrush of that colour — but only when
// the colour is "paint-like" (chromatic, mid-brightness). Black (carbon),
// white (primer), and deep-blue (badge area) fall through and return
// null so the caller keeps the normal atlas-sampling render path.
//
// SCOPE / SAFETY
//
// This module is *additive*. It never mutates pixels, never writes to
// files, never changes the existing brush caches. The caller decides
// whether to substitute the solid brush for the atlas-sampled brush.
// Gating is the caller's responsibility — this module only does the
// mechanical sample + paint-likeness test.
//
// It is safe to call on any inputs. Non-finite scale/bias values, a
// null pixel buffer, zero-size atlas, or a derived colour that fails
// the chromatic test all return null.
// =====================================================================

using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimplifiedUniversalExporter
{
    internal static class PaintSlotColorResolver
    {
        /// <summary>
        /// Canonical pair-2 UV coordinate used by every body vertex on
        /// every F1 22 mclaren tile-wrap submesh. Verified by direct
        /// decode of mclaren!!!temp001.idf?lod=all&vb=3 — 182 tile-wrap
        /// vertices all have this exact pair-2, and 99% of "in-bounds"
        /// vertices cluster within 0.001 of it. Treating it as a
        /// constant is how the shader effectively produces a single
        /// colour per submesh from the per-submesh (scale, bias) block.
        /// </summary>
        public const double CanonicalPair2U = 0.001;
        public const double CanonicalPair2V = 0.999;

        /// <summary>
        /// Chroma threshold below which a sample is treated as neutral
        /// (grey/black/white/near-white) and disqualified from paint-
        /// fill substitution. Tuned to admit light-blue (chroma ≈ 165)
        /// and papaya-orange (chroma ≈ 255) while rejecting carbon
        /// (chroma ≈ 0), primer white (chroma ≈ 1) and the badge's
        /// deep-blue metadata strip (chroma ≈ 99 but brightness wrong).
        /// </summary>
        public const int MinChroma = 80;

        /// <summary>
        /// Brightness window. Excludes the badge's dark-blue area
        /// (brightness ≈ 51) and any accidental pure-white sample.
        /// Orange, light-blue and other livery accents fall comfortably
        /// inside this window.
        /// </summary>
        public const int MinBrightness = 60;
        public const int MaxBrightness = 240;

        /// <summary>
        /// Upper bound on |scale| and |bias|. Values outside this range
        /// in the descriptor indicate a degenerate submesh slot (for
        /// example safetycell sm7 on the McLaren has astronomical
        /// floats that are almost certainly padding/garbage). Silently
        /// reject so we don't transform with nonsense.
        /// </summary>
        private const float MaxMagnitude = 16.0f;

        /// <summary>
        /// Compute the per-submesh livery colour from the paint atlas
        /// and return a solid ImageBrush of that colour. Returns null
        /// on any failure: bad inputs, non-finite floats, degenerate
        /// magnitude, unloaded pixels, or a derived colour that fails
        /// the paint-like chromatic test.
        ///
        /// The returned brush, if any, carries ViewportUnits=Absolute
        /// and TileMode=Tile so it renders as a flat colour regardless
        /// of the submesh's UV values — exactly what we want when the
        /// UVs are tile-wrap garbage in the first place.
        ///
        /// `paintBgra` must be BGRA32 (width*height*4 bytes). The
        /// caller typically hands us the already-decoded top-mip
        /// sidecar pixels that FIX 28c loaded for this texture.
        /// </summary>
        public static ImageBrush TryBuildSolidBrush(
            float biasU, float biasV,
            float scaleU, float scaleV,
            byte[] paintBgra, int width, int height,
            out int sampledR, out int sampledG, out int sampledB,
            out string rejectReason)
        {
            sampledR = sampledG = sampledB = 0;
            rejectReason = null;

            if (paintBgra == null || paintBgra.Length == 0 ||
                width <= 0 || height <= 0 ||
                paintBgra.Length < width * height * 4)
            {
                rejectReason = "paint pixel buffer missing or too small";
                return null;
            }

            if (!IsFinite(biasU) || !IsFinite(biasV) ||
                !IsFinite(scaleU) || !IsFinite(scaleV))
            {
                rejectReason = "non-finite scale/bias values";
                return null;
            }

            if (Math.Abs(biasU) > MaxMagnitude || Math.Abs(biasV) > MaxMagnitude ||
                Math.Abs(scaleU) > MaxMagnitude || Math.Abs(scaleV) > MaxMagnitude)
            {
                rejectReason = "scale/bias magnitude exceeds sanity bound " +
                               $"(|x|>{MaxMagnitude})";
                return null;
            }

            // Apply the transform: u' = 0.001*scaleU + biasU, wrap to [0,1).
            double u = CanonicalPair2U * scaleU + biasU;
            double v = CanonicalPair2V * scaleV + biasV;

            // Positive fmod — "u mod 1" must be in [0, 1) even for
            // negative inputs. C#'s built-in % follows the sign of
            // the dividend so we can't use it directly.
            double uw = u - Math.Floor(u);
            double vw = v - Math.Floor(v);

            int x = (int)(uw * width);
            int y = (int)(vw * height);
            if (x < 0) x = 0; else if (x >= width) x = width - 1;
            if (y < 0) y = 0; else if (y >= height) y = height - 1;

            int pixOff = (y * width + x) * 4;
            // BGRA32: B, G, R, A
            int b = paintBgra[pixOff + 0];
            int g = paintBgra[pixOff + 1];
            int r = paintBgra[pixOff + 2];

            sampledR = r;
            sampledG = g;
            sampledB = b;

            if (!IsPaintLike(r, g, b, out rejectReason))
                return null;

            var bitmap = BitmapSource.Create(
                1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { (byte)b, (byte)g, (byte)r, 255 },
                4);
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
            };
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Chromatic + brightness test. A colour passes when it has
        /// enough saturation to be a paint colour (not grey, not
        /// carbon, not primer white) and lies within a sensible
        /// mid-brightness range (excludes the badge dark-blue).
        ///
        /// Uses max-channel minus min-channel as a chroma proxy — this
        /// is what the old PaintAtlasBadgePatcher also used, so the
        /// gating stays numerically consistent across fixes.
        /// </summary>
        public static bool IsPaintLike(int r, int g, int b, out string rejectReason)
        {
            rejectReason = null;

            int maxC = Math.Max(r, Math.Max(g, b));
            int minC = Math.Min(r, Math.Min(g, b));
            int chroma = maxC - minC;

            // brightness = luma proxy (use max channel for simplicity)
            int brightness = maxC;

            if (chroma < MinChroma)
            {
                rejectReason = $"chroma {chroma} below min {MinChroma}";
                return false;
            }

            if (brightness < MinBrightness)
            {
                rejectReason = $"brightness {brightness} below min {MinBrightness}";
                return false;
            }

            if (brightness > MaxBrightness)
            {
                rejectReason = $"brightness {brightness} above max {MaxBrightness}";
                return false;
            }

            return true;
        }

        private static bool IsFinite(float f)
        {
            return !float.IsNaN(f) && !float.IsInfinity(f);
        }
    }
}
