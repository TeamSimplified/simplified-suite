using System;
using System.Windows;
using System.Windows.Media.Media3D;

namespace SimplifiedUniversalExporter
{
    /// <summary>Viewport camera controls: default 3/4 front view, reset,
    /// zoom buttons.</summary>
    public partial class MainWindow
    {
        /// <summary>
        /// Inset the coordinate-system gizmo from the viewport edge.
        /// Its position is baked into HelixViewport3D's control template
        /// (top-right, flush), so the template part gets a margin at
        /// runtime -- there is no public padding property for it.
        /// </summary>
        private void PadCoordinateGizmo()
        {
            try
            {
                if (Viewport?.Template?.FindName("PART_CoordinateView", Viewport)
                    is System.Windows.Controls.Viewport3D gizmo)
                    gizmo.Margin = new Thickness(20, 0, 0, 20);
            }
            catch { /* cosmetic only */ }
        }

        /// <summary>
        /// Frame the loaded model in a 3/4 front view: camera front-right
        /// of the car and slightly above, looking down at the centre.
        /// Used as the default view after every load and by the Reset
        /// button. Falls back to ZoomExtents when there's nothing loaded
        /// or the camera isn't the expected perspective type.
        /// </summary>
        private void SetDefaultCameraView()
        {
            var cam = Viewport?.Camera as PerspectiveCamera;
            if (cam == null || _currentModel?.Content is not Model3DGroup content ||
                content.Children.Count == 0)
            {
                Viewport?.ZoomExtents();
                return;
            }

            // World-space bounds: the model content transformed by the
            // upright rotation the viewport applies.
            Rect3D b = content.Bounds;
            if (_currentModel.Transform != null)
                b = _currentModel.Transform.TransformBounds(b);
            if (b.IsEmpty)
            {
                Viewport.ZoomExtents();
                return;
            }

            var center = new Point3D(
                b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            double radius = 0.5 * Math.Sqrt(
                b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ);
            if (radius <= 0) radius = 1;

            // This viewport is Z-up (HelixViewport3D's default
            // ModelUpDirection is (0,0,1); the loader's +90° X rotation
            // stands models upright for exactly that convention).
            //
            // 3/4 front from the car's LEFT side: the length axis (the
            // larger horizontal extent -- cars are longer than wide)
            // keeps the sign that faces the nose, the width axis flips
            // to pick the side. Raised on +Z, looking down at the
            // centre; LookDirection ends exactly at the centre so the
            // model sits centred in the viewport.
            var dir = b.SizeX >= b.SizeY
                ? new Vector3D(1, -1, -0.45)
                : new Vector3D(-1, 1, -0.45);
            dir.Normalize();
            double dist = radius * 2.1;

            cam.Position = center - dir * dist;
            cam.LookDirection = dir * dist;
            cam.UpDirection = new Vector3D(0, 0, 1);

            // Framing nudge: with the camera aimed dead-centre the car
            // reads a touch low-left (perspective + the car's own
            // asymmetric bounds). Slide the camera down-left WITHOUT
            // changing the look direction, which shifts the car up-right
            // in the frame.
            var right = Vector3D.CrossProduct(dir, cam.UpDirection);
            right.Normalize();
            var upScreen = Vector3D.CrossProduct(right, dir);
            upScreen.Normalize();
            cam.Position -= right * (radius * 0.12) + upScreen * (radius * 0.06);
        }

        private void OnResetViewClick(object sender, RoutedEventArgs e)
        {
            // Same framing math regardless of the active renderer: the
            // WPF camera is the source of truth, mirrored onto the DX
            // camera when the DX view is fronting.
            SetDefaultCameraView();
            if (_dxViewLive) SyncCameraToDx();
        }

        private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomBy(0.2);
        private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomBy(-0.25);

        /// <summary>Move the active camera (DX view when live, else the
        /// WPF viewport) along its look direction, keeping the look-at
        /// target fixed so orbiting afterwards still pivots around the
        /// model. Positive fraction zooms in.</summary>
        private void ZoomBy(double fraction)
        {
            // Never step past the target (keep at least 5% of the distance).
            if (fraction > 0.95) fraction = 0.95;

            if (_dxViewLive &&
                DxViewport.Camera is HelixToolkit.Wpf.SharpDX.PerspectiveCamera dxCam)
            {
                Vector3D dxLook = dxCam.LookDirection;
                dxCam.Position += dxLook * fraction;
                dxCam.LookDirection = dxLook * (1 - fraction);
                DxViewport.InvalidateRender();   // schedule the frame now
                return;
            }

            var cam = Viewport?.Camera as PerspectiveCamera;
            if (cam == null) return;
            Vector3D look = cam.LookDirection;
            cam.Position += look * fraction;
            cam.LookDirection = look * (1 - fraction);
        }

    }
}
