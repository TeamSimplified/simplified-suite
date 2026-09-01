using System.CommandLine;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Formats.Erp;
using SimplifiedToolkit.Core.Util;

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

            if (toErp) return ExtractToErp(erp, outDir, filter);

            Directory.CreateDirectory(outDir);
            var exporter = new ErpResourceExporter();
            var progress = new Progress<string>(Console.Write);
            exporter.Export(erp, outDir, progress, null, filter);
            Console.WriteLine();
            return 0;
        });

        return cmd;
    }

    /// <summary>
    /// Copy the resources whose identifier matches into a new ERP, rather than
    /// unpacking their fragments to loose files. Lets a subset be lifted out of an
    /// archive in one pass, which otherwise takes one <c>resource remove</c> call
    /// per resource you don't want.
    /// </summary>
    private static int ExtractToErp(ErpFile source, string outPath, string filter)
    {
        if (filter.Length == 0)
        {
            Console.Error.WriteLine("--to-erp needs --filter; extracting everything would just copy the file.");
            return 1;
        }

        Regex regex;
        try
        {
            regex = new Regex(filter);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Bad regex: {ex.Message}");
            return 1;
        }

        // Same version as the source: the offset maths in UpdateOffsets is
        // version-dependent, so a mismatch would write a subtly wrong archive.
        var output = new ErpFile { Version = source.Version };

        foreach (var resource in source.Resources)
        {
            if (!regex.IsMatch(resource.Identifier)) continue;

            // Repoint to the new file before adding, so its size maths uses the
            // output's version rather than the source's.
            resource.ParentFile = output;
            foreach (var fragment in resource.Fragments) fragment.ParentFile = output;
            output.Resources.Add(resource);
        }

        if (output.Resources.Count == 0)
        {
            Console.Error.WriteLine("Nothing matched — no ERP written.");
            return 1;
        }

        var directory = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        output.UpdateOffsets();
        ErpFileIO.Save(output, outPath);

        Console.WriteLine($"Wrote {outPath}  ({output.Resources.Count} of {source.Resources.Count} resources)");
        return 0;
    }
}
