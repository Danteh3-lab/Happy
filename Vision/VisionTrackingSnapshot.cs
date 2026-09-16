using System.Drawing;
using HappyBot.Vision;

namespace HappyBot;

/// <summary>
/// Immutable tracking state for one published frame. Consumers must use this
/// tuple instead of reading the mutable CombatGeometry fields independently;
/// that keeps anchor, Box, resolution, ROI, and directional zones coherent.
/// </summary>
internal sealed record VisionTrackingSnapshot
{
    public static VisionTrackingSnapshot Empty { get; } = new()
    {
        MarkerKind = "NONE",
        Anchor = new Point(-1, -1),
        AnchorScan = Rectangle.Empty,
        BoxScan = Rectangle.Empty,
        CombatRoi = Rectangle.Empty,
        TopZone = RectangleF.Empty,
        RightZone = RectangleF.Empty,
        LeftZone = RectangleF.Empty
    };

    public long Version { get; init; }
    public long TimestampMs { get; init; }
    public bool MarkerFound { get; init; }
    public string MarkerKind { get; init; } = "NONE";
    public Point Anchor { get; init; } = new(-1, -1);
    public int Box { get; init; }
    public double B55 { get; init; }
    public double Y55 { get; init; }
    public double X2 { get; init; }
    public double Y2 { get; init; }
    public double X3 { get; init; }
    public double Y3 { get; init; }
    public double X4 { get; init; }
    public double Y4 { get; init; }
    public double X5 { get; init; }
    public double Y5 { get; init; }
    public double X6 { get; init; }
    public double Y6 { get; init; }
    public double X7 { get; init; }
    public double Y7 { get; init; }
    public Rectangle AnchorScan { get; init; }
    public Rectangle BoxScan { get; init; }
    public Rectangle CombatRoi { get; init; }
    public RectangleF TopZone { get; init; }
    public RectangleF RightZone { get; init; }
    public RectangleF LeftZone { get; init; }
    public AnchorMarkerScanResult MarkerScan { get; init; }

    public static VisionTrackingSnapshot From(CombatGeometry geometry, bool markerFound,
        string markerKind, long timestampMs, long version, AnchorMarkerScanResult markerScan)
    {
        Rectangle combatRoi = geometry.CombatRoi();
        (RectangleF top, RectangleF right, RectangleF left) = geometry.Zones(combatRoi);
        return new VisionTrackingSnapshot
        {
            Version = version,
            TimestampMs = timestampMs,
            MarkerFound = markerFound,
            MarkerKind = markerKind ?? "NONE",
            Anchor = new Point(geometry.Ax, geometry.Ay),
            Box = geometry.Box,
            B55 = geometry.B55,
            Y55 = geometry.Y55,
            X2 = geometry.X2,
            Y2 = geometry.Y2,
            X3 = geometry.X3,
            Y3 = geometry.Y3,
            X4 = geometry.X4,
            Y4 = geometry.Y4,
            X5 = geometry.X5,
            Y5 = geometry.Y5,
            X6 = geometry.X6,
            Y6 = geometry.Y6,
            X7 = geometry.X7,
            Y7 = geometry.Y7,
            AnchorScan = geometry.AnchorScan(),
            BoxScan = geometry.BoxScan(),
            CombatRoi = combatRoi,
            TopZone = top,
            RightZone = right,
            LeftZone = left,
            MarkerScan = markerScan
        };
    }
}
