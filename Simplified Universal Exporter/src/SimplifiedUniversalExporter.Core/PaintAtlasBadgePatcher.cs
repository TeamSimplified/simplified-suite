using System;

namespace SimplifiedUniversalExporter.Core
{
    /// <summary>
    /// FIX 30: patches the "metadata badge corner" of F1 22 paint atlases.
    ///
    /// Background
    /// ----------
    /// From F1 2017 through F1 2021, team paint atlases (e.g.
    /// <c>mclaren_paint_d.tif</c>) placed the primary livery colour at
    /// the top-left corner. Body-panel submeshes authored with heavily
    /// tiled UVs like <c>uv=[-64, 0.09] × [-60, 0.10]</c> rely on that
    /// convention: after tile wrap, the effective sample lands near
    /// <c>(0, 0)</c>, which on a 2021 McLaren is papaya orange.
    ///
    /// In F1 22, Codemasters added a metadata badge (COMPETITOR / 2022 /
    /// GRAND PRIX / CHASSIS / Signature) to the top-left of every team
    /// paint atlas. The primary paint colour got pushed down to roughly
    /// <c>v=[0.25, 0.70)</c>. The game compensates at shader time -- we
    /// don't run the shader, so submeshes that used to sample pure paint
    /// at <c>(0, 0)</c> now sample the badge (dark blue + white text),
    /// producing the "mostly dark blue car" visible in F1 22 previews.
    ///
    /// On the F1 22 McLaren, 17 of 19 body panels have the tile-wrap UV
    /// pattern and therefore sample the badge corner. Only 2 panels
    /// (<c>sidepod_fl</c>, <c>rearwingmoving_top</c>) have clean
    /// in-bounds UVs that hit the legitimate orange / cyan regions of
    /// the atlas -- and those render correctly already.
    ///
    /// Strategy
    /// --------
    /// Patch ONLY the small top-left corner of the atlas that tile-wrap
    /// UVs land in -- roughly <c>u=[0, 0.125) × v=[0, 0.125)</c>. We
    /// overwrite that region with the team's primary paint colour,
    /// sampled from a known-safe interior coordinate (<c>u=0.25,
    /// v=0.45</c>, verified pure paint on McLaren and structurally so
    /// for any team whose atlas follows the F1 22 layout).
    ///
    /// This approach avoids the pitfalls of the earlier rotation attempt
    /// (FIX 29, reverted) which broke panels like <c>sidepod_fl</c> whose
    /// UVs genuinely cross the orange/cyan boundary -- that panel stays
    /// byte-identical because the fill region <c>u&lt;0.125</c> is
    /// strictly left of <c>sidepod_fl</c>'s <c>u=0.14</c> start.
    ///
    /// Self-gating for year and team safety
    /// ------------------------------------
    /// The patcher only fires when BOTH conditions hold:
    ///
    ///   (1) The top-left corner of the atlas LOOKS like a badge --
    ///       i.e. it contains a mix of non-paint colours (badge blue +
    ///       white text edges). Detection: quantize the corner and
    ///       check whether the dominant colour is non-chromatic
    ///       (badge dark blue and white both fail the chroma test)
    ///       OR the corner has low dominance (many distinct colours
    ///       from text edges).
    ///
    ///   (2) The sampled "team primary paint" coordinate produces a
    ///       saturated, mid-brightness, chromatic colour. If the
    ///       sample point happens to land on a logo or decal, the
    ///       test fails and we don't patch.
    ///
    /// F1 2021 McLaren atlases have papaya orange at the corner --
    /// condition (1) fails, no patch applied. Unchanged behaviour.
    ///
    /// Mercedes-silver or other low-chroma liveries -- condition (2)
    /// might fail. No patch applied; panels stay as-rendered. Worst
    /// case: same as current (no-op) behaviour.
    ///
    /// Ferrari, Williams, Aston Martin etc. -- if Codemasters used the
    /// same F1 22 badge-at-top authoring convention, condition (1)
    /// fires and the corner is patched with the team's primary paint
    /// (Ferrari red, Williams blue, etc.) sampled from the interior.
    /// If their atlas diverges from the convention, we fall back to
    /// no-op safely.
    /// </summary>
    internal static class PaintAtlasBadgePatcher
    {
        // ---------------------------------------------------------------------
        // Fill geometry
        // ---------------------------------------------------------------------

        /// <summary>
        /// Width of the top-left fill region as a fraction of atlas width.
        /// 1/8 was chosen to cover observed tile-wrap UV rects like
        /// <c>u=[-64, 0.09]</c> (which post-wrap land in <c>[0, ~0.10)</c>)
        /// with a small margin, while staying well left of the
        /// <c>sidepod_fl</c> panel's UV start at <c>u=0.14</c>.
        /// </summary>
        private const double FillWidthFraction = 0.125;

        /// <summary>
        /// Height of the top-left fill region as a fraction of atlas height.
        /// 1/8 matches tile-wrap v ranges which land in <c>[0, 0.10)</c>,
        /// and stays above the <c>sidepod_fl</c> panel's v start at
        /// <c>v=0.22</c>. Also well within the badge's vertical extent.
        /// </summary>
        private const double FillHeightFraction = 0.125;

        // ---------------------------------------------------------------------
        // Where to sample "team primary paint" from inside the atlas.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Normalized U of the sampling point. 0.25 is the centre of the
        /// atlas's left-half primary-paint quadrant in F1 22 authoring.
        /// </summary>
        private const double PrimarySampleU = 0.25;

        /// <summary>
        /// Normalized V of the sampling point. 0.45 is midway through the
        /// papaya orange band on F1 22 McLaren (v=[0.25, 0.70)) and is
        /// well clear of the badge (above) and carbon band (below).
        /// </summary>
        private const double PrimarySampleV = 0.45;

        // ---------------------------------------------------------------------
        // Paint-colour detector thresholds (shared between gate + validate)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Minimum chroma (max-min across RGB channels) for a colour to
        /// be considered "paint-like". Excludes greyscale (silver, white
        /// text), near-black (carbon), near-white (badge text). 80 is
        /// chosen to reject F1 22's dark-blue badge (chroma ~64) while
        /// accepting every observed real livery colour.
        /// </summary>
        private const int MinPaintChroma = 80;

        /// <summary>Minimum brightness for a paint-like colour.</summary>
        private const int MinPaintBrightness = 50;

        /// <summary>Maximum brightness for a paint-like colour.</summary>
        private const int MaxPaintBrightness = 210;

        /// <summary>
        /// If the top-left corner's dominant bucket occupies at least
        /// this fraction AND is paint-like, we consider the corner to
        /// ALREADY be paint and skip the patch. Otherwise the corner
        /// has enough non-paint content to warrant patching.
        /// </summary>
        private const double PaintCornerDominanceThreshold = 0.85;

        // ---------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------

        /// <summary>
        /// Detect whether the atlas top-left corner contains badge-like
        /// (non-paint) content and, if so, fill the same corner with the
        /// team's primary paint colour sampled from the interior.
        /// Returns the input buffer unchanged when no patch is needed.
        /// Never throws; any internal failure returns the input intact.
        /// </summary>
        /// <param name="bgra">BGRA32 pixel buffer; length = width*height*4.</param>
        /// <param name="width">Atlas width in pixels.</param>
        /// <param name="height">Atlas height in pixels.</param>
        /// <param name="log">Diagnostic sink; may be null.</param>
        /// <param name="textureLabel">Short texture name for log lines.</param>
        /// <returns>
        /// (modified buffer, true if patched) or (original buffer, false if no-op).
        /// The "modified buffer" is always a fresh byte[] when patched -- the
        /// input is never mutated in place. Safe to call on a cached buffer.
        /// </returns>
        public static (byte[] Bgra, bool Patched) MaybePatchBadgeCorner(
            byte[] bgra, int width, int height,
            Action<string> log, string textureLabel)
        {
            if (bgra == null || width <= 0 || height <= 0)
                return (bgra, false);
            if (bgra.Length != width * height * 4)
                return (bgra, false);

            try
            {
                int fillW = Math.Max(1, (int)(width  * FillWidthFraction));
                int fillH = Math.Max(1, (int)(height * FillHeightFraction));

                // Gate (1): does the corner LOOK like a badge (not paint)?
                if (!CornerLooksLikeBadge(bgra, width, fillW, fillH))
                {
                    // Corner is already paint-like (F1 2021 layout, or a
                    // team whose atlas diverges from F1 22 convention).
                    // Don't touch it.
                    return (bgra, false);
                }

                // Gate (2): is the sampled "primary paint" coordinate a
                // valid paint colour? If the sample lands on a decal or
                // some non-paint feature, bail out conservatively.
                int sampleX = (int)(width  * PrimarySampleU);
                int sampleY = (int)(height * PrimarySampleV);
                if (!TrySampleValidPaintColor(bgra, width, height, sampleX, sampleY,
                                              out byte paintR, out byte paintG, out byte paintB))
                {
                    log?.Invoke($"  paint-atlas patch: {textureLabel} corner looks like badge " +
                                $"but interior sample at (u={PrimarySampleU:0.00}, v={PrimarySampleV:0.00}) " +
                                "isn't a clean paint colour; leaving atlas unchanged");
                    return (bgra, false);
                }

                byte[] patched = FillCornerWithColor(
                    bgra, width, height, fillW, fillH,
                    paintR, paintG, paintB);

                log?.Invoke($"  paint-atlas patch: {textureLabel} " +
                            $"{fillW}x{fillH}px corner filled with sampled primary " +
                            $"rgb({paintR},{paintG},{paintB}) " +
                            $"(tile-wrap body panels will now sample this instead of the badge)");

                return (patched, true);
            }
            catch (Exception ex)
            {
                log?.Invoke($"  paint-atlas patch: {textureLabel} threw; leaving atlas unchanged: {ex.Message}");
                return (bgra, false);
            }
        }

        // ---------------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------------

        /// <summary>
        /// Returns true when the top-left <paramref name="regionW"/> by
        /// <paramref name="regionH"/> corner does NOT look like a clean
        /// saturated paint colour. "Doesn't look like paint" is defined
        /// as: either (a) the dominant coarse-quantized colour occupies
        /// less than <see cref="PaintCornerDominanceThreshold"/> of the
        /// region (noisy -- text edges, badge borders), OR (b) the
        /// dominant colour fails the paint-like test (low chroma --
        /// badge dark-blue / white).
        ///
        /// The 4x4x4 RGB bucket quantization is deliberately coarse so
        /// JPEG/BC1 minor colour variation within a solid-paint region
        /// all collapses to one bucket, letting "clean paint" pass the
        /// dominance test with high confidence.
        /// </summary>
        private static bool CornerLooksLikeBadge(
            byte[] bgra, int width, int regionW, int regionH)
        {
            var counts = new int[64]; // 4x4x4 RGB cube
            int stride = width * 4;

            for (int y = 0; y < regionH; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < regionW; x++)
                {
                    int px = rowBase + x * 4;
                    int b = bgra[px];
                    int g = bgra[px + 1];
                    int r = bgra[px + 2];

                    int bucket = ((r >> 6) << 4) | ((g >> 6) << 2) | (b >> 6);
                    counts[bucket]++;
                }
            }

            int total = regionW * regionH;
            int domBucket = 0;
            int domCount = counts[0];
            for (int i = 1; i < 64; i++)
            {
                if (counts[i] > domCount)
                {
                    domCount = counts[i];
                    domBucket = i;
                }
            }

            double domFrac = (double)domCount / total;
            if (domFrac < PaintCornerDominanceThreshold)
            {
                // Noisy / multi-color corner -> badge + text
                return true;
            }

            // Dominant bucket occupies most of the corner -- is that
            // dominant colour paint-like? If yes, corner is clean paint.
            // If no, corner is solid non-paint (badge dark blue, etc.).
            int rHi = ((domBucket >> 4) & 0x3) * 64 + 32;
            int gHi = ((domBucket >> 2) & 0x3) * 64 + 32;
            int bHi = (domBucket & 0x3)        * 64 + 32;

            int chroma = Math.Max(rHi, Math.Max(gHi, bHi))
                       - Math.Min(rHi, Math.Min(gHi, bHi));
            int brightness = (rHi + gHi + bHi) / 3;

            bool isPaintLike =
                chroma >= MinPaintChroma &&
                brightness >= MinPaintBrightness &&
                brightness <= MaxPaintBrightness;

            return !isPaintLike;
        }

        /// <summary>
        /// Read pixel at <paramref name="x"/>, <paramref name="y"/> and
        /// verify it passes the paint-like colour test. Returns true on
        /// success with the RGB channels as out parameters.
        /// </summary>
        private static bool TrySampleValidPaintColor(
            byte[] bgra, int width, int height, int x, int y,
            out byte r, out byte g, out byte b)
        {
            r = 0; g = 0; b = 0;
            if (x < 0 || x >= width || y < 0 || y >= height) return false;

            int px = (y * width + x) * 4;
            if (px + 3 >= bgra.Length) return false;

            byte bb = bgra[px];
            byte gg = bgra[px + 1];
            byte rr = bgra[px + 2];

            int chroma = Math.Max(rr, Math.Max(gg, bb))
                       - Math.Min(rr, Math.Min(gg, bb));
            int brightness = (rr + gg + bb) / 3;

            if (chroma < MinPaintChroma) return false;
            if (brightness < MinPaintBrightness) return false;
            if (brightness > MaxPaintBrightness) return false;

            r = rr; g = gg; b = bb;
            return true;
        }

        /// <summary>
        /// Produce a new BGRA buffer identical to <paramref name="bgra"/>
        /// except the top-left <paramref name="regionW"/> by
        /// <paramref name="regionH"/> block is overwritten with
        /// (<paramref name="r"/>, <paramref name="g"/>, <paramref name="b"/>).
        /// Alpha is preserved from the source pixels (F1 paint atlases
        /// are fully opaque in practice, but don't assume).
        /// </summary>
        private static byte[] FillCornerWithColor(
            byte[] bgra, int width, int height, int regionW, int regionH,
            byte r, byte g, byte b)
        {
            var output = new byte[bgra.Length];
            Buffer.BlockCopy(bgra, 0, output, 0, bgra.Length);

            int stride = width * 4;
            for (int y = 0; y < regionH; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < regionW; x++)
                {
                    int px = rowBase + x * 4;
                    // BGRA32 layout
                    output[px]     = b;
                    output[px + 1] = g;
                    output[px + 2] = r;
                    // output[px + 3] is unchanged -- keep source alpha
                }
            }

            return output;
        }
    }
}
