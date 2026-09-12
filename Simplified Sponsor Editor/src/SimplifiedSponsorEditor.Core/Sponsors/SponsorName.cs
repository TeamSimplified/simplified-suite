namespace SimplifiedSponsorEditor.Core.Sponsors;

/// <summary>
/// Which sponsor a decal belongs to, read out of its name.
///
/// The game names these <c>&lt;sponsor&gt;_&lt;variant&gt;_&lt;type&gt;</c> and
/// nothing says where the sponsor part ends — <c>7even_n_da</c>,
/// <c>7even_p_da</c> and <c>7even_s1_da</c> are three versions of one logo, and
/// <c>alpinestars_01_d</c> is another. So the name is read backwards, dropping
/// the parts that are plainly not a name, and whatever is left at the front is
/// the sponsor.
///
/// It is a heuristic and it is allowed to be: getting it wrong splits one
/// sponsor into two groups or merges two into one, which is untidy in the tree
/// and changes nothing about what is written.
/// </summary>
public static class SponsorName
{
    /// <summary>
    /// What a decal's last part says it is — diffuse, normal, specular and the
    /// lettered variants of each. Always at the end of the name.
    /// </summary>
    private static readonly HashSet<string> TypeSuffixes = new(StringComparer.Ordinal)
    {
        "d", "n", "s", "r", "e",
        "da", "db", "dc",
        "na", "nb", "nc",
        "sa", "sb", "sc",
        "ra", "rb", "rc",
        "ea", "eb", "ec",
    };

    /// <summary>The sponsor <paramref name="textureName"/> belongs to.</summary>
    public static string Of(string textureName)
    {
        var name = textureName.ToLowerInvariant();
        var parts = name.Split('_');

        if (parts.Length < 2) return parts.Length > 0 ? parts[0] : textureName;

        // Walk in from the end while each part looks like a suffix or a variant
        // marker rather than part of a name. Stop at the first part that does
        // not, since everything before it is the name.
        var end = parts.Length;
        for (var i = parts.Length - 1; i > 0; i--)
        {
            if (!IsDecoration(parts[i])) break;
            end = i;
        }

        return end > 0 ? string.Join('_', parts[..end]) : parts[0];
    }

    /// <summary>
    /// Whether a part is a texture type or a variant marker — <c>da</c>,
    /// <c>s1</c>, <c>p</c>, <c>01</c> — rather than a word in the sponsor's name.
    /// </summary>
    private static bool IsDecoration(string part) =>
        TypeSuffixes.Contains(part)
        || (part.Length == 1 && char.IsLetter(part[0]))                        // p, n
        || (part.Length == 2 && char.IsLetter(part[0]) && char.IsDigit(part[1])) // s1, s2
        || part.All(char.IsDigit);                                             // 01, 02
}
