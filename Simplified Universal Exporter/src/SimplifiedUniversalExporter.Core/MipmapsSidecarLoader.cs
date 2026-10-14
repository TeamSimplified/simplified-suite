using System;
using System.Collections.Generic;
using System.IO;

namespace SimplifiedUniversalExporter.Core
{
    /// <summary>
    /// Loads the high-resolution mip pyramid from a Codemasters .mipmaps
    /// sidecar file on the real on-disk install, so the tool can render
    /// driver numbers (and other streamed-in textures) at their true
    /// resolution instead of the 64x32 tail embedded in the ERP.
    ///
    /// Background
    /// ----------
    /// A texture's full mip chain is split between two locations:
    ///
    ///   * The ERP ships the TAIL of the chain (usually 4 mips, max
    ///     dimension 64x32 for driver decals) -- embedded as temp001.image
    ///     BC3 data. That's what the rest of the pipeline decodes today.
    ///
    ///   * A sibling .mipmaps file on the on-disk install holds the
    ///     high-resolution mips that sit ABOVE the tail (e.g. 128x64 up
    ///     to 1024x512). Each mip is stored as a standalone raw LZ4 block.
    ///     The file has no header -- mips are concatenated and must be
    ///     decoded sequentially, each producing a known uncompressed size
    ///     derived from the BC3 pyramid geometry.
    ///
    /// How we know a sidecar exists
    /// -----------------------------
    /// The ERP extraction produces a parallel temp002.image descriptor
    /// next to temp000.image/temp001.image in the extracted cache. Its
    /// layout (validated against mclaren_driver_1_tif___temp002.image):
    ///
    ///   +0     u8   pathStringLen
    ///   +1     u8[] pathString (length prefixed ASCII; the in-game
    ///              reference, e.g.
    ///              "f1_2021_vehicle_package/teams/mclaren/textures/
    ///               mclaren_driver_1.tif.mipmaps")
    ///   +1+n   u32  sidecarMipCount      (number of mips in .mipmaps)
    ///   +5+n   u32  unknown (observed: 3)
    ///   +9+n   ...  per-mip descriptors (partially understood; not
    ///              needed since mip sizes are reconstructable from
    ///              pyramid geometry + sequential LZ4 decoding)
    ///
    /// Presence of temp002.image IS the signal that a sidecar exists.
    /// Its absence means this texture has no streamed high-res data
    /// and we should skip sidecar loading silently.
    ///
    /// File naming
    /// -----------
    /// The referenced path uses dots ("{stem}.tif.mipmaps") but the
    /// on-disk file is renamed to "{stem}_tif.mipmaps" (".tif." becomes
    /// "_tif."). This loader tries both spellings.
    ///
    /// Requirements
    /// ------------
    /// * F1PackageLocator.Last must have been set by MainWindow with a
    ///   non-null VehiclePackageRoot. If the user loaded a stray ERP
    ///   that doesn't live in a real install, we return null and the
    ///   caller falls back to its existing 64x32-tail upscale path.
    /// </summary>
    internal static class MipmapsSidecarLoader
    {
        /// <summary>
        /// Try to load the highest-resolution mip for a texture from its
        /// .mipmaps sidecar. Returns null on any failure (no install root
        /// known, file missing, descriptor missing, decode failure, etc.)
        /// so the caller can fall back.
        /// </summary>
        /// <param name="textureEaid">
        /// The texture EAID the pipeline was trying to render, e.g.
        /// "eaid://f1_2021_vehicle_package/teams/mclaren/textures/mclaren_driver_1.tif".
        /// </param>
        /// <param name="extractedPackageRoot">
        /// The tool's extracted cache root (where temp000/temp001/temp002
        /// live). Used both to read the tail dimensions (temp000.image) and
        /// to detect whether a sidecar exists (temp002.image).
        /// </param>
        /// <param name="log">Optional log sink for diagnostic breadcrumbs.</param>
        public static DecodedTexture TryLoadTopMip(
            string textureEaid,
            string extractedPackageRoot,
            Action<string> log)
        {
            log = log ?? (_ => { });

            var located = F1PackageLocator.Last;
            if (located == null || string.IsNullOrEmpty(located.VehiclePackageRoot))
            {
                // No install located. This is the normal case for stray
                // ERPs; silent miss so we don't spam the log.
                return null;
            }

            // Split the EAID so we know the relative directory and stem.
            string rel = EaidPathResolver.StripEaidPrefix(textureEaid);
            if (rel == null) return null;

            int lastSlash = Math.Max(rel.LastIndexOf('/'), rel.LastIndexOf('\\'));
            string relDir = (lastSlash >= 0) ? rel.Substring(0, lastSlash) : "";
            string file = (lastSlash >= 0) ? rel.Substring(lastSlash + 1) : rel;
            int dot = file.LastIndexOf('.');
            string stem = (dot >= 0) ? file.Substring(0, dot) : file;
            string ext = (dot >= 0) ? file.Substring(dot + 1) : "tif";

            // ----- Step 1: confirm a sidecar exists by checking temp002.image
            //        in the extracted cache. Its mip_count field is the
            //        authoritative level count.
            int sidecarMipCount;
            if (!TryReadSidecarMipCount(textureEaid, extractedPackageRoot,
                                         log, out sidecarMipCount))
            {
                // Either temp002.image doesn't exist (this texture has no
                // sidecar) or it was unreadable. Either way, nothing to do.
                return null;
            }
            if (sidecarMipCount <= 0 || sidecarMipCount > 16)
            {
                log($"  mipmaps: implausible sidecarMipCount={sidecarMipCount}, skipping");
                return null;
            }

            // ----- Step 2: read the tail dimensions from temp000.image.
            var (_, metaPath, _) = EaidPathResolver.ResolveTexturePaths(
                textureEaid, extractedPackageRoot);
            if (metaPath == null || !File.Exists(metaPath))
            {
                log($"  mipmaps: descriptor {metaPath} missing, can't anchor pyramid");
                return null;
            }

            ImageDescriptor tail;
            try { tail = ImageDescriptor.FromFile(metaPath); }
            catch (Exception ex)
            {
                log($"  mipmaps: failed to read descriptor {metaPath}: {ex.Message}");
                return null;
            }

            if (!tail.IsBC3)
            {
                // This loader only handles BC3 at the moment. Driver decals
                // are always BC3; other formats can be added later if
                // high-res previews for them prove useful.
                log($"  mipmaps: tail format {tail.FormatCode} not BC3, skipping sidecar");
                return null;
            }

            if (tail.Width <= 0 || tail.Height <= 0)
            {
                log($"  mipmaps: implausible tail dims ({tail.Width}x{tail.Height}), skipping");
                return null;
            }

            // ----- Step 3: locate the .mipmaps file on disk.
            //
            // Codemasters installs DO NOT always mirror the EAID's logical
            // path on disk. The EAID places a texture under
            //   "{package}/teams/{team}/textures/..."
            // but the actual on-disk layout for F1 2021's streaming
            // textures varies (observed: the per-team "textures" subfolder
            // simply does not exist). So:
            //
            //   1. Try the optimistic direct path first (current EAID-
            //      mirrored layout). Cheap when it works.
            //   2. If that misses, recursively scan the located
            //      VehiclePackageRoot for a file with the expected name.
            //      Cached across calls so we scan at most once per
            //      filename per model load.
            //   3. If that still misses, escalate to a scan of the GameRoot
            //      (F1 2021's shared streaming textures may live above the
            //      package). Same cache.
            //
            // The cache is keyed by the lowercase filename, and holds the
            // absolute path or null-sentinel for confirmed misses.
            string wantedName = $"{stem}_{ext}.mipmaps";      // on-disk rename form
            string wantedNameLiteral = $"{stem}.{ext}.mipmaps"; // in-descriptor form

            // Descriptor-first: the texture's temp002 section inside the
            // ERP carries the exact game-install-relative .mipmaps path
            // (year, team, and filename included), so prefer it over any
            // filesystem scanning. Scanning remains the fallback for
            // textures whose descriptor lacks a Path field.
            string mipPath = GenericMipmapsSidecarLoader.TryLocateViaTemp002Descriptor(
                textureEaid, extractedPackageRoot, located, log)
                ?? TryLocateMipmapsFile(
                    located, relDir, wantedName, wantedNameLiteral, log);
            if (mipPath == null)
            {
                // Logged once per filename inside the locator; no spam here.
                return null;
            }

            // ----- Step 4: decode the largest mip.
            //
            // Sidecar mips cover levels ABOVE the tail, largest first. With
            // sidecarMipCount = N and tail dims (tW, tH), the largest mip
            // is (tW << N) x (tH << N). Each subsequent mip halves in each
            // dimension.
            int topW = tail.Width << sidecarMipCount;
            int topH = tail.Height << sidecarMipCount;
            return DecodeTopMip(mipPath, topW, topH, sidecarMipCount, log);
        }

        // ---------------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------------

        /// <summary>
        /// Read temp002.image (if it exists) and extract its mip_count field.
        /// Returns false if the file is absent or too short to hold the field;
        /// that means "no sidecar for this texture" -- a normal, silent miss.
        ///
        /// First tries the full BT-template-validated parser
        /// (<see cref="Temp002Descriptor.TryRead"/>). That's the authoritative
        /// path for F1 22 and F1 23; it returns a record with per-mip offsets
        /// and compression bytes -- not used here directly (this function
        /// only exposes mipCount for back-compat), but gives us a clean
        /// layout-validated mipCount value.
        ///
        /// If the full parser returns null (F1 2021 files with their
        /// "observed 3" quirk can fail the self-consistency check), fall
        /// back to the minimal-parse original logic that only reads
        /// pathStringLen + path + mipCount. That was the code in place
        /// before the BT-template reader existed, and it has kept F1 2021
        /// driver atlases working cleanly; keeping it as a fallback means
        /// zero regression risk for any file format the new parser
        /// doesn't recognise.
        /// </summary>
        private static bool TryReadSidecarMipCount(
            string textureEaid, string extractedPackageRoot,
            Action<string> log, out int mipCount)
        {
            mipCount = 0;

            string rel = EaidPathResolver.StripEaidPrefix(textureEaid);
            if (rel == null) return false;

            int lastSlash = Math.Max(rel.LastIndexOf('/'), rel.LastIndexOf('\\'));
            string dir = (lastSlash >= 0) ? rel.Substring(0, lastSlash) : "";
            string fileName = (lastSlash >= 0) ? rel.Substring(lastSlash + 1) : rel;
            // fileName is e.g. "mclaren_driver_1.tif"; the temp002.image lives
            // next to it with the same "!!!temp002.image" suffix pattern that
            // the existing code uses for temp000/temp001.
            string temp002 = Path.Combine(
                extractedPackageRoot,
                dir.Replace('/', Path.DirectorySeparatorChar),
                $"{fileName}!!!temp002.image");

            if (!File.Exists(temp002))
                return false;

            // PREFERRED PATH: full BT-template parse.
            var full = Temp002Descriptor.TryRead(temp002, log);
            if (full != null && full.MipCount > 0)
            {
                mipCount = full.MipCount;
                return true;
            }

            // FALLBACK: minimal parse preserved from the pre-BT-template
            // implementation. See docstring above for why.
            byte[] bytes;
            try { bytes = File.ReadAllBytes(temp002); }
            catch (Exception ex)
            {
                log($"  mipmaps: read {temp002} failed: {ex.Message}");
                return false;
            }

            if (bytes.Length < 1) return false;
            int strLen = bytes[0];
            int mipCountOffset = 1 + strLen;
            if (bytes.Length < mipCountOffset + 4)
            {
                log($"  mipmaps: temp002.image truncated (len={bytes.Length}, " +
                    $"need {mipCountOffset + 4})");
                return false;
            }

            mipCount = BitConverter.ToInt32(bytes, mipCountOffset);
            return true;
        }

        /// <summary>
        /// Decode the .mipmaps file to BGRA32 at the top mip's dimensions.
        /// Mips are stored largest-first as concatenated raw LZ4 blocks, with
        /// no per-mip header on disk -- each LZ4 call consumes exactly its
        /// own bytes and the next mip starts immediately after.
        /// </summary>
        private static DecodedTexture DecodeTopMip(
            string mipPath, int topW, int topH, int levels, Action<string> log)
        {
            byte[] fileBytes;
            try { fileBytes = File.ReadAllBytes(mipPath); }
            catch (Exception ex)
            {
                log($"  mipmaps: read failed {mipPath}: {ex.Message}");
                return null;
            }

            if (fileBytes.Length < 8)
            {
                log($"  mipmaps: file too small ({fileBytes.Length} bytes)");
                return null;
            }

            byte[] topBc3 = null;
            int cursor = 0;
            int w = topW, h = topH;
            for (int i = 0; i < levels; i++)
            {
                if (cursor >= fileBytes.Length)
                {
                    log($"  mipmaps: source exhausted at mip[{i}] of {levels}");
                    break;
                }
                int uncompSize = Bc3ByteSize(w, h);
                byte[] mip = new byte[uncompSize];
                int consumed;
                try
                {
                    consumed = EgoEngineLibrary.Graphics.Lz4RawBlock.Decode(fileBytes, cursor, mip);
                }
                catch (Exception ex)
                {
                    log($"  mipmaps: LZ4 decode threw on mip[{i}] {w}x{h}: {ex.Message}");
                    return null;
                }
                if (consumed <= 0)
                {
                    log($"  mipmaps: LZ4 consumed 0 bytes on mip[{i}] {w}x{h}, stream corrupt");
                    return null;
                }
                if (i == 0) topBc3 = mip; // keep the largest
                cursor += consumed;
                w /= 2; h /= 2;
                if (w < 1) w = 1;
                if (h < 1) h = 1;
            }

            if (topBc3 == null)
            {
                log($"  mipmaps: no top mip captured");
                return null;
            }

            // Decode BC3 -> BGRA32 using the existing decoder. Wrap any
            // exception so a malformed sidecar falls back to tail upscale
            // instead of crashing the whole model load.
            byte[] bgra;
            try
            {
                bgra = BlockCompressionDecoder.DecodeBC3(topBc3, topW, topH);
            }
            catch (Exception ex)
            {
                log($"  mipmaps: BC3 decode failed for top mip {topW}x{topH}: {ex.Message}");
                return null;
            }

            log($"  mipmaps: OK {Path.GetFileName(mipPath)} -> top mip {topW}x{topH} " +
                $"({levels} level(s), {fileBytes.Length} compressed bytes)");

            return new DecodedTexture
            {
                Bgra = bgra,
                Width = topW,
                Height = topH,
                FormatName = "BC3 (sidecar)",
            };
        }

        /// <summary>
        /// Locate the .mipmaps file on disk. Tries the optimistic direct
        /// path first (as in prior code), then falls back to a recursive
        /// search of the located package and the game root. Results are
        /// cached: at most one scan per unique filename per process
        /// lifetime, so a car with many driver-decal submeshes only pays
        /// the scan cost once.
        /// </summary>
        private static string TryLocateMipmapsFile(
            F1PackageLocator.Located located,
            string relDir,
            string wantedName,
            string wantedNameLiteral,
            Action<string> log)
        {
            // Cache hit -> return (including null-sentinel for confirmed miss).
            lock (_cacheLock)
            {
                if (_sidecarPathCache.TryGetValue(wantedName, out var cached))
                    return cached;
            }

            // 1. Fast path: the EAID's logical subdir under the package.
            string pkgLeaf = Path.GetFileName(located.VehiclePackageRoot);
            string subDir = StripLeadingSegment(relDir, pkgLeaf);
            string directDir = Path.Combine(
                located.VehiclePackageRoot,
                (subDir ?? "").Replace('/', Path.DirectorySeparatorChar));

            if (Directory.Exists(directDir))
            {
                string p1 = Path.Combine(directDir, wantedName);
                if (File.Exists(p1)) { CacheStore(wantedName, p1); return p1; }
                string p2 = Path.Combine(directDir, wantedNameLiteral);
                if (File.Exists(p2)) { CacheStore(wantedName, p2); return p2; }
            }

            // 2. Recursive scan of the package root.
            log($"  mipmaps: scanning {located.VehiclePackageRoot} for {wantedName}");
            string found = FindFileRecursive(
                located.VehiclePackageRoot, wantedName, wantedNameLiteral, excludeDirNames: null, log);

            // 3. If still missing, widen to the game root. Streaming
            //    textures sometimes live above the package (e.g. in a
            //    shared asset folder).
            //
            // IMPORTANT: the game root ALSO contains SIBLING vehicle_package
            // folders for prior seasons (e.g. F1 23 ships
            //   <GameRoot>\f1_2022_vehicle_package\teams\mclaren\textures\mclaren_paint_d.tif.mipmaps
            // next to the current-year package, to support F1 World mode's
            // prior-season cars). Without a guard, the broad scan returns
            // the first match and the preview ends up using last year's
            // livery. The fix: skip any subtree whose leaf name is a known
            // "*_vehicle_package" folder OTHER THAN the one the ERP lives
            // in. Files that legitimately live above the package (the
            // shared-streaming use case this step was originally added
            // for) still resolve because those paths don't go through
            // any *_vehicle_package folder at all.
            if (found == null && !string.IsNullOrEmpty(located.GameRoot))
            {
                string ownPackageLeaf = Path.GetFileName(
                    (located.VehiclePackageRoot ?? string.Empty)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                var foreignPackages = BuildForeignVehiclePackageSet(
                    ownPackageLeaf, located.GameRoot);

                log($"  mipmaps: not in package, scanning {located.GameRoot} " +
                    $"(excluding {foreignPackages.Count} foreign vehicle_package dir(s))");
                found = FindFileRecursive(
                    located.GameRoot, wantedName, wantedNameLiteral, foreignPackages, log);
            }

            if (found == null)
            {
                log($"  mipmaps: {wantedName} not found in install. High-res " +
                    $"driver texture unavailable -- falling back to tail-mip upscale.");
            }
            else
            {
                log($"  mipmaps: located at {found}");
            }

            CacheStore(wantedName, found);
            return found;
        }

        /// <summary>
        /// Build the set of "*_vehicle_package" folder names that represent
        /// SEASONS OTHER THAN the one currently being loaded. Kept in sync
        /// with GenericMipmapsSidecarLoader.BuildForeignVehiclePackageSet --
        /// both scanners need the same exclusion set because both walk the
        /// game root and both can hit sibling-season packages otherwise.
        /// </summary>
        private static HashSet<string> BuildForeignVehiclePackageSet(
            string ownPackageLeaf, string gameRoot)
        {
            // Was a HARDCODED year list (f1_2020..f1_2025). F1 25 shipped
            // f1_2026_vehicle_package (plus *_story_vehicle_package
            // variants) in the game root, none of which were on the list,
            // so a 2025 car's game-root scan could resolve a same-named
            // mipmaps file from the 2026 tree -- the "2025 Sauber renders
            // the 2026 Audi decal" bug. Now discovered dynamically by
            // PATTERN, mirroring GenericMipmapsSidecarLoader: any actual
            // folder in the game root (or one level down) that looks like
            // a vehicle package and isn't the ERP's own package is
            // excluded. Future seasons are covered automatically.
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot))
                return result;

            bool ownPackageIsVehiclePackage =
                !string.IsNullOrEmpty(ownPackageLeaf) &&
                ownPackageLeaf.EndsWith("_vehicle_package", StringComparison.OrdinalIgnoreCase) &&
                (ownPackageLeaf.StartsWith("f1_", StringComparison.OrdinalIgnoreCase) ||
                 ownPackageLeaf.StartsWith("f2_", StringComparison.OrdinalIgnoreCase));

            bool IsForeignAtlasPackageLeaf(string leaf)
            {
                if (string.IsNullOrEmpty(leaf)) return false;

                // (a) Other-season vehicle packages (incl. story variants).
                if (leaf.Length >= 20 &&
                    (leaf.StartsWith("f1_", StringComparison.OrdinalIgnoreCase) ||
                     leaf.StartsWith("f2_", StringComparison.OrdinalIgnoreCase)) &&
                    leaf.EndsWith("_vehicle_package", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // (b) Safety-car packages, but only when the caller is
                //     loading an F1/F2 vehicle (see GenericMipmapsSidecar-
                //     Loader for the full writeup).
                if (ownPackageIsVehiclePackage &&
                    (leaf.IndexOf("safetycar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     leaf.IndexOf("safety_car", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }

                return false;
            }

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

        /// <summary>
        /// Enumerate <paramref name="root"/> recursively looking for a file
        /// whose name matches either candidate (case-insensitive, filename
        /// match only -- Directory.EnumerateFiles wildcards don't always
        /// handle every filesystem quirk, so we do a manual equality check).
        /// Returns the first hit.
        ///
        /// When <paramref name="excludeDirNames"/> is non-null, any
        /// subdirectory whose leaf name matches (case-insensitive) is
        /// pruned from the walk -- the scanner won't descend into it. This
        /// is how the GameRoot-wide scan avoids sibling-season vehicle
        /// packages (see BuildForeignVehiclePackageSet above).
        /// </summary>
        private static string FindFileRecursive(
            string root, string wantedName, string wantedNameLiteral,
            HashSet<string> excludeDirNames, Action<string> log)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;

            // Manual DFS so we can prune whole subtrees -- EnumerateFiles
            // with AllDirectories doesn't let us skip a directory mid-walk.
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                string dir = stack.Pop();

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir, "*.mipmaps", SearchOption.TopDirectoryOnly);
                }
                catch (UnauthorizedAccessException ex)
                {
                    log($"  mipmaps: partial scan of {dir} (access denied: {ex.Message})");
                    continue;
                }
                catch (Exception ex)
                {
                    log($"  mipmaps: scan of {dir} failed: {ex.Message}");
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
                }

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
                _sidecarPathCache[wantedName] = resolvedPath;
            }
        }

        /// <summary>
        /// Resolution cache: wantedName (lowercase by virtue of
        /// case-insensitive comparer) -> absolute path or null for
        /// confirmed miss. Must be cleared on every Load ERP --
        /// across yearly vehicle packages (f1_2025_..., f1_2026_...)
        /// the wantedName basename for the same team often collides
        /// (e.g. "redbull_decal_da_tif.mipmaps" exists in both),
        /// so a path cached from a previous load would resolve to
        /// the wrong year's file on the next load.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, string>
            _sidecarPathCache =
                new System.Collections.Generic.Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// Drop every cached path resolution. Called by MainWindow on
        /// every Load ERP. See <see cref="_sidecarPathCache"/> comment
        /// for the cross-year collision rationale.
        /// </summary>
        public static void ResetPathCache()
        {
            lock (_cacheLock)
            {
                _sidecarPathCache.Clear();
            }
        }

        /// <summary>BC3 (16 bytes per 4x4 block) byte size for a WxH image.</summary>
        private static int Bc3ByteSize(int w, int h)
        {
            int bw = (w + 3) / 4; if (bw < 1) bw = 1;
            int bh = (h + 3) / 4; if (bh < 1) bh = 1;
            return bw * bh * 16;
        }

        /// <summary>
        /// If <paramref name="relPath"/> starts with "<paramref name="leadingSegment"/>/"
        /// (case-insensitive), return the part after it. Otherwise returns
        /// the original path. Used to translate an EAID's package-qualified
        /// relative path into a path relative to the located package root.
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
    }
}
