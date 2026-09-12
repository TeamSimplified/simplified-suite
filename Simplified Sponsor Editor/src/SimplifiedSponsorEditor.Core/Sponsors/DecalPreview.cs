using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;

namespace SimplifiedSponsorEditor.Core.Sponsors;

/// <summary>What a decal looks like in the game, and how big it is.</summary>
public sealed record DecalPicture(string PngPath, uint Width, uint Height, string Format);

/// <summary>
/// The game's own artwork for a decal, as a PNG that can be shown.
///
/// Decoded one at a time, when something asks to look at one, and kept in a temp
/// folder afterwards. The tool this replaces converted all 641 up front — a
/// texconv process each, in sequence, before the window would do anything — to
/// fill a list most of which nobody scrolls to. Here opening a project is
/// instant and the cost is paid per decal actually looked at.
/// </summary>
public sealed class DecalPreview : IDisposable
{
    private readonly string _archivePath;
    private readonly string _cacheDir;
    private readonly TexconvRunner _runner = new();
    private readonly Dictionary<string, DecalPicture> _cache = new(StringComparer.OrdinalIgnoreCase);

    private ErpFile? _archive;

    public DecalPreview(string archivePath)
    {
        _archivePath = archivePath;
        _cacheDir = Path.Combine(Path.GetTempPath(), "simplified-sponsor-editor", "preview",
                                 Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// The decal's picture, decoding it the first time it is asked for.
    /// Null when it cannot be read, which is a thing to show rather than throw.
    /// </summary>
    public DecalPicture? Get(string viewIdentifier)
    {
        if (_cache.TryGetValue(viewIdentifier, out var cached)) return cached;

        try
        {
            // Loaded on first use, not in the constructor: this is 19 MB, and a
            // project whose decals are all replaced never needs it at all.
            _archive ??= ErpFileIO.Load(_archivePath);

            var resource = _archive.FindResource(viewIdentifier);
            var srv = new ErpGfxSRVResource();
            srv.FromResource(resource);

            Directory.CreateDirectory(_cacheDir);
            var name = SponsorCatalogue.ShortName(viewIdentifier);
            var ddsPath = Path.Combine(_cacheDir, name + ".dds");

            // Stream.Null for the mip file: every decal in these archives keeps
            // its chain inline, so there is no sidecar to read.
            var dds = srv.ToDdsFile(Stream.Null, exportTexArray: false, texArrayIndex: 0);

            // -1, not 0. The argument is a cube face, and a negative one means
            // "there isn't one, write the plain image" - passing 0 sends Write
            // looking in a dictionary of faces that a decal has none of.
            using (var fs = File.Create(ddsPath)) dds.Write(fs, -1);

            var format = srv.SurfaceRes.Fragment0.ImageType;
            _runner.DdsToPng(ddsPath, _cacheDir, overwrite: true,
                             isSrgb: TexconvRunner.IsSrgbFormat(format));
            File.Delete(ddsPath);

            var pngPath = Path.Combine(_cacheDir, name + ".png");
            if (!File.Exists(pngPath)) return null;

            var picture = new DecalPicture(pngPath,
                srv.SurfaceRes.Fragment0.Width,
                srv.SurfaceRes.Fragment0.Height,
                format.ToString());

            _cache[viewIdentifier] = picture;
            return picture;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _archive = null;
        try { if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true); } catch { }
    }
}
