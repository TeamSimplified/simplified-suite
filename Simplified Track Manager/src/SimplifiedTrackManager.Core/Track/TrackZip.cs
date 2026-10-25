using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimplifiedTrackManager.Core.Track;

/// <summary>
/// A track as it is handed out: the zip the EgoGameDatabasesEditor installs from,
/// which Swofty's track packages share.
///
/// <code>
/// manifest.json      code, name, author, version, base track, overrides, ERS, where the rest is
/// &lt;code&gt;.erp        the track itself, every resource it needs under its own id
/// ui/images.json     the eleven menu pictures, by resource id inside that ERP
/// ui/minimap.json    the minimap's rotation, corners and textures
/// mipmaps/           external mip files, named md5(original path)[..12]_name
/// </code>
///
/// Everything is read from the manifest rather than assumed: the ERP's name, the
/// two UI files and the mip folders are all named there.
///
/// A Swofty addon (<c>.swoftyaddon</c>) of a track is read too, this app's own
/// or anyone's. It is a zip as well, with the track package in the folder its
/// <c>track.add</c> op names, so every path above is read inside that folder.
/// What the package's manifest no longer holds comes from the addon's ops: its
/// <c>set</c> ops on this track's rows are read back as the overrides they would
/// have been, and its <c>lang.set</c> ops as <see cref="Names"/>.
/// </summary>
public sealed partial class TrackZip : IDisposable
{
    private readonly ZipArchive _zip;

    /// <summary>The track package's folder in the zip, with its slash; empty for a track zip.</summary>
    private readonly string _root;

    /// <summary>Whether this is a Swofty addon rather than a track zip.</summary>
    public bool IsAddon { get; }

    /// <summary>
    /// An addon's names, by language key: its <c>lang.set</c> ops, the ones
    /// for every language (<c>*</c>) or else English. Empty for a track zip,
    /// which carries no text.
    /// </summary>
    public IReadOnlyDictionary<string, string> Names { get; } = new Dictionary<string, string>();

    /// <summary>
    /// An addon's database ops that aren't a <c>set</c> on this track's rows,
    /// as they are written. They can't be carried over, so the app says so.
    /// </summary>
    public IReadOnlyList<string> Unported { get; } = [];

    /// <summary>
    /// What the track says about itself: its manifest's description, which
    /// Swofty shows on the track's tile, else an addon's own. Empty when it
    /// says nothing.
    /// </summary>
    public string Description { get; } = "";

    /// <summary>An addon's README.md, as written; empty for a track zip, or an addon without one.</summary>
    public string Readme { get; } = "";

    public string ZipPath { get; }

    /// <summary>The code the track has in the zip, e.g. <c>melbourne24</c>.</summary>
    public string Code { get; }

    /// <summary>What it is called, e.g. <c>Istanbul Park</c>.</summary>
    public string Name { get; }

    public string Author { get; }
    public string Version { get; }

    /// <summary>The stock track it is built on, whose database rows activation clones.</summary>
    public string? CopyFromTrack { get; }

    /// <summary>The track whose Track row it takes, when the manifest names one apart from the base.</summary>
    public string? CopySlot { get; }

    /// <summary>
    /// The ERS values, in the manifest's order and its own spelling. Kept as
    /// the JSON wrote them — <c>9000000</c> stays <c>9000000</c> rather than
    /// coming back as <c>9E+06</c> — because they are written into the handling
    /// files as text.
    /// </summary>
    public IReadOnlyList<(string Key, string Value)> Ers { get; }

    /// <summary>The author's changes to the cloned TrackData rows (<c>activation.overrides</c>).</summary>
    public IReadOnlyList<TrackOverride> Overrides { get; }

    /// <summary>The author's changes to the cloned Track row (<c>activation.track_overrides</c>).</summary>
    public IReadOnlyList<TrackOverride> TrackOverrides { get; }

    /// <summary>The menu pictures, by role (<c>flag_circle</c>, <c>spline</c>…), as resource ids.</summary>
    public IReadOnlyDictionary<string, string> Images { get; }

    public MinimapInfo Minimap { get; }

    /// <summary>The two UI files' names in the zip, as the manifest gives them.</summary>
    public string ImagesFile { get; }
    public string MinimapFile { get; }

    /// <summary>The folders of mip files the manifest copies, each with its source and destination.</summary>
    public IReadOnlyList<(string Source, string Destination)> CopyDirectories { get; }

    /// <summary>The ERP's entry in the zip.</summary>
    public ZipArchiveEntry Erp { get; }

    /// <summary>Every file in the copied folders, by its path in the zip.</summary>
    public IReadOnlyDictionary<string, ZipArchiveEntry> Mipmaps { get; }

    private TrackZip(string path, ZipArchive zip)
    {
        ZipPath = path;
        _zip = zip;

        var addonDescription = "";

        // An addon: the package is where its track.add says, and the rest of
        // what it does is read from its op files.
        var addonDb = new List<JsonElement>();
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        var english = new Dictionary<string, string>(StringComparer.Ordinal);
        var documents = new List<JsonDocument>();
        _root = "";
        if (FindEntry("swofty.json") is not null)
        {
            IsAddon = true;
            var swofty = ReadJson("swofty.json");
            documents.Add(swofty);
            addonDescription = Text(swofty.RootElement, "description") ?? "";
            if (FindEntry("README.md") is { } readme)
                using (var reader = new StreamReader(readme.Open())) Readme = reader.ReadToEnd();
            var operations = swofty.RootElement.TryGetProperty("operations", out var o) ? o : default;

            var source = "track";
            foreach (var op in Ops(operations, "assets", documents))
            {
                switch (Text(op, "op"))
                {
                    case "track.add" when Text(op, "source") is { Length: > 0 } s:
                        source = s.Replace('\\', '/').Trim('/');
                        break;
                    // Database changes can sit among the assets too, as db.set:
                    // Swofty runs an addon's ops whichever file they're in.
                    case "set" or "db.set":
                        addonDb.Add(op);
                        break;
                    case "lang.set" when Text(op, "key") is { } key && Text(op, "value") is { } value:
                        var language = Text(op, "language") ?? "*";
                        if (language == "*") text[key] = value;
                        else if (language.Equals("english", StringComparison.OrdinalIgnoreCase)
                                 || language.Equals("en", StringComparison.OrdinalIgnoreCase)) english[key] = value;
                        break;
                }
            }
            foreach (var (key, value) in english) text.TryAdd(key, value);
            addonDb.AddRange(Ops(operations, "database", documents));

            _root = source.Length > 0 ? source + "/" : "";
            if (Entry("manifest.json") is null)
                throw new InvalidDataException($"The addon has no {_root}manifest.json, so it isn't a track addon.");
        }
        Names = text;

        using var manifest = ReadJson("manifest.json");
        var m = manifest.RootElement;

        Code = Text(m, "track_code") ?? throw new InvalidDataException("The manifest has no track_code.");
        Name = Text(m, "track_name") ?? Code;
        Author = Text(m, "author_name") ?? "";
        Version = Text(m, "version") ?? "";
        Description = Text(m, "description") is { Length: > 0 } own ? own : addonDescription;

        var activation = m.TryGetProperty("activation", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        CopyFromTrack = activation.ValueKind == JsonValueKind.Object ? Text(activation, "copy_from_track") : null;
        CopySlot = activation.ValueKind == JsonValueKind.Object ? Text(activation, "copy_slot") : null;
        var overrides = ReadOverrides(activation, "overrides").ToList();
        var trackOverrides = ReadOverrides(activation, "track_overrides").ToList();
        var unported = new List<string>();
        foreach (var op in addonDb)
        {
            var select = Text(op, "select") ?? "";
            var field = Text(op, "field");
            var value = op.TryGetProperty("value", out var v)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
                : null;
            if (Text(op, "op") is "set" or "db.set" && field is not null && value is not null)
            {
                if (TrackRowSelect().Match(select) is { Success: true } row && row.Groups[1].Value == Code)
                {
                    trackOverrides.Add(new TrackOverride(field, value, []));
                    continue;
                }
                if (TrackDataSelect().Match(select) is { Success: true } data && data.Groups[2].Value == Code)
                {
                    overrides.Add(new TrackOverride(field, value, [data.Groups[1].Value]));
                    continue;
                }
            }
            unported.Add(op.GetRawText());
        }
        Overrides = overrides;
        TrackOverrides = trackOverrides;
        Unported = unported;
        foreach (var document in documents) document.Dispose();

        var erpName = Text(m, "erp") ?? Code + ".erp";
        Erp = Entry(erpName) ?? throw new InvalidDataException($"The zip has no {erpName}, which its manifest names as the track.");

        Ers = m.TryGetProperty("ers", out var ers) && ers.ValueKind == JsonValueKind.Object
            ? ers.EnumerateObject().Select(p => (p.Name, p.Value.GetRawText().Trim('"'))).ToList()
            : [];

        var ui = m.TryGetProperty("ui", out var u) ? u : default;
        ImagesFile = (ui.ValueKind == JsonValueKind.Object ? Text(ui, "images_file") : null) ?? "ui/images.json";
        MinimapFile = (ui.ValueKind == JsonValueKind.Object ? Text(ui, "minimap_file") : null) ?? "ui/minimap.json";

        using (var images = ReadJson(ImagesFile))
        {
            Images = images.RootElement.GetProperty("resources").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        }

        using (var minimap = ReadJson(MinimapFile))
        {
            Minimap = MinimapInfo.From(minimap.RootElement);
        }

        CopyDirectories = m.TryGetProperty("copy_directories", out var copy) && copy.ValueKind == JsonValueKind.Array
            ? copy.EnumerateArray()
                .Select(c => (Source: Text(c, "source") ?? "", Destination: Text(c, "destination") ?? ""))
                .Where(c => c.Source.Length > 0)
                .ToList()
            : [("mipmaps/", "")];

        // By their path in the package, which is the zip's own for a track zip.
        var mips = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue; // a folder
            var full = entry.FullName.Replace('\\', '/');
            if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) continue;
            var name = full[_root.Length..];
            if (CopyDirectories.Any(c => name.StartsWith(c.Source.TrimStart('/'), StringComparison.OrdinalIgnoreCase)))
                mips.TryAdd(name, entry);
        }
        Mipmaps = mips;
    }

    /// <summary>
    /// The one Swofty addon a zip holds and nothing else of a track, as an
    /// author may hand one out — <c>Circuit_Paul_Ricard_1.0.0.zip</c> holding
    /// <c>twilight.paul-ricard.swoftyaddon</c> — or null. An archive inside an
    /// archive can't be read in place, so it has to be taken out first
    /// (<see cref="ExtractWrapped"/>).
    /// </summary>
    public static string? WrappedAddon(string path)
    {
        if (path.EndsWith(".swoftyaddon", StringComparison.OrdinalIgnoreCase)) return null;
        using var zip = ZipFile.OpenRead(path);
        if (zip.GetEntry("manifest.json") is not null || zip.GetEntry("swofty.json") is not null) return null;
        var addons = zip.Entries.Where(e => e.FullName.EndsWith(".swoftyaddon", StringComparison.OrdinalIgnoreCase)).ToList();
        return addons.Count == 1 ? addons[0].FullName : null;
    }

    /// <summary>Take <paramref name="entry"/> out of the zip at <paramref name="path"/> into <paramref name="dir"/>, and say where.</summary>
    public static string ExtractWrapped(string path, string entry, string dir)
    {
        Directory.CreateDirectory(dir);
        var to = Path.Combine(dir, Path.GetFileName(entry));
        using var zip = ZipFile.OpenRead(path);
        zip.GetEntry(entry)!.ExtractToFile(to, overwrite: true);
        return to;
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
    public void ExtractErp(string path, Action<double>? done = null)
    {
        using var source = Erp.Open();
        using var file = File.Create(path);
        var buffer = new byte[1 << 20];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            file.Write(buffer, 0, read);
            copied += read;
            if (Erp.Length > 0) done?.Invoke((double)copied / Erp.Length);
        }
    }

    /// <summary>A text file of the track package, such as one of the two UI files.</summary>
    public string ReadText(string name)
    {
        var entry = Entry(name) ?? throw new InvalidDataException($"The zip has no {name}.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>A file of the track package, by its path in the package.</summary>
    private ZipArchiveEntry? Entry(string name) => FindEntry(_root + name.TrimStart('/'));

    /// <summary>A file by its path in the zip.</summary>
    private ZipArchiveEntry? FindEntry(string name) =>
        _zip.GetEntry(name) ?? _zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));

    private JsonDocument ReadJson(string name)
    {
        var entry = (name == "swofty.json" && _root.Length == 0 ? FindEntry(name) : Entry(name))
                    ?? throw new InvalidDataException($"The zip has no {_root}{name}.");
        using var stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    /// <summary>
    /// The ops of every op file an addon lists under <paramref name="kind"/>,
    /// in order. The documents stay open, in <paramref name="documents"/>,
    /// for as long as the ops are read.
    /// </summary>
    private IEnumerable<JsonElement> Ops(JsonElement operations, string kind, List<JsonDocument> documents)
    {
        if (operations.ValueKind != JsonValueKind.Object
            || !operations.TryGetProperty(kind, out var files) || files.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.String || FindEntry(file.GetString()!.Replace('\\', '/').TrimStart('/')) is not { } entry)
                continue;
            JsonDocument document;
            using (var stream = entry.Open()) document = JsonDocument.Parse(stream);
            documents.Add(document);
            if (document.RootElement.TryGetProperty("ops", out var ops) && ops.ValueKind == JsonValueKind.Array)
                foreach (var op in ops.EnumerateArray()) yield return op;
        }
    }

    [GeneratedRegex(@"^tracks\.Track\[_metadata_=([^\]]+)\]$")]
    private static partial Regex TrackRowSelect();

    [GeneratedRegex(@"^track_data\.game_setup\.TrackDataSet\[_metadata_=([^\]]+)\]\.TrackData\[_metadata_=([^\]]+)\]$")]
    private static partial Regex TrackDataSelect();

    private static IReadOnlyList<TrackOverride> ReadOverrides(JsonElement activation, string name)
    {
        if (activation.ValueKind != JsonValueKind.Object
            || !activation.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            return [];

        return list.EnumerateArray()
            .Where(o => Text(o, "path") is not null && o.TryGetProperty("value", out _))
            .Select(o => new TrackOverride(
                Text(o, "path")!,
                o.GetProperty("value") is { ValueKind: JsonValueKind.String } s ? s.GetString()! : o.GetProperty("value").GetRawText(),
                o.TryGetProperty("datasets", out var d) && d.ValueKind == JsonValueKind.Array
                    ? d.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : []))
            .ToList();
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose() => _zip.Dispose();
}

/// <summary>One manifest override: a field path on a cloned row, its value, and the datasets it is limited to.</summary>
public sealed record TrackOverride(string Path, string Value, IReadOnlyList<string> Datasets);

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
