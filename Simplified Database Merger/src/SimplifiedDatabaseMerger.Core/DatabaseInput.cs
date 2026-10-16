using System.IO.Compression;
using EgoEngineLibrary.Data.GameDatabases;

namespace SimplifiedDatabaseMerger.Core;

/// <summary>One database the merge reads, and where it came from.</summary>
/// <param name="Path">The file that was picked.</param>
/// <param name="Entry">
/// The database's path inside it when the file was a mod's zip, or null when
/// the file was the database itself.
/// </param>
/// <param name="Database">What was read.</param>
public sealed record LoadedDatabase(string Path, string? Entry, GameDatabase Database)
{
    /// <summary>Which game's key the file is written in, e.g. <c>F1 25</c>.</summary>
    public string Game => Database.Cipher.Game;

    /// <summary>The file's name, and the database's inside it when it came out of a zip.</summary>
    public string Label => Entry is null
        ? System.IO.Path.GetFileName(Path)
        : $"{System.IO.Path.GetFileName(Path)} → {System.IO.Path.GetFileName(Entry)}";
}

/// <summary>
/// Reads a database out of whatever was picked: the <c>.bin</c> itself, or a
/// mod's zip with one inside it.
///
/// Mods go round as zips, and the database is one file somewhere in the folder
/// tree under them. Asking people to unpack one first only to point at a file
/// inside it is a step that exists to be skipped, so a zip is opened and the
/// database found by name. Which it is gets decided by the first bytes rather
/// than the extension — a zip renamed is still a zip.
/// </summary>
public static class DatabaseInput
{
    /// <summary>Read a database, or throw <see cref="InvalidDataException"/> saying plainly why not.</summary>
    public static LoadedDatabase Load(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"{Path.GetFileName(path)} isn't there any more.");

        if (!IsZip(path)) return new LoadedDatabase(path, null, Read(File.ReadAllBytes(path), Path.GetFileName(path)));

        using var zip = ZipFile.OpenRead(path);

        // Newest season first, the same order the game's own folder is searched
        // in, should a mod somehow carry two.
        var entry = zip.Entries
            .Where(e => e.Name.StartsWith("gamedatabases", StringComparison.OrdinalIgnoreCase)
                        && e.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? throw new InvalidDataException($"There's no gamedatabases .bin inside {Path.GetFileName(path)}.");

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return new LoadedDatabase(path, entry.FullName, Read(memory.ToArray(), entry.Name));
    }

    /// <summary>
    /// The game's own database, under its install folder: the one the editors
    /// start from, at <c>game_package\gamedatabases</c>.
    ///
    /// That one folder and nowhere else. The library's own search walks the
    /// whole install, which is right once and wrong here: Settings asks on every
    /// keystroke in the folder box, and the install is a hundred gigabytes.
    /// </summary>
    public static string? FindInGame(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return null;

        var folder = Path.Combine(gameDir.Trim(), "game_package", "gamedatabases");
        if (!Directory.Exists(folder)) return null;

        try
        {
            return Directory.EnumerateFiles(folder, "gamedatabases*.bin")
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static GameDatabase Read(byte[] bytes, string name)
    {
        GameDatabase database;
        try
        {
            database = GameDatabase.FromBytes(bytes);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{name}: {e.Message}");
        }
        catch (Exception)
        {
            // A file that decodes under a known key and then runs off its own
            // end is cut short or not a database at all; the reader's index
            // error says neither.
            throw new InvalidDataException($"{name} isn't a complete game database.");
        }

        Account(database, bytes.Length, name);
        return database;
    }

    /// <summary>
    /// Make sure every record in the file was read, and refuse the file if not.
    ///
    /// The reader goes by the count in the header and stops there, so anything
    /// after the last record would be dropped without a word, and a key in the
    /// file twice would collapse into one when the merge looks records up by
    /// key. Neither happens in any database the game or the editors have
    /// written — checked against every one to hand — which is exactly why it
    /// is refused here rather than handled: a file like that was made by
    /// something nobody has seen, and a merge that quietly lost part of it
    /// would look like a merge that worked.
    /// </summary>
    private static void Account(GameDatabase database, long length, string name)
    {
        // The header, then per record its key's high half, type, length, value
        // and terminator, and the next key's low half between each pair.
        var records = database.Records;
        var used = 8L + 4L * (records.Count - 1);
        foreach (var record in records) used += 10L + record.Data.Length;

        if (used != length)
            throw new InvalidDataException(
                $"{name} has {length - used:N0} bytes past its last record, which the merge would lose.");

        var keys = new HashSet<ulong>(records.Count);
        foreach (var record in records)
            if (!keys.Add(record.Key))
                throw new InvalidDataException(
                    $"{name} has the record {record.Key:X16} twice, so which one it means can't be told.");
    }

    private static bool IsZip(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var file = File.OpenRead(path);
        return file.Read(head) == 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4;
    }
}
