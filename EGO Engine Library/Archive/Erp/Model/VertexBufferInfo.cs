using System;
using System.Buffers.Binary;

namespace EgoEngineLibrary.Archive.Erp.Model
{
    /// <summary>
    /// The header of a GfxBufferRes (fragment 0, 16 bytes): what kind of buffer,
    /// how wide one element is, how many there are, and the element format.
    /// Fragment 1 holds the elements themselves, <see cref="Stride"/> bytes each.
    /// </summary>
    public readonly record struct VertexBufferInfo(uint Type, int Stride, int Count, uint Format)
    {
        /// <summary>A vertex buffer.</summary>
        public const uint VertexBuffer = 4361;

        /// <summary>An index buffer.</summary>
        public const uint IndexBuffer = 4362;

        /// <summary>Positions: three f32.</summary>
        public const uint FormatPosition = 0x27;

        /// <summary>Four f16 - normal, tangent or UV.</summary>
        public const uint FormatHalf4 = 0x23;

        /// <summary>Bone indices; byte 0 is the bone palette index (cars).</summary>
        public const uint FormatBones = 0x21;

        /// <summary>Two f16 - a UV set (characters, helmets: the main UV; car carbon slots: a second one).</summary>
        public const uint FormatHalf2 = 0x1d;

        /// <summary>16-bit indices.</summary>
        public const uint FormatIndex16 = 0x13;

        /// <summary>Skinning on characters and helmets: bone indices.</summary>
        public const uint FormatSkinIndices = 0x0d;

        /// <summary>Skinning on characters and helmets: bone weights.</summary>
        public const uint FormatSkinWeights = 0x0e;

        public static VertexBufferInfo Parse(ReadOnlySpan<byte> header) => new(
            BinaryPrimitives.ReadUInt32LittleEndian(header),
            BinaryPrimitives.ReadInt32LittleEndian(header[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[12..]));

        /// <summary>The header with a new element count, for a buffer that has grown.</summary>
        public static byte[] WithCount(ReadOnlySpan<byte> header, int count)
        {
            var copy = header.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(8), count);
            return copy;
        }
    }
}
