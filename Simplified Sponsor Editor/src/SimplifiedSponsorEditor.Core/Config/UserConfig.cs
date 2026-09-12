using System.Text.Json;

namespace SimplifiedSponsorEditor.Core.Config;

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
    /// The F1 25 install. Needed to package: the mod is built by merging into
    /// the game's own shader archives, which are not ours to bundle.
    /// </summary>
    public string? GameDir { get; set; }

    /// <summary>Pre-fills the Author field on a new project.</summary>
    public string? DefaultAuthor { get; set; }

    /// <summary>Where the last project was saved or opened from.</summary>
    public string? LastProjectFolder { get; set; }

    /// <summary>Where the last package was written, to open the dialog there.</summary>
    public string? LastPackageFolder { get; set; }

    /// <summary>Where the last replacement picture was picked from.</summary>
    public string? LastArtworkFolder { get; set; }

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
            // The app folder can be read-only - run from a locked-down location,
            // or straight out of the zip - and that is not worth a dialog while
            // someone is mid-edit.
        }
    }
}
