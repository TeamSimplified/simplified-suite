using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SimplifiedStudio.Util;

/// <summary>
/// Giving the app's own file types their icon in Explorer, and opening one by
/// double-clicking it.
///
/// This is the one thing in the app that writes outside its own folder, and it
/// cannot be otherwise: Explorer learns about a file type from the registry and
/// from nowhere else. So it is off until asked for, it writes only under
/// HKEY_CURRENT_USER — no administrator rights, no effect on other accounts —
/// and the same switch removes every key it created.
///
/// Being registered is also self-correcting. The registry records an absolute
/// path to this exe and its icons; a portable app gets moved, and the
/// association would then point at nothing. <see cref="RepairIfStale"/> puts it
/// right at startup, writing only when the recorded path has actually changed.
/// </summary>
public static class FileAssociation
{
    /// <summary>
    /// One registered file type. Adding another means adding a row here and
    /// shipping its icon — there is no code to change.
    /// </summary>
    public sealed record Kind(string Extension, string ProgId, string FriendlyName, string IconFile);

    /// <summary>
    /// What the app claims. A project is a document to be opened; a template is
    /// the thing a new project is started from, and the window tells them apart
    /// by extension.
    /// </summary>
    public static readonly Kind[] Kinds =
    {
        new(".simplified", "SimplifiedStudio.Project.1",  "Simplified Studio project",  "project.ico"),
        new(".serps",      "SimplifiedStudio.Template.1", "Simplified Studio template", "serps.ico"),
    };

    private const string AppName = "Simplified Studio";
    private const string Classes = @"Software\Classes\";

    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>
    /// The document icons ship in runtime/, with the native libraries, so the
    /// app folder stays down to the exe. The registry needs the absolute path,
    /// which is why RepairIfStale checks this as well as the exe.
    /// </summary>
    private static string IconPath(Kind kind) =>
        Path.Combine(AppContext.BaseDirectory, "runtime", kind.IconFile);

    /// <summary>True when every type this app claims currently points at it.</summary>
    public static bool IsRegistered
    {
        get
        {
            try
            {
                foreach (var kind in Kinds)
                {
                    using var ext = Registry.CurrentUser.OpenSubKey(Classes + kind.Extension);
                    if (ext?.GetValue(null) as string != kind.ProgId) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>A sentence naming the extensions, for the Settings label.</summary>
    public static string ExtensionList =>
        string.Join(" and ", Kinds.Select(k => k.Extension));

    /// <summary>
    /// Point every type at this copy of the app. Safe to call when already
    /// registered — it rewrites the paths, which is what a moved folder needs.
    /// </summary>
    public static void Register()
    {
        foreach (var kind in Kinds)
        {
            using (var ext = Registry.CurrentUser.CreateSubKey(Classes + kind.Extension))
            {
                ext.SetValue(null, kind.ProgId);
                // Puts the app in Explorer's "Open with" list even if something
                // else takes the default later.
                using var progids = ext.CreateSubKey("OpenWithProgids");
                progids.SetValue(kind.ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            using var type = Registry.CurrentUser.CreateSubKey(Classes + kind.ProgId);
            type.SetValue(null, kind.FriendlyName);

            // What Explorer prints in the Type column. Without it Windows
            // invents something from the extension and the ProgId, which is how
            // "SimplifiedStudio" turned up instead of the app's real name.
            type.SetValue("FriendlyTypeName", kind.FriendlyName);

            using (var icon = type.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, "\"" + IconPath(kind) + "\",0");

            using (var open = type.CreateSubKey(@"shell\open"))
            {
                // The name shown wherever Windows offers the app — Open with,
                // the Properties dialog. It falls back to guessing without this.
                open.SetValue("FriendlyAppName", AppName);
                using var command = open.CreateSubKey("command");
                command.SetValue(null, "\"" + ExePath + "\" \"%1\"");
            }

            ClearShellUserChoice(kind.Extension);
        }

        NotifyShell();
    }

    /// <summary>
    /// The types Windows will not let this app claim, with the application it
    /// has pinned each one to.
    ///
    /// Choosing "always open with" writes a UserChoice key that Windows
    /// protects against deletion, on purpose — it is what stops an installer
    /// quietly stealing a file type. So the registration is correct and simply
    /// outranked, and only the person at the machine can undo it. Saying so
    /// beats appearing to work and doing nothing.
    /// </summary>
    public static IReadOnlyList<(Kind Kind, string PinnedTo)> BlockedByWindows()
    {
        var blocked = new List<(Kind, string)>();
        foreach (var kind in Kinds)
        {
            try
            {
                using var choice = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\"
                    + kind.Extension + @"\UserChoice");

                if (choice?.GetValue("ProgId") is not string pinned || pinned == kind.ProgId) continue;

                // Applications\notepad++.exe reads better as Notepad++.
                var name = pinned.StartsWith(@"Applications\", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(pinned)
                    : pinned;
                blocked.Add((kind, name));
            }
            catch
            {
                // Unreadable is not the same as blocked; say nothing.
            }
        }
        return blocked;
    }

    /// <summary>
    /// Drop Explorer's remembered "always open with" for an extension, so the
    /// registration under Classes is what decides again.
    ///
    /// These keys belong to the signed-in user and Windows recreates them the
    /// next time anyone chooses an app, so removing them takes nothing away
    /// that cannot come straight back. UserChoice carries a hash Windows
    /// verifies and refuses to let anything forge — which is why this clears
    /// the key rather than trying to write it.
    /// </summary>
    private static void ClearShellUserChoice(string extension)
    {
        var fileExts =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension;

        foreach (var name in new[] { "UserChoice", "UserChoiceLatest" })
        {
            // UserChoice is ACL-protected against deletion and will refuse —
            // deliberately, so no app can take a type the user assigned by
            // hand. UserChoiceLatest, which Windows 11 writes alongside it,
            // does clear. BlockedByWindows reports whatever is left.
            try { Registry.CurrentUser.DeleteSubKeyTree(fileExts + @"\" + name, throwOnMissingSubKey: false); }
            catch { }
        }

        // The generic type Windows invents for an unknown extension carries no
        // icon, and shows up in Open with as a second, iconless entry.
        try
        {
            using var progids = Registry.CurrentUser.OpenSubKey(fileExts + @"\OpenWithProgids", writable: true);
            foreach (var stale in progids?.GetValueNames() ?? Array.Empty<string>())
                if (stale.EndsWith("_auto_file", StringComparison.OrdinalIgnoreCase))
                    progids.DeleteValue(stale, throwOnMissingValue: false);
        }
        catch { }
    }

    /// <summary>
    /// Remove every key this created. An extension key only goes if it still
    /// points here — if something else has since claimed one, taking it away
    /// would break that instead.
    /// </summary>
    public static void Unregister()
    {
        foreach (var kind in Kinds)
        {
            try
            {
                using (var ext = Registry.CurrentUser.OpenSubKey(Classes + kind.Extension))
                {
                    if (ext?.GetValue(null) as string == kind.ProgId)
                        Registry.CurrentUser.DeleteSubKeyTree(Classes + kind.Extension, throwOnMissingSubKey: false);
                }

                Registry.CurrentUser.DeleteSubKeyTree(Classes + kind.ProgId, throwOnMissingSubKey: false);
            }
            catch
            {
                // A key already gone is the outcome wanted anyway.
            }
        }

        NotifyShell();
    }

    /// <summary>
    /// Re-point the association at where the app is now, if it is registered
    /// and the recorded path no longer matches. Does nothing otherwise, so an
    /// ordinary startup writes nothing at all.
    /// </summary>
    public static void RepairIfStale()
    {
        try
        {
            if (!IsRegistered) return;

            foreach (var kind in Kinds)
            {
                using var command = Registry.CurrentUser.OpenSubKey(
                    Classes + kind.ProgId + @"\shell\open\command");
                var recorded = command?.GetValue(null) as string ?? "";
                if (!recorded.Contains(ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    Register();     // rewrites every type, which is what a move needs
                    return;
                }

                // The icon can move without the exe moving - it did, when these
                // were tidied into runtime/. Checking only the exe would leave
                // everyone already registered pointing at a file that had gone.
                using var icon = Registry.CurrentUser.OpenSubKey(
                    Classes + kind.ProgId + @"\DefaultIcon");
                var recordedIcon = icon?.GetValue(null) as string ?? "";
                if (!recordedIcon.Contains(IconPath(kind), StringComparison.OrdinalIgnoreCase))
                {
                    Register();
                    return;
                }
            }
        }
        catch
        {
            // Registry trouble is not a reason to refuse to start.
        }
    }

    /// <summary>
    /// Tell Explorer associations changed. Without this the icon only appears
    /// after a sign-out, which reads as the feature not working.
    /// </summary>
    private static void NotifyShell()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch { /* cosmetic only — the keys are written either way */ }
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
