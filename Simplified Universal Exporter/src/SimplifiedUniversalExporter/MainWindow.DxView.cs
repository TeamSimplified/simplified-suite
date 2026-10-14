using System;
using System.Windows;
using System.Windows.Media.Media3D;

namespace SimplifiedUniversalExporter
{
    /// <summary>
    /// The high-quality display path: ShowroomDx.PopulateNeutral renders
    /// the loaded model through the DirectX PBR pipeline (supersampled,
    /// MSAA, game-material response) with a neutral even light rig and a
    /// black backdrop -- a pure renderer upgrade over the WPF viewport,
    /// same look, same camera behaviour. The WPF viewport still fronts
    /// for the analysis modes (UV view / Untextured, whose WPF material
    /// swaps the DX conversion must not bake in) and is the fallback
    /// when DX is unavailable. The two cameras are kept in sync when the
    /// view switches, so the framing never jumps.
    ///
    /// Coordinate note: the WPF viewport is Z-up (content + the +90° X
    /// upright visual transform); the DX scene renders the RAW content
    /// under a Y-up camera. The two worlds differ by exactly that +90° X
    /// rotation, so cameras map as (x,y,z)wpf -> (x,z,-y)dx and back.
    /// </summary>
    public partial class MainWindow
    {
        private bool _dxViewLive;

        /// <summary>Rebuild the DX scene from the current WPF tree and
        /// front it -- unless an analysis mode wants the WPF viewport,
        /// nothing is loaded, or DX fails (fall back to WPF).</summary>
        private void RefreshDxView()
        {
            // 2D mode owns the main area: keep the flat view fronted and
            // refresh its selector/image against the (possibly new)
            // scene; the DX scene is rebuilt when 3D mode returns.
            if (_is2DMode)
            {
                Populate2DTextures();
                Update2DImage();
                return;
            }

            // The analysis modes (UV view / Untextured) render through
            // DX too: their swaps are ordinary textured/flat materials
            // (the UV wire is composited INTO the texture), so the
            // conversion shows them identically.
            if (_currentModel?.Content is not Model3DGroup root ||
                root.Children.Count == 0)
            {
                ShowWpfView();
                return;
            }

            // Repopulating while the DX view is live: carry its current
            // camera back first, so the rebuilt scene (and the WPF
            // viewport, if we end up falling back) keeps the framing the
            // user orbited to.
            if (_dxViewLive) SyncCameraFromDx();

            try
            {
                if (!ShowroomDx.PopulateNeutral(DxViewport, root))
                {
                    ShowWpfView();
                    LogToUi("DirectX renderer unavailable; using the basic viewport.");
                    return;
                }
                SyncCameraToDx();
                Viewport.Visibility = Visibility.Collapsed;
                DxViewport.Visibility = Visibility.Visible;
                _dxViewLive = true;
            }
            catch (Exception ex)
            {
                ShowWpfView();
                LogToUi("DX renderer failed; using the basic viewport: " + ex.Message);
            }
        }

        /// <summary>Tear down the live DX scene (it references the
        /// outgoing model's materials) while KEEPING the DX viewport
        /// fronted -- used when a load is about to replace the scene, so
        /// the WPF fallback (and its axis gizmo) never flashes through.</summary>
        private void ResetDxScene()
        {
            if (_dxViewLive)
            {
                _dxViewLive = false;
                SyncCameraFromDx();
                try { ShowroomDx.Clear(DxViewport); } catch { }
            }
        }

        /// <summary>Front the WPF viewport, tearing down any live DX
        /// scene (and carrying its camera over). Safe any time.</summary>
        private void ShowWpfView()
        {
            if (_dxViewLive)
            {
                _dxViewLive = false;
                SyncCameraFromDx();
                try { ShowroomDx.Clear(DxViewport); } catch { }
            }
            DxViewport.Visibility = Visibility.Collapsed;
            Viewport.Visibility = Visibility.Visible;
        }

        // -----------------------------------------------------------------
        // Camera sync: same framing in both worlds.
        // -----------------------------------------------------------------

        private static Point3D WpfToDx(Point3D p) => new Point3D(p.X, p.Z, -p.Y);
        private static Vector3D WpfToDx(Vector3D v) => new Vector3D(v.X, v.Z, -v.Y);
        private static Point3D DxToWpf(Point3D p) => new Point3D(p.X, -p.Z, p.Y);
        private static Vector3D DxToWpf(Vector3D v) => new Vector3D(v.X, -v.Z, v.Y);

        /// <summary>Mirror the WPF viewport camera onto the DX one.</summary>
        private void SyncCameraToDx()
        {
            if (Viewport?.Camera is not PerspectiveCamera cam) return;
            DxViewport.Camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera
            {
                Position = WpfToDx(cam.Position),
                LookDirection = WpfToDx(cam.LookDirection),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = cam.FieldOfView,
                // Sane depth range for car-scale scenes (see ShowroomDx
                // camera notes: a huge range burns Z precision up close
                // and thin far elements shimmer).
                NearPlaneDistance = 0.2,
                FarPlaneDistance = 300,
            };
            // A camera swap alone doesn't schedule a frame -- without
            // this the new view only appears on the next interaction.
            DxViewport.InvalidateRender();
        }

        /// <summary>Carry the DX camera back to the WPF viewport (after
        /// the user orbited/zoomed in the DX view).</summary>
        private void SyncCameraFromDx()
        {
            if (DxViewport?.Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera dxCam ||
                Viewport?.Camera is not PerspectiveCamera cam) return;
            cam.Position = DxToWpf(dxCam.Position);
            cam.LookDirection = DxToWpf(dxCam.LookDirection);
            cam.UpDirection = new Vector3D(0, 0, 1);
        }
    }
}
