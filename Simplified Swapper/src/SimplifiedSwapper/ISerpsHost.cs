using SimplifiedSwapper.Core.Project;

namespace SimplifiedSwapper;

/// <summary>
/// What a swap panel needs from the window.
///
/// The panels run the swaps; the window owns the template they go into. This is
/// the whole of what crosses between them, which keeps a panel unaware of how
/// a .serps is opened, saved or arranged.
/// </summary>
public interface ISerpsHost
{
    /// <summary>Whether a template is open — a swap has nowhere to go without one.</summary>
    bool HasTemplate { get; }

    /// <summary>Raised when that changes, so a panel can re-check whether it can swap.</summary>
    event EventHandler? TemplateChanged;

    /// <summary>
    /// The open template's shader package, e.g. <c>object_flag.nefx2.sm51</c>,
    /// or null with nothing open. It is what says whose template it is, so it
    /// is what decides the team slot a swap takes over.
    /// </summary>
    string? ShaderPackage { get; }

    /// <summary>
    /// A scratch path for a swap to write its ERP to. Outside the template, so
    /// a swap that fails part-way through leaves nothing behind in it.
    /// </summary>
    string ScratchErpPath(SwapParts.Destination destination);

    /// <summary>
    /// Move a finished swap into the open template and mark it unsaved.
    /// </summary>
    /// <returns>The part's path inside the template, for the log.</returns>
    string PlaceSwap(string erpPath, SwapParts.Destination destination);

    /// <summary>
    /// Say what is happening. There is one status line for the window rather
    /// than one per panel, because there is one page and one thing happening
    /// on it at a time.
    /// </summary>
    void ReportStatus(string text, SwapStatus kind = SwapStatus.Plain);

    /// <summary>How far along, 0 to 1. Negative clears the bar.</summary>
    void ReportProgress(double fraction);
}
