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
/// The half of the window that talks to the three tabs.
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
        Teams.Changed += (_, _) => MarkDirty();
        Seats.Changed += (_, _) => MarkDirty();
        Drivers.Changed += (_, _) => MarkDirty();

        Teams.Said += TabSaid;
        Seats.Said += TabSaid;
        Drivers.Said += TabSaid;
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
                DriverSeasonFor(_season));
        }
        catch (Exception ex)
        {
            _document = null;
            SimplifiedUi.MessageDialog.Show(this,
                $"The project's database couldn't be read.\n\n{ex.Message}",
                "Open project", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        Teams.Show(_project, _document?.Database, TeamSeasonFor(_season));
        Seats.Show(_document, DriverSeasonFor(_season));
        Drivers.Show(_document);

        ApplyAdvancedSwapping(_config.EnableAdvancedSwapping);
    }

    private void ClearTabs()
    {
        _document = null;
        Teams.Show(null, null, TeamSeasonFor(_season));
        Seats.Show(null, DriverSeasonFor(_season));
        Drivers.Show(null);
    }

    private void RefreshTabs()
    {
        Seats.Refresh();
        Drivers.Refresh();
    }

    private void ShowSeasonInTabs()
    {
        Teams.ShowSeason(TeamSeasonFor(_season));
        Seats.ShowSeason(DriverSeasonFor(_season));
    }

    private void ApplyAdvancedSwapping(bool show) => Seats.ShowAdvanced(show);

    private static TeamSeason TeamSeasonFor(TeamSeason season) => season;

    private static DriverSeason DriverSeasonFor(TeamSeason season) =>
        season == TeamSeason.Y2026 ? DriverSeason.F2026 : DriverSeason.F2025;

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
