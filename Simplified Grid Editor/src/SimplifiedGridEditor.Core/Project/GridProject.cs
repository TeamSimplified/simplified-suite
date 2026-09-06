namespace SimplifiedGridEditor.Core.Project;

/// <summary>
/// An open grid project. While open its contents live in <see cref="TempDir"/>;
/// the <c>.simplified</c> zip on disk is only touched on Save (rezipped from
/// the temp dir) and Open (extracted to it).
///
/// The layout is Studio's with two folders added, and no template behind it —
/// a grid project is created from the bundled base files rather than from a
/// <c>.serps</c>:
/// <code>
/// info.json
/// Database/
///     gamedatabases.2025.bin       teams, entrant lists, drivers
/// Language/
///     language_eng.lng  …          the eleven the game ships
///     2026_team_keys.lng           keys the 2026 grid adds
///     2026_team_names.json
/// ERPs/
///     ui_texarrays_team_branding.erp
/// </code>
///
/// One database serves all three tabs: team colours, the grid's seats and the
/// driver table are all records in the same file, which is the reason this app
/// exists rather than two that each rewrite it.
/// </summary>
public sealed class GridProject
{
    /// <summary>Path to the <c>.simplified</c> file on disk. Null until first Save As.</summary>
    public string? ZipPath { get; set; }

    /// <summary>Working temp directory mirroring the project's tree.</summary>
    public string TempDir { get; set; } = "";

    public ProjectInfo Info { get; set; } = new();

    public string DatabaseFolder => Path.Combine(TempDir, ProjectStore.DatabaseFolderName);
    public string LanguageFolder => Path.Combine(TempDir, ProjectStore.LanguageFolderName);
    public string ErpsFolder     => Path.Combine(TempDir, ProjectStore.ErpsFolderName);

    /// <summary>
    /// The project's database. Named after the game's own file, since that is
    /// where a package has to put it back.
    /// </summary>
    public string DatabasePath => Path.Combine(DatabaseFolder, ProjectStore.DatabaseFileName);

    /// <summary>UI-friendly name. "Untitled project" until first Save As.</summary>
    public string DisplayName => ZipPath is null
        ? "Untitled project"
        : Path.GetFileNameWithoutExtension(ZipPath);

    /// <summary>
    /// The team-branding texarray, which logo edits are imported into and which
    /// a package merges over the game's UI shader packages.
    /// </summary>
    public string BrandingErpPath => Path.Combine(ErpsFolder, "ui_texarrays_team_branding.erp");

    /// <summary>The eleven language files, in whatever order the folder gives them.</summary>
    public IEnumerable<string> LanguageFiles =>
        Directory.Exists(LanguageFolder)
            ? Directory.EnumerateFiles(LanguageFolder, "language_*.lng")
            : [];
}
