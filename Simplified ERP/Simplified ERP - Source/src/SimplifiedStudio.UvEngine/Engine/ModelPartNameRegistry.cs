// ModelPartNameRegistry.cs
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace F1modding3dtoolV1
{
    /// <summary>
    /// Remembers which top-level Model3D (typically a Model3DGroup
    /// wrapper) belongs to which logical part name. This is the
    /// wrapper-level companion to MeshFamilyRegistry, which keys by
    /// MeshGeometry3D and is therefore tied 1:1 to the underlying
    /// geometry instance.
    ///
    /// Used so that multiple wrapper groups can SHARE a single
    /// GeometryModel3D / MeshGeometry3D while still each reporting a
    /// different part name to the assembler. The classic-tyre loader
    /// uses this to emit four wheel positions (wheel_fl/fr/bl/br) that
    /// all draw the same underlying tyre geometry but get placed at
    /// four different world transforms by the body's render-graph.
    ///
    /// MainWindow's GetPartNameForGroup queries this registry FIRST
    /// before falling back to the mesh-level family registry, so a
    /// wheel-wrapper's wheel_fl/fr/bl/br name takes precedence over
    /// whatever family the underlying tyre meshes happened to have
    /// been registered with (typically "wall"/"tread"/"mesh_N").
    /// </summary>
    public static class ModelPartNameRegistry
    {
        private static readonly Dictionary<Model3D, string> _nameByModel =
            new Dictionary<Model3D, string>();

        public static void Register(Model3D model, string partName)
        {
            if (model == null) return;
            if (string.IsNullOrWhiteSpace(partName)) return;
            _nameByModel[model] = partName;
        }

        public static string GetPartName(Model3D model)
        {
            if (model == null) return null;
            return _nameByModel.TryGetValue(model, out var name) ? name : null;
        }

        /// <summary>
        /// Removes all entries. Call before loading a new car to
        /// prevent stale Model3D references from previous loads
        /// accumulating.
        /// </summary>
        public static void Clear()
        {
            _nameByModel.Clear();
        }
    }
}
