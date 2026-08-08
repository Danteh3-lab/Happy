using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HappyBot;

public sealed class KeyboardHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc;
    private readonly IntPtr _hook;
    private readonly Action<int, bool> _callback;

    public KeyboardHook(Action<int, bool> callback)
    {
        _callback = callback;
        _proc = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(module.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (wParam == (IntPtr)Native.WM_KEYDOWN)
                _callback((int)info.vkCode, true);
            else if (wParam == (IntPtr)Native.WM_KEYUP)
                _callback((int)info.vkCode, false);
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            Native.UnhookWindowsHookEx(_hook);
    }
}
