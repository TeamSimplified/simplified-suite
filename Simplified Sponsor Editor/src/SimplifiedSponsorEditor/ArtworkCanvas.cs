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
using SimplifiedSponsorEditor.Core.Artwork;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The artwork, as something you can click on: the Canvas half of the variation
/// window, and the whole of the old import dialog.
///
/// Two kinds of file arrive here, and both open as pieces to pick, paint and
/// drop. An SVG is a stack of shapes, so its pieces are its shapes. A picture —
/// PNG, JPEG, TIFF, WebP — is a grid of colours with no seams in it, so its
/// pieces have to be found first: <see cref="RasterParts"/> takes it apart
/// into runs of one colour, which for a logo is the same thing a person points
/// at. Either way what leaves is the list of changes rather than a flattened
/// picture, so the file stays somebody's file and the edits can be reopened on
/// top of it.
///
/// Said once here rather than twice. The two have the same tools above them and
/// the same ground behind them, and only the middle differs.
/// </summary>
public sealed class ArtworkCanvas
{
    // ── The drawing side ─────────────────────────────────────────────────

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
    private Color? _shownVector;

    // ── The picture side ─────────────────────────────────────────────────

    private readonly Picture? _source;
    private readonly HashSet<int> _gone = [];
    private readonly Dictionary<int, Rgb> _painted = [];
    private readonly HashSet<int> _picked = [];
    private int _hover = -1;

    private readonly Image? _picture;
    private readonly Image? _marks;
    private WriteableBitmap? _markBitmap;
    private byte[]? _markPixels;

    // The part of the picture on screen: all of it, or after a delete the box
    // round what is left. Pixels are found through it, so it is where a click
    // turns into a piece.
    private Int32Rect _area;

    /// <summary>What to put in the window. A ground with the artwork on it.</summary>
    public Border Element { get; }

    /// <summary>Whether this one is a drawing rather than a picture.</summary>
    public bool IsVector { get; }

    /// <summary>Raised whenever the selection, the colours or the parts change.</summary>
    public event Action? Changed;

    /// <summary>Whether the file could be read at all.</summary>
    public bool IsEmpty => IsVector ? _all.Count == 0 : _source is null;

    /// <summary>How many pieces are left.</summary>
    public int PartCount => IsVector ? _parts.Count : Raster.Parts.Count - _gone.Count;

    /// <summary>How many pieces are picked.</summary>
    public int SelectedCount => IsVector ? _selected.Count : _picked.Count;

    /// <summary>
    /// The colour the box shows, or null where what is picked disagrees. Pieces
    /// picked together that are different colours leave it blank rather than
    /// claiming one of them.
    /// </summary>
    public Color? Shown => IsVector ? _shownVector : RasterShown();

    /// <param name="overlay">
    /// A colour laid over the whole of a picture, as 2.0 saved one before
    /// pictures had pieces. Read as every piece painted that colour, which is
    /// what it looked like — and is written back that way, so it is only ever
    /// read here, never kept.
    /// </param>
    public ArtworkCanvas(string path, SvgEdits? edits = null, string? overlay = null)
    {
        IsVector = ArtworkFile.IsVector(path);

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
            _hoverMark = Mark();
            _pickedMark = Mark();
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
            _source = Picture.Open(path);

            Seed(edits, overlay);

            _area = new Int32Rect(0, 0, _source.Width, _source.Height);

            _picture = new Image
            {
                Stretch = Stretch.Uniform,
                // Centred rather than stretched, so the element is exactly the
                // picture and a point in it maps straight to a pixel. Stretched,
                // the element would be the whole pane with the picture
                // letterboxed somewhere inside it.
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // The highlight, as a picture of its own the same size laid over
            // the top — the bitmap counterpart of the vector side's marks, and
            // for the same reason: tinting the piece itself to show it is
            // picked would hide the colour somebody is deciding about.
            _marks = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            // Drawn through the same path every later change takes, so a
            // picture reopened with its background already dropped opens
            // fitted to what is left, not at its old size.
            Redraw();

            var stack = new Grid();
            stack.Children.Add(_picture);
            stack.Children.Add(_marks);

            // Enlarged to fill the pane when the picture is small, and a logo
            // is better smoothed up than shown as its pixels.
            RenderOptions.SetBitmapScalingMode(stack, BitmapScalingMode.HighQuality);
            ground.Child = stack;

            // On the ground rather than on the picture: the ground is the whole
            // pane, so a click beside the logo arrives here too and means "pick
            // nothing" — the same as a click on the vector side's empty space.
            ground.MouseMove += (_, e) => Hover(PieceUnder(e));
            ground.MouseLeave += (_, _) => Hover(-1);
            ground.MouseLeftButtonDown += (_, e) =>
            {
                var piece = PieceUnder(e);
                var adding = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                             || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

                if (piece < 0) _picked.Clear();
                else if (e.ClickCount == 2) PickRaster(Raster.SameColour(piece), adding);
                else PickRaster([piece], adding);

                _hover = -1;
                Settle();

                // Handled for the same reason as above: the window drags by any
                // press nothing else wanted.
                e.Handled = true;
            };
        }

        Element = ground;
        Settle();
    }

    // ── Shared tools ─────────────────────────────────────────────────────

    /// <summary>
    /// Paint what is picked, or everything when nothing is.
    ///
    /// Nothing picked paints the lot. A logo somebody wants one colour is the
    /// common case — recoloured to a team — and making them pick every piece
    /// first to say so was work for nothing. Picking some still means those
    /// only, which is the whole point of picking.
    /// </summary>
    public void Paint(Color colour)
    {
        if (!IsVector)
        {
            var rgb = new Rgb(colour.R, colour.G, colour.B);
            foreach (var piece in RasterTargets()) _painted[piece] = rgb;

            Redraw();
            Settle();
            return;
        }

        foreach (var part in _selected.Count > 0 ? _selected : (IEnumerable<SvgPart>)_parts)
        {
            part.Paint(colour);

            var path = _shapes[part];
            path.Fill = part.Fill ?? Brushes.Transparent;
            path.Stroke = part.Stroke?.Brush;
        }

        Settle();
    }

    /// <summary>Drop what is picked.</summary>
    public void Delete()
    {
        if (!IsVector)
        {
            _gone.UnionWith(_picked);
            _picked.Clear();
            _hover = -1;

            Redraw();
            Settle();
            return;
        }

        foreach (var part in _selected)
        {
            // Taken off the canvas but kept in the dictionary. Reset puts them
            // back, and a Path that had been thrown away would have to be built
            // again from a part whose colour has since been changed.
            _canvas!.Children.Remove(_shapes[part]);
            _parts.Remove(part);
        }

        // Nothing left is refused at Save, and keeping the last size until then
        // beats collapsing the pane to a point.
        if (_parts.Count > 0) Fit(_canvas!);

        _selected.Clear();
        if (_hoverMark is not null) _hoverMark.Opacity = 0;
        Settle();
    }

    /// <summary>
    /// Pick the background, ready to be deleted, and say how many pieces that
    /// was. Nothing is deleted here: what was found is shown picked, so a
    /// background that turned out to include half the logo is seen before it
    /// goes rather than after.
    ///
    /// For a picture, the colour its outer edge mostly is, wherever it touches
    /// the edge — see <see cref="RasterParts.Background"/>. For a drawing, the
    /// shapes at the bottom of the stack that cover the whole of it: the
    /// rectangle an exporter puts behind a logo it was given on a coloured page.
    /// </summary>
    public int SelectBackground()
    {
        if (!IsVector)
        {
            _picked.Clear();
            _picked.UnionWith(Raster.Background().Where(piece => !_gone.Contains(piece)));
            _hover = -1;
            Settle();
            return _picked.Count;
        }

        _selected.Clear();

        if (_parts.Count > 1)
        {
            var whole = SvgPicture.ToDrawing(_parts).Bounds;

            // Bottom up, and only while each one covers everything: the first
            // shape that does not is the start of the artwork, and anything
            // full-size above it is part of the logo — a frame, a badge.
            foreach (var part in _parts)
            {
                var bounds = part.Geometry.Bounds;
                if (bounds.Width < whole.Width * 0.98 || bounds.Height < whole.Height * 0.98) break;
                _selected.Add(part);
            }

            // Every shape covering the whole drawing is not a background over
            // a logo, it is a logo that happens to be square.
            if (_selected.Count == _parts.Count) _selected.Clear();
        }

        if (_hoverMark is not null) _hoverMark.Opacity = 0;
        Settle();
        return _selected.Count;
    }

    /// <summary>Whether Delete would do anything.</summary>
    public bool CanDelete => SelectedCount > 0;

    /// <summary>
    /// Whether there is anything to paint, which is not the same as anything
    /// being picked — nothing picked paints all of it.
    /// </summary>
    public bool CanPaint => PartCount > 0;

    /// <summary>Whether a colour would land on everything rather than on a pick.</summary>
    public bool PaintsEverything => SelectedCount == 0;

    /// <summary>
    /// Back to the file as it was imported: every piece returned and every
    /// colour its own again.
    ///
    /// The file was never written to, so this is the one thing that is always
    /// possible — the artwork on screen is the source plus a list of changes,
    /// and this throws the list away.
    /// </summary>
    public void Reset()
    {
        if (!IsVector)
        {
            _gone.Clear();
            _painted.Clear();
            _picked.Clear();
            _hover = -1;

            Redraw();
            Settle();
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
        Fit(_canvas);

        _selected.Clear();
        _hoverMark!.Opacity = 0;
        Settle();
    }

    /// <summary>
    /// Whether anything has been done that Reset would undo. Artwork nobody
    /// has touched is already its own file, and the button says so by being off.
    /// </summary>
    public bool CanReset => IsVector
        ? _parts.Count != _all.Count || !Result().IsEmpty
        : !Result().IsEmpty;

    /// <summary>
    /// What was done, said as changes rather than as the picture that came out —
    /// so the file stays the thing the project holds, and this can be reopened
    /// on top of it later.
    /// </summary>
    public SvgEdits Result()
    {
        var result = new SvgEdits();

        if (!IsVector)
        {
            // Only once the picture has been taken apart: until then nothing
            // can have been picked, so nothing can have changed.
            if (_source?.Parts is not { } parts) return result;

            result.Removed.AddRange(_gone.Order());

            // A piece painted back to its own colour is not an edit, and writing
            // it down would pin the picture to a colour its file already has.
            foreach (var (piece, colour) in _painted.OrderBy(p => p.Key))
                if (!_gone.Contains(piece) && colour != parts.Parts[piece].Colour)
                    result.Colours[piece] = SvgEdits.Format(Color.FromRgb(colour.R, colour.G, colour.B));

            return result;
        }

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
    /// Drawn at the artwork's own proportions. A drawing is drawn no larger than
    /// <paramref name="longest"/>, because what this feeds is a frame a couple
    /// of hundred pixels across; a picture is already a size, and is handed over
    /// at it. The marks are left out — a hover highlight is a thing in the
    /// window rather than a thing in the texture.
    /// </summary>
    public BitmapSource? Render(int longest = 1024)
    {
        if (!IsVector) return RenderRaster();

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
        var canvas = new Canvas
        {
            // So a click on empty space is a click rather than nothing.
            Background = Brushes.Transparent,
        };

        Fit(canvas);

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
                var adding = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                             || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

                // A second click picks every part this colour — the letters of
                // a word, each one its own shape and almost always meant
                // together. A part with no single colour has nothing to match.
                if (e.ClickCount == 2 && part.Colour is { } colour)
                {
                    if (!adding) _selected.Clear();
                    _selected.UnionWith(_parts.Where(p => p.Colour == colour));
                    Settle();
                }
                else
                {
                    Pick(part, adding);
                }

                _hoverMark!.Opacity = 0;
                e.Handled = true;
            };
        }

        return canvas;
    }

    /// <summary>
    /// Size the canvas to the parts that are left, so the Viewbox around it
    /// enlarges what remains to fill the pane.
    ///
    /// Again after every delete, not only once. Sized only when it was built,
    /// the canvas stayed the size of the whole drawing, so deleting the
    /// rectangle behind a logo left the logo the same size it was, now sitting
    /// in an empty frame — when the frames, which crop to the ink, showed it
    /// filling its texture. The two views have to agree on what the artwork is.
    /// </summary>
    private void Fit(Canvas canvas)
    {
        var bounds = _parts.Count == 0 ? new Rect(0, 0, 1, 1) : SvgPicture.ToDrawing(_parts).Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) bounds = new Rect(0, 0, 1, 1);

        canvas.Width = Math.Max(1, bounds.Width);
        canvas.Height = Math.Max(1, bounds.Height);
        canvas.RenderTransform = new TranslateTransform(-bounds.X, -bounds.Y);
    }

    /// <summary>
    /// Pick a piece, or add it to what is already picked — or, adding one that
    /// already is, take it back out.
    /// </summary>
    private void Pick(SvgPart part, bool add)
    {
        if (!add) _selected.Clear();
        if (!_selected.Add(part) && add) _selected.Remove(part);
        Settle();
    }

    /// <summary>
    /// A highlight that sits above the artwork rather than on it: tinting a
    /// shape to show it is picked would hide the colour somebody is deciding
    /// about.
    /// </summary>
    private static Path Mark()
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

            _shownVector = colours.Count == 1 ? colours[0] : null;
        }
        else
        {
            DrawMarks();
        }

        Changed?.Invoke();
    }

    // ── The picture side ─────────────────────────────────────────────

    /// <summary>
    /// The picture's pieces, worked out the first time anything asks for them.
    ///
    /// Not before. A slot's thumbnail and the packager both open a canvas only
    /// to draw it, and a picture nobody has edited draws as itself — taking it
    /// apart for them would be a third of a second per slot spent on nothing.
    /// </summary>
    private RasterParts Raster => _source!.Split();

    /// <summary>
    /// The saved edits, put back. Numbers that do not name a piece of this
    /// picture are ignored rather than fatal: they can only come from a project
    /// file somebody edited by hand, and the rest of the edits are still good.
    /// </summary>
    private void Seed(SvgEdits? edits, string? overlay)
    {
        if (edits is { IsEmpty: false })
        {
            var count = Raster.Parts.Count;

            foreach (var piece in edits.Removed)
                if (piece >= 0 && piece < count) _gone.Add(piece);

            foreach (var (piece, hex) in edits.Colours)
                if (piece >= 0 && piece < count && SvgEdits.Parse(hex) is { } colour)
                    _painted[piece] = new Rgb(colour.R, colour.G, colour.B);

            // Everything gone is a state nothing here can produce — Save
            // refuses it — so a file saying so is wrong, and showing the
            // picture whole is more use than an empty pane.
            if (_gone.Count >= count) _gone.Clear();
        }
        else if (SvgEdits.Parse(overlay) is { } legacy)
        {
            var rgb = new Rgb(legacy.R, legacy.G, legacy.B);
            for (var piece = 0; piece < Raster.Parts.Count; piece++) _painted[piece] = rgb;
        }
    }

    /// <summary>Whether anything has been done to the picture at all.</summary>
    private bool Touched => _gone.Count > 0 || _painted.Count > 0;

    /// <summary>
    /// The picture as it now is — null for "as the file has it" — and the part
    /// of it the artwork occupies: all of it, until pieces are dropped, and
    /// then the box round what is left. Empty when nothing is left.
    ///
    /// Cropped because deleting a background leaves the paper the logo was
    /// printed on, and that paper would count towards the logo's size: a logo
    /// on a wide white banner would go into the texture as a small logo in the
    /// middle of a wide nothing. A drawing is fitted to its parts for the same
    /// reason. A picture nobody has dropped anything from keeps its whole area,
    /// so the margins somebody drew around their own PNG are kept.
    /// </summary>
    private (byte[]? Pixels, Int32Rect Area) Now()
    {
        var whole = new Int32Rect(0, 0, _source!.Width, _source.Height);
        if (!Touched) return (null, whole);

        var pixels = Raster.Apply(_gone, _painted);
        return (pixels, _gone.Count == 0 ? whole : Ink(pixels, _source.Width, _source.Height));
    }

    /// <summary>
    /// Show the picture as it now is, fitted to what is left of it — the same
    /// thing the vector side's <see cref="Fit"/> does, so a deleted background
    /// leaves the logo filling the pane rather than sitting where it was in an
    /// empty one.
    /// </summary>
    private void Redraw()
    {
        if (_picture is null || _source is null) return;

        var (pixels, area) = Now();

        // Nothing left: shown as nothing, at the picture's own size rather than
        // collapsed to a point. Keeping the last view instead looked exactly
        // like a Delete that had not worked. Save is what refuses an empty one.
        if (area.IsEmpty) area = new Int32Rect(0, 0, _source.Width, _source.Height);

        _area = area;
        _picture.Source = pixels is null ? _source.Bitmap : Crop(pixels, _source.Width, area);

        // The highlight is the same size as what is shown, so it goes too.
        _markBitmap = null;
        _markPixels = null;
    }

    /// <summary>The picture for the frames, cropped the same way the canvas shows it.</summary>
    private BitmapSource? RenderRaster()
    {
        if (_source is null) return null;

        var (pixels, area) = Now();
        if (pixels is null) return _source.Bitmap;

        return area.IsEmpty ? null : Crop(pixels, _source.Width, area);
    }

    /// <summary>
    /// The box round everything with any substance to it, or an empty one when
    /// there is nothing.
    /// </summary>
    private static Int32Rect Ink(byte[] pixels, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            // The same line the pieces are drawn at: anything fainter is either
            // the soft edge of a piece, which goes when the piece goes, or the
            // haze some exporters leave across the whole paper — which belongs
            // to nothing and would otherwise hold the crop at the full size of
            // the picture.
            if (pixels[(y * width + x) * 4 + 3] < RasterParts.Paper) continue;

            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
        }

        return right < 0 ? Int32Rect.Empty : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    private static BitmapSource Crop(byte[] pixels, int width, Int32Rect area)
    {
        if (area.X == 0 && area.Y == 0 && area.Width == width && pixels.Length == width * area.Height * 4)
            return ArtworkFile.Bitmap(pixels, width, area.Height);

        var cropped = new byte[area.Width * area.Height * 4];

        for (var y = 0; y < area.Height; y++)
            Buffer.BlockCopy(pixels, ((area.Y + y) * width + area.X) * 4,
                             cropped, y * area.Width * 4, area.Width * 4);

        return ArtworkFile.Bitmap(cropped, area.Width, area.Height);
    }

    /// <summary>What a colour would land on: the pick, or every piece left.</summary>
    private IEnumerable<int> RasterTargets() =>
        _picked.Count > 0
            ? _picked
            : Enumerable.Range(0, Raster.Parts.Count).Where(piece => !_gone.Contains(piece));

    private Color? RasterShown()
    {
        if (_source is null || PartCount == 0) return null;

        var colours = RasterTargets()
            .Select(piece => _painted.TryGetValue(piece, out var now) ? now : Raster.Parts[piece].Colour)
            .Distinct()
            .Take(2)
            .ToList();

        return colours.Count == 1 ? Color.FromRgb(colours[0].R, colours[0].G, colours[0].B) : null;
    }

    private void PickRaster(IEnumerable<int> pieces, bool add)
    {
        var list = pieces.Where(piece => !_gone.Contains(piece)).ToList();

        if (!add)
        {
            _picked.Clear();
            _picked.UnionWith(list);
            return;
        }

        // Adding one already picked takes it back out, as on the vector side;
        // adding a whole colour only ever adds.
        if (list.Count == 1 && _picked.Contains(list[0])) _picked.Remove(list[0]);
        else _picked.UnionWith(list);
    }

    /// <summary>
    /// The piece under the pointer, or -1 for paper, a dropped piece, or
    /// somewhere beside the picture.
    /// </summary>
    private int PieceUnder(MouseEventArgs e)
    {
        if (_picture is null || _source is null || _picture.ActualWidth <= 0 || _picture.ActualHeight <= 0)
            return -1;

        // Through the area shown, which after a delete is a crop of the
        // picture rather than all of it.
        var at = e.GetPosition(_picture);
        var x = _area.X + (int)Math.Floor(at.X / _picture.ActualWidth * _area.Width);
        var y = _area.Y + (int)Math.Floor(at.Y / _picture.ActualHeight * _area.Height);

        if (x < _area.X || y < _area.Y || x >= _area.X + _area.Width || y >= _area.Y + _area.Height)
            return -1;

        var piece = Raster.PartAt(x, y);
        return piece < 0 || _gone.Contains(piece) ? -1 : piece;
    }

    private void Hover(int piece)
    {
        if (Element is { } ground) ground.Cursor = piece >= 0 ? Cursors.Hand : null;

        if (piece == _hover) return;

        _hover = piece;
        DrawMarks();
    }

    /// <summary>
    /// Paint the highlight: the pick at nearly half strength, the piece under
    /// the pointer at a fifth — the vector side's two marks, as pixels.
    /// </summary>
    private void DrawMarks()
    {
        if (_marks is null || _source is null) return;

        // Nothing to show, and nothing taken apart yet to show it with. A
        // canvas that is only being drawn for a thumbnail ends here.
        if (_picked.Count == 0 && _hover < 0)
        {
            _marks.Opacity = 0;
            return;
        }

        // The size of what is shown, so it lies exactly over the picture —
        // which after a delete is a crop.
        int width = _area.Width, height = _area.Height, stride = _source.Width;
        var map = Raster.Map;

        _markPixels ??= new byte[width * height * 4];
        _markBitmap ??= new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _marks.Source = _markBitmap;

        // Per piece rather than per pixel, so the loop below is an array read
        // rather than a set lookup a million times over.
        var strength = new byte[Raster.Parts.Count];
        foreach (var piece in _picked) strength[piece] = 115;
        if (_hover >= 0 && !_picked.Contains(_hover)) strength[_hover] = 56;

        var accent = Application.Current?.TryFindResource("Accent") is SolidColorBrush brush
            ? brush.Color
            : Color.FromRgb(0x3B, 0x82, 0xF6);

        // Premultiplied, as WPF composites it: a quarter-strength blue is a
        // quarter of blue, not blue said to be a quarter there.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var piece = map[(_area.Y + y) * stride + _area.X + x];
            var a = piece < 0 || _gone.Contains(piece) ? (byte)0 : strength[piece];

            _markPixels[i] = (byte)(accent.B * a / 255);
            _markPixels[i + 1] = (byte)(accent.G * a / 255);
            _markPixels[i + 2] = (byte)(accent.R * a / 255);
            _markPixels[i + 3] = a;
        }

        _markBitmap.WritePixels(new Int32Rect(0, 0, width, height), _markPixels, width * 4, 0);
        _marks.Opacity = 1;
    }

    /// <summary>
    /// A picture as read off disk, with its pieces once somebody has asked for
    /// them — shared between every canvas opened on the same file.
    ///
    /// Shared because the same picture is opened over and over: by its slot's
    /// thumbnail every time the slot is redrawn, by the sponsor card, by the
    /// window, and once more by the packager. Reading and taking apart a logo
    /// is a fraction of a second, but a fraction of a second per slot per
    /// redraw is a window that stutters. Keyed by when the file was written as
    /// well as where it is, so a logo exported again over the top is read
    /// again rather than remembered.
    /// </summary>
    private sealed class Picture
    {
        // A handful: the six slots of the sponsor that is open, and not much
        // more. Each is a few megabytes of pixels and the same again for its
        // pieces, so this is not somewhere to keep a whole project.
        private const int Kept = 8;

        private static readonly List<(string Key, Picture Picture)> Recent = [];

        private readonly byte[] _pixels;
        private RasterParts? _parts;

        public int Width { get; }

        public int Height { get; }

        /// <summary>The file's own pixels, as WPF draws them.</summary>
        public BitmapSource Bitmap { get; }

        /// <summary>The pieces, if they have been worked out yet.</summary>
        public RasterParts? Parts => _parts;

        private Picture(byte[] pixels, int width, int height)
        {
            _pixels = pixels;
            Width = width;
            Height = height;
            Bitmap = ArtworkFile.Bitmap(pixels, width, height);
        }

        public RasterParts Split() => _parts ??= RasterParts.Split(_pixels, Width, Height);

        public static Picture Open(string path)
        {
            var file = new System.IO.FileInfo(path);
            var key = $"{file.FullName}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";

            var at = Recent.FindIndex(entry => entry.Key == key);

            if (at >= 0)
            {
                var hit = Recent[at].Picture;
                Recent.RemoveAt(at);
                Recent.Add((key, hit));
                return hit;
            }

            var (pixels, width, height) = ArtworkFile.Read(path);
            var picture = new Picture(pixels, width, height);

            Recent.Add((key, picture));
            if (Recent.Count > Kept) Recent.RemoveAt(0);

            return picture;
        }
    }
}
