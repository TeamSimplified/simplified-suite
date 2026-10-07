using System.IO.Compression;
using System.Text.Json;

namespace SimplifiedSponsorEditor.Core.Project;

/// <summary>
/// The lifecycle of a <see cref="SponsorProject"/> — create, extract and rezip
/// the <c>.simplified</c>, manage temp directories.
///
/// The same shape as Studio's and the Grid Editor's, and deliberately so: a
/// project is a zip, opening extracts it whole and saving rezips it whole,
/// which means a folder this app has never heard of survives a round trip
/// untouched.
/// </summary>
public static class ProjectStore
{
    public const string ProjectFileExtension = ".simplified";
    public const string InfoFileName         = "info.json";

    /// <summary>The files somebody imported, kept as they arrived.</summary>
    public const string SourcesFolderName = "Sources";

    /// <summary>1.0's rendered pictures. Read for those projects; never written.</summary>
    public const string SponsorsFolderName = "Sponsors";

    /// <summary>Inside it, the menu versions of the same pictures.</summary>
    public const string MenuFolderName = "menu";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Tolerate the BOMs / trailing commas mods sometimes hand-edit in.
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static string TempRoot
        => Path.Combine(Path.GetTempPath(), "simplified-sponsor-editor", "open");

    /// <summary>
    /// A new, empty project.
    ///
    /// Nothing is seeded into it. The Grid Editor copies 90 MB of base files
    /// into every new project because its edits are changes to those files;
    /// here the base archives are read-only reference that every project shares,
    /// and a project holds only what the user put in it — which at this point is
    /// nothing.
    /// </summary>
    public static SponsorProject Create(string name, string author)
    {
        var project = new SponsorProject
        {
            TempDir = AllocateTempDir(),
            ZipPath = null,
            Info = new ProjectInfo
            {
                Name   = name,
                Author = author,
            },
        };

        Directory.CreateDirectory(project.SourcesFolder);

        WriteInfo(project);
        return project;
    }

    /// <summary>Open a <c>.simplified</c>, extracting it whole into a temp directory.</summary>
    public static SponsorProject Open(string zipPath)
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

        var project = new SponsorProject { TempDir = temp, ZipPath = zipPath, Info = info };

        // A project made by hand, or by an older build, may be missing this.
        // Empty is a state the app can show; absent is one it would trip over on
        // every path that enumerates it. A 1.0 project's Sponsors folder is left
        // exactly as it is — it is read, and creating one where there was none
        // would only put an empty folder into the next save.
        Directory.CreateDirectory(project.SourcesFolder);

        return project;
    }

    /// <summary>
    /// Save the project. With <paramref name="newZipPath"/> this is Save As.
    /// Rewrites <c>info.json</c> first, stamped with this build's version.
    /// </summary>
    public static void Save(SponsorProject project, string? newZipPath = null)
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
    public static void Close(SponsorProject project)
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
    /// killed, with a generous threshold in case two copies are open at once.
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

    private static void WriteInfo(SponsorProject project)
    {
        project.Info.App = [EditorVersion.AppId, EditorVersion.Stamp];

        // A 1.x project opened here goes back out with the new key alone. Both
        // would be a file saying two things about itself, and the next reader
        // would have to decide which to believe.
        project.Info.LegacyEditorVersion = null;

        File.WriteAllText(Path.Combine(project.TempDir, InfoFileName),
            JsonSerializer.Serialize(project.Info, Json));
    }
}
