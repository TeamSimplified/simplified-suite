using System.CommandLine;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Data.Pkg;
using EgoEngineLibrary.Formats.Erp;
using SimplifiedToolkit.Core.Util;

namespace SimplifiedToolkit.Cli.Commands;

internal static class PkgCommand
{
    public static Command Build()
    {
        return new Command("pkg", "Export/import PKG fragments (binary ↔ JSON)")
        {
            BuildExport(),
            BuildImport(),
        };
    }

    private static Command BuildExport()
    {
        var fileArg = new Argument<string>("file");
        var dirArg  = new Argument<string>("outDir");
        var cmd = new Command("export", "Decode PKG fragments to JSON files") { fileArg, dirArg };
        cmd.SetAction(p =>
        {
            var erp = ErpFileIO.Load(p.GetValue(fileArg)!);
            var outDir = p.GetValue(dirArg)!;
            int ok = 0, skip = 0, fail = 0;

            foreach (var res in erp.Resources)
            {
                foreach (var frag in res.Fragments)
                {
                    if (!TryReadAsPkg(frag, out var pkg)) { skip++; continue; }
                    try
                    {
                        var name = ErpResourceExporter.GetFragmentFileName(res, frag) + ".json";
                        var path = Path.Combine(outDir, res.Folder, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using var sw = new StreamWriter(path);
                        pkg!.WriteJson(sw);
                        ok++;
                    }
                    catch { fail++; }
                }
            }
            Console.WriteLine($"{ok} exported, {skip} skipped (not PKG), {fail} failed");
            return 0;
        });
        return cmd;
    }

    private static Command BuildImport()
    {
        var fileArg = new Argument<string>("file");
        var dirArg  = new Argument<string>("inDir");
        var cmd = new Command("import", "Re-encode JSON files into matching PKG fragments") { fileArg, dirArg };
        cmd.SetAction(p =>
        {
            var erp = ErpFileIO.Load(p.GetValue(fileArg)!);
            var inDir = p.GetValue(dirArg)!;
            int ok = 0, miss = 0, fail = 0;

            foreach (var res in erp.Resources)
            {
                for (var i = 0; i < res.Fragments.Count; i++)
                {
                    var frag = res.Fragments[i];
                    if (!TryReadAsPkg(frag, out _)) continue;
                    var name = ErpResourceExporter.GetFragmentFileName(res, frag) + ".json";
                    var path = Path.Combine(inDir, res.Folder, name);
                    if (!File.Exists(path)) { miss++; continue; }
                    try
                    {
                        using var fs = File.OpenRead(path);
                        var pkg = PkgFile.ReadJson(fs);
                        using var ms = new MemoryStream();
                        pkg.WritePkg(ms);
                        frag.SetData(ms.ToArray());
                        ok++;
                    }
                    catch { fail++; }
                }
            }
            erp.UpdateOffsets();
            ErpFileIO.Save(erp, p.GetValue(fileArg)!);
            Console.WriteLine($"{ok} imported, {miss} missing, {fail} failed");
            return 0;
        });
        return cmd;
    }

    private static bool TryReadAsPkg(ErpFragment frag, out PkgFile? pkg)
    {
        pkg = null;
        try
        {
            using var s = frag.GetDataStream(true);
            pkg = PkgFile.ReadPkg(s);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
