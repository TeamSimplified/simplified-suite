using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Simplified.Shared;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The artwork, as something you can click on: the Canvas half of the variation
/// window, and the whole of the old import dialog.
///
/// Two kinds of file arrive here and they are not the same thing to edit. An SVG
/// is a stack of shapes, so it opens as its pieces — click one, drop it or paint
/// it, and what leaves is the list of changes rather than a flattened picture. A
/// PNG is one shape with no seams in it, so there is nothing to pick: it shows
/// whole, and the colour applies over all of it at once.
///
/// Said once here rather than twice. The two have the same tools above them and
/// the same ground behind them, and only the middle differs; writing a bitmap
/// path into its own window would have meant a second copy of the picking,
/// marking and painting that the vector path already does.
/// </summary>
public sealed class ArtworkCanvas
{
    private readonly List<SvgPart> _all = [];
    private readonly List<SvgPart> _parts = [];
    private readonly List<Color?> _original = [];

    // The brushes the file arrived with, kept by reference. Paint replaces Fill
    // and Stroke rather than mutating them, so these stay the originals — and
    // putting them back is the only way to undo a part that had no colour of its
    // own, which Paint cannot express.
    private readonly List<Brush?> _originalFill = [];
    private readonly List<Pen?> _originalStroke = [];
    private readonly Dictionary<SvgPart, int> _index = [];
    private readonly Dictionary<SvgPart, Path> _shapes = [];
    private readonly HashSet<SvgPart> _selected = [];

    private readonly Canvas? _canvas;
    private readonly Path? _hoverMark;
    private readonly Path? _pickedMark;

    private readonly BitmapSource? _bitmap;
    private readonly Rectangle? _tint;
    private readonly Image? _picture;

    /// <summary>What to put in the window. A ground with the artwork on it.</summary>
    public Border Element { get; }

    /// <summary>Whether this one has pieces, which decides what the tools do.</summary>
    public bool IsVector { get; }

    /// <summary>Raised whenever the selection, the colours or the parts change.</summary>
    public event Action? Changed;

    /// <summary>Whether the file could be read at all.</summary>
    public bool IsEmpty => IsVector ? _all.Count == 0 : _bitmap is null;

    public int PartCount => IsVector ? _parts.Count : 1;

    /// <summary>
    /// How many pieces are picked. A bitmap counts as one picked thing always:
    /// there is nothing to choose, so the colour tool is never off for it.
    /// </summary>
    public int SelectedCount => IsVector ? _selected.Count : 1;

    /// <summary>
    /// The colour the box shows, or null where what is picked disagrees. Pieces
    /// picked together that are different colours leave it blank rather than
    /// claiming one of them.
    /// </summary>
    public Color? Shown { get; private set; }

    /// <summary>The colour laid over a bitmap, as <c>#RRGGBB</c>, or null.</summary>
    public string? Overlay { get; private set; }

    public ArtworkCanvas(string path, SvgEdits? edits = null, string? overlay = null)
    {
        IsVector = path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

        var ground = new Border
        {
            // No ground of its own: the window's Background slider sets this,
            // which is what the checkerboard used to do and does better — it
            // says "nothing is here" against every colour in turn rather than
            // against one chosen in advance.
            //
            // The padding is room to see the edges of the logo. The Viewbox
            // inside fills whatever it is given, so this is the only thing
            // between the artwork and the border, and at 18 a logo that reaches
            // its own bounding box sat almost on the line.
            Padding = new Thickness(44),
            BorderThickness = new Thickness(1),
        };

        ground.SetResourceReference(Border.BorderBrushProperty, "BorderSoft");

        if (IsVector)
        {
            _canvas = BuildParts(path, edits);
            _hoverMark = Mark(0.22);
            _pickedMark = Mark(0.45);
            _canvas.Children.Add(_hoverMark);
            _canvas.Children.Add(_pickedMark);
            ground.Child = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas };

            // A click that reached the ground is a click on no part: the parts
            // mark their own as handled, so nothing they were clicked with gets
            // this far.
            ground.MouseLeftButtonDown += (_, e) =>
            {
                _selected.Clear();
                _hoverMark.Opacity = 0;
                Settle();

                // Said to be handled so it stops here. The window this sits in
                // has no system frame and moves by DragMove, and an unhandled
                // click would reach that and drag the window every time
                // somebody clicked to pick nothing.
                e.Handled = true;
            };
        }
        else
        {
            _bitmap = Picture(path);

            // The tint sits over the picture with the picture as its mask, so
            // the colour lands on the ink and not on the transparent paper
            // around it. Painting the Image itself would have meant rewriting
            // its pixels, which is not something to do to somebody's file.
            _tint = new Rectangle { IsHitTestVisible = false, Opacity = 0 };

            // Uniform, to match the Image beside it. Fill was wrong and looked
            // it: the Image letterboxes itself inside the pane and the
            // rectangle fills the whole of it, so a mask stretched to the
            // rectangle landed on different pixels than the picture did — a
            // squashed copy of the logo, tinted, sitting over the real one.
            // Both sized the same way, both centred, and the colour lands
            // exactly on the ink.
            if (_bitmap is not null)
                _tint.OpacityMask = new ImageBrush(_bitmap) { Stretch = Stretch.Uniform };

            _picture = new Image
            {
                Source = _bitmap,
                Stretch = Stretch.Uniform,
                // A logo is drawn at the size it was made; smoothing it up to
                // the pane is better than showing its pixels.
                SnapsToDevicePixels = true,
            };

            var stack = new Grid();
            stack.Children.Add(_picture);
            stack.Children.Add(_tint);

            RenderOptions.SetBitmapScalingMode(stack, BitmapScalingMode.HighQuality);
            ground.Child = stack;

            if (overlay is not null && SvgEdits.Parse(overlay) is { } seeded) Paint(seeded);
        }

        Element = ground;
        Settle();
    }

    /// <summary>
    /// Pick a piece, or add it to what is already picked. Does nothing for a
    /// bitmap, which has one piece and it is always the one.
    /// </summary>
    private void Pick(SvgPart part, bool add)
    {
        if (!add) _selected.Clear();
        if (!_selected.Add(part) && add) _selected.Remove(part);
        Settle();
    }

    /// <summary>Paint what is picked, or the whole picture where it is a bitmap.</summary>
    public void Paint(Color colour)
    {
        if (!IsVector)
        {
            if (_tint is null || _picture is null) return;

            _tint.Fill = new SolidColorBrush(colour);
            _tint.Opacity = 1;

            // And the picture underneath goes. Laying the colour over it left
            // the original showing through everywhere the artwork was not fully
            // opaque — which is every anti-aliased edge in it, so a recoloured
            // logo kept a thin fringe of the colour it used to be. What is
            // wanted is this colour with the artwork's own alpha, and that is
            // the masked rectangle on its own.
            _picture.Opacity = 0;

            Overlay = SvgEdits.Format(colour);
            Shown = colour;
            Changed?.Invoke();
            return;
        }

        // Nothing picked paints the lot. A drawing somebody wants one colour
        // is the common case — a logo recoloured to a team — and making them
        // pick every piece first to say so was work for nothing. Picking some
        // still means those only, which is the whole point of picking.
        foreach (var part in _selected.Count > 0 ? _selected : (IEnumerable<SvgPart>)_parts)
        {
            part.Paint(colour);

            var path = _shapes[part];
            path.Fill = part.Fill ?? Brushes.Transparent;
            path.Stroke = part.Stroke?.Brush;
        }

        Settle();
    }

    /// <summary>
    /// Drop what is picked. A bitmap has no pieces, so there is nothing here for
    /// one — Reset is what takes a colour back off it.
    /// </summary>
    public void Delete()
    {
        if (!IsVector) return;

        foreach (var part in _selected)
        {
            // Taken off the canvas but kept in the dictionary. Reset puts them
            // back, and a Path that had been thrown away would have to be built
            // again from a part whose colour has since been changed.
            _canvas!.Children.Remove(_shapes[part]);
            _parts.Remove(part);
        }

        _selected.Clear();
        if (_hoverMark is not null) _hoverMark.Opacity = 0;
        Settle();
    }

    /// <summary>Whether Delete would do anything.</summary>
    public bool CanDelete => IsVector && _selected.Count > 0;

    /// <summary>
    /// Whether there is anything to paint, which is not the same as anything
    /// being picked — nothing picked paints all of it.
    /// </summary>
    public bool CanPaint => IsVector ? _parts.Count > 0 : _bitmap is not null;

    /// <summary>Whether a colour would land on everything rather than on a pick.</summary>
    public bool PaintsEverything => IsVector && _selected.Count == 0;

    /// <summary>
    /// Back to the file as it was imported: every piece returned, every colour
    /// its own again, and a picture's overlay off.
    ///
    /// The file was never written to, so this is the one thing that is always
    /// possible — the drawing on screen is the source plus a list of changes,
    /// and this throws the list away.
    /// </summary>
    public void Reset()
    {
        if (!IsVector)
        {
            if (_tint is null || _picture is null) return;

            _tint.Opacity = 0;
            _picture.Opacity = 1;
            Overlay = null;
            Shown = null;
            Changed?.Invoke();
            return;
        }

        // Repainted first, then re-stacked. Paint order is the file's order and
        // the canvas has to end up in it, so the children are rebuilt rather
        // than the missing ones appended — a part deleted early and restored
        // last would otherwise come back on top of everything.
        for (var i = 0; i < _all.Count; i++)
        {
            var part = _all[i];

            // The brushes themselves, not Paint with the old colour. A part
            // that arrived with no fill has no colour to paint back, and Paint
            // would leave the one somebody gave it.
            part.Fill = _originalFill[i];
            part.Stroke = _originalStroke[i];

            var shape = _shapes[part];
            shape.Fill = part.Fill ?? Brushes.Transparent;
            shape.Stroke = part.Stroke?.Brush;
        }

        _canvas!.Children.Clear();
        _parts.Clear();

        foreach (var part in _all)
        {
            _parts.Add(part);
            _canvas.Children.Add(_shapes[part]);
        }

        // The marks live on the canvas too, and go back on top of it.
        _canvas.Children.Add(_hoverMark);
        _canvas.Children.Add(_pickedMark);

        _selected.Clear();
        _hoverMark!.Opacity = 0;
        Settle();
    }

    /// <summary>
    /// Whether anything has been done that Reset would undo. A drawing nobody
    /// has touched is already its own file, and the button says so by being off.
    /// </summary>
    public bool CanReset => IsVector
        ? _parts.Count != _all.Count || !Result().IsEmpty
        : Overlay is not null;

    /// <summary>
    /// What was done, said as changes rather than as the picture that came out —
    /// so the file stays the thing the project holds, and this can be reopened
    /// on top of it later.
    /// </summary>
    public SvgEdits Result()
    {
        var result = new SvgEdits();
        if (!IsVector) return result;

        var kept = new HashSet<SvgPart>(_parts);

        for (var i = 0; i < _all.Count; i++)
            if (!kept.Contains(_all[i])) result.Removed.Add(i);

        foreach (var part in _parts)
        {
            var at = _index[part];
            if (part.Colour is not { } now || now == _original[at]) continue;
            result.Colours[at] = SvgEdits.Format(now);
        }

        return result;
    }

    /// <summary>
    /// The artwork as a picture, for the frames to put in their textures.
    ///
    /// Drawn at the artwork's own proportions and no larger than
    /// <paramref name="longest"/>, because what this feeds is a frame a couple
    /// of hundred pixels across and the marks are left out of it — a hover
    /// highlight is a thing in the window rather than a thing in the texture.
    /// </summary>
    public BitmapSource? Render(int longest = 1024)
    {
        // A bitmap is already a picture, but the overlay is a thing laid over it
        // in the window rather than in the file — so it has to be painted in
        // here, or the frames and the slot would both show an untinted logo
        // while the canvas shows a tinted one.
        if (!IsVector) return _bitmap is null ? null : Tinted(_bitmap, Overlay);

        if (_parts.Count == 0) return null;

        var drawing = SvgPicture.ToDrawing(_parts);
        var bounds = drawing.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        // Up as well as down. A bitmap has a resolution and enlarging it would
        // be inventing pixels, but a vector has none — its numbers are whatever
        // units the file was drawn in, and this one came out 160 across, which
        // would have put a 160px texture on the car and called it the artwork's
        // own size. For a drawing the texture size is a choice, so it is made
        // here rather than inherited from somebody's viewBox.
        var scale = longest / Math.Max(bounds.Width, bounds.Height);
        var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(width / bounds.Width, height / bounds.Height));
            context.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            context.DrawDrawing(drawing);
            context.Pop();
            context.Pop();
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// A picture with a colour laid over its ink, the same way the canvas shows
    /// it: the colour is drawn through the picture's own alpha, so the paper
    /// around a logo stays paper.
    /// </summary>
    private static BitmapSource Tinted(BitmapSource picture, string? overlay)
    {
        if (overlay is null || SvgEdits.Parse(overlay) is not { } colour) return picture;

        var width = picture.PixelWidth;
        var height = picture.PixelHeight;
        var area = new Rect(0, 0, width, height);

        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            // The colour through the picture's own alpha, and nothing else
            // under it. Drawing the picture first and laying the colour over it
            // left the original showing at every edge the artwork fades out on.
            context.PushOpacityMask(new ImageBrush(picture) { Stretch = Stretch.Fill });
            context.DrawRectangle(new SolidColorBrush(colour), null, area);
            context.Pop();
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    // ── Building the vector side ─────────────────────────────────────

    private Canvas BuildParts(string path, SvgEdits? edits)
    {
        _all.AddRange(SvgPicture.Parts(SvgPicture.Read(path)));

        // Where each part sits in the file's own order, and the colour it
        // arrived with. What leaves records only what somebody changed: a part
        // painted back to its own colour is not an edit, and writing it down
        // would pin the drawing to a colour its file already has.
        for (var i = 0; i < _all.Count; i++) _index[_all[i]] = i;
        _original.AddRange(_all.Select(p => p.Colour));
        _originalFill.AddRange(_all.Select(p => p.Fill));
        _originalStroke.AddRange(_all.Select(p => p.Stroke));

        var seed = edits ?? new SvgEdits();

        for (var i = 0; i < _all.Count; i++)
        {
            if (seed.Colours.TryGetValue(i, out var hex) && SvgEdits.Parse(hex) is { } colour)
                _all[i].Paint(colour);

            if (!seed.Removed.Contains(i)) _parts.Add(_all[i]);
        }

        // Only a hand-edited sidecar can name every part — nothing here writes
        // one that does. Showing the drawing whole beats opening an empty canvas
        // with nothing on it to click.
        if (_parts.Count == 0) _parts.AddRange(_all);

        // The drawing's own coordinates, so every shape is already where its
        // file puts it and a Viewbox does the fitting. Painted in order, so the
        // one on top is the one drawn last, which is the one a click wants.
        var bounds = _parts.Count == 0 ? new Rect(0, 0, 1, 1) : SvgPicture.ToDrawing(_parts).Bounds;

        var canvas = new Canvas
        {
            Width = Math.Max(1, bounds.Width),
            Height = Math.Max(1, bounds.Height),
            // So a click on empty space is a click rather than nothing.
            Background = Brushes.Transparent,
            RenderTransform = new TranslateTransform(-bounds.X, -bounds.Y),
        };

        // A shape for every part, including the ones the edits already took
        // out — only the kept ones go on the canvas. Reset puts a part back by
        // its shape, and a part that arrived already deleted had never had one
        // built, so reopening an edited variation and pressing Reset threw.
        var shown = new HashSet<SvgPart>(_parts);

        foreach (var part in _all)
        {
            var shape = new Path
            {
                Data = part.Geometry,
                // Transparent rather than nothing where a part has no fill: it
                // draws the same and it can be clicked inside, which is what
                // somebody expects of a shape they can see the outline of.
                Fill = part.Fill ?? Brushes.Transparent,
                Stroke = part.Stroke?.Brush,
                StrokeThickness = part.Stroke?.Thickness ?? 0,
                Opacity = part.Opacity,
                Clip = part.Clip,
                Cursor = Cursors.Hand,
            };

            _shapes[part] = shape;
            if (shown.Contains(part)) canvas.Children.Add(shape);

            shape.MouseEnter += (_, _) =>
            {
                _hoverMark!.Data = part.Geometry;
                _hoverMark.Opacity = _selected.Contains(part) ? 0 : 0.22;
            };

            shape.MouseLeave += (_, _) => _hoverMark!.Opacity = 0;

            shape.MouseLeftButtonDown += (_, e) =>
            {
                Pick(part, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                           || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                _hoverMark!.Opacity = 0;
                e.Handled = true;
            };
        }

        return canvas;
    }

    /// <summary>
    /// A highlight that sits above the artwork rather than on it: tinting a
    /// shape to show it is picked would hide the colour somebody is deciding
    /// about.
    /// </summary>
    private static Path Mark(double _)
    {
        var mark = new Path { IsHitTestVisible = false, Opacity = 0 };
        mark.SetResourceReference(Shape.FillProperty, "Accent");
        return mark;
    }

    /// <summary>Work out what is picked and what colour that makes it.</summary>
    private void Settle()
    {
        if (IsVector)
        {
            _pickedMark!.Data = _selected.Count == 0
                ? null
                : new GeometryGroup
                {
                    FillRule = FillRule.Nonzero,
                    Children = new GeometryCollection(_selected.Select(p => p.Geometry)),
                };

            _pickedMark.Opacity = _selected.Count == 0 ? 0 : 0.45;

            // What the box shows: the colour of what is picked, or — with
            // nothing picked, where a colour would land on everything — the
            // colour the whole drawing already is, when it is one colour. Parts
            // that disagree leave it blank rather than claiming one of them.
            var colours = (_selected.Count > 0 ? _selected : (IEnumerable<SvgPart>)_parts)
                .Select(p => p.Colour).Distinct().ToList();

            Shown = colours.Count == 1 ? colours[0] : null;
        }

        Changed?.Invoke();
    }

    private static BitmapSource? Picture(string path)
    {
        try
        {
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.UriSource = new Uri(path);
            picture.EndInit();
            picture.Freeze();
            return picture;
        }
        catch
        {
            return null;
        }
    }
}
