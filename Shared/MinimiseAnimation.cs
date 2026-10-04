using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Simplified.Shared;

/// <summary>
/// Gives a window without a title bar the minimise animation back.
///
/// Every app here draws its own chrome, which means WindowStyle None, which
/// means WPF drops WS_CAPTION. The shell reads that bit to decide whether a
/// window is the sort of thing it animates into the taskbar, so without it the
/// window vanishes on the spot while everything else on the desktop shrinks.
/// WS_MINIMIZEBOX on its own is not enough — measured, and it is not.
///
/// Putting the caption back costs nothing visible as long as the window also
/// says it has no non-client area to draw. WindowChrome already does exactly
/// that, so an app using it needs only the bit; a window without it gets a
/// WM_NCCALCSIZE hook here instead.
/// </summary>
public static class MinimiseAnimation
{
    /// <summary>
    /// Turn it on for one window. Safe to call from a constructor: it waits for
    /// the handle if there isn't one yet.
    /// </summary>
    public static void Enable(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(window);
        else window.SourceInitialized += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            SetWindowLong(handle, GwlStyle, GetWindowLong(handle, GwlStyle) | WsCaption | WsMinimiseBox);

            // WindowChrome answers WM_NCCALCSIZE itself, and two answers is one
            // too many. Only a window without it needs ours.
            if (WindowChrome.GetWindowChrome(window) is null)
                HwndSource.FromHwnd(handle)?.AddHook(NoNonClientArea);
        }
        catch
        {
            // A window that will not take the style still works; it just does
            // not animate, which is where it started.
        }
    }

    /// <summary>
    /// "None of me is border or title bar." Answering WM_NCCALCSIZE this way
    /// leaves the whole window as client area, so the caption bought above is
    /// never drawn.
    /// </summary>
    private static IntPtr NoNonClientArea(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmNcCalcSize || wParam == IntPtr.Zero) return IntPtr.Zero;

        handled = true;
        return IntPtr.Zero;
    }

    private const int GwlStyle = -16;
    private const int WsMinimiseBox = 0x00020000;
    private const int WsCaption = 0x00C00000;
    private const int WmNcCalcSize = 0x0083;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
