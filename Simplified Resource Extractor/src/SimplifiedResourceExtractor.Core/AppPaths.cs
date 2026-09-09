namespace SimplifiedResourceExtractor.Core;

/// <summary>
/// Where the app keeps what belongs to the app.
///
/// It is portable — the folder is the install — so settings and the rule list
/// live beside the exe rather than in %APPDATA%, and copying the folder carries
/// them with it.
///
/// Nothing else is written here. Results go wherever the user points the output
/// at, and the scan holds its findings in memory: the tool this replaces left
/// <c>resource_list.txt</c>, <c>erp_list.txt</c> and a folder of copied archives
/// in its own directory between steps.
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
    /// libraries and the icon. One folder for all of it, so the app folder
    /// itself is the exe and what the user edits.
    /// </summary>
    public static string RuntimeDir => Path.Combine(Root, "runtime");

    /// <summary>The window icon, as a file.</summary>
    public static string IconFile => Path.Combine(RuntimeDir, "icon.ico");

    /// <summary>
    /// The rules, as plain text beside the exe. A file rather than a setting
    /// because it is written a line at a time and kept between jobs, and
    /// because it is the one thing here worth copying to somebody else.
    /// </summary>
    public static string ReferenceFile => Path.Combine(Root, "reference.txt");
}
