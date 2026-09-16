using System.Diagnostics;
using System.Drawing;

namespace HappyBot.Vision;

/// <summary>
/// A ranked marker candidate built from the existing exact anchor colours.
/// PixelCount is the number of connected matching pixels, not a relaxed colour
/// score, so this detector does not change the configured marker thresholds.
/// </summary>
internal readonly record struct AnchorMarkerCandidate(
    Point Anchor,
    Point Center,
    string Kind,
    int PixelCount,
    int DistanceFromPrevious,
    int Score);

/// <summary>Result of one anchor-region candidate scan.</summary>
internal readonly record struct AnchorMarkerScanResult(
    bool Found,
    int X,
    int Y,
    string Kind,
    int CandidateCount,
    int ChosenPixelCount,
    int ChosenDistance,
    int ChosenScore,
    string SelectionReason,
    long ScanDurationUs)
{
    public static AnchorMarkerScanResult Empty(long scanDurationUs = 0) =>
        new(false, 0, 0, "NONE", 0, 0, -1, 0, "no-color-match", scanDurationUs);
}

/// <summary>
/// Finds all exact green/yellow anchor pixels in the configured scan region,
/// groups adjacent pixels into candidates, and ranks them against the last
/// accepted marker. The caller still owns temporal confirmation and geometry.
/// </summary>
internal sealed class AnchorMarkerDetector
{
    private const int GreenR = 5;
    private const int GreenG = 131;
    private const int GreenB = 65;
    private const int YellowR = 255;
    private const int YellowG = 255;
    private const int YellowB = 10;
    private const int NearbyRadiusPx = 64;
    private const int MaxPixelsPerKind = 4096;

    public AnchorMarkerScanResult Scan(ScreenFrame frame, Rectangle requestedRegion,
        bool previousFound, Point previousAnchor, string previousKind)
    {
        long started = Stopwatch.GetTimestamp();
        if (frame == null || frame.Buffer == null || frame.Width <= 0 || frame.Height <= 0)
            return AnchorMarkerScanResult.Empty(ElapsedMicroseconds(started));

        Rectangle frameBounds = new(frame.OriginX, frame.OriginY, frame.Width, frame.Height);
        Rectangle region = Rectangle.Intersect(requestedRegion, frameBounds);
        if (region.Width <= 0 || region.Height <= 0)
            return AnchorMarkerScanResult.Empty(ElapsedMicroseconds(started));

        var green = new List<Point>(32);
        var yellow = new List<Point>(32);
        // Always collect the complete configured anchor region. A local-only
        // fast path is unsafe: a decorative exact-color pixel can remain near
        // the old anchor while the real marker moves elsewhere in the region.
        // The region is small and fixed, so this bounded verification keeps
        // reacquisition lossless without changing color thresholds.
        ScanPixels(frame, region, green, yellow);

        List<AnchorMarkerCandidate> candidates = new(green.Count + yellow.Count);
        AddComponents(candidates, green, "GREEN", previousFound, previousAnchor, previousKind);
        AddComponents(candidates, yellow, "YELLOW", previousFound, previousAnchor, previousKind);
        if (candidates.Count == 0)
            return AnchorMarkerScanResult.Empty(ElapsedMicroseconds(started));

        bool hasNearby = previousFound && candidates.Any(candidate =>
            candidate.DistanceFromPrevious <= NearbyRadiusPx);
        List<AnchorMarkerCandidate> nearbyCandidates = hasNearby
            ? candidates.Where(candidate => candidate.DistanceFromPrevious <= NearbyRadiusPx).ToList()
            : new List<AnchorMarkerCandidate>();
        AnchorMarkerCandidate selected = hasNearby
            ? SelectEstablishedCandidate(candidates, nearbyCandidates)
            : RankBySizeAndDistance(candidates).First();

        string reason = hasNearby
            ? selected.DistanceFromPrevious <= NearbyRadiusPx ? "near-accepted-marker" : "global-reacquisition"
            : previousFound ? "global-reacquisition" : "initial-largest-component";
        // Preserve the legacy Ax/Ay convention: the accepted anchor is the
        // first exact matching pixel from the selected component. The center
        // remains useful for component ranking without shifting every ROI.
        return new AnchorMarkerScanResult(true, selected.Anchor.X, selected.Anchor.Y,
            selected.Kind, candidates.Count, selected.PixelCount,
            selected.DistanceFromPrevious, selected.Score, reason,
            ElapsedMicroseconds(started));
    }

    private static AnchorMarkerCandidate SelectEstablishedCandidate(
        List<AnchorMarkerCandidate> allCandidates,
        List<AnchorMarkerCandidate> nearbyCandidates)
    {
        AnchorMarkerCandidate nearby = RankBySizeAndDistance(nearbyCandidates).First();
        AnchorMarkerCandidate far = RankBySizeAndDistance(allCandidates
            .Where(candidate => candidate.DistanceFromPrevious > NearbyRadiusPx)).FirstOrDefault();

        // A far candidate is allowed to win only with substantially stronger
        // exact-color evidence than the nearby match. Requiring more than
        // double the nearby component prevents a distant 5-pixel marker from
        // replacing an established 3-pixel marker while still allowing a
        // moved 2x2 marker to beat a one-pixel stray.
        int requiredPixels = nearby.PixelCount * 2 + 1;
        return far.PixelCount >= requiredPixels ? far : nearby;
    }

    private static IOrderedEnumerable<AnchorMarkerCandidate> RankBySizeAndDistance(
        IEnumerable<AnchorMarkerCandidate> candidates) => candidates
        .OrderByDescending(candidate => candidate.PixelCount)
        .ThenBy(candidate => candidate.DistanceFromPrevious)
        .ThenBy(candidate => candidate.Kind == "GREEN" ? 0 : 1)
        .ThenBy(candidate => candidate.Center.Y)
        .ThenBy(candidate => candidate.Center.X);

    private static void ScanPixels(ScreenFrame frame, Rectangle region,
        List<Point> green, List<Point> yellow)
    {
        for (int y = region.Top; y < region.Bottom; y++)
        {
            int row = (y - frame.OriginY) * frame.Stride;
            for (int x = region.Left; x < region.Right; x++)
            {
                int offset = row + (x - frame.OriginX) * 4;
                int blue = frame.Buffer[offset];
                int greenValue = frame.Buffer[offset + 1];
                int red = frame.Buffer[offset + 2];
                if (red == GreenR && greenValue == GreenG && blue == GreenB)
                {
                    if (green.Count < MaxPixelsPerKind) green.Add(new Point(x, y));
                }
                else if (red == YellowR && greenValue == YellowG && blue == YellowB)
                {
                    if (yellow.Count < MaxPixelsPerKind) yellow.Add(new Point(x, y));
                }
            }
        }
    }

    private static void AddComponents(List<AnchorMarkerCandidate> output, List<Point> pixels,
        string kind, bool previousFound, Point previousAnchor, string previousKind)
    {
        if (pixels.Count == 0) return;

        var remaining = new HashSet<long>(pixels.Count);
        foreach (Point pixel in pixels) remaining.Add(Key(pixel));
        var queue = new Queue<Point>();
        while (remaining.Count > 0)
        {
            long seedKey = remaining.First();
            var seed = new Point((int)(seedKey >> 32), (int)seedKey);
            remaining.Remove(seedKey);
            queue.Enqueue(seed);
            int count = 0;
            long sumX = 0;
            long sumY = 0;
            Point firstPixel = new(int.MaxValue, int.MaxValue);

            while (queue.Count > 0)
            {
                Point current = queue.Dequeue();
                count++;
                sumX += current.X;
                sumY += current.Y;
                if (current.Y < firstPixel.Y ||
                    current.Y == firstPixel.Y && current.X < firstPixel.X)
                    firstPixel = current;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        Point neighbor = new(current.X + dx, current.Y + dy);
                        if (remaining.Remove(Key(neighbor))) queue.Enqueue(neighbor);
                    }
                }
            }

            var center = new Point((int)Math.Round(sumX / (double)count),
                (int)Math.Round(sumY / (double)count));
            int distance = previousFound
                ? Math.Max(Math.Abs(firstPixel.X - previousAnchor.X), Math.Abs(firstPixel.Y - previousAnchor.Y))
                : 0;
            int kindPenalty = previousFound && !string.Equals(kind, previousKind, StringComparison.Ordinal)
                ? 25
                : 0;
            // Keep the diagnostic score aligned with the selection policy:
            // proximity dominates after acquisition, while component size
            // wins during initial acquisition.
            int score = previousFound
                ? -(distance * 1000) + count - kindPenalty
                : count * 1000 - kindPenalty;
            output.Add(new AnchorMarkerCandidate(firstPixel, center, kind, count, distance, score));
        }
    }

    private static long Key(Point point) => ((long)point.X << 32) | (uint)point.Y;

    private static long ElapsedMicroseconds(long started) =>
        (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000.0);
}
