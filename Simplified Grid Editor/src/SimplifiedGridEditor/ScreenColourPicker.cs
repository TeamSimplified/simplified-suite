using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SimplifiedGridEditor;

/// <summary>
/// Taking a colour off the screen.
///
/// A team colour is usually already somewhere in front of you — a livery
/// screenshot, a brand page, the game itself — and reading it out by hand
/// means a round trip through an image editor.
///
/// The screen is grabbed once and that still picture is what gets shown and
/// sampled, rather than reading pixels live underneath a transparent window.
/// Live reading has to see through whatever the overlay itself is drawing,
/// which the compositor does not promise; a frozen copy means what is sampled
/// is exactly what is under the cursor on screen.
/// </summary>
public static class ScreenColourPicker
{
    /// <summary>How many screen pixels the loupe shows across, and how big it draws them.</summary>
    private const int LoupeCells = 15;
    private const double LoupeSize = 150;

    private sealed record Capture(int Left, int Top, int Width, int Height, byte[] Pixels)
    {
        /// <summary>The pixel at a virtual-screen point, clamped to the edges.</summary>
        public Color At(int x, int y)
        {
            x = Math.Clamp(x, 0, Width - 1);
            y = Math.Clamp(y, 0, Height - 1);
            var i = (y * Width + x) * 4;
            return Color.FromRgb(Pixels[i + 2], Pixels[i + 1], Pixels[i]);
        }
    }

    /// <summary>The colour picked off the screen, or null if cancelled.</summary>
    public static Color? Pick(Window owner)
    {
        // The dialog asking for the colour is usually sitting over the thing
        // worth sampling, so it steps out of the way before the grab.
        //
        // Moved by its window handle rather than by Visibility. Hiding a
        // window that was shown with ShowDialog, and then showing another
        // dialog while it is hidden, ends the first one's modal loop: its
        // ShowDialog returns then and there, and what comes back is an
        // ordinary window whose DialogResult can no longer be set. Setting it
        // throws, which took the app down on the Set button after a pick.
        var handle = new WindowInteropHelper(owner).Handle;
        var hidden = handle != IntPtr.Zero && owner.IsVisible;
        if (hidden) ShowWindow(handle, SW_HIDE);

        try
        {
            SettleScreen();
            var capture = Grab();
            return capture is null ? null : Choose(capture);
        }
        finally
        {
            if (hidden) ShowWindow(handle, SW_SHOW);
        }
    }

    /// <summary>
    /// Give the window we just hid time to actually leave the screen.
    ///
    /// Composition is not finished when Hide returns, and a grab taken too
    /// early catches the dialog still there. Blocking the UI thread for a
    /// frame or two is invisible next to a screen that is about to freeze.
    /// </summary>
    private static void SettleScreen()
    {
        for (var i = 0; i < 2; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    private static Color? Choose(Capture capture)
    {
        var screen = BitmapSource.Create(capture.Width, capture.Height, 96, 96,
            PixelFormats.Bgr32, null, capture.Pixels, capture.Width * 4);
        screen.Freeze();

        var backdrop = new Image { Source = screen, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(backdrop, BitmapScalingMode.NearestNeighbor);

        var zoom = new Image { Width = LoupeSize, Height = LoupeSize, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(zoom, BitmapScalingMode.NearestNeighbor);

        // A box the size of one magnified pixel, over the one being read.
        var cell = new Rectangle
        {
            Width = LoupeSize / LoupeCells,
            Height = LoupeSize / LoupeCells,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var zoomBox = new Grid { Width = LoupeSize, Height = LoupeSize };
        zoomBox.Children.Add(zoom);
        zoomBox.Children.Add(cell);

        var swatch = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
        };
        var readout = new TextBlock
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas, Cascadia Mono"),
        };

        var caption = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };
        caption.Children.Add(swatch);
        caption.Children.Add(readout);

        var stack = new StackPanel();
        stack.Children.Add(zoomBox);
        stack.Children.Add(caption);

        // Its own colours rather than the app's: this floats over whatever
        // happens to be on screen, so it has to read against anything.
        var loupe = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(235, 20, 20, 22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            Child = stack,
            IsHitTestVisible = false,
        };

        var canvas = new Canvas();
        canvas.Children.Add(loupe);

        var root = new Grid();
        root.Children.Add(backdrop);
        root.Children.Add(canvas);

        var overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Background = Brushes.Black,
            Cursor = Cursors.Cross,
            WindowStartupLocation = WindowStartupLocation.Manual,
            // The window is placed in device-independent units while the grab
            // is in real pixels. Every reading below comes from the cursor in
            // pixels instead, so a mixed-DPI desktop can shift what is drawn
            // without shifting which pixel is picked.
            Left = SystemParameters.VirtualScreenLeft,
            Top = SystemParameters.VirtualScreenTop,
            Width = SystemParameters.VirtualScreenWidth,
            Height = SystemParameters.VirtualScreenHeight,
            Content = root,
        };

        Color? picked = null;
        var current = capture.At(0, 0);

        void Track()
        {
            if (!GetCursorPos(out var p)) return;

            var x = p.X - capture.Left;
            var y = p.Y - capture.Top;
            current = capture.At(x, y);

            // Built a cell at a time rather than cropped out of the screen, so
            // the reading stays in the middle of the loupe at the edges of the
            // screen instead of sliding off centre.
            var cells = new byte[LoupeCells * LoupeCells * 4];
            for (var dy = 0; dy < LoupeCells; dy++)
            for (var dx = 0; dx < LoupeCells; dx++)
            {
                var c = capture.At(x + dx - LoupeCells / 2, y + dy - LoupeCells / 2);
                var i = (dy * LoupeCells + dx) * 4;
                cells[i] = c.B;
                cells[i + 1] = c.G;
                cells[i + 2] = c.R;
                cells[i + 3] = 255;
            }

            zoom.Source = BitmapSource.Create(LoupeCells, LoupeCells, 96, 96,
                PixelFormats.Bgra32, null, cells, LoupeCells * 4);
            swatch.Background = new SolidColorBrush(current);
            readout.Text = $"#{current.R:X2}{current.G:X2}{current.B:X2}";
        }

        void Follow(Point at)
        {
            var width = loupe.ActualWidth > 0 ? loupe.ActualWidth : LoupeSize + 18;
            var height = loupe.ActualHeight > 0 ? loupe.ActualHeight : LoupeSize + 56;

            // Ahead of the cursor, and behind it near the far edges so the
            // loupe never runs off the screen it is describing.
            var left = at.X + 24;
            var top = at.Y + 24;
            if (left + width > canvas.ActualWidth) left = at.X - 24 - width;
            if (top + height > canvas.ActualHeight) top = at.Y - 24 - height;

            Canvas.SetLeft(loupe, Math.Max(0, left));
            Canvas.SetTop(loupe, Math.Max(0, top));
        }

        root.MouseMove += (_, e) =>
        {
            Track();
            Follow(e.GetPosition(canvas));
        };

        root.MouseLeftButtonDown += (_, _) =>
        {
            Track();
            picked = current;
            overlay.Close();
        };

        // Either of the two ways out of something you did not mean to start.
        root.MouseRightButtonDown += (_, _) => overlay.Close();
        overlay.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape) overlay.Close();
        };

        overlay.Loaded += (_, _) =>
        {
            overlay.Focus();
            Track();
            Follow(Mouse.GetPosition(canvas));
        };

        overlay.ShowDialog();
        return picked;
    }

    // ──────────────────────────── The grab ────────────────────────────

    /// <summary>
    /// Every screen at once, in real pixels, top row first.
    ///
    /// The whole virtual desktop rather than one monitor: the colour worth
    /// having is as likely to be on the second screen as the first.
    /// </summary>
    private static Capture? Grab()
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0) return null;

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;

        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = IntPtr.Zero;

        try
        {
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) return null;

            previous = SelectObject(memory, bitmap);
            // CAPTUREBLT so layered windows — tooltips, anything translucent —
            // are in the picture rather than showing as holes.
            if (!BitBlt(memory, 0, 0, width, height, screen, left, top, SRCCOPY | CAPTUREBLT))
                return null;

            // The bitmap has to be out of the DC before its bits can be read.
            SelectObject(memory, previous);
            previous = IntPtr.Zero;

            var info = new BitmapInfo();
            info.Header.Size = Marshal.SizeOf<BitmapInfoHeader>();
            info.Header.Width = width;
            info.Header.Height = -height;   // negative: top row first
            info.Header.Planes = 1;
            info.Header.BitCount = 32;
            info.Header.Compression = BI_RGB;

            var pixels = new byte[width * height * 4];
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, DIB_RGB_COLORS) == 0)
                return null;

            return new Capture(left, top, width, height, pixels);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ───────────────────────────── Win32 ──────────────────────────────

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    /// <summary>Header plus the one colour entry the struct is defined to carry.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public int FirstColour;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines,
        byte[] bits, ref BitmapInfo info, uint usage);
}
