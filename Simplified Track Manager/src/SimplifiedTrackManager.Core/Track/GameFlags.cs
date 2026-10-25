using System.Text.Json;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Data.Pkg;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;

namespace SimplifiedTrackManager.Core.Track;

/// <summary>
/// The game's own flags, for putting a country's in place of a track's.
///
/// A track has three: round (28×28), rectangular (100×64) and small (40×28).
/// The game has every country's at three sizes for drivers' nationalities —
/// 40×28, the small track flag's own size; 128×80, the rectangle's shape; and
/// 268×152, the sharpest — but round ones only for its tracks' countries. So a
/// country with a track takes the track's round flag, any other the round flag
/// the app ships for it (from flag-icons, re-centred for a circle the way the
/// game's are), and a country with neither gets one cut from its large flag
/// (which the app does, since cutting a circle is drawing).
///
/// Each texture array is named slice by slice in <c>overrides.uidb</c>, the
/// menus' database; its order is the slices' order.
/// </summary>
public sealed class GameFlags
{
    /// <summary>
    /// One country's flags as PNG files. <see cref="Round"/> is the game's own
    /// round flag when <see cref="RoundFromGame"/>, else one the app ships
    /// (flag-icons), else null — which the app then cuts from the large flag.
    /// </summary>
    public sealed record Country(string Key, string Name, string Small, string Banner, string Large,
                                 string? Round, bool RoundFromGame);

    /// <summary>The round flag the app ships for a country the game has none for, or null.</summary>
    public static string? ShippedRound(string key)
    {
        var path = Path.Combine(AppPaths.RoundFlagsDir, key + ".png");
        return File.Exists(path) ? path : null;
    }

    public IReadOnlyList<Country> Countries { get; }

    private GameFlags(IReadOnlyList<Country> countries) => Countries = countries;

    private const string Ui = "2025_asset_groups/ui_package";

    /// <summary>
    /// Every country's flags out of the game into <paramref name="outDir"/>.
    /// The round ones come from the track array, matched to a country by the
    /// track's nationality in the game database.
    /// </summary>
    public static GameFlags Extract(string gameDir, StockGame game, TexconvRunner texconv, string outDir,
                                    CancellationToken cancel = default)
    {
        var ui = Path.Combine(gameDir, Ui.Replace('/', Path.DirectorySeparatorChar));
        var lists = ReadLists(Path.Combine(ui, "databases.erp"));

        var small = Export(Path.Combine(ui, "flow_car_monitor.erp"), "drivers", "nationality_flags", lists, texconv, Path.Combine(outDir, "small"), cancel);
        var banner = Export(Path.Combine(ui, "common_flow_customisation.erp"), "drivers", "nationality_flags_details_banner", lists, texconv, Path.Combine(outDir, "banner"), cancel);
        var large = Export(Path.Combine(ui, "flow_customisation.erp"), "drivers", "nationality_flags_large", lists, texconv, Path.Combine(outDir, "large"), cancel);
        var round = Export(Path.Combine(ui, "common_front_end.erp"), "tracks", "flags_circle", lists, texconv, Path.Combine(outDir, "round"), cancel);

        // A round track flag, by the country of its track. A code the game has
        // no row for is a track it once had, named after its country's flag.
        var roundByCountry = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, png) in round)
        {
            var country = game.TrackValue(code, "nationality") ?? (code switch
            {
                "istanbul" => "turkey",
                "qatar" => "qatar",
                _ => null,
            });
            if (country is { Length: > 0 }) roundByCountry.TryAdd(country, png);
        }

        var countries = new List<Country>();
        foreach (var (key, smallPng) in small)
        {
            if (key == "default" || StockGame.HiddenCountries.Contains(key)
                || !banner.TryGetValue(key, out var bannerPng) || !large.TryGetValue(key, out var largePng))
                continue;
            var gameRound = roundByCountry.GetValueOrDefault(key);
            countries.Add(new Country(key, Display(key), smallPng, bannerPng, largePng,
                                      gameRound ?? ShippedRound(key), gameRound is not null));
        }
        countries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        return new GameFlags(countries);
    }

    /// <summary>
    /// Every country the game has a flag for, by its nationality key — more
    /// than its tracks and drivers use: South Africa, Chile, Greece and some
    /// forty others. Only the menus' database is read, not the pictures, so
    /// it is quick.
    /// </summary>
    public static IReadOnlyList<string> CountryKeys(string gameDir)
    {
        var ui = Path.Combine(gameDir, Ui.Replace('/', Path.DirectorySeparatorChar));
        var names = ReadLists(Path.Combine(ui, "databases.erp")).GetValueOrDefault("drivers.nationality_flags") ?? [];
        return names.Where(n => n.Length > 0 && n != "default" && !StockGame.HiddenCountries.Contains(n)).Distinct().ToList();
    }

    /// <summary><c>czech_republic</c> as <c>Czech Republic</c>; a few the game spells its own way, put right.</summary>
    public static string Display(string key) => key switch
    {
        "uae" => "UAE",
        "usa" => "USA",
        "columbia" => "Colombia",
        "phillipines" => "Philippines",
        "britain" => "Great Britain",
        _ => string.Join(' ', key.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..])),
    };

    /// <summary>A texture array, every slice as a PNG named after its entry, by that name.</summary>
    private static Dictionary<string, string> Export(string archive, string category, string list,
        Dictionary<string, List<string>> lists, TexconvRunner texconv, string outDir, CancellationToken cancel)
    {
        var names = lists.GetValueOrDefault($"{category}.{list}") ?? [];
        var id = $"eaid://ui_package/art/texture_arrays/overrides.uidb/overrides.{category}.{list}.texarray";
        var resource = ErpFileIO.Load(archive).Resources.Find(r => r.Identifier == id)
                       ?? throw new InvalidDataException($"The game has no {category}.{list} flags in {Path.GetFileName(archive)}.");
        var srv = new ErpGfxSRVResource();
        srv.FromResource(resource);
        var srgb = TexconvRunner.IsSrgbFormat(srv.SurfaceRes.Fragment0.ImageType);

        Directory.CreateDirectory(outDir);
        var pngs = new Dictionary<string, string>(StringComparer.Ordinal);
        var slices = Math.Min(srv.SurfaceRes.Fragment0.ArraySize, (uint)names.Count);
        for (uint i = 0; i < slices; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var name = names[(int)i];
            if (pngs.ContainsKey(name)) continue;
            var dds = Path.Combine(outDir, name + ".dds");
            using (var fs = File.Create(dds)) srv.ToDdsFile(null, exportTexArray: false, texArrayIndex: i).Write(fs, -1);
            texconv.DdsToPng(dds, outDir, overwrite: true, isSrgb: srgb);
            File.Delete(dds);
            pngs[name] = Path.Combine(outDir, name + ".png");
        }
        return pngs;
    }

    /// <summary>
    /// The names of every texture array's slices in <c>overrides.uidb</c>, by
    /// <c>&lt;category&gt;.&lt;list&gt;</c>, in slice order.
    /// </summary>
    private static Dictionary<string, List<string>> ReadLists(string databases)
    {
        var resource = ErpFileIO.Load(databases).Resources.Find(r => r.Identifier == "eaid://ui_package/databases/overrides.uidb")
                       ?? throw new InvalidDataException("The game's databases.erp has no overrides.uidb.");
        var json = new StringWriter();
        PkgFile.ReadPkg(new MemoryStream(resource.Fragments[0].GetDataArray(true))).WriteJson(json);
        using var doc = JsonDocument.Parse(json.ToString());

        static bool Is(string key, string name) => key == name || key.StartsWith(name + ".ppv1.", StringComparison.Ordinal);
        static JsonElement? Child(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.EnumerateObject().FirstOrDefault(p => Is(p.Name, name)) is { Value.ValueKind: not JsonValueKind.Undefined } p
                ? p.Value : null;
        static string Text(JsonElement e) => e.GetString() is { } s && s.IndexOf(' ') is var i and >= 0 ? s[(i + 1)..] : "";
        static string Bare(string key) => key.Split(".ppv1.")[0];

        // The object whose keys are the categories is the one holding "tracks".
        static JsonElement? Categories(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (e.EnumerateObject().Any(p => Is(p.Name, "tracks"))) return e;
            foreach (var p in e.EnumerateObject())
                if (Categories(p.Value) is { } hit) return hit;
            return null;
        }

        var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (Categories(doc.RootElement) is not { } categories) return lists;
        foreach (var category in categories.EnumerateObject())
        {
            if (Child(category.Value, "Contents") is not { ValueKind: JsonValueKind.Object } contents) continue;
            foreach (var list in contents.EnumerateObject())
            {
                if (Child(list.Value, "Contents") is not { ValueKind: JsonValueKind.Array } entries) continue;
                lists[$"{Bare(category.Name)}.{Bare(list.Name)}"] = entries.EnumerateArray()
                    .Select(e => Child(e, "Name") is { } n ? Text(n) : "").ToList();
            }
        }
        return lists;
    }
}
