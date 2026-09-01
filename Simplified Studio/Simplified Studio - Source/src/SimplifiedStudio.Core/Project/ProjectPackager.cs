using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using EgoEngineLibrary.Archive.Erp;
using SimplifiedStudio.Core.Util;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// Packages an open project into a redistributable layout:
/// <code>
/// &lt;out&gt;/
///   mod.serps                              — name / author / version
///   F1 25/
///     shader_package_2025/
///       &lt;shader&gt;.erp        ← copy from game folder + every enabled part merged in
///       &lt;shader&gt;_amd.erp    ← same
/// </code>
/// where <c>&lt;shader&gt;</c> is <see cref="ProjectInfo.ShaderPackage"/> (the
/// <c>erp_file_string</c> the project picked up from the source .serps).
/// </summary>
public static class ProjectPackager
{
    /// <summary>Folder name reproduced at the package root — mirrors the game install dir.</summary>
    public const string GameFolderName = "F1 25";

    /// <summary>Subfolder inside <see cref="GameFolderName"/> that holds the shader-package ERPs.</summary>
    public const string ShaderFolderName = "shader_package_2025";

    /// <summary>Info file at the root of a packaged mod.</summary>
    public const string ModInfoFileName = "mod.serps";

    /// <summary>Info file at the root of an exported template — the same shape as mod.serps.</summary>
    public const string TemplateInfoFileName = "template.serps";

    /// <summary>
    /// Fields common to <c>mod.serps</c> and <c>template.serps</c>, kept in one
    /// place so they can't drift apart.
    ///
    /// Order is pinned with <c>JsonPropertyOrder</c> rather than left to
    /// declaration order, because the field that varies between the two is the
    /// first one and lives on the derived types.
    /// </summary>
    private abstract class SerpsInfoBase
    {
        [System.Text.Json.Serialization.JsonPropertyName("author")]
        [System.Text.Json.Serialization.JsonPropertyOrder(2)]
        public string Author { get; init; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("version")]
        [System.Text.Json.Serialization.JsonPropertyOrder(3)]
        public string Version { get; init; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("game")]
        [System.Text.Json.Serialization.JsonPropertyOrder(4)]
        public string Game { get; init; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("game_version")]
        [System.Text.Json.Serialization.JsonPropertyOrder(5)]
        public string GameVersion { get; init; } = "";
    }

    /// <summary>
    /// What both <c>mod.serps</c> and <c>template.serps</c> carry — the same
    /// fields in the same order, because since categories became folders there
    /// is nothing left to tell the two documents apart. They keep separate file
    /// names because a template and a packaged mod are different things.
    ///
    /// This is <see cref="ProjectInfo"/> minus what only makes sense inside a
    /// project (the selection, the source template, the custom flag), plus the
    /// two fields read off the game install at package time.
    /// </summary>
    private sealed class SerpsInfo : SerpsInfoBase
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        [System.Text.Json.Serialization.JsonPropertyOrder(1)]
        public string Name { get; init; } = "";

        /// <summary>
        /// Which Studio wrote this. Its absence tells a later build the file
        /// predates 1.3 — flat layout, and a conflict digit that used to live
        /// at the front of the prefix.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("studio_version")]
        [System.Text.Json.Serialization.JsonPropertyOrder(6)]
        public string StudioVersion { get; init; } = "";
    }

    /// <summary>
    /// The encoder allows non-ASCII through unescaped, so the game name lands in
    /// mod.serps as <c>F1® 25</c> rather than <c>F1® 25</c>. Both are valid
    /// JSON and parse the same, but the file is read by people.
    /// </summary>
    private static readonly JsonSerializerOptions SerpsJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    /// Build the package and write it under <paramref name="saveLocation"/>.
    /// Returns the absolute path of the resulting <c>.zip</c> when
    /// <paramref name="zip"/> is true, otherwise the staging folder's
    /// final location inside the save folder.
    /// </summary>
    public static string Package(
        SimplifiedProject project,
        string gameDir,
        string saveLocation,
        bool zip,
        string modVersion,
        Action<string>? log = null)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(gameDir))
            throw new InvalidOperationException(
                "Game directory isn't configured — set it in Settings before packaging.");
        if (!Directory.Exists(gameDir))
            throw new DirectoryNotFoundException($"Game directory not found: {gameDir}");
        if (string.IsNullOrWhiteSpace(project.Info.ShaderPackage))
            throw new InvalidOperationException(
                "Project has no shader_package in info.json — was the .serps template missing erp_file_string?");
        if (string.IsNullOrWhiteSpace(saveLocation))
            throw new ArgumentException("Save location is required.", nameof(saveLocation));
        Directory.CreateDirectory(saveLocation);

        var shader = project.Info.ShaderPackage;
        var srcDir = Path.Combine(gameDir, ShaderFolderName);
        var srcMain = Path.Combine(srcDir, shader + ".erp");
        var srcAmd  = Path.Combine(srcDir, shader + "_amd.erp");
        if (!File.Exists(srcMain))
            throw new FileNotFoundException(
                $"Shader package ERP not found: {srcMain}", srcMain);
        if (!File.Exists(srcAmd))
            throw new FileNotFoundException(
                $"AMD shader package ERP not found: {srcAmd}", srcAmd);

        // Already in merge order — category folders by their prefix, then parts
        // by theirs — so the number that decides where a part shows in the list
        // is the number that decides which part wins a resource clash.
        var enabled = project.EnabledErpPaths.ToList();
        if (enabled.Count == 0)
            throw new InvalidOperationException(
                "No active (enabled) ERPs in the project — enable at least one part before packaging.");

        // Stage everything in a temp dir, then zip/copy in a single move at the end.
        var stagingRoot = AllocateStaging();
        try
        {
            var stagedShaderDir = Path.Combine(stagingRoot, GameFolderName, ShaderFolderName);
            Directory.CreateDirectory(stagedShaderDir);
            var stagedMain = Path.Combine(stagedShaderDir, Path.GetFileName(srcMain));
            var stagedAmd  = Path.Combine(stagedShaderDir, Path.GetFileName(srcAmd));
            File.Copy(srcMain, stagedMain, overwrite: true);
            File.Copy(srcAmd,  stagedAmd,  overwrite: true);

            // Pre-load every active ERP once — merging into the two targets is
            // the same source-iteration each time, so loading twice is wasted I/O.
            var sources = enabled.Select(p => (Path: p, Erp: ErpFileIO.Load(p))).ToList();

            // The shader package targets also need the ALWAYS-merged 'serps.erp'
            // (two small resources — surface descriptor + shadow material —
            // that the game expects on every mod). The bytes are baked into
            // this assembly as an embedded resource so nothing ships loose;
            // we materialize it to the staging dir for the merge.
            var serpsErpPath = Path.Combine(stagingRoot, "serps.erp");
            ExtractEmbeddedSerpsErp(serpsErpPath);
            sources.Insert(0, (serpsErpPath, ErpFileIO.Load(serpsErpPath)));

            MergeSourcesInto(stagedMain, sources);
            MergeSourcesInto(stagedAmd,  sources);

            // Drop the staged copy — it isn't meant to ship in the final
            // package, only to fuel the merge.
            try { File.Delete(serpsErpPath); } catch { }

            // Capture the game build this mod was packaged against, read from
            // the F1 exe's file version. Best-effort — a blank field never
            // blocks a package.
            var gameVersion = ReadGameVersion(gameDir);
            if (string.IsNullOrEmpty(gameVersion))
                log?.Invoke("  ⚠ Couldn't read the F1 game version from the exe — game_version left blank in mod.serps.");
            else
                log?.Invoke($"  Game version: {gameVersion}");

            // The title itself, from the same exe, so a mod says which game it's
            // for and not just which build.
            var gameName = ReadGameName(gameDir);
            if (string.IsNullOrEmpty(gameName))
                log?.Invoke("  ⚠ Couldn't read the F1 game name from the exe — game left blank in mod.serps.");
            else
                log?.Invoke($"  Game: {gameName}");

            // mod.serps next to the F1 25/ folder.
            File.WriteAllText(
                Path.Combine(stagingRoot, ModInfoFileName),
                JsonSerializer.Serialize(new SerpsInfo
                {
                    Name          = project.Info.Name,
                    Author        = project.Info.Author,
                    Version       = modVersion ?? "",
                    Game          = gameName,
                    GameVersion   = gameVersion,
                    StudioVersion = StudioVersion.Current,
                }, SerpsJson));

            // Final delivery — zip or copy
            var baseName = SanitizeName(string.IsNullOrWhiteSpace(project.Info.Name)
                ? "mod" : project.Info.Name);
            // Append the user's version verbatim — they get to choose whether to prefix it with "v".
            if (!string.IsNullOrWhiteSpace(modVersion)) baseName += " " + modVersion.Trim();

            if (zip)
            {
                var zipPath = Path.Combine(saveLocation, baseName + ".zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(stagingRoot, zipPath,
                    CompressionLevel.Optimal, includeBaseDirectory: false);
                return zipPath;
            }

            var outDir = Path.Combine(saveLocation, baseName);
            CopyDirectoryRecursive(stagingRoot, outDir, overwrite: true);
            return outDir;
        }
        finally
        {
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Build a redistributable <c>.serps</c> template from the open project.
    /// Unlike <see cref="Package"/> this does NOT merge into shader-package
    /// ERPs — a .serps is a template, not an installable mod.
    ///
    /// The archive mirrors the project: every ERP under an <c>ERPs/</c> folder,
    /// in the category folder it sits in. The structure <em>is</em> the
    /// categories, so nothing has to describe them — a project made from the
    /// template comes out grouped the way the author left it.
    ///
    /// Every part ships, switched on or not. A template is a pool to choose
    /// from, so which parts its author happened to have active is beside the
    /// point — and shipping the lot means the recipient gets the same choices
    /// the author had.
    ///
    /// The archive comment stays <c>{"erp_file_string":"&lt;shader&gt;"}</c>,
    /// which is where the shader package lives for a template.
    /// </summary>
    public static string PackageAsSerps(
        SimplifiedProject project,
        string gameDir,
        string saveLocation,
        string modVersion)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(saveLocation))
            throw new ArgumentException("Save location is required.", nameof(saveLocation));
        if (string.IsNullOrWhiteSpace(project.Info.ShaderPackage))
            throw new InvalidOperationException(
                "Project has no shader_package set — fill it in via Edit project before exporting as .serps.");

        var parts = project.AllPartPaths.ToList();
        if (parts.Count == 0)
            throw new InvalidOperationException(
                "This project has no ERPs — add at least one before exporting as .serps.");

        Directory.CreateDirectory(saveLocation);

        var baseName = SanitizeName(string.IsNullOrWhiteSpace(project.Info.Name)
            ? "template" : project.Info.Name);
        if (!string.IsNullOrWhiteSpace(modVersion)) baseName += " " + modVersion.Trim();
        var outPath = Path.Combine(saveLocation, baseName + ".serps");

        if (File.Exists(outPath)) File.Delete(outPath);

        // The ZIP comment is read back by ProjectStore.ExtractShaderPackage —
        // keep the JSON shape ("erp_file_string": "<shader>") it expects.
        var comment = JsonSerializer.Serialize(new
        {
            erp_file_string = project.Info.ShaderPackage,
        });

        using (var fs = File.Create(outPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, System.Text.Encoding.UTF8))
        {
            // Category folders first, as directory entries, so one whose parts
            // are all switched off still reaches the recipient.
            foreach (var folder in project.CategoryFolderNames)
                zip.CreateEntry($"{ProjectStore.ErpsFolderName}/{folder}/");

            foreach (var partPath in parts)
            {
                var entryName = ProjectStore.ErpsFolderName + "/" + partPath.Replace('\\', '/');
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var es = entry.Open();
                using var ers = File.OpenRead(project.PathOf(partPath));
                ers.CopyTo(es);
            }

            // template.serps — the same document a packaged mod carries as
            // mod.serps. The grouping isn't in here any more; it's the folders.
            var info = new SerpsInfo
            {
                Name          = project.Info.Name,
                Author        = project.Info.Author,
                Version       = modVersion ?? "",
                Game          = ReadGameName(gameDir),
                GameVersion   = ReadGameVersion(gameDir),
                StudioVersion = StudioVersion.Current,
            };

            var infoEntry = zip.CreateEntry(TemplateInfoFileName, CompressionLevel.Optimal);
            using (var es = infoEntry.Open())
            using (var writer = new StreamWriter(es, System.Text.Encoding.UTF8))
            {
                writer.Write(JsonSerializer.Serialize(info, SerpsJson));
            }

            // ZipArchive doesn't expose Comment directly; the underlying
            // ZipFile API does. We round-trip through ZipFile to set it.
        }

        // Write the comment via System.IO.Compression.ZipFile, which is the
        // only way to set the archive comment from BCL today.
        SetZipArchiveComment(outPath, comment);

        return outPath;
    }

    /// <summary>
    /// Open the ZIP at <paramref name="zipPath"/> and overwrite its archive
    /// comment. ZipArchive doesn't expose Comment as a property, so we use
    /// the lower-level approach: write the comment bytes directly to the
    /// End-of-Central-Directory record at the tail of the file. The EOCD
    /// is fixed-length (22 bytes) followed by an optional variable-length
    /// comment; we rewrite the comment length + bytes.
    /// </summary>
    private static void SetZipArchiveComment(string zipPath, string comment)
    {
        var commentBytes = System.Text.Encoding.UTF8.GetBytes(comment ?? "");
        if (commentBytes.Length > ushort.MaxValue)
            throw new InvalidOperationException(
                $"ZIP archive comment too large ({commentBytes.Length} bytes; max {ushort.MaxValue}).");

        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.ReadWrite);
        // Scan backwards from EOF for the End-of-Central-Directory signature
        // (PK\x05\x06). Spec allows the EOCD to start at most 65,535+22 bytes
        // from the end, but in practice it sits at the very tail.
        const uint EocdSignature = 0x06054b50;
        const int EocdMinSize = 22;
        var maxScan = (long)Math.Min(fs.Length, ushort.MaxValue + EocdMinSize);
        var scanStart = fs.Length - maxScan;
        fs.Seek(scanStart, SeekOrigin.Begin);
        var buf = new byte[maxScan];
        ReadFully(fs, buf, 0, buf.Length);

        long eocdPos = -1;
        for (var i = buf.Length - EocdMinSize; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(buf, i) == EocdSignature)
            {
                eocdPos = scanStart + i;
                break;
            }
        }
        if (eocdPos < 0)
            throw new InvalidDataException("Couldn't locate End-of-Central-Directory record.");

        // Truncate any existing comment, then rewrite the comment length +
        // comment bytes. EOCD layout: 20 bytes fixed + ushort commentLen +
        // commentLen bytes of comment.
        var newLen = eocdPos + EocdMinSize + commentBytes.Length;
        fs.SetLength(newLen);
        fs.Seek(eocdPos + 20, SeekOrigin.Begin);
        Span<byte> lenBytes = stackalloc byte[2];
        lenBytes[0] = (byte)(commentBytes.Length & 0xFF);
        lenBytes[1] = (byte)((commentBytes.Length >> 8) & 0xFF);
        fs.Write(lenBytes);
        fs.Write(commentBytes, 0, commentBytes.Length);
    }

    private static void ReadFully(Stream s, byte[] buf, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = s.Read(buf, offset + total, count - total);
            if (n <= 0) break;
            total += n;
        }
    }

    /// <summary>
    /// Load <paramref name="targetPath"/>, merge every source ERP into it (source
    /// wins on identifier conflict), then save it back in place. Mirrors the CLI
    /// merge command's repoint pattern so UpdateOffsets uses the target's version
    /// for size math.
    /// </summary>
    private static void MergeSourcesInto(string targetPath,
        IReadOnlyCollection<(string Path, ErpFile Erp)> sources)
    {
        var target = ErpFileIO.Load(targetPath);
        foreach (var (_, src) in sources)
        {
            // Snapshot first — Resources.Add mutates the iterated list.
            foreach (var res in src.Resources.ToList())
            {
                var existing = target.Resources.Find(r => r.Identifier == res.Identifier);
                if (existing is not null) target.Resources.Remove(existing);

                res.ParentFile = target;
                foreach (var frag in res.Fragments) frag.ParentFile = target;
                target.Resources.Add(res);
            }
        }
        target.UpdateOffsets();
        ErpFileIO.Save(target, targetPath);
    }

    /// <summary>
    /// The <c>F1_*.exe</c> at the top of <paramref name="gameDir"/>, or null.
    /// </summary>
    private static string? FindGameExe(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return null;
        return Directory
            .EnumerateFiles(gameDir, "F1_*.exe", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
    }

    /// <summary>
    /// Read the F1 game build version from the <c>F1_*.exe</c> at the top of
    /// <paramref name="gameDir"/>, as a dotted
    /// <c>Major.Minor.Build.Private</c> string (e.g. <c>1.0.138.5601</c>).
    /// Best-effort: returns an empty string when the exe is missing or its
    /// version info can't be read — never throws.
    /// </summary>
    public static string ReadGameVersion(string gameDir)
    {
        try
        {
            var exe = FindGameExe(gameDir);
            if (exe is null) return "";
            var vi = FileVersionInfo.GetVersionInfo(exe);
            return $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}.{vi.FilePrivatePart}";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Read the game's name from the same exe — its <c>ProductName</c>, verbatim.
    ///
    /// Every F1 title from 2015 to 25 declares one, so this is the title's own
    /// spelling rather than something inferred: <c>F1 2015</c> through
    /// <c>F1 23</c>, then <c>F1® 24</c> and <c>F1® 25</c> once the mark was
    /// added. The exe's file name is only a fallback, for an exe that somehow
    /// carries no product name at all.
    ///
    /// Best-effort like the version: an empty string never blocks a package.
    /// </summary>
    public static string ReadGameName(string gameDir)
    {
        try
        {
            var exe = FindGameExe(gameDir);
            if (exe is null) return "";

            var productName = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
            if (!string.IsNullOrEmpty(productName)) return productName;

            // F1_25.exe → F1 25
            return Path.GetFileNameWithoutExtension(exe).Replace('_', ' ');
        }
        catch
        {
            return "";
        }
    }

    private static string AllocateStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "simplified-studio", "package",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CopyDirectoryRecursive(string src, string dst, bool overwrite)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDirectoryRecursive(d, Path.Combine(dst, Path.GetFileName(d)), overwrite);
    }

    /// <summary>Strip path-unfriendly characters from the mod name for use as a folder / zip name.</summary>
    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var span = name.AsSpan();
        var sb = new System.Text.StringBuilder(span.Length);
        foreach (var c in span) sb.Append(invalid.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().Trim('.', ' ');
        return string.IsNullOrEmpty(s) ? "mod" : s;
    }

    /// <summary>
    /// Write the embedded <c>serps.erp</c> resource to disk. The resource is
    /// declared in <c>SimplifiedStudio.Core.csproj</c> with <c>LogicalName="serps.erp"</c>
    /// so the lookup name is stable regardless of the assembly's namespace.
    /// </summary>
    private static void ExtractEmbeddedSerpsErp(string destPath)
    {
        var asm = typeof(ProjectPackager).Assembly;
        using var rs = asm.GetManifestResourceStream("serps.erp")
            ?? throw new InvalidOperationException(
                "Embedded resource 'serps.erp' not found in SimplifiedStudio.Core. " +
                "Check the <EmbeddedResource> entry in the csproj.");
        using var fs = File.Create(destPath);
        rs.CopyTo(fs);
    }
}
