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

    /// <summary>
    /// What to search: a folder of archives, usually the game install, or a
    /// single archive named directly.
    /// </summary>
    public string? SearchPath { get; set; }

    /// <summary>
    /// Whether a folder's subfolders are searched too. On by default: the
    /// question this tool answers is usually "where in the game is this", and
    /// the game keeps nothing at the top level.
    /// </summary>
    public bool Recursive { get; set; } = true;

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
    /// Name outputs after the folder their archive came out of, which is what
    /// tells apart results whose file names are otherwise the same.
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
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<UserConfig>(json) ?? new UserConfig();

            // The setting was called SearchDir while a folder was the only thing
            // it could be. Carried over so an existing install doesn't open with
            // the field blank after an update.
            //
            // Read from the document rather than from a property kept for the
            // purpose: a legacy name that lives on the class is one the class
            // then has to write out as null forever.
            if (config.SearchPath is null
                && JsonDocument.Parse(json).RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("SearchDir", out var legacy)
                && legacy.ValueKind == JsonValueKind.String)
            {
                config.SearchPath = legacy.GetString();
            }

            return config;
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
