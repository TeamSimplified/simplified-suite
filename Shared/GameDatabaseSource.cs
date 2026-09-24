namespace Simplified.Shared;

/// <summary>Which database an app ended up reading, and why.</summary>
/// <param name="Path">The file to read.</param>
/// <param name="FromInstall">True when it came out of the game rather than the bundle.</param>
/// <param name="Note">
/// Worth saying out loud, or null when there is nothing to explain. Taking the
/// game's own copy silently is the ordinary case; every other outcome has a
/// reason somebody would want to know.
/// </param>
public sealed record GameDatabaseChoice(string Path, bool FromInstall, string? Note);

/// <summary>
/// Where the pristine game database comes from.
///
/// The apps used to read only their own bundled copy, which is never wrong about
/// being clean and goes stale the moment the game patches — a package built from
/// it carries last month's database over this month's game. The install has the
/// current one, and it is the only file packaging needs that is actually there
/// as a loose file.
///
/// So: take the game's, but only when it is a file <see cref="GameFileGuard"/>
/// recognises. A database with somebody's mod in it is exactly what must not
/// become the base of a package — that is the whole reason the bundle existed —
/// and an install patched past what this release knows cannot be told apart from
/// one, so both fall back to the bundle and say so.
/// </summary>
public static class GameDatabaseSource
{
    /// <summary>
    /// The database to build from, given the install and the copy shipped in
    /// runtime\original. Never throws and never returns a path that isn't there
    /// if either one exists.
    /// </summary>
    public static GameDatabaseChoice Resolve(string? gameDir, string bundledPath)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return new GameDatabaseChoice(bundledPath, false,
                "The game folder isn't set, so the bundled database was used. Set it in Settings to " +
                "build on your installed game's own.");

        var check = GameFileGuard.Check(gameDir, GameFileGuard.DatabasePath);

        return check.Verdict switch
        {
            GameFileVerdict.Clean =>
                new GameDatabaseChoice(check.FullPath, true, null),

            GameFileVerdict.Unrecognised =>
                new GameDatabaseChoice(bundledPath, false,
                    "Your game's database doesn't match any build this app has on record, so the bundled " +
                    "one was used instead. Either the game has updated since this release, or something " +
                    "has changed that file — a database mod already installed would otherwise have been " +
                    "built into your package."),

            GameFileVerdict.Missing =>
                new GameDatabaseChoice(bundledPath, false,
                    "There's no database under the game folder, so the bundled one was used. Is that the " +
                    "install root?"),

            // No manifest shipped with this build. Nothing can be verified, so
            // nothing changes: the bundle is what it has always been.
            _ => new GameDatabaseChoice(bundledPath, false, null),
        };
    }
}
