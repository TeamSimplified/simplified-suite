using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;

namespace EgoEngineLibrary.Operations;

/// <summary>
/// The same operations, on a path rather than a loaded archive: each opens the
/// file, does the work, fixes the offsets and writes it back.
///
/// <see cref="ErpOperations"/> takes an <c>ErpFile</c> because several of them
/// are sometimes chained over one archive, and reloading between each would be
/// wasteful and wrong. But that leaves every caller doing the same four lines -
/// load, work, UpdateOffsets, save - and getting them right, which is how a
/// caller that is supposed to own no ERP code ends up calling UpdateOffsets.
///
/// So the callers that just want one thing done to one file use these, and the
/// load-and-save lives here rather than in each of them.
/// </summary>
public static class ErpFiles
{
    /// <summary>The resources in an archive, narrowed by type or identifier regex.</summary>
    public static ErpOperations.ListResult List(string erpPath, string? type = null, string? filter = null) =>
        ErpOperations.List(ErpFileIO.Load(erpPath), type, filter);

    /// <summary>
    /// Copy the resources whose identifier matches into a new ERP at
    /// <paramref name="outputErp"/>, leaving the source file untouched.
    /// </summary>
    public static ErpOperations.ExtractResult ExtractTo(string sourceErp, string outputErp, string filter) =>
        ErpOperations.ExtractToErp(ErpFileIO.Load(sourceErp), outputErp, filter);

    /// <summary>Unpack every fragment of every matching resource to disk.</summary>
    public static void ExtractToFolder(string erpPath, string outDir, string filter = "", Action<string>? log = null) =>
        ErpOperations.ExtractToFolder(ErpFileIO.Load(erpPath), outDir, filter, log);

    /// <summary>How a merge went, without the caller holding the archive to ask.</summary>
    public sealed record MergeResult(IReadOnlyList<ErpOperations.MergeTally> Tallies, int Total)
    {
        /// <summary>Resources a source claimed that the target already had, across every source.</summary>
        public int Skipped => Tallies.Sum(t => t.Skipped);
    }

    /// <summary>
    /// Fold one or more sources into <paramref name="targetErp"/>, writing to
    /// <paramref name="outputPath"/> when given and over the target otherwise.
    /// </summary>
    public static MergeResult Merge(string targetErp, IEnumerable<string> sourceErps,
                                    string? outputPath = null, bool overwrite = false)
    {
        var target = ErpFileIO.Load(targetErp);
        var tallies = ErpOperations.Merge(target, sourceErps, overwrite);
        Write(target, outputPath ?? targetErp);
        return new MergeResult(tallies, target.Resources.Count);
    }

    /// <summary>Substring find/replace across every resource identifier.</summary>
    public static int RenameAll(string erpPath, string find, string replace,
                                string? outputPath = null, Action<string>? log = null)
    {
        var erp = ErpFileIO.Load(erpPath);
        var renamed = ErpOperations.RenameAll(erp, find, replace, log);
        Write(erp, outputPath ?? erpPath);
        return renamed;
    }

    /// <summary>Pull each texture's external mip chain into the archive.</summary>
    public static ErpOperations.StripMipsResult StripMips(string erpPath, string gameDir,
                                                          string? outputPath = null, string? filter = null,
                                                          Action<string>? log = null)
    {
        var erp = ErpFileIO.Load(erpPath);
        var result = ErpOperations.StripMips(erp, gameDir, filter, log);
        Write(erp, outputPath ?? erpPath);
        return result;
    }

    /// <summary>Export every texture. Reads only, so nothing is written back.</summary>
    public static ErpTextureOperations.ExportResult ExportTextures(
        string erpPath, string outDir, bool png = false, bool withMips = false, string? gameDir = null,
        bool flat = false, string? filter = null, int slice = -1,
        TexconvRunner? runner = null, Action<string>? log = null)
    {
        Directory.CreateDirectory(outDir);
        return ErpTextureOperations.Export(ErpFileIO.Load(erpPath), outDir, png, withMips, gameDir,
                                           flat, filter, slice, runner, log);
    }

    /// <summary>Re-import textures from a folder laid out the way export writes it.</summary>
    public static ErpTextureOperations.ImportResult ImportTextures(
        string erpPath, string inDir, bool png = false, string? gameDir = null, string? mipsOut = null,
        int slice = -1, TexconvRunner? runner = null, Action<string>? log = null)
    {
        var erp = ErpFileIO.Load(erpPath);
        var result = ErpTextureOperations.Import(erp, inDir, png, gameDir, mipsOut, slice, runner, log);
        Write(erp, erpPath);
        return result;
    }

    /// <summary>
    /// The two lines that have to go together. Offsets are position-dependent
    /// and version-dependent, so an archive written without fixing them first
    /// is subtly wrong rather than obviously broken.
    /// </summary>
    private static void Write(ErpFile erp, string path)
    {
        erp.UpdateOffsets();
        ErpFileIO.Save(erp, path);
    }
}
