using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SimplifiedSponsorEditor.Core.Package;
using SimplifiedSponsorEditor.Core.Sponsors;

namespace SimplifiedSponsorEditor;

/// <summary>
/// Putting a picture on a decal, and taking it off again.
/// </summary>
public partial class MainWindow
{
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _selected is null) return;

        var picker = new OpenFileDialog
        {
            Title = $"Choose a picture for {_selected.Name}",
            Filter = "Pictures (*.png;*.svg)|*.png;*.svg"
                     + "|PNG images (*.png)|*.png"
                     + "|SVG drawings (*.svg)|*.svg"
                     + "|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = FirstExistingDir(_config.LastArtworkFolder) ?? "",
        };

        if (picker.ShowDialog(this) != true) return;

        _config.LastArtworkFolder = Path.GetDirectoryName(picker.FileName);
        _config.Save();

        Apply(_selected, picker.FileName);
    }

    /// <summary>
    /// Whether a file is artwork this app will take.
    ///
    /// One rule, because there are three ways in — the file dialog, a drop on a
    /// row, and a drop anywhere on the window — and a format one of them accepts
    /// and the others do not is not a thing anybody reports as a bug. They drag
    /// a file in, nothing happens, and they conclude that dragging does not
    /// work.
    /// </summary>
    private static bool IsArtwork(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || Simplified.Shared.SvgPicture.IsSvg(path);

    /// <summary>
    /// Take a picture into the project: the artwork as given, and a copy
    /// letterboxed for the customisation menu.
    ///
    /// An SVG is rendered to a raster first, at the size the decal it is going
    /// into wants, and everything after this point treats it as any other
    /// picture. The project stores PNGs — the game stores compressed blocks, so
    /// a drawing has to become pixels somewhere, and doing it once on the way in
    /// means the preview shows exactly what will be packaged.
    /// </summary>
    private void Apply(SponsorDecal decal, string sourcePng)
    {
        if (_project is null) return;

        try
        {
            Directory.CreateDirectory(_project.SponsorsFolder);
            Directory.CreateDirectory(_project.MenuFolder);

            var image = Simplified.Shared.SvgPicture.IsSvg(sourcePng)
                ? Simplified.Shared.SvgPicture.Render(sourcePng, LongEdgeFor(decal))
                : Load(sourcePng);

            // Saved re-encoded rather than copied, so what the project holds is
            // always a PNG this app can read back — someone's file could be a
            // JPEG that happens to be named .png, and the failure would surface
            // at package time rather than here.
            Save(image, _project.PicturePath(decal.Name));

            if (decal.HasMenuThumbnail)
                Save(Letterbox(image), _project.MenuPicturePath(decal.Name));

            MarkDirty();
            RebuildViews();
            ShowSelectedDecal();
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"{Path.GetFileName(sourcePng)} couldn't be used.\n\n{ex.Message}",
                "Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// How big to render a drawing: the longest side of the decal it is going
    /// into, which is the resolution the game itself keeps these at.
    ///
    /// From the archive rather than assumed, though every car decal in the game
    /// measures 1024x1024. Rendering larger would be sharper and would cost
    /// every person who installs the mod the extra megabytes, so matching the
    /// game is the default; anyone who wants more can hand over a big PNG.
    /// </summary>
    private int LongEdgeFor(SponsorDecal decal)
    {
        const int gameKeepsThemAt = 1024;

        if (_preview?.Get(decal.CarIdentifier) is not { } own) return gameKeepsThemAt;

        var longest = (int)Math.Max(own.Width, own.Height);
        return longest > 0 ? longest : gameKeepsThemAt;
    }

    /// <summary>
    /// Save out the picture this decal is wearing — the artwork if it has been
    /// replaced, the game's own if it has not.
    ///
    /// Copied rather than re-encoded. Both are already PNGs, one written by this
    /// app and one by texconv, so a copy is exactly what the decal holds and a
    /// re-encode could only lose something.
    /// </summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var source = PictureOn(_selected);
        if (source is null) return;

        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string(_selected.Name.Where(c => !invalid.Contains(c)).ToArray()).Trim();

        var dialog = new SaveFileDialog
        {
            Title = "Export artwork",
            Filter = "PNG image (*.png)|*.png",
            FileName = (stem.Length > 0 ? stem : "decal") + ".png",
            DefaultExt = ".png",
            InitialDirectory = FirstExistingDir(_config.LastArtworkFolder) ?? "",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.Copy(source, dialog.FileName, overwrite: true);

            _config.LastArtworkFolder = Path.GetDirectoryName(dialog.FileName);
            _config.Save();
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"{Path.GetFileName(dialog.FileName)} couldn't be written.\n\n{ex.Message}",
                "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// The PNG behind a decal as the pane shows it: the project's artwork when
    /// there is some, the game's own otherwise. Null when there is neither.
    /// </summary>
    private string? PictureOn(SponsorDecal decal)
    {
        var mine = _project?.IsReplaced(decal.Name) == true ? _project!.PicturePath(decal.Name) : null;
        if (mine is not null && File.Exists(mine)) return mine;

        var theirs = _preview?.Get(decal.CarIdentifier)?.PngPath;
        return theirs is not null && File.Exists(theirs) ? theirs : null;
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _selected is null) return;

        foreach (var path in new[] { _project.PicturePath(_selected.Name), _project.MenuPicturePath(_selected.Name) })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        MarkDirty();
        RebuildViews();
        ShowSelectedDecal();
    }

    /// <summary>
    /// Dropping a PNG on the window replaces whatever is selected. Dropping a
    /// project opens it, which is what dragging a <c>.simplified</c> anywhere
    /// else in the suite does.
    /// </summary>
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        var project = files.FirstOrDefault(f =>
            f.EndsWith(Core.Project.ProjectStore.ProjectFileExtension, StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            if (ConfirmDiscardChanges("open that project")) OpenProject(project);
            return;
        }

        var picture = files.FirstOrDefault(IsArtwork);
        if (picture is null) return;

        // Both of these say what to do next rather than what went wrong. A drop
        // that lands on nothing has to answer for itself — there is no status
        // line to leave the answer in, and doing nothing at all would read as
        // the app having missed the file.
        if (_project is null)
        {
            SimplifiedUi.MessageDialog.Show(this,
                "Start or open a project before dropping artwork on it.",
                "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selected is null)
        {
            SimplifiedUi.MessageDialog.Show(this,
                "Select the decal that picture should replace, then drop it again.",
                "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Apply(_selected, picture);
    }

    // ── The menu's copy ───────────────────────────────────────────────

    /// <summary>
    /// The same picture on the aspect the customisation menu wants, with
    /// transparent bars rather than a squeeze.
    ///
    /// The menu's pictures are 340×244 and a decal is square, so scaling one
    /// straight down flattens every logo in the list. Padding it to the menu's
    /// proportions first and letting texconv scale that keeps it the shape the
    /// artist drew.
    ///
    /// The canvas is worked out from the source rather than fixed: the tool this
    /// replaces padded to 1427×1024, which is the right answer only while the
    /// source is 1024 tall.
    /// </summary>
    private static BitmapSource Letterbox(BitmapSource source)
    {
        var wanted = (double)SponsorPackager.MenuWidth / SponsorPackager.MenuHeight;
        var actual = (double)source.PixelWidth / source.PixelHeight;

        // Already the right shape, to within a pixel of rounding.
        if (Math.Abs(actual - wanted) < 0.001) return source;

        int width, height;
        if (actual < wanted)
        {
            // Taller than the menu wants: widen it.
            height = source.PixelHeight;
            width = (int)Math.Round(height * wanted);
        }
        else
        {
            width = source.PixelWidth;
            height = (int)Math.Round(width / wanted);
        }

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(
                (width - source.PixelWidth) / 2.0,
                (height - source.PixelHeight) / 2.0,
                source.PixelWidth,
                source.PixelHeight));
        }

        // Pbgra32 keeps the alpha, which for a decal is most of the picture.
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    private static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);
        // OnLoad so the file is not held open afterwards.
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
