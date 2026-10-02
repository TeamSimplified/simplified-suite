using SimplifiedTeamEditor.Core.Teams;

namespace SimplifiedTeamEditor.Core.Logos;

/// <summary>
/// Which slice of which array holds which team's logo.
///
/// This is a table rather than a calculation, because the game's layout is not
/// regular and cannot be derived:
///
///   * every kind orders its teams differently — the 2025 badges run Sauber,
///     Racing Bulls, Alpine, Aston…, while the showroom runs Ferrari, Aston,
///     Haas…;
///   * 2026 starts at a different slice in each array: 45 for badges, 47 for
///     team select, 55 for the starting grid, and 0 for the showroom, which has
///     an array to itself;
///   * there are holes. The 2025 showroom has no slice 5, and Konnersport's
///     starting-grid logo sits at slice 20, ten past the team before it;
///   * not every team has every logo. APXGP has none at all, and Konnersport
///     has a badge and a grid logo but no showroom or team-select one.
///
/// Taken from the launcher's own maps, which were built by looking at what the
/// shipped arrays actually contain.
/// </summary>
public static class LogoSlices
{
    private static readonly Dictionary<string, int> Showroom2025 = new()
    {
        // No slice 5, and neither of the optional teams appears here.
        ["fer"] = 0, ["ast"] = 1, ["haas"] = 2, ["mcl"] = 3, ["mer"] = 4,
        ["rbr"] = 6, ["alp"] = 7, ["sau"] = 8, ["tor"] = 9, ["wil"] = 10,
    };

    private static readonly Dictionary<string, int> Showroom2026 = new()
    {
        ["cad"] = 0, ["fer"] = 1, ["ast"] = 2, ["haas"] = 3, ["mcl"] = 4, ["mer"] = 5,
        ["rbr"] = 6, ["alp"] = 7, ["audi"] = 8, ["rac"] = 9, ["wil"] = 10,
    };

    private static readonly Dictionary<string, int> Badges2025 = new()
    {
        ["sau"] = 0, ["tor"] = 1, ["alp"] = 2, ["ast"] = 3, ["fer"] = 4, ["haas"] = 5,
        ["mcl"] = 6, ["mer"] = 7, ["rbr"] = 8, ["wil"] = 9, ["kon"] = 10,
    };

    private static readonly Dictionary<string, int> Badges2026 = new()
    {
        ["audi"] = 45, ["rac"] = 46, ["alp"] = 47, ["ast"] = 48, ["cad"] = 49, ["fer"] = 50,
        ["haas"] = 51, ["mcl"] = 52, ["mer"] = 53, ["rbr"] = 54, ["wil"] = 55,
    };

    private static readonly Dictionary<string, int> TeamSelect2025 = new()
    {
        ["sau"] = 0, ["tor"] = 1, ["alp"] = 2, ["ast"] = 3, ["fer"] = 4,
        ["haas"] = 5, ["mcl"] = 6, ["mer"] = 7, ["rbr"] = 8, ["wil"] = 9,
    };

    private static readonly Dictionary<string, int> TeamSelect2026 = new()
    {
        ["audi"] = 47, ["rac"] = 48, ["alp"] = 49, ["ast"] = 50, ["cad"] = 51, ["fer"] = 52,
        ["haas"] = 53, ["mcl"] = 54, ["mer"] = 55, ["rbr"] = 56, ["wil"] = 57,
    };

    private static readonly Dictionary<string, int> StartingGrid2025 = new()
    {
        ["fer"] = 0, ["ast"] = 1, ["haas"] = 2, ["mcl"] = 3, ["mer"] = 4, ["rbr"] = 5,
        ["alp"] = 6, ["sau"] = 7, ["tor"] = 8, ["wil"] = 9,
        ["kon"] = 20,   // ten slices past the rest, for no reason the files explain
    };

    private static readonly Dictionary<string, int> StartingGrid2026 = new()
    {
        ["cad"] = 55, ["fer"] = 56, ["ast"] = 57, ["haas"] = 58, ["mcl"] = 59, ["mer"] = 60,
        ["rbr"] = 61, ["alp"] = 62, ["audi"] = 63, ["rac"] = 64, ["wil"] = 65,
    };

    private static readonly Dictionary<(string Kind, Season Season), Dictionary<string, int>> Table = new()
    {
        [("showroom_25", Season.Y2025)] = Showroom2025,
        [("showroom_26", Season.Y2026)] = Showroom2026,
        [("badges", Season.Y2025)] = Badges2025,
        [("badges", Season.Y2026)] = Badges2026,
        [("team_select", Season.Y2025)] = TeamSelect2025,
        [("team_select", Season.Y2026)] = TeamSelect2026,
        [("starting_grid", Season.Y2025)] = StartingGrid2025,
        [("starting_grid", Season.Y2026)] = StartingGrid2026,
    };

    /// <summary>
    /// The slice a team's logo of this kind occupies, or null when the game has
    /// no slot for it.
    /// </summary>
    public static int? SliceFor(LogoKind kind, Season season, string teamId)
        => Table.TryGetValue((kind.Id, season), out var slices)
           && slices.TryGetValue(teamId, out var slice)
            ? slice
            : null;

    /// <summary>
    /// Whose logo of this kind sits at a slice, and on which grid, or null
    /// when nobody's does.
    ///
    /// The table read backwards, which is how a stored picture is traced back
    /// to the team and the year it was chosen for. Three of the five kinds
    /// hold both grids in the one array — 2025 low, 2026 offset above it —
    /// so the slice is the only thing that says which year a logo is for, and
    /// that is what lets a package carry one season's logos and leave the
    /// other's behind.
    /// </summary>
    public static (Season Season, string TeamId)? WhoseSlice(string kindId, int slice)
    {
        foreach (var entry in Table)
        {
            if (entry.Key.Kind != kindId) continue;

            foreach (var team in entry.Value)
                if (team.Value == slice) return (entry.Key.Season, team.Key);
        }

        return null;
    }

    /// <summary>The kinds a team actually has a logo for, in this season.</summary>
    public static IEnumerable<LogoKind> KindsFor(Season season, string teamId)
        => TeamLogos.Kinds.Where(k => SliceFor(k, season, teamId) is not null);
}
