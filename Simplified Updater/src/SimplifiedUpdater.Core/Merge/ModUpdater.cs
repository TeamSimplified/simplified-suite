using SimplifiedUpdater.Core.Mod;

namespace SimplifiedUpdater.Core.Merge;

/// <summary>What to update, and where the result goes.</summary>
/// <param name="ModPath">The mod as its author packed it — .zip, .7z or .rar.</param>
/// <param name="OutputZipPath">Where the updated mod is written. Always a .zip.</param>
/// <param name="GameDir">The installed game, at the version being updated to.</param>
/// <param name="OldDir">The game's database and language files as the mod found them.</param>
/// <param name="NewDir">The same files from the version being updated to.</param>
public sealed record UpdateRequest(
    string ModPath, string OutputZipPath, string GameDir, string OldDir, string NewDir);

/// <summary>What the run turned out to be.</summary>
public sealed record UpdateReport(
    string OutputPath,
    int FilesWritten,
    DatabaseMergeResult? Database,
    IReadOnlyList<(string File, LanguageMergeResult Result)> Languages,
    IReadOnlyList<ShaderMergeResult> Shaders,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True when the mod held none of the three kinds of file this app updates.</summary>
    public bool TouchedNothing => Database is null && Languages.Count == 0 && Shaders.Count == 0;
}

/// <summary>Something that stops the run, said in words a person can act on.</summary>
public sealed class UpdateRefused(string message) : Exception(message);

/// <summary>
/// Taking a mod built for one version of the game and handing back the same mod
/// built for the next one.
///
/// The shape of the job is the same for all three kinds of file it touches: the
/// new game's file is the starting point, and only what the mod actually
/// changed is carried onto it. What differs is how "what the mod changed" is
/// worked out — by comparing against the old game's copy for the database and
/// the language files, and by identifier for the shader archives, where a mod's
/// resources are ones the archive never had.
///
/// Everything else in the mod is copied through untouched. A mod is more than
/// these three files, and the parts this app has no opinion about should come
/// out exactly as they went in.
/// </summary>
public static class ModUpdater
{
    public static UpdateReport Run(UpdateRequest request, Action<string>? log = null)
    {
        var problems = Preflight(request);
        if (problems.Count > 0) throw new UpdateRefused(string.Join("\n\n", problems));

        var before = Baseline.Read(request.OldDir, "old");
        var after = Baseline.Read(request.NewDir, "new");
        var shaderFolder = Path.Combine(request.GameDir, GameFiles.ShaderFolderName);

        // The mod is unpacked, edited in place and repacked. Working in temp
        // rather than beside the exe: the app folder is the install and this
        // is scratch, and Windows clears temp on its own if a run is killed.
        var work = Path.Combine(Path.GetTempPath(), "simplified-updater", Guid.NewGuid().ToString("N"));
        var warnings = new List<string>();

        try
        {
            log?.Invoke($"Unpacking {Path.GetFileName(request.ModPath)}…");
            var files = ModArchive.ExtractTo(request.ModPath, work, log);
            log?.Invoke($"{files.Count:N0} file(s) in the mod.");

            DatabaseMergeResult? database = null;
            var languages = new List<(string, LanguageMergeResult)>();
            var shaders = new List<ShaderMergeResult>();

            foreach (var relative in files)
            {
                var full = Path.Combine(work, relative);

                if (GameFiles.IsDatabase(relative))
                {
                    if (database is not null)
                    {
                        warnings.Add($"{relative} is a second database in this mod — it was left as it was.");
                        continue;
                    }

                    log?.Invoke($"Merging {relative}…");
                    var (bytes, result) = DatabaseMerge.Apply(
                        File.ReadAllBytes(before.DatabasePath),
                        File.ReadAllBytes(full),
                        File.ReadAllBytes(after.DatabasePath));

                    File.WriteAllBytes(full, bytes);
                    database = result;
                    warnings.AddRange(result.Notes);
                    log?.Invoke($"  {result.Edited:N0} record(s) changed, {result.Added:N0} added.");
                }
                else if (GameFiles.IsLanguage(relative))
                {
                    var name = Path.GetFileName(relative);
                    if (!before.Languages.TryGetValue(name, out var oldPath) ||
                        !after.Languages.TryGetValue(name, out var newPath))
                    {
                        warnings.Add($"{name} isn't in both old\\ and new\\, so there was nothing to " +
                                     "compare it against. The mod's own copy was kept.");
                        continue;
                    }

                    log?.Invoke($"Merging {relative}…");
                    var (bytes, result) = LanguageMerge.Apply(
                        File.ReadAllBytes(oldPath),
                        File.ReadAllBytes(full),
                        File.ReadAllBytes(newPath));

                    File.WriteAllBytes(full, bytes);
                    languages.Add((name, result));
                    foreach (var note in result.Notes) warnings.Add($"{name}: {note}");
                    log?.Invoke($"  {result.Edited:N0} entrie(s) changed, {result.Added:N0} added.");
                }
                else if (GameFiles.IsShaderArchive(relative))
                {
                    log?.Invoke($"Rebuilding {relative} on the game's own…");
                    var result = ShaderMerge.Apply(full, shaderFolder);
                    if (result is null)
                    {
                        warnings.Add($"{Path.GetFileName(relative)} isn't in the game's " +
                                     $"{GameFiles.ShaderFolderName} folder, so there was no fresh copy to " +
                                     "rebuild it on. The mod's own was kept.");
                        continue;
                    }

                    shaders.Add(result);
                    log?.Invoke($"  {result.Carried:N0} resource(s) carried over, " +
                                $"{result.Preserved:N0} left at the game's.");
                }
            }

            log?.Invoke("Packing…");
            var written = ModArchive.WriteZip(work, request.OutputZipPath);

            return new UpdateReport(request.OutputZipPath, written, database, languages, shaders, warnings);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch { /* temp, and Windows clears it */ }
        }
    }

    /// <summary>
    /// Everything that would stop the run, checked before a byte is unpacked.
    /// Empty means it can go ahead.
    /// </summary>
    public static List<string> Preflight(UpdateRequest request)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(request.ModPath) || !File.Exists(request.ModPath))
            problems.Add("Choose the mod to update.");
        else if (!ModArchive.LooksLikeMod(request.ModPath))
            problems.Add($"{Path.GetFileName(request.ModPath)} isn't an archive this can open. " +
                         $"It reads {string.Join(", ", ModArchive.Extensions)}.");

        if (string.IsNullOrWhiteSpace(request.GameDir))
            problems.Add("The game folder isn't set. Open Settings and pick (or detect) the install — " +
                         "a mod's shader archives are rebuilt on the game's own.");
        else if (!Directory.Exists(request.GameDir))
            problems.Add($"The game folder doesn't exist: {request.GameDir}");
        else if (!Directory.Exists(Path.Combine(request.GameDir, GameFiles.ShaderFolderName)))
            problems.Add($"There is no {GameFiles.ShaderFolderName} under the game folder — " +
                         "that may not be the install root.");

        problems.AddRange(Baseline.Problems(request.OldDir, request.NewDir));
        return problems;
    }
}
