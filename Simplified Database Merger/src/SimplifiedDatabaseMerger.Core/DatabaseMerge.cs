using System.Text;
using EgoEngineLibrary.Data.GameDatabases;

namespace SimplifiedDatabaseMerger.Core;

/// <summary>Which version of a contested record goes into the merged database.</summary>
public enum Keep
{
    First,
    Second,

    /// <summary>Neither mod's: the clean database's own, or no record at all if it had none.</summary>
    Clean,
}

/// <summary>What happened to one record in one edited database.</summary>
public enum ChangeKind
{
    Changed,
    Added,
    Removed,
}

/// <summary>One record an edited database has differently from the clean one.</summary>
/// <param name="Key">The record's key.</param>
/// <param name="Kind">Whether it was changed, added or removed.</param>
/// <param name="Clean">The clean database's version, or null when it was added.</param>
/// <param name="Edited">The edited database's version, or null when it was removed.</param>
public sealed record Change(ulong Key, ChangeKind Kind, DatabaseRecord? Clean, DatabaseRecord? Edited);

/// <summary>What one edited database did to the clean one: every change, in key order.</summary>
public sealed class Changes(IReadOnlyList<Change> list)
{
    public IReadOnlyList<Change> List { get; } = list;

    /// <summary>Records it has with a different value.</summary>
    public int Changed { get; } = list.Count(c => c.Kind == ChangeKind.Changed);

    /// <summary>Records it has that the clean one has not.</summary>
    public int Added { get; } = list.Count(c => c.Kind == ChangeKind.Added);

    /// <summary>Records the clean one has that it has not.</summary>
    public int Removed { get; } = list.Count(c => c.Kind == ChangeKind.Removed);

    public int Total => List.Count;
}

/// <summary>
/// A record both databases changed, each its own way. Which one survives is a
/// person's call: the file holds no names, only the hashes of them, so nothing
/// here can know what the record means.
/// </summary>
public sealed class Conflict
{
    public required ulong Key { get; init; }

    /// <summary>The record as the clean database has it, or null when it has none.</summary>
    public required DatabaseRecord? Clean { get; init; }

    /// <summary>The first database's version, or null when it removed the record.</summary>
    public required DatabaseRecord? First { get; init; }

    /// <summary>The second database's version, or null when it removed the record.</summary>
    public required DatabaseRecord? Second { get; init; }

    public Keep Keep { get; set; } = Keep.First;

    /// <summary>What <see cref="Keep"/> currently points at.</summary>
    public DatabaseRecord? Chosen => Keep switch
    {
        Keep.First => First,
        Keep.Second => Second,
        _ => Clean,
    };
}

/// <summary>
/// Everything the merge worked out, before anything is written: what each
/// database changed, where they agree, and where they collide.
/// </summary>
public sealed class MergePlan
{
    internal MergePlan(DatabaseCipher.Key cipher, Dictionary<ulong, DatabaseRecord?> settled,
                       List<Conflict> conflicts, Changes first, Changes second, int agreed, int cleanCount)
    {
        Cipher = cipher;
        _settled = settled;
        Conflicts = conflicts;
        First = first;
        Second = second;
        Agreed = agreed;
        CleanCount = cleanCount;
    }

    /// <summary>
    /// Every record whose outcome is decided, by key. Null where the decision is
    /// that the record goes — one of the two removed it and the other left it
    /// alone.
    /// </summary>
    private readonly Dictionary<ulong, DatabaseRecord?> _settled;

    public DatabaseCipher.Key Cipher { get; }

    /// <summary>Records the two changed differently, in key order. Each carries its own choice.</summary>
    public IReadOnlyList<Conflict> Conflicts { get; }

    /// <summary>The conflict on a key, or null when the key goes in without one.</summary>
    public Conflict? ConflictOn(ulong key)
    {
        _conflictsByKey ??= Conflicts.ToDictionary(c => c.Key);
        return _conflictsByKey.GetValueOrDefault(key);
    }

    private Dictionary<ulong, Conflict>? _conflictsByKey;

    public Changes First { get; }
    public Changes Second { get; }

    /// <summary>Records both databases changed to exactly the same thing. Not a conflict.</summary>
    public int Agreed { get; }

    /// <summary>How many records the clean database has.</summary>
    public int CleanCount { get; }

    /// <summary>True when neither database changed anything, so a merge would be the clean file again.</summary>
    public bool NothingToMerge => First.Total == 0 && Second.Total == 0;

    /// <summary>
    /// The merged database: the clean one with every change from both put in,
    /// and each conflict settled the way it has been chosen.
    ///
    /// The records are copies. The plan holds the three databases' own records,
    /// and a merged file that shared them would change under the plan the
    /// moment anything wrote to it.
    /// </summary>
    public GameDatabase Build()
    {
        var records = new List<DatabaseRecord>(_settled.Count + Conflicts.Count);

        foreach (var record in _settled.Values)
            if (record is not null) records.Add(Copy(record));

        foreach (var conflict in Conflicts)
            if (conflict.Chosen is { } record) records.Add(Copy(record));

        return GameDatabase.FromRecords(records, Cipher);
    }

    private static DatabaseRecord Copy(DatabaseRecord record) => new()
    {
        Key = record.Key,
        Tag = record.Tag,
        Data = (byte[])record.Data.Clone(),
        Terminator = record.Terminator,
    };
}

/// <summary>
/// The three-way merge: two edited databases, each compared with the clean
/// database it was made from, and every change from both laid onto a fresh copy
/// of the clean one.
///
/// The format makes this simpler than it sounds. A database is a flat list of
/// records, each a value under the hash of its path, so a change is always a
/// record that is different, new, or gone. Record by record, then:
///
///   - neither changed it: the clean value;
///   - one changed it: that one's;
///   - both changed it the same way: that, once;
///   - both changed it differently: a conflict, settled by a person.
///
/// "Changed" includes added and removed, so a record one mod removes and the
/// other leaves alone is removed, and one both add with different values is a
/// conflict — which is what two mods that each add a team in the same free slot
/// look like, and why that cannot be guessed at here.
/// </summary>
public static class DatabaseMerge
{
    /// <summary>
    /// Why these three cannot be merged, or null when they can. All three have
    /// to be the same game's: each game has its own key, and a record from one
    /// means nothing to another.
    /// </summary>
    /// <remarks>Either edited one may be missing, for a look at the other's changes alone.</remarks>
    public static string? Check(LoadedDatabase clean, LoadedDatabase? first, LoadedDatabase? second)
    {
        foreach (var (edited, which) in new[] { (first, "first"), (second, "second") })
            if (edited is not null && edited.Game != clean.Game)
                return $"The {which} database is from {edited.Game}, and the clean one is from {clean.Game}. " +
                       "All three have to be from the same game.";

        return null;
    }

    /// <summary>Work out the merge. Nothing is written; <see cref="MergePlan.Build"/> does that.</summary>
    public static MergePlan Plan(GameDatabase clean, GameDatabase first, GameDatabase second)
    {
        var c = ByKey(clean);
        var a = ByKey(first);
        var b = ByKey(second);

        var keys = new HashSet<ulong>(c.Keys);
        keys.UnionWith(a.Keys);
        keys.UnionWith(b.Keys);

        var settled = new Dictionary<ulong, DatabaseRecord?>(keys.Count);
        var conflicts = new List<Conflict>();
        var agreed = 0;

        foreach (var key in keys)
        {
            c.TryGetValue(key, out var was);
            a.TryGetValue(key, out var mine);
            b.TryGetValue(key, out var theirs);

            var firstChanged = !Same(was, mine);
            var secondChanged = !Same(was, theirs);

            if (!firstChanged && !secondChanged) settled[key] = was;
            else if (!secondChanged) settled[key] = mine;
            else if (!firstChanged) settled[key] = theirs;
            else if (Same(mine, theirs))
            {
                settled[key] = mine;
                agreed++;
            }
            else conflicts.Add(new Conflict { Key = key, Clean = was, First = mine, Second = theirs });
        }

        conflicts.Sort((x, y) => x.Key.CompareTo(y.Key));

        return new MergePlan(clean.Cipher, settled, conflicts, Compare(c, a), Compare(c, b), agreed, c.Count);
    }

    /// <summary>What an edited database did to the clean one, record by record, in key order.</summary>
    private static Changes Compare(Dictionary<ulong, DatabaseRecord> clean, Dictionary<ulong, DatabaseRecord> edited)
    {
        var list = new List<Change>();

        foreach (var (key, record) in edited)
        {
            if (!clean.TryGetValue(key, out var was)) list.Add(new Change(key, ChangeKind.Added, null, record));
            else if (!Same(was, record)) list.Add(new Change(key, ChangeKind.Changed, was, record));
        }

        foreach (var (key, record) in clean)
            if (!edited.ContainsKey(key)) list.Add(new Change(key, ChangeKind.Removed, record, null));

        list.Sort((x, y) => x.Key.CompareTo(y.Key));
        return new Changes(list);
    }

    /// <summary>
    /// The same record, byte for byte: value, type and terminator. Two absences
    /// are the same; an absence and a record are not.
    /// </summary>
    private static bool Same(DatabaseRecord? x, DatabaseRecord? y)
    {
        if (x is null || y is null) return x is null && y is null;
        return x.Tag == y.Tag && x.Terminator == y.Terminator && x.Data.AsSpan().SequenceEqual(y.Data);
    }

    /// <summary>
    /// By key. A file with a key twice would be one the game cannot search; the
    /// last of them wins here, as it does in the library's own lookup.
    /// </summary>
    private static Dictionary<ulong, DatabaseRecord> ByKey(GameDatabase database)
    {
        var map = new Dictionary<ulong, DatabaseRecord>(database.Records.Count);
        foreach (var record in database.Records) map[record.Key] = record;
        return map;
    }

    /// <summary>
    /// A record's value as a line of text for a person to compare: the text
    /// itself when it is text, which nearly every record is, and the bytes in
    /// hex when it is not.
    /// </summary>
    public static string Describe(DatabaseRecord? record)
    {
        if (record is null) return "(not there)";
        if (record.Data.Length == 0) return "(empty)";

        var text = Encoding.UTF8.GetString(record.Data);
        var readable = !text.Contains('�') && text.All(ch => ch >= ' ' || ch is '\t' or '\n' or '\r');

        if (readable) return text.Length > 200 ? text[..200] + "…" : text;

        var hex = Convert.ToHexString(record.Data.AsSpan(0, Math.Min(record.Data.Length, 32)));
        return record.Data.Length > 32 ? $"{hex}… ({record.Data.Length} bytes)" : hex;
    }
}
