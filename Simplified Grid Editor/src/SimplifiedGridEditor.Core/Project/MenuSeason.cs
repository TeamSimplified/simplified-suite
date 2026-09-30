using EgoEngineLibrary.Data.GameDatabases;
using Season = SimplifiedDriverSwapper.Core.Drivers.Season;

namespace SimplifiedGridEditor.Core.Project;

/// <summary>
/// Which grid the menu's featured drivers are drawn from.
///
/// One record holding an entrant list's name — <c>f1_2026</c> as the game ships,
/// <c>f1_2025</c> for last season's grid. The three slots beside it name drivers,
/// and a driver only resolves against the list this points at: set it to 2025 and
/// the menu shows the 2025 grid, which is how the 2025 drivers can be featured at
/// all.
///
/// Same shape as <see cref="MenuTrio"/> — on disk, what the untouched original
/// holds, and what a save would write — because it is edited beside them and
/// resets the same way.
/// </summary>
public sealed class MenuSeason
{
    private readonly ulong _key;
    private string _onDisk = "";
    private string _stock = "";
    private string? _staged;

    private MenuSeason(ulong key) => _key = key;

    /// <summary>
    /// The grids that can be chosen.
    ///
    /// The two F1 ones. The game ships pointing at 2026 and 2025 has been
    /// confirmed in game; the F2 lists are in the same file and may well work,
    /// but nobody has looked, and a menu that renders nothing is not worth
    /// offering on a guess.
    /// </summary>
    public static IReadOnlyList<Season> Choices { get; } = [Season.F2026, Season.F2025];

    /// <summary>True when the database has the record. An older one may not.</summary>
    public bool Available { get; private set; }

    public string OnDisk => _onDisk;

    /// <summary>What the untouched original holds, which is what reset means.</summary>
    public string Stock => _stock;

    /// <summary>What a save would write: the staged value, or what is already there.</summary>
    public string Effective => _staged ?? _onDisk;

    public bool IsStaged => _staged is not null;

    public bool DiffersFromStock => _stock.Length > 0 && Effective != _stock;

    public bool HasChanges => _staged is not null;

    /// <summary>The grid the current value names, or null if it names something else.</summary>
    public Season? Grid => Choices.FirstOrDefault(s => s.ListName == Effective);

    /// <summary>
    /// Stage a grid. Choosing what is already on disk clears the edit rather
    /// than recording a change that writes nothing.
    /// </summary>
    public void Set(string listName)
    {
        listName = listName.Trim();
        if (listName.Length == 0 || listName == _onDisk) _staged = null;
        else _staged = listName;
    }

    public static MenuSeason Read(GameDatabase database, GameDatabase? original, ulong key)
    {
        var season = new MenuSeason(key);

        if (database.FindByKey(key) is { } record) season._onDisk = record.Text;
        if (original?.FindByKey(key) is { } stock) season._stock = stock.Text;

        season.Available = database.FindByKey(key) is not null;
        return season;
    }

    /// <summary>
    /// Write the staged value into the database, leaving the record otherwise
    /// as it was found — the tag and terminator the game wrote stay put.
    /// </summary>
    public void Apply(GameDatabase database)
    {
        if (_staged is null) return;

        if (database.FindByKey(_key) is { } record && record.Text != _staged)
            record.Text = _staged;

        _onDisk = _staged;
        _staged = null;
    }
}
