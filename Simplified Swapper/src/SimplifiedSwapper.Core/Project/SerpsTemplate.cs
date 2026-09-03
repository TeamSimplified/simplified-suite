using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplifiedSwapper.Core.Project;

/// <summary>
/// An open <c>.serps</c> template. While open its contents live in
/// <see cref="TempDir"/>; the file on disk is only touched on Open (extracted)
/// and Save (rezipped), the way Studio treats a <c>.simplified</c> project.
///
/// On-disk layout inside the archive:
/// <code>
/// template.serps                                  ← name / author / version
/// ERPs/
///     01--00--UI/
///         01--00--26-ferrari_ui_teamname.erp
///     02--01--Livery - Default Chassis/
///         01--00--26-ferrari_car_default_livery-and-chassis.erp
/// </code>
///
/// The <c>NN--GG--</c> prefix on every folder and part is Studio's: the first
/// pair is display and merge order, the second a conflict group scoped to the
/// parent folder — two parts in the same folder sharing a non-zero group are
/// alternatives. That is why a swap lands in a folder numbered to match the
/// stock one it replaces rather than in a folder of its own naming.
///
/// This app never renumbers anything. It adds one folder per swap and leaves
/// the template exactly as its author arranged it.
/// </summary>
public sealed class SerpsTemplate
{
    /// <summary>Path to the .serps on disk. Never null — a template is always opened from one.</summary>
    public string SerpsPath { get; set; } = "";

    /// <summary>Working temp directory mirroring the archive.</summary>
    public string TempDir { get; set; } = "";

    /// <summary>
    /// The archive's ZIP comment, carried through verbatim.
    ///
    /// It holds <c>{"erp_file_string":"object_flag.nefx2.sm51"}</c> — the shader
    /// package, which is the one thing about a template that lives nowhere else
    /// in the file. Studio reads it when a project is created from the template,
    /// and a project without it cannot be packaged at all, so a rezip that drops
    /// it quietly ruins the template.
    /// </summary>
    public string Comment { get; set; } = "";

    public SerpsInfo Info { get; set; } = new();

    /// <summary>
    /// The shader package out of <see cref="Comment"/>, e.g.
    /// <c>object_flag.nefx2.sm51</c>. Empty when the comment is missing or
    /// isn't the JSON it should be.
    ///
    /// This is what says whose template it is — see <see cref="TeamErps"/>.
    /// </summary>
    public string ShaderPackage { get; set; } = "";

    public string ErpsFolder => Path.Combine(TempDir, SerpsStore.ErpsFolderName);

    /// <summary>
    /// The token every ERP in this template is named after — <c>26-ferrari</c>,
    /// <c>26-astonmartin</c>, <c>26-racingbulls</c>.
    ///
    /// <code>
    ///   01--00--26-ferrari_car_default_livery-and-chassis.erp
    ///           └──────┬──────┘
    ///                  this
    /// </code>
    ///
    /// Read off the parts rather than mapped from the team, because it is a
    /// fourth set of names again — neither the shader package, nor the game's
    /// folders, nor the driverwear sets — and the template is the authority on
    /// its own. Every one in the 2026 pack uses a single token throughout, so
    /// the commonest wins and a stray part can't move it.
    ///
    /// Empty when nothing in here is named that way, which is what a template
    /// stripped to nothing would look like.
    /// </summary>
    public string NamePrefix
    {
        get
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var part in AllPartPaths)
            {
                var m = NamePrefixPattern.Match(Path.GetFileName(part));
                if (!m.Success) continue;
                var token = m.Groups[1].Value;
                counts[token] = counts.GetValueOrDefault(token) + 1;
            }

            return counts.Count == 0 ? "" : counts.MaxBy(kv => kv.Value).Key;
        }
    }

    /// <summary>The prefixed name, then everything up to the first underscore.</summary>
    private static readonly System.Text.RegularExpressions.Regex NamePrefixPattern =
        new(@"^\d{2}--\d{2}--(.+?)_", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Name for the title bar: what the template calls itself, else the file name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Info.Name)
        ? Path.GetFileNameWithoutExtension(SerpsPath)
        : Info.Name;

    /// <summary>Category folders directly under <c>ERPs/</c>, in prefix order.</summary>
    public IEnumerable<string> CategoryFolderNames
    {
        get
        {
            if (!Directory.Exists(ErpsFolder)) return Enumerable.Empty<string>();
            return Directory.EnumerateDirectories(ErpsFolder)
                            .Select(Path.GetFileName)
                            .Where(n => !string.IsNullOrEmpty(n))
                            .Select(n => n!)
                            .Order(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Every part in the template, as a path relative to <c>ERPs/</c>.</summary>
    public IEnumerable<string> AllPartPaths
    {
        get
        {
            if (!Directory.Exists(ErpsFolder)) yield break;

            foreach (var f in ErpsIn(ErpsFolder)) yield return f;

            foreach (var folder in CategoryFolderNames)
                foreach (var f in ErpsIn(Path.Combine(ErpsFolder, folder)))
                    yield return Path.Combine(folder, f);
        }
    }

    private static IEnumerable<string> ErpsIn(string dir)
    {
        if (!Directory.Exists(dir)) return Enumerable.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.erp")
                        .Select(Path.GetFileName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                        .Order(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// <c>template.serps</c> at the archive root — everything about the template
/// that isn't an ERP.
///
/// Read only. The file is never rewritten: a swap adds ERPs and changes nothing
/// this document describes, and carrying the bytes through untouched means a
/// field a newer Studio writes cannot be lost by a round trip through this app.
/// <see cref="Extra"/> exists for the same reason on the reading side.
/// </summary>
public sealed class SerpsInfo
{
    [JsonPropertyName("name")]           public string Name          { get; set; } = "";
    [JsonPropertyName("author")]         public string Author        { get; set; } = "";
    [JsonPropertyName("version")]        public string Version       { get; set; } = "";
    [JsonPropertyName("game")]           public string Game          { get; set; } = "";
    [JsonPropertyName("game_version")]   public string GameVersion   { get; set; } = "";
    [JsonPropertyName("studio_version")] public string StudioVersion { get; set; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; } = new();
}
