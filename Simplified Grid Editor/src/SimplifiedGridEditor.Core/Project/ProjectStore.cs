using System.IO.Compression;
using System.Text.Json;
using EgoEngineLibrary.Data.GameDatabases;
using SimplifiedTeamEditor.Core.Lng;

namespace SimplifiedGridEditor.Core.Project;

/// <summary>
/// The lifecycle of a <see cref="GridProject"/>: start one, unpack and rezip the
/// <c>.simplified</c>, build the game files it is edited against, and read back
/// what it changed.
///
/// The same shape as Studio's, and deliberately so: a project is a zip, opening
/// extracts it whole and saving rezips it whole, which means a folder this app
/// has never heard of survives a round trip untouched.
///
/// What is in that zip is the difference — see <see cref="ProjectEdits"/>. The
/// database and the language files are built beside it from the installed game
/// every time it is opened, so a project follows the game rather than pinning
/// the copy it was started with.
/// </summary>
public static class ProjectStore
{
    public const string ProjectFileExtension = ".simplified";
    public const string InfoFileName         = "info.json";

    /// <summary>The UI archives team logos are merged into.</summary>
    public const string ErpsFolderName = "ERPs";

    /// <summary>
    /// The eleven language files, and the 2026 key list beside them. In the
    /// working directory now; it was a folder in the project before projects
    /// started storing their changes instead, which is why converting an older
    /// one still looks for it there.
    /// </summary>
    public const string LanguageFolderName = "Language";

    /// <summary>
    /// Holds the one database all three tabs edit. In the working directory,
    /// for the same reason as the language files above.
    /// </summary>
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
    /// A new project: nothing changed yet, and the game's files built beside it
    /// to start changing.
    ///
    /// There is no template to choose. Studio starts from a <c>.serps</c>
    /// because a Studio project is whichever car parts that template covers; a
    /// grid project is always the same three things, so the only question is
    /// what to call it.
    /// </summary>
    /// <param name="originalsDir">
    /// The pristine base files — <see cref="AppPaths.OriginalsDir"/>. Shipped,
    /// apart from the language files, which the app puts there out of the game.
    /// </param>
    public static GridProject Create(string originalsDir, string name, string author,
                                     string? gameDir, Action<string>? note = null,
                                     IProgress<string>? progress = null)
    {
        if (!Directory.Exists(originalsDir))
            throw new DirectoryNotFoundException(
                "The base game files are missing from this build, so a new project can't be started. " +
                $"Expected them at {originalsDir}.");

        var project = new GridProject
        {
            TempDir = AllocateTempDir(),
            WorkDir = AllocateTempDir(),
            ZipPath = null,
            Info = new ProjectInfo
            {
                Name          = name,
                Author        = author,
                EditorVersion = EditorVersion.Current,
            },
        };

        Directory.CreateDirectory(project.ErpsFolder);

        // The UI archives, which the project does carry: a logo edit is pixels
        // merged into an archive rather than records with keys, so there is no
        // difference to store instead.
        progress?.Report("Copying the game’s menu art…");
        var ui = Path.Combine(originalsDir, "ui");
        if (Directory.Exists(ui))
            foreach (var file in Directory.EnumerateFiles(ui, "*.erp"))
                File.Copy(file, Path.Combine(project.ErpsFolder, Path.GetFileName(file)), overwrite: true);

        // A new project has changed nothing yet, so its files are the game’s.
        new ProjectEdits().Save(project.EditsPath);
        Rebuild(project, originalsDir, gameDir, note, progress);

        WriteInfo(project);
        return project;
    }

    /// <summary>
    /// Build the game files for this session: the installed game’s database and
    /// language files, with the project’s changes applied on top.
    ///
    /// This is what keeps a project current. The files themselves are never
    /// stored - a project keeps only what it changed - so every open takes them
    /// from the game as it is today, and a patch that touched a record nobody
    /// edited simply arrives.
    /// </summary>
    public static void Rebuild(GridProject project, string originalsDir, string? gameDir,
                               Action<string>? note = null, IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(project.DatabaseFolder);
        Directory.CreateDirectory(project.LanguageFolder);

        var edits = ProjectEdits.Load(project.EditsPath);

        // The installed game’s database when it is one we recognise, and the
        // bundled copy otherwise - so a project follows the game, and one
        // somebody has already modded is not mistaken for the game’s.
        progress?.Report("Reading the game’s teams and drivers…");
        var chosen = Simplified.Shared.GameDatabaseSource.Resolve(
            gameDir, Path.Combine(originalsDir, DatabaseFileName));
        if (chosen.Note is not null) note?.Invoke(chosen.Note);

        if (!File.Exists(chosen.Path))
            throw new FileNotFoundException(
                "The game’s database isn’t where it should be, and a project is built from it.",
                chosen.Path);

        var database = GameDatabase.Load(chosen.Path);

        if (edits.Database.Count > 0)
        {
            progress?.Report("Putting your changes back…");
            var applied = edits.ApplyTo(database);
            if (applied < edits.Database.Count)
                note?.Invoke($"{edits.Database.Count - applied} of this project’s database changes are " +
                             "for records the installed game no longer has, and were left out.");
        }

        database.SaveAtomic(project.DatabasePath);

        // The eleven language files, from the originals folder - where they are
        // not shipped but taken out of the game’s own exe by the app, when it
        // opens and finds them missing or from a build the game has patched past.
        progress?.Report("Reading the game’s text…");
        var languageDir = Path.Combine(originalsDir, Simplified.Language.LanguageFiles.FolderName);
        var copied = 0;

        if (Directory.Exists(languageDir))
            foreach (var file in Directory.EnumerateFiles(languageDir, "*.lng"))
            {
                var to = Path.Combine(project.LanguageFolder, Path.GetFileName(file));

                if (edits.Text.Count == 0) File.Copy(file, to, overwrite: true);
                else File.WriteAllBytes(to, edits.ApplyTo(LngFile.Load(file)));

                copied++;
            }

        if (copied == 0)
            throw new InvalidOperationException(
                "The game’s language files aren’t here yet, and a project is built from them. " +
                "They come out of the game’s own exe, so set the game folder in Settings - the app " +
                "takes them the next time it opens.");

        // The 2026 key list and the names beside it: reference data the game
        // ships without, read by the Teams tab. Taken fresh like the rest.
        foreach (var extra in new[] { "2026_team_keys.lng", "2026_team_names.json" })
        {
            var from = Path.Combine(originalsDir, extra);
            if (File.Exists(from)) File.Copy(from, Path.Combine(project.LanguageFolder, extra), overwrite: true);
        }
    }

    /// <summary>
    /// Read what this project has changed out of its working files and store
    /// that, which is all a saved project holds. Called after the tabs have
    /// written their edits and before the zip is made.
    /// </summary>
    public static void CaptureEdits(GridProject project, string originalsDir, string? gameDir,
                                    IProgress<string>? progress = null)
    {
        progress?.Report("Working out what you changed…");

        var edits = ProjectEdits.Load(project.EditsPath);

        var chosen = Simplified.Shared.GameDatabaseSource.Resolve(
            gameDir, Path.Combine(originalsDir, DatabaseFileName));

        if (File.Exists(chosen.Path) && File.Exists(project.DatabasePath))
            edits.Database = ProjectEdits.DatabaseDifference(
                GameDatabase.Load(chosen.Path), GameDatabase.Load(project.DatabasePath));

        // English alone: every language is written the same edits, so one file
        // says what they were.
        var pristineText = Path.Combine(
            originalsDir, Simplified.Language.LanguageFiles.FolderName, ReferenceLanguage);
        var workingText = Path.Combine(project.LanguageFolder, ReferenceLanguage);

        if (File.Exists(pristineText) && File.Exists(workingText))
            edits.Text = ProjectEdits.TextDifference(
                LngFile.Load(pristineText), LngFile.Load(workingText));

        edits.BuiltAgainst = InstalledBuild(gameDir);
        edits.Save(project.EditsPath);
    }

    /// <summary>
    /// Which build of the game is installed, for the record kept in the
    /// project. Nothing reads it back: it is there so a project can say what it
    /// was last built against when somebody asks why a mod behaves oddly.
    /// </summary>
    private static string InstalledBuild(string? gameDir)
    {
        try
        {
            if (Simplified.Language.LanguageFiles.FindExe(gameDir) is not { } exe) return "";

            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// The language a difference is read from. Every language gets the same
    /// edits, and this is the one the editors work in.
    /// </summary>
    private const string ReferenceLanguage = "language_eng.lng";

    /// <summary>
    /// Open a <c>.simplified</c>: unpack what it holds, and build this
    /// session’s game files from the installed game with its changes on top.
    /// </summary>
    public static GridProject Open(string zipPath, string originalsDir, string? gameDir,
                                   Action<string>? note = null, IProgress<string>? progress = null)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Project file not found.", zipPath);

        progress?.Report("Unpacking the project…");
        var temp = AllocateTempDir();
        ZipFile.ExtractToDirectory(zipPath, temp);

        ProjectInfo info = new();
        var infoPath = Path.Combine(temp, InfoFileName);
        if (File.Exists(infoPath))
        {
            try { info = JsonSerializer.Deserialize<ProjectInfo>(File.ReadAllText(infoPath), Json) ?? new ProjectInfo(); }
            catch { /* keep defaults — a project with an unreadable info.json still opens */ }
        }

        var project = new GridProject
        {
            TempDir = temp,
            WorkDir = AllocateTempDir(),
            ZipPath = zipPath,
            Info = info,
        };

        Directory.CreateDirectory(project.ErpsFolder);

        // A project from before this change carries whole files rather than the
        // changes to them. What it changed is still in there — it is the
        // difference between the files it carries and the game’s own — so it is
        // read out once, here, and the files dropped.
        Convert(project, originalsDir, gameDir, note, progress);

        Rebuild(project, originalsDir, gameDir, note, progress);
        return project;
    }

    /// <summary>
    /// Turn a project that carries whole files into one that carries its
    /// changes. Does nothing to a project that already does.
    ///
    /// What it cannot know is which build of the game the old project was
    /// started from, so the comparison is against the game as it is now. A
    /// record the game has patched since will read as something this project
    /// changed, and be kept — which is the safe way round: it keeps what the
    /// project had rather than dropping it quietly, and it can be put back by
    /// hand.
    /// </summary>
    private static void Convert(GridProject project, string originalsDir, string? gameDir,
                                Action<string>? note, IProgress<string>? progress)
    {
        var storedDatabase = Path.Combine(project.TempDir, DatabaseFolderName, DatabaseFileName);
        var storedText = Path.Combine(project.TempDir, LanguageFolderName, ReferenceLanguage);

        if (!File.Exists(storedDatabase) && !File.Exists(storedText)) return;

        progress?.Report("Reading what this project changed…");

        var edits = ProjectEdits.Load(project.EditsPath);
        var chosen = Simplified.Shared.GameDatabaseSource.Resolve(
            gameDir, Path.Combine(originalsDir, DatabaseFileName));

        if (File.Exists(storedDatabase) && File.Exists(chosen.Path))
            edits.Database = ProjectEdits.DatabaseDifference(
                GameDatabase.Load(chosen.Path), GameDatabase.Load(storedDatabase));

        var pristineText = Path.Combine(
            originalsDir, Simplified.Language.LanguageFiles.FolderName, ReferenceLanguage);

        if (File.Exists(storedText) && File.Exists(pristineText))
            edits.Text = ProjectEdits.TextDifference(
                LngFile.Load(pristineText), LngFile.Load(storedText));

        edits.Save(project.EditsPath);

        // The files themselves go, or the next save would put them back in the
        // zip beside the changes they have become.
        foreach (var folder in new[] { DatabaseFolderName, LanguageFolderName })
        {
            var path = Path.Combine(project.TempDir, folder);
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch { /* left in the zip; harmless, and the next save tries again */ }
        }

        note?.Invoke($"This project was made before the change and has been converted: " +
                     $"{edits.Database.Count} database change(s) and {edits.Text.Count} text change(s) are " +
                     "kept as changes now, and the game files come from your install.");
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

    /// <summary>
    /// Discard a project: what it was unpacked into, and the game files built
    /// beside it.
    /// </summary>
    public static void Close(GridProject project)
    {
        foreach (var dir in new[] { project.TempDir, project.WorkDir })
        {
            try
            {
                if (dir.Length > 0 && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort — the next CleanupStaleTempDirs sweep will get it.
            }
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
