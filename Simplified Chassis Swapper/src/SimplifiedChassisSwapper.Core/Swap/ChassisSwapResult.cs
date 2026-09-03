namespace SimplifiedChassisSwapper.Core.Swap;

/// <summary>Per-step tallies from a swap, for the UI summary and for tests to assert on.</summary>
public sealed class ChassisSwapResult
{
    public int ResourcesIn { get; set; }
    public int ResourcesOut { get; set; }

    /// <summary>Textures whose external mip chain was embedded (3 fragments → 2).</summary>
    public int TexturesEmbedded { get; set; }
    /// <summary>Textures that had external mips we couldn't reach (kept base image only).</summary>
    public int TexturesMipsUnreachable { get; set; }

    public int IdentifiersRenamed { get; set; }
    public int FragmentsPatched { get; set; }

    public int RemovedVtf { get; set; }
    public int RemovedWep { get; set; }

    public int LayersRebuilt { get; set; }
    public int WepCopied { get; set; }

    /// <summary>
    /// The Wwise switch the output ended up with — what the car will sound like.
    /// </summary>
    public string? EngineAudioSwitch { get; set; }

    public bool AnimsetTransplanted { get; set; }
    public string? ChassisAnimset { get; set; }
    public string? TeamAnimset { get; set; }

    public List<string> Warnings { get; } = new();

    public string OutputPath { get; set; } = "";
}
