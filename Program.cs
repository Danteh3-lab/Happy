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
        try
        {
            if (string.IsNullOrEmpty(Config.Read("HoldButton"))) Config.Write("HoldButton", "LT");
            ViGEmInput.Init();
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show("DANBOT could not start.\n\n" + ex.Message, "DANBOT Startup Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ViGEmInput.Shutdown();
        }
    }
}
