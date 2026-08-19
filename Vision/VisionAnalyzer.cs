using System.Drawing;
using HappyBot.Combat;

namespace HappyBot.Vision;

/// <summary>Inputs required for one combat-region scan.</summary>
internal sealed record VisionScanRequest(
    long TimestampMs,
    bool MarkerFound,
    Point Anchor,
    int Box,
    Rectangle CombatRoi,
    double TopLeftX,
    double TopLeftY,
    double TopRightX,
    double TopRightY,
    double RightX,
    double RightY,
    double LeftX,
    double LeftY,
    Rectangle ScreenBounds,
    bool EHeld,
    bool FHeld,
    bool LtHeld,
    bool InputReady,
    bool SourceHeavyHeld,
    bool SourceLightHeld,
    bool IncludeTelemetryProbe);

/// <summary>Result of a scan, including the diagnostic red-match probe.</summary>
internal sealed record VisionAnalysisResult(CombatObservation Observation, ColorProbe RedProbe);

/// <summary>
/// Reads combat pixels from an already-captured frame.  ScreenCapture remains
/// responsible only for acquiring and indexing pixels; this class owns the
/// exact combat thresholds, ROI clipping, and directional half-plane rules.
/// </summary>
internal sealed class VisionAnalyzer
{
    public VisionAnalysisResult Scan(ScreenFrame frame, VisionScanRequest request)
    {
        if (!request.MarkerFound)
        {
            return Empty(frame, request, new Point(-1, -1), Rectangle.Empty);
        }

        Rectangle roi = Rectangle.Intersect(request.CombatRoi, request.ScreenBounds);
        if (roi.Width <= 0 || roi.Height <= 0)
        {
            return Empty(frame, request, new Point(-1, -1), roi);
        }

        bool red = frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1,
            255, 49, 41, 2, out int indicatorX, out int indicatorY);
        bool darkRed = frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1,
            255, 41, 34, 0, out _, out _);
        bool lightFlash = frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1,
            255, 154, 141, 0, out _, out _);
        bool orange = frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1,
            246, 98, 8, 0, out _, out _);
        bool orangeFeint = frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1,
            255, 34, 28, 3, out _, out _);

        Point indicator = red ? new Point(indicatorX, indicatorY) : new Point(-1, -1);
        CombatDirection direction = red
            ? ClassifyDirection(indicator.X, indicator.Y, request)
            : CombatDirection.None;
        ColorProbe probe = request.IncludeTelemetryProbe
            ? frame.ProbeColor(roi.Left - frame.OriginX, roi.Top - frame.OriginY,
                roi.Right - 1 - frame.OriginX, roi.Bottom - 1 - frame.OriginY,
                255, 49, 41, 2)
            : new ColorProbe(0, -1, -1, "n/a", -1);

        return new VisionAnalysisResult(
            new CombatObservation(request.TimestampMs, true, request.Anchor, request.Box, roi, red,
                indicator, direction, darkRed, lightFlash, orange, orangeFeint,
                request.EHeld, request.FHeld, request.LtHeld, request.InputReady,
                request.SourceHeavyHeld, request.SourceLightHeld), probe);
    }

    private static VisionAnalysisResult Empty(ScreenFrame frame, VisionScanRequest request,
        Point indicator, Rectangle roi)
    {
        return new VisionAnalysisResult(
            new CombatObservation(request.TimestampMs, request.MarkerFound, request.Anchor, request.Box,
                request.MarkerFound ? roi : request.CombatRoi, false, indicator, CombatDirection.None,
                false, false, false, false, request.EHeld, request.FHeld, request.LtHeld,
                request.InputReady, request.SourceHeavyHeld, request.SourceLightHeld),
            new ColorProbe(0, -1, -1, "n/a", -1));
    }

    private static CombatDirection ClassifyDirection(int x, int y, VisionScanRequest request)
    {
        if (x > request.RightX && y > request.RightY) return CombatDirection.Right;
        if (x < request.LeftX && y > request.RightY) return CombatDirection.Left;
        if (y > request.TopLeftY && y < request.TopRightY) return CombatDirection.Top;
        return CombatDirection.None;
    }
}
