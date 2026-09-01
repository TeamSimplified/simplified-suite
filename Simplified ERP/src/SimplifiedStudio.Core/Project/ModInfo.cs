using System.Text.Json.Serialization;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// Contents of <c>mod.json</c> at the root of a .simplified project. Property
/// names below pin the snake_case JSON keys the format expects.
/// </summary>
public sealed class ModInfo
{
    [JsonPropertyName("mod_name")]       public string ModName       { get; set; } = "";
    [JsonPropertyName("mod_author")]     public string Author        { get; set; } = "";
    /// <summary>Engine shader-package identifier (e.g. <c>object_my_team.nefx2.sm51</c>).
    /// Sourced from the .serps template's ZIP comment at project creation time, or
    /// typed by the user when the project was created from a custom template.</summary>
    [JsonPropertyName("shader_package")] public string ShaderPackage { get; set; } = "";
    /// <summary>File name (not path) of the .serps template the project was created from.
    /// Empty when the project was created with a custom template instead of a .serps file.</summary>
    [JsonPropertyName("serps_template")] public string SerpsTemplate { get; set; } = "";

    /// <summary>True when the project was created from a manually-entered shader package
    /// rather than a .serps template. The Studio shows "Choose ERPs" (add/remove arbitrary
    /// .erp files) instead of "Choose parts" (toggle from a template-defined pool) in this mode.</summary>
    [JsonPropertyName("custom_template")] public bool IsCustomTemplate { get; set; }
}
