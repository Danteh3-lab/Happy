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
    public const int VK_R = 0x52;
    public const int VK_S = 0x53;
    public const int VK_W = 0x57;
    public const int VK_NUMPAD3 = 0x63;
    public const int VK_NUMPAD4 = 0x64;
    public const int VK_NUMPAD5 = 0x65;
    public const int VK_NUMPAD6 = 0x66;
    public const int VK_NUMPAD8 = 0x68;
    public const int VK_NUMPAD9 = 0x69;
    public const int VK_LSHIFT = 0xA0;

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

    public static long InjectedCount;
    public static uint LastSendResult = 1;
    public static int LastSendError;

    public static string ActiveMode =>
        RequestedMode == InputMode.ViGEm ? (ViGEmInput.IsAvailable ? "ViGEm" : "ViGEm unavailable")
            : RequestedMode == InputMode.SendInput ? "SendInput" : "Event";

    public static bool IsReady => RequestedMode != InputMode.ViGEm || ViGEmInput.IsAvailable;

    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static void KeyDown(int vk)
    {
        if (RequestedMode == InputMode.ViGEm)
        {
            ReportSend(ViGEmInput.IsAvailable && ViGEmInput.Key(vk, true));
            return;
        }

        if (RequestedMode != InputMode.SendInput)
        {
            SendEventKey(vk, false);
            return;
        }

        SendKey(vk, false);
    }

    public static void KeyUp(int vk)
    {
        if (RequestedMode == InputMode.ViGEm)
        {
            ReportSend(ViGEmInput.IsAvailable && ViGEmInput.Key(vk, false));
            return;
        }

        if (RequestedMode != InputMode.SendInput)
        {
            SendEventKey(vk, true);
            return;
        }

        SendKey(vk, true);
    }

    public static void KeyTap(int vk)
    {
        KeyTap(vk, KeyTapDelayMs);
    }

    public static void KeyTap(int vk, int holdMs)
    {
        KeyDown(vk);
        Thread.Sleep(holdMs);
        KeyUp(vk);
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

    public static void ReleaseAutomationInputs()
    {
        int[] keys =
        {
            VK_SPACE, VK_LEFT, VK_UP, VK_RIGHT, VK_DOWN, VK_C,
            VK_NUMPAD3, VK_NUMPAD4, VK_NUMPAD5, VK_NUMPAD6, VK_NUMPAD8, VK_NUMPAD9
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

    private static void SendKey(int vk, bool up)
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
