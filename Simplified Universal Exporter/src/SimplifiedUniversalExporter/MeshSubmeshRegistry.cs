using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace SimplifiedUniversalExporter
{
    public static class MeshSubmeshRegistry
    {
        // Slot name the game binds this submesh under. Examples: "body",
        // "carbon_tile_1", "carbon_tile_2", "generic", "detail", "number",
        // "mirror_left", "rear light". The value is exactly what the IDF
        // descriptor labels the submesh; no translation or casing changes.
        private static readonly Dictionary<MeshGeometry3D, string> _submeshNameByMesh =
            new Dictionary<MeshGeometry3D, string>();

        // Original descriptor EAID fragment this submesh came from. Used by
        // the exporter to recover part-family names when MeshFamilyRegistry
        // can't resolve them (e.g. ERP EAID truncation).
        private static readonly Dictionary<MeshGeometry3D, string> _descriptorHintByMesh =
            new Dictionary<MeshGeometry3D, string>();

        // Full material EAID the game engine binds to this slot for this
        // submesh. Format is of the form
        //   eaid://<package>/teams/<team>/materials/<material>.material
        // The Classic / Legacy / 2021+ loaders each resolve this during
        // texture-pipeline hookup but didn't previously retain it per-mesh;
        // storing it here lets the glTF exporter emit material entries
        // named after the game's actual materials (e.g. "mclaren_paint",
        // "mclaren_carbon") rather than a generic "default" placeholder.
        private static readonly Dictionary<MeshGeometry3D, string> _materialEaidByMesh =
            new Dictionary<MeshGeometry3D, string>();

        // Diffuse-texture EAID bound to this submesh's material
        // (me_albedoMap / me_diffuseMap). Format:
        //   eaid://<package>/{shared/generics|teams/<team>}/textures/<name>.tif
        // Populated by the Classic loader after MaterialSlotResolver picks
        // a texture for the slot, so the glTF exporter can embed the
        // decoded pixel data into the output .gltf as a base64 PNG data
        // URI -- giving the carbon / paint / generic materials their
        // actual textures in Blender / Maya rather than the flat gray
        // fallback they had before.
        private static readonly Dictionary<MeshGeometry3D, string> _textureEaidByMesh =
            new Dictionary<MeshGeometry3D, string>();

        /// <summary>
        /// Legacy two-arg registration. Callers that don't have a material
        /// EAID available (e.g. textureless fallback paths) can keep using
        /// this and the exporter will still see the slot name.
        /// </summary>
        public static void Register(MeshGeometry3D mesh, string submeshName, string descriptorHint)
        {
            if (mesh == null)
                return;

            if (!string.IsNullOrWhiteSpace(submeshName))
                _submeshNameByMesh[mesh] = submeshName;

            if (!string.IsNullOrWhiteSpace(descriptorHint))
                _descriptorHintByMesh[mesh] = descriptorHint;
        }

        /// <summary>
        /// Full three-arg registration. Use this from the loaders whenever
        /// a material EAID has already been resolved for the slot; the
        /// exporter will then emit a glTF Material named after the
        /// EAID's final segment (e.g. "mclaren_paint" from an EAID that
        /// ends in ".../materials/mclaren_paint.material"), combined with
        /// the slot name so the modder can tell which binding point the
        /// material is attached to.
        /// </summary>
        public static void Register(MeshGeometry3D mesh, string submeshName, string descriptorHint, string materialEaid)
        {
            Register(mesh, submeshName, descriptorHint);

            if (mesh != null && !string.IsNullOrWhiteSpace(materialEaid))
                _materialEaidByMesh[mesh] = materialEaid;
        }

        public static string GetSubmeshName(MeshGeometry3D mesh)
        {
            if (mesh == null)
                return null;
            return _submeshNameByMesh.TryGetValue(mesh, out var n) ? n : null;
        }

        public static string GetDescriptorHint(MeshGeometry3D mesh)
        {
            if (mesh == null)
                return null;
            return _descriptorHintByMesh.TryGetValue(mesh, out var h) ? h : null;
        }

        /// <summary>
        /// Returns the material EAID previously stored with <see cref="Register(MeshGeometry3D, string, string, string)"/>,
        /// or null if either the mesh isn't registered or the caller used
        /// the two-arg overload. The exporter falls back to the slot name
        /// when this returns null.
        /// </summary>
        public static string GetMaterialEaid(MeshGeometry3D mesh)
        {
            if (mesh == null)
                return null;
            return _materialEaidByMesh.TryGetValue(mesh, out var m) ? m : null;
        }

        /// <summary>
        /// Associates a resolved diffuse-texture EAID with a mesh. Called
        /// by the Classic loader right after MaterialSlotResolver reports
        /// which texture was chosen for the submesh's me_albedoMap /
        /// me_diffuseMap binding. Safe to call repeatedly; the last value
        /// wins, which matches the usual "one dominant diffuse per slot"
        /// assumption the loaders already make.
        /// </summary>
        public static void SetTextureEaid(MeshGeometry3D mesh, string textureEaid)
        {
            if (mesh == null) return;
            if (string.IsNullOrWhiteSpace(textureEaid)) return;
            _textureEaidByMesh[mesh] = textureEaid;
        }

        /// <summary>
        /// Returns the diffuse texture EAID previously stored via
        /// <see cref="SetTextureEaid"/>, or null if none was recorded.
        /// The exporter uses this EAID to pull raw BGRA pixels out of the
        /// shared TextureCache and embed a PNG data URI in the .gltf.
        /// </summary>
        public static string GetTextureEaid(MeshGeometry3D mesh)
        {
            if (mesh == null) return null;
            return _textureEaidByMesh.TryGetValue(mesh, out var t) ? t : null;
        }

        // RAW decal-atlas UVs for overlay meshes, exactly as decoded from
        // the vertex stream (unclamped, no preview sentinels). The FBX
        // exporter's decal sidecar MUST use these instead of the overlay
        // mesh's TextureCoordinates: the preview pipeline clamps those
        // into [0,1] and appends two sentinel vertices to pin WPF's
        // brush-mapping bbox, which both corrupts the authentic values
        // and breaks the exporter's vertex-count pairing with the base.
        private static readonly Dictionary<MeshGeometry3D, System.Windows.Media.PointCollection> _rawDecalUvByMesh =
            new Dictionary<MeshGeometry3D, System.Windows.Media.PointCollection>();

        public static void SetRawDecalUv(MeshGeometry3D mesh, System.Windows.Media.PointCollection uv)
        {
            if (mesh == null || uv == null) return;
            _rawDecalUvByMesh[mesh] = uv;
        }

        public static System.Windows.Media.PointCollection GetRawDecalUv(MeshGeometry3D mesh)
        {
            if (mesh == null) return null;
            return _rawDecalUvByMesh.TryGetValue(mesh, out var uv) ? uv : null;
        }

        // UV-VIEW decal layout, keyed off the OVERLAY mesh: the base
        // submesh's FULL pair-2 UVs with the base's FULL triangle list.
        // The overlay mesh itself only keeps the triangles the
        // per-triangle content filter accepted, so drawing its wireframe
        // hugs the decal pixels; the AUTHORED layout (box panels etc.)
        // needs every base triangle. Stored separately from RawDecalUv
        // (whose dropped-face verts are parked for the FBX sidecar).
        private static readonly Dictionary<MeshGeometry3D,
            (System.Windows.Media.PointCollection uvs, System.Windows.Media.Int32Collection indices)>
            _uvViewDecalByMesh = new Dictionary<MeshGeometry3D,
                (System.Windows.Media.PointCollection, System.Windows.Media.Int32Collection)>();

        public static void SetUvViewDecal(MeshGeometry3D mesh,
            System.Windows.Media.PointCollection uvs,
            System.Windows.Media.Int32Collection indices)
        {
            if (mesh == null || uvs == null || indices == null) return;
            _uvViewDecalByMesh[mesh] = (uvs, indices);
        }

        public static bool TryGetUvViewDecal(MeshGeometry3D mesh,
            out System.Windows.Media.PointCollection uvs,
            out System.Windows.Media.Int32Collection indices)
        {
            uvs = null; indices = null;
            if (mesh == null || !_uvViewDecalByMesh.TryGetValue(mesh, out var t)) return false;
            uvs = t.uvs; indices = t.indices;
            return true;
        }

        /// <summary>
        /// Removes all entries. Call before loading a new car to prevent
        /// stale MeshGeometry3D references accumulating across loads.
        /// </summary>
        public static void Clear()
        {
            _submeshNameByMesh.Clear();
            _descriptorHintByMesh.Clear();
            _materialEaidByMesh.Clear();
            _textureEaidByMesh.Clear();
            _rawDecalUvByMesh.Clear();
            _uvViewDecalByMesh.Clear();
        }
    }
}
