using EgoEngineLibrary.Data.GameDatabases;
using SimplifiedDriverSwapper.Core.Drivers;

namespace SimplifiedGridEditor.Core.Project;

/// <summary>
/// The open project's database and everything read out of it, in one place the
/// tabs share.
///
/// In the Driver Swapper this state lives on the window, because there is only
/// one screen. Here the Grid and Drivers tabs are separate panels looking at the
/// same records — a seat's occupant on one, that driver's face and commentary
/// name on the other — so it has to be somewhere both can reach.
///
/// The reference copy is the project as it was created: the bundled originals,
/// before anything was edited. It is what "reset" means, and it is also the only
/// thing that still names every helmet design once a save has renamed them.
/// </summary>
public sealed class GridDocument
{
    private GridDocument(GameDatabase database, DriverCatalogue catalogue,
                         DatabaseSnapshot? reference, SeatPlan seats,
                         List<DriverAttributes.DriverRow> drivers, MenuRecords menus)
    {
        Database = database;
        Catalogue = catalogue;
        Reference = reference;
        Seats = seats;
        Drivers = drivers;
        Menus = menus;
    }

    public GameDatabase Database { get; }

    public DriverCatalogue Catalogue { get; }

    /// <summary>The pristine copy, for names and for reset. Null when it couldn't be read.</summary>
    public DatabaseSnapshot? Reference { get; }

    /// <summary>The seat plan for the season being shown. Rebuilt when that changes.</summary>
    public SeatPlan Seats { get; private set; }

    public List<DriverAttributes.DriverRow> Drivers { get; }

    /// <summary>The menu records the Misc tab edits.</summary>
    public MenuRecords Menus { get; }

    /// <summary>Driver record index → field → new value, before it is written.</summary>
    public Dictionary<int, Dictionary<string, string>> PendingAttributes { get; } = [];

    /// <summary>True when a save would change anything in the database.</summary>
    public bool HasChanges => Seats.HasChanges || PendingAttributes.Count > 0 || Menus.HasChanges;

    /// <summary>
    /// Settle what was staged: it is in the project's files now, so nothing is
    /// pending. Called once a save has written them.
    ///
    /// The menus settle themselves as they are applied, because a menu record
    /// holds one value and applying it is the whole of the change. Seats and
    /// driver fields are told separately.
    /// </summary>
    public void Committed()
    {
        Seats.Committed();
        PendingAttributes.Clear();
    }

    /// <summary>
    /// Read a project's database and the pristine copy beside it.
    /// </summary>
    /// <param name="databasePath">The project's own database, which is the one edited.</param>
    /// <param name="originalPath">
    /// The bundled original of the same file. Helmet designs are named there and
    /// may not be here, once a save has been through.
    /// </param>
    public static GridDocument Read(string databasePath, string? originalPath, Season season)
    {
        var database = GameDatabase.Load(databasePath);

        GameDatabase? original = null;
        if (originalPath is not null && File.Exists(originalPath))
        {
            try { original = GameDatabase.Load(originalPath); }
            catch { /* names fall back to the project's own copy */ }
        }

        var catalogue = DriverCatalogue.Read(database, original);
        var reference = original is null
            ? null
            : new DatabaseSnapshot(original, DriverCatalogue.Read(original));

        return new GridDocument(
            database, catalogue, reference,
            new SeatPlan(database, catalogue, season, reference),
            DriverAttributes.Read(database),
            MenuRecords.Read(database, original));
    }

    /// <summary>
    /// Point the seat plan at another season. Seat changes belong to one entrant
    /// list, so they do not survive this — the caller asks first.
    /// </summary>
    public void ShowSeason(Season season) =>
        Seats = new SeatPlan(Database, Catalogue, season, Reference);
}
