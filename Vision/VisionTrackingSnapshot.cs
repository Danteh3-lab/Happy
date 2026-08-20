using System.Drawing;

namespace HappyBot.Vision;

/// <summary>
/// One immutable publication of marker state and all geometry derived from it.
/// Marker freshness is evaluated from TickCount64, never from wall-clock time.
/// </summary>
internal sealed record VisionTrackingSnapshot
{
    public const int TrackingUsableWindowMs = 150;

    public long Version { get; init; }
    public long TimestampMs { get; init; }
    public bool RawMarkerFound { get; init; }
    public string MarkerKind { get; init; } = "NONE";
    public string LastMarkerKind { get; init; } = "NONE";
    public Point Anchor { get; init; } = new(-1, -1);
    public int Box { get; init; }
    public VisionGeometry Geometry { get; init; } = VisionGeometry.Empty;
    public long LastSeenMs { get; init; } = -1;
    public int AnchorDeltaX { get; init; }
    public int AnchorDeltaY { get; init; }
    public long MarkerAgeMs { get; init; } = -1;
    public bool TrackingUsable { get; init; }
    public bool TrackingStale { get; init; }

    public static VisionTrackingSnapshot Empty => new();

    public static VisionTrackingSnapshot Create(
        long version,
        long now,
        bool rawMarkerFound,
        string markerKind,
        Point anchor,
        int box,
        VisionGeometry geometry,
        long lastSeenMs,
        int anchorDeltaX,
        int anchorDeltaY,
        string lastMarkerKind = null)
    {
        long markerAge = AgeAt(lastSeenMs, now);
        bool usable = lastSeenMs >= 0 && markerAge <= TrackingUsableWindowMs;
        return new VisionTrackingSnapshot
        {
            Version = version,
            TimestampMs = now,
            RawMarkerFound = rawMarkerFound,
            MarkerKind = markerKind ?? "NONE",
            LastMarkerKind = lastMarkerKind ?? markerKind ?? "NONE",
            Anchor = anchor,
            Box = box,
            Geometry = geometry ?? VisionGeometry.Empty,
            LastSeenMs = lastSeenMs,
            AnchorDeltaX = anchorDeltaX,
            AnchorDeltaY = anchorDeltaY,
            MarkerAgeMs = markerAge,
            TrackingUsable = usable,
            TrackingStale = !rawMarkerFound && usable
        };
    }

    public VisionTrackingSnapshot At(long now)
    {
        long markerAge = AgeAt(LastSeenMs, now);
        bool usable = LastSeenMs >= 0 && markerAge <= TrackingUsableWindowMs;
        return this with
        {
            TimestampMs = now,
            MarkerAgeMs = markerAge,
            TrackingUsable = usable,
            TrackingStale = !RawMarkerFound && usable
        };
    }

    private static long AgeAt(long lastSeenMs, long now) =>
        lastSeenMs < 0 ? -1 : Math.Max(0, now - lastSeenMs);
}
