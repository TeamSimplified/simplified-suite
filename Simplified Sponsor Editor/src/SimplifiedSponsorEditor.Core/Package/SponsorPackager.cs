using System.IO.Compression;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;
using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;
using SimplifiedSponsorEditor.Core.Project;
using SimplifiedSponsorEditor.Core.Sponsors;

namespace SimplifiedSponsorEditor.Core.Package;

/// <summary>What a package needs to know.</summary>
public sealed record PackageRequest(
    SponsorProject Project,
    string OriginalsDir,
    string GameDir,
    string OutputZipPath);

/// <summary>What it produced.</summary>
public sealed record PackageResult(
    string ZipPath,
    int CarDecals,
    int MenuDecals,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turning a project's artwork into a mod anyone can install.
///
/// The decals do not go back into the archives they came from. They are merged
/// into <c>customisation_decals.nefx2.sm51.erp</c>, a shader archive holding a
/// single resource, and the game picks them up from there: EGO addresses a
/// resource by its eaid rather than by which archive it arrived in, so any
/// archive the game loads can supply one. Shipping a 33 MB pair of sponsor
/// archives to change four logos would be the alternative.
///
/// Both shader variants get the same treatment. <c>sm51</c> and <c>sm51_amd</c>
/// are the two paths the game takes depending on the card, and a mod that
/// patched one would work on some machines and not others.
///
/// Only the decals actually replaced are merged in. That matters for the same
/// reason it does in the Team Editor: everything carried across replaces what
/// is already there, so including a decal nobody edited would quietly revert it
/// to the bundled copy and undo whatever else the user has installed.
/// </summary>
public static class SponsorPackager
{
    /// <summary>Where the game keeps the archives this merges into.</summary>
    public const string ShaderFolderName = "shader_package_2025";

    /// <summary>The two shader variants, one per graphics vendor path.</summary>
    public static readonly IReadOnlyList<string> ShaderArchiveNames =
    [
        "customisation_decals.nefx2.sm51.erp",
        "customisation_decals.nefx2.sm51_amd.erp",
    ];

    /// <summary>
    /// The folder an installer drops over the game. Named for the game so a
    /// person unzipping it by hand can see where it goes.
    /// </summary>
    public const string PackageRootName = "F1 25";

    /// <summary>
    /// What the menu's pictures measure. The artwork is letterboxed onto this
    /// aspect when it is chosen; texconv does the scaling down on the way in.
    /// </summary>
    public const uint MenuWidth = 340;
    public const uint MenuHeight = 244;

    /// <summary>
    /// What a replaced decal is written back as.
    ///
    /// BC7 rather than the BC2 the game ships. Both are eight bits a pixel, so
    /// this costs nothing in size, and BC2 stores alpha as four explicit bits —
    /// which is visible as banding on the soft edge of a logo, exactly where
    /// these are looked at. The format travels in the resource's own metadata,
    /// so the game reads whatever is written.
    /// </summary>
    private const ErpGfxSurfaceFormat DecalFormat = ErpGfxSurfaceFormat.BC7_SRGB;

    /// <summary>
    /// How much of a decal to leave clear around the artwork, each side.
    ///
    /// The game never runs its own to an edge. Measured across its decals: a
    /// side margin from 1.6% to 5.3% of the texture, median 2.0%, and from 4.4%
    /// to 25% on the menu thumbnails, median 8.8%. Six per cent sits inside both
    /// ranges and reads as deliberate rather than as a rounding error — on a
    /// 1024 decal it is 61 pixels, which is the room the most generous of the
    /// game's own leaves.
    /// </summary>
    private const double DecalMargin = 0.06;

    public static PackageResult Build(PackageRequest request, Action<string>? log = null,
                                      IProgress<string>? say = null,
                                      CancellationToken cancel = default)
    {
        var project = request.Project;
        var replaced = project.ReplacedDecals;

        if (replaced.Count == 0)
            throw new InvalidOperationException("Nothing has been replaced, so there is nothing to package.");

        var shaderFolder = Path.Combine(request.GameDir, ShaderFolderName);
        foreach (var name in ShaderArchiveNames)
        {
            if (!File.Exists(Path.Combine(shaderFolder, name)))
                throw new FileNotFoundException(
                    $"{name} isn't in {shaderFolder}. The package is built by merging into the game's own " +
                    "shader archives, so it needs the install.",
                    Path.Combine(shaderFolder, name));
        }

        var catalogue = SponsorCatalogue.Read(request.OriginalsDir);
        var warnings = new List<string>();

        // The decals are merged into the game's own archives, because the
        // shaders in them have to be the game's. Anything already merged into
        // one goes out inside this package, so say so before it does.
        warnings.AddRange(Simplified.Shared.GameFileGuard.Warnings(
            request.GameDir, ShaderArchiveNames.Select(Simplified.Shared.GameFileGuard.ShaderPath)));
        var runner = new TexconvRunner();

        var work = Path.Combine(Path.GetTempPath(), "simplified-sponsor-editor", "package",
                                Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            // ── The two overlays ──────────────────────────────────────
            log?.Invoke($"Preparing {replaced.Count} decal(s)...");
            say?.Report("Reading your decals…");

            var decals = replaced
                .Select(catalogue.Find)
                .OfType<SponsorDecal>()
                .ToList();

            foreach (var missing in replaced.Where(n => catalogue.Find(n) is null))
                warnings.Add($"{missing} is not a decal in these archives — skipped.");

            cancel.ThrowIfCancellationRequested();

            var carOverlay = Overlay(
                Path.Combine(request.OriginalsDir, SponsorCatalogue.CarArchiveName),
                decals.Select(d => d.CarIdentifier));

            var menuOverlay = Overlay(
                Path.Combine(request.OriginalsDir, SponsorCatalogue.MenuArchiveName),
                decals.Where(d => d.HasMenuThumbnail).Select(d => d.MenuIdentifier!));

            // ── The pictures ──────────────────────────────────────────
            var carCount = 0;
            var menuCount = 0;
            var fitted = new List<string>();

            var done = 0;
            foreach (var decal in decals)
            {
                cancel.ThrowIfCancellationRequested();
                log?.Invoke($"  {decal.Name}");
                say?.Report($"Converting your pictures — {++done} of {decals.Count}…");

                if (Import(carOverlay, decal.CarIdentifier, project.PicturePath(decal.Name),
                           runner, work, width: 0, height: 0, warnings, fitted))
                {
                    carCount++;
                }

                if (!decal.HasMenuThumbnail) continue;

                // The menu picture is the letterboxed copy made when the
                // artwork was chosen; falling back to the raw artwork would put
                // a squashed logo in the menu, which is worse than none.
                var menuArtwork = project.MenuPicturePath(decal.Name);
                if (!File.Exists(menuArtwork))
                {
                    warnings.Add($"{decal.Name} has no menu version, so the menu keeps the original picture.");
                    continue;
                }

                if (Import(menuOverlay, decal.MenuIdentifier!, menuArtwork,
                           runner, work, MenuWidth, MenuHeight, warnings, fitted))
                {
                    menuCount++;
                }
            }

            // Said after the pictures rather than during them, so the notes sit
            // together rather than between the decals they belong to.
            warnings.AddRange(SliceFit.Summarise(fitted));

            // ── Merge into both shader archives and zip ───────────────
            var staging = Path.Combine(work, PackageRootName, ShaderFolderName);
            Directory.CreateDirectory(staging);


            foreach (var name in ShaderArchiveNames)
            {
                cancel.ThrowIfCancellationRequested();
                log?.Invoke($"Merging into {name}...");
                say?.Report("Adding your decals to the mod…");

                var target = ErpFileIO.Load(Path.Combine(shaderFolder, name));

                // The pair the game expects in any mod that ships a shader
                // package, the same two Studio and ERP put in theirs. First, so
                // a decal would win on a collision — the order Studio uses, and
                // nothing of these two can collide with artwork anyway.
                Simplified.Shared.SerpsOverlay.AddTo(target);
                MergeInto(target, carOverlay);
                MergeInto(target, menuOverlay);

                target.UpdateOffsets();
                ErpFileIO.Save(target, Path.Combine(staging, name));
            }

            log?.Invoke("Zipping...");
            say?.Report("Writing your mod file…");
            if (File.Exists(request.OutputZipPath)) File.Delete(request.OutputZipPath);

            var directory = Path.GetDirectoryName(request.OutputZipPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            ZipFile.CreateFromDirectory(work, request.OutputZipPath,
                CompressionLevel.Optimal, includeBaseDirectory: false);

            return new PackageResult(request.OutputZipPath, carCount, menuCount, warnings);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// An archive cut down to the decals being changed, and the surfaces holding
    /// their pixels.
    ///
    /// The surface has to come too: a texture is two resources, and the view
    /// reaches its pixels through
    /// <see cref="ErpGfxSRVResource0.SurfaceResourceName"/> — which resolves
    /// against whichever archive the resource belongs to, so a view without its
    /// surface cannot be read at all.
    /// </summary>
    private static ErpFile Overlay(string archivePath, IEnumerable<string> viewIdentifiers)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in viewIdentifiers)
        {
            keep.Add(view);
            keep.Add(view + ".image");
        }

        var overlay = ErpFileIO.Load(archivePath);
        overlay.Resources.RemoveAll(r => !keep.Contains(r.Identifier));
        return overlay;
    }

    /// <summary>Encode one picture and write it into the overlay's texture.</summary>
    /// <param name="width">
    /// The one size this texture will accept, or zero to let the picture keep
    /// its own resolution — which a standalone texture may, since the resource
    /// carries its own size. Its shape is another matter: see
    /// <see cref="SliceFit.EncodeToShape"/>.
    /// </param>
    private static bool Import(ErpFile overlay, string viewIdentifier, string pngPath,
                               TexconvRunner runner, string work,
                               uint width, uint height, List<string> warnings,
                               List<string>? fitted = null)
    {
        try
        {
            var resource = overlay.FindResource(viewIdentifier);

            var srv = new ErpGfxSRVResource();
            srv.FromResource(resource);

            // Keep the mip count the game gave this texture. Where the picture
            // is too small to make that many, PngToDds falls back to as many as
            // it can rather than refusing.
            var f0 = srv.SurfaceRes.Fragment0;
            var mips = Math.Max((int)f0.MipMapCount, 1);

            // Neither of these stretches a picture. The menu's are one size and
            // land on it exactly; a car decal keeps whatever resolution it
            // arrived at, and only its shape is corrected — the quad it is drawn
            // on belongs to the car model and does not move, so a texture of
            // another shape is stretched onto it however big it is. Every one of
            // the game's 641 car decals is 1024x1024.
            var fit = width > 0 && height > 0
                ? SliceFit.Encode(pngPath, work, DecalFormat, mips, width, height, runner, DecalMargin)
                : SliceFit.EncodeToShape(pngPath, work, DecalFormat, mips,
                                         f0.Width, f0.Height, runner, DecalMargin);

            // Only a change of shape is worth a note. Every picture is inset to
            // leave its margin, so saying so would be a line per decal reporting
            // that the app did what it always does.
            if (fit.How == FitKind.Padded && fit.ShapeChanged && fit.Describe() is { } how)
            {
                var note = $"{SponsorCatalogue.ShortName(viewIdentifier)} was {how}.";
                if (fitted is not null && !fitted.Contains(note)) fitted.Add(note);
            }

            using var stream = File.Open(fit.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var dds = new DdsFile(stream);

            // Stream.Null for the mipmaps: every decal in these archives keeps
            // its mip chain inline - checked, all 1,290 of them - so there is no
            // sidecar to write.
            dds.ToErpGfxSRVResource(srv, Stream.Null, importTexArray: false, texArrayIndex: 0);
            srv.ToResource(resource);

            File.Delete(fit.Path);
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"{SponsorCatalogue.ShortName(viewIdentifier)} couldn't be imported: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fold an overlay into a target archive, the overlay winning. Anything the
    /// overlay does not carry is left exactly as it was.
    /// </summary>
    private static void MergeInto(ErpFile target, ErpFile overlay)
    {
        // Snapshot: adding to Resources mutates the list being walked.
        foreach (var resource in overlay.Resources.ToList())
        {
            var existing = target.Resources.Find(r => r.Identifier == resource.Identifier);
            if (existing is not null) target.Resources.Remove(existing);

            resource.ParentFile = target;
            foreach (var fragment in resource.Fragments) fragment.ParentFile = target;
            target.Resources.Add(resource);
        }
    }
}
