using EgoEngineLibrary.Archive.Erp;
using SimplifiedStudio.Core.Context;

namespace SimplifiedStudio.Model;

/// <summary>
/// A row representing an ERP resource whose payload is a Codemasters Ego
/// <c>.context</c> blob — a table of named material slots.
///
/// The blob is parsed for display. Export and Import stay raw-byte round trips:
/// the slot table is understood well enough to read but not to rebuild, and the
/// third header field's purpose is still unknown.
/// </summary>
public sealed class ContextVm : ResourceListItem
{
    public ContextVm(ErpResource resource, ErpFile parentErp, string parentDisplayName)
        : base(resource, parentErp, parentDisplayName) { }

    public override ResourceKind Kind => ResourceKind.Context;

    private bool _parseAttempted;
    private ContextFile? _parsed;
    private string? _parseError;

    /// <summary>
    /// The parsed context, or null if it wouldn't parse. Lazy and cached: an
    /// archive can hold hundreds of these and most are never opened, but the
    /// summary line asks for it, so it is worth keeping once done.
    /// </summary>
    public ContextFile? Parsed
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
                _parsed = ContextReader.Read(buffer.ToArray());
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

    /// <summary>
    /// Slot count rather than the resource type — how many materials a context
    /// wires up is what tells one apart from the next.
    /// </summary>
    public override string SummaryLine
    {
        get
        {
            var context = Parsed;
            if (context is null) return $"Context — unparsed{SizeTag()}";

            var slots = context.Slots.Count == 1 ? "1 slot" : $"{context.Slots.Count} slots";
            var materials = context.DistinctMaterialCount == 1 ? "1 material" : $"{context.DistinctMaterialCount} materials";
            return $"Context — {slots}, {materials}{SizeTag()}";
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
