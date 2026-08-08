using System.Runtime.InteropServices;

namespace HappyBot;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static readonly IntPtr DpiAwarenessContextPerMonitorV2 = new(-4);

    [STAThread]
    private static void Main()
    {
        SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorV2);
        if (string.IsNullOrEmpty(Config.Read("HoldButton"))) Config.Write("HoldButton", "LT");
        InterceptionInput.Init();
        ViGEmInput.Init();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        ViGEmInput.Shutdown();
        InterceptionInput.Shutdown();
    }
}
