using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Data.Pkg;

namespace SimplifiedStudio.Model;

/// <summary>
/// A row representing an ERP resource whose payload is a PKG fragment
/// (Codemasters Ego binary data package). Display-only for now —
/// double-click / edit / preview comes later.
/// </summary>
public sealed class PkgVm : ResourceListItem
{
    public PkgVm(ErpResource resource, ErpFile parentErp, string parentDisplayName, PkgFile? parsed)
        : base(resource, parentErp, parentDisplayName)
    {
        Parsed = parsed;
    }

    public override ResourceKind Kind => ResourceKind.Pkg;

    /// <summary>The parsed PKG (null only if we surfaced a row but parsing failed later).</summary>
    public PkgFile? Parsed { get; }

    public override string SummaryLine
    {
        get
        {
            var bytes = SafeFragmentSize();
            var sizeTag = bytes >= 0 ? $"  {FormatBytes(bytes)}" : "";
            return $"PKG — {Resource.ResourceType}{sizeTag}";
        }
    }

    private long SafeFragmentSize()
    {
        try { return (long)Resource.Fragments[0].Size; }
        catch { return -1; }
    }

    private static string FormatBytes(long n) => n switch
    {
        < 1024            => $"{n} B",
        < 1024 * 1024     => $"{n / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{n / (1024d * 1024):0.#} MB",
        _                 => $"{n / (1024d * 1024 * 1024):0.#} GB",
    };
}
