using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace EgoEngineLibrary.Archive.Erp.Model
{
    /// <summary>One node of a model's skeleton: its name, parent and local transform.</summary>
    public sealed class ModelNode
    {
        public int Index { get; init; }
        public int Parent { get; init; }
        public string Name { get; init; } = string.Empty;

        /// <summary>Where the node's 120-byte record starts in the payload it was read from.</summary>
        public int Offset { get; init; }

        public Vector3 Translation { get; init; }
        public Quaternion Rotation { get; init; }
        public Vector3 Scale { get; init; }

        /// <summary>The local transform as a matrix: scale, then rotation, then translation.</summary>
        public Matrix4x4 Local =>
            Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
    }

    /// <summary>
    /// The node graph of a model, from its <c>?render</c> (ModelProxyRes):
    /// u16 node count, u16 palette count, then 120-byte node records from offset
    /// 12 (u32 name offset, i32 parent, f32 translation[3] rotation[4] scale[3],
    /// ...), then the u16 bone palette, then the names. The <c>?model</c>
    /// (GfxModelRes) carries an identical table at a fixed offset, which
    /// <see cref="ModelOffset"/> finds.
    ///
    /// <para>The bone palette is what skinned vertices index: a vertex's bone
    /// number is a palette entry, the palette entry is a node. Helmets of every
    /// make share one nine-bone palette, which is what lets parts move between
    /// them without remapping. Format from RM Designs' swappers.</para>
    /// </summary>
    public sealed class NodeTable
    {
        public IReadOnlyList<ModelNode> Nodes { get; }

        /// <summary>Bone palette: vertex bone index → node index.</summary>
        public IReadOnlyList<int> Palette { get; }

        private NodeTable(IReadOnlyList<ModelNode> nodes, IReadOnlyList<int> palette)
        {
            Nodes = nodes;
            Palette = palette;
        }

        /// <summary>The palette as node names, in order.</summary>
        public IEnumerable<string> PaletteNames => Palette.Select(i => Nodes[i].Name);

        public ModelNode? Find(string name) => Nodes.FirstOrDefault(n => n.Name == name);

        public string ParentName(ModelNode node) => node.Parent >= 0 ? Nodes[node.Parent].Name : string.Empty;

        public static NodeTable Parse(byte[] render)
        {
            int count = BinaryPrimitives.ReadUInt16LittleEndian(render), paletteCount = BinaryPrimitives.ReadUInt16LittleEndian(render.AsSpan(2));
            var names = 12 + count * 120 + paletteCount * 2;
            float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(render.AsSpan(o));

            var nodes = new List<ModelNode>(count);
            for (var i = 0; i < count; i++)
            {
                var b = 12 + i * 120;
                var nameAt = names + (int)BinaryPrimitives.ReadUInt32LittleEndian(render.AsSpan(b));
                var nameEnd = Array.IndexOf(render, (byte)0, nameAt);
                nodes.Add(new ModelNode
                {
                    Index = i,
                    Offset = b,
                    Parent = BinaryPrimitives.ReadInt32LittleEndian(render.AsSpan(b + 4)),
                    Name = Encoding.ASCII.GetString(render, nameAt, nameEnd - nameAt),
                    Translation = new Vector3(F(b + 8), F(b + 12), F(b + 16)),
                    Rotation = new Quaternion(F(b + 20), F(b + 24), F(b + 28), F(b + 32)),
                    Scale = new Vector3(F(b + 36), F(b + 40), F(b + 44)),
                });
            }

            var palette = new int[paletteCount];
            for (var i = 0; i < paletteCount; i++)
                palette[i] = BinaryPrimitives.ReadUInt16LittleEndian(render.AsSpan(12 + count * 120 + i * 2));
            return new NodeTable(nodes, palette);
        }

        /// <summary>
        /// Where this table sits inside the matching <c>?model</c> payload, as an
        /// offset to add to a node's <see cref="ModelNode.Offset"/>. The two copies
        /// must agree; a model whose copy differs is refused rather than half-edited.
        /// </summary>
        public int ModelOffset(byte[] render, byte[] model)
        {
            if (Nodes.Count == 0) return 0;
            var at = model.AsSpan().IndexOf(render.AsSpan(12, 120));
            if (at < 0) throw new InvalidDataException("The model's copy of the node table wasn't found.");
            var delta = at - 12;
            foreach (var n in Nodes)
                if (!render.AsSpan(n.Offset, 120).SequenceEqual(model.AsSpan(n.Offset + delta, 120)))
                    throw new InvalidDataException("The model's copy of the node table differs from the render's.");
            return delta;
        }
    }
}
