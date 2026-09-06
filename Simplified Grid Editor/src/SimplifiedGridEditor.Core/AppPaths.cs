namespace SimplifiedGridEditor.Core;

/// <summary>
/// Where the app keeps what belongs to the app.
///
/// It is portable — the folder is the install — so settings live beside the exe
/// rather than in %APPDATA%, and copying the folder carries them with it.
///
/// Scratch is deliberately not here. A project's working copy goes to the
/// system temp directory, which is writable wherever the exe happens to sit and
/// gets cleaned up by Windows on its own.
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
    /// The pristine game files a new project is built from — the database, the
    /// eleven language files, and the team-branding texarray. Bundled rather
    /// than read from the install, so a shared package carries the user's edits
    /// and nothing else their game happens to have in it.
    /// </summary>
    public static string OriginalsDir => Path.Combine(Root, "runtime", "original");
}
