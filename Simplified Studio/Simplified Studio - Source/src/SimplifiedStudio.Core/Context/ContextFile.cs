namespace SimplifiedStudio.Core.Context;

/// <summary>One named slot of a context, and the material bound to it.</summary>
public sealed record ContextSlot(string Name, string MaterialEaid);

/// <summary>
/// A parsed Codemasters Ego <c>.context</c> blob.
///
/// A context is a table of named material slots — "body", "wheel", "visor" —
/// each bound to a material EAID. It is the middle link of the chain a livery is
/// made of: a context names the slots, each material names its samplers, and
/// each sampler names a texture.
///
/// Slots may share a material, and most contexts have some that do.
/// </summary>
public sealed class ContextFile
{
    /// <summary>Slot count as the header declares it, in file order.</summary>
    public required IReadOnlyList<ContextSlot> Slots { get; init; }

    /// <summary>
    /// How many distinct materials the slots draw on. Lower than the slot count
    /// whenever slots share a material, which most contexts do.
    /// </summary>
    public required int DistinctMaterialCount { get; init; }

    /// <summary>
    /// The third header DWORD. Its meaning is not established — it is neither a
    /// file offset nor either string pool's length in the files examined — so it
    /// is surfaced rather than interpreted.
    /// </summary>
    public required uint HeaderValue { get; init; }

    /// <summary>Whole file bytes, kept so a caller can still show the raw form.</summary>
    public required ReadOnlyMemory<byte> RawBytes { get; init; }
}
