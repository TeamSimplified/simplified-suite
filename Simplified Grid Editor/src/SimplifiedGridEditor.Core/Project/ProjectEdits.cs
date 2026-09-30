using System.Text.Json;
using System.Text.Json.Serialization;
using EgoEngineLibrary.Data.GameDatabases;
using SimplifiedTeamEditor.Core.Lng;

namespace SimplifiedGridEditor.Core.Project;

/// <summary>
/// What a project actually contains: the changes, not the changed files.
///
/// A project used to hold a whole copy of the game's database and its eleven
/// language files, edited in place. That made a project a snapshot of the game
/// on the day it was started — and a mod packaged from it a month later put
/// that day's game back, quietly undoing whatever the game had patched in
/// between, while mod.serps claimed it had been built against the new build.
///
/// So the project holds the difference instead. The database is a set of
/// records by key, the text a set of entries by key, and both are applied to
/// files taken fresh from the installed game every time the project is opened.
/// A patch that changes a record nobody edited arrives on its own; a record
/// somebody did edit keeps their value, which is the only sensible answer when
/// the two disagree.
///
/// Measured before it was built: a driver swap and two menu changes come to
/// twelve records and about six hundred bytes, a full comparison of 742,857
/// records takes 70ms, and rebuilding a working copy from the game plus that
/// difference takes a fifth of a second.
/// </summary>
public sealed class ProjectEdits
{
    /// <summary>What the file is called inside a project.</summary>
    public const string FileName = "edits.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// A record as the project keeps it: its bytes, and enough of its shape to
    /// put it back if the installed game hasn't got it at all.
    /// </summary>
    public sealed class Record
    {
        [JsonPropertyName("tag")] public byte Tag { get; set; }
        [JsonPropertyName("terminator")] public byte Terminator { get; set; }

        /// <summary>The record's data, as hex — the same way the game's own editors export one.</summary>
        [JsonPropertyName("data")] public string Data { get; set; } = "";

        [JsonIgnore]
        public byte[] Bytes
        {
            get => Convert.FromHexString(Data);
            set => Data = Convert.ToHexString(value);
        }
    }

    [JsonPropertyName("_comment")]
    public string Comment { get; set; } =
        "The project's changes, applied to files taken fresh from the installed game. " +
        "Database records are by key, in hex; text entries are by language-file key.";

    /// <summary>Which game build the project was last rebuilt against, for the record.</summary>
    [JsonPropertyName("built_against")] public string BuiltAgainst { get; set; } = "";

    /// <summary>Changed database records, by key.</summary>
    [JsonPropertyName("database")] public Dictionary<string, Record> Database { get; set; } = new();

    /// <summary>
    /// Changed language entries, by key.
    ///
    /// One set for all eleven files: a team renamed in English is renamed in
    /// every language, which is what the game needs — the alternative is a team
    /// called one thing in menus and another in commentary subtitles.
    /// </summary>
    [JsonPropertyName("text")] public Dictionary<string, string> Text { get; set; } = new();

    [JsonIgnore] public bool Any => Database.Count > 0 || Text.Count > 0;

    public static ProjectEdits Load(string path)
    {
        if (!File.Exists(path)) return new ProjectEdits();

        try { return JsonSerializer.Deserialize<ProjectEdits>(File.ReadAllText(path), Json) ?? new ProjectEdits(); }
        catch { return new ProjectEdits(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    // ── Reading the difference ──────────────────────────────────────────────

    /// <summary>
    /// Every record whose bytes differ from the pristine database, plus any the
    /// pristine hasn't got at all.
    /// </summary>
    public static Dictionary<string, Record> DatabaseDifference(GameDatabase pristine, GameDatabase working)
    {
        var before = pristine.Records.ToDictionary(r => r.Key, r => r.Data);
        var changed = new Dictionary<string, Record>();

        foreach (var record in working.Records)
        {
            if (before.TryGetValue(record.Key, out var was) && was.AsSpan().SequenceEqual(record.Data))
                continue;

            changed[record.Key.ToString()] = new Record
            {
                Tag = record.Tag,
                Terminator = record.Terminator,
                Bytes = record.Data,
            };
        }

        return changed;
    }

    /// <summary>
    /// Every entry whose text differs from the pristine language file, plus any
    /// the pristine hasn't got — the 2026 keys the game ships without.
    ///
    /// One file is enough to read the difference from, because every language
    /// is written the same edits. English is the one the editors work in.
    /// </summary>
    public static Dictionary<string, string> TextDifference(LngFile pristine, LngFile working)
    {
        var changed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in working.Entries)
        {
            if (pristine.Entries.TryGetValue(key, out var was) && was == value) continue;
            changed[key] = value;
        }

        return changed;
    }

    // ── Putting it back ─────────────────────────────────────────────────────

    /// <summary>
    /// Apply the stored records to a database. Returns how many landed.
    ///
    /// A record the game no longer has is skipped rather than added back: the
    /// only way that happens is the game dropping it, and re-adding a record it
    /// has stopped reading would be writing to nothing.
    /// </summary>
    public int ApplyTo(GameDatabase database)
    {
        if (Database.Count == 0) return 0;

        var byKey = database.Records.ToDictionary(r => r.Key);
        var applied = 0;

        foreach (var (text, edit) in Database)
        {
            if (!ulong.TryParse(text, out var key)) continue;
            if (!byKey.TryGetValue(key, out var record)) continue;

            record.Data = edit.Bytes;
            applied++;
        }

        return applied;
    }

    /// <summary>
    /// Apply the stored text to one language file, and hand back the bytes to
    /// write.
    ///
    /// Adding a key changes which bucket everything hashes into, so a file that
    /// gains one is rebuilt whole rather than patched in place.
    /// </summary>
    public byte[] ApplyTo(LngFile language)
    {
        if (Text.Count == 0) return language.BuildSaveData(new Dictionary<string, string>(StringComparer.Ordinal));

        var adds = Text.Keys.Any(k => !language.Entries.ContainsKey(k));
        if (!adds) return language.BuildSaveData(Text);

        var all = new Dictionary<string, string>(language.Entries, StringComparer.Ordinal);
        foreach (var (key, value) in Text) all[key] = value;
        return language.BuildRebuiltData(all);
    }
}
