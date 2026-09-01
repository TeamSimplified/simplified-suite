using System.IO;

namespace SimplifiedStudio.Model;

/// <summary>
/// In-memory store for new external <c>.mipmaps</c> file content that has
/// been encoded but not yet written to disk. Operations that READ external
/// mips (preview, export, strip) call <see cref="HasPending"/> +
/// <see cref="OpenForRead"/> so they transparently see pending bytes when
/// present, falling back to the on-disk file otherwise. The store is
/// flushed by MainWindow when the ERP is saved.
/// </summary>
public sealed class PendingMips
{
    private readonly Dictionary<string, byte[]> _map = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _map.Count;
    public IReadOnlyCollection<string> Paths => _map.Keys;

    public bool HasPending(string path) => _map.ContainsKey(path);

    /// <summary>Replace the pending bytes for <paramref name="path"/>.</summary>
    public void Set(string path, byte[] bytes) => _map[path] = bytes;

    /// <summary>Drop the pending entry for <paramref name="path"/> (no-op if absent).</summary>
    public bool Remove(string path) => _map.Remove(path);

    public void Clear() => _map.Clear();

    /// <summary>Deep-copy the current dict for use as an undo snapshot.</summary>
    public Dictionary<string, byte[]> CaptureSnapshot()
        => _map.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Replace the current dict with the given snapshot.</summary>
    public void RestoreSnapshot(Dictionary<string, byte[]> snap)
    {
        _map.Clear();
        foreach (var kv in snap) _map[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Open a stream over the pending bytes. The caller is responsible for
    /// disposing it. Use <see cref="HasPending"/> first.
    /// </summary>
    public Stream OpenForRead(string path) => new MemoryStream(_map[path], writable: false);

    /// <summary>
    /// Write every pending entry to disk and clear the store. Returns the
    /// number of files written.
    /// </summary>
    public int Flush()
    {
        var n = 0;
        foreach (var kv in _map)
        {
            var dir = Path.GetDirectoryName(kv.Key);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(kv.Key, kv.Value);
            n++;
        }
        _map.Clear();
        return n;
    }
}
