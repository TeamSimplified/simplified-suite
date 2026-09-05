using System.Text.Json;

namespace SimplifiedDriverSwapper.Core.Config;

/// <summary>
/// Team-only settings, in a <c>team.simplified</c> file next to the executable.
/// The file's presence with <c>"advanced": true</c> is what unlocks the Ctrl+T
/// menu, and the same file holds what that menu toggles.
///
/// Kept out of the per-user <see cref="UserConfig"/> on purpose: everyone has a
/// config.json they could hand-edit, but only team builds ship a
/// team.simplified. A normal build has no marker, so there's no file to edit to
/// turn anything on.
///
/// <code>
/// {
///   "advanced": true,
///   "showLog": false
/// }
/// </code>
/// </summary>
public sealed class TeamConfig
{
    /// <summary>Master unlock — Ctrl+T does nothing unless this is true.</summary>
    public bool Advanced { get; set; }

    /// <summary>
    /// Whether the log panel is shown. Off by default, so a normal build never
    /// displays it.
    /// </summary>
    public bool ShowLog { get; set; }



    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "team.simplified");

    /// <summary>
    /// Load the marker. Returns an all-default (locked) instance when the file is
    /// missing or unreadable, so a normal build — or a corrupt marker — stays
    /// locked with everything off.
    /// </summary>
    public static TeamConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new TeamConfig();
            return JsonSerializer.Deserialize<TeamConfig>(File.ReadAllText(FilePath), Json) ?? new TeamConfig();
        }
        catch
        {
            return new TeamConfig();
        }
    }

    /// <summary>
    /// Write the marker back. Callers must keep <see cref="Advanced"/> true or
    /// they'd lock themselves out of the menu they're saving from.
    /// </summary>
    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); }
        catch { /* a read-only install shouldn't break the toggle for this session */ }
    }
}
