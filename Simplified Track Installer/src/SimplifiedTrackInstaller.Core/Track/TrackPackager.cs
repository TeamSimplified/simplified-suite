using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Util;

namespace SimplifiedTrackInstaller.Core.Track;

/// <summary>What to build: a track zip, against a game install, into a mod.</summary>
/// <param name="DatabasePath">
/// A game database to ship alongside, e.g. one the track was added to with the
/// EgoGameDatabasesEditor. Null to leave the game's own alone.
/// </param>
public sealed record TrackPackageRequest(
    string TrackZipPath,
    string GameDir,
    string OutputZipPath,
    string? DatabasePath,
    string[] SimplifiedApp);

public sealed record TrackPackageResult(
    string ZipPath,
    string TrackName,
    int TrackResources,
    int MipFiles,
    int Arrays,
    int HandlingFiles,
    bool Database,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a track zip into a mod the suite's launcher installs, the way every
/// other app in the suite packages one:
/// <code>
/// mod.serps
/// F1 25/
///   shader_package_2025/
///     track_info.nefx2.sm51.erp       the game's, with the track merged in
///     track_info.nefx2.sm51_amd.erp   the same
///   environment_package/tracks/&lt;code&gt;/mipmaps/
///     the track's own mip files
///   game_package/gamedatabases/gamedatabases.2025.bin
///     only when one was picked
/// </code>
///
/// What goes into the shader packages is everything the EgoGameDatabasesEditor
/// changes when it installs the track, measured by diffing a game before and
/// after it did:
/// <list type="bullet">
/// <item>the track's ERP, whole — every resource under its own id;</item>
/// <item>its pictures added to the menu's texture arrays, and named in overrides.uidb;</item>
/// <item>its entry in minimap.uidb;</item>
/// <item>its preview and session pictures in ui_dynamic_texture_loading.uidta,
///       and the copies of them those point at;</item>
/// <item>its ERS values in every 2026 car's handling file.</item>
/// </list>
/// A shader package replaces by id, so the changed databases, arrays and
/// handling files ship whole, built from the user's own game.
///
/// Not done here: editing the game database, which is what makes the track
/// selectable. One already edited can be picked and is shipped as it is.
/// </summary>
public static class TrackPackager
{
    public const string AppId = "track_installer";

    public const string GameFolderName = "F1 25";
    public const string ShaderFolderName = "shader_package_2025";

    /// <summary>The pair the track is merged into.</summary>
    public static readonly string[] ShaderArchiveNames =
    [
        "track_info.nefx2.sm51.erp",
        "track_info.nefx2.sm51_amd.erp",
    ];

    /// <summary>Where the game database sits in the install, and so in a package.</summary>
    public const string DatabasePath = "game_package/gamedatabases/gamedatabases.2025.bin";

    /// <summary>What a package for this track is called by default: <c>Istanbul Park - 2.45.0.zip</c>.</summary>
    public static string SuggestedFileName(TrackZip zip)
    {
        var name = SanitizeName(zip.Name);
        if (!string.IsNullOrWhiteSpace(zip.Version)) name += " - " + SanitizeName(zip.Version.Trim());
        return name + ".zip";
    }

    /// <summary>
    /// Null when <paramref name="path"/> reads as a game database, otherwise why
    /// it does not — so a wrong file is turned away when it is picked rather than
    /// shipped to somebody whose game then won't start.
    /// </summary>
    public static string? CheckDatabase(string path)
    {
        try
        {
            EgoEngineLibrary.Data.GameDatabases.GameDatabase.Load(path);
            return null;
        }
        catch (Exception ex)
        {
            return $"That isn't a game database the suite can read ({ex.Message}).";
        }
    }

    private static readonly JsonSerializerOptions SerpsJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static TrackPackageResult Build(TrackPackageRequest request, Action<string>? log = null,
                                           IProgress<string>? say = null, IProgress<double>? progress = null,
                                           CancellationToken cancel = default)
    {
        // How far through the whole job, 0 to 1, from how far through the
        // current stage. Only ever forward, so a stage that reports late can't
        // pull the bar back.
        var reached = 0.0;
        void Step(Stage stage, double fraction)
        {
            var (from, to) = Span(stage);
            var at = from + (to - from) * Math.Clamp(fraction, 0, 1);
            if (at <= reached) return;
            reached = at;
            progress?.Report(at);
        }

        var gameDir = request.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            throw new DirectoryNotFoundException("The game folder isn't set, or isn't there — set it in Settings.");

        var shaderFolder = Path.Combine(gameDir, ShaderFolderName);
        foreach (var name in ShaderArchiveNames)
            if (!File.Exists(Path.Combine(shaderFolder, name)))
                throw new FileNotFoundException($"The game has no {ShaderFolderName}\\{name}.");

        var warnings = new List<string>();
        void Warn(string text)
        {
            warnings.Add(text);
            log?.Invoke("  ⚠ " + text);
        }

        var work = Path.Combine(Path.GetTempPath(), "simplified-track-installer", "package", Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(work, "stage");
        Directory.CreateDirectory(stage);

        try
        {
            using var zip = TrackZip.Open(request.TrackZipPath);
            log?.Invoke($"{zip.Name} ({zip.Code}) {zip.Version}{(zip.Author.Length > 0 ? " by " + zip.Author : "")}");

            // ── The track ───────────────────────────────────────────────────
            say?.Report("Reading the track…");
            log?.Invoke($"Reading {zip.Erp.FullName} ({zip.Erp.Length / 1048576.0:N0} MB)...");
            var erpPath = Path.Combine(work, "track.erp");
            using (var source = zip.Erp.Open())
            using (var file = File.Create(erpPath))
                Copy(source, file, zip.Erp.Length, f => Step(Stage.Extract, f));
            var track = ErpFileIO.Load(erpPath);
            log?.Invoke($"  {track.Resources.Count:N0} resources.");
            Step(Stage.Read, 1);
            cancel.ThrowIfCancellationRequested();

            // ── Its mip files, in a folder of its own ───────────────────────
            say?.Report("Pointing the track at its own mip files…");
            log?.Invoke("Mip files...");
            var used = MipLinks.Repoint(track, zip.Code, zip.Mipmaps.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), log);
            var mipFolder = Path.Combine(stage, GameFolderName, MipLinks.FolderFor(zip.Code).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(mipFolder);
            foreach (var name in used)
            {
                using var source = zip.Mipmaps[name].Open();
                using var file = File.Create(Path.Combine(mipFolder, name));
                source.CopyTo(file);
            }
            var unused = zip.Mipmaps.Count - used.Count;
            if (unused > 0) Warn($"{unused} mip file(s) in the zip belong to no texture in the track, so they're left out.");
            Step(Stage.Mips, 1);
            cancel.ThrowIfCancellationRequested();

            // ── The menus ───────────────────────────────────────────────────
            say?.Report("Adding the track to the menus…");
            var game = new GameArchives(gameDir);
            var overlay = new List<ErpResource>();

            ErpResource Game(string id) =>
                game.Find(id, GameArchives.UiArchives)
                ?? throw new InvalidDataException($"The game's UI archives have no {id}.");

            log?.Invoke("Texture arrays...");
            var overridesRes = Game(MenuDatabases.OverridesId);
            var overrides = PkgJson.Read(overridesRes);
            var arrays = 0;
            foreach (var (picture, names) in TextureArrays.Slots)
            {
                if (!zip.Images.TryGetValue(picture, out var pictureId))
                {
                    Warn($"images.json has no {picture}, so the arrays that show it don't get the track.");
                    continue;
                }

                var pictureRes = track.Resources.Find(r => r.Identifier == pictureId + ".image")
                                 ?? throw new InvalidDataException($"The track has no {pictureId}.image.");

                foreach (var array in names)
                {
                    var (index, added) = MenuDatabases.AddToArray(overrides, array, zip.Code, pictureId);
                    var view = Game(TextureArrays.ViewId(array));
                    var surface = Game(TextureArrays.ViewId(array) + ".image");
                    TextureArrays.SetSlice(view, surface, pictureRes, index, array);
                    overlay.Add(view);
                    overlay.Add(surface);
                    arrays++;
                    log?.Invoke($"  {array}: slice {index}{(added ? "" : " (replaced)")}");
                }
            }
            PkgJson.Write(overridesRes, overrides);
            overlay.Add(overridesRes);

            log?.Invoke("Minimap...");
            var minimapRes = Game(MenuDatabases.MinimapId);
            var minimap = PkgJson.Read(minimapRes);
            var (mapIndex, mapAdded) = MenuDatabases.AddMinimap(minimap, zip, log);
            PkgJson.Write(minimapRes, minimap);
            overlay.Add(minimapRes);
            log?.Invoke($"  hud.minimap.tracks[{mapIndex}]{(mapAdded ? "" : " (replaced)")}");

            log?.Invoke("Preview and session pictures...");
            var loadingRes = Game(MenuDatabases.LoadingId);
            var loading = PkgJson.Read(loadingRes);
            var groups = MenuDatabases.AddPictures(loading, zip.Code);
            PkgJson.Write(loadingRes, loading);
            overlay.Add(loadingRes);
            overlay.AddRange(SharedPictures.Copy(track, zip));
            log?.Invoke($"  {groups} menu lists, {SharedPictures.Pictures.Length} pictures.");

            game.Release();
            Step(Stage.Menus, 1);
            cancel.ThrowIfCancellationRequested();

            // ── The cars ────────────────────────────────────────────────────
            say?.Report("Giving the cars the track's ERS…");
            log?.Invoke("Handling files...");
            var handling = EnergySettings.Build(gameDir, zip, log);
            overlay.AddRange(handling);
            Step(Stage.Handling, 1);
            cancel.ThrowIfCancellationRequested();

            // ── Into both shader packages ───────────────────────────────────
            foreach (var warning in Simplified.Shared.GameFileGuard.Warnings(gameDir,
                         ShaderArchiveNames.Select(Simplified.Shared.GameFileGuard.ShaderPath)))
                Warn(warning);

            var staged = Path.Combine(stage, GameFolderName, ShaderFolderName);
            Directory.CreateDirectory(staged);
            var trackResources = track.Resources.ToList();

            for (var i = 0; i < ShaderArchiveNames.Length; i++)
            {
                var name = ShaderArchiveNames[i];
                cancel.ThrowIfCancellationRequested();
                say?.Report($"Merging into {name}…");
                log?.Invoke($"Merging into {name}...");

                var target = ErpFileIO.Load(Path.Combine(shaderFolder, name));
                Simplified.Shared.SerpsOverlay.AddTo(target);

                // The track first, then what was built on the game: a shared
                // picture's copy or a handling file wins over anything of the
                // same name the track carried.
                MergeInto(target, trackResources);
                MergeInto(target, overlay);

                target.UpdateOffsets();
                ErpFileIO.Save(target, Path.Combine(staged, name));
                log?.Invoke($"  {target.Resources.Count:N0} resources, {new FileInfo(Path.Combine(staged, name)).Length / 1048576.0:N0} MB.");
                Step(Stage.Merge, (i + 1.0) / ShaderArchiveNames.Length);
            }

            // The extracted track is no longer needed, and it is large.
            track = null;
            try { File.Delete(erpPath); } catch { }

            // ── The game database, when one was picked ──────────────────────
            // Shipped as it is. Read once more first: checked when it was
            // picked, but it is a file somebody may have changed since.
            var database = !string.IsNullOrWhiteSpace(request.DatabasePath);
            if (database)
            {
                if (CheckDatabase(request.DatabasePath!) is { } problem) throw new InvalidDataException(problem);
                log?.Invoke($"Game database: {request.DatabasePath}");
                var target = Path.Combine(stage, GameFolderName, DatabasePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(request.DatabasePath!, target);
            }

            // ── mod.serps and the zip ───────────────────────────────────────
            File.WriteAllText(Path.Combine(stage, "mod.serps"), BuildModSerps(zip, gameDir, request.SimplifiedApp));

            say?.Report("Writing your mod file…");
            log?.Invoke("Zipping...");
            var zipPath = request.OutputZipPath;
            if (Path.GetDirectoryName(zipPath) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipDirectory(stage, zipPath, f => Step(Stage.Zip, f), cancel);
            Step(Stage.Zip, 1);

            return new TrackPackageResult(zipPath, zip.Name, trackResources.Count, used.Count, arrays,
                                          handling.Count, database, warnings);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// <summary>The steps a package goes through, in order.</summary>
    private enum Stage { Extract, Read, Mips, Menus, Handling, Merge, Zip }

    /// <summary>
    /// Where each stage starts and ends on the bar: its share of the time,
    /// measured on Istanbul Park. Zipping 3 GB is 78 of the 88 seconds, so it
    /// gets most of the bar, and follows the bytes as it goes.
    /// </summary>
    private static (double From, double To) Span(Stage stage) => stage switch
    {
        Stage.Extract => (0.00, 0.005),
        Stage.Read => (0.005, 0.01),
        Stage.Mips => (0.01, 0.04),
        Stage.Menus => (0.04, 0.08),
        Stage.Handling => (0.08, 0.10),
        Stage.Merge => (0.10, 0.12),
        _ => (0.12, 1.00),
    };

    /// <summary>Copy a stream, saying how much of <paramref name="length"/> has gone.</summary>
    private static void Copy(Stream source, Stream destination, long length, Action<double> done,
                             Action<long>? moved = null)
    {
        var buffer = new byte[1 << 20];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            copied += read;
            moved?.Invoke(read);
            if (length > 0) done((double)copied / length);
        }
    }

    /// <summary>
    /// The staging folder as a zip, its contents at the root — what
    /// <see cref="ZipFile.CreateFromDirectory(string, string, CompressionLevel, bool)"/>
    /// writes, written here so the bar can follow it: it is the longest step.
    /// </summary>
    private static void ZipDirectory(string folder, string zipPath, Action<double> done, CancellationToken cancel)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        var total = files.Sum(f => new FileInfo(f).Length);
        long written = 0;

        using var output = File.Create(zipPath);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            cancel.ThrowIfCancellationRequested();
            var name = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(file);

            using var source = File.OpenRead(file);
            using var target = entry.Open();
            Copy(source, target, total, _ => { }, n =>
            {
                written += n;
                done(total > 0 ? (double)written / total : 1);
            });
        }
    }

    /// <summary>Fold resources into a target archive, replacing anything of the same id.</summary>
    private static void MergeInto(ErpFile target, IEnumerable<ErpResource> resources)
    {
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < target.Resources.Count; i++) byId.TryAdd(target.Resources[i].Identifier, i);

        foreach (var resource in resources)
        {
            resource.ParentFile = target;
            foreach (var fragment in resource.Fragments) fragment.ParentFile = target;

            if (byId.TryGetValue(resource.Identifier, out var at))
            {
                target.Resources[at] = resource;
            }
            else
            {
                byId[resource.Identifier] = target.Resources.Count;
                target.Resources.Add(resource);
            }
        }
    }

    /// <summary>The manifest at the root, the shape every app in the suite writes.</summary>
    private static string BuildModSerps(TrackZip zip, string gameDir, string[] simplifiedApp)
    {
        var manifest = new Dictionary<string, object> { ["mod_name"] = zip.Name.Trim() };
        if (!string.IsNullOrWhiteSpace(zip.Author)) manifest["author"] = zip.Author.Trim();
        if (!string.IsNullOrWhiteSpace(zip.Version)) manifest["version"] = zip.Version.Trim();
        manifest["game"] = Simplified.Shared.GameVersion.ReadName(gameDir) ?? "";
        manifest["game_version"] = Simplified.Shared.GameVersion.Read(gameDir) ?? "";
        manifest["simplified_app"] = simplifiedApp;
        return JsonSerializer.Serialize(manifest, SerpsJson);
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.', ' ');
        return s.Length == 0 ? "track" : s;
    }
}
