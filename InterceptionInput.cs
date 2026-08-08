using System.Runtime.InteropServices;

namespace HappyBot;

public static class InterceptionInput
{
    private const int MouseStrokeSize = 20;

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr interception_create_context();

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void interception_destroy_context(IntPtr context);

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int interception_send(IntPtr context, int device, byte[] stroke, uint nstroke);

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint interception_get_hardware_id(IntPtr context, int device, IntPtr hardwareIdBuffer, uint bufferSize);

    private const ushort KeyDown = 0x00;
    private const ushort KeyUp = 0x01;
    private const ushort KeyE0 = 0x02;

    private const ushort MouseLeftDown = 0x001;
    private const ushort MouseLeftUp = 0x002;
    private const ushort MouseRightDown = 0x004;
    private const ushort MouseRightUp = 0x008;

    private const int KeyboardDevice = 1;
    private const int MouseDevice = 11;

    private static IntPtr _context;
    private static bool _available;
    private static int _keyboardDevice = KeyboardDevice;
    private static int _mouseDevice = MouseDevice;

    public static bool IsAvailable => _available;

    public static void Init()
    {
        try
        {
            _context = interception_create_context();
            if (_context != IntPtr.Zero)
            {
                _keyboardDevice = FindDevice(false);
                _mouseDevice = FindDevice(true);
                _available = _keyboardDevice > 0 && _mouseDevice > 0;
            }
        }
        catch
        {
            _available = false;
        }
    }

    public static void Shutdown()
    {
        if (_context != IntPtr.Zero)
        {
            interception_destroy_context(_context);
            _context = IntPtr.Zero;
            _available = false;
        }
    }

    public static bool Key(int vk, bool down)
    {
        if (!_available) return false;
        (byte code, bool extended) = ScanCode(vk);
        if (code == 0) return false;
        ushort state = down ? KeyDown : KeyUp;
        if (extended) state |= KeyE0;
        var stroke = new byte[8];
        BitConverter.GetBytes((ushort)code).CopyTo(stroke, 0);
        BitConverter.GetBytes(state).CopyTo(stroke, 2);
        return interception_send(_context, _keyboardDevice, stroke, 1) > 0;
    }

    public static bool MouseClick(int vk, bool down)
    {
        if (!_available) return false;
        ushort state;
        switch (vk)
        {
            case Input.VK_LBUTTON: state = down ? MouseLeftDown : MouseLeftUp; break;
            case Input.VK_RBUTTON: state = down ? MouseRightDown : MouseRightUp; break;
            default: return false;
        }
        var stroke = new byte[MouseStrokeSize];
        BitConverter.GetBytes(state).CopyTo(stroke, 0);
        return interception_send(_context, _mouseDevice, stroke, 1) > 0;
    }

    private static int FindDevice(bool mouse)
    {
        int start = mouse ? 11 : 1;
        int end = mouse ? 20 : 10;
        for (int i = start; i <= end; i++)
        {
            if (GetHardwareId(i) != "") return i;
        }
        return 0;
    }

    private static string GetHardwareId(int device)
    {
        var buffer = Marshal.AllocHGlobal(512 * sizeof(char));
        try
        {
            uint length = interception_get_hardware_id(_context, device, buffer, 512 * sizeof(char));
            if (length == 0 || length >= 512 * sizeof(char)) return "";
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (byte code, bool extended) ScanCode(int vk)
    {
        switch (vk)
        {
            case Input.VK_SPACE: return (0x39, false);
            case Input.VK_C: return (0x2E, false);
            case Input.VK_A: return (0x1E, false);
            case Input.VK_D: return (0x20, false);
            case Input.VK_S: return (0x1F, false);
            case Input.VK_W: return (0x11, false);
            case Input.VK_LEFT: return (0x4B, true);
            case Input.VK_UP: return (0x48, true);
            case Input.VK_RIGHT: return (0x4D, true);
            case Input.VK_DOWN: return (0x50, true);
            case Input.VK_NUMPAD3: return (0x51, false);
            case Input.VK_NUMPAD4: return (0x4B, false);
            case Input.VK_NUMPAD5: return (0x4C, false);
            case Input.VK_NUMPAD6: return (0x4D, false);
            case Input.VK_NUMPAD8: return (0x48, false);
            case Input.VK_NUMPAD9: return (0x49, false);
            default: return (0, false);
        }
    }
}
