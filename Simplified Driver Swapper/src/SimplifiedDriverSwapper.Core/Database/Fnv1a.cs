using System.Text;

namespace SimplifiedDriverSwapper.Core.Database;

/// <summary>
/// FNV-1a (64-bit). The database has no path strings in it — every record is
/// keyed by the hash of its path, so this is the only way to look one up.
/// </summary>
public static class Fnv1a
{
    private const ulong Offset = 0xCBF29CE484222325;
    private const ulong Prime = 0x100000001B3;

    public static ulong Hash(string path) => Hash(Encoding.UTF8.GetBytes(path));

    public static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        var h = Offset;
        foreach (var b in bytes)
        {
            h ^= b;
            h *= Prime;   // ulong wraps, which is the mask the reference applies
        }
        return h;
    }
}
