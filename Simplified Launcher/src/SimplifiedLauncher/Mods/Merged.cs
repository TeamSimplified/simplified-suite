using System.IO;
using EgoEngineLibrary.Data.GameDatabases;
using SimplifiedTeamEditor.Core;
using SimplifiedTeamEditor.Core.Lng;

namespace SimplifiedLauncher.Mods;

/// <summary>
/// The files that are merged into the game's own copy rather than laid over
/// it: the game database, and the language files.
///
/// Both are one file holding a whole table, and mods change a handful of
/// entries in it. Laid over one another the last mod's file would win whole,
/// and every entry the earlier mods changed would go back to the game's — two
/// team-colour mods, and only one of them in the game. So each mod's file is
/// read as what it changed from the game's clean copy, and those changes are
/// put into a fresh copy of it one mod after another in the order they were
/// ticked. Where two change the same entry the later one wins, the same rule
/// as any other file the two have in common.
/// </summary>
public static class Merged
{
    public enum Kinds
    {
        /// <summary>An ordinary file, laid over whatever is there.</summary>
        Not,
        Database,
        Language,
    }

    /// <summary>Which of the merged kinds a file is, by its name.</summary>
    public static Kinds Kind(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals(GameOriginals.DatabaseFileName, StringComparison.OrdinalIgnoreCase)) return Kinds.Database;
        if (name.EndsWith(".lng", StringComparison.OrdinalIgnoreCase)) return Kinds.Language;
        return Kinds.Not;
    }

    /// <summary>
    /// The clean database with every source's changes in it, written to
    /// <paramref name="target"/>. Returns how many record changes landed on
    /// one an earlier mod had made.
    /// </summary>
    /// <param name="sources">The mods' copies, in the order they were ticked.</param>
    public static int Database(IReadOnlyList<string> sources, string target)
    {
        var clean = GameDatabase.Load(Originals.DatabasePath);

        var layers = sources.Select(path =>
        {
            var layer = GameDatabase.Load(path);
            if (layer.Cipher.Game != clean.Cipher.Game)
                throw new InvalidDataException(
                    $"A mod's database is from {layer.Cipher.Game}, and the game's is {clean.Cipher.Game}.");
            return layer;
        }).ToList();

        var layered = DatabaseLayers.Apply(clean, layers);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        layered.Database.Save(target);
        return layered.Overridden;
    }

    /// <summary>
    /// Every language in one folder, each the game's own file with every mod's
    /// changes to it put in, written to <paramref name="targetFolder"/>.
    /// Returns how many files were written.
    ///
    /// All eleven are written whatever the mods shipped. A mod that carries
    /// only English still wants its names in the game when it is played in
    /// French, so a language a mod has no file for takes that mod's English
    /// changes — or its first file's, when it has no English either. A mod
    /// that does ship the language keeps its own translation.
    ///
    /// Only changed and added entries are changes. An entry missing from a
    /// mod's file is not a removal: the Language Editor exports exactly the
    /// lines that were changed and nothing else, and reading the rest as
    /// deleted would empty the game's text.
    /// </summary>
    /// <param name="mods">
    /// Per mod, in the order they were ticked: its language files in this
    /// folder, by file name.
    /// </param>
    public static int Languages(IReadOnlyList<IReadOnlyDictionary<string, string>> mods, string targetFolder)
    {
        var originals = Directory.EnumerateFiles(Originals.LanguageDir, "*.lng")
            .ToDictionary(path => Path.GetFileName(path), path => path, StringComparer.OrdinalIgnoreCase);

        // Read once each: a language file is six megabytes, and a mod's English
        // may stand in for ten other languages.
        var loaded = new Dictionary<string, LngFile>(StringComparer.OrdinalIgnoreCase);
        LngFile Load(string path) => loaded.TryGetValue(path, out var lng) ? lng : loaded[path] = LngFile.Load(path);

        var changes = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> Changes(string modFile)
        {
            if (changes.TryGetValue(modFile, out var known)) return known;
            var clean = Load(originals[Path.GetFileName(modFile)!]);
            return changes[modFile] = Difference(clean, Load(modFile));
        }

        Directory.CreateDirectory(targetFolder);
        var written = 0;

        foreach (var (name, path) in originals)
        {
            var edits = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var mod in mods)
            {
                if (Source(mod, name, originals) is not { } file) continue;
                foreach (var (key, value) in Changes(file)) edits[key] = value;
            }

            var language = Load(path);
            var data = edits.Keys.All(language.Entries.ContainsKey)
                ? language.BuildSaveData(edits)
                : language.BuildRebuiltData(Union(language.Entries, edits));

            Write(Path.Combine(targetFolder, name), data);
            written++;
        }

        return written;
    }

    /// <summary>
    /// Which of a mod's files speaks for <paramref name="language"/>: its own
    /// file for it, else its English, else the first it has. Only files that
    /// are one of the game's languages count — anything else is not a change
    /// to the game's text.
    /// </summary>
    private static string? Source(IReadOnlyDictionary<string, string> mod, string language,
                                  IReadOnlyDictionary<string, string> originals)
    {
        if (mod.TryGetValue(language, out var own)) return own;
        if (mod.TryGetValue(EnglishFileName, out var english)) return english;

        return mod.Where(pair => originals.ContainsKey(pair.Key))
                  .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                  .Select(pair => pair.Value)
                  .FirstOrDefault();
    }

    private const string EnglishFileName = "language_eng.lng";

    /// <summary>Whether a language file is one of the game's own, and so merged rather than copied.</summary>
    public static bool IsGameLanguage(string fileName) =>
        File.Exists(Path.Combine(Originals.LanguageDir, fileName));

    /// <summary>Entries the mod's file has with different text from the clean one, and entries it adds.</summary>
    private static Dictionary<string, string> Difference(LngFile clean, LngFile mod)
    {
        var changed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in mod.Entries)
            if (!clean.Entries.TryGetValue(key, out var was) || was != value) changed[key] = value;
        return changed;
    }

    private static Dictionary<string, string> Union(IReadOnlyDictionary<string, string> all,
                                                    IReadOnlyDictionary<string, string> edits)
    {
        var union = new Dictionary<string, string>(all, StringComparer.Ordinal);
        foreach (var (key, value) in edits) union[key] = value;
        return union;
    }

    private static void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }
}
