using System;
using System.Buffers.Binary;
using System.Numerics;

namespace EgoEngineLibrary.Archive.Erp.Model
{
    /// <summary>
    /// The bounds a GfxModelRes (<c>?model</c>, fragment 0) carries in bytes 16 to
    /// 68: a bounding sphere, a box, and a draw distance. A model given more
    /// geometry than it was made with needs these widened, or the game culls the
    /// new parts as out of view. Format from RM Designs' Helmet Swapper.
    /// </summary>
    public readonly record struct ModelBounds(Vector3 Centre, float Radius, Vector3 Min, Vector3 Max, float DrawDistance)
    {
        public const int Start = 16;
        public const int End = 68;

        public static ModelBounds Read(byte[] model)
        {
            float F(int i) => BinaryPrimitives.ReadSingleLittleEndian(model.AsSpan(Start + i * 4));
            return new ModelBounds(new Vector3(F(0), F(1), F(2)), F(3), new Vector3(F(5), F(6), F(7)), new Vector3(F(8), F(9), F(10)), F(12));
        }

        /// <summary>Write these bounds into a model payload, leaving the two padding floats as they were.</summary>
        public void WriteTo(byte[] model)
        {
            void W(int i, float v) => BinaryPrimitives.WriteSingleLittleEndian(model.AsSpan(Start + i * 4), v);
            W(0, Centre.X); W(1, Centre.Y); W(2, Centre.Z); W(3, Radius);
            W(5, Min.X); W(6, Min.Y); W(7, Min.Z);
            W(8, Max.X); W(9, Max.Y); W(10, Max.Z);
            W(12, DrawDistance);
        }

        /// <summary>Bounds covering both: the box of the two boxes, a sphere around it, the longer draw distance.</summary>
        public ModelBounds Union(ModelBounds other)
        {
            var min = Vector3.Min(Min, other.Min);
            var max = Vector3.Max(Max, other.Max);
            var centre = (min + max) / 2;
            return new ModelBounds(centre, Vector3.Distance(centre, max), min, max, MathF.Max(DrawDistance, other.DrawDistance));
        }
    }
}
