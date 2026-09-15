namespace SimplifiedUpdater.Core.Merge;

/// <summary>
/// One side of the comparison: the game's own database and language files at a
/// particular version.
/// </summary>
public sealed class BaselineFiles
{
    public required string Folder { get; init; }

    /// <summary>The database, full path.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>Language files by file name — <c>language_eng.lng</c> and the rest.</summary>
    public required IReadOnlyDictionary<string, string> Languages { get; init; }
}

/// <summary>
/// Reading and checking the <c>old\</c> and <c>new\</c> folders.
///
/// These are the whole basis of the merge: what a mod changed is what differs
/// from <c>old</c>, and what the update changed is what differs between the two.
/// Get them wrong — the same version in both, a missing database, last season's
/// language files — and the run still produces a zip, one that quietly undoes
/// half the mod. So they are checked before anything is unpacked, and a bad
/// pair stops the job rather than colouring the log.
/// </summary>
public static class Baseline
{
    /// <summary>
    /// Everything wrong with the two folders, in the order somebody would fix
    /// it. Empty means they are usable.
    /// </summary>
    public static List<string> Problems(string oldDir, string newDir)
    {
        var problems = new List<string>();

        var before = Check(oldDir, "old", problems);
        var after = Check(newDir, "new", problems);
        if (before is null || after is null) return problems;

        // The same files on both sides means one of them was never updated,
        // and every one of the mod's edits would look like a change the game
        // made. This is the mistake worth catching.
        if (SameFile(before.DatabasePath, after.DatabasePath))
            problems.Add("old and new hold the same database. Put the updated game's files in new\\ " +
                         "and the ones the mod was made against in old\\.");

        var shared = before.Languages.Keys.Intersect(after.Languages.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        if (shared.Count == 0)
            problems.Add("old and new have no language file in common — nothing could be compared. " +
                         "Both folders want the game's full set, named as the game names them.");

        return problems;
    }

    /// <summary>Read one side. Throws when it is not usable — call <see cref="Problems"/> first.</summary>
    public static BaselineFiles Read(string folder, string which)
    {
        var problems = new List<string>();
        var files = Check(folder, which, problems);
        if (files is null) throw new InvalidOperationException(string.Join(" ", problems));
        return files;
    }

    private static BaselineFiles? Check(string folder, string which, List<string> problems)
    {
        if (!Directory.Exists(folder))
        {
            problems.Add($"There is no {which}\\ folder beside the app. It wants the game's " +
                         "database and language files from that version.");
            return null;
        }

        var database = Directory.EnumerateFiles(folder, "*.bin", SearchOption.AllDirectories)
            .FirstOrDefault(GameFiles.IsDatabase);
        if (database is null)
        {
            problems.Add($"{which}\\ has no gamedatabases .bin in it.");
            return null;
        }

        var languages = Directory.EnumerateFiles(folder, "*.lng", SearchOption.AllDirectories)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key!, g => g.First(), StringComparer.OrdinalIgnoreCase);

        if (languages.Count == 0)
        {
            problems.Add($"{which}\\ has no language files in it.");
            return null;
        }

        return new BaselineFiles { Folder = folder, DatabasePath = database, Languages = languages };
    }

    /// <summary>
    /// Whether two files hold the same bytes. Length first, since two versions
    /// of an 18 MB database are almost never the same size, and only then the
    /// contents — streamed, so neither is pulled into memory whole.
    /// </summary>
    private static bool SameFile(string a, string b)
    {
        var left = new FileInfo(a);
        var right = new FileInfo(b);
        if (left.Length != right.Length) return false;

        using var one = File.OpenRead(a);
        using var two = File.OpenRead(b);
        Span<byte> bufferOne = new byte[64 * 1024];
        Span<byte> bufferTwo = new byte[64 * 1024];

        while (true)
        {
            var read = one.ReadAtLeast(bufferOne, bufferOne.Length, throwOnEndOfStream: false);
            two.ReadAtLeast(bufferTwo, read, throwOnEndOfStream: false);
            if (read == 0) return true;
            if (!bufferOne[..read].SequenceEqual(bufferTwo[..read])) return false;
        }
    }
}
