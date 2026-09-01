using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// Lifecycle helpers for <see cref="SimplifiedProject"/> — extract / rezip
/// the .simplified file, build a new project from a .serps template, manage
/// temp directories, sweep stale ones from previous sessions.
/// </summary>
public static class ProjectStore
{
    public const string ProjectFileExtension = ".simplified";
    public const string ModInfoFileName      = "mod.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Tolerate the BOMs / trailing commas mods sometimes hand-edit in.
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static string TempRoot
        => Path.Combine(Path.GetTempPath(), "simplified-studio", "open");

    // ─────────────────────── Create / Open / Save ─────────────────────

    /// <summary>
    /// Build a fresh project from a .serps template. Extracts every .erp in
    /// the template into <c>default/</c>, seeds an empty <c>enabled/</c> and
    /// <c>disabled/</c>, captures the shader package from the .serps ZIP
    /// comment, and writes <c>mod.json</c>. The project isn't saved to disk
    /// yet — caller passes the chosen save path to <see cref="Save"/>.
    /// </summary>
    public static SimplifiedProject CreateFromSerps(string serpsPath, string modName, string modAuthor)
    {
        if (!File.Exists(serpsPath))
            throw new FileNotFoundException("Template file not found.", serpsPath);

        var temp = AllocateTempDir();
        Directory.CreateDirectory(Path.Combine(temp, "default"));
        Directory.CreateDirectory(Path.Combine(temp, "enabled"));
        Directory.CreateDirectory(Path.Combine(temp, "disabled"));

        string shaderPackage = "";
        using (var zip = ZipFile.OpenRead(serpsPath))
        {
            shaderPackage = ExtractShaderPackage(zip.Comment);
            foreach (var entry in zip.Entries)
            {
                // Flat layout — ignore anything that's a directory entry or
                // not an .erp at the archive root.
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (!entry.FullName.Equals(entry.Name, StringComparison.Ordinal)) continue;
                if (!entry.Name.EndsWith(".erp", StringComparison.OrdinalIgnoreCase)) continue;

                var dest = Path.Combine(temp, "default", entry.Name);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }

        var info = new ModInfo
        {
            ModName       = modName,
            Author        = modAuthor,
            ShaderPackage = shaderPackage,
            SerpsTemplate = Path.GetFileName(serpsPath),
        };

        var project = new SimplifiedProject
        {
            TempDir = temp,
            ZipPath = null,
            ModInfo = info,
        };

        WriteModJson(project);
        return project;
    }

    /// <summary>
    /// Build a fresh project with a user-supplied shader package and an empty
    /// ERP set. No .serps template is involved — the user adds ERPs one by one
    /// via <see cref="AddCustomErp"/>. ERPs go straight into <c>enabled/</c>
    /// (there is no template-default pool to enable from), and the
    /// <c>default/</c> folder is left empty.
    /// </summary>
    public static SimplifiedProject CreateCustom(string modName, string modAuthor, string shaderPackage)
    {
        var temp = AllocateTempDir();
        Directory.CreateDirectory(Path.Combine(temp, "default"));
        Directory.CreateDirectory(Path.Combine(temp, "enabled"));
        Directory.CreateDirectory(Path.Combine(temp, "disabled"));

        var info = new ModInfo
        {
            ModName          = modName,
            Author           = modAuthor,
            ShaderPackage    = shaderPackage,
            SerpsTemplate    = "",
            IsCustomTemplate = true,
        };

        var project = new SimplifiedProject
        {
            TempDir = temp,
            ZipPath = null,
            ModInfo = info,
        };

        WriteModJson(project);
        return project;
    }

    /// <summary>Open a .simplified file. Extracts it to a fresh temp dir.</summary>
    public static SimplifiedProject Open(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Project file not found.", zipPath);

        var temp = AllocateTempDir();
        ZipFile.ExtractToDirectory(zipPath, temp);

        // Tolerate older layouts where the three subfolders may not exist yet.
        Directory.CreateDirectory(Path.Combine(temp, "default"));
        Directory.CreateDirectory(Path.Combine(temp, "enabled"));
        Directory.CreateDirectory(Path.Combine(temp, "disabled"));

        ModInfo info = new();
        var modJson = Path.Combine(temp, ModInfoFileName);
        if (File.Exists(modJson))
        {
            try { info = JsonSerializer.Deserialize<ModInfo>(File.ReadAllText(modJson)) ?? new ModInfo(); }
            catch { /* keep default ModInfo */ }
        }

        return new SimplifiedProject { TempDir = temp, ZipPath = zipPath, ModInfo = info };
    }

    /// <summary>
    /// Save the project. If <paramref name="newZipPath"/> is given it acts as
    /// Save As and updates <see cref="SimplifiedProject.ZipPath"/>. Rewrites
    /// <c>mod.json</c> from <see cref="SimplifiedProject.ModInfo"/> first.
    /// </summary>
    public static void Save(SimplifiedProject project, string? newZipPath = null)
    {
        var target = newZipPath ?? project.ZipPath
            ?? throw new InvalidOperationException(
                "No path to save to — call with newZipPath or set project.ZipPath first.");

        WriteModJson(project);

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
    /// Make a part active: if a previously-edited copy exists in
    /// <c>disabled/</c>, move it back to <c>enabled/</c>; otherwise copy a
    /// fresh one from <c>default/</c>. No-op when already enabled.
    /// </summary>
    public static void EnableErp(SimplifiedProject project, string erpName)
    {
        var enabled  = Path.Combine(project.EnabledFolder,  erpName);
        var disabled = Path.Combine(project.DisabledFolder, erpName);
        var def      = Path.Combine(project.DefaultFolder,  erpName);

        if (File.Exists(enabled)) return;
        Directory.CreateDirectory(project.EnabledFolder);

        if (File.Exists(disabled))
        {
            File.Move(disabled, enabled);
            return;
        }
        if (File.Exists(def))
        {
            File.Copy(def, enabled);
            return;
        }
        throw new FileNotFoundException(
            $"ERP '{erpName}' isn't in default/ or disabled/ — can't enable it.");
    }

    /// <summary>
    /// Move a part from <c>enabled/</c> to <c>disabled/</c>, preserving any
    /// in-memory edits already flushed to its file. No-op when not enabled.
    /// </summary>
    public static void DisableErp(SimplifiedProject project, string erpName)
    {
        var enabled  = Path.Combine(project.EnabledFolder,  erpName);
        var disabled = Path.Combine(project.DisabledFolder, erpName);
        if (!File.Exists(enabled)) return;
        Directory.CreateDirectory(project.DisabledFolder);
        if (File.Exists(disabled)) File.Delete(disabled);
        File.Move(enabled, disabled);
    }

    // ─────────────────────── Custom ERPs (no template) ─────────────────

    /// <summary>
    /// Copy an .erp from anywhere on disk into a custom-template project's
    /// <c>enabled/</c> folder. Throws if the project isn't a custom-template
    /// one, the source isn't an .erp, or an ERP with the same name already
    /// exists in the project.
    /// </summary>
    public static void AddCustomErp(SimplifiedProject project, string sourcePath)
    {
        if (!project.ModInfo.IsCustomTemplate)
            throw new InvalidOperationException(
                "AddCustomErp can only be used on custom-template projects.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source ERP not found.", sourcePath);
        if (!sourcePath.EndsWith(".erp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only .erp files can be added.");

        var name = Path.GetFileName(sourcePath);
        var dest = Path.Combine(project.EnabledFolder, name);
        Directory.CreateDirectory(project.EnabledFolder);

        if (File.Exists(dest))
            throw new InvalidOperationException(
                $"An ERP named '{name}' is already in this project.");

        File.Copy(sourcePath, dest);
    }

    /// <summary>
    /// Rename an ERP in a custom-template project. The file is renamed in
    /// whichever of <c>enabled/</c> / <c>disabled/</c> / <c>default/</c> it
    /// lives in. Throws on no-such-source, name collisions, or invalid names.
    /// </summary>
    public static void RenameCustomErp(SimplifiedProject project, string oldName, string newName)
    {
        if (!project.ModInfo.IsCustomTemplate)
            throw new InvalidOperationException(
                "RenameCustomErp can only be used on custom-template projects.");

        newName = (newName ?? "").Trim();
        if (string.IsNullOrEmpty(newName))
            throw new InvalidOperationException("New name can't be empty.");
        if (!newName.EndsWith(".erp", StringComparison.OrdinalIgnoreCase))
            newName += ".erp";
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException($"Name contains invalid characters: '{newName}'.");
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            return;

        var folders = new[] { project.EnabledFolder, project.DisabledFolder, project.DefaultFolder };

        // Collision check across every folder first — bail before touching anything
        // if the destination name is already taken.
        foreach (var folder in folders)
        {
            var dest = Path.Combine(folder, newName);
            if (File.Exists(dest))
                throw new InvalidOperationException(
                    $"An ERP named '{newName}' already exists in this project.");
        }

        var renamed = 0;
        foreach (var folder in folders)
        {
            var src = Path.Combine(folder, oldName);
            if (!File.Exists(src)) continue;
            var dest = Path.Combine(folder, newName);
            File.Move(src, dest);
            renamed++;
        }
        if (renamed == 0)
            throw new FileNotFoundException(
                $"ERP '{oldName}' isn't in this project — nothing to rename.");
    }

    /// <summary>
    /// Remove an ERP from a custom-template project. Deletes the file from
    /// whichever of <c>enabled/</c> / <c>disabled/</c> / <c>default/</c> it
    /// happens to live in. No-op when the name isn't present in any of them.
    /// </summary>
    public static void RemoveCustomErp(SimplifiedProject project, string erpName)
    {
        if (!project.ModInfo.IsCustomTemplate)
            throw new InvalidOperationException(
                "RemoveCustomErp can only be used on custom-template projects.");

        foreach (var folder in new[] { project.EnabledFolder, project.DisabledFolder, project.DefaultFolder })
        {
            var path = Path.Combine(folder, erpName);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ───────────────────────────── internals ───────────────────────────

    private static string AllocateTempDir()
    {
        Directory.CreateDirectory(TempRoot);
        var dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteModJson(SimplifiedProject project)
    {
        var path = Path.Combine(project.TempDir, ModInfoFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(project.ModInfo, Json));
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
