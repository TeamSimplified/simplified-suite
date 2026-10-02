using EgoEngineLibrary.Data.GameDatabases;
using SimplifiedGridEditor.Core.Packaging;
using SimplifiedTeamEditor.Core.Lng;
using SimplifiedTeamEditor.Core.Teams;

namespace SimplifiedGridEditor.Core.Project;

/// <summary>What a file somebody hands us would change, and where each change belongs.</summary>
/// <param name="Changes">
/// Everything it changes from the game's own, as the project stores changes.
/// Filtering this by part is what importing a selection means.
/// </param>
/// <param name="Counts">How many changes each part holds, for the chooser.</param>
public sealed record ImportScan(
    ProjectEdits Changes,
    IReadOnlyDictionary<PackagePart, int> Counts,
    PackageParts Parts)
{
    public bool Any => Changes.Any;

    /// <summary>The changes whose part was ticked, and nothing else.</summary>
    public ProjectEdits Wanted(IReadOnlySet<PackagePart> parts) => new()
    {
        Database = Changes.Database
            .Where(e => ulong.TryParse(e.Key, out var key) && parts.Contains(Parts.PartOf(key)))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),

        Text = Changes.Text
            .Where(e => parts.Contains(PackageParts.PartOfText(e.Key, Keys2026)))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
    };

    /// <summary>
    /// The 2026 name keys, carried so filtering sorts text the same way the
    /// scan counted it. Reading them off disk twice could give two answers.
    /// </summary>
    internal IReadOnlySet<string> Keys2026 { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Reading somebody else's game files for the work in them.
///
/// A mod is distributed as the game's files with changes in them, which is the
/// same shape a project reads its own changes out of: the difference between a
/// file and the game's own copy of it. So a database or a language file from
/// anywhere can be read the way a project reads itself, classified by the same
/// map that decides what a package carries, and brought in a part at a time.
///
/// Against the game's own copy rather than against the project, because the
/// question being asked is what this file changes about the game — not what it
/// has that the project happens to be missing. A record the project had already
/// changed is simply overwritten, which is what importing it means.
/// </summary>
public static class ProjectImport
{
    /// <summary>Whether a path looks like something this can read.</summary>
    public static bool IsDatabase(string path) =>
        Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase);

    public static bool IsLanguage(string path) =>
        Path.GetExtension(path).Equals(".lng", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a database would change.
    /// </summary>
    /// <param name="theirs">The database to read.</param>
    /// <param name="pristinePath">The game's own copy, to compare against.</param>
    /// <param name="languageFolder">
    /// The project's language folder, which carries the 2026 key list. Needed
    /// because a team's name field points at a key, and which keys are the 2026
    /// ones decides whether a name change is 2025's or 2026's.
    /// </param>
    public static ImportScan ScanDatabase(string theirs, string pristinePath, string languageFolder)
    {
        var pristine = GameDatabase.Load(pristinePath);

        var changes = new ProjectEdits
        {
            Database = ProjectEdits.DatabaseDifference(pristine, GameDatabase.Load(theirs)),
        };

        return Classify(pristine, changes, languageFolder);
    }

    /// <summary>
    /// What a language file would change.
    ///
    /// Compared with the game's copy of the same language, so a German file is
    /// read against the German one. What comes out is applied to every language
    /// the project holds, which is how this editor stores a name: one value for
    /// all eleven. It is also the only thing that survives a save, since a
    /// project reads its text changes out of the English file alone.
    /// </summary>
    public static ImportScan ScanLanguage(string theirs, string pristinePath, string languageFolder)
    {
        var changes = new ProjectEdits
        {
            Text = ProjectEdits.TextDifference(LngFile.Load(pristinePath), LngFile.Load(theirs)),
        };

        return Classify(null, changes, languageFolder);
    }

    /// <summary>
    /// Put the ticked changes into the project's working files, which is where
    /// the tabs read from and where the next save reads the project's own
    /// changes out of.
    /// </summary>
    /// <returns>How many changes landed.</returns>
    public static int Apply(GridProject project, ProjectEdits wanted)
    {
        var landed = 0;

        if (wanted.Database.Count > 0 && File.Exists(project.DatabasePath))
        {
            var database = GameDatabase.Load(project.DatabasePath);
            landed += wanted.ApplyTo(database);
            database.SaveAtomic(project.DatabasePath);
        }

        if (wanted.Text.Count > 0)
        {
            foreach (var path in project.LanguageFiles.ToList())
                File.WriteAllBytes(path, wanted.ApplyTo(LngFile.Load(path)));

            landed += wanted.Text.Count;
        }

        return landed;
    }

    private static ImportScan Classify(GameDatabase? pristine, ProjectEdits changes, string languageFolder)
    {
        var keys2026 = Season2026.LoadDefaults(languageFolder).Keys.ToHashSet(StringComparer.Ordinal);

        // A text-only scan has no database to build the map from, and needs
        // none: a language key is classified by the key itself.
        var parts = pristine is null
            ? PackageParts.Read(changes, keys2026)
            : PackageParts.Read(pristine, changes, keys2026);

        return new ImportScan(changes, parts.Counts, parts) { Keys2026 = keys2026 };
    }
}
