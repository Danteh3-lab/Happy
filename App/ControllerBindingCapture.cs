namespace HappyBot.App;

internal enum BindingEventKind
{
    None,
    /// <summary>A capture finished: <see cref="BindingEvent.Action"/> is now bound to the button.</summary>
    Bound,
    /// <summary>A button was newly pressed outside capture; the caller resolves its bound action.</summary>
    Pressed
}

internal readonly record struct BindingEvent(BindingEventKind Kind, ControllerToggleAction Action, string Button)
{
    public static readonly BindingEvent None = new(BindingEventKind.None, ControllerToggleAction.None, "");
}

/// <summary>
/// Rising-edge detection for source-controller buttons, plus bind capture.
/// A button already held when capture starts is ignored until released, so
/// the press that opened capture cannot bind itself.
/// </summary>
internal sealed class ControllerBindingCapture
{
    public const int TriggerThreshold = 32;

    private static readonly (ushort Mask, string Name)[] Buttons =
    {
        (0x0001, "DPad Up"), (0x0002, "DPad Down"), (0x0004, "DPad Left"), (0x0008, "DPad Right"),
        (0x0010, "Start"), (0x0020, "Back"), (0x0040, "LS"), (0x0080, "RS"),
        (0x0100, "LB"), (0x0200, "RB"), (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y")
    };

    private ButtonState _baseline;
    private ButtonState _previous;
    private bool _initialized;

    public ControllerToggleAction Capturing { get; private set; }

    public void Begin(ControllerToggleAction action, Native.XINPUT_GAMEPAD current)
    {
        if (action == ControllerToggleAction.None) throw new ArgumentOutOfRangeException(nameof(action));
        _baseline = ButtonState.From(current);
        Capturing = action;
    }

    public void Cancel() => Capturing = ControllerToggleAction.None;

    /// <param name="hasState">False when the source controller could not be read this tick.</param>
    public BindingEvent Poll(bool hasState, Native.XINPUT_GAMEPAD current)
    {
        if (!hasState)
        {
            _previous = default;
            _initialized = false;
            return BindingEvent.None;
        }

        var now = ButtonState.From(current);
        if (!_initialized)
        {
            _previous = now;
            _initialized = true;
            return BindingEvent.None;
        }

        bool capturing = Capturing != ControllerToggleAction.None;
        string pressed = FindNewPress(current, capturing ? _baseline : _previous);
        _previous = now;
        if (pressed.Length == 0) return BindingEvent.None;

        if (!capturing) return new BindingEvent(BindingEventKind.Pressed, ControllerToggleAction.None, pressed);

        ControllerToggleAction bound = Capturing;
        Capturing = ControllerToggleAction.None;
        return new BindingEvent(BindingEventKind.Bound, bound, pressed);
    }

    private static string FindNewPress(Native.XINPUT_GAMEPAD current, ButtonState previous)
    {
        foreach ((ushort mask, string name) in Buttons)
        {
            bool held = (current.wButtons & mask) != 0;
            if (held && (previous.Buttons & mask) == 0) return name;
        }

        if (current.bLeftTrigger > TriggerThreshold && !previous.Lt) return "LT";
        if (current.bRightTrigger > TriggerThreshold && !previous.Rt) return "RT";
        return "";
    }

    private readonly record struct ButtonState(ushort Buttons, bool Lt, bool Rt)
    {
        public static ButtonState From(Native.XINPUT_GAMEPAD state) =>
            new(state.wButtons, state.bLeftTrigger > TriggerThreshold, state.bRightTrigger > TriggerThreshold);
    }
}
