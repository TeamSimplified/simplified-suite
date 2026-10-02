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
/// Logos/
///     badges-6.png                 the pictures chosen for the game's logo slots
/// </code>
///
/// The database and the eleven language files are not in there. They are taken
/// fresh from the installed game every time the project is opened and the
/// project's changes applied on top, into <see cref="WorkDir"/> — which is why
/// a project started a month ago still packages against today's game instead of
/// putting back the game it was started on. See <see cref="ProjectEdits"/>.
///
/// The logos follow the same rule. A project carries the pictures that were
/// chosen, not the archive they were put into; that archive is built fresh at
/// packaging time. See <see cref="ProjectLogos"/>.
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

    /// <summary>The pictures chosen for the game's logo slots.</summary>
    public string LogosFolder => Path.Combine(TempDir, ProjectStore.LogosFolderName);

    /// <summary>
    /// Where an older project kept the game's UI archives, with its logos baked
    /// into them. Read once when such a project is opened, to get the pictures
    /// back out, and then dropped.
    /// </summary>
    public string ErpsFolder => Path.Combine(TempDir, ProjectStore.ErpsFolderName);

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
    /// The team-branding texarray an older project carried, with its logos
    /// already in it. The game's own copy lives in the originals folder; see
    /// <see cref="ProjectStore.PristineBrandingPath"/>.
    /// </summary>
    public string BrandingErpPath => Path.Combine(ErpsFolder, ProjectStore.BrandingFileName);

    /// <summary>The eleven language files, in whatever order the folder gives them.</summary>
    public IEnumerable<string> LanguageFiles =>
        Directory.Exists(LanguageFolder)
            ? Directory.EnumerateFiles(LanguageFolder, "language_*.lng")
            : [];
}
