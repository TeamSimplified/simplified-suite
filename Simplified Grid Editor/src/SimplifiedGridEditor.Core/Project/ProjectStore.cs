using System.IO.Compression;
using System.Text.Json;

namespace SimplifiedGridEditor.Core.Project;

/// <summary>
/// The lifecycle of a <see cref="GridProject"/> — build one from the bundled
/// base files, extract and rezip the <c>.simplified</c>, manage temp
/// directories.
///
/// The same shape as Studio's, and deliberately so: a project is a zip, opening
/// extracts it whole and saving rezips it whole, which means a folder this app
/// has never heard of survives a round trip untouched.
/// </summary>
public static class ProjectStore
{
    public const string ProjectFileExtension = ".simplified";
    public const string InfoFileName         = "info.json";

    /// <summary>The UI archives team logos are merged into.</summary>
    public const string ErpsFolderName = "ERPs";

    /// <summary>The eleven language files, and the 2026 key list beside them.</summary>
    public const string LanguageFolderName = "Language";

    /// <summary>Holds the one database all three tabs edit.</summary>
    public const string DatabaseFolderName = "Database";

    /// <summary>
    /// The database's file name, which is the game's own. A package puts it back
    /// at <c>game_package/gamedatabases/</c> under this name, so renaming it
    /// here would produce a package the game ignores.
    /// </summary>
    public const string DatabaseFileName = "gamedatabases.2025.bin";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Tolerate the BOMs / trailing commas mods sometimes hand-edit in.
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static string TempRoot
        => Path.Combine(Path.GetTempPath(), "simplified-grid-editor", "open");

    /// <summary>
    /// A new project, seeded from the bundled base files.
    ///
    /// There is no template to choose. Studio starts from a <c>.serps</c>
    /// because a Studio project is whichever car parts that template covers; a
    /// grid project is always the same three things, so the only question is
    /// what to call it.
    /// </summary>
    /// <param name="originalsDir">
    /// The bundled pristine files — <see cref="AppPaths.OriginalsDir"/>.
    /// </param>
    public static GridProject Create(string originalsDir, string name, string author,
                                     string? gameDir = null, Action<string>? note = null)
    {
        if (!Directory.Exists(originalsDir))
            throw new DirectoryNotFoundException(
                "The base game files are missing from this build, so a new project can't be started. " +
                $"Expected them at {originalsDir}.");

        var temp = AllocateTempDir();
        var project = new GridProject
        {
            TempDir = temp,
            ZipPath = null,
            Info = new ProjectInfo
            {
                Name          = name,
                Author        = author,
                EditorVersion = EditorVersion.Current,
            },
        };

        Directory.CreateDirectory(project.DatabaseFolder);
        Directory.CreateDirectory(project.LanguageFolder);
        Directory.CreateDirectory(project.ErpsFolder);

        // The database, under the name the game knows it by. The installed
        // game's own when it is one we recognise, and the bundled copy
        // otherwise — a project seeded from the current game stays current,
        // and one seeded from a database somebody has already modded would
        // carry that mod for the rest of its life.
        var chosen = Simplified.Shared.GameDatabaseSource.Resolve(
            gameDir, Path.Combine(originalsDir, DatabaseFileName));
        if (chosen.Note is not null) note?.Invoke(chosen.Note);
        if (File.Exists(chosen.Path)) File.Copy(chosen.Path, project.DatabasePath);

        // The eleven language files, plus the two 2026 files that sit beside
        // them in the bundle rather than in localisation/.
        var localisation = Path.Combine(originalsDir, "localisation");
        if (Directory.Exists(localisation))
            foreach (var file in Directory.EnumerateFiles(localisation, "*.lng"))
                File.Copy(file, Path.Combine(project.LanguageFolder, Path.GetFileName(file)), overwrite: true);

        foreach (var extra in new[] { "2026_team_keys.lng", "2026_team_names.json" })
        {
            var from = Path.Combine(originalsDir, extra);
            if (File.Exists(from)) File.Copy(from, Path.Combine(project.LanguageFolder, extra), overwrite: true);
        }

        // The UI archives.
        var ui = Path.Combine(originalsDir, "ui");
        if (Directory.Exists(ui))
            foreach (var file in Directory.EnumerateFiles(ui, "*.erp"))
                File.Copy(file, Path.Combine(project.ErpsFolder, Path.GetFileName(file)), overwrite: true);

        WriteInfo(project);
        return project;
    }

    /// <summary>Open a <c>.simplified</c>, extracting it whole into a temp directory.</summary>
    public static GridProject Open(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Project file not found.", zipPath);

        var temp = AllocateTempDir();
        ZipFile.ExtractToDirectory(zipPath, temp);

        ProjectInfo info = new();
        var infoPath = Path.Combine(temp, InfoFileName);
        if (File.Exists(infoPath))
        {
            try { info = JsonSerializer.Deserialize<ProjectInfo>(File.ReadAllText(infoPath), Json) ?? new ProjectInfo(); }
            catch { /* keep defaults — a project with an unreadable info.json still opens */ }
        }

        var project = new GridProject { TempDir = temp, ZipPath = zipPath, Info = info };

        // A project made by hand, or by an older build, may be missing one of
        // these. Empty is a state the app can show; absent is one it would trip
        // over on every path that enumerates them.
        Directory.CreateDirectory(project.DatabaseFolder);
        Directory.CreateDirectory(project.LanguageFolder);
        Directory.CreateDirectory(project.ErpsFolder);

        return project;
    }

    /// <summary>
    /// Save the project. With <paramref name="newZipPath"/> this is Save As.
    /// Rewrites <c>info.json</c> first, stamped with this build's version.
    /// </summary>
    public static void Save(GridProject project, string? newZipPath = null)
    {
        var target = newZipPath ?? project.ZipPath
            ?? throw new InvalidOperationException(
                "No path to save to — call with newZipPath or set project.ZipPath first.");

        WriteInfo(project);

        // Write to .tmp and swap, so a failed write can't leave the user with
        // neither the old project nor a complete new one.
        var temporary = target + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        ZipFile.CreateFromDirectory(project.TempDir, temporary,
            CompressionLevel.Optimal, includeBaseDirectory: false);

        if (File.Exists(target)) File.Delete(target);
        File.Move(temporary, target);

        project.ZipPath = target;
    }

    /// <summary>Discard a project: remove its temp directory.</summary>
    public static void Close(GridProject project)
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
    /// Remove temp directories left behind by sessions that crashed or were
    /// killed. Called at startup with a generous threshold: a project's working
    /// copy is around 90 MB, so leaving them to accumulate is not an option.
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
                if (new DirectoryInfo(dir).LastWriteTimeUtc >= cutoff) continue;
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch { /* skip */ }
        }
        return removed;
    }

    private static string AllocateTempDir()
    {
        var dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteInfo(GridProject project)
    {
        project.Info.EditorVersion = EditorVersion.Current;
        File.WriteAllText(Path.Combine(project.TempDir, InfoFileName),
            JsonSerializer.Serialize(project.Info, Json));
    }
}
