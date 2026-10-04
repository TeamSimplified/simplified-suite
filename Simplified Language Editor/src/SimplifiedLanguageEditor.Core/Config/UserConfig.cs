using System.Text.Json;

namespace SimplifiedLanguageEditor.Core.Config;

/// <summary>
/// User-level config persisted beside the exe, as <c>config.json</c>.
///
/// The app is portable — the folder is the install — so this travels with it
/// rather than living in %APPDATA%. Each Simplified app keeps its own.
/// </summary>
public sealed class UserConfig
{
    /// <summary>
    /// Which palette to wear. Dark by default: the rest of the suite is dark,
    /// and an app that opens light next to Studio looks like a different app.
    /// </summary>
    public string? Theme { get; set; } = "Dark";

    /// <summary>
    /// The folder the last file was opened from or saved to, so the picker
    /// starts where the work is. The eleven language files live together, so
    /// the second one is nearly always beside the first.
    /// </summary>
    public string? LastFolder { get; set; }

    /// <summary>
    /// Whether to show the log. Off by default: what it says is what just
    /// happened, which the table has already shown.
    /// </summary>
    public bool ShowLog { get; set; }

    /// <summary>
    /// How much of the table the key column takes, as a share of the whole ~
    /// 0.4 being the two-to-three the window opens with.
    ///
    /// A share rather than a width in pixels, because it has to mean the same
    /// thing on a window that is not the size this one was.
    /// </summary>
    public double KeyShare { get; set; } = 0.4;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string ConfigPath => AppPaths.ConfigFile;

    public static UserConfig Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path)) return new UserConfig();

        try
        {
            return JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(path)) ?? new UserConfig();
        }
        catch
        {
            return new UserConfig();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // The app folder can be read-only — run from a locked-down location,
            // or straight out of the zip — and that is not worth a dialog.
        }
    }
}
