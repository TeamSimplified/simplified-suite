using System.Buffers.Binary;
using EgoEngineLibrary.Archive.Erp;

namespace SimplifiedTrackInstaller.Core.Track;

/// <summary>
/// The menu's texture arrays, each with the track's picture in it.
///
/// An array is a view (<c>.texarray</c>) and a surface (<c>.texarray.image</c>).
/// The surface's header is ten u32s — <c>[2, 264, format, w, h, 1, mips,
/// slices, 0, flags]</c> — and its pixels are the slices one after another; the
/// view repeats the slice count as the u32 at byte 20. A track's slice is its
/// own picture's pixels, verbatim: the track ERP carries each one already in the
/// array's format and size, one level, so nothing is re-encoded.
///
/// Which slice is the track's comes from <c>overrides.uidb</c>: its position in
/// the array's list there. Added at the end, the slice is appended; already
/// there, the slice it has is replaced.
/// </summary>
internal static class TextureArrays
{
    /// <summary>The track's pictures, by role in images.json, and the arrays each goes in.</summary>
    public static readonly (string Picture, string[] Arrays)[] Slots =
    [
        ("flag_circle", ["tracks.flags_circle"]),
        ("flag_rectangle", ["tracks.flags_rectangle"]),
        ("flag_rectangle_small", ["tracks.flags_rectangle_small"]),
        ("preview_background", ["fe_world.tracks_bg", "fe_world.tracks_26_bg"]),
        ("preview_small", ["fe_world.tracks", "fe_world.tracks_26"]),
        ("spline", ["fe_world.tracks_spline"]),
        ("spline_large", ["fe_world.tracks_spline_large"]),
    ];

    public static string ViewId(string array) => "eaid://" + MenuDatabases.ArrayName(array);

    /// <summary>
    /// Put <paramref name="pixels"/> in slice <paramref name="index"/> of the
    /// array held by <paramref name="view"/> and <paramref name="surface"/>.
    /// </summary>
    public static void SetSlice(ErpResource view, ErpResource surface, ErpResource picture, int index, string array)
    {
        var header = surface.Fragments[0].GetDataArray(true);
        var data = surface.Fragments[1].GetDataArray(true);
        var slices = U32(header, 7);
        if (slices == 0 || data.Length % slices != 0)
            throw new InvalidDataException($"The {array} array doesn't divide into its {slices} slices.");

        var sliceSize = data.Length / (int)slices;

        var pictureHeader = picture.Fragments[0].GetDataArray(true);
        var pixels = picture.Fragments[1].GetDataArray(true);
        if (U32(pictureHeader, 2) != U32(header, 2) || U32(pictureHeader, 3) != U32(header, 3)
            || U32(pictureHeader, 4) != U32(header, 4) || pixels.Length != sliceSize)
        {
            throw new InvalidDataException(
                $"{picture.Identifier} is {U32(pictureHeader, 3)}x{U32(pictureHeader, 4)} in format {U32(pictureHeader, 2)}, " +
                $"{pixels.Length:N0} bytes; the {array} array wants {U32(header, 3)}x{U32(header, 4)} in format " +
                $"{U32(header, 2)}, {sliceSize:N0} bytes.");
        }

        if (index < slices)
        {
            pixels.CopyTo(data, index * sliceSize);
            surface.Fragments[1].SetData(data);
        }
        else if (index == slices)
        {
            var grown = new byte[data.Length + sliceSize];
            data.CopyTo(grown, 0);
            pixels.CopyTo(grown, data.Length);
            surface.Fragments[1].SetData(grown);

            SetU32(header, 7, slices + 1);
            surface.Fragments[0].SetData(header);

            var viewData = view.Fragments[0].GetDataArray(true);
            BinaryPrimitives.WriteUInt32LittleEndian(viewData.AsSpan(20), slices + 1);
            view.Fragments[0].SetData(viewData);
            view.UpdateHash();
        }
        else
        {
            throw new InvalidDataException(
                $"overrides.uidb puts the track at {index} in {array}, which has only {slices} slices.");
        }

        surface.UpdateHash();
    }

    private static uint U32(byte[] header, int i) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(i * 4));
    private static void SetU32(byte[] header, int i, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(i * 4), value);
}
