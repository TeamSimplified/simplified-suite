namespace SimplifiedSponsorEditor.Core.Project;

/// <summary>
/// An open sponsor project. While open its contents live in
/// <see cref="TempDir"/>; the <c>.simplified</c> zip on disk is only touched on
/// Save (rezipped from the temp dir) and Open (extracted to it).
///
/// <code>
/// info.json
/// Sources/
///     redbull_da.svg             the file somebody imported, byte for byte
///     redbull_da.edits.json      which of its parts they dropped or repainted
/// Sponsors/                      written by 1.0 only; still read, never written
///     redbull_da.png
///     menu/redbull_da.png
/// </code>
///
/// A project holds what was imported, not what will be shipped. Rendering,
/// cropping and resizing all happen at package time, which is what makes an SVG
/// still editable a month later: the pieces are in the file, so the editor can
/// be opened on it again and the drawing is only ever flattened on the way out.
/// It is also one resample rather than two — 1.0 rendered on the way in and the
/// packager scaled that, where this renders once at the size the texture wants.
///
/// The project holds artwork, not archives. The Grid Editor's rule is that a
/// package ships what was saved rather than a second rendering of it, and that
/// rule earns its keep where the edits are database records nobody could
/// reconstruct. Here an edit is a picture, encoding it is texconv doing the same
/// thing to the same bytes, and keeping 33 MB of archive per project to avoid
/// running it again would be a poor trade.
///
/// <c>Sponsors/</c> is 1.0's layout and is read for as long as projects made by
/// it are about. Nothing writes to it now: a decal with a source uses the
/// source, and replacing a 1.0 decal gives it one.
/// </summary>
public sealed class SponsorProject
{
    /// <summary>Path to the <c>.simplified</c> file on disk. Null until first Save As.</summary>
    public string? ZipPath { get; set; }

    /// <summary>Working temp directory mirroring the project's tree.</summary>
    public string TempDir { get; set; } = "";

    public ProjectInfo Info { get; set; } = new();

    /// <summary>The imported files, one per decal, named after it.</summary>
    public string SourcesFolder => Path.Combine(TempDir, ProjectStore.SourcesFolderName);

    /// <summary>Where 1.0 put its rendered pictures.</summary>
    public string SponsorsFolder => Path.Combine(TempDir, ProjectStore.SponsorsFolderName);

    /// <summary>And their letterboxed menu copies.</summary>
    public string MenuFolder => Path.Combine(SponsorsFolder, ProjectStore.MenuFolderName);

    /// <summary>UI-friendly name. "Untitled project" until first Save As.</summary>
    public string DisplayName => ZipPath is null
        ? "Untitled project"
        : Path.GetFileNameWithoutExtension(ZipPath);

    /// <summary>
    /// The file imported for a decal, whatever kind it is, or null if there is
    /// none. Found by name rather than by a list, so the folder cannot disagree
    /// with itself.
    /// </summary>
    public string? SourcePath(string decalName)
    {
        if (!Directory.Exists(SourcesFolder)) return null;

        return Directory.EnumerateFiles(SourcesFolder, decalName + ".*")
            .FirstOrDefault(f => !f.EndsWith(EditsSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where a decal's edits live, whether or not it has any.</summary>
    public string EditsPath(string decalName) =>
        Path.Combine(SourcesFolder, decalName + EditsSuffix);

    /// <summary>Where 1.0 kept this decal's rendered picture.</summary>
    public string PicturePath(string decalName) =>
        Path.Combine(SponsorsFolder, decalName + ".png");

    /// <summary>And its menu version.</summary>
    public string MenuPicturePath(string decalName) =>
        Path.Combine(MenuFolder, decalName + ".png");

    /// <summary>Whether this decal has been replaced, by either layout.</summary>
    public bool IsReplaced(string decalName) =>
        SourcePath(decalName) is not null || File.Exists(PicturePath(decalName));

    /// <summary>
    /// Every decal this project replaces, by name.
    ///
    /// Read from the folders rather than from a list in info.json: the files are
    /// the edits, so what is there cannot be out of step with what is listed.
    /// </summary>
    public IReadOnlyList<string> ReplacedDecals
    {
        get
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(SourcesFolder))
            {
                foreach (var file in Directory.EnumerateFiles(SourcesFolder))
                {
                    if (file.EndsWith(EditsSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileNameWithoutExtension(file) is { Length: > 0 } name) names.Add(name);
                }
            }

            if (Directory.Exists(SponsorsFolder))
            {
                foreach (var file in Directory.EnumerateFiles(SponsorsFolder, "*.png"))
                    if (Path.GetFileNameWithoutExtension(file) is { Length: > 0 } name) names.Add(name);
            }

            return names.ToList();
        }
    }

    /// <summary>
    /// Named so it cannot collide with an imported file: an extension is one
    /// segment, so a decal's source can never be called <c>.edits.json</c>.
    /// </summary>
    private const string EditsSuffix = ".edits.json";
}
