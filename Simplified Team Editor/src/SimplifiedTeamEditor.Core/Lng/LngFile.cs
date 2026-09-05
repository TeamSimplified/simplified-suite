using System.Buffers.Binary;
using System.Text;

namespace SimplifiedTeamEditor.Core.Lng;

/// <summary>
/// An F1 language file: the key/value table the game reads every piece of
/// on-screen text from.
///
/// Layout, all big-endian:
///
/// <code>
///   magic  4 bytes, kept as-is
///   size   4 bytes, the whole file
///   HSHS   bucket count, hash seed, hash multiplier
///   HSHT   one (start index, count) pair per bucket
///   SIDA   one (key offset, value offset) pair per entry
///   SIDB   the keys, NUL-terminated, back to back
///   LNGB   the values, likewise
/// </code>
///
/// Two ways to write one back, because the two seasons need different things.
/// Editing an existing entry only moves offsets around, so <see cref="BuildSaveData"/>
/// leaves the hash table alone. Adding an entry — which is what the 2026 keys
/// are — changes which bucket everything lands in, so <see cref="BuildRebuiltData"/>
/// re-hashes the lot.
///
/// Ported from the launcher's merge_lng.py. Its merge half is not here: this
/// app edits values and adds keys, and never reconciles two mods' files.
/// </summary>
public sealed class LngFile
{
    private byte[] _data = Array.Empty<byte>();

    private int _sidaOffset;
    private int _sidbOffset;
    private int _lngbOffset;
    private int _sidbSize;
    private int _lngbSize;

    private byte[] _magic = new byte[4];
    private uint _hashSeed;
    private uint _hashMultiplier;

    /// <summary>(key offset, value offset) pairs, in file order.</summary>
    private (int Key, int Value)[] _offsets = Array.Empty<(int, int)>();
    private byte[] _keysBlob = Array.Empty<byte>();
    private byte[] _valuesBlob = Array.Empty<byte>();

    /// <summary>Every entry in the file. Later duplicates win, as the game's own reader does.</summary>
    public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);

    public static LngFile Load(string path)
    {
        var lng = new LngFile();
        lng.Parse(File.ReadAllBytes(path));
        return lng;
    }

    public static LngFile FromBytes(byte[] data)
    {
        var lng = new LngFile();
        lng.Parse(data);
        return lng;
    }

    private void Parse(byte[] data)
    {
        _data = data;
        Array.Copy(data, _magic, 4);

        var hshs = FindSection("HSHS").Offset;
        // buckets is re-derived on rebuild, so only the seed and multiplier are
        // carried forward — a file's hash has to keep using its own constants.
        _hashSeed = ReadU32(hshs + 12);
        _hashMultiplier = ReadU32(hshs + 16);

        _sidaOffset = FindSection("SIDA").Offset;
        (_sidbOffset, _sidbSize) = FindSection("SIDB");
        (_lngbOffset, _lngbSize) = FindSection("LNGB");

        var count = (int)ReadU32(_sidaOffset + 8);
        _offsets = new (int, int)[count];
        for (var i = 0; i < count; i++)
        {
            var at = _sidaOffset + 12 + i * 8;
            _offsets[i] = ((int)ReadU32(at), (int)ReadU32(at + 4));
        }

        _keysBlob = data[(_sidbOffset + 8)..(_sidbOffset + 8 + _sidbSize)];
        _valuesBlob = data[(_lngbOffset + 8)..(_lngbOffset + 8 + _lngbSize)];

        Entries.Clear();
        foreach (var (keyAt, valueAt) in _offsets)
            Entries[ReadCString(_keysBlob, keyAt)] = ReadCString(_valuesBlob, valueAt);
    }

    /// <summary>
    /// The file with some values replaced, and everything else untouched.
    ///
    /// Only the value blob is rebuilt: the keys keep their offsets, so the hash
    /// table still points where it did and does not need recomputing.
    /// </summary>
    public byte[] BuildSaveData(IReadOnlyDictionary<string, string> edits)
    {
        var values = new MemoryStream();
        var valueOffsetByKey = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (keyAt, _) in _offsets)
        {
            var key = ReadCString(_keysBlob, keyAt);
            var value = edits.TryGetValue(key, out var edited) ? edited : Entries[key];

            valueOffsetByKey[key] = (int)values.Length;
            var bytes = Encoding.UTF8.GetBytes(value);
            values.Write(bytes, 0, bytes.Length);
            values.WriteByte(0);
        }

        // SIDA keeps its key offsets and takes the new value offsets.
        var sida = _data[_sidaOffset.._sidbOffset];
        for (var i = 0; i < _offsets.Length; i++)
        {
            var key = ReadCString(_keysBlob, _offsets[i].Key);
            WriteU32(sida, 12 + i * 8, (uint)_offsets[i].Key);
            WriteU32(sida, 12 + i * 8 + 4, (uint)valueOffsetByKey[key]);
        }

        var valuesBlob = values.ToArray();

        var output = new MemoryStream();
        output.Write(_data, 0, _sidaOffset);
        output.Write(sida, 0, sida.Length);
        output.Write(_data, _sidbOffset, _lngbOffset - _sidbOffset);
        WriteSection(output, "LNGB", valuesBlob);
        var lngbTotal = 8 + _lngbSize;
        output.Write(_data, _lngbOffset + lngbTotal, _data.Length - (_lngbOffset + lngbTotal));

        return output.ToArray();
    }

    /// <summary>
    /// The file rebuilt from a complete key/value map, so keys can be added.
    ///
    /// Everything is re-hashed and laid out in bucket order, because the game
    /// looks an entry up by hashing the key and then scanning its bucket — so
    /// where an entry sits has to agree with <see cref="Hash"/> exactly.
    /// </summary>
    public byte[] BuildRebuiltData(IReadOnlyDictionary<string, string> all)
    {
        var items = all.ToList();
        var buckets = items.Count / 2 + 1;

        var table = new List<KeyValuePair<string, string>>[buckets];
        for (var i = 0; i < buckets; i++) table[i] = new List<KeyValuePair<string, string>>();
        foreach (var pair in items) table[Hash(pair.Key, buckets)].Add(pair);

        var keys = new MemoryStream();
        var values = new MemoryStream();
        var sida = new MemoryStream();
        var hsht = new MemoryStream();

        var running = 0;
        foreach (var bucket in table)
        {
            // Per bucket: where its entries start in the flattened list, and how
            // many. An empty bucket stores a start of zero rather than the
            // running index, which is what the game's writer does.
            WriteU32(hsht, (uint)(bucket.Count > 0 ? running : 0));
            WriteU32(hsht, (uint)bucket.Count);

            foreach (var (key, value) in bucket)
            {
                WriteU32(sida, (uint)keys.Length);
                WriteU32(sida, (uint)values.Length);
                WriteCString(keys, key);
                WriteCString(values, value);
            }
            running += bucket.Count;
        }

        var output = new MemoryStream();
        output.Write(_magic, 0, 4);
        WriteU32(output, 0);                       // total size, patched at the end

        output.Write(Encoding.ASCII.GetBytes("HSHS"), 0, 4);
        WriteU32(output, 12);
        WriteU32(output, (uint)buckets);
        WriteU32(output, _hashSeed);
        WriteU32(output, _hashMultiplier);

        WriteSection(output, "HSHT", hsht.ToArray());
        output.Write(Encoding.ASCII.GetBytes("SIDA"), 0, 4);
        WriteU32(output, (uint)(items.Count * 8));
        WriteU32(output, (uint)items.Count);
        var sidaBytes = sida.ToArray();
        output.Write(sidaBytes, 0, sidaBytes.Length);

        WriteSection(output, "SIDB", keys.ToArray());
        WriteSection(output, "LNGB", values.ToArray());

        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)result.Length);
        return result;
    }

    /// <summary>
    /// The key hash: seed, then multiply and add each character. It is djb2
    /// with the file's own seed and multiplier rather than the usual 5381/33,
    /// so both are read from HSHS rather than assumed.
    ///
    /// Characters are taken as UTF-16 code units, matching the Python port's
    /// ord(). Keys are ASCII in every shipped file, so the two agree.
    /// </summary>
    private uint Hash(string key, int buckets)
    {
        var h = _hashSeed;
        foreach (var c in key) h = unchecked(h * _hashMultiplier + c);
        return h % (uint)buckets;
    }

    private (int Offset, int Size) FindSection(string tag)
    {
        var needle = Encoding.ASCII.GetBytes(tag);
        var at = _data.AsSpan().IndexOf(needle);
        if (at < 0) throw new InvalidDataException($"Language file has no {tag} section.");
        return (at, (int)ReadU32(at + 4));
    }

    private uint ReadU32(int at) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at));

    private static void WriteU32(byte[] buffer, int at, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(at), value);

    private static void WriteU32(Stream to, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        to.Write(b);
    }

    private static void WriteSection(Stream to, string tag, byte[] payload)
    {
        to.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
        WriteU32(to, (uint)payload.Length);
        to.Write(payload, 0, payload.Length);
    }

    private static void WriteCString(Stream to, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        to.Write(bytes, 0, bytes.Length);
        to.WriteByte(0);
    }

    private static string ReadCString(byte[] blob, int start)
    {
        var end = Array.IndexOf(blob, (byte)0, start);
        if (end < 0) end = blob.Length;
        return Encoding.UTF8.GetString(blob, start, end - start);
    }
}
