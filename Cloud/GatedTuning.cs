using System.Text.Json;

namespace HappyBot.Cloud;

/// <summary>
/// The detection tuning that lets the bot see anchor markers, impact flashes and
/// parries. These numbers are deliberately NOT compiled into release builds: the
/// license service hands them out only for an <c>active</c> grant, and they are
/// cached (DPAPI, so bound to this Windows account on this PC) so the usual
/// offline window keeps working. A build that never received a real grant reads
/// the fail-closed <see cref="Blank"/> snapshot and therefore detects nothing —
/// so patching the local license check alone yields a non-functional bot, and a
/// redistributed crack has no way to recover the values from the binary.
/// Set on the UI thread (license accept / startup), read on the bot thread; the
/// reference is swapped atomically and every snapshot is immutable.
/// </summary>
internal static class GatedTuning
{
    /// <summary>Schema version the service payload and this reader must agree on.</summary>
    private const int SchemaVersion = 1;

    /// <summary>One immutable set of gated detection values.</summary>
    internal sealed record Values(
        int AnchorGreenR, int AnchorGreenG, int AnchorGreenB,
        int AnchorYellowR, int AnchorYellowG, int AnchorYellowB,
        int AnchorNearbyRadiusPx, int AnchorMaxPixelsPerKind,
        long ParryConfirmationStartMs, long ParryConfirmationEndMs,
        int ParryMinimumBaseThreshold, int ParryBaselineDelta,
        int FlashClusterTolerance, int FlashLiveTolerance, int FlashLiveMinimumPixels,
        int FlashColorR, int FlashColorG, int FlashColorB);

    /// <summary>
    /// Fail-closed values for a build with no grant: impossible colours and
    /// unreachable thresholds, so every detector matches nothing. No real tuning
    /// is embedded here, so nothing can be lifted from the binary.
    /// </summary>
    private static readonly Values Blank = new(
        -1, -1, -1, -1, -1, -1, 0, 0,
        long.MaxValue, long.MaxValue, int.MaxValue, 0,
        0, 0, int.MaxValue, -1, -1, -1);

#if DEBUG
    /// <summary>
    /// DEBUG-only development tuning, so debug builds run without reaching the
    /// service. Never compiled into Release, so the real values ship nowhere.
    /// </summary>
    private static readonly Values Dev = new(
        5, 131, 65, 255, 255, 10, 64, 4096,
        150, 650, 400, 250, 24, 2, 3, 255, 154, 141);
#endif

    private static readonly DpapiFile Store = DpapiFile.InAppData("tuning.dat");
    private static volatile Values _current;

    /// <summary>The active snapshot. Fail-closed until a grant delivers the real values.</summary>
    public static Values Current =>
        _current ??
#if DEBUG
        Dev;
#else
        Blank;
#endif

    /// <summary>True once real values are available (always true in DEBUG via <see cref="Dev"/>).</summary>
    public static bool IsLoaded =>
#if DEBUG
        true;
#else
        _current != null;
#endif

    /// <summary>Stores and activates a tuning blob returned by the license service.</summary>
    public static void Accept(string blob)
    {
        Values values = Parse(blob);
        if (values == null) return;
        _current = values;
        try { Store.Write(blob); }
        catch { /* cache is best-effort; the live values still apply this run */ }
    }

    /// <summary>
    /// Loads the cached blob at startup. Call only while the stored grant still
    /// allows offline use, so the cache cannot outlive the license.
    /// </summary>
    public static void Load()
    {
        Values values = Parse(Store.Read());
        if (values != null) _current = values;
    }

    /// <summary>Forgets the values and clears the cache (on sign-out or a dead grant).</summary>
    public static void Clear()
    {
        _current = null;
        Store.Delete();
    }

    private static Values Parse(string blob)
    {
        if (string.IsNullOrEmpty(blob)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(blob);
            JsonElement root = document.RootElement;
            if (Int(root, "v") != SchemaVersion) return null;
            JsonElement anchor = root.GetProperty("anchor");
            JsonElement parry = root.GetProperty("parry");
            JsonElement flash = root.GetProperty("flash");
            return new Values(
                Int(anchor, "gr"), Int(anchor, "gg"), Int(anchor, "gb"),
                Int(anchor, "yr"), Int(anchor, "yg"), Int(anchor, "yb"),
                Int(anchor, "radius"), Int(anchor, "maxpx"),
                Int(parry, "start"), Int(parry, "end"),
                Int(parry, "minbase"), Int(parry, "delta"),
                Int(flash, "tol"), Int(flash, "livetol"), Int(flash, "livemin"),
                Int(flash, "r"), Int(flash, "g"), Int(flash, "b"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static int Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number)
            ? number
            : throw new FormatException($"tuning field '{name}' is missing or not an integer");
}
