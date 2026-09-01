using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace F1modding3dtoolV1
{
    public static class UniversalIdfNaming
    {
        // Corner / side suffix letters
        public static readonly string[] CornerSuffixes = { "_fl", "_fr", "_bl", "_br" };
        public static readonly string[] SideSuffixes = { "_l", "_r" };
        public static readonly string WaterSuffix = "_water";

        // LOD / VB / IB patterns (team-agnostic)
        public static readonly int[] LodLevels = { 0, 1, 2, 3, 4, 5, 6, 7 };

        public const string VbPattern = ".idf%3Flod=all&vb=";
        public const string IbPattern = ".idf%3Flod=all&ib=";
        public const string LodMeshPrefix = ".idf%3Flod=";
        public const string LodMeshSuffix = "&mesh";
        public const string ModelSuffix = "%3Fmodel";

        // Universal mesh names used to build mesh descriptors
        public static readonly string[] UniversalMeshNames = new[]
        {
            "camera_wing", "diffuser_bm", "disc_bl", "disc_br", "disc_fl", "disc_fr",
            "driver_surroun", "driver_surround_water", "frontflap_fl", "frontflap_fl_water",
            "frontflap_fr", "frontflap_fr_water", "frontspoiler_fm", "frontspoiler_fm_water",
            "frontwing_fl_01", "frontwing_fl_01_water", "frontwing_fl_02", "frontwing_fl_02_water",
            "frontwing_fr_01", "frontwing_fr_01_water", "frontwing_fr_02", "frontwing_fr_02_water",
            "hal", "hub_caliper_bl", "hub_caliper_br", "hub_caliper_fl", "hub_caliper_fr",
            "left_paddle", "right_paddle", "mesh", "mix_dial", "nosecone", "nosecone_water",
            "rearfin_bm", "rearwing_bl", "rearwing_br", "rearwing_top", "rearwing_top_water",
            "rearwingmoving_top", "rearwingmoving_top_2", "safetycell", "safetycell_water",
            "sidewing_fl", "sidewing_fl_water", "sidewing_fr", "sidewing_fr_water",
            "steering_wheel", "suspension_bl", "suspension_br", "suspension_fl", "suspension_fr",
            "suspension2_bl", "suspension2_br", "suspension2_fl", "suspension2_fr",
            "suspension3_bl", "suspension3_br", "suspension3_fl", "suspension3_fr",
            "tether_bl", "tether_br", "tether_fl", "tether_fr", "tyre_dial",
            "wheel_bl", "wheel_br", "wheel_fl", "wheel_fr", "wing_mirror_l", "wing_mirror_r"
        };

        /// <summary>
        /// All known car-part mesh names from this file (UniversalMeshNames + CanonicalPartNames).
        /// This is what the XAML.cs will now use to build mesh descriptors.
        /// </summary>
        public static IEnumerable<string> AllMeshNames
        {
            get
            {
                // Union both lists in case one evolves independently
                return UniversalMeshNames
                    .Concat(CanonicalPartNames)
                    .Distinct();
            }
        }

        /// <summary>
        /// NEW: Returns a clean object name for exporting.
        /// Does NOT strip _fl, _fr, _01, etc. ensuring distinct objects remain separate.
        /// </summary>
        public static string GetSmartPartName(string label, string vbufName)
        {
            // 1. Prefer the internal label if it looks specific (not generic "mesh_0")
            string candidate = label;

            // If label is missing or generic/useless, fallback to the file name (vbuf)
            if (string.IsNullOrWhiteSpace(candidate) ||
                candidate.StartsWith("mesh_", System.StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("temp", System.StringComparison.OrdinalIgnoreCase))
            {
                candidate = vbufName;
            }

            if (string.IsNullOrWhiteSpace(candidate)) return "unknown_mesh";

            // 2. Clean up the candidate string
            return CleanNamePreservingDetails(candidate);
        }

        private static string CleanNamePreservingDetails(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "object";

            // Remove path
            string name = Path.GetFileName(input);

            // Split by common junk delimiters found in F1 files
            // e.g. "mclaren!!!temp000_suspension_fl.idf?lod=0"

            // 1. Remove the "team!!!temp000" prefix if present
            int tripleExcl = name.IndexOf("!!!");
            if (tripleExcl >= 0)
            {
                // check if it starts with temp000 or temp001 after the !!!
                int after = tripleExcl + 3;
                if (after < name.Length)
                {
                    // Usually "temp000" or "temp000_"
                    if (name.Substring(after).StartsWith("temp"))
                    {
                        // Look for the next underscore or dot
                        int nextUnderscore = name.IndexOf('_', after);
                        int nextDot = name.IndexOf('.', after);

                        // Pick the earliest separator to start the real name
                        int startIdx = -1;
                        if (nextUnderscore > 0 && nextDot > 0) startIdx = System.Math.Min(nextUnderscore, nextDot);
                        else if (nextUnderscore > 0) startIdx = nextUnderscore;
                        else if (nextDot > 0) startIdx = nextDot;

                        if (startIdx > 0)
                            name = name.Substring(startIdx + 1); // skip the separator
                    }
                }
            }

            // 2. Remove URL parameters / Extensions
            // Cut off at first occurrence of .idf, ?, or &
            int dotIdf = name.IndexOf(".idf", System.StringComparison.OrdinalIgnoreCase);
            if (dotIdf > 0) name = name.Substring(0, dotIdf);

            int qMark = name.IndexOf('?');
            if (qMark > 0) name = name.Substring(0, qMark);

            int amp = name.IndexOf('&'); // sometimes &vb=... comes without ?
            if (amp > 0) name = name.Substring(0, amp);

            // 3. Remove URL encoding junk
            name = name.Replace("%3F", "");
            name = name.Replace("%20", "_");

            // 4. Final trim
            name = name.Trim('_', ' ', '.');

            if (string.IsNullOrWhiteSpace(name)) return "object";

            return name.ToLowerInvariant();
        }

        // [Existing GetFamilyName and GetPartName methods remain below for backward compatibility]
        public static string GetFamilyName(string meshName)
        {
            if (string.IsNullOrWhiteSpace(meshName))
                return "unknown";

            string name = meshName.Trim().ToLowerInvariant();

            int dot = name.IndexOf('.');
            if (dot >= 0) name = name.Substring(0, dot);

            if (!string.IsNullOrEmpty(WaterSuffix) && name.EndsWith(WaterSuffix))
                name = name.Substring(0, name.Length - WaterSuffix.Length);

            foreach (var s in CornerSuffixes)
            {
                if (name.EndsWith(s))
                {
                    name = name.Substring(0, name.Length - s.Length);
                    break;
                }
            }

            foreach (var s in SideSuffixes)
            {
                if (name.EndsWith(s))
                {
                    name = name.Substring(0, name.Length - s.Length);
                    break;
                }
            }

            int i = name.Length - 1;
            while (i >= 0 && (char.IsDigit(name[i]) || name[i] == '_'))
                i--;

            if (i >= 0) name = name.Substring(0, i + 1);

            if (string.IsNullOrWhiteSpace(name)) return "unknown";

            int us = name.IndexOf('_');
            if (us > 0) name = name.Substring(0, us);

            return name;
        }

        // Canonical part names used by GetPartName
        private static readonly string[] CanonicalPartNames =
        {
            // [Keep original list content]
            "camera_wing", "diffuser_bm", "disc_bl", "disc_br", "disc_fl", "disc_fr",
            "driver_surroun", "driver_surround_water", "frontflap_fl", "frontflap_fl_water",
            "frontflap_fr", "frontflap_fr_water", "frontspoiler_fm", "frontspoiler_fm_water",
            "frontwing_fl_01", "frontwing_fl_01_water", "frontwing_fl_02", "frontwing_fl_02_water",
            "frontwing_fr_01", "frontwing_fr_01_water", "frontwing_fr_02", "frontwing_fr_02_water",
            "hal", "hub_caliper_bl", "hub_caliper_br", "hub_caliper_fl", "hub_caliper_fr",
            "left_paddle", "right_paddle", "mesh", "mix_dial", "nosecone", "nosecone_water",
            "rearfin_bm", "rearwing_bl", "rearwing_br", "rearwing_top", "rearwing_top_water",
            "rearwingmoving_top", "rearwingmoving_top_2", "safetycell", "safetycell_water",
            "sidewing_fl", "sidewing_fl_water", "sidewing_fr", "sidewing_fr_water",
            "steering_wheel", "suspension_bl", "suspension_br", "suspension_fl", "suspension_fr",
            "suspension2_bl", "suspension2_br", "suspension2_fl", "suspension2_fr",
            "suspension3_bl", "suspension3_br", "suspension3_fl", "suspension3_fr",
            "tether_bl", "tether_br", "tether_fl", "tether_fr", "tyre_dial",
            "wheel_bl", "wheel_br", "wheel_fl", "wheel_fr", "wing_mirror_l", "wing_mirror_r"
        };

        public static string GetPartName(string meshName)
        {
            // [Keep original implementation]
            if (string.IsNullOrWhiteSpace(meshName)) return "unknown";
            string lower = meshName.ToLowerInvariant();
            string best = null;
            int bestLen = -1;
            foreach (var part in CanonicalPartNames)
            {
                int idx = lower.IndexOf(part);
                if (idx >= 0)
                {
                    if (part.Length > bestLen)
                    {
                        best = part;
                        bestLen = part.Length;
                    }
                }
            }
            if (!string.IsNullOrEmpty(best)) return best;
            return GetFamilyName(meshName);
        }

        public static readonly Dictionary<string, string> CornerSuffixToLogicalName =
            new Dictionary<string, string>
            {
                { "_fl", "FrontLeft"  }, { "_fr", "FrontRight" },
                { "_bl", "RearLeft"   }, { "_br", "RearRight"  },
                { "_l",  "Left"       }, { "_r",  "Right"      }
            };
    }
}
