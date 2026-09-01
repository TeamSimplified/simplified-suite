using System.CommandLine;
using System.Text.RegularExpressions;
using SimplifiedToolkit.Core.Util;

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

            Regex? rx = idFilter is null ? null : new Regex(idFilter, RegexOptions.IgnoreCase);
            var resources = erp.Resources
                .Where(r => typeFilter is null || string.Equals(r.ResourceType, typeFilter, StringComparison.OrdinalIgnoreCase))
                .Where(r => rx is null || rx.IsMatch(r.Identifier))
                .ToList();

            Console.WriteLine($"File:        {path}");
            Console.WriteLine($"Version:     {erp.Version}");
            Console.WriteLine($"Resources:   {erp.Resources.Count} total, {resources.Count} shown");
            Console.WriteLine();

            for (var i = 0; i < resources.Count; i++)
            {
                var r = resources[i];
                Console.WriteLine($"#{i,-4} [{r.ResourceType,-20}] {r.Fragments.Count}f {r.PackedSize,10:N0}b  {r.Identifier}");
            }
            return 0;
        });

        return cmd;
    }
}
