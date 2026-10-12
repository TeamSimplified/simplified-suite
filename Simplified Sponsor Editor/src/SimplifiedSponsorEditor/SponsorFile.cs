using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Simplified.Shared;
using SimplifiedSponsorEditor.Core.Artwork;
using SimplifiedSponsorEditor.Core.Project;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The sponsors a project defines, on disk.
///
/// One JSON each rather than one file listing them all. A sponsor is a thing
/// somebody made and will rename, and a file per sponsor means a rename touches
/// one file, a deletion removes one, and two people working on the same project
/// collide only where they edited the same sponsor. A single list would rewrite
/// itself completely every time any of it changed.
///
/// They live in <c>Sponsors/</c>, which 1.0 used for its rendered decal pictures
/// and nothing has written to since. The two cannot be confused: those are PNGs
/// named after a decal in the game, these are JSON named after a sponsor
/// somebody invented, and each reader looks only for its own extension.
///
/// <code>
/// Sponsors/
///     001-duotone.json
///     002-northward.json
/// </code>
///
/// Numbered because the shelf has an order and a folder does not. The number is
/// the position, rewritten whenever the shelf is written, so the order survives
/// a save and the name stays readable beside it.
/// </summary>
public static class SponsorFile
{
    private const string Extension = ".json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// What a sponsor is on disk.
    ///
    /// Its own type rather than the one the window edits: that one carries
    /// WPF-side state and six live variations, and a file format should not
    /// change because a field was added to a view. Everything here is written
    /// and read by name, so a later version adding to it leaves this one able to
    /// read its files.
    /// </summary>
    private sealed record Record
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";

        [JsonPropertyName("bio")] public string Bio { get; init; } = "";

        /// <summary>The game decal this one stands in for, by short name.</summary>
        [JsonPropertyName("replaces")] public string Replaces { get; init; } = "";

        /// <summary>
        /// Only the slots with something in them, each saying which slot it is.
        ///
        /// A sparse list rather than six entries with nulls between them: which
        /// slot a variation belongs to matters - the third is the one a card
        /// shows - and "at" says so in a way that survives the row ever being a
        /// different length.
        /// </summary>
        [JsonPropertyName("variations")] public List<Slot> Variations { get; init; } = [];
    }

    /// <summary>One variation, as the file keeps it.</summary>
    private sealed record Slot
    {
        [JsonPropertyName("at")] public int At { get; init; }

        /// <summary>Its name inside the project's own artwork folder.</summary>
        [JsonPropertyName("file")] public string File { get; init; } = "";

        [JsonPropertyName("removed")] public List<int>? Removed { get; init; }

        [JsonPropertyName("colours")] public Dictionary<int, string>? Colours { get; init; }

        [JsonPropertyName("overlay")] public string? Overlay { get; init; }

        /// <summary>
        /// For a picture with edits, which way of taking pictures apart
        /// numbered the pieces they name — see <see cref="RasterParts.Version"/>.
        /// A drawing's pieces are numbered by its file and need no such thing.
        /// </summary>
        [JsonPropertyName("parts")] public int? Parts { get; init; }

        [JsonPropertyName("car_span")] public double CarSpan { get; init; } = Variation.DefaultSpan;

        [JsonPropertyName("menu_span")] public double MenuSpan { get; init; } = Variation.DefaultSpan;
    }

    /// <summary>
    /// Write the shelf, replacing whatever was there.
    ///
    /// The folder is cleared of its JSON first rather than written over: a
    /// sponsor deleted in this session has a file that nothing would otherwise
    /// overwrite, and it would come back on the next open.
    /// </summary>
    public static void Write(SponsorProject project, IReadOnlyList<Sponsor> sponsors)
    {
        var folder = Folder(project);
        Directory.CreateDirectory(folder);

        foreach (var stale in Directory.EnumerateFiles(folder, "*" + Extension))
            File.Delete(stale);

        var artwork = Artwork(project);
        Directory.CreateDirectory(artwork);

        // What the artwork folder should hold once this is done. Collected as it
        // goes, so whatever is left over can be swept up afterwards.
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var at = 0; at < sponsors.Count; at++)
        {
            var sponsor = sponsors[at];
            var slots = new List<Slot>();

            for (var slot = 0; slot < sponsor.Variations.Count; slot++)
            {
                var variation = sponsor.Variations[slot];
                if (Store(variation, artwork) is not { } stored) continue;

                kept.Add(stored);

                slots.Add(new Slot
                {
                    At = slot,
                    File = stored,
                    // Left out when there is nothing to say, so an untouched
                    // drawing is a line with a filename on it rather than three
                    // empty collections.
                    Removed = variation.Edits.Removed.Count > 0 ? variation.Edits.Removed : null,
                    Colours = variation.Edits.Colours.Count > 0 ? variation.Edits.Colours : null,
                    Overlay = variation.Overlay,
                    Parts = !variation.IsVector && !variation.Edits.IsEmpty ? RasterParts.Version : null,
                    CarSpan = variation.CarSpan,
                    MenuSpan = variation.MenuSpan,
                });
            }

            var record = new Record
            {
                Name = sponsor.Name,
                Bio = sponsor.Bio,
                Replaces = sponsor.Replaces,
                Variations = slots,
            };

            File.WriteAllText(
                Path.Combine(folder, $"{at + 1:000}-{Slug(sponsor.Label)}{Extension}"),
                JsonSerializer.Serialize(record, Json));
        }

        Sweep(artwork, kept);
    }

    /// <summary>
    /// Put a variation's file inside the project, and say what it is called
    /// there. Null when the slot is empty or the file has gone.
    ///
    /// Copied once. The name is remembered on the variation, so saving a project
    /// ten times copies the artwork in once - and the copy is the project's,
    /// which is the point: a project that only remembered where a logo used to
    /// be would open empty on another machine.
    /// </summary>
    private static string? Store(Variation variation, string artwork)
    {
        if (variation.SourcePath is not { } source) return null;

        if (variation.StoredName is { } already
            && File.Exists(Path.Combine(artwork, already)))
            return already;

        if (!File.Exists(source)) return null;

        var extension = Path.GetExtension(source);
        string name;

        do name = Guid.NewGuid().ToString("N")[..8] + extension;
        while (File.Exists(Path.Combine(artwork, name)));

        File.Copy(source, Path.Combine(artwork, name));
        variation.StoredName = name;
        return name;
    }

    /// <summary>
    /// Throw away artwork no sponsor refers to any more - a variation that was
    /// emptied, or a sponsor that was deleted. Its own folder, so there is
    /// nothing else in here to delete by accident.
    /// </summary>
    private static void Sweep(string artwork, HashSet<string> kept)
    {
        foreach (var path in Directory.EnumerateFiles(artwork))
        {
            if (kept.Contains(Path.GetFileName(path))) continue;

            try { File.Delete(path); }
            catch { /* held open by something; it will be swept next time */ }
        }
    }

    /// <summary>
    /// Read the shelf back, in the order the names put them. An empty or absent
    /// folder is an empty shelf rather than a failure — a project saved before
    /// this existed has no sponsors, which is exactly true.
    /// </summary>
    public static List<Sponsor> Read(SponsorProject project)
    {
        var folder = Folder(project);
        if (!Directory.Exists(folder)) return [];

        var sponsors = new List<Sponsor>();

        foreach (var path in Directory.EnumerateFiles(folder, "*" + Extension)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (JsonSerializer.Deserialize<Record>(File.ReadAllText(path), Json)
                    is not { } record)
                    continue;

                var sponsor = new Sponsor
                {
                    Name = record.Name,
                    Bio = record.Bio,
                    Replaces = record.Replaces,
                };

                foreach (var slot in record.Variations)
                {
                    if (slot.At < 0 || slot.At >= sponsor.Variations.Count) continue;

                    var file = Path.Combine(Artwork(project), slot.File);
                    if (!File.Exists(file)) continue;

                    var variation = new Variation
                    {
                        SourcePath = file,
                        // After the path, because setting the path is what
                        // clears this - a variation pointed somewhere new has no
                        // copy in the project yet, and that is exactly what this
                        // name would otherwise be claiming.
                        StoredName = slot.File,
                        Edits = new SvgEdits
                        {
                            Removed = slot.Removed ?? [],
                            Colours = slot.Colours ?? [],
                        },
                        Overlay = slot.Overlay,
                        CarSpan = slot.CarSpan,
                        MenuSpan = slot.MenuSpan,
                    };

                    // A picture's edits name pieces by number, and the numbers
                    // belong to the version of the app that took the picture
                    // apart. Read against a different numbering they would
                    // paint and drop whatever is now called by those numbers,
                    // so they are let go and the picture opens as its file —
                    // which is wrong in a way somebody can see and redo, rather
                    // than in a way that ships.
                    if (!variation.IsVector && !variation.Edits.IsEmpty && slot.Parts != RasterParts.Version)
                        variation.Edits = new SvgEdits();

                    sponsor.Variations[slot.At] = variation;
                }

                sponsors.Add(sponsor);
            }
            catch
            {
                // One unreadable sponsor should not cost somebody the rest of
                // the project. It is skipped, and saving will not write it back.
            }
        }

        return sponsors;
    }

    private static string Folder(SponsorProject project) =>
        Path.Combine(project.TempDir, ProjectStore.SponsorsFolderName);

    /// <summary>
    /// Where the project keeps its copy of the artwork.
    ///
    /// Not <c>Sources/</c>. That one is 1.0 and 1.1's, keyed by the name of a
    /// decal in the game, and this is keyed by nothing of the sort - sweeping
    /// unreferenced files out of it would throw away artwork a 1.1 project put
    /// there and this version knows nothing about.
    /// </summary>
    private static string Artwork(SponsorProject project) =>
        Path.Combine(project.TempDir, "Variations");

    /// <summary>
    /// A name as a filename: lower case, words joined by a dash, and nothing a
    /// filesystem would object to. Only to make the folder readable — the number
    /// in front is what keeps two sponsors of the same name apart.
    /// </summary>
    private static string Slug(string name)
    {
        var slug = new StringBuilder();
        var dashed = false;

        foreach (var letter in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(letter))
            {
                slug.Append(letter);
                dashed = false;
            }
            else if (!dashed && slug.Length > 0)
            {
                slug.Append('-');
                dashed = true;
            }
        }

        var done = slug.ToString().Trim('-');

        // Something has to go after the number, and a name of nothing but
        // punctuation would otherwise leave a file called "001-.json".
        return done.Length == 0 ? "sponsor" : done[..Math.Min(done.Length, 40)];
    }
}
