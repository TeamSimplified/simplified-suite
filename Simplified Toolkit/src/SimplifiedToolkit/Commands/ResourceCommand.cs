using System.CommandLine;
using SimplifiedToolkit.Core.Operations;
using EgoEngineLibrary.Archive.Erp;
using SimplifiedToolkit.Core.Util;

namespace SimplifiedToolkit.Cli.Commands;

internal static class ResourceCommand
{
    public static Command Build()
    {
        return new Command("resource", "Modify resource metadata")
        {
            BuildRename(),
            BuildRenameAll(),
            BuildSetUri(),
            BuildSetType(),
            BuildDuplicate(),
            BuildRemove(),
        };
    }

    private static Command BuildRename()
    {
        var fileArg = new Argument<string>("file");
        var idArg   = new Argument<string>("id") { Description = "Identifier / filename / #index" };
        var nameArg = new Argument<string>("newName") { Description = "New filename (basename of identifier)" };
        var cmd = new Command("rename", "Change a resource's filename (preserves folder + eaid:// prefix)")
        {
            fileArg, idArg, nameArg
        };
        cmd.SetAction(p =>
        {
            var erp = ErpFileIO.Load(p.GetValue(fileArg)!);
            var res = ResourceLookup.FindOne(erp, p.GetValue(idArg)!);
            var old = res.Identifier;
            res.FileName = p.GetValue(nameArg)!;
            erp.UpdateOffsets();
            ErpFileIO.Save(erp, p.GetValue(fileArg)!);
            Console.WriteLine($"{old}\n  → {res.Identifier}");
            return 0;
        });
        return cmd;
    }

    private static Command BuildRenameAll()
    {
        var fileArg = new Argument<string>("file");
        var findArg = new Argument<string>("find");
        var replArg = new Argument<string>("replace");
        var cmd = new Command("rename-all", "Substring find/replace across every resource identifier")
        {
            fileArg, findArg, replArg
        };
        cmd.SetAction(p =>
        {
            var erp = ErpFileIO.Load(p.GetValue(fileArg)!);
            var find = p.GetValue(findArg)!;
            var repl = p.GetValue(replArg)!;
            int n = 0;
            foreach (var r in erp.Resources)
            {
                if (r.Identifier.Contains(find))
                {
                    r.Identifier = r.Identifier.Replace(find, repl);
                    n++;
                }
            }
            erp.UpdateOffsets();
            ErpFileIO.Save(erp, p.GetValue(fileArg)!);
            Console.WriteLine($"Updated {n} identifier(s)");
            return 0;
        });
        return cmd;
    }

    private static Command BuildSetUri()
    {
        var fileArg = new Argument<string>("file");
        var idArg   = new Argument<string>("id");
        var newArg  = new Argument<string>("newUri") { Description = "Full new identifier (URI)" };
        var cmd = new Command("set-uri", "Replace a resource's full identifier") { fileArg, idArg, newArg };
        cmd.SetAction(p =>
        {
            var erp = ErpFileIO.Load(p.GetValue(fileArg)!);
            var res = ResourceLookup.FindOne(erp, p.GetValue(idArg)!);
            var old = res.Identifier;
            res.Identifier = p.GetValue(newArg)!;
            erp.UpdateOffsets();
            ErpFileIO.Save(erp, p.GetValue(fileArg)!);
            Console.WriteLine($"{old}\n  → {res.Identifier}");
            return 0;
        });
        return cmd;
    }

    private static Command BuildSetType()
    {
        var fileArg = new Argument<string>("file");
        var idArg   = new Argument<string>("id");
        var newArg  = new Argument<string>("newType") { Description = "New ResourceType (16 chars max)" };
        var cmd = new Command("set-type", "Change a resource's ResourceType string") { fileArg, idArg, newArg };
        cmd.SetAction(p =>
        {
            var erp = ErpFileIO.Load(p.GetValue(fileArg)!);
            var res = ResourceLookup.FindOne(erp, p.GetValue(idArg)!);
            var newType = p.GetValue(newArg)!;
            if (newType.Length > 16)
            {
                Console.Error.WriteLine("ResourceType must be 16 characters or fewer.");
                return 1;
            }
            var old = res.ResourceType;
            res.ResourceType = newType;
            erp.UpdateOffsets();
            ErpFileIO.Save(erp, p.GetValue(fileArg)!);
            Console.WriteLine($"{res.Identifier}: {old} → {newType}");
            return 0;
        });
        return cmd;
    }

    private static Command BuildDuplicate()
    {
        var fileArg = new Argument<string>("file");
        var idArg   = new Argument<string>("id");
        var cmd = new Command("duplicate", "Deep-copy a resource (clones every fragment with re-compression)") { fileArg, idArg };
        cmd.SetAction(p =>
        {
            var erp = ErpFileIO.Load(p.GetValue(fileArg)!);
            var src = ResourceLookup.FindOne(erp, p.GetValue(idArg)!);
            var dst = new ErpResource(erp)
            {
                Identifier = src.Identifier,
                ResourceType = src.ResourceType,
            };
            foreach (var srcFrag in src.Fragments)
            {
                var dstFrag = new ErpFragment(erp);
                dstFrag.SetData(srcFrag.GetDataArray(true), compress: true);
                dst.Fragments.Add(dstFrag);
            }
            erp.Resources.Add(dst);
            erp.UpdateOffsets();
            ErpFileIO.Save(erp, p.GetValue(fileArg)!);
            Console.WriteLine($"Duplicated {src.Identifier} ({src.Fragments.Count} fragments)");
            return 0;
        });
        return cmd;
    }

    private static Command BuildRemove()
    {
        var fileArg = new Argument<string>("file");
        var idArg   = new Argument<string>("id");
        var cmd = new Command("remove", "Delete a resource") { fileArg, idArg };
        cmd.SetAction(p =>
        {
            var erp = ErpFileIO.Load(p.GetValue(fileArg)!);
            var res = ResourceLookup.FindOne(erp, p.GetValue(idArg)!);
            erp.Resources.Remove(res);
            erp.UpdateOffsets();
            ErpFileIO.Save(erp, p.GetValue(fileArg)!);
            Console.WriteLine($"Removed {res.Identifier}");
            return 0;
        });
        return cmd;
    }
}
