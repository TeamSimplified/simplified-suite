using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;

namespace SimplifiedTrackManager.Core.Track;

public sealed record AddonResult(string OutputPath, string Code, int SetOps, int LanguageKeys, int Pictures,
                                 IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a track zip into a Swofty addon of a track with its own code and name.
/// <code>
/// swofty.json          the addon: identity, category "tracks", the op files
/// assets/track.json    track.add, which activates the track from track/
/// db/track.json        every database change, as set ops
/// assets/text.json     every name, as lang.set ops for every language
/// track/               the track package, its manifest the zip's with the overrides moved out
///   manifest.json
///   &lt;code&gt;.erp
///   ui/images.json, ui/minimap.json
///   mipmaps/…
/// </code>
///
/// <c>manifest.json</c> stays because <c>track.add</c> reads it: activation is
/// what builds the track's archive, asset groups, menus, minimap and ERS. What
/// activation does not need moves out of it — the author's overrides become
/// <c>set</c> ops, which run on the rows activation cloned, so the result is the
/// same. A field the base track lacks is added by its <c>set</c>
/// (<c>create</c>, typed as the game's other tracks store it), since a plain
/// <c>set</c> on a field the copy lacks fails the build. Only a field no track
/// of the game has stays in the manifest, where activation copies it.
/// </summary>
public static class AddonBuilder
{
    /// <summary>
    /// Indented, and with text written as it is — <c>Apoc &amp; RK</c>, <c>&gt;=1.3.0</c>
    /// — since these are files people read; the default escapes both.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AddonResult Build(string zipPath, TrackSettings settings, StockGame game, string outputPath,
                                    Action<string>? log = null, IProgress<string>? say = null,
                                    IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (settings.Problems(game) is { Count: > 0 } problems)
            throw new InvalidOperationException(string.Join("\n", problems));

        var reached = 0.0;
        void Step(double from, double to, double fraction)
        {
            var at = from + (to - from) * Math.Clamp(fraction, 0, 1);
            if (at <= reached) return;
            reached = at;
            progress?.Report(at);
        }

        var warnings = new List<string>();
        void Warn(string text)
        {
            warnings.Add(text);
            log?.Invoke("  ⚠ " + text);
        }

        var code = settings.Code.Trim();
        var work = Path.Combine(Path.GetTempPath(), "simplified-track-manager", "addon", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            using var zip = TrackZip.Open(zipPath);
            var baseTrack = zip.CopyFromTrack ?? zip.CopySlot
                ?? throw new InvalidDataException("The zip's manifest names no base track (activation.copy_from_track).");
            var slotTrack = zip.CopySlot ?? baseTrack;
            if (!game.IsStockTrack(baseTrack)) throw new InvalidDataException($"The base track {baseTrack} isn't one the game has.");
            log?.Invoke($"{zip.Name} ({zip.Code}) built on {baseTrack} → {settings.Name} ({code})");

            // ── The track, under its own code ──────────────────────────────
            say?.Report("Reading the track…");
            var erpPath = Path.Combine(work, "track.erp");
            zip.ExtractErp(erpPath, f => Step(0.00, 0.08, f));
            var erp = ErpFileIO.Load(erpPath);
            log?.Invoke($"  {erp.Resources.Count:N0} resources");
            Step(0.08, 0.12, 1);
            cancel.ThrowIfCancellationRequested();

            say?.Report("Giving the track its own code…");
            TrackRecode.Apply(erp, zip.Code, code, log);
            if (zip.Code != code && TrackRecode.Remaining(erp, zip.Code) is > 0 and var left)
                throw new InvalidDataException($"{left} resource name(s) still hold {zip.Code} after the rename.");
            Step(0.12, 0.25, 1);
            cancel.ThrowIfCancellationRequested();

            // ── Pictures ───────────────────────────────────────────────────
            say?.Report("Converting the pictures…");
            var slots = TrackPictures.Slots(zip)
                .Select(s => s with { ResourceId = TrackRecode.RecodeText(s.ResourceId, zip.Code, code) })
                .ToList();
            var texconv = new TexconvRunner();
            var pictures = 0;
            foreach (var slot in slots)
            {
                if (!settings.Pictures.TryGetValue(slot.Key, out var png) || string.IsNullOrWhiteSpace(png)) continue;
                cancel.ThrowIfCancellationRequested();
                TrackPictures.Import(erp, slot, png, texconv, work);
                var (format, w, h) = TrackPictures.Describe(erp, slot.ResourceId);
                log?.Invoke($"  {slot.Group}: {slot.Label} from {Path.GetFileName(png)} ({w}×{h} {format})");
                pictures++;
            }
            if (TrackPictures.MatchArrayHeaders(erp, slots) is > 0 and var headers)
                log?.Invoke($"  {headers} picture header(s) matched to the menu arrays they go into");
            Step(0.25, 0.30, 1);
            cancel.ThrowIfCancellationRequested();

            // ── Staging: the track package ─────────────────────────────────
            var root = Path.Combine(work, "addon");
            var trackDir = Path.Combine(root, "track");
            Directory.CreateDirectory(trackDir);

            say?.Report("Writing the track…");
            ErpFileIO.Save(erp, Path.Combine(trackDir, code + ".erp"));
            erp = null;
            try { File.Delete(erpPath); } catch { }
            Step(0.30, 0.40, 1);

            foreach (var file in new[] { zip.ImagesFile, zip.MinimapFile })
            {
                var to = Path.Combine(trackDir, file.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.WriteAllText(to, TrackRecode.RecodeText(zip.ReadText(file), zip.Code, code));
            }

            // ── The database: the author's overrides, then the user's choices ──
            var datasets = game.DatasetsWith(baseTrack);
            var author = AuthorChanges(zip.TrackOverrides, zip.Overrides, baseTrack, slotTrack, code, settings, game);
            var ops = author.Ops;
            var kept = author.Kept;
            var keptTrack = author.KeptTrack;
            var trackRow = TrackRow(code);
            if (author.Created > 0) log?.Invoke($"  {author.Created} of the author's changes add fields {baseTrack} lacks");
            var authorOps = ops.Count;

            // A Track row field the base lacks means a choice that won't show,
            // which is worth a warning. A TrackData field missing from some
            // datasets is the game's own shape — names live in a few of them.
            foreach (var (field, value) in settings.TrackRowFields())
            {
                if (game.TrackHas(slotTrack, field)) ops.Add(SetOp(trackRow, field, value));
                else Warn($"{slotTrack}'s Track row has no {field}, so that one is left as the base track has it.");
            }
            var named = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var ds in datasets)
                foreach (var (field, value) in settings.TrackDataFields(ds))
                    if (game.TrackDataHas(ds, baseTrack, field))
                    {
                        ops.Add(SetOp(DataRow(code, ds), field, value));
                        named.Add(ds);
                    }
            // The race weekend, conditions, character and location the form changed.
            foreach (var t in author.Tuning)
            {
                if (t.Dataset is { } ds) ops.Add(SetOp(DataRow(code, ds), t.Field, t.Value));
                else if (game.TrackHas(slotTrack, t.Field)) ops.Add(SetOp(trackRow, t.Field, t.Value));
                else if (game.TrackFieldTag(t.Field) is { } tag) ops.Add(CreateOp(trackRow, t.Field, t.Value, tag));
                else Warn($"{slotTrack}'s Track row has no {t.Field}, so that one is left as the base track has it.");
            }

            log?.Invoke($"  database: {authorOps} of the author's changes and {ops.Count - authorOps} of yours as set ops; " +
                        $"names in {named.Count} of the {datasets.Count} dataset(s) with {baseTrack}: {string.Join(", ", named)}");
            if (kept.Count + keptTrack.Count > 0)
                Warn($"{kept.Count + keptTrack.Count} of the author's changes name fields no track of the game has, " +
                     "so they stay in the track's manifest, where activation copies them.");

            WriteJson(Path.Combine(root, "db", "track.json"), new JsonObject { ["schema_version"] = 1, ["ops"] = ops });

            // ── Names, in every language ───────────────────────────────────
            var keys = settings.LanguageKeys();
            WriteJson(Path.Combine(root, "assets", "text.json"), new JsonObject
            {
                ["schema_version"] = 1,
                ["ops"] = new JsonArray(keys.Select(k => (JsonNode)new JsonObject
                {
                    ["op"] = "lang.set", ["language"] = "*", ["key"] = k.Key, ["value"] = k.Value,
                }).ToArray()),
            });
            WriteJson(Path.Combine(root, "assets", "track.json"), new JsonObject
            {
                ["schema_version"] = 1,
                ["ops"] = new JsonArray(new JsonObject { ["op"] = "track.add", ["source"] = "track" }),
            });

            // ── The track's manifest ───────────────────────────────────────
            // The zip's own, with only what this changes set over it: the
            // code and names, the overrides that moved to set ops, the ERS
            // and the paths that hold the code. Every other field — a
            // description, which Swofty shows on the track's in-game tile, or
            // anything a newer format adds — goes over as the zip had it.
            var version = settings.Version.Trim();
            var manifest = JsonNode.Parse(zip.ReadText("manifest.json"))?.AsObject()
                           ?? throw new InvalidDataException("The zip's manifest.json is empty.");

            var activation = manifest["activation"] as JsonObject ?? new JsonObject();
            activation.Remove("overrides");
            activation.Remove("track_overrides");
            if (kept.Count > 0) activation["overrides"] = Overrides(kept);
            if (keptTrack.Count > 0) activation["track_overrides"] = Overrides(keptTrack);

            var ers = new JsonObject();
            foreach (var (key, value) in settings.Ers) ers[key.Trim()] = JsonNode.Parse(Number(value));

            var ui = manifest["ui"] as JsonObject ?? new JsonObject();
            ui["schema_version"] ??= 1;
            ui["images_file"] = zip.ImagesFile;
            ui["minimap_file"] = zip.MinimapFile;

            if (settings.Description.Trim().Length > 0) manifest["description"] = settings.Description.Trim();
            manifest["track_code"] = code;
            manifest["track_name"] = settings.Name.Trim();
            manifest["version"] = version;
            manifest["author_name"] = settings.Author.Trim();
            manifest["erp"] = code + ".erp";
            manifest["activation"] = activation.DeepClone();
            manifest["ers"] = ers;
            manifest["copy_directories"] = new JsonArray(zip.CopyDirectories.Select(c => (JsonNode)new JsonObject
            {
                ["source"] = c.Source,
                ["destination"] = c.Destination.Length > 0 ? TrackRecode.RecodeText(c.Destination, zip.Code, code)
                                                           : $"asset_groups/environment_package/tracks/{code}/mipmaps/",
            }).ToArray());
            manifest["ui"] = ui.DeepClone();
            WriteJson(Path.Combine(trackDir, "manifest.json"), manifest);

            // ── The addon ──────────────────────────────────────────────────
            var by = settings.Author.Trim().Length > 0 ? $" Track by {settings.Author.Trim()}." : "";
            var description = settings.Description.Trim().Length > 0 ? settings.Description.Trim()
                : $"{settings.Name.Trim()} as track {code}, with its own names.{by}";
            if (description.Length > 255) description = description[..252] + "...";
            if (settings.Readme.Trim().Length > 0)
                File.WriteAllText(Path.Combine(root, "README.md"), settings.Readme.Replace("\r\n", "\n").TrimEnd() + "\n");

            WriteJson(Path.Combine(root, "swofty.json"), new JsonObject
            {
                ["schema_version"] = 1,
                ["id"] = AddonId(code),
                ["name"] = settings.Name.Trim(),
                ["version"] = version,
                ["author"] = settings.Author.Trim().Length > 0 ? settings.Author.Trim() : "Unknown",
                ["description"] = description,
                ["kind"] = new JsonArray("database", "text"),
                ["category"] = "tracks",
                ["compat"] = new JsonObject { ["game"] = "f1_25", ["launcher"] = ">=1.3.0" },
                ["operations"] = new JsonObject
                {
                    ["database"] = new JsonArray("db/track.json"),
                    ["assets"] = new JsonArray("assets/track.json", "assets/text.json"),
                },
            });

            // ── One .swoftyaddon: the staged folder, then the mip files straight from the zip ──
            say?.Report("Writing the addon…");
            if (Path.GetDirectoryName(outputPath) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
            if (File.Exists(outputPath)) File.Delete(outputPath);

            var staged = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
            var total = staged.Sum(f => new FileInfo(f).Length) + zip.Mipmaps.Values.Sum(e => e.Length);
            long written = 0;
            void Moved(long n)
            {
                written += n;
                Step(0.40, 1.00, total > 0 ? (double)written / total : 1);
            }

            using (var output = File.Create(outputPath))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var file in staged)
                {
                    cancel.ThrowIfCancellationRequested();
                    var name = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                    using var source = File.OpenRead(file);
                    Store(archive, name, source, Moved);
                }
                foreach (var (name, entry) in zip.Mipmaps)
                {
                    cancel.ThrowIfCancellationRequested();
                    using var source = entry.Open();
                    Store(archive, "track/" + name, source, Moved);
                }
            }
            log?.Invoke($"  {zip.Mipmaps.Count} mip file(s); {new FileInfo(outputPath).Length / 1048576.0:N0} MB written");
            Step(0.40, 1.00, 1);

            return new AddonResult(outputPath, code, ops.Count, keys.Count, pictures, warnings);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// <summary>The addon's id: <c>track.&lt;code&gt;</c>, a dotted id Swofty takes for any code.</summary>
    public static string AddonId(string code) => "track." + code.Trim().ToLowerInvariant();

    /// <summary>
    /// The author's overrides, as the addon carries them: <c>set</c> ops for the
    /// fields the base track has, <c>set</c> ops that add the field (typed as the
    /// game's other tracks store it) for those it lacks, and, kept apart, the
    /// few no track of the game has, which stay in the manifest. A path like
    /// <c>Session Start Times_*.p1_start_time</c> becomes a set on each numbered
    /// field the base track has. A field the form sets is left out, since the
    /// form's value replaces it — which also hands back the form's own
    /// changes beyond the names (<see cref="TrackTuning"/>) as
    /// <see cref="AuthorChangeSet.Tuning"/>.
    /// </summary>
    public sealed record AuthorChangeSet(JsonArray Ops, List<TrackOverride> Kept, List<TrackOverride> KeptTrack, int Created,
                                         IReadOnlyList<TrackTuning.Op> Tuning);

    public static AuthorChangeSet AuthorChanges(IReadOnlyList<TrackOverride> trackOverrides, IReadOnlyList<TrackOverride> overrides,
                                                string baseTrack, string slotTrack, string code,
                                                TrackSettings settings, StockGame game)
    {
        var datasets = game.DatasetsWith(baseTrack);
        var ops = new JsonArray();
        var kept = new List<TrackOverride>();
        var keptTrack = new List<TrackOverride>();
        var trackRow = TrackRow(code);
        var created = 0;

        var source = new TrackSource(game, baseTrack, slotTrack, trackOverrides, overrides);
        var tuning = settings.Tuning is { } now && settings.TuningWas is { } was ? now.Ops(source, was) : [];
        var ours = settings.TrackRowFields().Select(f => f.Field)
            .Concat(tuning.Where(t => t.Dataset is null).Select(t => t.Field))
            .ToHashSet(StringComparer.Ordinal);
        var oursData = tuning.Where(t => t.Dataset is not null).Select(t => (t.Dataset!, t.Field)).ToHashSet();

        foreach (var o in trackOverrides)
        {
            var fields = TrackSource.Expand(o.Path, f => game.TrackHas(slotTrack, f));
            if (fields.Count > 0)
            {
                foreach (var field in fields.Where(f => !ours.Contains(f))) ops.Add(SetOp(trackRow, field, o.Value));
            }
            else if (ours.Contains(o.Path)) continue;
            else if (!o.Path.Contains("_*", StringComparison.Ordinal) && game.TrackFieldTag(o.Path) is { } tag)
            {
                ops.Add(CreateOp(trackRow, o.Path, o.Value, tag));
                created++;
            }
            else keptTrack.Add(o);
        }
        foreach (var o in overrides)
        {
            foreach (var ds in o.Datasets.Count > 0 ? o.Datasets : datasets)
            {
                if (!datasets.Contains(ds)) continue;
                bool Ours(string field) => oursData.Contains((ds, field)) || settings.TrackDataFields(ds).Any(f => f.Field == field);
                var fields = TrackSource.Expand(o.Path, f => game.TrackDataHas(ds, baseTrack, f));
                if (fields.Count > 0)
                {
                    foreach (var field in fields.Where(f => !Ours(f))) ops.Add(SetOp(DataRow(code, ds), field, o.Value));
                }
                else if (Ours(o.Path)) continue;
                else if (!o.Path.Contains("_*", StringComparison.Ordinal) && game.TrackDataFieldTag(o.Path) is { } tag)
                {
                    ops.Add(CreateOp(DataRow(code, ds), o.Path, o.Value, tag));
                    created++;
                }
                else kept.Add(o with { Datasets = [ds] });
            }
        }
        return new AuthorChangeSet(ops, kept, keptTrack, created, tuning);
    }

    /// <summary>
    /// What the addon carries over from the zip that the form doesn't show,
    /// as the addon writes it: the author's database changes, the ERS, and the
    /// manifest's other fields — those it passes on as the zip had them.
    /// </summary>
    public static JsonObject Unshown(string manifestJson, IReadOnlyList<TrackOverride> trackOverrides,
                                     IReadOnlyList<TrackOverride> overrides, IReadOnlyList<(string Key, string Value)> ers,
                                     string baseTrack, string slotTrack, TrackSettings settings, StockGame game)
    {
        var code = settings.Code.Trim();
        var author = AuthorChanges(trackOverrides, overrides, baseTrack, slotTrack, code, settings, game);

        var ersNode = new JsonObject();
        foreach (var (key, value) in ers) ersNode[key.Trim()] = JsonNode.Parse(Number(value));

        // The manifest's fields this doesn't write itself; activation without
        // the overrides, which are the set ops above, save any kept.
        var manifest = new JsonObject();
        if (JsonNode.Parse(manifestJson) is JsonObject zipManifest)
        {
            string[] written = ["track_code", "track_name", "version", "author_name", "erp", "ers", "copy_directories", "ui"];
            foreach (var (key, value) in zipManifest)
            {
                if (written.Contains(key)) continue;
                var copy = value?.DeepClone();
                if (key == "activation" && copy is JsonObject activation)
                {
                    activation.Remove("overrides");
                    activation.Remove("track_overrides");
                    if (author.Kept.Count > 0) activation["overrides"] = Overrides(author.Kept);
                    if (author.KeptTrack.Count > 0) activation["track_overrides"] = Overrides(author.KeptTrack);
                }
                manifest[key] = copy;
            }
        }

        return new JsonObject { ["database"] = author.Ops, ["ers"] = ersNode, ["manifest"] = manifest };
    }

    /// <summary>JSON as the addon's files have it: indented, text as written.</summary>
    public static string ToJson(JsonNode node) => node.ToJsonString(Json);

    private static string TrackRow(string code) => $"tracks.Track[_metadata_={code}]";

    private static string DataRow(string code, string dataset) =>
        $"track_data.game_setup.TrackDataSet[_metadata_={dataset}].TrackData[_metadata_={code}]";

    private static JsonObject SetOp(string select, string field, string value) =>
        new() { ["op"] = "set", ["select"] = select, ["field"] = field, ["value"] = value };

    /// <summary>A set that adds the field, for a row that lacks it.</summary>
    private static JsonObject CreateOp(string select, string field, string value, byte tag)
    {
        var op = SetOp(select, field, value);
        op["create"] = true;
        op["type"] = tag;
        return op;
    }

    /// <summary>
    /// Stored, not compressed: the archive and the mip files are compressed
    /// already, and deflating three gigabytes again costs minutes for nothing.
    /// </summary>
    private static void Store(ZipArchive archive, string name, Stream source, Action<long> moved)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var target = entry.Open();
        var buffer = new byte[1 << 20];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            target.Write(buffer, 0, read);
            moved(read);
        }
    }

    private static JsonArray Overrides(IEnumerable<TrackOverride> overrides) =>
        new(overrides.Select(o =>
        {
            var node = new JsonObject { ["path"] = o.Path, ["value"] = o.Value };
            if (o.Datasets.Count > 0) node["datasets"] = new JsonArray(o.Datasets.Select(d => (JsonNode)d).ToArray());
            return (JsonNode)node;
        }).ToArray());

    /// <summary>A number as JSON text, kept as written: <c>9000000</c> stays <c>9000000</c>.</summary>
    private static string Number(string value)
    {
        var v = value.Trim().Replace(',', '.');
        return double.TryParse(v, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out _) ? v : "0";
    }

    private static void WriteJson(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, node.ToJsonString(Json) + "\n");
    }
}
