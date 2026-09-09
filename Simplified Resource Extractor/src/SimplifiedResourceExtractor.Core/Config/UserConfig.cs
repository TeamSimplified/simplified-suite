using System.Text.Json;

namespace SimplifiedResourceExtractor.Core.Config;

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

    /// <summary>The folder to search. Usually the game install, but any will do.</summary>
    public string? SearchDir { get; set; }

    /// <summary>
    /// The F1 25 install, for the <c>.mipmaps</c> sidecars a texture's full
    /// resolution lives in. Only stripping needs it, and it is usually the same
    /// folder as the search — kept apart because searching a folder of archives
    /// somebody sent you still wants the game's mip files.
    /// </summary>
    public string? GameDir { get; set; }

    /// <summary>Where results are written.</summary>
    public string? OutputDir { get; set; }

    /// <summary>
    /// Fold each texture's external mip chain into the output, so it carries
    /// its own full-resolution images rather than pointing at the install.
    /// </summary>
    public bool StripMipmaps { get; set; }

    /// <summary>
    /// One output per source archive, rather than everything merged into one.
    /// </summary>
    public bool ExtractIndividually { get; set; }

    /// <summary>
    /// Name outputs after the output folder as well as themselves, for keeping
    /// several runs' results apart once they have been moved somewhere together.
    /// </summary>
    public bool AddFolderPrefix { get; set; }

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
            // someone is mid-job.
        }
    }
}
