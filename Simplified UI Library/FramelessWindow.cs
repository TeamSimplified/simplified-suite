using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

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
///   * WindowStyle None also drops WS_CAPTION, and the shell reads that bit to
///     decide what it animates into the taskbar — so without it the window
///     vanishes on the spot. See <see cref="Simplified.Shared.MinimiseAnimation"/>
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
            Simplified.Shared.MinimiseAnimation.Enable(window);
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

            // And the floor, which is the part that is easy to miss. Answering
            // this message at all takes it away from WPF, and with it goes the
            // enforcement of MinWidth and MinHeight — the window can then be
            // dragged smaller than its own content, which does not reflow but
            // is simply cut off. What goes first is whatever sits at an edge:
            // the caption buttons, and the bar along the bottom.
            if (MinTrackSize(hwnd) is { } floor) mmi.MinTrackSize = floor;

            Marshal.StructureToPtr(mmi, lParam, fDeleteOld: true);
            handled = true;
        }
        catch
        {
            // Leave the default sizing rather than fail the message.
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The window's own <see cref="FrameworkElement.MinWidth"/> and
    /// <see cref="FrameworkElement.MinHeight"/>, in the device pixels this
    /// message speaks in. Null when the window sets neither.
    /// </summary>
    private static Point32? MinTrackSize(IntPtr hwnd)
    {
        if (HwndSource.FromHwnd(hwnd) is not { RootVisual: Window window } source) return null;
        if (double.IsNaN(window.MinWidth) || double.IsNaN(window.MinHeight)) return null;
        if (window.MinWidth <= 0 && window.MinHeight <= 0) return null;

        // Device-independent up there, device pixels down here, and the two are
        // only the same at 100%.
        var scale = source.CompositionTarget?.TransformToDevice ?? Matrix.Identity;

        return new Point32
        {
            X = (int)Math.Ceiling(window.MinWidth * scale.M11),
            Y = (int)Math.Ceiling(window.MinHeight * scale.M22),
        };
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
