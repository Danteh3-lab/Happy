using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace HappyBot;

public static class ViGEmInput
{
    private static readonly object Sync = new();
    private static readonly object LifecycleSync = new();
    private static ViGEmClient _client;
    private static IXbox360Controller _controller;
    private static IVirtualGamepad _gamepad;
    private static System.Threading.Timer _sourceTimer;
    private static Native.XINPUT_GAMEPAD _source;
    private static Native.XINPUT_GAMEPAD _bot;
    // A short-lived stance may need the right stick without destroying the
    // continuous guard state held in _bot. It takes precedence only while set.
    private static bool _rightStickOverrideActive;
    private static short _rightStickOverrideX;
    private static short _rightStickOverrideY;
    private static bool _sourceConnected;
    private static int _sourceSlot = -1;
    private static long _lastRecoveryTick;

    public static bool IsAvailable { get; private set; }
    public static bool SourceConnected
    {
        get
        {
            lock (Sync) return _sourceConnected;
        }
    }

    public static int SourceSlot
    {
        get
        {
            lock (Sync) return _sourceSlot;
        }
    }

    public static InputBridgeSnapshot GetDiagnostics()
    {
        lock (Sync)
        {
            bool botRightStick = _bot.sThumbRX != 0 || _bot.sThumbRY != 0;
            short mergedRightX = _rightStickOverrideActive ? _rightStickOverrideX : botRightStick ? _bot.sThumbRX : _source.sThumbRX;
            short mergedRightY = _rightStickOverrideActive ? _rightStickOverrideY : botRightStick ? _bot.sThumbRY : _source.sThumbRY;
            return new InputBridgeSnapshot(
                IsAvailable,
                _sourceConnected,
                _sourceSlot,
                _source.sThumbRX,
                _source.sThumbRY,
                _bot.sThumbRX,
                _bot.sThumbRY,
                mergedRightX,
                mergedRightY);
        }
    }

    public static void Init()
    {
        lock (LifecycleSync)
        {
            try
            {
                if (IsAvailable) return;
                _client = new ViGEmClient();
                _controller = _client.CreateXbox360Controller();
                _gamepad = _controller as IVirtualGamepad
                    ?? throw new InvalidOperationException("Unable to create an Xbox 360 controller.");
                _gamepad.AutoSubmitReport = false;
                _controller.Connect();
                IsAvailable = true;
                lock (Sync)
                {
                    if (!ApplyLocked()) throw new InvalidOperationException("Unable to submit the initial controller report.");
                }
                _sourceTimer = new System.Threading.Timer(PollSource, null, 0, 8);
            }
            catch
            {
                Shutdown();
            }
        }
    }

    public static void Shutdown()
    {
        lock (LifecycleSync)
        {
            System.Threading.Timer timer = _sourceTimer;
            _sourceTimer = null;
            if (timer != null)
            {
                using var callbacksStopped = new ManualResetEvent(false);
                if (timer.Dispose(callbacksStopped)) callbacksStopped.WaitOne(500);
            }

            lock (Sync)
            {
                _source = default;
                _bot = default;
                _rightStickOverrideActive = false;
                _rightStickOverrideX = 0;
                _rightStickOverrideY = 0;
                _sourceConnected = false;
                _sourceSlot = -1;
                TrySubmitNeutralLocked();
            }
            try { _controller?.Disconnect(); } catch { }
            try { _client?.Dispose(); } catch { }
            _controller = null;
            _gamepad = null;
            _client = null;
            IsAvailable = false;
        }
    }

    public static void TryRecover()
    {
        if (IsAvailable) return;
        long now = Environment.TickCount64;
        if (now - _lastRecoveryTick < 2000) return;
        _lastRecoveryTick = now;
        Shutdown();
        Init();
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

    /// <summary>
    /// Temporarily owns the right stick without clearing the bot's current
    /// guard. Clearing the override immediately exposes that guard again.
    /// </summary>
    public static bool SetRightStickOverride(short x, short y)
    {
        if (!IsAvailable) return false;
        lock (Sync)
        {
            _rightStickOverrideActive = true;
            _rightStickOverrideX = x;
            _rightStickOverrideY = y;
            return ApplyLocked();
        }
    }

    public static bool ClearRightStickOverride()
    {
        lock (Sync)
        {
            _rightStickOverrideActive = false;
            _rightStickOverrideX = 0;
            _rightStickOverrideY = 0;
            return ApplyLocked();
        }
    }

    private static void PollSource(object _)
    {
        int outputSlot = OutputSlot();
        int sourceSlot = -1;
        Native.XINPUT_GAMEPAD source = default;
        for (int slot = 0; slot < 4; slot++)
        {
            if (slot == outputSlot) continue;
            if (Native.XInputGetState(slot, out var state) != 0) continue;
            sourceSlot = slot;
            source = state.Gamepad;
            break;
        }

        lock (Sync)
        {
            _source = source;
            _sourceConnected = sourceSlot >= 0;
            _sourceSlot = sourceSlot;
            if (!ApplyLocked()) IsAvailable = false;
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
            bool useRightOverride = _rightStickOverrideActive;
            _controller.SetButtonsFull((ushort)(_source.wButtons | _bot.wButtons));
            _controller.SetSliderValue(Xbox360Slider.LeftTrigger, Math.Max(_source.bLeftTrigger, _bot.bLeftTrigger));
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, Math.Max(_source.bRightTrigger, _bot.bRightTrigger));
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, botLeftStick ? _bot.sThumbLX : _source.sThumbLX);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY, botLeftStick ? _bot.sThumbLY : _source.sThumbLY);
            _controller.SetAxisValue(Xbox360Axis.RightThumbX, useRightOverride ? _rightStickOverrideX : botRightStick ? _bot.sThumbRX : _source.sThumbRX);
            _controller.SetAxisValue(Xbox360Axis.RightThumbY, useRightOverride ? _rightStickOverrideY : botRightStick ? _bot.sThumbRY : _source.sThumbRY);
            _gamepad.SubmitReport();
            return true;
        }
        catch
        {
            // The last successfully submitted report may still have a button or
            // trigger down. Attempt a neutral report before declaring the bridge
            // unavailable so a failed key-up cannot remain latched until recovery.
            TrySubmitNeutralLocked();
            IsAvailable = false;
            return false;
        }
    }

    private static void TrySubmitNeutralLocked()
    {
        if (_controller == null || _gamepad == null) return;
        try
        {
            _controller.SetButtonsFull(0);
            _controller.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, 0);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
            _controller.SetAxisValue(Xbox360Axis.RightThumbX, 0);
            _controller.SetAxisValue(Xbox360Axis.RightThumbY, 0);
            _gamepad.SubmitReport();
        }
        catch
        {
            // Disconnecting the target remains the final neutralization path.
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

public sealed record InputBridgeSnapshot(bool Available, bool SourceConnected, int SourceSlot,
    short SourceRightX, short SourceRightY, short BotRightX, short BotRightY,
    short MergedRightX, short MergedRightY);
