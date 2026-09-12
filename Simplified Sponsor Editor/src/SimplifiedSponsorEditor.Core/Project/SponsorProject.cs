namespace SimplifiedSponsorEditor.Core.Project;

/// <summary>
/// An open sponsor project. While open its contents live in
/// <see cref="TempDir"/>; the <c>.simplified</c> zip on disk is only touched on
/// Save (rezipped from the temp dir) and Open (extracted to it).
///
/// <code>
/// info.json
/// Sponsors/
///     redbull_da.png           what goes on the car
///     menu/
///         redbull_da.png       the same picture, letterboxed for the menu
/// </code>
///
/// The project holds artwork, not archives. The Grid Editor's rule is that a
/// package ships what was saved rather than a second rendering of it, and that
/// rule earns its keep where the edits are database records nobody could
/// reconstruct. Here an edit is a picture, encoding it is texconv doing the
/// same thing to the same bytes, and keeping 33 MB of archive per project to
/// avoid running it again would be a poor trade. What it buys instead: a
/// project is a few hundred KB, it can be passed around, and the artwork can
/// still be looked at and swapped a month later.
/// </summary>
public sealed class SponsorProject
{
    /// <summary>Path to the <c>.simplified</c> file on disk. Null until first Save As.</summary>
    public string? ZipPath { get; set; }

    /// <summary>Working temp directory mirroring the project's tree.</summary>
    public string TempDir { get; set; } = "";

    public ProjectInfo Info { get; set; } = new();

    /// <summary>The replacement pictures, one per decal, named after it.</summary>
    public string SponsorsFolder => Path.Combine(TempDir, ProjectStore.SponsorsFolderName);

    /// <summary>
    /// The same pictures prepared for the customisation menu — letterboxed onto
    /// its aspect so the logo is not squashed on the way down to 340×244.
    /// Written when the artwork is chosen, since that is where the picture is.
    /// </summary>
    public string MenuFolder => Path.Combine(SponsorsFolder, ProjectStore.MenuFolderName);

    /// <summary>UI-friendly name. "Untitled project" until first Save As.</summary>
    public string DisplayName => ZipPath is null
        ? "Untitled project"
        : Path.GetFileNameWithoutExtension(ZipPath);

    /// <summary>Where a decal's replacement picture lives, whether or not it is there.</summary>
    public string PicturePath(string decalName) =>
        Path.Combine(SponsorsFolder, decalName + ".png");

    /// <summary>Where its menu version lives.</summary>
    public string MenuPicturePath(string decalName) =>
        Path.Combine(MenuFolder, decalName + ".png");

    /// <summary>Whether this decal has been replaced.</summary>
    public bool IsReplaced(string decalName) => File.Exists(PicturePath(decalName));

    /// <summary>
    /// Every decal this project replaces, by name.
    ///
    /// Read from the folder rather than from a list in info.json: the pictures
    /// are the edits, so the folder cannot be out of step with itself.
    /// </summary>
    public IReadOnlyList<string> ReplacedDecals =>
        Directory.Exists(SponsorsFolder)
            ? Directory.EnumerateFiles(SponsorsFolder, "*.png")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
}
