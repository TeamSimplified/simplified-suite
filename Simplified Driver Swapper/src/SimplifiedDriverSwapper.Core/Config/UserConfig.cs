using System.Text.Json;

namespace SimplifiedDriverSwapper.Core.Config;

/// <summary>
/// User-level config persisted beside the exe, as <c>config.json</c>.
///
/// The app is portable — the folder is the install — so this travels with it
/// rather than living in %APPDATA%. Each Simplified app keeps its own.
///
/// Only remembers what would be tedious to re-enter every launch: the game
/// install, the folders last browsed, and how the app should look.
/// </summary>
public sealed class UserConfig
{
    /// <summary>
    /// Which palette to wear. Dark by default: the rest of the suite is dark,
    /// and an app that opens light next to Studio looks like a different app.
    /// </summary>
    public string? Theme { get; set; } = "Dark";

    /// <summary>Game install root — where the app looks for the game's own files.</summary>
    public string? GameDir { get; set; }

    /// <summary>Folder the file picker opens in — normally somewhere inside the game install.</summary>
    public string? LastFileFolder { get; set; }

    /// <summary>
    /// Whether the per-seat helmet, suit and glove controls are shown.
    ///
    /// Off by default: the kit rules already do the right thing on their own, so
    /// those controls are only for a seat that wants something specific. The
    /// rules stay on either way — this hides the choice, not the behaviour.
    /// </summary>
    public bool EnableAdvancedSwapping { get; set; }

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
            // Config is a convenience; a failed write must not interrupt a swap.
            // The app folder can be read-only - run from a locked-down location,
            // or straight out of the zip - and that is not worth a dialog while
            // someone is mid-edit.
        }
    }
}
