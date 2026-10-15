using System;
using System.IO;
using System.Text.Json;

namespace SimplifiedUniversalExporter.Core.Config;

/// <summary>
/// User-level config persisted beside the exe, as <c>config.json</c>.
///
/// The app is portable — the folder is the install — so this travels with it
/// rather than living in %APPDATA%. It keeps every setting the 2.x exporter kept,
/// so a config.json carried over from it still applies; the game folder moved
/// from <c>GameFolder</c> to the suite's <c>GameDir</c> and is read either way.
/// </summary>
public sealed class UserConfig
{
    /// <summary>
    /// Which palette to wear. Dark by default: the rest of the suite is dark,
    /// and an app that opens light next to Studio looks like a different app.
    /// </summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>
    /// The F1 25 install. Optional for exporting — an ERP carries small mips of
    /// its own — but with it the full-resolution textures in the game's external
    /// mip files are used.
    /// </summary>
    public string GameDir { get; set; }

    /// <summary>
    /// Push decal and logo meshes half a millimetre out along their normals, so
    /// they don't flicker against the body in Blender or Maya.
    /// </summary>
    public bool FixDecalZFighting { get; set; } = true;

    /// <summary>Which variant to load where an ERP carries both: Male, Female or Both.</summary>
    public string Gender { get; set; } = "Male";

    public bool SidebarVisible { get; set; } = true;
    public double SidebarWidth { get; set; } = 260;

    /// <summary>The export picker's ticks, remembered between runs.</summary>
    public bool ExportTextures { get; set; } = true;
    public bool ExportUvMaps { get; set; }
    public bool ExportFbx { get; set; } = true;

    /// <summary>Where the last ERP was opened from.</summary>
    public string LastErpFolder { get; set; }

    /// <summary>Show the model in the RM Renderer (RM Designs' web 3D renderer); off, the DirectX showroom.</summary>
    public bool RmRenderer { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string ConfigPath => AppPaths.ConfigFile;

    public static UserConfig Load()
    {
        var config = new UserConfig();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var text = File.ReadAllText(ConfigPath);
                config = JsonSerializer.Deserialize<UserConfig>(text) ?? new UserConfig();

                // 2.x called it GameFolder.
                if (string.IsNullOrWhiteSpace(config.GameDir))
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("GameFolder", out var old) && old.ValueKind == JsonValueKind.String)
                        config.GameDir = old.GetString();
                }
            }
            else if (File.Exists(AppPaths.LegacyConfigFile))
            {
                // Before 2.0: one "gameFolder=..." line.
                foreach (var line in File.ReadAllLines(AppPaths.LegacyConfigFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && string.Equals(line[..eq].Trim(), "gameFolder", StringComparison.OrdinalIgnoreCase))
                        config.GameDir = line[(eq + 1)..].Trim();
                }
            }
        }
        catch
        {
            return new UserConfig();
        }

        if (string.IsNullOrWhiteSpace(config.GameDir)) config.GameDir = null;
        return config;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Json));

            // The pre-2.0 file is superseded the first time this one is written.
            if (File.Exists(AppPaths.LegacyConfigFile)) File.Delete(AppPaths.LegacyConfigFile);
        }
        catch
        {
            // The app folder can be read-only — run from a locked-down location,
            // or straight out of the zip — and that is not worth a dialog.
        }
    }
}
