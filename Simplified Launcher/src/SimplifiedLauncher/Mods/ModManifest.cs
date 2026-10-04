using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplifiedLauncher.Mods;

/// <summary>
/// <c>mod.serps</c>: what a packaged mod says about itself, at the root of the
/// archive.
///
/// Two spellings of the name, because the suite has two. The Grid Editor, the
/// Team Editor and Simplified ERP all write <c>mod_name</c>, and ERP's packager
/// says why in as many words — it is what the community tooling expects.
/// Simplified Studio writes <c>name</c> instead, and mods made with it are in
/// circulation, so both are read and <c>mod_name</c> wins when a file somehow
/// carries both.
///
/// Everything is nullable and nothing is required. These files are written by
/// several tools and edited by hand, and a missing field should cost a line in
/// the list rather than an import.
/// </summary>
public sealed class ModManifest
{
    /// <summary>The file's name, at the root of a package.</summary>
    public const string FileName = "mod.serps";

    [JsonPropertyName("mod_name")]     public string? ModName     { get; set; }
    [JsonPropertyName("name")]         public string? Name        { get; set; }
    [JsonPropertyName("author")]       public string? Author      { get; set; }
    [JsonPropertyName("version")]      public string? Version     { get; set; }
    [JsonPropertyName("game")]         public string? Game        { get; set; }
    [JsonPropertyName("game_version")] public string? GameVersion { get; set; }

    /// <summary>What to call it, from whichever of the two keys is filled in.</summary>
    [JsonIgnore]
    public string? Title => Text(ModName) ?? Text(Name);

    /// <summary>
    /// Tolerant on purpose: these are hand-edited often enough that a trailing
    /// comma or a comment should not lose somebody their mod. Case-insensitive
    /// covers a file written with Pascal or camel keys by something that was not
    /// one of ours.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Parse one, or null if it cannot be read. A manifest that will not parse
    /// is treated as a manifest that is not there, which is a case the importer
    /// already handles.
    /// </summary>
    public static ModManifest? Parse(byte[] utf8)
    {
        try
        {
            return JsonSerializer.Deserialize<ModManifest>(utf8, Json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A field worth having, or null. Blank and whitespace are both nothing.</summary>
    public static string? Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
