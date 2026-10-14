using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using ZstdNet;

namespace SimplifiedUniversalExporter.Core
{
    internal sealed class ErpSection
    {
        public string Id = "";
        public long FileOffset;
        public long DataSize;
        /// <summary>
        /// Per-fragment flags field. Always present on Version >= 1
        /// ERPs (EgoEngineLibrary calls this "Flags" and reads it
        /// unconditionally). Earlier code in this project called it
        /// "Alignment" because v2 ERPs use it that way; the broader
        /// usage isn't pinned down. Kept as i32 to preserve the
        /// vanilla bit pattern verbatim.
        /// </summary>
        public int Flags;
        public int CompressionType;
        public long CompressedSize;
    }

    internal sealed class ErpResource
    {
        public int Index;
        public string Name = "";
        public string Type = "";
        public int FormatVersion;
        public List<ErpSection> Sections = new List<ErpSection>();

        /// <summary>
        /// Per-resource 16-byte trailer present in ERP major version > 2.
        /// Vanilla ERPs store a non-zero 128-bit value here (appears to be
        /// a per-resource content hash / integrity digest). Previous
        /// reader versions discarded this field; the writer therefore
        /// wrote zero for all 1100 resources on repack, which produced
        /// ERPs that the game's loader rejected.
        ///
        /// We now preserve it verbatim from the source ERP so repack
        /// output matches vanilla byte layout. If missing (older format,
        /// corrupt file, etc.) this stays null and the writer falls back
        /// to 16 zero bytes -- identical to prior behaviour.
        /// </summary>
        public byte[] TrailerBytes;
    }

    internal sealed class ErpContainer
    {
        /// <summary>
        /// Container format version. EgoEngineLibrary reads this as a
        /// single int32 (matching the actual on-disk field); we adopt
        /// that here. Previously this was split into MajorVersion +
        /// MinorVersion, which silently disagreed with reality whenever
        /// the low 16 bits of Version were non-zero.
        /// MajorVersion is kept as an alias on first read so the rest
        /// of the codebase that still references it keeps compiling.
        /// </summary>
        public int Version;
        public int MajorVersion => Version;
        public int MinorVersion => 0;
        public long TocOffset;
        public long TocSize;
        public long ResourceDataOffset;
        public int TotalResources;
        public int TotalSections;
        public List<ErpResource> Resources = new List<ErpResource>();
    }

    internal static class ErpContainerReader
    {
        /// <summary>
        /// Maximum container version we accept. EgoEngineLibrary
        /// likewise pins this to 4 (it rejects anything higher). When
        /// a future game bumps this, the rejection here is the signal
        /// to extend the reader.
        /// </summary>
        private const int SupportedVersion = 4;

        /// <summary>
        /// Compression bytes the format defines. EgoEngineLibrary uses
        /// an enum with these exact values. Anything outside this set
        /// is rejected at read time so silent passthroughs can't mask
        /// real corruption (the previous reader used a
        /// "compressedSize==dataSize" heuristic which would happily
        /// accept a corrupt compressed blob whose size coincidentally
        /// matched the declared uncompressed size).
        /// </summary>
        private static readonly HashSet<byte> KnownCompressionBytes =
            new HashSet<byte> { 0x00, 0x01, 0x03, 0x10, 0x11, 0x81, 0x90, 0x91 };

        private static readonly object _logLock = new object();
        private static string _logPath;

        /// <summary>
        /// One log per ERP read, under the app's temp folder. It used to be written
        /// beside the ERP, which for the game's own files meant inside the install.
        /// </summary>
        private static void SetLogPathForLoadedFile(string loadedFilePath)
        {
            try
            {
                var baseName = Path.GetFileNameWithoutExtension(loadedFilePath);
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = "ERP";

                var dir = Path.Combine(AppPaths.TempRoot, "logs");
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, baseName + "_ERP_Load_Log.txt");
            }
            catch
            {
                _logPath = Path.Combine(Path.GetTempPath(), "simplified-universal-exporter-ERP_Load_Log.txt");
            }
        }

        /// <summary>Where lines go before any ERP has been read.</summary>
        private static string GetTempFallbackLogPath()
        {
            var dir = Path.Combine(AppPaths.TempRoot, "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ERP_Load_Log.txt");
        }

        private static void Log(string msg)
        {
            try
            {
                lock (_logLock)
                {
                    if (string.IsNullOrEmpty(_logPath))
                        _logPath = GetTempFallbackLogPath();

                    File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n");
                }
            }
            catch { }
        }

        public static void LogReset(string loadedFilePath)
        {
            try
            {
                lock (_logLock)
                {
                    SetLogPathForLoadedFile(loadedFilePath);
                    File.WriteAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] --- LOG RESET ---\r\n");
                }
            }
            catch { }
        }

        public static string GetCurrentLogPath()
        {
            if (!string.IsNullOrEmpty(_logPath))
                return _logPath;
            return GetTempFallbackLogPath();
        }

        public static ErpContainer Read(string erpPath)
        {
            LogReset(erpPath);
            Log("ErpContainerReader.Read start");
            Log("ERP Path: " + erpPath);

            if (string.IsNullOrWhiteSpace(erpPath))
                throw new ArgumentException(nameof(erpPath));

            using var fs = File.OpenRead(erpPath);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            Log("ERP Length: " + fs.Length);

            if (fs.Length < 64)
                throw new InvalidDataException("ERP too small.");

            uint formatId = br.ReadUInt32();
            Log("Magic u32: 0x" + formatId.ToString("X8"));
            if (formatId != 0x4B505245)
                throw new InvalidDataException("Not an ERPK container.");

            // EgoEngineLibrary reads Version as a single int32 (NOT
            // two u16s). The previous split into MajorVersion +
            // MinorVersion was structurally wrong; reject any version
            // outside the supported range up front so corruption
            // can't masquerade as a high minor number.
            int version = br.ReadInt32();
            Log($"Version: {version}");
            if (version < 0 || version > SupportedVersion)
                throw new InvalidDataException(
                    $"Unsupported ERP version {version} (this reader knows 0..{SupportedVersion}).");

            ulong unk0 = br.ReadUInt64();
            ulong tocOffset = br.ReadUInt64();
            ulong tocSize = br.ReadUInt64();
            ulong resourceDataOffset = br.ReadUInt64();
            ulong unk1 = br.ReadUInt64();

            Log($"Header unk0=0x{unk0:X} unk1=0x{unk1:X}");
            Log($"TOC offset=0x{tocOffset:X} size=0x{tocSize:X} ({tocSize})");
            Log($"ResourceDataOffset=0x{resourceDataOffset:X} ({resourceDataOffset})");

            if (tocOffset >= (ulong)fs.Length)
                throw new InvalidDataException("Invalid TOC offset.");
            if (resourceDataOffset >= (ulong)fs.Length)
                throw new InvalidDataException("Invalid resource data offset.");

            fs.Position = (long)tocOffset;

            int totalResources = br.ReadInt32();
            int totalSections = br.ReadInt32();
            Log($"TOC: totalResources={totalResources} totalSections={totalSections}");

            if (totalResources <= 0 || totalResources > 5_000_000)
                throw new InvalidDataException($"Unreasonable resource count: {totalResources}");

            var container = new ErpContainer
            {
                Version = version,
                TocOffset = (long)tocOffset,
                TocSize = (long)tocSize,
                ResourceDataOffset = (long)resourceDataOffset,
                TotalResources = totalResources,
                TotalSections = totalSections
            };

            int maxVerboseLog = Math.Min(totalResources, 50);
            Log($"Verbose log for first {maxVerboseLog} resources; compact (type+name) line for the rest.");

            for (int i = 0; i < totalResources; i++)
            {
                if (fs.Position + 7 >= (long)tocOffset + (long)tocSize && tocSize > 0)
                {
                    Log($"WARNING: Reached end of TOC early at resource {i}. fs.Position=0x{fs.Position:X}");
                    break;
                }

                uint resourceSize = br.ReadUInt32();

                // resourceSize describes the bytes AFTER this size
                // field (it doesn't include itself). Capturing
                // entryStart here -- AFTER the read -- means
                // `fs.Position - entryStart` at end-of-resource
                // matches resourceSize exactly.
                long entryStart = fs.Position;

                // EgoEngineLibrary reads the identifier length as a
                // SIGNED short (negative values are nonsensical but
                // valid encodings); we match that and validate.
                short idStringSize = br.ReadInt16();
                if (idStringSize <= 0)
                    throw new InvalidDataException($"Bad idStringSize {idStringSize} at resource {i}");

                byte[] idBytes = br.ReadBytes(idStringSize);
                if (idBytes.Length != idStringSize)
                    throw new EndOfStreamException();

                string name = DecodeIdString(idBytes);

                string type = ReadFixedString(br, 16);

                int formatVersion = br.ReadInt32();

                // EgoEngineLibrary reads `Unknown2 (i16)` only when
                // the CONTAINER version >= 4. Same here.
                if (version >= 4)
                    br.ReadInt16();

                byte sectionCount = br.ReadByte();

                var res = new ErpResource
                {
                    Index = i,
                    Name = name,
                    Type = type,
                    FormatVersion = formatVersion
                };

                for (int s = 0; s < sectionCount; s++)
                {
                    string sid = ReadFixedString(br, 4);

                    long fileOffset = (long)br.ReadUInt64();
                    long dataSize = (long)br.ReadUInt64();

                    // Flags is ALWAYS read as i32 (EgoEngineLibrary
                    // reads it unconditionally). The previous code
                    // skipped this for version < 2 and desynced by
                    // 4 bytes per fragment on those ERPs.
                    int flags = br.ReadInt32();

                    // Compression byte + PackedSize only exist on
                    // version > 2. For older containers, treat as
                    // raw (compressionType=0, compressedSize=dataSize)
                    // without consuming any extra bytes.
                    int compressionType;
                    long compressedSize;
                    if (version > 2)
                    {
                        compressionType = br.ReadByte();
                        if (!KnownCompressionBytes.Contains((byte)compressionType))
                            throw new InvalidDataException(
                                $"Unknown compression byte 0x{compressionType:X2} " +
                                $"at resource {i} section {s}");
                        compressedSize = (long)br.ReadUInt64();
                    }
                    else
                    {
                        compressionType = 0;
                        compressedSize = dataSize;
                    }

                    res.Sections.Add(new ErpSection
                    {
                        Id = sid,
                        FileOffset = fileOffset,
                        DataSize = dataSize,
                        Flags = flags,
                        CompressionType = compressionType,
                        CompressedSize = compressedSize
                    });
                }

                // 16-byte trailer hash is present only on version > 2.
                if (version > 2)
                    res.TrailerBytes = br.ReadBytes(16);

                container.Resources.Add(res);

                if (i < maxVerboseLog)
                {
                    Log($"R[{i}] size={resourceSize} nameLen={idStringSize} type='{type}' fmtVer={formatVersion} secCount={sectionCount}");
                    Log($"     name='{Trunc(name, 200)}'");
                    for (int s = 0; s < res.Sections.Count; s++)
                    {
                        var sec = res.Sections[s];
                        Log($"     S[{s}] id='{sec.Id}' off=0x{sec.FileOffset:X} data={sec.DataSize} compType=0x{sec.CompressionType:X} compSize={sec.CompressedSize} flags=0x{sec.Flags:X}");
                    }
                }
                else
                {
                    Log($"R[{i}] type='{type}' name='{Trunc(name, 200)}' secCount={sectionCount}");
                }

                // Self-consistency: bytes consumed for THIS resource
                // must equal the declared resourceSize. EgoEngineLibrary
                // throws on mismatch; we do the same. Previously we
                // silently re-seeked to `entryStart + resourceSize`,
                // which papered over Version-parsing bugs and Flags-
                // skipping bugs by hiding the desync.
                long consumed = fs.Position - entryStart;
                if (consumed != resourceSize)
                {
                    throw new InvalidDataException(
                        $"Resource {i} declared size {resourceSize} but the parser " +
                        $"consumed {consumed} bytes (delta {consumed - resourceSize}). " +
                        $"Indicates a header-parsing bug or a malformed ERP.");
                }
            }

            // ----- Resource-type histogram -----
            // Quick at-a-glance summary of what's inside the ERP. Useful for
            // figuring out whether this container holds the actual model/mesh
            // resources or only orchestration data (layers/world/event graph)
            // that references meshes living in sibling ERPs.
            var typeHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in container.Resources)
            {
                string key = string.IsNullOrEmpty(r.Type) ? "(empty)" : r.Type;
                if (typeHistogram.TryGetValue(key, out int count))
                    typeHistogram[key] = count + 1;
                else
                    typeHistogram[key] = 1;
            }

            var sortedTypes = new List<KeyValuePair<string, int>>(typeHistogram);
            sortedTypes.Sort((a, b) =>
            {
                int byCount = b.Value.CompareTo(a.Value);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });

            Log("--- Resource type histogram ---");
            foreach (var kv in sortedTypes)
                Log($"  {kv.Value,5} x {kv.Key}");
            Log($"--- {container.Resources.Count} resources across {sortedTypes.Count} distinct types ---");

            Log($"Read complete. Parsed resources: {container.Resources.Count}");
            Log("ErpContainerReader.Read end");
            return container;
        }

        public static byte[] ReadSectionBytes(string erpPath, ErpContainer container, ErpSection section)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (section == null) throw new ArgumentNullException(nameof(section));

            long abs = container.ResourceDataOffset + section.FileOffset;
            long readLen = section.CompressionType == 0 ? section.DataSize : section.CompressedSize;

            Log($"ReadSectionBytes: secId='{section.Id}' abs=0x{abs:X} readLen={readLen} dataSize={section.DataSize} compType=0x{section.CompressionType:X}");

            if (abs < 0) throw new InvalidDataException("Negative section offset.");
            if (readLen < 0) throw new InvalidDataException("Negative section size.");

            using var fs = File.OpenRead(erpPath);
            if (abs > fs.Length) throw new EndOfStreamException("Section offset beyond EOF.");
            if (abs + readLen > fs.Length) throw new EndOfStreamException("Section exceeds EOF.");

            fs.Position = abs;
            byte[] buf = new byte[checked((int)readLen)];
            int got = 0;
            while (got < buf.Length)
            {
                int r = fs.Read(buf, got, buf.Length - got);
                if (r <= 0) throw new EndOfStreamException();
                got += r;
            }

            Log($"ReadSectionBytes: got={buf.Length} first4={Hex4(buf)}");

            // Explicit enum dispatch, matching EgoEngineLibrary's
            // ErpCompressionAlgorithm enum:
            //   None (0x00), None2 (0x81), None3 (0x90), None4 (0x91)
            //                    -> raw passthrough
            //   Zlib (0x01)      -> Zlib inflate
            //   Zstd (0x10), ZStandard2 (0x03), ZStandard3 (0x11)
            //                    -> Zstandard decompress
            // Previously a "compressedSize==dataSize" heuristic was
            // used to treat 0x90/0x91 as raw. That heuristic also
            // happily accepted any corrupt compressed blob whose
            // packed length coincidentally matched the uncompressed
            // length. Explicit cases close that hole.
            switch (section.CompressionType)
            {
                case 0x00:
                case 0x81:
                case 0x90:
                case 0x91:
                    return buf;

                case 0x01:
                {
                    var plain = DecompressZlib(buf, checked((int)section.DataSize));
                    Log($"Zlib OK: out={plain.Length} first4={Hex4(plain)}");
                    return plain;
                }

                case 0x03:
                case 0x10:
                case 0x11:
                {
                    var plain = DecompressZstd(buf, checked((int)section.DataSize));
                    Log($"Zstd OK (compType=0x{section.CompressionType:X}): out={plain.Length} first4={Hex4(plain)}");
                    return plain;
                }

                default:
                    throw new NotSupportedException(
                        $"Unsupported ERP compression type: 0x{section.CompressionType:X}");
            }
        }

        private static byte[] DecompressZstd(byte[] compressed, int expectedSize)
        {
            Log($"DecompressZstd: in={compressed.Length} expectedOut={expectedSize} magic={Hex4(compressed)}");

            try
            {
                using (var d = new Decompressor())
                {
                    var t = d.GetType();

                    var m1 = t.GetMethod("Unwrap", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(byte[]) }, null);
                    if (m1 != null)
                    {
                        var obj = m1.Invoke(d, new object[] { compressed });
                        var outBytes = (byte[])obj;
                        Log($"DecompressZstd: Unwrap(byte[]) OK out={outBytes.Length} first4={Hex4(outBytes)}");
                        return outBytes;
                    }

                    var m2 = t.GetMethod("Unwrap", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(byte[]), typeof(int) }, null);
                    if (m2 != null)
                    {
                        var obj = m2.Invoke(d, new object[] { compressed, expectedSize });
                        var outBytes = (byte[])obj;
                        Log($"DecompressZstd: Unwrap(byte[],int) OK out={outBytes.Length} first4={Hex4(outBytes)}");
                        return outBytes;
                    }

                    throw new MissingMethodException("ZstdNet.Decompressor has no Unwrap overload we can call.");
                }
            }
            catch (Exception ex)
            {
                Log("DecompressZstd FAILED: " + ex.GetType().FullName + " - " + ex.Message);
                if (ex is TargetInvocationException tie && tie.InnerException != null)
                    Log("DecompressZstd Inner: " + tie.InnerException.GetType().FullName + " - " + tie.InnerException.Message);
                throw;
            }
        }

        private static byte[] DecompressZlib(byte[] compressed, int expectedSize)
        {
            Log($"DecompressZlib: in={compressed.Length} expectedOut={expectedSize} magic={Hex4(compressed)}");
            using var src = new MemoryStream(compressed, writable: false);
            using var zs = new ZLibStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream(expectedSize > 0 ? expectedSize : 0);

            zs.CopyTo(dst);

            byte[] outBytes = dst.ToArray();
            if (expectedSize > 0 && outBytes.Length != expectedSize)
            {
                if (outBytes.Length > expectedSize)
                    Array.Resize(ref outBytes, expectedSize);
            }

            return outBytes;
        }

        private static string DecodeIdString(byte[] idBytes)
        {
            string s = Encoding.UTF8.GetString(idBytes);
            return s.TrimEnd('\0');
        }

        private static string ReadFixedString(BinaryReader br, int len)
        {
            byte[] b = br.ReadBytes(len);
            if (b.Length != len)
                throw new EndOfStreamException();

            int n = Array.IndexOf(b, (byte)0);
            if (n < 0) n = b.Length;
            // EgoEngineLibrary uses UTF-8 (its BinaryReader is UTF-8
            // by construction). Type tags / fragment ids are ASCII
            // in practice, so this is identical on every file we've
            // seen -- but matches A's contract for forward
            // compatibility.
            return Encoding.UTF8.GetString(b, 0, n);
        }

        private static string Hex4(byte[] b)
        {
            if (b == null || b.Length == 0) return "null";
            int n = Math.Min(4, b.Length);
            var sb = new StringBuilder(n * 2);
            for (int i = 0; i < n; i++)
                sb.Append(b[i].ToString("X2"));
            return sb.ToString();
        }

        private static string Trunc(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }
    }
}