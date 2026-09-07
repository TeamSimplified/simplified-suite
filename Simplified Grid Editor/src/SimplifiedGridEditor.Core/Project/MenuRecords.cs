using EgoEngineLibrary.Data.GameDatabases;
using SimplifiedTeamEditor.Core.Teams;

namespace SimplifiedGridEditor.Core.Project;

/// <summary>
/// The odds and ends on the Misc tab: the three menus that each show three
/// things picked out of the grid.
///
/// Every key here came from an export made by the game's own database editors,
/// which name records by key because the file holds no paths. Nothing in an
/// export says where on screen a record's value ends up, and the three sets do
/// not follow the same order, so each slot below was matched to its record by
/// looking at the game.
/// </summary>
public sealed class MenuRecords
{
    private MenuRecords(MenuTrio startScreen, MenuTrio careerMenu, MenuTrio featuredCars)
    {
        StartScreen = startScreen;
        CareerMenu = careerMenu;
        FeaturedCars = featuredCars;
        All = [startScreen, careerMenu, featuredCars];
    }

    public MenuTrio StartScreen { get; }
    public MenuTrio CareerMenu { get; }
    public MenuTrio FeaturedCars { get; }

    /// <summary>All three, in the order the tab shows them.</summary>
    public IReadOnlyList<MenuTrio> All { get; }

    public bool HasChanges => All.Any(t => t.HasChanges);

    /// <summary>True when the database has none of them, so the tab has nothing to offer.</summary>
    public bool AnyAvailable => All.Any(t => t.Available);

    // Which record sits where was confirmed in game, one trio at a time. It
    // could not have been arrived at any other way: a record carries no position
    // and the three of a set are indistinguishable in the file.
    //
    // The three sets do not agree with one another, which is why every slot is
    // spelled out rather than derived. Reading the export's first key as the
    // right-hand slot is right for the cars and wrong for both sets of drivers.
    public static MenuRecords Read(GameDatabase database, GameDatabase? original) => new(
        // Export order 1, 2, 3 reads right, left, centre.
        MenuTrio.Read(database, original,
            "Start screen", "The three drivers standing on the game's start screen.",
            TrioKind.Drivers,
            right: 7405927051429813244, centre: 7405930349964697877, left: 7405929250453069666),

        // Export order 1, 2, 3 reads left, right, centre.
        MenuTrio.Read(database, original,
            "Career menu", "The three drivers on the career menu.",
            TrioKind.Drivers,
            right: 14448577251764907159, centre: 14448579450788163581, left: 14448576152253278948),

        // Export order 1, 2, 3 reads right, centre, left.
        MenuTrio.Read(database, original,
            "Featured cars", "The three cars featured on the main menu.",
            TrioKind.Cars,
            right: 13435888910351434555, centre: 13435890009863062766, left: 13435891109374690977));

    public void Apply(GameDatabase database)
    {
        foreach (var trio in All) trio.Apply(database);
    }

    /// <summary>
    /// The cars that can be featured: the eleven 2026 teams.
    ///
    /// A car id is the team's database id with the season spelled out in full —
    /// <c>sauber_26</c> becomes <c>sauber_2026</c> — which is how the three the
    /// game ships are written. The teams are the Team Editor's list, so Audi
    /// still races on Sauber's record and Aston Martin on Force India's, and the
    /// label says the name anyone would look for.
    /// </summary>
    public static IReadOnlyList<Choice> CarChoices() => Season2026.Teams
        .Select(t => new Choice(CarIdFor(t.DatabaseId), t.Label))
        .OrderBy(c => c.Display, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string CarIdFor(string databaseId) =>
        databaseId.EndsWith("_26", StringComparison.Ordinal)
            ? databaseId[..^"_26".Length] + "_2026"
            : databaseId;
}
