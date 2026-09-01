using EgoEngineLibrary.Operations;

namespace SimplifiedDriverwearSwapper.Core.Toolkit;

/// <summary>
/// The ERP operations this app performs, as methods.
///
/// These used to build a command line and start <c>stk.exe</c>. They call the
/// shared library directly now, which took about 230ms of process startup off
/// every call — measured, and nearly all of what a call cost — and removed a
/// regex that read resource listings out of console text.
///
/// This app owns no ERP code. Everything below passes paths and reads numbers
/// back; nothing here opens an archive, holds one, or knows that offsets have
/// to be fixed before it is written. Anything missing is added to the library
/// rather than reimplemented here.
///
/// Each call runs on a background thread and reports failure the way the
/// process version did, so the callers around them did not have to change
/// shape: check <see cref="OpResult.Succeeded"/>, show
/// <see cref="OpResult.ErrorMessage"/>.
/// </summary>
public static class Erp
{
    /// <summary>What one operation produced. Failure carries a message fit to show.</summary>
    public sealed record OpResult(bool Succeeded, string ErrorMessage)
    {
        public static readonly OpResult Ok = new(true, "");

        /// <summary>
        /// The exception's own message, without the type name a stack trace
        /// would have carried through the old process boundary.
        /// </summary>
        public static OpResult From(Exception ex) => new(false, ex.Message);
    }

    /// <summary>A merge, with the numbers that used to be read back out of console text.</summary>
    /// <param name="Skipped">Resources two pieces both claimed, kept from the first.</param>
    public sealed record MergeResult(bool Succeeded, string ErrorMessage, int Skipped, int Total);

    /// <summary>An extract-to-erp: how many resources were taken, out of how many.</summary>
    public sealed record ExtractResult(bool Succeeded, string ErrorMessage, int Taken, int OutOf);

    /// <summary>A texture import, with the archive's own tally.</summary>
    public sealed record TextureImportResult(bool Succeeded, string ErrorMessage, int Imported);

    /// <summary>One resource in an archive.</summary>
    public sealed record ErpResource(
        int Index, string ResourceType, int FragmentCount, long Size, string Identifier)
    {
        /// <summary>File name at the end of the identifier, query string and all.</summary>
        public string Name => Identifier[(Identifier.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// The archive's resources, optionally narrowed by resource type or an
    /// identifier regex — both applied before anything comes back, so a large
    /// archive does not have to be returned in full.
    /// </summary>
    public static Task<List<ErpResource>> ListAsync(
        string erpPath, string? type = null, string? filter = null, CancellationToken cancel = default) =>
        Task.Run(() => ErpFiles.List(erpPath, type, filter).Shown
            .Select(r => new ErpResource(r.Index, r.ResourceType, r.FragmentCount, (long)r.PackedSize, r.Identifier))
            .ToList(), cancel);

    /// <summary>
    /// Copy the resources whose identifier matches into a new ERP at
    /// <paramref name="outputErp"/>, leaving the source untouched.
    /// </summary>
    public static Task<ExtractResult> ExtractToErpAsync(
        string sourceErp, string outputErp, string filter, CancellationToken cancel = default) =>
        Run(() =>
        {
            var taken = ErpFiles.ExtractTo(sourceErp, outputErp, filter);
            return new ExtractResult(true, "", taken.Taken, taken.OutOf);
        }, ex => new ExtractResult(false, ex.Message, 0, 0), cancel);

    /// <summary>
    /// Fold one or more sources into <paramref name="targetErp"/>. Writes to
    /// <paramref name="outputPath"/> when given, otherwise over the target.
    /// </summary>
    public static Task<MergeResult> MergeAsync(
        string targetErp, IEnumerable<string> sourceErps, string? outputPath = null,
        bool overwrite = false, CancellationToken cancel = default) =>
        Run(() =>
        {
            var merged = ErpFiles.Merge(targetErp, sourceErps, outputPath, overwrite);
            return new MergeResult(true, "", merged.Skipped, merged.Total);
        }, ex => new MergeResult(false, ex.Message, 0, 0), cancel);

    /// <summary>Substring find/replace across every resource identifier.</summary>
    public static Task<OpResult> RenameAllAsync(
        string erpPath, string find, string replace, CancellationToken cancel = default) =>
        Run(() => ErpFiles.RenameAll(erpPath, find, replace), cancel);

    /// <summary>Pull each texture's external mip chain into the archive.</summary>
    public static Task<OpResult> StripMipsAsync(
        string erpPath, string gameDir, Action<string>? log = null, CancellationToken cancel = default) =>
        Run(() => ErpFiles.StripMips(erpPath, gameDir, log: log), cancel);

    /// <summary>Export every texture as DDS, resolving external mips when asked.</summary>
    public static Task<OpResult> ExportTexturesAsync(
        string erpPath, string outDir, bool withMips = false, string? gameDir = null,
        Action<string>? log = null, CancellationToken cancel = default) =>
        Run(() => ErpFiles.ExportTextures(erpPath, outDir, withMips: withMips, gameDir: gameDir, log: log), cancel);

    /// <summary>Re-import textures from a folder laid out the way export writes it.</summary>
    public static Task<TextureImportResult> ImportTexturesAsync(
        string erpPath, string inDir, string? gameDir = null,
        Action<string>? log = null, CancellationToken cancel = default) =>
        Run(() =>
        {
            var tally = ErpFiles.ImportTextures(erpPath, inDir, gameDir: gameDir, log: log);
            return new TextureImportResult(true, "", tally.Imported);
        }, ex => new TextureImportResult(false, ex.Message, 0), cancel);

    /// <summary>
    /// Off the UI thread, with anything thrown turned into a failed result —
    /// which is how these read when each one was its own process.
    /// </summary>
    private static Task<OpResult> Run(Action work, CancellationToken cancel) =>
        Run(() => { work(); return OpResult.Ok; }, OpResult.From, cancel);

    private static Task<T> Run<T>(Func<T> work, Func<Exception, T> onError, CancellationToken cancel) =>
        Task.Run(() =>
        {
            try
            {
                return work();
            }
            catch (Exception ex)
            {
                return onError(ex);
            }
        }, cancel);
}
