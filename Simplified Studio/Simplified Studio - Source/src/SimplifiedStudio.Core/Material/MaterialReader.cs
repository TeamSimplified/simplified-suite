using System.Text;

// Ported from the Simplified Material Editor, which is where the .material
// format was reverse-engineered — see its docs/material-format-spec.md. Copied
// rather than referenced: the two apps ship and version separately, and a
// project reference across solutions would couple their release cycles.
//
// Only the read side is here. The spec is explicit that it is sufficient for a
// reader and NOT for a writer — per-record layouts and the float-pool sizing
// are undecoded — so this app parses materials to display them and continues to
// import and export them as raw bytes.

namespace SimplifiedStudio.Core.Material;

/// <summary>
/// Parses a .material file into <see cref="MaterialFile"/>. Strict on
/// the parts the spec doc covers (header, shader EAID position,
/// PerMaterial trailer); tolerant on the rest (records region passes
/// through as raw bytes).
/// </summary>
public static class MaterialReader
{
    private const string Trailer = "PerMaterial";
    private static readonly byte[] TrailerBytes = Encoding.ASCII.GetBytes(Trailer + "\0");

    public static MaterialFile Read(byte[] bytes) => Read(new ReadOnlyMemory<byte>(bytes));

    public static MaterialFile Read(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length < MaterialHeader.SizeBytes + TrailerBytes.Length)
            throw new InvalidDataException($"file too short to be a .material ({bytes.Length} bytes)");

        var span = bytes.Span;
        var header = MaterialHeader.Read(span);

        // Zero-parameter files (e.g. f1_vehicle_shadow) are degenerate:
        // they end at the shader EAID's null terminator with NO PerMaterial
        // trailer. Handle them separately — no records, no strings, no pool.
        if (header.ParameterCount == 0)
            return ReadShaderOnly(bytes, header);

        // Sanity: trailer at end-of-file for all real materials.
        var tail = span[^TrailerBytes.Length..];
        if (!tail.SequenceEqual(TrailerBytes))
            throw new InvalidDataException("missing 'PerMaterial\\0' trailer — not a .material file");

        // Records region: 0x20 .. offset_D. Pass through as raw bytes.
        if (header.OffsetD < MaterialHeader.SizeBytes || header.OffsetD > bytes.Length)
            throw new InvalidDataException($"header offset_D=0x{header.OffsetD:X} out of file bounds");
        var records = bytes[MaterialHeader.SizeBytes..(int)header.OffsetD];

        // Float defaults pool: offset_D .. offset_E, as uint32 slots.
        if (header.OffsetE < header.OffsetD || header.OffsetE > bytes.Length)
            throw new InvalidDataException(
                $"header offset_E=0x{header.OffsetE:X} not between offset_D and EOF");
        var floats = ReadFloatPool(span[(int)header.OffsetD..(int)header.OffsetE]);

        // Shader EAID lives after a back-reference DWORD that mirrors offset_A.
        // We don't trust a fixed position for it — we scan for the next
        // "eaid://shader_package/" string starting at offset_E (the first byte
        // past the float-defaults pool, where the per-shader region begins).
        var (shaderEaid, scanCursor) = FindShaderEaid(span, (int)header.OffsetE);

        // From scanCursor through the start of the PerMaterial trailer,
        // every null-terminated ASCII string belongs to one of:
        //   uniform name  (starts with "me_", not an EAID)
        //   sampler name  (starts with "me_", not an EAID)
        //   texture EAID  (starts with "eaid://")
        var trailerStart = bytes.Length - TrailerBytes.Length;
        var rawStrings = ReadNullTerminatedStrings(span[scanCursor..trailerStart]);

        var meStrings = rawStrings.Where(s => s.StartsWith("me_", StringComparison.Ordinal)).ToList();
        var eaidStrings = rawStrings
            .Where(s => s.StartsWith("eaid://", StringComparison.Ordinal))
            .ToList();

        // EAID pool is LOD0 followed by LOD1 (byte-exact duplicate in every
        // observed sample). If it's odd-length or the halves don't match,
        // fall back to treating everything as LOD0 and flag it.
        var lod0Count = eaidStrings.Count / 2;
        var lod1Duplicate = lod0Count > 0
                         && eaidStrings.Count == lod0Count * 2
                         && eaidStrings.Take(lod0Count).SequenceEqual(eaidStrings.Skip(lod0Count));
        var lod0 = lod1Duplicate ? eaidStrings.Take(lod0Count).ToList() : eaidStrings;

        // The last N me_ strings are sampler names (one per LOD0 EAID);
        // the rest are uniform names. This holds across every shader in
        // the 114-file reference sample.
        var samplerCount = lod0.Count;
        if (samplerCount > meStrings.Count)
            throw new InvalidDataException(
                $"more texture EAIDs ({samplerCount}) than me_* names ({meStrings.Count}) — file may be malformed");

        var uniforms = meStrings.Take(meStrings.Count - samplerCount).ToList();
        var samplerNames = meStrings.Skip(meStrings.Count - samplerCount).ToList();
        var samplers = samplerNames
            .Zip(lod0, (name, eaid) => new SamplerBinding(name, eaid))
            .ToList();

        return new MaterialFile
        {
            Header = header,
            ShaderEaid = shaderEaid,
            UniformNames = uniforms,
            Samplers = samplers,
            FloatDefaults = floats,
            RecordsRegion = records,
            RawBytes = bytes,
            Lod1IsExactDuplicate = lod1Duplicate,
        };
    }

    private static MaterialFile ReadShaderOnly(ReadOnlyMemory<byte> bytes, MaterialHeader header)
    {
        // Layout for the shadow-only case is just: Header + ShaderEAID\0
        // The shader EAID begins right after the header.
        var span = bytes.Span;
        var rest = span[MaterialHeader.SizeBytes..];
        var nul = rest.IndexOf((byte)0);
        if (nul < 0) throw new InvalidDataException("zero-param material missing shader EAID terminator");
        var shaderEaid = System.Text.Encoding.ASCII.GetString(rest[..nul]);

        return new MaterialFile
        {
            Header = header,
            ShaderEaid = shaderEaid,
            UniformNames = Array.Empty<string>(),
            Samplers = Array.Empty<SamplerBinding>(),
            FloatDefaults = Array.Empty<float>(),
            RecordsRegion = ReadOnlyMemory<byte>.Empty,
            RawBytes = bytes,
            Lod1IsExactDuplicate = false,
        };
    }

    private static (string eaid, int nextCursor) FindShaderEaid(ReadOnlySpan<byte> span, int searchFrom)
    {
        // Scan backwards from offset_E to find the start of the shader EAID —
        // it always begins with "eaid://shader_package/". The back-reference
        // DWORD sits immediately before it.
        ReadOnlySpan<byte> needle = "eaid://shader_package/"u8;
        int idx = span[..Math.Min(span.Length, searchFrom + 256)].IndexOf(needle);
        if (idx < 0 || idx > searchFrom)
        {
            // Try scanning the whole file as a last resort.
            idx = span.IndexOf(needle);
            if (idx < 0)
                throw new InvalidDataException("could not locate shader EAID string");
        }
        // Read forward to the null terminator.
        var rest = span[idx..];
        var nul = rest.IndexOf((byte)0);
        if (nul < 0) throw new InvalidDataException("shader EAID is not null-terminated");
        var s = Encoding.ASCII.GetString(rest[..nul]);
        return (s, idx + nul + 1);
    }

    private static List<float> ReadFloatPool(ReadOnlySpan<byte> region)
    {
        var count = region.Length / 4;
        var list = new List<float>(count);
        for (var i = 0; i < count; i++)
            list.Add(BitConverter.ToSingle(region.Slice(i * 4, 4)));
        return list;
    }

    private static List<string> ReadNullTerminatedStrings(ReadOnlySpan<byte> region)
    {
        var list = new List<string>();
        var i = 0;
        while (i < region.Length)
        {
            // Skip stray null padding between strings (rare, but harmless).
            if (region[i] == 0) { i++; continue; }
            var rest = region[i..];
            var nul = rest.IndexOf((byte)0);
            if (nul < 0) break; // unterminated trailing run — bail
            var s = Encoding.ASCII.GetString(rest[..nul]);
            // Only keep ASCII-printable strings; anything else is binary leak-through.
            if (LooksLikePrintableAscii(s)) list.Add(s);
            i += nul + 1;
        }
        return list;
    }

    private static bool LooksLikePrintableAscii(string s)
    {
        if (s.Length < 2) return false;
        foreach (var c in s)
            if (c < 0x20 || c > 0x7E) return false;
        return true;
    }
}
