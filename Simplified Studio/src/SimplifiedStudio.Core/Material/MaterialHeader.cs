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
/// The 32-byte fixed header at the start of every .material file.
/// 8 little-endian DWORDs. See docs/material-format-spec.md § 2.
/// </summary>
public readonly record struct MaterialHeader(
    uint ParameterCount,   // [0x00] uniforms + samplers
    uint Flag,             // [0x04] 0 only when ParameterCount=0
    uint OffsetA,          // [0x08] first per-parameter record start
    uint OffsetB,          // [0x0C]
    uint OffsetC,          // [0x10]
    uint OffsetD,          // [0x14] end of records / start of float defaults
    uint OffsetE,          // [0x18] LOD0/LOD1 boundary
    uint OffsetF)          // [0x1C] position of "PerMaterial\0" trailer
{
    public const int SizeBytes = 32;

    public static MaterialHeader Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < SizeBytes)
            throw new ArgumentException($"material header needs {SizeBytes} bytes, got {bytes.Length}");
        return new MaterialHeader(
            BitConverter.ToUInt32(bytes[0x00..]),
            BitConverter.ToUInt32(bytes[0x04..]),
            BitConverter.ToUInt32(bytes[0x08..]),
            BitConverter.ToUInt32(bytes[0x0C..]),
            BitConverter.ToUInt32(bytes[0x10..]),
            BitConverter.ToUInt32(bytes[0x14..]),
            BitConverter.ToUInt32(bytes[0x18..]),
            BitConverter.ToUInt32(bytes[0x1C..]));
    }
}
