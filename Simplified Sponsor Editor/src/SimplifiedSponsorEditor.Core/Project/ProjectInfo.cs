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
    /// Which app wrote this file and which build of it, as
    /// <c>["sponsor_editor", "v2.0"]</c>. Written on every save, so a project
    /// that turns up with an unexpected layout can be traced to the build that
    /// made it.
    ///
    /// Two values under one key rather than a key per app. A reader that wants
    /// to know what made a project should not have to know every app's name to
    /// ask — with <c>sponsor_editor_version</c>, finding out meant trying each
    /// one in turn and concluding from silence.
    /// </summary>
    [JsonPropertyName("simplified_app")] public string[] App { get; set; } = [];

    /// <summary>
    /// What 1.x wrote instead. Read so those projects still report their build;
    /// cleared on save, so a project opened and saved here comes out with the
    /// new key alone rather than both.
    /// </summary>
    [JsonPropertyName("sponsor_editor_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyEditorVersion { get; set; }

    /// <summary>
    /// The build that wrote this, whichever key it came from — <c>v2.0</c> from
    /// a 2.0 project, <c>1.1</c> from a 1.x one. Not serialised: it is a reading
    /// of the two above, and a third copy of the same fact could disagree with
    /// them.
    /// </summary>
    [JsonIgnore]
    public string EditorVersion =>
        App.Length > 1 && !string.IsNullOrWhiteSpace(App[1]) ? App[1] : LegacyEditorVersion ?? "";

    /// <summary>Which app, when a project says so. Empty for anything 1.x wrote.</summary>
    [JsonIgnore]
    public string AppId => App.Length > 0 ? App[0] : "";
}
