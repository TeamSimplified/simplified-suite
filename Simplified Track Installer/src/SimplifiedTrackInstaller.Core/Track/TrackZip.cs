using System.IO.Compression;
using System.Text.Json;

namespace SimplifiedTrackInstaller.Core.Track;

/// <summary>
/// A track as it is handed out: the zip the EgoGameDatabasesEditor installs from.
///
/// <code>
/// manifest.json      code, name, author, version, ERS values, where the rest is
/// &lt;code&gt;.erp        the track itself, every resource it needs under its own id
/// ui/images.json     the eleven menu pictures, by resource id inside that ERP
/// ui/minimap.json    the minimap's rotation, corners and textures
/// mipmaps/           external mip files, named md5(original path)[..12]_name
/// </code>
///
/// Everything is read from the manifest rather than assumed: the ERP's name, the
/// two UI files and the mip folders are all named there.
/// </summary>
public sealed class TrackZip : IDisposable
{
    private readonly ZipArchive _zip;

    public string ZipPath { get; }

    /// <summary>The slot the track takes, e.g. <c>melbourne24</c>.</summary>
    public string Code { get; }

    /// <summary>What it is called, e.g. <c>Istanbul Park</c>.</summary>
    public string Name { get; }

    public string Author { get; }
    public string Version { get; }

    /// <summary>The track it is built on, whose settings it starts from.</summary>
    public string? CopyFromTrack { get; }

    /// <summary>
    /// The ERS values, in the manifest's order and its own spelling. Kept as
    /// the JSON wrote them — <c>9000000</c> stays <c>9000000</c> rather than
    /// coming back as <c>9E+06</c> — because they are written into the handling
    /// files as text.
    /// </summary>
    public IReadOnlyList<(string Key, string Value)> Ers { get; }

    /// <summary>The menu pictures, by role (<c>flag_circle</c>, <c>spline</c>…), as resource ids.</summary>
    public IReadOnlyDictionary<string, string> Images { get; }

    public MinimapInfo Minimap { get; }

    /// <summary>The ERP's entry in the zip.</summary>
    public ZipArchiveEntry Erp { get; }

    /// <summary>Every external mip file, by its file name.</summary>
    public IReadOnlyDictionary<string, ZipArchiveEntry> Mipmaps { get; }

    private TrackZip(string path, ZipArchive zip)
    {
        ZipPath = path;
        _zip = zip;

        using var manifest = ReadJson("manifest.json");
        var m = manifest.RootElement;

        Code = Text(m, "track_code") ?? throw new InvalidDataException("The manifest has no track_code.");
        Name = Text(m, "track_name") ?? Code;
        Author = Text(m, "author_name") ?? "";
        Version = Text(m, "version") ?? "";
        CopyFromTrack = m.TryGetProperty("activation", out var activation) ? Text(activation, "copy_from_track") : null;

        var erpName = Text(m, "erp") ?? Code + ".erp";
        Erp = Entry(erpName) ?? throw new InvalidDataException($"The zip has no {erpName}, which its manifest names as the track.");

        Ers = m.TryGetProperty("ers", out var ers) && ers.ValueKind == JsonValueKind.Object
            ? ers.EnumerateObject().Select(p => (p.Name, p.Value.GetRawText().Trim('"'))).ToList()
            : [];

        var ui = m.TryGetProperty("ui", out var u) ? u : default;
        var imagesFile = ui.ValueKind == JsonValueKind.Object ? Text(ui, "images_file") : null;
        var minimapFile = ui.ValueKind == JsonValueKind.Object ? Text(ui, "minimap_file") : null;

        using (var images = ReadJson(imagesFile ?? "ui/images.json"))
        {
            Images = images.RootElement.GetProperty("resources").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        }

        using (var minimap = ReadJson(minimapFile ?? "ui/minimap.json"))
        {
            Minimap = MinimapInfo.From(minimap.RootElement);
        }

        // The folders the manifest says to copy. Only their files matter here:
        // where the editor put them is not where this puts them.
        var sources = m.TryGetProperty("copy_directories", out var copy) && copy.ValueKind == JsonValueKind.Array
            ? copy.EnumerateArray().Select(c => Text(c, "source")).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList()
            : ["mipmaps/"];

        var mips = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue; // a folder
            var name = entry.FullName.Replace('\\', '/');
            if (sources.Any(s => name.StartsWith(s.TrimStart('/'), StringComparison.OrdinalIgnoreCase)))
                mips.TryAdd(entry.Name, entry);
        }
        Mipmaps = mips;
    }

    public static TrackZip Open(string path)
    {
        var zip = ZipFile.OpenRead(path);
        try
        {
            return new TrackZip(path, zip);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The ERP, written out to <paramref name="path"/>. It has to be a file: an
    /// archive is read by seeking to each fragment, and a zip entry cannot seek.
    /// </summary>
    public void ExtractErp(string path)
    {
        using var source = Erp.Open();
        using var file = File.Create(path);
        source.CopyTo(file);
    }

    private ZipArchiveEntry? Entry(string name) =>
        _zip.GetEntry(name) ?? _zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));

    private JsonDocument ReadJson(string name)
    {
        var entry = Entry(name) ?? throw new InvalidDataException($"The zip has no {name}.");
        using var stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose() => _zip.Dispose();
}

/// <summary>What <c>ui/minimap.json</c> says about the track's map.</summary>
public sealed record MinimapInfo(
    double Dimensions, double Padding, double Rotation, double ZoomFactor,
    MinimapTextures Textures, MinimapTextures TexturesOmXm)
{
    public static MinimapInfo From(JsonElement e) => new(
        Number(e, "dimensions", 1024), Number(e, "padding", 20),
        Number(e, "rotation", 0), Number(e, "zoom_factor", 1),
        MinimapTextures.From(e.GetProperty("textures")),
        MinimapTextures.From(e.TryGetProperty("textures_om_xm", out var om) ? om : e.GetProperty("textures")));

    private static double Number(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
}

/// <summary>One set of map textures: the four corners in track space, and the pictures.</summary>
public sealed record MinimapTextures(
    IReadOnlyDictionary<string, (double X, double Y)> Corners,
    IReadOnlyDictionary<string, string> Resources)
{
    public static MinimapTextures From(JsonElement e) => new(
        e.GetProperty("corners").EnumerateObject().ToDictionary(
            p => p.Name, p => (p.Value[0].GetDouble(), p.Value[1].GetDouble()), StringComparer.Ordinal),
        e.GetProperty("resources").EnumerateObject().ToDictionary(
            p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal));
}
