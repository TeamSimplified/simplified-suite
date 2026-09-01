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
/// A parsed .material file. Surfaces the things a reader can confidently
/// extract today: header, shader EAID, uniform/sampler names, texture
/// EAIDs paired with samplers, and the float-defaults pool. Sections we
/// haven't fully decoded (per-parameter records, the optional second
/// record bank) are preserved as raw byte ranges so the editor can pass
/// them through unchanged when writing.
/// </summary>
public sealed class MaterialFile
{
    public required MaterialHeader Header { get; init; }
    public required string ShaderEaid { get; init; }

    /// <summary>Uniform parameter names, in file order.</summary>
    public required IReadOnlyList<string> UniformNames { get; init; }

    /// <summary>Sampler bindings paired with their LOD0 texture EAIDs.</summary>
    public required IReadOnlyList<SamplerBinding> Samplers { get; init; }

    /// <summary>
    /// Float-defaults pool, as raw 32-bit values. The pool also holds
    /// the occasional uint32 boolean — UI surfaces both interpretations.
    /// </summary>
    public required IReadOnlyList<float> FloatDefaults { get; init; }

    /// <summary>Raw bytes of the per-parameter records region (offset 0x20 .. offset_D).</summary>
    public required ReadOnlyMemory<byte> RecordsRegion { get; init; }

    /// <summary>Whole file bytes — kept so writer milestones can patch in place.</summary>
    public required ReadOnlyMemory<byte> RawBytes { get; init; }

    /// <summary>True if the LOD1 EAID pool is a byte-exact duplicate of LOD0.</summary>
    public required bool Lod1IsExactDuplicate { get; init; }
}

public sealed record SamplerBinding(string Name, string TextureEaid);
