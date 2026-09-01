namespace SimplifiedStudio.Model;

public sealed partial class TextureVm
{
    /// <summary>
    /// Nothing to work from. A project's ERPs are extracted out of a
    /// .simplified zip into a temporary folder, so where they sit says nothing
    /// about where the game is — the configured game folder is the only answer
    /// here.
    /// </summary>
    private partial string? MipRootFromArchive() => null;
}
