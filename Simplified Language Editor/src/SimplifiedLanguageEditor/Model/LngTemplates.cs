using System.Data;
using EgoEngineLibrary.Language;

namespace SimplifiedLanguageEditor.Model;

/// <summary>
/// Hardcoded structural constants for Codemasters Ego Engine LNG files
/// (Verified across F1 2024 language files — same values for all 11 languages.)
/// </summary>
public static class LngTemplates
{
    public const int LngMagic  = 0x4C4E4754; // "LNGT"
    public const int HshsMagic = 0x48534853; // "HSHS"
    public const int HshtMagic = 0x48534854; // "HSHT"
    public const int SidaMagic = 0x53494441; // "SIDA"
    public const int SidbMagic = 0x53494442; // "SIDB"
    public const int LngbMagic = 0x4C4E4742; // "LNGB"

    // Hash function = classic DJB2: h = (h * 33) + char, starting at 5381.
    public const uint Seed       = 0x1505;   // 5381
    public const uint Multiplier = 0x21;     // 33

    /// <summary>
    /// Build an empty LngFile ready for the user to start adding entries.
    /// LngFile.resize() grows the hash bucket array as entries are added.
    /// </summary>
    public static LngFile NewEmpty()
    {
        var info = new DataTable("info");
        info.Columns.Add("lngMagic",     typeof(int));
        info.Columns.Add("lngCount",     typeof(int));
        info.Columns.Add("hshsMagic",    typeof(int));
        info.Columns.Add("hshsBuckets",  typeof(uint));
        info.Columns.Add("hshsSeed",     typeof(uint));
        info.Columns.Add("hshsMult",     typeof(uint));
        info.Columns.Add("hshtMagic",    typeof(int));
        info.Columns.Add("sidaMagic",    typeof(int));
        info.Columns.Add("sidbMagic",    typeof(int));
        info.Columns.Add("lngbMagic",    typeof(int));

        var row = info.NewRow();
        row[0] = LngMagic;
        row[1] = 0;                  // count
        row[2] = HshsMagic;
        row[3] = (uint)1;            // starting bucket count (resize() grows it)
        row[4] = Seed;
        row[5] = Multiplier;
        row[6] = HshtMagic;
        row[7] = SidaMagic;
        row[8] = SidbMagic;
        row[9] = LngbMagic;
        info.Rows.Add(row);

        return new LngFile(info);
    }
}
