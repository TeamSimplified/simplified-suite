using SimplifiedTeamEditor.Core.Lng;

namespace SimplifiedUpdater.Core.Merge;

/// <summary>What one language file's merge turned out to be.</summary>
public sealed record LanguageMergeResult(
    int Edited, int Added, int Conflicts, int Dropped, IReadOnlyList<string> Notes)
{
    public int Total => Edited + Added;
}

/// <summary>
/// Carrying a mod's text changes onto a newer game's language file.
///
/// The same three-way rule as the database, over string keys instead of hashes:
/// every entry where the mod differs from the version it was built against is
/// the modder's and comes across, and every entry where it doesn't is the
/// game's and is left as the update left it.
/// </summary>
public static class LanguageMerge
{
    /// <summary>
    /// Apply the mod's changes to the new language file and hand back the bytes.
    /// </summary>
    public static (byte[] Bytes, LanguageMergeResult Result) Apply(
        byte[] oldBytes, byte[] modBytes, byte[] newBytes)
    {
        var old = LngFile.FromBytes(oldBytes).Entries;
        var mod = LngFile.FromBytes(modBytes).Entries;
        var updated = LngFile.FromBytes(newBytes);

        // Values for keys the new file already has, and whole entries for keys
        // it doesn't. The two are applied by different writers: changing a value
        // only moves offsets around, where adding a key changes which bucket
        // everything lands in and needs the hash table rebuilt.
        var edits = new Dictionary<string, string>(StringComparer.Ordinal);
        var additions = new Dictionary<string, string>(StringComparer.Ordinal);

        int conflicts = 0, dropped = 0;
        var notes = new List<string>();

        foreach (var (key, value) in mod)
        {
            var hadBefore = old.TryGetValue(key, out var before);
            if (hadBefore && before == value) continue;

            if (updated.Entries.TryGetValue(key, out var current))
            {
                if (hadBefore && current != before) conflicts++;
                edits[key] = value;
            }
            else if (hadBefore)
            {
                dropped++;
            }
            else
            {
                additions[key] = value;
            }
        }

        if (dropped > 0)
            notes.Add($"{dropped:N0} entrie(s) the mod changed are no longer in this language file. " +
                      "Those edits could not be carried over.");

        if (conflicts > 0)
            notes.Add($"{conflicts:N0} entrie(s) were changed by both the mod and the game update. " +
                      "The mod's text was kept.");

        if (edits.Count == 0 && additions.Count == 0)
            return (newBytes, new LanguageMergeResult(0, 0, conflicts, dropped, notes));

        byte[] bytes;
        if (additions.Count == 0)
        {
            bytes = updated.BuildSaveData(edits);
        }
        else
        {
            // Rebuild from the new file's own entries with everything applied,
            // rather than from the mod's: the update's text for keys nobody
            // touched is the point of the exercise.
            var all = new Dictionary<string, string>(updated.Entries, StringComparer.Ordinal);
            foreach (var (key, value) in edits) all[key] = value;
            foreach (var (key, value) in additions) all[key] = value;
            bytes = updated.BuildRebuiltData(all);
        }

        return (bytes, new LanguageMergeResult(edits.Count, additions.Count, conflicts, dropped, notes));
    }
}
