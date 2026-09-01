// MeshFamilyRegistry.cs
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace F1modding3dtoolV1
{
    /// <summary>
    /// Remembers which MeshGeometry3D belongs to which logical family
    /// (e.g. "safetycell", "suspension", "frontwing").
    /// The exporter uses this to group meshes.
    /// </summary>
    public static class MeshFamilyRegistry
    {
        private static readonly Dictionary<MeshGeometry3D, string> _familyByMesh =
            new Dictionary<MeshGeometry3D, string>();

        public static void Register(MeshGeometry3D mesh, string familyName)
        {
            if (mesh == null)
                return;

            if (string.IsNullOrWhiteSpace(familyName))
                familyName = "object";

            _familyByMesh[mesh] = familyName;
        }

        public static string GetFamily(MeshGeometry3D mesh)
        {
            if (mesh == null)
                return null;

            return _familyByMesh.TryGetValue(mesh, out var family)
                ? family
                : null;
        }

        /// <summary>
        /// Removes all entries. Call before loading a new car to prevent
        /// stale mesh references from previous loads accumulating.
        /// </summary>
        public static void Clear()
        {
            _familyByMesh.Clear();
        }
    }
}
