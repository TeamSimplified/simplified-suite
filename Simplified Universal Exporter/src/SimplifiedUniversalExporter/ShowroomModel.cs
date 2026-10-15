using System.Windows.Media;
using System.Windows.Media.Imaging;
using SimplifiedUniversalExporter.Core.Showroom;
using Media3D = System.Windows.Media.Media3D;

namespace SimplifiedUniversalExporter
{
    /// <summary>
    /// The loaded model, read out of the WPF tree into the plain meshes the
    /// RM Renderer's scene is built from (Core.Showroom.ShowroomScene).
    ///
    /// The tree is what the exporter has already assembled for every game it
    /// reads - parts placed, the chosen livery context applied, decal
    /// overlays built - so the renderer shows the same model the rest of the
    /// app does. This runs on the window's thread and only copies arrays; the
    /// pictures are frozen here and read later, off it, when the scene wants
    /// them.
    /// </summary>
    internal static class ShowroomModel
    {
        public static List<ShowroomMesh> Extract(Media3D.Model3DGroup root)
        {
            var list = new List<ShowroomMesh>();
            var images = new Dictionary<BitmapSource, ShowroomImage>();
            Walk(root, Media3D.Matrix3D.Identity, list, images);
            return list;
        }

        private static void Walk(Media3D.Model3D model, Media3D.Matrix3D parent, List<ShowroomMesh> list,
                                 Dictionary<BitmapSource, ShowroomImage> images)
        {
            var m = parent;
            if (model.Transform != null && model.Transform != Media3D.Transform3D.Identity)
                m = Media3D.Matrix3D.Multiply(model.Transform.Value, parent);

            if (model is Media3D.Model3DGroup g)
            {
                foreach (var child in g.Children) Walk(child, m, list, images);
                return;
            }
            if (model is not Media3D.GeometryModel3D gm || gm.Geometry is not Media3D.MeshGeometry3D mesh) return;
            if (mesh.Positions == null || mesh.TriangleIndices == null) return;

            int n = mesh.Positions.Count;
            var pos = new float[n * 3];
            for (int i = 0; i < n; i++)
            {
                var w = m.Transform(mesh.Positions[i]);
                pos[i * 3] = (float)w.X; pos[i * 3 + 1] = (float)w.Y; pos[i * 3 + 2] = (float)w.Z;
            }
            float[] nrm = null;
            if (mesh.Normals != null && mesh.Normals.Count == n)
            {
                nrm = new float[n * 3];
                for (int i = 0; i < n; i++)
                {
                    var v = m.Transform(mesh.Normals[i]);
                    nrm[i * 3] = (float)v.X; nrm[i * 3 + 1] = (float)v.Y; nrm[i * 3 + 2] = (float)v.Z;
                }
            }
            float[] uv = null;
            if (mesh.TextureCoordinates != null && mesh.TextureCoordinates.Count == n)
            {
                uv = new float[n * 2];
                for (int i = 0; i < n; i++)
                {
                    uv[i * 2] = (float)mesh.TextureCoordinates[i].X; uv[i * 2 + 1] = (float)mesh.TextureCoordinates[i].Y;
                }
            }

            var name = MeshSubmeshRegistry.GetSubmeshName(mesh) ?? "";
            var eaid = MeshSubmeshRegistry.GetMaterialEaid(mesh);
            if (string.IsNullOrEmpty(eaid)) GameMaterialCatalog.TryGetMaterialEaid(gm.Material, out eaid);

            list.Add(new ShowroomMesh
            {
                Slot = name,
                MaterialEaid = eaid,
                Overlay = F1ModelLoader.GetIsOverlay(gm) || name.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase),
                Positions = pos,
                Normals = nrm,
                Uvs = uv,
                Indices = mesh.TriangleIndices.ToArray(),
                Colour = Image(gm.Material, images, MeshSubmeshRegistry.GetTextureEaid(mesh)),
            });
        }

        /// <summary>The picture the exporter draws a piece with, frozen so the scene can read it off this thread.</summary>
        /// <remarks>
        /// When the picture is the texture cache's or the hi-res provider's own
        /// decode of <paramref name="textureEaid"/>, untouched, the scene is told
        /// so and sends that texture's block data instead of these pixels.
        /// </remarks>
        private static ShowroomImage Image(Media3D.Material material, Dictionary<BitmapSource, ShowroomImage> images,
                                           string textureEaid)
        {
            if ((FindDiffuse(material)?.Brush as ImageBrush)?.ImageSource is not BitmapSource source) return null;
            if (images.TryGetValue(source, out var known)) return known;

            var cache = F1ModelLoader.ActiveTextureCache ?? F1ModelLoaderClassic.ActiveTextureCache ?? F1ModelLoaderLegacy.ActiveTextureCache;
            bool plain = HighResBrushProvider.Holds(textureEaid, source) || (cache != null && cache.Holds(textureEaid, source));

            BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            if (!bgra.IsFrozen)
            {
                if (!bgra.CanFreeze) return null;
                bgra = (BitmapSource)bgra.GetCurrentValueAsFrozen();
            }
            var frozen = bgra;
            return images[source] = new ShowroomImage
            {
                Key = source,
                TextureEaid = plain ? textureEaid : null,
                Read = () =>
                {
                    int w = frozen.PixelWidth, h = frozen.PixelHeight;
                    var px = new byte[w * h * 4];
                    frozen.CopyPixels(px, w * 4, 0);
                    return (px, w, h);
                },
            };
        }

        private static Media3D.DiffuseMaterial FindDiffuse(Media3D.Material mat)
        {
            if (mat is Media3D.DiffuseMaterial d) return d;
            if (mat is Media3D.MaterialGroup g)
                foreach (var c in g.Children)
                    if (FindDiffuse(c) is { } f) return f;
            return null;
        }
    }
}
