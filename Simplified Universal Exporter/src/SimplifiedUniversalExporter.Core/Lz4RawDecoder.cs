using System;

namespace SimplifiedUniversalExporter.Core
{
    /// <summary>
    /// Decoder for raw LZ4 block streams -- the stream format used in
    /// Codemasters' .mipmaps sidecar files.
    ///
    /// Each mip level in a .mipmaps file is stored as a standalone raw
    /// LZ4 block (no frame header, no xxhash footer, no length prefix).
    /// The caller tells us how many uncompressed bytes that mip should
    /// produce (computed from the BC3 pyramid geometry -- tail_dim × 2^N);
    /// we decompress that exact amount and return how many source bytes
    /// we consumed. The next mip starts at srcOffset + returnedValue.
    ///
    /// The LZ4 format (per the Yann Collet reference):
    ///   sequence := token, lit_len_ext*, literals, [offset, match_len_ext*]
    ///   token:  hi-nibble = literal length (0-14, or 15 = extended),
    ///           lo-nibble = match length  (0-14, or 15 = extended).
    ///   literal/match extended length: sum 0xff bytes while we keep
    ///           reading 0xff, add the final non-0xff byte (0..254).
    ///   offset: 2 bytes little-endian, distance back into the already-
    ///           written output window.
    ///   final sequence: literals only, no match pair.
    ///
    /// RLE behaviour: when match_len > offset the copy reads bytes it
    /// just wrote in this sequence. Implementing the copy as a tight
    /// byte-by-byte loop preserves that behaviour; Buffer.BlockCopy
    /// would not.
    ///
    /// This was reverse-engineered against mclaren_driver_1_tif.mipmaps
    /// (25563 bytes, 4 mips 1024x512 -> 128x64 BC3) and validated to
    /// consume the full file with zero remainder.
    /// </summary>
    internal static class Lz4RawDecoder
    {
        /// <summary>
        /// Decode one LZ4 raw block into <paramref name="dst"/>.
        /// Stops cleanly when dst is full (dstPos == dst.Length) or when
        /// the source is exhausted. Returns the number of source bytes
        /// consumed, which is how far to advance into a stream of
        /// concatenated LZ4 blocks.
        /// </summary>
        /// <param name="src">Source buffer containing one or more LZ4 blocks.</param>
        /// <param name="srcOffset">Start offset in src.</param>
        /// <param name="dst">
        /// Destination buffer, sized to exactly the expected uncompressed
        /// size for this mip. Acts both as the write target AND as the
        /// match-history window (LZ4 is self-referential).
        /// </param>
        /// <returns>Number of source bytes consumed starting from srcOffset.</returns>
        public static int Decode(byte[] src, int srcOffset, byte[] dst)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (dst == null) throw new ArgumentNullException(nameof(dst));

            int i = srcOffset;
            int nSrc = src.Length;
            int dstPos = 0;
            int expected = dst.Length;

            while (i < nSrc && dstPos < expected)
            {
                // Sequence token: hi = literal length, lo = match length.
                byte token = src[i++];
                int litLen = token >> 4;
                int matchLen = token & 0x0F;

                // Extended literal length (0xFF bytes continue, final byte ends).
                if (litLen == 15)
                {
                    while (i < nSrc)
                    {
                        byte b = src[i++];
                        litLen += b;
                        if (b != 0xFF) break;
                    }
                }

                // Copy literals. Clamp by what we still have room for in dst;
                // advance i by the full litLen either way so the stream stays
                // aligned even if we stopped early.
                int take = Math.Min(litLen, expected - dstPos);
                if (take > 0 && i + take <= nSrc)
                {
                    Buffer.BlockCopy(src, i, dst, dstPos, take);
                    dstPos += take;
                }
                i += litLen;

                // Done if dst is full or we've reached the "final sequence:
                // literals only" end marker (no match pair follows).
                if (dstPos >= expected) break;
                if (i + 2 > nSrc) break;

                // Match offset (u16 LE). Must be 1..dstPos.
                int off = src[i] | (src[i + 1] << 8);
                i += 2;

                // Extended match length (same rule as literal length).
                if (matchLen == 15)
                {
                    while (i < nSrc)
                    {
                        byte b = src[i++];
                        matchLen += b;
                        if (b != 0xFF) break;
                    }
                }
                matchLen += 4; // LZ4 minimum match length is 4.

                // RLE-safe byte-by-byte copy from already-written output.
                // DO NOT replace with Buffer.BlockCopy: if off < matchLen
                // (run-length encoding of repeating bytes), the source
                // region overlaps the destination region we're writing.
                int start = dstPos - off;
                if (start < 0)
                {
                    // Malformed stream -- bail cleanly so the caller can
                    // fall back to the tail-mip upscale path.
                    return i - srcOffset;
                }
                int copyEnd = Math.Min(dstPos + matchLen, expected);
                int k = 0;
                while (dstPos < copyEnd)
                {
                    dst[dstPos++] = dst[start + k];
                    k++;
                }
            }

            return i - srcOffset;
        }
    }
}
