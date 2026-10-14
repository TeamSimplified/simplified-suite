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
    /// <summary>Assembly: parts -> world positions via the IDF ?render node graph.</summary>
    public partial class MainWindow
    {

        // =====================================================================
        // ASSEMBLY (parts -> world positions via the IDF ?render node graph)
        // =====================================================================

        /// <summary>
        /// Per-child record of which IDF folder the child came from. We need
        /// this so the assembler can parse the RIGHT ?render graph for each
        /// part -- the universal flow merges children from several IDFs into
        /// one Model3DGroup, so a single primary-IDF lookup wouldn't cover
        /// children from attachment / livery IDFs.
        /// </summary>
        private readonly Dictionary<Model3D, string> _childIdfFolder = new Dictionary<Model3D, string>();

        private void OnAssembleToggle(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null || !(_currentModel.Content is Model3DGroup root))
                return;
            bool assemble = AssembleCheckBox?.IsChecked == true;
            if (!assemble)
            {
                ClearAssemblyTransforms(root);
                StatusText.Text = "Assemble: OFF (raw loader output).";
                Viewport.ZoomExtents();
                RefreshDxView();
                SaveSettings();
                return;
            }
            ApplyAssemblyTransforms(root);
            Viewport.ZoomExtents();
            RefreshDxView();
            SaveSettings();
        }

        private static void ClearAssemblyTransforms(Model3DGroup root)
        {
            if (root == null) return;
            foreach (var child in root.Children)
            {
                if (child != null)
                    child.Transform = Transform3D.Identity;
            }
        }

        /// <summary>
        /// For each top-level child of <paramref name="root"/>, look up the
        /// IDF folder it came from, parse that IDF's ?render node graph,
        /// resolve the child's part-name to a node, and apply the cumulative
        /// world transform. Children whose part-name doesn't resolve to a
        /// node stay at identity (raw loader position). Caches per-folder
        /// node graphs to avoid re-parsing the same file once per child.
        /// </summary>
        private void ApplyAssemblyTransforms(Model3DGroup root)
        {
            if (root == null) return;

            var graphCache = new Dictionary<string, (List<F1FbxExporter.NodeInfo> nodes, Dictionary<string, int> nameToIndex)>(
                StringComparer.OrdinalIgnoreCase);
            int matched = 0, unmatched = 0;

            foreach (var child in root.Children)
            {
                if (child == null) continue;

                string partName = GetPartNameForGroup(child);
                if (string.IsNullOrWhiteSpace(partName))
                {
                    child.Transform = Transform3D.Identity;
                    unmatched++;
                    continue;
                }

                string folder = ResolveIdfFolderForChild(child);
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    child.Transform = Transform3D.Identity;
                    unmatched++;
                    continue;
                }

                if (!graphCache.TryGetValue(folder, out var graph))
                {
                    List<F1FbxExporter.NodeInfo> nodes;
                    try { nodes = F1FbxExporter.TryParseNodeGraph(folder); }
                    catch { nodes = new List<F1FbxExporter.NodeInfo>(); }
                    var n2i = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (nodes != null)
                    {
                        for (int i = 0; i < nodes.Count; i++)
                        {
                            string nm = nodes[i].Name;
                            if (string.IsNullOrWhiteSpace(nm)) continue;
                            if (!n2i.ContainsKey(nm)) n2i[nm] = i;
                        }
                    }
                    graph = (nodes, n2i);
                    graphCache[folder] = graph;
                }

                if (graph.nodes == null || graph.nodes.Count == 0)
                {
                    child.Transform = Transform3D.Identity;
                    unmatched++;
                    continue;
                }

                int nodeIdx = ResolveNodeIndex(partName, graph.nameToIndex);
                if (nodeIdx < 0)
                {
                    child.Transform = Transform3D.Identity;
                    unmatched++;
                    continue;
                }

                child.Transform = new MatrixTransform3D(ComputeWorldTransform(nodeIdx, graph.nodes));
                matched++;
            }

            StatusText.Text = $"Assemble: ON ({matched} part(s) placed, {unmatched} unmatched).";
        }

        /// <summary>
        /// Find the IDF folder a top-level child belongs to. Uses the
        /// per-child map populated during load; falls back to the primary
        /// IDF folder when the child wasn't recorded (e.g. tyre clones or
        /// any part the loader synthesised post-load).
        /// </summary>
        private string ResolveIdfFolderForChild(Model3D child)
        {
            if (child != null && _childIdfFolder.TryGetValue(child, out var f) && !string.IsNullOrWhiteSpace(f))
                return f;
            return _primaryIdfFolder;
        }

        /// <summary>
        /// Returns the part-name registered for <paramref name="model"/>.
        /// Wrapper-level registry wins (used by tyre wheel clones and any
        /// other wrapper-tagged part); falls through to walking descendant
        /// MeshGeometry3Ds and asking MeshFamilyRegistry.
        /// </summary>
        private static string GetPartNameForGroup(Model3D model)
        {
            if (model == null) return null;
            string wrapperName = ModelPartNameRegistry.GetPartName(model);
            if (!string.IsNullOrWhiteSpace(wrapperName)) return wrapperName;

            var stack = new Stack<Model3D>();
            stack.Push(model);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == null) continue;
                if (cur is Model3DGroup grp)
                {
                    foreach (var ch in grp.Children) stack.Push(ch);
                    continue;
                }
                if (cur is GeometryModel3D gm && gm.Geometry is MeshGeometry3D mg)
                {
                    string family = MeshFamilyRegistry.GetFamily(mg);
                    if (!string.IsNullOrWhiteSpace(family)) return family;
                }
            }
            return null;
        }

        /// <summary>
        /// Map a loader part-name to a node index. Exact case-insensitive
        /// match first; if that misses, a single-candidate prefix match in
        /// either direction (covers ERP EAID truncation).
        /// </summary>
        private static int ResolveNodeIndex(string partName, Dictionary<string, int> nameToIndex)
        {
            if (string.IsNullOrWhiteSpace(partName) || nameToIndex == null) return -1;
            if (nameToIndex.TryGetValue(partName, out int exact)) return exact;
            if (partName.Length < 3) return -1;
            int unique = -1;
            foreach (var kv in nameToIndex)
            {
                bool isPrefixMatch =
                    kv.Key.StartsWith(partName, StringComparison.OrdinalIgnoreCase) ||
                    partName.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase);
                if (!isPrefixMatch) continue;
                if (unique == -1) unique = kv.Value;
                else return -1; // ambiguous
            }
            return unique;
        }

        /// <summary>
        /// Build a node's local TRS matrix in WPF row-vector convention.
        /// Order: scale, then quaternion rotation, then translation -- the
        /// same order F1FbxExporter.BuildTransform uses on the Assimp side.
        /// </summary>
        private static Matrix3D LocalTrsMatrix(F1FbxExporter.NodeInfo n)
        {
            if (n == null) return Matrix3D.Identity;
            Matrix3D m = Matrix3D.Identity;
            m.Scale(new Vector3D(n.Sx, n.Sy, n.Sz));
            var quat = new System.Windows.Media.Media3D.Quaternion(n.Qx, n.Qy, n.Qz, n.Qw);
            var rotMatrix = new RotateTransform3D(new QuaternionRotation3D(quat)).Value;
            m = m * rotMatrix;
            m.Translate(new Vector3D(n.Tx, n.Ty, n.Tz));
            return m;
        }

        private static Matrix3D ComputeWorldTransform(int nodeIdx, List<F1FbxExporter.NodeInfo> nodes)
        {
            if (nodes == null || nodeIdx < 0 || nodeIdx >= nodes.Count) return Matrix3D.Identity;
            Matrix3D world = LocalTrsMatrix(nodes[nodeIdx]);
            int curIdx = nodes[nodeIdx].ParentIndex;
            int safety = 0;
            while (curIdx >= 0 && curIdx < nodes.Count && safety++ < 256)
            {
                world = world * LocalTrsMatrix(nodes[curIdx]);
                curIdx = nodes[curIdx].ParentIndex;
            }
            return world;
        }
    }
}
