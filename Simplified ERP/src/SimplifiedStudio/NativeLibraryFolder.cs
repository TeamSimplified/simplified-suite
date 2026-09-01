using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace SimplifiedStudio;

/// <summary>
/// Lets the app's native libraries live in a <c>runtime</c> folder beside the
/// exe instead of loose next to it.
///
/// Background: bundling them inside the single-file exe means the .NET host
/// extracts them to %TEMP%\.net at startup — outside the app folder, once per
/// build, and never cleaned up. So they are published as files instead
/// (IncludeNativeLibrariesForSelfExtract is off), and tidied into a subfolder,
/// which texconv.exe shares.
///
/// Windows will not find them there by itself, and neither will .NET: the
/// search covers the exe's own folder, not folders under it. ResolvingUnmanagedDll
/// is the hook for that — it fires only after the normal search has failed, so
/// this changes nothing about how anything else loads.
///
/// SetDllDirectory alone is not enough; it was tried first. .NET resolves a
/// DllImport through its own probing rather than a bare LoadLibrary, so the
/// process-wide search path never gets consulted and WPF dies with a
/// DllNotFoundException before the first window.
///
/// A module initializer is what makes the timing work — it runs when this
/// assembly is loaded, before Main and so before WPF asks for anything.
/// </summary>
internal static class NativeLibraryFolder
{
    internal const string FolderName = "runtime";

    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, FolderName);

        // Absent in a normal build, where the libraries sit beside the exe and
        // the default search finds them. Only a published layout has this.
        if (!Directory.Exists(dir)) return;

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            var path = Path.Combine(dir, Path.HasExtension(name) ? name : name + ".dll");

            // Loading by full path also makes Windows look in that same folder
            // for whatever the library itself depends on — which is how
            // vcruntime140_cor3.dll gets found, nothing importing it directly.
            return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
        };

        // And the case the resolver cannot see: one native library loading
        // another itself, later, by name — wpfgfx reaching for D3DCompiler when
        // something first needs a shader. That is a plain LoadLibrary from
        // native code, so it never reaches .NET, and the folder has to be on
        // the process search path for it to be found at all.
        try { SetDllDirectory(dir); } catch { /* the resolver still covers the rest */ }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);
}
