using System.Runtime.InteropServices;
using System.Text;

namespace HappyBot;

public static class Config
{
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "Config.ini");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPrivateProfileString(string app, string key, string def, StringBuilder ret, int size, string file);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WritePrivateProfileString(string app, string key, string val, string file);

    public static string Read(string key)
    {
        var sb = new StringBuilder(512);
        GetPrivateProfileString("Options", key, "", sb, sb.Capacity, Path);
        return sb.ToString();
    }

    public static void Write(string key, string value) => WritePrivateProfileString("Options", key, value, Path);
}
