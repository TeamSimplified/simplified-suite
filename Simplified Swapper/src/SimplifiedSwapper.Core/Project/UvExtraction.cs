using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Util;
using System.IO;

namespace SimplifiedSwapper.Core.Project;

/// <summary>
/// The real size of each texture in an ERP.
///
/// A UV template is vector data, so it can be drawn at any size — and the size
/// that matters is the one of the file a person will paint on. Where a texture
/// keeps its detail in an external <c>.mipmaps</c> sidecar, the ERP holds only
/// the small tail of the chain, so the engine's idea of the size is smaller
/// than the texture really is. Asking the surface resource gives the true one.
/// </summary>
public static class TextureSizes
{
    /// <summary>
    /// Every texture in the archive, by the name a UV template knows it as,
    /// with the size it should be drawn at.
    ///
    /// Best effort by design: a texture whose surface resource won't parse is
    /// left out rather than failing the lot, and the caller falls back to
    /// whatever size the engine worked out for it.
    /// </summary>
    public static Dictionary<string, (int Width, int Height)> Read(string erpPath)
    {
        var sizes = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);

        ErpFile erp;
        try { erp = ErpFileIO.Load(erpPath); }
        catch { return sizes; }

        foreach (var res in erp.Resources)
        {
            if (!res.ResourceType.Trim('\0', ' ').Equals("GfxSRVResource", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var srv = new ErpGfxSRVResource();
                srv.FromResource(res);

                var surface = srv.SurfaceRes;
                var width = surface.HasMips ? surface.MipWidth : surface.Width;
                var height = surface.HasMips ? surface.MipHeight : surface.Height;

                if (width <= 0 || height <= 0) continue;

                // Under both names. A resource is "livery_v2_d.tif" and the UV
                // template that samples it is "livery_v2_d", so keying only one
                // way silently finds nothing and everything draws at whatever
                // size the ERP happened to hold.
                sizes[res.FileName] = ((int)width, (int)height);
                sizes[Path.GetFileNameWithoutExtension(res.FileName)] = ((int)width, (int)height);
            }
            catch
            {
                // One unreadable texture is not a reason to give up on the rest.
            }
        }

        return sizes;
    }
}
