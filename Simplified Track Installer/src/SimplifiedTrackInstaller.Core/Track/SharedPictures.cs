using System.Text;
using EgoEngineLibrary.Archive.Erp;

namespace SimplifiedTrackInstaller.Core.Track;

/// <summary>
/// The pictures the menus stream in rather than keep in an array — the three
/// preview scenes and the session picture — copied to the ids the editor gives
/// them, under <c>track_installer/shared/&lt;code&gt;/</c>, which is where
/// <c>ui_dynamic_texture_loading.uidta</c> is pointed.
///
/// A copy is the track's own view and surface under the new name. The view
/// names its surface after a 24-byte header, so that name moves with it.
/// </summary>
internal static class SharedPictures
{
    public static readonly string[] Pictures = ["preview_scene_1", "preview_scene_2", "preview_scene_3", "session_image"];

    private const int ViewHeader = 24;

    public static IEnumerable<ErpResource> Copy(ErpFile track, TrackZip zip)
    {
        foreach (var picture in Pictures)
        {
            if (!zip.Images.TryGetValue(picture, out var id))
                throw new InvalidDataException($"images.json has no {picture}.");

            var view = track.Resources.Find(r => r.Identifier == id)
                       ?? throw new InvalidDataException($"The track has no {id}, which images.json names as {picture}.");
            var surface = track.Resources.Find(r => r.Identifier == id + ".image")
                          ?? throw new InvalidDataException($"The track has no {id}.image.");

            var newId = MenuDatabases.SharedPicture(zip.Code, picture);

            var viewCopy = view.Clone(track);
            viewCopy.Identifier = newId;
            var data = viewCopy.Fragments[0].GetDataArray(true);
            var named = Encoding.ASCII.GetString(data, ViewHeader, data.Length - ViewHeader).TrimEnd('\0');
            if (named != surface.Identifier)
                throw new InvalidDataException($"{id} doesn't name its surface where a view does.");

            var rewritten = new byte[ViewHeader + newId.Length + ".image".Length + 1];
            data.AsSpan(0, ViewHeader).CopyTo(rewritten);
            Encoding.ASCII.GetBytes(newId + ".image").CopyTo(rewritten, ViewHeader);
            viewCopy.Fragments[0].SetData(rewritten);
            viewCopy.UpdateHash();

            var surfaceCopy = surface.Clone(track);
            surfaceCopy.Identifier = newId + ".image";

            yield return viewCopy;
            yield return surfaceCopy;
        }
    }
}
