using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SimplifiedUniversalExporter.Core
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
        /// <c>null</c> on any error (file missing, short read, a layout the
        /// library refuses, sizes that make no sense) so the caller can fall
        /// back to its inference-based path unchanged. The log sink is called
        /// with a single diagnostic line on unexpected failure.
        ///
        /// The format itself is read by EgoEngineLibrary's ErpGfxSurfaceRes2,
        /// the same reader that embeds mips when a mod is built; this only adds
        /// the checks the exporter has always made before trusting a
        /// descriptor.
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

            var result = TryParse(data);
            if (result is null)
            {
                log($"  temp002: {Path.GetFileName(temp002Path)} ({data.Length} bytes) " +
                    $"didn't match Base or F1_23 layout -- skipping");
                return null;
            }

            result.DescriptorFileSize = data.Length;
            return result;
        }

        private static Result TryParse(byte[] data)
        {
            if (data == null || data.Length < 6) return null;

            EgoEngineLibrary.Archive.Erp.Data.ErpGfxSurfaceRes2 descriptor;
            try { descriptor = EgoEngineLibrary.Archive.Erp.Data.ErpGfxSurfaceRes2.FromBytes(data); }
            catch { return null; }

            // Sanity: mipCount should be small (1..16). Anything else is a
            // corrupt file or a layout the reader got wrong; reject.
            if (descriptor.Mips.Count <= 0 || descriptor.Mips.Count > 16) return null;

            var mips = new List<MipEntry>(descriptor.Mips.Count);
            foreach (var m in descriptor.Mips)
            {
                long offset = (long)m.Offset, packedSize = (long)m.PackedSize, uncompSize = (long)m.Size;
                byte compression = (byte)m.Compression;

                // Cheap sanity checks on each mip to catch layout drift.
                if (offset < 0 || packedSize < 0 || uncompSize <= 0) return null;
                if (packedSize > uncompSize + 1024 && compression == CompressionCode.Raw)
                {
                    // If the descriptor says "raw" but packed exceeds
                    // uncompressed by more than alignment slack, the
                    // layout was almost certainly read wrong.
                    return null;
                }

                mips.Add(new MipEntry
                {
                    Compression = compression,
                    Offset = offset,
                    PackedSize = packedSize,
                    UncompressedSize = uncompSize,
                });
            }

            return new Result
            {
                Path = descriptor.MipMapFileName,
                MipCount = mips.Count,
                Mips = mips,
                DetectedLayout = descriptor.HasF123Layout ? Layout.F1_23 : Layout.Base,
            };
        }
    }
}
