namespace HappyBot;

public sealed class Settings
{
    public string Res1 = "0";
    public string Res2 = "0";

    public int Pause;
    public int Pause1;
    public int Pause2;
    public int Pause3;
    public int ParryDelay;
    public int LegitParryChance = 55;
    public int GuardHold = 750;
    public int Left;
    public int Right;
    public int TopDeflect;
    public string AutoDodgeBind = "";
    public string OrangeParryBind = "";
    public string AutoParryBind = "";

    public bool DodgeH;
    public bool DodgeL;
    public bool Leftdodge;
    public bool Rightdodge;
    public bool Unblockables;
    public bool OrangeLight;
    public bool OrangeParry;
    public bool Autoblock;
    public bool Lightbash;
    public bool Parry;
    public bool Crushing;
    public bool Deflect;
    public bool Parry2;
    public bool Crushing2;
    public bool Nohero;
    public bool YourHero;
    public bool Legit;
    public bool BulwarkFallback;
    public bool IgnoreOwnOrange = true;
    public int CrushingFallbackChance = 50;
    public int DeflectFallbackChance = 50;

    public Dictionary<string, bool> Chars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Warden"] = false, ["Peacekeeper"] = false, ["Centurion"] = false, ["Blackprior"] = false,
        ["Gryphon"] = false, ["Conqueror"] = false, ["Lawbringer"] = false, ["Gladiator"] = false,
        ["Warmonger"] = false,
        ["Raider"] = false, ["Berserker"] = false, ["Highlander"] = false, ["Jormungandr"] = false,
        ["Warlord"] = false, ["Valkyrie"] = false, ["Shaman"] = false, ["Varangian"] = false,
        ["Null"] = false,
        ["Kensei"] = false, ["Orochi"] = false, ["Shinobi"] = false, ["Hitokiri"] = false,
        ["Sohei"] = false, ["Shugoki"] = false, ["Nobushi"] = false, ["Aramusha"] = false,
        ["Kyoshin"] = false,
        ["Tiandi"] = false, ["Nuxia"] = false, ["Zhanhu"] = false, ["Jiangjun"] = false,
        ["Shaolin"] = false, ["Juren"] = false,
        ["Pirate"] = false, ["Afeera"] = false, ["Medjay"] = false, ["Khatun"] = false,
        ["Ocelotl"] = false, ["Virtuosa"] = false
    };

    public bool Ch(string name) => Chars.TryGetValue(name, out bool v) && v;

    public Settings Clone()
    {
        var copy = (Settings)MemberwiseClone();
        copy.Chars = new Dictionary<string, bool>(Chars, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    public void CopyLiveSwitchesFrom(Settings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        AutoDodgeBind = source.AutoDodgeBind;
        OrangeParryBind = source.OrangeParryBind;
        AutoParryBind = source.AutoParryBind;
        DodgeH = source.DodgeH;
        DodgeL = source.DodgeL;
        Leftdodge = source.Leftdodge;
        Rightdodge = source.Rightdodge;
        Unblockables = source.Unblockables;
        OrangeLight = source.OrangeLight;
        OrangeParry = source.OrangeParry;
        Autoblock = source.Autoblock;
        Lightbash = source.Lightbash;
        Parry = source.Parry;
        Crushing = source.Crushing;
        Deflect = source.Deflect;
        Parry2 = source.Parry2;
        Crushing2 = source.Crushing2;
        Nohero = source.Nohero;
        YourHero = source.YourHero;
        Legit = source.Legit;
        BulwarkFallback = source.BulwarkFallback;
        IgnoreOwnOrange = source.IgnoreOwnOrange;
        Chars = new Dictionary<string, bool>(source.Chars, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every numeric value that waits for Apply before reaching the live bot.
    /// Copying and dirty-checking both read this table so they cannot drift.
    /// </summary>
    internal static readonly (string Key, Func<Settings, int> Get, Action<Settings, int> Set)[] TimingFields =
    {
        ("Pause", s => s.Pause, (s, v) => s.Pause = v),
        ("Pause1", s => s.Pause1, (s, v) => s.Pause1 = v),
        ("Pause2", s => s.Pause2, (s, v) => s.Pause2 = v),
        ("Pause3", s => s.Pause3, (s, v) => s.Pause3 = v),
        ("ParryDelay", s => s.ParryDelay, (s, v) => s.ParryDelay = v),
        ("LegitParryChance", s => s.LegitParryChance, (s, v) => s.LegitParryChance = v),
        ("CrushingFallbackChance", s => s.CrushingFallbackChance, (s, v) => s.CrushingFallbackChance = v),
        ("DeflectFallbackChance", s => s.DeflectFallbackChance, (s, v) => s.DeflectFallbackChance = v),
        ("GuardHold", s => s.GuardHold, (s, v) => s.GuardHold = v),
        ("Left", s => s.Left, (s, v) => s.Left = v),
        ("Right", s => s.Right, (s, v) => s.Right = v),
        ("TopDeflect", s => s.TopDeflect, (s, v) => s.TopDeflect = v)
    };

    public bool TimingsEqual(Settings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var field in TimingFields)
        {
            if (field.Get(this) != field.Get(other)) return false;
        }
        return true;
    }

    public void CopyTimingsFrom(Settings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var field in TimingFields) field.Set(this, field.Get(source));
    }

    public void CopyFrom(Settings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Res1 = source.Res1;
        Res2 = source.Res2;
        CopyTimingsFrom(source);
        CopyLiveSwitchesFrom(source);
    }
}
