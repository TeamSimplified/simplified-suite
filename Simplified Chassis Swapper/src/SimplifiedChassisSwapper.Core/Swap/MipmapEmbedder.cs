using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;

namespace SimplifiedChassisSwapper.Core.Swap;

/// <summary>
/// Pulls each texture's external <c>.mipmaps</c> chain into the ERP so the
/// output is self-contained: the swapped car ships at full resolution without
/// depending on the donor team's sidecar files.
///
/// Mechanically this drops the mips-descriptor fragment (3 fragments → 2) and
/// writes the whole decoded mip chain inline. Same operation as the Studio's
/// "Strip mipmaps".
/// </summary>
public static class MipmapEmbedder
{
    public sealed record EmbedTally(int Embedded, int Unreachable, int Skipped);

    /// <summary>
    /// Embed every texture's mip chain. Must run <b>before</b> identifiers are
    /// renamed, because the mips descriptor stores a game-relative path that has
    /// to keep matching the on-disk file.
    /// </summary>
    public static EmbedTally EmbedAll(ErpFile erp, string? gameDir, Action<string, double>? progress = null)
    {
        // Snapshot: the SRV rewrite below mutates resources as we go.
        var srvResources = erp.Resources
            .Where(r => r.ResourceType == "GfxSRVResource")
            .ToList();

        int embedded = 0, unreachable = 0, skipped = 0;
        for (var i = 0; i < srvResources.Count; i++)
        {
            var res = srvResources[i];
            progress?.Invoke($"Embedding mipmaps {i + 1}/{srvResources.Count}", (i + 1) / (double)srvResources.Count);

            try
            {
                var srv = new ErpGfxSRVResource();
                srv.FromResource(res);

                if (!srv.SurfaceRes.HasMips) { skipped++; continue; }

                var mipFileName = srv.SurfaceRes.Frag2.MipMapFileName;
                string? mipPath = string.IsNullOrEmpty(gameDir) || string.IsNullOrEmpty(mipFileName)
                    ? null
                    : Path.Combine(gameDir, mipFileName);

                var canReach = srv.SurfaceRes.HasValidMips
                            && mipPath is not null
                            && File.Exists(mipPath);

                // Tex arrays have to round-trip whole — a single slice can't
                // reconstruct the array layout.
                var isArray = srv.SurfaceRes.Fragment0.ArraySize > 1;

                DdsFile dds;
                using (Stream? mipStream = canReach ? File.OpenRead(mipPath!) : null)
                {
                    dds = srv.ToDdsFile(mipStream, exportTexArray: isArray, texArrayIndex: 0);
                }

                // Drop the mips descriptor from the linked surface (.image) resource.
                var surfaceRes = erp.FindResource(srv.Fragment0.SurfaceResourceName);
                if (surfaceRes.Fragments.Count == 3) surfaceRes.Fragments.RemoveAt(2);
                else if (surfaceRes.Fragments.Count != 2)
                    throw new InvalidOperationException(
                        $"Expected 2 or 3 fragments on the surface resource, found {surfaceRes.Fragments.Count}.");

                // Re-parse so HasMips is false, then write the full chain inline.
                var newSrv = new ErpGfxSRVResource();
                newSrv.FromResource(res);
                dds.ToErpGfxSRVResource(newSrv, mipMapsStream: null!, importTexArray: isArray, texArrayIndex: 0);
                newSrv.ToResource(res);

                if (canReach) embedded++; else unreachable++;
            }
            catch
            {
                // A texture we can't decode is left exactly as it was rather than
                // failing the whole swap; the caller reports the count.
                skipped++;
            }
        }

        return new EmbedTally(embedded, unreachable, skipped);
    }
}
