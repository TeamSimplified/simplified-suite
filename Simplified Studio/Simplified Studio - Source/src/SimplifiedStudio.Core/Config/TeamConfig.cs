using System.IO;
using System.Text.Json;

namespace SimplifiedStudio.Core.Config;

/// <summary>
/// Team-only settings, stored in a <c>team.simplified</c> file next to the
/// executable. The file's presence (with <c>"advanced": true</c>) unlocks the
/// Advanced menu, and the same file holds the Advanced feature flags.
///
/// Keeping these flags out of the per-user <see cref="UserConfig"/> is
/// deliberate: every user has a config.json they could hand-edit, but only
/// team members have a team.simplified. A normal shipped build has no marker,
/// so there is no file to edit to turn the flags on.
///
/// JSON shape (camelCase keys):
/// <code>
/// {
///   "advanced": true,
///   "enableCustomTemplates": false,
///   "enableMoreResourceTypes": false,
///   "packageFormat": "zip"
/// }
/// </code>
/// </summary>
public sealed class TeamConfig
{
    /// <summary>Master unlock — the Advanced menu (Ctrl+T) only opens when this is true.</summary>
    public bool Advanced { get; set; }

    public bool EnableCustomTemplates { get; set; }
    public bool EnableMoreResourceTypes { get; set; }

    /// <summary>Package output format: "zip" or "serps". Defaults to "zip".</summary>
    public string PackageFormat { get; set; } = "zip";

    /// <summary>Texture import/export format: "DDS" or "PNG". Defaults to "DDS".
    /// The selection UI lives in the Advanced menu for now, so non-team builds
    /// always get the default.</summary>
    public string FileFormat { get; set; } = "DDS";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "team.simplified");

    /// <summary>
    /// Load the marker next to the exe. Returns an all-default (locked)
    /// instance when the file is missing or unreadable — so a normal build,
    /// or a corrupt marker, stays locked with every flag off.
    /// </summary>
    public static TeamConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new TeamConfig();
            return JsonSerializer.Deserialize<TeamConfig>(File.ReadAllText(FilePath), Json) ?? new TeamConfig();
        }
        catch
        {
            return new TeamConfig();
        }
    }

    /// <summary>Write the marker back. Callers persisting feature-flag changes
    /// must keep <see cref="Advanced"/> true or they'd lock themselves out.</summary>
    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
}
