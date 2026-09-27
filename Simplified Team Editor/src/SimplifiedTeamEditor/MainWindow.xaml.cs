using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SimplifiedTeamEditor.Core;
using SimplifiedTeamEditor.Core.Config;
using EgoEngineLibrary.Data.GameDatabases;
using SimplifiedTeamEditor.Core.Lng;
using SimplifiedTeamEditor.Core.Logos;
using SimplifiedTeamEditor.Core.Packaging;
using EgoEngineLibrary.Texconv;
using SimplifiedTeamEditor.Core.Teams;
using Season = SimplifiedTeamEditor.Core.Teams.Season;
using SimplifiedTeamEditor.Model;
using SimplifiedTeamEditor.Util;
using Simplified.Shared;

namespace SimplifiedTeamEditor;

public partial class MainWindow : Window
{
    private readonly List<TeamRow> _rows = new();

    /// <summary>The team database, held so a colour edit can be written back into it.</summary>
    private GameDatabase? _database;

    /// <summary>
    /// Which grid is being edited. Each season keeps its own rows, so switching
    /// back and forth doesn't lose edits made to the other one.
    /// </summary>
    private Season _season = Season.Y2026;

    private readonly Dictionary<Season, List<TeamRow>> _rowsBySeason = new();

    private LogoPreviewCache? _previews;

    /// <summary>Made on first use: building one reads an 18 MB archive.</summary>
    private LogoPreviewCache Previews => _previews ??= new LogoPreviewCache(AssetsDir, new TexconvRunner());

    /// <summary>
    /// Every row's modified dot, so the marker can be refreshed without walking
    /// the visual tree looking for it.
    /// </summary>
    private readonly Dictionary<TeamRow, UIElement> _modifiedDots = new();

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);

        VersionLabel.Text = AppVersion.Display;
        ApplyWindowIcon();
        LoadTeams();
    }

    private static string AssetsDir => Path.Combine(AppPaths.Root, "runtime", "original");

    private static string SeasonLabel(Season season) => season == Season.Y2026 ? "2026" : "2025";

    /// <summary>Switch the list to a season, keeping the other one's edits.</summary>
    private void ShowSeason(Season season)
    {
        // A picker open on a team belongs to the season being left, so it
        // cannot stay: changing year drops back to that year's list.
        ClosePicker();

        _season = season;
        _rows.Clear();
        _rows.AddRange(_rowsBySeason.GetValueOrDefault(season) ?? new List<TeamRow>());

        Season2025Chip.IsChecked = season == Season.Y2025;
        Season2026Chip.IsChecked = season == Season.Y2026;

        BuildRows();
        UpdateModifiedCount();
    }

    private void Season_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowSeason(ReferenceEquals(sender, Season2026Chip) ? Season.Y2026 : Season.Y2025);
    }

    // ─────────────────────────── Loading ──────────────────────────────

    /// <summary>The install, as Settings has it or as it can be found.</summary>
    private static string? GameDir =>
        UserConfig.Load().GameDir ?? GameDirDetector.Detect()?.Path;

    /// <summary>
    /// Take the language files out of the game, then load the teams from them.
    ///
    /// Off the window's thread, because the first run on a build is about half
    /// a minute and the window is already up and painted by then. Every run
    /// after it is immediate, so this path is the exception rather than the
    /// cost of opening the app.
    /// </summary>
    private async void LoadTeamsWhenExtracted()
    {
        var gameDir = GameDir;
        if (gameDir is null)
        {
            StatusLine.Text = "Set the game folder in Settings — team names are read out of the game's exe.";
            return;
        }

        StatusLine.Text = "Taking the language files out of the game — this happens once per game update…";
        try
        {
            await Task.Run(() => Simplified.Language.LanguageFiles.EnsureAsync(
                gameDir, AppPaths.LanguageCacheDir));

            LoadTeams();
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"The game's language files couldn't be read: {ex.Message}";
        }
    }

    /// <summary>
    /// English is what the editor shows and edits. A change applies to every
    /// language when it is packaged — the other ten files get the same value,
    /// because a renamed team is renamed in all of them.
    /// </summary>
    private void LoadTeams()
    {
        // The language files live inside the game's exe, and getting them out
        // the first time on a given build means searching it end to end. A
        // window opening has no business doing that, so it asks whether the
        // work has already been done and, if not, has it done elsewhere and
        // comes back — see LoadTeamsWhenExtracted.
        var folder = Simplified.Language.LanguageFiles.Cached(GameDir, AppPaths.LanguageCacheDir);
        if (folder is null)
        {
            LoadTeamsWhenExtracted();
            return;
        }

        var path = Path.Combine(folder, "language_eng.lng");
        if (!File.Exists(path))
        {
            StatusLine.Text = $"Base language file not found: {path}";
            return;
        }

        try
        {
            var lng = LngFile.Load(path);

            // 2026's keys are absent from a stock game, so a name that reads
            // back empty is filled from the bundled defaults - that is what it
            // will say in-game once the key is added.
            var defaults = Season2026.LoadDefaults(AssetsDir);

            foreach (var season in new[] { Season.Y2025, Season.Y2026 })
            {
                var rows = new List<TeamRow>();
                foreach (var names in TeamNameEditing.Read(lng, season))
                {
                    if (season == Season.Y2026)
                    {
                        if (names.Full.Length == 0) names.Full = defaults.GetValueOrDefault(names.Team.FullKey, "");
                        if (names.Short.Length == 0) names.Short = defaults.GetValueOrDefault(names.Team.ShortKey, "");
                        if (names.SuperShort.Length == 0) names.SuperShort = defaults.GetValueOrDefault(names.Team.SuperShortKey, "");
                    }
                    rows.Add(TeamRow.From(names));
                }
                _rowsBySeason[season] = rows;
            }

            var colourNote = LoadColours();

            ShowSeason(_season);
            StatusLine.Text = $"Read from {Path.GetFileName(path)} ({lng.Entries.Count:N0} entries). {colourNote}";
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"Couldn't read the language file: {ex.Message}";
        }
    }

    /// <summary>
    /// Read the colours in beside the names. Returns a line for the status bar,
    /// since a missing or unreadable database is worth saying out loud rather
    /// than leaving the colour column mysteriously empty.
    /// </summary>
    private string LoadColours()
    {
        // The installed game's own database when it is one we recognise, and
        // the bundled copy otherwise. The same choice the packager makes, for
        // the same reason: what is shown here has to be what a package would be
        // built from, or the colours on screen are not the colours that ship.
        var choice = Simplified.Shared.GameDatabaseSource.Resolve(
            UserConfig.Load().GameDir ?? GameDirDetector.Detect()?.Path,
            Path.Combine(AppPaths.Root, "runtime", "original", "gamedatabases.2025.bin"));

        var path = choice.Path;
        if (!File.Exists(path)) return "Team database not found — colours unavailable.";

        try
        {
            _database = GameDatabase.Load(path);

            var notes = new List<string>();
            foreach (var (season, rows) in _rowsBySeason)
            {
                var byTeam = TeamColours.ReadAll(_database, season).ToDictionary(c => c.TeamId);
                foreach (var row in rows)
                {
                    if (!byTeam.TryGetValue(row.Team.Id, out var set)) continue;
                    row.DatabaseIndex = set.Index;
                    foreach (var (field, hex) in set.Colours)
                    {
                        row.Colours[field] = hex;
                        row.OriginalColours[field] = hex;
                    }
                }
                var found = rows.Count(r => r.DatabaseIndex is not null);
                notes.Add($"{SeasonLabel(season)} {found}/{rows.Count}");
            }

            return "Colours: " + string.Join(", ", notes) + ".";
        }
        catch (Exception ex)
        {
            return $"Team database unreadable: {ex.Message}";
        }
    }

    /// <summary>
    /// A team's four colours as swatches. Click one to change it; right-click
    /// puts it back, which is where the launcher keeps that too.
    /// </summary>
    private UIElement BuildColours(TeamRow row)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        foreach (var field in TeamColours.Fields)
        {
            if (!row.Colours.TryGetValue(field, out var hex)) continue;

            var swatch = new Button
            {
                Width = 30,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(0),
                ToolTip = $"{TeamColours.FieldLabel(field)} — {hex}\nRight-click to reset",
                Tag = field,
            };

            // A template of its own: the point of the button is the colour, and
            // the themed button chrome would sit on top of it.
            swatch.Template = SwatchTemplate();
            swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            swatch.BorderBrush = row.IsColourModified(field)
                ? (Brush)FindResource("Accent")
                : (Brush)FindResource("BorderStrong");
            swatch.BorderThickness = new Thickness(row.IsColourModified(field) ? 2 : 1);

            swatch.Click += (_, _) => EditColour(row, field);
            swatch.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                row.ResetColour(field);
                BuildRows();
                UpdateModifiedCount();
            };

            strip.Children.Add(swatch);
        }

        return strip;
    }

    private static ControlTemplate SwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty,
            new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty,
            new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    /// <summary>
    /// Ask for a new colour as hex. A full picker is a later job; this accepts
    /// what a colour is actually copied and pasted as.
    /// </summary>
    private void EditColour(TeamRow row, ColourField field)
    {
        var current = row.Colours.GetValueOrDefault(field, "#000000");
        var entered = ColourDialog.Show(this, $"{row.Team.Label} — {TeamColours.FieldLabel(field)}", current);
        if (entered is null || entered == current) return;

        row.Colours[field] = entered;
        BuildRows();
        UpdateModifiedCount();
    }

    // ────────────────────────── Modified state ────────────────────────

    private void UpdateModifiedCount()
    {
        var here = _rows.Count(r => r.IsModified);
        var elsewhere = _rowsBySeason
            .Where(kv => kv.Key != _season)
            .Sum(kv => kv.Value.Count(r => r.IsModified));

        var text = here switch
        {
            0 => "No changes",
            1 => "1 team changed",
            _ => $"{here} teams changed",
        };
        if (elsewhere > 0)
            text += $"  ·  {elsewhere} in {SeasonLabel(_season == Season.Y2026 ? Season.Y2025 : Season.Y2026)}";

        ModifiedCount.Text = text;
        ResetAllButton.IsEnabled = here > 0;
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Reset();
        BuildRows();
        UpdateModifiedCount();
    }

    // ────────────────────────── Packaging ─────────────────────────────

    /// <summary>
    /// A season's edits in the form the packager wants: every team's names, the
    /// ones that actually changed, and the colours that changed.
    ///
    /// The distinction matters. Unchanged names are left out so a package only
    /// carries what the user decided, rather than restating the game's own text
    /// as though it were an edit.
    /// </summary>
    private SeasonEdits EditsFor(Season season)
    {
        var rows = _rowsBySeason.GetValueOrDefault(season) ?? new List<TeamRow>();

        var colours = new Dictionary<string, Dictionary<ColourField, string>>();
        foreach (var row in rows)
        {
            var changed = row.Colours
                .Where(c => row.IsColourModified(c.Key))
                .ToDictionary(c => c.Key, c => c.Value);
            if (changed.Count > 0) colours[row.Team.Id] = changed;
        }

        return new SeasonEdits(
            season,
            rows.Select(r => r.ToNames()).ToList(),
            rows.Where(r => r.NamesModified).Select(r => r.ToNames()).ToList(),
            colours);
    }

    private void Package_Click(object sender, RoutedEventArgs e)
    {
        var y2025 = EditsFor(Season.Y2025);
        var y2026 = EditsFor(Season.Y2026);

        if (!y2025.Any && !y2026.Any)
        {
            SimplifiedUi.MessageDialog.Show(this, "Nothing has been changed yet, so there is nothing to package.",
                "Package", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var config = UserConfig.Load();
        var request = PackageDialog.Show(this, config.DefaultAuthor, config.LastPackageFolder);
        if (request is null) return;

        var info = request.Info;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;

            var runner = new TexconvRunner();
            var result = TeamPackager.Write(
                request.Path, AssetsDir, y2025, y2026, info,
                CollectLogoEdits(), config.GameDir ?? GameDirDetector.Detect()?.Path,
                AppPaths.LanguageCacheDir, runner);

            config.DefaultAuthor = info.Author;
            config.LastPackageFolder = Path.GetDirectoryName(request.Path);
            try { config.Save(); } catch { /* a read-only app folder shouldn't lose the package */ }

            StatusLine.Text = Describe(result, request.Path);

            if (result.Warnings.Count > 0)
                SimplifiedUi.MessageDialog.Show(this,
                    "The package was written, with these notes:\n\n  " + string.Join("\n  ", result.Warnings),
                    "Package", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this, $"The package could not be written.\n\n{ex.Message}",
                "Package", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>
    /// Every chosen picture, as the slice of the array it belongs in. A team
    /// with no slot for a kind cannot have chosen one, so a missing slice here
    /// would be a bug rather than a case to handle.
    /// </summary>
    private List<LogoEdit> CollectLogoEdits()
    {
        var edits = new List<LogoEdit>();
        foreach (var (season, rows) in _rowsBySeason)
            foreach (var row in rows)
                foreach (var (kindId, png) in row.Logos)
                {
                    var kind = TeamLogos.Kind(kindId);
                    var slice = LogoSlices.SliceFor(kind, season, row.Team.Id);
                    if (slice is not null) edits.Add(new LogoEdit(kind, slice.Value, png));
                }
        return edits;
    }

    private static string Describe(PackageResult result, string path)
    {
        var parts = new List<string>();
        if (result.LanguageFiles > 0) parts.Add($"{result.LanguageFiles} language files");
        if (result.Keys2026Added > 0) parts.Add($"{result.Keys2026Added} 2026 keys");
        if (result.Colours2025 + result.Colours2026 > 0)
            parts.Add($"{result.Colours2025 + result.Colours2026} colours");
        if (result.Reassigned2026 > 0) parts.Add($"{result.Reassigned2026} 2026 name fields repointed");
        if (result.ShaderPackages > 0) parts.Add($"{result.ShaderPackages} shader packages");

        var size = new FileInfo(path).Length / 1024.0 / 1024.0;
        return $"Wrote {Path.GetFileName(path)} ({size:N1} MB): "
             + (parts.Count > 0 ? string.Join(", ", parts) : "nothing") + ".";
    }


    // ───────────────────────────── Chrome ─────────────────────────────

    /// <summary>
    /// Title bar and Alt-Tab. The exe's Explorer icon comes from
    /// &lt;ApplicationIcon&gt; at build time and doesn't need this.
    ///
    /// IconBitmapDecoder, so WPF gets the largest frame in the .ico. A
    /// BitmapImage(uri) picks one frame at decode time — often the 16x16 —
    /// which Windows then upscales into something visibly rough.
    /// </summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var path = Path.Combine(AppPaths.Root, "icon.ico");
            if (!File.Exists(path)) return;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var decoder = new IconBitmapDecoder(
                fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch
        {
            // Cosmetic. The window still works with the default icon.
        }
    }

    /// <summary>
    /// The few things this app remembers between runs.
    ///
    /// The palette is applied while the dialog is open, so it needs no reaction
    /// here; the game folder is read fresh wherever it is used, so it needs
    /// none either. Nothing on screen depends on any of it.
    /// </summary>
    private void Settings_Click(object sender, RoutedEventArgs e) =>
        TeamSettings.Show(this, UserConfig.Load());

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Keep the middle button showing what it will do. Double-clicking the title
    /// bar maximises too — that's WindowChrome's doing, not ours — so the glyph
    /// has to follow the state rather than the click.
    /// </summary>
    private void Window_StateChanged(object sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // Escapes rather than pasted glyphs: these are Segoe MDL2 private-use
        // codepoints, which render as tofu in most editors and don't survive
        // every re-encode.
        WindowMaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        WindowMaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }
}
