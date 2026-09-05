using System.IO;
using System.Text.Json;

namespace SimplifiedStudio.Core.Config;

/// <summary>
/// User-level config persisted at <c>%APPDATA%/simplified-studio/config.json</c>.
/// Stores only the game install directory for now (used to locate the external
/// <c>.mipmaps</c> files referenced by ERP textures).
/// </summary>
public sealed class UserConfig
{
    public string? GameDir { get; set; }

    /// <summary>Last folder the user opened/saved a .simplified project in.</summary>
    public string? LastProjectFolder { get; set; }

    /// <summary>
    /// "Light" or "Dark". A personal preference, so it belongs here rather than
    /// in the team config — two people sharing a project shouldn't share a
    /// palette.
    /// </summary>
    public string? Theme { get; set; } = "Dark";

    /// <summary>Texture import/export format: "PNG" or "DDS". Defaults to PNG.</summary>
    public string FileFormat { get; set; } = "DDS";

    /// <summary>Pre-fills the "Mod author" field in the New project wizard.</summary>
    public string? DefaultAuthor { get; set; }

    // ─── Leftovers from the project-mode build ─────────────────────────
    // Nothing in this app reads these any more. Kept so an existing
    // config.json round-trips unchanged rather than losing fields.

    /// <summary>When true, the New Project wizard shows the "Custom template" tickbox.</summary>
    public bool EnableCustomTemplates { get; set; }

    /// <summary>Package output format. "zip" produces a standard .zip; "serps" a .serps template.</summary>
    public string PackageFormat { get; set; } = "zip";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Beside the exe, because the app is portable: the folder is the install,
    /// and carrying it to another machine should carry the settings with it.
    /// </summary>
    public static string ConfigPath => AppPaths.ConfigFile;

    /// <summary>
    /// Where builds up to v2.0 kept this file. Read once, when there is no
    /// config beside the exe yet, so an existing install does not lose its
    /// settings. Never written, and never deleted — removing it would be a
    /// write outside the app folder, and it is the user's file to delete.
    /// </summary>
    private static string LegacyConfigPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "simplified-studio", "config.json");
        }
    }

    public static UserConfig Load()
    {
        return ReadFrom(ConfigPath) ?? ReadFrom(LegacyConfigPath) ?? new UserConfig();
    }

    private static UserConfig? ReadFrom(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Write the settings out. Throws if the app folder is read-only — running
    /// from a locked-down location or straight out of a zip — which the caller
    /// reports rather than swallowing, since silently forgetting a setting the
    /// user just chose is worse than saying so.
    /// </summary>
    public void Save()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Json));
    }
}
