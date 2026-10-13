using System.Security.Cryptography;
using System.Text;
using EgoEngineLibrary.Archive.Erp;

namespace SimplifiedTrackInstaller.Core.Track;

/// <summary>
/// Points the track's textures at the mip files it ships, in a folder of its own.
///
/// A texture whose smaller levels live outside the archive carries a third
/// fragment naming the file, relative to the game folder:
/// <code>
/// [len][path][0x00][u32 count][count × 26-byte entries][tail]
/// </code>
/// (F1 22 and older have no 0x00 and 25-byte entries; only the path is touched,
/// so both are handled the same.)
///
/// A track zip ships some of those files — the ones the track changed — named
/// <c>md5(path)[..12]_&lt;file name&gt;</c>. The editor drops them into a folder
/// nothing reads and leaves every descriptor naming the original, so the base
/// track's own files are what load. Measured on Istanbul Park: 268 shipped, all
/// for Spa's paths, 4 of them different from Spa's.
///
/// Here they go to <c>environment_package/tracks/&lt;code&gt;/mipmaps/</c> and
/// the descriptors are repointed there, so the track gets its own files and the
/// track it is built on is never touched. Descriptors whose file is not shipped
/// keep naming the game's.
/// </summary>
public static class MipLinks
{
    /// <summary>Where a track's own mip files go, relative to the game folder.</summary>
    public static string FolderFor(string code) => $"environment_package/tracks/{code}/mipmaps";

    /// <summary>The name a track zip gives the file for <paramref name="path"/>.</summary>
    public static string ShippedName(string path) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(path)))[..12] + "_" + path[(path.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Repoint every descriptor whose file is in <paramref name="shipped"/>.
    /// Returns the shipped names that something now points at; anything shipped
    /// and never named is not worth copying.
    /// </summary>
    public static HashSet<string> Repoint(ErpFile track, string code, IReadOnlySet<string> shipped,
                                          Action<string>? log = null)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int linked = 0, repointed = 0;

        foreach (var resource in track.Resources)
        {
            if (resource.ResourceType != "GfxSurfaceRes" || resource.Fragments.Count < 3) continue;
            linked++;

            var fragment = resource.Fragments[2];
            var data = fragment.GetDataArray(true);
            if (data.Length < 2 || data[0] == 0 || data.Length < 1 + data[0]) continue;

            var path = Encoding.Latin1.GetString(data, 1, data[0]);
            var name = ShippedName(path);
            if (!shipped.Contains(name)) continue;

            var target = Encoding.Latin1.GetBytes($"{FolderFor(code)}/{name}");
            if (target.Length > byte.MaxValue)
                throw new InvalidDataException($"The mip path for {resource.Identifier} would be longer than a descriptor can hold.");

            // Length, new path, then everything after the old path untouched:
            // the entries are offsets into the mip file, not into this.
            var rewritten = new byte[1 + target.Length + data.Length - 1 - data[0]];
            rewritten[0] = (byte)target.Length;
            target.CopyTo(rewritten, 1);
            data.AsSpan(1 + data[0]).CopyTo(rewritten.AsSpan(1 + target.Length));

            fragment.SetData(rewritten);
            resource.UpdateHash();

            used.Add(name);
            repointed++;
        }

        log?.Invoke($"  {linked:N0} textures keep mips outside the archive; {repointed:N0} repointed to the track's own " +
                    $"{used.Count:N0} files, the rest use the game's.");
        return used;
    }
}
