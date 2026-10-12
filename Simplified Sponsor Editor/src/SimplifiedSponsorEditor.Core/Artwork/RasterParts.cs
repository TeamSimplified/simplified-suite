namespace SimplifiedSponsorEditor.Core.Artwork;

/// <summary>A colour, as the pixels here carry one.</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// One piece of a picture: a run of touching pixels that are all the same
/// colour, give or take what a JPEG does to a flat colour.
/// </summary>
/// <param name="Colour">The colour the piece is, which is what painting it moves away from.</param>
/// <param name="Pixels">How many pixels are mostly this piece.</param>
/// <param name="EdgePixels">How many of those sit on the picture's outer edge.</param>
/// <param name="Shade">Which of the picture's colours it is; pieces sharing one are the same colour.</param>
public sealed record RasterPart(Rgb Colour, int Pixels, int EdgePixels, int Shade);

/// <summary>
/// A picture taken apart into the pieces a person would point at in it — the
/// red of a badge, the white of the lettering on it, the background behind both
/// — so a PNG or a JPEG can have pieces repainted and dropped the way a drawing
/// can.
///
/// A drawing arrives in pieces. A picture does not: it is a grid of colours,
/// and the pieces have to be found. Two steps find them. The picture's colours
/// are gathered into the few it is really made of, and every pixel then belongs
/// to whichever of those it is; a piece is a run of touching pixels belonging
/// to the same one. That is what a logo is — a handful of flat colours in
/// shapes — and it is also what makes a photograph come apart into nonsense,
/// which is fine, because nobody repaints the parts of a photograph.
///
/// The pixels between two pieces are the hard part, and the reason this is more
/// than a flood fill. Every edge in a picture is anti-aliased: the pixel where
/// red meets white is pink. Give it to the red and repaint the red blue, and the
/// edge stays a pink line round a blue shape; give it to neither and the shape
/// comes away with a jagged edge. So an edge pixel remembers both pieces it is
/// made of and how much of each, and repainting or dropping either one is done
/// in that proportion. Measured below in <see cref="Apply"/>.
///
/// No WPF: pixels in, pixels out, so this can be exercised from a console with
/// nothing on screen. Everything here is a function of the pixels, so the same
/// file always comes apart the same way and its pieces keep their numbers —
/// which is what lets a project store edits as "piece 4 is blue" and mean the
/// same piece next time. <see cref="Version"/> is what changes if that stops
/// being true.
/// </summary>
public sealed class RasterParts
{
    /// <summary>
    /// Which way of taking pictures apart this is. Stored with every edit made
    /// on a picture, because the edits name pieces by number and the numbers
    /// are this code's: changing a threshold below can renumber every piece in
    /// a picture, and an edit read back against the new numbering would paint
    /// whatever happened to be called 4 now. Raised whenever that could happen.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Below this alpha a pixel is too faint to say anything about colour. It
    /// joins the solid piece it is the soft edge of, if there is one within
    /// reach, and is otherwise the paper the artwork is on. Above zero so the
    /// faint haze some exporters leave across a transparent background does
    /// not turn into a piece covering the canvas.
    /// </summary>
    public const int Paper = 16;

    /// <summary>
    /// Only pixels at least this solid say what colours a picture is made of.
    /// A half-transparent edge pixel's colour is real but is the edge of
    /// something rather than a colour in its own right, and some exporters
    /// darken them besides.
    /// </summary>
    private const int Solid = 128;

    /// <summary>
    /// How far apart two colours have to be — straight-line, in 0–255 RGB — to
    /// be two colours rather than one. JPEG moves a flat colour by up to about
    /// twenty in each channel at ordinary quality, which is ~35 across all
    /// three; deliberate shades in a logo are rarely closer than ~60.
    /// </summary>
    private const double Apart = 52;

    /// <summary>
    /// The most colours a picture is taken to be made of. A logo is a handful;
    /// a picture that wants more than this is a photograph or a gradient, and
    /// splitting it finer only makes pieces smaller, not more useful.
    /// </summary>
    private const int MostColours = 24;

    private readonly byte[] _pixels;
    private readonly int[] _piece;
    private readonly int[] _other;
    private readonly byte[] _share;
    private readonly List<RasterPart> _parts;

    public int Width { get; }

    public int Height { get; }

    /// <summary>The pieces, numbered in the order a scan of the picture first meets them.</summary>
    public IReadOnlyList<RasterPart> Parts => _parts;

    /// <summary>
    /// Which piece each pixel mostly is, row by row; -1 for paper. Handed out
    /// as the array itself because the canvas reads it on every hover, and a
    /// copy of a million entries per mouse move is not a price worth paying
    /// for protecting it from a caller in the same app.
    /// </summary>
    public int[] Map => _piece;

    private RasterParts(byte[] pixels, int width, int height,
                        int[] piece, int[] other, byte[] share, List<RasterPart> parts)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        _piece = piece;
        _other = other;
        _share = share;
        _parts = parts;
    }

    /// <summary>The piece at a pixel, or -1 for paper or outside the picture.</summary>
    public int PartAt(int x, int y) =>
        x < 0 || y < 0 || x >= Width || y >= Height ? -1 : _piece[y * Width + x];

    /// <summary>
    /// Take a picture apart.
    /// </summary>
    /// <param name="bgra">Four bytes a pixel, blue first, alpha not multiplied in.</param>
    public static RasterParts Split(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            throw new ArgumentException("The pixels do not fill the size given for them.");

        var count = width * height;
        var shades = Shades(bgra, count);

        // ── Every pixel to the colour it mostly is ──────────────────────────
        var shade = new int[count];

        for (var p = 0; p < count; p++)
        {
            var i = p * 4;
            shade[p] = bgra[i + 3] < Paper ? -1 : Classify(bgra[i + 2], bgra[i + 1], bgra[i], shades);
        }

        // ── Pieces: runs of touching pixels of one colour ───────────────────
        //
        // Built from the solid pixels first. The soft edge where a shape fades
        // into transparent paper says little about its colour — plenty of
        // exporters darken it, and a pixel a tenth there is mostly the paper's
        // colour anyway — so pieces flooded through it came out ringed in a
        // second piece of whatever colour the fringe happened to read as, and
        // took that colour for their own when the fringe was where a scan met
        // them first. Measured: an orange icon beside dark lettering came out
        // as the lettering's colour.
        var piece = new int[count];
        Array.Fill(piece, -1);
        var sizes = new List<int>();
        var shadeOfPiece = new List<int>();

        for (var start = 0; start < count; start++)
        {
            if (shade[start] < 0 || piece[start] >= 0 || bgra[start * 4 + 3] < Solid) continue;
            Flood(start, solidOnly: true);
        }

        // Then the edges, each to the solid piece it fades out of, so that
        // dropping a shape drops its whole edge with it. Left as paper, the
        // faintest of them stayed behind when the shape went: invisible, but
        // still there, and holding the crop open to where the shape had been —
        // measured, lettering deleted beside an icon left 177 pixels of its
        // edge, and the crop still reached the far side of the word.
        for (var p = 0; p < count; p++)
        {
            if (piece[p] >= 0 || bgra[p * 4 + 3] == 0) continue;

            var near = Beside(p, bgra, piece, width, height);
            if (near < 0) continue;

            piece[p] = near;
            sizes[near]++;
        }

        // Whatever is left is see-through and nowhere near anything solid: a
        // glow, or a shape drawn at half strength. That is real artwork, so it
        // gets pieces of its own.
        for (var start = 0; start < count; start++)
        {
            if (shade[start] < 0 || piece[start] >= 0) continue;
            Flood(start, solidOnly: false);
        }

        void Flood(int start, bool solidOnly)
        {
            var id = sizes.Count;
            var wanted = shade[start];
            var size = 0;
            var stack = new Stack<int>();

            piece[start] = id;
            stack.Push(start);

            while (stack.Count > 0)
            {
                var p = stack.Pop();
                size++;

                int x = p % width, y = p / width;

                // Eight ways rather than four. A thin diagonal stroke — the
                // slash of a 7, the leg of a K — touches itself only at the
                // corners of its pixels, and four-way filling cuts it into a
                // staircase of separate pieces.
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;

                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;

                    var n = ny * width + nx;
                    if (piece[n] >= 0 || shade[n] != wanted) continue;
                    if (solidOnly && bgra[n * 4 + 3] < Solid) continue;

                    piece[n] = id;
                    stack.Push(n);
                }
            }

            sizes.Add(size);
            shadeOfPiece.Add(wanted);
        }

        Absorb(piece, sizes, width, height);

        // ── Numbered afresh, in the order a scan meets them ─────────────────
        //
        // Absorbing leaves gaps in the numbering; closing them in scan order
        // keeps the numbers a function of the picture alone.
        var renumber = new Dictionary<int, int>();

        for (var p = 0; p < count; p++)
        {
            if (piece[p] < 0) continue;
            if (!renumber.TryGetValue(piece[p], out var now))
                renumber[piece[p]] = now = renumber.Count;
            piece[p] = now;
        }

        // The colour of a piece is the colour it was flooded with. Not the
        // colour of the first pixel a scan meets in it: that can be a speck it
        // absorbed, or an edge it was given, and either would name the piece
        // after something it only borders.
        var shadeOf = new int[renumber.Count];
        foreach (var (old, now) in renumber) shadeOf[now] = shadeOfPiece[old];

        var pixels = new int[renumber.Count];
        var edge = new int[renumber.Count];

        for (var p = 0; p < count; p++)
        {
            var id = piece[p];
            if (id < 0) continue;

            pixels[id]++;

            int x = p % width, y = p / width;
            if (x == 0 || y == 0 || x == width - 1 || y == height - 1) edge[id]++;
        }

        var parts = new List<RasterPart>(renumber.Count);

        for (var id = 0; id < renumber.Count; id++)
            parts.Add(new RasterPart(shades[shadeOf[id]], pixels[id], edge[id], shadeOf[id]));

        // ── The other half of every edge pixel ──────────────────────────────
        //
        // Looked for among the pieces actually beside it, and measured against
        // their colours. Looking among every colour in the picture instead was
        // measured and was wrong on a JPEG: the grain along an edge often sits
        // nearer the line to some colour from the far side of the logo than to
        // the one next door, the pairing then found nothing of that colour
        // nearby, and the pixel fell back to being all one piece — so painting
        // everything one colour left the outline of every letter showing, up to
        // 179 levels out on 1.8% of the picture.
        var other = new int[count];
        var share = new byte[count];

        for (var p = 0; p < count; p++)
        {
            (other[p], share[p]) = piece[p] < 0
                ? (-1, (byte)255)
                : Mix(p, bgra, piece, shadeOf, parts, width, height);
        }

        return new RasterParts(bgra, width, height, piece, other, share, parts);
    }

    /// <summary>
    /// Every piece the same colour as <paramref name="part"/>, itself included —
    /// the letters of a word, which are each their own piece and are almost
    /// always meant together.
    /// </summary>
    public IEnumerable<int> SameColour(int part)
    {
        if (part < 0 || part >= _parts.Count) yield break;

        var shade = _parts[part].Shade;

        for (var i = 0; i < _parts.Count; i++)
            if (_parts[i].Shade == shade) yield return i;
    }

    /// <summary>
    /// The pieces that are the background: the colour the picture's outer edge
    /// mostly is, wherever it reaches that edge. Empty when the edge is already
    /// mostly transparent, because then there is no background to take away.
    ///
    /// Only pieces touching the edge. The same white is often inside the logo
    /// too — the lettering on a red badge — and the edge is the one thing that
    /// tells the paper from the lettering. The cost is the holes in an O or an
    /// A, which are background but do not touch the edge; those are picked by
    /// hand, and are one click each.
    ///
    /// More than one colour where the edge needs it. A background that shades
    /// from one colour to another comes apart into bands, and taking the colours
    /// that make up nine tenths of the edge takes the whole of such a
    /// background while still leaving alone a logo that runs off the side of
    /// the picture somewhere.
    /// </summary>
    public IReadOnlyList<int> Background()
    {
        var solid = 0;
        var perimeter = 0;
        var byShade = new Dictionary<int, int>();

        void Look(int x, int y)
        {
            perimeter++;

            var p = y * Width + x;
            if (_pixels[p * 4 + 3] < Solid || _piece[p] < 0) return;

            solid++;
            var shade = _parts[_piece[p]].Shade;
            byShade[shade] = byShade.GetValueOrDefault(shade) + 1;
        }

        for (var x = 0; x < Width; x++)
        {
            Look(x, 0);
            if (Height > 1) Look(x, Height - 1);
        }

        for (var y = 1; y < Height - 1; y++)
        {
            Look(0, y);
            if (Width > 1) Look(Width - 1, y);
        }

        if (solid * 2 < perimeter) return [];

        var taken = new HashSet<int>();
        var covered = 0;

        foreach (var (shade, pixels) in byShade.OrderByDescending(s => s.Value).ThenBy(s => s.Key))
        {
            taken.Add(shade);
            covered += pixels;
            if (covered * 10 >= solid * 9) break;
        }

        var background = new List<int>();

        for (var i = 0; i < _parts.Count; i++)
            if (_parts[i].EdgePixels > 0 && taken.Contains(_parts[i].Shade)) background.Add(i);

        // A background that is the whole picture is not a background, it is a
        // picture of one colour — nothing in it stands apart from the rest to
        // be kept. Picking all of it would leave Delete with nothing to show
        // afterwards and nothing to save.
        return background.Count == _parts.Count ? [] : background;
    }

    /// <summary>
    /// The picture with pieces dropped and repainted, as four bytes a pixel,
    /// blue first, alpha not multiplied in.
    ///
    /// A repainted pixel becomes the new colour, lighter or darker by as much
    /// as it was lighter or darker than its piece — so a piece with shading in
    /// it keeps the shading in the new colour rather than turning into a flat
    /// cut-out of it. Lightness only, not the whole difference. Measured on a
    /// JPEG: the dark band JPEG leaves along the inside of every edge is
    /// almost all colour and almost no lightness (#B52C36 against a #D51E28
    /// piece is one level darker), and carrying the whole difference across
    /// put that band round every letter, up to 47 levels out of the new
    /// colour. Lightness keeps what a designer drew and drops what the
    /// compression added.
    ///
    /// An edge pixel is a mix of two pieces, and is repainted as the same mix
    /// of their new colours.
    ///
    /// Dropping a piece takes its share of every edge pixel out of that pixel's
    /// alpha, and what is left is the colour of the piece that stays. A red
    /// letter on a white ground with the white dropped comes out as a red
    /// letter with a soft edge — the edge it had, against nothing — rather than
    /// one with a pink rim.
    /// </summary>
    public byte[] Apply(IReadOnlySet<int> removed, IReadOnlyDictionary<int, Rgb> painted)
    {
        var output = (byte[])_pixels.Clone();
        if (removed.Count == 0 && painted.Count == 0) return output;

        // Each piece's colour now, worked out once rather than per pixel, and
        // whether it changed or went at all.
        var now = _parts.Select(part => part.Colour).ToArray();
        var moved = new bool[_parts.Count];
        var gone = new bool[_parts.Count];

        foreach (var (id, colour) in painted)
        {
            if (id < 0 || id >= _parts.Count || colour == _parts[id].Colour) continue;
            now[id] = colour;
            moved[id] = true;
        }

        foreach (var id in removed)
            if (id >= 0 && id < _parts.Count) gone[id] = true;

        for (var p = 0; p < _piece.Length; p++)
        {
            var a = _piece[p];
            if (a < 0) continue;

            var o = _other[p];
            var i = p * 4;

            if (o < 0)
            {
                if (gone[a]) output[i + 3] = 0;
                else if (moved[a]) Repaint(output, i, now[a], _parts[a].Colour);
                continue;
            }

            var mine = _share[p] / 255.0;

            if (gone[a] && gone[o])
            {
                output[i + 3] = 0;
            }
            else if (gone[a] || gone[o])
            {
                // Only one half of the pixel survives, so the pixel is that
                // half's colour, as solid as that half of it was.
                var kept = gone[a] ? o : a;
                var colour = now[kept];

                output[i + 2] = colour.R;
                output[i + 1] = colour.G;
                output[i] = colour.B;
                output[i + 3] = Clamp(output[i + 3] * (gone[a] ? 1 - mine : mine));
            }
            else if (moved[a] || moved[o])
            {
                Repaint(output, i, Blend(now[a], now[o], mine), Blend(_parts[a].Colour, _parts[o].Colour, mine));
            }
        }

        return output;
    }

    /// <summary>
    /// A pixel that was <paramref name="was"/>, give or take, made
    /// <paramref name="colour"/> by the same give or take in lightness.
    /// </summary>
    private static void Repaint(byte[] pixels, int i, Rgb colour, Rgb was)
    {
        var lighter = Lightness(pixels[i + 2], pixels[i + 1], pixels[i]) - Lightness(was.R, was.G, was.B);

        pixels[i + 2] = Clamp(colour.R + lighter);
        pixels[i + 1] = Clamp(colour.G + lighter);
        pixels[i] = Clamp(colour.B + lighter);
    }

    private static Rgb Blend(Rgb mine, Rgb theirs, double share) => new(
        Clamp(mine.R * share + theirs.R * (1 - share)),
        Clamp(mine.G * share + theirs.G * (1 - share)),
        Clamp(mine.B * share + theirs.B * (1 - share)));

    /// <summary>How light a colour looks, by the usual weighting of the three.</summary>
    private static double Lightness(double r, double g, double b) => 0.299 * r + 0.587 * g + 0.114 * b;

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    // ── Finding the colours ─────────────────────────────────────────────────

    /// <summary>
    /// The colours a picture is made of.
    ///
    /// Counted on a coarse grid first — 32 steps a channel — so a million
    /// pixels become a few thousand cells, and the commonest cells are taken as
    /// colours in turn. Commonest first is what keeps the edges out: the pink
    /// between red and white is plentiful in a big logo, but never as plentiful
    /// as the red and the white themselves, so by the time it is reached both
    /// are already colours and the pink is recognised as lying between them.
    ///
    /// Ties are broken by the cell's own number, so the order — and with it
    /// every piece's number — depends on nothing but the pixels.
    /// </summary>
    private static Rgb[] Shades(byte[] bgra, int count)
    {
        var cells = new Dictionary<int, (long R, long G, long B, int N)>();
        var solid = 0;

        for (var p = 0; p < count; p++)
        {
            var i = p * 4;
            if (bgra[i + 3] < Solid) continue;

            solid++;
            int r = bgra[i + 2], g = bgra[i + 1], b = bgra[i];
            var key = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);

            var cell = cells.GetValueOrDefault(key);
            cells[key] = (cell.R + r, cell.G + g, cell.B + b, cell.N + 1);
        }

        // Nothing solid at all — a picture that is all haze. Its own pixels
        // still need a colour to belong to, so it is taken to be one colour,
        // whatever its first visible pixel is.
        if (solid == 0)
        {
            for (var p = 0; p < count; p++)
                if (bgra[p * 4 + 3] >= Paper)
                    return [new Rgb(bgra[p * 4 + 2], bgra[p * 4 + 1], bgra[p * 4])];

            return [new Rgb(0, 0, 0)];
        }

        // A colour has to cover this much of the picture to count as one in its
        // own right rather than as grain: a twentieth of a per cent, or eight
        // pixels in a small one. A registered-trademark mark on a 1024 logo is
        // a few hundred pixels and clears it comfortably.
        var least = Math.Max(8, solid / 2000);

        var sums = new List<(double R, double G, double B, long N)>();

        // Colours still too rare to count, gathered until they are not. A
        // colour that is spread over many nearby shades — chrome, a gradient,
        // anything photographed — is a few pixels in each cell and never
        // enough in any one of them, so judged cell by cell it was not a
        // colour at all. Measured on a photograph of a chrome badge on red
        // paint: every grey in the badge was dropped, the whole picture came
        // out as one red piece, and there was nothing to pick but all of it.
        var pending = new List<(double R, double G, double B, long N)>();

        foreach (var (key, cell) in cells.OrderByDescending(c => c.Value.N).ThenBy(c => c.Key))
        {
            var mean = (R: (double)cell.R / cell.N, G: (double)cell.G / cell.N, B: (double)cell.B / cell.N);

            var (nearest, distance) = Nearest(mean, sums);

            if (nearest >= 0 && distance < Apart)
            {
                var s = sums[nearest];
                sums[nearest] = (s.R + cell.R, s.G + cell.G, s.B + cell.B, s.N + cell.N);
                continue;
            }

            // Between two colours already found: an edge, not a colour.
            if (OnSegment(mean, sums) < Apart / 2) continue;

            if (sums.Count >= MostColours) continue;

            if (cell.N >= least)
            {
                sums.Add((cell.R, cell.G, cell.B, cell.N));
                continue;
            }

            var (gathering, apart) = Nearest(mean, pending);

            if (gathering < 0 || apart >= Apart)
            {
                pending.Add((cell.R, cell.G, cell.B, cell.N));
                continue;
            }

            var g = pending[gathering];
            g = (g.R + cell.R, g.G + cell.G, g.B + cell.B, g.N + cell.N);

            if (g.N < least)
            {
                pending[gathering] = g;
                continue;
            }

            // Enough of it now to be a colour of the picture — unless what was
            // gathered is the band JPEG leaves along an edge. That is a mix of
            // the two colours either side, pushed a little off the straight
            // line between them by the compression, so it fails the edge test
            // above one cell at a time and passes it once pooled. Measured:
            // #BA4A54 between a #D51E28 badge and its white lettering, 35 off
            // the line, which came out as a fourth colour ringing every letter.
            // Held to a looser line here than a single cell is, because a
            // colour that only exists as many scraps is the suspect kind.
            pending.RemoveAt(gathering);

            var pooled = (R: g.R / g.N, G: g.G / g.N, B: g.B / g.N);
            if (OnSegment(pooled, sums) < Apart * 0.75) continue;

            sums.Add(g);
        }

        return sums.Select(s => new Rgb(
                (byte)Math.Round(s.R / s.N), (byte)Math.Round(s.G / s.N), (byte)Math.Round(s.B / s.N)))
            .ToArray();
    }

    private static (int Index, double Distance) Nearest(
        (double R, double G, double B) colour, List<(double R, double G, double B, long N)> sums)
    {
        var best = -1;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < sums.Count; i++)
        {
            var s = sums[i];
            var d = Distance(colour, (s.R / s.N, s.G / s.N, s.B / s.N));
            if (d < bestDistance) (best, bestDistance) = (i, d);
        }

        return (best, bestDistance);
    }

    /// <summary>
    /// How close a colour comes to being a mix of two colours already found —
    /// the distance to the straight line between them, for the closest such
    /// pair. Only the middle of the line counts: a colour just past one end of
    /// it is that colour, or a new one, not a mix.
    /// </summary>
    private static double OnSegment(
        (double R, double G, double B) colour, List<(double R, double G, double B, long N)> sums)
    {
        var best = double.MaxValue;

        for (var i = 0; i < sums.Count; i++)
        for (var j = i + 1; j < sums.Count; j++)
        {
            var a = (sums[i].R / sums[i].N, sums[i].G / sums[i].N, sums[i].B / sums[i].N);
            var b = (sums[j].R / sums[j].N, sums[j].G / sums[j].N, sums[j].B / sums[j].N);

            var (t, d) = Project(colour, a, b);
            if (t > 0.1 && t < 0.9 && d < best) best = d;
        }

        return best;
    }

    /// <summary>
    /// Which colour a pixel mostly is — which decides the piece it joins. A
    /// pixel close to one colour is that colour. One that sits better on the
    /// line between two colours than near either is a mix of them, and joins
    /// whichever it is more of; without that, the pink between red and white
    /// would join whatever colour happened to be nearest pink, which can be a
    /// third one entirely, and edges would come apart into slivers of it.
    /// </summary>
    private static int Classify(byte r, byte g, byte b, Rgb[] shades)
    {
        var colour = ((double)r, (double)g, (double)b);

        var nearest = 0;
        var nearestDistance = double.MaxValue;

        for (var i = 0; i < shades.Length; i++)
        {
            var d = Distance(colour, (shades[i].R, shades[i].G, shades[i].B));
            if (d < nearestDistance) (nearest, nearestDistance) = (i, d);
        }

        if (nearestDistance <= Apart / 2 || shades.Length < 2) return nearest;

        var pair = (A: -1, B: -1);
        var pairT = 0.0;
        var pairDistance = double.MaxValue;

        for (var i = 0; i < shades.Length; i++)
        for (var j = i + 1; j < shades.Length; j++)
        {
            var (t, d) = Project(colour,
                (shades[i].R, shades[i].G, shades[i].B),
                (shades[j].R, shades[j].G, shades[j].B));

            if (t <= 0 || t >= 1 || d >= pairDistance) continue;
            (pair, pairT, pairDistance) = ((i, j), t, d);
        }

        // A mix only where the line explains the pixel clearly better than
        // the nearest colour does; otherwise it is that colour, a bit off.
        if (pair.A < 0 || pairDistance >= nearestDistance * 0.75 || pairDistance >= Apart)
            return nearest;

        // t runs from A to B, so a pixel nearer A is more A.
        return pairT <= 0.5 ? pair.A : pair.B;
    }

    /// <summary>
    /// Where a colour lands on the line from <paramref name="a"/> to
    /// <paramref name="b"/> — 0 at a, 1 at b — and how far it is from that line.
    /// </summary>
    private static (double T, double Distance) Project(
        (double R, double G, double B) colour, (double R, double G, double B) a, (double R, double G, double B) b)
    {
        var ab = (R: b.R - a.R, G: b.G - a.G, B: b.B - a.B);
        var length = ab.R * ab.R + ab.G * ab.G + ab.B * ab.B;
        if (length < 1e-9) return (0, Distance(colour, a));

        var t = ((colour.R - a.R) * ab.R + (colour.G - a.G) * ab.G + (colour.B - a.B) * ab.B) / length;
        var on = (a.R + ab.R * t, a.G + ab.G * t, a.B + ab.B * t);
        return (t, Distance(colour, on));
    }

    private static double Distance((double R, double G, double B) a, (double R, double G, double B) b)
    {
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    // ── Tidying the pieces ──────────────────────────────────────────────────

    /// <summary>
    /// Fold pieces too small to point at into whichever neighbour they share
    /// the most border with. A JPEG leaves specks of off-colour inside every
    /// flat area and along every edge, and each would otherwise be a piece of
    /// its own: a logo of five colours coming apart into four hundred pieces,
    /// most of them a few pixels across.
    ///
    /// A speck with no neighbour at all — a full stop alone on transparent
    /// paper — is left as it is. It is small, but it is real.
    /// </summary>
    private static void Absorb(int[] piece, List<int> sizes, int width, int height)
    {
        var count = width * height;
        var least = Math.Max(12, count / 40000);

        var small = new HashSet<int>();
        for (var id = 0; id < sizes.Count; id++)
            if (sizes[id] < least) small.Add(id);

        if (small.Count == 0) return;

        // Where each small piece's pixels are, gathered in one pass rather than
        // searched for once per piece.
        var where = new Dictionary<int, List<int>>();

        for (var p = 0; p < count; p++)
        {
            if (piece[p] < 0 || !small.Contains(piece[p])) continue;
            if (!where.TryGetValue(piece[p], out var list)) where[piece[p]] = list = [];
            list.Add(p);
        }

        // Smallest first, so a speck next to a slightly larger speck goes into
        // whatever that one goes into, rather than the two of them pairing up
        // into a piece that is still too small.
        foreach (var id in small.OrderBy(s => sizes[s]).ThenBy(s => s))
        {
            var touching = new Dictionary<int, int>();

            foreach (var p in where[id])
            {
                int x = p % width, y = p / width;

                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;

                    var n = piece[ny * width + nx];
                    if (n < 0 || n == id) continue;
                    touching[n] = touching.GetValueOrDefault(n) + 1;
                }
            }

            if (touching.Count == 0) continue;

            var into = touching.OrderByDescending(t => t.Value).ThenBy(t => t.Key).First().Key;

            foreach (var p in where[id]) piece[p] = into;
            sizes[into] += sizes[id];
            sizes[id] = 0;

            // The pixels move with it, so a speck that later absorbs into this
            // one's new piece finds them where they now are.
            if (where.TryGetValue(into, out var target)) target.AddRange(where[id]);
        }
    }

    /// <summary>
    /// The solid piece nearest a see-through pixel, looking two pixels out —
    /// the width of an anti-aliased edge at any size a logo is drawn at. -1 if
    /// there is none that close.
    ///
    /// Solid pixels only. An edge pixel given to a piece is not itself a reason
    /// to give its neighbours to that piece too, or a faint glow would be
    /// handed along pixel by pixel until all of it belonged to whatever it
    /// started next to.
    /// </summary>
    private static int Beside(int p, byte[] bgra, int[] piece, int width, int height)
    {
        int x = p % width, y = p / width;

        for (var reach = 1; reach <= 2; reach++)
        for (var dy = -reach; dy <= reach; dy++)
        for (var dx = -reach; dx <= reach; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != reach) continue;

            int nx = x + dx, ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;

            var n = ny * width + nx;
            if (piece[n] >= 0 && bgra[n * 4 + 3] >= Solid) return piece[n];
        }

        return -1;
    }

    /// <summary>
    /// Whether a pixel is an edge — a mix of its own piece and one beside it —
    /// and if so, which piece and how much of the pixel is its own.
    ///
    /// The candidates are the pieces within two pixels, the width of an
    /// anti-aliased edge at any size a logo is drawn at, and each is tried as
    /// the far end of a line from the pixel's own colour. The one whose line
    /// passes closest is the mix it is. A pixel already close to its own
    /// colour is not an edge at all — that is the grain of a JPEG, and calling
    /// it a mix would only move it towards whatever happened to be next door.
    /// </summary>
    private static (int Other, byte Share) Mix(
        int p, byte[] bgra, int[] piece, int[] shadeOf, List<RasterPart> parts, int width, int height)
    {
        const double ItsOwn = 10;

        var own = piece[p];
        var mine = parts[own].Colour;
        var i = p * 4;
        var colour = ((double)bgra[i + 2], (double)bgra[i + 1], (double)bgra[i]);

        var fromOwn = Distance(colour, (mine.R, mine.G, mine.B));
        if (fromOwn <= ItsOwn) return (-1, 255);

        int x = p % width, y = p / width;
        var best = -1;
        var bestT = 0.0;
        var bestDistance = double.MaxValue;

        for (var dy = -2; dy <= 2; dy++)
        for (var dx = -2; dx <= 2; dx++)
        {
            int nx = x + dx, ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;

            var n = piece[ny * width + nx];
            if (n < 0 || n == own || n == best || shadeOf[n] == shadeOf[own]) continue;

            var theirs = parts[n].Colour;
            var (t, d) = Project(colour, (mine.R, mine.G, mine.B), (theirs.R, theirs.G, theirs.B));
            if (t <= 0 || t >= 1 || d >= bestDistance) continue;

            (best, bestT, bestDistance) = (n, t, d);
        }

        // Only where being a mix explains the pixel clearly better than being
        // its own colour slightly off does.
        if (best < 0 || bestDistance >= fromOwn * 0.75) return (-1, 255);

        // t runs from this piece's colour to the other's, so what is left is
        // this piece's share.
        return (best, (byte)Math.Round((1 - bestT) * 255));
    }
}
