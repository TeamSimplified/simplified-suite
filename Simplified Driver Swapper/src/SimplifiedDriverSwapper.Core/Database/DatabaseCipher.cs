using System.Buffers.Binary;
using System.Text;

namespace SimplifiedDriverSwapper.Core.Database;

/// <summary>
/// The stream cipher guarding record payloads: each byte is shifted by the next
/// byte of a repeating key.
///
/// The key counter runs <b>continuously across records</b> rather than resetting
/// per record, so payloads can only be decoded by walking the file in order —
/// and re-encoding one record means re-encoding everything after it.
/// </summary>
public static class DatabaseCipher
{
    /// <summary>A game's cipher key. Which one a file uses is detected, not declared.</summary>
    public sealed record Key(string Game, byte[] Bytes);

    /// <summary>Known keys, newest first — detection tries them all regardless.</summary>
    public static readonly IReadOnlyList<Key> KnownKeys = new[]
    {
        new Key("F1 25", Encoding.ASCII.GetBytes("ZnrH7yg2qTC4qj42Jcq9tG5MYmBZEZ")),
        new Key("F1 25 (beta)", Encoding.ASCII.GetBytes("wWCw3v8Emg3XSAcTDHw8ySa3VvosA5")),
        new Key("F1 24", Encoding.ASCII.GetBytes("nT26GLL3V7pWtaY6hh6hGsu3Jx2ZQE")),
        new Key("F1 23", Encoding.ASCII.GetBytes("PxA6v23AL0INY7sA1Ab701AA2A44c7")),
    };

    /// <summary>Decode in place, advancing the running counter.</summary>
    public static void Decode(ReadOnlySpan<byte> source, Span<byte> destination, byte[] key, ref int counter)
    {
        for (var i = 0; i < source.Length; i++)
        {
            destination[i] = (byte)(source[i] - key[counter]);
            counter = (counter + 1) % key.Length;
        }
    }

    /// <summary>Encode in place, advancing the running counter.</summary>
    public static void Encode(ReadOnlySpan<byte> source, Span<byte> destination, byte[] key, ref int counter)
    {
        for (var i = 0; i < source.Length; i++)
        {
            destination[i] = (byte)(source[i] + key[counter]);
            counter = (counter + 1) % key.Length;
        }
    }

    /// <summary>
    /// Work out which game wrote a file, by decoding its first few hundred
    /// records with each key and seeing which one yields sensible booleans.
    /// Returns null when nothing scores well enough to be trusted.
    /// </summary>
    public static Key? Detect(ReadOnlySpan<byte> head)
    {
        Key? best = null;
        var bestScore = 0;

        foreach (var key in KnownKeys)
        {
            var score = Score(head, key.Bytes);
            if (score <= bestScore) continue;
            bestScore = score;
            best = key;
        }

        return bestScore >= MinimumScore ? best : null;
    }

    /// <summary>
    /// Boolean records decode to the literal text "True"/"False", which is a
    /// strong enough tell: the wrong key turns them into noise.
    /// </summary>
    private const int MinimumScore = 5;
    private const int SampleRecords = 300;

    private static int Score(ReadOnlySpan<byte> buf, byte[] key)
    {
        if (buf.Length < 8) return 0;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        var pos = 8;
        var counter = 0;
        var score = 0;

        var limit = Math.Min(count, SampleRecords);
        Span<byte> decoded = stackalloc byte[64];

        for (var i = 0; i < limit; i++)
        {
            if (pos + 9 > buf.Length) break;
            pos += 4;                                   // high key word
            var tag = buf[pos];
            pos += 1;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[pos..]);
            pos += 4;
            if (length < 0 || pos + length + 1 > buf.Length) break;

            if (tag is 0x05 or 0x06 && length <= decoded.Length)
            {
                var slice = decoded[..length];
                var c = counter;
                Decode(buf.Slice(pos, length), slice, key, ref c);
                if (IsBooleanText(slice)) score++;
            }

            // The counter must advance over every payload, scored or not.
            counter = (counter + length) % key.Length;
            pos += length + 1;                          // payload + terminator

            if (i < count - 1)
            {
                if (pos + 4 > buf.Length) break;
                pos += 4;                               // next record's low key word
            }
        }

        return score;
    }

    private static bool IsBooleanText(ReadOnlySpan<byte> value) =>
        value.SequenceEqual("True"u8) || value.SequenceEqual("False"u8) ||
        value.SequenceEqual("true"u8) || value.SequenceEqual("false"u8);
}
