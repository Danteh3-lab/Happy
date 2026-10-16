namespace HappyBot.Overlay;

/// <summary>
/// HUD colors for the F7 vision overlay. The shared ones mirror the tokens in
/// ui/styles/tokens.css (OverlayThemeTests keeps them equal) so the overlay and
/// the control deck read as one product.
/// </summary>
internal static class OverlayTheme
{
    // Mirrors of ui/styles/tokens.css
    public static readonly Color Accent = ColorTranslator.FromHtml("#e2554a");   // --accent
    public static readonly Color Signal = ColorTranslator.FromHtml("#4adae6");   // --signal
    public static readonly Color SignalDim = ColorTranslator.FromHtml("#5a949d"); // --signal-dim
    public static readonly Color Ok = ColorTranslator.FromHtml("#3dcf8e");       // --ok
    public static readonly Color Warn = ColorTranslator.FromHtml("#f0b35a");     // --warn
    public static readonly Color Danger = ColorTranslator.FromHtml("#ff5c7a");   // --danger
    public static readonly Color Text = ColorTranslator.FromHtml("#f2f3f6");     // --text-1
    public static readonly Color TextMuted = ColorTranslator.FromHtml("#a7acb8"); // --text-2
    public static readonly Color Panel = ColorTranslator.FromHtml("#101217");    // --surface-1

    // Overlay-only colors
    public static readonly Color PanelLine = Color.FromArgb(42, 47, 58);
    public static readonly Color Orange = Color.FromArgb(255, 155, 62);
    public static readonly Color Yellow = Color.FromArgb(255, 221, 78);

    /// <summary>
    /// The window's transparency key. Near-black rather than magenta, so any
    /// anti-aliased edge that blends with it darkens instead of tinting pink.
    /// Nothing the overlay draws may use this exact color.
    /// </summary>
    public static readonly Color TransparencyKey = Color.FromArgb(1, 2, 3);

    public static Color StateColor(string state)
    {
        if (state.Contains("PARRY", StringComparison.OrdinalIgnoreCase)) return Danger;
        if (state.Contains("DEFLECT", StringComparison.OrdinalIgnoreCase)) return Orange;
        if (state.Contains("BULWARK", StringComparison.OrdinalIgnoreCase)) return Ok;
        if (state.Contains("CRUSHING", StringComparison.OrdinalIgnoreCase)) return Yellow;
        if (state.Contains("ORANGE", StringComparison.OrdinalIgnoreCase) || state.Contains("DODGE", StringComparison.OrdinalIgnoreCase)) return Orange;
        if (state.Contains("GUARD", StringComparison.OrdinalIgnoreCase)) return Ok;
        if (state.Contains("SKIPPED", StringComparison.OrdinalIgnoreCase) || state.Contains("BLOCK ONLY", StringComparison.OrdinalIgnoreCase)) return Yellow;
        return Signal;
    }
}
