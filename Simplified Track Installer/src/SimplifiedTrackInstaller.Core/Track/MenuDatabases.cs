using System.Text.Json.Nodes;

namespace SimplifiedTrackInstaller.Core.Track;

/// <summary>
/// The menu's three databases, changed the way the EgoGameDatabasesEditor
/// changes them — measured by installing a track with it and diffing the game.
///
/// Every edit is an upsert by name: a track already in a list, from an earlier
/// install of this one or from the editor itself, is updated where it stands
/// rather than added a second time.
/// </summary>
internal static class MenuDatabases
{
    public const string OverridesId = "eaid://ui_package/databases/overrides.uidb";
    public const string MinimapId = "eaid://ui_package/databases/minimap.uidb";
    public const string LoadingId = "eaid://ui_package/art/texture_arrays/ui_dynamic_texture_loading.uidta";

    /// <summary>The asset group the editor names for the preview and session pictures.</summary>
    public const string PicturesAssetGroup =
        "asset_groups/ui_package/texture_arrays/invitational_events/event_images/invitational_image_2.assetgroup";

    /// <summary>Where the editor keeps the copies of the pictures the menus stream in.</summary>
    public static string SharedPicture(string code, string picture) =>
        $"eaid://ui_package/art/textures/track_installer/shared/{code}/{picture}.tif";

    /// <summary>The menus that show the track select's three preview scenes.</summary>
    private static readonly string[] TrackSelectGroups =
    [
        "fe_world.track_select", "fe_world.track_select_f2",
        "fe_world.track_select_26", "fe_world.track_select_26_f2",
    ];

    /// <summary>The menus that show the session picture.</summary>
    private static readonly string[] SessionGroups =
    [
        "session_manager.track_images", "session_manager.track_images_26",
    ];

    public static string ArrayName(string array) =>
        $"ui_package/art/texture_arrays/overrides.uidb/overrides.{array}.texarray";

    /// <summary>
    /// Name the track in a texture array's list in <c>overrides.uidb</c>, against
    /// the picture it shows. The position in that list is the slice the picture
    /// has to be in, so that is what comes back.
    /// </summary>
    public static (int Index, bool Added) AddToArray(JsonNode overrides, string array, string code, string picture)
    {
        var name = ArrayName(array);
        var group = PkgJson.Objects(overrides).FirstOrDefault(o => PkgJson.Str(o, "TextureArrayName") == name)
                    ?? throw new InvalidDataException($"overrides.uidb has no list for {array}.");

        if (PkgJson.Get(group, "Contents") is not JsonArray contents)
            throw new InvalidDataException($"The list for {array} in overrides.uidb has no contents.");

        var (entry, index, added) = PkgJson.Upsert(contents, code);
        PkgJson.Set(entry, "Value", PkgJson.Stri(WithoutScheme(picture)));
        return (index, added);
    }

    /// <summary>
    /// The track's entry in <c>minimap.uidb</c>: a copy of the base track's, or
    /// of the last one, with the zip's numbers and pictures in it.
    /// </summary>
    /// <param name="slot">
    /// The slot the track ERP goes in. The minimap's textures are in that ERP,
    /// so the entry names the slot's asset group, as the editor's does.
    /// </param>
    public static (int Index, bool Added) AddMinimap(JsonNode minimap, TrackZip track, string slot, Action<string>? log)
    {
        var tracks = FindMinimapList(minimap)
                     ?? throw new InvalidDataException("minimap.uidb has no list of tracks.");

        var existing = Find(tracks, track.Code);
        int index;
        bool added;
        JsonObject entry;

        if (existing >= 0)
        {
            (entry, index, added) = ((JsonObject)tracks[existing]!, existing, false);
        }
        else
        {
            // The base track's entry when it has one: it is the closest thing
            // to what this track needs, and has every key the game reads.
            var model = track.CopyFromTrack is { } from && Find(tracks, from) is >= 0 and var at
                ? (JsonObject)tracks[at]!
                : (JsonObject)tracks[^1]!;
            entry = (JsonObject)model.DeepClone();
            PkgJson.Set(entry, "Name", PkgJson.Stri(track.Code));
            tracks.Add(entry);
            (index, added) = (tracks.Count - 1, true);
        }

        var contents = PkgJson.Get(entry, "Contents") as JsonObject
                       ?? throw new InvalidDataException($"The minimap entry for {track.Code} has no contents.");
        var m = track.Minimap;

        void Value(string name, string typed)
        {
            if (PkgJson.Get(contents, name) is JsonObject field) PkgJson.Set(field, "Value", typed);
            else log?.Invoke($"  ⚠ The minimap entry has no {name}; left out.");
        }

        Value("rotation", PkgJson.Fp32(m.Rotation));
        Value("zoom_factor", PkgJson.Fp32(m.ZoomFactor));
        Value("padding", PkgJson.Fp32(m.Padding));
        Value("dimensions", PkgJson.Fp32(m.Dimensions));
        Value("current_texture_dict", PkgJson.Stri($"hud.minimap.tracks[{index}].textures"));
        Value("asset_group", PkgJson.Stri(TrackSlots.AssetGroup(slot)));

        Textures(contents, "textures", m.Textures, log);
        Textures(contents, "textures_om_xm", m.TexturesOmXm, log);
        return (index, added);
    }

    /// <summary>
    /// The pictures the menus stream in: the track select's three preview scenes
    /// and the session picture, each pointing at the editor's shared copy.
    /// </summary>
    public static int AddPictures(JsonNode loading, string code)
    {
        var changed = 0;
        foreach (var group in Groups(loading, TrackSelectGroups))
        {
            for (var scene = 1; scene <= 3; scene++)
                Picture(group, scene == 1 ? code : $"{code}_{scene}", SharedPicture(code, $"preview_scene_{scene}"));
            changed++;
        }

        foreach (var group in Groups(loading, SessionGroups))
        {
            Picture(group, code, SharedPicture(code, "session_image"));
            changed++;
        }

        return changed;
    }

    private static void Picture(JsonArray group, string name, string resourceId)
    {
        var (entry, _, _) = PkgJson.Upsert(group, name);
        PkgJson.Set(entry, "AssetGroupPath", PkgJson.Stri(PicturesAssetGroup));
        PkgJson.Set(entry, "ResourceId", PkgJson.Stri(resourceId));
    }

    private static IEnumerable<JsonArray> Groups(JsonNode loading, string[] names)
    {
        foreach (var name in names)
        {
            var group = PkgJson.Objects(loading).FirstOrDefault(o =>
                PkgJson.Str(o, "Name") == name && PkgJson.Get(o, "TextureArray") is JsonArray);
            if (group is null)
                throw new InvalidDataException($"ui_dynamic_texture_loading.uidta has no {name}.");
            yield return (JsonArray)PkgJson.Get(group, "TextureArray")!;
        }
    }

    private static void Textures(JsonObject contents, string name, MinimapTextures textures, Action<string>? log)
    {
        if (PkgJson.Get(contents, name) is not JsonObject set || PkgJson.Get(set, "Contents") is not JsonObject inner)
        {
            log?.Invoke($"  ⚠ The minimap entry has no {name}; left out.");
            return;
        }

        foreach (var (corner, (x, y)) in textures.Corners)
        {
            if (PkgJson.Get(inner, corner) is not JsonObject c || PkgJson.Get(c, "Contents") is not JsonArray xy) continue;
            foreach (var axis in xy.OfType<JsonObject>())
            {
                var which = PkgJson.Str(axis, "Name");
                if (which == "x_coordinate") PkgJson.Set(axis, "Value", PkgJson.Fp32(x));
                else if (which == "y_coordinate") PkgJson.Set(axis, "Value", PkgJson.Fp32(y));
            }
        }

        foreach (var (picture, id) in textures.Resources)
        {
            if (PkgJson.Get(inner, picture) is JsonObject field)
                PkgJson.Set(field, "Value", PkgJson.Stri(WithoutScheme(id)));
        }
    }

    /// <summary>The array of tracks: the one whose entries carry a <c>current_texture_dict</c>.</summary>
    private static JsonArray? FindMinimapList(JsonNode minimap)
    {
        foreach (var o in PkgJson.Objects(minimap))
        {
            foreach (var (_, child) in o)
            {
                if (child is JsonArray a && a.Count > 0 && a[0] is JsonObject first
                    && PkgJson.Get(first, "Contents") is JsonObject c && PkgJson.Key(c, "current_texture_dict") is not null)
                    return a;
            }
        }
        return null;
    }

    private static int Find(JsonArray list, string name)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] is JsonObject o && PkgJson.Str(o, "Name") == name) return i;
        return -1;
    }

    private static string WithoutScheme(string id) =>
        id.StartsWith("eaid://", StringComparison.OrdinalIgnoreCase) ? id[7..] : id;
}
