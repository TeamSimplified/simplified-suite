using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SimplifiedUi;

/// <summary>
/// Work that takes a moment, reported along the bottom of the window: a line
/// saying what is happening, with a turning ring to the left of it.
///
/// This replaced a modal progress window. The window said the same things and
/// said them in the middle of the screen, over the work it was describing.
///
/// The bar exists only while the work does. It comes up with the ring and goes
/// away with it, so an app with nothing to do has nothing along its bottom
/// edge — no strip, and no line left over saying what happened a quarter of an
/// hour ago. Anything an app wants to say about a result goes wherever that app
/// already says such things; this line is for the waiting, and the waiting
/// only.
///
/// While it runs, everything but the status bar is switched off. That is the
/// part the modal window was really providing: not the words, but the
/// certainty that nothing else is being pressed while a package is written.
/// The difference is that a disabled toolbar shows you why, where a modal
/// window over the top only shows you that you cannot.
///
/// The call is synchronous, as the dialog's was: the work runs on a background
/// thread and this pumps messages until it finishes, so the window keeps
/// drawing and the caller keeps its shape.
/// </summary>
public static class Busy
{
    /// <summary>
    /// Run <paramref name="work"/> with the app switched off around it, and
    /// hand back what it returned.
    ///
    /// <paramref name="headline"/> is what the app is doing, in the user's
    /// terms — "Packaging Alpine 2026" — and goes up before the work starts.
    /// The work's own reports replace it as it moves through its steps.
    ///
    /// When it ends, the bar goes. The last step of a job is not a state to
    /// leave an app sitting in — "Putting the files in place…" reads as still
    /// happening — and neither is an empty strip where a line used to be.
    ///
    /// Exceptions come back to the caller as they would have without any of
    /// this, so call sites keep their own error handling.
    /// </summary>
    public static T Run<T>(Window owner, Spinner spinner, TextBlock status, string headline,
                           Func<IProgress<string>, T> work)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var dispatcher = owner.Dispatcher;

        // The bar, up for as long as the work lasts. Nothing is remembered to
        // put back afterwards: the line is never read at rest, because at rest
        // it is not on screen.
        var bar = StatusBar(owner, spinner);
        if (bar is not null) bar.Visibility = Visibility.Visible;
        if (status is not null && !string.IsNullOrEmpty(headline)) status.Text = headline;

        // Marshalled through the window's own dispatcher rather than through
        // Progress<T>, which captures whatever SynchronizationContext happened
        // to be current when it was built and posts to the thread pool when
        // there wasn't one — a UI element touched from a background thread,
        // which throws.
        IProgress<string> progress = new DispatcherProgress(dispatcher, line =>
        {
            if (!string.IsNullOrWhiteSpace(line) && status is not null) status.Text = line;
        });

        // One job at a time owns the switching off, however many of these are
        // stacked: opening a project runs one of these to unpack it and another
        // to read its parts, and one of those finishing is not the app becoming
        // usable again.
        var outermost = Depth(owner) == 0;
        Depth(owner, Depth(owner) + 1);

        var restore = outermost ? Disable(owner, bar) : static () => { };
        if (outermost && spinner is not null) spinner.IsActive = true;
        owner.Closing += Refuse;

        Exception? failure = null;
        var answer = default(T);

        try
        {
            var task = Task.Run(() =>
            {
                try { answer = work(progress); }
                catch (Exception ex) { failure = ex; }
            });

            // A nested message loop, which is what ShowDialog did for the
            // window this replaces. Without it the caller would have to be
            // rewritten around a callback, and every call site would be shaped
            // by how its work is scheduled rather than by what it does.
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            // In a finally because everything above can throw — a queued UI
            // callback that faults comes out of PushFrame, and the one thing
            // worse than a failed job is a window left switched off by it,
            // which reads as an app that has stopped taking input for good.
            Depth(owner, Depth(owner) - 1);
            owner.Closing -= Refuse;

            if (outermost)
            {
                if (spinner is not null) spinner.IsActive = false;
                restore();
            }

            if (outermost && bar is not null) bar.Visibility = Visibility.Collapsed;
        }

        if (failure is not null) throw Rethrow(failure);
        return answer!;

        // Closing is refused rather than queued: the work is already running
        // and cannot be called back, and a window that vanishes mid-package
        // leaves a half-written file behind. The close button is switched off
        // with everything else, so the refusal is visible rather than felt.
        static void Refuse(object? sender, System.ComponentModel.CancelEventArgs e) => e.Cancel = true;
    }

    /// <summary>The same, for work with nothing to hand back.</summary>
    public static void Run(Window owner, Spinner spinner, TextBlock status, string headline,
                           Action<IProgress<string>> work) =>
        Run<object?>(owner, spinner, status, headline, report => { work(report); return null; });

    /// <summary>
    /// How many of these are running on a window, so the outermost one owns
    /// switching it off and putting it back.
    /// </summary>
    private static readonly DependencyProperty DepthProperty =
        DependencyProperty.RegisterAttached("BusyDepth", typeof(int), typeof(Busy), new PropertyMetadata(0));

    private static int Depth(Window window) => (int)window.GetValue(DepthProperty);
    private static void Depth(Window window, int value) => window.SetValue(DepthProperty, value);

    /// <summary>
    /// The status bar: whichever direct child of the window's root holds the
    /// spinner, however deeply the app nests it inside that child.
    /// </summary>
    private static UIElement? StatusBar(Window owner, Spinner? spinner)
    {
        if (owner.Content is not Panel root || spinner is null) return null;

        DependencyObject? node = spinner;
        while (node is not null && VisualTreeHelper.GetParent(node) is { } parent && !ReferenceEquals(parent, root))
            node = parent;

        return node as UIElement;
    }

    /// <summary>
    /// Switch off everything except the status bar, and hand back the way to
    /// put it all back.
    ///
    /// By row rather than by hunting for buttons: a disabled Package button
    /// with a live team-name box beside it is still a window someone can edit
    /// while it is being packaged. The bar stays lit because it is the one part
    /// with something to say.
    /// </summary>
    private static Action Disable(Window owner, UIElement? bar)
    {
        if (owner.Content is not Panel root) return static () => { };

        var switched = new List<UIElement>();

        foreach (var child in root.Children)
        {
            if (child is not UIElement element || ReferenceEquals(element, bar)) continue;
            if (!element.IsEnabled) continue;   // already off; leave it that way

            element.IsEnabled = false;
            switched.Add(element);
        }

        return () =>
        {
            foreach (var element in switched) element.IsEnabled = true;
        };
    }

    /// <summary>
    /// Hand the caller's exception back with its stack intact, so a failure
    /// inside the work reads as though none of this had been here.
    /// </summary>
    private static Exception Rethrow(Exception failure)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return failure;   // never reached; keeps the compiler happy at call sites
    }

    /// <summary>
    /// Progress reports put onto a particular dispatcher, whatever thread they
    /// were made on and whatever context that thread was carrying.
    /// </summary>
    private sealed class DispatcherProgress(Dispatcher dispatcher, Action<string> report) : IProgress<string>
    {
        public void Report(string value) =>
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => report(value));
    }
}
