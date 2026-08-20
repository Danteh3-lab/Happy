using System.Drawing;

namespace HappyBot;

/// <summary>
/// A single, immutable view of the detection geometry and decision state used by
/// the diagnostic overlay. Coordinates are in primary-screen pixels.
/// </summary>
public sealed class VisionSnapshot
{
    public bool Running { get; init; }
    public bool MarkerFound { get; init; }
    public bool RawMarkerFound { get; init; }
    public bool TrackingUsable { get; init; }
    public bool TrackingStale { get; init; }
    public string MarkerKind { get; init; } = "NONE";
    public Point Anchor { get; init; } = new(-1, -1);
    public RectangleF AnchorScan { get; init; }
    public RectangleF CombatRoi { get; init; }
    public RectangleF TopZone { get; init; }
    public RectangleF LeftZone { get; init; }
    public RectangleF RightZone { get; init; }
    public bool AttackIndicator { get; init; }
    public Point Indicator { get; init; } = new(-1, -1);
    public string GuardDirection { get; init; } = "-";
    public string DecisionDirection { get; init; } = "";
    public string ReactionState { get; init; } = "SEARCHING";
    public string ReactionReason { get; init; } = "Waiting for an anchor";
    public bool Flash { get; init; }
    public int LoopHz { get; init; }
    public int Box { get; init; }
    public long AnchorAgeMs { get; init; }
    public long MarkerAgeMs { get; init; } = -1;
    public long LastMarkerSeenMs { get; init; } = -1;
    public long TrackingVersion { get; init; }
    public int AnchorDeltaX { get; init; }
    public int AnchorDeltaY { get; init; }
    public long GuardRemainingMs { get; init; }
    public long ReactionWaitMs { get; init; }
    public long CandidateId { get; init; }
    public long CandidateAgeMs { get; init; }
    public long CandidateLastValidAgeMs { get; init; }
    public string ActionWorkerState { get; init; } = "IDLE";
    public string LegitParryStatus { get; init; } = "LEGIT OFF";
    public bool TelemetryRecording { get; init; }
}
