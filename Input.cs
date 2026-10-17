namespace HappyBot;

public static class Input
{
    private const int KeyTapDelayMs = 30;
    private const int MouseTapDelayMs = 30;

    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_SPACE = 0x20;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;
    public const int VK_C = 0x43;
    public const int VK_NUMPAD4 = 0x64;
    public const int VK_NUMPAD5 = 0x65;
    public const int VK_NUMPAD6 = 0x66;
    public const int VK_NUMPAD8 = 0x68;
    public const int VK_NUMPAD9 = 0x69;

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
        if (HoldButton == "RT") return false;
        return ViGEmInput.TryGetSourceState(out var state) && state.bRightTrigger > 32;
    }

    /// <summary>
    /// Reads the physical controller's RB/light state for outgoing orange
    /// light attribution. RB is excluded when it is configured as the hold
    /// gate, just like RT above.
    /// </summary>
    public static bool PhysicalLightAttackHeld()
    {
        if (HoldButton == "RB") return false;
        return ViGEmInput.TryGetSourceState(out var state) && (state.wButtons & 0x0200) != 0;
    }

    /// <summary>Reads the physical controller's forwarded left stick.</summary>
    public static bool MovingForwardHeld() =>
        ViGEmInput.TryGetSourceState(out var state) && state.sThumbLY >= 12000;

    public static long InjectedCount;
    public static uint LastSendResult = 1;
    public static int LastSendError;

    public static string ActiveMode => ViGEmInput.IsAvailable ? "ViGEm · Direct DS4" : "ViGEm unavailable";

    public static bool IsReady => ViGEmInput.IsAvailable && ViGEmInput.SourceConnected;

    public static bool CanSendBulwark => ViGEmInput.IsAvailable;

    public static bool KeyDown(int vk)
    {
        bool sent = ViGEmInput.IsAvailable && ViGEmInput.Key(vk, true);
        ReportSend(sent);
        return sent;
    }

    public static bool KeyUp(int vk)
    {
        bool sent = ViGEmInput.IsAvailable && ViGEmInput.Key(vk, false);
        ReportSend(sent);
        return sent;
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

    /// <summary>Black Prior's Bulwark Stance is controller-native: right stick down.</summary>
    public static bool BeginBulwarkStance()
    {
        if (!ViGEmInput.IsAvailable)
        {
            ReportSend(false);
            return false;
        }

        bool sent = ViGEmInput.SetRightStickOverride(0, -32767);
        ReportSend(sent);
        return sent;
    }

    public static void EndBulwarkStance() => ReportSend(ViGEmInput.ClearRightStickOverride());

    /// <summary>Sends one directional light while restoring the controller guard stick afterwards.</summary>
    public static bool DirectionalLight(int guardKey)
    {
        if (!TryGetGuardDirection(guardKey, out short x, out short y) || !ViGEmInput.IsAvailable)
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

    public static void ReleaseAutomationInputs()
    {
        EndBulwarkStance();

        int[] keys =
        {
            VK_SPACE, VK_LEFT, VK_UP, VK_RIGHT, VK_DOWN, VK_C,
            VK_NUMPAD4, VK_NUMPAD5, VK_NUMPAD6, VK_NUMPAD8, VK_NUMPAD9
        };
        foreach (int key in keys) KeyUp(key);

        if (ViGEmInput.IsAvailable)
        {
            ReportSend(ViGEmInput.MouseClick(VK_LBUTTON, false));
            ReportSend(ViGEmInput.MouseClick(VK_RBUTTON, false));
        }
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
}
