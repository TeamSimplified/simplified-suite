using EgoEngineLibrary.Archive.Erp;
using SimplifiedStudio.Core.Material;

namespace SimplifiedStudio.Model;

/// <summary>
/// A row representing an ERP resource whose payload is a Codemasters Ego
/// <c>.material</c> blob.
///
/// The blob is parsed for display — shader, sampler bindings, uniform names and
/// their float defaults. Export and Import stay raw-byte round trips: the format
/// spec this reader came from is explicit that it is sufficient for reading and
/// not for writing, since the per-parameter records and the float pool's sizing
/// are still undecoded.
/// </summary>
public sealed class MaterialVm : ResourceListItem
{
    public MaterialVm(ErpResource resource, ErpFile parentErp, string parentDisplayName)
        : base(resource, parentErp, parentDisplayName) { }

    public override ResourceKind Kind => ResourceKind.Material;

    private bool _parseAttempted;
    private MaterialFile? _parsed;
    private string? _parseError;

    /// <summary>
    /// The parsed material, or null if it wouldn't parse. Lazy and cached: the
    /// list builds hundreds of these rows at once and most are never opened,
    /// but the summary line asks for it, so it is worth keeping once done.
    /// </summary>
    public MaterialFile? Parsed
    {
        get
        {
            if (_parseAttempted) return _parsed;
            _parseAttempted = true;
            try
            {
                using var stream = Resource.Fragments[0].GetDataStream(true);
                using var buffer = new System.IO.MemoryStream();
                stream.CopyTo(buffer);
                _parsed = MaterialReader.Read(buffer.ToArray());
            }
            catch (Exception ex)
            {
                _parseError = ex.Message;
            }
            return _parsed;
        }
    }

    /// <summary>Why the parse failed, for the preview to show instead of a bare hex dump.</summary>
    public string? ParseError
    {
        get { _ = Parsed; return _parseError; }
    }

    /// <summary>The shader's own name, without the eaid:// path around it.</summary>
    public string ShaderName => ShortenEaid(Parsed?.ShaderEaid ?? "");

    /// <summary>
    /// The last path segment of an eaid, minus its extension — what a person
    /// would call the thing. The full identifier stays available for anyone who
    /// needs it.
    /// </summary>
    public static string ShortenEaid(string eaid)
    {
        if (string.IsNullOrEmpty(eaid)) return "";
        var slash = eaid.LastIndexOf('/');
        var name = slash >= 0 ? eaid[(slash + 1)..] : eaid;
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>
    /// Shader and sampler count rather than the resource type and byte size —
    /// which shader a material drives is the thing that distinguishes one row
    /// from the next, and every one of them is a few KB.
    /// </summary>
    public override string SummaryLine
    {
        get
        {
            var material = Parsed;
            if (material is null) return $"Material — unparsed{SizeTag()}";

            var samplers = material.Samplers.Count == 1 ? "1 texture" : $"{material.Samplers.Count} textures";
            return $"Material — {ShaderName}  ·  {samplers}{SizeTag()}";
        }
    }

    private string SizeTag()
    {
        var bytes = SafeFragmentSize();
        return bytes >= 0 ? $"  {FormatBytes(bytes)}" : "";
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
