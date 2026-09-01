using System.CommandLine;
using EgoEngineLibrary.Archive.Erp;
using SimplifiedToolkit.Core.Util;

namespace SimplifiedToolkit.Cli.Commands;

internal static class FragmentCommand
{
    public static Command Build()
    {
        return new Command("fragment", "Add or remove fragments on a resource")
        {
            BuildAdd(),
            BuildRemoveLast(),
        };
    }

    private static Command BuildAdd()
    {
        var fileArg = new Argument<string>("file");
        var idArg   = new Argument<string>("id");
        var cmd = new Command("add", "Append a new fragment cloned from the resource's last fragment") { fileArg, idArg };
        cmd.SetAction(p =>
        {
            var erp = ErpFileIO.Load(p.GetValue(fileArg)!);
            var res = ResourceLookup.FindOne(erp, p.GetValue(idArg)!);
            if (res.Fragments.Count == 0)
            {
                Console.Error.WriteLine("Cannot clone: resource has no fragments to base the new one on.");
                return 1;
            }
            var last = res.Fragments[^1];
            var dup = new ErpFragment(erp);
            dup.SetData(last.GetDataArray(true), compress: true);
            res.Fragments.Add(dup);
            erp.UpdateOffsets();
            ErpFileIO.Save(erp, p.GetValue(fileArg)!);
            Console.WriteLine($"{res.Identifier}: fragments {res.Fragments.Count - 1} → {res.Fragments.Count}");
            return 0;
        });
        return cmd;
    }

    private static Command BuildRemoveLast()
    {
        var fileArg = new Argument<string>("file");
        var idArg   = new Argument<string>("id");
        var cmd = new Command("remove-last", "Delete the last fragment of a resource (requires count > 1)") { fileArg, idArg };
        cmd.SetAction(p =>
        {
            var erp = ErpFileIO.Load(p.GetValue(fileArg)!);
            var res = ResourceLookup.FindOne(erp, p.GetValue(idArg)!);
            if (res.Fragments.Count <= 1)
            {
                Console.Error.WriteLine($"Refusing to remove: resource has only {res.Fragments.Count} fragment(s).");
                return 1;
            }
            res.Fragments.RemoveAt(res.Fragments.Count - 1);
            erp.UpdateOffsets();
            ErpFileIO.Save(erp, p.GetValue(fileArg)!);
            Console.WriteLine($"{res.Identifier}: now {res.Fragments.Count} fragment(s)");
            return 0;
        });
        return cmd;
    }
}
