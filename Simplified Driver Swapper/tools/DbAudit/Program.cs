using SimplifiedDriverSwapper.Core.Database;
using SimplifiedDriverSwapper.Core.Drivers;

// Every record that differs between two databases, named where the path can be
// worked out. The file stores no paths, so known ones are hashed and matched
// back — enough to say "driver_57.nationality" rather than just "some record".
//
//   DbAudit <before.bin> <after.bin>

var before = GameDatabase.Load(args[0]);
var after = GameDatabase.Load(args[1]);

Console.WriteLine($"before : {before.Records.Count:N0} records");
Console.WriteLine($"after  : {after.Records.Count:N0} records");

// Keys have to be unique, or a lookup could land on the wrong record entirely.
var duplicates = before.Records.GroupBy(r => r.Key).Count(g => g.Count() > 1);
Console.WriteLine($"duplicate keys: {duplicates}");

var names = new Dictionary<ulong, string>();
void Name(string path) => names[Fnv1a.Hash(path)] = path;

var driverCount = before.Int("entrants.drivers._driver_instancecount_");
for (var i = 0; i < driverCount; i++)
{
    Name(Season.DriverPath(i, "_metadata_"));
    Name(Season.DriverPath(i, "car_number"));
    foreach (var f in DriverAttributes.Fields) Name(Season.DriverPath(i, f));
}
foreach (var season in Season.All)
    foreach (var seat in season.Seats)
        foreach (var f in new[] { "driver", "car_number", "_metadata_", "team", "vehicle" })
            Name(season.SeatPath(seat.Slot, f));
for (var i = 0; i < 400; i++)
{
    Name(Season.HelmetPath(i, "_metadata_"));
    Name(Season.HelmetPath(i, "id"));
    Name(Season.KitPath("suits", "Suit", i, "_metadata_"));
    Name(Season.KitPath("suits", "Suit", i, "id"));
    Name(Season.KitPath("gloves", "Gloves", i, "_metadata_"));
    Name(Season.KitPath("gloves", "Gloves", i, "id"));
}

var afterByKey = after.Records.ToDictionary(r => r.Key);
var changed = 0;
foreach (var r in before.Records)
{
    if (!afterByKey.TryGetValue(r.Key, out var b)) { Console.WriteLine($"  MISSING key {r.Key:X16}"); changed++; continue; }
    if (r.Data.AsSpan().SequenceEqual(b.Data)) continue;

    changed++;
    Console.WriteLine($"  {names.GetValueOrDefault(r.Key, $"(unnamed record {r.Key:X16})")}");
    Console.WriteLine($"      {r.Text}  ->  {b.Text}");
}

Console.WriteLine($"\n{changed} record(s) differ");
return 0;
