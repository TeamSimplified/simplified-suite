using System;

namespace F1modding3dtoolV1
{
    /// <summary>
    /// FIX 29: compensates for the F1 22 paint-atlas authoring change.
    ///
    /// Background
    /// ----------
    /// From F1 2017 through F1 2021, Codemasters authored team paint
    /// atlases (e.g. <c>mclaren_paint_d.tif</c>) with the primary livery
    /// colour occupying the top-left of the atlas. Body-panel submesh
    /// UVs -- including heavily tiled cases like safetycell's
    /// <c>uv=[-64, 0.09] x [-60, 0.10]</c> -- rely on this: after tile
    /// wrap the effective sample lands near <c>(0, 0)</c>, which on a
    /// 2021 McLaren is papaya orange.
    ///
    /// In F1 22 Codemasters added a metadata badge ("COMPETITOR 2022 /
    /// GRAND PRIX / CHASSIS MCL36 / Signature") to the TOP-LEFT quadrant
    /// of every team paint atlas. The primary livery colour got pushed
    /// down to roughly <c>v=[0.25, 0.75)</c>. The game compensates via
    /// its <c>vehicle_metallic_paint.nefx2</c> shader, which applies a
    /// hardcoded V-shift at sample time. This preview does not run that
    /// shader, so submeshes that used to land on orange now land on the
    /// badge (dark blue + white text), producing the "mostly blue car"
    /// you see under the F1 22 path.
    ///
    /// The material-parameter pass confirmed that the F1 22 McLaren paint
    /// material has <c>me_vPaintOffset = 0.0</c>, so the offset lives in
    /// the shader bytecode, not in any data file we read. Until we parse
    /// shader bytecode (substantial work), the cleanest fix is to
    /// reorient the ATLAS to match the UV convention the meshes were
    /// authored for -- i.e. put paint back at the top.
    ///
    /// Strategy
    /// --------
    /// Detect the vertical extent of the "non-paint band" at the top of
    /// the atlas and rotate the pixel buffer upward by that many rows.
    /// Rotation preserves tile semantics (the wrap seam is just moved),
    /// so body panels sampling near <c>(0, 0)</c> now see paint, and
    /// panels that previously sampled near <c>(0, 0.25)</c> see what
    /// lived at <c>(0, 0.5)</c> (also usually paint, or a short walk
    /// to the carbon band).
    ///
    /// Detection rule (left-half only, to avoid the cyan accent column
    /// that occupies the right half of F1 22 atlases):
    ///   * For each row, quantize pixels to a coarse 64-bucket RGB cube
    ///     and find the dominant bucket.
    ///   * A row is considered "saturated paint" when its dominant colour
    ///     has sufficient chroma (NOT greyscale/white/black) AND
    ///     mid-brightness (NOT near-black or near-white) AND the dominant
    ///     bucket occupies >= 85% of the sampled pixels.
    ///   * Walk from row 0 downward. The first saturated-paint row IS
    ///     the top of the paint band; the rotation offset is that row
    ///     index.
    ///   * If no saturated-paint row is found within the first half of
    ///     the atlas, return 0 (no rotation) -- the atlas is likely
    ///     silver/grey (Mercedes-style) or the detector can't parse it,
    ///     and doing nothing is safer than guessing.
    ///
    /// Safety
    /// ------
    /// F1 2021 atlases: row 0 is already dominantly saturated paint, so
    /// the detector returns offset = 0 and no rotation is applied -- the
    /// pixel buffer is returned byte-identical to the input. F1 2021
    /// behaviour is preserved exactly.
    ///
    /// Only applied to textures the caller opts in by calling
    /// <see cref="MaybeReorientPaintAtlas"/> -- the paint-atlas path
    /// in <c>EgoTexturePipeline.TryGetGenericSidecarBrush</c>. Every
    /// other texture category (decals, driver atlases, generics, detail,
    /// carbon, wheel, etc.) bypasses this code entirely.
    /// </summary>
    internal static class PaintAtlasReorienter
    {
        /// <summary>
        /// Minimum RGB chroma (max - min across channels) for a pixel to
        /// be considered "paint-like". Excludes greyscale (badge text,
        /// silver liveries) and near-neutral dark/light (black carbon,
        /// white space). 80 is chosen to reject the F1 22 badge's dark
        /// blue (chroma 64) while accepting all observed real livery
        /// colours (papaya orange chroma ~224, Ferrari red ~190,
        /// Williams blue ~170, Aston green ~100, Mercedes teal ~120).
        /// </summary>
        private const int MinPaintChroma = 80;

        /// <summary>
        /// Minimum brightness for a paint-like pixel (sum of channels / 3).
        /// Rejects the black carbon band at the bottom of F1 22 atlases.
        /// </summary>
        private const int MinPaintBrightness = 50;

        /// <summary>
        /// Maximum brightness for a paint-like pixel. Rejects the white
        /// text in the badge area.
        /// </summary>
        private const int MaxPaintBrightness = 210;

        /// <summary>
        /// Minimum dominant-bucket fraction for a row to be classified
        /// as "paint". Text and badge rows have low dominance (many
        /// distinct colours from letter edges) and fail this; uniform
        /// paint colour rows have fractions very near 1.0.
        /// </summary>
        private const double MinPaintDominance = 0.85;

        /// <summary>
        /// Main entry point. Given a decoded BGRA32 buffer, detect any
        /// non-paint band at the top and, if found, return a new buffer
        /// rotated by that offset. If no band is detected, returns the
        /// input buffer unchanged (no copy). Never throws; any internal
        /// failure returns the input untouched.
        /// </summary>
        /// <param name="bgra">BGRA32 pixel buffer, size = width*height*4.</param>
        /// <param name="width">Atlas width in pixels.</param>
        /// <param name="height">Atlas height in pixels.</param>
        /// <param name="log">Diagnostic sink; may be null.</param>
        /// <param name="textureLabel">Short texture name for logs (e.g. "mclaren_paint_d.tif").</param>
        /// <returns>
        /// Possibly-rotated BGRA buffer AND the detected offset in rows
        /// (0 when no rotation was applied).
        /// </returns>
        public static (byte[] Bgra, int OffsetRows) MaybeReorientPaintAtlas(
            byte[] bgra, int width, int height,
            Action<string> log, string textureLabel)
        {
            if (bgra == null || width <= 0 || height <= 0)
                return (bgra, 0);
            if (bgra.Length != width * height * 4)
                return (bgra, 0);

            int offset;
            try
            {
                offset = DetectPaintTopRow(bgra, width, height);
            }
            catch (Exception ex)
            {
                // Detector is best-effort; on any internal error leave
                // the atlas alone rather than produce wrong output.
                log?.Invoke($"  paint-atlas reorient: detector threw for {textureLabel}: " +
                            $"{ex.Message} -- leaving atlas unchanged");
                return (bgra, 0);
            }

            if (offset <= 0)
            {
                // Either row 0 already has paint (F1 2021 case -- silent
                // no-op) or no paint row was found at all (unusual livery;
                // stay conservative and don't rotate).
                return (bgra, 0);
            }

            log?.Invoke($"  paint-atlas reorient: {textureLabel} has non-paint band " +
                        $"{offset}px tall at top (v=[0, {(double)offset/height:0.000})); " +
                        $"rotating atlas upward by {offset} rows so paint is at v=0");

            return (RotateVerticallyUpward(bgra, width, height, offset), offset);
        }

        /// <summary>
        /// Walk from the top of the atlas and return the first row index
        /// whose left-half pixels are dominated (≥85%) by a single
        /// chromatic, mid-brightness colour. Returns 0 if the top row
        /// already satisfies the test (common for F1 2021 atlases);
        /// returns -1 if no such row is found in the top half of the
        /// atlas (caller treats -1 the same as 0 -- no rotation).
        /// </summary>
        private static int DetectPaintTopRow(byte[] bgra, int width, int height)
        {
            int halfWidth = width / 2;   // scan LEFT half only
            int searchHeight = height / 2; // paint band must start in the top half
            var counts = new int[64];    // 4x4x4 colour buckets

            for (int y = 0; y < searchHeight; y++)
            {
                Array.Clear(counts, 0, counts.Length);

                int rowBase = y * width * 4;
                for (int x = 0; x < halfWidth; x++)
                {
                    int px = rowBase + x * 4;
                    // BGRA32 layout
                    int b = bgra[px];
                    int g = bgra[px + 1];
                    int r = bgra[px + 2];

                    int bucket = ((r >> 6) << 4) | ((g >> 6) << 2) | (b >> 6);
                    counts[bucket]++;
                }

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

                double domFrac = (double)domCount / halfWidth;
                if (domFrac < MinPaintDominance)
                    continue; // noisy row: likely text / badge edges

                // Unpack bucket back to approximate RGB values (centre of bucket)
                int rHi = ((domBucket >> 4) & 0x3) * 64 + 32;
                int gHi = ((domBucket >> 2) & 0x3) * 64 + 32;
                int bHi = (domBucket & 0x3)        * 64 + 32;

                int chroma = Math.Max(rHi, Math.Max(gHi, bHi))
                           - Math.Min(rHi, Math.Min(gHi, bHi));
                int brightness = (rHi + gHi + bHi) / 3;

                if (chroma >= MinPaintChroma
                    && brightness >= MinPaintBrightness
                    && brightness <= MaxPaintBrightness)
                {
                    // First saturated-paint row -- offset is this y.
                    return y;
                }
            }

            // Nothing paint-like found in top half; don't guess.
            return -1;
        }

        /// <summary>
        /// Return a new BGRA buffer where row 0 of the output is row
        /// <paramref name="offsetRows"/> of the input (and everything
        /// above wraps to the bottom). Preserves tile semantics -- the
        /// image is a cylinder around V, and we're spinning it.
        /// </summary>
        private static byte[] RotateVerticallyUpward(
            byte[] bgra, int width, int height, int offsetRows)
        {
            int stride = width * 4;
            var output = new byte[bgra.Length];

            // Top portion: output rows [0, height - offsetRows) come from
            // input rows [offsetRows, height).
            int topOutBytes = (height - offsetRows) * stride;
            Buffer.BlockCopy(bgra, offsetRows * stride, output, 0, topOutBytes);

            // Bottom portion: output rows [height - offsetRows, height)
            // come from input rows [0, offsetRows).
            Buffer.BlockCopy(bgra, 0, output, topOutBytes, offsetRows * stride);

            return output;
        }
    }
}
