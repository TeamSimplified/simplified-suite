using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;
using EgoEngineLibrary.Texconv;

namespace SimplifiedTrackManager.Core.Track;

/// <summary>
/// The pictures a track shows in the menus and on the map, and putting a PNG
/// in place of one.
///
/// Every track package carries all of them in its archive — the eleven
/// <c>ui/images.json</c> names and the ten of <c>ui/minimap.json</c> — so a
/// picture is never added, only replaced: the PNG is encoded to the format and
/// size of the one it replaces, which is what the menus were built to take. The
/// flags, previews and splines become slices of the game's texture arrays,
/// whose sizes are fixed; the map's pictures can be any size, but the minimap
/// corners are placed against the existing one.
/// </summary>
public static class TrackPictures
{
    /// <summary>One picture: its key, where it shows, and the resource id it has in the track.</summary>
    public sealed record Slot(string Key, string Label, string Group, string ResourceId);

    /// <summary>What the menu pictures show, in the order they are offered.</summary>
    private static readonly (string Key, string Label)[] MenuPictures =
    [
        ("flag_circle", "Flag, round"),
        ("flag_rectangle", "Flag"),
        ("flag_rectangle_small", "Flag, small"),
        ("preview_small", "Track card"),
        ("preview_background", "Track card background"),
        ("preview_scene_1", "Track select, picture 1"),
        ("preview_scene_2", "Track select, picture 2"),
        ("preview_scene_3", "Track select, picture 3"),
        ("session_image", "Session picture"),
        ("spline", "Layout"),
        ("spline_large", "Layout, large"),
    ];

    private static readonly (string Key, string Label)[] MapPictures =
    [
        ("minimap", "Minimap"),
        ("minimap_sector_overlay", "Minimap sectors"),
        ("full_track_map", "Full map"),
        ("full_track_map_sector_overlay", "Full map sectors"),
        ("monitor", "Car monitor map"),
    ];

    /// <summary>
    /// The surface header field at offset 36 that the stock texture array a
    /// picture becomes a slice of carries, where it is not 0. Swofty takes a
    /// picture into an array only when this matches; the rectangular flags'
    /// array is the one stock array with a value there.
    /// </summary>
    private static readonly Dictionary<string, int> ArrayHeaderExtra = new(StringComparer.Ordinal)
    {
        ["flag_rectangle"] = 8,
    };

    /// <summary>
    /// The size the menus show a picture at: the slice size of the texture
    /// array it goes into, or the size the game's own tracks give it. The map's
    /// pictures have none; they keep the size the track has them at.
    /// </summary>
    public static string? MenuSize(string key) => key switch
    {
        "flag_circle" => "28×28",
        "flag_rectangle" => "100×64",
        "flag_rectangle_small" => "40×28",
        "preview_small" => "1244×700",
        "preview_background" or "preview_scene_1" or "preview_scene_2" or "preview_scene_3" => "1920×1080",
        "session_image" => "848×356",
        "spline" => "472×236",
        "spline_large" => "504×244",
        _ => null,
    };

    /// <summary>The three flags: round, rectangular and small.</summary>
    public static bool IsFlag(string key) => key is "flag_circle" or "flag_rectangle" or "flag_rectangle_small";

    /// <summary>Every picture the zip names: the flags, the other menu pictures, then both sets of map pictures.</summary>
    public static IReadOnlyList<Slot> Slots(TrackZip zip)
    {
        var slots = new List<Slot>();
        foreach (var (key, label) in MenuPictures)
            if (zip.Images.TryGetValue(key, out var id))
                slots.Add(new Slot(key, label, IsFlag(key) ? "Flags" : "Menus", id));
        foreach (var (key, label) in MapPictures)
            if (zip.Minimap.Textures.Resources.TryGetValue(key, out var id))
                slots.Add(new Slot("textures." + key, label, "Map", id));
        foreach (var (key, label) in MapPictures)
            if (zip.Minimap.TexturesOmXm.Resources.TryGetValue(key, out var id))
                slots.Add(new Slot("textures_om_xm." + key, label, "Map, X mode", id));
        return slots;
    }

    /// <summary>
    /// The format and size the picture has in the track, which an imported PNG
    /// is encoded to.
    /// </summary>
    public static (ErpGfxSurfaceFormat Format, uint Width, uint Height) Describe(ErpFile erp, string resourceId)
    {
        var srv = Srv(erp, resourceId);
        return (srv.SurfaceRes.Fragment0.ImageType, srv.SurfaceRes.Width, srv.SurfaceRes.Height);
    }

    /// <summary>
    /// Put <paramref name="pngPath"/> in place of the picture <paramref name="slot"/>,
    /// at that picture's format and size, as a single level the way every track
    /// picture is.
    /// </summary>
    public static void Import(ErpFile erp, Slot slot, string pngPath, TexconvRunner texconv, string workDir)
    {
        var resource = erp.Resources.Find(r => r.Identifier == slot.ResourceId)
                       ?? throw new InvalidDataException($"The track has no {slot.ResourceId}.");
        var srv = new ErpGfxSRVResource();
        srv.FromResource(resource);
        var format = srv.SurfaceRes.Fragment0.ImageType;
        if (!TexconvRunner.IsSupportedForPngConversion(format))
            throw new NotSupportedException($"{slot.Label} is {format}, which a PNG can't be converted to.");
        if (srv.SurfaceRes.HasMips || srv.SurfaceRes.Fragment0.ArraySize > 1)
            throw new NotSupportedException($"{slot.Label} isn't a plain single picture, so it can't be replaced.");

        var dir = Path.Combine(workDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Copied under a plain name first: texconv names its output after
            // its input, and a user's file name can be anything.
            var input = Path.Combine(dir, "picture.png");
            File.Copy(pngPath, input);
            var ddsPath = texconv.PngToDds(input, Path.Combine(dir, "out"), format, mipCount: 1,
                                           width: srv.SurfaceRes.Width, height: srv.SurfaceRes.Height);

            using (var fs = File.OpenRead(ddsPath))
            {
                var dds = new DdsFile(fs);
                dds.ToErpGfxSRVResource(srv, null!, importTexArray: false, texArrayIndex: 0);
            }
            srv.SurfaceRes.Fragment0.Unknown5 = ArrayHeaderExtra.GetValueOrDefault(slot.Key, srv.SurfaceRes.Fragment0.Unknown5);
            srv.ToResource(resource);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Give the pictures that become texture-array slices the header their
    /// array carries. A picture left as the zip had it still needs this, or
    /// Swofty leaves it out of the menus.
    /// </summary>
    public static int MatchArrayHeaders(ErpFile erp, IEnumerable<Slot> slots)
    {
        var fixedCount = 0;
        foreach (var slot in slots)
        {
            if (!ArrayHeaderExtra.TryGetValue(slot.Key, out var extra)) continue;
            var resource = erp.Resources.Find(r => r.Identifier == slot.ResourceId + ".image");
            if (resource is null) continue;
            var surface = new ErpGfxSurfaceRes();
            surface.FromResource(resource);
            if (surface.Fragment0.Unknown5 == extra) continue;
            surface.Fragment0.Unknown5 = extra;
            surface.ToResource(resource);
            fixedCount++;
        }
        return fixedCount;
    }

    /// <summary>
    /// The zip's own pictures as PNG files in <paramref name="outDir"/>, by
    /// <see cref="Slot.Key"/>, for showing beside the rows. The archive has to
    /// be written out and read whole to reach them — a zip entry can't be read
    /// in place — so this is a background job, and the archive is let go as
    /// soon as the pictures are out. A picture that can't be decoded is left
    /// out rather than failing the rest.
    /// </summary>
    public static Dictionary<string, string> ExtractPreviews(TrackZip zip, IReadOnlyList<Slot> slots,
        TexconvRunner texconv, string outDir, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(outDir);
        var erpPath = Path.Combine(outDir, "track.erp");
        var previews = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            zip.ExtractErp(erpPath);
            cancel.ThrowIfCancellationRequested();
            var erp = EgoEngineLibrary.Util.ErpFileIO.Load(erpPath);

            foreach (var slot in slots)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var srv = Srv(erp, slot.ResourceId);
                    var dds = srv.ToDdsFile(null, exportTexArray: false, texArrayIndex: 0);
                    var ddsPath = Path.Combine(outDir, slot.Key + ".dds");
                    using (var fs = File.Create(ddsPath)) dds.Write(fs, -1);
                    texconv.DdsToPng(ddsPath, outDir, overwrite: true,
                                     isSrgb: TexconvRunner.IsSrgbFormat(srv.SurfaceRes.Fragment0.ImageType));
                    File.Delete(ddsPath);
                    var png = Path.Combine(outDir, slot.Key + ".png");
                    if (File.Exists(png)) previews[slot.Key] = png;
                }
                catch (Exception) when (!cancel.IsCancellationRequested)
                {
                    // One picture that won't decode costs only its own preview.
                }
            }
        }
        finally
        {
            try { File.Delete(erpPath); } catch { }
        }
        return previews;
    }

    private static ErpGfxSRVResource Srv(ErpFile erp, string resourceId)
    {
        var resource = erp.Resources.Find(r => r.Identifier == resourceId)
                       ?? throw new InvalidDataException($"The track has no {resourceId}.");
        var srv = new ErpGfxSRVResource();
        srv.FromResource(resource);
        return srv;
    }
}
