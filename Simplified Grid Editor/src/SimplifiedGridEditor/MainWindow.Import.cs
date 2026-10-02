using System.IO;
using System.Windows;
using SimplifiedGridEditor.Core;
using SimplifiedGridEditor.Core.Project;

namespace SimplifiedGridEditor;

/// <summary>
/// Bringing somebody else's work in.
///
/// One button and one file dialog, because what to do with a file is a
/// property of the file. A mod is distributed as the game's own files with
/// changes in them, so each kind of file answers a different part of the
/// project: an archive the logos, a database the teams, seats, drivers and
/// menus, a language file the names.
///
/// What comes in is read as a difference from the game's own copy — the same
/// way a project reads its own changes — classified by the same map that
/// decides what a package carries, and offered a part at a time. A mod whose
/// colours are worth having and whose grid is not is the usual case.
/// </summary>
public partial class MainWindow
{
    private void ImportFile()
    {
        if (_project is null) return;

        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a file to import from",
            Filter = "Anything importable|*.erp;*.bin;*.lng"
                   + "|Mod archive, for team logos (*.erp)|*.erp"
                   + "|Game database, for teams, seats and drivers (*.bin)|*.bin"
                   + "|Language file, for team names (*.lng)|*.lng",
        };
        if (picker.ShowDialog(this) != true) return;

        var path = picker.FileName;

        if (ProjectImport.IsDatabase(path)) ImportChanges(path, database: true);
        else if (ProjectImport.IsLanguage(path)) ImportChanges(path, database: false);
        else Teams.ImportLogos(path);
    }

    /// <summary>
    /// The database and the language files, which are read the same way and
    /// differ only in what they are compared against.
    /// </summary>
    private void ImportChanges(string path, bool database)
    {
        if (_project is null) return;

        var name = Path.GetFileName(path);

        var pristine = database
            ? Simplified.Shared.GameDatabaseSource.Resolve(
                _config.GameDir, Path.Combine(AppPaths.OriginalsDir, ProjectStore.DatabaseFileName)).Path
            : Path.Combine(AppPaths.OriginalsDir,
                Simplified.Language.LanguageFiles.FolderName, name);

        if (!File.Exists(pristine))
        {
            SimplifiedUi.MessageDialog.Show(this,
                database
                    ? "The game's own database isn't here to compare against, so there is nothing to read "
                      + $"{name} as a change to."
                    : $"The game has no {name} to compare against. A language file has to be one of the "
                      + "game's eleven, under the name the game gives it.",
                "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ImportScan scan;
        try
        {
            scan = SimplifiedUi.Busy.Run(this, StatusSpinner, StatusLabel, $"Reading {name}",
                _ => database
                    ? ProjectImport.ScanDatabase(path, pristine, _project.LanguageFolder)
                    : ProjectImport.ScanLanguage(path, pristine, _project.LanguageFolder));
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"{name} couldn't be read.\n\n{ex.Message}",
                "Import", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!scan.Any)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"{name} is the same as the game's own, so there is nothing in it to import.",
                "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var heading =
            $"{name} changes {Count(scan)} from the game's own. Tick what to bring into this project — "
            + "anything already here that it touches is overwritten."
            + (database ? "" : " A name goes into every language, which is how this editor stores one.");

        if (ImportPartsDialog.Show(this, heading, scan.Counts) is not { Count: > 0 } wanted) return;

        // Saved first, for the same reason Package saves first: the tabs are
        // rebuilt from the project's files afterwards, so anything still
        // sitting in them has to be written down or it would be lost.
        if (!SaveProject(saveAs: false)) return;

        try
        {
            var landed = SimplifiedUi.Busy.Run(this, StatusSpinner, StatusLabel, $"Importing from {name}",
                _ => ProjectImport.Apply(_project, scan.Wanted(wanted)));

            LoadProjectIntoTabs();
            MarkDirty();

            SetStatus(landed == 0
                ? $"Nothing came in from {name}."
                : $"{landed} change(s) imported from {name}. Save to keep them.",
                landed == 0 ? MutedBrush : OkBrush);
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"The import didn't finish.\n\n{ex.Message}",
                "Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Count(ImportScan scan)
    {
        var parts = new List<string>();
        if (scan.Changes.Database.Count > 0) parts.Add($"{scan.Changes.Database.Count} record(s)");
        if (scan.Changes.Text.Count > 0) parts.Add($"{scan.Changes.Text.Count} entry(s)");
        return string.Join(" and ", parts);
    }
}
