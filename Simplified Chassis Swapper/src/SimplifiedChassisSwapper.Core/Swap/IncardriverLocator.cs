using EgoEngineLibrary.Archive.Erp;

namespace SimplifiedChassisSwapper.Core.Swap;

/// <summary>
/// Finds the driver in-car animation ERP inside a game install, and works out
/// which animset belongs to a given team folder.
///
/// Animset tokens don't always match team folder names (the folder
/// <c>force_india</c> owns the animset <c>driver_in_car_2025_forceindia</c>), so
/// candidates are always read from the ERP itself rather than constructed.
/// </summary>
public static class IncardriverLocator
{
    private const string AnimsetSuffix = ".idf?context=animset";

    /// <summary>One season's driver animation ERP, e.g. <c>2025</c> → <c>…/incardriver/2025/incardriver_f1.erp</c>.</summary>
    public sealed record IncardriverSeason(string Season, string Path)
    {
        public override string ToString() => Season;
    }

    /// <summary>
    /// Every <c>incardriver_f1.erp</c> under a game directory, newest season first.
    /// The game ships one per season (2024/2025/2026) and they carry different
    /// animsets, so the caller picks rather than the locator guessing.
    /// </summary>
    public static List<IncardriverSeason> ListSeasons(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return new List<IncardriverSeason>();

        try
        {
            return Directory.EnumerateFiles(gameDir, "incardriver_f1.erp", SearchOption.AllDirectories)
                // The season is the containing folder's name.
                .Select(p => new IncardriverSeason(Path.GetFileName(Path.GetDirectoryName(p)) ?? "", p))
                .OrderByDescending(s => s.Season, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<IncardriverSeason>(); }
    }

    /// <summary>
    /// Locate <c>incardriver_f1.erp</c> under a game directory.
    ///
    /// <paramref name="preferredYear"/> — normally taken from the chassis ERP's own
    /// path — picks the matching season, because a 2025 car needs 2025 animations;
    /// grabbing the newest folder would silently pull a different season's animset.
    /// Falls back to the newest only when there's no match.
    /// </summary>
    public static string? FindIncardriverErp(string? gameDir, string? preferredYear = null)
    {
        var seasons = ListSeasons(gameDir);
        if (seasons.Count == 0) return null;

        if (!string.IsNullOrEmpty(preferredYear))
        {
            // Compare the whole folder name so "2025" can't match "12025".
            var match = seasons.FirstOrDefault(s =>
                string.Equals(s.Season, preferredYear, StringComparison.Ordinal));
            if (match is not null) return match.Path;
        }

        return seasons[0].Path;
    }

    /// <summary>
    /// Season year implied by a path, e.g. <c>.../f1_2025_vehicle_package/...</c> → <c>2025</c>.
    /// Returns null when the path carries no year.
    /// </summary>
    /// <remarks>
    /// The <b>last</b> year in the path wins, because segments get more specific as
    /// you go deeper. A 2026 car lives at
    /// <c>…/2025_asset_groups/f1_2026_vehicle_package/teams/…</c> — the outer group
    /// is named for the game, not the season, so taking the first match would pull
    /// the wrong year's driver animations.
    /// </remarks>
    public static string? InferYear(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var matches = System.Text.RegularExpressions.Regex.Matches(path, @"(?<!\d)(20\d{2})(?!\d)");
        return matches.Count == 0 ? null : matches[^1].Groups[1].Value;
    }

    /// <summary>
    /// Every animset name present in the ERP, e.g. <c>driver_in_car_2025_forceindia</c>.
    /// This is the authoritative candidate list — the UI shows it so a mismatched
    /// auto-pick can be corrected instead of silently going wrong.
    /// </summary>
    public static List<string> ListAnimsets(ErpFile erp)
    {
        var names = new List<string>();
        foreach (var res in erp.Resources)
        {
            var id = res.Identifier;
            if (!id.EndsWith(AnimsetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(id[..^AnimsetSuffix.Length]);
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Best animset for a team token. Matches on the trailing segment, comparing
    /// with separators stripped so <c>force_india</c> resolves to
    /// <c>...forceindia</c>. Returns null when nothing matches — the caller
    /// surfaces that rather than guessing.
    /// </summary>
    public static string? MatchAnimset(IEnumerable<string> animsets, string teamToken)
    {
        var target = Normalize(teamToken);
        if (target.Length == 0) return null;

        string? best = null;
        foreach (var a in animsets)
        {
            // Compare only the trailing token, so the shared "driver_in_car_2025_"
            // prefix can't create false matches.
            var tail = Normalize(a);
            if (tail.EndsWith(target, StringComparison.Ordinal))
            {
                // Prefer the tightest match if several qualify.
                if (best is null || Normalize(a).Length < Normalize(best).Length) best = a;
            }
        }
        return best;
    }

    /// <summary>
    /// The animset for a car ERP: matched on the file's own name, and failing
    /// that on its team's.
    ///
    /// A special livery has no driver animations of its own. It uses the team's
    /// — the same ones the official car uses, which is what the game does too —
    /// and its file name matches nothing, because there is no animset called
    /// <c>…_toro_rosso_suzuka_26</c>. Without the fallback, a swap into one
    /// reported that it couldn't match the animations and left the driver alone.
    /// </summary>
    public static string? MatchAnimsetForCar(IEnumerable<string> animsets, string erpPath)
    {
        var list = animsets as IReadOnlyCollection<string> ?? animsets.ToList();

        return MatchAnimset(list, Path.GetFileNameWithoutExtension(erpPath))
               ?? (CarErp.TeamOf(erpPath) is { } team ? MatchAnimset(list, team) : null);
    }

    /// <summary>Full resource identifier for an animset name.</summary>
    public static string? FindAnimsetIdentifier(ErpFile erp, string animsetName)
    {
        foreach (var res in erp.Resources)
        {
            var id = res.Identifier;
            if (!id.EndsWith(AnimsetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(id[..^AnimsetSuffix.Length]);
            if (string.Equals(name, animsetName, StringComparison.OrdinalIgnoreCase)) return id;
        }
        return null;
    }

    private static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
