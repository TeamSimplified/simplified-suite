using System.IO;
using Simplified.Language;
using SimplifiedTeamEditor.Core;

namespace SimplifiedLauncher.Mods;

/// <summary>
/// The game's own copies of the files mods are merged into rather than laid
/// over: the database and the eleven language files, kept in
/// <c>runtime\original</c> beside the exe.
///
/// Taken the way the editors take them and kept the same way — the database
/// by the Team Editor's <see cref="GameOriginals"/>, the language files out of
/// the game's exe by <see cref="LanguageFiles"/> — each stamped with the build
/// they came from and taken again when the game moves on. Nothing here decides
/// anything the editors do not already decide.
///
/// Taken when the window opens, as the editors take theirs, and asked for
/// again at a launch only when a chosen mod carries one of these files and
/// they are still not current.
/// </summary>
public static class Originals
{
    /// <summary>What the app needs to run and nobody should be poking at.</summary>
    private static string RuntimeDir => Path.Combine(AppContext.BaseDirectory, "runtime");

    /// <summary>Where the copies live, the same place the editors keep theirs.</summary>
    public static string FolderPath => Path.Combine(RuntimeDir, "original");

    /// <summary>The clean database.</summary>
    public static string DatabasePath => Path.Combine(FolderPath, GameOriginals.DatabaseFileName);

    /// <summary>The clean language files, one per language.</summary>
    public static string LanguageDir => Path.Combine(FolderPath, LanguageFiles.FolderName);

    /// <summary>Where the language archive was found in the exe, so the next build's search is the only one.</summary>
    private static string LanguageCacheDir => Path.Combine(RuntimeDir, "language");

    /// <summary>Whether any of <paramref name="mods"/> carries a file that is merged rather than copied.</summary>
    public static bool Wanted(IEnumerable<InstalledMod> mods) =>
        mods.Any(mod =>
        {
            var game = Path.Combine(mod.Folder, ModStaging.GameFolderName);
            return Directory.Exists(game)
                   && Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories)
                               .Any(file => Merged.Kind(file) is not Merged.Kinds.Not);
        });

    /// <summary>
    /// True when the copies here came out of the game that is installed now.
    /// Reads a couple of small notes and file names, nothing more.
    /// </summary>
    public static bool Current(string? gameDir) =>
        LanguageFiles.Check(gameDir, LanguageDir) == LanguageState.Current
        && GameOriginals.Check(gameDir, FolderPath) == OriginalsState.Current;

    /// <summary>
    /// Take whichever of the two is missing or from an older build. Returns
    /// anything worth telling the person about the database taken, which is
    /// nothing ordinarily. Throws when the game cannot be read.
    /// </summary>
    public static IReadOnlyList<string> Refresh(string? gameDir)
    {
        if (LanguageFiles.Check(gameDir, LanguageDir) != LanguageState.Current)
            LanguageFiles.Refresh(gameDir, LanguageDir, LanguageCacheDir);

        return GameOriginals.Check(gameDir, FolderPath) == OriginalsState.Current
            ? []
            : GameOriginals.Refresh(gameDir, FolderPath);
    }
}
