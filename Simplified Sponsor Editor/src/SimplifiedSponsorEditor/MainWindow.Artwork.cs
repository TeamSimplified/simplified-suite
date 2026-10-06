using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Simplified.Shared;
using SimplifiedSponsorEditor.Core.Package;
using SimplifiedSponsorEditor.Core.Sponsors;

namespace SimplifiedSponsorEditor;

/// <summary>
/// Putting a picture on a decal, editing it afterwards, and taking it off again.
///
/// What the project keeps is the file somebody handed over, exactly as it
/// arrived, plus a note of what they did to it in the editor. Every raster this
/// app shows or ships is made from those two on demand — here for the panes,
/// and in <see cref="Materialise"/> for a package.
///
/// That is what makes a drawing editable a second time: its pieces are still in
/// the file, so the editor can be opened on it again. It also costs one resample
/// instead of two, because the render happens at the size the texture wants
/// rather than at an import-time guess the packager then scales.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Rendered artwork, by decal name. Cleared whole whenever anything about
    /// the project's sources changes: the alternative is tracking which decal a
    /// change touched, and a stale preview is a worse bug than a re-render
    /// nobody can see.
    /// </summary>
    private readonly Dictionary<string, BitmapSource?> _artwork = new(StringComparer.OrdinalIgnoreCase);

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
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || SvgPicture.IsSvg(path);

    /// <summary>
    /// Take a file into the project as the source for a decal.
    ///
    /// A drawing goes through the editor first, so a background plate or
    /// somebody else's colour can go before it is ever a picture; closing that
    /// window without applying drops the whole import. What is stored is the
    /// file as given and the edits as a separate note — never the result.
    /// </summary>
    private void Apply(SponsorDecal decal, string sourceFile)
    {
        if (_project is null) return;

        try
        {
            SvgEdits? edits = null;

            if (SvgPicture.IsSvg(sourceFile))
            {
                edits = SvgEditor.Show(this, sourceFile);
                if (edits is null) return;
            }
            else
            {
                // Read once before taking it in. The file is kept as it arrived,
                // so somebody's JPEG named .png would otherwise sail through and
                // fail at package time, an hour of work later.
                Load(sourceFile);
            }

            Directory.CreateDirectory(_project.SourcesFolder);
            Clear(decal.Name);

            File.Copy(sourceFile, Path.Combine(_project.SourcesFolder,
                                               decal.Name + Path.GetExtension(sourceFile)));

            edits?.Write(_project.EditsPath(decal.Name));

            Changed();
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"{Path.GetFileName(sourceFile)} couldn't be used.\n\n{ex.Message}",
                "Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Open the editor again on a drawing already imported, starting from what
    /// was done to it last time.
    /// </summary>
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _selected is null) return;

        var source = _project.SourcePath(_selected.Name);
        if (source is null || !SvgPicture.IsSvg(source)) return;

        try
        {
            var editsPath = _project.EditsPath(_selected.Name);

            var edits = SvgEditor.Show(this, source, SvgEdits.Read(editsPath), action: "Apply");
            if (edits is null) return;

            edits.Write(editsPath);
            Changed();
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"{Path.GetFileName(source)} couldn't be opened.\n\n{ex.Message}",
                "Edit artwork", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Whether this decal's artwork is a drawing, and so has pieces to edit.</summary>
    private bool CanEdit(SponsorDecal? decal) =>
        decal is not null && _project?.SourcePath(decal.Name) is { } source && SvgPicture.IsSvg(source);

    // ── Rendering ─────────────────────────────────────────────────────

    /// <summary>
    /// This decal's artwork as pixels — the drawing rendered with its edits, the
    /// imported PNG as it stands, or the picture a 1.0 project rendered on the
    /// way in. Null when the decal has not been replaced, or when what is there
    /// cannot be read.
    /// </summary>
    private BitmapSource? Artwork(SponsorDecal decal)
    {
        if (_project is null) return null;
        if (_artwork.TryGetValue(decal.Name, out var cached)) return cached;

        BitmapSource? image = null;

        try
        {
            var source = _project.SourcePath(decal.Name);

            if (source is not null)
            {
                image = SvgPicture.IsSvg(source)
                    ? SvgPicture.Render(
                        SvgEdits.Read(_project.EditsPath(decal.Name)).ApplyTo(SvgPicture.Read(source)),
                        LongEdgeFor(decal))
                    : Load(source);
            }
            else if (File.Exists(_project.PicturePath(decal.Name)))
            {
                image = Load(_project.PicturePath(decal.Name));
            }
        }
        catch
        {
            // Left null. The panes say "couldn't be read", which is the whole of
            // what can usefully be said about somebody's file here; the same
            // failure at package time is reported against the decal it belongs
            // to, where there is room to say it properly.
        }

        _artwork[decal.Name] = image;
        return image;
    }

    /// <summary>
    /// The same artwork on the shape the customisation menu wants. A 1.0 project
    /// has its own letterboxed copy, and that is used as it stands — rebuilding
    /// it from a picture that was already padded would pad it twice.
    /// </summary>
    private BitmapSource? MenuArtwork(SponsorDecal decal)
    {
        if (_project is null || !decal.HasMenuThumbnail) return null;

        if (_project.SourcePath(decal.Name) is null && File.Exists(_project.MenuPicturePath(decal.Name)))
        {
            try { return Load(_project.MenuPicturePath(decal.Name)); } catch { return null; }
        }

        return Artwork(decal) is { } image ? Letterbox(image) : null;
    }

    /// <summary>
    /// Render every replaced decal into a folder for the packager: the car's
    /// picture by name, the menu's under <c>menu\</c>.
    ///
    /// Done here rather than inside the packager because rendering a drawing is
    /// a WPF operation and wants the thread that owns one. The packager runs on
    /// a worker; this runs before it starts.
    /// </summary>
    private (string Dir, IReadOnlyList<string> Skipped) Materialise()
    {
        var dir = Path.Combine(Path.GetTempPath(), "simplified-sponsor-editor", "artwork",
                               Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, SponsorPackager.MenuFolderName));

        // A decal that cannot be prepared is said here, because the packager
        // only ever sees what did get written and so cannot know it is missing.
        var skipped = new List<string>();

        foreach (var name in _project?.ReplacedDecals ?? [])
        {
            if (_catalogue?.Find(name) is not { } decal)
            {
                skipped.Add($"{name} is not a decal in these archives — skipped.");
                continue;
            }

            if (Artwork(decal) is not { } image)
            {
                skipped.Add($"{name}'s artwork couldn't be read — skipped.");
                continue;
            }

            Save(image, Path.Combine(dir, decal.Name + ".png"));

            if (MenuArtwork(decal) is { } menu)
                Save(menu, Path.Combine(dir, SponsorPackager.MenuFolderName, decal.Name + ".png"));
        }

        return (dir, skipped);
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

    // ── Export and revert ─────────────────────────────────────────────

    /// <summary>
    /// Save out the picture this decal is wearing — the project's artwork if it
    /// has been replaced, the game's own if it has not.
    ///
    /// The project's is encoded here rather than copied, because what it holds
    /// may be a drawing; the game's is copied, since it is already a PNG written
    /// by texconv and a re-encode could only lose something.
    /// </summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var mine = _project?.IsReplaced(_selected.Name) == true ? Artwork(_selected) : null;
        var theirs = _preview?.Get(_selected.CarIdentifier)?.PngPath;

        if (mine is null && (theirs is null || !File.Exists(theirs))) return;

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
            if (mine is not null) Save(mine, dialog.FileName);
            else File.Copy(theirs!, dialog.FileName, overwrite: true);

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

    /// <summary>Whether there is a picture to export: this project's, or the game's.</summary>
    private bool HasPicture(SponsorDecal decal) =>
        _project?.IsReplaced(decal.Name) == true
        || _preview?.Get(decal.CarIdentifier)?.PngPath is { } png && File.Exists(png);

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _selected is null) return;

        Clear(_selected.Name);
        Changed();
    }

    /// <summary>
    /// Remove everything this project holds for a decal — the source, its edits,
    /// and anything 1.0 left behind for it. Called before an import too, so a
    /// second picture cannot end up beside the first under another extension.
    /// </summary>
    private void Clear(string decalName)
    {
        if (_project is null) return;

        var paths = new List<string> { _project.EditsPath(decalName),
                                       _project.PicturePath(decalName),
                                       _project.MenuPicturePath(decalName) };

        if (_project.SourcePath(decalName) is { } source) paths.Add(source);

        foreach (var path in paths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }

    /// <summary>The project's artwork changed: forget the renders and redraw.</summary>
    private void Changed()
    {
        _artwork.Clear();
        MarkDirty();
        RebuildViews();
        ShowSelectedDecal();
    }

    /// <summary>
    /// Dropping a picture on the window replaces whatever is selected. Dropping
    /// a project opens it, which is what dragging a <c>.simplified</c> anywhere
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
        target.Freeze();
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
