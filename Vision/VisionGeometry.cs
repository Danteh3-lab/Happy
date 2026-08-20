using System.Drawing;

namespace HappyBot.Vision;

/// <summary>
/// Immutable resolution and anchor-derived geometry used by one vision pass.
/// The coordinate formulas intentionally mirror the original BotCore formulas.
/// </summary>
internal sealed record VisionGeometry
{
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
    public double B55 { get; init; }
    public double Y55 { get; init; }
    public RectangleF AnchorScan { get; init; }
    public RectangleF BoxScan { get; init; }

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
    public double X16 { get; init; }
    public double Y16 { get; init; }
    public double X17 { get; init; }
    public double Y17 { get; init; }

    public static VisionGeometry Empty => CreateResolution(0, 0);

    public static VisionGeometry CreateResolution(int width, int height)
    {
        double b55 = width / 1920.0;
        double y55 = height / 1080.0;
        return new VisionGeometry
        {
            ScreenWidth = width,
            ScreenHeight = height,
            B55 = b55,
            Y55 = y55,
            AnchorScan = RectangleF.FromLTRB((float)(860 * b55), (float)(80 * y55), (float)(1075 * b55), (float)(425 * y55)),
            BoxScan = RectangleF.FromLTRB((float)(670 * b55), (float)(300 * y55), (float)(820 * b55), (float)(510 * y55))
        };
    }

    /// <summary>Recomputes only the anchor-relative coordinates.</summary>
    public VisionGeometry WithAnchor(Point anchor, int box, string markerKind)
    {
        double ax = anchor.X;
        double ay = anchor.Y;
        double b = B55;
        double y = Y55;

        if (string.Equals(markerKind, "GREEN", StringComparison.OrdinalIgnoreCase))
        {
            return box == 2
                ? WithCoordinates(
                    ax - 200 * b, ay + 20 * y, ax + 160 * b, ay + 170 * y,
                    ax + 5 * b, ay + 195 * y, ax + 160 * b, ay + 430 * y,
                    ax - 200 * b, ay + 195 * y, ax - 30 * b, ay + 430 * y,
                    ax - 200 * b, ay + 20 * y, ax + 160 * b, ay + 430 * y)
                : WithCoordinates(
                    ax - 100 * b, ay + 10 * y, ax + 80 * b, ay + 85 * y,
                    ax + 2.5 * b, ay + 97.5 * y, ax + 80 * b, ay + 227.7 * y,
                    ax - 100 * b, ay + 97.5 * y, ax - 15 * b, ay + 227.7 * y,
                    ax - 117.6 * b, ay + 10 * y, ax + 94.11 * b, ay + 227.7 * y);
        }

        if (string.Equals(markerKind, "YELLOW", StringComparison.OrdinalIgnoreCase))
        {
            return box == 2
                ? WithCoordinates(
                    ax - 175 * b, ay + 65 * y, ax + 185 * b, ay + 185 * y,
                    ax + 30 * b, ay + 215 * y, ax + 185 * b, ay + 430 * y,
                    ax - 175 * b, ay + 215 * y, ax - 5 * b, ay + 430 * y,
                    ax - 175 * b, ay + 65 * y, ax + 185 * b, ay + 430 * y)
                : WithCoordinates(
                    ax - 87.5 * b, ay + 35 * y, ax + 92.5 * b, ay + 92.5 * y,
                    ax + 15 * b, ay + 107.5 * y, ax + 92.5 * b, ay + 215 * y,
                    ax - 87.5 * b, ay + 107.5 * y, ax - 2.5 * b, ay + 215 * y,
                    ax - 87.5 * b, ay + 35 * y, ax + 92.5 * b, ay + 215 * y);
        }

        return this;
    }

    public RectangleF CombatRoi => RectangleF.FromLTRB(
        (float)Math.Min(X16, X17), (float)Math.Min(Y16, Y17),
        (float)Math.Max(X16, X17), (float)Math.Max(Y16, Y17));

    public Rectangle CombatRoiRectangle => Rectangle.FromLTRB(
        (int)Math.Floor(CombatRoi.Left), (int)Math.Floor(CombatRoi.Top),
        (int)Math.Ceiling(CombatRoi.Right), (int)Math.Ceiling(CombatRoi.Bottom));

    public RectangleF TopZone => RectangleF.FromLTRB(
        CombatRoi.Left, Math.Max(CombatRoi.Top, (float)Math.Min(Y2, Y3)),
        CombatRoi.Right, Math.Min(CombatRoi.Bottom, (float)Math.Max(Y2, Y3)));

    public RectangleF LeftZone => RectangleF.FromLTRB(
        CombatRoi.Left, Math.Max(CombatRoi.Top, (float)Y4),
        Math.Min(CombatRoi.Right, (float)X7), CombatRoi.Bottom);

    public RectangleF RightZone => RectangleF.FromLTRB(
        Math.Max(CombatRoi.Left, (float)X4), Math.Max(CombatRoi.Top, (float)Y4),
        CombatRoi.Right, CombatRoi.Bottom);

    private VisionGeometry WithCoordinates(
        double nx2, double ny2, double nx3, double ny3,
        double nx4, double ny4, double nx5, double ny5,
        double nx6, double ny6, double nx7, double ny7,
        double nx16, double ny16, double nx17, double ny17) => this with
        {
            X2 = nx2, Y2 = ny2, X3 = nx3, Y3 = ny3,
            X4 = nx4, Y4 = ny4, X5 = nx5, Y5 = ny5,
            X6 = nx6, Y6 = ny6, X7 = nx7, Y7 = ny7,
            X16 = nx16, Y16 = ny16, X17 = nx17, Y17 = ny17
        };
}
