using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Simplified.Shared;

/// <summary>
/// One piece of a drawing: a shape, how it is painted, and what it is clipped
/// by.
///
/// The paint is settable because that is the point of having these — a drawing
/// pulled apart into its pieces can have one of them recoloured or dropped and
/// put back together again. The shape is not: a part is the shape it is, and
/// something else drawn in its place is a different part.
/// </summary>
public sealed class SvgPart
{
    public SvgPart(Geometry geometry, Brush? fill, Pen? stroke, double opacity, Geometry? clip)
    {
        Geometry = geometry;
        Fill = fill;
        Stroke = stroke;
        Opacity = opacity;
        Clip = clip;
    }

    public Geometry Geometry { get; }

    public Brush? Fill { get; set; }

    public Pen? Stroke { get; set; }

    public double Opacity { get; }

    public Geometry? Clip { get; }

    /// <summary>
    /// The one colour this part reads as, or null when it is painted with
    /// something that is not one colour — a gradient, or a picture.
    /// </summary>
    public Color? Colour =>
        (Fill as SolidColorBrush)?.Color ?? (Stroke?.Brush as SolidColorBrush)?.Color;

    /// <summary>
    /// Paint it. Whichever of fill and stroke the part already has is the one
    /// that changes: a shape drawn as an outline is its outline, and giving it
    /// a fill instead would be drawing something else.
    /// </summary>
    public void Paint(Color colour)
    {
        if (Fill is not null)
        {
            Fill = new SolidColorBrush(colour);
            return;
        }

        if (Stroke is null) return;

        // Cloned rather than built: a new Pen would lose the dashes, the caps
        // and the joins along with the colour.
        var pen = Stroke.CloneCurrentValue();
        pen.Brush = new SolidColorBrush(colour);
        Stroke = pen;
    }
}

/// <summary>
/// Reading an SVG as a picture.
///
/// An SVG has no resolution, so one has to be chosen. Everything downstream of
/// an import wants a raster — the game stores compressed blocks — so the drawing
/// is rendered once, at the size the texture it is going into wants, and the
/// rest of the app never knows it was not a PNG.
///
/// What is rendered is the artwork, cropped to it. Two reasons, one measured and
/// one wanted:
///
///   * SharpVectors lays a drawing out in the <em>viewport</em> the width and
///     height attributes ask for, with the viewBox origin already folded in.
///     Measured: a document saying <c>width="1000" height="359.91"</c> over
///     <c>viewBox="0 0 264.58 95.23"</c> — the same shape at a different scale,
///     which is what Inkscape writes — comes back with bounds of 0,0 1000x359.91,
///     not 264x95. Clipping such a drawing to its viewBox keeps the top-left
///     quarter of it and throws the rest away, which is exactly what happened.
///     Resolving the viewport here would mean redoing the library's own unit
///     arithmetic (measured: 80mm becomes 302.36, so 96 to the inch) and being
///     wrong about it the first time a document uses a unit nobody tested.
///   * The artwork's own edges are the only ones worth keeping. How much blank
///     space an author left around a logo varies by who exported it, and that
///     variation would land in the game as logos of different sizes in the same
///     slot. Cropping to the ink and giving every one the same margin afterwards
///     is what makes them consistent.
/// </summary>
public static class SvgPicture
{
    /// <summary>Whether a path is one of these, by its name.</summary>
    public static bool IsSvg(string path)
        => string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The drawing an SVG describes.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The file holds nothing that can be drawn.
    /// </exception>
    public static DrawingGroup Read(string svgPath)
    {
        // IncludeRuntime false and TextAsGeometry true: text becomes paths, so
        // nothing of SharpVectors' own has to be alongside the app at run time
        // and a font the machine lacks cannot change the drawing.
        var settings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true };

        return new FileSvgReader(settings).Read(svgPath)
               ?? throw new InvalidOperationException("There is nothing in this SVG that can be drawn.");
    }

    /// <summary>
    /// An SVG rasterised with the longest side of its artwork at
    /// <paramref name="longEdge"/> and the other to match, so the drawing keeps
    /// its proportions.
    /// </summary>
    public static BitmapSource Render(string svgPath, int longEdge)
        => Render(Read(svgPath), longEdge);

    /// <summary>
    /// The same for a drawing already in hand — one that has been taken apart
    /// and put back together with pieces changed or missing.
    /// </summary>
    public static BitmapSource Render(Drawing drawing, int longEdge)
    {
        if (longEdge < 1) throw new ArgumentOutOfRangeException(nameof(longEdge));

        // The artwork's own box, in the space the drawing was laid out in — so
        // whatever scale the document asked for, this is right by construction.
        var art = drawing.Bounds;

        if (art.Width <= 0 || art.Height <= 0 || double.IsInfinity(art.Width) || double.IsInfinity(art.Height))
            throw new InvalidOperationException("This SVG has nothing drawn in it.");

        var scale = longEdge / Math.Max(art.Width, art.Height);
        var width = Math.Max(1, (int)Math.Round(art.Width * scale));
        var height = Math.Max(1, (int)Math.Round(art.Height * scale));

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(scale, scale));
            context.PushTransform(new TranslateTransform(-art.X, -art.Y));
            context.DrawDrawing(drawing);
            context.Pop();
            context.Pop();
        }

        // Pbgra32 keeps the alpha, which for a logo is most of the picture.
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// A drawing pulled apart into the shapes it is made of, in the order they
    /// are painted — so the last one in the list is the one on top, and the one
    /// a click should land on.
    ///
    /// The nesting goes away: every part carries its ancestors' transform baked
    /// into its own shape, so a part can be drawn, hit-tested and removed
    /// without the tree it came from.
    /// </summary>
    public static IReadOnlyList<SvgPart> Parts(Drawing drawing)
    {
        var parts = new List<SvgPart>();
        Walk(drawing, Transform.Identity, 1, null, parts);
        return parts;
    }

    /// <summary>The parts put back together, ready to render.</summary>
    public static DrawingGroup ToDrawing(IEnumerable<SvgPart> parts)
    {
        var group = new DrawingGroup();

        foreach (var part in parts)
        {
            Drawing drawing = new GeometryDrawing(part.Fill, part.Stroke, part.Geometry);

            // Only when there is something to say: a group per part would be a
            // deeper tree to walk for nothing.
            if (part.Opacity < 1 || part.Clip is not null)
            {
                var wrap = new DrawingGroup { Opacity = part.Opacity };
                if (part.Clip is not null) wrap.ClipGeometry = part.Clip;
                wrap.Children.Add(drawing);
                drawing = wrap;
            }

            group.Children.Add(drawing);
        }

        return group;
    }

    private static void Walk(
        Drawing drawing, Transform transform, double opacity, Geometry? clip, List<SvgPart> into)
    {
        switch (drawing)
        {
            case DrawingGroup group:
            {
                // A group's clip is in the space its children are drawn in, so
                // it carries the group's own transform as well as its ancestors'.
                // WPF applies ClipGeometry before Transform, and SVG resolves a
                // userSpaceOnUse clip in the space the element's own transform
                // establishes; both say the same thing.
                //
                // Measured, because this was wrong the other way round and the
                // artwork it broke did not look clipped - it looked cropped. A
                // bull drawn under matrix(1.33,0,0,-1.33,0,283) with a clip of
                // 0,0 340x213 came out as its own top-left corner: in the
                // children's space that clip is the whole canvas, 0,0 453x283,
                // but left in the ancestors' space it cut everything past
                // x=340 of artwork reaching x=409.
                var inside = Then(group.Transform, transform);
                var narrowed = Narrow(clip, group.ClipGeometry, inside);

                foreach (var child in group.Children)
                    Walk(child, inside, opacity * group.Opacity, narrowed, into);

                break;
            }

            // The shape's own transform counts towards the pen as well as the
            // ancestors'. SharpVectors hands an element's transform over on the
            // geometry and leaves the pen alone, and WPF then strokes in the
            // space after it — so a stroke of 0.4 inside scale(840) is drawn
            // 0.4 wide where SVG asks for 336. Measured on an Australian flag,
            // whose red diagonals are one such stroke and were simply absent.
            case GeometryDrawing { Geometry: not null } shape:
                into.Add(new SvgPart(
                    Place(shape.Geometry, transform), shape.Brush,
                    Thicken(shape.Pen, Then(shape.Geometry.Transform, transform)), opacity, clip));
                break;

            // Kept as a shape painted with the picture, rather than skipped: an
            // SVG with a bitmap in it is unusual, and dropping it silently would
            // lose part of the artwork on the way through.
            case ImageDrawing { ImageSource: not null } image:
                into.Add(new SvgPart(
                    Place(new RectangleGeometry(image.Rect), transform),
                    new ImageBrush(image.ImageSource), null, opacity, clip));
                break;

            // Text is asked for as geometry, so this should not arrive. If it
            // does, it is still a shape.
            case GlyphRunDrawing { GlyphRun: not null } text:
                into.Add(new SvgPart(
                    Place(text.GlyphRun.BuildGeometry(), transform),
                    text.ForegroundBrush, null, opacity, clip));
                break;
        }
    }

    /// <summary>
    /// An outline at the width it is drawn at once its shape has been moved.
    ///
    /// The transform is baked into the geometry, and a pen's thickness is not
    /// part of a geometry — so a line drawn 0.4 wide inside <c>scale(840)</c>
    /// would come out 0.4 wide instead of 336 and read as nothing at all. That
    /// is an Australian flag with no red in it, which is what it looked like.
    ///
    /// The square root of the determinant: exact for a uniform scale, which is
    /// what these documents use, and the one sensible number where a transform
    /// scales the axes differently — a pen has one thickness, so there is no
    /// answer that is right in both directions.
    /// </summary>
    private static Pen? Thicken(Pen? pen, Transform transform)
    {
        if (pen is null) return null;

        var matrix = transform.Value;
        if (matrix.IsIdentity) return pen;

        var scale = Math.Sqrt(Math.Abs(matrix.Determinant));
        if (double.IsNaN(scale) || scale <= 0 || Math.Abs(scale - 1) < 1e-9) return pen;

        // Cloned rather than built: a new Pen would lose the dashes, the caps
        // and the joins along with the width.
        var scaled = pen.CloneCurrentValue();
        scaled.Thickness = pen.Thickness * scale;
        return scaled;
    }

    /// <summary>A shape moved into the space its ancestors put it in.</summary>
    private static Geometry Place(Geometry geometry, Transform transform)
    {
        var placed = geometry.CloneCurrentValue();
        placed.Transform = Then(placed.Transform, transform);
        return placed;
    }

    /// <summary>
    /// One transform and then the other. A TransformGroup applies its children
    /// in order, so the first one named here happens first.
    /// </summary>
    private static Transform Then(Transform? first, Transform? second)
    {
        if (first is null || first.Value.IsIdentity) return second ?? Transform.Identity;
        if (second is null || second.Value.IsIdentity) return first;

        var group = new TransformGroup();
        group.Children.Add(first);
        group.Children.Add(second);
        return group;
    }

    /// <summary>Two clips are what both of them let through.</summary>
    private static Geometry? Narrow(Geometry? existing, Geometry? extra, Transform transform)
    {
        if (extra is null) return existing;

        var placed = Place(extra, transform);
        return existing is null
            ? placed
            : new CombinedGeometry(GeometryCombineMode.Intersect, existing, placed);
    }
}
