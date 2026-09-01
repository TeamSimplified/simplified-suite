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
