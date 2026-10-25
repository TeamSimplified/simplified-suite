using System.Globalization;

namespace SimplifiedTrackManager.Core.Track;

/// <summary>
/// The ERS values the form edits, out of the manifest's <c>ers</c> block:
/// what the 2026 cars may harvest a lap and where overtake mode is detected and
/// switched on. The installer writes the block into every 2026 car's handling
/// file as the track's <c>TrackSettings</c>; a value left out falls back to the
/// car's common one (8.5 MJ race, 9 MJ qualifying, no overtake points).
///
/// The form has harvests in megajoules and lap points in percent; the manifest
/// has joules and fractions of a lap. The AI's deploy and derate factors are
/// 0 to 1 on both. Every other key passes through as the zip had it.
/// </summary>
public sealed class TrackErs
{
    public const string HarvestKey = "HarvestLimitMGUK";
    public const string HarvestQualifyingKey = "HarvestLimitQualifyingMGUK";
    public const string DetectionKey = "OverrideDetectionLapProgress";
    public const string ActivationKey = "OverrideActivationLapProgress";
    public const string MediumDeployKey = "MediumDeployFactor";
    public const string MediumDerateKey = "MediumDerateFactor";
    public const string HotLapDeployKey = "HotLapDeployFactor";
    public const string HotLapDerateKey = "HotLapDerateFactor";

    /// <summary>The keys the form shows; the rest are left to the Database section.</summary>
    public static readonly string[] Keys =
        [HarvestKey, HarvestQualifyingKey, DetectionKey, ActivationKey, MediumDeployKey, MediumDerateKey, HotLapDeployKey, HotLapDerateKey];

    /// <summary>Megajoules a lap, race and qualifying, as typed.</summary>
    public string Harvest { get; set; } = "";
    public string HarvestQualifying { get; set; } = "";

    /// <summary>How far round the lap, in percent, as typed.</summary>
    public string Detection { get; set; } = "";
    public string Activation { get; set; } = "";

    /// <summary>How hard the AI uses its energy, 0 to 1: deploy is how much it spends, derate how much it holds back.</summary>
    public string MediumDeploy { get; set; } = "";
    public string MediumDerate { get; set; } = "";
    public string HotLapDeploy { get; set; } = "";
    public string HotLapDerate { get; set; } = "";

    /// <summary>Each value the form edits: its key, how many manifest units one form unit is, and the decimals written.</summary>
    private (string Key, string Text, double Unit, int Decimals)[] Fields() =>
    [
        (HarvestKey, Harvest, 1e6, 0),
        (HarvestQualifyingKey, HarvestQualifying, 1e6, 0),
        (DetectionKey, Detection, 0.01, 6),
        (ActivationKey, Activation, 0.01, 6),
        (MediumDeployKey, MediumDeploy, 1, 6),
        (MediumDerateKey, MediumDerate, 1, 6),
        (HotLapDeployKey, HotLapDeploy, 1, 6),
        (HotLapDerateKey, HotLapDerate, 1, 6),
    ];

    /// <summary>The form's values from a manifest's ERS.</summary>
    public static TrackErs Read(IEnumerable<(string Key, string Value)> ers)
    {
        var values = ers.GroupBy(e => e.Key.Trim()).ToDictionary(g => g.Key, g => g.Last().Value);
        string Scaled(string key, double by) =>
            values.TryGetValue(key, out var v) && Number(v) is { } n ? Text(n / by) : "";
        return new TrackErs
        {
            Harvest = Scaled(HarvestKey, 1e6),
            HarvestQualifying = Scaled(HarvestQualifyingKey, 1e6),
            Detection = Scaled(DetectionKey, 0.01),
            Activation = Scaled(ActivationKey, 0.01),
            MediumDeploy = Scaled(MediumDeployKey, 1),
            MediumDerate = Scaled(MediumDerateKey, 1),
            HotLapDeploy = Scaled(HotLapDeployKey, 1),
            HotLapDerate = Scaled(HotLapDerateKey, 1),
        };
    }

    /// <summary>
    /// The manifest's ERS with the form's values in place of the zip's: changed
    /// where typed, dropped where emptied, added at the end where new. The
    /// others keep their order and spelling.
    /// </summary>
    public List<(string Key, string Value)> Apply(IEnumerable<(string Key, string Value)> ers)
    {
        var was = Read(ers).Fields().ToDictionary(f => f.Key, f => f.Text);
        var form = new Dictionary<string, string?>();
        var unchanged = new HashSet<string>();
        foreach (var (key, text, unit, decimals) in Fields())
        {
            form[key] = Written(text, unit, decimals);
            if (Same(text, was[key])) unchanged.Add(key);
        }

        var result = new List<(string Key, string Value)>();
        foreach (var (key, value) in ers)
        {
            var k = key.Trim();
            if (!form.TryGetValue(k, out var typed) || unchanged.Contains(k)) result.Add((key, value));
            else if (typed is not null) { result.Add((key, typed)); form[k] = null; }
            form.Remove(k);
        }
        foreach (var (key, typed) in form)
            if (typed is not null && !unchanged.Contains(key)) result.Add((key, typed));
        return result;
    }

    /// <summary>What's wrong with the form's values, in words for the form.</summary>
    public List<string> Problems()
    {
        var problems = new List<string>();
        void Harvested(string text, string what)
        {
            if (text.Trim().Length == 0) return;
            if (Number(text) is not { } n || n < 0 || n > 20) problems.Add($"The {what} has to be a number of megajoules, like 7.1.");
        }
        void Percent(string text, string what)
        {
            if (text.Trim().Length == 0) return;
            if (Number(text) is not { } n || n < 0 || n > 100) problems.Add($"The {what} has to be a percentage of the lap, 0 to 100.");
        }
        Harvested(Harvest, "race harvest limit");
        Harvested(HarvestQualifying, "qualifying harvest limit");
        Percent(Detection, "overtake detection point");
        Percent(Activation, "overtake activation point");
        void Factor(string text, string what)
        {
            if (text.Trim().Length == 0) return;
            if (Number(text) is not { } n || n < 0 || n > 1) problems.Add($"The AI's {what} has to be between 0 and 1.");
        }
        Factor(MediumDeploy, "race deploy");
        Factor(MediumDerate, "race derate");
        Factor(HotLapDeploy, "hot lap deploy");
        Factor(HotLapDerate, "hot lap derate");
        if (Number(Detection) is { } d and >= 0 and <= 100 && Number(Activation) is { } a and > 0 and <= 100 && a < d)
            problems.Add("Overtake mode has to switch on after the detection point.");
        return problems;
    }

    private static string? Written(string text, double unit, int decimals)
    {
        if (Number(text) is not { } n) return null;
        var value = Math.Round(n * unit, decimals);
        return value.ToString(decimals == 0 ? "0" : "0.######", CultureInfo.InvariantCulture);
    }

    private static bool Same(string now, string was) => Number(now) is { } a && Number(was) is { } b
        ? Math.Abs(a - b) < 1e-9
        : now.Trim() == was.Trim();

    private static string Text(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static double? Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
}
