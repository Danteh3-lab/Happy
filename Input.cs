using System.Runtime.InteropServices;

namespace HappyBot;

public static class Input
{
    private const int KeyTapDelayMs = 30;
    private const int MouseTapDelayMs = 30;

    private enum InputMode
    {
        Event,
        SendInput,
        ViGEm
    }

    private static readonly InputMode RequestedMode = ReadMode();

    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_SPACE = 0x20;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;
    public const int VK_A = 0x41;
    public const int VK_C = 0x43;
    public const int VK_D = 0x44;
    public const int VK_E = 0x45;
    public const int VK_F = 0x46;
    public const int VK_S = 0x53;
    public const int VK_W = 0x57;
    public const int VK_NUMPAD4 = 0x64;
    public const int VK_NUMPAD5 = 0x65;
    public const int VK_NUMPAD6 = 0x66;
    public const int VK_NUMPAD8 = 0x68;
    public const int VK_NUMPAD9 = 0x69;

    public static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static readonly string HoldButton = ReadHoldButton();

    private static string ReadHoldButton()
    {
        string value = Config.Read("HoldButton")?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(value) ? "LT" : value;
    }

    public static bool HoldButtonHeld()
    {
        if (!ViGEmInput.TryGetSourceState(out var state)) return false;
        switch (HoldButton)
        {
            case "LT": return state.bLeftTrigger > 32;
            case "RT": return state.bRightTrigger > 32;
            case "LB": return (state.wButtons & 0x0100) != 0;
            case "RB": return (state.wButtons & 0x0200) != 0;
            case "L3": return (state.wButtons & 0x0040) != 0;
            case "R3": return (state.wButtons & 0x0080) != 0;
            case "A": return (state.wButtons & 0x1000) != 0;
            case "B": return (state.wButtons & 0x2000) != 0;
            case "X": return (state.wButtons & 0x4000) != 0;
            case "Y": return (state.wButtons & 0x8000) != 0;
            default: return false;
        }
    }

    /// <summary>
    /// Reads only the forwarded physical controller's RT state. DANBOT's
    /// virtual RT is kept separate so an automated parry cannot look like a
    /// player attack. RT is excluded when it is configured as the hold gate.
    /// </summary>
    public static bool PhysicalHeavyAttackHeld()
    {
        if (RequestedMode != InputMode.ViGEm || HoldButton == "RT") return false;
        return ViGEmInput.TryGetSourceState(out var state) && state.bRightTrigger > 32;
    }

    /// <summary>
    /// Reads the physical controller's RB/light state for outgoing orange
    /// light attribution. RB is excluded when it is configured as the hold
    /// gate, just like RT above.
    /// </summary>
    public static bool PhysicalLightAttackHeld()
    {
        if (RequestedMode != InputMode.ViGEm || HoldButton == "RB") return false;
        return ViGEmInput.TryGetSourceState(out var state) && (state.wButtons & 0x0200) != 0;
    }

    /// <summary>
    /// Uses the physical controller's forwarded left-stick state when the
    /// controller bridge is active. Keyboard modes keep the existing W gate.
    /// </summary>
    public static bool MovingForwardHeld()
    {
        if (RequestedMode != InputMode.ViGEm) return IsDown(VK_W);
        return ViGEmInput.TryGetSourceState(out var state) && state.sThumbLY >= 12000;
    }

    public static long InjectedCount;
    public static uint LastSendResult = 1;
    public static int LastSendError;

    public static string ActiveMode =>
        RequestedMode == InputMode.ViGEm ? (ViGEmInput.IsAvailable ? "ViGEm" : "ViGEm unavailable")
            : RequestedMode == InputMode.SendInput ? "SendInput" : "Event";

    public static bool IsReady => RequestedMode != InputMode.ViGEm || ViGEmInput.IsAvailable;

    public static bool UsesControllerBridge => RequestedMode == InputMode.ViGEm;

    public static bool CanSendBulwark => RequestedMode == InputMode.ViGEm && ViGEmInput.IsAvailable;

    public static bool KeyDown(int vk)
    {
        if (RequestedMode == InputMode.ViGEm)
        {
            bool sent = ViGEmInput.IsAvailable && ViGEmInput.Key(vk, true);
            ReportSend(sent);
            return sent;
        }

        if (RequestedMode != InputMode.SendInput)
        {
            SendEventKey(vk, false);
            return true;
        }

        return SendKey(vk, false);
    }

    public static bool KeyUp(int vk)
    {
        if (RequestedMode == InputMode.ViGEm)
        {
            bool sent = ViGEmInput.IsAvailable && ViGEmInput.Key(vk, false);
            ReportSend(sent);
            return sent;
        }

        if (RequestedMode != InputMode.SendInput)
        {
            SendEventKey(vk, true);
            return true;
        }

        return SendKey(vk, true);
    }

    public static bool KeyTap(int vk)
    {
        return KeyTap(vk, KeyTapDelayMs);
    }

    public static bool KeyTap(int vk, int holdMs)
    {
        bool downSent = KeyDown(vk);
        Thread.Sleep(holdMs);
        bool upSent = KeyUp(vk);
        return downSent && upSent;
    }

    public static bool MouseClick(int vk)
    {
        if (RequestedMode == InputMode.ViGEm)
        {
            if (!ViGEmInput.IsAvailable)
            {
                ReportSend(false);
                return false;
            }

            bool downSent = ViGEmInput.MouseClick(vk, true);
            ReportSend(downSent);
            Thread.Sleep(MouseTapDelayMs);
            bool upSent = ViGEmInput.MouseClick(vk, false);
            ReportSend(upSent);
            return downSent && upSent;
        }

        if (RequestedMode != InputMode.SendInput)
        {
            SendEventMouse(vk);
            return true;
        }

        uint down = vk == VK_LBUTTON ? 0x0002u : 0x0008u;
        uint up = vk == VK_LBUTTON ? 0x0004u : 0x0010u;
        bool downOk = SendMouse(down) > 0;
        Thread.Sleep(MouseTapDelayMs);
        bool upOk = SendMouse(up) > 0;
        return downOk && upOk;
    }

    public static void Block(bool on)
    {
        if (RequestedMode == InputMode.ViGEm) return;
        Native.BlockInput(on);
    }

    /// <summary>
    /// Black Prior's Bulwark Stance is controller-native: right stick down.
    /// This is deliberately unavailable outside the ViGEm controller bridge;
    /// no keyboard fallback can faithfully represent this controller input.
    /// </summary>
    public static bool BeginBulwarkStance()
    {
        if (RequestedMode != InputMode.ViGEm || !ViGEmInput.IsAvailable)
        {
            ReportSend(false);
            return false;
        }

        bool sent = ViGEmInput.SetRightStickOverride(0, -32767);
        ReportSend(sent);
        return sent;
    }

    public static void EndBulwarkStance()
    {
        if (RequestedMode != InputMode.ViGEm) return;
        ReportSend(ViGEmInput.ClearRightStickOverride());
    }

    /// <summary>
    /// Sends one directional light while restoring the controller guard stick afterwards.
    /// Keyboard modes use the existing matching guard bind around the RB/light input.
    /// </summary>
    public static bool DirectionalLight(int guardKey)
    {
        if (!TryGetGuardDirection(guardKey, out short x, out short y))
        {
            ReportSend(false);
            return false;
        }

        if (RequestedMode == InputMode.ViGEm)
        {
            if (!ViGEmInput.IsAvailable)
            {
                ReportSend(false);
                return false;
            }

            bool sent = ViGEmInput.SetRightStickOverride(x, y);
            ReportSend(sent);
            if (!sent) return false;
            try
            {
                sent &= MouseClick(VK_LBUTTON);
            }
            finally
            {
                bool restored = ViGEmInput.ClearRightStickOverride();
                ReportSend(restored);
                sent &= restored;
            }
            return sent;
        }

        bool delivered = true;
        Block(true);
        try
        {
            delivered &= KeyDown(guardKey);
            delivered &= MouseClick(VK_LBUTTON);
        }
        finally
        {
            delivered &= KeyUp(guardKey);
            Block(false);
        }
        return delivered;
    }

    public static void ReleaseAutomationInputs()
    {
        if (RequestedMode == InputMode.ViGEm)
            EndBulwarkStance();

        int[] keys =
        {
            VK_SPACE, VK_LEFT, VK_UP, VK_RIGHT, VK_DOWN, VK_C,
            VK_NUMPAD4, VK_NUMPAD5, VK_NUMPAD6, VK_NUMPAD8, VK_NUMPAD9
        };
        foreach (int key in keys) KeyUp(key);

        if (RequestedMode == InputMode.ViGEm)
        {
            if (ViGEmInput.IsAvailable)
            {
                ReportSend(ViGEmInput.MouseClick(VK_LBUTTON, false));
                ReportSend(ViGEmInput.MouseClick(VK_RBUTTON, false));
            }
        }
        else if (RequestedMode == InputMode.SendInput)
        {
            SendMouse(0x0004u);
            SendMouse(0x0010u);
        }
        else
        {
            Native.mouse_event(0x0004u, 0, 0, 0, UIntPtr.Zero);
            Native.mouse_event(0x0010u, 0, 0, 0, UIntPtr.Zero);
        }

        Native.BlockInput(false);
    }

    private static void SendEventKey(int vk, bool up)
    {
        Native.keybd_event((byte)vk, 0, up ? 0x0002u : 0u, UIntPtr.Zero);
        LastSendResult = 1;
        LastSendError = 0;
        InjectedCount++;
    }

    private static bool TryGetGuardDirection(int guardKey, out short x, out short y)
    {
        switch (guardKey)
        {
            case VK_NUMPAD4: x = -32767; y = 0; return true;
            case VK_NUMPAD8: x = 0; y = 32767; return true;
            case VK_NUMPAD6: x = 32767; y = 0; return true;
            default: x = 0; y = 0; return false;
        }
    }

    private static void SendEventMouse(int vk)
    {
        uint down = vk == VK_LBUTTON ? 0x0002u : 0x0008u;
        uint up = vk == VK_LBUTTON ? 0x0004u : 0x0010u;
        Native.mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(MouseTapDelayMs);
        Native.mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        LastSendResult = 1;
        LastSendError = 0;
        InjectedCount += 2;
    }

    private static InputMode ReadMode()
    {
        return Environment.GetEnvironmentVariable("HAPPYBOT_INPUT_MODE")?.Trim().ToLowerInvariant() switch
        {
            "vigem" => InputMode.ViGEm,
            "sendinput" => InputMode.SendInput,
            _ => InputMode.Event
        };
    }

    private static void ReportSend(bool ok)
    {
        if (ok)
        {
            LastSendResult = 1;
            LastSendError = 0;
            InjectedCount++;
        }
        else
        {
            LastSendResult = 0;
            LastSendError = -1;
        }
    }

    private static bool SendKey(int vk, bool up)
    {
        var input = new Native.INPUT
        {
            type = 1,
            U = new Native.INPUT_UNION
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    dwFlags = up ? 0x0002u : 0u
                }
            }
        };
        uint sent = Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
        LastSendResult = sent;
        LastSendError = sent == 0 ? Marshal.GetLastWin32Error() : 0;
        if (sent > 0) InjectedCount++;
        return sent > 0;
    }

    private static uint SendMouse(uint flags)
    {
        var input = new Native.INPUT
        {
            type = 0,
            U = new Native.INPUT_UNION
            {
                mi = new Native.MOUSEINPUT
                {
                    dwFlags = flags
                }
            }
        };
        uint sent = Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
        LastSendResult = sent;
        LastSendError = sent == 0 ? Marshal.GetLastWin32Error() : 0;
        if (sent > 0) InjectedCount++;
        return sent;
    }
}
