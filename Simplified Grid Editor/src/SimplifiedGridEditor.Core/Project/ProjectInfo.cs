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
}
