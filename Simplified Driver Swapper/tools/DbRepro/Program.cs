using EgoEngineLibrary.Data.GameDatabases;
using SimplifiedDriverSwapper.Core.Drivers;

// Reproduces exactly what the app's Save does — the seat plan applied, then the
// staged attribute edits written the same way — so the resulting file can be
// diffed against the original.
//
//   DbRepro <source.bin> <out.bin>

var db = GameDatabase.Load(args[0]);
var cat = DriverCatalogue.Read(db);
var season = Season.All[0];
var plan = new SeatPlan(db, cat, season);

var seated = season.Seats.Select(s => plan.Occupant(s.Slot)).ToHashSet(StringComparer.Ordinal);
var spare = cat.Drivers.Where(d => !seated.Contains(d.Id)).Select(d => d.Id).Take(2).ToList();

plan.Assign(0, spare[0]);
plan.Assign(1, spare[1]);
var applied = plan.Apply(new KitOptions());
Console.WriteLine($"seat plan wrote {applied.Total} record(s)");

// One attribute edit, exactly as Save_Click does it.
var rows = DriverAttributes.Read(db);
var target = rows.First(r => r.DriverId == spare[0]);
var field = db.Find(Season.DriverPath(target.Index, DriverAttributes.Nationality));
Console.WriteLine($"editing driver_{target.Index} ({target.DriverId}) nationality: '{field!.Text}' -> 'Testland'");
field.Text = "Testland";

db.SaveAtomic(args[1]);
Console.WriteLine($"saved {args[1]}");
return 0;
