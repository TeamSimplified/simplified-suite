using System.Security.Cryptography;
using EgoEngineLibrary.Archive.Erp;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Simplified.Shared;

/// <summary>What a check of one game file came to.</summary>
public enum GameFileVerdict
{
    /// <summary>Byte-for-byte a build this app has recorded.</summary>
    Clean,

    /// <summary>
    /// Holding something the game never puts there.
    ///
    /// The one verdict that is a statement rather than a question. A shader
    /// archive carries shader effects and nothing else - measured across a clean
    /// install: 390 archives, 1,092 resources, every one of them a .nefx2 - so a
    /// texture sitting in one did not arrive with the game. No manifest needed,
    /// which means this still answers on a build that shipped after this release.
    /// </summary>
    Altered,

    /// <summary>
    /// There, readable, and matching nothing on record.
    ///
    /// Deliberately not called "modified". A hash can say a file differs from
    /// what was written down; it cannot say why. A game update this app predates
    /// and a livery mod installed this morning produce exactly the same answer,
    /// and telling somebody their files are tampered with when the truth is that
    /// their game patched would be worse than saying nothing.
    /// </summary>
    Unrecognised,

    /// <summary>Not there at all.</summary>
    Missing,

    /// <summary>No manifest shipped, or none that mentions this file.</summary>
    Unknown,
}

/// <summary>One file, checked.</summary>
public sealed record GameFileCheck(string RelativePath, string FullPath, GameFileVerdict Verdict)
{
    /// <summary>The builds on record that carry this exact file, newest first.</summary>
    public IReadOnlyList<string> MatchedBuilds { get; init; } = [];

    /// <summary>What was found in there that the game does not put there.</summary>
    public IReadOnlyList<string> Foreign { get; init; } = [];

    /// <summary>Worth putting in front of somebody, in a sentence.</summary>
    public string? Warning => Verdict switch
    {
        GameFileVerdict.Altered =>
            $"{Path.GetFileName(RelativePath)} has {Foreign.Count} resource(s) in it that the game never " +
            $"puts there ({string.Join(", ", Foreign.Take(3))}{(Foreign.Count > 3 ? ", …" : "")}). " +
            "Something is already modding that archive, and merging into it would build whatever that is " +
            "into your package alongside your own work.",

        GameFileVerdict.Unrecognised =>
            $"{Path.GetFileName(RelativePath)} doesn't match any build this app has on record. " +
            "Either your game has updated since this release, or something has changed that file — " +
            "a mod already installed would be built into your package along with your own work.",
        _ => null,
    };
}

/// <summary>
/// Whether the game files a package is built from are the game's own.
///
/// Packaging reads some things from the user's install, because they have to
/// come from there: the shader archives a mod's resources are merged into are
/// the game's, and shipping our own copies of 140 MB of them is not on. That
/// makes somebody else's mod a real hazard — merge into an archive that already
/// carries one and it goes out inside your package, silently, under your name.
///
/// So the files are weighed against a manifest of SHA-256s taken from clean
/// installs. It records several builds at once, because a user on last month's
/// patch is not doing anything wrong, and it is keyed by install-relative path
/// so one manifest serves every app in the suite.
///
/// What it cannot do is prove a file is pristine. It proves a file is one this
/// release has seen. Everything else is <see cref="GameFileVerdict.Unrecognised"/>,
/// which is a question worth asking rather than an accusation — see the note on
/// that value.
///
/// A shader archive gets a second, stronger test that needs no manifest at all:
/// what is inside it. The game fills these with shader effects and nothing else,
/// so a texture in one is an addition, full stop — <see cref="GameFileVerdict.Altered"/>.
/// That test still answers on a build released after this app, which is exactly
/// when the hash has nothing to say.
/// </summary>
public static class GameFileGuard
{
    /// <summary>The manifest, in runtime\ beside the exe, same in every app.</summary>
    public const string ManifestFileName = "game-files.json";

    private static Manifest? _manifest;
    private static bool _loaded;

    /// <summary>
    /// Check one file of the install. <paramref name="relativePath"/> is its path
    /// under the game folder, spelled as the manifest spells it — that is the key
    /// the hash is recorded against.
    /// </summary>
    public static GameFileCheck Check(string gameDir, string relativePath)
    {
        var full = Path.Combine(gameDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
            return new GameFileCheck(relativePath, full, GameFileVerdict.Missing);

        // What is in it, before what it hashes to. A shader archive holding a
        // texture is modded whatever its hash says, and this is the half that
        // still works on a build released after this app was.
        if (IsShaderArchive(relativePath) && Foreign(full) is { Count: > 0 } foreign)
            return new GameFileCheck(relativePath, full, GameFileVerdict.Altered) { Foreign = foreign };

        var manifest = Load();
        var known = manifest?.BuildsCarrying(relativePath);
        if (known is null || known.Count == 0)
            return new GameFileCheck(relativePath, full, GameFileVerdict.Unknown);

        string hash;
        try
        {
            hash = Sha256(full);
        }
        catch (IOException)
        {
            // Locked by the game, or a disk that gave up mid-read. Not knowing
            // is not the same as finding something wrong.
            return new GameFileCheck(relativePath, full, GameFileVerdict.Unknown);
        }

        var matched = known
            .Where(b => string.Equals(b.Hash, hash, StringComparison.OrdinalIgnoreCase))
            .Select(b => b.Version)
            .ToList();

        return new GameFileCheck(relativePath, full,
            matched.Count > 0 ? GameFileVerdict.Clean : GameFileVerdict.Unrecognised)
        {
            MatchedBuilds = matched,
        };
    }

    /// <summary>
    /// Check several, and hand back only what is worth saying. Nothing to report
    /// is the ordinary case, so a clean install adds no noise to a package log.
    /// </summary>
    public static List<string> Warnings(string gameDir, IEnumerable<string> relativePaths) =>
        relativePaths
            .Select(p => Check(gameDir, p))
            .Select(c => c.Warning)
            .Where(w => w is not null)
            .Select(w => w!)
            .ToList();

    /// <summary>An archive under the shader folder, as the manifest keys it.</summary>
    public static string ShaderPath(string archiveName) => $"{ShaderFolderName}/{archiveName}";

    private const string ShaderFolderName = "shader_package_2025";

    private static bool IsShaderArchive(string relativePath) =>
        relativePath.StartsWith(ShaderFolderName + "/", StringComparison.OrdinalIgnoreCase)
        && relativePath.EndsWith(".erp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resources in a shader archive that the game does not put there, by
    /// identifier. Null when the file cannot be read as an archive at all —
    /// which is a different problem, and not one to report as a mod.
    ///
    /// The whole of a clean shader_package_2025 was measured to settle what
    /// belongs: 390 archives, 1,092 resources, every one a GfxNeFxResource whose
    /// identifier ends .nefx2. Nothing else. So anything else is an addition.
    ///
    /// Read from the resource table only — no payloads — so this costs a header
    /// read per archive and runs on the one or two a package actually touches.
    /// </summary>
    private static List<string>? Foreign(string path)
    {
        var index = ErpIndex.TryRead(path);
        if (index is null) return null;

        return index.Resources
            .Where(r => !r.Identifier.EndsWith(".nefx2", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Identifier)
            .ToList();
    }

    /// <summary>The game database, as the manifest keys it.</summary>
    public const string DatabasePath = "game_package/gamedatabases/gamedatabases.2025.bin";

    public static string Sha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// The manifest, read once. A missing one is not an error: it only means
    /// this build shipped without hashes, and every verdict becomes
    /// <see cref="GameFileVerdict.Unknown"/> — which reads as "no opinion"
    /// everywhere it is used.
    /// </summary>
    private static Manifest? Load()
    {
        if (_loaded) return _manifest;
        _loaded = true;

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "runtime", ManifestFileName);
            if (!File.Exists(path)) return _manifest = null;

            return _manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
        }
        catch
        {
            return _manifest = null;
        }
    }

    private sealed class Manifest
    {
        [JsonPropertyName("builds")]
        public List<Build> Builds { get; init; } = [];

        /// <summary>Every recorded (build, hash) pair for one file.</summary>
        public List<(string Version, string Hash)> BuildsCarrying(string relativePath) =>
            Builds
                .Where(b => b.Files.ContainsKey(relativePath))
                .Select(b => (Version: Describe(b), Hash: b.Files[relativePath]))
                .ToList();

        /// <summary>"F1® 25 1.0.141.7865", or just the build when no title was recorded.</summary>
        private static string Describe(Build b) =>
            string.Join(' ', new[] { b.Game, b.Version }.Where(s => s.Length > 0));
    }

    private sealed class Build
    {
        /// <summary>The title's own name for itself, e.g. <c>F1® 25</c>.</summary>
        [JsonPropertyName("game")]
        public string Game { get; init; } = "";

        [JsonPropertyName("version")]
        public string Version { get; init; } = "";

        [JsonPropertyName("taken")]
        public string Taken { get; init; } = "";

        /// <summary>Install-relative path to SHA-256, uppercase hex.</summary>
        [JsonPropertyName("files")]
        public Dictionary<string, string> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
