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
    private static volatile Ds4HidControllerSource _directSource;
    private static ControllerSourceMode _sourceMode;
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
    private static ushort _lastButtons;
    private static byte _lastLeftTrigger;
    private static byte _lastRightTrigger;
    private static short _lastLeftX;
    private static short _lastLeftY;
    private static short _lastRightX;
    private static short _lastRightY;
    private static bool _hasLastReport;
    private static long _sourcePolls;
    private static long _sourceChanges;
    private static long _reportsSubmitted;
    private static long _reportsSkipped;
    private static long _lastSubmitTick;
    private static long _lastSubmitIntervalMs;
    private static long _maxSubmitIntervalMs;
    private static long _bridgeFailures;
    private static long _lastSourceReportTick;
    private static int _otherXInputControllers;

    public static bool IsAvailable { get; private set; }
    public static bool UsesDirectSource => _sourceMode == ControllerSourceMode.DirectDs4;
    public static string SourceType => _sourceMode == ControllerSourceMode.DirectDs4
        ? "direct-ds4" : "xinput-legacy";
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
        // Snapshot the source once, outside Sync. Source callbacks publish
        // while holding the source lock and then take Sync, so acquiring
        // them in the opposite order here could deadlock. A single capture
        // also closes the null-check race on shutdown.
        Ds4HidControllerSource direct = _directSource;
        Ds4SourceDiagnostics? directDiagnostics = direct?.Diagnostics;
        lock (Sync)
        {
            bool botRightStick = _bot.sThumbRX != 0 || _bot.sThumbRY != 0;
            short mergedRightX = _rightStickOverrideActive ? _rightStickOverrideX : botRightStick ? _bot.sThumbRX : _source.sThumbRX;
            short mergedRightY = _rightStickOverrideActive ? _rightStickOverrideY : botRightStick ? _bot.sThumbRY : _source.sThumbRY;
            long lastSubmitAgeMs = _lastSubmitTick == 0
                ? -1
                : Math.Max(0, Environment.TickCount64 - _lastSubmitTick);
            return new InputBridgeSnapshot(
                IsAvailable,
                _sourceConnected,
                _sourceSlot,
                _source.sThumbRX,
                _source.sThumbRY,
                _bot.sThumbRX,
                _bot.sThumbRY,
                mergedRightX,
                mergedRightY,
                _reportsSubmitted,
                _reportsSkipped,
                _sourcePolls,
                _sourceChanges,
                lastSubmitAgeMs,
                _lastSubmitIntervalMs,
                _maxSubmitIntervalMs,
                _bridgeFailures,
                SourceType,
                directDiagnostics?.Transport ?? "xinput",
                directDiagnostics?.Fingerprint ?? "",
                directDiagnostics?.Reports ?? _sourcePolls,
                directDiagnostics?.StateChanges ?? _sourceChanges,
                directDiagnostics?.ParseErrors ?? 0,
                directDiagnostics?.ReadErrors ?? 0,
                directDiagnostics?.Reconnects ?? 0,
                direct == null
                    ? (_lastSourceReportTick == 0 ? -1 : Math.Max(0, Environment.TickCount64 - _lastSourceReportTick))
                    : directDiagnostics?.LastReportAgeMs ?? -1,
                directDiagnostics?.LastReportIntervalMs ?? _lastSubmitIntervalMs,
                directDiagnostics?.MaxReportIntervalMs ?? _maxSubmitIntervalMs,
                _otherXInputControllers);
        }
    }

    public static void Init()
    {
        lock (LifecycleSync)
        {
            try
            {
                _sourceMode = ControllerSourceModeExtensions.Read();
                if (IsAvailable) return;
                _otherXInputControllers = _sourceMode == ControllerSourceMode.DirectDs4
                    ? CountConnectedXInputControllers()
                    : 0;
                _client = new ViGEmClient();
                _controller = _client.CreateXbox360Controller();
                _gamepad = _controller as IVirtualGamepad
                    ?? throw new InvalidOperationException("Unable to create an Xbox 360 controller.");
                _gamepad.AutoSubmitReport = false;
                _controller.Connect();
                IsAvailable = true;
                lock (Sync)
                {
                    _hasLastReport = false;
                    if (!ApplyLocked()) throw new InvalidOperationException("Unable to submit the initial controller report.");
                }
                if (_sourceMode == ControllerSourceMode.DirectDs4)
                {
                    Ds4HidControllerSource source = null;
                    source = new Ds4HidControllerSource((state, diagnostics) => OnDirectSourceState(source, state, diagnostics));
                    _directSource = source;
                    source.Start();
                }
                else
                {
                    _sourceTimer = new System.Threading.Timer(PollSource, null, 0, 8);
                }
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

            Ds4HidControllerSource directSource = _directSource;
            _directSource = null;
            try { directSource?.Dispose(); } catch { }

            lock (Sync)
            {
                _source = default;
                _bot = default;
                _rightStickOverrideActive = false;
                _rightStickOverrideX = 0;
                _rightStickOverrideY = 0;
                _sourceConnected = false;
                _sourceSlot = -1;
                _lastSourceReportTick = 0;
                _otherXInputControllers = 0;
                TrySubmitNeutralLocked();
                _hasLastReport = false;
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

    private static void OnDirectSourceState(Ds4HidControllerSource sender, ControllerState state, Ds4SourceDiagnostics diagnostics)
    {
        // Fast reject for callbacks from a superseded source (its Stop()
        // already neutralized the bridge). Rechecked inside Sync below:
        // this callback may have waited on the lock while shutdown or
        // recovery replaced the source.
        if (sender == null || !ReferenceEquals(sender, _directSource)) return;
        lock (Sync)
        {
            if (!ReferenceEquals(sender, _directSource)) return;
            bool connected = diagnostics.Connected;
            Native.XINPUT_GAMEPAD next = state.ToXInput();
            if (_sourceConnected != connected || !GamepadEquals(_source, next))
                _sourceChanges++;
            _source = next;
            _sourceConnected = connected;
            _sourceSlot = connected ? -2 : -1;
            _sourcePolls++;
            if (connected) _lastSourceReportTick = Environment.TickCount64;
            else
            {
                // A disconnected physical source must not leave an automated
                // button, trigger, or guard latched on the virtual output.
                _bot = default;
                _rightStickOverrideActive = false;
                _rightStickOverrideX = 0;
                _rightStickOverrideY = 0;
            }
            if (!ApplyLocked()) IsAvailable = false;
        }
    }

    private static bool GamepadEquals(Native.XINPUT_GAMEPAD a, Native.XINPUT_GAMEPAD b) =>
        a.wButtons == b.wButtons &&
        a.bLeftTrigger == b.bLeftTrigger &&
        a.bRightTrigger == b.bRightTrigger &&
        a.sThumbLX == b.sThumbLX &&
        a.sThumbLY == b.sThumbLY &&
        a.sThumbRX == b.sThumbRX &&
        a.sThumbRY == b.sThumbRY;

    private static int CountConnectedXInputControllers()
    {
        int count = 0;
        for (int slot = 0; slot < 4; slot++)
        {
            if (Native.XInputGetState(slot, out _) == 0) count++;
        }
        return count;
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
            _sourcePolls++;
            if (sourceSlot != _sourceSlot ||
                source.wButtons != _source.wButtons ||
                source.bLeftTrigger != _source.bLeftTrigger ||
                source.bRightTrigger != _source.bRightTrigger ||
                source.sThumbLX != _source.sThumbLX ||
                source.sThumbLY != _source.sThumbLY ||
                source.sThumbRX != _source.sThumbRX ||
                source.sThumbRY != _source.sThumbRY)
            {
                _sourceChanges++;
            }
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

    private static bool ApplyLocked(bool force = false)
    {
        if (!IsAvailable || _controller == null) return false;
        try
        {
            bool botLeftStick = _bot.sThumbLX != 0 || _bot.sThumbLY != 0;
            bool botRightStick = _bot.sThumbRX != 0 || _bot.sThumbRY != 0;
            bool useRightOverride = _rightStickOverrideActive;
            ushort buttons = (ushort)(_source.wButtons | _bot.wButtons);
            byte leftTrigger = Math.Max(_source.bLeftTrigger, _bot.bLeftTrigger);
            byte rightTrigger = Math.Max(_source.bRightTrigger, _bot.bRightTrigger);
            short leftX = botLeftStick ? _bot.sThumbLX : _source.sThumbLX;
            short leftY = botLeftStick ? _bot.sThumbLY : _source.sThumbLY;
            short rightX = useRightOverride ? _rightStickOverrideX : botRightStick ? _bot.sThumbRX : _source.sThumbRX;
            short rightY = useRightOverride ? _rightStickOverrideY : botRightStick ? _bot.sThumbRY : _source.sThumbRY;

            if (!force && _hasLastReport &&
                buttons == _lastButtons &&
                leftTrigger == _lastLeftTrigger &&
                rightTrigger == _lastRightTrigger &&
                leftX == _lastLeftX && leftY == _lastLeftY &&
                rightX == _lastRightX && rightY == _lastRightY)
            {
                _reportsSkipped++;
                return true;
            }

            _controller.SetButtonsFull(buttons);
            _controller.SetSliderValue(Xbox360Slider.LeftTrigger, leftTrigger);
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, rightTrigger);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, leftX);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY, leftY);
            _controller.SetAxisValue(Xbox360Axis.RightThumbX, rightX);
            _controller.SetAxisValue(Xbox360Axis.RightThumbY, rightY);
            _gamepad.SubmitReport();
            _lastButtons = buttons;
            _lastLeftTrigger = leftTrigger;
            _lastRightTrigger = rightTrigger;
            _lastLeftX = leftX;
            _lastLeftY = leftY;
            _lastRightX = rightX;
            _lastRightY = rightY;
            _hasLastReport = true;
            NoteReportSubmittedLocked();
            return true;
        }
        catch
        {
            _bridgeFailures++;
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
            NoteReportSubmittedLocked();
            _lastButtons = 0;
            _lastLeftTrigger = 0;
            _lastRightTrigger = 0;
            _lastLeftX = 0;
            _lastLeftY = 0;
            _lastRightX = 0;
            _lastRightY = 0;
        }
        catch
        {
            // Disconnecting the target remains the final neutralization path.
        }
    }

    private static void NoteReportSubmittedLocked()
    {
        long now = Environment.TickCount64;
        if (_lastSubmitTick != 0)
        {
            long interval = Math.Max(0, now - _lastSubmitTick);
            _lastSubmitIntervalMs = interval;
            if (interval > _maxSubmitIntervalMs) _maxSubmitIntervalMs = interval;
        }
        _lastSubmitTick = now;
        _reportsSubmitted++;
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

internal enum ControllerSourceMode
{
    XInputLegacy,
    DirectDs4
}

internal static class ControllerSourceModeExtensions
{
    public static ControllerSourceMode Read()
    {
        string value = Environment.GetEnvironmentVariable("HAPPYBOT_CONTROLLER_SOURCE")?.Trim();
        if (string.IsNullOrWhiteSpace(value)) value = Config.Read("ControllerSource")?.Trim();
        return string.Equals(value, "direct-ds4", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "directds4", StringComparison.OrdinalIgnoreCase)
            ? ControllerSourceMode.DirectDs4
            : ControllerSourceMode.XInputLegacy;
    }
}

public sealed record InputBridgeSnapshot(bool Available, bool SourceConnected, int SourceSlot,
    short SourceRightX, short SourceRightY, short BotRightX, short BotRightY,
    short MergedRightX, short MergedRightY,
    long ReportsSubmitted = 0, long ReportsSkipped = 0, long SourcePolls = 0,
    long SourceChanges = 0, long LastSubmitAgeMs = -1, long LastSubmitIntervalMs = 0,
    long MaxSubmitIntervalMs = 0, long BridgeFailures = 0,
    string SourceType = "xinput-legacy", string SourceTransport = "xinput",
    string SourceFingerprint = "", long SourceReports = 0, long SourceStateChanges = 0,
    long SourceParseErrors = 0, long SourceReadErrors = 0, long SourceReconnects = 0,
    long LastSourceReportAgeMs = -1, long LastSourceReportIntervalMs = 0,
    long MaxSourceReportIntervalMs = 0, int OtherXInputControllers = 0);
