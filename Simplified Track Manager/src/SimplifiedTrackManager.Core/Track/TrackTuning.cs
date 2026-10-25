using System.Globalization;
using System.Text.RegularExpressions;

namespace SimplifiedTrackManager.Core.Track;

/// <summary>
/// A track as its database describes it beyond its names: the race weekend
/// (laps, tyres, session times, pit limit, night start), its conditions
/// (weather, temperatures, daylight), its character (downforce, tyre wear,
/// fuel burn, AI pace) and where it is.
///
/// <see cref="Current"/> reads all of it as the track has it — the author's
/// overrides where the track has them, else its base track's values — and
/// <see cref="Ops"/> writes back only what the user changed from that, so a
/// value the author tuned and the user left alone stays the author's.
///
/// A TrackData field is written to every dataset whose copy of the base track
/// has it, in that dataset's own shape: the game keeps laps as a list whose
/// layout differs by dataset (<c>57, 57, 57</c>; a sprint's <c>19, 57, 57</c>;
/// F2's <c>23, 32,0</c>), so the base track's numbers are swapped for the new
/// ones in place rather than the list rewritten; fuel burn differs by
/// dataset (2026 cars burn less), so each is scaled by the change.
/// </summary>
public sealed partial class TrackTuning
{
    // ── Race weekend ────────────────────────────────────────────────────────
    public string F1RaceLaps { get; set; } = "";
    public string F1SprintLaps { get; set; } = "";
    public string F2FeatureLaps { get; set; } = "";
    public string F2SprintLaps { get; set; } = "";

    /// <summary>The F1 compounds, C0 to C6, for the hard, medium and soft tyre.</summary>
    public string TyreHard { get; set; } = "";
    public string TyreMedium { get; set; } = "";
    public string TyreSoft { get; set; } = "";

    /// <summary>The F1 sessions' start times, <c>HH:MM</c>, in the order of <see cref="SessionFields"/>.</summary>
    public string[] SessionTimes { get; set; } = ["", "", "", "", ""];

    /// <summary>Pit lane speed limits, km/h.</summary>
    public string F1PitLimit { get; set; } = "";
    public string F2PitLimit { get; set; } = "";

    public bool NightStart { get; set; }

    /// <summary>Whether a change of laps or fuel burn also works out the F1 fuel loads.</summary>
    public bool WorkOutFuel { get; set; } = true;

    // ── Conditions ──────────────────────────────────────────────────────────
    /// <summary>The weather's odds in percent, in the order of <see cref="WeatherFields"/>.</summary>
    public string[] Weather { get; set; } = ["", "", "", "", ""];
    public string TemperatureMin { get; set; } = "";
    public string TemperatureMax { get; set; } = "";

    /// <summary>When the sun rises and sets, <c>HH:MM</c>.</summary>
    public string DaylightStart { get; set; } = "";
    public string DaylightEnd { get; set; } = "";

    // ── Character ───────────────────────────────────────────────────────────
    public string DownforceLevel { get; set; } = "";
    public string PerformanceType { get; set; } = "";
    public string TyreWear { get; set; } = "";

    /// <summary>Fuel burned a lap, kg: the Track row's for F1, the latest F2 dataset's for F2.</summary>
    public string F1FuelBurn { get; set; } = "";
    public string F2FuelBurn { get; set; } = "";

    /// <summary>The fastest AI lap, seconds: the last of the F1 2025 car's ten difficulty steps.</summary>
    public string AiLapTime { get; set; } = "";

    // ── Location ────────────────────────────────────────────────────────────
    public string Latitude { get; set; } = "";
    public string Longitude { get; set; } = "";
    public string Continent { get; set; } = "";

    /// <summary>Air pressure against sea level's 1, lower at altitude.</summary>
    public string AirPressure { get; set; } = "";

    // ── What the game takes ─────────────────────────────────────────────────
    public static readonly string[] SessionFields = ["p1_start_time", "p2_start_time", "p3_start_time", "q_start_time", "race_start_time"];
    public static readonly string[] SessionNames = ["P1", "P2", "P3", "Qualifying", "Race"];
    public static readonly string[] WeatherFields = ["clear", "cloudy", "overcast", "light_rain", "heavy_rain"];
    public static readonly string[] WeatherNames = ["Clear", "Cloudy", "Overcast", "Light rain", "Heavy rain"];
    public static readonly string[] Compounds = ["C0", "C1", "C2", "C3", "C4", "C5", "C6"];
    public static readonly string[] DownforceLevels = ["very_low", "low", "medium", "high", "very_high"];
    public static readonly string[] PerformanceTypes = ["Balanced", "HighDownforce", "HighTopSpeed"];
    public static readonly string[] TyreWears = ["Low", "Medium", "High"];
    public static readonly string[] Continents = ["Europe", "Americas", "AsiaAndOceania"];

    /// <summary>The AI's lap-time tables, one per car package, in the Track row under <c>ai_track_difficulties</c>.</summary>
    public static readonly string[] AiPackages =
    [
        "f1_2023_pro_vehicle_package", "f1_2023_story_vehicle_package", "f1_2023_vehicle_package",
        "f1_2024_beta_vehicle_package", "f1_2024_equal_performance_vehicle_package", "f1_2024_story_vehicle_package",
        "f1_2024_vehicle_package", "f1_2024_world_vehicle_package", "f1_2025_equal_performance_vehicle_package",
        "f1_2025_story_vehicle_package", "f1_2025_vehicle_package", "f1_2025_world_vehicle_package",
        "f1_2026_equal_performance_vehicle_package", "f1_2026_vehicle_package", "f1_classic_vehicle_package",
        "f1_safetycar_package", "f2_2022_story_vehicle_package", "f2_2022_vehicle_package", "f2_2023_vehicle_package",
        "f2_2024_vehicle_package", "f2_2025_vehicle_package", "f2_2026_vehicle_package",
    ];

    private const string Laps = "TrackGeneralData_0.number_of_laps";
    private const string Burn = "TrackGeneralData_0.fuel_burn_per_lap";
    private const string PitLimit = "TrackPitData_0.pitSpeedLimit";
    private const string Session = "Session Start Times_0.";
    private const string Fuel = "TrackFuelSettings_0.";
    private const string AiReference = "ai_track_difficulties.f1_2025_vehicle_package.time";
    private static readonly int[] FuelLaps = [2, 3, 5, 9, 10, 15, 20];
    private static readonly int[] FuelPercents = [25, 35, 50, 75, 100];

    // ── Worked out from other values ────────────────────────────────────────

    /// <summary>
    /// The laps a race takes, the FIA's way: the fewest complete laps that go
    /// past its distance — 305 km for an F1 Grand Prix, 100 km for a sprint,
    /// 170 km for an F2 feature race and 120 km for its sprint. The game's own
    /// tracks follow it to the lap (Spa's 7.004 km: 44, 15, 25, 18).
    /// </summary>
    public static int? LapsFor(double? lengthKm, double raceKm) =>
        lengthKm is > 0.5 ? (int)Math.Ceiling(raceKm / lengthKm.Value - 1e-9) : null;

    public const double F1RaceKm = 305, F1SprintKm = 100, F2FeatureKm = 170, F2SprintKm = 120;

    /// <summary>
    /// Fuel an F1 car burns a lap: the lap's length times what the game's
    /// tracks burn a kilometre at that downforce — about 0.32 kg at the fast
    /// ones, rising to 0.35 at Monaco. Within some 3% of every stock track.
    /// </summary>
    public static double? F1FuelBurnFor(double? lengthKm, string downforce) =>
        lengthKm is > 0.5 ? Math.Round(lengthKm.Value * downforce switch
        {
            "very_low" => 0.318,
            "low" => 0.328,
            "high" => 0.343,
            "very_high" => 0.354,
            _ => 0.330,
        }, 2) : null;

    /// <summary>An F2 car burns some 1.37 times what an F1 car does on the same lap, across the game's tracks.</summary>
    public static double? F2FuelBurnFor(double? lengthKm, string downforce) =>
        F1FuelBurnFor(lengthKm, downforce) is { } f1 ? Math.Round(f1 * 1.37, 2) : null;

    /// <summary>
    /// The game's continent for a country, by its nationality key: it has
    /// three, and its own tracks put Azerbaijan and Turkey in Europe and the
    /// Gulf in Asia and Oceania. It has no Africa; Africa's countries go with
    /// Europe, whose time zones they share. Null for the no-country one and
    /// for a key the game's list hasn't got.
    /// </summary>
    public static string? ContinentForCountry(string key) =>
        Americas.Contains(key) ? "Americas"
        : AsiaAndOceania.Contains(key) ? "AsiaAndOceania"
        : Europe.Contains(key) ? "Europe"
        : null;

    private static readonly HashSet<string> Americas =
        ["argentina", "barbados", "bolivia", "brazil", "canada", "chile", "columbia", "costa_rica", "ecuador", "el_salvador",
         "guatemala", "honduras", "jamaica", "mexico", "nicaragua", "panama", "paraguay", "peru", "uruguay", "usa", "venezuela"];
    private static readonly HashSet<string> AsiaAndOceania =
        ["australia", "bahrain", "china", "hong_kong", "india", "indonesia", "japan", "jordan", "kuwait", "lebanon",
         "malaysia", "new_zealand", "oman", "pakistan", "phillipines", "qatar", "saudi_arabia", "singapore", "south_korea",
         "thailand", "uae", "vietnam"];
    private static readonly HashSet<string> Europe =
        ["austria", "azerbaijan", "belgium", "bosnia", "britain", "bulgaria", "croatia", "cyprus", "czech_republic",
         "denmark", "england", "estonia", "finland", "france", "germany", "greece", "hungary", "iceland", "ireland", "italy",
         "latvia", "lithuania", "luxembourg", "malta", "monaco", "netherlands", "northern_ireland", "norway", "poland",
         "portugal", "romania", "russia", "scotland", "serbia", "slovakia", "slovenia", "spain", "sweden", "switzerland",
         "turkey", "ukraine", "wales",
         // Africa
         "algeria", "cameroon", "ghana", "south_africa"];

    public static bool IsF2(string dataset) => dataset.StartsWith("f2", StringComparison.Ordinal);
    public static bool IsSprint(string dataset) =>
        dataset.StartsWith("sprint_", StringComparison.Ordinal) || dataset.EndsWith("_sprint", StringComparison.Ordinal);

    public TrackTuning Clone()
    {
        var copy = (TrackTuning)MemberwiseClone();
        copy.SessionTimes = (string[])SessionTimes.Clone();
        copy.Weather = (string[])Weather.Clone();
        return copy;
    }

    // ── Reading ─────────────────────────────────────────────────────────────

    /// <summary>The track as it is: the author's values where it has them, else its base track's.</summary>
    public static TrackTuning Current(TrackSource track)
    {
        var t = new TrackTuning();
        var f1 = track.Datasets.Where(d => !IsF2(d)).ToList();
        var f2 = track.Datasets.Where(IsF2).ToList();
        string? First(IEnumerable<string> sets, string field) =>
            sets.Select(d => track.Data(d, field)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        // Laps: the most in a race list is the race, the fewest in a sprint's
        // the sprint; F2 the same between its feature and sprint races.
        var f1Main = Prefer(f1.Where(d => !IsSprint(d)), "f1");
        t.F1RaceLaps = Max(First(f1Main, Laps))?.ToString(CultureInfo.InvariantCulture) ?? "";
        t.F1SprintLaps = Min(First(f1.Where(IsSprint), Laps))?.ToString(CultureInfo.InvariantCulture) ?? "";
        var f2Main = Prefer(f2, "f2_2025");
        t.F2FeatureLaps = Max(First(f2Main, Laps))?.ToString(CultureInfo.InvariantCulture) ?? "";
        t.F2SprintLaps = Min(First(f2Main, Laps))?.ToString(CultureInfo.InvariantCulture) ?? "";

        // Tyres from the newest dataset that names them as C-numbers.
        var modern = Prefer(f1.Where(d => IsCompound(track.Data(d, "TyreData_0.compound_medium"))), "f1_2025").FirstOrDefault();
        if (modern is not null)
        {
            t.TyreHard = track.Data(modern, "TyreData_0.compound_harder") ?? "";
            t.TyreMedium = track.Data(modern, "TyreData_0.compound_medium") ?? "";
            t.TyreSoft = track.Data(modern, "TyreData_0.compound_softer") ?? "";
        }

        for (var i = 0; i < SessionFields.Length; i++)
            t.SessionTimes[i] = ShowTime(First(f1Main, Session + SessionFields[i]));

        t.F1PitLimit = First(f1Main.Concat(f1), PitLimit) ?? "";
        t.F2PitLimit = First(f2Main, PitLimit) ?? "";
        t.NightStart = string.Equals(track.Row("allow_night_start"), "True", StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < WeatherFields.Length; i++)
            t.Weather[i] = Number(track.Row($"Weather_0.{WeatherFields[i]}")) is { } w ? Text(w * 100) : "";
        t.TemperatureMin = track.Row("Weather_0.temperature_min") ?? "";
        t.TemperatureMax = track.Row("Weather_0.temperature_max") ?? "";
        t.DaylightStart = ShowTime(track.Row("Daylight_0.start"));
        t.DaylightEnd = ShowTime(track.Row("Daylight_0.end"));

        t.DownforceLevel = track.Row("downForceLevel") ?? "";
        t.PerformanceType = track.Row("performance_type") ?? "";
        t.TyreWear = track.Row("tyre_wear") ?? "";
        t.F1FuelBurn = track.Row("fuel_burn_per_lap") ?? First(f1Main, Burn) ?? "";
        t.F2FuelBurn = First(f2Main, Burn) ?? "";
        t.AiLapTime = List(track.Row(AiReference)) is { Count: > 0 } ai ? Text(ai[^1]) : "";

        t.Latitude = track.Row("latitude") ?? "";
        t.Longitude = track.Row("longitude") ?? "";
        t.Continent = track.Row("continent") ?? "";
        t.AirPressure = track.Row("barometricReferencePressure") ?? "";
        return t;
    }

    // ── Writing ─────────────────────────────────────────────────────────────

    /// <summary>One field to set: on the Track row when <see cref="Dataset"/> is null, else on that dataset's TrackData row.</summary>
    public sealed record Op(string? Dataset, string Field, string Value);

    /// <summary>
    /// What to write for every value changed from <paramref name="was"/>, the
    /// track as <see cref="Current"/> read it. A TrackData field goes only to
    /// datasets whose copy of the base track has it.
    /// </summary>
    public IReadOnlyList<Op> Ops(TrackSource track, TrackTuning was)
    {
        var ops = new List<Op>();
        void Row(string field, string value) => ops.Add(new Op(null, field, value));
        void Data(string dataset, string field, string value) => ops.Add(new Op(dataset, field, value));
        bool Changed(string now, string before) => !string.Equals(now.Trim(), before.Trim(), StringComparison.Ordinal);

        var f1 = track.Datasets.Where(d => !IsF2(d)).ToList();
        var f2 = track.Datasets.Where(IsF2).ToList();

        // Laps, each dataset's list with the base track's numbers swapped.
        var newLaps = new Dictionary<string, string>(StringComparer.Ordinal);
        var f1Laps = Changed(F1RaceLaps, was.F1RaceLaps) || Changed(F1SprintLaps, was.F1SprintLaps);
        var f2Laps = Changed(F2FeatureLaps, was.F2FeatureLaps) || Changed(F2SprintLaps, was.F2SprintLaps);
        if (f1Laps)
        {
            var race = Max(f1.Where(d => !IsSprint(d)).Select(d => track.BaseData(d, Laps)).FirstOrDefault(v => v is not null));
            var sprint = Min(f1.Where(IsSprint).Select(d => track.BaseData(d, Laps)).FirstOrDefault(v => v is not null));
            foreach (var d in f1)
                if (track.BaseData(d, Laps) is { } list)
                    newLaps[d] = Swap(list, race, Int(F1RaceLaps), IsSprint(d) ? sprint : null, Int(F1SprintLaps));
        }
        if (f2Laps)
        {
            var reference = Prefer(f2, "f2_2025").Select(d => track.BaseData(d, Laps)).FirstOrDefault(v => v is not null);
            var (feature, sprint) = (Max(reference), Min(reference));
            foreach (var d in f2)
                if (track.BaseData(d, Laps) is { } list)
                    newLaps[d] = Swap(list, feature, Int(F2FeatureLaps), sprint, Int(F2SprintLaps));
        }
        foreach (var (d, list) in newLaps) Data(d, Laps, list);

        if (Changed(TyreHard, was.TyreHard) || Changed(TyreMedium, was.TyreMedium) || Changed(TyreSoft, was.TyreSoft))
            foreach (var d in f1.Where(d => IsCompound(track.BaseData(d, "TyreData_0.compound_medium"))))
            {
                Data(d, "TyreData_0.compound_harder", TyreHard.Trim());
                Data(d, "TyreData_0.compound_medium", TyreMedium.Trim());
                Data(d, "TyreData_0.compound_softer", TyreSoft.Trim());
            }

        for (var i = 0; i < SessionFields.Length; i++)
            if (Changed(SessionTimes[i], was.SessionTimes[i]) && GameTime(SessionTimes[i]) is { } time)
                foreach (var d in f1.Where(d => !IsSprint(d) && track.BaseData(d, Session + SessionFields[i]) is not null))
                    Data(d, Session + SessionFields[i], time);

        if (Changed(F1PitLimit, was.F1PitLimit))
            foreach (var d in f1.Where(d => track.BaseData(d, PitLimit) is not null)) Data(d, PitLimit, F1PitLimit.Trim());
        if (Changed(F2PitLimit, was.F2PitLimit))
            foreach (var d in f2.Where(d => track.BaseData(d, PitLimit) is not null)) Data(d, PitLimit, F2PitLimit.Trim());
        if (NightStart != was.NightStart) Row("allow_night_start", NightStart ? "True" : "False");

        // Fuel burn: the Track row's as typed, and every dataset's scaled by the same change.
        var newBurn = new Dictionary<string, double>(StringComparer.Ordinal);
        var f1Burn = Changed(F1FuelBurn, was.F1FuelBurn) && Number(F1FuelBurn) is > 0;
        var f2Burn = Changed(F2FuelBurn, was.F2FuelBurn) && Number(F2FuelBurn) is > 0;
        if (f1Burn)
        {
            Row("fuel_burn_per_lap", F1FuelBurn.Trim());
            var reference = Number(track.BaseRow("fuel_burn_per_lap"))
                            ?? f1.Select(d => Number(track.BaseData(d, Burn))).FirstOrDefault(v => v is not null);
            foreach (var d in f1)
                if (Number(track.BaseData(d, Burn)) is { } burn && reference is > 0)
                    newBurn[d] = Math.Round(burn * Number(F1FuelBurn)!.Value / reference.Value, 2);
        }
        if (f2Burn)
        {
            var reference = Prefer(f2, "f2_2025").Select(d => Number(track.BaseData(d, Burn))).FirstOrDefault(v => v is not null);
            foreach (var d in f2)
                if (Number(track.BaseData(d, Burn)) is { } burn && reference is > 0)
                    newBurn[d] = Math.Round(burn * Number(F2FuelBurn)!.Value / reference.Value, 2);
        }
        foreach (var (d, burn) in newBurn) Data(d, Burn, Text(burn));

        if (WorkOutFuel && (f1Laps || f1Burn))
            foreach (var d in f1)
                ops.AddRange(FuelLoads(track, d, newLaps.GetValueOrDefault(d), newBurn.TryGetValue(d, out var b) ? b : null));

        if (Weather.Where((w, i) => Changed(w, was.Weather[i])).Any())
            for (var i = 0; i < WeatherFields.Length; i++)
                if (Number(Weather[i]) is { } percent) Row($"Weather_0.{WeatherFields[i]}", Text(percent / 100));
        if (Changed(TemperatureMin, was.TemperatureMin)) Row("Weather_0.temperature_min", TemperatureMin.Trim());
        if (Changed(TemperatureMax, was.TemperatureMax)) Row("Weather_0.temperature_max", TemperatureMax.Trim());
        if (Changed(DaylightStart, was.DaylightStart) && GameTime(DaylightStart) is { } dawn) Row("Daylight_0.start", dawn);
        if (Changed(DaylightEnd, was.DaylightEnd) && GameTime(DaylightEnd) is { } dusk) Row("Daylight_0.end", dusk);

        if (Changed(DownforceLevel, was.DownforceLevel)) Row("downForceLevel", DownforceLevel.Trim());
        if (Changed(PerformanceType, was.PerformanceType)) Row("performance_type", PerformanceType.Trim());
        if (Changed(TyreWear, was.TyreWear)) Row("tyre_wear", TyreWear.Trim());

        // AI pace: every package's ten steps scaled by the change to the reference.
        if (Changed(AiLapTime, was.AiLapTime) && Number(AiLapTime) is double lap and > 0 && Number(was.AiLapTime) is double old and > 0)
            foreach (var package in AiPackages)
            {
                var field = $"ai_track_difficulties.{package}.time";
                if (List(track.Row(field)) is { Count: > 1 } steps)
                    Row(field, string.Join(",", steps.Select(s => Text(Math.Round(s * lap / old, 4)))));
            }

        if (Changed(Latitude, was.Latitude)) Row("latitude", Latitude.Trim());
        if (Changed(Longitude, was.Longitude)) Row("longitude", Longitude.Trim());
        if (Changed(Continent, was.Continent)) Row("continent", Continent.Trim());
        if (Changed(AirPressure, was.AirPressure)) Row("barometricReferencePressure", AirPressure.Trim());
        return ops;
    }

    /// <summary>
    /// A dataset's F1 fuel loads for new laps or burn. Each base value is a
    /// reserve plus fuel for its laps — the reserve is what five laps take
    /// beyond five laps' burn — and the part beyond the reserve is scaled by
    /// the change in burn times laps, so an unchanged track keeps the game's
    /// numbers to the decimal, quirks and caps included. Qualifying fuel
    /// scales with the burn alone; the one-lap minimum is left as it is.
    /// </summary>
    private static IEnumerable<Op> FuelLoads(TrackSource track, string dataset, string? laps, double? burn)
    {
        var baseBurn = Number(track.BaseData(dataset, Burn));
        var baseLaps = track.BaseData(dataset, Laps);
        if (baseBurn is not > 0 || baseLaps is null) yield break;
        var nowBurn = burn ?? Number(track.Data(dataset, Burn)) ?? baseBurn.Value;
        var nowLaps = laps ?? track.Data(dataset, Laps) ?? baseLaps;
        var burnRatio = nowBurn / baseBurn.Value;

        var five = Number(track.BaseData(dataset, Fuel + "RaceFuelSettings_0.laps_5"));
        var reserve = five is { } f ? Math.Clamp(f - 5 * baseBurn.Value, 0, f) : 0;

        // A value whose share doesn't change is left as the game has it, and
        // one that does keeps the game's decimals (6.15 stays two places).
        Op? Scaled(string field, double ratio, double floor)
        {
            if (Math.Abs(ratio - 1) < 1e-9 || track.BaseData(dataset, field) is not { } text || Number(text) is not { } value) return null;
            var places = Math.Max(1, text.Trim().Split('.', ',').ElementAtOrDefault(1)?.TrimEnd('0').Length ?? 0);
            return new Op(dataset, field, Text(Math.Round(floor + Math.Max(0, value - floor) * ratio, Math.Min(places, 4))));
        }

        foreach (var q in new[] { "full", "one_shot", "short" })
            if (Scaled($"{Fuel}Qualifying.{q}", burnRatio, 0) is { } op) yield return op;

        // Race 0 is the sprint in a sprint weekend, and race 1 the race.
        var sprint = IsSprint(dataset);
        for (var race = 0; race < 2; race++)
        {
            var prefix = $"{Fuel}RaceFuelSettings_{race}.";
            if (track.BaseData(dataset, prefix + "laps_1") is null) continue;
            var lapsBefore = sprint && race == 0 ? Min(baseLaps) : Max(baseLaps);
            var lapsNow = sprint && race == 0 ? Min(nowLaps) : Max(nowLaps);
            var lapRatio = lapsBefore is > 0 && lapsNow is { } count ? (double)count / lapsBefore.Value : 1;

            foreach (var n in FuelLaps)
                if (Scaled($"{prefix}laps_{n}", burnRatio, reserve) is { } op) yield return op;
            foreach (var p in FuelPercents)
                if (Scaled($"{prefix}percent_{p}", burnRatio * lapRatio, reserve) is { } op) yield return op;
        }
    }

    // ── Checking ────────────────────────────────────────────────────────────

    /// <summary>What stops a package, one line each.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        void WholeNumber(string value, string what) { if (value.Trim().Length > 0 && Int(value) is not > 0) problems.Add($"{what} has to be a whole number."); }
        void Decimal(string value, string what) { if (value.Trim().Length > 0 && Number(value) is null) problems.Add($"{what} has to be a number."); }
        void Time(string value, string what) { if (value.Trim().Length > 0 && GameTime(value) is null) problems.Add($"{what} has to be a time like 14:30."); }

        WholeNumber(F1RaceLaps, "The F1 race laps");
        WholeNumber(F1SprintLaps, "The F1 sprint laps");
        WholeNumber(F2FeatureLaps, "The F2 feature race laps");
        WholeNumber(F2SprintLaps, "The F2 sprint laps");
        for (var i = 0; i < SessionTimes.Length; i++) Time(SessionTimes[i], $"The {SessionNames[i]} start");
        Decimal(F1PitLimit, "The F1 pit speed limit");
        Decimal(F2PitLimit, "The F2 pit speed limit");
        for (var i = 0; i < Weather.Length; i++) Decimal(Weather[i], $"The {WeatherNames[i].ToLowerInvariant()} weather");
        if (Weather.All(w => Number(w) is not null) && Math.Abs(Weather.Sum(w => Number(w)!.Value) - 100) > 0.5)
            problems.Add("The weather has to add up to 100%.");
        Decimal(TemperatureMin, "The lowest temperature");
        Decimal(TemperatureMax, "The highest temperature");
        if (Number(TemperatureMin) is { } low && Number(TemperatureMax) is { } high && low > high)
            problems.Add("The lowest temperature is above the highest.");
        Time(DaylightStart, "Sunrise");
        Time(DaylightEnd, "Sunset");
        Decimal(F1FuelBurn, "The F1 fuel burn");
        Decimal(F2FuelBurn, "The F2 fuel burn");
        Decimal(AiLapTime, "The AI lap time");
        if (Number(Latitude) is { } lat && Math.Abs(lat) > 90) problems.Add("The latitude has to be between -90 and 90.");
        else Decimal(Latitude, "The latitude");
        if (Number(Longitude) is { } lon && Math.Abs(lon) > 180) problems.Add("The longitude has to be between -180 and 180.");
        else Decimal(Longitude, "The longitude");
        Decimal(AirPressure, "The air pressure");
        return problems;
    }

    // ── Values ──────────────────────────────────────────────────────────────

    private static IEnumerable<string> Prefer(IEnumerable<string> sets, string first)
    {
        var list = sets.ToList();
        return list.Contains(first) ? list.Where(d => d == first).Concat(list.Where(d => d != first)) : list;
    }

    private static bool IsCompound(string? value) => value is not null && CompoundPattern().IsMatch(value.Trim());

    /// <summary>The numbers in a list like <c>19, 57, 57</c>.</summary>
    private static List<int> Ints(string? list) =>
        list is null ? [] : NumberPattern().Matches(list).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();

    private static int? Max(string? list) => Ints(list) is { Count: > 0 } n ? n.Max() : null;
    private static int? Min(string? list) => Ints(list).Where(n => n > 0).DefaultIfEmpty(-1).Min() is var m and > 0 ? m : null;

    /// <summary>
    /// A laps list with the base track's numbers swapped for new ones, in
    /// place, so the dataset's own layout and separators stay.
    /// </summary>
    private static string Swap(string list, int? from, int? to, int? sprintFrom, int? sprintTo) =>
        NumberPattern().Replace(list, m =>
        {
            var n = int.Parse(m.Value, CultureInfo.InvariantCulture);
            if (n == from && to is { } t) return t.ToString(CultureInfo.InvariantCulture);
            if (n == sprintFrom && sprintTo is { } s) return s.ToString(CultureInfo.InvariantCulture);
            return m.Value;
        });

    private static List<double> List(string? value) =>
        value is null ? [] : value.Split(',').Select(Number).Where(v => v is not null).Select(v => v!.Value).ToList();

    public static double? Number(string? value) =>
        double.TryParse(value?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static int? Int(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string Text(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The game's <c>14,30</c> (or <c>19,0</c>, or <c>14, 15</c>) as <c>14:30</c>.</summary>
    private static string ShowTime(string? value)
    {
        if (value is null || TimePattern().Match(value) is not { Success: true } m) return "";
        return $"{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture):00}:{int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture):00}";
    }

    /// <summary><c>14:30</c> (or <c>14,30</c>, <c>14.30</c>) as the game writes it, <c>14,30</c>; null when it isn't a time.</summary>
    private static string? GameTime(string value)
    {
        if (TimePattern().Match(value.Trim()) is not { Success: true } m) return null;
        var (h, min) = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        return h is >= 0 and < 24 && min is >= 0 and < 60 ? $"{h:00},{min:00}" : null;
    }

    [GeneratedRegex(@"^C\d$")]
    private static partial Regex CompoundPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^\s*(\d{1,2})\s*[:,.]\s*(\d{1,2})\s*$")]
    private static partial Regex TimePattern();
}

/// <summary>
/// A track's values as they will be once activated: the author's overrides
/// where it has them, else its base track's, with a manifest path like
/// <c>Session Start Times_*.p1_start_time</c> standing for every numbered one.
/// </summary>
public sealed partial class TrackSource(StockGame game, string baseTrack, string slotTrack,
                                        IReadOnlyList<TrackOverride> trackOverrides, IReadOnlyList<TrackOverride> overrides)
{
    private IReadOnlyList<string>? _datasets;

    public StockGame Game => game;
    public string BaseTrack => baseTrack;
    public string SlotTrack => slotTrack;
    public IReadOnlyList<string> Datasets => _datasets ??= game.DatasetsWith(baseTrack);

    public string? Row(string field) =>
        trackOverrides.LastOrDefault(o => Matches(o.Path, field))?.Value ?? game.TrackValue(slotTrack, field);

    public string? Data(string dataset, string field) =>
        overrides.LastOrDefault(o => (o.Datasets.Count == 0 || o.Datasets.Contains(dataset)) && Matches(o.Path, field))?.Value
        ?? game.TrackDataValue(dataset, baseTrack, field);

    public string? BaseRow(string field) => game.TrackValue(slotTrack, field);
    public string? BaseData(string dataset, string field) => game.TrackDataValue(dataset, baseTrack, field);

    /// <summary>Whether a manifest path names a field, a <c>_*</c> in it standing for any number.</summary>
    public static bool Matches(string path, string field) =>
        path == field || (path.Contains("_*", StringComparison.Ordinal)
                          && Regex.IsMatch(field, "^" + Regex.Escape(path).Replace(@"_\*", @"_\d+") + "$"));

    /// <summary>
    /// The fields a manifest path stands for where <paramref name="has"/> says
    /// they exist: itself, or for a <c>_*</c> path each numbered one.
    /// </summary>
    public static IReadOnlyList<string> Expand(string path, Func<string, bool> has)
    {
        if (!path.Contains("_*", StringComparison.Ordinal)) return has(path) ? [path] : [];
        var found = new List<string>();
        for (var i = 0; i < 16; i++)
        {
            var field = WildcardPattern().Replace(path, $"_{i}", 1);
            if (field.Contains("_*", StringComparison.Ordinal))
                found.AddRange(Expand(field, has));
            else if (has(field)) found.Add(field);
        }
        return found;
    }

    [GeneratedRegex(@"_\*")]
    private static partial Regex WildcardPattern();
}
