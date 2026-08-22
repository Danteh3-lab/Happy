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
    bool IncludeTelemetryProbe,
    Rectangle? MarkerGraceRoi = null,
    long MarkerLossAgeMs = 0,
    CombatDirection GraceDirection = CombatDirection.None,
    bool MarkerGraceScan = false,
    long TrackingGraceAgeMs = 0,
    FlashTemporalBaseline TemporalBaseline = null);

/// <summary>Result of a scan, including the diagnostic red-match probe.</summary>
internal sealed record VisionAnalysisResult(CombatObservation Observation, ColorProbe RedProbe);

/// <summary>
/// The luminance state captured when a directional candidate arms.  It is used
/// for telemetry-only flash calibration and is never a live action gate.
/// </summary>
internal sealed record FlashTemporalBaseline(Rectangle Region, Rectangle ExcludedIndicatorRegion, byte[] Luminance);

/// <summary>
/// Reads combat pixels from an already-captured frame.  ScreenCapture remains
/// responsible only for acquiring and indexing pixels; this class owns the
/// exact combat thresholds, ROI clipping, and directional half-plane rules.
/// </summary>
internal sealed class VisionAnalyzer
{
    internal const int FlashClusterTolerance = 24;
    internal const int FlashClusterMinimumMatches = 4;

    public VisionAnalysisResult Scan(ScreenFrame frame, VisionScanRequest request)
    {
        long trackingGraceAgeMs = request.TrackingGraceAgeMs != 0
            ? request.TrackingGraceAgeMs
            : request.MarkerLossAgeMs;
        bool candidateGrace = request.MarkerGraceScan &&
            request.MarkerGraceRoi is Rectangle &&
            trackingGraceAgeMs <= ReactionCoordinator.MissingGraceMs;
        bool markerGrace = candidateGrace && !request.MarkerFound;
        if (!request.MarkerFound && !candidateGrace)
        {
            return Empty(frame, request, new Point(-1, -1), Rectangle.Empty);
        }

        Rectangle configuredRoi = candidateGrace ? request.MarkerGraceRoi!.Value : request.CombatRoi;
        Rectangle roi = Rectangle.Intersect(configuredRoi, request.ScreenBounds);
        if (roi.Width <= 0 || roi.Height <= 0)
        {
            return Empty(frame, request, new Point(-1, -1), roi);
        }

        // Tolerant cluster scans are calibration evidence only. They are a
        // full-ROI pass, so do not spend that time during normal live play.
        // The exact strict pixel below remains the live parry gate in both
        // modes.
        bool collectFlashDiagnostics = request.IncludeTelemetryProbe || request.TemporalBaseline != null;
        FlashClusterMetric flashCluster = collectFlashDiagnostics
            ? FindFlashCluster(frame, roi)
            : default;
        FlashClusterMetric indicatorFlashCluster = !collectFlashDiagnostics || request.TemporalBaseline == null
            ? default
            : FindFlashCluster(frame, request.TemporalBaseline.ExcludedIndicatorRegion);
        TemporalFlashMetric temporalFlash = CountTemporalFlash(frame, request.TemporalBaseline);
        bool strictFlash = frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1,
            255, 154, 141, 0, out int strictFlashX, out int strictFlashY);
        Point strictFlashPoint = strictFlash ? new Point(strictFlashX, strictFlashY) : new Point(-1, -1);
        if (candidateGrace)
        {
            VisionScanMode mode = markerGrace ? VisionScanMode.MarkerGrace : VisionScanMode.AnchorGrace;
            return new VisionAnalysisResult(
                new CombatObservation(request.TimestampMs, false, request.Anchor, request.Box, roi,
                    false, new Point(-1, -1), request.GraceDirection, false,
                    strictFlash, false, false,
                    request.EHeld, request.FHeld, request.LtHeld, request.InputReady,
                    request.SourceHeavyHeld, request.SourceLightHeld,
                    mode, configuredRoi, request.MarkerLossAgeMs,
                    flashCluster.MatchCount, temporalFlash.MatchCount, temporalFlash.LargestCluster,
                    trackingGraceAgeMs, strictFlashPoint, indicatorFlashCluster.MatchCount,
                    indicatorFlashCluster.Bounds),
                new ColorProbe(0, -1, -1, "n/a", -1));
        }

        bool red = frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1,
            255, 49, 41, 2, out int indicatorX, out int indicatorY);
        bool darkRed = frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1,
            255, 41, 34, 0, out _, out _);
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
                indicator, direction, darkRed, strictFlash, orange, orangeFeint,
                request.EHeld, request.FHeld, request.LtHeld, request.InputReady,
                request.SourceHeavyHeld, request.SourceLightHeld,
                VisionScanMode.Tracked, configuredRoi, 0, flashCluster.MatchCount,
                temporalFlash.MatchCount, temporalFlash.LargestCluster, 0, strictFlashPoint,
                indicatorFlashCluster.MatchCount, indicatorFlashCluster.Bounds), probe);
    }

    private static VisionAnalysisResult Empty(ScreenFrame frame, VisionScanRequest request,
        Point indicator, Rectangle roi)
    {
        return new VisionAnalysisResult(
            new CombatObservation(request.TimestampMs, request.MarkerFound, request.Anchor, request.Box,
                request.MarkerFound ? roi : request.CombatRoi, false, indicator, CombatDirection.None,
                false, false, false, false, request.EHeld, request.FHeld, request.LtHeld,
                request.InputReady, request.SourceHeavyHeld, request.SourceLightHeld,
                VisionScanMode.Tracked, request.CombatRoi, request.MarkerLossAgeMs, 0,
                0, 0, request.TrackingGraceAgeMs != 0 ? request.TrackingGraceAgeMs : request.MarkerLossAgeMs),
            new ColorProbe(0, -1, -1, "n/a", -1));
    }

    private static FlashClusterMetric FindFlashCluster(ScreenFrame frame, Rectangle screenRegion)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Buffer == null) return default;

        int left = Math.Clamp(screenRegion.Left - frame.OriginX, 0, frame.Width - 1);
        int right = Math.Clamp(screenRegion.Right - 1 - frame.OriginX, 0, frame.Width - 1);
        int top = Math.Clamp(screenRegion.Top - frame.OriginY, 0, frame.Height - 1);
        int bottom = Math.Clamp(screenRegion.Bottom - 1 - frame.OriginY, 0, frame.Height - 1);
        if (right < left || bottom < top) return default;

        int matches = 0;
        int minimumX = int.MaxValue, minimumY = int.MaxValue;
        int maximumX = int.MinValue, maximumY = int.MinValue;
        for (int y = top; y <= bottom; y++)
        {
            int row = y * frame.Stride;
            for (int x = left; x <= right; x++)
            {
                int offset = row + x * 4;
                if (Math.Abs(frame.Buffer[offset + 2] - 255) <= FlashClusterTolerance &&
                    Math.Abs(frame.Buffer[offset + 1] - 154) <= FlashClusterTolerance &&
                    Math.Abs(frame.Buffer[offset] - 141) <= FlashClusterTolerance)
                {
                    matches++;
                    int screenX = x + frame.OriginX;
                    int screenY = y + frame.OriginY;
                    minimumX = Math.Min(minimumX, screenX);
                    minimumY = Math.Min(minimumY, screenY);
                    maximumX = Math.Max(maximumX, screenX);
                    maximumY = Math.Max(maximumY, screenY);
                }
            }
        }
        return matches == 0
            ? default
            : new FlashClusterMetric(matches, Rectangle.FromLTRB(minimumX, minimumY, maximumX + 1, maximumY + 1));
    }

    internal static FlashTemporalBaseline CaptureTemporalBaseline(ScreenFrame frame, Rectangle screenRegion,
        Point indicator)
    {
        Rectangle frameBounds = new(frame.OriginX, frame.OriginY, frame.Width, frame.Height);
        Rectangle region = Rectangle.Intersect(screenRegion, frameBounds);
        if (region.Width <= 0 || region.Height <= 0 || frame.Buffer == null)
            return null;

        byte[] luminance = new byte[region.Width * region.Height];
        for (int y = 0; y < region.Height; y++)
        {
            int sourceRow = (region.Top - frame.OriginY + y) * frame.Stride;
            for (int x = 0; x < region.Width; x++)
            {
                int offset = sourceRow + (region.Left - frame.OriginX + x) * 4;
                luminance[y * region.Width + x] = Luminance(frame.Buffer[offset + 2], frame.Buffer[offset + 1], frame.Buffer[offset]);
            }
        }

        // The red directional widget produces a very high tolerant-colour
        // count.  Leave a generous square around its armed position out of
        // temporal calibration so it cannot masquerade as a flash bloom.
        Rectangle excluded = Rectangle.FromLTRB(indicator.X - 90, indicator.Y - 90,
            indicator.X + 91, indicator.Y + 91);
        return new FlashTemporalBaseline(region, excluded, luminance);
    }

    private static TemporalFlashMetric CountTemporalFlash(ScreenFrame frame, FlashTemporalBaseline baseline)
    {
        if (baseline == null || baseline.Luminance == null || frame.Buffer == null)
            return default;

        Rectangle screenRegion = baseline.Region;
        Rectangle frameBounds = new(frame.OriginX, frame.OriginY, frame.Width, frame.Height);
        if (!frameBounds.Contains(screenRegion) ||
            baseline.Luminance.Length != screenRegion.Width * screenRegion.Height)
            return default;

        int matches = 0;
        int largestCluster = 0;
        int[] previousRowRuns = new int[screenRegion.Width];
        int[] currentRowRuns = new int[screenRegion.Width];
        for (int y = 0; y < screenRegion.Height; y++)
        {
            Array.Clear(currentRowRuns, 0, currentRowRuns.Length);
            int sourceRow = (screenRegion.Top - frame.OriginY + y) * frame.Stride;
            for (int x = 0; x < screenRegion.Width; x++)
            {
                int screenX = screenRegion.Left + x;
                int screenY = screenRegion.Top + y;
                int offset = sourceRow + (screenRegion.Left - frame.OriginX + x) * 4;
                int index = y * screenRegion.Width + x;
                int red = frame.Buffer[offset + 2];
                int green = frame.Buffer[offset + 1];
                int blue = frame.Buffer[offset];
                int current = Luminance(red, green, blue);
                int prior = baseline.Luminance[index];
                baseline.Luminance[index] = (byte)current;
                if (baseline.ExcludedIndicatorRegion.Contains(screenX, screenY) ||
                    current < 190 || current - prior < 55 || red < 220 || green < 145 || blue < 45)
                    continue;

                matches++;
                int above = previousRowRuns[x];
                int left = x == 0 ? 0 : currentRowRuns[x - 1];
                int component = Math.Max(1, Math.Max(above, left) + 1);
                currentRowRuns[x] = component;
                if (component > largestCluster) largestCluster = component;
            }
            (previousRowRuns, currentRowRuns) = (currentRowRuns, previousRowRuns);
        }
        return new TemporalFlashMetric(matches, largestCluster);
    }

    private static byte Luminance(int red, int green, int blue) =>
        (byte)((2126 * red + 7152 * green + 722 * blue) / 10000);

    private readonly record struct TemporalFlashMetric(int MatchCount, int LargestCluster);

    /// <summary>Calibration-only tolerant peach-colour measure.</summary>
    private readonly record struct FlashClusterMetric(int MatchCount, Rectangle Bounds);

    private static CombatDirection ClassifyDirection(int x, int y, VisionScanRequest request)
    {
        if (x > request.RightX && y > request.RightY) return CombatDirection.Right;
        if (x < request.LeftX && y > request.RightY) return CombatDirection.Left;
        if (y > request.TopLeftY && y < request.TopRightY) return CombatDirection.Top;
        return CombatDirection.None;
    }
}
