using System.IO;
using System.Text.Json;

namespace SimplifiedLauncher;

/// <summary>
/// What the launcher remembers, as <c>config.json</c> beside the exe.
///
/// Portable, like the rest of the suite: the folder is the install, so settings
/// travel with it rather than living in %APPDATA%, and deleting one file is how
/// you start over.
///
/// The values are the words on the chips rather than anything internal, so the
/// file reads as what was picked and can be edited by hand. GameDirDetector's
/// own vocabulary ("EA", not "EA App") is translated at the one place detection
/// meets the UI, not stored.
/// </summary>
public sealed class UserConfig
{
    /// <summary>
    /// The F1 25 install. Detected on first run; from then on this is what the
    /// launcher uses, unless the folder has gone, in which case it is ignored
    /// and the search runs again.
    /// </summary>
    public string? GameDir { get; set; }

    /// <summary>Which shop the game came from: <c>Steam</c> or <c>EA App</c>.</summary>
    public string? Platform { get; set; }

    /// <summary>Which graphics card to install for: <c>AMD</c> or <c>Other</c>.</summary>
    public string? Gpu { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Beside the exe. AppContext.BaseDirectory is right for both an ordinary
    /// build and a single-file one; Assembly.Location returns "" in single-file
    /// (IL3000).
    /// </summary>
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static UserConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new UserConfig();

        try
        {
            return JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(ConfigPath)) ?? new UserConfig();
        }
        catch
        {
            // A config that cannot be read is the same as not having one. Better
            // to open with defaults than to refuse to start over a stray comma.
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
