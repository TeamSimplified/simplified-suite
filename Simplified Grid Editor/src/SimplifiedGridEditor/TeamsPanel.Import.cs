using System.IO;
using System.Windows;
using EgoEngineLibrary.Texconv;
using SimplifiedGridEditor.Core;
using SimplifiedGridEditor.Core.Project;
using SimplifiedTeamEditor.Core.Logos;
using SimplifiedUi;
using Season = SimplifiedTeamEditor.Core.Teams.Season;

namespace SimplifiedGridEditor;

/// <summary>
/// Taking team logos out of somebody else's archive.
///
/// A mod that ships as an archive rather than as the pictures it was built from
/// used to be a dead end: the logos were in there, in arrays this app knows the
/// layout of, and the only way to get one was to install the mod and screenshot
/// it. Now the archive is read, every slice that differs from the game's own is
/// named by the team and the year it belongs to, and the chosen ones come out
/// as pictures the project stores like any other.
///
/// Both of the archives this is pointed at in practice work: a mod's own
/// <c>ui_texarrays_team_branding.erp</c>, and a <c>ui_texture</c> shader
/// package a launcher has already merged one into, since merging puts the
/// arrays in there whole.
/// </summary>
public partial class TeamsPanel
{
    /// <summary>Ask for an archive, and take the chosen logos out of it.</summary>
    public void ImportLogos()
    {
        if (_project is null) return;

        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an archive to take logos from",
            Filter = "ERP archive (*.erp)|*.erp",
        };
        if (picker.ShowDialog(Host) != true) return;

        var pristine = ProjectStore.PristineBrandingPath(AppPaths.OriginalsDir);
        var runner = new TexconvRunner();

        LogoSource source;
        try
        {
            source = LogoSource.Read(picker.FileName, pristine);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(Host,
                $"{Path.GetFileName(picker.FileName)} couldn't be read: {ex.Message}",
                "Import logos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Nothing to choose from is not a dialog with an empty list in it.
        if (source.Logos.Count == 0)
        {
            MessageDialog.Show(Host, ImportLogosDialog.Summarise(source),
                "Import logos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ImportLogosDialog.Show(Host, source) is not { Count: > 0 } wanted) return;

        // Beside the project's working files rather than inside the project:
        // these are chosen pictures, and a chosen picture is only copied into
        // the project when it is saved, the same as one picked off the desktop.
        var into = Path.Combine(_project.WorkDir, "imported");

        var taken = 0;
        var failed = new List<string>();

        foreach (var logo in wanted)
        {
            string? png = null;
            string? why = null;

            try { png = source.Extract(logo, into, runner); }
            catch (Exception ex) { why = ex.Message; }

            if (png is null)
            {
                failed.Add($"{Describe(logo)}: {why ?? "the picture couldn't be written"}");
                continue;
            }

            if (!_rowsBySeason.TryGetValue(logo.Season, out var rows)
                || rows.FirstOrDefault(r => r.Team.Id == logo.TeamId) is not { } row)
            {
                failed.Add($"{Describe(logo)}: that team isn't on this project's "
                           + $"{(logo.Season == Season.Y2026 ? "2026" : "2025")} list");
                continue;
            }

            row.Logos[logo.Kind.Id] = png;
            taken++;
        }

        BuildRows();
        UpdateModifiedCount();

        Say(taken == 0
            ? "Nothing was imported."
            : $"{taken} logo(s) imported from {Path.GetFileName(source.Path)}. Save to keep them.",
            problem: taken == 0);

        if (failed.Count > 0)
            MessageDialog.Show(Host,
                "These couldn't be imported:" + Environment.NewLine + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", failed),
                "Import logos", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string Describe(FoundLogo logo) =>
        $"{logo.Team?.Label ?? logo.TeamId} {logo.Kind.Label}";
}
