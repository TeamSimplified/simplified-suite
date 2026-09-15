using System.Reflection;

namespace SimplifiedUpdater.Util;

/// <summary>
/// The app's own version, taken from the assembly rather than a literal, so it
/// only ever needs setting in one place — <c>&lt;Version&gt;</c> in
/// Directory.Build.props.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// e.g. <c>1.0</c>. The informational version carries the build's commit as
    /// <c>1.0+&lt;sha&gt;</c>, so everything from the <c>+</c> onward is dropped —
    /// that's build provenance, not something to show in Settings.
    /// </summary>
    public static string Number
    {
        get
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

    /// <summary>Ready to display, e.g. <c>v1.0</c>. Empty when no version is set.</summary>
    public static string Display => Number.Length == 0 ? "" : "v" + Number;

    /// <summary>
    /// The app's name with its version, e.g. <c>Simplified Updater - v1.0</c>.
    /// The separator lives here so an unset version can't leave a trailing dash.
    /// </summary>
    public static string TitleFor(string appName)
        => Display.Length == 0 ? appName : $"{appName} - {Display}";
}
