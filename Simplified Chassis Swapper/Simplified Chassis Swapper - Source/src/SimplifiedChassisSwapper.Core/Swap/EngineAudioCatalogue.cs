using SimplifiedChassisSwapper.Core.Util;

namespace SimplifiedChassisSwapper.Core.Swap;

/// <summary>
/// The engine sounds available in a game install, collected by reading the audio
/// block out of each grid car's <c>-carmodel.layer</c>.
///
/// Scanned rather than hard-coded: the pairings aren't derivable from team names
/// (<c>lotus</c> runs <c>f1_m_alpine_26</c> on <c>renault_2025.bdl</c>), they
/// differ per package, and a scan picks up modded cars for free. Reading one PKG
/// fragment per ERP costs roughly 50 ms, so an install is a second or two.
/// </summary>
public static class EngineAudioCatalogue
{
    /// <summary>
    /// One selectable engine sound. Just the switch — it alone decides what the
    /// car sounds like, so the bundle sitting next to it in the layer isn't part
    /// of the choice.
    /// </summary>
    /// <param name="Switch">Wwise switch, e.g. <c>f1_m_cadillac_26</c>.</param>
    /// <param name="Team">Team folder it was found in, e.g. <c>lotus</c>.</param>
    /// <param name="Package">Season it belongs to, e.g. <c>2026</c>.</param>
    public sealed record EngineAudioEntry(string Switch, string Team, string Package)
    {
        /// <summary>
        /// Just the switch — that's the name people recognise. Where it came from
        /// stays on the record for ordering and for the hint under the dropdown.
        /// </summary>
        public string Label => Switch;
        public override string ToString() => Label;
    }

    /// <summary>
    /// Every distinct engine sound under a game install, newest package first.
    /// Returns an empty list rather than throwing when the folder isn't a game
    /// install — the caller falls back to the chassis/team choices.
    /// </summary>
    public static List<EngineAudioEntry> Scan(
        string? gameDir, CancellationToken cancel = default, Action<int, int>? progress = null)
    {
        var found = new List<EngineAudioEntry>();
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return found;

        List<string> erps;
        try { erps = FindCarErps(gameDir); }
        catch { return found; }

        // Same switch can appear on several ERPs (livery variants). The first win
        // is kept and the list is ordered canonical-first, so the team's main car
        // supplies the bundle rather than a one-off livery.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < erps.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Invoke(i + 1, erps.Count);

            var entry = ReadEntry(erps[i]);
            if (entry is null) continue;
            if (!seen.Add(entry.Switch)) continue;
            found.Add(entry);
        }

        return found
            .OrderByDescending(e => e.Package, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Team, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Car ERPs from the main grid packages, canonical cars first.
    ///
    /// Only the main packages are read — the story, world and F2 ones carry
    /// engines that don't belong on a grid car.
    ///
    /// Canonical means the ERP named after its team folder (<c>haas.erp</c>)
    /// rather than a livery variant (<c>haas_suzuka_25.erp</c>). Variants repeat
    /// their team's switch, so this only decides which car a duplicate is
    /// credited to in the label — the main grid car rather than a one-off livery.
    /// </summary>
    private static List<string> FindCarErps(string gameDir)
    {
        var packages = Directory
            .EnumerateDirectories(gameDir, "*vehicle_package*", SearchOption.AllDirectories)
            .Where(p => IsMainPackage(Path.GetFileName(p)))
            .ToList();

        var erps = new List<(string Path, bool Canonical)>();
        foreach (var package in packages)
        {
            var teamsDir = Path.Combine(package, "teams");
            if (!Directory.Exists(teamsDir)) continue;

            foreach (var teamDir in Directory.EnumerateDirectories(teamsDir))
            {
                var wep = Path.Combine(teamDir, "wep");
                if (!Directory.Exists(wep)) continue;

                var team = Path.GetFileName(teamDir);
                foreach (var erp in Directory.EnumerateFiles(wep, "*.erp"))
                {
                    var canonical = string.Equals(
                        Path.GetFileNameWithoutExtension(erp), team, StringComparison.OrdinalIgnoreCase);
                    erps.Add((erp, canonical));
                }
            }
        }

        return erps
            .OrderByDescending(e => e.Canonical)
            .Select(e => e.Path)
            .ToList();
    }

    /// <summary>
    /// True for a season's main grid package — <c>f1_2025_vehicle_package</c> —
    /// and not <c>..._story_...</c>, <c>..._world_...</c> or the F2 package.
    /// Detected by the label reducing to a bare year, so a future season is
    /// picked up without a code change.
    /// </summary>
    private static bool IsMainPackage(string? packageDir)
    {
        var label = PackageLabel(packageDir);
        return label.Length > 0 && label.All(char.IsDigit);
    }

    /// <summary>The engine a car ERP declares, or null when it declares none.</summary>
    private static EngineAudioEntry? ReadEntry(string erpPath)
    {
        string? switchName = null;
        try
        {
            var erp = ErpFileIO.Load(erpPath);
            foreach (var res in erp.Resources)
            {
                if (!res.Identifier.EndsWith(".layer", StringComparison.OrdinalIgnoreCase)) continue;
                if (res.Fragments.Count == 0) continue;

                Dictionary<string, string> audio;
                try { audio = PkgPatcher.ReadAudioBankValues(PkgPatcher.ToJson(res.Fragments[0].GetDataArray(true))); }
                catch { continue; }

                if (!audio.TryGetValue(PkgPatcher.SwitchNameKey, out var value)) continue;
                switchName = PkgPatcher.StripTypeTag(value);
                break;   // only the car-model layer declares one
            }
        }
        catch { return null; }

        if (string.IsNullOrWhiteSpace(switchName)) return null;

        // .../<package>/teams/<team>/wep/<file>.erp
        var wepDir = Path.GetDirectoryName(erpPath);
        var teamDir = Path.GetDirectoryName(wepDir);
        var packageDir = Path.GetDirectoryName(Path.GetDirectoryName(teamDir));

        return new EngineAudioEntry(
            switchName,
            Path.GetFileName(teamDir) ?? "?",
            PackageLabel(Path.GetFileName(packageDir)));
    }

    /// <summary><c>f1_2024_story_vehicle_package</c> → <c>2024 story</c>.</summary>
    private static string PackageLabel(string? packageDir)
    {
        if (string.IsNullOrEmpty(packageDir)) return "?";
        var label = packageDir;
        if (label.StartsWith("f1_", StringComparison.OrdinalIgnoreCase)) label = label[3..];
        const string suffix = "_vehicle_package";
        if (label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) label = label[..^suffix.Length];
        return label.Replace('_', ' ');
    }
}
