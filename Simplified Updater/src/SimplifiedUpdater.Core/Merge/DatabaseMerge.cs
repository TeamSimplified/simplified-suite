using EgoEngineLibrary.Data.GameDatabases;

namespace SimplifiedUpdater.Core.Merge;

/// <summary>What a database merge turned out to be.</summary>
/// <param name="Edited">Records the mod changed that were carried onto the new database.</param>
/// <param name="Added">Records the mod added that the game never had.</param>
/// <param name="Conflicts">
/// Records the mod and the game update both changed. The mod's value is kept —
/// a modder who set a value meant to override the game's, and that is as true
/// of the new value as it was of the old — but they are counted, because a
/// value the update changed is the one place this can be wrong.
/// </param>
/// <param name="Dropped">Records the mod changed that the new game no longer has.</param>
public sealed record DatabaseMergeResult(
    int Edited, int Added, int Conflicts, int Dropped, IReadOnlyList<string> Notes)
{
    public int Total => Edited + Added;
}

/// <summary>
/// Carrying a mod's database changes onto a newer game's database.
///
/// The file is a flat list of values addressed by the hash of a dotted path and
/// stores no paths at all, which for once is convenient: a record can only be
/// compared with the same record, so "what did the modder change" is exactly
/// "which keys differ from the version they started from".
///
/// Three files, then. <c>old</c> is what the modder had, <c>mod</c> is what they
/// shipped, <c>new</c> is what the game has now. Every key where mod differs
/// from old is theirs and gets carried across; every key where it doesn't is the
/// game's and is left alone, which is what stops an update being undone.
/// </summary>
public static class DatabaseMerge
{
    /// <summary>
    /// Apply the mod's changes to the new database and hand back the bytes.
    /// Nothing is written; the caller decides where it goes.
    /// </summary>
    public static (byte[] Bytes, DatabaseMergeResult Result) Apply(
        byte[] oldBytes, byte[] modBytes, byte[] newBytes)
    {
        var old = Index(GameDatabase.FromBytes(oldBytes));
        var mod = GameDatabase.FromBytes(modBytes);
        var updated = GameDatabase.FromBytes(newBytes);
        var byKey = Index(updated);

        int edited = 0, added = 0, conflicts = 0, dropped = 0;
        var notes = new List<string>();

        foreach (var record in mod.Records)
        {
            var hadBefore = old.TryGetValue(record.Key, out var before);

            // Untouched by the mod. The new game's value stands, whatever it is.
            if (hadBefore && before!.Data.AsSpan().SequenceEqual(record.Data)) continue;

            if (byKey.TryGetValue(record.Key, out var target))
            {
                if (hadBefore && !target.Data.AsSpan().SequenceEqual(before!.Data)) conflicts++;

                target.Data = record.Data;
                if (hadBefore) edited++; else added++;
            }
            else if (hadBefore)
            {
                // The update removed a record the mod had changed. Putting it
                // back would resurrect a key the game has stopped reading.
                dropped++;
            }
            else
            {
                // The mod invented this key and the game has never had it —
                // a 2026 team name, say. It comes across as it is.
                updated.Records.Add(new DatabaseRecord
                {
                    Key = record.Key,
                    Tag = record.Tag,
                    Data = record.Data,
                    Terminator = record.Terminator,
                });
                added++;
            }
        }

        // Records the mod dropped. No tool in the suite removes one, so this
        // says the mod's database came from somewhere else — worth reporting
        // and not worth acting on: deleting game records on a guess is a far
        // bigger change than leaving them.
        var modKeys = mod.Records.Select(r => r.Key).ToHashSet();
        var missing = old.Keys.Count(k => !modKeys.Contains(k));
        if (missing > 0)
            notes.Add($"The mod's database is missing {missing:N0} record(s) the old game had. " +
                      "They were left in place rather than removed.");

        if (dropped > 0)
            notes.Add($"{dropped:N0} record(s) the mod changed are no longer in the game's database. " +
                      "Those edits could not be carried over.");

        if (conflicts > 0)
            notes.Add($"{conflicts:N0} record(s) were changed by both the mod and the game update. " +
                      "The mod's value was kept.");

        return (updated.ToBytes(), new DatabaseMergeResult(edited, added, conflicts, dropped, notes));
    }

    private static Dictionary<ulong, DatabaseRecord> Index(GameDatabase db)
    {
        var map = new Dictionary<ulong, DatabaseRecord>(db.Records.Count);
        // Last wins, matching how the reader's own lookup ends up when a file
        // carries a key twice.
        foreach (var record in db.Records) map[record.Key] = record;
        return map;
    }
}
