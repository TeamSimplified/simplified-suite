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
