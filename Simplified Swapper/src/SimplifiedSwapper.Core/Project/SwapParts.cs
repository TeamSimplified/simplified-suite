using System.Text.RegularExpressions;

namespace SimplifiedSwapper.Core.Project;

/// <summary>
/// The <c>NN--GG--</c> prefix Studio puts on every folder and part in a
/// template.
///
/// <code>
///   02--01--Livery - Default Chassis
///   │     └── conflict group: 00 = none, 01-99 = a group
///   └──────── order: 00-99
/// </code>
///
/// Order is a sort key for display and for merge sequence. The group is what
/// makes two things alternatives: same parent, same non-zero group, so only one
/// of them is ever switched on.
///
/// Only as much of Studio's scheme as this app needs to read — it writes one
/// folder and renumbers nothing.
/// </summary>
public static class SerpsPrefix
{
    private static readonly Regex Pattern = new(@"^(\d{2})--(\d{2})--", RegexOptions.Compiled);

    /// <summary>Sorts after everything that carries a prefix.</summary>
    public const int NoOrder = int.MaxValue;

    /// <summary>Order 00-99, or <see cref="NoOrder"/> when unprefixed.</summary>
    public static int OrderOf(string name)
    {
        var m = Pattern.Match(name ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : NoOrder;
    }

    /// <summary>Conflict group 1-99, or null for none. <c>00</c> is "no group".</summary>
    public static int? GroupOf(string name)
    {
        var m = Pattern.Match(name ?? "");
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out var n)) return null;
        return n == 0 ? null : n;
    }

    /// <summary>The name with the prefix removed — what Studio shows.</summary>
    public static string Display(string name)
    {
        if (string.IsNullOrEmpty(name)) return name ?? "";
        var m = Pattern.Match(name);
        return m.Success ? name[m.Length..] : name;
    }

    /// <summary>Build a prefixed name. Both halves are clamped to 00-99.</summary>
    public static string Apply(string displayName, int order, int group) =>
        $"{Math.Clamp(order, 0, 99):00}--{Math.Clamp(group, 0, 99):00}--{displayName}";
}

/// <summary>
/// Where each swap lands inside a template.
///
/// The group is fixed and the order is not, because they mean different things.
/// The group is what a swap <em>is</em> — chassis swaps are group 01, the group
/// every template's stock chassis folders carry, so a swapped chassis arrives as
/// another option alongside them and Studio switches the stock one off when the
/// swapped one goes on. Getting that wrong would let both merge at once.
///
/// The order is only where it sits in the list, and the right answer differs per
/// template: the stock packs run to three layouts, with driverwear at 03, 04 or
/// 05 depending on how many chassis the team has. So it is read off the template
/// at insert time — see <see cref="SerpsStore.PlacePart"/> — which puts each swap
/// directly after the folders it is an alternative to, whichever layout it is.
/// </summary>
public static class SwapParts
{
    /// <param name="DisplayName">The folder's name, once its prefix is stripped.</param>
    /// <param name="ConflictGroup">What it is an alternative to.</param>
    /// <param name="NameSuffix">
    /// What the ERP inside is called, after the template's own token. It follows
    /// the stock parts: a template's chassis is
    /// <c>26-ferrari_car_default_livery-and-chassis</c> and its driverwear
    /// <c>26-ferrari_driverwear</c>, so a swap reads as one more of those rather
    /// than as something a different program left behind.
    /// </param>
    public sealed record Destination(string DisplayName, int ConflictGroup, string NameSuffix)
    {
        /// <summary>
        /// What identifies a swap of this kind inside a template, whatever its
        /// folder ended up called.
        ///
        /// The part's name rather than the folder's: a folder named after the
        /// car changes when the car does, and something has to survive that for
        /// a second swap to know what it is replacing. Stock parts are
        /// <c>car_default_…</c> and <c>car_FOM_…</c>, so this cannot match one
        /// of those by accident — which matters, since what it finds is deleted.
        /// </summary>
        public string Marker => NameSuffix;
    }

    /// <summary>
    /// Where a swapped chassis goes, named after the car that went in —
    /// <c>Livery - Ferrari Chassis</c> — so a template says what was done to it
    /// without being opened.
    ///
    /// That the name varies is why nothing looks a swap up by it; see
    /// <see cref="Destination.Marker"/>.
    /// </summary>
    public static Destination Chassis(string carName) =>
        new($"Livery - {Clean(carName)} Chassis", 1, "car_swapped_livery-and-chassis");

    public static readonly Destination Driverwear =
        new("Driverwear - Swapped", 2, "driverwear_swapped");

    /// <summary>
    /// A car name fit to be a folder name. The invalid characters would become
    /// real path separators, and a double hyphen reads as one of Studio's
    /// prefixes.
    /// </summary>
    private static string Clean(string name)
    {
        name = (name ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = name.Replace("--", "-").Trim();
        return name.Length == 0 ? "Swapped" : name;
    }

    /// <summary>
    /// Prefix for the ERP itself. Group <c>00</c> is "no group": the part is the
    /// only one in its folder, so it is an alternative to nothing inside it —
    /// the folder is what conflicts with the stock folders.
    /// </summary>
    private const string PartPrefix = "01--00--";

    /// <summary>
    /// What a swap is called inside the template:
    /// <c>01--00--26-ferrari_car_swapped_livery-and-chassis.erp</c>.
    ///
    /// With no token to go on — a template holding nothing named that way — the
    /// suffix stands alone rather than inventing a team.
    /// </summary>
    public static string PartFileName(string templatePrefix, Destination destination)
    {
        var name = string.IsNullOrWhiteSpace(templatePrefix)
            ? destination.NameSuffix
            : templatePrefix.Trim() + "_" + destination.NameSuffix;

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        return PartPrefix + name + ".erp";
    }
}
