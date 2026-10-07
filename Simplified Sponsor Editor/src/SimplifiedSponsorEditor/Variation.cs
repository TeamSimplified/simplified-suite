using System;
using System.IO;
using Simplified.Shared;
using SimplifiedSponsorEditor.Core.Package;

namespace SimplifiedSponsorEditor;

/// <summary>
/// One of the six variations of a sponsor's artwork: the file somebody picked,
/// what has been done to it, and how large it sits in each of the two textures
/// it ends up in.
///
/// The file itself is never written to. A variation is a path and a set of
/// changes, exactly as a decal was in v1.1 — so the SVG somebody dragged in is
/// still their SVG afterwards, and opening the variation again carries on from
/// where it was left rather than from the untouched file.
/// </summary>
public sealed class Variation
{
    /// <summary>
    /// How much of the texture the artwork spans by default, as a fraction of
    /// its shorter way across.
    ///
    /// Not a number of its own: it is the margin the packager leaves, said the
    /// other way round. A variation nobody has touched the scale on therefore
    /// packages exactly as v1.1 packaged it, and if that margin is ever changed
    /// this follows it.
    /// </summary>
    public const double DefaultSpan = 1 - 2 * SponsorPackager.DecalMargin;

    /// <summary>The narrowest the artwork may be squeezed, and the widest.</summary>
    public const double MinSpan = 0.10;
    public const double MaxSpan = 1.00;

    private string? _sourcePath;

    /// <summary>The file somebody picked, or null while the slot is empty.</summary>
    public string? SourcePath
    {
        get => _sourcePath;
        set
        {
            if (string.Equals(_sourcePath, value, StringComparison.OrdinalIgnoreCase)) return;

            _sourcePath = value;

            // A different file means the copy the project is holding is of the
            // old one. Forgetting its name is what makes the next save take a
            // fresh copy rather than leave the project pointing at artwork
            // nobody chose.
            StoredName = null;
        }
    }

    /// <summary>
    /// What this one is called inside the project, once it has been saved —
    /// e.g. <c>a1b2c3d4.svg</c>. Null until then.
    ///
    /// A name of its own rather than the file's: two sponsors can import logos
    /// called logo.svg, and a project is one folder. Assigned once and kept, so
    /// saving twice does not copy the same artwork in twice.
    /// </summary>
    public string? StoredName { get; set; }

    /// <summary>
    /// Which parts were dropped and which were repainted. Vector only: a PNG
    /// has no parts to pick, so this stays empty for one.
    /// </summary>
    public SvgEdits Edits { get; set; } = new();

    /// <summary>
    /// A colour laid over the whole picture, as <c>#RRGGBB</c>. Bitmap only —
    /// where a vector can have one shape repainted, a PNG has nothing to pick,
    /// so the colour applies to all of it or to none.
    /// </summary>
    public string? Overlay { get; set; }

    /// <summary>How much of the car texture the artwork spans.</summary>
    public double CarSpan { get; set; } = DefaultSpan;

    /// <summary>How much of the menu thumbnail the artwork spans.</summary>
    public double MenuSpan { get; set; } = DefaultSpan;

    public bool IsEmpty => string.IsNullOrWhiteSpace(SourcePath);

    /// <summary>
    /// Whether this one has parts to pick. Decided by the extension rather than
    /// by reading the file: the open dialog only offers these two, and a file
    /// renamed to lie about itself fails at the reader with a clearer complaint
    /// than it would make here.
    /// </summary>
    public bool IsVector =>
        SourcePath is { } path &&
        Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase);

    public string Name => SourcePath is { } path ? Path.GetFileName(path) : "";

    /// <summary>A copy, for a window to work on until somebody keeps it.</summary>
    public Variation Copy() => new()
    {
        SourcePath = SourcePath,
        StoredName = StoredName,
        Edits = Edits.Copy(),
        Overlay = Overlay,
        CarSpan = CarSpan,
        MenuSpan = MenuSpan,
    };
}
