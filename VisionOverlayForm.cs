using System.Drawing.Drawing2D;

namespace HappyBot;

/// <summary>
/// A non-activating transparent HUD. It displays only state already produced by
/// BotCore and is excluded from Windows screen capture before it is shown.
/// </summary>
public sealed class VisionOverlayForm : Form
{
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    private static readonly Color TransparentColor = Color.Magenta;
    private static readonly Color Cyan = Color.FromArgb(74, 218, 230);
    private static readonly Color CyanDim = Color.FromArgb(90, 148, 157);
    private static readonly Color Green = Color.FromArgb(83, 230, 137);
    private static readonly Color Yellow = Color.FromArgb(255, 221, 78);
    private static readonly Color Red = Color.FromArgb(255, 91, 84);
    private static readonly Color Orange = Color.FromArgb(255, 155, 62);
    private static readonly Color Panel = Color.FromArgb(218, 10, 18, 23);

    private readonly Func<VisionSnapshot> _getSnapshot;
    private readonly System.Windows.Forms.Timer _paintTimer;
    private Rectangle _screenBounds;
    private bool _showAnchorScan = true;

    public void SetAnchorScanVisible(bool visible)
    {
        _showAnchorScan = visible;
        Invalidate();
    }

    public VisionOverlayForm(Func<VisionSnapshot> getSnapshot)
    {
        _getSnapshot = getSnapshot;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = TransparentColor;
        TransparencyKey = TransparentColor;
        DoubleBuffered = true;

        _paintTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _paintTimer.Tick += (_, _) =>
        {
            EnsureScreenBounds();
            Invalidate();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate;
            return cp;
        }
    }

    public bool TryShowOverlay()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return false;

        EnsureScreenBounds();
        if (!Visible) Show();

        if (!Native.SetWindowDisplayAffinity(Handle, Native.WdaExcludeFromCapture))
        {
            HideOverlay();
            return false;
        }

        _paintTimer.Start();
        Invalidate();
        return true;
    }

    public void HideOverlay()
    {
        _paintTimer.Stop();
        if (Visible) Hide();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            m.Result = new IntPtr(MaNoActivate);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(TransparentColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        VisionSnapshot s = _getSnapshot();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        DrawHeader(e.Graphics, s);
        if (_showAnchorScan) DrawAnchorScan(e.Graphics, s);

        if (!s.MarkerFound) return;

        DrawRegion(e.Graphics, ToClient(s.CombatRoi), Cyan, "COMBAT ROI", 1.5f);
        DrawRegion(e.Graphics, ToClient(s.TopZone), ZoneColor(s, "TOP"), "TOP", 1.5f);
        DrawRegion(e.Graphics, ToClient(s.LeftZone), ZoneColor(s, "LEFT"), "LEFT", 1.5f);
        DrawRegion(e.Graphics, ToClient(s.RightZone), ZoneColor(s, "RIGHT"), "RIGHT", 1.5f);

        Color anchorColor = s.MarkerKind == "YELLOW" ? Yellow : Green;
        DrawTarget(e.Graphics, ToClient(s.Anchor), anchorColor, "ANCHOR " + s.MarkerKind);
        if (s.AttackIndicator && s.Indicator.X >= 0)
            DrawTarget(e.Graphics, ToClient(s.Indicator), Red, "INDICATOR");

        string direction = string.IsNullOrEmpty(s.DecisionDirection) ? DirectionLabel(s.GuardDirection) : s.DecisionDirection;
        Color decisionColor = StateColor(s.ReactionState);
        DrawDecision(e.Graphics, s, decisionColor, direction);
    }

    private void EnsureScreenBounds()
    {
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        if (bounds == _screenBounds) return;
        _screenBounds = bounds;
        Bounds = bounds;
    }

    private RectangleF ToClient(RectangleF screen) => new(screen.X - _screenBounds.Left, screen.Y - _screenBounds.Top, screen.Width, screen.Height);

    private Point ToClient(Point screen) => new(screen.X - _screenBounds.Left, screen.Y - _screenBounds.Top);

    private static Color ZoneColor(VisionSnapshot s, string zone)
    {
        string active = string.IsNullOrEmpty(s.DecisionDirection) ? DirectionLabel(s.GuardDirection) : s.DecisionDirection;
        return active == zone ? StateColor(s.ReactionState) : CyanDim;
    }

    private static Color StateColor(string state)
    {
        if (state.Contains("PARRY", StringComparison.OrdinalIgnoreCase)) return Red;
        if (state.Contains("ORANGE", StringComparison.OrdinalIgnoreCase) || state.Contains("DODGE", StringComparison.OrdinalIgnoreCase)) return Orange;
        if (state.Contains("GUARD", StringComparison.OrdinalIgnoreCase)) return Green;
        if (state.Contains("SKIPPED", StringComparison.OrdinalIgnoreCase)) return Yellow;
        return Cyan;
    }

    private static string DirectionLabel(string guard) => guard switch
    {
        "LFT" => "LEFT",
        "RGT" => "RIGHT",
        "TOP" => "TOP",
        _ => ""
    };

    private static void DrawHeader(Graphics g, VisionSnapshot s)
    {
        string mode = s.Running ? "LIVE" : "IDLE";
        string line = $"DANBOT // VISION    {mode}    {s.LoopHz} FPS";
        DrawChip(g, new Point(16, 16), line, s.Running ? Green : Cyan);
        string diagnostics = $"BOX {s.Box}   ANCHOR {s.AnchorAgeMs}ms   GUARD {s.GuardRemainingMs}ms   CAND {s.CandidateId}/{s.CandidateAgeMs}ms   {s.ActionWorkerState}   TELEMETRY {(s.TelemetryRecording ? "ON" : "OFF")}";
        DrawChip(g, new Point(16, 43), diagnostics, s.TelemetryRecording ? Green : CyanDim);
    }

    private void DrawAnchorScan(Graphics g, VisionSnapshot s)
    {
        RectangleF region = ToClient(s.AnchorScan);
        DrawRegion(g, region, s.MarkerFound ? Cyan : CyanDim, "ANCHOR SCAN", 1f);
    }

    private static void DrawRegion(Graphics g, RectangleF region, Color color, string label, float width)
    {
        if (region.Width <= 0 || region.Height <= 0) return;
        using var pen = new Pen(Color.FromArgb(210, color), width) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(pen, region.X, region.Y, region.Width, region.Height);
        DrawChip(g, new Point((int)region.Left + 5, Math.Max(3, (int)region.Top - 23)), label, color);
    }

    private static void DrawTarget(Graphics g, Point point, Color color, string label)
    {
        const int radius = 7;
        using var pen = new Pen(color, 1.5f);
        g.DrawEllipse(pen, point.X - radius, point.Y - radius, radius * 2, radius * 2);
        g.DrawLine(pen, point.X - 13, point.Y, point.X + 13, point.Y);
        g.DrawLine(pen, point.X, point.Y - 13, point.X, point.Y + 13);
        DrawChip(g, new Point(point.X + 12, point.Y + 10), label, color);
    }

    private void DrawDecision(Graphics g, VisionSnapshot s, Color color, string direction)
    {
        RectangleF roi = ToClient(s.CombatRoi);
        Point location = new((int)roi.Left + 6, (int)roi.Bottom + 7);
        string text = s.ReactionState;
        if (!string.IsNullOrEmpty(direction) && !text.StartsWith(direction, StringComparison.OrdinalIgnoreCase))
            text = direction + " → " + text;
        DrawChip(g, location, text, color);
        DrawChip(g, new Point(location.X, location.Y + 27), s.ReactionReason, CyanDim);
    }

    private static void DrawChip(Graphics g, Point location, string text, Color color)
    {
        using var font = new Font("Consolas", 8.2f, FontStyle.Bold, GraphicsUnit.Point);
        SizeF size = g.MeasureString(text, font);
        var rect = new RectangleF(location.X, location.Y, size.Width + 12, size.Height + 6);
        using var fill = new SolidBrush(Panel);
        using var border = new Pen(Color.FromArgb(220, color));
        using var foreground = new SolidBrush(Color.FromArgb(235, 241, 246));
        g.FillRectangle(fill, rect);
        g.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);
        g.DrawString(text, font, foreground, rect.X + 6, rect.Y + 3);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _paintTimer.Dispose();
        base.Dispose(disposing);
    }
}
