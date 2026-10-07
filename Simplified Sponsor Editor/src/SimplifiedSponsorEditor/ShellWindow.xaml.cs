using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using SimplifiedSponsorEditor.Core.Config;
using SimplifiedSponsorEditor.Core.Project;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The 2.0 window: the Launcher's surface, with this app's two states on it.
///
/// There are only two things on screen and one thing that changes between them.
/// The landing offers New and Open under the mark; choosing either leaves the
/// mark where it is, retitles the heading and fades a plus in at the foot of the
/// window. Nothing else is here yet — the rest of the app is being rebuilt onto
/// this rather than wrapped in it.
/// </summary>
public partial class ShellWindow : Window
{
    /// <summary>A square, and two overlapping ones once the window is maximised.</summary>
    private const string MaximiseData = "M 0.5,0.5 H 11.5 V 11.5 H 0.5 Z";
    private const string RestoreData = "M 3.5,0.5 H 11.5 V 8.5 M 0.5,3.5 H 8.5 V 11.5 H 0.5 Z";

    // ── How long the window takes to answer ───────────────────────────
    //
    // Two different numbers, and keeping them apart is the point. Every movement
    // runs at the Launcher's speed — these are its own figures, not ones picked
    // to look similar — and what makes the sequence unhurried is the pause
    // between them rather than any of them being slow. A slower fade reads as a
    // tired window; a quick fade with room after it reads as a deliberate one.
    //
    // Every delay below is built out of these rather than written in, so this
    // block is the one place the pace is set.

    /// <summary>The heading goes out, changes while nobody can see it, and comes back.</summary>
    private const double HeadingOut = 0.2;
    private const double HeadingIn = 0.26;

    /// <summary>How long one button takes to leave. The Launcher's ClearSeconds.</summary>
    private const double ClearSeconds = 0.3;

    /// <summary>How long the plus takes to arrive.</summary>
    private const double PlusIn = 0.3;

    /// <summary>
    /// The pause between one stage and the next, and the whole reason the
    /// sequence reads as four things rather than one.
    ///
    /// This is the number that sets the pace. Longer than the Launcher's 0.12
    /// trail because that one is a gap inside a single movement — its list and
    /// its buttons leaving together — where these four stages are the whole of
    /// what the window is doing, and each wants to be seen to finish.
    /// </summary>
    private const double Gap = 0.35;

    /// <summary>
    /// How long the block takes to travel to the top. The Launcher's
    /// SlideSeconds, which is what its heading moves on.
    /// </summary>
    private const double SlideSeconds = 0.55;

    /// <summary>Where the block sits once it has travelled, from the top of the window.</summary>
    private const double TopMargin = 62;

    /// <summary>
    /// Google's done, on its own 24-square and untranslated — the Canvas around
    /// it in the XAML is what keeps it to the grid, so it needs no moving by
    /// hand and cannot be measured by its own bounds. Its counterpart, the plus,
    /// is in the XAML because that is the one the button opens with.
    /// </summary>
    private const string TickData = "M9,16.17L4.83,12l-1.42,1.41L9,19L21,7l-1.41-1.41z";

    /// <summary>
    /// How long "SETTING UP YOUR PROJECT" stays up.
    ///
    /// Two full turns of the dots and a little over. Less than one turn and the
    /// animation reads as a glitch rather than as something being waited on.
    ///
    /// A fixed wait for now, because New and Open do not yet make or open
    /// anything — there is nothing to time. When they do, this becomes the floor
    /// rather than the whole of it: a project that opens instantly still needs
    /// its status read, and one that takes longer holds the status until it is
    /// done. Either way the chain below waits on the step rather than the clock,
    /// so only this number has to change.
    /// </summary>
    private const double Working = 2.6;

    /// <summary>
    /// How fast the dots cycle. The Launcher's, so a wait looks the same
    /// wherever somebody meets one.
    /// </summary>
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(420) };

    private readonly List<Run> _dots = [];
    private int _shown;

    /// <summary>
    /// The open project. Nothing reads it yet — the panes that will are still to
    /// be built — but it is what New and Open produce, and its temp directory
    /// has to be cleaned up on the way out.
    /// </summary>
    private SponsorProject? _project;

    public ShellWindow()
    {
        InitializeComponent();

        // The native half of drawing your own title bar, and none of it is
        // optional: Windows rounds ordinary windows itself but one with no
        // standard frame has to ask, a maximised chrome window covers the
        // taskbar until its content is inset to the monitor's work area, and
        // WindowStyle None drops WS_CAPTION — which the shell reads to decide
        // what it animates into the taskbar, so without it minimising makes the
        // window vanish on the spot rather than fly down.
        //
        // No maximise button passed: this one's glyph is a Path rather than a
        // Segoe codepoint, so Window_StateChanged below swaps it instead.
        SimplifiedUi.FramelessWindow.Attach(this);

        // Nothing, then one, then two, and round again.
        _ticker.Tick += (_, _) => Dots((_shown + 1) % 3);
    }

    // ── The two states ────────────────────────────────────────────────

    /// <summary>
    /// A new project: choose where it goes and what it is called, and that is
    /// the whole of what is asked. The name comes from the file name and the
    /// author from the saved default, so there is nothing to fill in — both are
    /// only metadata in info.json and neither is worth a form before anybody has
    /// put a decal in it.
    /// </summary>
    private void New_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Title = "New sponsor project",
            Filter = $"Simplified project (*{ProjectStore.ProjectFileExtension})"
                     + $"|*{ProjectStore.ProjectFileExtension}",
            DefaultExt = ProjectStore.ProjectFileExtension,
            FileName = "Sponsors" + ProjectStore.ProjectFileExtension,
            InitialDirectory = Remembered(),
            OverwritePrompt = true,
        };

        // Asked before anything moves. A dialog over a window mid-animation
        // looks like the app was interrupted, and cancelling has to leave the
        // landing exactly as it was.
        if (picker.ShowDialog(this) != true) return;

        var path = picker.FileName;
        Remember(path);

        Begin(NewButton, "SETTING UP", () =>
        {
            var project = ProjectStore.Create(
                Path.GetFileNameWithoutExtension(path),
                UserConfig.Load().DefaultAuthor ?? "");

            // Written out at once rather than on first save: somebody who picked
            // a name and a folder has said where this lives, and a project that
            // is not there yet would be a surprise.
            ProjectStore.Save(project, path);
            return project;
        });
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Open sponsor project",
            Filter = $"Simplified project (*{ProjectStore.ProjectFileExtension})"
                     + $"|*{ProjectStore.ProjectFileExtension}",
            DefaultExt = ProjectStore.ProjectFileExtension,
            CheckFileExists = true,
            InitialDirectory = Remembered(),
        };

        if (picker.ShowDialog(this) != true) return;

        var path = picker.FileName;
        Remember(path);

        Begin(OpenButton, "OPENING", () => ProjectStore.Open(path));
    }

    private static string Remembered()
    {
        var saved = UserConfig.Load().LastProjectFolder;
        return Directory.Exists(saved ?? "") ? saved! : "";
    }

    private static void Remember(string path)
    {
        try
        {
            var config = UserConfig.Load();
            config.LastProjectFolder = Path.GetDirectoryName(path);
            config.Save();
        }
        catch
        {
            // Where the last project was is a convenience. Failing to write it
            // down is not a reason to refuse to open one.
        }
    }

    /// <summary>
    /// On to a sponsor: the block travels to the top of the window, the heading
    /// says what this page is for, and the plus becomes the tick that will
    /// finish it.
    ///
    /// All three start together. This is one movement — the page becoming
    /// another page — and staging it would read as three things agreeing to
    /// happen rather than as one.
    /// </summary>
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        AddButton.IsHitTestVisible = false;

        Lift();
        Retitle("EDIT YOUR", "SPONSOR");

        // The button stays; only its glyph changes, and it changes while nobody
        // can see it. Same trick as the heading, and for the same reason: a
        // shape cannot be animated into another shape, but it can be swapped at
        // the bottom of a fade.
        Fade(AddButton, to: 0, seconds: ClearSeconds);

        After(ClearSeconds + Gap, () =>
        {
            ActionGlyph.Data = Geometry.Parse(TickData);
            AddButton.ToolTip = "Done";
            AddButton.IsHitTestVisible = true;
            FadeInAfter(AddButton, delay: 0, seconds: PlusIn);
        });
    }

    /// <summary>
    /// Move the block to the top of the window.
    ///
    /// How far is measured rather than written down: the block is a mark, a
    /// label, two lines of type and the margins between them, and every one of
    /// those has been changed today. The shift already applied is taken back off
    /// first, because TranslatePoint reports where the block is drawn and that
    /// includes the transform — so read from a block already at the top it would
    /// say it needs to move nowhere, which is true of where it is and useless as
    /// an answer to where it belongs.
    /// </summary>
    private void Lift()
    {
        // Where it is drawn now, before anything is taken out from under it.
        var was = Block.TranslatePoint(new Point(0, 0), Root).Y;

        // The choices have been invisible since the project opened but were
        // still laid out, which is what kept the heading from jumping when they
        // faded. Travelling to the top they would carry 84px of nothing down
        // with them and sit between the heading and whatever goes under it, so
        // this is where they stop taking up room.
        Choices.Visibility = Visibility.Collapsed;
        Root.UpdateLayout();

        // Collapsing them shortens the block, and a block centred on the window
        // moves when it gets shorter — half the height, downward. So the slide
        // starts from wherever that put it rather than from zero, which cancels
        // the jump exactly: the first frame draws it where the last one did.
        var laidOut = Block.TranslatePoint(new Point(0, 0), Root).Y - BlockShift.Y;

        var travel = Glide(TopMargin - laidOut, SlideSeconds);
        travel.From = was - laidOut;

        BlockShift.BeginAnimation(TranslateTransform.YProperty, travel);
    }

    /// <summary>
    /// Move to the project page: retitle, put the choices away, bring the plus
    /// in.
    ///
    /// The heading fades out, changes while nobody can see it, and fades back.
    /// Text is not animatable, so the swap happens at the bottom of the fade —
    /// which also means Opacity is only ever animated and Text only ever set,
    /// and neither is written by both.
    /// </summary>
    private void Begin(Button clicked, string doing, Func<SponsorProject> work)
    {
        var other = ReferenceEquals(clicked, NewButton) ? OpenButton : NewButton;

        // Hit-testing off at the top, before any of it runs: the fades take the
        // better part of a second and a second click inside that would start the
        // whole sequence again over itself.
        Choices.IsHitTestVisible = false;

        // The one not chosen leaves on its own first, so what is left on screen
        // is the button somebody pressed — and the eye is already on it when the
        // window answers.
        //
        // A delayed fade-out needs no help from FadeInAfter. The trap that
        // helper exists for is that a waiting animation shows the element's base
        // value — which for these is fully opaque, and fully opaque is exactly
        // what they should be while they wait their turn.
        Fade(other, to: 0, seconds: ClearSeconds);

        // Then the chosen button and the heading move together. These two are
        // one event rather than two: the button going is the question being
        // taken, and the heading changing is the answer, so holding them apart
        // made the window look like it had to think about it.
        const double answering = ClearSeconds + Gap;

        Fade(clicked, to: 0, seconds: ClearSeconds, delay: answering);

        // Say what is happening, and start doing it once that is on screen. Each
        // step hands over to the one after it rather than being scheduled
        // against the clock, so a step that takes longer than expected pushes
        // the rest along instead of being run over by them.
        Retitle(doing, "YOUR PROJECT", working: true, delay: answering,
                then: () => Finish(work));
    }

    /// <summary>
    /// Do the work behind the status, then move on.
    ///
    /// Two things have to finish: the job, and the time the status wants to be
    /// readable for. Whichever takes longer decides, which is why
    /// <see cref="Working"/> is started as a clock rather than waited on
    /// afterwards — a project that opens in 40ms still gets its status read, and
    /// one that takes four seconds holds it until it is done.
    ///
    /// The job runs off the UI thread because it unzips and rezips; nothing it
    /// touches is on screen.
    /// </summary>
    private async void Finish(Func<SponsorProject> work)
    {
        var floor = Task.Delay(TimeSpan.FromSeconds(Working));

        SponsorProject? project = null;
        Exception? failed = null;

        try { project = await Task.Run(work); }
        catch (Exception ex) { failed = ex; }

        await floor;

        if (project is null)
        {
            // Said plainly and then back to where they started, because there is
            // nothing else on this screen to retreat to.
            SimplifiedUi.MessageDialog.Show(this,
                failed?.Message ?? "That project couldn't be opened.",
                "Project", MessageBoxButton.OK, MessageBoxImage.Error);

            ShowLanding();
            return;
        }

        _project = project;

        Retitle("ADD A", "SPONSOR", then: () =>
        {
            // After it, not alongside: two things moving at once read as one
            // event and neither gets looked at, which is exactly why the button
            // and the first heading are together and this is not.
            AddButton.IsHitTestVisible = true;
            FadeInAfter(AddButton, delay: Gap, seconds: PlusIn);
        });
    }

    /// <summary>
    /// Back to the start, with both choices offered again. Only reached when the
    /// work failed, so there is no project to put away.
    /// </summary>
    private void ShowLanding()
    {
        Retitle("READY TO", "EDIT SPONSORS", then: () =>
        {
            // Only reached before the block has ever travelled, so these are
            // still laid out — but said anyway, because the one thing worse than
            // a dead branch is a dead branch that would be wrong if it ran.
            Choices.Visibility = Visibility.Visible;
            Choices.IsHitTestVisible = true;
            Fade(NewButton, to: 1, seconds: ClearSeconds);
            Fade(OpenButton, to: 1, seconds: ClearSeconds);
        });
    }

    /// <summary>
    /// Change the heading, and say what to do once it has arrived.
    ///
    /// <paramref name="then"/> rather than a second call with a delay on it, and
    /// the reason is worth knowing: both calls animate the same property on the
    /// same element, so scheduling the second would replace the first's
    /// animation outright — and a delayed animation does not hold its property
    /// while it waits. The heading would snap back to full, the first swap's
    /// Completed would never run, and the words it was supposed to show would
    /// never appear. One change hands over to the next instead.
    /// </summary>
    private void Retitle(string first, string second,
                         bool working = false, double delay = 0, Action? then = null)
    {
        var leaving = Glide(0, HeadingOut, delay);

        leaving.Completed += (_, _) =>
        {
            Caption(working, first, second);

            var arriving = Glide(1, HeadingIn);
            if (then is not null) arriving.Completed += (_, _) => then();

            Headline.BeginAnimation(OpacityProperty, arriving);
        };

        Headline.BeginAnimation(OpacityProperty, leaving);
    }

    /// <summary>
    /// Write the two lines, with the dots on the end when something is still
    /// happening. A finished state has none and sits still.
    /// </summary>
    private void Caption(bool working, string first, string second)
    {
        Headline.Inlines.Clear();
        _dots.Clear();

        Headline.Inlines.Add(new Run(first));
        Headline.Inlines.Add(new LineBreak());

        // Two transparent twins in front of the second line, because the real
        // dots behind it carry width whether or not anything is painted for
        // them. A line is laid out whole and then centred, so without these the
        // words sit a dot left of the middle and shuffle across when the status
        // changes. Balanced, the letters land on the middle and the dots hang
        // off the right — which is what hanging punctuation is for.
        if (working)
            for (var i = 0; i < 2; i++) Headline.Inlines.Add(Spacer());

        Headline.Inlines.Add(new Run(second));

        if (!working)
        {
            _ticker.Stop();
            return;
        }

        // Both dots are in the line the whole time; what cycles is whether each
        // is painted. Adding and removing them would change the line's width and
        // a centred line would shuffle under them twice a second.
        for (var i = 0; i < 2; i++)
        {
            var dot = new Run(".");
            _dots.Add(dot);
            Headline.Inlines.Add(dot);
        }

        Dots(0);
        _ticker.Start();
    }

    /// <summary>A glyph that takes up room and is never drawn.</summary>
    private static Run Spacer() => new(".") { Foreground = Brushes.Transparent };

    private void Dots(int count)
    {
        _shown = count;

        for (var i = 0; i < _dots.Count; i++)
            _dots[i].Foreground = i < count ? Headline.Foreground : Brushes.Transparent;
    }

    /// <summary>
    /// Do something once, after a wait. A timer rather than an animation's
    /// delay, because nothing is moving during it — the window is holding still
    /// on purpose.
    /// </summary>
    private void After(double seconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };

        timer.Start();
    }

    private static void Fade(UIElement element, double to, double seconds, double delay = 0) =>
        element.BeginAnimation(OpacityProperty, Glide(to, seconds, delay));

    /// <summary>
    /// Bring something back, but not yet — and keep it out of sight until then.
    ///
    /// A delayed animation does not hold its property while it waits: the clock
    /// has not started, so what shows is the element's own base value. Scheduling
    /// a fade-in half a second out therefore puts the thing on screen
    /// immediately and then fades it from visible to visible. The plus only
    /// escapes that because its base value is already zero, which is luck rather
    /// than design and would stop being true the first time it is faded back out.
    ///
    /// So the animation holding it is dropped first and the base set to hidden,
    /// giving the wait something invisible to wait on. The Launcher's, and for
    /// the reason it measured.
    /// </summary>
    private static void FadeInAfter(UIElement element, double delay, double seconds = 0.3)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, Glide(1, seconds, delay));
    }

    /// <summary>
    /// Every movement in the window goes through here, so they all ease the same
    /// way: cubic in and out, which leans into the start and settles at the end
    /// rather than running at one speed.
    ///
    /// The gradient's drift is the one thing that does not use this. It eases on
    /// a sine, because it turns round and comes back forever — a cubic would
    /// hold it still at each end and the wandering would read as a pulse.
    /// </summary>
    private static DoubleAnimation Glide(double to, double seconds, double delay = 0) =>
        new(to, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };

    // ── The window's own buttons ──────────────────────────────────────

    private void Minimise_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximise_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// An open project lives in a temp directory until it is saved, so the way
    /// out has to take it with it. Nothing is asked about unsaved work yet
    /// because nothing can change a project here — the panes that will are still
    /// to be built, and this wants revisiting when the first one lands.
    /// </summary>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_project is null) return;

        ProjectStore.Close(_project);
        _project = null;
    }

    /// <summary>
    /// The glyph says what the button will do next, and it follows the window
    /// rather than the click — double-clicking the caption maximises too.
    ///
    /// Keeping the maximised window inside the work area is Attach's job, not
    /// this one's: it measures the monitor, so a taskbar on another edge or a
    /// second screen at another scale all come out right.
    /// </summary>
    private void Window_StateChanged(object sender, EventArgs e)
    {
        var maximised = WindowState == WindowState.Maximized;

        MaximiseGlyph.Data = Geometry.Parse(maximised ? RestoreData : MaximiseData);
        MaximiseButton.ToolTip = maximised ? "Restore" : "Maximise";
    }
}
