using System.IO;
using System.Windows;
using System.Windows.Controls;
using EgoEngineLibrary.Data.GameDatabases;
using EgoEngineLibrary.Texconv;
using SimplifiedGridEditor.Core.Project;
using SimplifiedGridEditor.Model;
using SimplifiedTeamEditor.Core.Lng;
using SimplifiedTeamEditor.Core.Logos;
using SimplifiedTeamEditor.Core.Packaging;
using SimplifiedTeamEditor.Core.Teams;
using Season = SimplifiedTeamEditor.Core.Teams.Season;

namespace SimplifiedGridEditor;

/// <summary>
/// The Teams tab: every team on the season's grid, its three names, its four
/// colours and its logo.
///
/// Lifted from the Team Editor, whose whole window this was. Two things differ.
/// The names come from the project's own <c>language_eng.lng</c> rather than
/// from the bundled originals, because a project carries its own copy and that
/// is the copy being edited. And the colours are read from — and written back
/// to — the project's database, which is the same file the Grid and Drivers
/// tabs are editing.
/// </summary>
public partial class TeamsPanel : UserControl
{
    private readonly List<TeamRow> _rows = new();

    /// <summary>
    /// Which grid is being edited. Each season keeps its own rows, so switching
    /// back and forth doesn't lose edits made to the other one.
    /// </summary>
    private Season _season = Season.Y2026;

    private readonly Dictionary<Season, List<TeamRow>> _rowsBySeason = new();

    /// <summary>
    /// Every row's modified dot, so the marker can be refreshed without walking
    /// the visual tree looking for it.
    /// </summary>
    private readonly Dictionary<TeamRow, UIElement> _modifiedDots = new();

    private GridProject? _project;

    /// <summary>The project's database, held so a colour edit can be written back into it.</summary>
    private GameDatabase? _database;

    private LogoPreviewCache? _previews;

    /// <summary>Made on first use: building one reads an 18 MB archive.</summary>
    private LogoPreviewCache Previews => _previews ??= new LogoPreviewCache(BrandingErpPath, new TexconvRunner());

    /// <summary>
    /// The team-branding texarray, in the project's own ERPs folder. That is
    /// the copy a logo edit is previewed against, so a project carries its
    /// logos the way it carries its names and colours.
    /// </summary>
    private string BrandingErpPath => _project?.BrandingErpPath ?? "";

    public TeamsPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The window this tab is sitting in. Dialogs opened from a tab still belong
    /// to the window, not to the tab: an owner that isn't a Window leaves them
    /// unparented, free to fall behind the app.
    /// </summary>
    private Window Host => Window.GetWindow(this)!;

    /// <summary>Raised whenever an edit is made, so the window can mark the project dirty.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised with a line worth putting on the status bar.</summary>
    public event EventHandler<PanelMessage>? Said;

    /// <summary>True when any team, in either season, has been edited.</summary>
    public bool HasChanges => _rowsBySeason.Values.Any(rows => rows.Any(r => r.IsModified));

    /// <summary>How many teams in the season on screen have been edited.</summary>
    public int ModifiedHere => _rows.Count(r => r.IsModified);

    /// <summary>Point the panel at a project, or at nothing.</summary>
    public void Show(GridProject? project, GameDatabase? database, Season season)
    {
        _project = project;
        _database = database;
        _season = season;

        // A new project means new files behind the previews.
        _previews = null;
        _rowsBySeason.Clear();
        _rows.Clear();
        _modifiedDots.Clear();

        if (project is null)
        {
            TeamList.Children.Clear();
            ClosePicker();
            return;
        }

        LoadTeams();
    }

    /// <summary>Switch which season's teams are shown, keeping the other's edits.</summary>
    public void ShowSeason(Season season)
    {
        // A picker open on a team belongs to the season being left, so it
        // cannot stay: changing year drops back to that year's list.
        ClosePicker();

        _season = season;
        _rows.Clear();
        _rows.AddRange(_rowsBySeason.GetValueOrDefault(season) ?? new List<TeamRow>());
        BuildRows();
    }

    /// <summary>Put every team in the season on screen back as the project had it.</summary>
    public void ResetAll()
    {
        foreach (var row in _rows) row.Reset();
        BuildRows();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A season's edits in the form the packager wants: every team's names, the
    /// ones that actually changed, and the colours that changed.
    ///
    /// The distinction matters. Unchanged names are left out so a package only
    /// carries what the user decided, rather than restating the game's own text
    /// as though it were an edit.
    /// </summary>
    public SeasonEdits EditsFor(Season season)
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

    /// <summary>
    /// Every chosen picture, as the slice of the array it belongs in. A team
    /// with no slot for a kind cannot have chosen one, so a missing slice here
    /// would be a bug rather than a case to handle.
    /// </summary>
    public IReadOnlyList<LogoEdit> LogoEdits
    {
        get
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
    }

    /// <summary>
    /// English is what the editor shows and edits. A change applies to every
    /// language when it is packaged — the other ten files get the same value,
    /// because a renamed team is renamed in all of them.
    /// </summary>
    private void LoadTeams()
    {
        if (_project is null) return;

        var path = Path.Combine(_project.LanguageFolder, "language_eng.lng");
        if (!File.Exists(path))
        {
            Say($"This project has no language_eng.lng, so team names can't be shown.", problem: true);
            return;
        }

        try
        {
            var lng = LngFile.Load(path);

            // 2026's keys are absent from a stock game, so a name that reads
            // back empty is filled from the project's defaults - that is what it
            // will say in-game once the key is added.
            var defaults = Season2026.LoadDefaults(_project.LanguageFolder);

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
            Say($"{lng.Entries.Count:N0} language entries. {colourNote}", problem: false);
        }
        catch (Exception ex)
        {
            Say($"The project's language file couldn't be read: {ex.Message}", problem: true);
        }
    }

    /// <summary>
    /// Read the colours in beside the names. Returns a line for the status bar,
    /// since a database that has no team records is worth saying out loud rather
    /// than leaving the colour column mysteriously empty.
    /// </summary>
    private string LoadColours()
    {
        if (_database is null) return "The project has no database — colours unavailable.";

        try
        {
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
            return $"The project's database is unreadable: {ex.Message}";
        }
    }

    private static string SeasonLabel(Season season) => season == Season.Y2026 ? "2026" : "2025";

    /// <summary>
    /// Ask for a new colour as hex. A full picker is a later job; this accepts
    /// what a colour is actually copied and pasted as.
    /// </summary>
    private void EditColour(TeamRow row, ColourField field)
    {
        var current = row.Colours.GetValueOrDefault(field, "#000000");
        var entered = ColourDialog.Show(Window.GetWindow(this)!, $"{row.Team.Label} — {TeamColours.FieldLabel(field)}", current);
        if (entered is null || entered == current) return;

        row.Colours[field] = entered;
        BuildRows();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// What the Team Editor called UpdateModifiedCount. The count belongs to the
    /// window here — it owns the status bar — so this is the one line that tells
    /// it something changed.
    /// </summary>
    private void UpdateModifiedCount() => Changed?.Invoke(this, EventArgs.Empty);

    private void Say(string message, bool problem) =>
        Said?.Invoke(this, new PanelMessage(message, problem));

    private void Say(string message) => Say(message, problem: false);
}
