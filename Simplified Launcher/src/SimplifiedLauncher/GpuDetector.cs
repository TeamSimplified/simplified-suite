using Microsoft.Win32;

namespace SimplifiedLauncher;

/// <summary>
/// Which graphics card the game is going to be running on, as far as Windows
/// knows.
///
/// The question the settings page asks is narrower than it looks: the game
/// ships a second set of shader packages for AMD cards, the <c>_amd</c> ones,
/// and the answer decides which a mod gets installed against. So this only has
/// to separate AMD from everything else.
///
/// Read from the display class key in the registry rather than through WMI,
/// which would mean taking System.Management on as a dependency for one
/// question, and rather than DXGI, which would mean interop for the same.
/// </summary>
public static class GpuDetector
{
    /// <summary>What the chips offer, and the honest third answer.</summary>
    public enum Card
    {
        /// <summary>Nothing recognisable was found. Leave the choice alone.</summary>
        Unknown,

        Amd,
        Other,
    }

    /// <summary>Where Windows keeps every display adapter it has a driver for.</summary>
    private const string DisplayClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    // PCI vendor ids, which are the thing worth matching on. The names beside
    // them are marketing and change: one machine says "AMD Radeon RX 6800",
    // another "Advanced Micro Devices, Inc.", an OEM build says something else
    // again. 1002 has meant ATI and then AMD for thirty years.
    private const string Amd = "ven_1002";
    private const string Nvidia = "ven_10de";

    /// <summary>
    /// The card to assume, or <see cref="Card.Unknown"/> when nothing says.
    ///
    /// NVIDIA beats AMD deliberately. A machine with both is nearly always an
    /// AMD processor's built-in graphics sitting beside a discrete NVIDIA card,
    /// and the game will be running on the NVIDIA one. The reverse pairing — an
    /// Intel chip beside a discrete AMD card — has no NVIDIA in it at all, so it
    /// still comes out AMD.
    /// </summary>
    public static Card Detect()
    {
        try
        {
            return Choose(Adapters());
        }
        catch
        {
            return Card.Unknown;   // a question that cannot be answered is not an error
        }
    }

    /// <summary>
    /// The rule, apart from where the ids come from, so it can be put a set of
    /// adapters and asked what it makes of them. Most of the pairings this has
    /// to get right are ones no single machine can be used to check.
    /// </summary>
    internal static Card Choose(IReadOnlyCollection<string> deviceIds)
    {
        if (deviceIds.Any(id => id.Contains(Nvidia, StringComparison.OrdinalIgnoreCase))) return Card.Other;
        if (deviceIds.Any(id => id.Contains(Amd, StringComparison.OrdinalIgnoreCase))) return Card.Amd;

        return deviceIds.Count > 0 ? Card.Other : Card.Unknown;
    }

    /// <summary>
    /// Every real display adapter's device id.
    ///
    /// Only the ones on PCI. This machine carries forty "Microsoft Remote
    /// Display Adapter" entries alongside its two real cards — Remote Desktop
    /// leaves one behind per session — and a virtual adapter says nothing about
    /// what the game will render on.
    /// </summary>
    private static List<string> Adapters()
    {
        var found = new List<string>();

        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var display = hive.OpenSubKey(DisplayClass);
        if (display is null) return found;

        foreach (var name in display.GetSubKeyNames())
        {
            // The adapters are numbered 0000, 0001 and so on; the key also holds
            // Configuration and Properties, which are not adapters.
            if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;

            try
            {
                using var adapter = display.OpenSubKey(name);
                if (adapter?.GetValue("MatchingDeviceId") is not string id) continue;
                if (!id.StartsWith("pci", StringComparison.OrdinalIgnoreCase)) continue;

                found.Add(id);
            }
            catch
            {
                // One unreadable adapter is not the whole answer.
            }
        }

        return found;
    }
}
