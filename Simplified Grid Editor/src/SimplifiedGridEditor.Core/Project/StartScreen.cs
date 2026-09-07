using EgoEngineLibrary.Data.GameDatabases;

namespace SimplifiedGridEditor.Core.Project;

/// <summary>
/// The three drivers standing on the game's start screen.
///
/// Each is one database record holding a driver id, and unlike everything else
/// the app edits these are reached by key rather than by path. The file stores
/// no paths — only the hash of each — so a path that cannot be guessed cannot be
/// recovered, and this one resisted a search of every one, two and three segment
/// name built from the database's own vocabulary. The keys below are the ones
/// the game's database editors export, and they are the record's identity as far
/// as the file is concerned, so nothing is lost by using them directly.
///
/// The three sit next to each other in key order and in the file, which is why
/// they read as a set rather than three unrelated values.
/// </summary>
public sealed class StartScreen
{
    /// <summary>Where a driver stands, as you look at the screen.</summary>
    public enum Slot
    {
        Left,
        Centre,
        Right,
    }

    /// <summary>
    /// Which record holds which position.
    ///
    /// The order here is the order they read on screen; the records themselves
    /// run the other way — the first is the driver on the right.
    /// </summary>
    private static readonly (Slot Slot, ulong Key)[] Keys =
    [
        (Slot.Left,   7405930349964697877),
        (Slot.Centre, 7405929250453069666),
        (Slot.Right,  7405927051429813244),
    ];

    /// <summary>The slots in the order they appear on screen, left to right.</summary>
    public static IReadOnlyList<Slot> Slots { get; } = Keys.Select(k => k.Slot).ToList();

    private readonly Dictionary<Slot, string> _onDisk = new();
    private readonly Dictionary<Slot, string> _stock = new();
    private readonly Dictionary<Slot, string> _staged = new();

    private StartScreen() { }

    /// <summary>
    /// True when the database has all three records. A game whose start screen
    /// works differently would not, and the tab says so rather than pretending.
    /// </summary>
    public bool Available { get; private init; }

    /// <summary>What the project's database holds for a slot right now.</summary>
    public string OnDisk(Slot slot) => _onDisk.GetValueOrDefault(slot, "");

    /// <summary>What the untouched original holds, which is what reset means.</summary>
    public string Stock(Slot slot) => _stock.GetValueOrDefault(slot, "");

    /// <summary>What a save would write: the staged driver, or what's already there.</summary>
    public string Effective(Slot slot) => _staged.TryGetValue(slot, out var v) ? v : OnDisk(slot);

    /// <summary>True when this slot has an edit waiting to be saved.</summary>
    public bool IsStaged(Slot slot) => _staged.ContainsKey(slot);

    /// <summary>True when this slot differs from the untouched original.</summary>
    public bool DiffersFromStock(Slot slot) =>
        _stock.ContainsKey(slot) && Effective(slot) != Stock(slot);

    public bool HasChanges => _staged.Count > 0;

    /// <summary>
    /// Stage a driver for a slot. Setting it back to what is already on disk
    /// clears the edit rather than recording a change that writes nothing.
    /// </summary>
    public void Set(Slot slot, string driverId)
    {
        driverId = driverId.Trim();
        if (driverId.Length == 0 || driverId == OnDisk(slot)) _staged.Remove(slot);
        else _staged[slot] = driverId;
    }

    /// <summary>Drop a slot's staged edit.</summary>
    public void Clear(Slot slot) => _staged.Remove(slot);

    /// <summary>Read the three records out of a database, and the original beside it.</summary>
    public static StartScreen Read(GameDatabase database, GameDatabase? original)
    {
        var screen = new StartScreen
        {
            Available = Keys.All(k => database.FindByKey(k.Key) is not null),
        };

        foreach (var (slot, key) in Keys)
        {
            if (database.FindByKey(key) is { } record) screen._onDisk[slot] = record.Text;
            if (original?.FindByKey(key) is { } stock) screen._stock[slot] = stock.Text;
        }

        return screen;
    }

    /// <summary>
    /// Write the staged drivers into the database.
    ///
    /// The records are left exactly as they were found apart from their text, so
    /// the tag and terminator the game wrote stay put — these carry a different
    /// tag from the ordinary text records around them.
    /// </summary>
    public void Apply(GameDatabase database)
    {
        foreach (var (slot, key) in Keys)
        {
            if (!_staged.TryGetValue(slot, out var value)) continue;
            if (database.FindByKey(key) is not { } record || record.Text == value) continue;
            record.Text = value;
        }

        // What was staged is now what is on disk, so nothing is left pending.
        foreach (var (slot, value) in _staged) _onDisk[slot] = value;
        _staged.Clear();
    }
}
