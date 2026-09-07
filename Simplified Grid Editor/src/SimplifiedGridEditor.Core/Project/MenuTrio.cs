using EgoEngineLibrary.Data.GameDatabases;

namespace SimplifiedGridEditor.Core.Project;

/// <summary>What a trio's three records name, which decides what can be put in them.</summary>
public enum TrioKind
{
    /// <summary>Driver ids. The choices are the drivers on the 2026 grid.</summary>
    Drivers,

    /// <summary>Car ids, <c>ferrari_2026</c> and the like. The choices are the 2026 teams.</summary>
    Cars,
}

/// <summary>One thing that can go in a slot: what is written, and what is shown.</summary>
public sealed record Choice(string Id, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// Three records the game shows side by side on a menu — the start screen's
/// drivers, the career menu's drivers, the featured cars.
///
/// All three work the same way, so they are one class rather than three. Each is
/// reached by key rather than by path: the file stores no paths, only the hash
/// of each, and these resisted a search of every one, two and three segment name
/// built from the database's own vocabulary. The keys are what the game's own
/// database editors export, and they are the record's identity as far as the
/// file is concerned.
/// </summary>
public sealed class MenuTrio
{
    /// <summary>Where a slot sits, as you look at the screen.</summary>
    public enum Slot
    {
        Left,
        Centre,
        Right,
    }

    /// <summary>The slots in reading order, left to right.</summary>
    public static IReadOnlyList<Slot> Slots { get; } = [Slot.Left, Slot.Centre, Slot.Right];

    private readonly Dictionary<Slot, ulong> _keys = new();
    private readonly Dictionary<Slot, string> _onDisk = new();
    private readonly Dictionary<Slot, string> _stock = new();
    private readonly Dictionary<Slot, string> _staged = new();

    private MenuTrio(string title, string hint, TrioKind kind)
    {
        Title = title;
        Hint = hint;
        Kind = kind;
    }

    /// <summary>What this trio is called on the tab.</summary>
    public string Title { get; }

    /// <summary>A line saying what it drives in game.</summary>
    public string Hint { get; }

    public TrioKind Kind { get; }

    /// <summary>
    /// True when the database has all three records. A game whose menus work
    /// differently would not, and the tab says so rather than pretending.
    /// </summary>
    public bool Available { get; private set; }

    public string OnDisk(Slot slot) => _onDisk.GetValueOrDefault(slot, "");

    /// <summary>What the untouched original holds, which is what reset means.</summary>
    public string Stock(Slot slot) => _stock.GetValueOrDefault(slot, "");

    /// <summary>What a save would write: the staged value, or what is already there.</summary>
    public string Effective(Slot slot) => _staged.TryGetValue(slot, out var v) ? v : OnDisk(slot);

    public bool IsStaged(Slot slot) => _staged.ContainsKey(slot);

    public bool DiffersFromStock(Slot slot) => _stock.ContainsKey(slot) && Effective(slot) != Stock(slot);

    public bool HasChanges => _staged.Count > 0;

    /// <summary>
    /// Stage a value for a slot. Setting it back to what is already on disk
    /// clears the edit rather than recording a change that writes nothing.
    /// </summary>
    public void Set(Slot slot, string id)
    {
        id = id.Trim();
        if (id.Length == 0 || id == OnDisk(slot)) _staged.Remove(slot);
        else _staged[slot] = id;
    }

    /// <summary>
    /// Read a trio out of a database, and the untouched original beside it.
    /// </summary>
    /// <param name="right">
    /// The keys, named by where their value appears on screen rather than by the
    /// order they came out of an export — the three sets do not share one order,
    /// and only the game says which is which. Naming them here is the whole
    /// reason nothing above this line has to know that.
    /// </param>
    public static MenuTrio Read(
        GameDatabase database, GameDatabase? original,
        string title, string hint, TrioKind kind,
        ulong right, ulong centre, ulong left)
    {
        var trio = new MenuTrio(title, hint, kind);
        trio._keys[Slot.Right] = right;
        trio._keys[Slot.Centre] = centre;
        trio._keys[Slot.Left] = left;

        foreach (var (slot, key) in trio._keys)
        {
            if (database.FindByKey(key) is { } record) trio._onDisk[slot] = record.Text;
            if (original?.FindByKey(key) is { } stock) trio._stock[slot] = stock.Text;
        }

        trio.Available = trio._keys.Values.All(k => database.FindByKey(k) is not null);
        return trio;
    }

    /// <summary>
    /// Write the staged values into the database.
    ///
    /// Each record is left exactly as it was found apart from its text, so the
    /// tag and terminator the game wrote stay put — these carry a different tag
    /// from the ordinary text records around them.
    /// </summary>
    public void Apply(GameDatabase database)
    {
        foreach (var (slot, value) in _staged)
        {
            if (!_keys.TryGetValue(slot, out var key)) continue;
            if (database.FindByKey(key) is not { } record || record.Text == value) continue;
            record.Text = value;
        }

        // What was staged is now what is on disk, so nothing is left pending.
        foreach (var (slot, value) in _staged) _onDisk[slot] = value;
        _staged.Clear();
    }
}
