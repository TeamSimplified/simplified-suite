using System.Text.Json;

namespace SimplifiedTeamEditor.Core.Config;

/// <summary>
/// User-level config persisted beside the exe, as <c>config.json</c>.
///
/// The app is portable — the folder is the install — so this travels with it
/// rather than living in %APPDATA%. Each Simplified app keeps its own.
/// </summary>
public sealed class UserConfig
{
    /// <summary>
    /// The F1 25 install. Needed even though the base files are bundled: the
    /// UI shader packages a logo edit merges into are read from the game.
    /// </summary>
    /// <summary>
    /// Which palette to wear. Dark by default: the rest of the suite is dark,
    /// and an app that opens light next to Studio looks like a different app.
    /// </summary>
    public string? Theme { get; set; } = "Dark";

    public string? GameDir { get; set; }

    /// <summary>Pre-fills the Author field when packaging.</summary>
    public string? DefaultAuthor { get; set; }

    /// <summary>Where the last package was written, to open the dialog there.</summary>
    public string? LastPackageFolder { get; set; }

    /// <summary>Where the last preset was saved or loaded from.</summary>
    public string? LastPresetFolder { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string ConfigPath => AppPaths.ConfigFile;

    public static UserConfig Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path)) return new UserConfig();
        try
        {
            return JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(path)) ?? new UserConfig();
        }
        catch
        {
            return new UserConfig();
        }
    }

    /// <summary>
    /// Write the settings out. Throws if the app folder is read-only — running
    /// from a locked-down location or straight out of a zip — which the caller
    /// reports rather than swallowing, since silently forgetting a setting the
    /// user just chose is worse than saying so.
    /// </summary>
    public void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Json));
}
