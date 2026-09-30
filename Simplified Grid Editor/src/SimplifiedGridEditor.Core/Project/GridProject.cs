namespace SimplifiedGridEditor.Core.Project;

/// <summary>
/// An open grid project. While open its contents live in <see cref="TempDir"/>;
/// the <c>.simplified</c> zip on disk is only touched on Save (rezipped from
/// the temp dir) and Open (extracted to it).
///
/// What the zip holds is the changes, not the changed files:
/// <code>
/// info.json
/// edits.json                       the database records and text this project changes
/// ERPs/
///     ui_texarrays_team_branding.erp
/// </code>
///
/// The database and the eleven language files are not in there. They are taken
/// fresh from the installed game every time the project is opened and the
/// project's changes applied on top, into <see cref="WorkDir"/> — which is why
/// a project started a month ago still packages against today's game instead of
/// putting back the game it was started on. See <see cref="ProjectEdits"/>.
///
/// The team-branding archive is still carried whole. Its edits are pixels
/// merged into an archive rather than records with keys, and reading them back
/// out as a difference is a different job; it stays as it was for now.
///
/// One database serves all three tabs: team colours, the grid's seats and the
/// driver table are all records in the same file, which is the reason this app
/// exists rather than two that each rewrite it.
/// </summary>
public sealed class GridProject
{
    /// <summary>Path to the <c>.simplified</c> file on disk. Null until first Save As.</summary>
    public string? ZipPath { get; set; }

    /// <summary>Working temp directory holding what the zip holds.</summary>
    public string TempDir { get; set; } = "";

    /// <summary>
    /// Where the game's own files are rebuilt for this session — the database
    /// and the language files, taken from the installed game with the project's
    /// changes applied.
    ///
    /// Beside the project's temp directory rather than inside it, so that what
    /// gets zipped stays the changes alone. Nothing in here is worth keeping
    /// once the app is closed; the next open builds it again.
    /// </summary>
    public string WorkDir { get; set; } = "";

    public ProjectInfo Info { get; set; } = new();

    public string DatabaseFolder => Path.Combine(WorkDir, ProjectStore.DatabaseFolderName);
    public string LanguageFolder => Path.Combine(WorkDir, ProjectStore.LanguageFolderName);
    public string ErpsFolder     => Path.Combine(TempDir, ProjectStore.ErpsFolderName);

    /// <summary>The changes themselves, which is what the project really is.</summary>
    public string EditsPath => Path.Combine(TempDir, ProjectEdits.FileName);

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
