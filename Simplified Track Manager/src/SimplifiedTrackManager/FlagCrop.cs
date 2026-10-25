using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimplifiedTrackManager;

/// <summary>
/// A round flag cut from a rectangular one, for the countries the game has no
/// round flag for — it has those only for its tracks' countries.
///
/// The centre square of the flag, scaled into a circle with a smooth edge and
/// transparent corners, the way the game's own round track flags are drawn.
/// Cut from the largest flag the game has (268×152) so the scaling has detail
/// to work from.
/// </summary>
public static class FlagCrop
{
    /// <summary>The round flag, <paramref name="size"/> pixels across.</summary>
    public static BitmapSource Round(string pngPath, int size)
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.UriSource = new Uri(Path.GetFullPath(pngPath));
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.EndInit();

        var side = Math.Min(source.PixelWidth, source.PixelHeight);
        var square = new CroppedBitmap(source, new Int32Rect(
            (source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            var area = new Rect(0, 0, size, size);
            dc.PushClip(new EllipseGeometry(area));
            dc.DrawImage(square, area);
            dc.Pop();
        }

        var round = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        round.Render(visual);
        round.Freeze();
        return round;
    }

    /// <summary>
    /// A chequered flag, which the game has none of: <paramref name="columns"/>
    /// squares across and as many down as the shape takes, black and white from
    /// a black top-left. Each square's edges fall on whole pixels and are drawn
    /// hard, so even the 28-pixel round one is sharp. <paramref name="round"/>
    /// cuts it to a circle with transparent corners, as the round flags are.
    /// </summary>
    public static BitmapSource Chequered(int width, int height, int columns, bool round = false)
    {
        var rows = Math.Max(1, (int)Math.Round(columns * (double)height / width));
        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
        using (var dc = visual.RenderOpen())
        {
            if (round)
            {
                // The circle itself is smoothed; only the squares are hard.
                dc.PushClip(new EllipseGeometry(new Rect(0, 0, width, height)));
            }
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                if ((row + column) % 2 != 0) continue;
                int x0 = column * width / columns, x1 = (column + 1) * width / columns;
                int y0 = row * height / rows, y1 = (row + 1) * height / rows;
                dc.DrawRectangle(Brushes.Black, null, new Rect(x0, y0, x1 - x0, y1 - y0));
            }
            if (round) dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Write a picture as a PNG.</summary>
    public static void Save(BitmapSource picture, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
