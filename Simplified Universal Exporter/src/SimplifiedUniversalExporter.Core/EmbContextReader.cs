using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SimplifiedUniversalExporter.Core
{
    /// <summary>
    /// Parses a Codemasters ".emb?context=N" livery binding file
    /// (GfxMatBindRes resource type).
    /// Maps per-mesh material-slot names (e.g. "body", "number",
    /// "carbon_tile_1") to actual .material resource EAIDs.
    ///
    /// Each F1 2017-2021 car ships 100 context files, one per possible
    /// driver number (1..99 plus a generic). Across those 100 files,
    /// ONLY two slots change:
    ///
    ///   "number"        -> {team}_number_{N}.material
    ///   "carbon number" -> {team}_carbon_number_{N}.material
    ///
    /// All other slots (body, wheel, steering wheel, carbon tiles,
    /// mirrors, rainbeads, detail, flag, generic, shadow, etc.) are
    /// identical across every context -- they describe the shared
    /// team livery.
    ///
    /// FILE STRUCTURE (per the GfxMatBindRes binary template):
    ///
    ///   @0x00  u32 tableEntryCount         number of slot->material pairs
    ///                                       (19 for 2021 McLaren)
    ///   @0x04  u32 uniqueResourceCount     count of distinct material EAIDs
    ///                                       referenced (<= tableEntryCount;
    ///                                       used here only for sanity).
    ///   @0x08  u32 offsetToResourceTable   byte offset, RELATIVE TO 0x0C,
    ///                                       at which the material-EAID
    ///                                       string pool begins. The slot-
    ///                                       name pool begins immediately
    ///                                       after the entry table at
    ///                                       (0x0C + 8 * tableEntryCount).
    ///   @0x0C  Entry[tableEntryCount]      each is (u32 slotNameOfs, u32 matEaidOfs)
    ///   {Entry table ends here}            slot-name string pool begins immediately
    ///   {null-terminated slot names}
    ///   {null-terminated material EAIDs}   begins at (0x0C + offsetToResourceTable)
    ///
    ///   slotNameOfs  is 0-based into the slot-name pool
    ///   matEaidOfs   is 0-based into the material-EAID pool
    /// </summary>
    public sealed class EmbContextReader
    {
        /// <summary>
        /// Sentinel ID returned by <see cref="ExtractContextIdFromName"/> for
        /// files named with the literal <c>context=default</c> tag rather
        /// than a numeric driver number.
        ///
        /// <para>F1 2017-2021 stock-team livery ERPs ship 100 numbered
        /// contexts (1..99 plus 0 / generic), one per possible driver
        /// number, where the only slots that vary across contexts are
        /// <c>number</c> and <c>carbon number</c>. Some classic-content
        /// chassis instead ship a single <c>context=default</c> file
        /// which carries the same kind of slot-to-material table but
        /// without per-driver number variants -- the team's classic
        /// liveries don't have a driver-specific number atlas to swap
        /// in.</para>
        ///
        /// <para>The c_mclaren_98 ERP is one such case: its only
        /// livery-binding resource is
        /// <c>c_mclaren_98.emb?context=default</c>, and its slot table
        /// contains the unusual binding <c>"carbon 4" -&gt;
        /// c_mclaren_98_carbon4.material</c> (note the lowercase
        /// "carbon", no "tile"). Without this sentinel the regex
        /// <c>context=(\d+)</c> rejected the file outright -- the
        /// classic loader's <c>_activeContext</c> stayed null, and
        /// the slot fell through to the hardcoded fallback table
        /// which only knows <c>"carbon tile N"</c>. The export
        /// therefore landed with a material literally named
        /// "carbon 4" instead of <c>c_mclaren_98_carbon4</c>.</para>
        ///
        /// <para>Choosing <see cref="int.MinValue"/> as the sentinel
        /// keeps it disjoint from every legitimate driver number
        /// (positive in practice, but even <c>context=0</c> would
        /// not collide) so callers can pattern-match on it without
        /// risk of catching a real numeric context.</para>
        /// </summary>
        public const int DefaultContextId = int.MinValue;

        public int ContextId { get; }
        public int EntryCount { get; }

        /// <summary>
        /// Slot-name -> material-EAID dictionary.
        /// Lookups are case-insensitive to tolerate occasional
        /// capitalisation mismatches between mesh files and contexts.
        /// </summary>
        public IReadOnlyDictionary<string, string> SlotToMaterial { get; }

        private EmbContextReader(int ctxId, int entryCount,
                                 Dictionary<string, string> map)
        {
            ContextId = ctxId;
            EntryCount = entryCount;
            SlotToMaterial = map;
        }

        /// <summary>
        /// Parses a context file from disk. The file name should contain
        /// "context=N" so ContextId can be extracted; otherwise ContextId
        /// will be -1.
        /// </summary>
        public static EmbContextReader FromFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int ctxId = ExtractContextIdFromName(path);
            return Parse(bytes, ctxId);
        }

        /// <summary>
        /// Parses a context file from raw bytes. ContextId is optional
        /// (pass -1 if unknown).
        /// </summary>
        public static EmbContextReader Parse(byte[] d, int ctxId = -1)
        {
            if (d == null || d.Length < 12)
                throw new InvalidDataException("EMB context file too small.");

            int tableEntryCount = BitConverter.ToInt32(d, 0);
            int uniqueResourceCount = BitConverter.ToInt32(d, 4);
            int offsetToResourceTable = BitConverter.ToInt32(d, 8);

            // Sanity bounds. The duplicate-of-count assumption from earlier
            // versions of this parser was wrong: field @0x04 is actually
            // uniqueResourceCount, which is <= tableEntryCount whenever the
            // same material is bound to more than one slot. For McLaren 2021
            // the two values happen to be equal (19 == 19) because every
            // slot uses a distinct material, but the equality is not part
            // of the file format.
            if (tableEntryCount <= 0 || tableEntryCount > 256)
                throw new InvalidDataException(
                    $"EMB tableEntryCount out of range ({tableEntryCount}).");
            if (uniqueResourceCount <= 0 || uniqueResourceCount > tableEntryCount)
                throw new InvalidDataException(
                    $"EMB uniqueResourceCount out of range ({uniqueResourceCount} of {tableEntryCount}).");

            int entriesStart = 0x0C;
            int slotPoolStart = entriesStart + tableEntryCount * 8;
            if (slotPoolStart > d.Length)
                throw new InvalidDataException("EMB truncated: no slot pool.");

            // Material pool location is now read directly from the header.
            // Earlier this scanned forward for the literal "eaid://" because
            // the offset-to-resource-table field was misidentified as a
            // string-section size. The find-based approach worked only as
            // long as every resource pool happened to start with that scheme;
            // future games that introduce a different prefix (or an empty
            // first string entry, which would put a null byte before the
            // first scheme tag) would silently bind every slot to the wrong
            // material with no parse error. The header offset is
            // authoritative and removes that risk.
            int materialPoolStart = entriesStart + offsetToResourceTable;
            if (materialPoolStart < slotPoolStart || materialPoolStart >= d.Length)
                throw new InvalidDataException(
                    $"EMB resource pool offset out of range " +
                    $"(materialPoolStart={materialPoolStart}, slotPoolStart={slotPoolStart}, fileSize={d.Length}).");

            var map = new Dictionary<string, string>(
                tableEntryCount, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < tableEntryCount; i++)
            {
                int off = entriesStart + i * 8;
                int slotOfs = BitConverter.ToInt32(d, off + 0);
                int matOfs = BitConverter.ToInt32(d, off + 4);

                string slotName = ReadCString(d, slotPoolStart + slotOfs);
                string material = ReadCString(d, materialPoolStart + matOfs);

                if (!string.IsNullOrEmpty(slotName))
                    map[slotName] = material;
            }

            return new EmbContextReader(ctxId, tableEntryCount, map);
        }

        /// <summary>
        /// Resolves a submesh's material slot name (from the trailing
        /// region of an IDF mesh file) to the actual .material EAID.
        /// Returns null if the slot isn't in this context.
        /// </summary>
        public string Resolve(string slotName)
        {
            if (string.IsNullOrEmpty(slotName)) return null;
            return SlotToMaterial.TryGetValue(slotName, out var eaid) ? eaid : null;
        }

        // =====================================================================
        // CONVENIENCE: FIND ALL CONTEXT FILES IN A FOLDER
        // =====================================================================

        /// <summary>
        /// Enumerates all context files in a folder, returning
        /// (context-id, full-path) pairs. Numeric contexts come first in
        /// ascending ID order, then the <c>context=default</c> file (if
        /// any) at the tail. Both numeric and default contexts are
        /// included; only files that have no recognisable context tag
        /// at all are filtered out.
        /// </summary>
        public static IEnumerable<(int id, string path)> EnumerateContexts(string folder)
        {
            if (!Directory.Exists(folder))
                yield break;

            var hits = new List<(int, string)>();
            foreach (var f in Directory.EnumerateFiles(folder, "*emb*context=*"))
            {
                int id = ExtractContextIdFromName(f);
                // Accept numeric IDs (>= 0) AND the DefaultContextId sentinel.
                // Reject only -1, which is "no context= tag found at all" --
                // those files are unrelated to livery binding (defensive
                // against future filename variants).
                if (id >= 0 || id == DefaultContextId)
                    hits.Add((id, f));
            }

            // Sort: numeric ascending first, default at the tail. We
            // use the sentinel's own value (int.MinValue) which would
            // sort BEFORE any numeric ID under the natural order, so
            // we explicitly remap it to int.MaxValue for comparison so
            // numeric contexts win the FirstOrDefault() pick when both
            // are present (matches the long-standing behaviour of
            // preferring per-driver atlases over generic defaults).
            hits.Sort((a, b) =>
            {
                int ka = a.Item1 == DefaultContextId ? int.MaxValue : a.Item1;
                int kb = b.Item1 == DefaultContextId ? int.MaxValue : b.Item1;
                return ka.CompareTo(kb);
            });
            foreach (var h in hits)
                yield return h;
        }

        /// <summary>
        /// Loads a single context by its ID from a folder. Returns null if
        /// no matching file exists.
        /// </summary>
        public static EmbContextReader LoadFromFolder(string folder, int contextId)
        {
            foreach (var (id, path) in EnumerateContexts(folder))
            {
                if (id == contextId)
                    return FromFile(path);
            }
            return null;
        }

        // =====================================================================
        // HELPERS
        // =====================================================================

        private static string ReadCString(byte[] data, int start)
        {
            if (start < 0 || start >= data.Length) return "";
            int end = start;
            while (end < data.Length && data[end] != 0) end++;
            return Encoding.ASCII.GetString(data, start, end - start);
        }

        private static int ExtractContextIdFromName(string path)
        {
            // Numeric form: context=N where N is one or more digits. This
            // is the long-standing F1 2017-2021 driver-number convention.
            var m = Regex.Match(path, @"context=(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n))
                return n;

            // Literal form: context=default. Some classic-content ERPs
            // (e.g. c_mclaren_98) ship a single binding context with no
            // per-driver variants. Returning the DefaultContextId
            // sentinel keeps the file in the EnumerateContexts result
            // while still being distinguishable from real numeric
            // contexts -- the classic loader picks a numeric context
            // first when one exists, and only falls through to default
            // on cars that never shipped a numeric variant.
            //
            // Match anywhere in the path to mirror the regex form above
            // (the on-disk file names URL-encode '?' as '%3F' so the
            // tag is "context=default" without further punctuation).
            if (path.IndexOf("context=default", StringComparison.OrdinalIgnoreCase) >= 0)
                return DefaultContextId;

            return -1;
        }
    }
}