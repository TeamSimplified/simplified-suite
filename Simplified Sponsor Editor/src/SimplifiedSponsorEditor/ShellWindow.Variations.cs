using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SimplifiedSponsorEditor.Core.Sponsors;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The six variations under the bio, and what happens when one is clicked.
///
/// A slot is one of two things and the click means something different for each.
/// Empty, it is an invitation: the file dialog opens, and the variation window
/// opens straight after on whatever was picked, because somebody who just chose
/// a logo wants to see it rather than to click the same square again. Filled, it
/// is what is in it, and the click opens that.
/// </summary>
public partial class ShellWindow
{
    /// <summary>
    /// What is in each slot. Six from the start rather than a growing list: the
    /// row is six squares whether or not anything is in them, so the model that
    /// backs it is six as well and an empty one is an empty variation rather
    /// than a missing entry.
    /// </summary>
    private readonly List<Variation> _variations =
        Enumerable.Range(0, 6).Select(_ => new Variation()).ToList();

    private Button[] Slots => [Slot0, Slot1, Slot2, Slot3, Slot4, Slot5];

    private void Slot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag
            || !int.TryParse(tag, out var at) || at < 0 || at >= _variations.Count)
            return;

        var variation = _variations[at];
        var wasEmpty = variation.IsEmpty;

        // Empty: ask for the file first. Cancelling there is cancelling the
        // whole thing — there is nothing to open a window on.
        if (wasEmpty)
        {
            if (Browse() is not { } picked) return;
            variation.SourcePath = picked;
        }

        if (VariationWindow.Show(this, variation) is { } kept)
            _variations[at] = kept;
        else if (wasEmpty)
            // Closed without keeping it, and the slot held nothing a moment
            // ago. The browse was a step on the way in rather than a decision
            // of its own, so cancelling undoes that too — otherwise Cancel
            // leaves behind a file nobody ever confirmed.
            variation.SourcePath = null;

        Show(at);
    }

    /// <summary>
    /// The file, or null if nobody picked one. Two kinds, because they are the
    /// two the canvas can open: a vector opens as its pieces and a bitmap opens
    /// whole.
    /// </summary>
    private string? Browse()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose artwork for this variation",
            Filter = "Artwork (*.svg;*.png)|*.svg;*.png|Vector (*.svg)|*.svg|Picture (*.png)|*.png",
            CheckFileExists = true,
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Draw what is in a slot. An empty one shows nothing at all — the outline
    /// is the slot, and a placeholder glyph inside it would be a seventh kind of
    /// mark in a window that has three.
    /// </summary>
    private void Show(int at)
    {
        var slot = Slots[at];
        var variation = _variations[at];

        if (variation.IsEmpty || Thumbnail(variation) is not { } picture)
        {
            slot.Content = null;
            slot.ToolTip = null;

            // Nothing in it, so nothing to offer. A menu with one item greyed
            // out is a worse answer to a right-click than no menu at all.
            slot.ContextMenu = null;
            return;
        }

        slot.Content = new Image
        {
            Source = picture,
            Stretch = Stretch.Uniform,
        };

        slot.ToolTip = variation.Name;
        slot.ContextMenu = Menu(at);
    }

    /// <summary>
    /// What a right-click on a filled slot offers.
    ///
    /// Only emptying it. The left click already covers opening one, and a menu
    /// that repeats what the thing does when you click it is a longer way to do
    /// the same thing.
    ///
    /// No confirmation: what goes is this slot's choice of file and what was
    /// done to it, and the file itself was never touched — it is still wherever
    /// it was picked from. Asking "are you sure" before something that costs a
    /// second to redo teaches people to click through the ones that matter.
    /// </summary>
    private ContextMenu Menu(int at)
    {
        var delete = new MenuItem { Header = "Delete" };

        delete.Click += (_, _) =>
        {
            // A fresh one rather than clearing this one: the window may still be
            // holding the variation it was opened with, and a slot going empty
            // should not reach back into that.
            _variations[at] = new Variation();
            Show(at);
        };

        return new ContextMenu { Items = { delete } };
    }

    /// <summary>
    /// The artwork as the slot shows it — which is as the car texture shows it,
    /// not as the file is.
    ///
    /// A square, at the variation's own scale, with the margin round it. The
    /// slot is a small view of the thing that goes on the car, so a logo set to
    /// span half its texture reads as half a slot here too, and six slots at six
    /// scales can be compared without opening any of them. Showing the file
    /// instead would make every variation fill its square whatever had been
    /// decided about it.
    ///
    /// Composed small. The texture is 1024 square and the slot is 50 across, and
    /// drawing one at full size for the other is four hundred times the pixels
    /// anybody will see — six times over.
    /// </summary>
    private static BitmapSource? Thumbnail(Variation variation)
    {
        if (variation.SourcePath is not { } path) return null;

        try
        {
            var canvas = new ArtworkCanvas(path, variation.Edits, variation.Overlay);
            if (canvas.IsEmpty || canvas.Render(longest: 128) is not { } art) return null;

            var texture = SponsorTextures.Car;
            return TextureFit.Compose(art, texture.Width, texture.Height,
                                      variation.CarSpan, longest: 128);
        }
        catch
        {
            // A file that cannot be drawn leaves the slot looking empty, which
            // is wrong but quiet. The window says so properly when it is opened.
            return null;
        }
    }
}
