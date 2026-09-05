using System.Buffers.Binary;
using System.Text;

namespace SimplifiedTeamEditor.Core.GameDatabase;

/// <summary>One entry: a hashed path, a type tag, and its payload.</summary>
public sealed class DatabaseRecord
{
    public required ulong Key { get; init; }
    public required byte Tag { get; init; }
    public required byte Terminator { get; init; }
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>The payload read as text, which is how every value in here is stored.</summary>
    public string Text => Encoding.UTF8.GetString(Data);
}

/// <summary>
/// F1 25's <c>gamedatabases.bin</c> — the record store the game keeps entrant
/// data in, team colours among it.
///
/// A record is addressed by the FNV-1a 64 hash of a dotted path, so nothing in
/// the file names anything: <c>entrants.teams.team_3.primary_colour</c> exists
/// only as a number. Records are held in hash order because the engine binary
/// searches them, which is also why writing one back has to preserve that order.
///
/// Payloads are obscured with a repeating-key Vigenère cipher. The key stream
/// runs continuously across the whole file rather than restarting per record,
/// so records cannot be decoded independently or reordered without re-encoding
/// everything after them.
///
/// Layout, little-endian:
/// <code>
///   count            u32
///   key low          u32     of the first record
///   per record:
///     key high       u32
///     tag            u8
///     length         u32
///     data           length bytes, ciphered
///     terminator     u8
///     key low        u32     of the NEXT record, omitted after the last
/// </code>
///
/// Ported from the launcher's team_palette.py. Its CLI half is not here.
/// </summary>
public sealed class GameDatabaseFile
{
    /// <summary>
    /// The cipher key. Not a secret — it ships in the game and in every tool
    /// that reads these files; it only stops the payloads being greppable.
    /// </summary>
    private static readonly byte[] CipherKey = Encoding.ASCII.GetBytes("ZnrH7yg2qTC4qj42Jcq9tG5MYmBZEZ");

    private const ulong FnvOffset64 = 0xCBF29CE484222325;
    private const ulong FnvPrime64 = 0x100000001B3;

    private readonly List<DatabaseRecord> _records = new();
    private readonly Dictionary<ulong, DatabaseRecord> _byKey = new();

    public IReadOnlyList<DatabaseRecord> Records => _records;

    /// <summary>The hash a dotted path resolves to.</summary>
    public static ulong HashOf(string path)
    {
        var h = FnvOffset64;
        foreach (var b in Encoding.UTF8.GetBytes(path))
            h = unchecked((h ^ b) * FnvPrime64);
        return h;
    }

    public static GameDatabaseFile Load(string path) => FromBytes(File.ReadAllBytes(path));

    public static GameDatabaseFile FromBytes(byte[] buffer)
    {
        if (buffer.Length < 8) throw new InvalidDataException("Too short to be gamedatabases.bin.");

        var db = new GameDatabaseFile();
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var low = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));

        var pos = 8;
        var counter = 0;

        for (var i = 0; i < count; i++)
        {
            var high = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos)); pos += 4;
            var tag = buffer[pos]; pos += 1;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos)); pos += 4;

            var data = new byte[length];
            for (var j = 0; j < length; j++)
            {
                data[j] = unchecked((byte)(buffer[pos + j] - CipherKey[counter]));
                counter = (counter + 1) % CipherKey.Length;
            }
            pos += length;

            var terminator = buffer[pos]; pos += 1;

            var record = new DatabaseRecord
            {
                Key = ((ulong)high << 32) | low,
                Tag = tag,
                Terminator = terminator,
                Data = data,
            };
            db._records.Add(record);
            db._byKey[record.Key] = record;

            if (i < count - 1)
            {
                low = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos));
                pos += 4;
            }
        }

        return db;
    }

    /// <summary>
    /// The file as bytes. Records are sorted by key first, because the engine
    /// binary searches them and an out-of-order file would simply not resolve.
    /// </summary>
    public byte[] ToBytes()
    {
        _records.Sort((a, b) => a.Key.CompareTo(b.Key));

        var output = new MemoryStream();
        WriteU32(output, (uint)_records.Count);
        WriteU32(output, (uint)(_records[0].Key & 0xFFFFFFFF));

        var counter = 0;
        for (var i = 0; i < _records.Count; i++)
        {
            var record = _records[i];
            WriteU32(output, (uint)(record.Key >> 32));
            output.WriteByte(record.Tag);
            WriteU32(output, (uint)record.Data.Length);

            foreach (var b in record.Data)
            {
                output.WriteByte(unchecked((byte)(b + CipherKey[counter])));
                counter = (counter + 1) % CipherKey.Length;
            }

            output.WriteByte(record.Terminator);

            if (i < _records.Count - 1)
                WriteU32(output, (uint)(_records[i + 1].Key & 0xFFFFFFFF));
        }

        return output.ToArray();
    }

    public void Save(string path) => File.WriteAllBytes(path, ToBytes());

    /// <summary>The record at a dotted path, or null when there isn't one.</summary>
    public DatabaseRecord? Find(string path)
        => _byKey.TryGetValue(HashOf(path), out var record) ? record : null;

    private static void WriteU32(Stream to, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        to.Write(b);
    }
}
