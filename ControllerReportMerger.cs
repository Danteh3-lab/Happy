namespace HappyBot;

/// <summary>
/// Merged virtual-pad report: physical source OR automation buttons, maximum
/// triggers, and automation (or temporary override) sticks. A plain value so
/// merge rules are testable without a driver.
/// </summary>
internal readonly record struct MergedControllerReport(
    ushort Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY);

/// <summary>
/// Pure merge of physical source, automation, and stick-override state into
/// one virtual-pad report. No locks, handles, or submissions; the bridge
/// owns threading and deduplication around this function.
/// </summary>
internal static class ControllerReportMerger
{
    public static MergedControllerReport Merge(
        Native.XINPUT_GAMEPAD source,
        Native.XINPUT_GAMEPAD bot,
        bool overrideActive,
        short overrideX,
        short overrideY)
    {
        bool botLeftStick = bot.sThumbLX != 0 || bot.sThumbLY != 0;
        bool botRightStick = bot.sThumbRX != 0 || bot.sThumbRY != 0;
        return new MergedControllerReport(
            (ushort)(source.wButtons | bot.wButtons),
            Math.Max(source.bLeftTrigger, bot.bLeftTrigger),
            Math.Max(source.bRightTrigger, bot.bRightTrigger),
            botLeftStick ? bot.sThumbLX : source.sThumbLX,
            botLeftStick ? bot.sThumbLY : source.sThumbLY,
            overrideActive ? overrideX : botRightStick ? bot.sThumbRX : source.sThumbRX,
            overrideActive ? overrideY : botRightStick ? bot.sThumbRY : source.sThumbRY);
    }
}
