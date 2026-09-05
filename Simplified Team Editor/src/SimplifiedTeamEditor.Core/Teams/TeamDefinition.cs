namespace SimplifiedTeamEditor.Core.Teams;

/// <summary>Which grid a team belongs to.</summary>
public enum Season
{
    /// <summary>The game's shipped 2025 grid, twelve teams including the two optional ones.</summary>
    Y2025,

    /// <summary>The 2026 grid: eleven teams, Audi and Cadillac in, the optional two out.</summary>
    Y2026,
}

/// <summary>
/// One team, and where its name lives.
///
/// <c>Id</c> is the abbreviation the game's own language keys use, and
/// <c>DatabaseId</c> the name the same team goes by in gamedatabases.bin.
/// Neither necessarily matches the team's current branding: F1 25 kept both
/// through every rebrand, so Aston Martin is <c>ast</c> in one and
/// <c>forceindia</c> in the other.
/// </summary>
/// <param name="Id">Short id, e.g. <c>ast</c>.</param>
/// <param name="DatabaseId">The <c>_metadata_</c> value in the game database.</param>
/// <param name="Label">How the team is listed in the editor.</param>
/// <param name="AccentHex">Fallback marker colour, used until the real one is read.</param>
/// <param name="FullKey">Language key for the full name.</param>
/// <param name="ShortKey">Language key for the short name.</param>
/// <param name="SuperShortKey">Language key for the three-letter form.</param>
public sealed record TeamDefinition(
    string Id,
    string DatabaseId,
    string Label,
    string AccentHex,
    string FullKey,
    string ShortKey,
    string SuperShortKey);

/// <summary>
/// The 2025 grid, in the order the editor lists it.
///
/// Named TeamGrid rather than Teams: a class with the same name as the
/// namespace around it reads fine until something outside tries to use it, and
/// then every reference is ambiguous.
///
/// The last two entries are the game's optional extras: Konnersport as the
/// eleventh team, and APXGP — keyed <c>zenith</c> — from the film tie-in. Both
/// break the key naming the other ten follow, which is why every key is spelled
/// out per team rather than derived from the id.
/// </summary>
public static class TeamGrid
{
    public static readonly IReadOnlyList<TeamDefinition> All = new[]
    {
        new TeamDefinition("mcl",  "mclaren",     "McLaren Formula 1 Team",     "#e18728",
            "lng_team_mcl_honda_title", "lng_team_mcl_honda_title_short", "lng_team_mcl_honda_title_super_short"),
        new TeamDefinition("fer",  "ferrari",     "Scuderia Ferrari HP",        "#9c1b1b",
            "lng_team_fer_title", "lng_team_fer_title_short", "lng_team_fer_title_super_short"),
        new TeamDefinition("rbr",  "redbull",     "Oracle Red Bull Racing",     "#447baf",
            "lng_team_rbr_title", "lng_team_rbr_title_short", "lng_team_rbr_title_super_short"),
        new TeamDefinition("mer",  "mercedes",    "Mercedes-AMG PETRONAS",      "#39a3a2",
            "lng_team_mer_title", "lng_team_mer_title_short", "lng_team_mer_title_super_short"),
        // Aston Martin, on the old Force India record.
        new TeamDefinition("ast",  "forceindia",  "Aston Martin Aramco",        "#2d7767",
            "lng_team_ast_title", "lng_team_ast_title_short", "lng_team_ast_title_super_short"),
        // Alpine, on the old Renault record.
        new TeamDefinition("alp",  "renault",     "BWT Alpine",                 "#345c94",
            "lng_team_alp_title", "lng_team_alp_title_short", "lng_team_alp_title_super_short"),
        new TeamDefinition("haas", "haas",        "MoneyGram Haas F1 Team",     "#555555",
            "lng_team_haas_title", "lng_team_haas_title_short", "lng_team_haas_title_super_short"),
        // Racing Bulls, on the old Toro Rosso record.
        new TeamDefinition("tor",  "tororosso",   "Visa Cash App Racing Bulls", "#3c519b",
            "lng_team_tor_title", "lng_team_tor_title_short", "lng_team_tor_title_super_short"),
        new TeamDefinition("wil",  "williams",    "Atlassian Williams Racing",  "#5190c1",
            "lng_team_wil_title", "lng_team_wil_title_short", "lng_team_wil_title_super_short"),
        new TeamDefinition("sau",  "sauber",      "KICK Sauber F1 Team",        "#4e964e",
            "lng_team_sau_title", "lng_team_sau_title_short", "lng_team_sau_title_super_short"),
        new TeamDefinition("kon",  "konnersport", "Konnersport",                "#ffeb19",
            "lng_team_kon_title", "lng_team_kon_title_short", "lng_team_kon_title_super_short"),
        new TeamDefinition("zen",  "zenith_25",   "APXGP",                      "#b5a26b",
            "lng_zenith", "lng_zenith_short", "lng_zenith_super_short"),
    };

    /// <summary>The grid for a season.</summary>
    public static IReadOnlyList<TeamDefinition> For(Season season)
        => season == Season.Y2026 ? Season2026.Teams : All;
}
