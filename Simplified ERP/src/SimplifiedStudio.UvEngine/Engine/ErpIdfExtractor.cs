using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace F1modding3dtoolV1
{
    /// <summary>
    /// Extracts the contents of a Codemasters ERP archive into a temp folder
    /// laid out like an on-disk vehicle package:
    ///
    ///   {tempRoot}/
    ///       teams/{team}/idf/*.idf?lod=... etc           (mesh descriptors + VB/IB payloads)
    ///       teams/{team}/idf/*.emb?context=N             (livery contexts)
    ///       teams/{team}/materials/*.material            (material bindings)
    ///       teams/{team}/textures/*.tif, *.image         (texture descriptors + BC payloads)
    ///       shared/generics/materials/*.material         (rainbeads, generics, discs, ...)
    ///       shared/generics/textures/*.image             (cf_pattern_*_d, metallic_flake_n, ...)
    ///       shared/textures/*.image                      (vfx_package textures e.g. rainbeads_large_i)
    ///
    /// The package-name segment of each EAID (e.g. "f1_2021_vehicle_package",
    /// "vfx_package") is NOT retained in the on-disk path. F1 2021 materials
    /// cross-reference assets in other logical packages, and EgoTexturePipeline's
    /// EaidPathResolver strips that prefix before concatenating with a single
    /// packageRoot. Flattening at extract time keeps every cross-package
    /// reference resolvable without touching the loader.
    ///
    /// The canonical layout is what EgoTexturePipeline + F1ModelLoader.AutoConfigureFromFolder
    /// already expect, so the existing pipeline works without modification when it is
    /// pointed at the extracted IDF subfolder.
    ///
    /// Public entry point is ExtractEmbeddedIdfToTempFolder, which keeps the same
    /// signature and semantics as the previous IDF-only extractor (returns the IDF
    /// folder path on success, empty string on failure). Callers don't need to change.
    ///
    /// Sibling ERPs (common.erp, common_sponsors_*.erp) living next to the picked
    /// ERP are automatically merged for their materials + textures, so shared
    /// resources like rainbeads/generics/sponsor decals are resolved out of the box
    /// without requiring the user to pre-extract anything.
    /// </summary>
    internal static class ErpIdfExtractor
    {
        // =====================================================================
        // PUBLIC API
        // =====================================================================

        /// <summary>
        /// Extract everything needed to render and texture the cars in an ERP.
        /// Returns the IDF folder path (suitable to hand to F1ModelLoader.LoadFolder),
        /// or empty string if no cars were found.
        /// </summary>
        public static string ExtractEmbeddedIdfToTempFolder(string erpPath)
        {
            if (string.IsNullOrWhiteSpace(erpPath) || !File.Exists(erpPath))
                return "";

            string cacheRoot = BuildCacheRoot(erpPath);

            // Cache hygiene: keep ONLY the cache for the ERP we're
            // about to open; wipe everything else. Aggressive, but
            // the most bug-resistant choice -- no stale state can
            // survive across loads, so cross-ERP contamination
            // (e.g. 2025 ERP picking up 2026 textures from a leftover
            // cache) is impossible.
            try { PruneOldCaches(keepRoots: new[] { cacheRoot }, keepCount: 0); }
            catch { /* best-effort -- never fail an extract on cleanup */ }

            string idfFolder = TryReuseCache(cacheRoot);
            if (idfFolder != null)
                return idfFolder;

            // Fresh extraction. Log to the extractor's own file so diagnosis of
            // missing materials/textures is possible without digging through the
            // shared ERP reader log.
            string logPath = Path.Combine(cacheRoot, "_extract_log.txt");
            Directory.CreateDirectory(cacheRoot);
            using var log = new TeeStreamWriter(logPath) { AutoFlush = true };
            log.WriteLine($"[{DateTime.Now:HH:mm:ss}] Full-package extract begin: {erpPath}");

            string teamIdfFolder;
            try
            {
                teamIdfFolder = ExtractOneErpFull(erpPath, cacheRoot, log);
            }
            catch (Exception ex)
            {
                log.WriteLine($"[{DateTime.Now:HH:mm:ss}] Main ERP extract failed: {ex.Message}");
                return "";
            }

            // Merge sibling ERPs (common.erp, common_track.erp, common_sponsors_*.erp,
            // and the team-specific {team}.erp if a common package container was
            // picked). We do NOT want to pull IDFs from sibling ERPs -- the car
            // geometry comes from the main picked ERP only. Sibling ERPs contribute
            // ONLY shared/generics materials and textures.
            try
            {
                MergeSiblingErps(erpPath, cacheRoot, log);
            }
            catch (Exception ex)
            {
                log.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sibling-ERP merge encountered errors: {ex.Message}");
            }

            log.WriteLine($"[{DateTime.Now:HH:mm:ss}] Full-package extract end. IDF folder: {teamIdfFolder}");

            // Mark the cache complete so future loads can skip re-extraction.
            File.WriteAllText(
                Path.Combine(cacheRoot, "_cache_ok.txt"),
                $"{erpPath}|{GetCacheKey(erpPath)}|{teamIdfFolder}");

            return teamIdfFolder ?? "";
        }

        // =====================================================================
        // CACHING
        // =====================================================================

        // BuildCacheRoot is intentionally internal so F1ModelLoaderClassic
        // can compute the same cache path this extractor will use BEFORE
        // calling ExtractEmbeddedIdfToTempFolder. The loader uses that to
        // (a) wipe the cache for forced re-extraction of Tyres.erp on every
        // classic load, and (b) point its tyre-folder discovery at the
        // freshly-populated cache root.
        internal static string BuildCacheRoot(string erpPath)
        {
            // Cache keyed by ERP path fingerprint so opening the same ERP twice in
            // a session is instant and doesn't re-decompress hundreds of sections.
            string key = GetCacheKey(erpPath);
            string name = Path.GetFileNameWithoutExtension(erpPath) + "_" + key;
            return Path.Combine(Path.GetTempPath(), "F1modding3dtoolV1", "erp_cache", name);
        }

        /// <summary>
        /// Prune the on-disk extraction cache. Folders pinned in
        /// <paramref name="keepRoots"/> always survive; otherwise we
        /// keep the <paramref name="keepCount"/> most-recent (by
        /// last-write-time) and recursively wipe the rest. Best-
        /// effort: per-folder failures (locked files, permission,
        /// etc.) are swallowed so cleanup of an unrelated stale
        /// folder can never block an extract in progress.
        /// </summary>
        private static void PruneOldCaches(string[] keepRoots, int keepCount)
        {
            string root = Path.Combine(
                Path.GetTempPath(), "F1modding3dtoolV1", "erp_cache");
            if (!Directory.Exists(root)) return;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(root); }
            catch { return; }

            var keepSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (keepRoots != null)
                foreach (var k in keepRoots)
                    if (!string.IsNullOrWhiteSpace(k)) keepSet.Add(k);

            var withTimes = new List<(string path, DateTime mtime)>();
            foreach (var d in subdirs)
            {
                DateTime mt;
                try { mt = Directory.GetLastWriteTimeUtc(d); }
                catch { mt = DateTime.MinValue; }
                withTimes.Add((d, mt));
            }
            withTimes.Sort((a, b) => b.mtime.CompareTo(a.mtime));

            int kept = 0;
            int deleted = 0;
            foreach (var (path, _) in withTimes)
            {
                if (keepSet.Contains(path)) { kept++; continue; }
                if (kept < keepCount) { kept++; continue; }
                try
                {
                    Directory.Delete(path, recursive: true);
                    deleted++;
                }
                catch { /* locked / missing -- skip and continue */ }
            }
            if (deleted > 0)
                MainWindow.LogToStatic($"Cache: cleared {deleted} old extraction folder(s).");
        }

        /// <summary>
        /// Schema version baked into the cache key. Bump this whenever
        /// extractor / merger logic changes in a way that would leave
        /// existing caches in a wrong state. Bumping makes the OLD
        /// cache key unreachable so TryReuseCache misses and the
        /// extractor runs fresh.
        /// </summary>
        private const int CacheSchemaVersion = 2;

        private static string GetCacheKey(string erpPath)
        {
            var fi = new FileInfo(erpPath);
            unchecked
            {
                long h = (long)0x9E3779B97F4A7C15UL;
                h = h * 31 + erpPath.ToLowerInvariant().GetHashCode();
                h = h * 31 + fi.Length;
                h = h * 31 + fi.LastWriteTimeUtc.Ticks;
                h = h * 31 + CacheSchemaVersion;
                return h.ToString("X16");
            }
        }

        private static string TryReuseCache(string cacheRoot)
        {
            // A cache is considered valid if _cache_ok.txt exists AND at least one
            // mesh descriptor file is present. The second check protects against
            // a partial/interrupted previous extraction leaving a misleading
            // marker behind.
            string markerPath = Path.Combine(cacheRoot, "_cache_ok.txt");
            if (!File.Exists(markerPath))
                return null;

            string recordedIdfFolder;
            try
            {
                string line = File.ReadAllText(markerPath).Trim();
                int lastPipe = line.LastIndexOf('|');
                if (lastPipe < 0) return null;
                recordedIdfFolder = line.Substring(lastPipe + 1);
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(recordedIdfFolder) || !Directory.Exists(recordedIdfFolder))
                return null;

            // Reject caches whose pickedIdfFolder was captured by the
            // pre-fix extractor (which let "shared/tyres/idf" win because
            // the per-team tyre IDFs come earlier in classic-vehicle TOCs
            // than the car's own IDFs). The new extractor always records
            // a "/teams/<team>/idf/" path when one exists. If we see a
            // recorded folder that isn't team-rooted -- but a sibling
            // "teams" directory DOES exist next to it -- the cache was
            // written by the broken codepath and we force a re-extract
            // so the marker gets rewritten with the correct folder.
            // Caches without a teams sibling (e.g. shared-only ERPs that
            // legitimately have no team folder) are kept.
            try
            {
                bool isTeamRooted = recordedIdfFolder.IndexOf(
                    Path.DirectorySeparatorChar + "teams" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isTeamRooted)
                {
                    string teamsDir = Path.Combine(cacheRoot, "teams");
                    if (Directory.Exists(teamsDir) &&
                        Directory.EnumerateDirectories(teamsDir).Any())
                    {
                        return null;
                    }
                }
            }
            catch
            {
                // Conservative: if we can't decide, fall through to the
                // mesh-presence check below rather than invalidating.
            }

            try
            {
                bool hasAnyMesh = Directory.EnumerateFiles(
                    recordedIdfFolder, "*temp000*mesh*", SearchOption.TopDirectoryOnly).Any();
                if (!hasAnyMesh)
                    return null;
            }
            catch
            {
                return null;
            }

            return recordedIdfFolder;
        }

        // =====================================================================
        // MAIN EXTRACT
        // =====================================================================

        /// <summary>
        /// Extract every recognisable resource from one ERP into the cache root.
        /// Returns the IDF folder path of the team whose geometry was extracted,
        /// or null if no cars were found.
        /// </summary>
        private static string ExtractOneErpFull(
            string erpPath, string cacheRoot, StreamWriter log)
        {
            ErpContainer container = ErpContainerReader.Read(erpPath);

            log.WriteLine($"  ERP version={container.MajorVersion}.{container.MinorVersion} " +
                          $"resources={container.Resources.Count} sections={container.TotalSections}");

            int idfWritten = 0;
            int matWritten = 0;
            int texWritten = 0;
            int taxWritten = 0;
            int skipped = 0;

            string pickedIdfFolder = null;

            // Diagnostic: first time we see a new 'skipped' EAID pattern,
            // keep a sample so the log shows the user what was dropped.
            // Bounded so a mega-ERP doesn't flood the log.
            var skippedSamples = new List<string>();
            const int MaxSkippedSamples = 30;

            foreach (var entry in container.Resources)
            {
                if (entry.Sections == null || entry.Sections.Count == 0)
                    continue;

                var kind = ClassifyEaid(entry.Name);

                try
                {
                    switch (kind)
                    {
                        case ResourceKind.Idf:
                        case ResourceKind.Emb:
                            WriteIdfOrEmb(entry, container, erpPath, cacheRoot, log);
                            idfWritten++;
                            // Pick the team's own IDF folder as the loader's
                            // starting point, preferring "/teams/<team>/idf/"
                            // over "/shared/.../idf/" paths.
                            //
                            // The previous "first IDF wins" rule worked for
                            // F1 2019/2020 STOCK ERPs where the per-team
                            // resources come early in the TOC and the
                            // ?model resource at index ~81 (mclaren.emb)
                            // is the first IDF resource encountered. It
                            // FAILED for classic-vehicle ERPs (1990 Ferrari
                            // 641 c_ferrari_90, etc. inside
                            // f1_2019_classic_vehicle_package) because
                            // those packages embed the per-team tyre IDFs
                            // (shared/tyres/idf/c_01_tyres.idf) at TOC
                            // index ~62 -- BEFORE the car's own IDFs at
                            // index ~137. Old rule captured the tyres
                            // folder as pickedIdfFolder; loader was
                            // pointed at shared/tyres/idf and never saw
                            // the car descriptors, so the viewport stayed
                            // empty even though geometry sat correctly
                            // extracted under teams/c_ferrari_90/idf.
                            //
                            // Fix: any "/teams/<team>/idf/" path replaces
                            // a non-team pickedIdfFolder; a non-team path
                            // is captured only as a fallback for ERPs that
                            // have no team folder at all.
                            string thisIdfDir = EaidToCacheDir(entry.Name, cacheRoot);
                            if (thisIdfDir != null)
                            {
                                bool thisIsTeamPath = thisIdfDir.IndexOf(
                                    Path.DirectorySeparatorChar + "teams" + Path.DirectorySeparatorChar,
                                    StringComparison.OrdinalIgnoreCase) >= 0;
                                bool currentIsTeamPath = pickedIdfFolder != null &&
                                    pickedIdfFolder.IndexOf(
                                        Path.DirectorySeparatorChar + "teams" + Path.DirectorySeparatorChar,
                                        StringComparison.OrdinalIgnoreCase) >= 0;
                                if (pickedIdfFolder == null)
                                    pickedIdfFolder = thisIdfDir;
                                else if (thisIsTeamPath && !currentIsTeamPath)
                                    pickedIdfFolder = thisIdfDir;
                            }
                            break;

                        case ResourceKind.Material:
                            WriteMaterial(entry, container, erpPath, cacheRoot, log);
                            matWritten++;
                            break;

                        case ResourceKind.Texture:
                            WriteTexture(entry, container, erpPath, cacheRoot, log);
                            texWritten++;
                            break;

                        case ResourceKind.TextureArray:
                            WriteTextureArray(entry, container, erpPath, cacheRoot, log);
                            taxWritten++;
                            break;

                        default:
                            skipped++;
                            if (skippedSamples.Count < MaxSkippedSamples)
                                skippedSamples.Add(entry.Name);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    log.WriteLine($"  FAIL {kind} '{entry.Name}': {ex.Message}");
                }
            }

            log.WriteLine($"  Wrote idf={idfWritten} material={matWritten} texture={texWritten} texarray={taxWritten} skipped={skipped}");
            log.WriteLine($"  pickedIdfFolder = {(pickedIdfFolder ?? "<null -- no IDF resources classified>")}");

            if (skippedSamples.Count > 0)
            {
                log.WriteLine($"  First {skippedSamples.Count} skipped resource(s) (by EAID):");
                foreach (var s in skippedSamples)
                    log.WriteLine($"    SKIP  {s}");
                if (skipped > skippedSamples.Count)
                    log.WriteLine($"    (+{skipped - skippedSamples.Count} more not sampled)");
            }

            return pickedIdfFolder;
        }

        // =====================================================================
        // SIBLING ERP MERGE
        // =====================================================================
        //
        // F1 2017-2021 packages split resources across multiple ERPs. A per-team
        // ERP ({team}.erp) carries the car's own meshes/materials/textures.
        // Shared materials (rainbeads, generics, discs, rear_light, ...) live in
        // common.erp. Sponsor decal atlases live in common_sponsors_{year}.erp.
        //
        // When the user picks the team ERP directly, we scan the sibling folder
        // and auto-merge the shared ERPs so those materials resolve without
        // manual extraction. We DO NOT pull IDFs from sibling ERPs -- only their
        // materials and textures. This avoids stomping on the picked car's
        // geometry with another team's meshes.

        private static void MergeSiblingErps(string pickedErpPath, string cacheRoot, StreamWriter log)
        {
            string pickedDir = Path.GetDirectoryName(pickedErpPath);
            if (string.IsNullOrEmpty(pickedDir) || !Directory.Exists(pickedDir))
                return;

            // Codemasters' F1 2017-2021 packages lay ERPs out like:
            //
            //   f1_2021_vehicle_package/
            //       teams/
            //           common.erp                       <-- shared generics (rainbeads etc)
            //           common_sponsors_2021.erp         <-- sponsor decal atlases
            //           mclaren/wep/mclaren.erp          <-- the team ERP the user picks
            //           ferrari/wep/ferrari.erp
            //           ...
            //
            // Sometimes a sibling "common*.erp" sits at the package root too.
            // The 3DSimEd plugin string table proves this convention: it loads
            //   ..\..\common.erp
            //   ..\..\common_sponsors_{year}.erp
            // relative to the IDF folder, which means "two levels up" -> the
            // teams/ folder. From the picked ERP at teams/mclaren/wep/mclaren.erp
            // that's THREE levels up. We walk up six levels and also scan the
            // one-below-root 'common' subfolder if it exists, so any reasonable
            // Codemasters layout finds its shared ERPs.
            log.WriteLine($"[{DateTime.Now:HH:mm:ss}] MergeSiblingErps: walking up from {pickedDir}");

            // F1 25 lays multiple yearly vehicle packages side by side
            // under one parent (`2025_asset_groups`):
            //   .../2025_asset_groups/f1_2024_vehicle_package/...
            //   .../2025_asset_groups/f1_2025_vehicle_package/...
            //   .../2025_asset_groups/f1_2026_vehicle_package/...
            //   .../2025_asset_groups/f2_2025_vehicle_package/...
            // Walking ABOVE the picked car's `f1_NNNN_vehicle_package`
            // root would let the sibling scan reach the parent and
            // potentially merge resources from a DIFFERENT year's
            // package -- producing the "2025 car loaded with 2026
            // textures" cross-contamination class of bug.
            //
            // The fix: clamp the walk at the package root. A path
            // segment matching `<prefix>_NNNN_<suffix>_package`
            // (where NNNN is 4 digits) is the package boundary; we
            // add it to the search list and stop. The match is loose
            // enough to cover f1, f2, classic, story, and myteam
            // variants without hard-coding a list.
            var packageBoundary = new System.Text.RegularExpressions.Regex(
                @"^[a-z][a-z0-9]*_\d{4}_.+_package$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var searchDirs = new List<string>();
            string cur = pickedDir;
            for (int hop = 0; hop < 6 && !string.IsNullOrEmpty(cur); hop++)
            {
                if (!searchDirs.Contains(cur, StringComparer.OrdinalIgnoreCase))
                {
                    searchDirs.Add(cur);
                    log.WriteLine($"  hop {hop}: scan {cur}");
                }
                string thisLeaf = Path.GetFileName(cur) ?? "";
                if (packageBoundary.IsMatch(thisLeaf))
                {
                    log.WriteLine($"  reached package root '{thisLeaf}'; " +
                                  $"stopping walk to prevent cross-year contamination.");
                    break;
                }
                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, cur, StringComparison.Ordinal))
                    break;
                cur = parent;
            }

            // Also look inside any "common" / "shared" / "generics" subfolders at
            // each hop -- some F1 installs put shared ERPs in a dedicated
            // subdirectory rather than loose next to the team folders.
            var extraDirs = new List<string>();
            foreach (var d in searchDirs)
            {
                foreach (var sub in new[] { "common", "shared", "generics", "sponsors" })
                {
                    string candidate = Path.Combine(d, sub);
                    if (Directory.Exists(candidate) && !searchDirs.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        extraDirs.Add(candidate);
                        log.WriteLine($"  extra: scan {candidate}");
                    }
                }
            }
            searchDirs.AddRange(extraDirs);

            string pickedName = Path.GetFileName(pickedErpPath);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pickedErpPath };
            int totalErpsFound = 0;
            int totalErpsMerged = 0;

            foreach (var searchDir in searchDirs)
            {
                string[] erps;
                try { erps = Directory.GetFiles(searchDir, "*.erp", SearchOption.TopDirectoryOnly); }
                catch { continue; }

                foreach (var sibling in erps)
                {
                    if (!seen.Add(sibling)) continue;
                    if (string.Equals(Path.GetFileName(sibling), pickedName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    totalErpsFound++;

                    string sname = Path.GetFileNameWithoutExtension(sibling).ToLowerInvariant();
                    bool isShared =
                        sname.StartsWith("common") ||
                        sname.Contains("sponsor") ||
                        sname.Contains("shared") ||
                        sname.Contains("generic");

                    if (!isShared)
                    {
                        log.WriteLine($"  skip (not shared): {sibling}");
                        continue;
                    }

                    log.WriteLine($"  FOUND shared ERP: {sibling}");

                    // Crash-sentinel breadcrumb. If the process is
                    // killed inside the reader (uncatchable OOM /
                    // StackOverflow / AV), the sentinel survives and
                    // the next Extract can permanently blacklist this
                    // sibling instead of crashing again.
                    // Sentinel lives in the PARENT of the per-ERP
                    // cache (the shared erp_cache/ dir), so it
                    // survives the aggressive per-ERP cache wipe.
                    string blacklistDir = Path.GetDirectoryName(cacheRoot);
                    // Use a STABLE hash of the full path -- string.GetHashCode()
                    // is per-process randomized in .NET Core 3+, so a sentinel
                    // written on run 1 wouldn't be found on run 2 (different hash
                    // per launch). MD5 is deterministic across runs.
                    string sentinelKey;
                    using (var md5 = System.Security.Cryptography.MD5.Create())
                    {
                        var bytes = md5.ComputeHash(
                            Encoding.UTF8.GetBytes(sibling.ToLowerInvariant()));
                        sentinelKey = BitConverter.ToString(bytes, 0, 4).Replace("-", "");
                    }
                    string crashSentinel = Path.Combine(
                        blacklistDir ?? cacheRoot,
                        "_merge_blacklist_" +
                        Path.GetFileNameWithoutExtension(sibling) +
                        "_" + sentinelKey +
                        ".txt");
                    if (File.Exists(crashSentinel))
                    {
                        log.WriteLine($"    BLACKLISTED (previous crash sentinel present): {crashSentinel}");
                        continue;
                    }
                    try { File.WriteAllText(crashSentinel, sibling); } catch { }

                    ErpContainer container;
                    try { container = ErpContainerReader.Read(sibling); }
                    catch (Exception ex)
                    {
                        log.WriteLine($"    could not read: {ex.Message}");
                        try { File.Delete(crashSentinel); } catch { }
                        continue;
                    }
                    log.WriteLine($"    read OK: {container.Resources.Count} resources");

                    // Guard: shared ERPs sane for our merge are small
                    // (common.erp ships ~171). If a sibling ships
                    // thousands of resources (common_fom.erp = 20873)
                    // it's a track/FOM aggregate whose material writes
                    // exhaust memory / handles before the loop finishes.
                    // Skip the merge; keep the sentinel so subsequent
                    // loads short-circuit before even reading it.
                    const int kMaxMergeableResourceCount = 5000;
                    if (container.Resources.Count > kMaxMergeableResourceCount)
                    {
                        log.WriteLine(
                            $"    SKIP merge: resource count {container.Resources.Count} " +
                            $"exceeds cap {kMaxMergeableResourceCount} (would crash the writer). " +
                            $"Sentinel kept so next load skips this ERP entirely.");
                        continue;
                    }

                    int mergedFromThis = 0;
                    foreach (var entry in container.Resources)
                    {
                        if (entry.Sections == null || entry.Sections.Count == 0)
                            continue;
                        var kind = ClassifyEaid(entry.Name);
                        if (kind != ResourceKind.Material &&
                            kind != ResourceKind.Texture &&
                            kind != ResourceKind.TextureArray)
                            continue;

                        try
                        {
                            if (kind == ResourceKind.Material)
                                WriteMaterial(entry, container, sibling, cacheRoot, log);
                            else if (kind == ResourceKind.Texture)
                                WriteTexture(entry, container, sibling, cacheRoot, log);
                            else
                                WriteTextureArray(entry, container, sibling, cacheRoot, log);
                            mergedFromThis++;
                        }
                        catch { /* best-effort; don't let one bad section stop the merge */ }
                    }

                    log.WriteLine($"    merged {mergedFromThis} material/texture/texarray resources from {Path.GetFileName(sibling)}");
                    totalErpsMerged++;
                    try { File.Delete(crashSentinel); } catch { }
                }
            }

            log.WriteLine($"[{DateTime.Now:HH:mm:ss}] MergeSiblingErps: scanned {searchDirs.Count} dir(s), " +
                          $"saw {totalErpsFound} non-picked ERP(s), merged {totalErpsMerged} shared ERP(s)");
        }

        // =====================================================================
        // EAID CLASSIFICATION + PATH ROUTING
        // =====================================================================
        //
        // EAID URIs have the shape:
        //   eaid://{packageName}/teams/{team}/idf/car.idf?lod=0&...
        //   eaid://{packageName}/teams/{team}/materials/{stem}.material
        //   eaid://{packageName}/teams/{team}/textures/{stem}.tif
        //   eaid://{packageName}/shared/generics/materials/{stem}.material
        //   eaid://{packageName}/shared/generics/textures/{stem}.tif
        //
        // We mirror that layout 1:1 under the cache root, so the existing
        // on-disk pipeline (EgoTexturePipeline + F1ModelLoader.AutoConfigureFromFolder)
        // finds the package root automatically.

        // ResourceKind enumeration. Each entry corresponds to one branch of
        // the dispatch switch in ExtractOneErpFull / MergeSiblingErps.
        //
        // TextureArray (added 2026-04-29) is the .texarray asset family: a
        // multi-slice texture container Codemasters uses for compound-keyed
        // assets like tyre diffuse maps. Each .texarray ships as a 3-resource
        // group (stub + image payload + sampler) parallel to .tif, but its
        // pixel payload holds N stacked BC slices instead of one. The
        // descriptor's element-count field (offset 0x1C in the metadata
        // section) reads >1 for these where a regular .tif reads 1.
        //
        // Routing texarrays through their own ResourceKind keeps the dispatch
        // intentional and gives the loader a clean "no texarray on disk"
        // failure path when the asset isn't bundled (vs the previous behaviour
        // where the resource was silently classified Unknown and dropped).
        private enum ResourceKind { Unknown, Idf, Emb, Material, Texture, TextureArray }

        private static ResourceKind ClassifyEaid(string eaid)
        {
            if (string.IsNullOrWhiteSpace(eaid)) return ResourceKind.Unknown;

            // IDF (mesh descriptor or VB/IB payload)
            if (eaid.IndexOf("/idf/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (eaid.IndexOf(".emb?context=", StringComparison.OrdinalIgnoreCase) >= 0)
                    return ResourceKind.Emb;

                // F1 2017-2021 IDF query shapes:
                //   {car}.idf?model                                 (whole-car model metadata)
                //   {car}.idf?render                                (whole-car render metadata)
                //   {car}.idf?lod=N&{part}&mesh                     (per-part mesh descriptor at LOD N)
                //   {car}.idf?lod=N&mesh                            (whole-car mesh at LOD N)
                //   {car}.idf?lod=all&vb=K                          (shared vertex buffer)
                //   {car}.idf?lod=all&ib=0                          (shared index buffer)
                if (eaid.IndexOf(".idf?model", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    eaid.IndexOf(".idf?render", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    eaid.IndexOf("&mesh", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    eaid.IndexOf("&vb=", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    eaid.IndexOf("&ib=", StringComparison.OrdinalIgnoreCase) >= 0)
                    return ResourceKind.Idf;

                // F1 22 added a new per-part metadata shape that does NOT
                // appear in the 2021 package:
                //   {car}.idf&{part}?model
                //   {car}.idf&{part}?render
                // i.e. the part name is attached with '&' BEFORE the role
                // query (?model / ?render), whereas 2021 placed the part name
                // inside the '?...' segment ("?lod=N&{part}&mesh").
                //
                // Measured on F1 22 mclaren.erp: 136 resources use this shape
                // (68 parts x 2 roles). The car's geometry is still in the
                // classical "?lod=N&{part}&mesh" resources -- those are
                // matched above -- but these new per-part metadata blobs are
                // worth extracting too so the on-disk layout reflects the
                // real ERP contents and downstream material/model lookups
                // that might reference them resolve.
                //
                // The detection pattern ".idf&" is specific enough that no
                // 2021 resource collides with it: every 2021 IDF query starts
                // with ".idf?", never ".idf&". Adding this branch is
                // strictly additive for 2021 ERPs.
                if (eaid.IndexOf(".idf&", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (eaid.IndexOf("?model", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     eaid.IndexOf("?render", StringComparison.OrdinalIgnoreCase) >= 0))
                    return ResourceKind.Idf;

                return ResourceKind.Unknown;
            }

            if (ContainsMaterialsOrTexturesSegment(eaid, "materials") &&
                eaid.EndsWith(".material", StringComparison.OrdinalIgnoreCase))
                return ResourceKind.Material;

            // Texture arrays. Lives under "/texture_arrays/" and uses the same
            // 3-resource decomposition as a regular .tif:
            //
            //   {stem}.texarray          stub  (1 section, descriptor placeholder)
            //   {stem}.texarray.image    image (2-3 sections: 32B metadata + slice payload + optional streaming ref)
            //   {stem}.texarray.sampler  sampler (1 section)
            //
            // The base extension is ".texarray" instead of ".tif"/".dds"/".png"
            // so IsKnownTextureTail (which keys off baseExt) misses them. We
            // route them through a parallel classifier branch -> ResourceKind.TextureArray
            // and a parallel writer (WriteTextureArray) so the on-disk filenames
            // stay distinct from regular textures.
            if (ContainsTextureArraysSegment(eaid) &&
                IsKnownTextureArrayTail(eaid))
                return ResourceKind.TextureArray;

            // Codemasters splits a single logical texture into THREE ERP resources,
            // each with its own EAID suffix:
            //   .tif          -> GfxSRVResource  (1 section: descriptor stub)
            //   .tif.image    -> GfxSurfaceRes   (2-3 sections: 32B metadata + pixel payload, sometimes a streaming ref)
            //   .tif.sampler  -> GfxSampleState  (1 section: sampler state)
            // The texture pipeline only actually reads the two sections of the
            // .tif.image resource, but we extract all three so the on-disk
            // layout exactly matches what a real package looks like when
            // extracted. An earlier version of this file only matched the
            // plain ".tif" tail, which meant every metadata + payload resource
            // was silently dropped and no textures ever rendered.
            //
            // F1 25 per-event livery note: Codemasters introduced per-livery
            // texture/material directories (e.g. "textures_silverstone_25",
            // "materials_silverstone_25") that sit alongside the canonical
            // "textures"/"materials" folders in the same team. The naming
            // convention is always "textures[_suffix]" / "materials[_suffix]",
            // never "subtextures" or "texturestreaming" or similar collisions.
            // ContainsMaterialsOrTexturesSegment handles both shapes so a
            // single classifier pass extracts both the base and the livery
            // variants from the same ERP without losing any resources.
            if (ContainsMaterialsOrTexturesSegment(eaid, "textures") &&
                IsKnownTextureTail(eaid))
                return ResourceKind.Texture;

            // Some single-image textures (slick_diffuse.tif,
            // wet_normal.tif, etc.) sit under "/texture_arrays/"
            // alongside the real .texarray files. They have a regular
            // .tif tail and are NOT multi-slice arrays. The two
            // classifier branches above only fire for ".texarray"
            // tails OR "/textures/" segments, so these resources
            // fell into ResourceKind.Unknown and were silently
            // skipped during extraction (visible symptom: tread
            // submeshes rendered solid white in the preview).
            // Treat .tif/.dds/etc. tails living under
            // "/texture_arrays/" as plain textures.
            if (ContainsTextureArraysSegment(eaid) &&
                IsKnownTextureTail(eaid))
                return ResourceKind.Texture;

            return ResourceKind.Unknown;
        }

        /// <summary>
        /// Return true if <paramref name="eaid"/> contains a path segment that
        /// starts with <paramref name="baseName"/> and is either exactly
        /// "/{baseName}/" (canonical) or "/{baseName}_..." (F1 25+ livery
        /// variant, where Codemasters appends a suffix like "_silverstone_25"
        /// to differentiate per-event liveries inside a single team folder).
        ///
        /// Example matches for baseName="textures":
        ///   /teams/mclaren/textures/...                     canonical
        ///   /teams/mclaren/textures_silverstone_25/...      livery variant
        /// Example non-matches:
        ///   /shared/texturestreaming/...                    word-boundary violated
        ///   /teams/mclaren/subtextures/...                  not a separator start
        ///
        /// The boundary check is intentionally strict: after "/{baseName}"
        /// we require either a '/' (canonical) or a '_' (variant). Any
        /// other character (letter, digit, punctuation) means we're in the
        /// middle of a longer word and must keep scanning.
        /// </summary>
        private static bool ContainsMaterialsOrTexturesSegment(string eaid, string baseName)
        {
            if (string.IsNullOrEmpty(eaid) || string.IsNullOrEmpty(baseName))
                return false;

            string needle = "/" + baseName;
            int idx = 0;
            while (true)
            {
                idx = eaid.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;

                int after = idx + needle.Length;
                if (after < eaid.Length)
                {
                    char c = eaid[after];
                    if (c == '/' || c == '_') return true;
                }
                // else: "textures" at end-of-string has no further segment,
                // so it can't be a valid resource URI — keep scanning just
                // in case there's another hit (there won't be in practice).

                idx = after;
            }
        }

        private static bool IsKnownTextureTail(string eaid)
        {
            // Accept {ext}, {ext}.image, {ext}.sampler for tif/dds/png.
            foreach (var baseExt in new[] { ".tif", ".dds", ".png" })
            {
                if (eaid.EndsWith(baseExt, StringComparison.OrdinalIgnoreCase)) return true;
                if (eaid.EndsWith(baseExt + ".image", StringComparison.OrdinalIgnoreCase)) return true;
                if (eaid.EndsWith(baseExt + ".sampler", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// True iff the EAID's path contains a "/texture_arrays/" or
        /// "/texture_arrays_..." segment. Mirrors ContainsMaterialsOrTextures
        /// Segment, but specifically for the texarray folder. We don't share
        /// that helper because callers need to distinguish "lives under
        /// texture_arrays" from "lives under textures" -- the latter is far
        /// more common and a folder named "textures_arrays" without the
        /// underscore (hypothetical) would otherwise false-positive.
        /// </summary>
        private static bool ContainsTextureArraysSegment(string eaid)
        {
            if (string.IsNullOrEmpty(eaid)) return false;
            const string needle = "/texture_arrays";
            int idx = 0;
            while (true)
            {
                idx = eaid.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;

                int after = idx + needle.Length;
                if (after < eaid.Length)
                {
                    char c = eaid[after];
                    if (c == '/' || c == '_') return true;
                }
                idx = after;
            }
        }

        /// <summary>
        /// Accept {ext}, {ext}.image, {ext}.sampler where ext = ".texarray".
        /// Symmetric with IsKnownTextureTail. Kept separate to avoid having
        /// the texture branch silently absorb texarrays (we want them to go
        /// down the texarray dispatch path so they land in the texarray
        /// on-disk folder, not the textures folder).
        /// </summary>
        private static bool IsKnownTextureArrayTail(string eaid)
        {
            const string baseExt = ".texarray";
            if (eaid.EndsWith(baseExt, StringComparison.OrdinalIgnoreCase)) return true;
            if (eaid.EndsWith(baseExt + ".image", StringComparison.OrdinalIgnoreCase)) return true;
            if (eaid.EndsWith(baseExt + ".sampler", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Convert an EAID like "eaid://pkg/teams/mclaren/idf/car.idf?lod=0..."
        /// into the cache directory the file goes in ("{cacheRoot}/teams/mclaren/idf").
        ///
        /// The package-name segment is intentionally STRIPPED. F1 2021 materials
        /// cross-reference textures across logical packages:
        ///
        ///   common.erp contains rainbeads.material (pkg = f1_2021_vehicle_package)
        ///   but that material binds its texture via
        ///     eaid://vfx_package/shared/textures/rainbeads_large_i.tif
        ///
        /// EgoTexturePipeline's EaidPathResolver strips the package prefix and
        /// concatenates the rest with a single packageRoot, so once F1ModelLoader
        /// picks {cacheRoot} as the packageRoot, every lookup of the form
        /// "shared/textures/foo.tif" works regardless of which logical package
        /// the EAID named. If we kept the package name in the path, the resolver
        /// would look under f1_2021_vehicle_package for a vfx_package file and
        /// come up empty (the symptom you saw: "Texture files missing for
        /// eaid://vfx_package/..." on every rainbeads submesh).
        ///
        /// Returns null on malformed URIs.
        /// </summary>
        private static string EaidToCacheDir(string eaid, string cacheRoot)
        {
            const string scheme = "eaid://";
            if (string.IsNullOrEmpty(eaid) || !eaid.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return null;

            int slashAfterScheme = eaid.IndexOf('/', scheme.Length);
            if (slashAfterScheme < 0) return null;

            // Skip the "eaid://{packageName}/" segment entirely -- leaving just
            // the path portion (e.g. "teams/mclaren/idf/car.idf?lod=0&..").
            string relAfterPackage = eaid.Substring(slashAfterScheme + 1);

            int q = relAfterPackage.IndexOf('?');
            if (q >= 0) relAfterPackage = relAfterPackage.Substring(0, q);

            int lastSlash = relAfterPackage.LastIndexOf('/');
            if (lastSlash < 0) return null;

            string dirPart = relAfterPackage.Substring(0, lastSlash);
            return Path.Combine(cacheRoot, dirPart.Replace('/', Path.DirectorySeparatorChar));
        }

        // =====================================================================
        // WRITERS
        // =====================================================================

        /// <summary>
        /// Write a .material resource. Codemasters materials are always a
        /// single section; name pattern is "{stem}!!!temp000.material".
        /// </summary>
        private static void WriteMaterial(
            ErpResource entry, ErpContainer container, string erpPath, string cacheRoot, StreamWriter log)
        {
            string outDir = EaidToCacheDir(entry.Name, cacheRoot);
            if (outDir == null) return;
            Directory.CreateDirectory(outDir);

            // Extract the stem + ext from the EAID ("mclaren_paint.material" -> stem="mclaren_paint").
            ExtractStemAndExt(entry.Name, out string stem, out string ext);
            if (string.IsNullOrEmpty(stem) || !string.Equals(ext, "material", StringComparison.OrdinalIgnoreCase))
                return;

            // Single-section material: write the first section as the descriptor.
            // A few materials in the wild have extra side sections (unused by us);
            // those get written as !!!temp001.material etc but the loader ignores them.
            for (int i = 0; i < entry.Sections.Count; i++)
            {
                byte[] bytes;
                try { bytes = ErpContainerReader.ReadSectionBytes(erpPath, container, entry.Sections[i]); }
                catch (Exception ex)
                {
                    log.WriteLine($"    material section read failed: {entry.Name}[{i}]: {ex.Message}");
                    continue;
                }

                string leaf = $"{stem}!!!temp{i:D3}.{ext}";
                string outPath = Path.Combine(outDir, SanitizeFileName(leaf));
                File.WriteAllBytes(outPath, bytes);
            }
        }

        /// <summary>
        /// Dispatch a texture-category resource to the right writer based on its
        /// EAID tail. Codemasters emits three resources per texture and each
        /// follows its own section-count convention:
        ///
        ///   {stem}.tif          -> 1 section  = descriptor stub
        ///   {stem}.tif.image    -> 2 sections = 32B image metadata + pixel payload
        ///   {stem}.tif.sampler  -> 1 section  = sampler state
        ///
        /// The three resources live back-to-back in the ERP TOC. We map each to
        /// the canonical on-disk filename pattern EgoTexturePipeline expects:
        ///
        ///   {stem}!!!temp000.tif          (stub, not actually read, kept for layout parity)
        ///   {stem}.tif!!!temp000.image    (metadata -- read by ImageDescriptor.FromFile)
        ///   {stem}.tif!!!temp001.image    (payload  -- read by GetBrushForTexture)
        ///   {stem}.tif!!!temp000.sampler  (sampler state, not read, kept for parity)
        ///
        /// Section index maps directly to the 'temp{N}.image' suffix for the
        /// image resource, so if a texture has a streaming-reference third
        /// section it lands at temp002.image automatically.
        /// </summary>
        private static void WriteTexture(
            ErpResource entry, ErpContainer container, string erpPath, string cacheRoot, StreamWriter log)
        {
            string outDir = EaidToCacheDir(entry.Name, cacheRoot);
            if (outDir == null) return;
            Directory.CreateDirectory(outDir);

            ExtractTextureStemAndBaseExt(entry.Name, out string stem, out string baseExt, out TextureResourceKind subKind);
            if (string.IsNullOrEmpty(stem) || string.IsNullOrEmpty(baseExt)) return;

            switch (subKind)
            {
                case TextureResourceKind.Stub:
                    WriteTextureStub(entry, container, erpPath, outDir, stem, baseExt, log);
                    break;
                case TextureResourceKind.Image:
                    WriteTextureImage(entry, container, erpPath, outDir, stem, baseExt, log);
                    break;
                case TextureResourceKind.Sampler:
                    WriteTextureSampler(entry, container, erpPath, outDir, stem, baseExt, log);
                    break;
            }
        }

        private enum TextureResourceKind { Stub, Image, Sampler }

        /// <summary>
        /// Texarray writer. Codemasters' texarray resources follow the same
        /// 3-resource decomposition as a regular .tif texture (stub + image
        /// payload + sampler), so we can reuse the texture sub-writers verbatim
        /// — they take baseExt as a parameter and lay out filenames as
        /// "{stem}.{ext}!!!temp00N.image". The only difference from WriteTexture
        /// is the dispatch source: we get here when ClassifyEaid routed the
        /// resource to ResourceKind.TextureArray rather than ResourceKind.Texture,
        /// and we want to keep that distinction visible in the extractor log
        /// (separate "texarray=" counter) so it's easy to verify a merge is
        /// pulling them down. Divergence from .tif handling, if any is ever
        /// needed (different on-disk filename convention, additional metadata
        /// emission for the slice-count, etc.), goes here in the future.
        ///
        /// Expected on-disk layout for c_03_sidewall_diffuse.texarray:
        ///
        ///   c_03_sidewall_diffuse!!!temp000.texarray          (1-section stub)
        ///   c_03_sidewall_diffuse.texarray!!!temp000.image    (32B metadata: format, width, height, mip, slice count)
        ///   c_03_sidewall_diffuse.texarray!!!temp001.image    (BC payload: N slices stacked)
        ///   c_03_sidewall_diffuse.texarray!!!temp002.image    (optional streaming/mipmaps reference)
        ///   c_03_sidewall_diffuse.texarray!!!temp000.sampler  (sampler state)
        /// </summary>
        private static void WriteTextureArray(
            ErpResource entry, ErpContainer container, string erpPath, string cacheRoot, StreamWriter log)
        {
            string outDir = EaidToCacheDir(entry.Name, cacheRoot);
            if (outDir == null) return;
            Directory.CreateDirectory(outDir);

            ExtractTextureStemAndBaseExt(entry.Name, out string stem, out string baseExt, out TextureResourceKind subKind);
            if (string.IsNullOrEmpty(stem) || string.IsNullOrEmpty(baseExt)) return;

            // Sanity check: defending against a future EAID classification
            // change that accidentally routes a non-texarray resource here.
            // If this ever fires it's a bug elsewhere, not in this writer.
            if (!string.Equals(baseExt, "texarray", StringComparison.OrdinalIgnoreCase))
            {
                log.WriteLine($"    WARN texarray writer received non-texarray ext " +
                              $"'{baseExt}' for {entry.Name}; writing anyway.");
            }

            switch (subKind)
            {
                case TextureResourceKind.Stub:
                    WriteTextureStub(entry, container, erpPath, outDir, stem, baseExt, log);
                    break;
                case TextureResourceKind.Image:
                    WriteTextureImage(entry, container, erpPath, outDir, stem, baseExt, log);
                    break;
                case TextureResourceKind.Sampler:
                    WriteTextureSampler(entry, container, erpPath, outDir, stem, baseExt, log);
                    break;
            }
        }

        /// <summary>Writes the 1-section GfxSRVResource as {stem}!!!temp000.{ext}.</summary>
        private static void WriteTextureStub(
            ErpResource entry, ErpContainer container, string erpPath,
            string outDir, string stem, string baseExt, StreamWriter log)
        {
            if (entry.Sections.Count == 0) return;
            byte[] bytes;
            try { bytes = ErpContainerReader.ReadSectionBytes(erpPath, container, entry.Sections[0]); }
            catch (Exception ex)
            {
                log.WriteLine($"    tex-stub read failed: {entry.Name}: {ex.Message}");
                return;
            }
            string leaf = $"{stem}!!!temp000.{baseExt}";
            File.WriteAllBytes(Path.Combine(outDir, SanitizeFileName(leaf)), bytes);
        }

        /// <summary>
        /// Writes the multi-section GfxSurfaceRes as {stem}.{ext}!!!temp00N.image.
        /// Section 0 is the 32-byte image metadata the decoder reads via
        /// ImageDescriptor.FromFile; section 1 is the BC-compressed pixel payload
        /// the decoder reads via File.ReadAllBytes. Any extra sections are
        /// preserved as temp002.image etc for completeness.
        /// </summary>
        private static void WriteTextureImage(
            ErpResource entry, ErpContainer container, string erpPath,
            string outDir, string stem, string baseExt, StreamWriter log)
        {
            for (int i = 0; i < entry.Sections.Count; i++)
            {
                byte[] bytes;
                try { bytes = ErpContainerReader.ReadSectionBytes(erpPath, container, entry.Sections[i]); }
                catch (Exception ex)
                {
                    log.WriteLine($"    tex-image[{i}] read failed: {entry.Name}: {ex.Message}");
                    continue;
                }
                string leaf = $"{stem}.{baseExt}!!!temp{i:D3}.image";
                File.WriteAllBytes(Path.Combine(outDir, SanitizeFileName(leaf)), bytes);
            }
        }

        /// <summary>Writes the 1-section GfxSampleState as {stem}.{ext}!!!temp000.sampler.</summary>
        private static void WriteTextureSampler(
            ErpResource entry, ErpContainer container, string erpPath,
            string outDir, string stem, string baseExt, StreamWriter log)
        {
            if (entry.Sections.Count == 0) return;
            byte[] bytes;
            try { bytes = ErpContainerReader.ReadSectionBytes(erpPath, container, entry.Sections[0]); }
            catch (Exception ex)
            {
                log.WriteLine($"    tex-sampler read failed: {entry.Name}: {ex.Message}");
                return;
            }
            string leaf = $"{stem}.{baseExt}!!!temp000.sampler";
            File.WriteAllBytes(Path.Combine(outDir, SanitizeFileName(leaf)), bytes);
        }

        /// <summary>
        /// Extract the texture's logical stem + base extension from an EAID that
        /// may have a .image or .sampler trailing segment.
        ///
        ///   "cf_pattern_1_d.tif"         -> stem="cf_pattern_1_d", ext="tif", sub=Stub
        ///   "cf_pattern_1_d.tif.image"   -> stem="cf_pattern_1_d", ext="tif", sub=Image
        ///   "cf_pattern_1_d.tif.sampler" -> stem="cf_pattern_1_d", ext="tif", sub=Sampler
        /// </summary>
        private static void ExtractTextureStemAndBaseExt(
            string eaid, out string stem, out string baseExt, out TextureResourceKind sub)
        {
            stem = ""; baseExt = ""; sub = TextureResourceKind.Stub;
            if (string.IsNullOrWhiteSpace(eaid)) return;

            int q = eaid.IndexOf('?');
            string noQuery = q >= 0 ? eaid.Substring(0, q) : eaid;

            int slash = noQuery.LastIndexOf('/');
            if (slash < 0) return;

            string file = noQuery.Substring(slash + 1);

            // Peel off the optional sub-suffix.
            if (file.EndsWith(".image", StringComparison.OrdinalIgnoreCase))
            {
                sub = TextureResourceKind.Image;
                file = file.Substring(0, file.Length - ".image".Length);
            }
            else if (file.EndsWith(".sampler", StringComparison.OrdinalIgnoreCase))
            {
                sub = TextureResourceKind.Sampler;
                file = file.Substring(0, file.Length - ".sampler".Length);
            }
            else
            {
                sub = TextureResourceKind.Stub;
            }

            int dot = file.LastIndexOf('.');
            if (dot < 0) return;
            stem = file.Substring(0, dot);
            baseExt = file.Substring(dot + 1);
        }

        /// <summary>
        /// IDF / EMB resources use the same section-index-based naming the original
        /// extractor already implemented. Kept 1:1 compatible.
        /// </summary>
        private static void WriteIdfOrEmb(
            ErpResource entry, ErpContainer container, string erpPath, string cacheRoot, StreamWriter log)
        {
            string outDir = EaidToCacheDir(entry.Name, cacheRoot);
            if (outDir == null) return;
            Directory.CreateDirectory(outDir);

            for (int si = 0; si < entry.Sections.Count; si++)
            {
                byte[] payload;
                try { payload = ErpContainerReader.ReadSectionBytes(erpPath, container, entry.Sections[si]); }
                catch (Exception ex)
                {
                    log.WriteLine($"    idf/emb section read failed: {entry.Name}[{si}]: {ex.Message}");
                    continue;
                }

                foreach (var leaf in BuildExtractedLeafNames(entry.Name, si, entry.Sections.Count))
                {
                    string outPath = Path.Combine(outDir, SanitizeFileName(leaf));
                    File.WriteAllBytes(outPath, payload);
                }
            }
        }

        // =====================================================================
        // IDF / EMB NAMING (carried over verbatim from the original extractor
        //                   so mesh loads keep working identically)
        // =====================================================================

        private static List<string> BuildExtractedLeafNames(string eaidName, int sectionIndex, int sectionCount)
        {
            var results = new List<string>();

            ExtractStemExtQuery(eaidName, out string stem, out string ext, out string query);
            if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(ext) || string.IsNullOrWhiteSpace(query))
            {
                string leaf = LeafName(eaidName);
                if (string.IsNullOrWhiteSpace(leaf)) leaf = "entry";
                leaf = leaf.Replace("?", "%3F");
                results.Add(leaf);
                return results;
            }

            string escapedQuery = query.Replace("?", "%3F");

            bool isVb = eaidName.IndexOf(".idf?lod=all&vb=", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isIb = eaidName.IndexOf(".idf?lod=all&ib=", StringComparison.OrdinalIgnoreCase) >= 0;

            if (ext.Equals("emb", StringComparison.OrdinalIgnoreCase))
            {
                results.Add($"{stem}!!!temp000.{ext}{escapedQuery}");
                return results;
            }

            if (isVb || isIb)
            {
                if (sectionCount <= 1)
                {
                    results.Add($"{stem}!!!temp001.idf{escapedQuery}");
                    return results;
                }
                if (sectionIndex == 0)
                    results.Add($"{stem}!!!temp000.idf{escapedQuery}");
                else
                    results.Add($"{stem}!!!temp001.idf{escapedQuery}");
                return results;
            }

            results.Add($"{stem}!!!temp000.idf{escapedQuery}");
            return results;
        }

        private static void ExtractStemExtQuery(string eaidName, out string stem, out string ext, out string query)
        {
            stem = ""; ext = ""; query = "";
            if (string.IsNullOrWhiteSpace(eaidName)) return;

            int idfIdx = eaidName.LastIndexOf("/idf/", StringComparison.OrdinalIgnoreCase);
            if (idfIdx < 0) return;

            string tail = eaidName.Substring(idfIdx + 5);

            int dotEmb = tail.IndexOf(".emb", StringComparison.OrdinalIgnoreCase);
            if (dotEmb >= 0)
            {
                stem = tail.Substring(0, dotEmb);
                ext = "emb";
                query = tail.Substring(dotEmb + 4);
                return;
            }

            int dotIdf = tail.IndexOf(".idf", StringComparison.OrdinalIgnoreCase);
            if (dotIdf < 0) return;

            stem = tail.Substring(0, dotIdf);
            ext = "idf";
            query = tail.Substring(dotIdf + 4);
        }

        /// <summary>
        /// Extract the filename stem + extension from any non-IDF EAID
        /// (e.g. "eaid://pkg/teams/mclaren/materials/mclaren_paint.material" ->
        /// stem="mclaren_paint", ext="material").
        /// </summary>
        private static void ExtractStemAndExt(string eaid, out string stem, out string ext)
        {
            stem = ""; ext = "";
            if (string.IsNullOrWhiteSpace(eaid)) return;

            int q = eaid.IndexOf('?');
            string noQuery = q >= 0 ? eaid.Substring(0, q) : eaid;

            int slash = noQuery.LastIndexOf('/');
            if (slash < 0) return;

            string file = noQuery.Substring(slash + 1);
            int dot = file.LastIndexOf('.');
            if (dot < 0) return;

            stem = file.Substring(0, dot);
            ext = file.Substring(dot + 1);
        }

        private static string LeafName(string eaidName)
        {
            if (string.IsNullOrWhiteSpace(eaidName)) return "";

            int idfIdx = eaidName.LastIndexOf("/idf/", StringComparison.OrdinalIgnoreCase);
            if (idfIdx >= 0) return eaidName.Substring(idfIdx + 5);

            int slash = eaidName.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < eaidName.Length)
                return eaidName.Substring(slash + 1);

            return eaidName;
        }

        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";
            foreach (char c in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');
            return fileName;
        }

        // StreamWriter that ALSO mirrors every WriteLine call to the
        // UI activity log via MainWindow.LogToStatic. Lets callers keep
        // using the same `log.WriteLine(...)` API while the user sees
        // progress live in the Activity Log expander.
        private sealed class TeeStreamWriter : StreamWriter
        {
            public TeeStreamWriter(string path)
                : base(path, append: false, Encoding.UTF8) { }

            public override void WriteLine(string value)
            {
                base.WriteLine(value);
                try { MainWindow.LogToStatic(value); } catch { }
            }

            public override void WriteLine()
            {
                base.WriteLine();
            }
        }
    }
}