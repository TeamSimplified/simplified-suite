using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SimplifiedStudio.Notes;

/// <summary>
/// Whether PureRef is on this machine, and where to get it.
///
/// Boards are its file format and it is the only thing that can open one, so a
/// machine without it can keep boards in a project and do nothing with them.
/// Worth saying plainly rather than letting a click fail: it is free, and the
/// alternative to explaining that is a shell error nobody can act on.
/// </summary>
internal static class PureRef
{
    public const string Website = "https://www.pureref.com/";

    /// <summary>
    /// Whether a board can be opened.
    ///
    /// Asked of the registry rather than a hardcoded folder, because the
    /// association is what actually opens the file — someone who installed it
    /// somewhere unusual still has it. The known folders are only a fallback
    /// for an install whose association another program has taken over.
    /// </summary>
    public static bool IsInstalled => FromAssociation() is not null || FromKnownFolders() is not null;

    /// <summary>
    /// Which of these boards PureRef appears to have open.
    ///
    /// Read off its window titles, which carry the whole path —
    /// <c>References - PureRef - C:\…\Boards\References.pur</c>. There is no
    /// lock to test for: PureRef leaves the file readable while it has it open,
    /// so a project could be closed, its working folder deleted, and the next
    /// save in PureRef would write into nothing.
    ///
    /// Matched on the file name rather than the full path, because the title
    /// shows the path as it was opened and a temp folder can reach the same
    /// file by more than one spelling. That can name a board of the same name
    /// belonging to something else — worth it, since the cost of a false
    /// warning is a sentence to read and the cost of missing one is lost work.
    /// </summary>
    public static IReadOnlyList<string> OpenBoards(IEnumerable<string> boardPaths)
    {
        string[] titles;
        try
        {
            titles = Process.GetProcessesByName("PureRef")
                .Select(p => { try { return p.MainWindowTitle; } catch { return ""; } })
                .Where(t => t.Length > 0)
                .ToArray();
        }
        catch
        {
            return [];   // can't see other processes; don't block the user over it
        }

        if (titles.Length == 0) return [];

        return boardPaths
            .Where(path => titles.Any(t => t.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
    }

    /// <summary>Send the user to the download page.</summary>
    public static void OpenWebsite()
    {
        try { Process.Start(new ProcessStartInfo(Website) { UseShellExecute = true }); }
        catch { /* no browser — nothing useful to do about it */ }
    }

    /// <summary>The exe registered to open a .pur, if it is still there.</summary>
    private static string? FromAssociation()
    {
        try
        {
            using var extension = Registry.ClassesRoot.OpenSubKey(".pur");
            if (extension?.GetValue(null) is not string progId || progId.Length == 0) return null;

            using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            if (command?.GetValue(null) is not string line || line.Length == 0) return null;

            return ProgramFrom(line);
        }
        catch
        {
            return null;   // no access to the hive, or it's shaped differently
        }
    }

    /// <summary>
    /// The program out of a shell open command.
    ///
    /// Cutting at the first space is the obvious way and the wrong one: PureRef
    /// registers itself unquoted as
    /// <c>C:\Program Files\PureRef\PureRef.exe "%1"</c>, which that reads as
    /// <c>C:\Program</c>. Cutting after the <c>.exe</c> handles both that and
    /// the quoted form, and doesn't care what arguments follow.
    /// </summary>
    private static string? ProgramFrom(string command)
    {
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        var path = exe >= 0
            ? command[..(exe + 4)].Trim().Trim('"')
            : command.Trim().Trim('"');

        return path.Length > 0 && File.Exists(path) ? path : null;
    }

    private static string? FromKnownFolders()
    {
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                 })
        {
            if (string.IsNullOrEmpty(folder)) continue;

            var candidate = Path.Combine(folder, "PureRef", "PureRef.exe");
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* unreadable path — try the next */ }
        }

        return null;
    }
}
