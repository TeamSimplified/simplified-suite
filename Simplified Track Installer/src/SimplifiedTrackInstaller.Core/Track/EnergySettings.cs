using System.Xml;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Util;
using EgoEngineLibrary.Xml;

namespace SimplifiedTrackInstaller.Core.Track;

/// <summary>
/// The track's ERS values, in every 2026 car's handling file.
///
/// A handling file (<c>.vtf</c>, binary XML) lists per-track energy settings in
/// its top <c>EnergyRecoverySystem</c>, one numbered <c>TrackSettings</c> each,
/// with the count in <c>numTrackSettingsTypes</c>. A track missing from that
/// list has no ERS in the 2026 cars. The editor adds one to every file, keys in
/// the manifest's order, numbered after the last:
/// <code>
/// &lt;TrackSettings name="28"&gt;
///   &lt;Track value="melbourne24" /&gt;
///   &lt;HarvestLimitMGUK value="9000000" /&gt; …
/// </code>
/// The same file sits in several team archives; the package carries one copy
/// of each, since a shader package replaces by id.
/// </summary>
internal static class EnergySettings
{
    public const string TeamsFolder = "2025_asset_groups/f1_2026_vehicle_package/teams";

    public static List<ErpResource> Build(string gameDir, TrackZip zip, Action<string>? log)
    {
        var done = new Dictionary<string, ErpResource>(StringComparer.Ordinal);
        var folder = Path.Combine(gameDir, TeamsFolder.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(folder))
        {
            log?.Invoke("  ⚠ The 2026 teams folder isn't in this install, so no car gets the track's ERS.");
            return [];
        }

        if (zip.Ers.Count == 0)
        {
            log?.Invoke("  The track sets no ERS values; the handling files are left alone.");
            return [];
        }

        var differing = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*.erp", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var index = ErpIndex.TryRead(path);
            if (index is null || !index.Resources.Any(r => IsHandling(r.Identifier, r.ResourceType))) continue;

            var erp = ErpFileIO.Load(path);
            foreach (var resource in erp.Resources.Where(r => IsHandling(r.Identifier, r.ResourceType)))
            {
                if (done.TryGetValue(resource.Identifier, out var first))
                {
                    if (!first.Fragments[0].GetDataArray(true).AsSpan().SequenceEqual(Apply(resource, zip) ?? []))
                        differing++;
                    continue;
                }

                if (Apply(resource, zip) is not { } changed) continue;
                resource.Fragments[0].SetData(changed);
                resource.UpdateHash();
                done[resource.Identifier] = resource;
            }
        }

        log?.Invoke($"  {done.Count} handling files get the track's ERS.");
        if (differing > 0)
            log?.Invoke($"  {differing} other copies of those files differ from the first found; the first is the one packaged.");
        return done.Values.ToList();
    }

    private static bool IsHandling(string identifier, string type) =>
        type == "VTF" || identifier.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file with the track's settings in, or null when it has no list to add to.</summary>
    private static byte[]? Apply(ErpResource resource, TrackZip zip)
    {
        XmlFile xml;
        using (var data = new MemoryStream(resource.Fragments[0].GetDataArray(true)))
        {
            if (!XmlFile.IsXmlFile(data)) return null;
            xml = new XmlFile(data);
        }

        var doc = xml.Document;
        if (doc.DocumentElement?.SelectSingleNode("EnergyRecoverySystem[@numTrackSettingsTypes]") is not XmlElement ers)
            return null;

        var settings = ers.SelectNodes("TrackSettings")!.Cast<XmlElement>().ToList();
        var mine = settings.FirstOrDefault(s =>
            (s.SelectSingleNode("Track") as XmlElement)?.GetAttribute("value") == zip.Code);

        if (mine is null)
        {
            mine = doc.CreateElement("TrackSettings");
            mine.SetAttribute("name", settings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ers.AppendChild(mine);
            ers.SetAttribute("numTrackSettingsTypes",
                (settings.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            mine.RemoveAll();
            mine.SetAttribute("name", settings.IndexOf(mine).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Add(doc, mine, "Track", zip.Code);
        foreach (var (key, value) in zip.Ers) Add(doc, mine, key, value);

        using var output = new MemoryStream();
        xml.Write(output);
        return output.ToArray();
    }

    private static void Add(XmlDocument doc, XmlElement parent, string name, string value)
    {
        var element = doc.CreateElement(name);
        element.SetAttribute("value", value);
        parent.AppendChild(element);
    }
}
