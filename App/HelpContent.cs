namespace HappyBot.App;

/// <summary>
/// Text for the legacy `howto` / `readme` dialogs. The control deck shows the
/// same guidance in its Help panel; these stay for pages that still ask the host.
/// </summary>
internal static class HelpContent
{
    public const string HowTo =
        "IN GAME SETTINGS\n\n" +
        "1) Set FOV to 81 and contrast to 55.\n" +
        "2) Disable shadows, motion blur, ambient occlusion, dynamic reflections, and dynamic shadows.\n" +
        "3) Match the menu resolution to the game render.\n" +
        "4) Use fullscreen or borderless fullscreen, not windowed mode.\n" +
        "5) Hide physical/source controllers with HidHide and leave the ViGEm output visible.\n" +
        "6) Hold LT on the DS4 before an attack for parry/counter actions. Orange handling runs automatically when enabled; own controller RT/RB attacks are ignored until their orange clears. Controller & Binds can bind controller buttons for Auto dodge, Orange parry, and Auto parry.\n" +
        "7) The Vision overlay switch shows the diagnostic overlay. It is click-through and does not change bot behavior.";

    public const string ReadMe =
        "FEATURES\n\n" +
        "- Screen-aware orange and red indicator detection\n" +
        "- Orange-only dodge/light and optional orange plus red RT parry\n" +
        "- Own controller RT/RB attacks are excluded from orange responses\n" +
        "- Auto block and directional guard\n" +
        "- Hero-specific evades and reactions (deflect follow-ups: RB light, Orochi RT heavy, Shinobi X)\n" +
        "- Direct DS4 input merged into the ViGEm output\n" +
        "- Configurable reaction delays\n" +
        "- Anchor-following vision overlay\n" +
        "\nDANBOT by Danteh. The UI is a WebView2 shell over the existing C# bot core.";
}
