namespace HappyBot;

internal static class BuildInfo
{
    public const string Version = "2.0.3";

    public static string Configuration
    {
#if DEBUG
        get => "Debug";
#else
        get => "Release";
#endif
    }

    public static string Display => $"v{Version} · {Configuration}";
}
