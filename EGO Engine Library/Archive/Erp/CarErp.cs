using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EgoEngineLibrary.Util;

namespace EgoEngineLibrary.Archive.Erp
{
    /// <summary>
    /// What tells a car ERP apart from something that merely lives beside one.
    ///
    /// A team folder's <c>wep/</c> holds the team's car and, for teams that ran
    /// one, its special liveries — <c>toro_rosso.erp</c> next to
    /// <c>toro_rosso_suzuka_26.erp</c>. Most of those liveries are whole cars:
    /// same resource count, same nine <c>/wep/</c> resources, same seven
    /// <c>.layer</c> files, differing only in that every one of them is named
    /// after the livery rather than the team.
    ///
    /// Some are not. <c>toro_rosso_barcelona_26.erp</c> carries thirty-three
    /// resources and no <c>/wep/</c> at all. A chassis swap works by grafting the
    /// slot's <c>/wep/</c> setup onto the donor, so an archive without one is
    /// neither a car to swap in nor a slot to swap into — offering it produces a
    /// build that looks fine and loads as nothing.
    /// </summary>
    public static class CarErp
    {
        /// <summary>The path segment every car's own setup sits under.</summary>
        public const string WepSegment = "/wep/";

        /// <summary>
        /// True when this archive carries the <c>/wep/</c> setup that makes the
        /// game load it as a car.
        /// </summary>
        public static bool IsCar(ErpFile erp) =>
            erp.Resources.Any(r =>
                r.Identifier.IndexOf(WepSegment, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// The same question about a file on disk. False for anything that can't
        /// be read at all — a listing shouldn't throw because one archive in a
        /// folder is damaged.
        /// </summary>
        public static bool IsCar(string path)
        {
            try
            {
                return IsCar(ErpFileIO.Load(path));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Every car ERP in a team's <c>wep/</c> folder: the team's own first,
        /// then its special liveries by name. Archives without a <c>/wep/</c>
        /// setup are left out.
        /// </summary>
        /// <param name="wepDir">A <c>teams/&lt;team&gt;/wep</c> folder.</param>
        /// <param name="team">The folder's team, whose name the base car carries.</param>
        /// <param name="isCar">
        /// How to decide whether a file is a car. Overridable so a caller that
        /// lists these repeatedly can remember the answers — reading every
        /// archive in an install takes long enough to be felt in a dropdown.
        /// </param>
        public static List<string> InFolder(string wepDir, string team, Func<string, bool>? isCar = null)
        {
            isCar ??= IsCar;

            if (!Directory.Exists(wepDir)) return new List<string>();

            List<string> files;
            try { files = Directory.EnumerateFiles(wepDir, "*.erp").ToList(); }
            catch { return new List<string>(); }

            return files
                .Where(isCar)
                .OrderByDescending(p => IsBaseCar(p, team))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The team a car ERP belongs to, taken from the folder it sits in
        /// rather than its file name — <c>teams/toro_rosso/wep/anything.erp</c>
        /// is Racing Bulls' whatever the file is called.
        ///
        /// That distinction is the point: a special livery's file name is its
        /// own, but everything it shares with the team — the driver animations
        /// among them — is found under the team's.
        /// </summary>
        public static string? TeamOf(string erpPath)
        {
            // .../teams/<team>/wep/<file>.erp
            var wep = Path.GetDirectoryName(erpPath);
            var teamDir = Path.GetDirectoryName(wep);
            var team = teamDir is null ? null : Path.GetFileName(teamDir);
            return string.IsNullOrEmpty(team) ? null : team;
        }

        /// <summary>True for <c>&lt;team&gt;/wep/&lt;team&gt;.erp</c> — the car the team actually races.</summary>
        public static bool IsBaseCar(string erpPath, string team) =>
            string.Equals(Path.GetFileNameWithoutExtension(erpPath), team, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// What to call a special livery, from its file name:
        /// <c>toro_rosso_suzuka_26</c> against team <c>toro_rosso</c> reads as
        /// <c>Suzuka 26</c>.
        ///
        /// Returns null for the base car, which is named after its team and has
        /// nothing to add.
        /// </summary>
        public static string? LiveryName(string erpPath, string team)
        {
            var name = Path.GetFileNameWithoutExtension(erpPath);
            if (IsBaseCar(erpPath, team)) return null;

            var suffix = name.StartsWith(team + "_", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(team.Length + 1)
                : name;

            // "suzuka_26" → "Suzuka 26". The trailing number is the livery's own
            // year, not the season's — a 2026 pack carries both 25 and 26 ones.
            return string.Join(' ', suffix
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Length > 0 && char.IsLetter(w[0])
                    ? char.ToUpperInvariant(w[0]) + w.Substring(1)
                    : w));
        }
    }
}
