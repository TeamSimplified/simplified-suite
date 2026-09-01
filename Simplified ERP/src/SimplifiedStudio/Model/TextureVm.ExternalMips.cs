namespace SimplifiedStudio.Model;

public sealed partial class TextureVm
{
    /// <summary>
    /// Walk up from the ERP's own location until the relative mip path
    /// resolves. An archive opened from inside a game install answers this,
    /// which is why ERP needs no game folder configured for the common case.
    /// </summary>
    private partial string? MipRootFromArchive() => ExternalMips.FindRoot(ParentPath, MipMapFileName);
}
