using System.Reflection;

namespace SimplifiedGridEditor.Core.Project;

/// <summary>
/// The app's own version, stamped into every project it writes so a later build
/// can tell what rules a file was written under. Read from the assembly rather
/// than a literal — Directory.Build.props sets it for the app and this library
/// alike, so there is one place to bump.
/// </summary>
public static class EditorVersion
{
    /// <summary>e.g. <c>1.0</c>. Build metadata after a <c>+</c> is dropped.</summary>
    public static string Current { get; } = Read();

    /// <summary>
    /// What this app calls itself in a project's <c>simplified_app</c>. Fixed
    /// rather than derived from the assembly name, so a key other apps have to
    /// match cannot change because a product name did.
    /// </summary>
    public const string AppId = "grid_editor";

    /// <summary>
    /// How the version is written down, e.g. <c>v1.4</c>. The prefix is part of
    /// the value rather than something a reader adds, so what is in the file is
    /// what gets shown.
    /// </summary>
    public static string Stamp { get; } = "v" + Current;

    /// <summary>
    /// Parsed form, or null when absent or malformed. Parsing matters: compared
    /// as text, "1.10" would sort before "1.3".
    /// </summary>
    public static Version? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var text = version.Trim();

        // Versions are written with a v now, and older projects have one
        // without. Both have to parse, or opening an old project would read as
        // opening one from the future.
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        return Version.TryParse(text, out var parsed) ? parsed : null;
    }

    /// <summary>True when a project says it was written by a newer build than this one.</summary>
    public static bool IsNewerThanCurrent(string? version)
    {
        var theirs = Parse(version);
        var ours = Parse(Current);
        return theirs is not null && ours is not null && theirs > ours;
    }

    private static string Read()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return (plus >= 0 ? informational[..plus] : informational).Trim();
        }
        return assembly.GetName().Version?.ToString() ?? "";
    }
}
