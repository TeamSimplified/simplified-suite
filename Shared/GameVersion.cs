using System.Diagnostics;
using System.IO;

namespace Simplified.Shared;

/// <summary>
/// Which build of the game a package was made against, read off the install.
///
/// Every app that writes a <c>mod.serps</c> stamps these two fields into it, and
/// they only mean anything if all of them spell a given build the same way. They
/// did not: Studio assembled the number from the exe's version parts while the
/// Team Editor and the Grid Editor read <c>ProductVersion</c>, which on F1 25 is
/// the comma-separated <c>1,0,141,2878</c> rather than <c>1.0.141.2878</c>. Two
/// manifests could name the same build and not compare equal, which is the whole
/// point of recording it.
///
/// So it lives here, once. The packagers that use it sit in the Core libraries
/// rather than the app projects, so this file is linked into those.
///
/// Everything here is best-effort and never throws: a missing or unreadable exe
/// leaves the field empty rather than stopping someone packaging a mod.
/// </summary>
public static class GameVersion
{
    /// <summary>
    /// The game build as <c>Major.Minor.Build.Private</c> — <c>1.0.141.2878</c>
    /// — or null when it can't be read.
    ///
    /// Built from the numeric parts on purpose. <c>ProductVersion</c> is whatever
    /// string the exe happens to carry, and this title carries commas.
    /// </summary>
    public static string? Read(string? gameDir)
    {
        try
        {
            if (Exe(gameDir) is not { } exe) return null;
            var vi = FileVersionInfo.GetVersionInfo(exe);
            return $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}.{vi.FilePrivatePart}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The game's own name for itself — its <c>ProductName</c>, verbatim.
    ///
    /// Every F1 title from 2015 on declares one, so this is the title's own
    /// spelling rather than something inferred: <c>F1 2015</c> through
    /// <c>F1 23</c>, then <c>F1® 24</c> and <c>F1® 25</c> once the mark was
    /// added. The exe's file name is only a fallback, for a build that declares
    /// no product name at all.
    /// </summary>
    public static string? ReadName(string? gameDir)
    {
        try
        {
            if (Exe(gameDir) is not { } exe) return null;

            var product = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
            return string.IsNullOrEmpty(product)
                ? Path.GetFileNameWithoutExtension(exe).Replace('_', ' ')   // F1_25.exe → F1 25
                : product;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The <c>F1_*.exe</c> at the top of the install, or null.</summary>
    private static string? Exe(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return null;
        try
        {
            return Directory.EnumerateFiles(gameDir, "F1_*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
