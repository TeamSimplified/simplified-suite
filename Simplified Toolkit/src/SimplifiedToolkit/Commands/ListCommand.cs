using System.CommandLine;
using EgoEngineLibrary.Operations;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Util;

namespace SimplifiedToolkit.Cli.Commands;

internal static class ListCommand
{
    public static Command Build()
    {
        var fileArg = new Argument<string>("file") { Description = "Path to .erp file" };
        var typeOpt = new Option<string?>("--type") { Description = "Filter by ResourceType (e.g. GfxSRVResource)" };
        var filterOpt = new Option<string?>("--filter") { Description = "Regex match against identifier" };

        var cmd = new Command("list", "List resources in an ERP file") { fileArg, typeOpt, filterOpt };

        cmd.SetAction(p =>
        {
            var path = p.GetValue(fileArg)!;
            var typeFilter = p.GetValue(typeOpt);
            var idFilter = p.GetValue(filterOpt);
            var erp = ErpFileIO.Load(path);

            var result = ErpOperations.List(erp, typeFilter, idFilter);

            Console.WriteLine($"File:        {path}");
            Console.WriteLine($"Version:     {result.Version}");
            Console.WriteLine($"Resources:   {result.Total} total, {result.Shown.Count} shown");
            Console.WriteLine();

            foreach (var r in result.Shown)
            {
                Console.WriteLine($"#{r.Index,-4} [{r.ResourceType,-20}] {r.FragmentCount}f {r.PackedSize,10:N0}b  {r.Identifier}");
            }
            return 0;
        });

        return cmd;
    }
}
