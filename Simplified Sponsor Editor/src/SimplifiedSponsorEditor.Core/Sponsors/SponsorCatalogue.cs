using EgoEngineLibrary.Archive.Erp;

namespace SimplifiedSponsorEditor.Core.Sponsors;

/// <summary>
/// One decal: what it is called, and where it lives in each of the two archives.
/// </summary>
/// <param name="Name">The short name, e.g. <c>7even_n_da</c>. What a project's PNG is filed under.</param>
/// <param name="Sponsor">The sponsor it groups under.</param>
/// <param name="CarIdentifier">Its resource in <c>sponsors.erp</c> — the one on the bodywork.</param>
/// <param name="MenuIdentifier">Its resource in <c>sponsors_ui.erp</c>, or null when it has none.</param>
public sealed record SponsorDecal(string Name, string Sponsor, string CarIdentifier, string? MenuIdentifier)
{
    /// <summary>Whether replacing this also updates the picture shown in the customisation menu.</summary>
    public bool HasMenuThumbnail => MenuIdentifier is not null;
}

/// <summary>Every decal, and the sponsors they group into.</summary>
public sealed record Catalogue(IReadOnlyList<SponsorDecal> Decals)
{
    /// <summary>Sponsors in name order, each with its decals in name order.</summary>
    public IReadOnlyList<IGrouping<string, SponsorDecal>> BySponsor { get; } =
        Decals.GroupBy(d => d.Sponsor)
              .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
              .ToList();

    /// <summary>Find a decal by its short name.</summary>
    public SponsorDecal? Find(string name) =>
        Decals.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What there is to edit, read from the two bundled archives.
///
/// Every decal exists twice: once at full size on the car
/// (<c>ego_package/decals/textures</c>) and once as a small picture in the
/// customisation menu (<c>ui_package/art/textures/customisation/sponsor_decals</c>).
/// They share a short name, which is what pairs them, and the pairing is the
/// reason this app exists rather than two passes over two archives — replace a
/// logo on the car without replacing the one in the menu and the menu goes on
/// showing the old artwork for the thing you just changed.
///
/// Read with <see cref="ErpIndex"/>: this is a question about what the archives
/// contain, and 33 MB of texture payload is no part of the answer.
/// </summary>
public static class SponsorCatalogue
{
    /// <summary>The bundled archive of decals as they appear on the car.</summary>
    public const string CarArchiveName = "sponsors.erp";

    /// <summary>The bundled archive of the pictures the customisation menu shows.</summary>
    public const string MenuArchiveName = "sponsors_ui.erp";

    /// <summary>
    /// The suffix every texture resource's identifier carries. A texture is two
    /// resources — the view and the surface holding its pixels — and it is the
    /// view that is named without it.
    /// </summary>
    private const string SurfaceSuffix = ".image";

    /// <summary>Read both archives out of a folder and pair them up.</summary>
    public static Catalogue Read(string originalsDir)
    {
        var car = Views(Path.Combine(originalsDir, CarArchiveName));
        var menu = Views(Path.Combine(originalsDir, MenuArchiveName));

        var decals = car
            .Select(entry => new SponsorDecal(
                entry.Key,
                SponsorName.Of(entry.Key),
                entry.Value,
                menu.GetValueOrDefault(entry.Key)))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Catalogue(decals);
    }

    /// <summary>
    /// The texture views in an archive, by short name.
    ///
    /// Views only, not the surfaces beside them: the pair is one texture, and
    /// listing both would show every decal twice under names differing by
    /// ".image". Importing a picture writes through to the surface anyway.
    /// </summary>
    private static Dictionary<string, string> Views(string archivePath)
    {
        var index = ErpIndex.TryRead(archivePath);
        if (index is null) return [];

        return index.Resources
            .Where(r => r.ResourceType == "GfxSRVResource")
            .Where(r => !r.Identifier.EndsWith(SurfaceSuffix, StringComparison.Ordinal))
            .GroupBy(r => ShortName(r.Identifier), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Identifier, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>eaid://ego_package/decals/textures/7even_n_da.tif</c> → <c>7even_n_da</c>.
    /// The name a person recognises, and what the PNG in a project is called.
    /// </summary>
    public static string ShortName(string identifier)
    {
        var name = identifier;

        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];

        // ".tif" is the name the artwork had before it was ever a texture, and
        // is part of every one of these identifiers.
        if (name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        return name;
    }
}
