using System;
using System.IO;

namespace SimplifiedUniversalExporter.Core;

/// <summary>
/// Where the app keeps what belongs to the app.
///
/// It is portable — the folder is the install — so settings live beside the exe
/// rather than in %APPDATA%, and copying the folder carries them with it. Scratch
/// work (the extracted ERPs, logs) goes under %TEMP%, never beside the files
/// being read: an ERP opened from the game folder used to get a load log written
/// next to it, inside the install.
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

    /// <summary>The 2.x settings file, read once to carry its game folder over.</summary>
    public static string LegacyConfigFile => Path.Combine(Root, "helmet_exporter_settings.txt");

    /// <summary>
    /// What the app needs to run and nobody should be poking at — the native
    /// libraries and the icon.
    /// </summary>
    public static string RuntimeDir => Path.Combine(Root, "runtime");

    /// <summary>The window icon, as a file.</summary>
    public static string IconFile => Path.Combine(RuntimeDir, "icon.ico");

    /// <summary>
    /// Everything the app writes while it works: the ERP extraction cache, the
    /// readers' logs and the command line's log.
    /// </summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "simplified-universal-exporter");
}
