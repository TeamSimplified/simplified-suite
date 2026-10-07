using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Util;

namespace SimplifiedSponsorEditor.Core.Sponsors;

/// <summary>
/// How big the game draws a sponsor, read from the game's own archives.
///
/// A decal that replaces one of these is written to the shape of the one it
/// replaces, so the frames showing where artwork lands have to be the real
/// texture rather than a number somebody liked. v1.1 asks per decal, because by
/// then one has been picked. Here nothing has, so this asks the archive what
/// they are in general — and the answer is that they are all the same: measured
/// across 400 of them, every car decal is 1024 square and every menu thumbnail
/// is 340 by 244.
///
/// Read rather than written down even so, because a game update that changed
/// them would otherwise leave every frame in the app quietly lying. The measured
/// pair is only the fallback, for when the archives are not beside the exe.
/// </summary>
public static class SponsorTextures
{
    /// <summary>What the archives said, once they have been asked.</summary>
    private static (uint Width, uint Height)? _car;
    private static (uint Width, uint Height)? _menu;

    private static readonly object Gate = new();

    /// <summary>The texture a decal is painted into on the car.</summary>
    public static (uint Width, uint Height) Car =>
        Ask(ref _car, SponsorCatalogue.CarArchiveName, (1024, 1024));

    /// <summary>The thumbnail a decal gets in the customisation menu.</summary>
    public static (uint Width, uint Height) Menu =>
        Ask(ref _menu, SponsorCatalogue.MenuArchiveName, (340, 244));

    /// <summary>
    /// The first texture in an archive, which is every texture in it.
    ///
    /// Loaded once and kept: the archive is 20 MB and the answer is two numbers
    /// that cannot change while the app is running. Under a lock because the
    /// frames can ask for both at once.
    /// </summary>
    private static (uint Width, uint Height) Ask(
        ref (uint Width, uint Height)? held, string archive, (uint, uint) measured)
    {
        lock (Gate)
        {
            if (held is { } known) return known;

            var answer = Read(Path.Combine(AppPaths.OriginalsDir, archive)) ?? measured;
            held = answer;
            return answer;
        }
    }

    private static (uint Width, uint Height)? Read(string archivePath)
    {
        if (!File.Exists(archivePath)) return null;

        try
        {
            var erp = ErpFileIO.Load(archivePath);

            foreach (var resource in erp.Resources)
            {
                // The views, not the surfaces beside them: the pair is one
                // texture, and the view is the one named without ".image".
                if (resource.ResourceType != "GfxSRVResource"
                    || resource.Identifier.EndsWith(".image", StringComparison.Ordinal))
                    continue;

                var srv = new ErpGfxSRVResource();
                srv.FromResource(resource);

                var size = (srv.SurfaceRes.Fragment0.Width, srv.SurfaceRes.Fragment0.Height);
                if (size.Width > 0 && size.Height > 0) return size;
            }

            return null;
        }
        catch
        {
            // An archive that cannot be read is not worth failing a preview
            // over. The measured pair is what it would have said anyway.
            return null;
        }
    }
}
