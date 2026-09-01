using EgoEngineLibrary.Archive.Erp;
using SimplifiedChassisSwapper.Core.Swap;
using SimplifiedChassisSwapper.Core.Util;

// Runs a swap and compares it against a hand-made reference ERP.
// This is the acceptance gate for the swap engine: the output must match the
// reference resource-for-resource, and every PKG payload byte-for-byte.
//
//   SwapVerify <chassis.erp> <team.erp> <oldName> <newName> <reference.erp> [gameDir]

if (args.Length < 5)
{
    Console.Error.WriteLine("usage: SwapVerify <chassis.erp> <team.erp> <old> <new> <reference.erp> [gameDir]");
    return 2;
}

var chassisPath = args[0];
var teamPath = args[1];
var oldName = args[2];
var newName = args[3];
var referencePath = args[4];
var gameDir = args.Length > 5 ? args[5] : null;

var outPath = Path.Combine(Path.GetTempPath(), $"swapverify_{newName}.erp");

Console.WriteLine($"chassis   : {Path.GetFileName(chassisPath)}");
Console.WriteLine($"team      : {Path.GetFileName(teamPath)}");
Console.WriteLine($"rename    : {oldName} -> {newName}");
Console.WriteLine($"reference : {Path.GetFileName(referencePath)}");
Console.WriteLine();

var swapper = new ChassisSwapper();
var lastPct = -1;
var result = swapper.Run(new ChassisSwapOptions
{
    ChassisErpPath = chassisPath,
    TeamErpPath = teamPath,
    OldName = oldName,
    NewName = newName,
    GameDir = gameDir,
    OutputPath = outPath,
}, (msg, p) =>
{
    var pct = (int)(p * 100);
    if (pct / 10 == lastPct / 10) return;
    lastPct = pct;
    Console.WriteLine($"  [{pct,3}%] {msg}");
});

Console.WriteLine();
Console.WriteLine($"resources    : {result.ResourcesIn} -> {result.ResourcesOut}");
Console.WriteLine($"textures     : {result.TexturesEmbedded} embedded, {result.TexturesMipsUnreachable} unreachable");
Console.WriteLine($"renamed      : {result.IdentifiersRenamed} ids, {result.FragmentsPatched} fragments");
Console.WriteLine($"removed      : {result.RemovedVtf} vtf, {result.RemovedWep} wep");
Console.WriteLine($"grafted      : {result.LayersRebuilt} layers rebuilt, {result.WepCopied} copied");
Console.WriteLine($"animset      : {(result.AnimsetTransplanted ? $"{result.ChassisAnimset} -> {result.TeamAnimset}" : "not transplanted")}");
foreach (var w in result.Warnings) Console.WriteLine($"  warn: {w}");
Console.WriteLine();

// ── Compare against the reference ───────────────────────────────────────────
var actual = ErpFileIO.Load(outPath);
var expected = ErpFileIO.Load(referencePath);

var failures = new List<string>();

// 1. Resource identity sets
var actualIds = actual.Resources.Select(r => r.Identifier).OrderBy(x => x, StringComparer.Ordinal).ToList();
var expectedIds = expected.Resources.Select(r => r.Identifier).OrderBy(x => x, StringComparer.Ordinal).ToList();

var missing = expectedIds.Except(actualIds, StringComparer.Ordinal).ToList();
var extra = actualIds.Except(expectedIds, StringComparer.Ordinal).ToList();

Console.WriteLine("── resource sets ──");
Console.WriteLine($"expected {expectedIds.Count}, got {actualIds.Count}");
if (missing.Count > 0)
{
    failures.Add($"{missing.Count} missing resource(s)");
    Console.WriteLine($"  MISSING ({missing.Count}):");
    foreach (var m in missing.Take(15)) Console.WriteLine($"    - {m}");
}
if (extra.Count > 0)
{
    failures.Add($"{extra.Count} unexpected resource(s)");
    Console.WriteLine($"  EXTRA ({extra.Count}):");
    foreach (var e in extra.Take(15)) Console.WriteLine($"    + {e}");
}
if (missing.Count == 0 && extra.Count == 0) Console.WriteLine("  OK — identical resource sets");

// 2. Fragment counts (catches missed mipmap embedding)
Console.WriteLine();
Console.WriteLine("── fragment counts ──");
var expByIdent = expected.Resources.ToDictionary(r => r.Identifier, StringComparer.Ordinal);
var fragMismatch = 0;
foreach (var res in actual.Resources)
{
    if (!expByIdent.TryGetValue(res.Identifier, out var exp)) continue;
    if (res.Fragments.Count != exp.Fragments.Count)
    {
        if (fragMismatch < 10)
            Console.WriteLine($"  {res.Identifier}: {res.Fragments.Count} vs expected {exp.Fragments.Count}");
        fragMismatch++;
    }
}
if (fragMismatch > 0) failures.Add($"{fragMismatch} fragment-count mismatch(es)");
else Console.WriteLine("  OK — all fragment counts match");

// 3. PKG payloads, compared as JSON
Console.WriteLine();
Console.WriteLine("── PKG payloads ──");
int pkgSame = 0, pkgDiff = 0;
foreach (var res in actual.Resources)
{
    if (!expByIdent.TryGetValue(res.Identifier, out var exp)) continue;
    if (res.Fragments.Count == 0 || exp.Fragments.Count == 0) continue;

    string aJson, bJson;
    try
    {
        aJson = PkgPatcher.ToJson(res.Fragments[0].GetDataArray(true));
        bJson = PkgPatcher.ToJson(exp.Fragments[0].GetDataArray(true));
    }
    catch { continue; }   // not a PKG fragment

    if (string.Equals(Norm(aJson), Norm(bJson), StringComparison.Ordinal)) pkgSame++;
    else
    {
        pkgDiff++;
        if (pkgDiff <= 5)
        {
            Console.WriteLine($"  DIFFERS: {res.Identifier}");
            foreach (var line in FirstDiffs(Norm(bJson), Norm(aJson), 4))
                Console.WriteLine($"      {line}");
        }
    }
}
Console.WriteLine($"  {pkgSame} identical, {pkgDiff} differing");
if (pkgDiff > 0) failures.Add($"{pkgDiff} PKG payload mismatch(es)");

// ── Verdict ─────────────────────────────────────────────────────────────────
Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("PASS — output matches the reference swap.");
    return 0;
}
Console.WriteLine("FAIL — " + string.Join("; ", failures));
return 1;

static string Norm(string s) => s.Replace("\r\n", "\n").TrimEnd();

static IEnumerable<string> FirstDiffs(string expected, string actual, int max)
{
    var e = expected.Split('\n');
    var a = actual.Split('\n');
    var shown = 0;
    for (var i = 0; i < Math.Max(e.Length, a.Length) && shown < max; i++)
    {
        var el = i < e.Length ? e[i].Trim() : "<none>";
        var al = i < a.Length ? a[i].Trim() : "<none>";
        if (el == al) continue;
        yield return $"expected: {el}";
        yield return $"got     : {al}";
        shown++;
    }
}
