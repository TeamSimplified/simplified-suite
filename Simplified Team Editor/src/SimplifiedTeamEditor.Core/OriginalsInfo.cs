using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplifiedTeamEditor.Core;

/// <summary>
/// Which build of the game the original files came from.
///
/// A new project is built from the app's own copies of the database, the
/// language files and the team-branding archive rather than from whatever is
/// installed, so a shared package carries the user's edits and nothing else
/// their game happens to have in it. Those copies are taken out of the game -
/// see <see cref="GameOriginals"/> - and nothing in the files themselves says
/// which build they are, so the app writes it down beside them when it takes
/// them, and compares it with the install when it starts.
///
/// The F2 26 update is why this exists. It moved an entrant list, so every grid
/// after it shifted by one, and the copies then said nothing about being from
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

    /// <summary>
    /// The build exactly: its version and its exe's length, which is what
    /// <see cref="GameOriginals.Check"/> compares. Empty in notes written before
    /// the app took these files itself.
    /// </summary>
    [JsonPropertyName("build")]
    public string Build { get; init; } = "";

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

    /// <summary>Write the note, after the files beside it have been taken from <paramref name="game"/> at <paramref name="version"/>.</summary>
    public static void Write(string originalsDir, string game, string version, string build)
    {
        var info = new OriginalsInfo
        {
            Game = game,
            GameVersion = version,
            Taken = DateTime.Now.ToString("yyyy-MM-dd"),
            Note = "Taken from the installed game by the app. Read at startup to notice a game update.",
            Build = build,
        };
        File.WriteAllText(Path.Combine(originalsDir, FileName),
            JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
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
