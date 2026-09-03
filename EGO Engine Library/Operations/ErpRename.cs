using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EgoEngineLibrary.Archive.Erp;

namespace EgoEngineLibrary.Operations;

/// <summary>
/// Replace a token everywhere an ERP names it — resource identifiers and the
/// strings inside fragment payloads — without the old and new having to be the
/// same length.
///
/// The same-length rule that mods have lived with is not the format's. An
/// identifier is a length-prefixed string and the resource header is recomputed
/// from it, so that half was always free. The other half, the strings inside
/// fragments, is what byte-for-byte patching was working around: it can splice
/// a name in place without knowing anything about the payload, at the cost of
/// never changing its length.
///
/// What makes it safe to do properly is that the payloads carrying these names
/// hold plain NUL-terminated strings and nothing indexes them by position.
/// Measured on a shipped car archive, across the six fragment types that carry
/// the name — GfxMaterialRes, GfxMatBindRes, GfxModelRes, GfxSurfaceRes,
/// GfxSRVResource and GfxMeshRes — not one int32 in any of them equals the
/// offset of a string in the same payload. So a string can grow or shrink and
/// the bytes after it can move.
///
/// <c>WOInstances</c> and <c>World</c> are the exception, and they use three
/// framings between them:
///
/// <code>
///   07 "ferrari" 00                  a length byte, then the string
///   10 00 00 00 "ferrari" 00×9       a slot of fixed width, padded out
///   "vehiclecameras" 00×2 "ferrari" 00×9   an array of those slots
/// </code>
///
/// The first two are rewritten. The third can only be written within the room
/// the slot already has, since the width belongs to the array rather than to
/// any one entry — sixteen bytes in every case seen — so a name longer than
/// that is reported instead of forced.
///
/// Nothing is guessed at: a length is only believed where it counts the string
/// that follows it, and a slot is only written inside its existing padding.
/// Whatever can't be placed that way comes back in <see cref="Report.Skipped"/>,
/// where a caller can refuse the result rather than ship a half-renamed archive.
/// </summary>
public static class ErpRename
{
    /// <summary>
    /// Resource types that put a length byte before some strings. Everything
    /// else in a car archive is bare NUL-terminated.
    /// </summary>
    private static readonly HashSet<string> LengthPrefixedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "WOInstances", "World" };

    /// <summary>What a rename did, and what it declined to do.</summary>
    public sealed record Report(
        int Identifiers,
        int Fragments,
        int Strings,
        int HashesRecomputed,
        IReadOnlyList<string> Skipped)
    {
        /// <summary>True when every occurrence was rewritten.</summary>
        public bool Complete => Skipped.Count == 0;
    }

    /// <summary>
    /// Rename <paramref name="oldToken"/> to <paramref name="newToken"/>
    /// throughout <paramref name="erp"/>. The two may differ in length.
    ///
    /// Every fragment that changes has its resource's hash recomputed, which
    /// byte patching never did — the hash is MD5 over the fragment data, so
    /// editing a payload has always left it stale.
    /// </summary>
    public static Report Apply(ErpFile erp, string oldToken, string newToken)
    {
        ArgumentNullException.ThrowIfNull(erp);
        if (string.IsNullOrEmpty(oldToken))
            throw new ArgumentException("There is no token to replace.", nameof(oldToken));
        if (newToken is null)
            throw new ArgumentNullException(nameof(newToken));

        var oldBytes = Encoding.UTF8.GetBytes(oldToken);
        var newBytes = Encoding.UTF8.GetBytes(newToken);

        int identifiers = 0, fragments = 0, strings = 0, hashes = 0;
        var skipped = new List<string>();

        foreach (var res in erp.Resources)
        {
            if (res.Identifier.Contains(oldToken, StringComparison.Ordinal))
            {
                res.Identifier = res.Identifier.Replace(oldToken, newToken, StringComparison.Ordinal);
                identifiers++;
            }

            var prefixed = LengthPrefixedTypes.Contains(res.ResourceType.Trim('\0', ' '));
            var touched = false;

            foreach (var frag in res.Fragments)
            {
                byte[] data;
                try { data = frag.GetDataArray(true); }
                catch (Exception ex)
                {
                    skipped.Add($"{res.Identifier}: fragment unreadable ({ex.Message})");
                    continue;
                }

                var rewritten = Rewrite(data, oldBytes, newBytes, prefixed, res.Identifier, skipped, out var count);
                if (rewritten is null) continue;

                try { frag.SetData(rewritten); }
                catch (Exception ex)
                {
                    skipped.Add($"{res.Identifier}: fragment wouldn't re-pack ({ex.Message})");
                    continue;
                }

                fragments++;
                strings += count;
                touched = true;
            }

            if (touched && res.RecomputeHash()) hashes++;
        }

        return new Report(identifiers, fragments, strings, hashes, skipped);
    }

    /// <summary>
    /// The longest replacement <paramref name="oldToken"/> can be given in this
    /// archive without leaving something behind.
    ///
    /// Worth asking before doing the work rather than after: a rename that
    /// can't reach every occurrence produces an archive naming two different
    /// things, which is worse than one that was never attempted.
    ///
    /// <see cref="int.MaxValue"/> when nothing constrains it.
    /// </summary>
    public static int MaxLength(ErpFile erp, string oldToken)
    {
        ArgumentNullException.ThrowIfNull(erp);
        if (string.IsNullOrEmpty(oldToken)) return 0;

        var oldBytes = Encoding.UTF8.GetBytes(oldToken);
        var limit = int.MaxValue;

        foreach (var res in erp.Resources)
        {
            if (!LengthPrefixedTypes.Contains(res.ResourceType.Trim('\0', ' '))) continue;

            foreach (var frag in res.Fragments)
            {
                byte[] data;
                try { data = frag.GetDataArray(true); } catch { continue; }

                var i = 0;
                while (i <= data.Length - oldBytes.Length)
                {
                    if (!Matches(data, oldBytes, i)) { i++; continue; }

                    var start = i;
                    while (start > 0 && IsText(data[start - 1])) start--;
                    var end = i;
                    while (end < data.Length && IsText(data[end])) end++;
                    if (end >= data.Length || data[end] != 0) { i = end + 1; continue; }

                    // A length-byte string can take anything up to 255. A slot
                    // can take what the slot holds, less what the rest of the
                    // string around the token needs.
                    if (start > 0 && data[start - 1] == end - start) limit = Math.Min(limit, byte.MaxValue);
                    else if (start < i && data[start] == end - start - 1) limit = Math.Min(limit, byte.MaxValue);
                    else if (SlotCapacity(data, start, end) is var room and > 0)
                        limit = Math.Min(limit, room - (end - start - oldBytes.Length));

                    i = end;
                }
            }
        }

        return limit;
    }

    /// <summary>
    /// A payload with every string mentioning the token rewritten, or null when
    /// nothing in it mentioned the token.
    ///
    /// Works string by string rather than match by match: a name is replaced
    /// inside the whole NUL-terminated run it belongs to, so the run's own
    /// framing can be dealt with as a unit.
    /// </summary>
    private static byte[]? Rewrite(byte[] data, byte[] oldBytes, byte[] newBytes,
                                   bool lengthPrefixed, string owner,
                                   List<string> skipped, out int stringsChanged)
    {
        stringsChanged = 0;
        var output = new MemoryStream(data.Length);
        var copiedTo = 0;
        var i = 0;

        while (i <= data.Length - oldBytes.Length)
        {
            if (!Matches(data, oldBytes, i)) { i++; continue; }

            // Widen to the NUL-terminated run this match sits in.
            var start = i;
            while (start > 0 && IsText(data[start - 1])) start--;
            var end = i;
            while (end < data.Length && IsText(data[end])) end++;

            if (end >= data.Length || data[end] != 0)
            {
                // Not a terminated string — this is binary that happens to
                // contain the bytes. Leave it and say so.
                skipped.Add($"{owner}: a match at {i} isn't inside a terminated string");
                i = end + 1;
                continue;
            }

            // A length byte in front is only believed when it actually counts
            // the string that follows it.
            //
            // It can fall either side of where the run appears to start, and
            // that is not a detail: a length of 65 is 'A', which is printable,
            // so the run widens straight over its own prefix. Both positions
            // are checked, longer string first.
            var hasPrefix = false;
            var capacity = 0;
            if (lengthPrefixed)
            {
                if (start > 0 && data[start - 1] == end - start)
                {
                    hasPrefix = true;                       // prefix sits before the run
                }
                else if (start < i && data[start] == end - start - 1)
                {
                    hasPrefix = true;                       // the run's first byte is the prefix
                    start++;
                }
                else
                {
                    // The third shape: a name sitting in a slot of fixed width,
                    // padded out with NULs. Nothing to rewrite but the name, as
                    // long as the new one fits the room already there.
                    capacity = SlotCapacity(data, start, end);
                }
            }

            var text = Encoding.UTF8.GetString(data, start, end - start);

            if (lengthPrefixed && !hasPrefix && capacity == 0)
            {
                skipped.Add($"{owner}: '{Ellipsis(text)}' isn't framed in any way this understands");
                i = end;
                continue;
            }

            var replaced = text.Replace(Encoding.UTF8.GetString(oldBytes),
                                        Encoding.UTF8.GetString(newBytes), StringComparison.Ordinal);
            var replacedBytes = Encoding.UTF8.GetBytes(replaced);

            if (hasPrefix && replacedBytes.Length > byte.MaxValue)
            {
                skipped.Add($"{owner}: '{Ellipsis(replaced)}' is too long for a one-byte length");
                i = end;
                continue;
            }

            if (capacity > 0)
            {
                // A fixed field: write inside the room it already has, padded
                // out, and leave the capacity alone. Growing it would mean
                // moving whatever the record expects to find after it.
                if (replacedBytes.Length > capacity)
                {
                    skipped.Add($"{owner}: '{Ellipsis(replaced)}' is {replacedBytes.Length} bytes " +
                                $"and its field holds {capacity}");
                    i = end;
                    continue;
                }

                output.Write(data, copiedTo, start - copiedTo);
                output.Write(replacedBytes, 0, replacedBytes.Length);
                output.Write(new byte[capacity - replacedBytes.Length], 0, capacity - replacedBytes.Length);

                // The slot is the same size it was, so nothing after it moves.
                copiedTo = start + capacity;
                stringsChanged++;
                i = copiedTo;
                continue;
            }

            // Everything up to the string, then the string, then carry on.
            var upTo = hasPrefix ? start - 1 : start;
            output.Write(data, copiedTo, upTo - copiedTo);
            if (hasPrefix) output.WriteByte((byte)replacedBytes.Length);
            output.Write(replacedBytes, 0, replacedBytes.Length);

            copiedTo = end;
            stringsChanged++;
            i = end;
        }

        if (stringsChanged == 0) return null;

        output.Write(data, copiedTo, data.Length - copiedTo);
        return output.ToArray();
    }

    /// <summary>
    /// The room a run has where it sits: its own length plus the NULs padding
    /// it out. Zero when there is none to speak of.
    ///
    /// These layers hold arrays of fixed-width slots — sixteen bytes each, the
    /// width declared once for the whole array rather than before each entry —
    /// so there is no length near a given name to read. The padding is the
    /// budget instead, and writing inside it changes no byte count and moves
    /// nothing.
    ///
    /// A single trailing NUL is not padding, it is a terminator, so a name is
    /// only allowed to grow where there are at least two.
    /// </summary>
    private static int SlotCapacity(byte[] data, int start, int end)
    {
        var padding = 0;
        while (end + padding < data.Length && data[end + padding] == 0 && padding < 256) padding++;

        if (padding == 0) return 0;
        return padding >= 2 ? end - start + padding : end - start;
    }

    private static bool IsText(byte b) => b >= 32 && b < 127;

    private static bool Matches(byte[] hay, byte[] needle, int at)
    {
        if (at + needle.Length > hay.Length) return false;
        for (var j = 0; j < needle.Length; j++)
            if (hay[at + j] != needle[j]) return false;
        return true;
    }

    private static string Ellipsis(string s) => s.Length <= 60 ? s : s[..57] + "...";
}
