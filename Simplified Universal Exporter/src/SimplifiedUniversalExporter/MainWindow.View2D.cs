using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Media3D;

namespace SimplifiedUniversalExporter
{
    /// <summary>
    /// The 2D view (v2.1): the main area shows one of the loaded model's
    /// textures flat, picked from a texture selector that takes over the
    /// side tab (the Models list serves the 3D mode, the selector the 2D
    /// mode; the hamburger toggles the tab's visibility in both). When
    /// the UV view is on, the displayed bitmap is the wire-composited
    /// copy from the overlay cache, so the wireframe shows in 2D too.
    /// </summary>
    public partial class MainWindow
    {
        public sealed class TextureEntry
        {
            public string Name { get; set; }
            public System.Windows.Media.Imaging.BitmapSource Bitmap { get; set; }
            public override string ToString() => Name;
        }

        private readonly ObservableCollection<TextureEntry> _textures2D =
            new ObservableCollection<TextureEntry>();

        private bool _is2DMode;

        private void OnMode3DClick(object sender, RoutedEventArgs e) => SetViewMode(false);
        private void OnMode2DClick(object sender, RoutedEventArgs e) => SetViewMode(true);

        private void SetViewMode(bool is2D)
        {
            if (_is2DMode == is2D) return;
            _is2DMode = is2D;
            UpdateModeButtons();

            // Side tab content follows the mode; its visibility stays
            // whatever the hamburger last set.
            ModelsPane.Visibility = is2D ? Visibility.Collapsed : Visibility.Visible;
            TexturesPane.Visibility = is2D ? Visibility.Visible : Visibility.Collapsed;

            if (is2D)
            {
                Viewport.Visibility = Visibility.Collapsed;
                DxViewport.Visibility = Visibility.Collapsed;
                CameraButtonsPanel.Visibility = Visibility.Collapsed;
                View2D.Visibility = Visibility.Visible;
                Populate2DTextures();
                Update2DImage();
            }
            else
            {
                View2D.Visibility = Visibility.Collapsed;
                CameraButtonsPanel.Visibility = Visibility.Visible;
                RefreshDxView();   // fronts DX (or the WPF fallback)
            }
        }

        /// <summary>Accent the active mode button (the code equivalent of
        /// DynamicResource, so a palette swap keeps following).</summary>
        private void UpdateModeButtons()
        {
            if (_is2DMode)
            {
                Mode3DButton.ClearValue(BackgroundProperty);
                Mode2DButton.SetResourceReference(BackgroundProperty, "AccentSoft");
            }
            else
            {
                Mode2DButton.ClearValue(BackgroundProperty);
                Mode3DButton.SetResourceReference(BackgroundProperty, "AccentSoft");
            }
        }

        /// <summary>Rebuild the selector from the live scene, keeping the
        /// current selection by name when it survives a reload.</summary>
        private void Populate2DTextures()
        {
            string keep = (Texture2DList.SelectedItem as TextureEntry)?.Name;
            _textures2D.Clear();
            if (Texture2DList.ItemsSource == null)
                Texture2DList.ItemsSource = _textures2D;

            if (_currentModel?.Content is not Model3DGroup root ||
                root.Children.Count == 0)
                return;

            List<UvTemplateExporter.OverlayJob> jobs;
            try { jobs = UvTemplateExporter.CollectOverlayJobs(root); }
            catch { return; }
            jobs.Sort((a, b) => string.Compare(a.Name, b.Name,
                StringComparison.OrdinalIgnoreCase));
            foreach (var j in jobs)
                _textures2D.Add(new TextureEntry { Name = j.Name, Bitmap = j.Key });

            TextureEntry sel = null;
            if (keep != null)
                foreach (var t in _textures2D)
                    if (string.Equals(t.Name, keep, StringComparison.OrdinalIgnoreCase))
                    { sel = t; break; }
            Texture2DList.SelectedItem = sel ?? (_textures2D.Count > 0 ? _textures2D[0] : null);
        }

        private void OnTexture2DSelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e) => Update2DImage();

        /// <summary>Show the selected texture -- the wire-composited copy
        /// when the UV view is on and has one for it.</summary>
        private void Update2DImage()
        {
            var entry = Texture2DList.SelectedItem as TextureEntry;
            if (entry?.Bitmap == null)
            {
                Texture2DImage.Source = null;
                return;
            }
            System.Windows.Media.Imaging.BitmapSource shown = entry.Bitmap;
            if (UvViewCheckBox?.IsChecked == true &&
                _uvOverlayCache.TryGetValue(entry.Bitmap, out var composite))
                shown = composite;
            Texture2DImage.Source = shown;
        }
    }
}
