namespace SimplifiedUpdater.Core;

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
    /// The game's files as they were when the mod was made.
    ///
    /// Beside the exe rather than in runtime\, because these are the user's to
    /// keep current: each time the game updates, what was in <see cref="NewDir"/>
    /// becomes what belongs here. They are the whole basis of the merge — what
    /// a mod changed is what differs from these.
    /// </summary>
    public static string OldDir => Path.Combine(Root, "old");

    /// <summary>The same files from the version being updated to.</summary>
    public static string NewDir => Path.Combine(Root, "new");
}
