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

    public bool LiveSwitchesEqual(Settings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (DodgeH != other.DodgeH || DodgeL != other.DodgeL ||
            Leftdodge != other.Leftdodge || Rightdodge != other.Rightdodge ||
            Unblockables != other.Unblockables || OrangeLight != other.OrangeLight ||
            OrangeParry != other.OrangeParry || Autoblock != other.Autoblock ||
            Lightbash != other.Lightbash || Parry != other.Parry ||
            Crushing != other.Crushing || Deflect != other.Deflect ||
            Parry2 != other.Parry2 || Crushing2 != other.Crushing2 ||
            Nohero != other.Nohero || YourHero != other.YourHero ||
            Legit != other.Legit || BulwarkFallback != other.BulwarkFallback ||
            Chars.Count != other.Chars.Count)
            return false;

        foreach ((string key, bool value) in Chars)
        {
            if (!other.Chars.TryGetValue(key, out bool otherValue) || otherValue != value)
                return false;
        }
        return true;
    }

    public void CopyLiveSwitchesFrom(Settings source)
    {
        ArgumentNullException.ThrowIfNull(source);
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
        Chars = new Dictionary<string, bool>(source.Chars, StringComparer.OrdinalIgnoreCase);
    }

    public void CopyFrom(Settings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Res1 = source.Res1;
        Res2 = source.Res2;
        Pause = source.Pause;
        Pause1 = source.Pause1;
        Pause2 = source.Pause2;
        Pause3 = source.Pause3;
        ParryDelay = source.ParryDelay;
        LegitParryChance = source.LegitParryChance;
        GuardHold = source.GuardHold;
        Left = source.Left;
        Right = source.Right;
        TopDeflect = source.TopDeflect;
        AutoDodgeBind = source.AutoDodgeBind;
        CrushingFallbackChance = source.CrushingFallbackChance;
        DeflectFallbackChance = source.DeflectFallbackChance;
        CopyLiveSwitchesFrom(source);
    }
}
