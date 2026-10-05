using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EgoEngineLibrary.Texconv;
using SimplifiedSponsorEditor.Core;
using SimplifiedSponsorEditor.Core.Package;
using SimplifiedSponsorEditor.Core.Sponsors;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The two frames a decal is actually drawn in, side by side.
///
/// A sponsor goes into the game twice: a square texture painted on the car, and
/// a wide thumbnail in the customisation menu. They are different shapes and
/// different sizes, and the one pane showing one picture could not answer the
/// question that matters when artwork goes in — where does it land, and is it
/// still the right shape in both places.
///
/// So this view shows each texture at its own proportions with its size under
/// it, for the game's own artwork and for a replacement alike. What it draws for
/// a replacement is the texture as it will be packaged, not the file as it was
/// picked: the padding and the margin come from the same arithmetic the packager
/// uses, so what is on screen is what the game gets.
/// </summary>
public partial class MainWindow
{
    /// <summary>Which of the three the pane is showing.</summary>
    private enum DecalViewKind
    {
        /// <summary>The replacement artwork, as large as the pane allows.</summary>
        Artwork,

        /// <summary>Both textures the decal goes into, at their own proportions.</summary>
        Frames,

        /// <summary>Where it lands on the car. Not built yet.</summary>
        Car,
    }

    /// <summary>
    /// What the config asks for, with anything it does not recognise reading as
    /// the frames — a hand-edited name with a typo in it should land somewhere
    /// sensible rather than on an empty pane.
    /// </summary>
    private DecalViewKind View => _config.DecalView switch
    {
        "Artwork" => DecalViewKind.Artwork,
        "Car" => DecalViewKind.Car,
        _ => DecalViewKind.Frames,
    };

    /// <summary>
    /// The menu archive's own pictures. A second reader rather than a second
    /// use of the first: each holds one archive, and these are the thumbnails
    /// from sponsors_ui.erp rather than the decals from sponsors.erp.
    ///
    /// Made on demand. Someone who never opens this view never pays the 19 MB
    /// load, and the archive is only read at all when a frame needs it.
    /// </summary>
    private DecalPreview? _menuPreview;

    private DecalPreview? MenuPreview
    {
        get
        {
            if (_menuPreview is not null) return _menuPreview;

            var path = Path.Combine(AppPaths.OriginalsDir, SponsorCatalogue.MenuArchiveName);
            if (!File.Exists(path)) return null;

            return _menuPreview = new DecalPreview(path);
        }
    }

    // The true texture sizes behind each frame, which is what the frames are
    // drawn to the shape of. Held because the shape is wanted again on every
    // resize, and recomputing it would mean re-reading an archive to do it.
    private (uint Width, uint Height)? _carTexture;
    private (uint Width, uint Height)? _menuTexture;

    /// <summary>
    /// Both frames for this decal. Replaces the single preview while the Frames
    /// chip is picked.
    /// </summary>
    private void ShowFrames(SponsorDecal decal, bool replaced)
    {
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        FramesView.Visibility = Visibility.Visible;

        var own = _preview?.Get(decal.CarIdentifier);

        // ── On the car ───────────────────────────────────────────────
        var artwork = replaced && _project is not null ? _project.PicturePath(decal.Name) : null;

        if (artwork is not null && File.Exists(artwork) && Picture(artwork) is { } mine)
        {
            // The texture keeps the resolution handed over, corrected to the
            // decal's shape — so a sharper decal than the game's stays sharper.
            var texture = own is null
                ? (Width: (uint)mine.PixelWidth, Height: (uint)mine.PixelHeight)
                : SliceFit.ShapeOf((uint)mine.PixelWidth, (uint)mine.PixelHeight, own.Width, own.Height);

            Fill(CarFrame, CarImage, CarSizeLabel, CarSourceLabel,
                 Packaged(mine, texture.Width, texture.Height),
                 texture, (mine.PixelWidth, mine.PixelHeight));
            _carTexture = texture;
        }
        else if (own?.PngPath is not null && Picture(own.PngPath) is { } theirs)
        {
            Fill(CarFrame, CarImage, CarSizeLabel, CarSourceLabel,
                 theirs, (own.Width, own.Height), null);
            _carTexture = (own.Width, own.Height);
        }
        else
        {
            Empty(CarFrame, CarImage, CarSizeLabel, CarSourceLabel, "couldn't be read");
            _carTexture = null;
        }

        // ── In the menu ──────────────────────────────────────────────
        if (!decal.HasMenuThumbnail)
        {
            Empty(MenuFrame, MenuImage, MenuSizeLabel, MenuSourceLabel, "this one isn't in the menu");
            _menuTexture = null;
        }
        else
        {
            // The letterboxed copy made when the artwork was chosen. Where a
            // project has none the packager leaves the game's thumbnail alone,
            // so this shows the game's too rather than a picture that will not
            // be there.
            var menuArt = replaced && _project is not null ? _project.MenuPicturePath(decal.Name) : null;
            var menuOwn = MenuPreview?.Get(decal.MenuIdentifier!);

            if (menuArt is not null && File.Exists(menuArt) && Picture(menuArt) is { } mineMenu)
            {
                var texture = (Width: SponsorPackager.MenuWidth, Height: SponsorPackager.MenuHeight);

                Fill(MenuFrame, MenuImage, MenuSizeLabel, MenuSourceLabel,
                     Packaged(mineMenu, texture.Width, texture.Height),
                     texture, (mineMenu.PixelWidth, mineMenu.PixelHeight));
                _menuTexture = texture;
            }
            else if (menuOwn?.PngPath is not null && Picture(menuOwn.PngPath) is { } theirsMenu)
            {
                Fill(MenuFrame, MenuImage, MenuSizeLabel, MenuSourceLabel,
                     theirsMenu, (menuOwn.Width, menuOwn.Height), null);
                _menuTexture = (menuOwn.Width, menuOwn.Height);
            }
            else
            {
                Empty(MenuFrame, MenuImage, MenuSizeLabel, MenuSourceLabel, "couldn't be read");
                _menuTexture = null;
            }
        }

        LayOutFrames();
        ApplyBackdrop();
    }

    /// <summary>
    /// A replacement as the packager will write it: the artwork scaled into the
    /// texture, centred, with the margin left clear around it.
    ///
    /// The scale comes from <see cref="SliceFit.Canvas"/> rather than from the
    /// margin directly, so this and the packager cannot drift apart — it is the
    /// canvas the artwork would be padded onto, and the texture over that canvas
    /// is how much the artwork shrinks on the way in.
    ///
    /// Drawn at the texture's own size, with a cap only to bound what an
    /// enormous import would allocate. It was capped at 512 and that was wrong:
    /// a 1024 decal was composited at half size, so its artwork went 1024 to 450
    /// to the 255 it is drawn at — two resamples and under twice the pixels it
    /// is shown with — while the game's own went 1024 straight to 255 in one, at
    /// four times. The two sat side by side and the imported one looked soft,
    /// which is a fair thing to notice when the whole point of the view is
    /// comparing them.
    /// </summary>
    private static BitmapSource Packaged(BitmapSource art, uint textureW, uint textureH)
    {
        // Past the largest texture anything here puts out, so in practice the
        // composite is one for one and the only scaling is the margin's.
        const double cap = 2048;

        var canvas = SliceFit.Canvas((uint)art.PixelWidth, (uint)art.PixelHeight,
                                     textureW, textureH, SponsorPackager.DecalMargin);

        var share = canvas is { } c && c.Width > 0
            ? textureW / (double)c.Width
            : textureW / (double)art.PixelWidth;

        var shown = Math.Min(1, cap / Math.Max(textureW, textureH));
        var width = Math.Max(1, (int)Math.Round(textureW * shown));
        var height = Math.Max(1, (int)Math.Round(textureH * shown));

        var artWidth = art.PixelWidth * share * shown;
        var artHeight = art.PixelHeight * share * shown;

        var visual = new DrawingVisual();

        // The margin shrinks the artwork by a tenth on the way in, and the
        // default filter makes a visible job of even that.
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        using (var context = visual.RenderOpen())
        {
            context.DrawImage(art, new Rect(
                (width - artWidth) / 2, (height - artHeight) / 2, artWidth, artHeight));
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void Fill(
        Border frame, Image image, TextBlock size, TextBlock source,
        BitmapSource picture, (uint Width, uint Height) texture, (int Width, int Height)? from)
    {
        image.Source = picture;
        frame.Visibility = Visibility.Visible;

        size.Text = $"{texture.Width} × {texture.Height}";

        // Where the artwork came from, when the texture is not that size. The
        // number somebody recognises is the one they picked.
        source.Text = from is { } f && (f.Width != texture.Width || f.Height != texture.Height)
            ? $"from {f.Width} × {f.Height}"
            : "";
    }

    private static void Empty(Border frame, Image image, TextBlock size, TextBlock source, string why)
    {
        image.Source = null;
        frame.Visibility = Visibility.Hidden;
        size.Text = why;
        source.Text = "";
    }

    /// <summary>
    /// Give each frame its texture's proportions, as large as its half of the
    /// pane allows.
    ///
    /// Never larger than the texture really is. The menu's is 340 by 244, and a
    /// maximised window has room for three times that — blown up to fill it, a
    /// thumbnail is shown at a size the game never draws it, and every edge in
    /// it is softened by the enlargement rather than by anything real. At its own
    /// size it is small, and small is the truth about it.
    ///
    /// Sized here rather than by a Viewbox: a Viewbox scales the outline along
    /// with the picture, and one texture is 1024 across while the other is 340,
    /// so the two frames would be outlined in visibly different weights.
    /// </summary>
    private void LayOutFrames()
    {
        // Half of what is left after the gap, each. Measured off the pane,
        // which fills it and so has a width of its own; the row the frames sit
        // in hugs them, and asking that how wide it is would be asking it about
        // the number it was just given.
        const double gap = 24;

        var room = (FramesView.ActualWidth - gap) / 2;

        // Whichever caption runs to three lines decides, so the two frames sit
        // the same height off the text whether or not one of them is saying
        // where its artwork came from.
        var captions = Math.Max(CarCaption.ActualHeight, MenuCaption.ActualHeight);
        if (captions <= 0) captions = 52;

        var height = FramesView.ActualHeight - captions - 8;

        Shape(CarFrame, _carTexture, room, height);
        Shape(MenuFrame, _menuTexture, room, height);
    }

    private static void Shape(Border frame, (uint Width, uint Height)? texture,
                              double room, double height)
    {
        if (texture is not { } size || size.Width == 0 || size.Height == 0
            || room <= 0 || height <= 0)
        {
            frame.Width = double.NaN;
            frame.Height = double.NaN;
            return;
        }

        // One for one at most: shrink a texture to fit the pane, never stretch
        // one past the pixels it has.
        var scale = Math.Min(1, Math.Min(room / size.Width, height / size.Height));

        frame.Width = Math.Max(1, size.Width * scale);
        frame.Height = Math.Max(1, size.Height * scale);
    }

    private void Frames_SizeChanged(object sender, SizeChangedEventArgs e) => LayOutFrames();

    /// <summary>
    /// Which view somebody picked. Written down, so the pane opens on it next
    /// time rather than on whichever one is the default.
    /// </summary>
    private void DecalView_Changed(object sender, RoutedEventArgs e)
    {
        // Fires while InitializeComponent is still parsing: the markup checks
        // the Frames chip, and the chip declared after it does not exist yet.
        // Both have to be there before either can be asked — reading one that
        // was not built yet crashed the window before it opened.
        if (_config is null || FramesViewChip is null || CarViewChip is null) return;

        var picked = CarViewChip.IsChecked == true ? "Car" : "Frames";

        if (picked == _config.DecalView) return;

        _config.DecalView = picked;
        _config.Save();

        ShowSelectedDecal();
    }

    /// <summary>
    /// The car view, which is a chip and a promise so far. Said plainly rather
    /// than left as a view that does nothing when picked.
    /// </summary>
    private void ShowCarView()
    {
        PreviewImage.Source = null;
        FramesView.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "Seeing the decal on the car isn't built yet.";

        ApplyBackdrop();
    }

    /// <summary>
    /// A picture off disk, or null when it cannot be read.
    ///
    /// OnLoad so the file is not held open: a decal's artwork is replaced in
    /// place, and a locked file makes the next import fail.
    /// </summary>
    private static BitmapSource? Picture(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
