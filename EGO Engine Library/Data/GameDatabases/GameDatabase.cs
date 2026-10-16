using System.Buffers.Binary;
using System.Text;

namespace EgoEngineLibrary.Data.GameDatabases;

/// <summary>One entry in the database: a value addressed by the hash of its path.</summary>
public sealed class DatabaseRecord
{
    public required ulong Key { get; init; }

    /// <summary>Value type. Only 0x05/0x06 (boolean) is interpreted, for key detection.</summary>
    public required byte Tag { get; init; }

    /// <summary>Decoded payload. Everything this app edits is UTF-8 text.</summary>
    public byte[] Data { get; set; } = [];

    public required byte Terminator { get; init; }

    public string Text
    {
        get => Encoding.UTF8.GetString(Data);
        set => Data = Encoding.UTF8.GetBytes(value);
    }
}

/// <summary>
/// <c>gamedatabases.bin</c> — a flat list of records, each keyed by the FNV-1a
/// hash of a dotted path and each payload run through a stream cipher whose
/// counter carries across record boundaries.
///
/// The file stores no paths, so a record can only be found by hashing the path
/// you're looking for. Records that aren't touched are written back with the
/// exact bytes they were read with.
/// </summary>
public sealed class GameDatabase
{
    private readonly Dictionary<ulong, DatabaseRecord> _byKey = [];

    private GameDatabase(List<DatabaseRecord> records, DatabaseCipher.Key cipher)
    {
        Records = records;
        Cipher = cipher;
        foreach (var record in records) _byKey[record.Key] = record;
    }

    public List<DatabaseRecord> Records { get; }

    /// <summary>Which game's key decoded this file.</summary>
    public DatabaseCipher.Key Cipher { get; }

    public static GameDatabase Load(string path) => FromBytes(File.ReadAllBytes(path));

    /// <summary>
    /// A database made of records assembled elsewhere — a merge of several
    /// files, say — written with <paramref name="cipher"/>, which should be the
    /// key the records were read under so the game that reads it is the game
    /// they came from.
    /// </summary>
    public static GameDatabase FromRecords(IEnumerable<DatabaseRecord> records, DatabaseCipher.Key cipher)
    {
        var list = records.ToList();
        if (list.Count == 0) throw new ArgumentException("A database needs at least one record.", nameof(records));
        return new GameDatabase(list, cipher);
    }

    /// <summary>
    /// Read a database already in memory — one lifted out of a zip, or a copy
    /// held while something else is written over the file it came from.
    /// </summary>
    public static GameDatabase FromBytes(byte[] buf)
    {
        if (buf.Length < 8) throw new InvalidDataException("File is too short to be a database.");

        var cipher = DatabaseCipher.Detect(buf.AsSpan(0, Math.Min(buf.Length, 65536)))
                     ?? throw new InvalidDataException(
                         "Couldn't recognise this file — none of the known game keys decode it. " +
                         "Is it a gamedatabases.bin?");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        var low = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));

        var pos = 8;
        var counter = 0;
        var records = new List<DatabaseRecord>((int)count);

        for (uint i = 0; i < count; i++)
        {
            var high = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
            pos += 4;
            var tag = buf[pos];
            pos += 1;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
            pos += 4;

            var data = new byte[length];
            DatabaseCipher.Decode(buf.AsSpan(pos, length), data, cipher.Bytes, ref counter);
            pos += length;

            var terminator = buf[pos];
            pos += 1;

            records.Add(new DatabaseRecord
            {
                Key = ((ulong)high << 32) | low,
                Tag = tag,
                Data = data,
                Terminator = terminator,
            });

            if (i < count - 1)
            {
                low = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
                pos += 4;
            }
        }

        return new GameDatabase(records, cipher);
    }

    /// <summary>
    /// Write the database out. Records are ordered by key, which is the order the
    /// game's own files are in — the engine binary searches them, so an
    /// out-of-order file simply would not resolve. It also has to be
    /// deterministic because the cipher counter runs across records, which makes
    /// order decide the ciphertext.
    /// </summary>
    public void Save(string path)
    {
        using var stream = File.Create(path);
        Write(stream);
    }

    /// <summary>
    /// The file as bytes, for a caller writing it somewhere other than a path —
    /// into a zip entry, say.
    /// </summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        Write(stream);
        return stream.ToArray();
    }

    private void Write(Stream stream)
    {
        Records.Sort((a, b) => a.Key.CompareTo(b.Key));

        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // BinaryWriter is little-endian for primitives on every platform, which
        // matches the format.
        writer.Write((uint)Records.Count);
        writer.Write((uint)(Records[0].Key & 0xFFFFFFFF));

        var counter = 0;
        for (var i = 0; i < Records.Count; i++)
        {
            var record = Records[i];

            writer.Write((uint)(record.Key >> 32));
            writer.Write(record.Tag);
            writer.Write((uint)record.Data.Length);

            var encoded = new byte[record.Data.Length];
            DatabaseCipher.Encode(record.Data, encoded, Cipher.Bytes, ref counter);
            writer.Write(encoded);

            writer.Write(record.Terminator);

            if (i < Records.Count - 1)
                writer.Write((uint)(Records[i + 1].Key & 0xFFFFFFFF));
        }
    }

    /// <summary>
    /// Save over an existing file without risking it: written to a temporary file
    /// first and moved into place, so an interrupted write can't leave a
    /// half-finished database where the game expects a working one.
    /// </summary>
    public void SaveAtomic(string path)
    {
        var temp = path + ".tmp";
        try
        {
            Save(temp);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Take a one-time backup of a database beside itself, <c>.bin</c> becoming
    /// <c>.bak</c>. Returns the backup's path when one was made, null when there
    /// already was one.
    ///
    /// Only ever written once, and never overwritten: the point is to preserve
    /// the file as it was before this app first touched it. A second backup
    /// taken after an edit would capture the edit and lose the original.
    /// </summary>
    public static string? EnsureBackup(string path)
    {
        var backup = BackupPathFor(path);
        if (backup is null || File.Exists(backup)) return null;

        File.Copy(path, backup);
        return backup;
    }

    /// <summary>The backup path for a database: <c>.bin</c> becomes <c>.bak</c>.</summary>
    public static string BackupPathFor(string path) => Path.ChangeExtension(path, ".bak");

    /// <summary>
    /// Put the backup back, replacing the working database. Used to start again
    /// from the file as it was before this app first touched it.
    ///
    /// The copy lands on a temporary file and is then moved into place, so a
    /// failure part-way leaves the existing database intact rather than deleting
    /// it and failing to write the replacement.
    /// </summary>
    public static void RestoreFromBackup(string path)
    {
        var backup = BackupPathFor(path);
        if (!File.Exists(backup))
            throw new FileNotFoundException("There's no backup to restore from.", backup);

        var temp = path + ".restore";
        try
        {
            File.Copy(backup, temp, overwrite: true);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// The game's own database under an install directory, newest season first.
    /// Backups are ignored — they don't end in <c>.bin</c>.
    /// </summary>
    public static string? FindInGameDirectory(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return null;

        try
        {
            return Directory
                .EnumerateFiles(gameDir, "gamedatabases*.bin", SearchOption.AllDirectories)
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The record at a dotted path, or null when the database has no such entry.</summary>
    public DatabaseRecord? Find(string path) =>
        _byKey.TryGetValue(Fnv1a.Hash(path), out var record) ? record : null;

    /// <summary>
    /// The record with this key, for one whose path isn't known.
    ///
    /// Normally a record is found by hashing its path, which is the readable way
    /// and the one to prefer. But the file holds no paths, so a path that cannot
    /// be guessed cannot be recovered — and a few records are only ever
    /// identified by the key itself, as the game's own database editors export
    /// them. This is how those are reached.
    /// </summary>
    public DatabaseRecord? FindByKey(ulong key) =>
        _byKey.TryGetValue(key, out var record) ? record : null;

    /// <summary>Text at a dotted path, or <paramref name="fallback"/> when absent or empty.</summary>
    public string Text(string path, string fallback = "")
    {
        var record = Find(path);
        return record is null || record.Data.Length == 0 ? fallback : record.Text;
    }

    /// <summary>Integer at a dotted path, or <paramref name="fallback"/> when absent or unparsable.</summary>
    public int Int(string path, int fallback = 0) =>
        int.TryParse(Text(path), out var value) ? value : fallback;
}
