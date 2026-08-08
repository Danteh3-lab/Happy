using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace HappyBot;

public static class ViGEmInput
{
    private const int SourceSlot = 0;
    private static readonly object Sync = new();
    private static ViGEmClient _client;
    private static IXbox360Controller _controller;
    private static IVirtualGamepad _gamepad;
    private static System.Threading.Timer _sourceTimer;
    private static Native.XINPUT_GAMEPAD _source;
    private static Native.XINPUT_GAMEPAD _bot;
    private static bool _sourceConnected;

    public static bool IsAvailable { get; private set; }
    public static bool SourceConnected
    {
        get
        {
            lock (Sync) return _sourceConnected;
        }
    }

    public static void Init()
    {
        try
        {
            _client = new ViGEmClient();
            _controller = _client.CreateXbox360Controller();
            _gamepad = _controller as IVirtualGamepad
                ?? throw new InvalidOperationException("Unable to create an Xbox 360 controller.");
            _gamepad.AutoSubmitReport = false;
            _controller.Connect();
            IsAvailable = true;
            lock (Sync) ApplyLocked();
            _sourceTimer = new System.Threading.Timer(PollSource, null, 0, 8);
        }
        catch
        {
            Shutdown();
        }
    }

    public static void Shutdown()
    {
        _sourceTimer?.Dispose();
        _sourceTimer = null;
        lock (Sync)
        {
            _source = default;
            _bot = default;
            _sourceConnected = false;
            try { ApplyLocked(); } catch { }
        }
        try { _controller?.Disconnect(); } catch { }
        try { _client?.Dispose(); } catch { }
        _controller = null;
        _gamepad = null;
        _client = null;
        IsAvailable = false;
    }

    internal static bool TryGetSourceState(out Native.XINPUT_GAMEPAD state)
    {
        lock (Sync)
        {
            state = _source;
            return _sourceConnected;
        }
    }

    public static bool Key(int vk, bool down)
    {
        return vk switch
        {
            Input.VK_SPACE => Button(Xbox360Button.A, down),
            Input.VK_NUMPAD5 => Button(Xbox360Button.X, down),
            Input.VK_NUMPAD9 or Input.VK_C => Slider(Xbox360Slider.LeftTrigger, down ? (byte)255 : (byte)0),
            Input.VK_UP => Axis(Xbox360Axis.LeftThumbY, down ? (short)32767 : (short)0),
            Input.VK_DOWN => Axis(Xbox360Axis.LeftThumbY, down ? (short)-32767 : (short)0),
            Input.VK_LEFT => Axis(Xbox360Axis.LeftThumbX, down ? (short)-32767 : (short)0),
            Input.VK_RIGHT => Axis(Xbox360Axis.LeftThumbX, down ? (short)32767 : (short)0),
            Input.VK_NUMPAD8 => Axis(Xbox360Axis.RightThumbY, down ? (short)32767 : (short)0),
            Input.VK_NUMPAD4 => Axis(Xbox360Axis.RightThumbX, down ? (short)-32767 : (short)0),
            Input.VK_NUMPAD6 => Axis(Xbox360Axis.RightThumbX, down ? (short)32767 : (short)0),
            _ => true
        };
    }

    public static bool MouseClick(int vk, bool down)
    {
        return vk switch
        {
            Input.VK_LBUTTON => Button(Xbox360Button.RightShoulder, down),
            Input.VK_RBUTTON => Slider(Xbox360Slider.RightTrigger, down ? (byte)255 : (byte)0),
            _ => true
        };
    }

    private static void PollSource(object _)
    {
        bool connected = Native.XInputGetState(SourceSlot, out var state) == 0 && OutputSlot() != SourceSlot;
        lock (Sync)
        {
            _source = connected ? state.Gamepad : default;
            _sourceConnected = connected;
            ApplyLocked();
        }
    }

    private static int OutputSlot()
    {
        try { return _controller?.UserIndex ?? -1; }
        catch { return -1; }
    }

    private static bool Slider(Xbox360Slider slider, byte value)
    {
        if (!IsAvailable) return false;
        lock (Sync)
        {
            if (slider == Xbox360Slider.LeftTrigger) _bot.bLeftTrigger = value;
            else _bot.bRightTrigger = value;
            return ApplyLocked();
        }
    }

    private static bool Button(Xbox360Button button, bool down)
    {
        if (!IsAvailable) return false;
        ushort mask = ButtonMask(button);
        if (mask == 0) return false;
        lock (Sync)
        {
            if (down) _bot.wButtons |= mask;
            else _bot.wButtons = (ushort)(_bot.wButtons & ~mask);
            return ApplyLocked();
        }
    }

    private static bool Axis(Xbox360Axis axis, short value)
    {
        if (!IsAvailable) return false;
        lock (Sync)
        {
            if (ReferenceEquals(axis, Xbox360Axis.LeftThumbX)) _bot.sThumbLX = value;
            else if (ReferenceEquals(axis, Xbox360Axis.LeftThumbY)) _bot.sThumbLY = value;
            else if (ReferenceEquals(axis, Xbox360Axis.RightThumbX)) _bot.sThumbRX = value;
            else if (ReferenceEquals(axis, Xbox360Axis.RightThumbY)) _bot.sThumbRY = value;
            else return false;
            return ApplyLocked();
        }
    }

    private static bool ApplyLocked()
    {
        if (!IsAvailable || _controller == null) return false;
        try
        {
            bool botLeftStick = _bot.sThumbLX != 0 || _bot.sThumbLY != 0;
            bool botRightStick = _bot.sThumbRX != 0 || _bot.sThumbRY != 0;
            _controller.SetButtonsFull((ushort)(_source.wButtons | _bot.wButtons));
            _controller.SetSliderValue(Xbox360Slider.LeftTrigger, Math.Max(_source.bLeftTrigger, _bot.bLeftTrigger));
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, Math.Max(_source.bRightTrigger, _bot.bRightTrigger));
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, botLeftStick ? _bot.sThumbLX : _source.sThumbLX);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY, botLeftStick ? _bot.sThumbLY : _source.sThumbLY);
            _controller.SetAxisValue(Xbox360Axis.RightThumbX, botRightStick ? _bot.sThumbRX : _source.sThumbRX);
            _controller.SetAxisValue(Xbox360Axis.RightThumbY, botRightStick ? _bot.sThumbRY : _source.sThumbRY);
            _gamepad.SubmitReport();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ushort ButtonMask(Xbox360Button button)
    {
        if (ReferenceEquals(button, Xbox360Button.Up)) return 0x0001;
        if (ReferenceEquals(button, Xbox360Button.Down)) return 0x0002;
        if (ReferenceEquals(button, Xbox360Button.Left)) return 0x0004;
        if (ReferenceEquals(button, Xbox360Button.Right)) return 0x0008;
        if (ReferenceEquals(button, Xbox360Button.Start)) return 0x0010;
        if (ReferenceEquals(button, Xbox360Button.Back)) return 0x0020;
        if (ReferenceEquals(button, Xbox360Button.LeftThumb)) return 0x0040;
        if (ReferenceEquals(button, Xbox360Button.RightThumb)) return 0x0080;
        if (ReferenceEquals(button, Xbox360Button.LeftShoulder)) return 0x0100;
        if (ReferenceEquals(button, Xbox360Button.RightShoulder)) return 0x0200;
        if (ReferenceEquals(button, Xbox360Button.A)) return 0x1000;
        if (ReferenceEquals(button, Xbox360Button.B)) return 0x2000;
        if (ReferenceEquals(button, Xbox360Button.X)) return 0x4000;
        if (ReferenceEquals(button, Xbox360Button.Y)) return 0x8000;
        return 0;
    }
}
