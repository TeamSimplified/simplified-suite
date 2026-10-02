using EgoEngineLibrary.Archive.Erp;

namespace SimplifiedTeamEditor.Core.Logos;

/// <summary>
/// The places a team's logo appears, and the texture arrays each one lives in.
///
/// A logo is not one image. The same badge is drawn from several texture
/// arrays depending on where it appears — the timing tower, the broadcast
/// overlay, the team-select screen — so replacing "the logo" means writing the
/// same picture into each of them at the same slice.
/// </summary>
/// <param name="SharedBetweenSeasons">
/// Whether both grids live in the one array. The showroom logos have an array
/// per season and start at slice 0; everything else shares, with 2026 offset
/// above 2025.
/// </param>
public sealed record LogoKind(
    string Id, string Label, IReadOnlyList<string> Texarrays, bool SharedBetweenSeasons)
{
    /// <summary>Every resource this kind touches, the arrays and their images.</summary>
    public IEnumerable<string> ResourceIdentifiers =>
        Texarrays.SelectMany(t => new[]
        {
            TeamLogos.IdentifierFor(t),
            TeamLogos.IdentifierFor(t + ".image"),
        });
}

public static class TeamLogos
{
    /// <summary>
    /// Where texarray resources live inside the branding archive. Every one is
    /// addressed as this folder plus the array's name.
    /// </summary>
    public const string TexarrayFolder = "eaid://ui_package/art/texture_arrays/overrides.uidb";

    public static string IdentifierFor(string texarrayBase) => $"{TexarrayFolder}/{texarrayBase}";

    /// <summary>
    /// Team badges appear in three parallel 24x24 arrays that share one team
    /// to slice layout, so a single edit has to fan out to all three or the
    /// badge changes in one place and not the others.
    /// </summary>
    private static readonly string[] BadgeArrays =
    {
        "overrides.osd.timing_tower.team_badges_broadcast.texarray",
        "overrides.osd.timing_tower.team_badges_hud.texarray",
        "overrides.teams.badges_broadcast.texarray",
    };

    /// <summary>Team-select and starting-grid logos each live in one array, shared by both seasons.</summary>
    private static readonly string[] TeamSelectArrays =
        { "overrides.team_logos.driver_career_2024_team_select.texarray" };

    private static readonly string[] StartingGridArrays =
        { "overrides.broadcast_ui_generic.grid_team_logos.texarray" };

    public static readonly IReadOnlyList<LogoKind> Kinds = new[]
    {
        // Each season has its own showroom array, eleven slices, starting at 0.
        new LogoKind("showroom_25", "Showroom (2025)", new[] { "overrides.team_logos.f1_2023.texarray" }, false),
        new LogoKind("showroom_26", "Showroom (2026)", new[] { "overrides.team_logos.f1_2026.texarray" }, false),
        // These three share one array between the seasons.
        new LogoKind("badges", "Timing tower badge", BadgeArrays, true),
        new LogoKind("team_select", "Team select", TeamSelectArrays, true),
        new LogoKind("starting_grid", "Starting grid", StartingGridArrays, true),
    };

    /// <summary>
    /// An archive's resource for one of these arrays, or null when it has none.
    ///
    /// ErpFile.FindResource throws for a name it does not hold, which turns
    /// "does this archive carry that array?" into an exception rather than an
    /// answer — and that is the question asked of every archive somebody
    /// hands us, and of every one an older project kept.
    /// </summary>
    public static ErpResource? ResourceFor(ErpFile archive, string texarrayBase)
        => archive.Resources.Find(r => r.Identifier == IdentifierFor(texarrayBase));

    public static LogoKind Kind(string id)
        => Kinds.FirstOrDefault(k => k.Id == id)
           ?? throw new ArgumentOutOfRangeException(nameof(id), id, "No such logo kind.");

    // Which slice a given team occupies is not derivable — it is a table, and
    // lives in LogoSlices. An earlier version of this file computed it from a
    // fixed 2026 offset, which the shipped arrays disagree with in four
    // different ways.
}
