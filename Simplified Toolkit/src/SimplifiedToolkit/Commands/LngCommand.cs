using System.CommandLine;
using EgoEngineLibrary.Language;

namespace SimplifiedToolkit.Cli.Commands;

/// <summary>
/// Subcommands for working with Codemasters Ego Engine <c>.lng</c> language
/// files: list / read / mutate keys, merge multiple language files.
/// </summary>
internal static class LngCommand
{
    public static Command Build()
    {
        return new Command("lng", "Codemasters Ego Engine .lng language file operations")
        {
            BuildList(),
            BuildGet(),
            BuildSet(),
            BuildAdd(),
            BuildRemove(),
            BuildMerge(),
        };
    }

    // ─────────────────────── list ─────────────────────────────────────

    private static Command BuildList()
    {
        var fileArg   = new Argument<string>("file") { Description = "Path to a .lng file" };
        var valuesOpt = new Option<bool>("--values") { Description = "Also print each key's value" };

        var cmd = new Command("list", "Print every key in the .lng file") { fileArg, valuesOpt };
        cmd.SetAction(p =>
        {
            var lng = LoadLng(p.GetValue(fileArg)!);
            var withValues = p.GetValue(valuesOpt);
            var entries = AllEntries(lng).OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
            foreach (var e in entries)
            {
                if (withValues) Console.WriteLine($"{e.Key}\t{e.Value}");
                else            Console.WriteLine(e.Key);
            }
            Console.Error.WriteLine($"({entries.Count} entries)");
            return 0;
        });
        return cmd;
    }

    // ─────────────────────── get ──────────────────────────────────────

    private static Command BuildGet()
    {
        var fileArg = new Argument<string>("file");
        var keyArg  = new Argument<string>("key");

        var cmd = new Command("get", "Print the value of a key") { fileArg, keyArg };
        cmd.SetAction(p =>
        {
            var lng = LoadLng(p.GetValue(fileArg)!);
            var key = p.GetValue(keyArg)!;
            if (!lng.ContainsKey(key))
            {
                Console.Error.WriteLine($"Key '{key}' not found.");
                return 1;
            }
            Console.WriteLine(lng[key]);
            return 0;
        });
        return cmd;
    }

    // ─────────────────────── set ──────────────────────────────────────

    private static Command BuildSet()
    {
        var fileArg   = new Argument<string>("file");
        var keyArg    = new Argument<string>("key");
        var valueArg  = new Argument<string>("value");
        var outputOpt = new Option<string?>("--output") { Description = "Write to this path instead of overwriting the input file" };

        var cmd = new Command("set", "Change the value of an existing key") { fileArg, keyArg, valueArg, outputOpt };
        cmd.SetAction(p =>
        {
            var path = p.GetValue(fileArg)!;
            var key  = p.GetValue(keyArg)!;
            var val  = p.GetValue(valueArg)!;
            var outPath = p.GetValue(outputOpt) ?? path;

            var lng = LoadLng(path);
            if (!lng.ContainsKey(key))
            {
                Console.Error.WriteLine($"Key '{key}' not found. Use 'add' to create it.");
                return 1;
            }
            lng.Add(key, val);   // LngFile.Add updates in-place when the key exists
            SaveLng(lng, outPath);
            Console.WriteLine($"Updated '{key}' → {outPath}");
            return 0;
        });
        return cmd;
    }

    // ─────────────────────── add ──────────────────────────────────────

    private static Command BuildAdd()
    {
        var fileArg   = new Argument<string>("file");
        var keyArg    = new Argument<string>("key");
        var valueArg  = new Argument<string?>("value") { Description = "Optional value (defaults to empty string)" };
        var outputOpt = new Option<string?>("--output") { Description = "Write to this path instead of overwriting the input file" };
        var forceOpt  = new Option<bool>("--force") { Description = "Overwrite the value if the key already exists" };

        var cmd = new Command("add", "Add a new key (with optional value)") { fileArg, keyArg, valueArg, outputOpt, forceOpt };
        cmd.SetAction(p =>
        {
            var path = p.GetValue(fileArg)!;
            var key  = p.GetValue(keyArg)!;
            var val  = p.GetValue(valueArg) ?? "";
            var outPath = p.GetValue(outputOpt) ?? path;
            var force = p.GetValue(forceOpt);

            var lng = LoadLng(path);
            if (lng.ContainsKey(key) && !force)
            {
                Console.Error.WriteLine($"Key '{key}' already exists. Use 'set' to change its value, or pass --force.");
                return 1;
            }
            lng.Add(key, val);
            SaveLng(lng, outPath);
            Console.WriteLine($"Added '{key}' → {outPath}");
            return 0;
        });
        return cmd;
    }

    // ─────────────────────── remove ───────────────────────────────────

    private static Command BuildRemove()
    {
        var fileArg   = new Argument<string>("file");
        var keyArg    = new Argument<string>("key");
        var outputOpt = new Option<string?>("--output") { Description = "Write to this path instead of overwriting the input file" };

        var cmd = new Command("remove", "Delete a key") { fileArg, keyArg, outputOpt };
        cmd.SetAction(p =>
        {
            var path = p.GetValue(fileArg)!;
            var key  = p.GetValue(keyArg)!;
            var outPath = p.GetValue(outputOpt) ?? path;

            var lng = LoadLng(path);
            if (!lng.ContainsKey(key))
            {
                Console.Error.WriteLine($"Key '{key}' not found.");
                return 1;
            }
            lng.Remove(key);
            SaveLng(lng, outPath);
            Console.WriteLine($"Removed '{key}' → {outPath}");
            return 0;
        });
        return cmd;
    }

    // ─────────────────────── merge ────────────────────────────────────

    private static Command BuildMerge()
    {
        var targetArg    = new Argument<string>("target")  { Description = "Target .lng file (modified in place unless --output is set)" };
        var sourcesArg   = new Argument<string[]>("sources") { Description = "One or more source .lng files (applied left-to-right)" };
        var overwriteOpt = new Option<bool>("--overwrite") { Description = "Source wins on key conflict. Without this flag, the target's existing value is preserved." };
        var outputOpt    = new Option<string?>("--output") { Description = "Write merged .lng to this path instead of overwriting target" };

        var cmd = new Command("merge",
            "Merge keys from source .lng files into the target. " +
            "By default the target's existing values are preserved (only new keys are added). " +
            "Pass --overwrite to let source values replace target values for keys present in both.")
        {
            targetArg, sourcesArg, overwriteOpt, outputOpt
        };

        cmd.SetAction(p =>
        {
            var targetPath = p.GetValue(targetArg)!;
            var sources    = p.GetValue(sourcesArg) ?? [];
            var overwrite  = p.GetValue(overwriteOpt);
            var outPath    = p.GetValue(outputOpt) ?? targetPath;

            if (sources.Length == 0)
            {
                Console.Error.WriteLine("At least one source file is required.");
                return 1;
            }

            var target = LoadLng(targetPath);
            Console.WriteLine($"Target:   {targetPath}  ({target.Count} entries)");

            foreach (var srcPath in sources)
            {
                var src = LoadLng(srcPath);
                int added = 0, replaced = 0, skipped = 0;
                foreach (var e in AllEntries(src))
                {
                    if (target.ContainsKey(e.Key))
                    {
                        if (overwrite)
                        {
                            target.Add(e.Key, e.Value);   // LngFile.Add updates in-place when key exists
                            replaced++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    else
                    {
                        target.Add(e.Key, e.Value);
                        added++;
                    }
                }
                Console.WriteLine($"  + {srcPath}: {added} added, {replaced} replaced, {skipped} preserved");
            }

            SaveLng(target, outPath);
            Console.WriteLine($"Wrote {outPath}  ({target.Count} entries)");
            return 0;
        });

        return cmd;
    }

    // ─────────────────────── helpers ──────────────────────────────────

    private static LngFile LoadLng(string path)
    {
        using var fs = File.OpenRead(path);
        return new LngFile(fs);
    }

    private static void SaveLng(LngFile lng, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        lng.Write(fs);
    }

    private static IEnumerable<HashEntry> AllEntries(LngFile lng) => lng.hsht.Entries;
}
