using System;
using System.Collections.Generic;
using System.IO;

namespace SimplifiedUniversalExporter.Core
{
    /// <summary>
    /// Loader for the SHARED-GENERIC-TEXTURE flavour of the .mipmaps
    /// sidecar format (companion to <see cref="MipmapsSidecarLoader"/>,
    /// which handles driver-atlas sidecars).
    ///
    /// Background: why a second loader exists
    /// ---------------------------------------
    /// F1 2021 ships two different .mipmaps flavours that the previews
    /// tool needs to decode, and they differ in three independent ways:
    ///
    ///                            driver atlas          shared generic
    ///                            (MipmapsSidecarLoader) (this class)
    ///     Authority descriptor   temp002.image          ABSENT
    ///                            emitted next to the    (shared-generics
    ///                            temp000/001 pair       ERP doesn't include
    ///                            during ERP extract     the sidecar block)
    ///
    ///     Tail compression       BC3 (DXT5)             BC1 (DXT1)
    ///                            format code 57         format code 54
    ///
    ///     Sidecar payload        Concatenated raw       Concatenated RAW
    ///                            LZ4 blocks, one per    BC data, one per
    ///                            mip level, largest-    mip level, largest-
    ///                            first. Tiny (~35 KB    first. Large
    ///                            for a 1024x512x4       (2.79 MB for a
    ///                            chain) because driver  2048x2048x4 chain)
    ///                            atlases are mostly     because there is
    ///                            transparent pixels.    no compression.
    ///
    /// A concrete measurement on the McLaren F1 2021 pack:
    /// generic_main_d_tif.mipmaps is 2,785,280 bytes, which is EXACTLY
    /// the sum of four uncompressed BC1 mip sizes at 2048x2048 + 1024x1024
    /// + 512x512 + 256x256 (2097152 + 524288 + 131072 + 32768). Spot-
    /// checking bytes at each level-boundary offset shows valid BC1
    /// blocks (two RGB565 color words plus indices), not LZ4 header
    /// bytes -- confirming the raw-concat layout.
    ///
    /// How this loader works
    /// ---------------------
    /// 1. Read the tail dimensions + format from temp000.image in the
    ///    extracted cache. We need this for two reasons: the level
    ///    above the tail is (tailW*2, tailH*2), and the BC block size
    ///    depends on the format (8 bytes/block for BC1, 16 for BC2/BC3).
    ///
    /// 2. Locate the sibling .mipmaps file on the real on-disk install.
    ///    The search pattern mirrors MipmapsSidecarLoader's approach:
    ///    try the EAID-mirrored path under the vehicle_package first,
    ///    then recursively scan the package root, then recursively scan
    ///    the game root. Results are cached per-filename.
    ///
    /// 3. Infer the sidecar mip count N by solving
    ///      fileSize == sum_{i=1..N} blockBytes(tailW &lt;&lt; i, tailH &lt;&lt; i)
    ///    in integer arithmetic. F1 2021's generic sidecars all produce
    ///    an exact match at one specific N. A non-match aborts this
    ///    loader (the file is either LZ4-compressed after all, or some
    ///    format variant we haven't catalogued); the caller falls back
    ///    to its ERP-tail path unchanged.
    ///
    /// 4. Slice the first (largest, top-mip) block out of the file and
    ///    hand it to the appropriate BlockCompressionDecoder to yield
    ///    BGRA32 pixels.
    ///
    /// Returns null on any failure, silently -- matching
    /// <see cref="MipmapsSidecarLoader.TryLoadTopMip"/>'s contract so
    /// the caller's "null -> fall back to ERP tail" logic works without
    /// special-casing either loader.
    /// </summary>
    internal static class GenericMipmapsSidecarLoader
    {
        /// <summary>
        /// Try to load the top-mip BGRA32 for a shared/generic texture
        /// from its on-disk .mipmaps sidecar. See class-level comment for
        /// the full design. Null on any failure (no install, no file,
        /// size mismatch, unsupported format, decode failure).
        /// </summary>
        /// <param name="textureEaid">
        /// The texture EAID, e.g.
        /// "eaid://f1_2021_vehicle_package/shared/generics/textures/generic_main_d.tif".
        /// </param>
        /// <param name="extractedPackageRoot">
        /// The tool's extracted ERP cache root -- used to read the tail
        /// dimensions + format from temp000.image. Must contain the
        /// texture's temp000 descriptor (the caller already displays the
        /// texture via that descriptor, so this is the same value that
        /// MipmapsSidecarLoader uses).
        /// </param>
        /// <summary>BC5 decoding mode for callers requesting a BC5 texture's
        /// top-mip. The block-compressed payload is identical between modes;
        /// only the interpretation of the two channels differs.</summary>
        public enum BC5DecodeMode
        {
            /// <summary>R channel as grayscale (mask textures, surface PBR
            /// maps where the dominant signal is in R). Default behaviour.</summary>
            GrayscaleR = 0,
            /// <summary>Tangent-space normal map (R = X, G = Y, B reconstructed
            /// from sqrt(1-X²-Y²)). Use for "_n.tif"-style normal maps.</summary>
            NormalMap = 1,
        }

        public static DecodedTexture TryLoadTopMip(
            string textureEaid,
            string extractedPackageRoot,
            Action<string> log,
            BC5DecodeMode bc5Mode = BC5DecodeMode.GrayscaleR)
        {
            log = log ?? (_ => { });

            var located = F1PackageLocator.Last;
            if (located == null || string.IsNullOrEmpty(located.VehiclePackageRoot))
            {
                // Stray ERP loaded without an install context. Silent.
                return null;
            }

            // ----- 1. Parse EAID for relative dir, stem, and extension.
            string rel = EaidPathResolver.StripEaidPrefix(textureEaid);
            if (rel == null) return null;

            int lastSlash = Math.Max(rel.LastIndexOf('/'), rel.LastIndexOf('\\'));
            string relDir = (lastSlash >= 0) ? rel.Substring(0, lastSlash) : "";
            string fileLeaf = (lastSlash >= 0) ? rel.Substring(lastSlash + 1) : rel;
            int dot = fileLeaf.LastIndexOf('.');
            string stem = (dot >= 0) ? fileLeaf.Substring(0, dot) : fileLeaf;
            string ext = (dot >= 0) ? fileLeaf.Substring(dot + 1) : "tif";

            // ----- 2. Read tail descriptor (temp000.image) for dims + format.
            var (_, metaPath, _) = EaidPathResolver.ResolveTexturePaths(
                textureEaid, extractedPackageRoot);
            if (metaPath == null || !File.Exists(metaPath))
            {
                // No descriptor means we can't compute mip geometry.
                // Silent: this is "not a candidate for sidecar".
                return null;
            }

            ImageDescriptor tail;
            try { tail = ImageDescriptor.FromFile(metaPath); }
            catch (Exception ex)
            {
                log($"  generic-mipmaps: descriptor read failed ({metaPath}): {ex.Message}");
                return null;
            }

            if (tail.Width <= 0 || tail.Height <= 0)
            {
                log($"  generic-mipmaps: implausible tail dims ({tail.Width}x{tail.Height}), skipping");
                return null;
            }

            // BC1/BC2/BC3 are the standard diffuse/sponsor/decal atlas
            // formats. BC5 added 2026 for tangent-space normal maps so
            // FBX exports can ship the hi-res streamed sidecar instead
            // of the low-res ERP tail. BC7 added 2026 too, for character
            // surface maps (_s.tif on driver helmets like Hulkenberg,
            // format code 69) -- 8-bit RGBA per block with the most
            // advanced BC compression mode in the standard set.
            //
            // BC5 + BC7 .mipmaps files share the same descriptor-driven
            // layout as BC1/2/3; only the per-block decode differs (see
            // DecodeByFormat below).
            int tailBlockBytes;
            string tailFormatName;
            if (tail.IsBC1) { tailBlockBytes = 8; tailFormatName = "BC1"; }
            else if (tail.IsBC2) { tailBlockBytes = 16; tailFormatName = "BC2"; }
            else if (tail.IsBC3) { tailBlockBytes = 16; tailFormatName = "BC3"; }
            else if (tail.IsBC5) { tailBlockBytes = 16; tailFormatName = "BC5"; }
            else if (tail.IsBC7) { tailBlockBytes = 16; tailFormatName = "BC7"; }
            else
            {
                log($"  generic-mipmaps: tail format {tail.FormatCode} not BC1/BC2/BC3/BC5/BC7, skipping");
                return null;
            }

            // ----- 3. Locate the .mipmaps file on disk.
            //
            // AUTHORITATIVE PATH (matches EgoErpArchiver, by design):
            //   - Every F1 22+ texture ships a temp002.image descriptor
            //     whose first field is the GAME-INSTALL-RELATIVE path
            //     to the .mipmaps file (e.g.
            //     "2025_asset_groups/f1_2025_vehicle_package/teams/redbull/
            //     textures/redbull_decal_da.tif.mipmaps"). Codemasters'
            //     own tools write this string at build time; it's
            //     already year-disambiguated and points at exactly the
            //     right file. EgoErpArchiver just does
            //     Path.Combine(F1Directory, descriptor.MipMapFileName)
            //     + File.Exists -- no scanning, no heuristics.
            //
            //   - We adopt the same algorithm here: read temp002,
            //     append its Path to the game-install root, check
            //     for the file. Eliminates an entire class of
            //     cross-year contamination bugs (2025 ERP picking
            //     up 2026 mipmaps via recursive scan).
            //
            // FALLBACK (older formats / pre-F1 22): only when temp002
            // is unreadable do we drop to the legacy EAID-guess scan,
            // since some F1 2021-era resources ship no descriptor.
            string wantedName = $"{stem}_{ext}.mipmaps";      // on-disk rename form (.tif -> _tif)
            string wantedNameLiteral = $"{stem}.{ext}.mipmaps"; // in-descriptor form
            string eaidPackageName = ExtractEaidPackageName(textureEaid);

            string mipPath = TryLocateViaTemp002Descriptor(
                textureEaid, extractedPackageRoot, located, log);

            if (mipPath == null)
            {
                // Legacy path: synthesise from EAID + game folder.
                mipPath = TryLocateMipmapsFile(
                    located, relDir, wantedName, wantedNameLiteral, eaidPackageName, log);
            }
            if (mipPath == null)
            {
                // Already logged by the locator.
                return null;
            }

            // ----- 4. Read the file.
            byte[] fileBytes;
            try { fileBytes = File.ReadAllBytes(mipPath); }
            catch (Exception ex)
            {
                log($"  generic-mipmaps: read failed {mipPath}: {ex.Message}");
                return null;
            }
            if (fileBytes.Length < tailBlockBytes)
            {
                log($"  generic-mipmaps: file too small ({fileBytes.Length} bytes)");
                return null;
            }

            // ----- 5. Decode dispatch.
            //
            // Three .mipmaps layouts are handled. Tried in order; the
            // first one that cleanly produces a top-mip wins.
            //
            //   (c) Descriptor-driven (F1 22+, and ALSO F1 2021 when
            //       temp002.image is present for the texture -- which is
            //       true for all F1 2021 driver atlases, and whatever
            //       else ships a descriptor in earlier ERPs). Reads
            //       <texture>!!!temp002.image from the extracted cache,
            //       parses the BT-template layout via Temp002Descriptor
            //       to obtain per-mip offset / packedSize / size /
            //       compression, and slices the top mip out of the
            //       .mipmaps file using the descriptor's exact offsets.
            //
            //       This is the authoritative path. It mimics what the
            //       game does internally and is the most robust to future
            //       per-mip compression changes (the descriptor tells us
            //       the compression code per mip; if F1 24 adds zstd
            //       mips, we only need to add a decoder branch here).
            //
            //       First try, so the fallbacks stay stable.
            //
            //   (a) Raw block-compressed concat (F1 2021 shared generics,
            //       which do NOT ship temp002.image). File size equals
            //       sum_{i=1..N} BlockBytes(tail<<i). Pure integer
            //       arithmetic, no decompression.
            //
            //   (b) LZ4-per-mip concat inferred from file size (fallback
            //       for F1 22+ textures whose descriptor read failed for
            //       any reason). Each mip is one LZ4-raw block;
            //       concatenated with no framing.
            //
            // The dispatcher is FORMAT-DRIVEN, not year-driven: as new
            // game years ship, whichever branch matches their file
            // layout wins regardless of which folder the file sits in.

            DecodedTexture result = TryDecodeViaTemp002(
                fileBytes, tail, tailBlockBytes, tailFormatName,
                mipPath, textureEaid, extractedPackageRoot, log, bc5Mode);
            if (result != null)
                return result;

            result = TryDecodeRawConcat(
                fileBytes, tail, tailBlockBytes, tailFormatName, mipPath, log, bc5Mode);
            if (result != null)
                return result;

            result = TryDecodeLz4PerMipConcat(
                fileBytes, tail, tailBlockBytes, tailFormatName, mipPath, log, bc5Mode);
            if (result != null)
                return result;

            // No layout matched. Caller falls back to ERP tail.
            log($"  generic-mipmaps: file size {fileBytes.Length} doesn't match any " +
                $"known layout for tail {tail.Width}x{tail.Height} {tailFormatName}; skipping " +
                $"(tried temp002 descriptor, raw-concat, LZ4-per-mip-concat)");
            return null;
        }

        // ---------------------------------------------------------------------
        // DECODERS (dispatched from step 5 above; each returns null on mismatch)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Attempt (c) from the dispatch: descriptor-driven decode using
        /// the <c>temp002.image</c> sidecar.
        ///
        /// Locates the texture's <c>temp002.image</c> in the extracted
        /// cache (same folder as <c>temp000.image</c> / <c>temp001.image</c>)
        /// and parses it with <see cref="Temp002Descriptor.TryRead"/>.
        /// The parsed descriptor is authoritative -- it tells us per-mip
        /// offset inside the .mipmaps file, packedSize, uncompressedSize,
        /// and compression code. We use mip[0] (largest) by convention.
        ///
        /// Validates against the tail descriptor: the declared
        /// uncompressedSize of mip[0] must match the BC-block bytes for
        /// the expected top-mip geometry (tail.W &lt;&lt; mipCount,
        /// tail.H &lt;&lt; mipCount). Mismatch means something's off
        /// (tail format wrong, tail dims wrong, descriptor layout
        /// misidentified) so we reject and fall back.
        ///
        /// Compression branches:
        ///   * 0x00 (Raw): slice <c>packedSize</c> bytes from the file at
        ///     <c>offset</c> and pass straight to the BC decoder.
        ///   * 0x03 (LZ4 raw block): slice <c>packedSize</c> bytes from
        ///     the file, decode one LZ4 raw block into a buffer of
        ///     exactly <c>uncompressedSize</c> bytes, pass to BC.
        ///   * Anything else: reject (we don't know how to decompress).
        ///     Future codes (e.g. zstd mips in F1 24) would be added here.
        /// </summary>
        private static DecodedTexture TryDecodeViaTemp002(
            byte[] fileBytes, ImageDescriptor tail, int tailBlockBytes,
            string tailFormatName, string mipPath,
            string textureEaid, string extractedPackageRoot,
            Action<string> log, BC5DecodeMode bc5Mode)
        {
            // Build the temp002.image path (same dir as temp000/temp001).
            string rel = EaidPathResolver.StripEaidPrefix(textureEaid);
            if (rel == null) return null;
            int lastSlash = Math.Max(rel.LastIndexOf('/'), rel.LastIndexOf('\\'));
            string dir = (lastSlash >= 0) ? rel.Substring(0, lastSlash) : "";
            string fileLeaf = (lastSlash >= 0) ? rel.Substring(lastSlash + 1) : rel;
            string temp002Path = Path.Combine(
                extractedPackageRoot,
                dir.Replace('/', Path.DirectorySeparatorChar),
                $"{fileLeaf}!!!temp002.image");

            if (!File.Exists(temp002Path))
                return null; // silent: no descriptor, try inference paths

            var desc = Temp002Descriptor.TryRead(temp002Path, log);
            if (desc == null || desc.Mips == null || desc.Mips.Count == 0)
                return null;

            // Geometry check: the top mip's uncompressed size must equal
            // the expected BC byte count for (tail.W << N, tail.H << N).
            int N = desc.MipCount;
            int topW = tail.Width << N;
            int topH = tail.Height << N;
            long expectedTopBytes = BlockBytes(topW, topH, tailBlockBytes);
            var topMip = desc.Mips[0]; // largest first
            if (topMip.UncompressedSize != expectedTopBytes)
            {
                log($"  generic-mipmaps: temp002 declares top mip uncomp={topMip.UncompressedSize} " +
                    $"but expected {expectedTopBytes} for {topW}x{topH} {tailFormatName}; skipping " +
                    $"temp002 path");
                return null;
            }

            // Bounds check on offset+packedSize.
            if (topMip.Offset < 0 ||
                topMip.PackedSize <= 0 ||
                topMip.Offset + topMip.PackedSize > fileBytes.Length)
            {
                log($"  generic-mipmaps: temp002 top mip offset={topMip.Offset} " +
                    $"packed={topMip.PackedSize} exceeds .mipmaps size {fileBytes.Length}; " +
                    $"skipping temp002 path");
                return null;
            }

            // Extract the top mip's raw block bytes.
            byte[] blocks;
            if (topMip.Compression == Temp002Descriptor.CompressionCode.Raw)
            {
                if (topMip.PackedSize != topMip.UncompressedSize)
                {
                    log($"  generic-mipmaps: temp002 raw mip with packed!=size ({topMip.PackedSize}!={topMip.UncompressedSize})");
                    return null;
                }
                blocks = new byte[topMip.UncompressedSize];
                Buffer.BlockCopy(fileBytes, (int)topMip.Offset, blocks, 0, blocks.Length);
            }
            else if (topMip.Compression == Temp002Descriptor.CompressionCode.Lz4)
            {
                blocks = new byte[topMip.UncompressedSize];
                int consumed;
                try
                {
                    consumed = EgoEngineLibrary.Graphics.Lz4RawBlock.Decode(fileBytes, (int)topMip.Offset, blocks);
                }
                catch (Exception ex)
                {
                    log($"  generic-mipmaps: temp002 LZ4 decode threw for {topW}x{topH}: {ex.Message}");
                    return null;
                }
                // Sanity: expect the LZ4 decoder to consume roughly the
                // declared packed size. Small drift can come from LZ4
                // padding alignment; reject large discrepancies.
                if (consumed <= 0 || Math.Abs((long)consumed - topMip.PackedSize) > 16)
                {
                    log($"  generic-mipmaps: temp002 LZ4 consumed {consumed} bytes vs " +
                        $"declared packed {topMip.PackedSize}; skipping temp002 path");
                    return null;
                }
            }
            else
            {
                log($"  generic-mipmaps: temp002 declares unknown compression code " +
                    $"0x{topMip.Compression:X2} for mip[0]; skipping temp002 path");
                return null;
            }

            byte[] bgra;
            try
            {
                bgra = DecodeByFormat(tail, blocks, topW, topH, bc5Mode);
            }
            catch (Exception ex)
            {
                log($"  generic-mipmaps: temp002 BC decode failed for {topW}x{topH} {tailFormatName}: {ex.Message}");
                return null;
            }

            string compName = topMip.Compression == Temp002Descriptor.CompressionCode.Lz4 ? "LZ4" : "raw";
            log($"  generic-mipmaps: OK {Path.GetFileName(mipPath)} -> top mip {topW}x{topH} " +
                $"({N} level(s), via temp002[{desc.DetectedLayout}], {compName}, {tailFormatName})");

            return new DecodedTexture
            {
                Bgra = bgra,
                Width = topW,
                Height = topH,
                FormatName = $"{tailFormatName} (generic sidecar, temp002-driven)",
            };
        }

        /// <summary>
        /// Attempt (a) from the dispatch: raw block-compressed concat.
        /// Infers N by solving
        ///     fileSize == sum_{i=1..N} BlockBytes(tailW &lt;&lt; i, tailH &lt;&lt; i, bpb)
        /// If N is found, slices the first (top, largest) block and decodes.
        /// Returns null if no N matches -- the file is not raw-concat.
        /// </summary>
        private static DecodedTexture TryDecodeRawConcat(
            byte[] fileBytes, ImageDescriptor tail, int tailBlockBytes,
            string tailFormatName, string mipPath, Action<string> log,
            BC5DecodeMode bc5Mode)
        {
            int N = InferRawMipCount(
                fileBytes.Length, tail.Width, tail.Height, tailBlockBytes);
            if (N <= 0) return null;

            int topW = tail.Width << N;
            int topH = tail.Height << N;
            int topSize = BlockBytes(topW, topH, tailBlockBytes);
            if (topSize <= 0 || topSize > fileBytes.Length)
            {
                log($"  generic-mipmaps: raw-concat matched N={N} but implausible top-mip " +
                    $"size {topSize} (file is {fileBytes.Length} bytes)");
                return null;
            }

            byte[] topBlocks = new byte[topSize];
            Buffer.BlockCopy(fileBytes, 0, topBlocks, 0, topSize);

            byte[] bgra;
            try
            {
                bgra = DecodeByFormat(tail, topBlocks, topW, topH, bc5Mode);
            }
            catch (Exception ex)
            {
                log($"  generic-mipmaps: raw-concat {tailFormatName} decode failed for {topW}x{topH}: {ex.Message}");
                return null;
            }

            log($"  generic-mipmaps: OK {Path.GetFileName(mipPath)} -> top mip {topW}x{topH} " +
                $"({N} level(s), raw {fileBytes.Length} bytes, {tailFormatName})");

            return new DecodedTexture
            {
                Bgra = bgra,
                Width = topW,
                Height = topH,
                FormatName = $"{tailFormatName} (generic sidecar, raw-concat)",
            };
        }

        /// <summary>
        /// Attempt (b) from the dispatch: LZ4-per-mip concat. Each mip is
        /// an LZ4-raw block whose uncompressed size is known from the
        /// mip pyramid geometry (tail.Width &lt;&lt; level, tail.Height &lt;&lt; level,
        /// tailBlockBytes). The blocks are concatenated with NO framing.
        ///
        /// To find N without a descriptor: for N = 1..10, attempt to
        /// LZ4-decode N consecutive mips starting with a top of
        /// (tail.W &lt;&lt; N, tail.H &lt;&lt; N) and halving each level, and see
        /// whether the source cursor lands EXACTLY at end-of-file. This
        /// is a strong check because LZ4 can't produce a 0-byte consume
        /// and any wrong-N guess exhausts early or leaves trailing bytes.
        ///
        /// Once N is found, the top mip has already been decoded during
        /// the trial -- we return it directly.
        ///
        /// This is the layout F1 22 uses for all shared generics, and is
        /// also what F1 2021 uses for driver atlases (those go through
        /// the sibling <see cref="MipmapsSidecarLoader"/>, not this loader,
        /// because they also have a temp002.image descriptor telling the
        /// loader the mip count up front). Here we infer N from scratch.
        /// </summary>
        private static DecodedTexture TryDecodeLz4PerMipConcat(
            byte[] fileBytes, ImageDescriptor tail, int tailBlockBytes,
            string tailFormatName, string mipPath, Action<string> log,
            BC5DecodeMode bc5Mode)
        {
            const int MaxN = 10;

            byte[] topBlocks = null;
            int topW = 0, topH = 0;
            int matchedN = 0;

            for (int N = 1; N <= MaxN; N++)
            {
                int candTopW = tail.Width << N;
                int candTopH = tail.Height << N;
                if (candTopW > 16384 || candTopH > 16384) break;

                int cursor = 0;
                byte[] firstMip = null;
                bool chainOK = true;
                int w = candTopW, h = candTopH;

                for (int i = 0; i < N; i++)
                {
                    if (cursor >= fileBytes.Length) { chainOK = false; break; }
                    int uncompSize = BlockBytes(w, h, tailBlockBytes);
                    byte[] mip = new byte[uncompSize];
                    int consumed;
                    try
                    {
                        consumed = EgoEngineLibrary.Graphics.Lz4RawBlock.Decode(fileBytes, cursor, mip);
                    }
                    catch
                    {
                        chainOK = false; break;
                    }
                    if (consumed <= 0) { chainOK = false; break; }
                    if (i == 0) firstMip = mip;
                    cursor += consumed;
                    w /= 2; h /= 2;
                    if (w < 1) w = 1;
                    if (h < 1) h = 1;
                }

                // EXACT-consume check: the correct N decodes a valid LZ4
                // chain AND lands the source cursor exactly at EOF. Any
                // other N leaves either slack or over-runs.
                if (chainOK && cursor == fileBytes.Length && firstMip != null)
                {
                    matchedN = N;
                    topBlocks = firstMip;
                    topW = candTopW;
                    topH = candTopH;
                    break;
                }
            }

            if (topBlocks == null)
                return null;

            byte[] bgra;
            try
            {
                bgra = DecodeByFormat(tail, topBlocks, topW, topH, bc5Mode);
            }
            catch (Exception ex)
            {
                log($"  generic-mipmaps: lz4-concat {tailFormatName} decode failed for {topW}x{topH}: {ex.Message}");
                return null;
            }

            log($"  generic-mipmaps: OK {Path.GetFileName(mipPath)} -> top mip {topW}x{topH} " +
                $"({matchedN} level(s), LZ4-per-mip {fileBytes.Length} bytes, {tailFormatName})");

            return new DecodedTexture
            {
                Bgra = bgra,
                Width = topW,
                Height = topH,
                FormatName = $"{tailFormatName} (generic sidecar, lz4-concat)",
            };
        }

        // ---------------------------------------------------------------------
        // INTERNALS
        // ---------------------------------------------------------------------

        /// <summary>
        /// Block-compressed byte count for a WxH image at <paramref name="bytesPerBlock"/>
        /// (8 for BC1, 16 for BC2/BC3). Enforces the minimum-one-block rule
        /// for images narrower than 4 pixels (matches the canonical
        /// BC1/BC3 size rule the DDS spec describes).
        /// </summary>
        private static int BlockBytes(int w, int h, int bytesPerBlock)
        {
            int bw = (w + 3) / 4; if (bw < 1) bw = 1;
            int bh = (h + 3) / 4; if (bh < 1) bh = 1;
            return bw * bh * bytesPerBlock;
        }

        /// <summary>
        /// Given a file size and a known tail geometry, find N in
        /// [1, 10] such that
        ///     sum_{i=1..N} BlockBytes(tailW &lt;&lt; i, tailH &lt;&lt; i, bpb) == fileSize
        /// Returns 0 if no N matches exactly. The search is capped at
        /// N=10 because 1024x the tail dimension already exceeds any
        /// realistic game-asset resolution; larger N would only be a
        /// spurious match on pathological file sizes.
        /// </summary>
        private static int InferRawMipCount(int fileSize, int tailW, int tailH, int bytesPerBlock)
        {
            for (int N = 1; N <= 10; N++)
            {
                long sum = 0;
                bool overflow = false;
                for (int i = 1; i <= N; i++)
                {
                    int w = tailW << i;
                    int h = tailH << i;
                    if (w > 16384 || h > 16384) { overflow = true; break; }
                    sum += BlockBytes(w, h, bytesPerBlock);
                    if (sum > int.MaxValue) { overflow = true; break; }
                }
                if (overflow) break;
                if (sum == fileSize) return N;
            }
            return 0;
        }

        private static byte[] DecodeByFormat(ImageDescriptor tail, byte[] data, int w, int h,
                                             BC5DecodeMode bc5Mode)
        {
            if (tail.IsBC1) return BlockCompressionDecoder.DecodeBC1(data, w, h);
            if (tail.IsBC2) return BlockCompressionDecoder.DecodeBC2(data, w, h);
            if (tail.IsBC3) return BlockCompressionDecoder.DecodeBC3(data, w, h);
            // BC5: same block format, different interpretation. Callers
            // requesting normal-map output get (R=X, G=Y, B=Z); everyone
            // else gets the R channel as grayscale (used for mask
            // textures and PBR surface "_s" textures where the dominant
            // signal is in R). Without this routing the hi-res sidecar
            // for surface textures rendered as a purple/pink normal-
            // map visualisation, which is "the corruption on Hulkenberg's
            // helmet" reported on 2026-05-25.
            if (tail.IsBC5)
            {
                // GrayscaleR mode: use the RAW R-channel decoder, NOT
                // the tyre-band-clamping DecodeBC5AsGrayscaleR. The
                // latter remaps 0..255 to 14..27 to fake tyre rubber's
                // narrow visual range in the WPF preview -- correct for
                // tread textures, catastrophic for surface (_s) and mask
                // (_m) textures where the actual roughness / opacity
                // values matter. Hulkenberg's helmet_s came out flat
                // dark grey under the clamping decoder; the raw decoder
                // preserves the full range.
                return bc5Mode == BC5DecodeMode.NormalMap
                    ? BlockCompressionDecoder.DecodeBC5AsNormalMap(data, w, h)
                    : BlockCompressionDecoder.DecodeBC5AsRawR(data, w, h);
            }
            if (tail.IsBC7) return BlockCompressionDecoder.DecodeBC7(data, w, h);
            throw new NotSupportedException($"format {tail.FormatCode}");
        }

        // ----- File-location search (mirrors MipmapsSidecarLoader's pattern) -----

        /// <summary>
        /// Try to find the .mipmaps file on disk. Search order:
        ///   1. Direct path mirroring the EAID's logical subdir under
        ///      the located VehiclePackageRoot (cheap when it works).
        ///   2. Recursive scan of the VehiclePackageRoot for either
        ///      naming variant (on-disk rename form or literal form).
        ///   3. Recursive scan of the GameRoot (F1 2021's shared
        ///      streaming textures can live above the package).
        ///
        /// Results are cached per-filename for the process lifetime so
        /// repeated lookups don't re-scan disk.
        /// </summary>
        /// <summary>
        /// Returns true when <paramref name="candidate"/> looks like a
        /// year-stamped variant of <paramref name="wantedLiteral"/>. F1 25
        /// ships per-driver helmet textures in two parallel forms:
        ///   nico_hulkenberg_helmet_d.tif.2024.mipmaps
        ///   nico_hulkenberg_helmet_d.tif.2025.mipmaps
        /// Either can serve as the source for a hi-res mip lookup. We accept
        /// either; the caller takes whichever scan finds first.
        /// </summary>
        /// <summary>
        /// Extracts the package-name segment from an EAID -- the part between
        /// "eaid://" and the first forward slash. Returns null when the EAID
        /// is malformed.
        /// Example: "eaid://character_package/drivers/..." -> "character_package".
        /// </summary>
        private static string ExtractEaidPackageName(string eaid)
        {
            if (string.IsNullOrEmpty(eaid)) return null;
            const string scheme = "eaid://";
            if (!eaid.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
            int firstSlash = eaid.IndexOf('/', scheme.Length);
            if (firstSlash <= scheme.Length) return null;
            return eaid.Substring(scheme.Length, firstSlash - scheme.Length);
        }

        /// <summary>
        /// Look in EXACTLY the given directory (no recursion) for a .mipmaps
        /// file matching either of the two literal forms OR the year-stamped
        /// variant (e.g. ".tif.2024.mipmaps" / ".tif.2025.mipmaps"). Returns
        /// the first match found, or null when the directory doesn't exist
        /// or no candidate matches.
        /// </summary>
        private static string FindMipmapsInExactDir(string dir, string wantedName, string wantedNameLiteral)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            try
            {
                string p1 = Path.Combine(dir, wantedName);
                if (File.Exists(p1)) return p1;
                string p2 = Path.Combine(dir, wantedNameLiteral);
                if (File.Exists(p2)) return p2;
                foreach (var f in Directory.EnumerateFiles(dir, "*.mipmaps"))
                {
                    string name = Path.GetFileName(f);
                    if (IsYearStampedMipmapsMatch(name, wantedNameLiteral))
                        return f;
                }
            }
            catch { }
            return null;
        }

        private static bool IsYearStampedMipmapsMatch(string candidate, string wantedLiteral)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(wantedLiteral))
                return false;
            const string suffix = ".mipmaps";
            if (!wantedLiteral.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
            if (!candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
            string wantedStem = wantedLiteral.Substring(0, wantedLiteral.Length - suffix.Length);
            string candStem = candidate.Substring(0, candidate.Length - suffix.Length);
            // Year-stamped form: candStem = wantedStem + "." + 4digits
            if (candStem.Length < wantedStem.Length + 5) return false;
            if (!candStem.StartsWith(wantedStem + ".", StringComparison.OrdinalIgnoreCase)) return false;
            string year = candStem.Substring(wantedStem.Length + 1);
            if (year.Length != 4) return false;
            for (int i = 0; i < 4; i++)
                if (year[i] < '0' || year[i] > '9') return false;
            return true;
        }

        /// <summary>
        /// Authoritative locator that reads the in-ERP <c>temp002.image</c>
        /// descriptor and uses its <c>Path</c> field verbatim, joined to
        /// the game-install root. Matches EgoErpArchiver's lookup -- the
        /// path field is year-disambiguated by construction so there's
        /// no ambiguity to resolve. Returns <c>null</c> when the
        /// descriptor is missing, unreadable, or the synthesised path
        /// doesn't exist on disk (caller falls back to the legacy
        /// EAID-guess locator in that case).
        /// </summary>
        // Internal: MipmapsSidecarLoader (the legacy driver-atlas loader)
        // reuses this so BOTH sidecar loaders resolve mipmaps from the
        // ERP's own temp002 descriptor before falling back to scanning.
        internal static string TryLocateViaTemp002Descriptor(
            string textureEaid, string extractedPackageRoot,
            F1PackageLocator.Located located, Action<string> log)
        {
            if (located == null) return null;

            // The descriptor's Path field is a GAME-INSTALL-RELATIVE
            // path that starts with the package leaf (e.g.
            // "f1_2025_vehicle_package/teams/redbull/textures/...").
            // For older F1 games (F1 22-24) the package sits directly
            // under the game install root, so combining with GameRoot
            // works. F1 25 wraps every package in a `2025_asset_groups/`
            // (or similar) intermediate folder, so Path.Combine(GameRoot,
            // descPath) misses by one directory.
            //
            // The fix: try BOTH plausible anchors, in order:
            //
            //   1. PARENT of VehiclePackageRoot -- the folder the package
            //      leaf sits inside (`2025_asset_groups/` on F1 25,
            //      GameRoot itself on F1 22-24). Covers installs where
            //      the mipmaps live next to the ERPs.
            //   2. GameRoot -- F1 25 ships the streaming .mipmaps trees
            //      as TOP-LEVEL `f1_20XX_vehicle_package/` folders under
            //      the install root, NOT inside `2025_asset_groups/`.
            //      With only anchor 1 the descriptor path missed and the
            //      loader fell back to filesystem scanning (which is
            //      what allowed cross-year picks before the foreign-
            //      package exclusion). The descriptor path is exact --
            //      year, team, and filename all come from the ERP itself.
            var anchors = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(located.VehiclePackageRoot))
            {
                string parent = Path.GetDirectoryName(
                    located.VehiclePackageRoot.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                    anchors.Add(parent);
            }
            if (!string.IsNullOrWhiteSpace(located.GameRoot) &&
                Directory.Exists(located.GameRoot) &&
                !anchors.Exists(a => string.Equals(a, located.GameRoot, StringComparison.OrdinalIgnoreCase)))
                anchors.Add(located.GameRoot);
            if (anchors.Count == 0)
                return null;

            // Resolve the temp002 path in the extracted cache. Mirrors
            // the same construction TryDecodeViaTemp002 does below;
            // factoring it out would require a small refactor we can
            // do later -- duplicating two lines here keeps this change
            // narrow.
            string rel = EaidPathResolver.StripEaidPrefix(textureEaid);
            if (rel == null) return null;
            int lastSlash = Math.Max(rel.LastIndexOf('/'), rel.LastIndexOf('\\'));
            string dir = (lastSlash >= 0) ? rel.Substring(0, lastSlash) : "";
            string fileLeaf = (lastSlash >= 0) ? rel.Substring(lastSlash + 1) : rel;
            string temp002Path = Path.Combine(
                extractedPackageRoot,
                dir.Replace('/', Path.DirectorySeparatorChar),
                $"{fileLeaf}!!!temp002.image");
            if (!File.Exists(temp002Path)) return null;

            var desc = Temp002Descriptor.TryRead(temp002Path, log);
            if (desc == null || string.IsNullOrWhiteSpace(desc.Path)) return null;

            // The descriptor's Path field starts with the package
            // leaf (e.g. "f1_2025_vehicle_package/teams/redbull/
            // textures/redbull_decal_da.tif.mipmaps"). Joining it
            // to an anchor yields the on-disk path verbatim.
            string descPath = desc.Path.Replace('/', Path.DirectorySeparatorChar);

            lock (_cacheLock)
            {
                string key = $"temp002|{descPath}";
                if (_pathCache.TryGetValue(key, out var cached))
                    return cached;
            }

            foreach (var anchor in anchors)
            {
                string candidate = Path.Combine(anchor, descPath);

                if (File.Exists(candidate))
                {
                    log($"  generic-mipmaps: temp002 path '{desc.Path}' -> {candidate}");
                    CacheStore($"temp002|{descPath}", candidate);
                    return candidate;
                }

                // F1 25 character textures sometimes use a year-stamped form
                // (".tif.2025.mipmaps") at the same dir. Only check that
                // exact directory -- no recursion, in keeping with the
                // EgoErpArchiver discipline.
                try
                {
                    string parent = Path.GetDirectoryName(candidate);
                    string leaf = Path.GetFileName(candidate);
                    string leafNoExt = leaf.EndsWith(".mipmaps", StringComparison.OrdinalIgnoreCase)
                        ? leaf.Substring(0, leaf.Length - ".mipmaps".Length) : leaf;
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    {
                        foreach (var f in Directory.EnumerateFiles(
                            parent, leafNoExt + ".*.mipmaps"))
                        {
                            log($"  generic-mipmaps: temp002 path '{desc.Path}' " +
                                $"-> year-stamped variant {f}");
                            CacheStore($"temp002|{descPath}", f);
                            return f;
                        }
                    }
                }
                catch { /* best-effort */ }
            }

            log($"  generic-mipmaps: temp002 path '{desc.Path}' not found under any anchor " +
                $"({anchors.Count} tried)");
            CacheStore($"temp002|{descPath}", null);
            return null;
        }

        private static string TryLocateMipmapsFile(
            F1PackageLocator.Located located,
            string relDir,
            string wantedName,
            string wantedNameLiteral,
            string eaidPackageName,
            Action<string> log)
        {
            lock (_cacheLock)
            {
                if (_pathCache.TryGetValue(wantedName, out var cached))
                    return cached;
            }

            // 1a. Direct EAID-FULL-path lookup. The EAID encodes the package
            // name (e.g. "character_package") as its first segment. The game
            // install keeps that segment as a SUBDIRECTORY of the root, even
            // though the extracted-cache layout drops it. Reconstruct the
            // full path so we hit the exact file the EAID points at -- this
            // is the ONLY way to disambiguate when the install ships parallel
            // siblings (Hamilton 2024 mercedes vs. 2025 ferrari vs. 2025
            // ferrarimonza all carry a lewis_hamilton_helmet_d.tif.<year>.mipmaps).
            // Without this, a recursive scan picks whichever match comes
            // first and the preview ends up with cross-year textures stitched
            // together.
            if (!string.IsNullOrEmpty(eaidPackageName))
            {
                string fullDir = Path.Combine(
                    located.VehiclePackageRoot,
                    eaidPackageName.Replace('/', Path.DirectorySeparatorChar),
                    (relDir ?? "").Replace('/', Path.DirectorySeparatorChar));
                string p = FindMipmapsInExactDir(fullDir, wantedName, wantedNameLiteral);
                if (p != null) { CacheStore(wantedName, p); return p; }
            }

            // 1b. Direct EAID-mirrored path (legacy path, no package prefix).
            string pkgLeaf = Path.GetFileName(located.VehiclePackageRoot);
            string subDir = StripLeadingSegment(relDir, pkgLeaf);
            string directDir = Path.Combine(
                located.VehiclePackageRoot,
                (subDir ?? "").Replace('/', Path.DirectorySeparatorChar));

            {
                string p = FindMipmapsInExactDir(directDir, wantedName, wantedNameLiteral);
                if (p != null) { CacheStore(wantedName, p); return p; }
            }

            // 2. Recursive scan of the package root.
            log($"  generic-mipmaps: scanning {located.VehiclePackageRoot} for {wantedName}");
            string found = FindFileRecursive(
                located.VehiclePackageRoot, wantedName, wantedNameLiteral, excludeDirNames: null, log);

            // 3. Widen to game root if still missing.
            //
            // IMPORTANT: the game root also contains SIBLING vehicle_package
            // folders for prior seasons (e.g. F1 23 ships
            //   <GameRoot>\f1_2022_vehicle_package\teams\mclaren\textures\mclaren_paint_d.tif.mipmaps
            // alongside the current-year package, to support F1 World mode's
            // prior-season cars). Without a guard, a broad recursive scan
            // happily returns the first match it hits and the preview ends
            // up textured with last year's livery even though the correct
            // year was detected and the correct package was scanned first.
            //
            // The fix: compute the set of "foreign" vehicle_package folder
            // names (every known package EXCEPT the one the ERP lives in),
            // and have the recursive walker skip any directory whose leaf
            // name is in that set. Files that legitimately live above the
            // package (the F1 2021 shared-streaming use case this step was
            // originally added for) are still discoverable because those
            // paths don't go through any *_vehicle_package folder at all.
            if (found == null && !string.IsNullOrEmpty(located.GameRoot))
            {
                string ownPackageLeaf = Path.GetFileName(
                    (located.VehiclePackageRoot ?? string.Empty)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                var foreignPackages = BuildForeignVehiclePackageSet(
                    ownPackageLeaf, located.GameRoot);

                log($"  generic-mipmaps: not in package, scanning {located.GameRoot} " +
                    $"(excluding {foreignPackages.Count} foreign vehicle_package dir(s): " +
                    $"{string.Join(", ", foreignPackages)})");
                found = FindFileRecursive(
                    located.GameRoot, wantedName, wantedNameLiteral, foreignPackages, log);
            }

            if (found == null)
            {
                log($"  generic-mipmaps: {wantedName} not found in install " +
                    $"-- falling back to ERP tail");
            }
            else
            {
                log($"  generic-mipmaps: located at {found}");
            }

            CacheStore(wantedName, found);
            return found;
        }

        /// <summary>
        /// Build the set of "*_vehicle_package" folder names that represent
        /// SEASONS OTHER THAN the one currently being loaded. The broad
        /// GameRoot scan skips any directory whose leaf name is in this
        /// set so that F1 23 loads can't accidentally pick up F1 22 sidecar
        /// files (which really do sit side-by-side on disk for F1 World
        /// mode's prior-season cars) and vice versa.
        ///
        /// Approach: walk the GameRoot (and one level down) looking for
        /// every folder matching the case-insensitive regex
        /// <c>f[12]_\d{4}.*_vehicle_package</c>, then exclude all of
        /// them EXCEPT the own package's leaf name. This catches both
        /// the canonical <c>f2_2019_vehicle_package</c> name and the
        /// real-world variant <c>f2_2019_19_vehicle_package</c> (which
        /// some F1 2019 installs actually ship ? mid-season bump with
        /// an extra "_19" component). Previously the exclusion set was
        /// a hardcoded enum of canonical names only, so F1 2019 previews
        /// picked up F2 2019_19 sidecar mipmaps and rendered the wrong
        /// "generic" atlas.
        ///
        /// Only folders that actually exist on disk end up in the result,
        /// so the exclusion is always exactly the set of sibling packages
        /// the walker would otherwise descend into.
        /// </summary>
        private static HashSet<string> BuildForeignVehiclePackageSet(
            string ownPackageLeaf, string gameRoot)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot))
                return result;

            // Any folder whose leaf name starts with "f1_" or "f2_" and
            // ends with "_vehicle_package" is treated as a vehicle
            // package. We check that pattern rather than a fully anchored
            // regex to tolerate unusual separators that some extractors
            // produce (e.g. trailing dots, version suffixes).
            //
            // ALSO excluded: the safety-car package. F1 2020 (and adjacent
            // years) ships f1_safetycar_package alongside the vehicle
            // package, and that folder contains its OWN
            // shared/generics/textures/generic_main_d.tif.mipmaps for the
            // Mercedes-AMG GT safety car. The safety car's atlas has the
            // same filename as the F1 cars' shared generic atlas but
            // entirely different content (it has a closed-cockpit half
            // for the AMG GT cabin where the F1 atlas has the open-cockpit
            // half for F1 cars). When an F1 car's vehicle_package doesn't
            // ship its own copy of generic_main_d.tif.mipmaps in a place
            // the per-package recursive scan can find, the broader
            // GameRoot fallback would happily pick up the safety car's
            // copy -- the cockpit submesh would then sample the wrong
            // half and only the "car components" portion of the texture
            // would render correctly. f1_safetycar_package isn't a
            // *_vehicle_package leaf so the original pattern missed it;
            // catch any leaf containing "safetycar" or "safety_car" as a
            // foreign package whenever the OWN package is a vehicle
            // package (i.e. when the user is loading an F1 car, not the
            // safety car itself).
            bool ownPackageIsVehiclePackage =
                !string.IsNullOrEmpty(ownPackageLeaf) &&
                ownPackageLeaf.EndsWith("_vehicle_package", StringComparison.OrdinalIgnoreCase) &&
                (ownPackageLeaf.StartsWith("f1_", StringComparison.OrdinalIgnoreCase) ||
                 ownPackageLeaf.StartsWith("f2_", StringComparison.OrdinalIgnoreCase));

            bool IsForeignAtlasPackageLeaf(string leaf)
            {
                if (string.IsNullOrEmpty(leaf)) return false;

                // (a) Other-season vehicle packages -- the original case.
                if (leaf.Length >= 20 &&
                    (leaf.StartsWith("f1_", StringComparison.OrdinalIgnoreCase) ||
                     leaf.StartsWith("f2_", StringComparison.OrdinalIgnoreCase)) &&
                    leaf.EndsWith("_vehicle_package", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // (b) Safety-car packages, but only when the caller is
                //     loading an F1/F2 vehicle -- otherwise we'd lock the
                //     safety car out of finding its own textures when the
                //     user is loading the safety car itself.
                if (ownPackageIsVehiclePackage &&
                    (leaf.IndexOf("safetycar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     leaf.IndexOf("safety_car", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }

                return false;
            }

            // Scan GameRoot and one level down. One level of nesting is
            // enough for the typical "2019_asset_groups/f1_2019_vehicle_
            // package" layout without descending into the thousands of
            // per-team subfolders underneath each package.
            IEnumerable<string> SafeEnumerateDirs(string path)
            {
                try
                {
                    return Directory.EnumerateDirectories(path);
                }
                catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
                catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
                catch (IOException) { return Array.Empty<string>(); }
            }

            foreach (var dir in SafeEnumerateDirs(gameRoot))
            {
                string leaf = Path.GetFileName(dir);
                if (IsForeignAtlasPackageLeaf(leaf) &&
                    !string.Equals(leaf, ownPackageLeaf, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(leaf);
                }

                // One level deeper.
                foreach (var subdir in SafeEnumerateDirs(dir))
                {
                    string subLeaf = Path.GetFileName(subdir);
                    if (IsForeignAtlasPackageLeaf(subLeaf) &&
                        !string.Equals(subLeaf, ownPackageLeaf, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(subLeaf);
                    }
                }
            }

            return result;
        }

        private static string FindFileRecursive(
            string root, string wantedName, string wantedNameLiteral,
            HashSet<string> excludeDirNames, Action<string> log)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;

            // Manual DFS so we can prune whole subtrees (excludeDirNames) --
            // Directory.EnumerateFiles with AllDirectories gives no way to
            // skip a directory mid-walk.
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                string dir = stack.Pop();

                // Scan files at this level.
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir, "*.mipmaps", SearchOption.TopDirectoryOnly);
                }
                catch (UnauthorizedAccessException ex)
                {
                    log($"  generic-mipmaps: partial scan of {dir} (access denied: {ex.Message})");
                    continue;
                }
                catch (Exception ex)
                {
                    log($"  generic-mipmaps: scan of {dir} failed: {ex.Message}");
                    continue;
                }

                foreach (var path in files)
                {
                    string name = Path.GetFileName(path);
                    if (string.Equals(name, wantedName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, wantedNameLiteral, StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }

                    // F1 25 character textures use a year-stamped variant of
                    // the literal form -- "nico_hulkenberg_helmet_d.tif.2025.mipmaps"
                    // and "...2024.mipmaps" both exist alongside each other.
                    // Accept any name that matches "<stem>.<ext>.<4digits>.mipmaps"
                    // where the stem+ext portion equals wantedNameLiteral with
                    // the ".mipmaps" suffix removed.
                    if (IsYearStampedMipmapsMatch(name, wantedNameLiteral))
                        return path;
                }

                // Queue subdirectories, minus any we're excluding by leaf name.
                IEnumerable<string> subs;
                try
                {
                    subs = Directory.EnumerateDirectories(dir);
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (Exception) { continue; }

                foreach (var sub in subs)
                {
                    string leaf = Path.GetFileName(sub);
                    if (excludeDirNames != null && leaf != null && excludeDirNames.Contains(leaf))
                    {
                        // Pruned: foreign-year vehicle_package.
                        continue;
                    }
                    stack.Push(sub);
                }
            }

            return null;
        }

        private static void CacheStore(string wantedName, string resolvedPath)
        {
            lock (_cacheLock)
            {
                _pathCache[wantedName] = resolvedPath;
            }
        }

        /// <summary>
        /// Drop every cached path resolution. Called by MainWindow
        /// on every Load ERP so that a basename like
        /// "redbull_decal_da_tif.mipmaps" -- which has different
        /// resolutions for different yearly vehicle packages
        /// (f1_2025_..., f1_2026_..., etc.) -- can't be served from
        /// a previous load's cache and produce a wrong-year path.
        /// </summary>
        public static void ResetPathCache()
        {
            lock (_cacheLock)
            {
                _pathCache.Clear();
            }
        }

        /// <summary>
        /// If <paramref name="relPath"/> starts with "<paramref name="leadingSegment"/>/"
        /// (case-insensitive), strip that prefix. Used to turn an EAID's
        /// package-qualified relative path ("f1_2021_vehicle_package/
        /// shared/generics/textures/...") into a path relative to the
        /// located package root ("shared/generics/textures/...").
        /// </summary>
        private static string StripLeadingSegment(string relPath, string leadingSegment)
        {
            if (string.IsNullOrEmpty(relPath) || string.IsNullOrEmpty(leadingSegment))
                return relPath;
            string prefix = leadingSegment + "/";
            if (relPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return relPath.Substring(prefix.Length);
            string prefixBack = leadingSegment + "\\";
            if (relPath.StartsWith(prefixBack, StringComparison.OrdinalIgnoreCase))
                return relPath.Substring(prefixBack.Length);
            return relPath;
        }

        /// <summary>
        /// Resolution cache: wantedName (case-insensitive) -> absolute
        /// path or null for confirmed miss. Kept separate from
        /// <see cref="MipmapsSidecarLoader"/>'s cache so the two loaders'
        /// name-resolution pools don't cross-contaminate (they can
        /// legitimately have different hits for the same base name
        /// under edge-case install layouts).
        /// </summary>
        private static readonly Dictionary<string, string> _pathCache
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();
    }
}