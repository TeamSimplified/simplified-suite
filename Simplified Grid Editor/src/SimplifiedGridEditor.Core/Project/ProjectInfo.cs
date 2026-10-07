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
    /// Which app wrote this file and which build of it, as
    /// <c>["grid_editor", "v1.4"]</c>. Written on every save, so a project that
    /// turns up with an unexpected layout can be traced to the build that made
    /// it.
    ///
    /// Two values under one key rather than a key per app, and the same key in
    /// every app that writes an info.json. A reader asking what made a project
    /// should not have to know every app's name to ask — with
    /// <c>grid_editor_version</c>, finding out meant trying each in turn and
    /// concluding from silence.
    /// </summary>
    [JsonPropertyName("simplified_app")] public string[] App { get; set; } = [];

    /// <summary>
    /// What earlier builds wrote instead. Read so those projects still report
    /// theirs; cleared on save, so one opened and saved here comes out with the
    /// new key alone rather than both.
    /// </summary>
    [JsonPropertyName("grid_editor_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyEditorVersion { get; set; }

    /// <summary>
    /// The build that wrote this, whichever key it came from. Not serialised: it
    /// is a reading of the two above, and a third copy of the same fact could
    /// disagree with them.
    /// </summary>
    [JsonIgnore]
    public string EditorVersion =>
        App.Length > 1 && !string.IsNullOrWhiteSpace(App[1]) ? App[1] : LegacyEditorVersion ?? "";

    /// <summary>Which app, when a project says so. Empty for anything older.</summary>
    [JsonIgnore]
    public string AppId => App.Length > 0 ? App[0] : "";
}
