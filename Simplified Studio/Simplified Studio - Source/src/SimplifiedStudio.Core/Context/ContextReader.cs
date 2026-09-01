using System.Text;

namespace SimplifiedStudio.Core.Context;

/// <summary>
/// Parses a <c>.context</c> blob into <see cref="ContextFile"/>.
///
/// The layout, derived by surveying every context in the reference archives and
/// the game's character package — 1,562 files, all agreeing:
///
/// <code>
///   0x00  uint32  slot count
///   0x04  uint32  count of distinct materials — NOT the same number
///   0x08  uint32  purpose unknown; neither an offset nor a pool length
///   0x0C  uint32  zero
///   0x10  uint32  zero
///   0x14          (slots - 1) pairs of uint32: each slot's name offset and its
///                 material's offset, relative to their pools. Slot 0 is
///                 implicit at (0, 0) and has no entry.
///   ...           `slots` null-terminated names
///   ...           `materials` null-terminated EAIDs
/// </code>
///
/// The offset table has to be followed rather than skipped, because several
/// slots routinely bind the same material — a driver suit context has 37 slots
/// sharing 31 materials, and 1,034 of the files surveyed share at least one.
/// Pairing the two string pools positionally works only when every slot happens
/// to have its own material, which is true of vehicle contexts and false of
/// suits.
/// </summary>
public static class ContextReader
{
    private const string EaidPrefix = "eaid://";
    private const int HeaderSize = 20;

    /// <summary>Guards against a wild count turning a stray file into a huge allocation.</summary>
    private const uint MaxSlots = 4096;

    public static ContextFile Read(byte[] bytes) => Read(new ReadOnlyMemory<byte>(bytes));

    public static ContextFile Read(ReadOnlyMemory<byte> bytes)
    {
        var span = bytes.Span;
        if (span.Length < HeaderSize)
            throw new InvalidDataException($"file too short to be a .context ({span.Length} bytes)");

        var slotCount = BitConverter.ToUInt32(span[0x00..]);
        var materialCount = BitConverter.ToUInt32(span[0x04..]);
        var headerValue = BitConverter.ToUInt32(span[0x08..]);

        if (slotCount == 0)
            throw new InvalidDataException("context declares no slots");
        if (slotCount > MaxSlots || materialCount > MaxSlots)
            throw new InvalidDataException($"context declares {slotCount} slots and {materialCount} materials, beyond anything plausible");
        if (materialCount > slotCount)
            throw new InvalidDataException($"context declares more materials ({materialCount}) than slots ({slotCount})");

        var stringsStart = HeaderSize + ((int)slotCount - 1) * 8;
        if (stringsStart >= span.Length)
            throw new InvalidDataException("offset table runs past the end of the file");

        var strings = ReadStrings(span, stringsStart);
        var names = strings.Where(s => !s.Text.StartsWith(EaidPrefix, StringComparison.Ordinal)).ToList();
        var eaids = strings.Where(s => s.Text.StartsWith(EaidPrefix, StringComparison.Ordinal)).ToList();

        // The counts matching is the first real evidence this is a context: a
        // blob of some other kind is very unlikely to hold exactly the declared
        // number of plain strings followed by the declared number of EAIDs.
        if (names.Count != slotCount || eaids.Count != materialCount)
            throw new InvalidDataException(
                $"context declares {slotCount} slot(s) and {materialCount} material(s) "
                + $"but holds {names.Count} name(s) and {eaids.Count} EAID(s)");

        var nameAt = ByRelativeOffset(names);
        var eaidAt = ByRelativeOffset(eaids);

        var slots = new List<ContextSlot>((int)slotCount);
        for (var i = 0; i < slotCount; i++)
        {
            // Slot 0 has no table entry — it is the first of each pool.
            var nameOffset = 0;
            var eaidOffset = 0;
            if (i > 0)
            {
                var at = HeaderSize + (i - 1) * 8;
                nameOffset = (int)BitConverter.ToUInt32(span[at..]);
                eaidOffset = (int)BitConverter.ToUInt32(span[(at + 4)..]);
            }

            // Every offset landing exactly on a string start is the second piece
            // of evidence, and the one that would fail on a coincidence.
            if (!nameAt.TryGetValue(nameOffset, out var name))
                throw new InvalidDataException($"slot {i}'s name offset 0x{nameOffset:X} isn't the start of a name");
            if (!eaidAt.TryGetValue(eaidOffset, out var eaid))
                throw new InvalidDataException($"slot {i}'s material offset 0x{eaidOffset:X} isn't the start of an EAID");

            slots.Add(new ContextSlot(name, eaid));
        }

        return new ContextFile
        {
            Slots = slots,
            DistinctMaterialCount = (int)materialCount,
            HeaderValue = headerValue,
            RawBytes = bytes,
        };
    }

    /// <summary>Each string keyed by its distance from the first in its pool.</summary>
    private static Dictionary<int, string> ByRelativeOffset(List<(int Offset, string Text)> pool)
    {
        var map = new Dictionary<int, string>(pool.Count);
        if (pool.Count == 0) return map;
        var basis = pool[0].Offset;
        foreach (var (offset, text) in pool) map[offset - basis] = text;
        return map;
    }

    private static List<(int Offset, string Text)> ReadStrings(ReadOnlySpan<byte> span, int from)
    {
        var found = new List<(int, string)>();
        var run = new StringBuilder();
        var start = 0;
        for (var i = from; i < span.Length; i++)
        {
            var b = span[i];
            // Printable ASCII only: these files hold names and EAIDs, and
            // anything else is padding or a field that isn't a string.
            if (b >= 0x20 && b < 0x7F)
            {
                if (run.Length == 0) start = i;
                run.Append((char)b);
                continue;
            }
            if (run.Length > 0) { found.Add((start, run.ToString())); run.Clear(); }
        }
        if (run.Length > 0) found.Add((start, run.ToString()));
        return found;
    }
}
