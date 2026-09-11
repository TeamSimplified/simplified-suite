using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Operations;
using EgoEngineLibrary.Util;
using SimplifiedResourceExtractor.Core.Scan;

namespace SimplifiedResourceExtractor.Core.Extract;

/// <summary>What to write, and how.</summary>
/// <param name="Scan">What the rules found. Only these archives are opened.</param>
/// <param name="OutputDir">Where results land.</param>
/// <param name="Individual">One output per source archive rather than one merged.</param>
/// <param name="StripMipmaps">Fold external mip chains in, so the output stands alone.</param>
/// <param name="AddFolderPrefix">Name outputs after the folder they came out of.</param>
/// <param name="GameDir">The install the <c>.mipmaps</c> sidecars live in. Stripping needs it.</param>
public sealed record JobRequest(
    ScanResult Scan,
    string OutputDir,
    bool Individual,
    bool StripMipmaps,
    bool AddFolderPrefix,
    string? GameDir);

/// <summary>What a job wrote.</summary>
public sealed record JobResult(IReadOnlyList<string> Written, int Resources);

/// <summary>
/// Turning a scan into archives on disk.
///
/// The scan already said which archives matter and which resources in them are
/// wanted, so this opens only those, takes only those, and writes. Nothing is
/// staged: the tool this replaces copied every matching archive into a working
/// folder first, which for a game-wide rule is gigabytes of copying to produce
/// a file that is usually megabytes.
/// </summary>
public static class ExtractionJob
{
    /// <summary>What a merged output is called before any prefix or suffix.</summary>
    private const string MergedName = "extracted";

    public static JobResult Run(JobRequest request, Action<string>? log = null,
                                CancellationToken cancel = default)
    {
        if (request.Scan.Matched.Count == 0)
            throw new InvalidOperationException("Nothing matched, so there is nothing to write.");

        if (request.StripMipmaps && string.IsNullOrWhiteSpace(request.GameDir))
            throw new InvalidOperationException(
                "Stripping mipmaps reads the game's .mipmaps files, so it needs the game folder.");

        Directory.CreateDirectory(request.OutputDir);

        return request.Individual ? RunIndividually(request, log, cancel) : RunMerged(request, log, cancel);
    }

    // ── One archive out of many ───────────────────────────────────────

    private static JobResult RunMerged(JobRequest request, Action<string>? log, CancellationToken cancel)
    {
        var wanted = Wanted(request.Scan);
        var paths = request.Scan.Matched.Select(a => a.Path).ToList();

        // Deliberately not "merging N resources": the wanted set carries a
        // companion for every texture in it, and those are asked for rather
        // than known to exist. What was collected is reported after the fact.
        log?.Invoke($"Merging from {paths.Count:N0} archive(s)...");

        // The output's own version, not the default: UpdateOffsets does
        // version-dependent size maths, so an archive that claims 4 while
        // holding 3's layout writes subtly wrong offsets.
        var output = new ErpFile { Version = request.Scan.Version };

        foreach (var path in paths)
        {
            cancel.ThrowIfCancellationRequested();
            ErpOperations.Merge(output, [path], overwrite: true, include: wanted.Contains);
        }

        log?.Invoke($"  {output.Resources.Count:N0} resources collected.");

        var name = Decorate(request, MergedName, SourceFolderName(paths));
        var written = Finish(output, Path.Combine(request.OutputDir, name + ".erp"), request, log);

        return new JobResult([written], output.Resources.Count);
    }

    // ── One archive per source ────────────────────────────────────────

    private static JobResult RunIndividually(JobRequest request, Action<string>? log, CancellationToken cancel)
    {
        var wanted = Wanted(request.Scan);
        var written = new List<string>();
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var resources = 0;

        foreach (var archive in request.Scan.Matched)
        {
            cancel.ThrowIfCancellationRequested();

            var output = new ErpFile { Version = archive.Version };
            ErpOperations.Merge(output, [archive.Path], overwrite: true, include: wanted.Contains);

            if (output.Resources.Count == 0) continue;

            // Two archives under different folders can share a file name -
            // every team's wep\ holds one - so the second gets a number rather
            // than overwriting the first. The folder prefix usually tells them
            // apart before it comes to that, since the folder is what differs.
            var stem = Decorate(request, Path.GetFileNameWithoutExtension(archive.Path) + "_extracted",
                                SourceFolderName([archive.Path]));

            used[stem] = used.GetValueOrDefault(stem) + 1;
            if (used[stem] > 1) stem += "_" + used[stem];

            var path = Path.Combine(request.OutputDir, stem + ".erp");

            log?.Invoke($"{Path.GetFileName(archive.Path)} - {output.Resources.Count:N0} resource(s)");
            written.Add(Finish(output, path, request, log));
            resources += output.Resources.Count;
        }

        return new JobResult(written, resources);
    }

    // ── Shared ────────────────────────────────────────────────────────

    /// <summary>
    /// Every identifier the rules matched, anywhere, plus the other half of any
    /// texture among them.
    ///
    /// A texture is two resources: the view (<c>x.texarray</c>) and the surface
    /// holding its pixels (<c>x.texarray.image</c>). One without the other is
    /// not a texture, and a rule can easily name one and not the other — so
    /// whichever is matched brings its partner. The tool this replaces got the
    /// same effect by accident, keeping any resource whose identifier merely
    /// contained a matched one, which also quietly undid exclusions: a rule
    /// ending <c>- .image</c> matched the view, and then the substring test put
    /// the image back.
    /// </summary>
    private static HashSet<string> Wanted(ScanResult scan)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var archive in scan.Matched)
        foreach (var identifier in archive.Identifiers)
        {
            wanted.Add(identifier);
        }

        foreach (var identifier in wanted.ToList())
        {
            wanted.Add(identifier + SurfaceSuffix);
            if (identifier.EndsWith(SurfaceSuffix, StringComparison.Ordinal))
                wanted.Add(identifier[..^SurfaceSuffix.Length]);
        }

        return wanted;
    }

    /// <summary>What a surface resource's identifier adds to its view's.</summary>
    private const string SurfaceSuffix = ".image";

    /// <summary>
    /// Strip if asked, then write. Stripping happens in memory on the way out
    /// rather than as a second pass over a file already on disk, so only the
    /// archive anybody wanted ever exists.
    /// </summary>
    private static string Finish(ErpFile erp, string path, JobRequest request, Action<string>? log)
    {
        if (request.StripMipmaps)
        {
            var result = ErpOperations.StripMips(erp, request.GameDir!, filter: null, log: null);
            log?.Invoke($"  mipmaps: {result.Embedded} embedded, {result.AlreadyInline} already inline, " +
                        $"{result.Unreachable} not found, {result.Failed} failed");

            path = Path.Combine(Path.GetDirectoryName(path)!,
                                Path.GetFileNameWithoutExtension(path) + "_stripped.erp");
        }

        erp.UpdateOffsets();
        ErpFileIO.Save(erp, path);
        log?.Invoke($"  wrote {Path.GetFileName(path)}");

        return path;
    }

    /// <summary>
    /// The source folder's name in front, when asked for.
    ///
    /// The folder the archives came OUT of, not the one they are going in to:
    /// the destination is the same for everything in a run and says nothing
    /// about any of it, while the source is exactly what tells two outputs apart
    /// — the install holds six <c>liam_lawson.erp</c>, identical in name and
    /// different only in which asset group they sit under.
    /// </summary>
    private static string Decorate(JobRequest request, string name, string sourceFolder) =>
        request.AddFolderPrefix && sourceFolder.Length > 0 ? sourceFolder + "_" + name : name;

    /// <summary>
    /// The deepest folder holding everything that went into one output: an
    /// archive's own folder when it is the only one, and the folder they share
    /// when several were merged.
    ///
    /// Merging has no single source, so the honest answer is the point at which
    /// they meet. Matches scattered across an install meet at its root, which
    /// still names the thing they came out of.
    /// </summary>
    private static string SourceFolderName(IReadOnlyList<string> archivePaths)
    {
        if (archivePaths.Count == 0) return "";

        var common = Path.GetDirectoryName(archivePaths[0])?.Split(Path.DirectorySeparatorChar) ?? [];

        foreach (var path in archivePaths.Skip(1))
        {
            var parts = Path.GetDirectoryName(path)?.Split(Path.DirectorySeparatorChar) ?? [];
            var shared = 0;
            while (shared < common.Length && shared < parts.Length
                   && string.Equals(common[shared], parts[shared], StringComparison.OrdinalIgnoreCase))
            {
                shared++;
            }

            common = common[..shared];
            if (common.Length == 0) break;
        }

        // A drive root splits to "D:" and an empty segment; neither is a name
        // worth putting in front of a file.
        var last = common.LastOrDefault(p => p.Length > 0 && !p.EndsWith(':')) ?? "";
        return last;
    }
}
