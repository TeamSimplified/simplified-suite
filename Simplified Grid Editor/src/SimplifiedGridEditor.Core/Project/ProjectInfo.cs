using System.Text.Json.Serialization;

namespace SimplifiedGridEditor.Core.Project;

/// <summary>
/// <c>info.json</c> at the root of a .simplified project — everything about the
/// project that isn't a file.
///
/// Studio's has more in it, because a Studio project is a set of ERP parts that
/// can be switched on and off. A grid project has no parts: it holds one
/// database, one set of language files and the UI archives, all of them always
/// in play. So what is left is the mod's own description.
/// </summary>
public sealed class ProjectInfo
{
    [JsonPropertyName("name")]   public string Name   { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";

    /// <summary>The mod's own version. Persisted, so the package dialog can prefill it.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>
    /// Which Simplified Grid Editor wrote this file, e.g. <c>1.0</c>. Written on
    /// every save, so a project that turns up with an unexpected layout can be
    /// traced to the build that made it.
    /// </summary>
    [JsonPropertyName("grid_editor_version")] public string EditorVersion { get; set; } = "";

    /// <summary>
    /// The logo kinds this project has replaced pictures in, by id.
    ///
    /// The pictures themselves are not listed, because they are not kept: a
    /// chosen PNG is imported into the project's own copy of the branding
    /// archive when the project is saved, and the file the user picked is never
    /// looked at again. What is left to remember is which resources in that
    /// archive now differ from the game's — that is what a package has to merge
    /// into the UI shader packages, and merging the ones that don't differ would
    /// overwrite the AMD variant with the other one's bytes.
    /// </summary>
    [JsonPropertyName("logo_kinds")] public List<string> LogoKinds { get; set; } = new();
}
