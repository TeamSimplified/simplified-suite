namespace SimplifiedChassisSwapper.Core.Swap;

/// <summary>Which car the swapped chassis sounds like.</summary>
public enum EngineAudioSource
{
    /// <summary>Keep the donor's engine — the audio that belongs to the model being kept.</summary>
    Chassis,

    /// <summary>Take the slot's engine, so the car sounds like the team it's replacing.</summary>
    Team,

    /// <summary>Use a specific engine from the install, named by <see cref="ChassisSwapOptions.EngineAudioPick"/>.</summary>
    Explicit,
}

/// <summary>
/// Inputs for a chassis swap. The swap takes a <b>chassis</b> ERP (the car
/// model/physics you want to keep), re-badges it under a <b>new name</b>, and
/// grafts on the <b>team</b>'s camera/wep setup so the game loads it as that
/// team's car.
/// </summary>
public sealed class ChassisSwapOptions
{
    /// <summary>The donor car — its geometry, textures and camera values are kept.</summary>
    public required string ChassisErpPath { get; init; }

    /// <summary>The slot being filled — its <c>/wep/</c> resources define how the game finds the car.</summary>
    public required string TeamErpPath { get; init; }

    /// <summary>
    /// Driver in-car animation ERP (<c>incardriver_f1.erp</c>). Auto-detected from
    /// <see cref="GameDir"/> when not set. Optional: without it the animset step is skipped.
    /// </summary>
    public string? IncardriverErpPath { get; init; }

    /// <summary>Token to replace throughout the chassis, e.g. <c>force_india</c>.</summary>
    public required string OldName { get; init; }

    /// <summary>
    /// Replacement token, e.g. <c>ff25monster</c>. Must be the same length as
    /// <see cref="OldName"/> — fragment payloads are patched by equal-length byte
    /// replacement, so a length change would corrupt them.
    /// </summary>
    public required string NewName { get; init; }

    /// <summary>
    /// Game install root, used to resolve external <c>.mipmaps</c> files so textures
    /// can be embedded at full resolution. Without it textures keep only their
    /// low-res in-ERP base image.
    /// </summary>
    public string? GameDir { get; init; }

    /// <summary>Where the swapped ERP is written.</summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// Which engine sound the swapped car uses. Defaults to the chassis's, since
    /// the audio belongs to the model being kept.
    /// </summary>
    public EngineAudioSource EngineAudio { get; init; } = EngineAudioSource.Chassis;

    /// <summary>
    /// The engine to use when <see cref="EngineAudio"/> is
    /// <see cref="EngineAudioSource.Explicit"/>. Ignored otherwise; falling back
    /// to the chassis's own audio if it's missing.
    /// </summary>
    public EngineAudioCatalogue.EngineAudioEntry? EngineAudioPick { get; init; }

    /// <summary>
    /// Explicit chassis animset name (e.g. <c>driver_in_car_2025_forceindia</c>) —
    /// the animations that get used. Normally resolved automatically; set to
    /// override when the automatic match picks the wrong one.
    /// </summary>
    public string? ChassisAnimsetOverride { get; init; }

    /// <summary>
    /// Explicit team animset name — the identifier the animations are published
    /// under. Same story as <see cref="ChassisAnimsetOverride"/>: automatic
    /// unless overridden.
    /// </summary>
    public string? TeamAnimsetOverride { get; init; }

    /// <summary>
    /// Give this car's own textures identifiers nobody else will have.
    ///
    /// Two mods built for the same team otherwise name their textures
    /// identically — the names come from the team, not from the mod — so
    /// installing both means one quietly winning. A tag in the texture path
    /// makes each swap's textures its own.
    ///
    /// Only the car's own textures move. Anything it shares with the rest of
    /// the game keeps the name the rest of the game knows it by.
    /// </summary>
    public bool UniqueTextureNames { get; init; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OldName) || string.IsNullOrWhiteSpace(NewName))
            throw new InvalidOperationException("Both the old and new name are required.");
        if (OldName.Length != NewName.Length)
            throw new InvalidOperationException(
                $"'{OldName}' ({OldName.Length}) and '{NewName}' ({NewName.Length}) must be the same length — " +
                "fragment data is patched in place by equal-length byte replacement.");
        if (!File.Exists(ChassisErpPath))
            throw new FileNotFoundException("Chassis ERP not found.", ChassisErpPath);
        if (!File.Exists(TeamErpPath))
            throw new FileNotFoundException("Team ERP not found.", TeamErpPath);
        if (string.IsNullOrWhiteSpace(OutputPath))
            throw new InvalidOperationException("Output path is required.");
    }
}
