using System.Buffers.Binary;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics.Dds;

namespace EgoEngineLibrary.Texconv;

/// <summary>What had to be done to a picture to make it fit where it was going.</summary>
public enum FitKind
{
    /// <summary>Nothing. It went in as it was.</summary>
    Exact,

    /// <summary>The right shape but another size, so scaled to it.</summary>
    Scaled,

    /// <summary>
    /// Another shape, so padded with transparency to the right one. Nothing is
    /// squashed and nothing is cropped.
    /// </summary>
    Padded,
}

/// <summary>A picture encoded for a texture, and how it got there.</summary>
/// <param name="Path">The DDS written.</param>
/// <param name="Width">What it came out as, which is not always what was asked for.</param>
public sealed record Fit(
    string Path, FitKind How,
    uint SourceWidth, uint SourceHeight,
    uint Width, uint Height)
{
    /// <summary>
    /// Whether the picture's proportions had to change, as against it only being
    /// inset to leave a margin. The first is worth telling somebody about; the
    /// second is the policy working as intended.
    /// </summary>
    public bool ShapeChanged => (long)SourceWidth * Height != (long)SourceHeight * Width;

    /// <summary>One line for a note to the user, or null when nothing happened to it.</summary>
    public string? Describe() => How switch
    {
        FitKind.Scaled => $"scaled from {SourceWidth}×{SourceHeight} to {Width}×{Height}",
        FitKind.Padded when !ShapeChanged =>
            $"inset inside {Width}×{Height} to keep it clear of the edges",
        FitKind.Padded => $"padded from {SourceWidth}×{SourceHeight} to {Width}×{Height} "
                          + "to keep its shape",
        _ => null,
    };
}

/// <summary>
/// Encoding a picture into a texture of a shape it does not have, by fitting it
/// rather than stretching it.
///
/// Two textures need this, for the same reason in different clothes:
///
///   * A texture array slice sits in a fixed stretch of its resource, so only
///     the array's exact size can go in. Telling texconv the size with
///     <c>-w -h</c> hits it, but it resizes each axis on its own, so a wide logo
///     asked for a square comes out squashed. <see cref="Encode"/>.
///   * A standalone texture may be any size, because the resource carries its
///     own — but the quad it is drawn on cannot move, so a texture of another
///     shape is stretched onto it just the same. <see cref="EncodeToShape"/>
///     keeps the picture's resolution and corrects only its shape.
///
/// Either way a picture of the wrong shape is padded with transparency first, at
/// its own resolution, so that any resize afterwards is the same factor on both
/// axes. Only transparent pixels are ever added and nothing is cropped.
///
/// This is how the game does it. Measured: Red Bull's wordmark is 24x9 of opaque
/// pixels inside a 24x24 badge slot and 178x68 inside a 180x180 starting-grid
/// slot, letterboxed rather than filled; and every one of the 641 car sponsor
/// decals is square, 1024x1024.
///
/// The game also never lets its artwork touch an edge. Measured across its own
/// decals: a side margin of between 1.6% and 5.3% of the texture, median 2.0%,
/// and on the menu thumbnails between 4.4% and 25%, median 8.8%. So a margin can
/// be asked for here, and fitting leaves that much of the texture clear on every
/// side.
/// </summary>
public static class SliceFit
{
    /// <summary>
    /// A picture as a DDS of exactly <paramref name="width"/> by
    /// <paramref name="height"/>, for a texture that will take no other size.
    /// </summary>
    /// <param name="work">
    /// A scratch directory this may use as it likes. The returned DDS is in it.
    /// </param>
    /// <param name="margin">
    /// How much of the texture to leave clear on each side, as a fraction of it.
    /// Zero lets the artwork reach the edges.
    /// </param>
    public static Fit Encode(
        string picturePath, string work, ErpGfxSurfaceFormat format, int mipCount,
        uint width, uint height, TexconvRunner runner, double margin = 0)
    {
        var source = PngSize(picturePath);

        // Nothing to do but encode it. Kept as its own case so a picture that
        // was already right goes through exactly the path it always did — which
        // a margin rules out, since leaving room is itself something to do.
        if (margin <= 0 && source is { } exact && exact.Width == width && exact.Height == height)
        {
            return new Fit(
                runner.PngToDds(picturePath, work, format, mipCount),
                FitKind.Exact, exact.Width, exact.Height, width, height);
        }

        // The right shape already, or a size we could not read — either way a
        // forced resize is uniform, or is the best that can be done blind.
        if (source is not { } size
            || Canvas(size.Width, size.Height, width, height, margin) is not { } canvas)
        {
            return new Fit(
                runner.PngToDds(picturePath, work, format, mipCount, width: width, height: height),
                FitKind.Scaled, source?.Width ?? 0, source?.Height ?? 0, width, height);
        }

        var padded = Pad(picturePath, work, canvas.Width, canvas.Height, runner);
        if (padded is null)
        {
            // Padding is the nicety; landing in the texture is not. A picture
            // that could not be padded is better in squashed than absent.
            return new Fit(
                runner.PngToDds(picturePath, work, format, mipCount, width: width, height: height),
                FitKind.Scaled, size.Width, size.Height, width, height);
        }

        return new Fit(
            runner.PngToDds(padded, work, format, mipCount, width: width, height: height),
            FitKind.Padded, size.Width, size.Height, width, height);
    }

    /// <summary>
    /// A picture as a DDS of its own resolution, corrected to a shape.
    ///
    /// For a texture whose size is its own business but whose proportions are
    /// not: a car decal is written at whatever resolution was handed over, which
    /// is how somebody gets a sharper decal than the game ships, but it is drawn
    /// on a quad belonging to the car model and a texture of another shape is
    /// stretched onto that quad.
    /// </summary>
    /// <param name="margin">
    /// How much of the texture to leave clear on each side, as a fraction of it.
    /// </param>
    public static Fit EncodeToShape(
        string picturePath, string work, ErpGfxSurfaceFormat format, int mipCount,
        uint shapeW, uint shapeH, TexconvRunner runner, double margin = 0)
    {
        var source = PngSize(picturePath);

        // Already the shape and no room to leave, or a size we could not read.
        // Encoded as it is, which is what happened to every picture before any
        // of this.
        if (source is not { } size
            || Canvas(size.Width, size.Height, shapeW, shapeH, margin) is not { } canvas)
        {
            var asIs = source ?? (Width: 0u, Height: 0u);
            return new Fit(
                runner.PngToDds(picturePath, work, format, mipCount),
                FitKind.Exact, asIs.Width, asIs.Height, asIs.Width, asIs.Height);
        }

        // The size this comes out as: the shape corrected at the picture's own
        // resolution, with no room left for a margin. The margin is made by
        // padding onto a larger canvas and then landing on this, so leaving room
        // around the artwork costs texture size rather than adding it — a decal
        // stays the resolution it arrived at.
        var natural = Canvas(size.Width, size.Height, shapeW, shapeH) ?? (size.Width, size.Height);

        var padded = Pad(picturePath, work, canvas.Width, canvas.Height, runner);
        if (padded is null)
        {
            return new Fit(
                runner.PngToDds(picturePath, work, format, mipCount),
                FitKind.Exact, size.Width, size.Height, size.Width, size.Height);
        }

        return new Fit(
            runner.PngToDds(padded, work, format, mipCount,
                            width: natural.Item1, height: natural.Item2),
            FitKind.Padded, size.Width, size.Height, natural.Item1, natural.Item2);
    }

    /// <summary>
    /// The size a picture of this size becomes when corrected to that shape, for
    /// saying so before it happens. The same as the picture when it is already
    /// that shape.
    /// </summary>
    public static (uint Width, uint Height) ShapeOf(uint sourceW, uint sourceH, uint shapeW, uint shapeH)
        => Canvas(sourceW, sourceH, shapeW, shapeH) ?? (sourceW, sourceH);

    /// <summary>
    /// What fitting a picture of this size into that shape would do to it,
    /// without doing it.
    ///
    /// For telling somebody in advance, in the same words the import will use
    /// afterwards — the point of having one rule rather than a second opinion in
    /// the interface.
    /// </summary>
    public static FitKind Plan(uint sourceW, uint sourceH, uint targetW, uint targetH,
                               double margin = 0)
        => margin <= 0 && sourceW == targetW && sourceH == targetH ? FitKind.Exact
            : Canvas(sourceW, sourceH, targetW, targetH, margin) is null ? FitKind.Scaled
            : FitKind.Padded;

    /// <summary>
    /// The padding notes as they should be read, which past a few is a count.
    ///
    /// Padding is the common case rather than the exception — three of the five
    /// logo slots are square and most team logos are not, and every car decal is
    /// square while most sponsor logos are wordmarks — so a full set would be
    /// twenty notes, and would bury the warnings that need acting on.
    /// </summary>
    public static IEnumerable<string> Summarise(IReadOnlyList<string> fitted)
    {
        if (fitted.Count <= 3) return fitted;

        return
        [
            $"{fitted.Count} pictures were padded with transparency to fit their shape, which keeps "
            + "them the shape they were drawn rather than squashing them. The game's own artwork "
            + "sits in these textures the same way.",
        ];
    }

    /// <summary>
    /// The canvas a picture needs to be centred on to have the target's shape
    /// and to leave <paramref name="margin"/> of it clear on each side, or null
    /// when it already does.
    ///
    /// The picture itself is untouched, so this only ever adds transparent
    /// pixels: the room for the margin is made by growing the canvas, and the
    /// resize that follows shrinks the artwork into it. Growing rather than
    /// shrinking is what leaves that one resize to do all of the scaling.
    ///
    /// Public so that an interface can say the size something is going to
    /// become, in the same arithmetic that will produce it.
    /// </summary>
    public static (uint Width, uint Height)? Canvas(
        uint sourceW, uint sourceH, uint targetW, uint targetH, double margin = 0)
    {
        if (sourceW == 0 || sourceH == 0 || targetW == 0 || targetH == 0) return null;

        // The share of the texture the artwork is to occupy. Clamped because a
        // margin of half would leave it nothing at all.
        var room = 1.0 - 2 * Math.Clamp(margin, 0, 0.45);

        var needW = (uint)Math.Max(sourceW, Math.Ceiling(sourceW / room));
        var needH = (uint)Math.Max(sourceH, Math.Ceiling(sourceH / room));

        // Cross-multiplied rather than divided: integer arithmetic, and equal
        // products mean the shapes already agree.
        var wide = (long)needW * targetH;
        var tall = (long)needH * targetW;

        var canvas = wide > tall
            ? (needW, (uint)Math.Round(needW * (double)targetH / targetW))
            : ((uint)Math.Round(needH * (double)targetW / targetH), needH);

        // Rounding down would crop, which this never does.
        canvas = (Math.Max(canvas.Item1, needW), Math.Max(canvas.Item2, needH));

        return canvas == (sourceW, sourceH) ? null : canvas;
    }

    /// <summary>
    /// The picture centred on a transparent canvas of that size, as a DDS.
    /// Null when it could not be done, which the caller treats as a reason to
    /// fall back rather than to fail.
    ///
    /// texconv does the decoding — it reads every PNG flavour there is and we
    /// read none — and this only moves the bytes it hands back. Uncompressed
    /// R8G8B8A8 is the one format where a pixel is four bytes at a known
    /// offset, which is what makes the move a row copy.
    /// </summary>
    private static string? Pad(string picturePath, string work, uint width, uint height, TexconvRunner runner)
    {
        try
        {
            var rawDir = Path.Combine(work, "raw");
            var rawPath = runner.PngToDds(picturePath, rawDir, ErpGfxSurfaceFormat.ABGR8, mipCount: 1);

            DdsFile dds;
            using (var stream = File.OpenRead(rawPath)) dds = new DdsFile(stream);

            var sourceW = dds.header.width;
            var sourceH = dds.header.height;
            if (sourceW > width || sourceH > height) return null;

            var row = checked((int)sourceW * 4);
            var needed = row * (long)sourceH;
            if (dds.bdata.Length < needed) return null;

            // Transparent black, and the picture centred in it. Odd leftovers
            // go to the right and the bottom, which is where a half pixel has
            // to go and is what centring in an image editor does.
            var canvas = new byte[checked((int)width * 4 * (int)height)];
            var left = (int)(width - sourceW) / 2;
            var top = (int)(height - sourceH) / 2;

            for (var y = 0; y < sourceH; y++)
            {
                Buffer.BlockCopy(
                    dds.bdata, y * row,
                    canvas, ((top + y) * (int)width + left) * 4,
                    row);
            }

            dds.bdata = canvas;
            dds.header.width = width;
            dds.header.height = height;
            dds.header.pitchOrLinearSize = width * 4;
            dds.header.mipMapCount = Math.Max(dds.header.mipMapCount, 1);

            // Its own name, so the encode that follows cannot be asked to write
            // over the file it is reading.
            var fitDir = Path.Combine(work, "fit");
            Directory.CreateDirectory(fitDir);
            var fitPath = Path.Combine(fitDir,
                Path.GetFileNameWithoutExtension(picturePath) + ".fit.dds");

            using (var file = File.Create(fitPath)) dds.Write(file, -1);
            return fitPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A PNG's size, straight out of its IHDR, or null for anything that is not
    /// one. Thirteen bytes read rather than an image decoded, because all this
    /// decides is whether there is any fitting to do.
    /// </summary>
    public static (uint Width, uint Height)? PngSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[24];
            if (fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;

            ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
            if (!head[..8].SequenceEqual(signature)) return null;

            return (BinaryPrimitives.ReadUInt32BigEndian(head[16..20]),
                    BinaryPrimitives.ReadUInt32BigEndian(head[20..24]));
        }
        catch
        {
            return null;
        }
    }
}
