namespace SimplifiedLanguageExtractor.Core;

/// <summary>
/// Where the app keeps what belongs to the app.
///
/// It is portable — the folder is the install — so settings live beside the exe
/// rather than in %APPDATA%, and copying the folder carries them with it.
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
    /// libraries and the icon.
    /// </summary>
    public static string RuntimeDir => Path.Combine(Root, "runtime");

    /// <summary>The window icon, as a file.</summary>
    public static string IconFile => Path.Combine(RuntimeDir, "icon.ico");

    /// <summary>
    /// Where the language library keeps what it has worked out — the offset of
    /// the archive in each build of the exe it has seen.
    ///
    /// Under the app folder, because these apps write nothing outside it.
    /// </summary>
    public static string LanguageCacheDir => Path.Combine(RuntimeDir, "language");
}
