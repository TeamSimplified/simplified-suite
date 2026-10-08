namespace SimplifiedSponsorEditor.Core;

/// <summary>
/// Where the app keeps what belongs to the app.
///
/// It is portable — the folder is the install — so settings live beside the exe
/// rather than in %APPDATA%, and copying the folder carries them with it.
///
/// A project's working copy is deliberately not here. It goes to the system
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
    /// What the app needs to run and nobody should be poking at — the native
    /// libraries, texconv, the icon, the sponsor archives. One folder for all of
    /// it, so the app folder itself is the exe and the user's settings.
    /// </summary>
    public static string RuntimeDir => Path.Combine(Root, "runtime");

    /// <summary>The window icon, as a file.</summary>
    public static string IconFile => Path.Combine(RuntimeDir, "icon.ico");

    /// <summary>
    /// The pristine sponsor archives every project is read against.
    ///
    /// Bundled rather than taken from the install, and shared by every project
    /// rather than copied into each: they are read-only reference here. A
    /// project holds the pictures somebody chose, and these say what those
    /// pictures are replacing.
    /// </summary>
    public static string OriginalsDir => Path.Combine(RuntimeDir, "original");

    /// <summary>
    /// The game's language files, taken out of the install rather than bundled.
    ///
    /// Not here when the app has never packaged anything: they are eleven files
    /// and sixty-six megabytes, they belong to whichever build of the game is
    /// installed, and nothing but packaging needs them. Packaging takes them
    /// the first time it runs and again whenever the game has moved on.
    ///
    /// Under original\ with the archives, which is where every app in the suite
    /// keeps them, so the folder means the same thing in each.
    /// </summary>
    public static string LanguageDir =>
        Path.Combine(OriginalsDir, Simplified.Language.LanguageFiles.FolderName);

    /// <summary>
    /// Where the search through the exe for the language archive is remembered.
    /// Finding it means reading a 408 MB file, and that is the part worth not
    /// repeating on every package.
    /// </summary>
    public static string CacheDir => Path.Combine(RuntimeDir, "cache");
}
