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
    private readonly Dictionary<string, (uint Width, uint Height)?> _sizes = new(StringComparer.Ordinal);
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
    /// The size a picture put into one of this kind's slots ends up at, which is
    /// the array's own — every slice of one shares it.
    ///
    /// Read from the archive rather than measured off the preview PNG: a preview
    /// that could not be made would otherwise take the answer with it, and this
    /// is wanted precisely when telling somebody their picture is a different
    /// size.
    /// </summary>
    public (uint Width, uint Height)? SizeFor(LogoKind kind)
    {
        var texarray = kind.Texarrays[0];
        if (_sizes.TryGetValue(texarray, out var cached)) return cached;

        (uint Width, uint Height)? result = null;
        try
        {
            if (File.Exists(_brandingErpPath))
            {
                _branding ??= ErpFileIO.Load(_brandingErpPath);
                result = LogoBaker.SliceSize(_branding, texarray);
            }
        }
        catch
        {
            // As with a preview: not knowing leaves the line saying less, rather
            // than stopping the page from opening.
        }

        _sizes[texarray] = result;
        return result;
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
