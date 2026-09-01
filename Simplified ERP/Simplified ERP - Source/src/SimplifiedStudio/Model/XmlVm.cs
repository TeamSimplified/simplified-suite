using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Xml;

namespace SimplifiedStudio.Model;

/// <summary>
/// A row representing an ERP resource that contains a binary-XML fragment.
/// Display-only — surfaced so the user can see what's in the project.
///
/// Not sealed: <see cref="VtfVm"/> derives from it so vehicle-handling
/// resources get their own category while still being read and written as
/// XML everywhere it matters.
/// </summary>
public class XmlVm : ResourceListItem
{
    public XmlVm(ErpResource resource, ErpFile parentErp, string parentDisplayName, XmlFile? parsed)
        : base(resource, parentErp, parentDisplayName)
    {
        Parsed = parsed;
    }

    public override ResourceKind Kind => ResourceKind.Xml;

    public XmlFile? Parsed { get; }

    /// <summary>Label this row leads with — the tag a subclass wants shown.</summary>
    protected virtual string KindPrefix => "XML";

    public override string SummaryLine
    {
        get
        {
            var bytes = SafeFragmentSize();
            var sizeTag = bytes >= 0 ? $"  {FormatBytes(bytes)}" : "";
            return $"{KindPrefix} — {Resource.ResourceType}{sizeTag}";
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
