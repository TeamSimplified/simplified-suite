using EgoEngineLibrary.Data.Pkg;

namespace SimplifiedChassisSwapper.Core.Swap;

/// <summary>
/// Rewrites PKG payloads for the swap. PKGs are edited via their JSON
/// representation because the rules are keyed on specific PKG fields.
///
/// The rules below were derived by diffing a hand-made reference swap
/// (force_india + sauber → ff25monster) and reproduce all seven of its
/// <c>.layer</c> files byte-for-byte.
/// </summary>
public static class PkgPatcher
{
    /// <summary>
    /// PKG keys that point at the car's own model/material assets. These follow
    /// the <i>new</i> name because the assets travel with the chassis and get
    /// re-badged; every other reference follows the <i>team</i> name.
    /// </summary>
    private static readonly string[] AssetPointerKeys =
    {
        "mat binding.ppv1.2829679144",
        "model.ppv1.2961925722",
    };

    /// <summary>
    /// PKG keys naming audio banks rather than ERP resources. They are never
    /// renamed: the value is an entry inside the game's shipped sound banks, so
    /// rewriting it points at something that doesn't exist and the car loses its
    /// engine audio.
    ///
    /// They can't be recognised by shape — both are bare strings, not
    /// <c>eaid://</c> URIs — and they don't track the team folder either: several
    /// teams share one bundle (<c>lotus</c> and <c>mclaren</c> both run
    /// <c>*_2025.bdl</c>), and the switch follows the brand, so <c>force_india</c>
    /// owns <c>f1_m_aston_martin</c> and <c>lotus</c> owns <c>f1_m_alpine_26</c>.
    /// Hence an explicit list.
    ///
    /// Only <see cref="SwitchNameKey"/> is ever <i>written</i>; it alone decides
    /// which engine plays. <see cref="BundleNameKey"/> is listed here purely so it
    /// survives the rename, and keeps whatever the chassis had.
    /// </summary>
    /// <summary>
    /// Wwise switch. This alone decides which engine sound plays — the bundle
    /// beside it has no bearing on it.
    /// </summary>
    public const string SwitchNameKey = "switchName.ppv1.3614812426";

    /// <summary>A <c>.bdl</c> named per engine supplier. Preserved, never written.</summary>
    public const string BundleNameKey = "bundleName.ppv1.344247110";

    private static readonly string[] AudioBankKeys = { SwitchNameKey, BundleNameKey };

    /// <summary>The PKG field holding an animset's own name.</summary>
    private const string AnimsetNameKey = "name.ppv1.2369371622";

    /// <summary>Reads a PKG fragment payload as JSON text.</summary>
    public static string ToJson(byte[] pkgBytes)
    {
        using var ms = new MemoryStream(pkgBytes, writable: false);
        var pkg = PkgFile.ReadPkg(ms);
        using var sw = new StringWriter();
        pkg.WriteJson(sw);
        return sw.ToString();
    }

    /// <summary>Re-encodes JSON text back into a PKG fragment payload.</summary>
    public static byte[] FromJson(string json)
    {
        using var inMs = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var pkg = PkgFile.ReadJson(inMs);
        using var outMs = new MemoryStream();
        pkg.WritePkg(outMs);
        return outMs.ToArray();
    }

    /// <summary>
    /// Rebuild a chassis <c>.layer</c> so it can sit in the team's slot:
    /// every mention of the chassis becomes the team, <i>except</i> the
    /// asset-pointer keys, which become the new (re-badged) name, and the audio
    /// bank keys, which are never renamed.
    ///
    /// Applied line-wise: the PKG JSON writer emits one key per line, so a key's
    /// value can be targeted without a full document walk — and that keeps values
    /// that merely contain the token (rather than being a bare token) intact.
    /// </summary>
    /// <param name="audioOverrides">
    /// Audio bank values to substitute wholesale, from
    /// <see cref="ReadAudioBankValues"/> on another layer. Null keeps the
    /// chassis's own engine sound. Keys absent from the map are left as they are,
    /// so passing a layer that has no audio block is a no-op.
    /// </param>
    public static string PatchLayerJson(
        string chassisLayerJson, string oldName, string teamName, string newName,
        IReadOnlyDictionary<string, string>? audioOverrides = null)
    {
        var lines = chassisLayerJson.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var audioKey = MatchKey(lines[i], AudioBankKeys);
            if (audioKey is not null)
            {
                if (audioOverrides is not null && audioOverrides.TryGetValue(audioKey, out var value))
                    lines[i] = ReplaceQuotedValue(lines[i], value);
                continue;   // never renamed either way
            }

            var isAssetPointer = MatchKey(lines[i], AssetPointerKeys) is not null;
            lines[i] = lines[i].Replace(oldName, isAssetPointer ? newName : teamName, StringComparison.Ordinal);
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The audio bank values a layer declares, keyed by PKG key. Empty for layers
    /// with no audio block — only the car-model layer carries one.
    /// </summary>
    public static Dictionary<string, string> ReadAudioBankValues(string layerJson)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in layerJson.Split('\n'))
        {
            var key = MatchKey(line, AudioBankKeys);
            if (key is null) continue;
            var value = ExtractQuotedValue(line);
            if (value is not null) map[key] = value;
        }
        return map;
    }

    /// <summary>
    /// An engine choice in the form <see cref="PatchLayerJson"/> expects. Only the
    /// switch is set, so the layer keeps its own bundle. The <c>stri</c> tag is
    /// added here because callers deal in bare names; values read out of a layer
    /// already carry their own tag and are passed through untouched.
    /// </summary>
    public static Dictionary<string, string> BuildAudioOverrides(string switchName) =>
        new(StringComparer.Ordinal) { [SwitchNameKey] = "stri " + switchName };

    /// <summary>Drops the PKG type tag: <c>stri f1_m_ferrari_26</c> → <c>f1_m_ferrari_26</c>.</summary>
    public static string StripTypeTag(string value)
    {
        var space = value.IndexOf(' ');
        return space < 0 ? value : value[(space + 1)..];
    }

    private static string? MatchKey(string line, string[] keys)
    {
        foreach (var key in keys)
            if (line.Contains(key, StringComparison.Ordinal)) return key;
        return null;
    }

    /// <summary>
    /// The quoted value of a <c>"key": "value"</c> line, including its type tag
    /// (e.g. <c>stri f1_m_ferrari_26</c>). Null if the line isn't that shape.
    /// </summary>
    private static string? ExtractQuotedValue(string line)
    {
        var (open, close) = FindValueQuotes(line);
        return open < 0 ? null : line[(open + 1)..close];
    }

    /// <summary>
    /// Swap in a new value, keeping the line's indentation and trailing comma —
    /// the comma depends on the key's position in its block, so the surrounding
    /// text can't be assumed to match the layer the value came from.
    /// </summary>
    private static string ReplaceQuotedValue(string line, string value)
    {
        var (open, close) = FindValueQuotes(line);
        return open < 0 ? line : string.Concat(line.AsSpan(0, open + 1), value, line.AsSpan(close));
    }

    /// <summary>Positions of the quotes around the value, or (-1, -1).</summary>
    private static (int Open, int Close) FindValueQuotes(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return (-1, -1);
        var open = line.IndexOf('"', colon + 1);
        if (open < 0) return (-1, -1);
        var close = line.LastIndexOf('"');
        return close > open ? (open, close) : (-1, -1);
    }

    /// <summary>
    /// Retarget an animset's own name while leaving its clip list pointing at the
    /// chassis's animations. Used to publish the chassis's animset under the
    /// team's animset identifier.
    /// </summary>
    public static string RenameAnimsetJson(string chassisAnimsetJson, string chassisAnimset, string teamAnimset)
    {
        var lines = chassisAnimsetJson.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(AnimsetNameKey, StringComparison.Ordinal))
                lines[i] = lines[i].Replace(chassisAnimset, teamAnimset, StringComparison.Ordinal);
        }
        return string.Join('\n', lines);
    }
}
