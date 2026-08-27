using HappyBot;

internal static class LiveSettingsRegression
{
    internal static void VerifyLiveSwitchCopy()
    {
        var runtime = new Settings
        {
            Pause = 125,
            LegitParryChance = 55,
            Autoblock = false,
            Parry = false,
            OrangeParry = false,
            YourHero = false
        };
        runtime.Chars["Warden"] = true;

        Settings editor = runtime.Clone();
        editor.Pause = 900;
        editor.LegitParryChance = 90;
        editor.Autoblock = true;
        editor.Parry = true;
        editor.OrangeParry = true;
        editor.YourHero = true;
        editor.Chars["Warden"] = false;
        editor.Chars["Orochi"] = true;

        runtime.CopyLiveSwitchesFrom(editor);

        if (!runtime.Autoblock || !runtime.Parry || !runtime.OrangeParry || !runtime.YourHero)
            throw new InvalidOperationException("Live checkbox settings were not copied to the runtime snapshot.");
        if (runtime.Pause != 125 || runtime.LegitParryChance != 55)
            throw new InvalidOperationException("Staged numeric settings must not be copied by the live-switch path.");
        if (runtime.Ch("Warden") || !runtime.Ch("Orochi"))
            throw new InvalidOperationException("Live hero selection was not copied correctly.");

        editor.Chars["Orochi"] = false;
        if (!runtime.Ch("Orochi"))
            throw new InvalidOperationException("Live hero settings must be copied independently of the editor snapshot.");
    }
}
