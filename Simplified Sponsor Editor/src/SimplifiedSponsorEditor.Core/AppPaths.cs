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
}
