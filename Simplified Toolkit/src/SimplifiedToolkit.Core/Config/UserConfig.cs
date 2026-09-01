using Newtonsoft.Json;

namespace SimplifiedToolkit.Core.Config;

/// <summary>
/// User-level configuration stored at <c>%APPDATA%/erp-handler/config.json</c>.
/// </summary>
public sealed class UserConfig
{
    [JsonProperty("gameDir")]
    public string? GameDir { get; set; }

    [JsonProperty("variables")]
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.Ordinal);

    public static string ConfigPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "erp-handler", "config.json");
        }
    }

    public static UserConfig Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path)) return new UserConfig();
        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonConvert.DeserializeObject<UserConfig>(json) ?? new UserConfig();
            cfg.Variables ??= new Dictionary<string, string>(StringComparer.Ordinal);
            return cfg;
        }
        catch
        {
            return new UserConfig();
        }
    }

    public void Save()
    {
        var path = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Resolve the game directory using the standard precedence:
    /// explicit override &gt; ERP_GAME_DIR env var &gt; user config &gt; null.
    /// </summary>
    public static string? ResolveGameDir(string? cliOverride)
    {
        if (!string.IsNullOrWhiteSpace(cliOverride)) return cliOverride;
        var env = Environment.GetEnvironmentVariable("ERP_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Load().GameDir;
    }
}
