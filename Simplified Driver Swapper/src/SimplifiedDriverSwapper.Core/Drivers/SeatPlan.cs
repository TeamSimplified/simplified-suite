using SimplifiedDriverSwapper.Core.Database;

namespace SimplifiedDriverSwapper.Core.Drivers;

/// <summary>What a seat's kit follows once someone other than its default driver sits in it.</summary>
public sealed class KitOptions
{
    /// <summary>Move the occupant's own helmet onto the seat.</summary>
    public bool Helmet { get; set; } = true;

    /// <summary>Swap the seat's suit for the plain team one.</summary>
    public bool Suit { get; set; } = true;

    /// <summary>Swap the seat's gloves for the plain team ones.</summary>
    public bool Gloves { get; set; } = true;
}

/// <summary>
/// A single value the save will change. Carries the record path so applying is
/// just writing each one — the plan is the instruction, not a report to parse
/// back.
/// </summary>
/// <param name="Path">Database path of the record to write.</param>
/// <param name="Description">Which record, in the user's terms.</param>
public sealed record PlannedWrite(string Path, string Description, string From, string To)
{
    private static string Show(string value) => value.Length == 0 ? "(empty)" : value;

    public string FromDisplay => Show(From);
    public string ToDisplay => Show(To);
}

/// <summary>A database and its catalogue, read together.</summary>
public sealed record DatabaseSnapshot(GameDatabase Database, DriverCatalogue Catalogue);

/// <summary>
/// What a seat already differs by on disk, against an untouched copy.
///
/// Needed because edits are indistinguishable from stock once saved: nothing
/// records that a seat was changed, so without something to compare against, a
/// reopened file looks untouched.
/// </summary>
public sealed record SavedDifferences(bool Driver, bool Number, bool Helmet, bool Suit, bool Gloves)
{
    public static readonly SavedDifferences None = new(false, false, false, false, false);
    public bool Any => Driver || Number || Helmet || Suit || Gloves;
}

/// <summary>Where the helmet a seat is already wearing came from.</summary>
public enum HelmetSource
{
    /// <summary>Unchanged from the backup.</summary>
    Stock,

    /// <summary>The occupant's own, as a driver swap would have brought.</summary>
    Own,

    /// <summary>One of the game's unbranded helmets, as a driver with none of his own gets.</summary>
    Generic,

    /// <summary>Something picked deliberately — neither of the above.</summary>
    Chosen,
}

/// <summary>Where the suit or gloves a seat is already wearing came from.</summary>
public enum KitSource
{
    /// <summary>Unchanged from the backup.</summary>
    Stock,

    /// <summary>The plain team kit, as a driver swap would have applied.</summary>
    Team,

    /// <summary>Something picked deliberately.</summary>
    Chosen,
}

/// <summary>Everything a save would do, grouped so the UI can report it.</summary>
public sealed class SavePlan
{
    public List<PlannedWrite> Seats { get; } = [];
    public List<PlannedWrite> Helmets { get; } = [];
    public List<PlannedWrite> Suits { get; } = [];
    public List<PlannedWrite> Gloves { get; } = [];
    public List<string> Notes { get; } = [];

    public int Total => Seats.Count + Helmets.Count + Suits.Count + Gloves.Count;
}

/// <summary>
/// The grid as the user is arranging it, and what writing it back would change.
///
/// Two rules shape everything here:
///
/// <para>A seat nobody has touched is left exactly as it is on disk. In a vanilla
/// database that means empty — the game fills in its own default lineup — so
/// writing a default driver into it explicitly would be a change, not a no-op.</para>
///
/// <para>The database has no notion of "this driver's helmet". A helmet is shown
/// because its metadata matches the seat's lookup string, so putting a driver's
/// helmet on a seat means moving that string onto his record, and giving the
/// record that held it the incoming record's old string — a swap, not an
/// overwrite, so the strings stay unique.</para>
/// </summary>
public sealed class SeatPlan
{
    private readonly GameDatabase _db;
    private readonly DriverCatalogue _catalogue;
    private readonly Season _season;

    private readonly string[] _occupants;
    private readonly string[] _occupantsOnDisk;

    private readonly Dictionary<int, int> _helmetOverride = [];

    /// <summary>
    /// What the chosen helmet was called when it was picked.
    ///
    /// Kept because the name can't be recovered afterwards: assigning a helmet
    /// writes the seat's lookup string over the chosen record's metadata, so the
    /// record stops being named after the design it carries.
    /// </summary>
    private readonly Dictionary<int, string> _helmetOverrideLabel = [];
    private readonly Dictionary<int, string> _numberOverride = [];
    private readonly Dictionary<int, string> _suitOverride = [];
    private readonly Dictionary<int, string> _gloveOverride = [];

    /// <summary>
    /// Seats touched this session. Only these get written, which is what keeps an
    /// untouched database byte-identical.
    /// </summary>
    private readonly HashSet<int> _dirty = [];

    private readonly Dictionary<int, SavedDifferences> _saved = [];
    private DatabaseSnapshot? _reference;

    /// <summary>
    /// Seats asked to go back to how the backup has them. Separate from clearing
    /// an override, which only drops a choice made this session — these were
    /// written in an earlier one, so undoing them means writing the stock values
    /// back rather than declining to write new ones.
    /// </summary>
    private readonly HashSet<int> _restoreNumber = [];
    private readonly HashSet<int> _restoreHelmet = [];
    private readonly HashSet<int> _restoreSuit = [];
    private readonly HashSet<int> _restoreGloves = [];

    public SeatPlan(
        GameDatabase db, DriverCatalogue catalogue, Season season, DatabaseSnapshot? reference = null)
    {
        _db = db;
        _catalogue = catalogue;
        _season = season;

        _occupants = new string[season.Seats.Count];
        for (var i = 0; i < _occupants.Length; i++)
        {
            var seat = season.Seats[i];
            var onDisk = db.Text(season.SeatPath(seat.Slot, "driver"));
            _occupants[i] = onDisk.Length > 0 ? onDisk : seat.DefaultDriver;
        }

        // Who was here when we opened the file. Lets a driver placed *this*
        // session (bring his own number) be told apart from one already saved
        // earlier (leave the saved number alone).
        _occupantsOnDisk = (string[])_occupants.Clone();

        _reference = reference;
        if (reference is not null) FindSavedDifferences(reference);
    }

    /// <summary>
    /// Compare each seat against the untouched copy, so edits made in an earlier
    /// session still read as changes.
    ///
    /// Kit is compared by the design a seat ends up on rather than by which
    /// record serves it, since a helmet can be changed either by moving metadata
    /// or by redirecting an id, and both should count.
    /// </summary>
    private void FindSavedDifferences(DatabaseSnapshot reference)
    {
        foreach (var seat in _season.Seats)
        {
            var lookup = seat.LookupString;

            _saved[seat.Slot] = new SavedDifferences(
                Driver: _db.Text(_season.SeatPath(seat.Slot, "driver"))
                        != reference.Database.Text(_season.SeatPath(seat.Slot, "driver")),
                Number: _db.Text(_season.SeatPath(seat.Slot, "car_number"), "255")
                        != reference.Database.Text(_season.SeatPath(seat.Slot, "car_number"), "255"),
                Helmet: HelmetDesign(_catalogue, lookup) != HelmetDesign(reference.Catalogue, lookup),
                Suit: KitId(_catalogue.Suits, lookup) != KitId(reference.Catalogue.Suits, lookup),
                Gloves: KitId(_catalogue.Gloves, lookup) != KitId(reference.Catalogue.Gloves, lookup));
        }

        static string? HelmetDesign(DriverCatalogue catalogue, string lookup) =>
            catalogue.Helmets.TryGetValue(lookup, out var record)
                ? catalogue.HelmetIds.GetValueOrDefault(record)
                : null;

        static string? KitId(Dictionary<string, DriverCatalogue.KitEntry> table, string lookup) =>
            table.TryGetValue(lookup, out var entry) ? entry.Id : null;
    }

    /// <summary>How this seat already differs from the untouched copy.</summary>
    public SavedDifferences Saved(int slot) => _saved.GetValueOrDefault(slot, SavedDifferences.None);

    /// <summary>
    /// Where the suit already on a seat came from — the team kit a driver swap
    /// applies, or something chosen. Same reasoning as the helmet: once saved,
    /// both are only "not what the backup has".
    /// </summary>
    public KitSource SavedSuit(int slot) =>
        SavedKit(slot, Saved(slot).Suit, _catalogue.Suits);

    /// <summary>Where the gloves already on a seat came from.</summary>
    public KitSource SavedGloves(int slot) =>
        SavedKit(slot, Saved(slot).Gloves, _catalogue.Gloves);

    private KitSource SavedKit(int slot, bool differs, Dictionary<string, DriverCatalogue.KitEntry> table)
    {
        if (!differs || _reference is null) return KitSource.Stock;

        var seat = _season.Seats[slot];
        if (!table.TryGetValue(seat.LookupString, out var current) || current.Id is null) return KitSource.Chosen;

        return table.TryGetValue($"{_season.Year}-{seat.KitToken}", out var team) && team.Id == current.Id
            ? KitSource.Team
            : KitSource.Chosen;
    }

    /// <summary>
    /// Where the helmet already on a seat came from.
    ///
    /// Once saved, a helmet that followed a driver and one that was picked by
    /// hand are both just "not what the backup has". They're told apart by what
    /// the seat now wears: the design its occupant would have brought, the plain
    /// stand-in, or neither.
    ///
    /// The comparison is against the backup's idea of whose helmet is whose,
    /// because assigning one renames records — in the saved file the occupant no
    /// longer appears to own the design he brought with him.
    /// </summary>
    public HelmetSource SavedHelmet(int slot)
    {
        if (!Saved(slot).Helmet || _reference is null) return HelmetSource.Stock;

        var lookup = _season.Seats[slot].LookupString;
        if (!_catalogue.Helmets.TryGetValue(lookup, out var record)) return HelmetSource.Chosen;

        var design = _catalogue.HelmetIds.GetValueOrDefault(record);
        if (design is null) return HelmetSource.Chosen;

        var occupant = _occupants[slot];
        if (_reference.Catalogue.HomeHelmets.TryGetValue(occupant, out var own)
            && _reference.Catalogue.HelmetIds.GetValueOrDefault(own) == design)
            return HelmetSource.Own;

        if (_reference.Catalogue.GenericHelmetDesigns.Contains(design)) return HelmetSource.Generic;

        return HelmetSource.Chosen;
    }

        /// <summary>The grid being edited.</summary>
    public Season Season => _season;

    public IReadOnlyList<Season.Seat> Seats => _season.Seats;

    public bool HasChanges => _dirty.Count > 0;

    /// <summary>
    /// Seats touched this session — the ones a save would write. Exposed so the
    /// same edits can be replayed onto another copy of the database; replaying
    /// only the occupants would miss a seat that was reset back to its default,
    /// and would wrongly mark seats nobody touched.
    /// </summary>
    public IReadOnlyCollection<int> TouchedSeats => _dirty;

    public string Occupant(int slot) => _occupants[slot];

    public bool DriverIsDefault(int slot) => _occupants[slot] == _season.Seats[slot].DefaultDriver;

    /// <summary>
    /// True when driver, helmet, number, suit and gloves are all stock — both
    /// unchanged this session and matching the untouched copy.
    /// </summary>
    public bool IsDefault(int slot) =>
        DriverIsDefault(slot)
        && !_helmetOverride.ContainsKey(slot)
        && !_numberOverride.ContainsKey(slot)
        && !_suitOverride.ContainsKey(slot)
        && !_gloveOverride.ContainsKey(slot)
        && !Saved(slot).Any;

    // ── Arranging ───────────────────────────────────────────────────────────

    public void Assign(int slot, string driverId)
    {
        _occupants[slot] = driverId;
        _dirty.Add(slot);
    }

    public void Reset(int slot)
    {
        _occupants[slot] = _season.Seats[slot].DefaultDriver;
        _helmetOverride.Remove(slot);
        _helmetOverrideLabel.Remove(slot);
        _numberOverride.Remove(slot);
        _suitOverride.Remove(slot);
        _gloveOverride.Remove(slot);

        // Anything an earlier session wrote has to be written back, not just
        // left alone — the seat is only stock once the file says so.
        var saved = Saved(slot);
        if (saved.Number) _restoreNumber.Add(slot);
        if (saved.Helmet) _restoreHelmet.Add(slot);
        if (saved.Suit) _restoreSuit.Add(slot);
        if (saved.Gloves) _restoreGloves.Add(slot);

        _dirty.Add(slot);
    }

    /// <summary>
    /// Which parts of this seat are scheduled to go back to how the backup has
    /// them. The view needs these to show what the seat will be, rather than
    /// what the file still says until the restore is written.
    /// </summary>
    public bool IsRestoringNumber(int slot) => _restoreNumber.Contains(slot);
    public bool IsRestoringHelmet(int slot) => _restoreHelmet.Contains(slot);
    public bool IsRestoringSuit(int slot) => _restoreSuit.Contains(slot);
    public bool IsRestoringGloves(int slot) => _restoreGloves.Contains(slot);

    public bool IsRestoring(int slot) =>
        IsRestoringNumber(slot) || IsRestoringHelmet(slot) || IsRestoringSuit(slot) || IsRestoringGloves(slot);

    public int? HelmetOverride(int slot) => _helmetOverride.TryGetValue(slot, out var v) ? v : null;
    public string? NumberOverride(int slot) => _numberOverride.GetValueOrDefault(slot);
    public string? SuitOverride(int slot) => _suitOverride.GetValueOrDefault(slot);
    public string? GloveOverride(int slot) => _gloveOverride.GetValueOrDefault(slot);

    public void SetHelmetOverride(int slot, int helmetIndex, string? label = null)
    {
        _restoreHelmet.Remove(slot);
        _helmetOverride[slot] = helmetIndex;
        if (label is null) _helmetOverrideLabel.Remove(slot);
        else _helmetOverrideLabel[slot] = label;
        _dirty.Add(slot);
    }

    public void ClearHelmetOverride(int slot)
    {
        _helmetOverride.Remove(slot);
        _helmetOverrideLabel.Remove(slot);
        if (Saved(slot).Helmet) _restoreHelmet.Add(slot);
        _dirty.Add(slot);
    }

    /// <summary>The chosen helmet's name, or null when it wasn't recorded.</summary>
    public string? HelmetOverrideLabel(int slot) => _helmetOverrideLabel.GetValueOrDefault(slot);

    public void SetNumberOverride(int slot, string number) { _restoreNumber.Remove(slot); _numberOverride[slot] = number; _dirty.Add(slot); }
    public void ClearNumberOverride(int slot)
    {
        _numberOverride.Remove(slot);
        if (Saved(slot).Number) _restoreNumber.Add(slot);
        _dirty.Add(slot);
    }

    public void SetSuitOverride(int slot, string suitMetadata) { _restoreSuit.Remove(slot); _suitOverride[slot] = suitMetadata; _dirty.Add(slot); }
    public void ClearSuitOverride(int slot)
    {
        _suitOverride.Remove(slot);
        if (Saved(slot).Suit) _restoreSuit.Add(slot);
        _dirty.Add(slot);
    }

    public void SetGloveOverride(int slot, string gloveMetadata) { _restoreGloves.Remove(slot); _gloveOverride[slot] = gloveMetadata; _dirty.Add(slot); }
    public void ClearGloveOverride(int slot)
    {
        _gloveOverride.Remove(slot);
        if (Saved(slot).Gloves) _restoreGloves.Add(slot);
        _dirty.Add(slot);
    }

    // ── Derived state for display ───────────────────────────────────────────

    private string NumberOnDisk(int slot) => _db.Text(_season.SeatPath(slot, "car_number"), "255");

    /// <summary>The number this seat will end up with. Mirrors what <see cref="Plan"/> writes.</summary>
    public string EffectiveNumber(int slot)
    {
        if (_numberOverride.TryGetValue(slot, out var forced)) return forced;

        if (_restoreNumber.Contains(slot) && _reference is not null)
            return _reference.Database.Text(_season.SeatPath(slot, "car_number"), "255");

        if (!DriverIsDefault(slot) && _occupants[slot] != _occupantsOnDisk[slot])
            return _catalogue.Numbers.GetValueOrDefault(_occupants[slot], "255");

        return NumberOnDisk(slot);
    }

    /// <summary>
    /// The helmet record this seat will show, or null when it has none.
    ///
    /// A seat nobody has touched reports what the database already has on it —
    /// the record carrying its lookup string — rather than what its occupant
    /// would bring. Those differ in a file that has been saved before, and
    /// claiming the latter showed helmets the game isn't using.
    /// </summary>
    /// <summary>
    /// Which unbranded helmet each seat's occupant gets, for drivers who have
    /// none of their own.
    ///
    /// Picked from the driver's own id so it stays put between refreshes rather
    /// than moving about, and spread across the pool so two drivers don't turn
    /// up in the same one. Helmets already spoken for — an explicit choice, or
    /// some driver's own — are left alone.
    /// </summary>
    private Dictionary<int, int> AssignGenericHelmets()
    {
        var assigned = new Dictionary<int, int>();
        var pool = _catalogue.GenericHelmets;
        if (pool.Count == 0) return assigned;

        var taken = new HashSet<int>();
        foreach (var seat in _season.Seats)
        {
            if (HelmetOverride(seat.Slot) is { } chosen) taken.Add(chosen);
            else if (_catalogue.OwnHelmet(_occupants[seat.Slot]) is { } own) taken.Add(own);
        }

        foreach (var seat in _season.Seats)
        {
            var occupant = _occupants[seat.Slot];
            if (HelmetOverride(seat.Slot) is not null) continue;
            if (_catalogue.HasOwnHelmet(occupant)) continue;

            // Start somewhere that depends only on the driver, then walk on until
            // something free turns up.
            var start = (int)(Fnv1a.Hash(occupant) % (ulong)pool.Count);
            for (var step = 0; step < pool.Count; step++)
            {
                var candidate = pool[(start + step) % pool.Count];
                if (!taken.Add(candidate)) continue;
                assigned[seat.Slot] = candidate;
                break;
            }
        }

        return assigned;
    }

    public int? EffectiveHelmet(int slot)
    {
        if (_helmetOverride.TryGetValue(slot, out var forced)) return forced;

        if (_restoreHelmet.Contains(slot) && _reference is not null
            && _reference.Catalogue.Helmets.TryGetValue(_season.Seats[slot].LookupString, out var stock))
            return stock;

        if (_dirty.Contains(slot))
        {
            if (_catalogue.OwnHelmet(_occupants[slot]) is { } own) return own;
            var generic = AssignGenericHelmets().GetValueOrDefault(slot, -1);
            return generic >= 0 ? generic : null;
        }

        return _catalogue.Helmets.TryGetValue(_season.Seats[slot].LookupString, out var onFile)
            ? onFile
            : _catalogue.OwnHelmet(_occupants[slot]);
    }

    /// <summary>
    /// Re-state choices that are already written, so the view keeps showing them
    /// after a save without counting them as pending.
    ///
    /// They can't be recovered by reading the file back. Assigning a helmet moves
    /// the seat's lookup string onto the chosen record, which overwrites the name
    /// that said what that record was — so once saved, nothing in the database
    /// still identifies the design by name, only the record carrying it.
    /// </summary>
    public void AdoptAsSaved(SeatPlan previous)
    {
        foreach (var slot in previous._helmetOverride.Keys) _helmetOverride[slot] = previous._helmetOverride[slot];
        foreach (var slot in previous._helmetOverrideLabel.Keys) _helmetOverrideLabel[slot] = previous._helmetOverrideLabel[slot];
        foreach (var slot in previous._numberOverride.Keys) _numberOverride[slot] = previous._numberOverride[slot];
        foreach (var slot in previous._suitOverride.Keys) _suitOverride[slot] = previous._suitOverride[slot];
        foreach (var slot in previous._gloveOverride.Keys) _gloveOverride[slot] = previous._gloveOverride[slot];
        _dirty.Clear();
    }

    /// <summary>True when this driver has a helmet of his own rather than a generic one.</summary>
    public bool HasOwnHelmet(string driverId) => _catalogue.HasOwnHelmet(driverId);

    /// <summary>True when the seat can be given any helmet at all.</summary>
    public bool HelmetAvailable(string driverId) =>
        _catalogue.HasOwnHelmet(driverId) || _catalogue.GenericHelmets.Count > 0;

    /// <summary>
    /// Drivers sitting in more than one seat. Untouched seats count: the game
    /// shows their default driver even though the record is empty, so leaving a
    /// duplicate would put the same driver on the grid twice.
    /// </summary>
    public Dictionary<string, List<int>> Duplicates()
    {
        var seen = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var slot = 0; slot < _occupants.Length; slot++)
        {
            if (!seen.TryGetValue(_occupants[slot], out var slots))
                seen[_occupants[slot]] = slots = [];
            slots.Add(slot);
        }
        return seen.Where(kv => kv.Value.Count > 1).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    // ── Planning the write ──────────────────────────────────────────────────

    public SavePlan Plan(KitOptions options)
    {
        var plan = new SavePlan();
        PlanSeats(plan);
        PlanHelmets(plan, options);
        PlanKits(plan, options);
        return plan;
    }

    private void PlanSeats(SavePlan plan)
    {
        foreach (var seat in _season.Seats)
        {
            if (!_dirty.Contains(seat.Slot)) continue;

            var driverOnDisk = _db.Text(_season.SeatPath(seat.Slot, "driver"));
            var numberOnDisk = NumberOnDisk(seat.Slot);
            var (wantDriver, wantNumber) = TargetFor(seat.Slot);

            if (wantDriver != driverOnDisk)
                plan.Seats.Add(new PlannedWrite(
                    _season.SeatPath(seat.Slot, "driver"),
                    $"Seat {seat.Slot} driver", driverOnDisk, wantDriver));

            if (wantNumber != numberOnDisk)
                plan.Seats.Add(new PlannedWrite(
                    _season.SeatPath(seat.Slot, "car_number"),
                    $"Seat {seat.Slot} number", numberOnDisk, wantNumber));
        }
    }

    /// <summary>
    /// Rearrange helmet metadata so each touched seat shows the helmet it should.
    ///
    /// Done as a sequence of swaps: the incoming record takes the seat's lookup
    /// string, and whichever record held that string takes the incoming record's
    /// old one. That keeps every string on exactly one record, so the displaced
    /// driver's helmet stays valid even if his seat is never touched.
    /// </summary>
    private void PlanHelmets(SavePlan plan, KitOptions options)
    {
        var generics = AssignGenericHelmets();

        var metaByIndex = new Dictionary<int, string>(_catalogue.HelmetMetaByIndex);
        var indexByMeta = new Dictionary<string, int>(_catalogue.Helmets, StringComparer.Ordinal);

        foreach (var seat in _season.Seats)
        {
            if (!_dirty.Contains(seat.Slot)) continue;

            if (_restoreHelmet.Contains(seat.Slot)) { PlanHelmetRestore(plan, seat); continue; }

            // An explicit choice applies either way; the follow-the-driver
            // behaviour is what the rule switches off. Same as suits and gloves,
            // where an override also wins over the team rule being off.
            var mover = HelmetOverride(seat.Slot);
            if (mover is null)
            {
                if (!options.Helmet) continue;

                // His own, or an unbranded one when he hasn't got one. Both go on
                // the same way, which is what makes them show during a race.
                mover = _catalogue.OwnHelmet(_occupants[seat.Slot])
                        ?? generics.GetValueOrDefault(seat.Slot, -1);
                if (mover < 0) continue;
            }

            var lookup = seat.LookupString;
            var holder = indexByMeta.TryGetValue(lookup, out var h) ? h : (int?)null;
            if (holder == mover) continue;   // already where it needs to be

            var moverOldMeta = metaByIndex[mover.Value];

            if (holder is not null)
            {
                metaByIndex[holder.Value] = moverOldMeta;
                indexByMeta[moverOldMeta] = holder.Value;
            }
            else
            {
                indexByMeta.Remove(moverOldMeta);
            }

            metaByIndex[mover.Value] = lookup;
            indexByMeta[lookup] = mover.Value;
        }

        foreach (var (index, meta) in metaByIndex.OrderBy(kv => kv.Key))
        {
            var original = _catalogue.HelmetMetaByIndex.GetValueOrDefault(index, "?");
            if (original != meta)
                plan.Helmets.Add(new PlannedWrite(
                    Season.HelmetPath(index, "_metadata_"), $"Helmet_{index}", original, meta));
        }

    }

    /// <summary>
    /// Put a seat's helmet back to how the backup has it.
    ///
    /// Undoes both ways a helmet can have been changed: the record now carrying
    /// the seat's lookup string gets its original name and design back, and the
    /// record the backup says should carry that string gets it returned. That is
    /// the inverse of the move that placed it, so the pair ends up as it started.
    /// </summary>
    private void PlanHelmetRestore(SavePlan plan, Season.Seat seat)
    {
        if (_reference is null) return;

        var lookup = seat.LookupString;
        var current = _catalogue.Helmets.GetValueOrDefault(lookup, -1);
        var original = _reference.Catalogue.Helmets.GetValueOrDefault(lookup, -1);

        if (current >= 0)
        {
            var wasNamed = _reference.Catalogue.HelmetMetaByIndex.GetValueOrDefault(current);
            if (wasNamed is not null && wasNamed != _catalogue.HelmetMetaByIndex.GetValueOrDefault(current))
                plan.Helmets.Add(new PlannedWrite(
                    Season.HelmetPath(current, "_metadata_"), $"Helmet_{current}",
                    _catalogue.HelmetMetaByIndex.GetValueOrDefault(current, ""), wasNamed));

            var wasDesign = _reference.Catalogue.HelmetIds.GetValueOrDefault(current);
            if (wasDesign is not null && wasDesign != _catalogue.HelmetIds.GetValueOrDefault(current))
                plan.Helmets.Add(new PlannedWrite(
                    Season.HelmetPath(current, "id"), $"Seat {seat.Slot} helmet",
                    _catalogue.HelmetIds.GetValueOrDefault(current, ""), wasDesign));
        }

        if (original >= 0 && original != current
            && _catalogue.HelmetMetaByIndex.GetValueOrDefault(original) != lookup)
        {
            plan.Helmets.Add(new PlannedWrite(
                Season.HelmetPath(original, "_metadata_"), $"Helmet_{original}",
                _catalogue.HelmetMetaByIndex.GetValueOrDefault(original, ""), lookup));
        }
    }

    private void PlanKits(SavePlan plan, KitOptions options)
    {
        PlanKit(plan.Suits, _catalogue.Suits, _suitOverride, _restoreSuit, options.Suit, "suits", "Suit", "suit");
        PlanKit(plan.Gloves, _catalogue.Gloves, _gloveOverride, _restoreGloves, options.Gloves, "gloves", "Gloves", "gloves");

        void PlanKit(
            List<PlannedWrite> writes,
            Dictionary<string, DriverCatalogue.KitEntry> table,
            Dictionary<int, string> overrides,
            HashSet<int> restoring,
            bool followTeam,
            string directory, string className, string label)
        {
            foreach (var seat in _season.Seats)
            {
                if (!_dirty.Contains(seat.Slot)) continue;

                // The record being edited is always the seat's own; only the id
                // it points at changes.
                if (!table.TryGetValue(seat.LookupString, out var record)) continue;

                // Back to the backup's value, whatever an earlier session wrote.
                if (restoring.Contains(seat.Slot) && _reference is not null)
                {
                    var referenceTable = label == "suit" ? _reference.Catalogue.Suits : _reference.Catalogue.Gloves;
                    if (referenceTable.TryGetValue(seat.LookupString, out var was)
                        && was.Id is not null && was.Id != record.Id)
                    {
                        writes.Add(new PlannedWrite(
                            Season.KitPath(directory, className, record.Index, "id"),
                            $"Seat {seat.Slot} {label}", record.Id ?? "", was.Id));
                    }
                    continue;
                }

                string? wantId;
                if (overrides.TryGetValue(seat.Slot, out var chosen))
                {
                    if (!table.TryGetValue(chosen, out var target))
                    {
                        plan.Notes.Add($"Seat {seat.Slot}: {label} '{chosen}' isn't in this database — skipped.");
                        continue;
                    }
                    wantId = target.Id;
                }
                else if (!followTeam)
                {
                    continue;   // no override and the team rule is off: leave it alone
                }
                else if (DriverIsDefault(seat.Slot))
                {
                    wantId = record.Id;   // stock kit restored
                }
                else
                {
                    if (!table.TryGetValue($"{_season.Year}-{seat.KitToken}", out var generic)) continue;
                    wantId = generic.Id;
                }

                if (wantId is not null && wantId != record.Id)
                    writes.Add(new PlannedWrite(
                        Season.KitPath(directory, className, record.Index, "id"),
                        $"Seat {seat.Slot} {label}", record.Id ?? "", wantId));
            }
        }
    }

    // ── Committing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Apply the plan to the in-memory database. Nothing reaches disk until the
    /// database itself is saved.
    /// </summary>
    public SavePlan Apply(KitOptions options)
    {
        var plan = Plan(options);

        foreach (var write in plan.Seats.Concat(plan.Helmets).Concat(plan.Suits).Concat(plan.Gloves))
            SetText(write.Path, write.To);

        return plan;
    }

    /// <summary>The driver and number a seat should end up with. Single source for both plan and apply.</summary>
    private (string Driver, string Number) TargetFor(int slot)
    {
        var driverOnDisk = _db.Text(_season.SeatPath(slot, "driver"));
        var numberOnDisk = NumberOnDisk(slot);

        string driver, number;
        if (DriverIsDefault(slot))
        {
            driver = "";
            number = driverOnDisk.Length > 0 ? "255" : numberOnDisk;
        }
        else
        {
            driver = _occupants[slot];
            number = _occupants[slot] != _occupantsOnDisk[slot]
                ? _catalogue.Numbers.GetValueOrDefault(_occupants[slot], "255")
                : numberOnDisk;
        }

        if (_restoreNumber.Contains(slot) && _reference is not null)
            number = _reference.Database.Text(_season.SeatPath(slot, "car_number"), "255");

        if (_numberOverride.TryGetValue(slot, out var forced)) number = forced;
        return (driver, number);
    }

    private void SetText(string path, string value)
    {
        var record = _db.Find(path);
        if (record is not null) record.Text = value;
    }
}
