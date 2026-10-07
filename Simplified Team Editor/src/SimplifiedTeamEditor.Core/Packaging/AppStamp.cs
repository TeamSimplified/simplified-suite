using System.Reflection;

namespace SimplifiedTeamEditor.Core.Packaging;

/// <summary>
/// What this app calls itself in a <c>simplified_app</c>, and which build it is.
///
/// The other apps in the suite keep this beside their project store, because
/// they stamp it into a project as well as into a package. This one has no
/// project — the Team Editor edits the game's own files and packages the
/// result — so a mod is the only thing it ever signs, and this lives next to the
/// packager that signs it.
///
/// Read from the assembly rather than a literal: Directory.Build.props sets the
/// version for the app and this library alike, so there is one place to bump and
/// no way for a stamp to claim a build that did not write it.
/// </summary>
internal static class AppStamp
{
    /// <summary>
    /// Fixed rather than derived from the assembly name, which is "Simplified
    /// Team Editor" with spaces and capitals. A key other tools match on should
    /// not change because a product name did.
    /// </summary>
    public const string AppId = "team_editor";

    /// <summary>How the version is written down, e.g. <c>v1.0</c>.</summary>
    public static string Stamp { get; } = "v" + Read();

    /// <summary>The pair as a manifest carries it.</summary>
    public static string[] Pair => [AppId, Stamp];

    private static string Read()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Build metadata after a + is the commit, which is for a log rather
            // than for a manifest somebody reads.
            var plus = informational.IndexOf('+');
            return (plus >= 0 ? informational[..plus] : informational).Trim();
        }

        return assembly.GetName().Version?.ToString() ?? "";
    }
}
