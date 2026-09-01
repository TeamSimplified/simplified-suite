using System.CommandLine;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Formats.Erp;
using EgoEngineLibrary.Xml;
using SimplifiedToolkit.Core.Util;

namespace SimplifiedToolkit.Cli.Commands;

internal static class XmlCommand
{
    public static Command Build()
    {
        return new Command("xml", "Export/import XML fragments (binary XML ↔ text)")
        {
            BuildExport(),
            BuildImport(),
        };
    }

    private static Command BuildExport()
    {
        var fileArg = new Argument<string>("file");
        var dirArg  = new Argument<string>("outDir");
        var cmd = new Command("export", "Decode all XML fragments to text files") { fileArg, dirArg };
        cmd.SetAction(p =>
        {
            var erp = ErpFileIO.Load(p.GetValue(fileArg)!);
            var outDir = p.GetValue(dirArg)!;
            int ok = 0, skip = 0, fail = 0;

            foreach (var res in erp.Resources)
            {
                foreach (var frag in res.Fragments)
                {
                    if (!TryReadAsXml(frag, out var xml)) { skip++; continue; }
                    try
                    {
                        var name = ErpResourceExporter.GetFragmentFileName(res, frag);
                        var path = Path.Combine(outDir, res.Folder, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using var sw = new StreamWriter(path);
                        xml!.WriteXml(sw);
                        ok++;
                    }
                    catch { fail++; }
                }
            }
            Console.WriteLine($"{ok} exported, {skip} skipped (not XML), {fail} failed");
            return 0;
        });
        return cmd;
    }

    private static Command BuildImport()
    {
        var fileArg = new Argument<string>("file");
        var dirArg  = new Argument<string>("inDir");
        var cmd = new Command("import", "Re-encode text XML files back into matching fragments") { fileArg, dirArg };
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
                    if (!TryReadAsXml(frag, out _)) continue;
                    var name = ErpResourceExporter.GetFragmentFileName(res, frag);
                    var path = Path.Combine(inDir, res.Folder, name);
                    if (!File.Exists(path)) { miss++; continue; }
                    try
                    {
                        using var fs = File.OpenRead(path);
                        var xml = new XmlFile(fs);
                        using var ms = new MemoryStream();
                        xml.Write(ms);
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

    private static bool TryReadAsXml(ErpFragment frag, out XmlFile? xml)
    {
        xml = null;
        try
        {
            using var s = frag.GetDataStream(true);
            xml = new XmlFile(s);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
