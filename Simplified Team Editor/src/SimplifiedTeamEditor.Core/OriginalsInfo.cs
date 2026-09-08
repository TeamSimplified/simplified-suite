using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplifiedTeamEditor.Core;

/// <summary>
/// Which build of the game the bundled original files came from.
///
/// The apps ship their own copies of the database, the language files and the
/// team-branding archive, and a new project is built from those rather than from
/// whatever is installed. That is deliberate — a shared package then carries the
/// user's edits and nothing else their game happens to have in it — but it means
/// the copies go stale silently when the game updates. Nothing in the files
/// themselves says which build they are, so it is written down beside them.
///
/// The F2 26 update is why this exists. It moved an entrant list, so every grid
/// after it shifted by one, and the bundled files said nothing about being from
/// before that.
/// </summary>
public sealed class OriginalsInfo
{
    /// <summary>The file that carries this, inside the originals folder.</summary>
    public const string FileName = "originals.json";

    /// <summary>The game's own name for itself, e.g. <c>F1® 25</c>.</summary>
    [JsonPropertyName("game")]
    public string Game { get; init; } = "";

    /// <summary>The build, as <c>Major.Minor.Build.Private</c>.</summary>
    [JsonPropertyName("game_version")]
    public string GameVersion { get; init; } = "";

    /// <summary>When the files were taken, as <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("taken")]
    public string Taken { get; init; } = "";

    /// <summary>What was notable about that build, in a sentence.</summary>
    [JsonPropertyName("note")]
    public string Note { get; init; } = "";

    /// <summary>How it reads in a sentence — "F1® 25 1.0.141.2878".</summary>
    public string Describe() =>
        string.Join(' ', new[] { Game, GameVersion }.Where(s => s.Length > 0));

    /// <summary>
    /// Read it out of an originals folder, or null when it isn't there or can't
    /// be read. A missing file is not an error: it only means the build wasn't
    /// recorded, which is how every version before this one shipped.
    /// </summary>
    public static OriginalsInfo? Read(string? originalsDir)
    {
        if (string.IsNullOrWhiteSpace(originalsDir)) return null;

        try
        {
            var path = Path.Combine(originalsDir, FileName);
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<OriginalsInfo>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether these files match the build installed at <paramref name="gameDir"/>.
    ///
    /// Null when either side is unknown — an unset game folder, an unreadable
    /// exe, or originals from before this file existed — because "we cannot tell"
    /// and "they differ" are worth saying differently.
    /// </summary>
    public bool? MatchesInstalled(string? gameDir)
    {
        if (GameVersion.Length == 0) return null;

        var installed = Simplified.Shared.GameVersion.Read(gameDir);
        return string.IsNullOrEmpty(installed) ? null : installed == GameVersion;
    }
}
