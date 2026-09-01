using System.CommandLine;
using EgoEngineLibrary.Archive.Erp;
using SimplifiedToolkit.Core.Util;

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

            foreach (var srcPath in sources)
            {
                var src = ErpFileIO.Load(srcPath);
                int added = 0, replaced = 0, skipped = 0;
                foreach (var res in src.Resources)
                {
                    var existing = target.Resources.Find(r => r.Identifier == res.Identifier);
                    if (existing is not null)
                    {
                        if (overwrite)
                        {
                            target.Resources.Remove(existing);
                            RepointAndAdd(target, res);
                            replaced++;
                        }
                        else skipped++;
                    }
                    else
                    {
                        RepointAndAdd(target, res);
                        added++;
                    }
                }
                Console.WriteLine($"  + {srcPath}: {added} added, {replaced} replaced, {skipped} skipped");
            }

            target.UpdateOffsets();
            ErpFileIO.Save(target, outputPath);
            Console.WriteLine($"Wrote {outputPath}  ({target.Resources.Count} resources)");
            return 0;
        });

        return cmd;
    }

    /// <summary>
    /// Repoint a merged-in resource (and its fragments) to the target ERP so that
    /// version-dependent size math in UpdateOffsets uses the target's version.
    /// (Fixes the latent bug in the EEA GUI implementation.)
    /// </summary>
    private static void RepointAndAdd(ErpFile target, ErpResource resource)
    {
        resource.ParentFile = target;
        foreach (var frag in resource.Fragments)
            frag.ParentFile = target;
        target.Resources.Add(resource);
    }
}
