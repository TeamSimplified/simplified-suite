using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

        /// <summary>The library's description of this fragment, which reads it.</summary>
        public EgoEngineLibrary.Archive.Erp.ErpIndexFragment Fragment;
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

        /// <summary>The library's table, which fragments are read through.</summary>
        public EgoEngineLibrary.Archive.Erp.ErpIndexResult Index;
    }

    internal static class ErpContainerReader
    {
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

        /// <summary>
        /// The archive's resource table, from <see cref="EgoEngineLibrary.Archive.Erp.ErpIndex"/>:
        /// the table only, with each fragment's place, so an ERP of any size is opened
        /// without its payloads. This used to be a parser of its own, with its own
        /// compression dispatch and ZstdNet; the format lives in the shared library now.
        /// </summary>
        public static ErpContainer Read(string erpPath)
        {
            LogReset(erpPath);
            Log("ErpContainerReader.Read start");
            Log("ERP Path: " + erpPath);

            if (string.IsNullOrWhiteSpace(erpPath))
                throw new ArgumentException(nameof(erpPath));

            EgoEngineLibrary.Archive.Erp.ErpIndexResult index;
            try
            {
                index = EgoEngineLibrary.Archive.Erp.ErpIndex.Read(erpPath);
            }
            catch (Exception ex) when (ex is FileFormatException or NotSupportedException)
            {
                // Callers catch InvalidDataException for "not an archive I can read".
                throw new InvalidDataException(ex.Message, ex);
            }

            Log($"Version: {index.Version}");
            Log($"ResourceDataOffset=0x{index.ResourceOffset:X} ({index.ResourceOffset})");

            var container = new ErpContainer
            {
                Version = index.Version,
                ResourceDataOffset = (long)index.ResourceOffset,
                TotalResources = index.Resources.Count,
                Index = index,
            };

            int maxVerboseLog = Math.Min(index.Resources.Count, 50);
            Log($"Verbose log for first {maxVerboseLog} resources; compact (type+name) line for the rest.");

            for (int i = 0; i < index.Resources.Count; i++)
            {
                var entry = index.Resources[i];
                var res = new ErpResource
                {
                    Index = i,
                    Name = entry.Identifier,
                    Type = entry.ResourceType,
                    FormatVersion = entry.Unknown,
                    TrailerBytes = entry.Hash.Length > 0 ? entry.Hash : null,
                };

                foreach (var f in entry.Fragments)
                {
                    res.Sections.Add(new ErpSection
                    {
                        Id = f.Name,
                        FileOffset = (long)f.Offset,
                        DataSize = (long)f.Size,
                        Flags = f.Flags,
                        CompressionType = (int)f.Compression,
                        CompressedSize = (long)f.PackedSize,
                        Fragment = f,
                    });
                }
                container.TotalSections += res.Sections.Count;
                container.Resources.Add(res);

                if (i < maxVerboseLog)
                {
                    Log($"R[{i}] type='{res.Type}' fmtVer={res.FormatVersion} secCount={res.Sections.Count}");
                    Log($"     name='{Trunc(res.Name, 200)}'");
                    for (int s = 0; s < res.Sections.Count; s++)
                    {
                        var sec = res.Sections[s];
                        Log($"     S[{s}] id='{sec.Id}' off=0x{sec.FileOffset:X} data={sec.DataSize} compType=0x{sec.CompressionType:X} compSize={sec.CompressedSize} flags=0x{sec.Flags:X}");
                    }
                }
                else
                {
                    Log($"R[{i}] type='{res.Type}' name='{Trunc(res.Name, 200)}' secCount={res.Sections.Count}");
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
                typeHistogram[key] = typeHistogram.TryGetValue(key, out int count) ? count + 1 : 1;
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

        /// <summary>One fragment's bytes, decompressed, read on demand by the library.</summary>
        public static byte[] ReadSectionBytes(string erpPath, ErpContainer container, ErpSection section)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (section == null) throw new ArgumentNullException(nameof(section));

            Log($"ReadSectionBytes: secId='{section.Id}' off=0x{container.ResourceDataOffset + section.FileOffset:X} dataSize={section.DataSize} compType=0x{section.CompressionType:X}");
            var plain = EgoEngineLibrary.Archive.Erp.ErpIndex.ReadFragment(erpPath, container.Index, section.Fragment);
            Log($"ReadSectionBytes: out={plain.Length} first4={Hex4(plain)}");
            return plain;
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
