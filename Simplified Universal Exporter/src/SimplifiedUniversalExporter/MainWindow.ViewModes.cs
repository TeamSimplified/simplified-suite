using HelixToolkit.Wpf;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Media3D;

namespace SimplifiedUniversalExporter
{
    /// <summary>Viewport view modes: Untextured flat-shading and the UV view overlay.</summary>
    public partial class MainWindow
    {

        /// <summary>
        /// Original DiffuseMaterial for each GeometryModel3D leaf,
        /// captured the first time "Untextured" gets toggled ON so
        /// we can restore it exactly when toggled OFF. Keyed by
        /// object reference; entries stay valid for as long as the
        /// leaf is alive in the scene.
        /// </summary>
        private readonly Dictionary<GeometryModel3D, (Material front, Material back)>
            _savedMaterials = new Dictionary<GeometryModel3D, (Material, Material)>();

        /// <summary>Frozen flat-grey material reused across every
        /// leaf when Untextured is on. Cheap enough to keep as a
        /// single instance; freezing lets WPF share it across the
        /// entire scene graph.</summary>
        private static readonly DiffuseMaterial _flatShadedMaterial = BuildFlatShaded();
        private static DiffuseMaterial BuildFlatShaded()
        {
            var m = new DiffuseMaterial(new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(200, 200, 200)));
            m.Freeze();
            return m;
        }

        private void OnUntexturedToggle(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null || !(_currentModel.Content is Model3DGroup root))
                return;
            bool untextured = UntexturedCheckBox?.IsChecked == true;
            var leaves = new List<GeometryModel3D>();
            CollectGeometryLeavesRecursive(root, leaves);

            if (untextured)
            {
                // The two view swaps must not stack -- each one snapshots
                // "original" materials, so layering them would snapshot the
                // other view's swapped materials as originals.
                if (UvViewCheckBox?.IsChecked == true)
                {
                    UvViewCheckBox.IsChecked = false;
                    RestoreUvView(root);
                }
                foreach (var g in leaves)
                {
                    if (g == null) continue;
                    // Snapshot the original materials (only once per leaf --
                    // if the user re-toggles we don't want to save the
                    // flat-shaded one as "original").
                    if (!_savedMaterials.ContainsKey(g))
                        _savedMaterials[g] = (g.Material, g.BackMaterial);
                    g.Material = _flatShadedMaterial;
                    g.BackMaterial = _flatShadedMaterial;
                }
                LogToUi($"Untextured: ON ({leaves.Count} mesh(es) flat-shaded).");
            }
            else
            {
                int restored = 0;
                foreach (var g in leaves)
                {
                    if (g == null) continue;
                    if (_savedMaterials.TryGetValue(g, out var pair))
                    {
                        g.Material = pair.front;
                        g.BackMaterial = pair.back;
                        restored++;
                    }
                }
                _savedMaterials.Clear();
                LogToUi($"Untextured: OFF ({restored} mesh(es) restored).");
            }

            RefreshDxView();
        }

        // =====================================================================
        // UV VIEW (wireframe overlaid onto the live textures)
        // =====================================================================

        /// <summary>Original materials snapshotted when "UV view" swaps
        /// them, restored exactly on toggle-off. Separate from
        /// <see cref="_savedMaterials"/> -- the two views are mutually
        /// exclusive but must never confuse each other's snapshots.</summary>
        private readonly Dictionary<GeometryModel3D, (Material front, Material back)>
            _uvViewSaved = new Dictionary<GeometryModel3D, (Material, Material)>();

        /// <summary>Textures identified as decal sheets during collect:
        /// their composites exist in the cache (for the 2D view) but are
        /// never applied to the 3D model. Cleared with the cache.</summary>
        private readonly HashSet<System.Windows.Media.Imaging.BitmapSource>
            _uvDecalTextures = new HashSet<System.Windows.Media.Imaging.BitmapSource>();

        /// <summary>Composited texture (original + wireframe) per live
        /// texture bitmap. Keyed by reference, so a model reload (new
        /// bitmaps) naturally misses; cleared on reload to drop the old
        /// entries. Kept across toggle-offs so re-toggling is instant.</summary>
        private readonly Dictionary<System.Windows.Media.Imaging.BitmapSource,
                                    System.Windows.Media.Imaging.BitmapSource>
            _uvOverlayCache = new Dictionary<System.Windows.Media.Imaging.BitmapSource,
                                             System.Windows.Media.Imaging.BitmapSource>();

        private void OnUvViewToggle(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null || !(_currentModel.Content is Model3DGroup root))
                return;

            if (UvViewCheckBox?.IsChecked != true)
            {
                RestoreUvView(root);
                RefreshDxView();
                return;
            }

            // Mutual exclusion with Untextured (see OnUntexturedToggle).
            if (UntexturedCheckBox?.IsChecked == true)
            {
                UntexturedCheckBox.IsChecked = false;
                OnUntexturedToggle(this, new RoutedEventArgs());
            }

            List<UvTemplateExporter.OverlayJob> jobs;
            try
            {
                jobs = UvTemplateExporter.CollectOverlayJobs(root);
            }
            catch (Exception ex)
            {
                StatusText.Text = "UV view failed: " + ex.Message;
                UvViewCheckBox.IsChecked = false;
                return;
            }

            // Decal sheets render their composite too (the 2D view
            // shows it), but they are remembered here so ApplyUvView
            // never layers one onto the 3D model -- their overlay meshes
            // blanket the whole car and the islands add noise on top of
            // every other texture's wireframe.
            foreach (var j in jobs)
            {
                if (j.IsDecalSheet ||
                    (j.Name ?? "").IndexOf("decal",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    _uvDecalTextures.Add(j.Key);
            }

            // Only textures not already composited need the worker.
            jobs.RemoveAll(j => _uvOverlayCache.ContainsKey(j.Key));
            if (jobs.Count == 0)
            {
                ApplyUvView(root);
                return;
            }

            RenderOverlayJobsAsync(jobs);
        }

        /// <summary>
        /// Rasterize the wireframe layers one dispatcher tick at a time
        /// ON THE UI THREAD. RenderTargetBitmap on a worker thread wedges
        /// WPF's composition engine after the first render (the thread
        /// never pumps, so the channel notification for the second render
        /// never arrives) -- and now that the layers are wireframe-only
        /// and capped at 2048px, each render is fast enough that yielding
        /// between textures keeps the window responsive.
        /// </summary>
        private async void RenderOverlayJobsAsync(List<UvTemplateExporter.OverlayJob> jobs)
        {
            UvViewCheckBox.IsEnabled = false;
            UntexturedCheckBox.IsEnabled = false;
            try
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    var job = jobs[i];
                    StatusText.Text = $"UV view: rendering wireframe {i + 1}/{jobs.Count} " +
                                      $"({job.Name})...";
                    // Pure CPU rasterization into a pixel array -- safe on
                    // the thread pool, result comes back frozen.
                    var wire = await System.Threading.Tasks.Task.Run(
                        () => UvTemplateExporter.RenderOverlayWireframe(job));
                    _uvOverlayCache[job.Key] = wire;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "UV view failed: " + ex.Message;
                UvViewCheckBox.IsChecked = false;
                return;
            }
            finally
            {
                UvViewCheckBox.IsEnabled = true;
                UntexturedCheckBox.IsEnabled = true;
            }

            // The user may have toggled off or reloaded a new model while
            // the renders were in flight; only apply when still wanted.
            if (UvViewCheckBox.IsChecked == true &&
                _currentModel?.Content is Model3DGroup liveRoot)
                ApplyUvView(liveRoot);
        }

        /// <summary>Swap every leaf whose material samples a composited
        /// texture over to the overlay version, snapshotting originals.</summary>
        private void ApplyUvView(Model3DGroup root)
        {
            var leaves = new List<GeometryModel3D>();
            CollectGeometryLeavesRecursive(root, leaves);
            int swapped = 0;
            foreach (var g in leaves)
            {
                if (g == null || _uvViewSaved.ContainsKey(g)) continue;
                bool any = false;
                var front = BuildOverlayMaterial(g.Material, ref any);
                var back = BuildOverlayMaterial(g.BackMaterial, ref any);
                if (!any) continue;
                _uvViewSaved[g] = (g.Material, g.BackMaterial);
                g.Material = front;
                g.BackMaterial = back;
                swapped++;
            }
            RefreshDxView();   // analysis mode -> WPF viewport fronts
            StatusText.Text = $"UV view: ON ({swapped} mesh(es) overlaid).";
            LogToUi($"UV view: ON ({swapped} mesh(es) overlaid, " +
                    $"{_uvOverlayCache.Count} texture(s) composited).");
        }

        private void RestoreUvView(Model3DGroup root)
        {
            int restored = 0;
            foreach (var kv in _uvViewSaved)
            {
                var g = kv.Key;
                if (g == null) continue;
                g.Material = kv.Value.front;
                g.BackMaterial = kv.Value.back;
                restored++;
            }
            _uvViewSaved.Clear();
            if (restored > 0)
            {
                StatusText.Text = $"UV view: OFF ({restored} mesh(es) restored).";
                LogToUi($"UV view: OFF ({restored} mesh(es) restored).");
            }
        }

        /// <summary>
        /// Rebuild <paramref name="mat"/> with the mesh's PRIMARY texture
        /// (the first ImageBrush, the one the UV collector groups this
        /// mesh under) swapped for its wire-composited copy. The brush is
        /// CLONED, keeping Viewport/Viewbox/TileMode/Transform. Returns
        /// <paramref name="mat"/> as-is (and leaves <paramref name="any"/>
        /// alone) when this mesh's texture has no composite.
        /// </summary>
        private Material BuildOverlayMaterial(Material mat, ref bool any)
        {
            if (mat == null) return null;
            var primary = UvTemplateExporter.ExtractBitmap(mat);
            if (primary == null ||
                _uvDecalTextures.Contains(primary) ||
                !_uvOverlayCache.TryGetValue(primary, out var composite))
                return mat;
            var swapped = SwapImageSource(mat, primary, composite);
            if (swapped == null) return mat;
            any = true;
            return swapped;
        }

        /// <summary>Deep-rebuild of a material tree with every ImageBrush
        /// sampling <paramref name="from"/> re-pointed at
        /// <paramref name="to"/>. Null when nothing matched (caller keeps
        /// the original). Untouched children are shared, not cloned.</summary>
        private static Material SwapImageSource(Material mat,
            System.Windows.Media.Imaging.BitmapSource from,
            System.Windows.Media.Imaging.BitmapSource to)
        {
            switch (mat)
            {
                case MaterialGroup mg:
                {
                    bool anyChild = false;
                    var ng = new MaterialGroup();
                    foreach (var child in mg.Children)
                    {
                        var sw = SwapImageSource(child, from, to);
                        ng.Children.Add(sw ?? child);
                        if (sw != null) anyChild = true;
                    }
                    return anyChild ? ng : null;
                }
                case DiffuseMaterial dm when dm.Brush is System.Windows.Media.ImageBrush ib &&
                    ReferenceEquals(ib.ImageSource, from):
                {
                    var nb = ib.Clone();
                    nb.ImageSource = to;
                    nb.Freeze();
                    return new DiffuseMaterial(nb)
                    {
                        Color = dm.Color,
                        AmbientColor = dm.AmbientColor
                    };
                }
                case EmissiveMaterial em when em.Brush is System.Windows.Media.ImageBrush ib &&
                    ReferenceEquals(ib.ImageSource, from):
                {
                    var nb = ib.Clone();
                    nb.ImageSource = to;
                    nb.Freeze();
                    return new EmissiveMaterial(nb) { Color = em.Color };
                }
                default:
                    return null;
            }
        }

        /// <summary>Recursive geometry-leaf walker used by
        /// OnUntexturedToggle (differs from
        /// <see cref="CollectGeometryLeaves"/> only in being an
        /// instance method available at this call site).</summary>
        private static void CollectGeometryLeavesRecursive(
            Model3D model, List<GeometryModel3D> leaves)
        {
            if (model == null) return;
            if (model is GeometryModel3D gm) { leaves.Add(gm); return; }
            if (model is Model3DGroup grp)
                foreach (var ch in grp.Children) CollectGeometryLeavesRecursive(ch, leaves);
        }
    }
}
