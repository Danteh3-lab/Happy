namespace HappyBot.Overlay;

/// <summary>
/// HUD colors for the vision overlay. The shared ones mirror the tokens in
/// ui/styles/tokens.css (OverlayThemeTests keeps them equal) so the overlay and
/// the control deck read as one product.
/// </summary>
internal static class OverlayTheme
{
    // Mirrors of ui/styles/tokens.css
    public static readonly Color Accent = ColorTranslator.FromHtml("#f0a3b8");   // --accent
    public static readonly Color Signal = ColorTranslator.FromHtml("#4adae6");   // --signal
    public static readonly Color SignalDim = ColorTranslator.FromHtml("#5a949d"); // --signal-dim
    public static readonly Color Ok = ColorTranslator.FromHtml("#22c55e");       // --ok
    public static readonly Color Warn = ColorTranslator.FromHtml("#f59e0b");     // --warn
    public static readonly Color Danger = ColorTranslator.FromHtml("#ef4444");   // --danger
    public static readonly Color Text = ColorTranslator.FromHtml("#fafafa");     // --text-1
    public static readonly Color TextMuted = ColorTranslator.FromHtml("#a1a1aa"); // --text-2
    public static readonly Color Panel = ColorTranslator.FromHtml("#0b0b10");    // --surface-1

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
