using System.IO;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;

namespace SimplifiedStudio.Model;

/// <summary>
/// One row in the texture list. Wraps the raw <see cref="ErpResource"/> and
/// surfaces the parsed surface-resource fields (size, format, mips, etc.).
/// </summary>
public sealed partial class TextureVm : ResourceListItem
{
    public ErpGfxSRVResource Srv { get; private set; }

    public TextureVm(ErpResource resource, ErpFile parentErp, string parentDisplayName)
        : base(resource, parentErp, parentDisplayName)
    {
        Srv = new ErpGfxSRVResource();
        Srv.FromResource(resource);
    }

    public override ResourceKind Kind => ResourceKind.Texture;

    /// <summary>Re-parse the wrapped resource — call after mutating fragments.</summary>
    public void Refresh()
    {
        Srv = new ErpGfxSRVResource();
        Srv.FromResource(Resource);
    }

    public uint Width        => Srv.SurfaceRes.Width;
    public uint Height       => Srv.SurfaceRes.Height;
    public uint MipMapCount  => Srv.SurfaceRes.Fragment0.MipMapCount;
    public uint ArraySize    => Srv.SurfaceRes.Fragment0.ArraySize;
    public ErpGfxSurfaceFormat ImageType => Srv.SurfaceRes.Fragment0.ImageType;

    /// <summary>Total mip levels including the external mipmap chain if present.</summary>
    public uint TotalMipLevels => MipMapCount + (uint)Srv.SurfaceRes.Frag2.Mips.Count;

    /// <summary>If non-empty, points to the game-relative external mipmap file path.</summary>
    public string MipMapFileName => Srv.SurfaceRes.Frag2.MipMapFileName ?? string.Empty;

    public bool HasExternalMips => Srv.SurfaceRes.HasMips;
    public bool HasValidExternalMips => Srv.SurfaceRes.HasValidMips;

    /// <summary>
    /// Absolute path of the on-disk .mipmaps file.
    ///
    /// <see cref="MipMapFileName"/> is game-root-relative, so an app that knows
    /// where the archive itself sits can resolve it with nothing configured.
    /// Whether it can is the one thing that differs between the apps, so each
    /// answers <see cref="MipRootFromArchive"/> for itself; the configured game
    /// folder is what decides when it cannot.
    /// </summary>
    public string? ResolveExternalMipPath(string? gameDir)
    {
        if (string.IsNullOrEmpty(MipMapFileName)) return null;

        if (MipRootFromArchive() is { } root) return Path.Combine(root, MipMapFileName);

        return string.IsNullOrEmpty(gameDir) ? null : Path.Combine(gameDir, MipMapFileName);
    }

    /// <summary>
    /// The folder this texture's mip path hangs off, worked out from the
    /// archive's own location, or null when this app cannot know that.
    ///
    /// Deliberately has no default: the answer is a real difference between the
    /// apps, and a partial method with a return type has to be implemented, so
    /// each has to say which it is rather than inherit someone else's answer.
    /// </summary>
    private partial string? MipRootFromArchive();

    public uint FullWidth  => HasExternalMips ? Srv.SurfaceRes.MipWidth  : Width;
    public uint FullHeight => HasExternalMips ? Srv.SurfaceRes.MipHeight : Height;

    /// <summary>Compact label for the list — e.g. "2048×2048 BC7_SRGB +9mips".</summary>
    public override string SummaryLine
    {
        get
        {
            var mipsTag = HasExternalMips
                ? $" {TotalMipLevels} mips ({Srv.SurfaceRes.Frag2.Mips.Count} ext)"
                : (MipMapCount > 1 ? $" {MipMapCount} mips" : "");
            var arrayTag = ArraySize > 1 ? $" Slices [{ArraySize}]" : "";
            return $"{FullWidth}×{FullHeight} {ImageType}{mipsTag}{arrayTag}";
        }
    }
}
