namespace SimplifiedUpdater.Core;

/// <summary>
/// What the files a mod replaces are called, and how they are recognised.
///
/// Paths are matched by shape rather than in full. A mod is laid out however
/// its author laid it out — some root the zip at <c>F1 25\</c>, some wrap that
/// in another folder, some put the whole thing under the mod's own name — and a
/// path that has to match exactly would turn every one of those into "this mod
/// contains nothing".
/// </summary>
public static class GameFiles
{
    /// <summary>The install folder's own name, as the launcher and the suite use it.</summary>
    public const string GameFolderName = "F1 25";

    /// <summary>
    /// Holds the shader archives a mod's own resources are merged into. Also
    /// what tells a real install apart from a folder that merely shares a name.
    /// </summary>
    public const string ShaderFolderName = "shader_package_2025";

    /// <summary>
    /// The folder the language files live in, inside old\ and new\.
    ///
    /// The game does not ship them loose — they arrive through the launcher —
    /// which is why the two reference copies are kept beside the exe rather
    /// than read out of the install.
    /// </summary>
    public const string LanguageFolderName = "localisation";

    /// <summary>
    /// True for the game database — <c>gamedatabases.2025.bin</c> — wherever in
    /// a mod it sits. Matched on the file name rather than the folder above it,
    /// since that folder is <c>gamedatabases</c> in a package and the root of
    /// old\ or new\ here.
    /// </summary>
    public static bool IsDatabase(string path) =>
        Path.GetFileName(path).StartsWith("gamedatabases", StringComparison.OrdinalIgnoreCase)
        && Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a language file.</summary>
    public static bool IsLanguage(string path) =>
        Path.GetExtension(path).Equals(".lng", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for an archive inside the shader package folder. The folder has to
    /// be in the path: a mod can carry ERPs of its own elsewhere — a livery
    /// package, a character package — and those are none of this app's business.
    /// </summary>
    public static bool IsShaderArchive(string path) =>
        Path.GetExtension(path).Equals(".erp", StringComparison.OrdinalIgnoreCase)
        && Segments(path).Any(s => s.Equals(ShaderFolderName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A path's parts, however it was spelled — zip entries use /, Windows uses \.
    ///
    /// The separators are an explicit array, not two char arguments: written as
    /// <c>Split('/', '\\', options)</c> the compiler binds the second separator
    /// to something else and quietly splits on the first alone, which makes
    /// every Windows-spelled path one long segment and every shader archive
    /// invisible.
    /// </summary>
    public static string[] Segments(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}
