using System.Text.Json;

namespace SimplifiedTrackInstaller.Core.Config;

/// <summary>
/// User-level config persisted beside the exe, as <c>config.json</c>.
///
/// The app is portable — the folder is the install — so this travels with it
/// rather than living in %APPDATA%. Each Simplified app keeps its own.
/// </summary>
public sealed class UserConfig
{
    /// <summary>
    /// Which palette to wear. Dark by default: the rest of the suite is dark,
    /// and an app that opens light next to Studio looks like a different app.
    /// </summary>
    public string? Theme { get; set; } = "Dark";

    /// <summary>
    /// The F1 25 install. The shader packages a track is merged into, and the
    /// menu and handling files it is added to, are all read from here.
    /// </summary>
    public string? GameDir { get; set; }

    /// <summary>Where the last track zip was picked from.</summary>
    public string? LastTrackFolder { get; set; }

    /// <summary>Where the last game database was picked from.</summary>
    public string? LastDatabaseFolder { get; set; }

    /// <summary>Where the last mod was packaged to.</summary>
    public string? LastPackageFolder { get; set; }

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

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // The app folder can be read-only — run from a locked-down location,
            // or straight out of the zip — and that is not worth a dialog.
        }
    }
}
