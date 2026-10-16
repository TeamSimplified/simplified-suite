using System.IO;

namespace SimplifiedLauncher.Mods;

/// <summary>What came of stacking the chosen mods into one folder.</summary>
/// <param name="Folder">The install folder, whether or not anything went in it.</param>
/// <param name="Files">How many files were written.</param>
/// <param name="Replaced">
/// How many of those landed on a file another mod had already put there. The
/// number worth showing one day: it is exactly the count of places where the
/// order mattered.
/// </param>
/// <param name="Empty">
/// Mods that had nothing to give — an import that came out
/// <see cref="Staged.Unrecognised"/> has no <c>F1 25</c> folder to take from.
/// </param>
/// <param name="Merged">
/// Databases and language files written by merging the mods' copies into the
/// game's own, rather than by copying one of them.
/// </param>
/// <param name="Problem">Why there is no usable install folder. Null when there is.</param>
public sealed record Stacked(
    string Folder,
    int Files = 0,
    int Replaced = 0,
    int Empty = 0,
    int Merged = 0,
    string? Problem = null);

/// <summary>
/// Lays the chosen mods over one another into a single <c>install</c> folder
/// beside the exe: <c>install\F1 25\</c>, and under that the files of every mod
/// that was ticked, merged. What comes out is what will go into the game, which
/// is why it is shaped like the game's own folder and not like a mod.
///
/// Order is the order they were ticked, and it is the whole point of keeping
/// that order: two mods that both touch a file cannot both have it, so the one
/// chosen later is written last and is the one that survives. Everything else
/// about the two is kept — merging is per file, not per folder, so a mod that
/// only replaces one car does not take the other mod's cars with it.
///
/// The game database and the language files are the exception to the order
/// winning whole: they are set aside while the rest is laid, and merged into
/// the game's own copies afterwards — see <see cref="Merged"/>. The order
/// still decides, but entry by entry rather than file by file.
///
/// Built from nothing every time. The install folder is not a record of
/// anything, it is this launch's answer: last launch's leftovers in it would be
/// files nobody chose, installed into the game without ever appearing in the
/// list.
/// </summary>
public static class ModStack
{
    /// <summary>Beside the mods folder, which is beside the exe.</summary>
    public static string FolderPath => Path.Combine(AppContext.BaseDirectory, "install");

    /// <summary>Marks a half-built one, so a crash leaves something recognisable.</summary>
    private const string BuildingPrefix = ".install-";

    /// <summary>
    /// Stack <paramref name="order"/> into the install folder, first ticked
    /// first, and say what that took.
    ///
    /// Built off to one side and moved into place at the end, so an install
    /// folder that exists is always a finished one. Anything that goes wrong
    /// half way — a mod deleted from under us, a file held open — leaves the
    /// previous one where it was rather than a mixture of two launches that
    /// would look exactly like a finished one.
    /// </summary>
    /// <param name="gameDir">
    /// The game's folder, for the patched exe that language files need. See
    /// <see cref="ExePatch"/>.
    /// </param>
    public static Stacked Build(IReadOnlyList<InstalledMod> order, string? gameDir = null)
    {
        var parent = Path.GetDirectoryName(FolderPath)!;
        Directory.CreateDirectory(parent);
        Sweep(parent);

        var building = Path.Combine(parent, BuildingPrefix + Guid.NewGuid().ToString("n"));
        var game = Path.Combine(building, ModStaging.GameFolderName);

        try
        {
            Directory.CreateDirectory(game);

            var files = 0;
            var replaced = 0;
            var empty = 0;

            // Set aside for merging, in the order they were ticked: each
            // database by where it goes, and each folder of language files as
            // one set per mod.
            var databases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var languages = new Dictionary<string, List<IReadOnlyDictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in order)
            {
                var from = Path.Combine(mod.Folder, ModStaging.GameFolderName);

                if (!Directory.Exists(from))
                {
                    empty++;
                    continue;
                }

                var theirs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

                var (written, over) = Lay(from, game, (relative, file) =>
                {
                    if (Merged.Kind(file) == Merged.Kinds.Database)
                    {
                        if (!databases.TryGetValue(relative, out var list)) databases[relative] = list = [];
                        list.Add(file);
                        return;
                    }

                    var folder = Path.GetDirectoryName(relative) ?? "";
                    if (!theirs.TryGetValue(folder, out var set))
                        theirs[folder] = set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    set[Path.GetFileName(file)] = file;
                });

                foreach (var (folder, set) in theirs)
                {
                    if (!languages.TryGetValue(folder, out var list)) languages[folder] = list = [];
                    list.Add(set);
                }

                files += written;
                replaced += over;
            }

            var merged = 0;

            foreach (var (relative, sources) in databases)
            {
                replaced += Merged.Database(sources, Path.Combine(game, relative));
                merged++;
            }

            foreach (var (folder, sets) in languages)
                merged += Merged.Languages(sets, Path.Combine(game, folder));

            // Language files the game would never read without it.
            if (languages.Count > 0) ExePatch.Stage(gameDir, game);

            Swap(building);

            return new Stacked(FolderPath, files, replaced, empty, merged);
        }
        catch (Exception e)
        {
            return new Stacked(FolderPath, Problem: $"The mods couldn't be put together. ({e.GetType().Name}: {e.Message})");
        }
        finally
        {
            try { if (Directory.Exists(building)) Directory.Delete(building, recursive: true); }
            catch { /* dot-prefixed, and swept by the next launch */ }
        }
    }

    /// <summary>
    /// Throw the install folder away.
    ///
    /// For once the game has been put back: what is in it is then a copy of
    /// files that are nowhere near the game any more, taking a whole set of
    /// mods' worth of disk sitting beside the mods it was copied from.
    ///
    /// Moved aside and then deleted, the way it was built. What a delete that
    /// stops partway leaves behind is a dot-prefixed folder the next launch
    /// sweeps up, rather than half an install folder where a whole one goes.
    /// </summary>
    public static void Clear()
    {
        if (!Directory.Exists(FolderPath)) return;

        var spent = Path.Combine(
            Path.GetDirectoryName(FolderPath)!, BuildingPrefix + Guid.NewGuid().ToString("n"));

        try
        {
            Directory.Move(FolderPath, spent);
            Directory.Delete(spent, recursive: true);
        }
        catch { /* out of the way, and swept by the next launch */ }
    }

    /// <summary>
    /// Copy one mod's game files over whatever is already there, and say how
    /// many were written and how many of those took another mod's place.
    ///
    /// A file that is merged instead goes to <paramref name="setAside"/> with
    /// its path under the game folder — but only when there is a clean copy to
    /// merge it into. Without one the file is laid like any other, which is
    /// what the launcher always did, rather than left out.
    /// </summary>
    private static (int Written, int Replaced) Lay(string from, string onto, Action<string, string> setAside)
    {
        var written = 0;
        var replaced = 0;

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);

            if (Mergeable(file))
            {
                setAside(relative, file);
                continue;
            }

            var landing = Path.Combine(onto, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(landing)!);

            if (File.Exists(landing)) replaced++;

            File.Copy(file, landing, overwrite: true);
            written++;
        }

        return (written, replaced);
    }

    /// <summary>
    /// A database or a language file the game ships, and a clean copy of it
    /// here to merge into. A language file of some other name — a mod's own
    /// lookup table, say — is the mod's and is laid as it is.
    /// </summary>
    private static bool Mergeable(string file) => Merged.Kind(file) switch
    {
        Merged.Kinds.Database => File.Exists(Originals.DatabasePath),
        Merged.Kinds.Language => Merged.IsGameLanguage(Path.GetFileName(file)),
        _ => false,
    };

    /// <summary>
    /// Put the newly built folder where the install folder goes.
    ///
    /// The old one is moved aside before it is deleted rather than deleted in
    /// place, for the reason the mod library does the same: a recursive delete
    /// that meets a file somebody has open stops partway, and an install folder
    /// half deleted is indistinguishable from one that was built that way.
    /// </summary>
    private static void Swap(string building)
    {
        string? old = null;

        if (Directory.Exists(FolderPath))
        {
            old = Path.Combine(Path.GetDirectoryName(FolderPath)!, BuildingPrefix + Guid.NewGuid().ToString("n"));
            Directory.Move(FolderPath, old);
        }

        try
        {
            Directory.Move(building, FolderPath);
        }
        catch
        {
            if (old is not null) Directory.Move(old, FolderPath);
            throw;
        }

        if (old is not null)
            try { Directory.Delete(old, recursive: true); } catch { /* swept next time */ }
    }

    /// <summary>Delete what an earlier launch could not finish with.</summary>
    private static void Sweep(string parent)
    {
        foreach (var folder in Directory.EnumerateDirectories(parent, BuildingPrefix + "*"))
            try { Directory.Delete(folder, recursive: true); } catch { /* another time */ }
    }
}
