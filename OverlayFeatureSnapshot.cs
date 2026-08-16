namespace HappyBot;

/// <summary>
/// Effective feature state used by the overlay's vertical module list.
/// This is separate from VisionSnapshot so the geometry snapshot remains
/// compatible with the existing diagnostic overlay data.
/// </summary>
public sealed class OverlayFeatureSnapshot
{
    public bool AutoBlock { get; init; }
    public bool AutoParry { get; init; }
    public bool AutoCrushing { get; init; }
    public bool AutoDeflect { get; init; }
    public bool AutoDodge { get; init; }
    public bool OrangeLight { get; init; }
    public bool OrangeParry { get; init; }
    public bool Legit { get; init; }
    public int LegitChance { get; init; }
    public bool BulwarkFallback { get; init; }
    public bool Telemetry { get; init; }
}
