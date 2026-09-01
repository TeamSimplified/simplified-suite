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

namespace F1modding3dtoolV1
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

        // -----------------------------------------------------------------
        private static void CollectRecursive(
            Model3D model,
            Dictionary<BitmapSource, TexGroup> groups,
            HashSet<string> usedNames,
            ref int skipped)
        {
            if (model is Model3DGroup grp)
            {
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

            // UV source: raw decal UVs for overlays, else the mesh's own
            // TextureCoordinates.
            string subName = MeshSubmeshRegistry.GetSubmeshName(mesh) ?? "";
            PointCollection uvs = null;
            if (subName.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase))
                uvs = MeshSubmeshRegistry.GetRawDecalUv(mesh);
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

            // Dedupe shared edges per mesh (interior edges belong to two
            // triangles; draw once).
            var seen = new HashSet<(int, int)>();
            var idx = mesh.TriangleIndices;
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

        private static BitmapSource ExtractBitmap(Material mat)
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
        // LOCAL CHANGE (Simplified ERP): private -> internal, so a single
        // template can be rendered on its own. RenderAll writes every texture
        // in the archive plus a report; exporting the UV map of the one texture
        // the user has selected needs just this. Nothing else here is modified.
        internal static void RenderGroup(TexGroup g, string outPath)
        {
            int w = g.Width;
            int h = g.Height;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // Two passes: black underlay then white core, so lines
                // read on both light and dark texture regions.
                var under = new Pen(Brushes.Black, 2.4) { LineJoin = PenLineJoin.Round };
                var core = new Pen(Brushes.White, 1.0) { LineJoin = PenLineJoin.Round };
                under.Freeze();
                core.Freeze();

                var geo = new StreamGeometry();
                using (var gc = geo.Open())
                {
                    foreach (var (u0, v0, u1, v1) in g.Edges)
                    {
                        gc.BeginFigure(new Point(u0 * w, v0 * h), false, false);
                        gc.LineTo(new Point(u1 * w, v1 * h), true, false);
                    }
                }
                geo.Freeze();

                dc.DrawGeometry(null, under, geo);
                dc.DrawGeometry(null, core, geo);
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(outPath))
                enc.Save(fs);
        }
    }
}
