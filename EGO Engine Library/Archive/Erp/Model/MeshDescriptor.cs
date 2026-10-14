using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EgoEngineLibrary.Archive.Erp.Model
{
    /// <summary>
    /// One submesh of a <see cref="MeshDescriptor"/>: a slot (the material it is
    /// drawn with), a range of the shared index buffer, and the vertex streams it
    /// reads. Backed by the 88-byte record, so whatever this doesn't name is kept.
    /// </summary>
    public sealed class MeshSubmesh
    {
        internal byte[] Record { get; }

        internal MeshSubmesh(byte[] record, string slot)
        {
            Record = record;
            Slot = slot;
        }

        /// <summary>The material slot, e.g. <c>helmet</c>, <c>visor_attachments</c>, <c>steering wheel</c>.</summary>
        public string Slot { get; set; }

        public int StreamCount => (int)U32(0);

        /// <summary>First index of this submesh in the shared index buffer.</summary>
        public int IndexOffset { get => (int)U32(12); set => W32(12, (uint)value); }

        /// <summary>How many indices (three per triangle).</summary>
        public int IndexCount { get => (int)U32(16); set => W32(16, (uint)value); }

        /// <summary>Added to every index: where this submesh's vertices start in its streams.</summary>
        public int BaseVertex { get => (int)U32(20); set => W32(20, (uint)value); }

        /// <summary>Bounding sphere: centre and radius, in model space.</summary>
        public (float X, float Y, float Z, float Radius) BoundingSphere
        {
            get => (F32(24), F32(28), F32(32), F32(36));
            set { WF(24, value.X); WF(28, value.Y); WF(32, value.Z); WF(36, value.Radius); }
        }

        /// <summary>Which of the descriptor's <see cref="MeshDescriptor.Streams"/> this submesh reads, in order.</summary>
        public int[] StreamIndices => Enumerable.Range(0, Math.Min(StreamCount, 6)).Select(k => (int)U32(56 + k * 4)).ToArray();

        internal void SetStreamIndex(int k, int stream) => W32(56 + k * 4, (uint)stream);

        private uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(Record.AsSpan(o));
        private float F32(int o) => BinaryPrimitives.ReadSingleLittleEndian(Record.AsSpan(o));
        private void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Record.AsSpan(o), v);
        private void WF(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(Record.AsSpan(o), v);
    }

    /// <summary>
    /// A GfxMeshRes descriptor (fragment 0 of a <c>?lod=N&amp;mesh</c> resource):
    /// the submeshes of one detail level, the vertex streams and index buffer they
    /// share, and the node each is attached to.
    ///
    /// <para>Layout - a 76-byte header of 19 u32s, offsets in it counted from byte 76:
    /// [0] submeshes, [1] streams, [2] 1, [3] distinct slot names (submeshes drawn
    /// with the same material share one), [4] submeshes again, [5] stream-name
    /// table, [6] index-buffer name, [7] slot-name table, [8] per-submesh node table
    /// (8 bytes: node, submesh), [9] streams again, [10] per-stream formats (20
    /// bytes), [11] per-stream slots (8 bytes: 1, stream), [12] node-group count,
    /// [13] node groups (92 bytes: 4x4 matrix, 0, node, 0, first submesh, submesh
    /// count, 0, 0), [14] range count, [15] ranges (16 bytes: opaque flag, submesh,
    /// index offset, index count), [16..18] (18 = LOD). Characters and helmets
    /// have a node group and a range per submesh; cars have none at their top
    /// detail level and many below it.
    /// The 88-byte submesh records follow the header directly.</para>
    ///
    /// <para>Worked out by RM Designs for the Steering Wheel and Helmet Swappers, both
    /// confirmed in game, and brought here with their permission so the suite reads
    /// meshes in one place. <see cref="Write"/> lays the descriptor out again from
    /// what it holds; <see cref="Merge"/> appends one descriptor's submeshes to
    /// another's. An all-zero descriptor is a detail level the model leaves empty.</para>
    /// </summary>
    public sealed class MeshDescriptor
    {
        private const int HeaderSize = 76;

        public uint[] Header { get; private set; } = new uint[19];
        public List<MeshSubmesh> Submeshes { get; private set; } = new();

        /// <summary>Node groups, 92 bytes each.</summary>
        public List<byte[]> Groups { get; private set; } = new();

        /// <summary>Index ranges, 16 bytes each: one per submesh on characters, any number on cars.</summary>
        public List<byte[]> Ranges { get; private set; } = new();

        /// <summary>Vertex buffer identifiers.</summary>
        public List<string> Streams { get; private set; } = new();

        public string IndexBuffer { get; set; } = string.Empty;

        /// <summary>Per submesh: the node it hangs from, and its own index.</summary>
        public List<(uint Node, uint Submesh)> Nodes { get; private set; } = new();

        /// <summary>Per-stream format records, 20 bytes each.</summary>
        public List<byte[]> Formats { get; private set; } = new();

        public List<(uint A, uint Stream)> StreamSlots { get; private set; } = new();

        /// <summary>True for a detail level the model leaves empty: an all-zero descriptor.</summary>
        public bool IsEmpty { get; private set; }

        /// <summary>The submesh drawn with <paramref name="slot"/>, or null.</summary>
        public MeshSubmesh? Find(string slot) => Submeshes.FirstOrDefault(s => s.Slot == slot);

        /// <summary>The stream identifiers a submesh reads, in its order.</summary>
        public string[] StreamsOf(MeshSubmesh submesh) => submesh.StreamIndices.Select(i => Streams[i]).ToArray();

        public static MeshDescriptor Parse(byte[] data)
        {
            var m = new MeshDescriptor();
            if (data.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                m.IsEmpty = true;
                return m;
            }

            for (var i = 0; i < 19; i++) m.Header[i] = U(data, i * 4);
            var h = m.Header;
            int nsub = (int)h[0], nstream = (int)h[1];
            if (h[3] > nsub || h[4] != nsub || h[9] != nstream || h[13] != nsub * 88)
                throw new InvalidDataException("This mesh descriptor isn't laid out the way this reader knows.");

            var records = new List<byte[]>(nsub);
            for (var i = 0; i < nsub; i++) records.Add(data[(HeaderSize + i * 88)..(HeaderSize + i * 88 + 88)]);
            for (var i = 0; i < h[12]; i++) m.Groups.Add(Slice(data, HeaderSize + (int)h[13] + i * 92, 92));
            for (var i = 0; i < h[14]; i++) m.Ranges.Add(Slice(data, HeaderSize + (int)h[15] + i * 16, 16));

            var p = HeaderSize + (int)h[5];
            for (var i = 0; i < nstream; i++) m.Streams.Add(CString(data, ref p));
            if (p != HeaderSize + h[6]) throw new InvalidDataException("This mesh descriptor's stream names don't end where it says.");
            m.IndexBuffer = CString(data, ref p);

            for (var i = 0; i < nsub; i++)
            {
                var q = HeaderSize + (int)h[7] + (int)U(records[i], 48);
                m.Submeshes.Add(new MeshSubmesh(records[i], CString(data, ref q)));
            }
            for (var i = 0; i < nsub; i++)
                m.Nodes.Add((U(data, HeaderSize + (int)h[8] + i * 8), U(data, HeaderSize + (int)h[8] + i * 8 + 4)));
            for (var i = 0; i < nstream; i++) m.Formats.Add(Slice(data, HeaderSize + (int)h[10] + i * 20, 20));
            for (var i = 0; i < nstream; i++)
                m.StreamSlots.Add((U(data, HeaderSize + (int)h[11] + i * 8), U(data, HeaderSize + (int)h[11] + i * 8 + 4)));
            if (HeaderSize + h[11] + nstream * 8 != data.Length)
                throw new InvalidDataException("This mesh descriptor is not the size its tables add up to.");
            return m;
        }

        /// <summary>The descriptor laid out again from what it holds.</summary>
        public byte[] Write()
        {
            if (IsEmpty) throw new InvalidOperationException("An empty detail level has nothing to write.");

            int nsub = Submeshes.Count, nstream = Streams.Count;
            var names = new List<byte>();
            var nameOff = new int[nsub];
            var named = new Dictionary<string, int>();
            for (var i = 0; i < nsub; i++)
            {
                if (named.TryGetValue(Submeshes[i].Slot, out nameOff[i])) continue;
                nameOff[i] = named[Submeshes[i].Slot] = names.Count;
                names.AddRange(Encoding.ASCII.GetBytes(Submeshes[i].Slot));
                names.Add(0);
            }
            var streamBytes = new List<byte>();
            foreach (var s in Streams) { streamBytes.AddRange(Encoding.ASCII.GetBytes(s)); streamBytes.Add(0); }
            var ib = Encoding.ASCII.GetBytes(IndexBuffer + "\0");

            int groups = nsub * 88, ranges = groups + Groups.Count * 92, streams = ranges + Ranges.Count * 16;
            int ibOff = streams + streamBytes.Count, slots = ibOff + ib.Length, nodes = slots + names.Count;
            int formats = nodes + nsub * 8, sslots = formats + nstream * 20, end = sslots + nstream * 8;
            var d = new byte[HeaderSize + end];

            var h = (uint[])Header.Clone();
            h[0] = h[4] = (uint)nsub;
            h[14] = (uint)Ranges.Count;
            h[3] = (uint)named.Count;
            h[1] = h[9] = (uint)nstream;
            h[5] = (uint)streams; h[6] = (uint)ibOff; h[7] = (uint)slots; h[8] = (uint)nodes;
            h[10] = (uint)formats; h[11] = (uint)sslots;
            h[12] = (uint)Groups.Count; h[13] = (uint)groups; h[15] = (uint)ranges;
            for (var i = 0; i < 19; i++) W(d, i * 4, h[i]);

            for (var i = 0; i < nsub; i++)
            {
                var r = (byte[])Submeshes[i].Record.Clone();
                W(r, 40, (uint)(i * 8));
                W(r, 48, (uint)nameOff[i]);
                r.CopyTo(d, HeaderSize + i * 88);
            }
            for (var i = 0; i < Groups.Count; i++) Groups[i].CopyTo(d, HeaderSize + groups + i * 92);
            for (var i = 0; i < Ranges.Count; i++) Ranges[i].CopyTo(d, HeaderSize + ranges + i * 16);
            streamBytes.ToArray().CopyTo(d, HeaderSize + streams);
            ib.CopyTo(d, HeaderSize + ibOff);
            names.ToArray().CopyTo(d, HeaderSize + slots);
            for (var i = 0; i < nsub; i++)
            {
                W(d, HeaderSize + nodes + i * 8, Nodes[i].Node);
                W(d, HeaderSize + nodes + i * 8 + 4, Nodes[i].Submesh);
            }
            for (var i = 0; i < nstream; i++) Formats[i].CopyTo(d, HeaderSize + formats + i * 20);
            for (var i = 0; i < nstream; i++)
            {
                W(d, HeaderSize + sslots + i * 8, StreamSlots[i].A);
                W(d, HeaderSize + sslots + i * 8 + 4, StreamSlots[i].Stream);
            }
            return d;
        }

        /// <summary>
        /// <paramref name="a"/> followed by <paramref name="b"/>'s submeshes: b's
        /// streams are appended, b's indices follow a's in one index buffer
        /// (<paramref name="aIndexCount"/> = a's index buffer length, in indices),
        /// and b's node groups hang from <paramref name="node"/>, a node of a's model.
        /// </summary>
        public static MeshDescriptor Merge(MeshDescriptor a, MeshDescriptor b, int aIndexCount, uint node)
        {
            var m = new MeshDescriptor
            {
                Header = (uint[])a.Header.Clone(),
                Submeshes = a.Submeshes.Select(s => new MeshSubmesh((byte[])s.Record.Clone(), s.Slot)).ToList(),
                Groups = a.Groups.Select(g => (byte[])g.Clone()).ToList(),
                Ranges = a.Ranges.Select(r => (byte[])r.Clone()).ToList(),
                Streams = [.. a.Streams, .. b.Streams],
                IndexBuffer = a.IndexBuffer,
                Nodes = [.. a.Nodes],
                Formats = [.. a.Formats, .. b.Formats],
                StreamSlots = [.. a.StreamSlots, .. b.StreamSlots.Select(s => (s.A, s.Stream + (uint)a.Streams.Count))],
            };

            var sub0 = a.Submeshes.Count;
            for (var i = 0; i < b.Submeshes.Count; i++)
            {
                var s = new MeshSubmesh((byte[])b.Submeshes[i].Record.Clone(), b.Submeshes[i].Slot);
                s.IndexOffset += aIndexCount;
                var streams = s.StreamIndices;
                for (var k = 0; k < streams.Length; k++) s.SetStreamIndex(k, streams[k] + a.Streams.Count);
                m.Submeshes.Add(s);

                m.Nodes.Add((node, (uint)(sub0 + i)));
            }
            foreach (var r in b.Ranges)
            {
                var range = (byte[])r.Clone();
                W(range, 4, U(range, 4) + (uint)sub0);
                W(range, 8, U(range, 8) + (uint)aIndexCount);
                m.Ranges.Add(range);
            }
            foreach (var g in b.Groups)
            {
                var c = (byte[])g.Clone();
                W(c, 68, node);
                W(c, 76, U(c, 76) + (uint)sub0);
                m.Groups.Add(c);
            }
            return m;
        }

        private static byte[] Slice(byte[] d, int offset, int length) => d[offset..(offset + length)];
        private static uint U(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
        private static void W(byte[] d, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);

        private static string CString(byte[] d, ref int p)
        {
            var e = Array.IndexOf(d, (byte)0, p);
            var s = Encoding.ASCII.GetString(d, p, e - p);
            p = e + 1;
            return s;
        }
    }
}
