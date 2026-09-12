using System.Text.Json.Serialization;

namespace SimplifiedSponsorEditor.Core.Project;

/// <summary>
/// <c>info.json</c> at the root of a <c>.simplified</c> project — everything
/// about the project that isn't a file.
///
/// What it does not hold is a list of which decals were replaced. That is read
/// from the folder: a project's edits <em>are</em> the pictures in
/// <c>Sponsors/</c>, so a list beside them would be a second copy of the same
/// fact and the two could disagree. The Grid Editor keeps such a list because
/// its edits are imported into an archive on save and the pictures are not
/// kept; here they are the whole point.
/// </summary>
public sealed class ProjectInfo
{
    [JsonPropertyName("name")]   public string Name   { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";

    /// <summary>The mod's own version. Persisted, so the package dialog can prefill it.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>
    /// Which Simplified Sponsor Editor wrote this file, e.g. <c>1.0</c>. Written
    /// on every save, so a project that turns up with an unexpected layout can
    /// be traced to the build that made it.
    /// </summary>
    [JsonPropertyName("sponsor_editor_version")] public string EditorVersion { get; set; } = "";
}
