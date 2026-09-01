using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace SimplifiedUi;

/// <summary>
/// The native half of a window that draws its own title bar.
///
/// <c>WindowStyle="None"</c> with a <c>WindowChrome</c> keeps the real resize
/// borders, snapping and Aero Snap layouts, which <c>AllowsTransparency</c>
/// would throw away. Two things still have to be done in code, and neither is
/// obvious enough to leave each app to rediscover:
///
///   * a maximised chrome window covers the taskbar unless WM_GETMINMAXINFO is
///     answered with the monitor's work area
///   * the maximise glyph has to follow the window state rather than the click,
///     because double-clicking the bar maximises too
///
/// Studio and ERP carry this inline, from before there was anywhere shared to
/// put it. The swappers use this.
/// </summary>
public static class FramelessWindow
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    /// <summary>
    /// Hook <paramref name="window"/> up: rounded corners, the taskbar-aware
    /// maximise, and - when a maximise button is given - a glyph that keeps
    /// saying what the button will do.
    /// </summary>
    public static void Attach(Window window, Button? maximizeButton = null)
    {
        window.SourceInitialized += (_, _) =>
        {
            DialogChrome.RoundCorners(window);
            if (PresentationSource.FromVisual(window) is HwndSource source) source.AddHook(WindowProc);
        };

        if (maximizeButton is null) return;

        void Follow()
        {
            var maximized = window.WindowState == WindowState.Maximized;
            // Escapes rather than pasted glyphs: these are Segoe MDL2
            // private-use codepoints, which render as tofu in most editors and
            // do not survive every re-encode.
            maximizeButton.Content = maximized ? "\uE923" : "\uE922";
            maximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        }

        window.StateChanged += (_, _) => Follow();
        Follow();
    }

    /// <summary>Maximise, or come back down if it already is.</summary>
    public static void ToggleMaximize(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;

        try
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return IntPtr.Zero;

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);

            // Position and size are relative to the monitor, not the desktop,
            // which is why the monitor rect is subtracted — on a secondary
            // screen the work area's absolute origin is not zero.
            mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left;
            mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
            mmi.MaxSize.X = info.Work.Right - info.Work.Left;
            mmi.MaxSize.Y = info.Work.Bottom - info.Work.Top;
            mmi.MaxTrackSize = mmi.MaxSize;

            Marshal.StructureToPtr(mmi, lParam, fDeleteOld: true);
            handled = true;
        }
        catch
        {
            // Leave the default sizing rather than fail the message.
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point32 Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
