using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Simplified.Language;
using Simplified.Shared;
using SimplifiedSponsorEditor.Core;
using SimplifiedSponsorEditor.Core.Config;
using SimplifiedSponsorEditor.Core.Package;
using SimplifiedSponsorEditor.Core.Sponsors;
using SimplifiedUi;

namespace SimplifiedSponsorEditor;

/// <summary>
/// Turning the project into a mod somebody can install.
///
/// The work itself is <see cref="SponsorPackager"/>, which was already written
/// and tested. What is here is the half that cannot live in that assembly:
/// deciding which of the game's decals each variation is going into, and turning
/// drawings into pixels — which is a WPF operation and wants the thread that
/// owns one. The packager is handed a folder of PNGs named after the decals they
/// replace, which is all it ever wanted.
/// </summary>
public partial class ShellWindow
{
    /// <summary>
    /// Whether a package is being written. Nothing else may happen while one is:
    /// it reads every variation's artwork and merges into the game's archives,
    /// and a sponsor edited halfway through would be in the project but not in
    /// the zip, with nothing to say which.
    /// </summary>
    private bool _packaging;

    /// <summary>One variation, and the game decal it is going into.</summary>
    /// <param name="At">
    /// Which of the six it was, for saying so. One-based, because the squares
    /// are not numbered on screen and "variation 4" has to mean the fourth.
    /// </param>
    private sealed record Bound(SponsorDecal Decal, Variation Variation, string Sponsor, int At);

    // ── The button ──────────────────────────────────────────────────────────

    private void Package_Click(object sender, RoutedEventArgs e)
    {
        if (_project is not { } project || _packaging) return;

        var (work, notes) = PackagePlan();

        if (work.Count == 0)
        {
            // The notes are the answer here rather than a footnote to it. A
            // project with six sponsors and no replacements set is not an empty
            // project, and saying "nothing to package" without saying why would
            // leave somebody looking at a full shelf.
            MessageDialog.Show(this,
                notes.Count > 0
                    ? $"There is nothing to package yet.\n\n{string.Join('\n', notes)}"
                    : "There is nothing to package yet.\n\nAdd a sponsor, give it some artwork, "
                      + "and say which of the game's sponsors it replaces.",
                "Package", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        if (GameFolder() is not { } gameDir) return;

        var name = string.IsNullOrWhiteSpace(project.Info.Name)
            ? project.DisplayName
            : project.Info.Name.Trim();

        var picker = new SaveFileDialog
        {
            Title = "Save the mod",
            Filter = "Zip archives (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = FileNameFor(name) + ".zip",
            InitialDirectory = LastPackageFolder(),
            OverwritePrompt = true,
        };

        if (picker.ShowDialog(this) != true) return;

        RememberPackageFolder(picker.FileName);

        // Rendered before the worker starts, on this thread, for the reason
        // given at the top of the file.
        var (artwork, skipped) = Materialise(work);

        if (!Directory.EnumerateFiles(artwork, "*.png").Any())
        {
            try { Directory.Delete(artwork, recursive: true); } catch { }

            MessageDialog.Show(this,
                "None of the artwork could be prepared, so there is nothing to package.\n\n"
                + string.Join('\n', skipped),
                "Package", MessageBoxButton.OK, MessageBoxImage.Error);

            return;
        }

        var request = new PackageRequest(
            artwork, AppPaths.OriginalsDir, gameDir, picker.FileName,
            ModName: name,
            Author: project.Info.Author,
            ModVersion: project.Info.Version,

            // What the game should call them. Taken from the sponsors rather
            // than from the bound variations: the name belongs to the sponsor,
            // and one of ours replacing a sponsor with one variation filled
            // still renames the whole of it.
            Text: Spoken(work),
            LanguageDir: AppPaths.LanguageDir,

            // The artwork arrives already placed. Each PNG is the whole texture
            // with the logo sized into it the way the variation window showed
            // it, so the packager's own inset would be a second margin inside
            // the one somebody chose, and every logo would come out smaller than
            // it looked.
            Margin: 0);

        Write(request, artwork, [.. notes, .. skipped]);
    }

    /// <summary>
    /// What each replaced sponsor should now be called, one entry per sponsor.
    ///
    /// Only the sponsors whose artwork is actually in this package. A sponsor
    /// with a name and no artwork has already been reported as left out, and
    /// renaming one the package does not otherwise touch would make that report
    /// a lie — and leave somebody with a game that calls the old logo by the
    /// new name.
    ///
    /// Only names and bios that were filled in, too: writing an empty one would
    /// blank the game's own, which is worse than leaving it.
    /// </summary>
    private IReadOnlyList<SponsorText> Spoken(IReadOnlyList<Bound> work)
    {
        var packaged = new HashSet<string>(
            work.Select(bound => bound.Decal.Sponsor), StringComparer.OrdinalIgnoreCase);

        var said = new Dictionary<string, SponsorText>(StringComparer.OrdinalIgnoreCase);

        foreach (var sponsor in _sponsors)
        {
            if (string.IsNullOrWhiteSpace(sponsor.Replaces)) continue;
            if (!packaged.Contains(sponsor.Replaces)) continue;

            var name = sponsor.Name.Trim();
            var bio = sponsor.Bio.Trim();
            if (name.Length == 0 && bio.Length == 0) continue;

            // Keyed by the game's sponsor, so the first one in wins if two of
            // ours ever claim it. The plan has already settled that, and this
            // agrees with it rather than deciding again.
            if (!said.ContainsKey(sponsor.Replaces))
                said[sponsor.Replaces] = new SponsorText(sponsor.Replaces, name, bio);
        }

        return [.. said.Values];
    }

    /// <summary>
    /// Make sure the game's language files are beside the app, and say what
    /// happened if they could not be.
    ///
    /// Taken here rather than at startup. They are sixty-six megabytes out of
    /// a 408 MB exe and nothing but packaging wants them, so an app that only
    /// ever edits artwork should never pay for them — and the one moment
    /// somebody is already waiting is the one this runs in.
    /// </summary>
    /// <summary>
    /// Why the game's text is not beside the app, when it is not. Null when it
    /// is, or when nobody has tried yet.
    /// </summary>
    private string? _languageProblem;

    /// <summary>
    /// Make sure the game's text is beside the app, fetching it if it is not.
    ///
    /// At startup, which is where the rest of the suite does this and the whole
    /// reason it is here rather than in the package: the files live inside the
    /// game's exe, getting them out means reading 408 MB, and a person opening
    /// an app is expecting it to take a moment where a person who has just
    /// pressed package is expecting a mod.
    ///
    /// Run again when the game folder changes, because the answer to "did these
    /// come out of the installed game" changes with it — and because somebody
    /// whose first run had no game set would otherwise never get them.
    /// </summary>
    private async Task EnsureGameFiles()
    {
        string? gameDir;
        LanguageState state;

        // Asking is as fallible as fetching: a half-written stamp, a folder
        // somebody has taken away mid-read. This runs from Loaded, which is an
        // async void handler, so anything that escapes it does not reach a
        // caller — it reaches the dispatcher, and takes the window with it
        // before the window has finished opening.
        try
        {
            gameDir = KnownGameFolder();
            state = LanguageFiles.Check(gameDir, AppPaths.LanguageDir);
        }
        catch (Exception problem)
        {
            _languageProblem = problem.Message;
            return;
        }

        // Current, or no game to take them from. Headline says which by having
        // nothing to say.
        if (LanguageFiles.Headline(state) is null)
        {
            _languageProblem = state == LanguageState.Current
                ? null
                : "the game folder isn't set";

            return;
        }

        // Nothing comes out of the game until the person has been told it will
        // be the base of their mod and has had the chance to verify it first.
        if (!SimplifiedUi.GameFilesNotice.Confirm(this))
        {
            _languageProblem = "the game's files weren't extracted";
            return;
        }

        // Said on the heading rather than done quietly: this is the one thing
        // the app does that can take half a minute, and an app that sat on one
        // word for that long would read as one that had not finished opening.
        //
        // The door stays open. There is nothing half-written to spoil here, so
        // there is no reason to keep somebody in while it reads.
        Occupied(true, theDoorToo: false);
        Retitle("READING", "THE GAME", working: true);

        try
        {
            await Task.Run(() =>
                LanguageFiles.Refresh(gameDir, AppPaths.LanguageDir, AppPaths.CacheDir));

            _languageProblem = null;
        }
        catch (Exception problem)
        {
            // Not a dialog and not a failure. The app opens and works; what it
            // cannot do until this is sorted out is name a sponsor in game, and
            // the package says so itself when it gets there.
            _languageProblem = problem.Message;
        }

        Occupied(false, theDoorToo: false);

        Retitle(TitleFor(_stage).First, TitleFor(_stage).Second);
    }

    /// <summary>
    /// The game's text, or null with the reason in <paramref name="notes"/>.
    ///
    /// Only looked at here. Fetching it is startup's job, and a package that
    /// went and got it would be the slow thing happening at the moment chosen
    /// to avoid.
    /// </summary>
    private string? GameText(List<string> notes)
    {
        if (Directory.Exists(AppPaths.LanguageDir)
            && Directory.EnumerateFiles(AppPaths.LanguageDir, SponsorLanguage.FilePattern).Any())
        {
            return AppPaths.LanguageDir;
        }

        notes.Add(_languageProblem is { } why
            ? $"The game's text isn't here ({why}), so the sponsors keep their old names in game."
            : "The game's text isn't here, so the sponsors keep their old names in game.");

        return null;
    }

    /// <summary>
    /// Build it, behind the heading, and say how it went.
    ///
    /// The heading is where this app says what it is doing — OPENING YOUR
    /// PROJECT, PROJECT SAVED — so a package says it there too, with the dots
    /// that mean something is still happening. The dialog afterwards is for the
    /// detail: where the file went, and anything that did not make it in.
    /// </summary>
    private async void Write(PackageRequest request, string artwork, List<string> notes)
    {
        _packaging = true;
        Occupied(true);

        var language = request.Text is { Count: > 0 } ? GameText(notes) : null;

        Retitle("PACKAGING", "YOUR MOD", working: true);

        // The same floor the other long jobs have: a status nobody can read is
        // not a status. Merging two 33 MB archives is rarely that quick anyway.
        var floor = Task.Delay(TimeSpan.FromSeconds(Working));

        PackageResult? result = null;
        Exception? failed = null;

        try
        {
            result = await Task.Run(() => SponsorPackager.Build(
                request with { LanguageDir = language ?? "" }));
        }
        catch (Exception problem) { failed = problem; }

        await floor;

        _packaging = false;
        Occupied(false);

        // The rendered copies were only ever for the packager, and it has
        // finished with them either way.
        try { if (Directory.Exists(artwork)) Directory.Delete(artwork, recursive: true); }
        catch { /* under the temp folder, so it goes in the end regardless */ }

        if (result is null)
        {
            Retitle(ProjectTitle.First, ProjectTitle.Second);

            MessageDialog.Show(this,
                $"The mod couldn't be packaged.\n\n{failed?.Message}",
                "Package", MessageBoxButton.OK, MessageBoxImage.Error);

            return;
        }

        Announce("MOD", "PACKAGED.");

        // A dialog only for what the heading cannot say. It already says the mod
        // was packaged, and somebody who chose where it went a moment ago does
        // not need telling where it went — so a package with nothing to report
        // says nothing more. What is missing from the zip is another matter, and
        // is the whole of what is left to show.
        var all = notes.Concat(result.Warnings).ToList();
        if (all.Count == 0) return;

        var summary = $"Packaged {Count(result.CarDecals, "decal")}, "
            + $"{Count(result.MenuDecals, "menu picture")} and "
            + $"{Count(result.LanguageFiles, "language file")} to\n{result.ZipPath}";

        MessageDialog.Show(this,
            $"{summary}\n\n{string.Join('\n', all)}",
            "Package complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Count(int many, string thing) =>
        many == 1 ? $"1 {thing}" : $"{many} {thing}s";

    /// <summary>
    /// Everything off while a package is written, and back on afterwards.
    ///
    /// IsEnabled for the buttons, which dims them: a row that takes no clicks
    /// and looks exactly as it did is an app that has stopped working, where a
    /// dim row is an app that is busy. Nothing fades out — the tools leaving and
    /// coming back would read as the project closing and reopening.
    ///
    /// The shelf only stops taking clicks. A card is not a button to look at,
    /// and six dimmed sponsors would say something was wrong with them.
    /// </summary>
    /// <param name="theDoorToo">
    /// Whether the close button goes off with the rest. It does while a package
    /// is being written, where closing is refused anyway and a refusal nobody
    /// was warned about is worse than a button that says in advance it will not
    /// do it. It does not while the app is only reading the game at startup:
    /// there is nothing half-written to spoil, and no reason to keep somebody
    /// in.
    /// </param>
    private void Occupied(bool busy, bool theDoorToo = true)
    {
        Choices.IsEnabled = !busy;
        Tools.IsEnabled = !busy;
        SettingsButton.IsEnabled = !busy;
        AddButton.IsEnabled = !busy;
        ProjectTag.IsEnabled = !busy;

        Shelf.IsHitTestVisible = !busy;

        if (theDoorToo) CloseButton.IsEnabled = !busy;
    }

    // ── What goes where ─────────────────────────────────────────────────────

    /// <summary>
    /// Which decal each variation replaces, and what could not be placed.
    ///
    /// A sponsor of ours stands in for a whole sponsor of the game's, and the
    /// game's are six decals each — 106 of its 109 are, measured. So the six
    /// squares line up with the six decals in name order, first with first. The
    /// three sponsors that have fewer take as many as they have, and a variation
    /// past the end is said rather than dropped quietly.
    ///
    /// Empty variations are simply left out. Everything a package carries
    /// replaces what the game has, so shipping an untouched decal would revert
    /// it to the bundled copy and undo whatever else was installed over it.
    /// </summary>
    private (IReadOnlyList<Bound> Work, IReadOnlyList<string> Notes) PackagePlan()
    {
        var work = new List<Bound>();
        var notes = new List<string>();

        if (GameDecals is not { } catalogue)
        {
            notes.Add("The game's own decals couldn't be read, so nothing could be matched up.");
            return (work, notes);
        }

        // Which of our sponsors claimed each decal, so two claiming the same one
        // is caught here rather than settled silently by whichever was written
        // second.
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sponsor in _sponsors)
        {
            if (string.IsNullOrWhiteSpace(sponsor.Replaces))
            {
                notes.Add($"{sponsor.Label} doesn't say which sponsor it replaces, so it was left out.");
                continue;
            }

            var group = catalogue.BySponsor.FirstOrDefault(
                g => string.Equals(g.Key, sponsor.Replaces, StringComparison.OrdinalIgnoreCase));

            if (group is null)
            {
                notes.Add($"{sponsor.Label} replaces {sponsor.Replaces}, which isn't a sponsor "
                    + "in these archives — left out.");
                continue;
            }

            var decals = group.ToList();
            var placed = 0;

            for (var at = 0; at < sponsor.Variations.Count; at++)
            {
                var variation = sponsor.Variations[at];
                if (variation.IsEmpty) continue;

                if (at >= decals.Count)
                {
                    notes.Add($"{sponsor.Label}: the game's {group.Key} has only "
                        + $"{Count(decals.Count, "variation")}, so variation {at + 1} "
                        + "has nowhere to go.");
                    continue;
                }

                var decal = decals[at];

                if (claimed.TryGetValue(decal.Name, out var already))
                {
                    notes.Add($"{sponsor.Label}'s variation {at + 1} and {already} both replace "
                        + $"{decal.Name}; {already} kept it.");
                    continue;
                }

                claimed[decal.Name] = sponsor.Label;
                work.Add(new Bound(decal, variation, sponsor.Label, at + 1));
                placed++;
            }

            if (placed == 0 && sponsor.Variations.All(v => v.IsEmpty))
                notes.Add($"{sponsor.Label} has no artwork in it yet, so it was left out.");
        }

        return (work, notes);
    }

    // ── Drawings into pixels ────────────────────────────────────────────────

    /// <summary>
    /// Render every bound variation into a folder for the packager: the car's
    /// picture named after the decal, the menu's the same name under
    /// <c>menu\</c>.
    ///
    /// Each one is composed onto the whole texture at the span the variation
    /// carries — the same arithmetic the frames in the variation window draw, so
    /// what was on screen there is what lands in the game.
    /// </summary>
    private static (string Dir, IReadOnlyList<string> Skipped) Materialise(IReadOnlyList<Bound> work)
    {
        var dir = Path.Combine(Path.GetTempPath(), "simplified-sponsor-editor", "artwork",
                               Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, SponsorPackager.MenuFolderName));

        // A variation that cannot be drawn is said here, because the packager
        // only ever sees what did get written and so cannot know it is missing.
        var skipped = new List<string>();

        var car = SponsorTextures.Car;
        var menu = SponsorTextures.Menu;

        foreach (var bound in work)
        {
            try
            {
                var canvas = new ArtworkCanvas(
                    bound.Variation.SourcePath!, bound.Variation.Edits, bound.Variation.Overlay);

                // Drawn once at the car texture's resolution and used for both.
                // The menu's is a third of the size, so rendering a second time
                // for it would be work for a picture that gets scaled down
                // either way — and a vector drawn at 1024 holds more detail than
                // 340 can take.
                var longest = (int)Math.Max(car.Width, car.Height);

                if (canvas.IsEmpty || canvas.Render(longest) is not { } art)
                {
                    skipped.Add($"{bound.Sponsor}'s variation {bound.At} couldn't be drawn, "
                        + $"so {bound.Decal.Name} keeps the game's artwork.");
                    continue;
                }

                WritePng(TextureFit.Compose(art, car.Width, car.Height, bound.Variation.CarSpan),
                         Path.Combine(dir, bound.Decal.Name + ".png"));

                if (!bound.Decal.HasMenuThumbnail) continue;

                WritePng(TextureFit.Compose(art, menu.Width, menu.Height, bound.Variation.MenuSpan),
                         Path.Combine(dir, SponsorPackager.MenuFolderName, bound.Decal.Name + ".png"));
            }
            catch (Exception problem)
            {
                skipped.Add($"{bound.Sponsor}'s variation {bound.At} couldn't be prepared "
                    + $"({problem.Message}), so {bound.Decal.Name} keeps the game's artwork.");
            }
        }

        return (dir, skipped);
    }

    private static void WritePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ── The game folder ─────────────────────────────────────────────────────

    /// <summary>
    /// Where F1 25 is, or null when the question could not be answered — in
    /// which case it has already been reported.
    ///
    /// Looked up, then detected, then asked for. Asked for last because the
    /// answer is almost always findable: a person who has the game should not
    /// have to tell an app where it put itself.
    /// </summary>
    private string? GameFolder()
    {
        if (KnownGameFolder() is { } known) return known;

        var config = UserConfig.Load();

        var answer = MessageDialog.Choose(this,
            "Where F1 25 is",
            "Packaging needs your F1 25 folder.",
            "The mod is built by merging your decals into the game's own shader archives, which "
            + "aren't ours to ship — so this needs the install. It is only read, never written to.",
            ("Choose", MessageBoxResult.Yes),
            ("Cancel", MessageBoxResult.Cancel));

        if (answer != MessageBoxResult.Yes) return null;

        var picker = new OpenFolderDialog
        {
            Title = "Where F1 25 is installed",
            InitialDirectory = Directory.Exists(config.GameDir ?? "") ? config.GameDir! : "",
        };

        if (picker.ShowDialog(this) != true) return null;

        if (!Usable(picker.FolderName))
        {
            MessageDialog.Show(this,
                $"There is no {SponsorPackager.ShaderFolderName} folder in there, so this is not "
                + "the game's own folder. It is the one F1_25.exe sits in.",
                "Where F1 25 is", MessageBoxButton.OK, MessageBoxImage.Warning);

            return null;
        }

        Keep(config, picker.FolderName);
        return picker.FolderName;
    }

    /// <summary>
    /// Where F1 25 is, without asking. Looked up, then detected and remembered.
    ///
    /// Separate from <see cref="GameFolder"/> because startup needs the answer
    /// and must not have the question: an app that opened with a folder picker
    /// in front of it would be asking before it had been given anything to do.
    /// </summary>
    private static string? KnownGameFolder()
    {
        var config = UserConfig.Load();

        if (Usable(config.GameDir)) return config.GameDir;

        if (GameDirDetector.Detect() is { } found && Usable(found.Path))
        {
            Keep(config, found.Path);
            return found.Path;
        }

        return null;
    }

    /// <summary>
    /// Not merely a folder that exists: the shader package is what a mod is
    /// merged into, and its absence is the difference between the game's own
    /// folder and a folder with the game's name.
    /// </summary>
    private static bool Usable(string? dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && Directory.Exists(Path.Combine(dir, SponsorPackager.ShaderFolderName));

    private static void Keep(UserConfig config, string dir)
    {
        config.GameDir = dir;
        config.Save();
    }

    // ── Where the last one went ─────────────────────────────────────────────

    private static string LastPackageFolder()
    {
        var saved = UserConfig.Load().LastPackageFolder;
        return Directory.Exists(saved ?? "") ? saved! : "";
    }

    private static void RememberPackageFolder(string path)
    {
        try
        {
            var config = UserConfig.Load();
            config.LastPackageFolder = Path.GetDirectoryName(path);
            config.Save();
        }
        catch
        {
            // Where the last mod went is a convenience, and failing to write it
            // down is no reason to refuse to write the mod.
        }
    }
}
