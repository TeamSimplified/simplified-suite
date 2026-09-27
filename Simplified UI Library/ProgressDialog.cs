using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SimplifiedUi;

/// <summary>
/// The suite's "this is taking a moment" window: a headline, a line that says
/// what is happening now, and a bar that shows the app has not died.
///
/// It exists because the alternative was a frozen window. Opening a project or
/// starting one can mean unpacking a few hundred megabytes, or taking the
/// language files out of the game — which, the first time on a new game build,
/// is half a minute of a window that does not repaint and a person wondering
/// whether to kill it. Every one of those is quick enough on the second run to
/// have been easy to miss while building it, and slow enough on the first to
/// look like a crash.
///
/// The work runs on a background thread and reports through
/// <see cref="IProgress{T}"/>; this window owns the dialog and closes itself
/// when the work finishes. Exceptions come back to the caller as they would
/// have without it, so the call sites keep their own error handling.
///
/// Wording is the caller's, and it follows one shape wherever this window is
/// used: the headline is what was asked for — "Opening Alpine 2026",
/// "Updating game files" — and the line under it is the step running now,
/// ending in an ellipsis because it is still going. A step says what it is
/// doing for the person waiting rather than what it is doing to the disk.
///
/// There is no title bar. A caption saying "New project" over a headline saying
/// what is being started is the same sentence twice, and an empty bar left in
/// its place is a gap above the first line.
/// </summary>
public static class ProgressDialog
{
    /// <summary>
    /// How long the window stays invisible before it shows itself.
    ///
    /// Long enough that work finishing immediately never flashes a window,
    /// short enough not to read as a pause. It is not a threshold to decide
    /// whether to show one — that decision blocks — only how long to wait
    /// before it can be seen.
    /// </summary>
    private static readonly TimeSpan RevealAfter = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Run <paramref name="work"/> behind a modal progress window, and hand
    /// back what it returned.
    ///
    /// The window appears only once the work has been going a moment: most runs
    /// finish in well under a second, and a dialog that flashes up and vanishes
    /// is worse than no dialog at all.
    /// </summary>
    public static T Run<T>(Window? owner, string headline, Func<IProgress<string>, T> work)
    {
        var dispatcher = owner?.Dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher is null) return work(new Progress<string>(_ => { }));

        var status = new TextBlock
        {
            Text = "Starting…",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Themed("TextMuted", Brushes.Gray),
            Margin = new Thickness(0, 0, 0, 12),
        };

        var window = Build(owner, headline, status);

        // Marshalled through the window's own dispatcher rather than through
        // Progress<T>, which captures whatever SynchronizationContext happened
        // to be current when it was built and posts to the thread pool when
        // there wasn't one. That is a UI element touched from a background
        // thread, and it throws - found by a harness that had no dispatcher
        // context on its main thread, which is exactly the case that would have
        // been missed until some caller hit it in the field.
        IProgress<string> progress = new DispatcherProgress(window.Dispatcher, line =>
        {
            if (!string.IsNullOrWhiteSpace(line)) status.Text = line;
        });

        Exception? failure = null;
        var answer = default(T);

        var task = Task.Run(() =>
        {
            try { answer = work(progress); }
            catch (Exception ex) { failure = ex; }
        });

        // The window goes up now, and the calling thread is not made to wait to
        // find out whether it is needed.
        //
        // It used to block here for a quarter of a second, so that work which
        // finished at once never flashed a window. That quarter second was the
        // UI thread: the app stopped repainting the moment the button was
        // pressed, which is the exact complaint this dialog exists to answer,
        // and it was most visible where it mattered most — packaging, where
        // what follows the press is minutes of work.
        //
        // So the window is shown straight away and left transparent for a
        // moment instead. Quick work still closes it before anyone sees it,
        // and the app carries on drawing throughout.
        window.Opacity = 0;
        var reveal = new System.Windows.Threading.DispatcherTimer(
            RevealAfter, System.Windows.Threading.DispatcherPriority.Normal,
            (_, _) => window.Opacity = 1, window.Dispatcher);

        // Closed by the work finishing, not by the person: there is nothing to
        // cancel here, and a dialog that can be dismissed while the thing it
        // describes carries on is a lie.
        var finished = false;
        task.ContinueWith(_ => window.Dispatcher.BeginInvoke(() =>
        {
            finished = true;
            reveal.Stop();
            // Only if it got as far as being shown. Work that finished inside
            // the first frame leaves a window that was never opened, and
            // closing one of those throws.
            if (!window.IsVisible) return;
            window.Closing -= Refuse;
            window.Close();
        }));

        window.Closing += Refuse;
        // Both this check and the line that sets it run on the UI thread, so
        // nothing can slip between them; a report that is already queued runs
        // inside ShowDialog's own message loop and closes the window there.
        if (!finished) window.ShowDialog();

        reveal.Stop();

        if (failure is not null) throw Rethrow(failure);
        return answer!;

        static void Refuse(object? sender, System.ComponentModel.CancelEventArgs e) => e.Cancel = true;
    }

    /// <summary>The same, for work with nothing to hand back.</summary>
    public static void Run(Window? owner, string headline, Action<IProgress<string>> work) =>
        Run<object?>(owner, headline, report => { work(report); return null; });

    private static Window Build(Window? owner, string headline, TextBlock status)
    {
        var root = new StackPanel { Margin = new Thickness(18) };

        root.Children.Add(new TextBlock
        {
            Text = headline,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Themed("TextPrimary", Brushes.Black),
            Margin = new Thickness(0, 0, 0, 8),
        });

        root.Children.Add(status);
        root.Children.Add(BusyBar());

        var window = new Window
        {
            // Nothing in the title bar: see the class comment.
            Title = "",
            Content = root,
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        // No title bar at all: there is nothing to put in one, and nothing to
        // cancel — a close button that refuses the click reads as a hang.
        DialogChrome.Apply(window, closable: false, titled: false);
        return window;
    }

    /// <summary>
    /// A bar with a lit band sliding along it.
    ///
    /// Drawn here rather than styled as a ProgressBar: the stock one builds its
    /// own chrome from system colours, so on the dark palette it arrives as the
    /// one light rectangle on screen — the same reason every other control in
    /// this library is retemplated. Two borders and an animation is less code
    /// than retemplating it would be.
    /// </summary>
    private static UIElement BusyBar()
    {
        const double width = 484;
        const double band = 140;

        var lit = new Border
        {
            Width = band,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = Themed("Accent", Brushes.SteelBlue),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = new TranslateTransform(),
        };

        var track = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = Themed("SurfaceSunken", Brushes.LightGray),
            ClipToBounds = true,
            Child = lit,
        };

        var slide = new DoubleAnimation
        {
            From = -band,
            To = width,
            Duration = TimeSpan.FromSeconds(1.4),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

        // Started when the bar is on screen rather than now: an animation on an
        // element that has not been rendered yet is a timer running for nothing.
        track.Loaded += (_, _) => lit.RenderTransform.BeginAnimation(TranslateTransform.XProperty, slide);
        return track;
    }

    /// <summary>
    /// Hand the caller's exception back with its stack intact, so a failure
    /// inside the work reads as though the dialog had not been there.
    /// </summary>
    private static Exception Rethrow(Exception failure)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return failure;   // never reached; keeps the compiler happy at call sites
    }

    private static Brush Themed(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    /// <summary>
    /// Progress reports put onto a particular dispatcher, whatever thread they
    /// were made on and whatever context that thread was carrying.
    /// </summary>
    private sealed class DispatcherProgress(System.Windows.Threading.Dispatcher dispatcher, Action<string> report)
        : IProgress<string>
    {
        public void Report(string value) =>
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                                   () => report(value));
    }
}
