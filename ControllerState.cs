namespace HappyBot;

/// <summary>
/// Normalized gameplay state shared by physical controller sources and the
/// merged ViGEm output. Values use the same ranges as XInput.
/// </summary>
public readonly record struct ControllerState(
    ushort Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY)
{
    public static ControllerState Empty => default;

    internal Native.XINPUT_GAMEPAD ToXInput() => new()
    {
        wButtons = Buttons,
        bLeftTrigger = LeftTrigger,
        bRightTrigger = RightTrigger,
        sThumbLX = LeftX,
        sThumbLY = LeftY,
        sThumbRX = RightX,
        sThumbRY = RightY
    };

    internal static ControllerState FromXInput(Native.XINPUT_GAMEPAD state) => new(
        state.wButtons,
        state.bLeftTrigger,
        state.bRightTrigger,
        state.sThumbLX,
        state.sThumbLY,
        state.sThumbRX,
        state.sThumbRY);
}
