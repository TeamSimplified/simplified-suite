using System.Text.Json;
using System.Text.Json.Serialization;

namespace Simplified.Language;

/// <summary>Where a set of language files came from, and what it cost.</summary>
/// <param name="Folder">The folder holding them.</param>
/// <param name="Files">Their names.</param>
/// <param name="FromCache">True when they were already there and nothing was read out of the game.</param>
/// <param name="Searched">True when the exe had to be searched — the slow path, once per build.</param>
public sealed record LanguageSet(string Folder, IReadOnlyList<string> Files, bool FromCache, bool Searched);

/// <summary>
/// The game's language files, had from the game rather than carried.
///
/// A full install holds no .lng at all. They live in an archive whose header is
/// inside F1_25.exe and whose data is in game.dat beside it, which is why every
/// app that wanted them used to ship its own copy — eleven files, sixty-six
/// megabytes, correct on the day they were taken and drifting from that day on.
///
/// This takes them out instead, and then keeps them: extraction is quick, but
/// finding the archive means searching a 408 MB exe end to end, which is about
/// half a minute. So the answer to "where is it" is written down against the
/// build it was found in, and the files themselves are kept against that same
/// build. A game update invalidates both by not matching, which is exactly when
/// they should be taken again.
/// </summary>
public static class LanguageFiles
{
    /// <summary>How many the game ships. Used to tell a finished cache from a half-written one.</summary>
    public const int Expected = 11;

    /// <summary>What the game's exe is called, as a pattern.</summary>
    public const string ExeNamePattern = LanguageExtractor.ExeNamePattern;

    private const string LocationFileName = "archive.json";

    /// <summary>The game's exe inside an install, or null.</summary>
    public static string? FindExe(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return null;
        try
        {
            return Directory.EnumerateFiles(gameDir, ExeNamePattern, SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A folder holding this install's language files, extracting them if they
    /// are not already there.
    ///
    /// <paramref name="cacheRoot"/> is the app's own — somewhere under its
    /// folder, since these apps write nothing outside it. The files sit in a
    /// subfolder named for the build, so an update lands beside the old set
    /// rather than on top of it.
    /// </summary>
    public static async Task<LanguageSet> EnsureAsync(
        string? gameDir,
        string cacheRoot,
        Action<string>? log = null,
        CancellationToken cancel = default)
    {
        var exe = FindExe(gameDir)
                  ?? throw new ExtractionFailed(
                      "The game folder isn't set, or has no game exe at the top of it. The language files " +
                      "are inside that exe — there is nowhere else to get them.");

        var stamp = Stamp(exe);
        var folder = Path.Combine(cacheRoot, stamp);

        // A finished set for this build is the whole point of keeping them.
        if (Directory.Exists(folder))
        {
            var have = Directory.GetFiles(folder, "*" + LanguageExtractor.Extension);
            if (have.Length >= Expected)
                return new LanguageSet(folder, have.Select(Path.GetFileName).ToList()!, true, false);

            // Half a set means a run that died partway. Start again rather than
            // building a package out of some of last month's text.
            try { Directory.Delete(folder, recursive: true); } catch { /* the extract will say */ }
        }

        log?.Invoke("Taking the language files out of the game…");
        var known = ReadLocation(cacheRoot, stamp);
        var result = await LanguageExtractor.ExtractAsync(exe, folder, known, log, cancel);

        if (result.FoundBySearch) WriteLocation(cacheRoot, stamp, result.Location);

        return new LanguageSet(folder, result.Written, false, result.FoundBySearch);
    }

    /// <summary>
    /// The folder for this install's build if it is already there, and null
    /// otherwise — without reading a byte of the game.
    ///
    /// For the callers that must not stall: a window opening has no business
    /// spending half a minute in a constructor, so it asks whether the work has
    /// already been done and arranges to do it elsewhere if not.
    /// </summary>
    public static string? Cached(string? gameDir, string cacheRoot)
    {
        var exe = FindExe(gameDir);
        if (exe is null) return null;

        var folder = Path.Combine(cacheRoot, Stamp(exe));
        try
        {
            return Directory.Exists(folder)
                   && Directory.GetFiles(folder, "*" + LanguageExtractor.Extension).Length >= Expected
                ? folder
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="EnsureAsync"/> for callers that are already blocking — a
    /// package being written, a project being created. Run on the pool rather
    /// than waited on in place: waiting on the window's own thread for work
    /// that wants to come back to it is how a deadlock is spelled.
    /// </summary>
    public static LanguageSet Ensure(string? gameDir, string cacheRoot, Action<string>? log = null) =>
        Task.Run(() => EnsureAsync(gameDir, cacheRoot, log)).GetAwaiter().GetResult();

    /// <summary>
    /// Write them somewhere the caller names, without keeping a copy. For the
    /// extractor app, where the folder is the point rather than a cache.
    /// </summary>
    public static async Task<ExtractResult> ExtractToAsync(
        string? gameDir,
        string outputDir,
        string cacheRoot,
        Action<string>? log = null,
        CancellationToken cancel = default)
    {
        var exe = FindExe(gameDir)
                  ?? throw new ExtractionFailed(
                      "The game folder isn't set, or has no game exe at the top of it.");

        var stamp = Stamp(exe);
        var result = await LanguageExtractor.ExtractAsync(exe, outputDir, ReadLocation(cacheRoot, stamp), log, cancel);
        if (result.FoundBySearch) WriteLocation(cacheRoot, stamp, result.Location);
        return result;
    }

    /// <summary>
    /// What identifies a build of the exe: its version and its length.
    ///
    /// Not a hash — that is 408 MB to read before anything can start, which is
    /// most of what remembering was meant to save. These two are free, and a
    /// wrong match costs one failed read before the search runs anyway.
    /// </summary>
    private static string Stamp(string exePath)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            var version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
            return $"{version}-{new FileInfo(exePath).Length}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static ArchiveLocation? ReadLocation(string cacheRoot, string stamp)
    {
        try
        {
            var path = Path.Combine(cacheRoot, LocationFileName);
            if (!File.Exists(path)) return null;

            var all = JsonSerializer.Deserialize<List<StoredLocation>>(File.ReadAllText(path));
            return all?.FirstOrDefault(l => l.Stamp == stamp)?.ToLocation();
        }
        catch
        {
            return null;
        }
    }

    private static void WriteLocation(string cacheRoot, string stamp, ArchiveLocation location)
    {
        try
        {
            Directory.CreateDirectory(cacheRoot);
            var path = Path.Combine(cacheRoot, LocationFileName);

            var all = File.Exists(path)
                ? JsonSerializer.Deserialize<List<StoredLocation>>(File.ReadAllText(path)) ?? []
                : [];

            all.RemoveAll(l => l.Stamp == stamp);
            all.Insert(0, StoredLocation.From(stamp, location));

            File.WriteAllText(path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A cache that cannot be written costs half a minute next time and
            // nothing else. Not worth failing an extraction over.
        }
    }

    private sealed class StoredLocation
    {
        [JsonPropertyName("stamp")] public string Stamp { get; set; } = "";
        [JsonPropertyName("dataFile")] public string DataFilePath { get; set; } = "";
        [JsonPropertyName("primaryOffset")] public long PrimaryOffset { get; set; }
        [JsonPropertyName("primarySize")] public int? PrimarySize { get; set; }
        [JsonPropertyName("secondaryOffset")] public long SecondaryOffset { get; set; }
        [JsonPropertyName("secondarySize")] public int? SecondarySize { get; set; }

        public ArchiveLocation ToLocation() =>
            new(DataFilePath, PrimaryOffset, PrimarySize, SecondaryOffset, SecondarySize);

        public static StoredLocation From(string stamp, ArchiveLocation l) => new()
        {
            Stamp = stamp,
            DataFilePath = l.DataFilePath,
            PrimaryOffset = l.PrimaryOffset,
            PrimarySize = l.PrimarySize,
            SecondaryOffset = l.SecondaryOffset,
            SecondarySize = l.SecondarySize,
        };
    }
}
