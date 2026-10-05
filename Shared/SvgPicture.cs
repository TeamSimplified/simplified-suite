using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Simplified.Shared;

/// <summary>
/// Reading an SVG as a picture.
///
/// An SVG has no resolution, so one has to be chosen. Everything downstream of
/// an import wants a raster — the game stores compressed blocks — so the drawing
/// is rendered once, here, at the size the texture it is going into wants, and
/// the rest of the app never knows it was not a PNG.
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
    /// An SVG rasterised with the longest side of its artwork at
    /// <paramref name="longEdge"/> and the other to match, so the drawing keeps
    /// its proportions.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The file holds nothing that can be drawn.
    /// </exception>
    public static BitmapSource Render(string svgPath, int longEdge)
    {
        if (longEdge < 1) throw new ArgumentOutOfRangeException(nameof(longEdge));

        // IncludeRuntime false and TextAsGeometry true: text becomes paths, so
        // nothing of SharpVectors' own has to be alongside the app at run time
        // and a font the machine lacks cannot change the drawing.
        var settings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true };

        var drawing = new FileSvgReader(settings).Read(svgPath)
                      ?? throw new InvalidOperationException(
                          "There is nothing in this SVG that can be drawn.");

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
}
