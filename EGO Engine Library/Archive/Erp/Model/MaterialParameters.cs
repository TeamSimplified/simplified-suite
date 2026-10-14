using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace EgoEngineLibrary.Archive.Erp.Model
{
    /// <summary>
    /// The named parameters of a material (GfxMaterialRes, fragment 0): u32 count,
    /// then from byte 32 one 36-byte record per parameter (u32 name offset into
    /// the name table, u32 type at +8 - 3 float, 6 int - and u32 value offset at
    /// +28 into the value block), the value block after the records, and the
    /// <c>me_*</c> names after that. Enough to read or set a value by name -
    /// <c>me_alpha</c>, <c>me_roughness</c>, <c>me_indexOfRefraction</c> -
    /// without moving anything. Format from RM Designs' Helmet Swapper.
    /// </summary>
    public static class MaterialParameters
    {
        /// <summary>Where a parameter's value is, or -1 when the material hasn't got it.</summary>
        public static int ValueOffset(byte[] material, string name)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(material);
            var values = 32 + count * 36;
            var names = material.AsSpan(values).IndexOf("me_"u8);
            if (count <= 0 || names < 0) return -1;
            names += values;

            for (var k = 0; k < count; k++)
            {
                var record = 32 + k * 36;
                var at = names + BinaryPrimitives.ReadInt32LittleEndian(material.AsSpan(record));
                if (at < 0 || at >= material.Length) continue;
                var end = Array.IndexOf(material, (byte)0, at);
                if (end > at && Encoding.ASCII.GetString(material, at, end - at) == name)
                    return values + BinaryPrimitives.ReadInt32LittleEndian(material.AsSpan(record + 28));
            }
            return -1;
        }

        public static bool Has(byte[] material, string name) => ValueOffset(material, name) >= 0;

        public static float GetFloat(byte[] material, string name) =>
            BinaryPrimitives.ReadSingleLittleEndian(material.AsSpan(Require(material, name)));

        /// <summary>Set a float parameter in place; the material keeps its size and layout.</summary>
        public static void SetFloat(byte[] material, string name, float value) =>
            BinaryPrimitives.WriteSingleLittleEndian(material.AsSpan(Require(material, name)), value);

        private static int Require(byte[] material, string name) =>
            ValueOffset(material, name) is var at and >= 0 ? at : throw new InvalidDataException($"The material has no {name}.");
    }
}
