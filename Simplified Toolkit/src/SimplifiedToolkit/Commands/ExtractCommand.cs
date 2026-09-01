using System.CommandLine;
using EgoEngineLibrary.Operations;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Formats.Erp;
using EgoEngineLibrary.Util;

namespace SimplifiedToolkit.Cli.Commands;

internal static class ExtractCommand
{
    public static Command Build()
    {
        var fileArg = new Argument<string>("file") { Description = "Path to .erp file" };
        var dirArg  = new Argument<string>("outDir") { Description = "Output directory, or the .erp to write with --to-erp" };
        var filterOpt = new Option<string?>("--filter") { Description = "Regex match against identifier" };
        var toErpOpt = new Option<bool>("--to-erp")
        {
            Description = "Write the matching resources to a new .erp at <outDir> instead of extracting fragments to disk"
        };

        var cmd = new Command("extract", "Extract every fragment of every resource to disk")
        {
            fileArg, dirArg, filterOpt, toErpOpt
        };

        cmd.SetAction(p =>
        {
            var path = p.GetValue(fileArg)!;
            var outDir = p.GetValue(dirArg)!;
            var filter = p.GetValue(filterOpt) ?? "";
            var toErp = p.GetValue(toErpOpt);

            var erp = ErpFileIO.Load(path);

            if (toErp)
            {
                // The operation states this in its own words; the command keeps
                // the wording that names the flag, since that is what the person
                // typing it is looking at.
                if (filter.Length == 0)
                {
                    Console.Error.WriteLine("--to-erp needs --filter; extracting everything would just copy the file.");
                    return 1;
                }

                try
                {
                    var taken = ErpOperations.ExtractToErp(erp, outDir, filter);
                    Console.WriteLine($"Wrote {outDir}  ({taken.Taken} of {taken.OutOf} resources)");
                    return 0;
                }
                catch (Exception ex) when (ex is ArgumentException or System.Text.RegularExpressions.RegexParseException)
                {
                    Console.Error.WriteLine(ex is System.Text.RegularExpressions.RegexParseException
                        ? $"Bad regex: {ex.Message}"
                        : ex.Message.Split(" (Parameter")[0]);
                    return 1;
                }
            }

            ErpOperations.ExtractToFolder(erp, outDir, filter, Console.Write);
            Console.WriteLine();
            return 0;
        });

        return cmd;
    }
}
