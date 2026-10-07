using System.Text.Json.Serialization;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// <c>info.json</c> at the root of a .simplified project — everything about
/// the project that isn't an ERP.
///
/// The two export files are projections of this: <c>template.serps</c> and
/// <c>mod.serps</c> carry the same fields minus the project-only ones
/// (<see cref="Enabled"/>, <see cref="SerpsTemplate"/>,
/// <see cref="IsCustomTemplate"/>), plus <c>game</c> / <c>game_version</c>,
/// which are read off the install at package time rather than stored here —
/// a project packaged months after it was made must not ship a stale game
/// version.
///
/// Categories aren't here either: they're the folder structure under
/// <c>ERPs/</c>.
/// </summary>
public sealed class ProjectInfo
{
    [JsonPropertyName("name")]     public string Name    { get; set; } = "";
    [JsonPropertyName("author")]   public string Author  { get; set; } = "";

    /// <summary>The mod's own version. Persisted, so the package dialog can prefill it.</summary>
    [JsonPropertyName("version")]  public string Version { get; set; } = "";

    /// <summary>
    /// Which Simplified Studio wrote this file, e.g. <c>1.3</c>. Its absence is
    /// the marker for a pre-1.3 project: the flat default/enabled/disabled
    /// layout, and a <c>NNN--</c> prefix whose <em>first</em> digit was the
    /// conflict group.
    /// </summary>
    /// <summary>
    /// What 1.3 through 2.0 wrote instead. Read so those projects still report
    /// their build; cleared on save, so one opened and saved here comes out with
    /// the new key alone rather than both.
    ///
    /// It was kept at first on the understanding that its absence marked a
    /// pre-1.3 project. It does not, and the comment that said so was stale:
    /// <see cref="ProjectStore.Inspect"/> calls a project legacy when it has no
    /// <c>info.json</c> at all, because before 1.3 the file was called
    /// <c>mod.json</c>. A 1.3+ project missing only this key reads as current,
    /// which is what it is.
    /// </summary>
    [JsonPropertyName("studio_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyStudioVersion { get; set; }

    /// <summary>
    /// The build that wrote this, whichever key it came from. Not serialised: it
    /// is a reading of the two above, and a third copy of the same fact could
    /// disagree with them.
    /// </summary>
    [JsonIgnore]
    public string StudioVersion =>
        App.Length > 1 && !string.IsNullOrWhiteSpace(App[1]) ? App[1] : LegacyStudioVersion ?? "";

    /// <summary>
    /// Which app wrote this file and which build of it, as
    /// <c>["studio", "v2.0"]</c>. The same key in every app that writes an
    /// info.json: a reader asking what made a project should not have to know
    /// every app's name to ask, which with a key per app meant trying each in
    /// turn and concluding from silence.
    /// </summary>
    [JsonPropertyName("simplified_app")] public string[] App { get; set; } = [];

    /// <summary>Which app, when a project says so. Empty for anything older.</summary>
    [JsonIgnore]
    public string AppId => App.Length > 0 ? App[0] : "";

    /// <summary>Engine shader-package identifier, e.g. <c>object_my_team.nefx2.sm51</c>.
    /// Taken from a .serps template's ZIP comment on creation, or typed by the user.</summary>
    [JsonPropertyName("shader_package")] public string ShaderPackage { get; set; } = "";

    /// <summary>File name of the .serps this project was created from. Empty for custom projects.</summary>
    [JsonPropertyName("serps_template")] public string SerpsTemplate { get; set; } = "";

    /// <summary>True when the project was created from a typed shader package rather than a template.</summary>
    [JsonPropertyName("custom_template")] public bool IsCustomTemplate { get; set; }

    /// <summary>
    /// The parts currently switched on, as paths relative to <c>ERPs/</c> —
    /// e.g. <c>011--Wings/001--front_wing.erp</c>, or a bare file name for an
    /// uncategorised part. Replaces the old enabled/ vs disabled/ folder split,
    /// so toggling a part is a write here rather than moving a file.
    ///
    /// The folder is the authority on what exists and this list on what's on:
    /// a name here with no file is ignored, and a file not named here is off.
    /// </summary>
    [JsonPropertyName("enabled")] public List<string> Enabled { get; set; } = new();
}
