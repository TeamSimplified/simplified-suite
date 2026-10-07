using System.Reflection;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// The Studio's own version, stamped into every file it writes so a later build
/// can tell what rules a file was written under. Read from the assembly rather
/// than a literal — Directory.Build.props sets it for the app and this library
/// alike, so there's one place to bump.
///
/// A separate schema number would be more precise, but it's another thing to
/// remember to bump; the release number is already maintained.
/// </summary>
public static class StudioVersion
{
    /// <summary>e.g. <c>1.3</c>. Build metadata after a <c>+</c> is dropped.</summary>
    public static string Current { get; } = Read();

    /// <summary>
    /// What this app calls itself in a project's <c>simplified_app</c>. Fixed
    /// rather than derived from the assembly name, so a key other apps have to
    /// match cannot change because a product name did.
    /// </summary>
    public const string AppId = "studio";

    /// <summary>
    /// How the version is written down, e.g. <c>v2.0</c>. The prefix is part of
    /// the value rather than something a reader adds.
    /// </summary>
    public static string Stamp { get; } = "v" + Current;

    /// <summary>
    /// Compare two version strings numerically. Returns &lt;0 when
    /// <paramref name="a"/> is older. Anything unparseable counts as oldest,
    /// which is what makes a missing version mean "pre-1.3".
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var va = Parse(a);
        var vb = Parse(b);
        if (va is null && vb is null) return 0;
        if (va is null) return -1;
        if (vb is null) return 1;
        return va.CompareTo(vb);
    }

    /// <summary>True when <paramref name="version"/> is newer than this build.</summary>
    public static bool IsNewerThanCurrent(string? version) => Compare(version, Current) > 0;

    /// <summary>
    /// Parsed form, or null when absent or malformed. Parsing matters: compared
    /// as text, "1.10" would sort before "1.3".
    /// </summary>
    public static Version? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var text = version.Trim();

        // simplified_app writes the version with a v and studio_version without.
        // Both have to parse, or one project would read as newer than the other
        // for saying the same thing twice.
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        return Version.TryParse(text, out var parsed) ? parsed : null;
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
