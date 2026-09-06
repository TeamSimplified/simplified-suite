using System.IO;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using SimplifiedTeamEditor.Core;
using SimplifiedTeamEditor.Core.Logos;
using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;

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
    /// The team-branding texarray archive. A path rather than a folder to look
    /// in: this came from an app with one fixed bundle layout, and a project
    /// keeps the same file somewhere else.
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

                var resource = _branding.FindResource(TeamLogos.IdentifierFor(texarray));
                if (resource is not null)
                {
                    var srv = new ErpGfxSRVResource();
                    srv.FromResource(resource);

                    if (slice < srv.SurfaceRes.Fragment0.ArraySize)
                    {
                        Directory.CreateDirectory(_directory);
                        var ddsPath = Path.Combine(_directory, key + ".dds");

                        var dds = srv.ToDdsFile(null, exportTexArray: false, texArrayIndex: (uint)slice);
                        using (var file = File.Create(ddsPath)) dds.Write(file, -1);

                        _runner.DdsToPng(ddsPath, _directory,
                            isSrgb: TexconvRunner.IsSrgbFormat(srv.SurfaceRes.Fragment0.ImageType));

                        var pngPath = Path.Combine(_directory, key + ".png");
                        if (File.Exists(pngPath)) result = pngPath;
                        try { File.Delete(ddsPath); } catch { }
                    }
                }
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
