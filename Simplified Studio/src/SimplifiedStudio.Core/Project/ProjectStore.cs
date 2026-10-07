using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// Lifecycle helpers for <see cref="SimplifiedProject"/> — extract / rezip the
/// .simplified file, build a new project from a .serps template, convert
/// projects written by an older Studio, manage temp directories.
/// </summary>
public static class ProjectStore
{
    public const string ProjectFileExtension = ".simplified";
    public const string InfoFileName         = "info.json";
    public const string ErpsFolderName       = "ERPs";

    /// <summary>Root files a pre-1.3 project carried, used to recognise one.</summary>
    private const string LegacyInfoFileName       = "mod.json";
    private const string LegacyCategoriesFileName = "categories.json";
    private static readonly string[] LegacyFolders = { "default", "enabled", "disabled" };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Tolerate the BOMs / trailing commas mods sometimes hand-edit in.
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static string TempRoot
        => Path.Combine(Path.GetTempPath(), "simplified-studio", "open");

    // ─────────────────────── Format detection ─────────────────────────

    /// <summary>What a .simplified on disk turns out to be.</summary>
    public enum ProjectFormat
    {
        /// <summary>Written by this build or an older 1.3+ one — opens directly.</summary>
        Current,
        /// <summary>Pre-1.3: default/enabled/disabled + mod.json. Needs converting.</summary>
        Legacy,
        /// <summary>Written by a newer Studio. Opens, but something may be lost.</summary>
        Newer,
    }

    public sealed record Inspection(ProjectFormat Format, string StudioVersion);

    /// <summary>
    /// Work out what a project file is without extracting it — the caller needs
    /// this to ask before converting anything.
    /// </summary>
    public static Inspection Inspect(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Project file not found.", zipPath);

        using var zip = ZipFile.OpenRead(zipPath);

        var info = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals(InfoFileName, StringComparison.OrdinalIgnoreCase));

        if (info is null)
            return new Inspection(ProjectFormat.Legacy, "");

        var version = "";
        try
        {
            using var stream = info.Open();
            using var doc = JsonDocument.Parse(stream);
            // simplified_app first, studio_version behind it: a project written
            // by 1.3 through 2.0 has only the old key, and one written from here
            // has both.
            if (doc.RootElement.TryGetProperty("simplified_app", out var app)
                && app.ValueKind == JsonValueKind.Array
                && app.GetArrayLength() > 1
                && app[1].ValueKind == JsonValueKind.String)
            {
                version = app[1].GetString() ?? "";
            }
            else if (doc.RootElement.TryGetProperty("studio_version", out var v) &&
                     v.ValueKind == JsonValueKind.String)
            {
                version = v.GetString() ?? "";
            }
        }
        catch { /* unreadable info.json — treat as current and let Open cope */ }

        return new Inspection(
            StudioVersion.IsNewerThanCurrent(version) ? ProjectFormat.Newer : ProjectFormat.Current,
            version);
    }

    // ─────────────────────── Create / Open / Save ─────────────────────

    /// <summary>
    /// Build a fresh project from a .serps template: copy its structure into
    /// <c>ERPs/</c> (category folders and all), take the shader package from the
    /// ZIP comment, seed name/author from the wizard and everything switched
    /// off. Templates written before 1.3 are flat, so their parts land
    /// uncategorised. Not saved to disk yet — the caller passes a path to
    /// <see cref="Save"/>.
    /// </summary>
    public static SimplifiedProject CreateFromSerps(string serpsPath, string name, string author)
    {
        if (!File.Exists(serpsPath))
            throw new FileNotFoundException("Template file not found.", serpsPath);

        var temp = AllocateTempDir();
        var erps = Path.Combine(temp, ErpsFolderName);
        Directory.CreateDirectory(erps);

        string shaderPackage;
        string templateVersion = "";
        string templateModVersion = "";

        using (var zip = ZipFile.OpenRead(serpsPath))
        {
            shaderPackage = ExtractShaderPackage(zip.Comment);

            foreach (var entry in zip.Entries)
            {
                // Directory entries carry an empty Name; recreate them so an
                // empty category survives the round trip.
                if (string.IsNullOrEmpty(entry.Name))
                {
                    var dir = TemplateRelativePath(entry.FullName);
                    if (dir is not null) Directory.CreateDirectory(Path.Combine(erps, dir));
                    continue;
                }

                if (entry.Name.Equals(ProjectPackager.TemplateInfoFileName, StringComparison.OrdinalIgnoreCase) &&
                    entry.FullName.Equals(entry.Name, StringComparison.Ordinal))
                {
                    (templateVersion, templateModVersion) = ReadTemplateInfo(entry);
                    continue;
                }

                if (!entry.Name.EndsWith(".erp", StringComparison.OrdinalIgnoreCase)) continue;

                var rel = TemplateRelativePath(entry.FullName);
                if (rel is null) continue;

                var dest = Path.Combine(erps, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }

        var project = new SimplifiedProject
        {
            TempDir = temp,
            ZipPath = null,
            Info = new ProjectInfo
            {
                Name          = name,
                Author        = author,
                Version       = templateModVersion,
                StudioVersion = StudioVersion.Current,
                ShaderPackage = shaderPackage,
                SerpsTemplate = Path.GetFileName(serpsPath),
            },
        };

        WriteInfo(project);
        return project;
    }

    /// <summary>
    /// Where a template entry lands under <c>ERPs/</c>. A 1.3 template nests
    /// everything under an <c>ERPs/</c> folder; a pre-1.3 one is flat at the
    /// archive root, and lands uncategorised.
    /// </summary>
    private static string? TemplateRelativePath(string fullName)
    {
        var normalized = fullName.Replace('\\', '/').TrimStart('/');
        var prefix = ErpsFolderName + "/";

        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var rel = normalized[prefix.Length..].TrimEnd('/');
            return rel.Length == 0 ? null : rel.Replace('/', Path.DirectorySeparatorChar);
        }

        // Flat (pre-1.3): only root-level files count, and only as parts.
        return normalized.Contains('/') ? null : normalized;
    }

    /// <summary>The Studio version and mod version a template records, if any.</summary>
    private static (string studioVersion, string modVersion) ReadTemplateInfo(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            string Read(string key) =>
                doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";
            return (Read("studio_version"), Read("version"));
        }
        catch
        {
            return ("", "");
        }
    }

    /// <summary>
    /// Build a project with a typed shader package and no parts. The user adds
    /// ERPs one by one via <see cref="AddCustomErp"/>.
    /// </summary>
    public static SimplifiedProject CreateCustom(string name, string author, string shaderPackage)
    {
        var temp = AllocateTempDir();
        Directory.CreateDirectory(Path.Combine(temp, ErpsFolderName));

        var project = new SimplifiedProject
        {
            TempDir = temp,
            ZipPath = null,
            Info = new ProjectInfo
            {
                Name             = name,
                Author           = author,
                StudioVersion    = StudioVersion.Current,
                ShaderPackage    = shaderPackage,
                SerpsTemplate    = "",
                IsCustomTemplate = true,
            },
        };

        WriteInfo(project);
        return project;
    }

    /// <summary>
    /// Open a .simplified. A pre-1.3 project is converted in place — call
    /// <see cref="Inspect"/> first and ask, because the conversion rewrites
    /// every part's file name. <paramref name="log"/> receives one line per
    /// rename so a bad conversion can be traced afterwards.
    /// </summary>
    public static SimplifiedProject Open(string zipPath, Action<string>? log = null)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Project file not found.", zipPath);

        var temp = AllocateTempDir();
        ZipFile.ExtractToDirectory(zipPath, temp);

        var infoPath = Path.Combine(temp, InfoFileName);
        if (!File.Exists(infoPath))
        {
            var migrated = MigrateLegacy(temp, log);
            migrated.ZipPath = zipPath;
            return migrated;
        }

        ProjectInfo info = new();
        try { info = JsonSerializer.Deserialize<ProjectInfo>(File.ReadAllText(infoPath), Json) ?? new ProjectInfo(); }
        catch { /* keep defaults — a project with an unreadable info.json still opens */ }

        Directory.CreateDirectory(Path.Combine(temp, ErpsFolderName));

        var project = new SimplifiedProject { TempDir = temp, ZipPath = zipPath, Info = info };

        // Files may have been added or removed by hand since the last save; the
        // folder is the authority on what exists.
        PruneSelection(project);
        return project;
    }

    /// <summary>
    /// Save the project. With <paramref name="newZipPath"/> this is Save As.
    /// Rewrites <c>info.json</c> first, stamped with this build's version.
    /// </summary>
    public static void Save(SimplifiedProject project, string? newZipPath = null)
    {
        var target = newZipPath ?? project.ZipPath
            ?? throw new InvalidOperationException(
                "No path to save to — call with newZipPath or set project.ZipPath first.");

        WriteInfo(project);

        // Atomic-ish: write to .tmp, swap. Avoids losing the original on a failed write.
        var tmpZip = target + ".tmp";
        if (File.Exists(tmpZip)) File.Delete(tmpZip);
        ZipFile.CreateFromDirectory(project.TempDir, tmpZip,
            CompressionLevel.Optimal, includeBaseDirectory: false);

        if (File.Exists(target)) File.Delete(target);
        File.Move(tmpZip, target);

        project.ZipPath = target;
    }

    /// <summary>Discard a project: remove its temp directory.</summary>
    public static void Close(SimplifiedProject project)
    {
        try
        {
            if (Directory.Exists(project.TempDir))
                Directory.Delete(project.TempDir, recursive: true);
        }
        catch
        {
            // Best-effort — the next CleanupStaleTempDirs sweep will get it.
        }
    }

    /// <summary>
    /// Remove temp directories left behind by previous sessions that crashed
    /// or were killed. Call at app startup with a generous threshold.
    /// </summary>
    public static int CleanupStaleTempDirs(TimeSpan olderThan)
    {
        if (!Directory.Exists(TempRoot)) return 0;
        var cutoff = DateTime.UtcNow - olderThan;
        var removed = 0;
        foreach (var dir in Directory.GetDirectories(TempRoot))
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
            }
            catch { /* skip */ }
        }
        return removed;
    }

    // ─────────────────────── Parts (enable / disable) ─────────────────

    /// <summary>
    /// Switch a part on, switching off whatever it conflicts with. Returns the
    /// parts that were displaced — worth surfacing, since a folder-level
    /// conflict can take several at once. No files move; this is a list edit.
    /// </summary>
    public static IReadOnlyList<string> EnablePart(SimplifiedProject project, string partPath)
    {
        partPath = Normalize(partPath);
        if (!File.Exists(project.PathOf(partPath)))
            throw new FileNotFoundException($"Part '{partPath}' isn't in this project.");

        var displaced = PartConflicts.Displaced(partPath, project.Info.Enabled);
        foreach (var gone in displaced) RemoveFromSelection(project, gone);

        if (!IsEnabled(project, partPath)) project.Info.Enabled.Add(partPath);
        return displaced;
    }

    /// <summary>Switch a part off. No-op when it's already off.</summary>
    public static void DisablePart(SimplifiedProject project, string partPath)
        => RemoveFromSelection(project, Normalize(partPath));

    public static bool IsEnabled(SimplifiedProject project, string partPath) =>
        project.Info.Enabled.Any(e =>
            string.Equals(Normalize(e), Normalize(partPath), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Drop selected parts that no longer exist, and settle any conflict the
    /// selection has picked up from hand-editing. Called on open.
    /// </summary>
    public static void PruneSelection(SimplifiedProject project)
    {
        var present = new HashSet<string>(project.AllPartPaths, StringComparer.OrdinalIgnoreCase);
        var alive = project.Info.Enabled
            .Select(Normalize)
            .Where(present.Contains)
            .ToList();
        project.Info.Enabled = PartConflicts.Reconcile(alive);
    }

    // ─────────────────────── Categories (folders) ──────────────────────

    /// <summary>
    /// Make a category folder. <paramref name="folderName"/> carries its own
    /// <c>000--</c> prefix; the caller decides order and conflict group.
    /// </summary>
    public static void CreateCategory(SimplifiedProject project, string folderName)
    {
        RequireSimpleName(folderName);
        var dir = Path.Combine(project.ErpsFolder, folderName);
        if (Directory.Exists(dir))
            throw new InvalidOperationException($"A category folder named '{folderName}' already exists.");
        Directory.CreateDirectory(dir);
    }

    /// <summary>Rename a category folder, keeping every part inside it selected.</summary>
    public static void RenameCategory(SimplifiedProject project, string oldName, string newName)
    {
        RequireSimpleName(newName);
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;

        var from = Path.Combine(project.ErpsFolder, oldName);
        var to   = Path.Combine(project.ErpsFolder, newName);
        if (!Directory.Exists(from))
            throw new DirectoryNotFoundException($"No category folder named '{oldName}'.");
        if (Directory.Exists(to))
            throw new InvalidOperationException($"A category folder named '{newName}' already exists.");

        Directory.Move(from, to);
        RepointSelection(project, oldName, newName);
    }

    /// <summary>
    /// Remove a category, moving its parts back to the root rather than
    /// deleting them — losing an ERP to a folder operation would be nasty.
    /// </summary>
    public static void DeleteCategory(SimplifiedProject project, string folderName)
    {
        var dir = Path.Combine(project.ErpsFolder, folderName);
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "*.erp"))
        {
            var name = Path.GetFileName(file);
            var dest = UniquePath(project.ErpsFolder, name);
            File.Move(file, dest);
            RepointPart(project,
                Path.Combine(folderName, name),
                Path.GetFileName(dest));
        }
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Move a part into a category folder, or to the root when
    /// <paramref name="targetFolder"/> is empty. The selection follows it.
    /// </summary>
    public static string MovePart(SimplifiedProject project, string partPath, string targetFolder)
    {
        partPath = Normalize(partPath);
        var source = project.PathOf(partPath);
        if (!File.Exists(source))
            throw new FileNotFoundException($"Part '{partPath}' isn't in this project.");

        var destDir = string.IsNullOrEmpty(targetFolder)
            ? project.ErpsFolder
            : Path.Combine(project.ErpsFolder, targetFolder);
        Directory.CreateDirectory(destDir);

        var destPath = UniquePath(destDir, Path.GetFileName(partPath));
        if (string.Equals(source, destPath, StringComparison.OrdinalIgnoreCase)) return partPath;

        File.Move(source, destPath);

        var newRel = string.IsNullOrEmpty(targetFolder)
            ? Path.GetFileName(destPath)
            : Path.Combine(targetFolder, Path.GetFileName(destPath));
        RepointPart(project, partPath, newRel);
        return newRel;
    }

    /// <summary>
    /// Rename a part in place, keeping it in its folder. This is how order and
    /// conflict group are changed — both live in the file name.
    /// </summary>
    public static string RenamePart(SimplifiedProject project, string partPath, string newFileName)
    {
        partPath = Normalize(partPath);
        RequireSimpleName(newFileName);
        if (!newFileName.EndsWith(".erp", StringComparison.OrdinalIgnoreCase)) newFileName += ".erp";

        var source = project.PathOf(partPath);
        if (!File.Exists(source))
            throw new FileNotFoundException($"Part '{partPath}' isn't in this project.");

        var folder = SimplifiedProject.CategoryOf(partPath);
        var destDir = Path.GetDirectoryName(source)!;
        var destPath = Path.Combine(destDir, newFileName);
        if (string.Equals(source, destPath, StringComparison.OrdinalIgnoreCase)) return partPath;
        if (File.Exists(destPath))
            throw new InvalidOperationException($"A part named '{newFileName}' is already in that folder.");

        File.Move(source, destPath);

        var newRel = string.IsNullOrEmpty(folder) ? newFileName : Path.Combine(folder, newFileName);
        RepointPart(project, partPath, newRel);
        return newRel;
    }

    // ─────────────────────── Custom-project parts ──────────────────────

    /// <summary>
    /// Copy an .erp from disk into the project and switch it on. Custom
    /// projects only — template projects take their parts from the template.
    /// </summary>
    public static string AddCustomErp(SimplifiedProject project, string sourcePath, string targetFolder = "")
    {
        if (!project.Info.IsCustomTemplate)
            throw new InvalidOperationException("AddCustomErp can only be used on custom-template projects.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source ERP not found.", sourcePath);
        if (!sourcePath.EndsWith(".erp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only .erp files can be added.");

        var destDir = string.IsNullOrEmpty(targetFolder)
            ? project.ErpsFolder
            : Path.Combine(project.ErpsFolder, targetFolder);
        Directory.CreateDirectory(destDir);

        var name = Path.GetFileName(sourcePath);
        if (File.Exists(Path.Combine(destDir, name)))
            throw new InvalidOperationException($"An ERP named '{name}' is already in that folder.");

        File.Copy(sourcePath, Path.Combine(destDir, name));

        var rel = string.IsNullOrEmpty(targetFolder) ? name : Path.Combine(targetFolder, name);
        EnablePart(project, rel);
        return rel;
    }

    /// <summary>Remove a part from the project entirely.</summary>
    public static void RemoveErp(SimplifiedProject project, string partPath)
    {
        partPath = Normalize(partPath);
        var path = project.PathOf(partPath);
        if (File.Exists(path)) File.Delete(path);
        RemoveFromSelection(project, partPath);
    }

    /// <summary>
    /// Turn a template project into a custom one. Purely a flag flip — the
    /// layout is identical either way, so no part or selection is touched.
    /// </summary>
    public static void ConvertToCustom(SimplifiedProject project)
    {
        if (project.Info.IsCustomTemplate) return;
        project.Info.IsCustomTemplate = true;
        project.Info.SerpsTemplate = "";
        WriteInfo(project);
    }

    // ─────────────────────── Migration from pre-1.3 ────────────────────

    /// <summary>
    /// Convert an extracted pre-1.3 project in place.
    ///
    /// The old layout kept a pristine <c>default/</c> copy alongside the
    /// working <c>enabled/</c> or <c>disabled/</c> one; since nothing could ever
    /// reach the pristine copy, the edited one wins and the duplicate goes.
    /// Categories become folders, and every part is re-prefixed, because the
    /// digits changed meaning: the conflict group moved from the first digit to
    /// the third, and order became the first two.
    ///
    /// Every rename is logged. Once the files are renamed there's nothing left
    /// to check the result against, so the log is the only way to work out what
    /// a bad conversion did.
    /// </summary>
    private static SimplifiedProject MigrateLegacy(string temp, Action<string>? log)
    {
        log?.Invoke("Converting project from a pre-1.3 layout.");

        var legacyInfo = ReadLegacyInfo(Path.Combine(temp, LegacyInfoFileName));
        var categories = ReadLegacyCategories(Path.Combine(temp, LegacyCategoriesFileName));

        // Where each part's usable bytes are, and whether it was switched on.
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wasEnabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in LegacyFolders)
        {
            var dir = Path.Combine(temp, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.erp"))
            {
                var name = Path.GetFileName(file);
                // enabled/ and disabled/ hold the edits; default/ only fills gaps.
                if (folder != "default" || !sources.ContainsKey(name)) sources[name] = file;
                if (folder == "enabled") wasEnabled.Add(name);
            }
        }

        var erps = Path.Combine(temp, ErpsFolderName);
        Directory.CreateDirectory(erps);

        // Category order becomes folder order; part order within a category
        // becomes part order. Anything uncategorised lands at the root.
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabled = new List<string>();
        var folderOrder = 1;

        foreach (var (category, parts) in categories)
        {
            var isRoot = category.Length == 0;
            string destDir = erps, folderName = "";
            if (!isRoot)
            {
                folderName = PartPrefix.Apply(category, folderOrder++, 0);
                destDir = Path.Combine(erps, folderName);
                Directory.CreateDirectory(destDir);
                log?.Invoke($"  category '{category}' → {ErpsFolderName}/{folderName}");
            }

            var partOrder = 1;
            foreach (var part in parts)
            {
                if (!sources.TryGetValue(part, out var source)) continue;
                MovePartIn(source, part, destDir, folderName, partOrder++);
                placed.Add(part);
            }
        }

        // Whatever categories.json didn't mention keeps its own order at the root.
        var leftovers = sources.Keys.Where(k => !placed.Contains(k))
                                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                                    .ToList();
        var rootOrder = 1;
        foreach (var part in leftovers)
            MovePartIn(sources[part], part, erps, "", rootOrder++);

        void MovePartIn(string source, string oldName, string destDir, string folderName, int order)
        {
            // The old first digit was the conflict group; carry it to the third.
            var oldConflict = LegacyConflictDigit(oldName);
            var newName = PartPrefix.Apply(StripLegacyPrefix(oldName), order, oldConflict);
            var dest = UniquePath(destDir, newName);
            File.Move(source, dest);

            var rel = string.IsNullOrEmpty(folderName)
                ? Path.GetFileName(dest)
                : Path.Combine(folderName, Path.GetFileName(dest));
            if (wasEnabled.Contains(oldName)) enabled.Add(rel);

            log?.Invoke($"  {oldName} → {rel}" +
                        (oldConflict > 0 ? $"   (conflict group {oldConflict} kept)" : ""));
        }

        foreach (var folder in LegacyFolders)
        {
            var dir = Path.Combine(temp, folder);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
        foreach (var file in new[] { LegacyInfoFileName, LegacyCategoriesFileName })
        {
            try { File.Delete(Path.Combine(temp, file)); } catch { }
        }

        var project = new SimplifiedProject
        {
            TempDir = temp,
            Info = new ProjectInfo
            {
                Name             = legacyInfo.name,
                Author           = legacyInfo.author,
                StudioVersion    = StudioVersion.Current,
                ShaderPackage    = legacyInfo.shaderPackage,
                SerpsTemplate    = legacyInfo.serpsTemplate,
                IsCustomTemplate = legacyInfo.isCustom,
                Enabled          = enabled,
            },
        };

        // A pre-1.3 selection could hold combinations the new folder-level rule
        // forbids, so settle it rather than carrying an illegal one forward.
        var before = project.Info.Enabled.Count;
        PruneSelection(project);
        if (project.Info.Enabled.Count != before)
            log?.Invoke($"  {before - project.Info.Enabled.Count} part(s) switched off — they now conflict.");

        WriteInfo(project);
        log?.Invoke($"Converted: {project.AllPartPaths.Count()} part(s), {project.Info.Enabled.Count} enabled.");
        return project;
    }

    private static (string name, string author, string shaderPackage, string serpsTemplate, bool isCustom)
        ReadLegacyInfo(string path)
    {
        if (!File.Exists(path)) return ("", "", "", "", false);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string S(string key) =>
                doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";
            var custom = doc.RootElement.TryGetProperty("custom_template", out var c) &&
                         c.ValueKind == JsonValueKind.True;
            return (S("mod_name"), S("mod_author"), S("shader_package"), S("serps_template"), custom);
        }
        catch
        {
            return ("", "", "", "", false);
        }
    }

    /// <summary>
    /// The old <c>categories.json</c> as an ordered list of (category, parts).
    /// The uncategorised group comes back as an empty category name. Both
    /// shapes it was ever written in are read: category → parts, and the older
    /// part → category.
    /// </summary>
    private static List<(string category, List<string> parts)> ReadLegacyCategories(string path)
    {
        var result = new List<(string, List<string>)>();
        if (!File.Exists(path)) return result;

        List<string> PartsFor(string category)
        {
            var key = category.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase) ? "" : category;
            var found = result.FindIndex(r => string.Equals(r.Item1, key, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) return result[found].Item2;
            var list = new List<string>();
            result.Add((key, list));
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("categories", out var node) ||
                node.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var pair in node.EnumerateObject())
            {
                if (pair.Value.ValueKind == JsonValueKind.Array)
                {
                    var parts = PartsFor(pair.Name.Trim());
                    foreach (var erp in pair.Value.EnumerateArray())
                    {
                        var name = erp.GetString();
                        if (!string.IsNullOrWhiteSpace(name)) parts.Add(name.Trim());
                    }
                }
                else if (pair.Value.ValueKind == JsonValueKind.String)
                {
                    var category = pair.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(category))
                        PartsFor(category.Trim()).Add(pair.Name);
                }
            }
        }
        catch { /* unreadable — everything lands uncategorised */ }

        return result;
    }

    /// <summary>The pre-1.3 conflict group: the first digit of a <c>NNN--</c> prefix.</summary>
    private static int LegacyConflictDigit(string fileName)
    {
        if (fileName.Length < 5 || !fileName[..3].All(char.IsDigit) ||
            fileName[3] != '-' || fileName[4] != '-')
            return 0;
        return fileName[0] - '0';
    }

    private static string StripLegacyPrefix(string fileName)
    {
        if (fileName.Length >= 5 && fileName[..3].All(char.IsDigit) &&
            fileName[3] == '-' && fileName[4] == '-')
            return fileName[5..];
        return fileName;
    }

    // ───────────────────────────── internals ───────────────────────────

    private static string AllocateTempDir()
    {
        Directory.CreateDirectory(TempRoot);
        var dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteInfo(SimplifiedProject project)
    {
        project.Info.StudioVersion = StudioVersion.Current;
        project.Info.App = [StudioVersion.AppId, StudioVersion.Stamp];
        var path = Path.Combine(project.TempDir, InfoFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(project.Info, Json));
    }

    private static string Normalize(string partPath) =>
        (partPath ?? "").Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);

    private static void RemoveFromSelection(SimplifiedProject project, string partPath) =>
        project.Info.Enabled.RemoveAll(e =>
            string.Equals(Normalize(e), Normalize(partPath), StringComparison.OrdinalIgnoreCase));

    /// <summary>Follow a part that moved or was renamed, keeping it selected if it was.</summary>
    private static void RepointPart(SimplifiedProject project, string oldPath, string newPath)
    {
        for (var i = 0; i < project.Info.Enabled.Count; i++)
        {
            if (string.Equals(Normalize(project.Info.Enabled[i]), Normalize(oldPath),
                              StringComparison.OrdinalIgnoreCase))
                project.Info.Enabled[i] = Normalize(newPath);
        }
    }

    /// <summary>Follow every part in a renamed category folder.</summary>
    private static void RepointSelection(SimplifiedProject project, string oldFolder, string newFolder)
    {
        for (var i = 0; i < project.Info.Enabled.Count; i++)
        {
            var entry = Normalize(project.Info.Enabled[i]);
            var folder = SimplifiedProject.CategoryOf(entry);
            if (string.Equals(folder, oldFolder, StringComparison.OrdinalIgnoreCase))
                project.Info.Enabled[i] = Path.Combine(newFolder, Path.GetFileName(entry));
        }
    }

    /// <summary>A free path in <paramref name="dir"/>, suffixing the name if taken.</summary>
    private static string UniquePath(string dir, string fileName)
    {
        var candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);
        for (var n = 2; ; n++)
        {
            candidate = Path.Combine(dir, $"{stem}_{n}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void RequireSimpleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Name can't be empty.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException($"Name contains invalid characters: '{name}'.");
    }

    /// <summary>
    /// The .serps ZIP comment is JSON like <c>{"erp_file_string":"foo.nefx2.sm51"}</c>.
    /// Pull <c>erp_file_string</c> as the shader-package identifier. Returns
    /// empty when the comment is missing, malformed, or doesn't have the key.
    /// </summary>
    private static string ExtractShaderPackage(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return "";
        try
        {
            using var doc = JsonDocument.Parse(comment);
            if (doc.RootElement.TryGetProperty("erp_file_string", out var v) &&
                v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch { /* not JSON or no key — fall through */ }
        return "";
    }
}
