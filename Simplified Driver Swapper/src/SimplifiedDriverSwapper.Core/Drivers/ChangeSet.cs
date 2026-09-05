using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplifiedDriverSwapper.Core.Drivers;

/// <summary>
/// Pending changes, in a form that survives leaving this machine.
///
/// Everything is stored by name rather than by record index — drivers by id,
/// kit by metadata string — because indexes shift between game versions and a
/// file shared with someone on a different patch would otherwise land on the
/// wrong records.
/// </summary>
public sealed class ChangeSet
{
    public const string ToolName = "simplified-driver-swapper";
    public const int CurrentVersion = 1;

    [JsonPropertyName("tool")] public string Tool { get; set; } = ToolName;
    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("grid")] public GridChanges Grid { get; set; } = new();

    /// <summary>Driver id → field → value.</summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, Dictionary<string, string>> Attributes { get; set; } = [];

    public sealed class GridChanges
    {
        /// <summary>Which grid these seats belong to, e.g. <c>2026</c>.</summary>
        [JsonPropertyName("year")] public string Year { get; set; } = "";

        [JsonPropertyName("list")] public int List { get; set; }
        [JsonPropertyName("options")] public OptionFlags Options { get; set; } = new();

        /// <summary>Seat slot (as text, so it survives JSON object keys) → what changed.</summary>
        [JsonPropertyName("seats")]
        public Dictionary<string, SeatChange> Seats { get; set; } = [];
    }

    public sealed class OptionFlags
    {
        [JsonPropertyName("helmet")] public bool Helmet { get; set; } = true;
        [JsonPropertyName("suit")] public bool Suit { get; set; } = true;
        [JsonPropertyName("gloves")] public bool Gloves { get; set; } = true;
    }

    public sealed class SeatChange
    {
        [JsonPropertyName("driver")] public string? Driver { get; set; }
        [JsonPropertyName("number")] public string? Number { get; set; }

        /// <summary>Helmet metadata string, not its record index.</summary>
        [JsonPropertyName("helmet")] public string? Helmet { get; set; }

        [JsonPropertyName("suit")] public string? Suit { get; set; }
        [JsonPropertyName("gloves")] public string? Gloves { get; set; }

        [JsonIgnore] public bool IsEmpty =>
            Driver is null && Number is null && Helmet is null && Suit is null && Gloves is null;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static ChangeSet Load(string path)
    {
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ChangeSet>(text)
               ?? throw new InvalidDataException("That file doesn't contain any changes.");
    }

    // ── Building one from the current state ─────────────────────────────────

    public static ChangeSet Capture(
        SeatPlan plan,
        KitOptions options,
        DriverCatalogue catalogue,
        IReadOnlyDictionary<int, Dictionary<string, string>> pendingAttributes,
        IReadOnlyList<DriverAttributes.DriverRow> attributeRows)
    {
        var set = new ChangeSet();
        set.Grid.Year = plan.Season.Year;
        set.Grid.List = plan.Season.EntrantList;
        set.Grid.Options = new OptionFlags { Helmet = options.Helmet, Suit = options.Suit, Gloves = options.Gloves };

        foreach (var seat in plan.Seats)
        {
            var change = new SeatChange();

            if (plan.Occupant(seat.Slot) != seat.DefaultDriver)
                change.Driver = plan.Occupant(seat.Slot);

            change.Number = plan.NumberOverride(seat.Slot);
            change.Suit = plan.SuitOverride(seat.Slot);
            change.Gloves = plan.GloveOverride(seat.Slot);

            var helmet = plan.HelmetOverride(seat.Slot);
            if (helmet is not null)
                change.Helmet = catalogue.HelmetName(helmet.Value);

            if (!change.IsEmpty) set.Grid.Seats[seat.Slot.ToString()] = change;
        }

        var idByIndex = attributeRows.ToDictionary(r => r.Index, r => r.DriverId);
        foreach (var (index, fields) in pendingAttributes)
            if (idByIndex.TryGetValue(index, out var id))
                set.Attributes[id] = new Dictionary<string, string>(fields, StringComparer.Ordinal);

        return set;
    }

    [JsonIgnore]
    public int SeatCount => Grid.Seats.Count;

    [JsonIgnore]
    public int AttributeFieldCount => Attributes.Values.Sum(v => v.Count);
}
