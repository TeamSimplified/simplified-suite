using System.IO;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;
using SimplifiedTeamEditor.Core.Logos;

namespace SimplifiedGridEditor;

/// <summary>
/// The game's own logo for a slot, as a PNG that can be shown.
///
/// Getting one means reading the texture array out of the bundled archive and
/// running the slice through texconv, which is far too slow to do while
/// drawing a list. So they are made on demand, when a team's logos are opened,
/// and kept for the rest of the session — the pictures cannot change underneath
/// us, since the archive they come from is bundled and read-only.
///
/// The files go in the system temp directory rather than the app folder: they
/// are derived, worthless once the app closes, and Windows clears them up.
/// </summary>
public sealed class LogoPreviewCache
{
    private readonly string _brandingErpPath;
    private readonly string _directory;
    private readonly TexconvRunner _runner;

    private readonly Dictionary<string, string?> _byKey = new(StringComparer.Ordinal);
    private ErpFile? _branding;

    /// <param name="brandingErpPath">
    /// The team-branding texarray archive, which is the game's own. A project
    /// carries the pictures that were chosen rather than the archive they go
    /// into, so what comes out of here really is what the game has in the slot.
    /// </param>
    public LogoPreviewCache(string brandingErpPath, TexconvRunner runner)
    {
        _brandingErpPath = brandingErpPath;
        _runner = runner;
        _directory = Path.Combine(Path.GetTempPath(), "simplified-grid-editor", "previews");
    }

    /// <summary>
    /// A PNG of what the game has in that slice, or null if it can't be made.
    /// The first array read loads the archive, which is the slow part.
    /// </summary>
    public string? PathFor(LogoKind kind, int slice)
    {
        var texarray = kind.Texarrays[0];
        var key = $"{texarray}.{slice}";
        if (_byKey.TryGetValue(key, out var cached)) return cached;

        string? result = null;
        try
        {
            if (File.Exists(_brandingErpPath))
            {
                _branding ??= ErpFileIO.Load(_brandingErpPath);
                result = LogoBaker.ExportSlice(_branding, texarray, slice, _directory, _runner, key + ".png");
            }
        }
        catch
        {
            // A preview is a nicety. Failing to make one leaves the slot blank
            // rather than stopping the dialog from opening.
        }

        _byKey[key] = result;
        return result;
    }
}
