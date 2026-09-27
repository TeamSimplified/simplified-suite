using System.Diagnostics;
using System.IO.Abstractions;
using VictorBush.Ego.NefsLib.ArchiveSource;
using VictorBush.Ego.NefsLib.IO;
using VictorBush.Ego.NefsLib.Progress;

namespace Simplified.Language;

/// <summary>
/// Where the archive is, so it need only be found once.
///
/// The header is in the exe and the file data is not — it is in game.dat beside
/// it — so a location that remembered only the offsets would read the right
/// lengths out of the header and then take those bytes from the wrong file.
/// That produces files of exactly the right name and size and entirely the
/// wrong contents, which is how it was found: the warm run's output did not
/// match the cold run's.
/// </summary>
public sealed record ArchiveLocation(
    string DataFilePath, long PrimaryOffset, int? PrimarySize, long SecondaryOffset, int? SecondarySize);

/// <summary>What an extraction came to.</summary>
public sealed record ExtractResult(
    IReadOnlyList<string> Written,
    string OutputFolder,
    ArchiveLocation Location,
    bool FoundBySearch,
    TimeSpan Elapsed);

/// <summary>Something that stopped the run, said in words a person can act on.</summary>
public sealed class ExtractionFailed(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The language files, out of the game's own exe.
///
/// The game does not ship them as files. Every app in the suite that needed
/// them carried its own copy, taken by hand out of a launcher's output and
/// going quietly stale from the day it was taken — and there is nowhere else to
/// get them, because a full install holds no .lng at all. They are inside
/// F1_25.exe, in an embedded NeFS archive, along with about six thousand other
/// things the game loads by name.
///
/// NefsLib does the format. What is here is the two practical problems around
/// it: finding the header, which on a 408 MB exe takes over a minute because
/// the search has to cover the whole file, and not paying that again — the
/// answer is four numbers, so it is remembered against the build it was found
/// in and checked before it is trusted.
/// </summary>
public static class LanguageExtractor
{
    /// <summary>What the game calls its language files.</summary>
    public const string Extension = ".lng";

    /// <summary>The exe that carries them, as the install names it.</summary>
    public const string ExeNamePattern = "F1_*.exe";

    /// <summary>
    /// Pull every language file out of <paramref name="exePath"/> into
    /// <paramref name="outputDir"/>.
    /// </summary>
    /// <param name="say">
    /// Lines for a progress window: the stages, in the user's terms. Separate
    /// from <c>log</c>, which is the extractor tool's own running commentary —
    /// file names and byte counts, right in the window that writes them out and
    /// detail anywhere else.
    /// </param>
    /// <param name="known">
    /// Where the archive was last found in this exe, if it has been found
    /// before. Tried first and verified by use; a wrong answer costs one failed
    /// read and then the search runs anyway.
    /// </param>
    public static async Task<ExtractResult> ExtractAsync(
        string exePath,
        string outputDir,
        ArchiveLocation? known = null,
        Action<string>? log = null,
        IProgress<string>? say = null,
        CancellationToken cancel = default)
    {
        if (!File.Exists(exePath))
            throw new ExtractionFailed($"There's no exe at {exePath}.");

        var fileSystem = new FileSystem();
        var progress = new NefsProgress(cancel);
        var reader = new NefsReader(fileSystem);
        var stopwatch = Stopwatch.StartNew();

        var (archive, location, bySearch) = await OpenAsync(exePath, known, reader, fileSystem, progress, log, say, cancel);

        var items = archive.Items.EnumerateById()
            .Where(i => i.FileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (items.Count == 0)
            throw new ExtractionFailed(
                "The archive in that exe holds no language files. It opened and was read, so this is the " +
                "right kind of file — but not the one with the text in it.");

        Directory.CreateDirectory(outputDir);
        var transformer = new NefsTransformer(fileSystem);
        var written = new List<string>();

        foreach (var item in items)
        {
            cancel.ThrowIfCancellationRequested();

            var path = Path.Combine(outputDir, item.FileName);
            await transformer.DetransformAsync(item.DataSource, path, 0, progress);

            // Every language file starts LNGT. Checking it costs four bytes and
            // catches the one failure that otherwise looks like success: data
            // read from the wrong file still has the right name and the right
            // length, because both of those come from the header.
            if (!await StartsWithMagicAsync(path, cancel))
                throw new ExtractionFailed(
                    $"{item.FileName} came out of the archive without the mark a language file starts with. " +
                    "The header was read from somewhere that doesn't match the file data, so what was " +
                    "written is not what it claims to be.");

            written.Add(item.FileName);
            log?.Invoke($"  {item.FileName,-22} {new FileInfo(path).Length,12:N0} bytes");
            say?.Report($"Extracting the LNG files — {written.Count} of {items.Count}…");
        }

        return new ExtractResult(written, outputDir, location, bySearch, stopwatch.Elapsed);
    }

    /// <summary>
    /// Whether this archive really is the one with the text in it: take the
    /// first language file out to a scratch path and see whether it starts the
    /// way one does. A few megabytes and a fraction of a second, against the
    /// alternative of writing eleven wrong files over somebody's good ones.
    /// </summary>
    private static async Task<bool> ReadsAsLanguageAsync(
        VictorBush.Ego.NefsLib.NefsArchive archive, IFileSystem fileSystem,
        NefsProgress progress, CancellationToken cancel)
    {
        var first = archive.Items.EnumerateById()
            .FirstOrDefault(i => i.FileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase));
        if (first is null) return false;

        var scratch = Path.Combine(Path.GetTempPath(), "simplified-language-extractor",
                                   Guid.NewGuid().ToString("N"), first.FileName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(scratch)!);
            await new NefsTransformer(fileSystem).DetransformAsync(first.DataSource, scratch, 0, progress);
            return await StartsWithMagicAsync(scratch, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
        finally
        {
            try
            {
                var dir = Path.GetDirectoryName(scratch)!;
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* temp, and Windows clears it */ }
        }
    }

    /// <summary>What every .lng opens with — checked across all eleven the game ships.</summary>
    private static readonly byte[] LanguageMagic = "LNGT"u8.ToArray();

    private static async Task<bool> StartsWithMagicAsync(string path, CancellationToken cancel)
    {
        var head = new byte[LanguageMagic.Length];
        await using var stream = File.OpenRead(path);
        return await stream.ReadAsync(head, cancel) == head.Length && head.AsSpan().SequenceEqual(LanguageMagic);
    }

    /// <summary>
    /// The archive, by the short way if there is one and the long way otherwise.
    /// </summary>
    private static async Task<(VictorBush.Ego.NefsLib.NefsArchive Archive, ArchiveLocation Location, bool BySearch)>
        OpenAsync(string exePath, ArchiveLocation? known, NefsReader reader, IFileSystem fileSystem,
                  NefsProgress progress, Action<string>? log, IProgress<string>? say, CancellationToken cancel)
    {
        // A remembered location is four numbers and no proof, so it is tried
        // rather than trusted: if the exe moved on and the read fails, the
        // search runs and puts it right.
        if (known is not null)
        {
            try
            {
                if (!File.Exists(known.DataFilePath))
                    throw new FileNotFoundException("The data file is no longer there.", known.DataFilePath);

                log?.Invoke("Opening at the offset remembered for this build…");
                say?.Report("Opening the game's archive…");
                var source = NefsArchiveSource.Headless(known.DataFilePath, exePath,
                    known.PrimaryOffset, known.PrimarySize, known.SecondaryOffset, known.SecondarySize);
                var archive = await reader.ReadArchiveAsync(source, progress);

                // Opening is not enough to trust it. A header read against the
                // wrong data gives every item a plausible name and length and
                // nothing behind them, so one file is taken out and checked
                // before the rest are written anywhere.
                if (await ReadsAsLanguageAsync(archive, fileSystem, progress, cancel))
                    return (archive, known, false);

                log?.Invoke("  what that offset opens doesn't read as language files — searching instead.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke("  that offset no longer reads — searching the exe instead.");
            }
        }

        // The whole file, not just its data section. Newer F1 titles keep the
        // header outside it, which is what NefsEdit 0.7.1 went and fixed; the
        // quick search finds nothing at all here. It is about a minute on a
        // 408 MB exe, and it is why the answer is worth keeping.
        log?.Invoke("Searching the exe for the archive header. This takes a minute or so, once per build…");
        say?.Report("Searching the game for the LNG files — about a minute, once per update…");
        var finder = new NefsExeHeaderFinder(fileSystem);
        var found = await finder.FindHeadersAsync(exePath, Path.GetDirectoryName(exePath)!, true, progress);

        if (found.Count == 0)
            throw new ExtractionFailed(
                "No NeFS archive header could be found in that exe. Either it isn't the game's exe, or this " +
                "version keeps its files somewhere this can't follow.");

        cancel.ThrowIfCancellationRequested();
        log?.Invoke($"  found {found.Count} archive(s).");

        // Every exe seen so far carries one; if that ever changes, the one with
        // language files in it is the one wanted, so they are tried in turn.
        Exception? last = null;
        foreach (var source in found)
        {
            try
            {
                var archive = await reader.ReadArchiveAsync(source, progress);
                var hasLanguages = archive.Items.EnumerateById()
                    .Any(i => i.FileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase));

                if (!hasLanguages && found.Count > 1) continue;

                return (archive, new ArchiveLocation(source.DataFilePath,
                    source.PrimaryOffset, source.PrimarySize, source.SecondaryOffset, source.SecondarySize), true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        throw new ExtractionFailed(
            "An archive header was found, but the archive behind it couldn't be read.", last);
    }
}
