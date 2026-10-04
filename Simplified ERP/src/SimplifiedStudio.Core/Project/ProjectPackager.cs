using System.IO;
using System.IO.Compression;
using System.Text.Json;
using EgoEngineLibrary.Archive.Erp;
using SimplifiedStudio.Core.Util;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// Packages an open project into a redistributable layout:
/// <code>
/// &lt;out&gt;/
///   mod.serps                              — mod_name / author / version
///   F1 25/
///     shader_package_2025/
///       &lt;shader&gt;.erp        ← copy from game folder + every enabled ERP merged in
///       &lt;shader&gt;_amd.erp    ← same
/// </code>
/// where <c>&lt;shader&gt;</c> is <see cref="ModInfo.ShaderPackage"/> (the
/// <c>erp_file_string</c> the project picked up from the source .serps).
/// </summary>
public static class ProjectPackager
{
    /// <summary>Folder name reproduced at the package root — mirrors the game install dir.</summary>
    public const string GameFolderName = "F1 25";

    /// <summary>Subfolder inside <see cref="GameFolderName"/> that holds the shader-package ERPs.</summary>
    public const string ShaderFolderName = "shader_package_2025";

    private static readonly JsonSerializerOptions SerpsJson = new() { WriteIndented = true };

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
        if (string.IsNullOrWhiteSpace(project.ModInfo.ShaderPackage))
            throw new InvalidOperationException(
                "Project has no shader_package in mod.json — was the .serps template missing erp_file_string?");
        if (string.IsNullOrWhiteSpace(saveLocation))
            throw new ArgumentException("Save location is required.", nameof(saveLocation));
        Directory.CreateDirectory(saveLocation);

        var shader = project.ModInfo.ShaderPackage;
        var srcDir = Path.Combine(gameDir, ShaderFolderName);
        var srcMain = Path.Combine(srcDir, shader + ".erp");
        var srcAmd  = Path.Combine(srcDir, shader + "_amd.erp");
        if (!File.Exists(srcMain))
            throw new FileNotFoundException(
                $"Shader package ERP not found: {srcMain}", srcMain);
        if (!File.Exists(srcAmd))
            throw new FileNotFoundException(
                $"AMD shader package ERP not found: {srcAmd}", srcAmd);

        // The pair this project merges into comes out of the user's install,
        // because the game's own shaders have to be in them. Anything already
        // merged into one goes out inside this package, so say so before it does.
        foreach (var warning in Simplified.Shared.GameFileGuard.Warnings(gameDir, new[]
                 {
                     Simplified.Shared.GameFileGuard.ShaderPath(Path.GetFileName(srcMain)),
                     Simplified.Shared.GameFileGuard.ShaderPath(Path.GetFileName(srcAmd)),
                 }))
            log?.Invoke($"  ⚠ {warning}");

        var enabled = Directory.Exists(project.EnabledFolder)
            ? Directory.EnumerateFiles(project.EnabledFolder, "*.erp").ToList()
            : new List<string>();
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

            // mod.serps next to the F1 25/ folder. Uses the JSON shape the
            // community tooling expects (mod_name / author / version), not our
            // internal mod.json schema.
            var serps = new
            {
                mod_name = project.ModInfo.ModName,
                author   = project.ModInfo.Author,
                version  = modVersion ?? "",
            };
            File.WriteAllText(
                Path.Combine(stagingRoot, "mod.serps"),
                JsonSerializer.Serialize(serps, SerpsJson));

            // Final delivery — zip or copy
            var baseName = SanitizeName(string.IsNullOrWhiteSpace(project.ModInfo.ModName)
                ? "mod" : project.ModInfo.ModName);
            // Append the user's version after a dash, verbatim — they get to choose
            // whether to prefix it with "v". The dash is what tells a reader where the
            // name stops: a mod with a number in its title gives no other clue.
            if (!string.IsNullOrWhiteSpace(modVersion)) baseName += " - " + modVersion.Trim();

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
    /// ERPs — a .serps is a template, not an installable mod. Output is a
    /// flat ZIP archive of every active (enabled) ERP at the archive root,
    /// with the archive comment set to
    /// <c>{"erp_file_string":"&lt;shader_package&gt;"}</c> so the comment
    /// round-trips when the .serps is later opened as a New project
    /// template. Returns the absolute path of the resulting .serps file.
    /// </summary>
    public static string PackageAsSerps(
        SimplifiedProject project,
        string saveLocation,
        string modVersion)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(saveLocation))
            throw new ArgumentException("Save location is required.", nameof(saveLocation));
        if (string.IsNullOrWhiteSpace(project.ModInfo.ShaderPackage))
            throw new InvalidOperationException(
                "Project has no shader_package set — fill it in via Edit project before exporting as .serps.");

        var enabled = Directory.Exists(project.EnabledFolder)
            ? Directory.EnumerateFiles(project.EnabledFolder, "*.erp").ToList()
            : new List<string>();
        if (enabled.Count == 0)
            throw new InvalidOperationException(
                "No active ERPs in the project — add at least one ERP before exporting as .serps.");

        Directory.CreateDirectory(saveLocation);

        var baseName = SanitizeName(string.IsNullOrWhiteSpace(project.ModInfo.ModName)
            ? "template" : project.ModInfo.ModName);
        if (!string.IsNullOrWhiteSpace(modVersion)) baseName += " - " + modVersion.Trim();
        var outPath = Path.Combine(saveLocation, baseName + ".serps");

        if (File.Exists(outPath)) File.Delete(outPath);

        // The ZIP comment is read back by ProjectStore.ExtractShaderPackage —
        // keep the JSON shape ("erp_file_string": "<shader>") it expects.
        var comment = JsonSerializer.Serialize(new
        {
            erp_file_string = project.ModInfo.ShaderPackage,
        });

        using (var fs = File.Create(outPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, System.Text.Encoding.UTF8))
        {
            foreach (var erpPath in enabled)
            {
                var entry = zip.CreateEntry(Path.GetFileName(erpPath), CompressionLevel.Optimal);
                using var es = entry.Open();
                using var ers = File.OpenRead(erpPath);
                ers.CopyTo(es);
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
