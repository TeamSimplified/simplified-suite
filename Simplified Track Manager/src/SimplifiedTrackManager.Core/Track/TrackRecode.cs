using System.Text;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Data.Pkg;

namespace SimplifiedTrackManager.Core.Track;

/// <summary>
/// Giving a track archive a code of its own.
///
/// Resource names are renamed; data that embeds the code is rewritten through
/// its own format — worlds and layers (<c>!pkg</c>) through their JSON form,
/// texture headers through the SRV reader. Meshes, models, material bindings,
/// materials and terrain name the resources they use as paths inside a string
/// table, with offsets to each string kept elsewhere in a format this doesn't
/// read; a code of a different length would move every string after it. So
/// there the code is replaced in place, and only by one of the same length,
/// which moves nothing — a track whose own data names its paths that way can
/// only be given a code as long as its own (<see cref="EmbedsCode"/>).
///
/// The old code is matched where no letter or digit touches it, so
/// <c>melbourne</c> never matches inside <c>melbourne24</c>. An underscore does
/// not protect a name — <c>melbourne_common</c> is renamed — and that is what is
/// wanted, since the archive builds its names as <c>&lt;code&gt;_common</c>. A
/// fragment found holding the code only inside a longer word is left as it is.
///
/// Two sets of files the game finds by building a path from the code are moved
/// when the author left them under another track's folder, as a track built on
/// a base often does: the racing line (<c>tracks/&lt;x&gt;/ai/&lt;x&gt;_ai_track.*</c>) and the
/// track-space data (<c>tracks/&lt;x&gt;/track_space_data/&lt;x&gt;_common.*</c>).
/// </summary>
public static class TrackRecode
{
    public sealed record Result(int Renamed, int Pkgs, int Headers, int InPlace, IReadOnlyList<string> Moved);

    private const string Tracks = "eaid://environment_package/tracks/";

    /// <summary>
    /// Rename <paramref name="from"/> to <paramref name="to"/> throughout
    /// <paramref name="erp"/>, in memory. <paramref name="from"/> may equal
    /// <paramref name="to"/>, for a track that has its own code already and only
    /// needs its racing line and track-space files moved home.
    /// </summary>
    public static Result Apply(ErpFile erp, string from, string to, Action<string>? log = null)
    {
        // Path stems the game builds from the code, found under another
        // track's folder: each is renamed to the same stem under the new code.
        var stems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in erp.Resources)
        {
            if (Stem(r.Identifier) is not { } found || found.Code == from || found.Code == to) continue;
            stems.TryAdd(found.Stem, found.Stem.Replace($"/{found.Code}/", $"/{to}/").Replace($"/{found.Code}_", $"/{to}_"));
        }

        var word = from == to ? null : new Regex($"(?<![A-Za-z0-9]){Regex.Escape(from)}(?![A-Za-z0-9])");
        string Recode(string s)
        {
            foreach (var (old, @new) in stems) s = s.Replace(old, @new, StringComparison.Ordinal);
            return word is null ? s : word.Replace(s, to);
        }

        var needles = stems.Keys.Select(Encoding.ASCII.GetBytes).ToList();
        if (word is not null) needles.Add(Encoding.ASCII.GetBytes(from));

        // In place, byte for byte: Latin-1 maps each byte to one character and
        // back, and the two codes are the same length, so nothing moves.
        var inPlaceOk = word is not null && Encoding.ASCII.GetByteCount(to) == from.Length;

        int renamed = 0, pkgs = 0, headers = 0, inPlace = 0;
        var refused = new List<string>();
        foreach (var r in erp.Resources)
        {
            var id = Recode(r.Identifier);
            if (id != r.Identifier) { r.Identifier = id; renamed++; }

            var changed = false;
            for (var i = 0; i < r.Fragments.Count; i++)
            {
                var f = r.Fragments[i];
                var data = f.GetDataArray(true);
                if (!needles.Any(n => data.AsSpan().IndexOf(n) >= 0)) continue;

                if (data.AsSpan().StartsWith("!pkg"u8))
                {
                    var json = new StringWriter();
                    PkgFile.ReadPkg(new MemoryStream(data)).WriteJson(json);
                    var text = json.ToString();
                    var edited = Recode(text);
                    if (edited == text) continue; // the bytes were part of a longer word
                    var pkg = PkgFile.ReadJson(new MemoryStream(Encoding.UTF8.GetBytes(edited)));
                    var output = new MemoryStream();
                    pkg.WritePkg(output);
                    f.SetData(output.ToArray());
                    pkgs++;
                    changed = true;
                }
                else if (r.ResourceType == "GfxSRVResource" && i == 0)
                {
                    var srv = new ErpGfxSRVResource0();
                    srv.FromFragment(f);
                    var name = Recode(srv.SurfaceResourceName);
                    if (name == srv.SurfaceResourceName) continue;
                    srv.SurfaceResourceName = name;
                    srv.ToFragment(f);
                    headers++;
                    changed = true;
                }
                else if (word is not null && word.IsMatch(Encoding.Latin1.GetString(data)))
                {
                    if (!inPlaceOk)
                    {
                        refused.Add($"{r.Identifier} ({r.ResourceType}, fragment {f.Name})");
                        continue;
                    }
                    f.SetData(Encoding.Latin1.GetBytes(word.Replace(Encoding.Latin1.GetString(data), to)));
                    inPlace++;
                    changed = true;
                }
            }
            if (changed) r.UpdateHash();
        }

        if (refused.Count > 0)
            throw new InvalidDataException(
                $"{refused.Count} of the track's meshes, models and materials name their files with {from} " +
                $"inside data that can't change length, so its new code has to be {from.Length} characters " +
                $"long, as {from} is. {to} is {to.Length}.");

        var moved = stems.Select(s => $"{s.Key} → {s.Value}").ToList();
        log?.Invoke(from == to
            ? $"  kept the code {to}"
            : $"  {from} → {to}: {renamed:N0} resources renamed, {pkgs} worlds and layers, {headers} texture headers " +
              $"and {inPlace} meshes, models and materials rewritten");
        foreach (var m in moved) log?.Invoke($"  moved {m}");
        return new Result(renamed, pkgs, headers, inPlace, moved);
    }

    /// <summary>
    /// The same rename on a text, such as the two UI files, which name the
    /// track's pictures by resource id.
    /// </summary>
    public static string RecodeText(string text, string from, string to) =>
        from == to ? text : Regex.Replace(text, $"(?<![A-Za-z0-9]){Regex.Escape(from)}(?![A-Za-z0-9])", to);

    /// <summary>
    /// Whether the track's own data — a mesh, model, material binding,
    /// material or terrain — names a path with <paramref name="code"/>, which
    /// means a new code has to be as long as this one.
    /// </summary>
    public static bool EmbedsCode(ErpFile erp, string code)
    {
        var word = new Regex($"(?<![A-Za-z0-9]){Regex.Escape(code)}(?![A-Za-z0-9])");
        var needle = Encoding.ASCII.GetBytes(code);
        foreach (var r in erp.Resources)
        {
            if (r.ResourceType == "GfxSRVResource") continue;
            foreach (var f in r.Fragments)
            {
                var data = f.GetDataArray(true);
                if (data.AsSpan().IndexOf(needle) < 0 || data.AsSpan().StartsWith("!pkg"u8)) continue;
                if (word.IsMatch(Encoding.Latin1.GetString(data))) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether any resource name in <paramref name="erp"/> still holds
    /// <paramref name="code"/> as a word. For the read-back after a rename.
    /// </summary>
    public static int Remaining(ErpFile erp, string code)
    {
        var word = new Regex($"(?<![A-Za-z0-9]){Regex.Escape(code)}(?![A-Za-z0-9])");
        return erp.Resources.Count(r => word.IsMatch(r.Identifier));
    }

    /// <summary>
    /// The racing-line or track-space stem a resource sits under, and the code
    /// that stem was built from: <c>…/tracks/spa/ai/spa_ai_track.</c>, <c>spa</c>.
    /// </summary>
    private static (string Stem, string Code)? Stem(string id)
    {
        if (!id.StartsWith(Tracks, StringComparison.Ordinal)) return null;
        var rest = id[Tracks.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return null;
        var code = rest[..slash];

        foreach (var tail in new[] { $"/ai/{code}_ai_track.", $"/track_space_data/{code}_common." })
        {
            var stem = $"{Tracks}{code}{tail}";
            if (id.StartsWith(stem, StringComparison.Ordinal)) return (stem, code);
        }
        return null;
    }
}
