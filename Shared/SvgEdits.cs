using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Simplified.Shared;

/// <summary>
/// What somebody did to a drawing: which of its pieces they dropped, and which
/// they repainted.
///
/// Kept apart from the file it describes so the file can stay exactly as it
/// arrived. The alternative — writing the edited drawing back out as the
/// project's copy — would mean a deleted part was gone for good, and the second
/// visit to the editor would start from somebody's earlier decisions with no way
/// back to the artwork they were made on.
///
/// Parts are addressed by their position in <see cref="SvgPicture.Parts"/>,
/// which walks the drawing in paint order. That is a function of the file, so
/// the same file always numbers its parts the same way; a <em>different</em>
/// file under the same name would renumber them, which is why the edits live
/// beside the source they belong to and are replaced whenever it is.
/// </summary>
public sealed class SvgEdits
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    /// <summary>Indices of the parts that were deleted.</summary>
    public List<int> Removed { get; set; } = [];

    /// <summary>
    /// Parts that were repainted, as <c>#RRGGBB</c>. Only the ones that changed:
    /// a part left alone is not listed, so re-reading the source picks up its
    /// own colour.
    /// </summary>
    public Dictionary<int, string> Colours { get; set; } = [];

    /// <summary>Whether this says anything at all.</summary>
    [JsonIgnore]
    public bool IsEmpty => Removed.Count == 0 && Colours.Count == 0;

    /// <summary>
    /// A separate set saying the same thing, for a window to work on until
    /// somebody keeps it. Both members are rebuilt rather than handed over, so
    /// cancelling leaves the original untouched.
    /// </summary>
    public SvgEdits Copy() => new() { Removed = [.. Removed], Colours = new(Colours) };

    /// <summary>
    /// The edits beside a source file, or an empty set when there are none —
    /// including when the file is unreadable, since a drawing with no edits is a
    /// state the app can show and a failure to open one is not.
    /// </summary>
    public static SvgEdits Read(string path)
    {
        if (!File.Exists(path)) return new SvgEdits();

        try
        {
            return JsonSerializer.Deserialize<SvgEdits>(File.ReadAllText(path), Json) ?? new SvgEdits();
        }
        catch
        {
            return new SvgEdits();
        }
    }

    /// <summary>
    /// Write them beside their source, or delete the file when there is nothing
    /// to say — so a drawing imported whole leaves no sidecar claiming it was
    /// touched.
    /// </summary>
    public void Write(string path)
    {
        if (IsEmpty)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>
    /// The drawing with these applied: the dropped parts missing and the
    /// repainted ones repainted.
    ///
    /// <see cref="SvgPicture.Parts"/> hands back new parts each time, so
    /// painting them here cannot reach the caller's drawing.
    /// </summary>
    public DrawingGroup ApplyTo(Drawing drawing)
    {
        var parts = SvgPicture.Parts(drawing);
        var kept = new List<SvgPart>(parts.Count);

        for (var i = 0; i < parts.Count; i++)
        {
            if (Removed.Contains(i)) continue;

            var part = parts[i];
            if (Colours.TryGetValue(i, out var hex) && Parse(hex) is { } colour) part.Paint(colour);
            kept.Add(part);
        }

        return SvgPicture.ToDrawing(kept);
    }

    /// <summary>How a colour is written here.</summary>
    public static string Format(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    /// <summary>
    /// The colour a <c>#RRGGBB</c> names, or null if it is not one. Anything
    /// hand-edited into the file that cannot be read leaves that part its own
    /// colour rather than failing the whole drawing.
    /// </summary>
    public static Color? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var hex = text.TrimStart('#');
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit)) return null;

        return Color.FromRgb(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }
}
