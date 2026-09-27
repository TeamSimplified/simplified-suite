using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace SimplifiedUi;

/// <summary>
/// Gives a code-built dialog the same frameless look as the main window: no
/// system border, our own title bar, and rounded corners.
///
/// Applied from each dialog's construction site rather than by subclassing
/// Window, because the dialogs are plain Windows built inline with object
/// initialisers — one call after the initialiser leaves them exactly as they
/// were in every other respect.
/// </summary>
public static class DialogChrome
{
    private const double CaptionHeight = 38;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Wrap <paramref name="window"/>'s content in a title bar and drop the
    /// system frame. Call it after the object initialiser and before ShowDialog:
    /// the current Content is what gets wrapped, so anything assigned later
    /// would replace the bar along with it.
    ///
    /// With <paramref name="titled"/> false there is no bar at all — for a
    /// dialog with nothing to put in one. An empty bar is not nothing: it is
    /// 38px of blank above the first line, which reads as the content having
    /// slipped down the window. The top of the window stays draggable either
    /// way, since the caption area is the chrome's and not the bar's.
    ///
    /// Left unsaid, it works that out: a window with no title and no close
    /// button has nothing to put in a bar, so it doesn't get one. Any dialog
    /// that forgets to set a Title is then short a bar rather than showing a
    /// blank one.
    /// </summary>
    public static void Apply(Window window, bool closable = true, bool? titled = null)
    {
        var showBar = titled ?? (closable || !string.IsNullOrWhiteSpace(window.Title));

        var content = window.Content as UIElement;
        // Detach first — an element can't be added to the new parent while the
        // window still holds it.
        window.Content = null;

        window.WindowStyle = WindowStyle.None;
        // A resource reference rather than a resolved brush: a dialog open while
        // the palette is swapped — Settings, where the toggle lives — would
        // otherwise keep the ground it was built with.
        window.SetResourceReference(Window.BackgroundProperty, "Surface");

        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = CaptionHeight,
            // A NoResize dialog keeps a zero border, or the frame would offer
            // resize grips the window doesn't honour.
            ResizeBorderThickness = window.ResizeMode == ResizeMode.NoResize
                ? new Thickness(0)
                : new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        if (showBar)
        {
            var bar = BuildTitleBar(window, closable);
            Grid.SetRow(bar, 0);
            root.Children.Add(bar);
        }

        if (content is not null)
        {
            Grid.SetRow(content, showBar ? 1 : 0);
            Grid.SetRowSpan(content, showBar ? 1 : 2);
            root.Children.Add(content);
        }

        window.Content = root;
        window.SourceInitialized += (_, _) => RoundCorners(window);
    }

    /// <summary>
    /// Ask the compositor to round a window's corners. Windows rounds ordinary
    /// windows itself, but one with no standard frame has to ask. Pre-Windows-11
    /// the call fails and the corners stay square.
    /// </summary>
    public static void RoundCorners(Window window)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var preference = DwmwcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch { /* older Windows — square corners are a fine outcome */ }
    }

    private static UIElement BuildTitleBar(Window window, bool closable)
    {
        var dock = new DockPanel { LastChildFill = false };

        var close = new Button
        {
            Content = "\uE8BB",   // Segoe MDL2 close
            ToolTip = "Close",
            Style = Application.Current?.TryFindResource("CloseButton") as Style,
        };
        // Closing without setting DialogResult reads as a cancel, which is what
        // the system close button did.
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Right);
        // Left out entirely when the window cannot be closed by hand. A close
        // button that refuses is worse than none: it invites the click and then
        // ignores it, which reads as the app having hung - the very thing a
        // progress window exists to disprove.
        if (closable) dock.Children.Add(close);

        // The title the Window was given, since the system no longer draws it.
        var title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimary");
        title.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(Window.Title)) { Source = window });
        DockPanel.SetDock(title, Dock.Left);
        dock.Children.Add(title);

        // No fill of its own and no rule under it: the bar shares the dialog's
        // surface so the title reads as the top of one sheet. A tinted strip with
        // a divider is the shape of a system title bar, which is what made these
        // look like old dialogs even without a system frame.
        //
        // The main window keeps its tinted bar on purpose — that one is app
        // chrome above a workspace, where a dialog is a single small surface.
        var bar = new Border
        {
            Height = CaptionHeight,
            // Left padding matches the 18px margin the dialog bodies use, so the
            // title sits on the same line as the text below it.
            Padding = new Thickness(18, 0, 6, 0),
            Child = dock,
        };
        bar.SetResourceReference(Border.BackgroundProperty, "Surface");
        return bar;
    }
}
