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

    /// <summary>
    /// Put one picture into one slice of one array.
    ///
    /// The slice keeps the array's own format and mip count — a texture array
    /// is a single resource with every slice sharing those, so an import has to
    /// match rather than choose.
    /// </summary>
    /// <returns>
    /// False when the array is too short to hold that slice, which happens and
    /// is not an error: the three badge arrays are not the same length, so the
    /// last 2026 team has a slot in two of them and not the third.
    /// </returns>
    public static bool ImportSlice(
        ErpFile overlay, string texarrayBase, int slice, string pngPath, TexconvRunner runner)
    {
        var identifier = TeamLogos.IdentifierFor(texarrayBase);
        var resource = overlay.FindResource(identifier)
            ?? throw new InvalidOperationException($"The overlay has no {texarrayBase}.");

        var srv = new ErpGfxSRVResource();
        srv.FromResource(resource);

        if (srv.SurfaceRes.HasMips)
            throw new NotSupportedException(
                $"{texarrayBase} keeps its mipmaps in a separate file, which this "
                + "does not write. No UI texture array in the shipped game does.");

        var arraySize = srv.SurfaceRes.Fragment0.ArraySize;
        if (slice < 0 || slice >= arraySize) return false;

        var format = srv.SurfaceRes.Fragment0.ImageType;
        var mipCount = Math.Max((int)srv.SurfaceRes.Fragment0.MipMapCount, 1);

        var work = Path.Combine(Path.GetTempPath(), "simplified-team-editor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var ddsPath = runner.PngToDds(pngPath, work, format, mipCount);
            using var fs = File.Open(ddsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var dds = new DdsFile(fs);

            // importTexArray false: one slice is being replaced, not the whole
            // array. The stream argument is only used for external mipmaps,
            // which the check above has already ruled out.
            dds.ToErpGfxSRVResource(srv, Stream.Null, importTexArray: false, texArrayIndex: (uint)slice);
            srv.ToResource(resource);
            return true;
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
        var resource = archive.FindResource(TeamLogos.IdentifierFor(texarrayBase));
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
        var resource = archive.FindResource(TeamLogos.IdentifierFor(texarrayBase));
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
    /// Reports what went in and what would not fit.
    /// </summary>
    public static (int Imported, List<string> Skipped) ImportAll(
        ErpFile overlay, IEnumerable<LogoEdit> edits, TexconvRunner runner)
    {
        var imported = 0;
        var skipped = new List<string>();

        foreach (var edit in edits)
        {
            foreach (var texarray in edit.Kind.Texarrays)
            {
                if (ImportSlice(overlay, texarray, edit.Slice, edit.PngPath, runner)) imported++;
                else skipped.Add($"{texarray} has no slice {edit.Slice}");
            }
        }

        return (imported, skipped);
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
