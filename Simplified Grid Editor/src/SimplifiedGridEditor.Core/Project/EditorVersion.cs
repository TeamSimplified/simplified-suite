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
