// =====================================================================
// UvTemplateExporter — texture-sized UV wireframe PNGs.
//
// For every distinct texture the loaded model samples, rasterize the
// UV layout of ALL triangles bound to that texture into a transparent
// PNG at the texture's exact pixel size. The result drops straight
// onto the texture as a template layer in any image editor -- the
// classic livery-painting workflow.
//
// Data sources (all already produced by the loaders):
//   * geometry  : every GeometryModel3D leaf under the loaded root
//   * texture   : the leaf material's ImageBrush.ImageSource -- exactly
//                 the bitmap the viewport renders (hi-res sidecar when
//                 one was used), so the PNG size always matches what
//                 the modder actually edits
//   * uv channel: the mesh's own TextureCoordinates (the channel its
//                 material samples). Overlay submeshes prefer the RAW
//                 decal UVs stashed in MeshSubmeshRegistry -- their
//                 preview TextureCoordinates are clamp-massaged with
//                 sentinel verts appended for WPF brush pinning.
//   * name      : MeshSubmeshRegistry.GetTextureEaid leaf, falling
//                 back to the submesh name.
//
// Tiled UVs: triangles authored whole wrap-tiles away from [0,1]
// (carbon at u=[-133..102], body paint etc.) are folded back into the
// unit tile PER TRIANGLE (offset by floor of the triangle's centroid),
// which keeps each triangle intact and lands it where the GPU's wrap
// sampler actually reads it. Triangles that genuinely straddle a tile
// boundary render partially outside and get clipped by the bitmap --
// acceptable for a template.
//
// Rendering: two-pass stroke (wider black underlay + 1px white core)
// so the wireframe stays readable over any livery colour. Shared
// edges are deduped per mesh so interior edges draw once.
// =====================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace SimplifiedUniversalExporter
{
    internal static class UvTemplateExporter
    {
        internal sealed class TexGroup
        {
            public BitmapSource Bitmap;   // live only during Collect
            public string Name;
            // Contributing submesh names (diagnostics + name fallback).
            public List<string> Sources = new();
            // True once Name came from a real texture/material EAID.
            // Placeholder names (submesh / texture_N) upgrade when a
            // later mesh in the same group knows its EAID -- character
            // content (racesuits, gloves) binds textures through a path
            // that doesn't register EAIDs on every mesh.
            public bool NameFromEaid;
            // True when any contributing mesh is a decal layer (overlay
            // mesh or decal-named base slot): this texture is a decal /
            // logo atlas whose UV layout should render un-masked in the
            // 2D view and stay OFF the 3D model.
            public bool IsDecalSheet;
            public int Width;
            public int Height;
            // Folded edge segments in UV space: (u0,v0)-(u1,v1).
            public List<(double u0, double v0, double u1, double v1)> Edges = new();
        }

        /// <summary>
        /// PHASE 1 (UI thread -- touches the live visual tree): walk the
        /// model and collect, per distinct texture, its name, pixel size
        /// and every folded UV edge. The result is plain data, safe to
        /// hand to a worker thread.
        /// </summary>
        public static (List<TexGroup> groups, int skipped) Collect(Model3DGroup root)
        {
            var groups = new Dictionary<BitmapSource, TexGroup>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skipped = 0;
            CollectRecursive(root, groups, usedNames, ref skipped);

            var list = new List<TexGroup>();
            foreach (var g in groups.Values)
            {
                if (g.Edges.Count == 0) { skipped++; continue; }
                // Detach from the live BitmapSource: only the pixel size
                // is needed for rendering, and keeping the reference
                // would leak thread-affine state onto the worker.
                g.Width = Math.Max(1, g.Bitmap.PixelWidth);
                g.Height = Math.Max(1, g.Bitmap.PixelHeight);
                g.Bitmap = null;
                list.Add(g);
            }
            return (list, skipped);
        }

        /// <summary>
        /// PHASE 2 (run on a dedicated STA worker -- rasterizing hundreds
        /// of thousands of edges into 2K/4K bitmaps takes seconds per
        /// texture and must not freeze the UI): render each collected
        /// group to "uvmap_&lt;name&gt;.png" in <paramref name="outDir"/>.
        /// <paramref name="progress"/> receives one message per file and
        /// is responsible for its own thread marshalling.
        /// </summary>
        public static int RenderAll(List<TexGroup> groups, string outDir, Action<string> progress)
        {
            Directory.CreateDirectory(outDir);
            int written = 0;
            foreach (var g in groups)
            {
                try
                {
                    string file = Path.Combine(outDir, "uvmap_" + g.Name + ".png");
                    progress?.Invoke($"Rendering UV template {written + 1}/{groups.Count}: " +
                                     $"{g.Name} ({g.Width}x{g.Height}, {g.Edges.Count} edges)...");
                    RenderGroup(g, file);
                    written++;
                }
                catch (Exception ex)
                {
                    progress?.Invoke($"UV template FAILED for '{g.Name}': {ex.Message}");
                }
            }

            // Companion report: which submeshes/parts fed each template.
            // The generic "uvmap_texture_N" cases can be traced from here
            // to the loader path that produced their meshes.
            try
            {
                var rep = new System.Text.StringBuilder();
                rep.AppendLine("UV template report");
                foreach (var g in groups)
                {
                    var uniq = new SortedSet<string>(g.Sources, StringComparer.OrdinalIgnoreCase);
                    rep.AppendLine($"uvmap_{g.Name}.png  ({g.Width}x{g.Height}, " +
                                   $"{g.Edges.Count} edges, nameFromEaid={g.NameFromEaid})");
                    rep.AppendLine("    sources: " + string.Join(", ", uniq));
                }
                File.WriteAllText(Path.Combine(outDir, "uvmap_report.txt"), rep.ToString());
            }
            catch { }
            return written;
        }

        // =================================================================
        // UV VIEW -- live overlay of the wireframe onto the model.
        //
        // The wireframe is rasterized once per distinct texture as a
        // TRANSPARENT layer in texture space (same folded edges the
        // template export draws) and layered over the existing material
        // through a clone of its own ImageBrush -- so whatever mapping
        // the brush applies (plain tiling, or the pinned viewbox tricks
        // the decal overlays use) applies identically to the wireframe.
        // The texture itself is never touched or re-rendered, and the
        // layer is capped at 2048px: plenty for an on-model view, and an
        // order of magnitude cheaper than compositing 4K liveries.
        // =================================================================

        internal sealed class OverlayJob
        {
            public BitmapSource Key;      // live texture; dictionary identity
            public List<(double u0, double v0, double u1, double v1)> Edges;
            public int W;                 // texture size (for aspect + status)
            public int H;
            public string Name;
            // Full BGRA pixels of the texture (straight alpha). The
            // wire is COMPOSITED onto a copy of the texture -- a single
            // ordinary bitmap that both renderers (WPF and the DX PBR
            // conversion) display identically -- and the alpha masks the
            // wire to where the texture actually has content.
            public byte[] TexBgra;
            public bool TexHasAlpha;
            // See TexGroup.IsDecalSheet.
            public bool IsDecalSheet;
        }

        /// <summary>
        /// PHASE 1 of the UV view (UI thread): same walk as the template
        /// export; only the live texture reference (as lookup key), its
        /// size and the folded edges are kept -- plain data, so the
        /// render worker never touches a live bitmap.
        /// </summary>
        public static List<OverlayJob> CollectOverlayJobs(Model3DGroup root)
        {
            var groups = new Dictionary<BitmapSource, TexGroup>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skipped = 0;
            CollectRecursive(root, groups, usedNames, ref skipped);

            var jobs = new List<OverlayJob>();
            foreach (var kv in groups)
            {
                var g = kv.Value;
                if (g.Edges.Count == 0) continue;
                jobs.Add(new OverlayJob
                {
                    Key = kv.Key,
                    Edges = g.Edges,
                    W = Math.Max(1, kv.Key.PixelWidth),
                    H = Math.Max(1, kv.Key.PixelHeight),
                    Name = g.Name,
                    TexBgra = TryExtractBgra(kv.Key, out bool hasAlpha),
                    TexHasAlpha = hasAlpha,
                    IsDecalSheet = g.IsDecalSheet
                });
            }
            return jobs;
        }

        /// <summary>Full BGRA pixels of a texture (straight alpha),
        /// or null when extraction fails. UI thread only (touches the
        /// live bitmap). <paramref name="hasAlpha"/> reports whether the
        /// source format carried an alpha channel at all.</summary>
        private static byte[] TryExtractBgra(BitmapSource src, out bool hasAlpha)
        {
            hasAlpha = !(src.Format == PixelFormats.Bgr24 ||
                         src.Format == PixelFormats.Bgr32 ||
                         src.Format == PixelFormats.Rgb24 ||
                         src.Format == PixelFormats.Gray8);
            try
            {
                var conv = src.Format == PixelFormats.Bgra32
                    ? src
                    : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                var bgra = new byte[w * h * 4];
                conv.CopyPixels(bgra, w * 4, 0);
                return bgra;
            }
            catch { return null; }
        }

        /// <summary>
        /// PHASE 2 of the UV view (any thread -- pure CPU): the wireframe
        /// alone on a transparent ground, frozen so it can cross threads.
        ///
        /// The wire is SEMI-TRANSPARENT (85% white core on a 45% black
        /// underlay) so the texture always reads through it -- body-paint
        /// atlases are small (512px) but carry 70k+ edges, and an opaque
        /// wireframe covered them completely. For the same reason the
        /// layer renders at 1024-2048px regardless of texture size:
        /// upscaling a small texture's layer makes each line finer
        /// relative to the surface it covers.
        ///
        /// Rasterized BY HAND into coverage masks (WPF's vector stroking
        /// never finishes on 200k+ edge sheets; this takes tens of ms).
        /// Masks composite ONCE per pixel, so dense crossings don't
        /// compound toward opaque.
        /// </summary>
        public static BitmapSource RenderOverlayWireframe(OverlayJob job)
        {
            const int minSide = 2048, cap = 4096;
            int w = job.W, h = job.H;
            int longest = Math.Max(w, h);
            double scale = longest > cap ? (double)cap / longest
                         : longest < minSide ? (double)minSide / longest
                         : 1.0;
            w = Math.Max(1, (int)Math.Round(w * scale));
            h = Math.Max(1, (int)Math.Round(h * scale));

            double s = Math.Max(1.0, Math.Max(w, h) / 1024.0);
            int r = Math.Max(1, (int)Math.Round(0.35 * s));

            var mask = new byte[w * h];
            foreach (var (u0, v0, u1, v1) in job.Edges)
                PlotLine(mask, w, h, u0 * w, v0 * h, u1 * w, v1 * h, r);

            // Opaque Pbgra32: the same rich green for every sheet.
            const uint linePx = 0xFF227300u;

            // The alpha mask keeps the wire off a texture's transparent
            // regions -- essential on the 3D model, where decal overlay
            // meshes blanket the car. Decal sheets never reach the 3D
            // model (2D view only), so they show their FULL UV layout,
            // exactly like the exported templates. IsDecalSheet is the
            // mesh-driven signal (overlay / decal-named contributors);
            // the name test stays as a fallback for sheets whose meshes
            // aren't registered.
            bool maskByAlpha = !job.IsDecalSheet &&
                (job.Name ?? "").IndexOf("decal",
                StringComparison.OrdinalIgnoreCase) < 0;

            // Composite: the texture itself (nearest-sampled up to the
            // wire resolution, premultiplied) with the wire drawn on top
            // -- masked to where the texture has content, since decal
            // atlases are transparent outside the stickers and their
            // overlay meshes span the whole car.
            var px = new uint[w * h];
            var tb = job.TexBgra;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int trow = tb != null ? (y * job.H / h) * job.W : 0;
                for (int x = 0; x < w; x++)
                {
                    byte a = 255;
                    if (tb != null)
                    {
                        int ti = (trow + x * job.W / w) * 4;
                        byte bb = tb[ti], gg = tb[ti + 1], rr = tb[ti + 2];
                        a = job.TexHasAlpha ? tb[ti + 3] : (byte)255;
                        px[row + x] = (uint)(a << 24 |
                            (rr * a / 255) << 16 | (gg * a / 255) << 8 | (bb * a / 255));
                    }
                    if (mask[row + x] != 0 && (!maskByAlpha || a >= 32))
                        px[row + x] = linePx;
                }
            }

            var bmp = BitmapSource.Create(w, h, 96, 96,
                PixelFormats.Pbgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Coverage-mask variant of the line plotter, for the
        /// semi-transparent UV-view layers.</summary>
        private static void PlotLine(byte[] mask, int w, int h,
            double x0, double y0, double x1, double y1, int r)
        {
            // Liang-Barsky clip against the bitmap rect padded by r.
            double dx = x1 - x0, dy = y1 - y0;
            double t0 = 0, t1 = 1;
            if (!ClipT(-dx, x0 - (-r), ref t0, ref t1)) return;
            if (!ClipT(dx, (w - 1 + r) - x0, ref t0, ref t1)) return;
            if (!ClipT(-dy, y0 - (-r), ref t0, ref t1)) return;
            if (!ClipT(dy, (h - 1 + r) - y0, ref t0, ref t1)) return;
            double cx0 = x0 + t0 * dx, cy0 = y0 + t0 * dy;
            double cx1 = x0 + t1 * dx, cy1 = y0 + t1 * dy;

            int steps = (int)Math.Ceiling(Math.Max(
                Math.Abs(cx1 - cx0), Math.Abs(cy1 - cy0)));
            if (steps == 0) steps = 1;
            double sx = (cx1 - cx0) / steps, sy = (cy1 - cy0) / steps;

            double fx = cx0, fy = cy0;
            for (int i = 0; i <= steps; i++, fx += sx, fy += sy)
            {
                int cx = (int)Math.Round(fx), cy = (int)Math.Round(fy);
                for (int oy = -r; oy <= r; oy++)
                {
                    int y = cy + oy;
                    if ((uint)y >= (uint)h) continue;
                    int row = y * w;
                    for (int ox = -r; ox <= r; ox++)
                    {
                        int x = cx + ox;
                        if ((uint)x < (uint)w) mask[row + x] = 1;
                    }
                }
            }
        }

        /// <summary>DDA line with a square stamp of the given radius,
        /// clipped to the bitmap first -- edges can reach hundreds of
        /// tiles outside the unit square (carbon at u=[-133..102]), and
        /// without clipping a single such edge means 100k+ steps.</summary>
        private static void PlotLine(uint[] px, int w, int h,
            double x0, double y0, double x1, double y1, uint color, int r)
        {
            // Liang-Barsky clip against the bitmap rect padded by r.
            double dx = x1 - x0, dy = y1 - y0;
            double t0 = 0, t1 = 1;
            if (!ClipT(-dx, x0 - (-r), ref t0, ref t1)) return;
            if (!ClipT(dx, (w - 1 + r) - x0, ref t0, ref t1)) return;
            if (!ClipT(-dy, y0 - (-r), ref t0, ref t1)) return;
            if (!ClipT(dy, (h - 1 + r) - y0, ref t0, ref t1)) return;
            double cx0 = x0 + t0 * dx, cy0 = y0 + t0 * dy;
            double cx1 = x0 + t1 * dx, cy1 = y0 + t1 * dy;

            int steps = (int)Math.Ceiling(Math.Max(
                Math.Abs(cx1 - cx0), Math.Abs(cy1 - cy0)));
            if (steps == 0) steps = 1;
            double sx = (cx1 - cx0) / steps, sy = (cy1 - cy0) / steps;

            double fx = cx0, fy = cy0;
            for (int i = 0; i <= steps; i++, fx += sx, fy += sy)
            {
                int cx = (int)Math.Round(fx), cy = (int)Math.Round(fy);
                for (int oy = -r; oy <= r; oy++)
                {
                    int y = cy + oy;
                    if ((uint)y >= (uint)h) continue;
                    int row = y * w;
                    for (int ox = -r; ox <= r; ox++)
                    {
                        int x = cx + ox;
                        if ((uint)x < (uint)w) px[row + x] = color;
                    }
                }
            }
        }

        private static bool ClipT(double denom, double num, ref double t0, ref double t1)
        {
            if (denom == 0) return num >= 0;
            double t = num / denom;
            if (denom < 0)
            {
                if (t > t1) return false;
                if (t > t0) t0 = t;
            }
            else
            {
                if (t < t0) return false;
                if (t < t1) t1 = t;
            }
            return true;
        }

        // -----------------------------------------------------------------
        private static void CollectRecursive(
            Model3D model,
            Dictionary<BitmapSource, TexGroup> groups,
            HashSet<string> usedNames,
            ref int skipped)
        {
            if (model is Model3DGroup grp)
            {
                // Tyres are view-only: the per-wheel wrappers the loader
                // builds (wheel_fl/fr/bl/br) contribute neither UV
                // templates nor UV-view overlays.
                string wrapName = ModelPartNameRegistry.GetPartName(grp);
                if (wrapName != null &&
                    wrapName.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase))
                    return;
                foreach (var child in grp.Children)
                    CollectRecursive(child, groups, usedNames, ref skipped);
                return;
            }
            if (!(model is GeometryModel3D gm) ||
                !(gm.Geometry is MeshGeometry3D mesh) ||
                mesh.TriangleIndices == null || mesh.TriangleIndices.Count < 3)
                return;

            var bmp = ExtractBitmap(gm.Material);
            if (bmp == null) { skipped++; return; }

            // Tiny textures (solid-colour fills, 1x1 paint chips, LCD
            // stand-ins) make useless templates -- skip anything under
            // 10x10 px.
            if (bmp.PixelWidth < 10 || bmp.PixelHeight < 10) { skipped++; return; }

            // UV source: for overlays, the AUTHORED pair-2 layout (full
            // base triangles + unfiltered decal UVs) so decal panels show
            // as their authored islands (boxes etc.) instead of the
            // content-filtered triangle subset that hugs the sticker
            // pixels; raw decal UVs as fallback, else the mesh's own
            // TextureCoordinates.
            string subName = MeshSubmeshRegistry.GetSubmeshName(mesh) ?? "";
            bool isDecalLayer =
                subName.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase) ||
                F1ModelLoader.IsDecalLikeSlotName(subName);
            PointCollection uvs = null;
            System.Collections.Generic.IList<int> tri = mesh.TriangleIndices;
            bool cullOutOfTile = false;
            if (subName.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase))
            {
                if (MeshSubmeshRegistry.TryGetUvViewDecal(mesh, out var vUvs, out var vIdx))
                {
                    uvs = vUvs;
                    tri = vIdx;
                    // The full base triangle list includes the tiled
                    // carbon copies the overlay filter drops (pair-2 u up
                    // to 2.5+). Folding those into the unit tile would
                    // smear junk over the authored layout -- skip any
                    // triangle whose centroid sits outside the tile.
                    cullOutOfTile = true;
                }
                else
                    uvs = MeshSubmeshRegistry.GetRawDecalUv(mesh);
            }
            if (uvs == null) uvs = mesh.TextureCoordinates;
            if (uvs == null || uvs.Count == 0) { skipped++; return; }

            if (!groups.TryGetValue(bmp, out var g))
            {
                bool fromEaid;
                string name = DeriveName(mesh, subName, usedNames, out fromEaid);
                g = new TexGroup { Bitmap = bmp, Name = name, NameFromEaid = fromEaid };
                groups[bmp] = g;
            }
            else if (!g.NameFromEaid)
            {
                // Try to upgrade a placeholder name from this mesh.
                string eaidName = EaidLeafName(mesh);
                if (!string.IsNullOrEmpty(eaidName))
                {
                    usedNames.Remove(g.Name);
                    g.Name = UniqueName(eaidName, usedNames);
                    g.NameFromEaid = true;
                }
            }

            // Record where this group's geometry comes from -- surfaces in
            // uvmap_report.txt so nameless templates can be traced to the
            // loader path that produced their meshes.
            string srcName = subName;
            if (string.IsNullOrWhiteSpace(srcName))
                srcName = MeshFamilyRegistry.GetFamily(mesh);
            g.Sources.Add(string.IsNullOrWhiteSpace(srcName) ? "<unnamed>" : srcName);
            if (isDecalLayer) g.IsDecalSheet = true;

            // Dedupe shared edges per mesh (interior edges belong to two
            // triangles; draw once).
            var seen = new HashSet<(int, int)>();
            var idx = tri;
            for (int t = 0; t + 2 < idx.Count; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if (a < 0 || b < 0 || c < 0 ||
                    a >= uvs.Count || b >= uvs.Count || c >= uvs.Count)
                    continue;   // sentinel verts past the raw-UV array etc.

                Point pa = uvs[a], pb = uvs[b], pc = uvs[c];

                // Fold the triangle into the unit tile by its centroid.
                double cu = (pa.X + pb.X + pc.X) / 3.0;
                double cv = (pa.Y + pb.Y + pc.Y) / 3.0;
                double du = Math.Floor(cu), dv = Math.Floor(cv);
                if (du != 0 || dv != 0)
                {
                    if (cullOutOfTile) continue;   // tiled-copy junk, not layout
                    pa = new Point(pa.X - du, pa.Y - dv);
                    pb = new Point(pb.X - du, pb.Y - dv);
                    pc = new Point(pc.X - du, pc.Y - dv);
                }

                AddEdge(g, seen, a, b, pa, pb);
                AddEdge(g, seen, b, c, pb, pc);
                AddEdge(g, seen, c, a, pc, pa);
            }
        }

        private static void AddEdge(TexGroup g, HashSet<(int, int)> seen,
                                    int i0, int i1, Point p0, Point p1)
        {
            var key = i0 < i1 ? (i0, i1) : (i1, i0);
            if (!seen.Add(key)) return;
            g.Edges.Add((p0.X, p0.Y, p1.X, p1.Y));
        }

        internal static BitmapSource ExtractBitmap(Material mat)
        {
            switch (mat)
            {
                case DiffuseMaterial dm when dm.Brush is ImageBrush ib:
                    return ib.ImageSource as BitmapSource;
                case MaterialGroup mg:
                    foreach (var m in mg.Children)
                    {
                        var b = ExtractBitmap(m);
                        if (b != null) return b;
                    }
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>Texture-EAID (or material-EAID) leaf for the mesh,
        /// extension stripped; null when neither is registered.</summary>
        private static string EaidLeafName(MeshGeometry3D mesh)
        {
            string eaid = MeshSubmeshRegistry.GetTextureEaid(mesh);
            if (string.IsNullOrEmpty(eaid))
                eaid = MeshSubmeshRegistry.GetMaterialEaid(mesh);
            if (string.IsNullOrEmpty(eaid)) return null;

            int slash = eaid.LastIndexOf('/');
            string name = slash >= 0 ? eaid.Substring(slash + 1) : eaid;
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);
            foreach (char bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        private static string UniqueName(string name, HashSet<string> usedNames)
        {
            string unique = name;
            int n = 2;
            while (!usedNames.Add(unique)) unique = $"{name}_{n++}";
            return unique;
        }

        private static string DeriveName(MeshGeometry3D mesh, string subName,
                                         HashSet<string> usedNames, out bool fromEaid)
        {
            string name = EaidLeafName(mesh);
            fromEaid = !string.IsNullOrEmpty(name);
            if (!fromEaid)
            {
                name = string.IsNullOrWhiteSpace(subName) ? "texture" : subName;
                foreach (char bad in Path.GetInvalidFileNameChars())
                    name = name.Replace(bad, '_');
            }
            return UniqueName(name, usedNames);
        }

        // -----------------------------------------------------------------
        private static void RenderGroup(TexGroup g, string outPath)
        {
            int w = g.Width;
            int h = g.Height;

            // Hand-rasterized, same as the UV view: WPF's vector stroking
            // (StreamGeometry + Pen through RenderTargetBitmap) never
            // finishes on edge-dense sheets -- a 228k-edge decal sheet at
            // 1024px hung indefinitely, where this takes ~35ms. Two
            // passes: black underlay then white core, so lines read on
            // both light and dark texture regions.
            var px = new uint[w * h];
            foreach (var (u0, v0, u1, v1) in g.Edges)
                PlotLine(px, w, h, u0 * w, v0 * h, u1 * w, v1 * h, 0xFF000000u, 1);
            foreach (var (u0, v0, u1, v1) in g.Edges)
                PlotLine(px, w, h, u0 * w, v0 * h, u1 * w, v1 * h, 0xFFFFFFFFu, 0);

            var bmp = BitmapSource.Create(w, h, 96, 96,
                PixelFormats.Pbgra32, null, px, w * 4);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(outPath))
                enc.Save(fs);
        }
    }
}
