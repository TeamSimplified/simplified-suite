using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SimplifiedTeamEditor.Core;

namespace SimplifiedTeamEditor.Util;

/// <summary>
/// Auto-discovery for the F1 game install directory. Scans every Steam
/// library found via Steam's <c>libraryfolders.vdf</c> plus a short list of
/// common EA / Origin install roots, returning any folder that looks like
/// an F1 game install (folder named <c>F1 NN</c>/<c>F1 NNNN</c> containing
/// a matching <c>F1_*.exe</c>).
///
/// Only the one game this app targets — <see cref="GameFiles.GameFolderName"/> —
/// is ever returned. Older F1 titles sitting in the same Steam library are of
/// no use here, so the user is never asked to pick from a list.
/// </summary>
internal static class GameDirDetector
{
    /// <summary>Detected install — absolute path + how we found it (Steam / EA), for logging.</summary>
    public sealed record Candidate(string Path, string Source);

    /// <summary>
    /// The F1 25 install, or null when it isn't on the machine. When more than
    /// one folder could pass for it (<c>F1 25</c> alongside a renamed copy), the
    /// one that actually carries <see cref="GameFiles.ShaderFolderName"/>
    /// wins — that's the folder the shader packages are read from.
    /// </summary>
    public static Candidate? Detect()
    {
        // 25, from "F1 25". A local rather than a static field: static
        // initialisers run in declaration order, and this needs F1FolderRegex,
        // which is declared further down the file.
        var targetGame = GameNumberFromFolder(GameFiles.GameFolderName);

        Candidate? fallback = null;

        foreach (var c in DetectAll())
        {
            if (GameNumberFromFolder(System.IO.Path.GetFileName(c.Path)) != targetGame)
                continue;

            if (Directory.Exists(System.IO.Path.Combine(c.Path, GameFiles.ShaderFolderName)))
                return c;

            fallback ??= c;
        }

        return fallback;
    }

    /// <summary>
    /// Discover every plausible F1 install directory on the machine.
    /// Returns a deduped list ordered newest-game-first.
    /// </summary>
    private static IReadOnlyList<Candidate> DetectAll()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<Candidate>();

        void Add(string path, string source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var full = System.IO.Path.GetFullPath(path);
            if (seen.Add(full)) hits.Add(new Candidate(full, source));
        }

        foreach (var lib in EnumerateSteamLibraries())
        {
            var common = System.IO.Path.Combine(lib, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            foreach (var f1 in EnumerateF1Folders(common))
                Add(f1, "Steam");
        }

        foreach (var root in EnumerateEaInstallRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var f1 in EnumerateF1Folders(root))
                Add(f1, "EA");
        }

        // Newest game first — "F1 25" > "F1 24" > … > "F1 2020".
        return hits
            .OrderByDescending(c => GameNumberFromFolder(System.IO.Path.GetFileName(c.Path)))
            .ToList();
    }

    // ─────────────────────── Steam ────────────────────────────────────

    /// <summary>
    /// Steam's main install directory, taken from the registry. Returns null
    /// when Steam isn't installed (or the registry key is missing — same thing).
    /// </summary>
    private static string? GetSteamInstallDir()
    {
        // 64-bit Steam (rare) lives under the non-WOW64 view; 32-bit Steam
        // (the default install) under WOW6432Node. Cover both, plus the
        // per-user path which some Steam betas use.
        foreach (var (root, view, sub, name) in new (RegistryHive, RegistryView, string, string)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam",             "InstallPath"),
            (RegistryHive.CurrentUser,  RegistryView.Default,    @"SOFTWARE\Valve\Steam",             "SteamPath"),
        })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(root, view);
                using var key  = hive.OpenSubKey(sub);
                if (key?.GetValue(name) is string s && !string.IsNullOrWhiteSpace(s))
                    return s.Replace('/', '\\');
            }
            catch { /* try next */ }
        }
        return null;
    }

    /// <summary>
    /// Every Steam library root configured on the machine, including the
    /// main Steam install dir itself. Parses <c>libraryfolders.vdf</c> with
    /// a single regex over <c>"path" "..."</c> entries — robust enough for
    /// Valve's stable schema and avoids pulling in a VDF parser dependency.
    /// </summary>
    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        var steam = GetSteamInstallDir();
        if (steam is null) yield break;
        yield return steam;

        var vdf = System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string body;
        try { body = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (Match m in Regex.Matches(body, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            // Steam writes paths with escaped backslashes inside the VDF.
            var p = m.Groups[1].Value.Replace("\\\\", "\\");
            if (!string.IsNullOrWhiteSpace(p)) yield return p;
        }
    }

    // ─────────────────────── EA / Origin ──────────────────────────────

    /// <summary>
    /// Common root folders where EA / Origin drops game installs. We don't
    /// try to scan every drive — this hits 99% of installs without hammering
    /// the filesystem.
    /// </summary>
    private static IEnumerable<string> EnumerateEaInstallRoots()
    {
        var pf  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { pf, pf86 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return System.IO.Path.Combine(root, "EA Games");
            yield return System.IO.Path.Combine(root, "Origin Games");
        }
    }

    // ─────────────────────── F1 folder matching ───────────────────────

    private static readonly Regex F1FolderRegex = new(
        @"^F1[ _](20\d\d|\d{2})( .*)?$",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Folders under <paramref name="root"/> that look like an F1 install:
    /// name matches <c>F1 NN</c> / <c>F1 NNNN</c>, contains a matching
    /// <c>F1_*.exe</c> at the top level.
    /// </summary>
    private static IEnumerable<string> EnumerateF1Folders(string root)
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root); }
        catch { yield break; }

        foreach (var d in dirs)
        {
            var name = System.IO.Path.GetFileName(d);
            if (!F1FolderRegex.IsMatch(name)) continue;

            bool hasExe = false;
            try { hasExe = Directory.EnumerateFiles(d, "F1_*.exe", SearchOption.TopDirectoryOnly).Any(); }
            catch { /* permission issue — skip */ }
            if (hasExe) yield return d;
        }
    }

    /// <summary>
    /// Extract the year number from an F1 folder name for ordering.
    /// "F1 25" → 25, "F1 2021" → 2021. Unknown → 0.
    /// </summary>
    private static int GameNumberFromFolder(string name)
    {
        var m = F1FolderRegex.Match(name);
        if (!m.Success) return 0;
        return int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }
}
