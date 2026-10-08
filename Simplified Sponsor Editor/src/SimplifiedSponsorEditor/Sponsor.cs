using System.Collections.Generic;
using System.Linq;

namespace SimplifiedSponsorEditor;

/// <summary>
/// A sponsor as the project holds one: a name, a line about it, and the six
/// variations of its artwork.
///
/// Six always, whether or not anything is in them, so a variation that is empty
/// is an empty variation rather than a gap in a list. The page that edits one is
/// six squares either way, and a model that only held the filled ones would have
/// to invent the rest every time it was shown.
/// </summary>
public sealed class Sponsor
{
    /// <summary>
    /// Which variation stands for the sponsor in a list. The third, because that
    /// is the one the shelf shows — said here rather than typed into the card,
    /// so the day it becomes a choice there is one place to change.
    /// </summary>
    public const int FaceAt = 2;

    public string Name { get; set; } = "";

    public string Bio { get; set; } = "";

    /// <summary>
    /// The short name of the game decal this one takes the place of, e.g.
    /// <c>7even_n_da</c>, or empty while nobody has said.
    ///
    /// The name rather than the resource identifier: the identifier is a path
    /// into an archive and would change if the game moved it, where the short
    /// name is what the catalogue pairs the two archives on and what a project
    /// has always filed its artwork under.
    /// </summary>
    public string Replaces { get; set; } = "";

    public List<Variation> Variations { get; set; } =
        Enumerable.Range(0, 6).Select(_ => new Variation()).ToList();

    /// <summary>
    /// The one a card draws, or null when that slot is empty. Null rather than
    /// the first one that happens to be filled: a card standing for a sponsor
    /// should show the same artwork every time it is drawn, and falling back
    /// would make which variation it shows depend on what has been filled in
    /// since.
    /// </summary>
    public Variation? Face =>
        FaceAt < Variations.Count && !Variations[FaceAt].IsEmpty ? Variations[FaceAt] : null;

    /// <summary>
    /// What a card calls it. A sponsor with no name still has to be something
    /// on the shelf, and an unlabelled square is a thing nobody can tell from
    /// the one beside it.
    /// </summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? "Untitled" : Name.Trim();
}
