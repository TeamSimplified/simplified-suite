using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EgoEngineLibrary.Archive.Erp
{
    /// <summary>One resource, as much of it as reading the table alone can tell.</summary>
    public readonly record struct ErpIndexEntry(string Identifier, string ResourceType);

    /// <summary>What an archive holds, without its contents.</summary>
    public sealed record ErpIndexResult(int Version, IReadOnlyList<ErpIndexEntry> Resources);

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
    /// begin. What comes back is identifiers and types. Anything more than that
    /// means opening the archive properly.
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

            if (count == 0) return new ErpIndexResult(version, Array.Empty<ErpIndexEntry>());

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
                return new ErpIndexResult(version, ParseTable(table, version, count));
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
            // contains is what ErpFragment.Read consumes.
            var fragmentSize = version > 2 ? 33 : 16;
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

                pos += 4 + unknown2Size; // unknown, unknown2

                var fragments = ReadByte(table, ref pos);
                pos += fragments * fragmentSize + hashSize;

                if (pos - bodyStart != declared || pos > table.Length)
                {
                    throw new FileFormatException(
                        $"Resource {i} says it is {declared} bytes but reads as {pos - bodyStart}.");
                }

                entries.Add(new ErpIndexEntry(identifier, resourceType));
            }

            return entries;
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> span, ref int pos)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
            pos += 4;
            return value;
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
