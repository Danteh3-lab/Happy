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
    public string AutoDodgeBind = "";

    public bool DodgeH;
    public bool DodgeL;
    public bool Leftdodge;
    public bool Rightdodge;
    public bool Unblockables;
    public bool OrangeLight;
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
}
