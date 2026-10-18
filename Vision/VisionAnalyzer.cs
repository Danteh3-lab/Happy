using System.Drawing;
using HappyBot;
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
    bool FHeld,
    bool InputReady,
    bool SourceHeavyHeld,
    bool SourceLightHeld,
    bool IncludeTelemetryProbe,
    Rectangle? MarkerGraceRoi = null,
    long MarkerLossAgeMs = 0,
    CombatDirection GraceDirection = CombatDirection.None,
    bool MarkerGraceScan = false,
    long TrackingGraceAgeMs = 0,
    FlashTemporalBaseline TemporalBaseline = null,
    VisionTrackingSnapshot Tracking = null,
    double ResolutionScale = 1.0,
    Rectangle? FlashRegion = null);

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

    /// <summary>
    /// The flash renders a point or two off 255,154,141 on most frames, so an
    /// exact pixel fired late or not at all (Oct 17 training: 6 of 20 lights
    /// flashed with dozens of near pixels and no exact one). Live parry/deflect
    /// gating takes a small cluster within this per-channel tolerance instead.
    /// </summary>
    internal const int LiveFlashTolerance = 2;
    internal const int LiveFlashMinimumPixels = 3;

    /// <summary>
    /// Red pixels closer than this (at 1080p) belong to one indicator. The
    /// indicator often renders as two or three nearby pieces; Oct 17 training
    /// frames join into one 65-570 pixel cluster at this distance.
    /// </summary>
    internal const int RedClusterCellPx = 12;

    /// <summary>
    /// Smallest red cluster (at 1080p) treated as an indicator. Real
    /// indicators measured 43+ pixels on the frame they armed; stray red from
    /// effects and the map measured 1-10.
    /// </summary>
    internal const int RedClusterMinimumPixels = 24;

    /// <summary>
    /// Half-size (at 1080p) of the square around the indicator in which the
    /// flash must land. The flash renders on the indicator itself: on Oct 17,
    /// 441 of 442 accepted flash pixels fell inside this square.
    /// </summary>
    internal const int IndicatorRegionHalfPx = 90;

    private const int MaxPixelsPerSignal = 4096;
    private readonly List<Point> _redPixels = new(512);
    private readonly List<Point> _flashPixels = new(256);

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
        LiveSignalMetric live = ScanLiveSignals(frame, roi, request.IncludeTelemetryProbe);
        RedSelection redSelection = candidateGrace ? RedSelection.None : SelectRed(request);
        Rectangle? flashRegion = request.FlashRegion ??
            (redSelection.Found ? IndicatorRegion(redSelection.Indicator, request.ResolutionScale) : null);
        (bool strictFlash, Point strictFlashPoint) = CountStrictFlash(flashRegion);
        if (candidateGrace)
        {
            VisionScanMode mode = markerGrace ? VisionScanMode.MarkerGrace : VisionScanMode.AnchorGrace;
            return new VisionAnalysisResult(
                new CombatObservation(request.TimestampMs, false, request.Anchor, request.Box, roi,
                    false, new Point(-1, -1), request.GraceDirection, false,
                    strictFlash, false, false,
                    request.FHeld, request.InputReady,
                    request.SourceHeavyHeld, request.SourceLightHeld,
                    mode, configuredRoi, request.MarkerLossAgeMs,
                    flashCluster.MatchCount, temporalFlash.MatchCount, temporalFlash.LargestCluster,
                    trackingGraceAgeMs, strictFlashPoint, indicatorFlashCluster.MatchCount,
                    indicatorFlashCluster.Bounds) with { Tracking = request.Tracking },
                new ColorProbe(0, -1, -1, "n/a", -1));
        }

        return new VisionAnalysisResult(
            new CombatObservation(request.TimestampMs, true, request.Anchor, request.Box, roi, redSelection.Found,
                redSelection.Indicator, redSelection.Direction, live.DarkRed, strictFlash, live.Orange, live.OrangeFeint,
                request.FHeld, request.InputReady,
                request.SourceHeavyHeld, request.SourceLightHeld,
                VisionScanMode.Tracked, configuredRoi, 0, flashCluster.MatchCount,
                temporalFlash.MatchCount, temporalFlash.LargestCluster, 0, strictFlashPoint,
                indicatorFlashCluster.MatchCount, indicatorFlashCluster.Bounds) with
            {
                Tracking = request.Tracking,
                RedAmbiguous = redSelection.Ambiguous,
                RedClusters = redSelection.Clusters
            }, live.RedProbe);
    }

    /// <summary>The square around an indicator in which its flash must land.</summary>
    internal static Rectangle IndicatorRegion(Point indicator, double resolutionScale)
    {
        int half = Math.Max(1, (int)Math.Round(IndicatorRegionHalfPx * resolutionScale));
        return Rectangle.FromLTRB(indicator.X - half, indicator.Y - half,
            indicator.X + half + 1, indicator.Y + half + 1);
    }

    /// <summary>
    /// Groups this frame's red pixels and keeps clusters large enough to be an
    /// indicator. One directional cluster is the threat; clusters pointing in
    /// different directions mean two attackers whose owner cannot be told
    /// apart, so the scan reports no indicator and flags the frame instead.
    /// </summary>
    private RedSelection SelectRed(VisionScanRequest request)
    {
        if (_redPixels.Count == 0) return RedSelection.None;

        double scale = request.ResolutionScale > 0 ? request.ResolutionScale : 1.0;
        int cell = Math.Max(2, (int)Math.Round(RedClusterCellPx * scale));
        int minimum = Math.Max(6, (int)Math.Round(RedClusterMinimumPixels * scale * scale));
        List<PixelCluster> clusters = PixelClusters.Group(_redPixels, cell)
            .Where(cluster => cluster.PixelCount >= minimum)
            .OrderByDescending(cluster => cluster.PixelCount)
            .ToList();
        if (clusters.Count == 0) return RedSelection.None;

        var threats = clusters
            .Select(cluster => (cluster, direction: ClassifyDirection(cluster.Center.X, cluster.Center.Y, request)))
            .Where(threat => threat.direction != CombatDirection.None)
            .ToList();
        if (threats.Select(threat => threat.direction).Distinct().Count() > 1)
            return new RedSelection(false, new Point(-1, -1), CombatDirection.None, true, clusters);

        // A lone non-directional cluster is still reported as an indicator
        // with no direction, matching how a stray red pixel used to show.
        PixelCluster chosen = threats.Count > 0 ? threats[0].cluster : clusters[0];
        CombatDirection direction = threats.Count > 0 ? threats[0].direction : CombatDirection.None;
        return new RedSelection(true, chosen.Center, direction, false, clusters);
    }

    /// <summary>
    /// The live parry/deflect gate: enough flash pixels inside the owned
    /// indicator's square, or anywhere in the ROI when no indicator is owned.
    /// </summary>
    private (bool StrictFlash, Point StrictFlashPoint) CountStrictFlash(Rectangle? region)
    {
        int count = 0;
        Point firstPoint = new(-1, -1);
        foreach (Point pixel in _flashPixels)
        {
            if (region is Rectangle owned && !owned.Contains(pixel)) continue;
            if (count == 0) firstPoint = pixel;
            if (++count >= LiveFlashMinimumPixels) return (true, firstPoint);
        }
        return (false, new Point(-1, -1));
    }

    /// <summary>
    /// Performs the live color predicates in one row-major pass.  The old
    /// implementation walked the same combat ROI once per color, which made
    /// capture/vision cost scale with the number of indicators.  Dark red and
    /// orange record their first match; red and flash pixels are collected so
    /// they can be grouped and tied to one indicator afterwards.
    /// </summary>
    private LiveSignalMetric ScanLiveSignals(ScreenFrame frame, Rectangle screenRegion,
        bool includeProbe)
    {
        _redPixels.Clear();
        _flashPixels.Clear();
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Buffer == null)
            return LiveSignalMetric.Empty;

        Rectangle frameBounds = new(frame.OriginX, frame.OriginY, frame.Width, frame.Height);
        Rectangle region = Rectangle.Intersect(screenRegion, frameBounds);
        if (region.Width <= 0 || region.Height <= 0)
            return LiveSignalMetric.Empty;

        bool darkRed = false, orange = false, orangeFeint = false;
        int probeCount = 0;
        int firstProbeX = -1, firstProbeY = -1;
        int closestDistance = int.MaxValue;
        int closestR = 0, closestG = 0, closestB = 0;
        for (int y = region.Top; y < region.Bottom; y++)
        {
            int localY = y - frame.OriginY;
            int row = localY * frame.Stride;
            for (int x = region.Left; x < region.Right; x++)
            {
                int localX = x - frame.OriginX;
                int offset = row + localX * 4;
                int pixelB = frame.Buffer[offset];
                int pixelG = frame.Buffer[offset + 1];
                int pixelR = frame.Buffer[offset + 2];

                if (includeProbe)
                {
                    int distance = Math.Abs(pixelR - 255) + Math.Abs(pixelG - 49) + Math.Abs(pixelB - 41);
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        closestR = pixelR;
                        closestG = pixelG;
                        closestB = pixelB;
                    }
                    if (probeCount < 100 && Math.Abs(pixelR - 255) <= 2 &&
                        Math.Abs(pixelG - 49) <= 2 && Math.Abs(pixelB - 41) <= 2)
                    {
                        if (probeCount == 0)
                        {
                            // ProbeColor reports coordinates local to the frame.
                            firstProbeX = localX;
                            firstProbeY = localY;
                        }
                        probeCount++;
                    }
                }

                if (Math.Abs(pixelR - 255) <= 2 && Math.Abs(pixelG - 49) <= 2 &&
                    Math.Abs(pixelB - 41) <= 2 && _redPixels.Count < MaxPixelsPerSignal)
                    _redPixels.Add(new Point(x, y));
                if (!darkRed && pixelR == 255 && pixelG == 41 && pixelB == 34) darkRed = true;
                if (Math.Abs(pixelR - 255) <= LiveFlashTolerance &&
                    Math.Abs(pixelG - 154) <= LiveFlashTolerance && Math.Abs(pixelB - 141) <= LiveFlashTolerance &&
                    _flashPixels.Count < MaxPixelsPerSignal)
                    _flashPixels.Add(new Point(x, y));
                // Orange stays a first-pixel match: real unblockables measured
                // as little as 1-4 exact pixels, so a size gate would miss them.
                if (!orange && pixelR == 246 && pixelG == 98 && pixelB == 8) orange = true;
                if (!orangeFeint && Math.Abs(pixelR - 255) <= 3 && Math.Abs(pixelG - 34) <= 3 &&
                    Math.Abs(pixelB - 28) <= 3) orangeFeint = true;
            }
        }

        ColorProbe probe = includeProbe
            ? new ColorProbe(probeCount, firstProbeX, firstProbeY,
                closestDistance == int.MaxValue ? "n/a" : $"{closestR},{closestG},{closestB}",
                closestDistance == int.MaxValue ? -1 : closestDistance)
            : new ColorProbe(0, -1, -1, "n/a", -1);
        return new LiveSignalMetric(darkRed, orange, orangeFeint, probe);
    }

    private static VisionAnalysisResult Empty(ScreenFrame frame, VisionScanRequest request,
        Point indicator, Rectangle roi)
    {
        return new VisionAnalysisResult(
            new CombatObservation(request.TimestampMs, request.MarkerFound, request.Anchor, request.Box,
                request.MarkerFound ? roi : request.CombatRoi, false, indicator, CombatDirection.None,
                false, false, false, false, request.FHeld,
                request.InputReady, request.SourceHeavyHeld, request.SourceLightHeld,
                VisionScanMode.Tracked, request.CombatRoi, request.MarkerLossAgeMs, 0,
                0, 0, request.TrackingGraceAgeMs != 0 ? request.TrackingGraceAgeMs : request.MarkerLossAgeMs) with { Tracking = request.Tracking },
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

    private readonly record struct LiveSignalMetric(
        bool DarkRed,
        bool Orange,
        bool OrangeFeint,
        ColorProbe RedProbe)
    {
        internal static LiveSignalMetric Empty => new(false, false, false, new ColorProbe(0, -1, -1, "n/a", -1));
    }

    private readonly record struct RedSelection(
        bool Found,
        Point Indicator,
        CombatDirection Direction,
        bool Ambiguous,
        IReadOnlyList<PixelCluster> Clusters)
    {
        internal static RedSelection None => new(false, new Point(-1, -1), CombatDirection.None, false, null);
    }

    private static CombatDirection ClassifyDirection(int x, int y, VisionScanRequest request)
    {
        if (x > request.RightX && y > request.RightY) return CombatDirection.Right;
        if (x < request.LeftX && y > request.RightY) return CombatDirection.Left;
        if (y > request.TopLeftY && y < request.TopRightY) return CombatDirection.Top;
        return CombatDirection.None;
    }
}
