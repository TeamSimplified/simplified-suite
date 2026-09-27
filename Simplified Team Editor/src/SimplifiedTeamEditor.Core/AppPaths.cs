namespace SimplifiedTeamEditor.Core;

/// <summary>
/// Where the app keeps what belongs to the app.
///
/// It is portable — the folder is the install — so settings live beside the exe
/// rather than in %APPDATA%, and copying the folder carries them with it.
///
/// Scratch is deliberately not here. Transient working files go to the system
/// temp directory, which is writable wherever the exe happens to sit and gets
/// cleaned up by Windows on its own.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// The folder the exe lives in. AppContext.BaseDirectory is right for both
    /// an ordinary build and a single-file one; Assembly.Location returns ""
    /// in single-file (IL3000).
    /// </summary>
    public static string Root => AppContext.BaseDirectory;

    /// <summary>User settings. Beside the exe, one file, easy to delete.</summary>
    public static string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>
    /// The pristine game files a package is built from — the database, the
    /// language files, and the team-branding texarrays.
    /// </summary>
    public static string OriginalsDir => Path.Combine(Root, "runtime", "original");

    /// <summary>
    /// The eleven language files, in the folder the game itself uses for them.
    ///
    /// Sitting with the rest of the originals because that is what they are, but
    /// unlike the rest they are not shipped: the game keeps no .lng on disk, so
    /// they are taken out of its exe and put here — once, and again whenever the
    /// game is patched. See Simplified.Language.LanguageFiles, and the check the
    /// window runs when it opens.
    /// </summary>
    public static string LanguageDir =>
        Path.Combine(OriginalsDir, Simplified.Language.LanguageFiles.FolderName);

    /// <summary>
    /// Where the language library remembers where in each build of the game's
    /// exe the archive it wants is — the part worth not working out twice, since
    /// working it out means reading 408 MB.
    ///
    /// Under the app folder, because these apps write nothing outside it. It is
    /// derived and can be deleted at any time.
    /// </summary>
    public static string LanguageCacheDir => Path.Combine(Root, "runtime", "language");
}
