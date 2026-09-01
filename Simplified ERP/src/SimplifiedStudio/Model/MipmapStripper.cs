using System.IO;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;

namespace SimplifiedStudio.Model;

/// <summary>
/// Folding a texture's external mip chain back into the ERP.
///
/// This lives apart from <see cref="TextureIO"/> on purpose. That file is kept
/// byte-identical across Simplified Studio and Simplified ERP, and stripping is
/// an ERP-only operation — Studio offers no way to invoke it. Anything added to
/// the shared file reaches both apps whether or not both have a use for it, so
/// what only one app needs belongs here instead.
/// </summary>
public static class MipmapStripper
{
    public sealed record StripResult(uint Width, uint Height, bool UsedFullRes,
                                     int MipLevelsInline, string SurfaceResourceName);

    /// <summary>
    /// Move the full external mip chain INTO the ERP and remove the surface
    /// resource's mips-descriptor fragment. After this the texture no longer
    /// needs an external .mipmaps file — any pending entry for it is dropped
    /// since it'd become an orphan write on save.
    /// </summary>
    public static StripResult Strip(TextureVm vm, string? gameDir, PendingMips? pendingMips = null)
    {
        // 1. Decode to a full-resolution DDS in memory.
        var mipPath = vm.ResolveExternalMipPath(gameDir);

        Stream? mipStream = null;
        if (vm.HasExternalMips && vm.HasValidExternalMips && !string.IsNullOrEmpty(mipPath))
        {
            if (pendingMips is not null && pendingMips.HasPending(mipPath))
                mipStream = pendingMips.OpenForRead(mipPath);
            else if (File.Exists(mipPath))
                mipStream = File.OpenRead(mipPath);
        }
        var useFullRes = mipStream is not null;

        // Tex arrays must be round-tripped whole (all slices) — see the export-side comment.
        var isArray = vm.Srv.SurfaceRes.Fragment0.ArraySize > 1;

        DdsFile dds;
        using (mipStream)
        {
            dds = vm.Srv.ToDdsFile(mipStream, exportTexArray: isArray, texArrayIndex: 0);
        }

        // 2. Strip the mips descriptor fragment from the surface (.image) resource.
        var surfaceResName = vm.Srv.Fragment0.SurfaceResourceName;
        var surfaceRes = vm.Resource.ParentFile.FindResource(surfaceResName);

        if (surfaceRes.Fragments.Count == 3)
        {
            surfaceRes.Fragments.RemoveAt(2);
        }
        else if (surfaceRes.Fragments.Count != 2)
        {
            throw new InvalidOperationException(
                $"Expected the surface resource to have 2 or 3 fragments, found {surfaceRes.Fragments.Count}.");
        }

        // 3. Re-parse the SRV so HasMips reads as false.
        var newSrv = new ErpGfxSRVResource();
        newSrv.FromResource(vm.Resource);

        // 4. Re-import the in-memory DDS. With HasMips=false the entire DDS
        //    body (all mip levels concatenated) is dumped inline into Fragment1.Data.
        dds.ToErpGfxSRVResource(newSrv, mipMapsStream: null!, importTexArray: isArray, texArrayIndex: 0);
        newSrv.ToResource(vm.Resource);

        // 5. The texture no longer references the external file — drop any
        //    pending write for it so we don't flush an orphan on save.
        if (!string.IsNullOrEmpty(mipPath))
            pendingMips?.Remove(mipPath);

        return new StripResult(
            dds.header.width,
            dds.header.height,
            useFullRes,
            (int)dds.header.mipMapCount,
            surfaceResName);
    }
}
