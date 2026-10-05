using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SimplifiedUi;

namespace SimplifiedSponsorEditor;

/// <summary>
/// Dragging a picture onto a decal, and dragging a decal's picture out.
///
/// The same two gestures Studio and the ERP app have over their resource list,
/// on the only list this app has. Dropping a picture on a row replaces that
/// decal — not the selected one — and dragging a row out hands over the picture
/// the preview is showing: the project's if it has been replaced, the game's own
/// if it hasn't.
///
/// What counts as a picture is the import's own rule, so a drawing can be
/// dragged in exactly as a PNG can.
/// </summary>
public partial class MainWindow
{
    // ── Dropping a picture on a row ───────────────────────────────────

    /// <summary>The row lit up as the drop target, so it can be put back.</summary>
    private ListBoxItem? _dropRow;

    /// <summary>
    /// True while the list is the source of a drag, which makes it refuse to be
    /// the target of one.
    ///
    /// Otherwise a drag started to take a picture out and then let go of over
    /// the list — a change of mind, a slipped button — would put that decal's
    /// artwork onto whatever row happened to be underneath.
    /// </summary>
    private bool _draggingOut;

    private void DecalList_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingOut)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var (container, row) = RowUnderPointer(e);
        if (row is null)
        {
            // Not ours. Left unhandled so the window's own handler still sees
            // it — a .simplified dropped anywhere, the list included, opens.
            ClearDropRow();
            return;
        }

        e.Handled = true;

        if (_project is null)
        {
            // Nowhere to put it. The cursor says so here; the window's handler
            // is the one that explains, when the drop lands off the list.
            ClearDropRow();
            e.Effects = DragDropEffects.None;
            return;
        }

        LightUp(container!);
        e.Effects = DragDropEffects.Copy;
    }

    private void DecalList_DragLeave(object sender, DragEventArgs e) => ClearDropRow();

    private void DecalList_Drop(object sender, DragEventArgs e)
    {
        ClearDropRow();
        if (_draggingOut) return;

        var (_, row) = RowUnderPointer(e);
        if (row is null || _project is null) return;

        e.Handled = true;

        // The row that was dropped on becomes the selection, so the preview is
        // showing the decal that just changed rather than whichever one was
        // selected before the drag.
        DecalList.SelectedItem = row;
        Apply(row.Decal, DroppedPicture(e)!);
    }

    /// <summary>
    /// The row under the pointer and its container, or nulls when this drag is
    /// not one the list should take.
    /// </summary>
    private (ListBoxItem? Container, DecalRow? Row) RowUnderPointer(DragEventArgs e)
    {
        if (DroppedPicture(e) is null) return (null, null);

        var hit = DecalList.InputHitTest(e.GetPosition(DecalList)) as DependencyObject;
        return ItemsControl.ContainerFromElement(DecalList, hit) is ListBoxItem container
            ? (container, container.DataContext as DecalRow)
            : (null, null);
    }

    /// <summary>
    /// The one picture being dragged, or null when the drag is not a single one.
    ///
    /// A <c>.simplified</c> is excluded on purpose: it belongs to the window,
    /// which opens it. Several files at once are excluded too — one picture onto
    /// one decal is a rule with no ambiguity in it, where a handful of files
    /// over a list would have to guess at pairing them up.
    /// </summary>
    private static string? DroppedPicture(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files) return null;

        var file = files[0];
        return File.Exists(file) && IsArtwork(file) ? file : null;
    }

    private void LightUp(ListBoxItem container)
    {
        if (ReferenceEquals(_dropRow, container)) return;
        ClearDropRow();
        DropHighlight.SetIsOver(container, true);
        _dropRow = container;
    }

    private void ClearDropRow()
    {
        if (_dropRow is null) return;
        DropHighlight.SetIsOver(_dropRow, false);
        _dropRow = null;
    }

    // ── Dragging a picture out ────────────────────────────────────────
    //
    // A drop target is handed file paths, not bytes, so the picture is put in a
    // temp folder under the decal's own name and it is that path which travels.
    // What goes out is what the preview is showing: the project's picture when
    // the decal has been replaced, and the game's own when it hasn't — which
    // makes this the way to get an original out to paint over.

    /// <summary>Where the pointer went down, to tell a drag from a click.</summary>
    private Point _dragOrigin;

    /// <summary>The row a drag would carry, captured on mouse-down.</summary>
    private DecalRow? _dragRow;

    /// <summary>
    /// The folder the last drag copied into, cleared when the next one starts.
    /// It sits under the system temp directory, so what a killed session leaves
    /// behind is Windows' to sweep up.
    /// </summary>
    private string? _lastDragFolder;

    private void DecalList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(null);
        _dragRow = (ItemsControl.ContainerFromElement(DecalList, e.OriginalSource as DependencyObject)
            as ListBoxItem)?.DataContext as DecalRow;
    }

    private void DecalList_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is null || e.LeftButton != MouseButtonState.Pressed) return;

        var moved = e.GetPosition(null) - _dragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var row = _dragRow;
        _dragRow = null;   // one drag per press, however far the pointer wanders
        DragRowOut(row);
    }

    /// <summary>
    /// Copy the decal's picture out under its own name and hand the path to the
    /// drag. Synchronous on purpose: a drag has to start while the button is
    /// still down, and awaiting the copy would let go of the gesture.
    /// </summary>
    private void DragRowOut(DecalRow row)
    {
        var replaced = _project?.IsReplaced(row.Name) ?? false;
        var source = replaced
            ? _project!.PicturePath(row.Name)
            : _preview?.Get(row.Decal.CarIdentifier)?.PngPath;

        if (source is null || !File.Exists(source)) return;

        // The last drag's file goes now rather than at the end of this one: the
        // drop target copies it inside the drop itself, but one that deferred
        // the copy would find it already deleted.
        ClearLastDragFolder();
        var folder = Path.Combine(Path.GetTempPath(), "simplified-sponsor-editor", "drag",
                                  Guid.NewGuid().ToString("N"));

        string picture;
        var previousCursor = Mouse.OverrideCursor;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            Directory.CreateDirectory(folder);
            _lastDragFolder = folder;

            // Named for the decal rather than whatever the cache called it, so
            // what lands in the folder says what it is.
            picture = Path.Combine(folder, row.Name + ".png");
            File.Copy(source, picture, overwrite: true);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = previousCursor;
            SimplifiedUi.MessageDialog.Show(this,
                $"{row.Name} couldn't be got ready to drag.\n\n{ex.Message}",
                "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = previousCursor;
        }

        _draggingOut = true;
        try
        {
            DragDrop.DoDragDrop(DecalList, new DataObject(DataFormats.FileDrop, new[] { picture }),
                                DragDropEffects.Copy);
        }
        finally
        {
            _draggingOut = false;
        }
    }

    private void ClearLastDragFolder()
    {
        if (_lastDragFolder is null) return;
        try { if (Directory.Exists(_lastDragFolder)) Directory.Delete(_lastDragFolder, recursive: true); }
        catch { /* temp, and Windows clears it */ }
        _lastDragFolder = null;
    }
}
