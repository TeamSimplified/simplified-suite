using System.Text.Json;

namespace SimplifiedHelmetSwapper.Core.Config;

/// <summary>
/// Settings, in a <c>config.json</c> beside the exe.
///
/// Next to the app rather than under <c>%APPDATA%</c> because these are portable
/// programs: the whole folder can be moved to another machine, or deleted, and
/// nothing of the app is left anywhere else. Only what would be tedious to
/// re-enter every launch is kept — the game install and the last folder written
/// to.
/// </summary>
public sealed class UserConfig
{
    /// <summary>
    /// Which palette to wear. Dark by default: the rest of the suite is dark,
    /// and an app that opens light next to Studio looks like a different app.
    /// </summary>
    public string? Theme { get; set; } = "Dark";

    /// <summary>Game install root — where the helmets are read from.</summary>
    public string? GameDir { get; set; }

    /// <summary>Folder the last swap was written to.</summary>
    public string? LastOutputFolder { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

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
        }
    }
}
