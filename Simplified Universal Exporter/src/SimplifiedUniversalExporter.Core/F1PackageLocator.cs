using System;
using System.Collections.Generic;
using System.IO;

namespace SimplifiedUniversalExporter.Core
{
    /// <summary>
    /// Locates the game install root and the relevant vehicle_package folder
    /// for a picked ERP file.
    ///
    /// F1/F2 installs from 2020-2021 arrange car ERPs under one of four
    /// package roots that always live as siblings under the game root
    /// (the folder containing the .exe):
    ///
    ///   <GameRoot>\<year>_asset_groups\f1_2021_vehicle_package\teams\<team>\wep\*.erp
    ///   <GameRoot>\<year>_asset_groups\f1_2020_vehicle_package\teams\<team>\wep\*.erp
    ///   <GameRoot>\<year>_asset_groups\f2_2021_vehicle_package\teams\<team>\wep\*.erp
    ///   <GameRoot>\<year>_asset_groups\f2_2020_vehicle_package\teams\<team>\wep\*.erp
    ///
    /// The ERP itself sits deep inside one of those four package folders; we
    /// walk UP from the ERP's directory chain looking for a level whose
    /// children include AT LEAST ONE of the four known package names. That
    /// level is the game root. Having both anchors -- game root and the
    /// specific package the ERP lives in -- lets downstream code (the
    /// NUMBER-OVERLAY compositor in particular) look for shared high-res
    /// assets in the real package folder rather than in the tool's temp
    /// erp_cache, where only the tail mipmap lives.
    ///
    /// If the user loaded a stray ERP copy that isn't sitting under a real
    /// install, the walk fails cleanly (returns null) and the texture
    /// pipeline falls back to its existing tail-mip behaviour.
    /// </summary>
    internal static class F1PackageLocator
    {
        /// <summary>
        /// Set by MainWindow after a successful LocateFromErp call. Read by
        /// the texture pipeline to find high-res sidecar files (.mipmaps)
        /// in the real on-disk install. Null between loads, and null whenever
        /// the loaded ERP doesn't live inside a recognised install.
        /// </summary>
        public static Located Last { get; set; }

        /// <summary>
        /// (v12.16) Diagnostic trace from the most recent LocateFromErp call.
        /// Captures the input path, every directory-leaf the strict walk-up
        /// tested, whether each leaf matched a KnownPackages entry, and
        /// whether the permissive secondary walk (any folder whose name
        /// contains "vehicle_package") fired. Used by
        /// F1ModelLoaderClassic's package-name override helper to emit
        /// the trace into the loader's log so a Last=null result is
        /// debuggable instead of opaque.
        ///
        /// Empty string between sessions and on locator failure before
        /// the trace was populated.
        /// </summary>
        public static string LastWalkTrace
        {
            get { return _lastWalkTraceSb == null ? "" : _lastWalkTraceSb.ToString(); }
        }
        private static readonly System.Text.StringBuilder _lastWalkTraceSb =
            new System.Text.StringBuilder();

        public enum PackageKind
        {
            Unknown = 0,
            F1_2018,
            F1_2019,
            F1_2020,
            F1_2021,
            F1_2022,
            F1_2023,
            F1_2024,
            F1_2025,
            F2_2020,
            F2_2021,
            F2_2022,
            F2_2023,
            F2_2024,
            F2_2025,
            // F1 2018's classic-content asset groups. Each one bundles a
            // distinct era of historic cars; the per-ERP layout matches
            // 2018 stock (same 9-int mesh-descriptor header, same shared/
            // tyres + shared/generics decomposition). Year is 2018 for
            // all three -- they only ship in F1 2018.
            //
            //   01: late-80s / early-90s drivers (Senna, Prost...)
            //   02: late-90s drivers (c_mclaren_98 = Hakkinen MP4-13...)
            //   03: early-2000s drivers
            F1_Classic_01,
            F1_Classic_02,
            F1_Classic_03,
            // v12.08: F1 2020 ships classic cars (Benetton 94, McLaren 98,
            // Ferrari 76, etc.) under a new bare unnumbered packaging
            // "f1_classic_vehicle_package" (no _NN_ infix, no year prefix).
            // The mipmap sidecars live in the same layout as every other
            // packaging -- <package>/teams/<team>/textures/*.tif.mipmaps --
            // so adding it here lets F1PackageLocator point the high-res
            // texture pipeline at the right folder. Year is 2020 because
            // the ERP lives inside an F1 2020 install (2020_asset_groups).
            F1_Classic_2020,
            // v12.17: F1 2019 ships classic cars (1990 Ferrari, etc.) under
            // a packaging named "f1_2019_classic_vehicle_package" (with
            // the year IN the prefix, distinct from F1 2018's three
            // "f1_classic_NN_vehicle_package" era groups and F1 2020's
            // bare "f1_classic_vehicle_package"). Real install path
            // captured from the user's log:
            //   G:\SteamLibrary\steamapps\common\F1 2019\2019_asset_groups\
            //     f1_2019_classic_vehicle_package\teams\c_ferrari_90\
            //     wep\c_ferrari_90.erp
            // Year is 2019 because the ERP lives inside an F1 2019 install
            // (steamapps\common\F1 2019\2019_asset_groups). Mirrors how
            // F1_Classic_2020 above keys on its game-install year, not on
            // the car-content era year (the 1990 Ferrari is from 1990
            // chassis-history-wise, but it's shipped in an F1 2019 install
            // and uses F1 2019's per-package material/texture layout, so
            // year=2019 is the right answer for everything downstream
            // -- mipmap pipeline path, material EAID prefix, etc.).
            F1_Classic_2019,
        }

        /// <summary>
        /// Convenience: year number for a PackageKind, or 0 if Unknown.
        /// Same number for F1 and F2 of a given year (both are "2023" etc.).
        /// </summary>
        public static int GetYearForKind(PackageKind kind)
        {
            switch (kind)
            {
                case PackageKind.F1_2018: return 2018;
                case PackageKind.F1_2019: return 2019;
                case PackageKind.F1_2020:
                case PackageKind.F2_2020: return 2020;
                case PackageKind.F1_2021:
                case PackageKind.F2_2021: return 2021;
                case PackageKind.F1_2022:
                case PackageKind.F2_2022: return 2022;
                case PackageKind.F1_2023:
                case PackageKind.F2_2023: return 2023;
                case PackageKind.F1_2024:
                case PackageKind.F2_2024: return 2024;
                case PackageKind.F1_2025:
                case PackageKind.F2_2025: return 2025;
                case PackageKind.F1_Classic_01:
                case PackageKind.F1_Classic_02:
                case PackageKind.F1_Classic_03: return 2018;
                // v12.08: F1 2020's bare classic packaging. The ERPs live
                // inside an F1 2020 install (year = 2020) even though the
                // car CONTENT they ship is pre-2018 chassis. The "year"
                // returned here is the GAME year (which install layout
                // the ERP came from), not the car-era year -- consistent
                // with how F1_2020 / F2_2020 work.
                case PackageKind.F1_Classic_2020: return 2020;
                // v12.17: F1 2019's classic-content packaging. Same
                // game-year-vs-content-era reasoning as F1_Classic_2020:
                // year returned here is the GAME install year (2019),
                // not the car-era year (1990 for Ferrari 90, etc.).
                case PackageKind.F1_Classic_2019: return 2019;
                default: return 0;
            }
        }

        /// <summary>
        /// v12.14: For an F1 install of the given <paramref name="gameYear"/>,
        /// return the folder name of the classic-content vehicle package
        /// that install ships -- or null if no classic packaging is known
        /// for that year.
        ///
        /// Used by the classic mesh loader's package-name detection when
        /// a descriptor's embedded EAID doesn't contain ANY recognisable
        /// "*_vehicle_package" substring (the F1 2019 1990 Ferrari case,
        /// and similar classic-expansion content). In that situation the
        /// loader still needs SOME package name to build texture and
        /// material EAIDs that the on-disk mipmap pipeline can resolve,
        /// and the only authoritative signal it has at that point is
        /// which game install the ERP came from (cached in
        /// <see cref="Last"/>).
        ///
        /// Mapping rules:
        ///   2018-19 installs ship classic content under
        ///   "f1_classic_02_vehicle_package" (the late-90s era group;
        ///   this is the only era guaranteed-present across both years
        ///   for the cars currently in scope -- the numbered _01 and
        ///   _03 are F1-2018-exclusive era groups).
        ///   2020 installs ship classic content under the bare
        ///   "f1_classic_vehicle_package" instead.
        ///
        /// All other years (2021+) don't ship a classic-content package
        /// the tool currently targets, so this returns null and the
        /// caller keeps its existing behaviour.
        /// </summary>
        public static string GetExpectedClassicPackageForGameYear(int gameYear)
        {
            if (gameYear == 2018 || gameYear == 2019)
                return "f1_classic_02_vehicle_package";
            if (gameYear == 2020)
                return "f1_classic_vehicle_package";
            return null;
        }

        /// <summary>
        /// v12.14: For an F1 install of the given <paramref name="gameYear"/>,
        /// return the folder name of the STOCK (modern-car) vehicle package
        /// that install ships -- or null if no stock packaging is known
        /// for that year.
        ///
        /// Companion to <see cref="GetExpectedClassicPackageForGameYear"/>.
        /// Used in the same package-name-detection fallback path for
        /// modern descriptors whose embedded EAID happens to omit the
        /// "f1_NNNN_vehicle_package" substring the existing string-scan
        /// looks for. The string-scan in the classic loader's
        /// DetectPackageNameFromDescriptors only enumerates 2016-2018
        /// stock names (it predates F1 2019-2020 support being relevant
        /// to that code path), so for 2019 and 2020 stock content the
        /// fallback here supplies the right name.
        ///
        /// F1 21+ already lands in F1ModelLoader (not classic loader),
        /// so we don't need to enumerate those here -- a null return
        /// just means "let the caller's existing fallback handle it".
        /// </summary>
        public static string GetExpectedStockPackageForGameYear(int gameYear)
        {
            if (gameYear == 2018) return "f1_2018_vehicle_package";
            if (gameYear == 2019) return "f1_2019_vehicle_package";
            if (gameYear == 2020) return "f1_2020_vehicle_package";
            return null;
        }

        public sealed class Located
        {
            /// <summary>Folder that directly contains the *_vehicle_package directories (the game install root).</summary>
            public string GameRoot;
            /// <summary>Full path to the vehicle_package folder the picked ERP lives inside.</summary>
            public string VehiclePackageRoot;
            /// <summary>Which of the four known packages this is.</summary>
            public PackageKind Kind;
            /// <summary>Folder name of the package (e.g. "f1_2021_vehicle_package").</summary>
            public string PackageFolderName;
        }

        // Canonical names, ordered newest-first so that an install with
        // multiple sibling package folders (common when F1 games ship with
        // the prior season's cars alongside the current one) resolves to
        // the most recent package that actually contains the picked ERP.
        //
        // Matching is case-insensitive because Windows filesystems are.
        //
        // F1 22+ note: Codemasters keeps the "{game}_{year}_vehicle_package"
        // convention for all titles from 2020 through 2025. The per-ERP
        // resource layout is nearly identical between F1 22 and F1 23-25
        // (see ErpIdfExtractor.ClassifyEaid, which is package-name agnostic
        // and already handles both the classic "?lod=N&part&mesh" shape
        // and the F1 22-style ".idf&part?model/render" shape). That means
        // adding a new year here is a pure recognition change -- the
        // locator then correctly points the texture pipeline at the real
        // install, and downstream extraction/decoding Just Works.
        private static readonly (string Name, PackageKind Kind)[] KnownPackages = new[]
        {
            ("f1_2025_vehicle_package", PackageKind.F1_2025),
            ("f1_2024_vehicle_package", PackageKind.F1_2024),
            ("f1_2023_vehicle_package", PackageKind.F1_2023),
            ("f1_2022_vehicle_package", PackageKind.F1_2022),
            ("f1_2021_vehicle_package", PackageKind.F1_2021),
            ("f1_2020_vehicle_package", PackageKind.F1_2020),
            ("f1_2019_vehicle_package", PackageKind.F1_2019),
            ("f1_2018_vehicle_package", PackageKind.F1_2018),
            ("f2_2025_vehicle_package", PackageKind.F2_2025),
            ("f2_2024_vehicle_package", PackageKind.F2_2024),
            ("f2_2023_vehicle_package", PackageKind.F2_2023),
            ("f2_2022_vehicle_package", PackageKind.F2_2022),
            ("f2_2021_vehicle_package", PackageKind.F2_2021),
            ("f2_2020_vehicle_package", PackageKind.F2_2020),
            // F1 2018 classic content. Listed last so that a freak install
            // with a mixture of stock and classic packages resolves to the
            // most-specific stock package first; classics only match when
            // the ERP genuinely sits inside one of these folders.
            ("f1_classic_03_vehicle_package", PackageKind.F1_Classic_03),
            ("f1_classic_02_vehicle_package", PackageKind.F1_Classic_02),
            ("f1_classic_01_vehicle_package", PackageKind.F1_Classic_01),
            // v12.08: F1 2020's bare unnumbered classic packaging. Matched
            // as an exact folder-name equality against the cursor's leaf
            // during the walk-up in LocateFromErp, so it can't substring-
            // collide with the numbered F1 2018 classics above. The
            // mipmap loaders use VehiclePackageRoot directly (recursive
            // walk for .tif.mipmaps sidecars), so once the locator points
            // at this folder the high-res texture pipeline finds the
            // same layout it does for every other packaging:
            //   <package>/teams/<team>/textures/<basename>_tif.mipmaps
            //   <package>/shared/.../textures/<basename>_tif.mipmaps
            ("f1_classic_vehicle_package", PackageKind.F1_Classic_2020),
            // v12.17: F1 2019 classic-content packaging
            // ("f1_2019_classic_vehicle_package"). Verified to exist in
            // real installs from log7 walk trace:
            //   G:\SteamLibrary\steamapps\common\F1 2019\
            //     2019_asset_groups\f1_2019_classic_vehicle_package\
            //     teams\c_ferrari_90\wep\c_ferrari_90.erp
            // The legacy loader's DetectPackageNameFromDescriptors at
            // F1ModelLoaderLegacy.cs:564 already references this same
            // name as one of its known package candidates, so the name
            // is corroborated from two independent sources. Listed
            // here so the strict KnownPackages walk catches it
            // directly with Kind=F1_Classic_2019 -- previously the
            // permissive "*vehicle_package*" walk caught it but with
            // Kind=Unknown, which caused downstream year-aware code
            // (GetYearForKind, v12.14 helpers) to return 0 / fall
            // through to their default branches even though the
            // game-year was determinable from the path.
            //
            // Also adding f1_2020_classic_vehicle_package for symmetry
            // -- legacy loader's candidate list references it too, and
            // F1 2020 may ship its classic content under either the
            // bare name (already in the list above) or the year-
            // prefixed variant depending on patch level. Listed BEFORE
            // the bare 2020 form so most-specific-first precedence is
            // preserved when an unusual install ships both.
            ("f1_2019_classic_vehicle_package", PackageKind.F1_Classic_2019),
            ("f1_2020_classic_vehicle_package", PackageKind.F1_Classic_2020),
        };

        /// <summary>
        /// Given the path of a picked ERP (e.g. ...\teams\mclaren\wep\mclaren.erp),
        /// walk up the directory chain until the current folder contains one
        /// of the known *_vehicle_package subfolders. Returns that folder as
        /// GameRoot and the specific package the ERP lives in as
        /// VehiclePackageRoot. Returns null if the ERP is not inside a
        /// recognisable install layout.
        /// </summary>
        public static Located LocateFromErp(string erpPath)
        {
            // v12.16: capture diagnostic walk trace.  Cleared on every
            // call so the trace reflects the most recent invocation.
            _lastWalkTraceSb.Clear();
            _lastWalkTraceSb.AppendLine($"LocateFromErp called with: '{erpPath ?? "<null>"}'");

            if (string.IsNullOrWhiteSpace(erpPath))
            {
                _lastWalkTraceSb.AppendLine("  -> returning null: path is null/whitespace");
                return null;
            }
            string fullErp;
            try { fullErp = Path.GetFullPath(erpPath); }
            catch (Exception ex)
            {
                _lastWalkTraceSb.AppendLine($"  -> returning null: Path.GetFullPath threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            _lastWalkTraceSb.AppendLine($"  fullErp: '{fullErp}'");

            if (!File.Exists(fullErp))
            {
                _lastWalkTraceSb.AppendLine("  -> returning null: File.Exists(fullErp) is false (picked file not present on disk?)");
                return null;
            }

            // 1. Figure out which package the ERP sits inside by scanning the
            //    ERP's own directory chain for a folder name that matches.
            //    This is the "which package is the ERP in" question; the
            //    walk-up below answers the "where does this package live"
            //    question, and the two combine to give the full answer.
            string erpDir = Path.GetDirectoryName(fullErp);
            _lastWalkTraceSb.AppendLine($"  erpDir: '{erpDir ?? "<null>"}'");
            string matchedPackageName = null;
            PackageKind matchedKind = PackageKind.Unknown;
            string packageAbsPath = null;

            string cursor = erpDir;
            _lastWalkTraceSb.AppendLine("  --- strict walk-up against KnownPackages (exact name, case-insensitive) ---");
            while (!string.IsNullOrEmpty(cursor))
            {
                string leaf = Path.GetFileName(cursor);
                _lastWalkTraceSb.AppendLine($"    testing leaf='{leaf}'  (full='{cursor}')");
                foreach (var (name, kind) in KnownPackages)
                {
                    if (string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedPackageName = name;
                        matchedKind = kind;
                        packageAbsPath = cursor;
                        break;
                    }
                }
                if (matchedPackageName != null)
                {
                    _lastWalkTraceSb.AppendLine($"    STRICT MATCH: '{matchedPackageName}' (kind={matchedKind})");
                    break;
                }
                cursor = Path.GetDirectoryName(cursor);
            }

            if (matchedPackageName == null)
            {
                _lastWalkTraceSb.AppendLine("  strict walk-up completed without a KnownPackages match.");

                // v12.16 permissive fallback: any folder leaf containing
                // the substring "vehicle_package" (case-insensitive) is
                // treated as a valid package folder even when it isn't
                // in the KnownPackages whitelist. This catches install
                // layouts the hardcoded list doesn't know -- the most
                // important case being F1 2019's classic-expansion
                // packaging, which uses a folder name the original
                // whitelist (designed for F1 2018/2020/2021+ stock plus
                // F1 2018/2020 classics) doesn't enumerate.
                //
                // PackageKind is Unknown for these matches because we
                // can't infer the game year without a name match;
                // downstream code (GetYearForKind) gracefully returns
                // 0 for Unknown, and the year-aware v12.14 path falls
                // through to the existing hardcoded behaviour in that
                // case. What matters for the v12.15 override is just
                // having a non-null PackageFolderName -- the texture
                // pipeline then resolves EAIDs against the real on-
                // disk VehiclePackageRoot, which is the actual path
                // we found here.
                //
                // The "vehicle_package" substring is specific enough
                // that random user directories will not false-positive
                // (it does not appear outside F1 / Codemasters folder
                // layouts in practice).
                _lastWalkTraceSb.AppendLine("  --- permissive walk-up (any leaf containing 'vehicle_package' substring) ---");
                cursor = erpDir;
                while (!string.IsNullOrEmpty(cursor))
                {
                    string leaf = Path.GetFileName(cursor);
                    if (!string.IsNullOrEmpty(leaf) &&
                        leaf.IndexOf("vehicle_package", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // v12.17: scan the full ERP path for year markers
                        // ("F1 2019", "F1 2020", "2019_asset_groups",
                        // year-prefixed package leaves, etc.) so the
                        // permissive match returns a meaningful Kind
                        // instead of Unknown when the year is determinable
                        // from the install path. This is purely additive
                        // -- when no year marker is found, the helper
                        // returns Unknown and behaviour matches the
                        // pre-v12.17 permissive path verbatim.
                        PackageKind kindFromPath = InferKindFromInstallPath(fullErp, leaf);
                        _lastWalkTraceSb.AppendLine($"    PERMISSIVE MATCH: leaf='{leaf}' contains 'vehicle_package'; using as PackageFolderName (Kind={kindFromPath}, inferred from full path scan).");
                        return new Located
                        {
                            GameRoot = null,
                            VehiclePackageRoot = cursor,
                            Kind = kindFromPath,
                            PackageFolderName = leaf,
                        };
                    }
                    _lastWalkTraceSb.AppendLine($"    testing leaf='{leaf}'  -> no 'vehicle_package' substring");
                    cursor = Path.GetDirectoryName(cursor);
                }

                _lastWalkTraceSb.AppendLine("  -> returning null: neither strict nor permissive walk matched. ERP appears to be a loose copy or sits in a layout with no '*vehicle_package*' folder anywhere in its parent chain.");
                // ERP lives outside any recognised package -- probably a
                // loose copy. Give up cleanly.
                return null;
            }

            // 2. Walk up looking for the game install root -- the folder
            //    that contains the .exe, per the user's instructions.
            //
            //    Two supported layouts:
            //       a) CANONICAL (F1 2021 etc):
            //          <GameRoot>\<year>_asset_groups\<package>\...
            //          -- game root is the GRANDPARENT of the package.
            //       b) FLAT (older/simpler installs):
            //          <GameRoot>\<package>\...
            //          -- game root is the direct PARENT of the package.
            //
            //    The discriminator: a "*_asset_groups" folder is never the
            //    game root. Its only job is to group one or more packages,
            //    and the game root sits above it. So the rule is:
            //       - If parent(package) has a name ending "_asset_groups"
            //         -> game root is one level up from that.
            //       - Otherwise -> game root is parent(package) itself.
            //
            //    We then sanity-check the candidate by verifying it really
            //    does contain at least one of the four known packages
            //    (either directly or via an asset_groups subfolder). If
            //    the sanity check fails, we keep walking up a few more
            //    levels as a last resort before giving up.
            string parent = Path.GetDirectoryName(packageAbsPath);
            string candidate = null;
            if (!string.IsNullOrEmpty(parent))
            {
                string parentLeaf = Path.GetFileName(parent) ?? "";
                candidate = IsAssetGroupsLeaf(parentLeaf)
                    ? Path.GetDirectoryName(parent)
                    : parent;
            }

            if (!string.IsNullOrEmpty(candidate) && LooksLikeGameRoot(candidate))
            {
                _lastWalkTraceSb.AppendLine($"  -> STRICT-MATCH SUCCESS with game root '{candidate}'.");
                return new Located
                {
                    GameRoot = candidate,
                    VehiclePackageRoot = packageAbsPath,
                    Kind = matchedKind,
                    PackageFolderName = matchedPackageName,
                };
            }

            // Fallback: walk up a few extra levels in case the layout has an
            // unexpected extra folder. Bail after a bounded number of steps
            // to avoid wandering up to the drive root pointlessly.
            string probe = candidate;
            for (int steps = 0; steps < 4 && !string.IsNullOrEmpty(probe); steps++)
            {
                if (LooksLikeGameRoot(probe))
                {
                    _lastWalkTraceSb.AppendLine($"  -> STRICT-MATCH SUCCESS with deeper game root '{probe}' (probed {steps + 1} level(s) up).");
                    return new Located
                    {
                        GameRoot = probe,
                        VehiclePackageRoot = packageAbsPath,
                        Kind = matchedKind,
                        PackageFolderName = matchedPackageName,
                    };
                }
                probe = Path.GetDirectoryName(probe);
            }

            // We found the package the ERP lives in but couldn't identify a
            // game root above it. Return what we have -- the package path is
            // the important anchor for finding high-res assets, and
            // GameRoot = null tells callers the install layout is odd.
            _lastWalkTraceSb.AppendLine($"  -> STRICT-MATCH PARTIAL: found package '{matchedPackageName}' but no game-root sanity-check passed. GameRoot=null.");
            return new Located
            {
                GameRoot = null,
                VehiclePackageRoot = packageAbsPath,
                Kind = matchedKind,
                PackageFolderName = matchedPackageName,
            };
        }

        /// <summary>
        /// A folder "looks like" the game install root if it contains at
        /// least one of the four known packages, either directly (flat
        /// layout) or via a "*_asset_groups" subfolder (canonical layout).
        /// A "*_asset_groups" folder is itself NEVER the game root even if
        /// it contains a package directly, because that's an intermediate
        /// grouping folder, not the install root.
        /// </summary>
        private static bool LooksLikeGameRoot(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            string leaf = Path.GetFileName(dir) ?? "";
            if (IsAssetGroupsLeaf(leaf))
                return false;

            return DirectoryContainsAnyKnownPackageDirectly(dir)
                || DirectoryContainsAssetGroupsWithKnownPackage(dir);
        }

        private static bool DirectoryContainsAnyKnownPackageDirectly(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            foreach (var (name, _) in KnownPackages)
            {
                if (Directory.Exists(Path.Combine(dir, name))) return true;
            }
            return false;
        }

        private static bool DirectoryContainsAssetGroupsWithKnownPackage(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            IEnumerable<string> children;
            // We enumerate ALL subdirectories here rather than using the
            // glob pattern "*_asset_groups" because F1 2018 installs the
            // package under a plain "asset_groups" folder (no year prefix,
            // no leading underscore) -- the glob would miss it. The
            // IsAssetGroupsLeaf check below handles both shapes.
            try { children = Directory.EnumerateDirectories(dir); }
            catch { return false; }
            foreach (string child in children)
            {
                string leaf = Path.GetFileName(child) ?? "";
                if (!IsAssetGroupsLeaf(leaf)) continue;
                if (DirectoryContainsAnyKnownPackageDirectly(child)) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true for any folder whose leaf name identifies it as an
        /// "asset groups" container -- the intermediate grouping folder
        /// that sits between the game install root and the individual
        /// *_vehicle_package folders.
        ///
        /// F1 2020+ uses year-prefixed names ("2021_asset_groups",
        /// "2023_asset_groups", etc.), which match the EndsWith branch.
        /// F1 2018 uses a plain "asset_groups" folder (no year prefix),
        /// which is why the case-insensitive exact-match branch is
        /// required.
        /// </summary>
        private static bool IsAssetGroupsLeaf(string leaf)
        {
            if (string.IsNullOrEmpty(leaf)) return false;
            return leaf.Equals("asset_groups", StringComparison.OrdinalIgnoreCase)
                || leaf.EndsWith("_asset_groups", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// (v12.17) Path-based game-year detector used as a Kind fallback by
        /// the permissive walk inside <see cref="LocateFromErp"/>. Looks at
        /// the FULL on-disk path of the picked ERP and the matched
        /// package-folder leaf to decide which year's content the install
        /// represents. Returns a year-specific <see cref="PackageKind"/>
        /// when any of the markers below appear; falls back to
        /// <see cref="PackageKind.Unknown"/> when nothing matches.
        ///
        /// Markers checked, ordered most-specific-first:
        ///   - Year-prefixed package leaf ("f1_2019_classic_vehicle_package",
        ///     "f1_2020_classic_vehicle_package", etc.) -> classic kind
        ///     for that year.
        ///   - Stock year-prefixed leaf ("f1_2019_vehicle_package", etc.)
        ///     -> stock kind for that year.
        ///   - Asset-groups folder name ("2019_asset_groups",
        ///     "2020_asset_groups", etc.) anywhere in the path -> classic
        ///     when the leaf contains "classic", stock otherwise.
        ///   - Install root folder name ("F1 2019", "F1 2020", etc.) any-
        ///     where in the path -> classic when the leaf contains
        ///     "classic", stock otherwise. Handles both space ("F1 2019")
        ///     and underscore ("F1_2019") variants because both appear in
        ///     practice (Steam vs Epic vs MS Store install layouts).
        ///
        /// Year-detection scope: 2018-2025 (matches the years
        /// <see cref="GetYearForKind"/> covers). Restricting to that
        /// range is purely about not having an enum value to return for
        /// years outside it -- the marker scan itself would be happy to
        /// hit "F1 2017" / "F1 2016", but there's nowhere to put the
        /// result, so we don't bother.
        ///
        /// Pure read; no state changes. Walk-trace appends are made so
        /// the diagnostic LastWalkTrace shows which marker matched.
        /// </summary>
        private static PackageKind InferKindFromInstallPath(string fullErpPath, string matchedLeaf)
        {
            if (string.IsNullOrEmpty(fullErpPath)) return PackageKind.Unknown;

            string leaf = matchedLeaf ?? "";
            bool leafIsClassic = leaf.IndexOf("classic", StringComparison.OrdinalIgnoreCase) >= 0;

            // 1. Year-prefixed package leaf -- highest signal.
            //    "f1_2019_classic_vehicle_package" / "f1_2019_vehicle_package".
            for (int y = 2018; y <= 2025; y++)
            {
                string yearStockName = "f1_" + y + "_vehicle_package";
                string yearClassicName = "f1_" + y + "_classic_vehicle_package";

                if (leaf.Equals(yearClassicName, StringComparison.OrdinalIgnoreCase))
                {
                    _lastWalkTraceSb.AppendLine($"    InferKindFromInstallPath: leaf='{leaf}' exact-matches '{yearClassicName}'.");
                    return ClassicKindForYear(y);
                }
                if (leaf.Equals(yearStockName, StringComparison.OrdinalIgnoreCase))
                {
                    _lastWalkTraceSb.AppendLine($"    InferKindFromInstallPath: leaf='{leaf}' exact-matches '{yearStockName}'.");
                    return StockKindForYear(y);
                }
            }

            // 2. Asset-groups folder name anywhere in the path.
            //    "2019_asset_groups" / "2020_asset_groups" / ...
            for (int y = 2018; y <= 2025; y++)
            {
                string assetGroupsMarker = y + "_asset_groups";
                if (fullErpPath.IndexOf(assetGroupsMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    PackageKind k = leafIsClassic ? ClassicKindForYear(y) : StockKindForYear(y);
                    _lastWalkTraceSb.AppendLine($"    InferKindFromInstallPath: path contains '{assetGroupsMarker}'; leafIsClassic={leafIsClassic} -> {k}.");
                    return k;
                }
            }

            // 3. Install-root folder name "F1 YYYY" / "F1_YYYY" /
            //    "F1YYYY" anywhere in the path.
            for (int y = 2018; y <= 2025; y++)
            {
                string[] installMarkers =
                {
                    "F1 " + y,
                    "F1_" + y,
                    "F1" + y,
                };
                foreach (var marker in installMarkers)
                {
                    if (fullErpPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        PackageKind k = leafIsClassic ? ClassicKindForYear(y) : StockKindForYear(y);
                        _lastWalkTraceSb.AppendLine($"    InferKindFromInstallPath: path contains install marker '{marker}'; leafIsClassic={leafIsClassic} -> {k}.");
                        return k;
                    }
                }
            }

            _lastWalkTraceSb.AppendLine("    InferKindFromInstallPath: no year marker found in path or leaf; returning Unknown.");
            return PackageKind.Unknown;
        }

        /// <summary>
        /// (v12.17) Map a stock game year to its corresponding
        /// <see cref="PackageKind"/>. Returns
        /// <see cref="PackageKind.Unknown"/> for years without a stock
        /// enum value (covers 2018-2025; classic-only would be the only
        /// case we don't have but the caller only invokes this when it
        /// has already decided the leaf is non-classic, so that case
        /// shouldn't occur in practice).
        /// </summary>
        private static PackageKind StockKindForYear(int year)
        {
            switch (year)
            {
                case 2018: return PackageKind.F1_2018;
                case 2019: return PackageKind.F1_2019;
                case 2020: return PackageKind.F1_2020;
                case 2021: return PackageKind.F1_2021;
                case 2022: return PackageKind.F1_2022;
                case 2023: return PackageKind.F1_2023;
                case 2024: return PackageKind.F1_2024;
                case 2025: return PackageKind.F1_2025;
                default: return PackageKind.Unknown;
            }
        }

        /// <summary>
        /// (v12.17) Map a game year to its corresponding classic-content
        /// <see cref="PackageKind"/>. Returns
        /// <see cref="PackageKind.Unknown"/> for years that don't ship
        /// a classic-content package the tool currently targets.
        /// 2018 maps to F1_Classic_02 (the late-90s era group, the only
        /// one with broad coverage; the numbered _01/_03 are era-
        /// specific and not useful as a default).
        /// </summary>
        private static PackageKind ClassicKindForYear(int year)
        {
            switch (year)
            {
                case 2018: return PackageKind.F1_Classic_02;
                case 2019: return PackageKind.F1_Classic_2019;
                case 2020: return PackageKind.F1_Classic_2020;
                default: return PackageKind.Unknown;
            }
        }
    }
}