using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Simplified.Shared;
using SimplifiedLauncher.Mods;

namespace SimplifiedLauncher;

/// <summary>
/// The launcher: a list of mods to choose from, a button that installs them and
/// starts the game, and a settings page behind the gear.
///
/// A tall rounded window drawn rather than framed. None of the Simplified
/// Suite's chrome, palette or conventions are used here, so what it looks like
/// is only what this file and its XAML say. The one thing it does borrow is the
/// game-install search, which is the suite's own GameDirDetector.
///
/// Two things in here are still shape rather than substance, and both are
/// marked where they are: the catalogue below is invented, and Play_Click mimes
/// a launch without installing or starting anything.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RefreshMods();
        Caption(false, Resting);
        _ticker.Tick += (_, _) => Dots((_shown + 1) % 3);
        Ready();

        Loaded += (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                Anchor();

                // Set rather than slid: a window that opens empty should already
                // have the words in the middle, not drift there while you watch.
                // Nothing has animated this property yet, so writing it still
                // takes — after the first Slide it would not.
                HeadingShift.Y = RestingShift;
            }));
            _wasAt = Left;
        };

        LocationChanged += (_, _) => Wobble();

        // Saved first, detected second: the search only fills in what the config
        // was silent about. Both before the window is shown, so the settings page
        // is never briefly wrong.
        LoadSettings();
        FindGameInstall();

        // The suite's windows get this from FramelessWindow.Attach; this one
        // draws its own chrome without any of that library, so it asks directly.
        MinimiseAnimation.Enable(this);

        // A path typed and then closed over with the window button would
        // otherwise never reach the file: the box loses focus on the way out, but
        // nothing guarantees the order.
        GameDir.LostFocus += (_, _) => SaveSettings();
        Closing += (_, _) => SaveSettings();
    }

    /// <summary>
    /// True once the platform has been picked by a person, which stops the
    /// search below from having an opinion about it.
    ///
    /// The game folder needs no such flag: an empty box is its own proof that
    /// nobody has chosen. A pair of chips always says something — Steam is
    /// checked in the XAML so the page never opens with neither — so the only
    /// way to tell a choice from a default is to watch for the choice.
    /// </summary>
    private bool _platformPicked;

    private void Platform_Click(object sender, RoutedEventArgs e)
    {
        _platformPicked = true;
        SaveSettings();
    }

    /// <summary>
    /// Nothing detects a graphics card, so this one is only ever what was
    /// chosen — no flag to keep, just a write.
    /// </summary>
    private void Gpu_Click(object sender, RoutedEventArgs e) => SaveSettings();

    /// <summary>
    /// Put back what was saved last time.
    ///
    /// A remembered folder that is no longer on disk tells us nothing except
    /// that the install has moved, so it is ignored and the search runs instead.
    /// The platform goes with it: it was true of an install that is not there any
    /// more, and a guess from the folder we are about to find is worth more than
    /// a fact about the one we lost. A card choice outlives both, since it is
    /// about the machine rather than the game.
    /// </summary>
    private void LoadSettings()
    {
        var config = UserConfig.Load();

        var stillThere = !string.IsNullOrWhiteSpace(config.GameDir)
                         && Directory.Exists(config.GameDir);

        if (stillThere)
        {
            GameDir.Text = config.GameDir!;

            // Saved means settled, which is what stops FindGameInstall having an
            // opinion about it below.
            if (Pick(config.Platform, PlatformSteam, "Steam", PlatformEa, "EA App"))
                _platformPicked = true;
        }

        Pick(config.Gpu, GpuAmd, "AMD", GpuOther, "Other");
    }

    /// <summary>
    /// Write all three. Called from every place one of them can change rather
    /// than from an OK button, because there is no OK button — the settings page
    /// is left by closing it, and a change you can see should already be kept.
    /// </summary>
    private void SaveSettings()
    {
        var config = UserConfig.Load();

        var typed = GameDir.Text.Trim();
        config.GameDir = typed.Length > 0 ? typed : null;
        config.Platform = PlatformEa.IsChecked == true ? "EA App" : "Steam";
        config.Gpu = GpuOther.IsChecked == true ? "Other" : "AMD";

        config.Save();
    }

    /// <summary>
    /// Point a pair of chips at a saved answer, and say whether the answer meant
    /// anything. A value this build does not recognise leaves the default checked
    /// rather than picking the other one, so a hand-edited config with a typo in
    /// it costs nothing worse than being ignored.
    /// </summary>
    private static bool Pick(string? saved, RadioButton first, string firstName,
                             RadioButton second, string secondName)
    {
        if (string.IsNullOrWhiteSpace(saved)) return false;

        if (saved.Equals(firstName, StringComparison.OrdinalIgnoreCase))
        {
            first.IsChecked = true;
            return true;
        }

        if (saved.Equals(secondName, StringComparison.OrdinalIgnoreCase))
        {
            second.IsChecked = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Find the game, and fill in what finding it tells us: the folder, and
    /// which shop it came from.
    ///
    /// The same search the editors run, out of the same file, so the launcher
    /// looks for the install where they look. Two things differ here. It runs
    /// off the UI thread, because the window opens with an animation that should
    /// not wait on a registry read. And what it finds goes into the controls
    /// rather than into a config, because there is no config yet — so a folder
    /// or a platform you pick by hand lasts until you close the app and no
    /// longer.
    ///
    /// Detect rather than DetectIfUnset: the same two rules apply, but they are
    /// applied here, once, because the platform needs the candidate's Source and
    /// DetectIfUnset hands back only a path. Both guards are read after the
    /// await as well as before, which is what covers someone filling the box in
    /// or pressing a chip while the search was still running.
    /// </summary>
    private async void FindGameInstall()
    {
        var hit = await Task.Run(Find);
        if (hit is null) return;

        if (string.IsNullOrWhiteSpace(GameDir.Text)) GameDir.Text = hit.Path;
        if (!_platformPicked) Preselect(hit.Source);

        // So the file exists and is complete after a first run, rather than
        // appearing only once something is touched.
        SaveSettings();

        static GameDirDetector.Candidate? Find()
        {
            try { return GameDirDetector.Detect(); }
            catch { return null; }   // a search that fails is not a reason not to start
        }
    }

    /// <summary>
    /// Point the platform chips at the shop the install was found in.
    ///
    /// These two strings are GameDirDetector's, written where it adds a
    /// candidate, and nothing but this comment ties the two files together. That
    /// is why neither branch is an else: a source this does not recognise leaves
    /// the chips alone rather than guessing, so renaming one over in Shared
    /// costs a wrong default here instead of a wrong platform.
    /// </summary>
    private void Preselect(string source)
    {
        if (source.Equals("Steam", StringComparison.OrdinalIgnoreCase)) PlatformSteam.IsChecked = true;
        else if (source.Equals("EA", StringComparison.OrdinalIgnoreCase)) PlatformEa.IsChecked = true;
    }

    /// <summary>
    /// What the heading says when nothing is happening.
    ///
    /// An empty library is the one state where the window has to ask for
    /// something instead of naming what is on it: there is no other way in, no
    /// add button and no file dialog, so a window that only said CHOOSE YOUR
    /// MODS over an empty list would be a dead end.
    ///
    /// Every place that falls back to rest reads this rather than spelling the
    /// words out, so the empty case cannot be forgotten in one of them.
    /// </summary>
    private string[] Resting => Mods.Items.Count == 0
        ? ["DROP YOUR", "MODS HERE"]
        : ["CHOOSE", "YOUR MODS"];

    /// <summary>
    /// Where the heading sits when nothing is happening, beside what it says.
    ///
    /// Asking for a mod over an empty window means the words are the whole
    /// window, so they take the middle of it. The moment there is a list they
    /// are a heading again and go back to the top to make room for it.
    ///
    /// The same two positions the launch sequence uses, and the same Slide, so
    /// there is one idea of where the heading can be rather than two.
    /// </summary>
    private double RestingShift => Mods.Items.Count == 0 ? CentreOffset() : 0;

    /// <summary>
    /// What is installed, read off disk.
    ///
    /// The mods folder is the list. Nothing is cached and there is no index to
    /// drift from what is actually there, so deleting a mod's folder removes it
    /// from the list and the only thing that has to happen is this running
    /// again.
    /// </summary>
    private void RefreshMods() => Show(ModLibrary.Read());

    /// <summary>
    /// Put a list on screen, keeping whatever was ticked.
    ///
    /// Separate from reading the library because the order matters when the
    /// heading is moving: the rows have to be swapped while they are invisible,
    /// which means the caller decides when, not this.
    /// </summary>
    private void Show(IReadOnlyList<InstalledMod> mods)
    {
        var chosen = Mods.SelectedItems
            .OfType<InstalledMod>()
            .Select(m => m.Folder)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Mods.ItemsSource = mods;

        // Put the ticks back on the same mods, by folder rather than by position:
        // an import can land anywhere in an alphabetical list and push everything
        // after it down one.
        foreach (var mod in Mods.Items.OfType<InstalledMod>())
            if (chosen.Contains(mod.Folder)) Mods.SelectedItems.Add(mod);

        Ready();
    }

    /// <summary>
    /// Read the library again and move the window around it, one thing at a
    /// time.
    ///
    /// The heading's two resting places overlap where the list starts, so the
    /// two cannot travel together: rows sliding through a heading on its way
    /// past reads as a collision rather than a sequence. So the room empties,
    /// then the heading moves, then the room fills — and in the other
    /// direction the heading goes up before anything arrives in the space it
    /// leaves.
    ///
    /// Only when the heading is actually going somewhere, which is only ever
    /// the empty boundary. A mod added to a list that already had some is just
    /// a new row, and making that wait a second for a heading that is not
    /// moving would be ceremony.
    ///
    /// The fade back in is scheduled rather than awaited, with the slide's own
    /// length as its delay. It therefore starts exactly when the heading stops,
    /// whatever else the caller is doing to the caption meanwhile.
    /// </summary>
    private async Task Resettle()
    {
        var had = Mods.Items.Count > 0;
        var mods = ModLibrary.Read();
        var has = mods.Count > 0;

        if (had == has)
        {
            Show(mods);
            return;
        }

        if (had)
        {
            Fade(Mods, 0, seconds: ClearSeconds);
            if (!await Wait(ClearSeconds)) return;

            Show(mods);
            Slide(RestingShift);
            return;
        }

        // Filling. The rows are put in place already invisible, so none of them
        // shows for a frame before the heading has got out of the way.
        Fade(Mods, 0, seconds: 0);
        Show(mods);
        Slide(RestingShift);
        Fade(Mods, 1, seconds: ClearSeconds, delay: SlideSeconds);
    }

    /// <summary>
    /// Keep a right click from ticking the row.
    ///
    /// Selecting a mod is what decides whether it gets launched, so opening a
    /// menu on one has no business changing that. See the note in the row's
    /// template for why this is the bubbling event rather than the preview one.
    /// </summary>
    private void Row_MouseRightButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>
    /// Delete the mod the menu was opened on.
    ///
    /// The mod comes from the clicked item's DataContext rather than from the
    /// list's selection. With the right button no longer selecting anything, the
    /// selection has nothing to do with which row was clicked — and on a
    /// multi-select list it never did say which one was meant.
    ///
    /// It goes immediately, with no are-you-sure. Re-dropping the archive puts it
    /// back, and the whole point of the folder-per-mod layout is that removing one
    /// is this cheap.
    /// </summary>
    private async void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (sender is not FrameworkElement { DataContext: InstalledMod mod }) return;

        _running = true;
        Ready();

        // Off the UI thread: a mod is tens of megabytes of small files, and
        // deleting those is slow enough to freeze the window.
        var gone = await Task.Run(() => ModLibrary.Remove(mod));
        if (!IsLoaded) return;

        await Resettle();

        if (gone) await Say(false, Resting);
        else await Say(false, "THAT MOD", "WOULDN'T GO");

        _running = false;
        Ready();
    }

    /// <summary>
    /// Take the drag only when it is carrying files.
    ///
    /// Preview, so it is decided before anything inside the window gets a say —
    /// the folder box would otherwise take the drop itself and try to read a zip
    /// as text. Anything that is not files is left alone and travels on, which is
    /// what keeps dragging a path into that box working.
    /// </summary>
    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        e.Effects = _running ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } dropped)
            Import(dropped);
    }

    /// <summary>
    /// Install what was dropped, one archive after another.
    ///
    /// Held under the same flag a launch runs under, for the same reason: the
    /// window should not be closed while files are half unpacked, and Play should
    /// not start on a list that is about to change underneath it.
    ///
    /// The work is on a thread pool thread. A rar of any size takes long enough
    /// that doing it here would stop the dots moving, which is the one thing on
    /// screen saying the app has not hung.
    /// </summary>
    private async void Import(string[] paths)
    {
        if (_running) return;
        _running = true;
        Ready();

        var installed = 0;
        string? problem = null;
        var refused = 0;

        await Say("ADDING", paths.Length == 1 ? "YOUR MOD" : "YOUR MODS");

        foreach (var path in paths)
        {
            var result = await Task.Run(() => ModImport.Install(path));
            if (!IsLoaded) return;

            if (result.Mod is null) problem ??= result.Problem;
            else installed++;

            refused += result.Refused;
        }

        await Resettle();

        // One line, and the first problem rather than all of them: there is one
        // heading to say it in, and a person who dropped five archives and got
        // one failure needs to know which rather than to read a list.
        if (installed == 0 && problem is not null) await Say(false, "THAT FILE", "DIDN'T OPEN");
        else if (problem is not null) await Say(false, "SOME OF THOSE", "DIDN'T OPEN");
        else if (refused > 0) await Say(false, "ADDED, MINUS", "SOME ODD PATHS");
        else await Say(false, Resting);

        _running = false;
        Ready();
    }

    /// <summary>
    /// A window with no caption has nothing to drag it by, so its surface is
    /// the handle. The buttons mark their own clicks as handled, which is why
    /// pressing one does not also start a drag.
    /// </summary>
    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>
    /// The whole of a launch, mimed.
    ///
    /// Nothing is installed and nothing is started: this is the shape of the
    /// thing, so the timings and the movement can be looked at before there
    /// is anything behind them. The three captions and their waits are the
    /// only part that will not survive contact with real work.
    /// </summary>
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        Ready();

        // The room empties in order: the list, then the buttons a breath
        // behind it, and only once both are gone does the heading move. All of
        // it at once reads as one lurch — staggered, it reads as a sequence,
        // and the heading arrives somewhere that is already empty.
        Fade(Mods, 0, seconds: ClearSeconds);
        Fade(BottomBar, 0, seconds: ClearSeconds, delay: TrailSeconds);
        Mods.IsHitTestVisible = false;
        Slide(CentreOffset(), delay: ClearSeconds + TrailSeconds);

        if (!await Say("INSTALLING", "YOUR MODS")) return;
        if (!await Wait(6)) return;

        if (!await Say("LAUNCHING", "YOUR GAME")) return;
        if (!await Wait(5)) return;

        if (!await Say("GAME", "LAUNCHED.", working: false)) return;
        if (!await Wait(10)) return;

        if (!await Say("RESTORING", "YOUR GAME")) return;
        if (!await Wait(5)) return;

        Slide(RestingShift);
        Fade(Mods, 1, seconds: ClearSeconds, delay: SlideSeconds);
        Fade(BottomBar, 1, seconds: ClearSeconds, delay: SlideSeconds + TrailSeconds);
        if (!await Say(false, Resting)) return;
        Mods.IsHitTestVisible = true;

        _running = false;
        Ready();
    }

    private void Mods_SelectionChanged(object sender, SelectionChangedEventArgs e) => Ready();

    /// <summary>
    /// Into settings: the list and both of the things you do with it go, and
    /// the way back out takes play's place.
    ///
    /// The heading stays where it is. Nothing is happening that the window
    /// needs to make room for — this is another page, not a process.
    /// </summary>
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _inSettings) return;
        _inSettings = true;
        Ready();

        // Open at the top, every time. The page is faded out rather than
        // unloaded, so its ScrollViewer survives and would otherwise hand back
        // whatever offset the last visit ended on. Done before the fade begins,
        // while it is still invisible, so there is nothing to see jump.
        if (Options.Template.FindName("Scroll", Options) is ScrollViewer scroll) scroll.ScrollToTop();

        // Settings belongs under a heading at the top. Over an empty library
        // the heading is in the middle of the window, sitting where the page is
        // about to be, so it has to travel before anything can arrive — the
        // room empties, the heading goes up, and the page appears under it.
        //
        // When the heading is already up there is nothing to wait for, and the
        // page simply takes over from the list as it always did. Waiting out a
        // slide that is not happening would be half a second of nothing.
        var travelling = RestingShift != 0;
        var ready = travelling ? ClearSeconds + TrailSeconds + SlideSeconds : TrailSeconds;

        Fade(Mods, 0, seconds: ClearSeconds);
        Fade(SettingsSlot, 0, seconds: ClearSeconds, delay: TrailSeconds);
        Fade(PlaySlot, 0, seconds: ClearSeconds, delay: TrailSeconds);

        if (travelling) Slide(0, delay: ClearSeconds + TrailSeconds);

        Fade(Options, 1, seconds: ClearSeconds, delay: ready);
        Fade(TeamSlot, 1, seconds: ClearSeconds, delay: ready);
        Fade(BackSlot, 1, seconds: ClearSeconds, delay: ready);
        Mods.IsHitTestVisible = false;
        Options.IsHitTestVisible = true;
        PlaySlot.IsHitTestVisible = false;
        SettingsSlot.IsHitTestVisible = false;
        BackSlot.IsHitTestVisible = true;

        await Say("LAUNCHER", "SETTINGS", working: false);
    }

    /// <summary>
    /// Ask for the game folder.
    ///
    /// Starts wherever the box already points, so correcting a path does not
    /// begin from the top of the drive again.
    /// </summary>
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the game folder",
            InitialDirectory = GameDir.Text.Trim(),
        };

        if (picker.ShowDialog(this) != true) return;

        GameDir.Text = picker.FolderName;
        SaveSettings();
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!_inSettings) return;
        _inSettings = false;
        Ready();

        // The same thing backwards: the page goes, then the heading drops to
        // wherever the list says it belongs, and only then does the list come
        // back under it.
        var travelling = RestingShift != 0;
        var ready = travelling ? ClearSeconds + SlideSeconds : TrailSeconds;

        Fade(Options, 0, seconds: ClearSeconds);
        Fade(TeamSlot, 0, seconds: ClearSeconds);
        Fade(BackSlot, 0, seconds: ClearSeconds);

        if (travelling) Slide(RestingShift, delay: ClearSeconds);

        Fade(Mods, 1, seconds: ClearSeconds, delay: ready);
        Fade(SettingsSlot, 1, seconds: ClearSeconds, delay: ready + TrailSeconds);
        Fade(PlaySlot, 1, seconds: ClearSeconds, delay: ready + TrailSeconds);
        Mods.IsHitTestVisible = true;
        Options.IsHitTestVisible = false;
        PlaySlot.IsHitTestVisible = true;
        SettingsSlot.IsHitTestVisible = true;
        BackSlot.IsHitTestVisible = false;

        await Say(false, Resting);
    }

    private bool _inSettings;

    /// <summary>
    /// What can be pressed, decided in one place.
    ///
    /// Play needs something picked and nothing already under way. Close needs
    /// only the second: shutting the window halfway through would leave the
    /// game half dealt with, so it goes away for the duration and comes back
    /// at the end.
    ///
    /// One method for both, because the alternative is the end of a run
    /// switching things back on without asking whether they should be — which
    /// is how play would come back over an empty list.
    ///
    /// Minimise stays live throughout: putting the window away while it works
    /// is reasonable, and it does not interrupt anything.
    ///
    /// Enabling is what stops an invisible button being clicked. A thing at
    /// zero opacity is still there and still takes the pointer, so fading
    /// something out without switching it off leaves a trap where it was.
    /// </summary>
    private void Ready()
    {
        Play.IsEnabled = !_running && !_inSettings && Mods.SelectedItems.Count > 0;
        Settings.IsEnabled = !_running && !_inSettings;
        Back.IsEnabled = _inSettings;
        CloseButton.IsEnabled = !_running;
    }

    private bool _running;

    /// <summary>
    /// Wait, and say whether the window is still there to carry on with.
    /// Closing it mid-sequence would otherwise land on a dead window.
    /// </summary>
    private async Task<bool> Wait(double seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        return IsLoaded;
    }

    /// <param name="working">
    /// Whether this is something still happening, which is what the dots say.
    /// A finished state has none and sits still.
    /// </param>
    private void Caption(bool working, params string[] lines)
    {
        Headline.Inlines.Clear();
        _dots.Clear();

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) Headline.Inlines.Add(new LineBreak());
            Headline.Inlines.Add(new Run(lines[i]));
        }

        if (!working)
        {
            _ticker.Stop();
            return;
        }

        // Both dots are in the line the whole time; what cycles is whether
        // each is painted. Adding and removing them would change the width of
        // the longest line, and the block is centred on that — so the heading
        // would hop sideways twice a second.
        for (var i = 0; i < 2; i++)
        {
            var dot = new Run(".");
            _dots.Add(dot);
            Headline.Inlines.Add(dot);
        }

        Dots(0);
        _ticker.Start();
    }

    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private readonly List<Run> _dots = [];
    private int _shown;

    private void Dots(int count)
    {
        _shown = count;
        for (var i = 0; i < _dots.Count; i++)
            _dots[i].Foreground = i < count ? Headline.Foreground : Brushes.Transparent;
    }

    /// <summary>
    /// Change the stacked line, out and back in.
    ///
    /// Only this line changes its words. The whole block travels together,
    /// which is a different thing and lives in Slide.
    /// </summary>
    /// <returns>False if the window went away mid-fade.</returns>
    private Task<bool> Say(string first, string second, bool working = true) =>
        Say(working, first, second);

    private async Task<bool> Say(bool working, params string[] lines)
    {
        Fade(Headline, 0, seconds: 0.2);
        await Task.Delay(TimeSpan.FromSeconds(0.2));
        if (!IsLoaded) return false;

        Caption(working, lines);
        Fade(Headline, 1, seconds: 0.26);
        return true;
    }

    /// <summary>
    /// How long the heading takes to travel, and how long the list takes to go
    /// or come back.
    ///
    /// Named because three other things have to wait exactly this long. They
    /// used to wait 0.25 and 0.12, numbers picked to look right against a slide
    /// whose length was written down somewhere else — which held until the
    /// slide changed.
    /// </summary>
    private const double SlideSeconds = 0.55;

    private const double ClearSeconds = 0.3;

    /// <summary>
    /// How far behind the list the bottom row follows.
    ///
    /// Short enough to read as one movement rather than two, long enough that
    /// the eye sees an order to it. Everything going at once is a light being
    /// switched off; the list first and the buttons after is the room
    /// emptying.
    /// </summary>
    private const double TrailSeconds = 0.12;

    private void Slide(double to, double delay = 0) =>
        HeadingShift.BeginAnimation(TranslateTransform.YProperty, Glide(to, SlideSeconds, delay));

    /// <summary>
    /// How far the heading has to drop for the block to sit on the window's
    /// middle. All three of its lines go together.
    ///
    /// Measured rather than written down: the block is a wordmark, a label,
    /// two lines of type and the margins between them, and every one of those
    /// has been changed today.
    ///
    /// The shift already applied is taken back off, because TranslatePoint
    /// reports where the heading is drawn and that includes the transform. Read
    /// from a heading already sitting in the middle, the raw measurement says
    /// it needs to move almost nowhere — which is true of where it is, and
    /// useless as an answer to where it belongs. Taking the transform off makes
    /// this the distance from its laid-out position, which is what Slide wants
    /// and what makes it safe to ask at any moment rather than only at rest.
    /// </summary>
    private double CentreOffset()
    {
        var top = Heading.TranslatePoint(new Point(0, 0), Surface).Y - HeadingShift.Y;
        return (Surface.ActualHeight - Heading.ActualHeight) / 2 - top;
    }

    /// <summary>
    /// Pin the heading sideways, where it sits with the first caption on it.
    ///
    /// Centred, the block is as wide as its longest line, so every caption
    /// re-centred the lot — INSTALLING is wider than CHOOSE and the wordmark
    /// and the label shuffled left and right under it on every change. Pinned
    /// to where CHOOSE YOUR MODS put it, nothing moves horizontally again and
    /// the longer captions simply run further right. Slightly off centre, and
    /// still.
    ///
    /// At Loaded priority, so the first layout pass has been and the position
    /// being read is a real one.
    /// </summary>
    private void Anchor()
    {
        if (Heading.Parent is not FrameworkElement host || Heading.ActualWidth <= 0) return;

        var x = Heading.TranslatePoint(new Point(0, 0), host).X;
        Heading.HorizontalAlignment = HorizontalAlignment.Left;
        Heading.Margin = new Thickness(x, Heading.Margin.Top, 0, Heading.Margin.Bottom);
    }

    private static void Fade(UIElement element, double to, double delay = 0, double seconds = 0.35) =>
        element.BeginAnimation(OpacityProperty, Glide(to, seconds, delay));

    /// <summary>
    /// Every movement in the window goes through here, so they all ease the
    /// same way: cubic in and out, which leans into the start and settles at
    /// the end rather than running at one speed.
    ///
    /// The gradient's drift is the one thing that does not use this. It
    /// eases on a sine, because it turns round and comes back forever — a
    /// cubic would hold it still at each end and the wandering would read
    /// as a pulse.
    /// </summary>
    private static DoubleAnimation Glide(double to, double seconds, double delay = 0) =>
        new(to, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };

    // ── Shaking it off ──────────────────────────────────────────────────

    /// <summary>
    /// A shake clears the selection.
    ///
    /// What counts as one is a run of direction changes: the window has to
    /// turn back on itself three times, each turn after travelling nine
    /// pixels or so, and all three inside about a second. Any one of those on
    /// its own is just dragging the window somewhere — it is the repetition
    /// in a short space of time that nothing else does by accident.
    ///
    /// Three numbers, and they trade against each other: fewer turns, a
    /// shorter travel and a longer window each make it easier, and all three
    /// at once make it fire while somebody is only moving the window.
    ///
    /// Sideways only. Dragging a window up and down the screen is ordinary,
    /// and a shake is a sideways gesture everywhere it exists.
    /// </summary>
    private void Wobble()
    {
        var x = Left;
        var moved = x - _wasAt;
        _wasAt = x;

        // Below this it is the hand holding still, not a change of direction.
        if (Math.Abs(moved) < 9) return;

        var way = Math.Sign(moved);
        if (_way != 0 && way != _way)
        {
            var now = DateTime.UtcNow;
            if (_turns == 0 || now - _began > ShakeWindow)
            {
                _turns = 0;
                _began = now;
            }

            if (++_turns >= 3)
            {
                _turns = 0;
                Shaken();
            }
        }

        _way = way;
    }

    private static readonly TimeSpan ShakeWindow = TimeSpan.FromSeconds(1.2);

    private double _wasAt;
    private int _way;
    private int _turns;
    private DateTime _began;

    /// <summary>
    /// Nothing is selected any more. Deliberately silent about it: every
    /// marker going grey at once is the feedback, and a gesture that also
    /// announced itself would be louder than the thing it did.
    ///
    /// Ignored mid-run, when the list is not on screen to be cleared and the
    /// window may well be being moved out of the way.
    /// </summary>
    private void Shaken()
    {
        if (_running || Mods.SelectedItems.Count == 0) return;
        Mods.UnselectAll();
    }

    private void Minimise_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
