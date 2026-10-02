using EgoEngineLibrary.Data.GameDatabases;
using SimplifiedGridEditor.Core.Project;
using DriverSeason = SimplifiedDriverSwapper.Core.Drivers.Season;
using TeamSeason = SimplifiedTeamEditor.Core.Teams.Season;

namespace SimplifiedGridEditor.Core.Packaging;

/// <summary>
/// The parts of a project a package can be built from: a tab, and where a tab
/// has one, a year.
///
/// These are the tabs as somebody thinks of them rather than as the database
/// holds them. A grid mod is often several pieces of work that were never meant
/// to go out together — this year's grid while last year's is half finished, the
/// names without the colours — and a package could only ever be all of it.
/// </summary>
public enum PackagePart
{
    /// <summary>What the 2025 teams are called, in every language.</summary>
    TeamNames2025,

    /// <summary>
    /// What the 2026 teams are called. Their names live in keys the game ships
    /// without, so this carries the keys as well as the text in them.
    /// </summary>
    TeamNames2026,

    /// <summary>The 2025 teams' four colours.</summary>
    TeamColours2025,

    /// <summary>The 2026 teams' four colours.</summary>
    TeamColours2026,

    /// <summary>The 2025 team logos, merged into the game's UI shader packages.</summary>
    Logos2025,

    /// <summary>The 2026 team logos.</summary>
    Logos2026,

    /// <summary>
    /// A logo in a slice no team sits at, which only a project somebody has
    /// opened up and put a file into can have. It gets a line of its own
    /// rather than being dropped without a word.
    /// </summary>
    Logos,

    /// <summary>The 2025 grid: who sits where, and their numbers.</summary>
    Grid2025,

    /// <summary>The 2026 grid.</summary>
    Grid2026,

    /// <summary>The 2026 Formula 2 grid.</summary>
    GridF2,

    /// <summary>The driver table: names, nationalities, faces.</summary>
    Drivers,

    /// <summary>The menus: the featured drivers, the featured cars, the grid they come from.</summary>
    Menus,

    /// <summary>
    /// Helmets, suits and gloves that belong to no grid this app edits — the
    /// classic helmets, the custom liveries, and the seasons before these two.
    ///
    /// The ones that do belong to a grid are filed under it. An entry says
    /// which: the pools name themselves "2026-mclaren_lando_norris",
    /// "2025-ferrari", "2023-f2-artgp", and the year at the front is the grid
    /// it is worn on. That matters because kit is most of what a swap writes —
    /// two drivers changing seats is four seat records and six of these — and
    /// filing it all together would mean packaging one year's grid carried the
    /// other year's kit.
    /// </summary>
    HelmetsAndKit,

    /// <summary>
    /// Anything else this project changed.
    ///
    /// Normally nothing. A change nobody can name is still a change, and
    /// dropping it silently would be worse than listing it, so it gets a line
    /// of its own and a tick like the rest.
    /// </summary>
    Other,

}

/// <summary>
/// Which part of a project each of its changes belongs to.
///
/// The database stores a record's path as a hash, so a key cannot be read back
/// into the name it came from. What can be done is the other way round: build
/// every path the app is capable of writing, hash those, and look a changed key
/// up in the result. A key that isn't in there is something this app didn't
/// write or doesn't know about, and goes to <see cref="PackagePart.Other"/>.
/// </summary>
public sealed class PackageParts
{
    private readonly Dictionary<ulong, PackagePart> _database = [];

    /// <summary>How many changes each part holds.</summary>
    public IReadOnlyDictionary<PackagePart, int> Counts { get; private set; } =
        new Dictionary<PackagePart, int>();

    /// <summary>What a part is called on screen.</summary>
    public static string Label(PackagePart part) => part switch
    {
        PackagePart.TeamNames2025 => "Team names — 2025",
        PackagePart.TeamNames2026 => "Team names — 2026",
        PackagePart.TeamColours2025 => "Team colours — 2025",
        PackagePart.TeamColours2026 => "Team colours — 2026",
        PackagePart.Grid2025  => "Grid — 2025",
        PackagePart.Grid2026  => "Grid — 2026",
        PackagePart.GridF2    => "Grid — 2026 F2",
        PackagePart.Drivers   => "Drivers",
        PackagePart.Menus     => "Menus",
        PackagePart.HelmetsAndKit => "Helmets and kit",
        PackagePart.Other     => "Everything else",
        PackagePart.Logos2025 => "Team logos — 2025",
        PackagePart.Logos2026 => "Team logos — 2026",
        PackagePart.Logos     => "Team logos — no season",
        _ => part.ToString(),
    };

    /// <summary>A line under the name, saying what is in it.</summary>
    public static string Hint(PackagePart part) => part switch
    {
        PackagePart.TeamNames2025 => "in every language",
        PackagePart.TeamNames2026 => "in every language, and the keys they live in",
        PackagePart.TeamColours2025 => "the four each team carries",
        PackagePart.TeamColours2026 => "the four each team carries",
        PackagePart.Grid2025  => "who sits where, and their numbers",
        PackagePart.Grid2026  => "who sits where, and their numbers",
        PackagePart.GridF2    => "who sits where, and their numbers",
        PackagePart.Drivers   => "the driver table",
        PackagePart.Menus     => "the start screen and career menu",
        PackagePart.HelmetsAndKit => "classic and custom kit, and earlier seasons",
        PackagePart.Other     => "changes this app doesn't recognise",
        PackagePart.Logos2025 => "merged into the game's shader packages",
        PackagePart.Logos2026 => "merged into the game's shader packages",
        PackagePart.Logos     => "in a slice no team sits at",
        _ => "",
    };

    /// <summary>Which part a changed record belongs to.</summary>
    public PackagePart PartOf(ulong key) => _database.GetValueOrDefault(key, PackagePart.Other);

    /// <summary>
    /// Which part a changed language entry belongs to. The 2026 keys are the
    /// ones the game ships without, and the project carries the list.
    /// </summary>
    public static PackagePart PartOfText(string key, IReadOnlySet<string> keys2026) =>
        keys2026.Contains(key) ? PackagePart.TeamNames2026 : PackagePart.TeamNames2025;

    /// <summary>
    /// Work out what each of a project's changes is, against the database it
    /// was built from.
    /// </summary>
    public static PackageParts Read(GameDatabase database, ProjectEdits edits, IReadOnlySet<string> keys2026)
    {
        var parts = new PackageParts();
        parts.Build(database, edits);

        var counts = new Dictionary<PackagePart, int>();

        foreach (var key in edits.Database.Keys)
        {
            if (!ulong.TryParse(key, out var id)) continue;
            var part = parts.PartOf(id);
            counts[part] = counts.GetValueOrDefault(part) + 1;
        }

        foreach (var key in edits.Text.Keys)
        {
            var part = PartOfText(key, keys2026);
            counts[part] = counts.GetValueOrDefault(part) + 1;
        }

        parts.Counts = counts;
        return parts;
    }

    // ── Building the map ────────────────────────────────────────────────────

    private void Build(GameDatabase database, ProjectEdits edits)
    {
        // The grids. Every seat, and the two fields a seat carries that this app
        // writes: who is in it, and the number they race under.
        foreach (var (season, part) in new[]
                 {
                     (DriverSeason.F2026, PackagePart.Grid2026),
                     (DriverSeason.F2025, PackagePart.Grid2025),
                     (DriverSeason.F2_2026, PackagePart.GridF2),
                 })
        {
            foreach (var seat in season.Seats)
                foreach (var field in new[] { "driver", "car_number" })
                    Put(season.SeatPath(seat.Slot, field), part);
        }

        // The driver table. Every driver the database holds, every field the
        // Drivers tab can edit.
        var drivers = CountOf(database, "entrants.drivers._driver_instancecount_");
        for (var i = 0; i < drivers; i++)
            foreach (var field in SimplifiedDriverSwapper.Core.Drivers.DriverAttributes.Fields)
                Put(DriverSeason.DriverPath(i, field), PackagePart.Drivers);

        // The teams. A team record carries two different jobs — the colours it
        // races in, and which key its name is read from — and somebody
        // recolouring a grid is not usually the same person renaming it, so
        // they are offered apart.
        var teams = CountOf(database, "entrants.teams._team_instancecount_");
        for (var i = 0; i < teams; i++)
        {
            var is2026 = Is2026Team(database, i);

            foreach (var field in ColourFields)
                Put($"entrants.teams.team_{i}.{field}",
                    is2026 ? PackagePart.TeamColours2026 : PackagePart.TeamColours2025);

            foreach (var field in SimplifiedTeamEditor.Core.Teams.Season2026.NameFields)
                Put($"entrants.teams.team_{i}.{field}",
                    is2026 ? PackagePart.TeamNames2026 : PackagePart.TeamNames2025);
        }

        // The helmet, suit and glove pools. Two fields each: the name the game
        // knows an entry by, and the id a seat points at. The name says which
        // grid wears it, so that is what each entry is filed under.
        foreach (var (directory, className, countField) in Kits)
        {
            var count = CountOf(database, DriverSeason.KitCountPath(directory, countField));

            for (var i = 0; i < count; i++)
            {
                var namePath = DriverSeason.KitPath(directory, className, i, "_metadata_");

                // The project's own name for it where it has one: a swap
                // renames these, and the new name is the grid it is going to.
                var name = edits.Database.TryGetValue(Fnv1a.Hash(namePath).ToString(), out var renamed)
                    ? System.Text.Encoding.UTF8.GetString(renamed.Bytes).TrimEnd('\0')
                    : database.Text(namePath);

                var part = GridOf(name);

                Put(namePath, part);
                Put(DriverSeason.KitPath(directory, className, i, "id"), part);
            }
        }

        // The menus, which were found by key rather than by name — nothing in
        // the file says where a record's value appears on screen.
        foreach (var key in MenuKeys) _database[key] = PackagePart.Menus;
    }

    /// <summary>
    /// The colour fields, from TeamColours rather than written out again, so a
    /// colour added there is classified here without anybody remembering to.
    /// The name fields come from Season2026 for the same reason.
    /// </summary>
    private static IEnumerable<string> ColourFields =>
        Enum.GetValues<SimplifiedTeamEditor.Core.Teams.ColourField>()
            .Select(SimplifiedTeamEditor.Core.Teams.TeamColours.FieldName);

    /// <summary>
    /// Whether a team record is one of the 2026 teams, read from its own id:
    /// those are the ones whose metadata ends _26.
    /// </summary>
    private static bool Is2026Team(GameDatabase database, int index) =>
        database.Text($"entrants.teams.team_{index}._metadata_")
                .EndsWith("_26", StringComparison.Ordinal);

    /// <summary>
    /// Which season a replaced logo belongs to.
    ///
    /// Not a question the project can be asked directly: a logo is pixels in a
    /// texture array rather than a record with a path, and three of the five
    /// arrays hold both grids at once. What tells them apart is the slice, so
    /// the season comes from reading the game's own layout backwards — whose
    /// slot is this?
    ///
    /// A slice no team sits at falls back to the pair taken together rather
    /// than guessing a year.
    /// </summary>
    public static PackagePart PartOfLogo(string kindId, int slice) =>
        SimplifiedTeamEditor.Core.Logos.LogoSlices.WhoseSlice(kindId, slice)?.Season switch
        {
            TeamSeason.Y2025 => PackagePart.Logos2025,
            TeamSeason.Y2026 => PackagePart.Logos2026,
            _ => PackagePart.Logos,
        };

    /// <summary>
    /// How many logos a project carries, by season.
    ///
    /// Counted from the pictures it holds rather than from its changes: a logo
    /// is not a change to a file this app diffs, it is a picture that goes into
    /// the game's arrays when the mod is built.
    /// </summary>
    public static Dictionary<PackagePart, int> CountLogos(IEnumerable<StoredLogo> stored)
    {
        var counts = new Dictionary<PackagePart, int>();

        foreach (var logo in stored)
        {
            var part = PartOfLogo(logo.Kind.Id, logo.Slice);
            counts[part] = counts.GetValueOrDefault(part) + 1;
        }

        return counts;
    }

    /// <summary>
    /// The grid a pool entry is worn on, read from the name the game knows it
    /// by: "2026-mclaren_lando_norris" is this year's, "2023-f2-artgp" an F2
    /// one. A name with no year, or a year these grids don't cover, belongs to
    /// no grid here — the classic helmets and the custom liveries are most of
    /// those.
    /// </summary>
    private static PackagePart GridOf(string name) => name switch
    {
        _ when name.StartsWith("2026-f2-", StringComparison.Ordinal) => PackagePart.GridF2,
        _ when name.StartsWith("2026-", StringComparison.Ordinal) => PackagePart.Grid2026,
        _ when name.StartsWith("2025-", StringComparison.Ordinal) => PackagePart.Grid2025,
        _ => PackagePart.HelmetsAndKit,
    };

    /// <summary>
    /// The three pools a driver's kit comes out of, as DriverCatalogue reads
    /// them: the folder, what an entry in it is called, and the record holding
    /// how many there are.
    /// </summary>
    private static readonly (string Directory, string ClassName, string CountField)[] Kits =
    [
        ("helmets", "Helmet", "_Helmet_instancecount_"),
        ("suits", "Suit", "_Suit_instancecount_"),
        ("gloves", "Gloves", "_Gloves_instancecount_"),
    ];

    /// <summary>
    /// The menu records: the three on the start screen, the three on the career
    /// menu, the three cars, and the grid they are all looked up in.
    /// </summary>
    private static readonly ulong[] MenuKeys =
    [
        7405927051429813244, 7405930349964697877, 7405929250453069666,
        14448577251764907159, 14448579450788163581, 14448576152253278948,
        13435888910351434555, 13435890009863062766, 13435891109374690977,
        5697272913582422247,
    ];

    private void Put(string path, PackagePart part) => _database[Fnv1a.Hash(path)] = part;

    private static int CountOf(GameDatabase database, string path) => database.Int(path, 0);
}
