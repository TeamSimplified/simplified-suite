using System.Text.Json;
using System.Text.Json.Serialization;

namespace Simplified.Language;

/// <summary>Whether the language files beside an app still match the game.</summary>
public enum LanguageState
{
    /// <summary>They are there and they came from the installed build.</summary>
    Current,

    /// <summary>They have never been taken. A fresh install of the app.</summary>
    FirstTime,

    /// <summary>They are from a build the game has since moved on from.</summary>
    GameUpdated,

    /// <summary>No game to take them from, so there is nothing to say.</summary>
    NoGame,
}

/// <summary>
/// The game's language files, kept beside the app and kept current.
///
/// A full install holds no .lng anywhere. They are in an archive whose header
/// sits inside F1_25.exe and whose data sits in game.dat beside it, which is why
/// every app that wanted them used to ship its own copy - eleven files,
/// sixty-six megabytes, correct on the day they were taken and drifting from
/// that day on.
///
/// They are taken from the game instead, into the same original\localisation\
/// the apps have always read, so nothing downstream had to learn a new place to
/// look. What is new is that the folder says which build it came from, and an
/// app checks that when it starts: a game that has moved on is the one moment
/// these need taking again, and the start of a session is the one moment a
/// person is expecting to wait.
/// </summary>
public static class LanguageFiles
{
    /// <summary>How many the game ships. Fewer than this is a run that died partway.</summary>
    public const int Expected = 11;

    /// <summary>What the game's exe is called, as a pattern.</summary>
    public const string ExeNamePattern = LanguageExtractor.ExeNamePattern;

    /// <summary>
    /// What the game calls the folder these sit in, and so what the apps call
    /// the folder they put them in. One spelling, since a package has to put
    /// them back where the game looks.
    /// </summary>
    public const string FolderName = "localisation";

    /// <summary>The note beside the files saying which build they came out of.</summary>
    public const string StampFileName = "taken-from.json";

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
    /// Whether the files in <paramref name="languageDir"/> still match the
    /// installed game — asked at startup, so it reads names and a small note and
    /// nothing else.
    /// </summary>
    public static LanguageState Check(string? gameDir, string languageDir)
    {
        var exe = FindExe(gameDir);
        if (exe is null) return LanguageState.NoGame;

        var have = Count(languageDir);
        if (have < Expected) return LanguageState.FirstTime;

        var stamp = ReadStamp(languageDir);
        return stamp == Stamp(exe) ? LanguageState.Current : LanguageState.GameUpdated;
    }

    /// <summary>
    /// What to say to a person while <see cref="Refresh"/> runs, or null when
    /// there is nothing to run.
    ///
    /// Here rather than in each app, so the two that do this say the same
    /// thing. The same line either way: whether the app has never had these
    /// files or has them from a build the game has moved past, what it is doing
    /// is bringing them up to date, and which of the two it is is our
    /// bookkeeping rather than news.
    /// </summary>
    public static string? Headline(LanguageState state) =>
        state is LanguageState.FirstTime or LanguageState.GameUpdated ? "Updating game files" : null;

    /// <summary>
    /// Take them out of the game into <paramref name="languageDir"/>, replacing
    /// whatever is there, and write the note that says which build they are.
    ///
    /// <paramref name="cacheRoot"/> is where the archive's location is
    /// remembered between builds — finding it means searching a 408 MB exe, and
    /// that is the part worth not repeating.
    /// </summary>
    public static void Refresh(string? gameDir, string languageDir, string cacheRoot,
                               IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var exe = FindExe(gameDir)
                  ?? throw new ExtractionFailed(
                      "The game folder isn't set, or has no game exe at the top of it. The language files " +
                      "are inside that exe — there is nowhere else to get them.");

        var stamp = Stamp(exe);

        // Into a folder of its own first, then swapped in. A half-written set is
        // worse than none: the app would read eleven files, some of them from
        // the build before, and nothing would look wrong.
        var staging = Path.Combine(cacheRoot, "incoming");
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* the extract will say */ }

        progress?.Report("Checking the game files…");
        var known = ReadLocation(cacheRoot, stamp);

        // Two channels, because they are for two different readers: the
        // extractor's log names files and byte counts, which belongs in the tool
        // that writes them out, and `say` is the stage a person waiting is in.
        var result = Task.Run(() => LanguageExtractor.ExtractAsync(
            exe, staging, known, log: null, say: progress, cancel: cancel))
            .GetAwaiter().GetResult();

        if (result.FoundBySearch) WriteLocation(cacheRoot, stamp, result.Location);

        progress?.Report("Putting the files in place…");
        Directory.CreateDirectory(languageDir);
        foreach (var file in Directory.EnumerateFiles(staging, "*" + LanguageExtractor.Extension))
            File.Copy(file, Path.Combine(languageDir, Path.GetFileName(file)), overwrite: true);

        WriteStamp(languageDir, stamp);
        Tidy(cacheRoot);
    }

    /// <summary>
    /// Clear the folders an earlier build of these apps kept the files in.
    ///
    /// They used to live here, one folder per game build, and the app read them
    /// from here. Now they sit with the rest of the originals, so anything left
    /// under the cache is sixty-six megabytes of a previous arrangement — and on
    /// a portable app the folder is the install, so it would stay there for good.
    /// The location cache itself is a file, not a folder, and is kept.
    /// </summary>
    private static void Tidy(string cacheRoot)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(cacheRoot))
                try { Directory.Delete(folder, recursive: true); } catch { /* in use; next time */ }
        }
        catch
        {
            // Housekeeping. A refresh that worked is not undone by this failing.
        }
    }

    /// <summary>
    /// Write them somewhere the caller names, without touching what an app
    /// reads. For the extractor tool, where the folder is the point.
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
        var result = await LanguageExtractor.ExtractAsync(
            exe, outputDir, ReadLocation(cacheRoot, stamp), log, cancel: cancel);
        if (result.FoundBySearch) WriteLocation(cacheRoot, stamp, result.Location);
        return result;
    }

    private static int Count(string languageDir)
    {
        try
        {
            return Directory.Exists(languageDir)
                ? Directory.GetFiles(languageDir, "*" + LanguageExtractor.Extension)
                    .Count(f => Path.GetFileName(f).StartsWith("language_", StringComparison.OrdinalIgnoreCase))
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// What identifies a build of the exe: its version and its length.
    ///
    /// Not a hash — that is 408 MB to read at every startup, to answer a
    /// question these two answer for free. A wrong match would cost one failed
    /// read and a search, and cannot happen without the exe being rebuilt at
    /// the same version and the same byte count.
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

    private static string? ReadStamp(string languageDir)
    {
        try
        {
            var path = Path.Combine(languageDir, StampFileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<TakenFrom>(File.ReadAllText(path))?.Build
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteStamp(string languageDir, string stamp)
    {
        try
        {
            File.WriteAllText(Path.Combine(languageDir, StampFileName),
                JsonSerializer.Serialize(
                    new TakenFrom
                    {
                        Comment = "Which build of the game these language files came out of. Written by the " +
                                  "app when it takes them; read at startup to notice a game update.",
                        Build = stamp,
                        Taken = DateTime.Now.ToString("yyyy-MM-dd"),
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Without the note the next startup takes them again, which is slow
            // and correct. Not worth failing over.
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
            // nothing else.
        }
    }

    private sealed class TakenFrom
    {
        [JsonPropertyName("_comment")] public string Comment { get; set; } = "";
        [JsonPropertyName("build")] public string Build { get; set; } = "";
        [JsonPropertyName("taken")] public string Taken { get; set; } = "";
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
