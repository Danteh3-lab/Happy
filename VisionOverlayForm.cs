using System.Drawing.Drawing2D;
using System.Drawing.Text;
using HappyBot.Overlay;

namespace HappyBot;

/// <summary>
/// A non-activating transparent HUD. It displays only state already produced by
/// BotCore and is excluded from Windows screen capture before it is shown.
/// </summary>
public sealed class VisionOverlayForm : Form, App.IVisionOverlay
{
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int ChipBar = 3;
    private const int ChipPadX = 7;
    private const int ChipPadY = 4;
    private const int ChipGap = 6;

    private readonly Func<VisionSnapshot> _getSnapshot;
    private readonly Func<OverlayFeatureSnapshot> _getFeatures;
    private readonly System.Windows.Forms.Timer _paintTimer;
    private readonly OverlayPaintCache _paint = new();
    private Rectangle _screenBounds;
    private bool _showAnchorScan = true;

    public void SetAnchorScanVisible(bool visible)
    {
        _showAnchorScan = visible;
        Invalidate();
    }

    public VisionOverlayForm(Func<VisionSnapshot> getSnapshot, Func<OverlayFeatureSnapshot> getFeatures)
    {
        _getSnapshot = getSnapshot;
        _getFeatures = getFeatures;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = OverlayTheme.TransparencyKey;
        TransparencyKey = OverlayTheme.TransparencyKey;
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

    bool App.IVisionOverlay.TryShow() => TryShowOverlay();

    void App.IVisionOverlay.Hide() => HideOverlay();

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
        e.Graphics.Clear(OverlayTheme.TransparencyKey);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        VisionSnapshot s = _getSnapshot();
        OverlayFeatureSnapshot features = _getFeatures();
        // Grayscale anti-aliasing: ClearType's colored subpixels fringe against the key color.
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.None;

        DrawHeader(g, s);
        DrawFeatureList(g, features);
        if (_showAnchorScan)
            DrawRegion(g, ToClient(s.AnchorScan), s.MarkerFound ? OverlayTheme.Signal : OverlayTheme.SignalDim, "ANCHOR SCAN", active: false);

        if (!s.MarkerFound) return;

        string direction = string.IsNullOrEmpty(s.DecisionDirection) ? DirectionLabel(s.GuardDirection) : s.DecisionDirection;
        Color decisionColor = OverlayTheme.StateColor(s.ReactionState);

        DrawRegion(g, ToClient(s.CombatRoi), OverlayTheme.Signal, "COMBAT ROI", active: false);
        foreach ((string zone, RectangleF rect) in new[] { ("TOP", s.TopZone), ("LEFT", s.LeftZone), ("RIGHT", s.RightZone) })
        {
            bool active = zone == direction;
            DrawRegion(g, ToClient(rect), active ? decisionColor : OverlayTheme.SignalDim, zone, active);
        }

        Color anchorColor = s.MarkerKind == "YELLOW" ? OverlayTheme.Yellow : OverlayTheme.Ok;
        DrawTarget(g, ToClient(s.Anchor), anchorColor, "ANCHOR " + s.MarkerKind);
        if (s.AttackIndicator && s.Indicator.X >= 0)
            DrawTarget(g, ToClient(s.Indicator), OverlayTheme.Danger, "INDICATOR");

        DrawDecision(g, s, decisionColor, direction);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _paintTimer.Dispose();
            _paint.Dispose();
        }
        base.Dispose(disposing);
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

    private static string DirectionLabel(string guard) => guard switch
    {
        "LFT" => "LEFT",
        "RGT" => "RIGHT",
        "TOP" => "TOP",
        _ => ""
    };

    private void DrawHeader(Graphics g, VisionSnapshot s)
    {
        Rectangle title = DrawChip(g, new Point(16, 16), "DANBOT // VISION", OverlayTheme.Accent, _paint.Label);
        Rectangle pill = DrawPill(g, new Point(title.Right + ChipGap, title.Top), s.Running ? "LIVE" : "IDLE",
            s.Running ? OverlayTheme.Ok : OverlayTheme.SignalDim, filled: s.Running, height: title.Height);
        DrawChip(g, new Point(pill.Right + ChipGap, title.Top), $"{s.LoopHz} FPS", OverlayTheme.Signal, _paint.Label);

        string diagnostics = $"BOX {s.Box} V{s.TrackingVersion}   ANCHOR {s.AnchorAgeMs}ms   SCAN {s.AnchorCandidateCount}/{s.AnchorCandidateDistance}px   GUARD {s.GuardRemainingMs}ms   CAND {s.CandidateId}/{s.CandidateAgeMs}ms   {s.ActionWorkerState}   {s.LegitParryStatus}   TELEMETRY {(s.TelemetryRecording ? "ON" : "OFF")}";
        DrawChip(g, new Point(16, title.Bottom + ChipGap), diagnostics,
            s.TelemetryRecording ? OverlayTheme.Ok : OverlayTheme.SignalDim, _paint.Mono, OverlayTheme.TextMuted);
    }

    private void DrawFeatureList(Graphics g, OverlayFeatureSnapshot features)
    {
        var items = new List<(string Text, Color Color)>();
        if (features.AutoBlock) items.Add(("AUTO BLOCK", OverlayTheme.Ok));
        if (features.AutoParry) items.Add(("AUTO PARRY", OverlayTheme.Danger));
        if (features.AutoCrushing) items.Add(("AUTO CRUSH", OverlayTheme.Yellow));
        if (features.AutoDeflect) items.Add(("AUTO DEFLECT", OverlayTheme.Orange));
        if (features.AutoDodge) items.Add(("AUTO DODGE", OverlayTheme.Orange));
        if (features.OrangeLight) items.Add(("ORANGE LIGHT", OverlayTheme.Orange));
        if (features.OrangeParry) items.Add(("ORANGE PARRY", OverlayTheme.Danger));
        if (features.Legit) items.Add(($"LEGIT {features.LegitChance}%", OverlayTheme.Signal));
        if (features.BulwarkFallback) items.Add(("BULWARK", OverlayTheme.Ok));
        if (features.Telemetry) items.Add(("TELEMETRY", OverlayTheme.Signal));

        // ClientSize, not VisibleClipBounds: the clip shrinks on partial invalidation.
        int right = ClientSize.Width - 18;
        Rectangle heading = DrawChip(g, new Point(right, 16), "MODULES", OverlayTheme.SignalDim, _paint.Label,
            OverlayTheme.TextMuted, alignRight: true);
        int y = heading.Bottom + ChipGap;

        if (items.Count == 0)
        {
            DrawChip(g, new Point(right, y), "NO MODULES", OverlayTheme.SignalDim, _paint.Label, OverlayTheme.TextMuted, alignRight: true);
            return;
        }

        foreach ((string text, Color color) in items)
        {
            y = DrawChip(g, new Point(right, y), text, color, _paint.Label, alignRight: true).Bottom + 4;
        }
    }

    private void DrawRegion(Graphics g, RectangleF region, Color color, string label, bool active)
    {
        if (region.Width <= 0 || region.Height <= 0) return;
        Pen pen = _paint.Pen(color, active ? 2f : 1f);
        g.DrawRectangle(pen, (int)region.X, (int)region.Y, (int)region.Width, (int)region.Height);
        Rectangle chip = MeasureChip(g, label, _paint.Label);
        DrawChip(g, new Point((int)region.Left, Math.Max(3, (int)region.Top - chip.Height - 4)), label, color, _paint.Label,
            active ? OverlayTheme.Text : OverlayTheme.TextMuted);
    }

    private void DrawTarget(Graphics g, Point point, Color color, string label)
    {
        const int radius = 7;
        Pen pen = _paint.Pen(color, 1.5f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawEllipse(pen, point.X - radius, point.Y - radius, radius * 2, radius * 2);
        g.SmoothingMode = SmoothingMode.None;
        g.DrawLine(pen, point.X - 13, point.Y, point.X - radius - 2, point.Y);
        g.DrawLine(pen, point.X + radius + 2, point.Y, point.X + 13, point.Y);
        g.DrawLine(pen, point.X, point.Y - 13, point.X, point.Y - radius - 2);
        g.DrawLine(pen, point.X, point.Y + radius + 2, point.X, point.Y + 13);
        DrawChip(g, new Point(point.X + 14, point.Y + 10), label, color, _paint.Label);
    }

    private void DrawDecision(Graphics g, VisionSnapshot s, Color color, string direction)
    {
        RectangleF roi = ToClient(s.CombatRoi);
        var location = new Point((int)roi.Left, (int)roi.Bottom + 8);
        string text = s.ReactionState;
        if (!string.IsNullOrEmpty(direction) && !text.StartsWith(direction, StringComparison.OrdinalIgnoreCase))
            text = direction + " → " + text;
        Rectangle decision = DrawChip(g, location, text, color, _paint.LabelLarge);
        if (!string.IsNullOrEmpty(s.ReactionReason))
            DrawChip(g, new Point(location.X, decision.Bottom + 4), s.ReactionReason, OverlayTheme.SignalDim, _paint.Mono, OverlayTheme.TextMuted);
    }

    private Rectangle MeasureChip(Graphics g, string text, Font font)
    {
        SizeF size = g.MeasureString(text, font, PointF.Empty, _paint.Format);
        return new Rectangle(0, 0, ChipBar + ChipPadX * 2 + (int)Math.Ceiling(size.Width), ChipPadY * 2 + (int)Math.Ceiling(size.Height));
    }

    /// <summary>Opaque chip: panel fill, hairline border, a 3px bar in the state color, then text.</summary>
    private Rectangle DrawChip(Graphics g, Point location, string text, Color accent, Font font,
        Color? textColor = null, bool alignRight = false)
    {
        Rectangle rect = MeasureChip(g, text, font);
        rect.Location = new Point(alignRight ? location.X - rect.Width : location.X, location.Y);
        g.FillRectangle(_paint.Brush(OverlayTheme.Panel), rect);
        g.DrawRectangle(_paint.Pen(OverlayTheme.PanelLine), rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        g.FillRectangle(_paint.Brush(accent), rect.X, rect.Y, ChipBar, rect.Height);
        g.DrawString(text, font, _paint.Brush(textColor ?? OverlayTheme.Text), rect.X + ChipBar + ChipPadX, rect.Y + ChipPadY, _paint.Format);
        return rect;
    }

    private Rectangle DrawPill(Graphics g, Point location, string text, Color color, bool filled, int height)
    {
        SizeF size = g.MeasureString(text, _paint.Label, PointF.Empty, _paint.Format);
        var rect = new Rectangle(location.X, location.Y, (int)Math.Ceiling(size.Width) + ChipPadX * 2, height);
        g.FillRectangle(_paint.Brush(filled ? color : OverlayTheme.Panel), rect);
        g.DrawRectangle(_paint.Pen(color), rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        g.DrawString(text, _paint.Label, _paint.Brush(filled ? OverlayTheme.Panel : color), rect.X + ChipPadX,
            rect.Y + (height - size.Height) / 2f, _paint.Format);
        return rect;
    }
}
