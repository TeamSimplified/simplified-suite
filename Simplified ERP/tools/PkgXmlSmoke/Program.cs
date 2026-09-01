using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Data.Pkg;
using EgoEngineLibrary.Xml;
using SimplifiedStudio.Core.Util;

// Walk both reference ERPs and classify every fragment as PKG / XML / other,
// confirming the ported codecs work without surprises.

string[] erps = args.Length > 0 ? args : new[]
{
    @"F:\Claude Code\Simplified 26 Suite\References\ferrari_pkg.erp",
    @"F:\Claude Code\Simplified 26 Suite\References\f1_performance.erp",
};

foreach (var path in erps)
{
    Console.WriteLine($"=== {Path.GetFileName(path)} ===");
    var erp = ErpFileIO.Load(path);
    int pkg = 0, xml = 0, other = 0;
    foreach (var res in erp.Resources)
    {
        foreach (var frag in res.Fragments)
        {
            string kind;
            if (TryRead(frag, s => { var p = PkgFile.ReadPkg(s); _ = p; })) { kind = "PKG"; pkg++; }
            else if (TryRead(frag, s => { var x = new XmlFile(s); _ = x; }))  { kind = "XML"; xml++; }
            else                                                                 { kind = "—";   other++; }
            Console.WriteLine($"  [{kind,3}] {res.ResourceType,-18} {res.FileName,-46} frag={frag.Name}");
        }
    }
    Console.WriteLine($"  -> {pkg} PKG, {xml} XML, {other} other\n");
}

static bool TryRead(ErpFragment frag, Action<Stream> tryParse)
{
    try { using var s = frag.GetDataStream(true); tryParse(s); return true; }
    catch { return false; }
}
