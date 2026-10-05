using System.IO;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using SimplifiedTeamEditor.Core;
using SimplifiedTeamEditor.Core.Logos;
using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;

namespace SimplifiedTeamEditor;

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

    public LogoPreviewCache(string assetsDir, TexconvRunner runner)
    {
        _brandingErpPath = Path.Combine(assetsDir, "ui", "ui_texarrays_team_branding.erp");
        _runner = runner;
        _directory = Path.Combine(Path.GetTempPath(), "simplified-team-editor", "previews");
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
