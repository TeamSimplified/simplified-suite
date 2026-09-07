using System.Diagnostics;
using EgoEngineLibrary.Data.GameDatabases;
using SimplifiedDriverSwapper.Core.Drivers;

// Acceptance gate for the database layer. Checks, in order:
//   1. the codec round-trips a real file byte-for-byte
//   2. the catalogue reads drivers and kit out of it
//   3. a seat assignment plans the writes it should
//   4. helmet metadata stays unique through a permutation
//   5. applying, saving and reloading yields exactly the planned values
//
//   DbVerify <gamedatabases.bin>

if (args.Length < 1) { Console.Error.WriteLine("usage: DbVerify <gamedatabases.bin>"); return 2; }

var path = args[0];
var season = args.Length > 1
    ? Season.All.First(x => x.Year == args[1])
    : Season.All[0];
var failures = new List<string>();
void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  {what}"); if (!ok) failures.Add(what); }

// ── 1. codec ────────────────────────────────────────────────────────────────
var sw = Stopwatch.StartNew();
var db = GameDatabase.Load(path);
sw.Stop();
Console.WriteLine($"file     : {Path.GetFileName(path)} ({new FileInfo(path).Length:N0} bytes)");
Console.WriteLine($"detected : {db.Cipher.Game}");
Console.WriteLine($"records  : {db.Records.Count:N0} in {sw.ElapsedMilliseconds} ms");

Console.WriteLine("\n── codec ──");
var tmp = Path.Combine(Path.GetTempPath(), "dbverify.bin");
db.Save(tmp);
Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(tmp)), "untouched save is byte-identical");

// ── 1b. backup ──────────────────────────────────────────────────────────────
Console.WriteLine("\n── backup ──");
{
    var dir = Path.Combine(Path.GetTempPath(), "dbverify_backup");
    Directory.CreateDirectory(dir);
    var sample = Path.Combine(dir, "gamedatabases.2025.bin");
    var expected = Path.Combine(dir, "gamedatabases.2025.bak");
    File.Delete(expected);
    File.WriteAllText(sample, "original");

    var first = GameDatabase.EnsureBackup(sample);
    Check(first == expected, $".bin becomes .bak ({Path.GetFileName(first ?? "null")})");
    Check(File.ReadAllText(expected) == "original", "backup holds the original bytes");

    // Simulate an edit, then reopening: the first backup must survive.
    File.WriteAllText(sample, "edited");
    var second = GameDatabase.EnsureBackup(sample);
    Check(second is null, "a second open makes no new backup");
    Check(File.ReadAllText(expected) == "original", "the original backup is not overwritten by the edit");

    Directory.Delete(dir, recursive: true);
}

// ── 2. catalogue ────────────────────────────────────────────────────────────
Console.WriteLine("\n── catalogue ──");
var cat = DriverCatalogue.Read(db);
Console.WriteLine($"  {cat.Drivers.Count} drivers, {cat.Helmets.Count} helmets, {cat.Suits.Count} suits, {cat.Gloves.Count} gloves");
Console.WriteLine($"  {cat.HomeHelmets.Count} drivers have a helmet");
Check(cat.Drivers.Count > 100, "drivers were read");
Check(cat.Helmets.Count > 100, "helmets were read");
Check(!cat.Drivers.Any(d => d.Id == "default_driver"), "default_driver is excluded");
foreach (var probe in new[] { "max_verstappen", "lando_norris", "lewis_hamilton" })
{
    var has = cat.HomeHelmets.TryGetValue(probe, out var idx);
    Console.WriteLine($"    {probe,-20} -> {(has ? cat.HelmetMetaByIndex[idx] : "(none)")}");
    Check(has, $"home helmet resolved for {probe}");
}

// Drivers with no helmet of their own get an unbranded one, so a seat never
// silently keeps the previous occupant's design.
Console.WriteLine($"  generic helmets: {cat.GenericHelmets.Count}"
                  + (cat.GenericHelmets.Count > 0
                     ? $"  e.g. {string.Join(", ", cat.GenericHelmets.Take(3).Select(r => cat.HelmetMetaByIndex[r]))}"
                     : ""));
Check(cat.GenericHelmets.Count >= season.Seats.Count,
      "there are enough generic helmets for a full grid");
{
    var without = cat.Drivers.Count(d => !cat.HasOwnHelmet(d.Id));
    Console.WriteLine($"  {without} of {cat.Drivers.Count} drivers have no helmet of their own");
}

// What the kit pickers will show, in the order the UI sorts them.
Console.WriteLine("\n── kit picker contents ──");
foreach (var (label, keys) in new (string, IEnumerable<string>)[]
         {
             ("helmets", cat.Helmets.Keys),
             ("suits", cat.Suits.Keys),
             ("gloves", cat.Gloves.Keys),
         })
{
    var sorted = keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    Console.WriteLine($"  {label}: {sorted.Count}");
    Console.WriteLine($"    first : {string.Join(" | ", sorted.Take(4))}");
    Console.WriteLine($"    last  : {string.Join(" | ", sorted.TakeLast(4))}");

    // Anything named after its own record slot rather than what it is.
    var generic = sorted.Where(k =>
        k.StartsWith("Helmet_", StringComparison.OrdinalIgnoreCase) ||
        k.StartsWith("Suit_", StringComparison.OrdinalIgnoreCase) ||
        k.StartsWith("Gloves_", StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"    record-named: {generic.Count}"
                      + (generic.Count > 0 ? $"  e.g. {string.Join(" | ", generic.Take(4))}" : ""));
}

// ── 3. seats ────────────────────────────────────────────────────────────────
Console.WriteLine("\n── seats ──");
var plan = new SeatPlan(db, cat, season);
var occupied = season.Seats.Count(s => db.Text(season.SeatPath(s.Slot, "driver")).Length > 0);
Console.WriteLine($"  {season.Label}: entrant_list_{season.EntrantList}, {occupied}/{season.Seats.Count} seats hold an explicit driver on disk");
Check(db.Find(season.SeatPath(0, "driver")) is not null, $"entrant_list_{season.EntrantList} exists in this database");

// Every grid has to be where its table says it is. The F2 26 update inserted a
// list at 20 and shifted every later one down, moving the 2026 grid from 33 to
// 34 — an app editing by the old number would have written a grid's worth of
// changes over f1_beta instead. Checking the name catches the next shift on the
// first run rather than after someone's edits have gone somewhere unintended.
Console.WriteLine("\n── grids are where their tables say ──");
foreach (var s in Season.All)
{
    var actual = s.FindIn(db);
    Console.WriteLine($"  {s.Label,-14} expects list {s.EntrantList,3} (\"{s.ListName}\"), " +
                      $"database has it at {(actual?.ToString() ?? "no such list")}");
    Check(s.IsWhereExpected(db), $"{s.ListName} is at entrant_list_{s.EntrantList}");

    // A seat's lookup string has to name a kit record the database carries, or
    // assigning a helmet writes a string nothing will ever match.
    var unmatched = s.Seats.Where(seat => !cat.Helmets.ContainsKey(seat.LookupString)).ToList();
    Check(unmatched.Count == 0,
        $"{s.ListName}: every seat's lookup string names a real helmet record"
        + (unmatched.Count > 0 ? $" — missing {string.Join(", ", unmatched.Take(4).Select(u => u.LookupString))}" : ""));

    // And the teams have to match the list seat for seat, or the table is
    // describing a different grid from the one it will edit.
    var wrongTeam = s.Seats
        .Where(seat => db.Text($"entrants.entrant_lists.entrant_list_{s.EntrantList}.entrant_{seat.Slot}.team") != seat.Team)
        .ToList();
    Check(wrongTeam.Count == 0,
        $"{s.ListName}: every seat's team matches the entrant list"
        + (wrongTeam.Count > 0 ? $" — first off at slot {wrongTeam[0].Slot}" : ""));
}

Console.WriteLine();
Check(!plan.HasChanges, "a freshly opened plan has no changes");
Check(plan.Plan(new KitOptions()).Total == 0, "and plans no writes");

// Pick two drivers who aren't on the grid already and do have a helmet, so the
// checks hold whether this database is vanilla or already edited.
var seated = season.Seats.Select(s => plan.Occupant(s.Slot)).ToHashSet(StringComparer.Ordinal);
var spare = cat.Drivers
    .Where(d => !seated.Contains(d.Id) && cat.HomeHelmets.ContainsKey(d.Id))
    .Select(d => d.Id)
    .Take(2)
    .ToList();

if (spare.Count < 2)
{
    Console.WriteLine("  (not enough unseated drivers with helmets to test with — skipping)");
    Console.WriteLine(failures.Count == 0 ? "\nPASS" : "\nFAIL");
    return failures.Count == 0 ? 0 : 1;
}

var (driverA, driverB) = (spare[0], spare[1]);
Console.WriteLine($"  using {driverA} and {driverB} (neither currently on the grid)");

var options = new KitOptions();
plan.Assign(0, driverA);
var p = plan.Plan(options);
Console.WriteLine($"  assigning {driverA} to seat 0 plans {p.Seats.Count} seat, {p.Helmets.Count} helmet, {p.Suits.Count} suit, {p.Gloves.Count} glove write(s)");
foreach (var w in p.Seats) Console.WriteLine($"    {w.Description}: {w.FromDisplay} -> {w.ToDisplay}");
foreach (var w in p.Helmets.Take(4)) Console.WriteLine($"    {w.Description}: {w.From} -> {w.To}");
Check(p.Seats.Any(w => w.Description == "Seat 0 driver" && w.To == driverA), "seat 0 driver is written");
Check(p.Seats.Any(w => w.Description == "Seat 0 number" && w.To == cat.Numbers[driverA]), "seat 0 takes his own number");

// Duplicates: put the same driver in a second seat, then resolve it.
plan.Assign(1, driverA);
Check(plan.Duplicates().ContainsKey(driverA), "duplicate detected when one driver holds two seats");
plan.Assign(1, driverB);
Check(!plan.Duplicates().ContainsKey(driverA), "duplicate clears once the second seat changes");

// Resetting a seat has to clear every override on it, not just the driver.
{
    var scratch = new SeatPlan(db, cat, season);
    scratch.Assign(2, driverA);
    scratch.SetNumberOverride(2, "42");
    scratch.SetHelmetOverride(2, cat.HomeHelmets[driverA]);
    Check(scratch.NumberOverride(2) == "42", "number override sticks before reset");

    scratch.Reset(2);
    Check(scratch.NumberOverride(2) is null, "reset clears the number override");
    Check(scratch.HelmetOverride(2) is null, "reset clears the helmet override");
    Check(scratch.DriverIsDefault(2), "reset puts the default driver back");
    Check(scratch.IsDefault(2), "reset leaves the seat fully stock");
}

// Each seat's kit lookup string has to exist, or that seat's kit edits would
// silently land on nothing.
{
    var missing = season.Seats.Where(x => !cat.Suits.ContainsKey(x.LookupString)).ToList();
    Console.WriteLine($"  seat kit strings present: {season.Seats.Count - missing.Count}/{season.Seats.Count}");
    foreach (var m in missing) Console.WriteLine($"    MISSING  seat {m.Slot}: {m.LookupString}");
    Check(missing.Count == 0, "every seat's kit lookup string exists in this database");
}

// Helmet-less drivers each get a different generic helmet, placed the same way
// any other helmet is so it shows during a race and not only in menus.
{
    var noHelmet = cat.Drivers.Where(d => !cat.HasOwnHelmet(d.Id) && !seated.Contains(d.Id))
                              .Select(d => d.Id).Take(3).ToList();
    if (noHelmet.Count == 3)
    {
        var scratch = new SeatPlan(db, cat, season);
        for (var i = 0; i < 3; i++) scratch.Assign(i + 3, noHelmet[i]);
        var sp = scratch.Plan(new KitOptions());

        Console.WriteLine($"  {string.Join(", ", noHelmet)} have no helmet of their own");
        var landed = new List<int>();
        foreach (var slot in new[] { 3, 4, 5 })
        {
            var helmet = scratch.EffectiveHelmet(slot);
            Console.WriteLine($"    seat {slot} -> {(helmet is { } h ? cat.HelmetMetaByIndex[h] : "(none)")}");
            if (helmet is { } v) landed.Add(v);
        }

        Check(landed.Count == 3, "each helmet-less driver gets one");
        Check(landed.Distinct().Count() == 3, "and no two of them get the same one");
        Check(landed.All(r => cat.GenericHelmets.Contains(r)), "all of them from the generic pool");
        Check(sp.Helmets.All(w => w.Path.EndsWith("._metadata_", StringComparison.Ordinal)),
              "placed by moving metadata, like any other helmet");

        // Stable between runs, or the preview would disagree with the save.
        var again = new SeatPlan(db, cat, season);
        for (var i = 0; i < 3; i++) again.Assign(i + 3, noHelmet[i]);
        Check(new[] { 3, 4, 5 }.All(slot => again.EffectiveHelmet(slot) == scratch.EffectiveHelmet(slot)),
              "and the same driver always lands on the same one");
    }
    else Console.WriteLine("  (not enough helmet-less drivers to test with)");
}

// ── 4. helmet permutation ───────────────────────────────────────────────────
Console.WriteLine("\n── helmet permutation ──");
p = plan.Plan(options);
var after = new Dictionary<int, string>(cat.HelmetMetaByIndex);
foreach (var w in p.Helmets) after[int.Parse(w.Description["Helmet_".Length..])] = w.To;
Check(after.Values.Distinct().Count() == after.Count, "every helmet metadata string is still unique");
Check(after.Count == cat.HelmetMetaByIndex.Count, "no helmet records gained or lost");
var seat0Lookup = season.Seats[0].LookupString;
var holder = after.FirstOrDefault(kv => kv.Value == seat0Lookup);
Check(holder.Value == seat0Lookup && holder.Key == cat.HomeHelmets[driverA],
      $"'{seat0Lookup}' now sits on {driverA}'s helmet record");
var displacedRecord = cat.Helmets[seat0Lookup];
Console.WriteLine($"    displaced record Helmet_{displacedRecord} now holds '{after[displacedRecord]}'");
Check(after[displacedRecord] == cat.HelmetMetaByIndex[cat.HomeHelmets[driverA]],
      $"the displaced record took {driverA}'s old string");

// A helmet override has to survive a save and reload — the seat's lookup string
// must end up on the chosen record, whatever the driver's own helmet is.
Console.WriteLine();
Console.WriteLine("── helmet override round trip ──");
{
    var chosen = cat.Helmets.Keys.First(k => k.StartsWith("classic-", StringComparison.Ordinal));
    var chosenRecord = cat.Helmets[chosen];

    var scratch = new SeatPlan(db, cat, season);
    scratch.Assign(7, driverA);                 // a driver who has his own helmet
    scratch.SetHelmetOverride(7, chosenRecord);

    var op = scratch.Plan(options);
    Console.WriteLine($"  seat 7: {driverA} (own helmet {cat.HelmetMetaByIndex[cat.HomeHelmets[driverA]]})");
    Console.WriteLine($"  override: {chosen} (Helmet_{chosenRecord})");
    foreach (var w in op.Helmets) Console.WriteLine($"    {w.Description}: {w.From} -> {w.To}");

    var lookup = season.Seats[7].LookupString;
    Check(op.Helmets.Any(w => w.Path == Season.HelmetPath(chosenRecord, "_metadata_") && w.To == lookup),
          "the override record takes the seat's lookup string");
    Check(!op.Helmets.Any(w => w.Path == Season.HelmetPath(cat.HomeHelmets[driverA], "_metadata_") && w.To == lookup),
          "the driver's own helmet does not take it instead");

    // And with the "helmet follows the driver" rule switched off.
    // Full path: apply, write to disk, reopen, and read the file back.
    {
        var fresh = GameDatabase.Load(path);
        var freshCat = DriverCatalogue.Read(fresh);
        var live = new SeatPlan(fresh, freshCat, season);
        live.Assign(7, driverA);
        live.SetHelmetOverride(7, freshCat.Helmets[chosen]);
        live.Apply(options);

        var saved = Path.Combine(Path.GetTempPath(), "dbverify_override.bin");
        fresh.SaveAtomic(saved);

        var back = GameDatabase.Load(saved);
        var backCat = DriverCatalogue.Read(back);
        var onFile = backCat.Helmets.TryGetValue(lookup, out var r) ? r : -1;

        Console.WriteLine($"  after save, '{lookup}' sits on Helmet_{onFile} " +
                          $"(override was Helmet_{freshCat.Helmets[chosen]})");
        Check(onFile == freshCat.Helmets[chosen], "the override survives save and reload on disk");
        File.Delete(saved);
    }

    // Reopening an edited file must still report what each seat wears. The
    // record's own metadata can't say — it was overwritten by the seat's lookup
    // string — so names come from the untouched reference instead.
    {
        var work = Path.Combine(Path.GetTempPath(), "dbverify_reopen.bin");
        File.Copy(path, work, overwrite: true);
        var reference = GameDatabase.Load(path);

        var edit = GameDatabase.Load(work);
        var editCat = DriverCatalogue.Read(edit, reference);
        var live = new SeatPlan(edit, editCat, season);
        live.Assign(7, driverA);
        live.SetHelmetOverride(7, editCat.Helmets[chosen], chosen);
        live.Apply(options);
        edit.SaveAtomic(work);

        // Fresh open, nothing carried over from the session that made the edit.
        var reopened = GameDatabase.Load(work);
        var reopenedCat = DriverCatalogue.Read(reopened, reference);
        var reopenedPlan = new SeatPlan(reopened, reopenedCat, season);

        var withRef = new SeatPlan(reopened, reopenedCat, season,
                                   new DatabaseSnapshot(reference, DriverCatalogue.Read(reference)));
        Console.WriteLine($"  reopened: seat 7 saved differences = {withRef.Saved(7)}");
        Check(withRef.Saved(7).Helmet, "a helmet changed in an earlier session still reads as changed");
        Check(withRef.Saved(7).Driver, "and so does the driver");
        Check(!withRef.IsDefault(7), "so the seat is not reported as stock");
        Check(withRef.Saved(0) == SavedDifferences.None, "an untouched seat reports no differences");

        // A helmet that followed a driver must still read as his after a save,
        // not as something picked by hand.
        {
            var followed = Path.Combine(Path.GetTempPath(), "dbverify_followed.bin");
            File.Copy(path, followed, overwrite: true);
            var swap = GameDatabase.Load(followed);
            var fCat = DriverCatalogue.Read(swap, reference);
            var fPlan = new SeatPlan(swap, fCat, season);
            fPlan.Assign(9, driverA);              // no override, just a swap
            fPlan.Apply(options);
            swap.SaveAtomic(followed);

            var fBack = GameDatabase.Load(followed);
            var fBackCat = DriverCatalogue.Read(fBack, reference);
            var fBackPlan = new SeatPlan(fBack, fBackCat, season,
                                         new DatabaseSnapshot(reference, DriverCatalogue.Read(reference)));
            Console.WriteLine($"  reopened after a plain swap: seat 9 helmet is {fBackPlan.SavedHelmet(9)}");
            Check(fBackPlan.SavedHelmet(9) == HelmetSource.Own,
                  "a helmet that followed the driver still reads as his own");
            File.Delete(followed);
        }

        // A transferred driver's team kit must survive reopening and re-saving.
        {
            var kitFile = Path.Combine(Path.GetTempPath(), "dbverify_kit.bin");
            File.Copy(path, kitFile, overwrite: true);
            var k = GameDatabase.Load(kitFile);
            var kCat = DriverCatalogue.Read(k, reference);
            var kPlan = new SeatPlan(k, kCat, season);
            kPlan.Assign(11, driverA);
            kPlan.Apply(options);
            k.SaveAtomic(kitFile);

            var refSnap = new DatabaseSnapshot(reference, DriverCatalogue.Read(reference));
            var lookup11 = season.Seats[11].LookupString;
            var teamKit = $"{season.Year}-{season.Seats[11].KitToken}";

            var reopen = GameDatabase.Load(kitFile);
            var reopenCat = DriverCatalogue.Read(reopen, reference);
            var reopenPlan = new SeatPlan(reopen, reopenCat, season, refSnap);

            var suitNow = reopenCat.Suits[lookup11].Id;
            var suitTeam = reopenCat.Suits.GetValueOrDefault(teamKit)?.Id;
            var suitStock = refSnap.Catalogue.Suits[lookup11].Id;
            Console.WriteLine($"  seat 11 suit: on file {suitNow}, team {suitTeam}, stock {suitStock}");
            Check(suitNow == suitTeam, "the saved suit is the team one");
            Check(reopenPlan.Saved(11).Suit, "and reads as changed from the backup");

            Console.WriteLine($"  reopened: suit reads {reopenPlan.SavedSuit(11)}, gloves {reopenPlan.SavedGloves(11)}");
            Check(reopenPlan.SavedSuit(11) == KitSource.Team, "the reopened suit reads as team kit, not as an override");
            Check(reopenPlan.SavedGloves(11) == KitSource.Team, "and so do the gloves");

            // Touch the seat and re-save: the team kit must not be undone.
            reopenPlan.Assign(11, driverA);
            var again = reopenPlan.Plan(options);
            foreach (var w in again.Suits) Console.WriteLine($"    would write {w.Description}: {w.FromDisplay} -> {w.ToDisplay}");
            Check(again.Suits.All(w => w.To != suitStock), "re-saving does not put the stock suit back");
            Check(again.Gloves.All(w => w.To != refSnap.Catalogue.Gloves[lookup11].Id),
                  "nor the stock gloves");

            File.Delete(kitFile);
        }

        // Clearing a change made in an earlier session must put the file back.
        {
            var refCatEarly = DriverCatalogue.Read(reference);
            var undo = new SeatPlan(reopened, reopenedCat, season,
                                    new DatabaseSnapshot(reference, refCatEarly));
            undo.ClearHelmetOverride(7);
            Check(undo.IsRestoringHelmet(7), "clearing schedules a restore");
            Check(undo.EffectiveHelmet(7) == refCatEarly.Helmets.GetValueOrDefault(season.Seats[7].LookupString, -1),
                  "and the seat immediately reports the stock helmet, before any save");
            undo.SetHelmetOverride(7, editCat.Helmets[chosen], chosen);
            Check(!undo.IsRestoringHelmet(7), "choosing again cancels the restore");

            undo.Reset(7);
            undo.Apply(options);

            var restored = Path.Combine(Path.GetTempPath(), "dbverify_restored.bin");
            reopened.SaveAtomic(restored);

            var cleared = GameDatabase.Load(restored);
            var afterCat = DriverCatalogue.Read(cleared, reference);
            var refCat = DriverCatalogue.Read(reference);
            var lookup7 = season.Seats[7].LookupString;

            Console.WriteLine($"  after clearing: '{lookup7}' back on Helmet_{afterCat.Helmets.GetValueOrDefault(lookup7, -1)} " +
                              $"(backup has Helmet_{refCat.Helmets.GetValueOrDefault(lookup7, -1)})");
            Check(afterCat.Helmets.GetValueOrDefault(lookup7, -1) == refCat.Helmets.GetValueOrDefault(lookup7, -2),
                  "clearing puts the seat's helmet back where the backup has it");
            Check(cleared.Text(season.SeatPath(7, "driver")) == reference.Text(season.SeatPath(7, "driver")),
                  "and the driver back to stock");

            var checkPlan = new SeatPlan(cleared, afterCat, season, new DatabaseSnapshot(reference, refCat));
            Check(!checkPlan.Saved(7).Any, "so the seat no longer differs from the backup at all");
            File.Delete(restored);
        }

        var worn = reopenedPlan.EffectiveHelmet(7);
        var wornName = worn is { } w2 ? reopenedCat.HelmetName(w2) : "(none)";
        Console.WriteLine($"  reopened: seat 7 reports '{wornName}'");
        Check(worn == editCat.Helmets[chosen], "a reopened file reports the record the seat actually wears");
        Check(wornName == chosen, "and names it by its design, not by the label it now carries");

        File.Delete(work);
    }

    var offPlan = scratch.Plan(new KitOptions { Helmet = false });
    Console.WriteLine($"  with the helmet rule off: {offPlan.Helmets.Count} helmet write(s)");
    Check(offPlan.Helmets.Any(w => w.Path == Season.HelmetPath(chosenRecord, "_metadata_") && w.To == lookup),
          "an explicit override still applies when the helmet rule is off");
}

// ── 5. apply, save, reload ──────────────────────────────────────────────────
Console.WriteLine("\n── apply / save / reload ──");
plan.SetNumberOverride(0, "7");
var applied = plan.Apply(options);
Console.WriteLine($"  applied {applied.Total} write(s)");
db.SaveAtomic(tmp);

var reloaded = GameDatabase.Load(tmp);
Check(reloaded.Records.Count == db.Records.Count, "record count survives the round trip");
Check(reloaded.Text(season.SeatPath(0, "driver")) == driverA, "seat 0 driver persisted");
Check(reloaded.Text(season.SeatPath(0, "car_number")) == "7", "seat 0 number override persisted");
Check(reloaded.Text(season.SeatPath(1, "driver")) == driverB, "seat 1 driver persisted");
foreach (var w in applied.Helmets.Take(3))
    Check(reloaded.Text(w.Path) == w.To, $"{w.Description} persisted as {w.To}");

// Nothing outside the plan may have moved.
var plannedPaths = applied.Seats.Concat(applied.Helmets).Concat(applied.Suits).Concat(applied.Gloves)
                          .Select(w => w.Path).ToHashSet(StringComparer.Ordinal);
var original = GameDatabase.Load(path);
var origByKey = original.Records.ToDictionary(r => r.Key);
var changedKeys = plannedPaths.Select(Fnv1a.Hash).ToHashSet();
var strayed = reloaded.Records.Count(r =>
    !changedKeys.Contains(r.Key) && origByKey.TryGetValue(r.Key, out var o) && !o.Data.AsSpan().SequenceEqual(r.Data));
Console.WriteLine($"  {strayed} record(s) changed outside the plan");
Check(strayed == 0, "no records changed outside the plan");

File.Delete(tmp);
Console.WriteLine();
Console.WriteLine(failures.Count == 0 ? "PASS" : $"FAIL — {failures.Count}: {string.Join("; ", failures)}");
return failures.Count == 0 ? 0 : 1;
