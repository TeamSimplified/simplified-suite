using System.IO;
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
            Filter = "PNG images (*.png)|*.png|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = FirstExistingDir(_config.LastArtworkFolder) ?? "",
        };

        if (picker.ShowDialog(this) != true) return;

        _config.LastArtworkFolder = Path.GetDirectoryName(picker.FileName);
        _config.Save();

        Apply(_selected, picker.FileName);
    }

    /// <summary>
    /// Take a picture into the project: the artwork as given, and a copy
    /// letterboxed for the customisation menu.
    /// </summary>
    private void Apply(SponsorDecal decal, string sourcePng)
    {
        if (_project is null) return;

        try
        {
            Directory.CreateDirectory(_project.SponsorsFolder);
            Directory.CreateDirectory(_project.MenuFolder);

            var image = Load(sourcePng);

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

        var png = files.FirstOrDefault(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        if (png is null) return;

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

        Apply(_selected, png);
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
