using System.Text.RegularExpressions;
using EgoEngineLibrary.Data.GameDatabases;

namespace SimplifiedTrackManager.Core.Track;

/// <summary>
/// What the game's own database says about its tracks, for the questions a
/// conversion has to answer before it writes anything:
/// <list type="bullet">
/// <item>is the zip's code one the game already has — a real track or one of
///       the empty <c>*24</c> slots — so that it needs a code of its own;</item>
/// <item>which datasets hold the base track, since activation clones a
///       TrackData row only into those;</item>
/// <item>does the base track have a field, since activation copies what the
///       base has and a <c>set</c> on a field the copy lacks fails the build;</item>
/// <item>which three letters the game's tracks already use.</item>
/// </list>
///
/// Read from the database in the game folder. Swofty applies its changes at
/// runtime and leaves that file stock, so it is the game's own.
/// </summary>
public sealed partial class StockGame
{
    private readonly GameDatabase _db;
    private readonly Dictionary<string, int> _trackRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _datasets = new(StringComparer.Ordinal);

    /// <summary>Every track code the game has, in its own order.</summary>
    public IReadOnlyList<string> TrackCodes { get; }

    /// <summary>The datasets under <c>track_data.game_setup</c>, by label, in row order.</summary>
    public IReadOnlyList<string> Datasets { get; }

    /// <summary>
    /// The nationalities the game's tracks and drivers use, less
    /// <see cref="HiddenCountries"/>: <see cref="NoCountry"/> first, then the
    /// countries sorted.
    /// </summary>
    public IReadOnlyList<string> Nationalities { get; }

    /// <summary>
    /// Countries the app leaves out of its nationality list and its in-game
    /// flags, by the game's key. One list, so the two can't disagree.
    /// </summary>
    /// <summary>
    /// The game's nationality for no country, which it gives Sochi and the
    /// drivers it made neutral; its flag is a world map.
    /// </summary>
    public const string NoCountry = "default";

    public static readonly IReadOnlySet<string> HiddenCountries =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "israel" };

    /// <summary>
    /// Three-letter initials the game's tracks already have, lower case: the
    /// infix of their language keys (<c>lng_track_aus_title</c>) and the
    /// acronym those show (<c>AUS</c>). A new track may use neither.
    /// </summary>
    public IReadOnlySet<string> TakenInitials { get; }

    /// <summary>
    /// The acronyms the game's language files show for its tracks, which the
    /// database names only by key (<c>lng_track_bak_tla</c> reads <c>AZE</c>).
    /// Read off the English language file of F1 25.
    /// </summary>
    public static readonly string[] StockAcronyms =
    [
        "AUS", "AUT", "AZE", "BEL", "BHR", "BRA", "CAN", "CAT", "CHN", "ESP", "FRA", "GBR", "HUN",
        "IMO", "ITA", "JPN", "MCO", "MEX", "MIA", "NLD", "POR", "QAT", "SAU", "SGP", "UAE", "USA", "VGS",
    ];

    /// <summary>
    /// The infixes of the track keys in the language files, among them tracks
    /// whose database rows name a different key (Catalunya, Madrid).
    /// </summary>
    public static readonly string[] StockKeyInitials =
    [
        "are", "aus", "aut", "bak", "bel", "bhr", "bra", "can", "cat", "chn", "esp", "fra", "gbr", "hun",
        "imo", "ita", "jpn", "las", "los", "mad", "mco", "mex", "mia", "nld", "por", "sau", "sgp", "usa",
    ];

    private StockGame(GameDatabase db)
    {
        _db = db;

        var codes = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            var code = db.Text($"tracks.Track_{i}._metadata_");
            if (code.Length == 0) continue;
            codes.Add(code);
            _trackRows.TryAdd(code, i);
        }
        TrackCodes = codes;

        var sets = new List<string>();
        for (var i = 0; i < 64; i++)
        {
            var name = db.Text($"track_data.game_setup.TrackDataSet_{i}._metadata_");
            if (name.Length == 0) break;
            sets.Add(name);
            _datasets[name] = i;
        }
        Datasets = sets;

        var nationalities = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in _trackRows.Values)
            if (db.Text($"tracks.Track_{row}.nationality") is { Length: > 0 } n) nationalities.Add(n);
        for (var i = 0; i < 2000; i++)
            if (db.Text($"entrants.drivers.driver_{i}.nationality") is { Length: > 0 } n) nationalities.Add(n);
        nationalities.Remove(NoCountry);
        nationalities.ExceptWith(HiddenCountries);
        Nationalities = [NoCountry, .. nationalities];

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in StockAcronyms) taken.Add(a.ToLowerInvariant());
        foreach (var k in StockKeyInitials) taken.Add(k);
        foreach (var row in _trackRows.Values)
            foreach (var field in new[] { "title", "acronym_name" })
                if (TrackKeyInfix().Match(db.Text($"tracks.Track_{row}.{field}")) is { Success: true } m)
                    taken.Add(m.Groups[1].Value.ToLowerInvariant());
        TakenInitials = taken;
    }

    /// <summary>Read the database in <paramref name="gameDir"/>.</summary>
    public static StockGame Load(string gameDir)
    {
        var path = GameDatabase.FindInGameDirectory(gameDir)
                   ?? throw new FileNotFoundException("The game folder has no game database (game_package\\gamedatabases).");
        return new StockGame(GameDatabase.Load(path));
    }

    public bool IsStockTrack(string code) => _trackRows.ContainsKey(code);

    /// <summary>The datasets that hold <paramref name="track"/>, so get a copy of it on activation.</summary>
    public IReadOnlyList<string> DatasetsWith(string track) =>
        Datasets.Where(d => TrackDataRow(d, track) is not null).ToList();

    /// <summary>A field of the track's Track row, or null when the row lacks it.</summary>
    public string? TrackValue(string track, string field) =>
        _trackRows.TryGetValue(track, out var row) ? _db.Find($"tracks.Track_{row}.{field}")?.Text : null;

    public bool TrackHas(string track, string field) => TrackValue(track, field) is not null;

    /// <summary>Whether the track's TrackData row in <paramref name="dataset"/> has a field.</summary>
    public bool TrackDataHas(string dataset, string track, string field) =>
        TrackDataValue(dataset, track, field) is not null;

    /// <summary>A field of the track's TrackData row in <paramref name="dataset"/>, or null when the row lacks it.</summary>
    public string? TrackDataValue(string dataset, string track, string field) =>
        TrackDataRow(dataset, track) is { } row ? _db.Find($"{row}.{field}")?.Text : null;

    /// <summary>
    /// The record type the game stores a Track row field as, from any of its
    /// tracks that has it; null when none does. A <c>set</c> that adds a field
    /// the base track lacks needs it.
    /// </summary>
    public byte? TrackFieldTag(string field) =>
        _trackRows.Values.Select(row => _db.Find($"tracks.Track_{row}.{field}")?.Tag).FirstOrDefault(t => t is not null);

    /// <summary>The same for a field of the TrackData rows, from any track in any dataset.</summary>
    public byte? TrackDataFieldTag(string field)
    {
        foreach (var set in _datasets.Values)
            for (var j = 0; j < 200; j++)
            {
                var row = $"track_data.game_setup.TrackDataSet_{set}.TrackData_{j}";
                if (_db.Find($"{row}._metadata_") is null) break;
                if (_db.Find($"{row}.{field}") is { } record) return record.Tag;
            }
        return null;
    }

    /// <summary>
    /// Rows found, by dataset and track: the form asks for dozens of fields
    /// each time it checks itself. Concurrent, as a package reads it from a
    /// worker thread.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), string?> _dataRows = new();

    private string? TrackDataRow(string dataset, string track) =>
        _dataRows.GetOrAdd((dataset, track), key =>
        {
            if (!_datasets.TryGetValue(key.Item1, out var set)) return null;
            for (var j = 0; j < 200; j++)
            {
                var label = _db.Find($"track_data.game_setup.TrackDataSet_{set}.TrackData_{j}._metadata_");
                if (label is null) return null;
                if (label.Text == key.Item2) return $"track_data.game_setup.TrackDataSet_{set}.TrackData_{j}";
            }
            return null;
        });

    [GeneratedRegex("^lng_track_([a-z0-9]{3})(?:_|$)")]
    private static partial Regex TrackKeyInfix();
}
