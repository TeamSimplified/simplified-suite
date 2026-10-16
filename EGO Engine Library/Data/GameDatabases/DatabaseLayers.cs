namespace EgoEngineLibrary.Data.GameDatabases;

/// <summary>What laying several databases over a clean one came to.</summary>
/// <param name="Database">The result: the clean database with every layer's changes in it.</param>
/// <param name="Applied">Record changes taken from the layers, counting each layer's separately.</param>
/// <param name="Overridden">
/// Of those, how many landed on a record an earlier layer had already
/// changed — the places where the order decided the outcome.
/// </param>
public sealed record Layered(GameDatabase Database, int Applied, int Overridden);

/// <summary>
/// Several edited databases laid over the clean one they were made from, in
/// order, the later winning wherever two changed the same record.
///
/// Every layer is read as the difference between it and the clean database —
/// what it changed, added and removed — and only that difference is applied.
/// A layer is a whole database, so everything it did not change it still
/// carries, at the clean value; applying it whole would put those clean values
/// back over an earlier layer's work. Taking the difference is what lets two
/// mods that touch different records both arrive.
///
/// Removals are changes like any other. A database mod is always the game's
/// whole file, so a record it lacks is one it took out.
/// </summary>
public static class DatabaseLayers
{
    public static Layered Apply(GameDatabase clean, IEnumerable<GameDatabase> layers)
    {
        var baseline = new Dictionary<ulong, DatabaseRecord>(clean.Records.Count);
        foreach (var record in clean.Records) baseline[record.Key] = record;

        // What the result holds, by key: null where a layer removed the record.
        var result = new Dictionary<ulong, DatabaseRecord?>(baseline.Count);
        foreach (var (key, record) in baseline) result[key] = record;

        var touched = new HashSet<ulong>();
        var applied = 0;
        var overridden = 0;

        foreach (var layer in layers)
        {
            var seen = new HashSet<ulong>(layer.Records.Count);

            foreach (var record in layer.Records)
            {
                seen.Add(record.Key);
                if (baseline.TryGetValue(record.Key, out var was) && Same(was, record)) continue;

                Note(record.Key);
                result[record.Key] = record;
            }

            foreach (var key in baseline.Keys)
            {
                if (seen.Contains(key)) continue;

                Note(key);
                result[key] = null;
            }
        }

        var records = new List<DatabaseRecord>(result.Count);
        foreach (var record in result.Values)
            if (record is not null) records.Add(Copy(record));

        return new Layered(GameDatabase.FromRecords(records, clean.Cipher), applied, overridden);

        void Note(ulong key)
        {
            applied++;
            if (!touched.Add(key)) overridden++;
        }
    }

    /// <summary>The same record byte for byte: value, type and terminator.</summary>
    private static bool Same(DatabaseRecord x, DatabaseRecord y) =>
        x.Tag == y.Tag && x.Terminator == y.Terminator && x.Data.AsSpan().SequenceEqual(y.Data);

    /// <summary>
    /// A copy, so the result shares nothing with the databases it was made
    /// from and writing to one cannot change another.
    /// </summary>
    private static DatabaseRecord Copy(DatabaseRecord record) => new()
    {
        Key = record.Key,
        Tag = record.Tag,
        Data = (byte[])record.Data.Clone(),
        Terminator = record.Terminator,
    };
}
