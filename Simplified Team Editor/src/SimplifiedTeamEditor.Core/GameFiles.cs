namespace SimplifiedTeamEditor.Core;

/// <summary>
/// Where things sit inside an F1 25 install, and inside a package built for it.
///
/// These names are the game's, not ours, and several unrelated parts of the app
/// need the same ones — detecting the install, reading the shader packages,
/// laying out a package zip — so they are stated once here.
/// </summary>
public static class GameFiles
{
    /// <summary>The install folder's own name, as the launcher and Studio use it.</summary>
    public const string GameFolderName = "F1 25";

    /// <summary>
    /// Holds the UI shader packages a logo edit merges into. Also what tells a
    /// real install apart from a folder that merely shares the name.
    /// </summary>
    public const string ShaderFolderName = "shader_package_2025";

    /// <summary>
    /// The two GPU variants of the UI texture package. Team logos live in
    /// texarrays inside these, so a logo edit has to be merged into both.
    /// </summary>
    public static readonly string[] ShaderErpNames =
    {
        "ui_texture.nefx2.sm51.erp",
        "ui_texture.nefx2.sm51_amd.erp",
    };

    /// <summary>The team colour database, relative to the install root.</summary>
    public static readonly string[] GameDatabaseRelPath =
    {
        "game_package", "gamedatabases", "gamedatabases.2025.bin",
    };

    /// <summary>
    /// Where language files live inside a package. Note the spelling: the
    /// folder really is "localisatioa" in the path a package uses — it is
    /// copied from what the launcher writes, and changing it would produce a
    /// package the game ignores.
    /// </summary>
    public static readonly string[] PackageLocalisationPath =
    {
        GameFolderName, "localisatioa", "2025_release",
    };
}
