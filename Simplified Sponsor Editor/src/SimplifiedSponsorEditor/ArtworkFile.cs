using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimplifiedSponsorEditor;

/// <summary>
/// Which files can be artwork, and reading the ones that are pictures.
///
/// Said once, because three places ask: the file dialog offering them, the
/// variation deciding whether it is a drawing, and the canvas opening one. When
/// these were three separate lists of extensions, adding a format meant finding
/// all three.
///
/// Every picture format here is read by Windows itself — the same imaging
/// component Explorer's thumbnails come from — so supporting one costs no
/// library and nothing beside the exe. The price is WebP, which Windows reads
/// only with Microsoft's own extension installed. Windows 11 ships it; where it
/// is missing the file fails to open with a sentence saying how to get it,
/// rather than a codec error.
/// </summary>
public static class ArtworkFile
{
    /// <summary>
    /// The longest side a picture is worked on at. The largest texture a
    /// sponsor goes into is 1024 across, so a bigger picture only ever reaches
    /// the game scaled down to it; scaling it once on the way in means taking
    /// it apart, hovering over it and repainting it all cost a megapixel rather
    /// than the twelve a phone photograph is.
    /// </summary>
    public const int Longest = 1024;

    private static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".tif", ".tiff", ".webp"];

    /// <summary>What the open dialog offers, every kind together first.</summary>
    public const string Filter =
        "Artwork (*.svg;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp)|*.svg;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp"
        + "|Vector (*.svg)|*.svg"
        + "|Picture (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp";

    /// <summary>
    /// Whether a path is a drawing. Decided by the extension rather than by
    /// reading the file: a file renamed to lie about itself fails at the
    /// reader, with a clearer complaint than it would make here.
    /// </summary>
    public static bool IsVector(string path) =>
        Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a path is anything the canvas can open.</summary>
    public static bool IsArtwork(string path) =>
        IsVector(path) || Pictures.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A picture as four bytes a pixel — blue, green, red, alpha, the alpha not
    /// multiplied in — the right way up and no larger than <see cref="Longest"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The file could not be read, with a sentence saying why.
    /// </exception>
    public static (byte[] Pixels, int Width, int Height) Read(string path)
    {
        BitmapFrame frame;

        try
        {
            // Read whole and closed again at once, so the file is not held
            // open for as long as the picture is on screen — somebody exporting
            // a new version of it over the top should not be told it is in use.
            using var stream = File.OpenRead(path);

            // The first frame. A TIFF can carry several pages; the first is
            // the picture, and the rest are thumbnails or layers somebody's
            // software saved alongside it.
            frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                                         BitmapCacheOption.OnLoad).Frames[0];
        }
        // Everything but a file that is missing or locked, which already say so
        // in words. What Windows' imaging throws for a format it has no reader
        // for depends on how far it got — not supported, a bad format, or a COM
        // error with a number in it — and none of those is a sentence.
        catch (Exception problem) when (problem is not (IOException or UnauthorizedAccessException))
        {
            throw new InvalidOperationException(Unreadable(path), problem);
        }

        BitmapSource picture = frame;

        // Down first, then round: the turn is exact either way, and turning a
        // twelve-megapixel photo only to shrink it afterwards is twelve times
        // the work.
        var longest = Math.Max(picture.PixelWidth, picture.PixelHeight);

        if (longest > Longest)
        {
            var scale = (double)Longest / longest;
            picture = new TransformedBitmap(picture, new ScaleTransform(scale, scale));
        }

        picture = Upright(picture, Orientation(frame));

        // Straight alpha, which is what the pieces are worked out in: a
        // premultiplied edge pixel is darker than the colour it is an edge of,
        // and would read as a mix with black. A JPEG has no alpha at all and
        // comes out solid, which is what it is.
        var converted = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        return (pixels, width, height);
    }

    /// <summary>Four bytes a pixel back into something WPF can draw.</summary>
    public static BitmapSource Bitmap(byte[] pixels, int width, int height)
    {
        // 96 across the board. A JPEG from a camera says 72 and a scan says
        // 300, and WPF sizes a picture by those — so two pictures of the same
        // pixels would be drawn at different sizes wherever nothing stretches
        // them. Pixels are what matter here; the number is made to not matter.
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Which way up the camera says the picture is, as the EXIF orientation
    /// number: 1 for as stored, up to 8. A phone stores a portrait photo
    /// sideways and writes this down rather than turning the pixels, and a
    /// logo photographed off a wall arrives that way.
    /// </summary>
    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata is BitmapMetadata metadata
                   && metadata.ContainsQuery("System.Photo.Orientation")
                   && metadata.GetQuery("System.Photo.Orientation") is ushort turn
                ? turn
                : 1;
        }
        catch
        {
            // Formats with no metadata of this kind throw rather than answer;
            // a picture that cannot say which way up it is, is the way it is.
            return 1;
        }
    }

    /// <summary>The picture turned and flipped the way its orientation says.</summary>
    private static BitmapSource Upright(BitmapSource picture, int orientation)
    {
        // Mirrored first and then turned, which is how the EXIF numbering is
        // defined: 2, 4, 5 and 7 are the mirrored ones.
        var (mirror, turn) = orientation switch
        {
            2 => (true, 0),
            3 => (false, 180),
            4 => (true, 180),
            5 => (true, 270),
            6 => (false, 90),
            7 => (true, 90),
            8 => (false, 270),
            _ => (false, 0),
        };

        if (!mirror && turn == 0) return picture;

        var steps = new TransformGroup();
        if (mirror) steps.Children.Add(new ScaleTransform(-1, 1));
        if (turn != 0) steps.Children.Add(new RotateTransform(turn));

        return new TransformedBitmap(picture, steps);
    }

    /// <summary>
    /// Why a picture would not open. A WebP has two possible reasons and no
    /// cheap way to tell them apart — the file is damaged, or this PC has no
    /// WebP reader at all — so it is given both, the fixable one first.
    /// </summary>
    private static string Unreadable(string path) =>
        Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase)
            ? "Windows couldn't read this WebP picture. If WebP pictures don't open "
              + "anywhere on this PC, Microsoft's free \"WebP Image Extensions\" from the "
              + "Microsoft Store adds them. Saving the picture as a PNG works either way."
            : "This file isn't a picture Windows can read, even though its name says it is.";
}
