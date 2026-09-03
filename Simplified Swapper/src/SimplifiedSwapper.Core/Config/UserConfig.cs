using System.Text.Json;

namespace SimplifiedSwapper.Core.Config;

/// <summary>
/// User-level config persisted at <c>%APPDATA%/simplified-chassis-swapper/config.json</c>.
/// Only remembers what would be tedious to re-enter every launch — the game
/// install and the folders last browsed.
/// </summary>
public sealed class UserConfig
{
    /// <summary>
    /// Game install root. Used to resolve external <c>.mipmaps</c> files and to
    /// auto-detect <c>incardriver_f1.erp</c>, so both texture quality and driver
    /// animations depend on it.
    /// </summary>
    /// <summary>
    /// Which palette to wear. Dark by default: the rest of the suite is dark,
    /// and an app that opens light next to Studio looks like a different app.
    /// </summary>
    public string? Theme { get; set; } = "Dark";

    public string? GameDir { get; set; }

    /// <summary>Folder the ERP pickers open in — normally somewhere inside the game install.</summary>
    public string? LastErpFolder { get; set; }

    /// <summary>Folder the last swap was written to.</summary>
    public string? LastOutputFolder { get; set; }

    /// <summary>Folder the UV maps were last written to.</summary>
    public string? LastUvFolder { get; set; }

    /// <summary>Folder the .serps picker opens in — normally the template pack.</summary>
    public string? LastTemplateFolder { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string ConfigPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "simplified-chassis-swapper", "config.json");
        }
    }

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
            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // Config is a convenience; a failed write must not interrupt a swap.
        }
    }
}
