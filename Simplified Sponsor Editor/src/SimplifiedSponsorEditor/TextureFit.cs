using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimplifiedSponsorEditor;

/// <summary>
/// Artwork inside a texture: scaled to span a share of it, centred, with the
/// rest left clear.
///
/// One place rather than two. The frames draw this at full size and a variation
/// slot draws the same thing an inch across, and they have to agree — a slot
/// that showed the file while the frame showed the file placed in a square
/// would be two answers to where the logo sits.
/// </summary>
public static class TextureFit
{
    /// <summary>
    /// Uniform, so the logo keeps its proportions and the span is of whichever
    /// way round it runs out of room first — the same thing SliceFit does by
    /// padding onto a larger canvas, said forwards.
    /// </summary>
    /// <param name="longest">
    /// The most pixels to spend on the longer side. Past the largest texture
    /// anything here puts out the composite is one for one; a thumbnail asks
    /// for far less, because a 1024 square drawn for a 50px square is 400 times
    /// the pixels anybody will see.
    /// </param>
    public static BitmapSource Compose(
        BitmapSource art, uint width, uint height, double span, double longest = 2048)
    {
        var shown = Math.Min(1, longest / Math.Max(width, height));
        var w = Math.Max(1, (int)Math.Round(width * shown));
        var h = Math.Max(1, (int)Math.Round(height * shown));

        var room = Math.Clamp(span, Variation.MinSpan, Variation.MaxSpan);
        var fit = Math.Min(w * room / art.PixelWidth, h * room / art.PixelHeight);

        var artWidth = art.PixelWidth * fit;
        var artHeight = art.PixelHeight * fit;

        var visual = new DrawingVisual();

        // The span shrinks the artwork on the way in, and the default filter
        // makes a visible job of even a little of that.
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        using (var context = visual.RenderOpen())
        {
            context.DrawImage(art, new Rect(
                (w - artWidth) / 2, (h - artHeight) / 2, artWidth, artHeight));
        }

        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
