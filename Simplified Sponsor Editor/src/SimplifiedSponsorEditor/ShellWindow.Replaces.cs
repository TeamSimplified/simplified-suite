using System;
using System.Collections.Generic;
using System.Linq;
using SimplifiedSponsorEditor.Core;
using SimplifiedSponsorEditor.Core.Sponsors;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The game's own decals, as something to choose from.
/// </summary>
public partial class ShellWindow
{
    /// <summary>One of the game’s sponsors, as the dropdown shows it.</summary>
    /// <param name="Name">
    /// The sponsor’s own name, which is what a sponsor of ours records.
    /// </param>
    /// <param name="Label">
    /// What the row reads, with the count after it. A sponsor is several decals
    /// and all of them are replaced together, so the number is the useful half
    /// of knowing which one you picked.
    /// </param>
    public sealed record Replaceable(string Name, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// Read once, from the archives bundled beside the exe.
    ///
    /// Static because the catalogue is the game’s rather than the project’s: it
    /// is the same list whichever project is open, and reading it per project
    /// would be the same work for the same answer.
    /// </summary>
    private static IReadOnlyList<Replaceable>? _replaceable;

    /// <summary>The archives, once they have been read, and whether they have been.</summary>
    private static Catalogue? _catalogue;
    private static bool _asked;

    /// <summary>
    /// Every decal the game ships, or null when the archives could not be read.
    ///
    /// The dropdown is one question about this and packaging is another — which
    /// decals a sponsor's six variations are going into — so the catalogue
    /// itself is what is kept, rather than only the list of names built from it.
    ///
    /// Null is not the same as empty: a copied folder with no archives beside
    /// the exe can answer nothing at all, and a package that quietly matched
    /// nothing would look like a project with nothing in it.
    /// </summary>
    private static Catalogue? GameDecals
    {
        get
        {
            if (_asked) return _catalogue;
            _asked = true;

            try { _catalogue = SponsorCatalogue.Read(AppPaths.OriginalsDir); }
            catch { _catalogue = null; }

            return _catalogue;
        }
    }

    private static IReadOnlyList<Replaceable> Replaceables
    {
        get
        {
            if (_replaceable is not null) return _replaceable;

            // Sponsors, not decals. One of ours takes the place of a whole
            // sponsor — every variation of it the game ships — so listing the
            // decals would be offering the same choice six times over and
            // inviting somebody to replace a fifth of a sponsor.
            //
            // No archives leaves the row showing nothing rather than taking the
            // window down.
            _replaceable = GameDecals?.BySponsor
                .Select(group => new Replaceable(
                    group.Key,
                    group.Count() == 1 ? group.Key : $"{group.Key}  ({group.Count()})"))
                .ToList() ?? [];

            return _replaceable;
        }
    }

    /// <summary>Fill the dropdown. Once: the list never changes while the app runs.</summary>
    private void FillReplaces()
    {
        if (Replaces.ItemsSource is null) Replaces.ItemsSource = Replaceables;
    }

    /// <summary>What the page currently says this sponsor replaces, or "".</summary>
    private string ChosenReplacement =>
        (Replaces.SelectedItem as Replaceable)?.Name ?? "";

    /// <summary>
    /// Point the dropdown at a decal by name. Anything it does not know about
    /// clears the box rather than inventing a row — a project naming a decal
    /// this build has never heard of should say nothing, not something wrong.
    /// </summary>
    private void ShowReplacement(string? name)
    {
        FillReplaces();

        Replaces.SelectedItem = string.IsNullOrWhiteSpace(name)
            ? null
            : Replaceables.FirstOrDefault(
                r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
