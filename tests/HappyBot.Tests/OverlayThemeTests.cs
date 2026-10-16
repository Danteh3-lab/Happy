using System.Drawing;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using HappyBot;
using HappyBot.Overlay;

static partial class Program
{
    private static void OverlayThemeMatchesUiTokens()
    {
        string css = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "styles", "tokens.css"));
        Color Token(string name)
        {
            Match match = Regex.Match(css, "--" + name + @":\s*(#[0-9a-fA-F]{6})\s*;");
            Require(match.Success, "tokens.css has no hex --" + name);
            return ColorTranslator.FromHtml(match.Groups[1].Value);
        }

        foreach ((string token, Color color) in new[]
        {
            ("accent", OverlayTheme.Accent), ("signal", OverlayTheme.Signal), ("signal-dim", OverlayTheme.SignalDim),
            ("ok", OverlayTheme.Ok), ("warn", OverlayTheme.Warn), ("danger", OverlayTheme.Danger),
            ("text-1", OverlayTheme.Text), ("text-2", OverlayTheme.TextMuted), ("surface-1", OverlayTheme.Panel)
        })
        {
            Require(Token(token).ToArgb() == color.ToArgb(), "OverlayTheme differs from --" + token);
        }
    }

    private static void VisionOverlayDrawsOpaqueHud()
    {
        var snapshot = new VisionSnapshot
        {
            Running = true,
            MarkerFound = true,
            MarkerKind = "WHITE",
            Anchor = new Point(400, 300),
            AnchorScan = new RectangleF(250, 180, 300, 240),
            CombatRoi = new RectangleF(300, 320, 220, 160),
            TopZone = new RectangleF(360, 330, 100, 40),
            LeftZone = new RectangleF(310, 380, 80, 80),
            RightZone = new RectangleF(430, 380, 80, 80),
            AttackIndicator = true,
            Indicator = new Point(470, 410),
            DecisionDirection = "RIGHT",
            ReactionState = "PARRY SENT",
            ReactionReason = "Flash on RIGHT; RT after 60 ms",
            LoopHz = 118
        };
        var features = new OverlayFeatureSnapshot { AutoBlock = true, AutoParry = true, Legit = true, LegitChance = 55 };

        using var form = new VisionOverlayForm(() => snapshot, () => features) { ClientSize = new Size(900, 600) };
        using var bitmap = new Bitmap(900, 600);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            var args = new PaintEventArgs(g, new Rectangle(0, 0, 900, 600));
            MethodInfo background = typeof(VisionOverlayForm).GetMethod("OnPaintBackground", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo paint = typeof(VisionOverlayForm).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance);
            for (int frame = 0; frame < 3; frame++)
            {
                background.Invoke(form, new object[] { args });
                paint.Invoke(form, new object[] { args });
            }
        }

        int key = OverlayTheme.TransparencyKey.ToArgb();
        int panel = OverlayTheme.Panel.ToArgb();
        int keyPixels = 0, panelPixels = 0, translucent = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                int argb = pixel.ToArgb();
                if (argb == key) keyPixels++;
                else if (argb == panel) panelPixels++;
                if (pixel.A != 255) translucent++;
            }
        }
        Require(keyPixels > bitmap.Width * bitmap.Height / 2, "most of the overlay must be the transparency key");
        Require(panelPixels > 2000, "chips must be drawn with the opaque panel color");
        Require(translucent == 0, "the overlay must not draw translucent pixels (they blend with the key)");

        string preview = Path.Combine(Path.GetTempPath(), "danbot-overlay-preview.png");
        try { bitmap.Save(preview); } catch (IOException) { }
    }
}
