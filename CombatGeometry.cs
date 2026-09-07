namespace HappyBot;

/// <summary>
/// Single owner for combat screen geometry: resolution scalers, the anchor
/// scan and box scan regions, marker-relative directional zones, and the
/// accepted anchor position. Mutated only by the loop thread (marker
/// acceptance) and the UI thread (resolution changes), as before.
/// </summary>
public sealed class CombatGeometry
{
    public double B55, Y55;
    public double X2, Y2, X3, Y3, X4, Y4, X5, Y5, X6, Y6, X7, Y7;
    public double X8, Y8, X9, Y9, X16, Y16, X17, Y17, X18, Y18, X19, Y19;
    public int Ax;
    public int Ay;
    public int Box;

    /// <summary>Anchor scan, box scan, and scaler setup for a game resolution.</summary>
    public void UpdateResolution(int width, int height)
    {
        B55 = width / 1920.0;
        Y55 = height / 1080.0;
        X8 = (width / 1920.0) * 860;
        Y8 = (height / 1080.0) * 80;
        X9 = (width / 1920.0) * 1075;
        Y9 = (height / 1080.0) * 425;
        X18 = (width / 1920.0) * 670;
        Y18 = (height / 1080.0) * 300;
        X19 = (width / 1920.0) * 820;
        Y19 = (height / 1080.0) * 510;
    }

    /// <summary>Accepts a debounced marker and derives all relative zones.</summary>
    public void ApplyMarker(int x, int y, string kind, int box)
    {
        Ax = x;
        Ay = y;
        Box = box;
        if (kind == "GREEN")
        {
            if (box == 2)
                SetCoords(x - 200 * B55, y + 20 * Y55, x + 160 * B55, y + 170 * Y55,
                          x + 5 * B55, y + 195 * Y55, x + 160 * B55, y + 430 * Y55,
                          x - 200 * B55, y + 195 * Y55, x - 30 * B55, y + 430 * Y55,
                          x - 200 * B55, y + 20 * Y55, x + 160 * B55, y + 430 * Y55);
            else
                SetCoords(x - 100 * B55, y + 10 * Y55, x + 80 * B55, y + 85 * Y55,
                          x + 2.5 * B55, y + 97.5 * Y55, x + 80 * B55, y + 227.7 * Y55,
                          x - 100 * B55, y + 97.5 * Y55, x - 15 * B55, y + 227.7 * Y55,
                          x - 117.6 * B55, y + 10 * Y55, x + 94.11 * B55, y + 227.7 * Y55);
            return;
        }

        if (box == 2)
            SetCoords(x - 175 * B55, y + 65 * Y55, x + 185 * B55, y + 185 * Y55,
                      x + 30 * B55, y + 215 * Y55, x + 185 * B55, y + 430 * Y55,
                      x - 175 * B55, y + 215 * Y55, x - 5 * B55, y + 430 * Y55,
                      x - 175 * B55, y + 65 * Y55, x + 185 * B55, y + 430 * Y55);
        else
            SetCoords(x - 87.5 * B55, y + 35 * Y55, x + 92.5 * B55, y + 92.5 * Y55,
                      x + 15 * B55, y + 107.5 * Y55, x + 92.5 * B55, y + 215 * Y55,
                      x - 87.5 * B55, y + 107.5 * Y55, x - 2.5 * B55, y + 215 * Y55,
                      x - 87.5 * B55, y + 35 * Y55, x + 92.5 * B55, y + 215 * Y55);
    }

    public Rectangle CombatRoi()
    {
        int horizontalPadding = Math.Max(0, (int)Math.Round(96 * B55));
        int left = (int)Math.Floor(Math.Min(X16, X17)) - horizontalPadding;
        int top = (int)Math.Floor(Math.Min(Y16, Y17));
        int right = (int)Math.Ceiling(Math.Max(X16, X17)) + horizontalPadding;
        int bottom = (int)Math.Ceiling(Math.Max(Y16, Y17));
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    public Rectangle AnchorScan() => Rectangle.FromLTRB(
        (int)Math.Floor(X8), (int)Math.Floor(Y8),
        (int)Math.Ceiling(X9), (int)Math.Ceiling(Y9));

    public Rectangle BoxScan() => Rectangle.FromLTRB(
        (int)Math.Floor(X18), (int)Math.Floor(Y18),
        (int)Math.Ceiling(X19), (int)Math.Ceiling(Y19));

    /// <summary>
    /// Directional display zones clipped to the combat ROI. These mirror the
    /// exact half-plane thresholds the coordinator searches inside.
    /// </summary>
    public (RectangleF Top, RectangleF Right, RectangleF Left) Zones(RectangleF combatRoi)
    {
        var top = RectangleF.FromLTRB(combatRoi.Left, Math.Max(combatRoi.Top, (float)Math.Min(Y2, Y3)),
            combatRoi.Right, Math.Min(combatRoi.Bottom, (float)Math.Max(Y2, Y3)));
        var right = RectangleF.FromLTRB(Math.Max(combatRoi.Left, (float)X4), Math.Max(combatRoi.Top, (float)Y4),
            combatRoi.Right, combatRoi.Bottom);
        var left = RectangleF.FromLTRB(combatRoi.Left, Math.Max(combatRoi.Top, (float)Y4),
            Math.Min(combatRoi.Right, (float)X7), combatRoi.Bottom);
        return (top, right, left);
    }

    private void SetCoords(double nx2, double ny2, double nx3, double ny3, double nx4, double ny4,
                           double nx5, double ny5, double nx6, double ny6, double nx7, double ny7,
                           double nx16, double ny16, double nx17, double ny17)
    {
        X2 = nx2; Y2 = ny2;
        X3 = nx3; Y3 = ny3;
        X4 = nx4; Y4 = ny4;
        X5 = nx5; Y5 = ny5;
        X6 = nx6; Y6 = ny6;
        X7 = nx7; Y7 = ny7;
        X16 = nx16; Y16 = ny16;
        X17 = nx17; Y17 = ny17;
    }
}
