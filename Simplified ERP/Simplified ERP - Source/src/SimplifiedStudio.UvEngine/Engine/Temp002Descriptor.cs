using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace F1modding3dtoolV1
{
    /// <summary>
    /// Authoritative reader for Codemasters <c>temp002.image</c> sidecar
    /// descriptors. Returns a fully-populated record telling the caller
    /// EXACTLY where each mip sits in the companion <c>.mipmaps</c> file,
    /// its compressed and uncompressed byte counts, and which compression
    /// scheme the mip uses -- no geometry inference needed.
    ///
    /// Why this exists
    /// ---------------
    /// Previous iterations of MipmapsSidecarLoader read only the header
    /// fields (<c>pathStringLen</c>, <c>path</c>, <c>mipCount</c>) and
    /// treated the per-mip descriptor block as "partially understood,
    /// skipped". That worked for F1 2021 driver atlases because the mip
    /// geometry is deterministic from the tail dimensions, and because
    /// all F1 2021 per-mip compression bytes happened to be <c>0x03</c>
    /// (LZ4 raw block). It won't scale: F1 22 added temp002.image for
    /// every textured asset (paint, detail, decals, etc.) and F1 23
    /// added two extra "unknown" bytes to the descriptor. Parsing the
    /// format for real once, here, removes the guess-and-check cycle.
    ///
    /// Layout (from the reverse-engineered 010 Editor binary template)
    /// ----------------------------------------------------------------
    /// <code>
    ///   u8        pathStringLen
    ///   u8[N]     path             (N == pathStringLen, ASCII)
    ///   if F1 23: u8 unknown4      (extra byte, meaning TBD)
    ///   u32       mipCount
    ///   mipCount * {
    ///       if F1 23: u8 unknown   (extra byte, meaning TBD)
    ///       u8        compression  (0x00 = raw passthrough; 0x03 = LZ4 raw)
    ///       u64       offset       (byte offset inside the .mipmaps file)
    ///       u64       packedSize   (bytes occupied in the .mipmaps file)
    ///       u64       size         (uncompressed byte count, exact BC size)
    ///   }
    ///   if GRID Legends: u8 unknown3
    ///   if F1 2017:      float streamingPriority
    ///                    float unknown2
    /// </code>
    ///
    /// The F1 2017 pair of trailing floats is present in every file from
    /// F1 2017 through F1 22 that we've observed (streaming priority
    /// first, then a 1.0 constant). GRID Legends adds a single trailer
    /// byte before those floats.
    ///
    /// F1 22 uses the base layout. F1 23 adds the two <c>unknown</c>
    /// bytes. Detection is done by trying the F1 22 layout first; if the
    /// declared <c>mipCount</c> produces a self-consistent total length
    /// that matches the actual file size, that's the layout. Otherwise
    /// we try the F1 23 layout with the same self-consistency test.
    /// If neither matches, the descriptor is rejected and the caller
    /// falls back to its existing file-size-inference path so no
    /// regressions vs the prior behaviour.
    /// </summary>
    internal static class Temp002Descriptor
    {
        /// <summary>
        /// Known compression codes in the per-mip <c>compression</c> byte.
        /// </summary>
        public static class CompressionCode
        {
            /// <summary>Passthrough: <c>packedSize == size</c>, the mip bytes are raw BC blocks.</summary>
            public const byte Raw = 0x00;

            /// <summary>LZ4 raw block: each mip is one LZ4 block, decoded to <c>size</c> bytes.</summary>
            public const byte Lz4 = 0x03;
        }

        /// <summary>
        /// Identifies which layout variant was detected when parsing a
        /// given temp002.image. Exposed on the result so callers (and
        /// the log) can see at a glance which branch fired.
        /// </summary>
        public enum Layout
        {
            /// <summary>F1 2017 through F1 22 -- base layout, no extras.</summary>
            Base,
            /// <summary>F1 23 -- adds one byte after path and one byte per mip descriptor.</summary>
            F1_23,
        }

        public sealed class MipEntry
        {
            /// <summary>Compression code; see <see cref="CompressionCode"/>.</summary>
            public byte Compression;
            /// <summary>Byte offset inside the .mipmaps file where this mip's bytes begin.</summary>
            public long Offset;
            /// <summary>Bytes occupied in the .mipmaps file (compressed size).</summary>
            public long PackedSize;
            /// <summary>Exact uncompressed byte count (BC block bytes).</summary>
            public long UncompressedSize;
        }

        public sealed class Result
        {
            /// <summary>
            /// The in-descriptor path string, e.g.
            /// <c>"f1_2022_vehicle_package/teams/mclaren/textures/mclaren_paint_d.tif.mipmaps"</c>.
            /// </summary>
            public string Path;

            /// <summary>Mip count declared in the descriptor header.</summary>
            public int MipCount;

            /// <summary>Per-mip descriptors; ordered largest-first.</summary>
            public List<MipEntry> Mips;

            /// <summary>Which layout variant matched on parse.</summary>
            public Layout DetectedLayout;

            /// <summary>Total file size of the descriptor (for diagnostics).</summary>
            public long DescriptorFileSize;
        }

        /// <summary>
        /// Try to read and parse a <c>temp002.image</c> file. Returns
        /// <c>null</c> on any error (file missing, short read, corrupt
        /// layout, self-consistency check fails) so the caller can fall
        /// back to an inference-based path unchanged. The log sink is
        /// called with a single diagnostic line on unexpected failure.
        /// </summary>
        public static Result TryRead(string temp002Path, Action<string> log = null)
        {
            log ??= _ => { };

            if (string.IsNullOrEmpty(temp002Path) || !File.Exists(temp002Path))
                return null;

            byte[] data;
            try { data = File.ReadAllBytes(temp002Path); }
            catch (Exception ex)
            {
                log($"  temp002: read failed for {temp002Path}: {ex.Message}");
                return null;
            }

            // Try F1 22-and-earlier base layout first. F1 22 is the most
            // common case today; try it first so the typical path is
            // zero-retry. F1 23 try happens only if Base rejects.
            if (TryParse(data, Layout.Base, out var r))
            {
                r.DescriptorFileSize = data.Length;
                return r;
            }

            if (TryParse(data, Layout.F1_23, out r))
            {
                r.DescriptorFileSize = data.Length;
                return r;
            }

            log($"  temp002: {Path.GetFileName(temp002Path)} ({data.Length} bytes) " +
                $"didn't match Base or F1_23 layout -- skipping");
            return null;
        }

        /// <summary>
        /// Parse <paramref name="data"/> under the given layout. Returns
        /// <c>true</c> only when EVERY byte is consumed (the self-
        /// consistency check): the computed end offset must equal
        /// <c>data.Length</c>. A layout that "almost fits" but leaves
        /// slack or overflows is rejected -- both are signs the layout
        /// guess was wrong.
        /// </summary>
        private static bool TryParse(byte[] data, Layout layout, out Result result)
        {
            result = null;
            if (data == null || data.Length < 6) return false;

            int pos = 0;

            // pathStringLen + path
            int strLen = data[pos]; pos += 1;
            if (pos + strLen > data.Length) return false;
            string pathStr;
            try { pathStr = Encoding.ASCII.GetString(data, pos, strLen); }
            catch { return false; }
            pos += strLen;

            // F1 23 adds one byte here
            if (layout == Layout.F1_23)
            {
                if (pos + 1 > data.Length) return false;
                pos += 1; // unknown4, discarded
            }

            // mipCount
            if (pos + 4 > data.Length) return false;
            int mipCount = BitConverter.ToInt32(data, pos);
            pos += 4;

            // Sanity: mipCount should be small (1..16). Anything else is
            // either a corrupt file or a layout mismatch; reject.
            if (mipCount <= 0 || mipCount > 16) return false;

            // Per-mip descriptors
            int perMipBytes = (layout == Layout.F1_23 ? 1 : 0) + 1 + 8 + 8 + 8;
            if (pos + mipCount * perMipBytes > data.Length) return false;

            var mips = new List<MipEntry>(mipCount);
            for (int i = 0; i < mipCount; i++)
            {
                if (layout == Layout.F1_23)
                {
                    pos += 1; // per-mip unknown, discarded
                }
                byte compression = data[pos]; pos += 1;
                long offset       = BitConverter.ToInt64(data, pos); pos += 8;
                long packedSize   = BitConverter.ToInt64(data, pos); pos += 8;
                long uncompSize   = BitConverter.ToInt64(data, pos); pos += 8;

                // Cheap sanity checks on each mip to catch layout drift.
                if (offset < 0 || packedSize < 0 || uncompSize <= 0) return false;
                if (packedSize > uncompSize + 1024 && compression == CompressionCode.Raw)
                {
                    // If the descriptor says "raw" but packed exceeds
                    // uncompressed by more than alignment slack, the
                    // layout guess is almost certainly wrong.
                    return false;
                }

                mips.Add(new MipEntry
                {
                    Compression = compression,
                    Offset = offset,
                    PackedSize = packedSize,
                    UncompressedSize = uncompSize,
                });
            }

            // Trailing bytes (unknown3 for GRID Legends + floats for
            // F1 2017+). We don't need these values but we DO use their
            // consumed-byte count as the self-consistency check: a correct
            // layout guess leaves exactly 8 bytes of trailing floats
            // (F1 2017 layout) or 9 bytes (GRID Legends = 1 + 8). Any
            // other remainder means the layout guess was wrong.
            int remaining = data.Length - pos;
            if (remaining != 8 && remaining != 9) return false;

            // Floats at tail are not stored -- they're streaming hints
            // the game uses, not something the preview needs. Consuming
            // the count is what matters for layout validation.

            result = new Result
            {
                Path = pathStr,
                MipCount = mipCount,
                Mips = mips,
                DetectedLayout = layout,
            };
            return true;
        }
    }
}
