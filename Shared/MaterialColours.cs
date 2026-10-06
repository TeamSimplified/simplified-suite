using System;
using System.Collections.Generic;
using System.Windows.Media;
using SimplifiedStudio.Core.Material;

namespace Simplified.Shared;

/// <summary>One colour a material carries, and where it lives in the file.</summary>
public sealed class MaterialColour
{
    public required string Label { get; init; }

    /// <summary>The shader uniform it feeds, for anyone who wants to know.</summary>
    public required string Uniform { get; init; }

    /// <summary>Absolute file offset of the red lane; green and blue follow it.</summary>
    public required int Offset { get; init; }

    public required Color Value { get; init; }
}

/// <summary>
/// The colours a material lets you change.
///
/// Addressed by absolute file offset, keyed by shader. That is not laziness: the
/// format spec is explicit that the float-defaults pool does not start at
/// offset_D for every shader — several put a second records bank in between —
/// and nothing decoded so far says which float belongs to which named uniform.
/// A generic "slot N of the pool" would be a guess, and a wrong guess here
/// writes four bytes into the middle of somebody's material.
///
/// So each entry below is a shader whose layout was measured, and a material
/// whose shader is not listed simply has no colours to offer. Adding one is a
/// diff of two files that differ only by the colour, which is how this one was
/// found: the ten tyre sidewalls are the same material in ten compounds, and
/// sixteen bytes separate them.
/// </summary>
public static class MaterialColours
{
    private sealed record Field(string Label, string Uniform, int Offset);

    private static readonly (string Shader, Field[] Fields)[] Known =
    [
        // The sidewall's logo colour: the band that says which compound a tyre
        // is. Read back from the game's own nine, the values are the Pirelli
        // range as it was named — hypersoft pink through superhard orange.
        ("tyre_sidewall", [new Field("Sidewall colour", "me_logoColour", 0x38C)]),
    ];

    /// <summary>
    /// The colours this material has, with their current values. Empty for a
    /// shader nobody has measured, and empty when what is at the offset does
    /// not look like a colour — which guards against a shader that shares a
    /// name but not a layout.
    /// </summary>
    public static IReadOnlyList<MaterialColour> In(MaterialFile material)
    {
        var found = new List<MaterialColour>();
        var bytes = material.RawBytes.Span;

        foreach (var (shader, fields) in Known)
        {
            if (!material.ShaderEaid.Contains($"/{shader}/", StringComparison.Ordinal)) continue;

            foreach (var field in fields)
            {
                if (field.Offset + 12 > bytes.Length) continue;

                var r = BitConverter.ToSingle(bytes[field.Offset..]);
                var g = BitConverter.ToSingle(bytes[(field.Offset + 4)..]);
                var b = BitConverter.ToSingle(bytes[(field.Offset + 8)..]);

                if (!IsLane(r) || !IsLane(g) || !IsLane(b)) continue;

                found.Add(new MaterialColour
                {
                    Label = field.Label,
                    Uniform = field.Uniform,
                    Offset = field.Offset,
                    Value = Color.FromRgb(Lane(r), Lane(g), Lane(b)),
                });
            }
        }

        return found;
    }

    /// <summary>
    /// The material's bytes with one colour changed. Three floats overwritten in
    /// place — the file keeps its length and every other byte, which is what
    /// makes this safe while the rest of the format is undecoded.
    /// </summary>
    public static byte[] With(MaterialFile material, MaterialColour colour, Color value)
    {
        var bytes = material.RawBytes.ToArray();

        if (colour.Offset + 12 > bytes.Length)
            throw new InvalidOperationException("This material is too short to hold that colour.");

        Write(bytes, colour.Offset, value.R);
        Write(bytes, colour.Offset + 4, value.G);
        Write(bytes, colour.Offset + 8, value.B);
        return bytes;
    }

    private static void Write(byte[] into, int at, byte channel) =>
        BitConverter.TryWriteBytes(into.AsSpan(at), channel / 255f);

    /// <summary>
    /// Whether a float reads as a colour channel. The lanes the game ships sit
    /// in 0..1; anything else at this offset means the layout is not the one
    /// that was measured, and the colour is not offered rather than shown wrong.
    /// </summary>
    private static bool IsLane(float value) =>
        float.IsFinite(value) && value is >= 0f and <= 1f;

    /// <summary>
    /// A lane as a byte. Taken as straight 0..1 rather than put through a gamma
    /// curve: read that way, the game's nine compounds come out as the colours
    /// they are called — pink, purple, red, yellow, white, ice blue, orange,
    /// green, blue — and under any other reading they do not.
    /// </summary>
    private static byte Lane(float value) => (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
