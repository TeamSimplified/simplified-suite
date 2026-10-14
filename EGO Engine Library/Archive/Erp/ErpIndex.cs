using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EgoEngineLibrary.Archive.Erp
{
    /// <summary>
    /// Where one fragment's bytes are, as the table describes them.
    /// <paramref name="Offset"/> is relative to <see cref="ErpIndexResult.ResourceOffset"/>;
    /// <paramref name="PackedSize"/> is what is stored, <paramref name="Size"/> what
    /// it decompresses to.
    /// </summary>
    public readonly record struct ErpIndexFragment(
        string Name, ulong Offset, ulong Size, int Flags, ErpCompressionAlgorithm Compression, ulong PackedSize);

    /// <summary>One resource, as much of it as reading the table alone can tell.</summary>
    public readonly record struct ErpIndexEntry(string Identifier, string ResourceType)
    {
        /// <summary>The resource's own version field (<see cref="ErpResource.Unknown"/>).</summary>
        public int Unknown { get; init; }

        /// <summary>Where each fragment is, in order.</summary>
        public IReadOnlyList<ErpIndexFragment> Fragments { get; init; } = Array.Empty<ErpIndexFragment>();

        /// <summary>The 16-byte content hash (version 3 and up), or empty.</summary>
        public byte[] Hash { get; init; } = Array.Empty<byte>();
    }

    /// <summary>What an archive holds, without its contents.</summary>
    public sealed record ErpIndexResult(int Version, IReadOnlyList<ErpIndexEntry> Resources)
    {
        /// <summary>Where the payloads begin: every fragment offset counts from here.</summary>
        public ulong ResourceOffset { get; init; }
    }

    /// <summary>
    /// What is in an archive, read from its resource table and nothing else.
    ///
    /// <see cref="ErpFile.Read"/> is eager all the way down: <see cref="ErpFragment.Read"/>
    /// seeks to each fragment and pulls its payload into memory as the table is
    /// walked. That is the right shape for opening one archive to work on it,
    /// and the wrong one entirely for asking a question of many — a full F1 25
    /// install is 8,166 archives totalling 28 GB, and the question "which of
    /// these mentions this resource" needs none of those bytes.
    ///
    /// So this reads the header, then the resource table in one sequential read,
    /// and stops at <see cref="ErpFile.ResourceOffset"/> where the payloads
    /// begin. What comes back is identifiers, types and where each fragment is;
    /// <see cref="ReadFragment"/> then fetches one fragment's bytes on demand,
    /// which is how a multi-gigabyte track archive is read without holding it in
    /// memory.
    /// </summary>
    public static class ErpIndex
    {
        private const uint Magic = 1263555141; // "ERPK"

        /// <summary>Bytes before the first resource entry: the 48-byte header, then two counts.</summary>
        private const int TableStart = 56;

        private const int SupportedVersion = 4;

        /// <summary>
        /// Index an archive, or null when the file is not one this can read — a
        /// wrong magic, an unsupported version, a truncated table. A scan runs
        /// over whatever files a folder happens to hold, so "not an ERP" is an
        /// ordinary answer rather than a failure.
        /// </summary>
        public static ErpIndexResult? TryRead(string path)
        {
            try
            {
                return Read(path);
            }
            catch (Exception e) when (e is IOException or FileFormatException or NotSupportedException
                                          or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Index the archive at <paramref name="path"/>.</summary>
        public static ErpIndexResult Read(string path)
        {
            // SequentialScan because that is exactly what this does: the header,
            // then the table, then nothing. The buffer is small deliberately —
            // both reads are large enough to go straight to the caller's array.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                              bufferSize: 4096, FileOptions.SequentialScan);
            return Read(stream);
        }

        /// <summary>Index an archive already open.</summary>
        public static ErpIndexResult Read(Stream stream)
        {
            Span<byte> header = stackalloc byte[TableStart];
            stream.ReadExactly(header);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            {
                throw new FileFormatException("This is not an ERP file!");
            }

            var version = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            if (version is < 0 or > SupportedVersion)
            {
                throw new NotSupportedException($"Erp version {version} is not supported.");
            }

            var resourceOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
            var count = BinaryPrimitives.ReadInt32LittleEndian(header[48..]);

            if (count == 0) return new ErpIndexResult(version, Array.Empty<ErpIndexEntry>()) { ResourceOffset = resourceOffset };

            if (count < 0 || resourceOffset < TableStart || resourceOffset > int.MaxValue)
            {
                throw new FileFormatException($"The resource table of this ERP does not describe itself sensibly: {count} resources ending at {resourceOffset}.");
            }

            var tableLength = (int)resourceOffset - TableStart;
            var buffer = ArrayPool<byte>.Shared.Rent(tableLength);

            try
            {
                var table = buffer.AsSpan(0, tableLength);
                stream.ReadExactly(table);
                return new ErpIndexResult(version, ParseTable(table, version, count)) { ResourceOffset = resourceOffset };
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static List<ErpIndexEntry> ParseTable(ReadOnlySpan<byte> table, int version, int count)
        {
            // Sizes taken from the read path rather than from ErpResource.UpdateOffsets:
            // the two disagree for version 2 and below, and what a file actually
            // contains is what ErpFragment.Read consumes - name 4, offset 8, size 8,
            // flags 4, then compression 1 and stored size 8 from version 3. (This
            // said 16 for version 2 and below until the fragments were read field
            // by field; the per-resource length check would have refused them.)
            var fragmentSize = version > 2 ? 33 : 24;
            var hashSize = version > 2 ? 16 : 0;
            var unknown2Size = version >= 4 ? 2 : 0;

            var entries = new List<ErpIndexEntry>(count);
            var pos = 0;

            for (var i = 0; i < count; ++i)
            {
                // Each entry declares its own length, which is checked against
                // the walk below rather than trusted in place of it. A table
                // that disagrees with itself is a file being read wrongly, and
                // saying so beats returning identifiers read from the middle of
                // a fragment header.
                var declared = ReadUInt32(table, ref pos);
                var bodyStart = pos;

                var identifier = ReadString(table, ref pos, ReadUInt16(table, ref pos));
                var resourceType = ReadString(table, ref pos, 16);

                var unknown = (int)ReadUInt32(table, ref pos);
                pos += unknown2Size;

                var fragmentCount = ReadByte(table, ref pos);
                if (pos + fragmentCount * fragmentSize + hashSize > table.Length)
                {
                    throw new FileFormatException($"Resource {i} runs past the end of the table.");
                }

                // As ErpFragment.Read takes them: name, offset, size, flags, then
                // from version 3 the compression and the stored size.
                var fragments = new ErpIndexFragment[fragmentCount];
                for (var f = 0; f < fragmentCount; ++f)
                {
                    var name = ReadString(table, ref pos, 4);
                    var offset = ReadUInt64(table, ref pos);
                    var size = ReadUInt64(table, ref pos);
                    var flags = (int)ReadUInt32(table, ref pos);
                    var compression = ErpCompressionAlgorithm.None;
                    var packed = size;
                    if (version > 2)
                    {
                        compression = (ErpCompressionAlgorithm)ReadByte(table, ref pos);
                        packed = ReadUInt64(table, ref pos);
                    }
                    fragments[f] = new ErpIndexFragment(name, offset, size, flags, compression, packed);
                }

                var hash = hashSize > 0 ? table.Slice(pos, hashSize).ToArray() : Array.Empty<byte>();
                pos += hashSize;

                if (pos - bodyStart != declared || pos > table.Length)
                {
                    throw new FileFormatException(
                        $"Resource {i} says it is {declared} bytes but reads as {pos - bodyStart}.");
                }

                entries.Add(new ErpIndexEntry(identifier, resourceType) { Unknown = unknown, Fragments = fragments, Hash = hash });
            }

            return entries;
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> span, ref int pos)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
            pos += 4;
            return value;
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> span, ref int pos)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(span[pos..]);
            pos += 8;
            return value;
        }

        /// <summary>
        /// One fragment's bytes, decompressed, read straight from the archive at
        /// <paramref name="path"/> without loading anything else.
        /// </summary>
        public static byte[] ReadFragment(string path, ErpIndexResult index, ErpIndexFragment fragment)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
            return ReadFragment(stream, index, fragment);
        }

        /// <summary>One fragment's bytes, decompressed, from an archive already open.</summary>
        public static byte[] ReadFragment(Stream stream, ErpIndexResult index, ErpIndexFragment fragment)
        {
            var start = checked((long)(index.ResourceOffset + fragment.Offset));
            var packed = checked((int)fragment.PackedSize);
            if (start < 0 || start + packed > stream.Length)
            {
                throw new EndOfStreamException($"Fragment '{fragment.Name}' runs past the end of the archive.");
            }

            var data = new byte[packed];
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(data);
            return ErpFragment.Decompress(data, fragment.Compression, fragment.Size);
        }

        private static int ReadUInt16(ReadOnlySpan<byte> span, ref int pos)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]);
            pos += 2;
            return value;
        }

        private static byte ReadByte(ReadOnlySpan<byte> span, ref int pos) => span[pos++];

        /// <summary>
        /// A fixed-width field holding a null-terminated string, matching
        /// <see cref="ErpBinaryReader.ReadString(int)"/>: the field is always
        /// consumed whole, and the text ends at the first null.
        /// </summary>
        private static string ReadString(ReadOnlySpan<byte> span, ref int pos, int length)
        {
            var field = span.Slice(pos, length);
            pos += length;

            var end = field.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
        }
    }
}
