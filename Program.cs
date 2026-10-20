using System.Runtime.InteropServices;
using HappyBot.Update;

namespace HappyBot;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static readonly IntPtr DpiAwarenessContextPerMonitorV2 = new(-4);

    [STAThread]
    private static void Main(string[] args)
    {
        SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorV2);
        ApplicationConfiguration.Initialize();

        // Before the virtual controller exists: a newer DANBOT may take over from here.
        if (AutoUpdater.UpdateAndRelaunch(args)) return;

        try
        {
            if (string.IsNullOrEmpty(Config.Read("HoldButton"))) Config.Write("HoldButton", "LT");
            ViGEmInput.Init();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show("DANBOT could not start.\n\n" + ex.Message, "DANBOT Startup Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ViGEmInput.Close();
        }
    }
}
