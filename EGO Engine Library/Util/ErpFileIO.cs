using EgoEngineLibrary.Archive.Erp;

namespace EgoEngineLibrary.Util;

public static class ErpFileIO
{
    public static ErpFile Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"ERP file not found: {path}", path);
        var erp = new ErpFile();
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        erp.Read(fs);
        return erp;
    }

    public static void Save(ErpFile erp, string path)
    {
        using var fs = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        erp.Write(fs);
    }
}
