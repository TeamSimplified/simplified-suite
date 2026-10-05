using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;
using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;

namespace SimplifiedTeamEditor.Core.Logos;

/// <summary>One replacement: a picture, and which slice of which array it goes into.</summary>
public sealed record LogoEdit(LogoKind Kind, int Slice, string PngPath);

/// <summary>
/// Baking custom team logos into the game's UI shader packages.
///
/// Three steps, in the order the launcher does them:
///
/// 1. Build an <em>overlay</em> — a copy of the bundled team-branding archive
///    stripped down to only the texture arrays being changed. Stripping matters:
///    the whole archive merged in would replace arrays nobody edited with the
///    bundled copies of them, quietly reverting anything else installed.
/// 2. Import each picture into its slice of each array.
/// 3. Merge the overlay into both shader packages, overlay winning.
///
/// The launcher shells out to its own toolkit for step 2; this reads and writes
/// the archives directly, so there is no external tool to ship or to fail.
/// </summary>
public static class LogoBaker
{
    /// <summary>
    /// The branding archive reduced to the resources these edits touch.
    /// Returns the overlay and how many resources were dropped.
    /// </summary>
    public static (ErpFile Overlay, int Removed) BuildOverlay(
        string brandingErpPath, IEnumerable<LogoEdit> edits)
        => BuildOverlay(brandingErpPath, edits.SelectMany(e => e.Kind.ResourceIdentifiers));

    /// <summary>
    /// The same, for a caller that knows which resources it wants but no longer
    /// has the edits that named them — a project whose pictures were imported
    /// into its own copy of the archive at save time, and which only remembers
    /// afterwards which kinds were touched.
    /// </summary>
    public static (ErpFile Overlay, int Removed) BuildOverlay(
        string brandingErpPath, IEnumerable<string> keepIdentifiers)
    {
        var keep = keepIdentifiers.ToHashSet(StringComparer.Ordinal);

        var overlay = ErpFileIO.Load(brandingErpPath);
        var before = overlay.Resources.Count;
        overlay.Resources.RemoveAll(r => !keep.Contains(r.Identifier));

        return (overlay, before - overlay.Resources.Count);
    }

    /// <summary>The size every slice of an array has, or null for an array it has not got.</summary>
    public static (uint Width, uint Height)? SliceSize(ErpFile archive, string texarrayBase)
    {
        var resource = TeamLogos.ResourceFor(archive, texarrayBase);
        if (resource is null) return null;

        var srv = new ErpGfxSRVResource();
        srv.FromResource(resource);

        return (srv.SurfaceRes.Fragment0.Width, srv.SurfaceRes.Fragment0.Height);
    }

    /// <summary>
    /// Put one picture into one slice of one array.
    ///
    /// The slice keeps the array's own size, format and mip count — a texture
    /// array is a single resource with every slice sharing those, so an import
    /// has to match rather than choose. A picture of another size is fitted to
    /// it rather than refused, and one of another shape is padded rather than
    /// squashed, which is how the game's own logos sit in these slots.
    /// </summary>
    /// <returns>
    /// How the picture was fitted, or null when the array is too short to hold
    /// that slice, which happens and is not an error: the three badge arrays
    /// are not the same length, so the last 2026 team has a slot in two of them
    /// and not the third.
    /// </returns>
    public static Fit? ImportSlice(
        ErpFile overlay, string texarrayBase, int slice, string pngPath, TexconvRunner runner)
    {
        var resource = TeamLogos.ResourceFor(overlay, texarrayBase)
            ?? throw new InvalidOperationException($"The overlay has no {texarrayBase}.");

        var srv = new ErpGfxSRVResource();
        srv.FromResource(resource);

        if (srv.SurfaceRes.HasMips)
            throw new NotSupportedException(
                $"{texarrayBase} keeps its mipmaps in a separate file, which this "
                + "does not write. No UI texture array in the shipped game does.");

        var f0 = srv.SurfaceRes.Fragment0;
        if (slice < 0 || slice >= f0.ArraySize) return null;

        var mipCount = Math.Max((int)f0.MipMapCount, 1);

        var work = Path.Combine(Path.GetTempPath(), "simplified-team-editor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // Fitted rather than handed straight over: a slice is a fixed
            // stretch of its resource, so only the array's exact size can go
            // in, and SliceFit lands a picture of any size there without
            // squashing it.
            var fit = SliceFit.Encode(pngPath, work, f0.ImageType, mipCount, f0.Width, f0.Height, runner);

            using var fs = File.Open(fit.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var dds = new DdsFile(fs);

            // importTexArray false: one slice is being replaced, not the whole
            // array. The stream argument is only used for external mipmaps,
            // which the check above has already ruled out.
            dds.ToErpGfxSRVResource(srv, Stream.Null, importTexArray: false, texArrayIndex: (uint)slice);
            srv.ToResource(resource);
            return fit;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The bytes of one slice, or null when the array is too short to have
    /// one. Enough to tell two copies of an array apart slice by slice.
    /// </summary>
    public static byte[]? SliceBytes(ErpFile archive, string texarrayBase, int slice)
    {
        var resource = TeamLogos.ResourceFor(archive, texarrayBase);
        if (resource is null) return null;

        var srv = new ErpGfxSRVResource();
        srv.FromResource(resource);

        var count = (int)srv.SurfaceRes.Fragment0.ArraySize;
        if (slice < 0 || slice >= count) return null;

        var per = srv.SurfaceRes.Fragment1.Data.Length / count;
        return srv.SurfaceRes.Fragment1.Data.AsSpan(slice * per, per).ToArray();
    }

    /// <summary>
    /// One slice, out as a PNG: the inverse of <see cref="ImportSlice"/>.
    ///
    /// The slice goes out as a one-image DDS and texconv turns that into a
    /// picture, which is how the logo picker shows what the game has in a slot
    /// and how a project that baked its logos into an archive gets them back.
    /// </summary>
    /// <param name="fileName">
    /// What to call it. Defaults to the array and the slice, which is what a
    /// cache of previews wants; a project names them after the kind instead.
    /// </param>
    /// <returns>The PNG's path, or null when there is nothing there to read.</returns>
    public static string? ExportSlice(
        ErpFile archive, string texarrayBase, int slice, string outDir,
        TexconvRunner runner, string? fileName = null)
    {
        var resource = TeamLogos.ResourceFor(archive, texarrayBase);
        if (resource is null) return null;

        var srv = new ErpGfxSRVResource();
        srv.FromResource(resource);
        if (slice < 0 || slice >= srv.SurfaceRes.Fragment0.ArraySize) return null;

        var stem = Path.GetFileNameWithoutExtension(fileName ?? $"{texarrayBase}.{slice}");

        Directory.CreateDirectory(outDir);
        var ddsPath = Path.Combine(outDir, stem + ".dds");

        var dds = srv.ToDdsFile(null, exportTexArray: false, texArrayIndex: (uint)slice);
        using (var file = File.Create(ddsPath)) dds.Write(file, -1);

        try
        {
            runner.DdsToPng(ddsPath, outDir,
                isSrgb: TexconvRunner.IsSrgbFormat(srv.SurfaceRes.Fragment0.ImageType));
        }
        finally
        {
            try { File.Delete(ddsPath); } catch { }
        }

        var pngPath = Path.Combine(outDir, stem + ".png");
        return File.Exists(pngPath) ? pngPath : null;
    }

    /// <summary>
    /// Apply every edit to the overlay, fanning each across its kind's arrays.
    /// Reports what went in, what had to be done to it, and what did not go in.
    ///
    /// A picture that cannot be used costs that picture and nothing else. It
    /// used to cost all of them: the throw travelled out past every edit still
    /// to do and into the packager's single catch, so a package of a dozen
    /// logos shipped with none of them because one file was bad.
    /// </summary>
    public static (int Imported, List<string> Skipped, List<string> Fitted) ImportAll(
        ErpFile overlay, IEnumerable<LogoEdit> edits, TexconvRunner runner)
    {
        var imported = 0;
        var skipped = new List<string>();
        var fitted = new List<string>();

        foreach (var edit in edits)
        {
            var picture = Path.GetFileName(edit.PngPath);

            foreach (var texarray in edit.Kind.Texarrays)
            {
                try
                {
                    var fit = ImportSlice(overlay, texarray, edit.Slice, edit.PngPath, runner);
                    if (fit is null)
                    {
                        skipped.Add($"{texarray} has no slice {edit.Slice}");
                        continue;
                    }

                    imported++;

                    // Only the padding is worth saying afterwards. Scaling a
                    // picture to the slot's size is invisible and expected;
                    // padding it is a decision made on somebody's behalf, and
                    // the transparent bands it leaves are the thing they might
                    // not have asked for.
                    //
                    // Noted once per picture and slot rather than once per
                    // array: a badge goes into three of them and was padded the
                    // same way for each, and three identical notes read like
                    // three problems.
                    if (fit.How == FitKind.Padded && fit.Describe() is { } how)
                    {
                        var note = $"{picture} was {how}.";
                        if (!fitted.Contains(note)) fitted.Add(note);
                    }
                }
                catch (Exception ex)
                {
                    // On one line: these are read in a list beside other notes,
                    // and a message with newlines in it breaks the list up.
                    var why = string.Join(" ", ex.Message.Split(
                        ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

                    skipped.Add($"{picture} couldn't go into {edit.Kind.Label}: {why}");
                }
            }
        }

        return (imported, skipped, fitted);
    }

    /// <summary>
    /// Merge the overlay into a shader package and write the result.
    ///
    /// A resource the overlay carries replaces the one already there; anything
    /// it does not carry is left alone, which is the whole point of having
    /// stripped it.
    /// </summary>
    public static void MergeInto(string shaderErpPath, ErpFile overlay, string outputPath)
    {
        var target = ErpFileIO.Load(shaderErpPath);

        // Snapshot first: adding to Resources mutates the list being walked.
        foreach (var resource in overlay.Resources.ToList())
        {
            var existing = target.Resources.Find(r => r.Identifier == resource.Identifier);
            if (existing is not null) target.Resources.Remove(existing);

            resource.ParentFile = target;
            foreach (var fragment in resource.Fragments) fragment.ParentFile = target;
            target.Resources.Add(resource);
        }

        target.UpdateOffsets();

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        ErpFileIO.Save(target, outputPath);
    }
}
