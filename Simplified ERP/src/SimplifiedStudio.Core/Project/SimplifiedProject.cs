using System.IO;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// An open Simplified Studio project. While open, contents live in
/// <see cref="TempDir"/>. The <c>.simplified</c> zip on disk is only touched
/// on Save (rezipped from the temp dir) and Open (extracted to temp).
///
/// On-disk layout inside the .simplified zip / temp dir:
/// <code>
/// mod.json
/// default/   — pristine copies of every ERP from the source .serps template
/// enabled/   — ERPs the user currently has active (shown in texture list)
/// disabled/  — ERPs the user previously enabled then disabled (preserves edits)
/// </code>
/// </summary>
public sealed class SimplifiedProject
{
    /// <summary>Path to the <c>.simplified</c> file on disk. Null until first Save As.</summary>
    public string? ZipPath { get; set; }

    /// <summary>Working temp directory mirroring the project's tree.</summary>
    public string TempDir { get; set; } = "";

    public ModInfo ModInfo { get; set; } = new();

    public string DefaultFolder  => System.IO.Path.Combine(TempDir, "default");
    public string EnabledFolder  => System.IO.Path.Combine(TempDir, "enabled");
    public string DisabledFolder => System.IO.Path.Combine(TempDir, "disabled");

    /// <summary>UI-friendly name. "Untitled project" until first Save As.</summary>
    public string DisplayName => ZipPath is null
        ? "Untitled project"
        : System.IO.Path.GetFileNameWithoutExtension(ZipPath);

    /// <summary>ERP file names (not paths) in <see cref="DefaultFolder"/>.</summary>
    public IEnumerable<string> DefaultErpNames  => ListErpNames(DefaultFolder);
    public IEnumerable<string> EnabledErpNames  => ListErpNames(EnabledFolder);
    public IEnumerable<string> DisabledErpNames => ListErpNames(DisabledFolder);

    /// <summary>Absolute paths to every .erp currently in <see cref="EnabledFolder"/>.</summary>
    public IEnumerable<string> EnabledErpPaths
    {
        get
        {
            if (!Directory.Exists(EnabledFolder)) yield break;
            foreach (var p in Directory.EnumerateFiles(EnabledFolder, "*.erp")) yield return p;
        }
    }

    private static IEnumerable<string> ListErpNames(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var p in Directory.EnumerateFiles(dir, "*.erp"))
            yield return System.IO.Path.GetFileName(p);
    }
}
