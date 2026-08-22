using System.Drawing;

namespace HappyBot;

internal enum CaptureMode
{
    Bootstrap,
    Tracked,
    FullFallback
}

internal readonly record struct CapturePlan(Rectangle Region, CaptureMode Mode, bool IsFullScreen)
{
    public static CapturePlan Full(Rectangle screenBounds) =>
        new(screenBounds, CaptureMode.FullFallback, true);
}

/// <summary>Builds screen-space crops that contain every region queried this frame.</summary>
internal static class CaptureRegionPlanner
{
    internal const int CombatHorizontalPaddingReference = 96;

    public static Rectangle PossibleCombatBounds(Rectangle markerScan, double b55, double y55)
    {
        if (markerScan.Width <= 0 || markerScan.Height <= 0) return Rectangle.Empty;

        // Covers both marker colours, both box variants, and the padded side ROI
        // for any marker inside the configured anchor search rectangle.
        int horizontalMargin = (int)Math.Ceiling((200 + CombatHorizontalPaddingReference) * b55);
        int rightMargin = (int)Math.Ceiling((185 + CombatHorizontalPaddingReference) * b55);
        int topMargin = (int)Math.Ceiling(10 * y55);
        int bottomMargin = (int)Math.Ceiling(430 * y55);
        return Rectangle.FromLTRB(
            markerScan.Left - horizontalMargin,
            markerScan.Top + topMargin,
            markerScan.Right + rightMargin,
            markerScan.Bottom + bottomMargin);
    }

    public static CapturePlan Build(Rectangle screenBounds, Rectangle markerScan, Rectangle boxScan,
        Rectangle possibleCombat, Rectangle activeCombat, Rectangle cachedCombat,
        Rectangle confirmationRegion, bool tracked)
    {
        Rectangle region = tracked
            ? Union(markerScan, boxScan, activeCombat, cachedCombat, confirmationRegion)
            : Union(markerScan, boxScan, possibleCombat, Rectangle.Empty, confirmationRegion);
        Rectangle clipped = Rectangle.Intersect(region, screenBounds);
        if (clipped.Width <= 0 || clipped.Height <= 0)
            return CapturePlan.Full(screenBounds);

        return new CapturePlan(clipped, tracked ? CaptureMode.Tracked : CaptureMode.Bootstrap, false);
    }

    private static Rectangle Union(Rectangle first, Rectangle second, Rectangle third,
        Rectangle fourth, Rectangle fifth)
    {
        Rectangle result = Rectangle.Empty;
        Add(ref result, first);
        Add(ref result, second);
        Add(ref result, third);
        Add(ref result, fourth);
        Add(ref result, fifth);
        return result;
    }

    private static void Add(ref Rectangle result, Rectangle rectangle)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
        result = result.Width <= 0 || result.Height <= 0
            ? rectangle
            : Rectangle.Union(result, rectangle);
    }
}
