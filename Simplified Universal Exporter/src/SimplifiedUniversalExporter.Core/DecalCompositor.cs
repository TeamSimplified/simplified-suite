using System;

namespace SimplifiedUniversalExporter.Core
{
    /// <summary>
    /// CPU-side alpha compositor for driver-number decal atlases.
    ///
    /// ---------------------------------------------------------------------
    /// WHY THIS EXISTS
    /// ---------------------------------------------------------------------
    /// The EGO engine renders driver numbers at runtime by alpha-blending
    /// the driver-N decal atlas (me_logoMap -> mclaren_driver_N.tif, a
    /// ~92%-transparent BC3 image containing only the digit) on top of the
    /// body paint or carbon weave at the number submesh's UV pair 2.
    ///
    /// WPF 3D's DiffuseMaterial cannot reproduce this: it writes to the
    /// depth buffer for ALL rasterised pixels regardless of alpha, so a
    /// transparent-pixel pass punches a visible "hole through the car" at
    /// the number geometry. The safetycell number submesh on a 2021 McLaren
    /// is 1.88 metres long and covers the entire engine cover top, making
    /// the hole impossible to miss.
    ///
    /// The abandoned 3DSimEd plugin side-stepped this by running inside
    /// 3DSimEd's own renderer which supports proper alpha-test / alpha-to-
    /// coverage. We don't have that in WPF, so we do the equivalent on the
    /// CPU at load time: alpha-blend the digit atlas onto a representative
    /// sample of the paint/carbon colour, write the result into a fully
    /// opaque texture, and apply it as a single-pass DiffuseMaterial. No
    /// transparency, no depth hole, no z-fighting.
    ///
    /// ---------------------------------------------------------------------
    /// HOW IT WORKS
    /// ---------------------------------------------------------------------
    /// 1. The MaterialSlotResolver detects a number submesh and returns
    ///    me_logoMap as the BASE texture (the digit atlas) and the EAID of
    ///    me_albedoMap / me_diffuseMap in BackgroundTextureEaid (the
    ///    paint/carbon atlas).
    ///
    /// 2. The TextureCache decodes both to raw BGRA32 via
    ///    GetPixelsForTexture, then calls SamplePaintColour to pluck a
    ///    single representative (R,G,B) from the paint/carbon atlas.
    ///
    /// 3. CompositeNumberOnBackground blends the number atlas over that
    ///    solid background colour pixel-by-pixel, writing a new BGRA buffer
    ///    the same size as the number atlas (typically 64x32 or 128x64).
    ///
    /// 4. The TextureCache wraps the result in a frozen ImageBrush and
    ///    returns it as the base texture. The loader renders the submesh
    ///    normally with UV pair 2 (the number atlas UVs authored in vb=4).
    ///    No overlay pass is added on top.
    ///
    /// ---------------------------------------------------------------------
    /// CAVEATS
    /// ---------------------------------------------------------------------
    /// * The paint colour behind the number is sampled from a single
    ///   representative point (atlas centre). Paint atlases are authored
    ///   with broadly uniform base colour, so this is visually
    ///   indistinguishable from the game's per-texel lookup for every
    ///   car-and-driver pair in the 2021 package that has been tested.
    ///   If a future team ships a number submesh that sits over a paint
    ///   colour discontinuity (e.g. a striped livery), the compositor will
    ///   produce the wrong background colour for ~one submesh and the fix
    ///   would be to sample the paint atlas at the number's actual UV1
    ///   centroid instead of (0.5, 0.5).
    ///
    /// * Semi-transparent pixels in the digit atlas (mostly the anti-
    ///   aliased edge) blend at load time using straight over-operator
    ///   alpha. The game's shader uses the same blend mode, so results
    ///   match.
    /// </summary>
    public static class DecalCompositor
    {
        /// <summary>
        /// Sample a single pixel from a BGRA32 texture using normalised
        /// [0,1] UV coordinates with nearest-neighbour filtering. Out-of-
        /// range UVs are clamped. Returns the pixel as (R, G, B) -- the
        /// alpha channel is ignored because the caller always needs an
        /// opaque background colour.
        ///
        /// Nearest-neighbour is deliberate: the compositor uses this to
        /// grab ONE representative paint-colour per submesh, not to build
        /// a full texture. Bilinear would just blur a single pixel with
        /// three of its neighbours, which has no visual benefit for a
        /// single sample.
        /// </summary>
        public static (byte R, byte G, byte B) SamplePaintColour(
            byte[] bgra, int width, int height, float u, float v)
        {
            if (bgra == null || bgra.Length < 4 || width <= 0 || height <= 0)
                return (128, 128, 128); // mid-grey fallback so output is never invisible

            int x = (int)(u * width);
            int y = (int)(v * height);
            if (x < 0) x = 0; else if (x >= width) x = width - 1;
            if (y < 0) y = 0; else if (y >= height) y = height - 1;

            int off = (y * width + x) * 4;
            if (off + 2 >= bgra.Length) return (128, 128, 128);

            // BGRA32 byte order: B at +0, G at +1, R at +2, A at +3.
            return (bgra[off + 2], bgra[off + 1], bgra[off + 0]);
        }

        /// <summary>
        /// Alpha-blend a number/decal atlas over a solid background colour,
        /// producing a new BGRA32 buffer the same dimensions as the atlas.
        /// Every output pixel is fully opaque (alpha = 255).
        ///
        /// Blend formula per channel:
        ///   out = src * a + bg * (1 - a)
        /// where a = atlasAlpha / 255. This is the standard over-operator
        /// blend the EGO shader uses when it samples me_logoMap over the
        /// paint atlas.
        ///
        /// Performance: single linear scan over width*height pixels, no
        /// allocation beyond the output buffer. For a 64x32 driver atlas
        /// this is ~2048 iterations -- fast enough to run at load time
        /// for every number submesh (typically ~4-6 per car) without
        /// perceptible delay.
        /// </summary>
        public static byte[] CompositeNumberOnBackground(
            byte[] numberBgra, int width, int height,
            byte bgR, byte bgG, byte bgB,
            byte alphaThreshold = 4)
        {
            if (numberBgra == null)
                throw new ArgumentNullException(nameof(numberBgra));

            int expected = width * height * 4;
            if (numberBgra.Length < expected)
                throw new ArgumentException(
                    $"Buffer too small: expected {expected} bytes for {width}x{height}, " +
                    $"got {numberBgra.Length}.", nameof(numberBgra));

            byte[] result = new byte[expected];

            for (int i = 0; i < width * height; i++)
            {
                int off = i * 4;
                byte b = numberBgra[off + 0];
                byte g = numberBgra[off + 1];
                byte r = numberBgra[off + 2];
                byte a = numberBgra[off + 3];

                if (a < alphaThreshold)
                {
                    // Effectively-transparent pixel: fill with background.
                    // Using a small non-zero threshold avoids spending the
                    // blend calculation on pixels that contribute nothing.
                    result[off + 0] = bgB;
                    result[off + 1] = bgG;
                    result[off + 2] = bgR;
                }
                else if (a >= 250)
                {
                    // Effectively-opaque pixel: copy atlas colour directly.
                    // Saves a multiply+add per channel for the bulk of the
                    // digit interior.
                    result[off + 0] = b;
                    result[off + 1] = g;
                    result[off + 2] = r;
                }
                else
                {
                    // Semi-transparent (anti-aliased digit edge): over-blend.
                    //
                    // Fixed-point arithmetic in 8-bit space:
                    //   blended = (src * a + bg * (255 - a) + 127) / 255
                    // Adding 127 before dividing rounds to nearest instead
                    // of truncating, which matters for edge pixels where
                    // an off-by-one shows up as a visible colour fringe.
                    int ia = 255 - a;
                    result[off + 0] = (byte)((b * a + bgB * ia + 127) / 255);
                    result[off + 1] = (byte)((g * a + bgG * ia + 127) / 255);
                    result[off + 2] = (byte)((r * a + bgR * ia + 127) / 255);
                }

                result[off + 3] = 255; // every output pixel fully opaque
            }

            return result;
        }

        /// <summary>
        /// Average the colour of a rectangular region of a BGRA32 texture.
        /// Provided as an optional drop-in replacement for SamplePaintColour
        /// when a single-pixel sample turns out to be noisy (e.g. if the
        /// atlas centre happens to land on a seam or a dark gap between
        /// paint swatches). Not used by the default pipeline but kept here
        /// so it's available if a future team's atlas needs it.
        /// </summary>
        public static (byte R, byte G, byte B) AverageRegion(
            byte[] bgra, int width, int height,
            float u0, float v0, float u1, float v1)
        {
            if (bgra == null || width <= 0 || height <= 0)
                return (128, 128, 128);

            int x0 = Math.Max(0, Math.Min(width - 1, (int)(u0 * width)));
            int y0 = Math.Max(0, Math.Min(height - 1, (int)(v0 * height)));
            int x1 = Math.Max(0, Math.Min(width - 1, (int)(u1 * width)));
            int y1 = Math.Max(0, Math.Min(height - 1, (int)(v1 * height)));
            if (x1 < x0) (x0, x1) = (x1, x0);
            if (y1 < y0) (y0, y1) = (y1, y0);

            long sumR = 0, sumG = 0, sumB = 0, count = 0;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int off = (y * width + x) * 4;
                    if (off + 2 >= bgra.Length) continue;
                    sumB += bgra[off + 0];
                    sumG += bgra[off + 1];
                    sumR += bgra[off + 2];
                    count++;
                }
            }

            if (count == 0) return (128, 128, 128);
            return ((byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));
        }
    }
}
