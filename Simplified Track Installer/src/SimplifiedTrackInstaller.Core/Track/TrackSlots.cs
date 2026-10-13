using System.Text.Json;

namespace SimplifiedTrackInstaller.Core.Track;

/// <summary>
/// Which archive the track itself goes in.
///
/// A track is not something a shader package can carry: the game loads those
/// before any track exists, and a whole track's worlds, meshes and AI data in
/// one is what crashed it. The EgoGameDatabasesEditor puts the track ERP in one
/// of the invitational event-image slots instead — an archive the game already
/// has an asset group for — and points the track's database entry at that
/// group. The track is loaded with the track, the way the game's own are.
///
/// Which slot belongs to the database, since that is what names it. The editor
/// writes down the slot it gave each track it installed, so when this track is
/// in that record its slot is the one the database expects; otherwise it is the
/// slot the editor gives a first track.
/// </summary>
public static class TrackSlots
{
    public const string Folder = "2025_asset_groups/ui_package/texture_arrays/invitational_events/event_images";

    /// <summary>The slot the editor gives a track when it has no record of one.</summary>
    public const string DefaultSlot = "invitational_image_4";

    /// <summary>The editor's record of which slot each installed track has.</summary>
    public const string RecordFileName = "f1_db_editor_invitational_slots.json";

    /// <summary>Where a slot's archive sits, relative to the game folder.</summary>
    public static string ArchivePath(string slot) => $"{Folder}/{slot}.erp";

    /// <summary>The asset group that loads a slot's archive, as the databases name it.</summary>
    public static string AssetGroup(string slot) =>
        $"asset_groups/ui_package/texture_arrays/invitational_events/event_images/{slot}.assetgroup";

    /// <summary>
    /// The slot for <paramref name="code"/>, and whether it came from the
    /// editor's record rather than the default.
    /// </summary>
    public static (string Slot, bool Recorded) For(string gameDir, string code)
    {
        try
        {
            var record = Path.Combine(gameDir, Folder.Replace('/', Path.DirectorySeparatorChar), RecordFileName);
            if (File.Exists(record))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(record));
                if (json.RootElement.TryGetProperty("installations", out var installs)
                    && installs.TryGetProperty(code, out var install)
                    && install.TryGetProperty("slot_name", out var slot)
                    && slot.GetString() is { Length: > 0 } name)
                {
                    return (name, true);
                }
            }
        }
        catch
        {
            // An unreadable record is no record.
        }

        return (DefaultSlot, false);
    }
}
