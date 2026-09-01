using System.IO;

namespace SimplifiedStudio.Model;

/// <summary>
/// Reads and writes one resource's payload as a file, for every kind except
/// textures. Both the single Export/Import buttons and a bulk run over the
/// whole ERP go through here, so the two can't drift apart — a bulk export
/// produces exactly the files a one-by-one export would.
///
/// Textures are deliberately absent: they need texconv and the external mip
/// chain, which is <see cref="TextureIO"/>'s job.
/// </summary>
public static class ResourceFileIO
{
    /// <summary>Extension the exported file carries for a given row.</summary>
    public static string ExtensionFor(ResourceListItem vm) => vm switch
    {
        PkgVm      => ".json",
        XmlVm      => ".xml",
        MaterialVm => ".material",
        ContextVm  => ".context",
        _          => "",
    };

    /// <summary>
    /// The file name a resource maps to on disk: its EAID file name with '?'
    /// escaped, carrying the kind's extension. Material and Context resources
    /// normally already end in theirs, so it's only appended when missing.
    /// </summary>
    public static string FileNameFor(ResourceListItem vm)
    {
        var name = vm.FileName.Replace("?", "%3F");
        var ext = ExtensionFor(vm);
        if (ext.Length == 0) return name;

        return name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? name : name + ext;
    }

    /// <summary>
    /// Write the resource to <paramref name="path"/>, creating its folder.
    /// PKG and XML are written in their parsed text form; everything else is
    /// a straight copy of the first fragment's bytes.
    /// </summary>
    public static void Export(ResourceListItem vm, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        switch (vm)
        {
            case PkgVm pkg:
            {
                if (pkg.Parsed is null)
                    throw new InvalidOperationException("PKG resource didn't parse — nothing to export.");
                using var sw = new StreamWriter(path);
                pkg.Parsed.WriteJson(sw);
                break;
            }
            case XmlVm xml:
            {
                if (xml.Parsed is null)
                    throw new InvalidOperationException("XML resource didn't parse — nothing to export.");
                using var sw = new StreamWriter(path);
                xml.Parsed.WriteXml(sw);
                break;
            }
            default:
            {
                RequireFragment(vm);
                using var s = vm.Resource.Fragments[0].GetDataStream(true);
                using var fs = File.Create(path);
                s.CopyTo(fs);
                break;
            }
        }
    }

    /// <summary>
    /// Replace the resource's first fragment with the contents of
    /// <paramref name="path"/>. PKG and XML are re-serialized from their text
    /// form; everything else is taken as raw bytes. In-memory only — the
    /// caller still has to save the ERP.
    /// </summary>
    public static void Import(ResourceListItem vm, string path)
    {
        RequireFragment(vm);

        switch (vm)
        {
            case PkgVm:
            {
                using var fs = File.OpenRead(path);
                var pkg = EgoEngineLibrary.Data.Pkg.PkgFile.ReadJson(fs);
                using var ms = new MemoryStream();
                pkg.WritePkg(ms);
                vm.Resource.Fragments[0].SetData(ms.ToArray());
                break;
            }
            case XmlVm:
            {
                using var fs = File.OpenRead(path);
                var xml = new EgoEngineLibrary.Xml.XmlFile(fs);
                using var ms = new MemoryStream();
                xml.Write(ms);
                vm.Resource.Fragments[0].SetData(ms.ToArray());
                break;
            }
            default:
                vm.Resource.Fragments[0].SetData(File.ReadAllBytes(path));
                break;
        }
    }

    private static void RequireFragment(ResourceListItem vm)
    {
        if (vm.Resource.Fragments.Count == 0)
            throw new InvalidOperationException("Resource has no fragments.");
    }
}
