using System.CommandLine;
using EgoEngineLibrary.Formats.Erp;
using EgoEngineLibrary.Util;

namespace SimplifiedToolkit.Cli.Commands;

internal static class RepackCommand
{
    public static Command Build()
    {
        var fileArg = new Argument<string>("file") { Description = "Path to .erp file (will be overwritten)" };
        var dirArg  = new Argument<string>("inDir") { Description = "Directory containing extracted fragments" };

        var cmd = new Command("repack", "Re-import fragments from disk and write the ERP back") { fileArg, dirArg };

        cmd.SetAction(p =>
        {
            var path = p.GetValue(fileArg)!;
            var inDir = p.GetValue(dirArg)!;
            var erp = ErpFileIO.Load(path);

            var files = Directory.GetFiles(inDir, "*", SearchOption.AllDirectories);
            var exporter = new ErpResourceExporter();
            var progress = new Progress<string>(Console.Write);
            exporter.Import(erp, files, progress, null);
            Console.WriteLine();

            ErpFileIO.Save(erp, path);
            Console.WriteLine($"Wrote {path}");
            return 0;
        });

        return cmd;
    }
}
