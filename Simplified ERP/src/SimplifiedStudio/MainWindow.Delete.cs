using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EgoEngineLibrary.Archive.Erp;
using SimplifiedStudio.Model;

namespace SimplifiedStudio;

/// <summary>
/// Taking a resource out of the archive.
///
/// This app only. Studio shares the preview panel with it but not this: Studio
/// builds a mod out of a project, where what reaches an archive is decided by
/// the project rather than by a row somebody removed.
///
/// Nothing is written to disk here. A deletion marks the archive dirty and Save
/// is what commits it, so the way out of a mistake is the same as for every
/// other edit: Ctrl+Z, or close without saving.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Right-clicking a row selects it, unless it is already part of the
    /// selection — in which case the selection stands, so a menu opened over
    /// one of several rows still acts on all of them.
    /// </summary>
    private void TextureList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = RowUnder(e.OriginalSource as DependencyObject);
        if (row is null) return;

        if (!row.IsSelected)
        {
            TextureList.SelectedItems.Clear();
            row.IsSelected = true;
        }

        // Focus follows the click, or the keyboard would still be on whatever
        // was selected before.
        row.Focus();
    }

    /// <summary>
    /// No menu on empty space. A right-click below the last row would otherwise
    /// offer to delete whatever happened to be selected, which is not what the
    /// pointer is over.
    /// </summary>
    private void TextureList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var selected = SelectedResources();
        if (selected.Count == 0) { e.Handled = true; return; }

        var images = CompanionCount(selected);

        DeleteResourceMenuItem.Header = selected.Count == 1
            ? (images > 0 ? "Delete  (with its image)" : "Delete")
            : $"Delete {selected.Count} resources"
              + (images > 0 ? $"  (with {images} image{(images == 1 ? "" : "s")})" : "");
    }

    private void DeleteResources_Click(object sender, RoutedEventArgs e)
    {
        if (!HasErp) return;

        var selected = SelectedResources();
        if (selected.Count == 0) return;

        var going = Gather(selected, out var companions);
        if (going.Count == 0) return;

        var what = selected.Count == 1
            ? $"Delete {selected[0].FileName}?"
            : $"Delete these {selected.Count} resources?";

        var alsoNote = companions.Count > 0
            ? $"\n\nIts surface data goes too — {companions.Count} .image resource"
              + $"{(companions.Count == 1 ? "" : "s")}, which nothing else in this archive uses."
            : "";

        var answer = ShowMessage(
            $"{what}{alsoNote}\n\nThe archive isn't written until you save, and Ctrl+Z undoes this.",
            "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            TakeUndoSnapshot(selected.Count == 1
                ? $"delete {selected[0].FileName}"
                : $"delete {selected.Count} resources");

            var removed = 0;
            foreach (var resource in going)
                if (_erp!.File.Resources.Remove(resource)) removed++;

            // Not MarkDirty(vm): those rows are gone, and the per-row dot it
            // sets belongs to a row that still exists.
            MarkDirty();

            foreach (var item in selected) Log($"Deleted {item.FileName}");
            foreach (var companion in companions) Log($"  and its surface {ShortName(companion.Identifier)}");
            Log($"Deleted {removed} resource(s). Save to write it out.");

            RebuildTextureList();
            ShowPreviewForItem(null);
        }
        catch (Exception ex)
        {
            Log($"!! Delete failed: {ex.Message}");
            ShowMessage(ex.Message, "Delete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Everything the selection takes with it: the resources behind the rows,
    /// plus the surface resource each texture's pixels live in.
    ///
    /// A texture is two resources — the view the list shows, and an
    /// <c>.image</c> holding the bytes, which the list does not show because it
    /// is not a thing anybody picks. Deleting the view alone would leave the
    /// larger half of the texture behind, taking the space while nothing
    /// addressed it.
    /// </summary>
    private List<ErpResource> Gather(IReadOnlyList<ResourceListItem> selected, out List<ErpResource> companions)
    {
        var going = new List<ErpResource>();
        var seen = new HashSet<ErpResource>();
        companions = [];

        foreach (var item in selected)
            if (seen.Add(item.Resource)) going.Add(item.Resource);

        foreach (var surface in Surfaces(selected))
        {
            if (!seen.Add(surface)) continue;
            going.Add(surface);
            companions.Add(surface);
        }

        return going;
    }

    private int CompanionCount(IReadOnlyList<ResourceListItem> selected) => Surfaces(selected).Count;

    /// <summary>
    /// The surface resources belonging to the selected textures, minus any a
    /// texture that is staying still points at.
    ///
    /// The name comes from the texture's own record rather than from
    /// <c>identifier + ".image"</c>: that convention holds everywhere it has
    /// been looked at, but the record is what the engine actually resolves, and
    /// a guess that is wrong here deletes somebody else's pixels.
    /// </summary>
    private List<ErpResource> Surfaces(IReadOnlyList<ResourceListItem> selected)
    {
        if (!HasErp) return [];

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var leaving = new HashSet<ErpResource>(selected.Select(s => s.Resource));

        foreach (var texture in selected.OfType<TextureVm>())
        {
            var name = SurfaceNameOf(texture);
            if (!string.IsNullOrEmpty(name)) wanted.Add(name);
        }

        if (wanted.Count == 0) return [];

        // A surface still spoken for by a texture that is staying stays too.
        foreach (var item in _textures.OfType<TextureVm>())
        {
            if (leaving.Contains(item.Resource)) continue;
            var name = SurfaceNameOf(item);
            if (!string.IsNullOrEmpty(name)) wanted.Remove(name);
        }

        return _erp!.File.Resources
            .Where(r => wanted.Contains(r.Identifier))
            .ToList();
    }

    private static string SurfaceNameOf(TextureVm texture)
    {
        try { return texture.Srv.Fragment0.SurfaceResourceName ?? ""; }
        catch { return ""; }
    }

    private static string ShortName(string identifier)
    {
        var slash = identifier.LastIndexOf('/');
        return slash >= 0 ? identifier[(slash + 1)..] : identifier;
    }

    /// <summary>The row a click landed on, or null if it landed on none.</summary>
    private static ListBoxItem? RowUnder(DependencyObject? from)
    {
        while (from is not null and not ListBoxItem)
        {
            from = from is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(from)
                : LogicalTreeHelper.GetParent(from);
        }

        return from as ListBoxItem;
    }
}
