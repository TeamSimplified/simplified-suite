using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace F1modding3dtoolV1
{
    /// <summary>
    /// Detects the F1 game year (2017-2026) and, for classics, the car year and team
    /// for a given ERP file.
    ///
    /// Why this is non-trivial: classic cars are largely shared between games. The
    /// 1976 Ferrari 312 T2 ships in F1 2018 AND F1 2019 with very similar assets, so
    /// "find a classic Ferrari from 1976" is not enough to identify which game it
    /// came from. We combine four signals and let weighted voting pick a winner:
    ///
    ///   1. Path scan       - "F1 2018" install folder, "c_mclaren_98" team folder,
    ///                        "f1_classic_02_vehicle_package" markers, and any
    ///                        "f1_20XX_vehicle_package" modern-car package.
    ///   2. ERP content     - the same f1_20XX / f1_classic_0X tokens, scanned in
    ///                        the file's ASCII bytes (catches references embedded
    ///                        inside a classic ERP that point at a modern package).
    ///   3. Roster narrow   - the (team, carYear) tuple is matched against per-game
    ///                        rosters. Some cars are exclusive (e.g. the Schumacher
    ///                        DLC quartet only exists in F1 2020), giving a hard
    ///                        answer; others narrow the search to 2-3 games.
    ///   4. Resolution      - highest weighted vote wins; confidence reflects the
    ///                        score gap and roster agreement.
    ///
    /// Backward compatibility: DetectFromErp(string) -&gt; int still returns the
    /// game year (or 0). New callers should prefer Detect(string) for the full
    /// breakdown including reasons.
    /// </summary>
    internal static class F1YearDetector
    {
        // ---------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------

        /// <summary>
        /// Backward-compatible entry point. Returns the detected GAME year, or 0.
        /// </summary>
        public static int DetectFromErp(string erpPath)
        {
            return Detect(erpPath).GameYear;
        }

        /// <summary>
        /// Full detection: game year, car year (if classic), team, classic package,
        /// ERP version, plus a reason trail for logging.
        /// </summary>
        public static F1DetectionResult Detect(string erpPath)
        {
            var result = new F1DetectionResult();

            if (string.IsNullOrWhiteSpace(erpPath))
            {
                result.AddReason("Empty path supplied");
                return result;
            }

            // Stage 1: path-based signals (no I/O required).
            ScanPath(erpPath, result);

            // Stage 2: ERP content scan (binary-as-ASCII token search).
            if (File.Exists(erpPath))
                ScanErpContents(erpPath, result);
            else
                result.AddReason("File does not exist on disk; using path-only signals");

            // Stage 3: narrow possible games via the per-game rosters.
            NarrowGameYearByRoster(result);

            // Stage 4: pick the winning game year and assign confidence.
            ResolveGameYear(result);

            return result;
        }

        // ---------------------------------------------------------------------
        // Regex patterns (compiled once)
        // ---------------------------------------------------------------------

        // Classic-car team folder, e.g. "c_mclaren_98" or "c_red_bull_10".
        // Greedy team capture so multi-word teams work, with the trailing
        // 2-digit year anchored at end of segment.
        private static readonly Regex ClassicTeamFolderRegex =
            new Regex(@"(?:^|[\\/])c_(?<team>[a-z][a-z0-9_]*)_(?<year>\d{2})(?=[\\/]|$|\.)",
                      RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Same shape as above but for matching inside ASCII blobs from the ERP,
        // where path separators are forward slashes (eaid:// URIs).
        private static readonly Regex ClassicTeamRefRegex =
            new Regex(@"/c_(?<team>[a-z][a-z0-9_]*)_(?<year>\d{2})(?=[/.])",
                      RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ClassicPackageRegex =
            new Regex(@"f1_classic_(?<num>\d{2})_vehicle_package",
                      RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex InstallFolderRegex =
            new Regex(@"[\\/]F1[ _](?<year>20\d{2})[\\/]",
                      RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ModernPackageRegex =
            new Regex(@"f1_(?<year>20\d{2})_vehicle_package",
                      RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // F1 2019 and F1 2020 ship pre-2018-chassis "classic" cars
        // (1990 Ferrari 641, 1988 McLaren MP4/4, etc.) inside their own
        // dedicated game-year-specific classic packages:
        //   f1_2019_classic_vehicle_package
        //   f1_2020_classic_vehicle_package
        // These are NOT the F1 2018-style numeric classic_NN packages
        // (which ClassicPackageRegex handles). The contents use the
        // 20-VB Pool A/B/C/D modern-2019 layout, so they load through
        // F1ModelLoaderLegacy -- but only if the year detector pins
        // them to F1 2019/2020 and the loader's package-name detection
        // builds eaid:// strings against the correct prefix.
        private static readonly Regex ModernClassicPackageRegex =
            new Regex(@"f1_(?<year>20\d{2})_classic_vehicle_package",
                      RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ---------------------------------------------------------------------
        // Stage 1: path scanning
        // ---------------------------------------------------------------------

        private static void ScanPath(string erpPath, F1DetectionResult r)
        {
            // "F1 2018" install folder is the strongest game-year signal.
            var instMatch = InstallFolderRegex.Match(erpPath);
            if (instMatch.Success && int.TryParse(instMatch.Groups["year"].Value, out int gy))
            {
                r.AddGameYearVote(gy, weight: 4, source: $"install folder 'F1 {gy}' in path");
            }

            // Classic team folder gives us team + 2-digit car year.
            var teamMatch = ClassicTeamFolderRegex.Match(erpPath);
            if (teamMatch.Success)
            {
                r.IsClassic = true;
                string rawTeam = teamMatch.Groups["team"].Value.ToLowerInvariant();
                string rawYear = teamMatch.Groups["year"].Value;
                r.Team = NormalizeTeam(rawTeam);
                r.CarKey = "c_" + rawTeam + "_" + rawYear;
                r.CarYear = TwoDigitToFullYear(int.Parse(rawYear));
                r.AddReason($"Path team folder: '{r.CarKey}' -> team='{r.Team}', carYear={r.CarYear}");
            }

            // Classic package marker (classic_01 = pre-1988, classic_02 = 1988+ in F1 2018).
            var pkgMatch = ClassicPackageRegex.Match(erpPath);
            if (pkgMatch.Success)
            {
                r.IsClassic = true;
                r.ClassicPackage = "classic_" + pkgMatch.Groups["num"].Value;
                r.AddReason($"Path package: f1_{r.ClassicPackage}_vehicle_package");
            }

            // Modern-classic path marker (F1 2019/2020 classic-DLC pkgs).
            // Sets IsClassic and votes the embedded year. Match this BEFORE
            // ModernPackageRegex below: ModernPackageRegex would otherwise
            // be tempted to vote here too, but its own regex requires the
            // exact "f1_<year>_vehicle_package" tail without a "_classic"
            // segment, so it won't double-fire on the same substring. Kept
            // separate so the vote source is clearly attributed.
            var modClassicMatch = ModernClassicPackageRegex.Match(erpPath);
            if (modClassicMatch.Success && int.TryParse(modClassicMatch.Groups["year"].Value, out int mcy))
            {
                r.IsClassic = true;
                r.AddGameYearVote(mcy, weight: 5,
                    source: $"classic-vehicle package in path 'f1_{mcy}_classic_vehicle_package'");
                r.AddReason($"Path package: f1_{mcy}_classic_vehicle_package");
            }

            // Modern car package directly in path.
            var modMatch = ModernPackageRegex.Match(erpPath);
            if (modMatch.Success && int.TryParse(modMatch.Groups["year"].Value, out int my))
            {
                r.AddGameYearVote(my, weight: 5,
                    source: $"modern package in path 'f1_{my}_vehicle_package'");
                if (r.CarYear == 0) r.CarYear = my;
            }
        }

        // ---------------------------------------------------------------------
        // Stage 2: ERP content scanning
        // ---------------------------------------------------------------------

        private static void ScanErpContents(string erpPath, F1DetectionResult r)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(erpPath);
            }
            catch (Exception ex)
            {
                r.AddReason("Unable to read ERP bytes: " + ex.Message);
                return;
            }
            if (data.Length < 16) return;

            // ERP header: magic "ERPK" + version major/minor (uint32 each).
            try
            {
                if (BitConverter.ToUInt32(data, 0) == 0x4B505245u)
                {
                    int verMajor = BitConverter.ToInt32(data, 4);
                    int verMinor = BitConverter.ToInt32(data, 8);
                    r.ErpVersion = $"{verMajor}.{verMinor}";
                    r.AddReason($"ERP header: magic OK, version {r.ErpVersion}");
                }
                else
                {
                    r.AddReason("ERP header: magic mismatch (file may be corrupt or not an ERP)");
                }
            }
            catch
            {
                // Header read failure isn't fatal, fall through to ASCII scan.
            }

            string ascii = Encoding.ASCII.GetString(data);

            // f1_20XX_classic_vehicle_package tokens: the 2019/2020-era
            // classic-DLC packages. Run this BEFORE the ModernPackageRegex
            // sweep so we credit the right vote source. Both regexes are
            // text-shape-distinct so they can't double-count the same
            // substring (the modern one demands "_vehicle_package" right
            // after the year, with no intermediate "_classic" segment).
            var modernClassicCounts = new Dictionary<int, int>();
            foreach (Match m in ModernClassicPackageRegex.Matches(ascii))
            {
                if (int.TryParse(m.Groups["year"].Value, out int y))
                {
                    if (modernClassicCounts.ContainsKey(y)) modernClassicCounts[y]++;
                    else modernClassicCounts[y] = 1;
                }
            }
            foreach (var kv in modernClassicCounts)
            {
                r.IsClassic = true;
                r.AddGameYearVote(kv.Key, weight: 3,
                    source: $"ERP token f1_{kv.Key}_classic_vehicle_package x{kv.Value}");
            }

            // f1_20XX_vehicle_package tokens: most reliable for current-season cars,
            // but classic ERPs sometimes reference them too via cross-package eaid URIs.
            var modernCounts = new Dictionary<int, int>();
            foreach (Match m in ModernPackageRegex.Matches(ascii))
            {
                if (int.TryParse(m.Groups["year"].Value, out int y))
                {
                    if (modernCounts.ContainsKey(y)) modernCounts[y]++;
                    else modernCounts[y] = 1;
                }
            }
            foreach (var kv in modernCounts)
            {
                r.AddGameYearVote(kv.Key, weight: 3,
                    source: $"ERP token f1_{kv.Key}_vehicle_package x{kv.Value}");
            }

            // Classic package tokens (just confirms classic; doesn't pin game year).
            foreach (Match m in ClassicPackageRegex.Matches(ascii))
            {
                r.IsClassic = true;
                if (string.IsNullOrEmpty(r.ClassicPackage))
                {
                    r.ClassicPackage = "classic_" + m.Groups["num"].Value;
                    r.AddReason($"ERP token: f1_{r.ClassicPackage}_vehicle_package");
                }
            }

            // If path didn't yield a team/car year, harvest from ERP content.
            if (string.IsNullOrEmpty(r.Team))
            {
                var teamMatch = ClassicTeamRefRegex.Match(ascii);
                if (teamMatch.Success)
                {
                    r.IsClassic = true;
                    string rawTeam = teamMatch.Groups["team"].Value.ToLowerInvariant();
                    string rawYear = teamMatch.Groups["year"].Value;
                    r.Team = NormalizeTeam(rawTeam);
                    r.CarKey = "c_" + rawTeam + "_" + rawYear;
                    if (r.CarYear == 0)
                        r.CarYear = TwoDigitToFullYear(int.Parse(rawYear));
                    r.AddReason($"ERP token team ref: '{r.CarKey}' -> team='{r.Team}', carYear={r.CarYear}");
                }
            }
        }

        // ---------------------------------------------------------------------
        // Stage 3: roster narrowing
        // ---------------------------------------------------------------------

        private static void NarrowGameYearByRoster(F1DetectionResult r)
        {
            if (!r.IsClassic || string.IsNullOrEmpty(r.Team) || r.CarYear == 0)
                return;

            var key = (r.Team, r.CarYear);
            if (CarToGames.TryGetValue(key, out var games))
            {
                r.PossibleGameYears = games.ToArray();
                r.RosterValid = true;
                r.AddReason(
                    $"Roster: ({r.Team}, {r.CarYear}) appears in F1 [{string.Join(", ", games)}]");

                if (games.Count == 1)
                {
                    // Exclusive car -> hard answer.
                    r.AddGameYearVote(games[0], weight: 5,
                        source: $"car ({r.Team} {r.CarYear}) is exclusive to F1 {games[0]}");
                }
                else
                {
                    // Shared car -> register each candidate weakly so it appears in
                    // the vote map even when no other signal mentions that year.
                    foreach (var g in games)
                    {
                        r.AddGameYearVote(g, weight: 1,
                            source: $"roster candidate F1 {g}");
                    }
                }
            }
            else
            {
                r.RosterValid = false;
                r.AddReason(
                    $"Roster: ({r.Team}, {r.CarYear}) is NOT a known classic in F1 2017-2020. " +
                    "This may be a custom mod or a team-naming mismatch.");
            }
        }

        // ---------------------------------------------------------------------
        // Stage 4: resolve
        // ---------------------------------------------------------------------

        private static void ResolveGameYear(F1DetectionResult r)
        {
            if (r.GameYearVotes.Count == 0)
            {
                r.GameYear = 0;
                r.Confidence = DetectionConfidence.Unknown;
                return;
            }

            var ordered = r.GameYearVotes
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .ToList();

            r.GameYear = ordered[0].Key;
            int topScore = ordered[0].Value;
            int secondScore = ordered.Count > 1 ? ordered[1].Value : 0;

            bool rosterAgrees = r.PossibleGameYears == null
                                || r.PossibleGameYears.Contains(r.GameYear);

            if (!rosterAgrees)
            {
                // Path/ERP says one game, roster says the car can't be in that game.
                // Trust roster: replace winner with best roster-valid candidate.
                var rosterValid = ordered
                    .Where(kv => r.PossibleGameYears.Contains(kv.Key))
                    .ToList();

                if (rosterValid.Count > 0)
                {
                    int rejected = r.GameYear;
                    r.GameYear = rosterValid[0].Key;
                    topScore = rosterValid[0].Value;
                    secondScore = rosterValid.Count > 1 ? rosterValid[1].Value : 0;
                    r.AddReason(
                        $"OVERRIDE: top vote was F1 {rejected} but roster forbids it; " +
                        $"falling back to F1 {r.GameYear}");
                    rosterAgrees = true;
                }
                else
                {
                    r.AddReason(
                        $"WARNING: detected F1 {r.GameYear} but roster restricts car to " +
                        $"[{string.Join(", ", r.PossibleGameYears)}]. No valid fallback.");
                }
            }

            int gap = topScore - secondScore;
            if (topScore >= 4 && rosterAgrees && gap >= 3)
                r.Confidence = DetectionConfidence.High;
            else if (topScore >= 2 && rosterAgrees)
                r.Confidence = DetectionConfidence.Medium;
            else
                r.Confidence = DetectionConfidence.Low;
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static int TwoDigitToFullYear(int twoDigit)
        {
            // Classic cars span 1972-2010; threshold of 50 cleanly separates them
            // and leaves headroom for any future modern cars (00-49 -> 20xx).
            return twoDigit >= 50 ? 1900 + twoDigit : 2000 + twoDigit;
        }

        private static string NormalizeTeam(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            // "red_bull" / "redbull" / "Red_Bull" all collapse to "redbull".
            return raw.Replace("_", string.Empty).ToLowerInvariant();
        }

        // ---------------------------------------------------------------------
        // Per-game classic rosters (team names normalized)
        // ---------------------------------------------------------------------
        //
        // F1 2017: 12 cars (1988-2010, McLaren/Ferrari/Williams/Renault/Red Bull).
        // F1 2018: all 12 from 2017 + 6 historics (1972-1982) + 2 Headline DLC
        //          (Williams FW25, Brawn BGP-001) = 20 total.
        // F1 2019: F1 2018 minus Ferrari 412 T2 (1995) and F2002 (2002),
        //          plus Williams FW25 / Brawn BGP-001 promoted to standard,
        //          plus Legends Edition (1990 Ferrari + McLaren) and
        //          Anniversary/Legends (2010 Ferrari + McLaren) = 22 total.
        // F1 2020: drastic cut - all 70s/early-80s cars dropped. 16 standard +
        //          4 Schumacher DLC (Jordan 191, Benetton B194, Benetton B195,
        //          Ferrari F1-2000) = 20 total.
        //
        // Note: the 1990 Ferrari is "F1-90" in F1 2019 and "641" in F1 2020 - same
        // chassis, different game label. Folder name should be identical
        // (c_ferrari_90), so it maps to the same roster key here.
        //
        // If your install uses different team folder spellings (e.g. "c_red_bull_10"
        // vs "c_redbull_10"), normalization handles it - both map to "redbull".

        private static readonly Dictionary<(string team, int year), List<int>> CarToGames =
            BuildCarToGames();

        private static Dictionary<(string team, int year), List<int>> BuildCarToGames()
        {
            var rosters = new Dictionary<int, (string team, int year)[]>
            {
                [2017] = new (string, int)[]
                {
                    ("mclaren", 1988), ("mclaren", 1991), ("williams", 1992),
                    ("ferrari", 1995), ("williams", 1996), ("mclaren", 1998),
                    ("ferrari", 2002), ("ferrari", 2004), ("renault", 2006),
                    ("ferrari", 2007), ("mclaren", 2008), ("redbull",  2010),
                },

                [2018] = new (string, int)[]
                {
                    // All 2017 cars return:
                    ("mclaren", 1988), ("mclaren", 1991), ("williams", 1992),
                    ("ferrari", 1995), ("williams", 1996), ("mclaren", 1998),
                    ("ferrari", 2002), ("ferrari", 2004), ("renault", 2006),
                    ("ferrari", 2007), ("mclaren", 2008), ("redbull",  2010),
                    // New historic cars in F1 2018:
                    ("lotus",   1972), ("ferrari", 1976), ("mclaren", 1976),
                    ("lotus",   1978), ("ferrari", 1979), ("mclaren", 1982),
                    // Headline Edition (DLC after 60 days):
                    ("williams", 2003), ("brawn",   2009),
                },

                [2019] = new (string, int)[]
                {
                    // From 2018 minus 1995 Ferrari (412 T2) and 2002 Ferrari (F2002):
                    ("mclaren", 1988), ("mclaren", 1991), ("williams", 1992),
                    ("williams", 1996), ("mclaren", 1998),
                    ("ferrari", 2004), ("renault", 2006), ("ferrari", 2007),
                    ("mclaren", 2008), ("redbull",  2010),
                    ("lotus",   1972), ("ferrari", 1976), ("mclaren", 1976),
                    ("lotus",   1978), ("ferrari", 1979), ("mclaren", 1982),
                    ("williams", 2003), ("brawn",   2009),
                    // Legends Edition (Senna vs Prost):
                    ("ferrari", 1990), ("mclaren", 1990),
                    // Anniversary Edition / Legends (10-year anniversary):
                    ("ferrari", 2010), ("mclaren", 2010),
                },

                [2020] = new (string, int)[]
                {
                    // Standard 16 - all 70s/early-80s cars dropped:
                    ("mclaren",  1988),
                    ("ferrari",  1990), ("mclaren", 1990),
                    ("mclaren",  1991), ("williams", 1992),
                    ("williams", 1996), ("mclaren", 1998),
                    ("williams", 2003), ("ferrari", 2004),
                    ("renault",  2006), ("ferrari", 2007),
                    ("mclaren",  2008), ("brawn",   2009),
                    ("redbull",  2010), ("ferrari", 2010), ("mclaren", 2010),
                    // Schumacher Deluxe Edition DLC (F1 2020 EXCLUSIVE):
                    ("jordan",   1991), ("benetton", 1994),
                    ("benetton", 1995), ("ferrari",  2000),
                },
            };

            // Invert to (team, year) -> list of games.
            var inverted = new Dictionary<(string, int), List<int>>();
            foreach (var kv in rosters)
            {
                int game = kv.Key;
                foreach (var entry in kv.Value)
                {
                    var k = (entry.team, entry.year);
                    if (!inverted.TryGetValue(k, out var list))
                    {
                        list = new List<int>();
                        inverted[k] = list;
                    }
                    if (!list.Contains(game)) list.Add(game);
                }
            }
            foreach (var list in inverted.Values) list.Sort();
            return inverted;
        }
    }

    // -------------------------------------------------------------------------
    // Result types
    // -------------------------------------------------------------------------

    internal enum DetectionConfidence
    {
        Unknown,
        Low,
        Medium,
        High,
    }

    internal sealed class F1DetectionResult
    {
        public int GameYear;                 // 2017..2026, or 0 if unknown
        public int CarYear;                  // 1972..2026, or 0 if unknown / not a classic
        public string Team;                  // normalized: "mclaren", "redbull", ...
        public string CarKey;                // raw folder, e.g. "c_mclaren_98"
        public string ErpVersion;            // e.g. "3.0"
        public bool IsClassic;
        public string ClassicPackage;        // "classic_01", "classic_02", or null
        public bool RosterValid;             // (team, carYear) found in any 2017-2020 roster
        public DetectionConfidence Confidence;
        public int[] PossibleGameYears;      // narrowed candidates from roster
        public List<string> Reasons = new List<string>();
        public Dictionary<int, int> GameYearVotes = new Dictionary<int, int>();

        public void AddGameYearVote(int year, int weight, string source)
        {
            if (year < 2010 || year > 2030) return;
            if (GameYearVotes.TryGetValue(year, out int cur))
                GameYearVotes[year] = cur + weight;
            else
                GameYearVotes[year] = weight;
            Reasons.Add($"  +{weight} -> F1 {year}: {source}");
        }

        public void AddReason(string reason) => Reasons.Add(reason);

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("F1DetectionResult { ");
            sb.Append($"GameYear={GameYear}, ");
            if (IsClassic)
            {
                sb.Append($"CarYear={CarYear}, ");
                sb.Append($"Team={Team ?? "?"}, ");
                sb.Append($"CarKey={CarKey ?? "?"}, ");
                sb.Append($"ClassicPackage={ClassicPackage ?? "?"}, ");
            }
            sb.Append($"IsClassic={IsClassic}, ");
            sb.Append($"Confidence={Confidence}");
            if (!string.IsNullOrEmpty(ErpVersion)) sb.Append($", ErpVersion={ErpVersion}");
            sb.Append(" }");
            return sb.ToString();
        }
    }
}