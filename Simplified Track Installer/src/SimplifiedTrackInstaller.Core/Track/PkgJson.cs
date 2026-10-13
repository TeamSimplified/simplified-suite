using System.Text;
using System.Text.Json.Nodes;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Data.Pkg;

namespace SimplifiedTrackInstaller.Core.Track;

/// <summary>
/// The UI databases (<c>.uidb</c>, <c>.uidta</c>) are PKG files, edited here as
/// the library's JSON form of them: read to JSON, changed, read back from JSON.
/// Measured: what the editor writes goes through that round trip unchanged.
///
/// Every key carries a hash suffix — <c>Name.ppv1.266367750</c> — so fields are
/// found by the name in front of it, and values carry their type in front —
/// <c>stri melbourne24</c>, <c>fp32 87</c>.
/// </summary>
internal static class PkgJson
{
    public static JsonNode Read(ErpResource resource)
    {
        using var data = new MemoryStream(resource.Fragments[0].GetDataArray(true));
        var pkg = PkgFile.ReadPkg(data);
        using var text = new StringWriter();
        pkg.WriteJson(text);
        return JsonNode.Parse(text.ToString())
               ?? throw new InvalidDataException($"{resource.Identifier} came out empty.");
    }

    public static void Write(ErpResource resource, JsonNode json)
    {
        using var text = new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()));
        var pkg = PkgFile.ReadJson(text);
        using var data = new MemoryStream();
        pkg.WritePkg(data);
        resource.Fragments[0].SetData(data.ToArray());
        resource.UpdateHash();
    }

    /// <summary>The key for <paramref name="name"/>, suffix and all, or null.</summary>
    public static string? Key(JsonObject o, string name)
    {
        var prefix = name + ".ppv1.";
        foreach (var (key, _) in o)
            if (key == name || key.StartsWith(prefix, StringComparison.Ordinal)) return key;
        return null;
    }

    public static JsonNode? Get(JsonObject o, string name) => Key(o, name) is { } key ? o[key] : null;

    /// <summary>A <c>stri</c> value without its type, or null.</summary>
    public static string? Str(JsonObject o, string name) =>
        Get(o, name) is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("stri ", StringComparison.Ordinal)
            ? s[5..] : null;

    /// <summary>Replace a field's value, keeping its key. False when there is no such field.</summary>
    public static bool Set(JsonObject o, string name, string typedValue)
    {
        if (Key(o, name) is not { } key) return false;
        o[key] = typedValue;
        return true;
    }

    public static string Stri(string value) => "stri " + value;

    /// <summary>
    /// How the library spells a float: the shortest text that reads back the
    /// same, so <c>87</c>, <c>1024</c>, <c>633.768</c>.
    /// </summary>
    public static string Fp32(double value) =>
        "fp32 " + ((float)value).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Every object in the tree, depth first.</summary>
    public static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                yield return o;
                foreach (var (_, child) in o)
                    foreach (var inner in Objects(child)) yield return inner;
                break;
            case JsonArray a:
                foreach (var child in a)
                    foreach (var inner in Objects(child)) yield return inner;
                break;
        }
    }

    /// <summary>
    /// Put an entry named <paramref name="name"/> in <paramref name="list"/>:
    /// the one already there if there is one, otherwise a copy of the last entry
    /// added at the end, so the new one has exactly the keys its neighbours do.
    /// Returns it and its position.
    /// </summary>
    public static (JsonObject Entry, int Index, bool Added) Upsert(JsonArray list, string name)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] is JsonObject o && Str(o, "Name") == name) return (o, i, false);

        if (list.Count == 0 || list[^1] is not JsonObject last)
            throw new InvalidDataException($"There is nothing to model a new entry for {name} on.");

        var copy = (JsonObject)last.DeepClone();
        Set(copy, "Name", Stri(name));
        list.Add(copy);
        return (copy, list.Count - 1, true);
    }
}
