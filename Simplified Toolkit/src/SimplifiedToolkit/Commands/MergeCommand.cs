using System.CommandLine;
using EgoEngineLibrary.Operations;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Util;

namespace SimplifiedToolkit.Cli.Commands;

internal static class MergeCommand
{
    public static Command Build()
    {
        var targetArg  = new Argument<string>("target") { Description = "Target .erp file" };
        var sourcesArg = new Argument<string[]>("sources") { Description = "One or more source .erp files (applied left-to-right)" };
        var overwriteOpt = new Option<bool>("--overwrite") { Description = "If set, source wins on identifier conflict (otherwise target wins)" };
        var outputOpt = new Option<string?>("--output") { Description = "Write merged ERP to this path instead of overwriting target" };

        var cmd = new Command("merge", "Merge resources from one or more source ERPs into the target")
        {
            targetArg, sourcesArg, overwriteOpt, outputOpt
        };

        cmd.SetAction(p =>
        {
            var targetPath = p.GetValue(targetArg)!;
            var sources    = p.GetValue(sourcesArg) ?? [];
            var overwrite  = p.GetValue(overwriteOpt);
            var outputPath = p.GetValue(outputOpt) ?? targetPath;

            if (sources.Length == 0)
            {
                Console.Error.WriteLine("At least one source file is required.");
                return 1;
            }

            var target = ErpFileIO.Load(targetPath);
            Console.WriteLine($"Target:   {targetPath}  ({target.Resources.Count} resources, v{target.Version})");

            foreach (var tally in ErpOperations.Merge(target, sources, overwrite))
            {
                Console.WriteLine($"  + {tally.Source}: {tally.Added} added, {tally.Replaced} replaced, {tally.Skipped} skipped");
            }

            target.UpdateOffsets();
            ErpFileIO.Save(target, outputPath);
            Console.WriteLine($"Wrote {outputPath}  ({target.Resources.Count} resources)");
            return 0;
        });

        return cmd;
    }
}
