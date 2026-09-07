using System.IO;
using System.Windows;
using EgoEngineLibrary.Texconv;
using SimplifiedGridEditor.Core;
using SimplifiedGridEditor.Core.Packaging;
using SimplifiedGridEditor.Core.Project;
using TeamSeason = SimplifiedTeamEditor.Core.Teams.Season;
using DriverSeason = SimplifiedDriverSwapper.Core.Drivers.Season;

namespace SimplifiedGridEditor;

/// <summary>
/// The half of the window that talks to the four tabs.
///
/// The two halves of the app came from two apps that each had their own idea of
/// what a season is — the Team Editor's is an enum, the Driver Swapper's is a
/// class carrying an entrant-list number and a seat map. Neither is wrong and
/// neither is worth rewriting, so this is where the one the window holds is
/// translated for whichever tab is asking.
/// </summary>
public partial class MainWindow
{
    private GridDocument? _document;

    /// <summary>Wire the tabs up. Called once, from the constructor.</summary>
    private void InitTabs()
    {
        // Recompute rather than latch. A tab says "look again", and what it has
        // staged is the answer — which is why opening a project no longer
        // reports unsaved changes before anything has been touched. Filling a
        // row's text boxes raises TextChanged on the way in, and latching on
        // that made a freshly-loaded project look edited.
        Teams.Changed += (_, _) => RefreshEverything();
        Seats.Changed += (_, _) => RefreshEverything();
        Drivers.Changed += (_, _) => RefreshEverything();
        Start.Changed += (_, _) => RefreshEverything();

        Teams.Said += TabSaid;
        Seats.Said += TabSaid;
        Drivers.Said += TabSaid;
        Start.Said += TabSaid;
    }

    private void TabSaid(object? sender, PanelMessage message) =>
        SetStatus(message.Text, message.IsProblem ? ErrorBrush : MutedBrush);

    /// <summary>Anything staged in a tab that a save would write.</summary>
    private bool TabsHaveChanges =>
        Teams.HasChanges || (_document?.HasChanges ?? false);

    /// <summary>Read the open project's files and hand them to the tabs.</summary>
    private void LoadProjectIntoTabs()
    {
        if (_project is null) { ClearTabs(); return; }

        try
        {
            _document = GridDocument.Read(
                _project.DatabasePath,
                Path.Combine(AppPaths.OriginalsDir, ProjectStore.DatabaseFileName),
                DriverSeasonNow);
        }
        catch (Exception ex)
        {
            _document = null;
            SimplifiedUi.MessageDialog.Show(this,
                $"The project's database couldn't be read.\n\n{ex.Message}",
                "Open project", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        Teams.Show(_project, _document?.Database, TeamSeasonNow);
        Seats.Show(_document, DriverSeasonNow);
        Drivers.Show(_document);
        Start.Show(_document, DriverSeasonNow);

        ApplyAdvancedSwapping(_config.EnableAdvancedSwapping);
        CheckGridIsWhereExpected();
    }

    /// <summary>
    /// Refuse to edit a grid this project's database doesn't have where we think.
    ///
    /// A project carries its own copy of the database, so one made before the
    /// F2 26 update still has the grids at their old list numbers — that update
    /// inserted a list and pushed the 2026 grid from 33 to 34. Editing by the
    /// new number would write a grid's worth of changes into <c>f1_beta</c> and
    /// look like it had worked, so the Grid tab is switched off instead.
    ///
    /// Teams and Drivers are unaffected: they address records by name and by
    /// driver index, neither of which the update moved.
    /// </summary>
    private void CheckGridIsWhereExpected()
    {
        if (_document?.Database is not { } database) { GridTab.IsEnabled = true; return; }

        var season = DriverSeasonNow;
        var ok = season.IsWhereExpected(database);
        GridTab.IsEnabled = ok;
        if (ok) return;

        if (ReferenceEquals(Tabs.SelectedItem, GridTab)) Tabs.SelectedIndex = 0;

        var found = season.FindIn(database);
        SetStatus($"The Grid tab is off: this project's database doesn't hold {season.Label} where expected.", ErrorBrush);
        SimplifiedUi.MessageDialog.Show(this,
            $"This project's database doesn't have the {season.Label} where this version expects it.\n\n" +
            $"Expected \"{season.ListName}\" at entrant list {season.EntrantList}, but " +
            (found is null
                ? "the database hasn't got that grid at all."
                : $"it is at {found} instead.") +
            "\n\nThat is what a project made before the F2 26 game update looks like. Editing the " +
            "grid would write over a different one, so the Grid tab has been switched off. " +
            "Teams and Drivers are safe to use.",
            "Open project", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ClearTabs()
    {
        _document = null;
        Teams.Show(null, null, TeamSeasonNow);
        Seats.Show(null, DriverSeasonNow);
        Drivers.Show(null);
        Start.Show(null, DriverSeasonNow);
    }

    private void RefreshTabs()
    {
        Seats.Refresh();
        Drivers.Refresh();
        Start.Refresh();
    }

    private void ShowSeasonInTabs()
    {
        Teams.ShowSeason(TeamSeasonNow);
        Seats.ShowSeason(DriverSeasonNow);
        Start.Show(_document, DriverSeasonNow);

        // Each grid sits in its own list, so switching season asks the question
        // again — an old database can hold one of them where we expect and not
        // the other.
        CheckGridIsWhereExpected();
    }

    private void ApplyAdvancedSwapping(bool show) => Seats.ShowAdvanced(show);

    /// <summary>
    /// What the Teams tab is showing. F2 never changes it: that tab edits the
    /// F1 outfits either way, and is simply switched off while F2 is selected.
    /// </summary>
    private TeamSeason TeamSeasonNow => _season;

    /// <summary>
    /// What the Grid tab is showing — the F2 grid when it is selected, otherwise
    /// the F1 grid for the season the chips are on.
    /// </summary>
    private DriverSeason DriverSeasonNow =>
        _f2 ? DriverSeason.F2_2026
        : _season == TeamSeason.Y2026 ? DriverSeason.F2026
        : DriverSeason.F2025;

    // ── Writing the edits into the project ──────────────────────────────────

    /// <summary>
    /// Turn what the tabs are holding into the project's own files.
    ///
    /// This is the difference between the two apps this came from and this one.
    /// They keep edits in memory and materialise them only when a package is
    /// written, because they have nowhere else to put them. A project does: the
    /// files in it are the working copy, so Save is where the edits land, and a
    /// package is then just those files at their in-game paths.
    /// </summary>
    private void ApplyEditsToProject()
    {
        if (_project is null) return;

        var notes = new List<string>();
        GridPackager.ApplyToProject(
            _project,
            _document,
            Seats.Kit,
            Teams.EditsFor(TeamSeason.Y2025),
            Teams.EditsFor(TeamSeason.Y2026),
            Teams.LogoEdits,
            new TexconvRunner(),
            notes);

        if (notes.Count > 0)
            SimplifiedUi.MessageDialog.Show(this,
                "The project was saved, with these notes:" + Environment.NewLine + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", notes),
                "Save", MessageBoxButton.OK, MessageBoxImage.Information);

        // Everything the tabs were holding is now in the files, so they read it
        // back rather than keeping a stale copy of what they just wrote.
        LoadProjectIntoTabs();
    }

    // ── Packaging ───────────────────────────────────────────────────────────

    private void PackageProject()
    {
        if (_project is null) return;

        var request = PackageDialog.Show(
            this, _project.Info.Name.Length > 0 ? _project.Info.Name : _project.DisplayName,
            _project.Info.Version, _config.LastPackageFolder);
        if (request is null) return;

        _project.Info.Version = request.Version;
        _config.LastPackageFolder = Path.GetDirectoryName(request.Path);
        _config.Save();

        // Package what is in the project, so what ships is what was saved —
        // which means anything staged in a tab has to be written down first.
        if (!SaveProject(saveAs: false)) return;

        try
        {
            var result = GridPackager.Write(request.Path, _project, _config.GameDir);

            SetStatus(GridPackager.Describe(result, request.Path), OkBrush);

            if (result.Warnings.Count > 0)
                SimplifiedUi.MessageDialog.Show(this,
                    "The package was written, with these notes:\n\n  " + string.Join("\n  ", result.Warnings),
                    "Package", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"The package could not be written.\n\n{ex.Message}",
                "Package", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
