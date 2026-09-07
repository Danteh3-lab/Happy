using System.Text.Json;

namespace HappyBot;

/// <summary>
/// Pure persistence mappings between <see cref="Settings"/>, profile INI
/// keys, and UI JSON. No form, bot, or store access; fully unit-testable.
/// </summary>
internal static class SettingsCodec
{
    public const int MaxDelayMs = 10000;
    public const int PeacekeeperDeflectDelayMs = 100;

    public static readonly string[] EditKeys = { "res1", "res2", "Pause", "Pause1", "Pause2", "Pause3", "ParryDelay", "LegitParryChance", "CrushingFallbackChance", "DeflectFallbackChance", "GuardHold", "Left", "Right", "TopDeflect", "AutoDodgeBind" };

    public static readonly string[] CheckKeys =
    {
        "DodgeL", "DodgeH", "Leftdodge", "Rightdodge", "Unblockables", "OrangeLight", "OrangeParry", "Autoblock", "Lightbash",
        "Parry", "Crushing", "Deflect", "Parry2", "Crushing2", "Nohero", "YourHero", "Legit", "BulwarkFallback",
        "Warden", "Peacekeeper", "Centurion", "Blackprior", "Gryphon", "Conqueror", "Lawbringer", "Gladiator", "Warmonger",
        "Raider", "Berserker", "Highlander", "Jormungandr", "Warlord", "Valkyrie", "Shaman", "Varangian", "Null",
        "Kensei", "Orochi", "Shinobi", "Hitokiri", "Sohei", "Shugoki", "Nobushi", "Aramusha", "Kyoshin",
        "Tiandi", "Nuxia", "Zhanhu", "Jiangjun", "Shaolin", "Juren",
        "Pirate", "Afeera", "Medjay", "Khatun", "Ocelotl", "Virtuosa"
    };

    public static readonly string[] HeroKeys = new Settings().Chars.Keys.ToArray();

    public static Dictionary<string, object> ToSnapshot(Settings s)
    {
        var values = new Dictionary<string, object>
        {
            ["res1"] = s.Res1,
            ["res2"] = s.Res2,
            ["Pause"] = s.Pause,
            ["Pause1"] = s.Pause1,
            ["Pause2"] = s.Pause2,
            ["Pause3"] = s.Pause3,
            ["ParryDelay"] = s.ParryDelay,
            ["LegitParryChance"] = s.LegitParryChance,
            ["CrushingFallbackChance"] = s.CrushingFallbackChance,
            ["DeflectFallbackChance"] = s.DeflectFallbackChance,
            ["GuardHold"] = s.GuardHold,
            ["Left"] = s.Left,
            ["Right"] = s.Right,
            ["TopDeflect"] = s.TopDeflect,
            ["AutoDodgeBind"] = s.AutoDodgeBind
        };
        foreach (string key in CheckKeys) values[key] = GetCheck(s, key);
        return values;
    }

    /// <summary>Merges UI JSON over a base snapshot with validation and clamps.</summary>
    public static Settings ApplyJson(Settings baseSettings, JsonElement values)
    {
        Settings editor = baseSettings.Clone();
        editor.Res1 = ReadString(values, "res1", editor.Res1);
        editor.Res2 = ReadString(values, "res2", editor.Res2);
        editor.Pause = ClampDelay(ReadInt(values, "Pause", editor.Pause));
        editor.Pause1 = ClampDelay(ReadInt(values, "Pause1", editor.Pause1));
        editor.Pause2 = ClampDelay(ReadInt(values, "Pause2", editor.Pause2));
        editor.Pause3 = ClampDelay(ReadInt(values, "Pause3", editor.Pause3));
        editor.ParryDelay = ClampDelay(ReadInt(values, "ParryDelay", editor.ParryDelay));
        editor.LegitParryChance = Math.Clamp(ReadInt(values, "LegitParryChance", editor.LegitParryChance), 0, 100);
        editor.CrushingFallbackChance = Math.Clamp(ReadInt(values, "CrushingFallbackChance", editor.CrushingFallbackChance), 0, 100);
        editor.DeflectFallbackChance = Math.Clamp(ReadInt(values, "DeflectFallbackChance", editor.DeflectFallbackChance), 0, 100);
        editor.GuardHold = Math.Clamp(ReadInt(values, "GuardHold", editor.GuardHold), 60, MaxDelayMs);
        editor.Left = ClampDelay(ReadInt(values, "Left", editor.Left));
        editor.Right = ClampDelay(ReadInt(values, "Right", editor.Right));
        editor.TopDeflect = ClampDelay(ReadInt(values, "TopDeflect", editor.TopDeflect));
        editor.AutoDodgeBind = ReadString(values, "AutoDodgeBind", editor.AutoDodgeBind).Trim();
        foreach (string key in CheckKeys)
        {
            if (values.TryGetProperty(key, out _))
                SetCheck(editor, key, ReadBool(values, key, GetCheck(editor, key)));
        }

        NormalizeHeroSelection(editor);
        return editor;
    }

    public static bool TryParseResolution(Settings s, out int width, out int height)
    {
        width = int.TryParse(s.Res1, out int parsedWidth) ? parsedWidth : 0;
        height = int.TryParse(s.Res2, out int parsedHeight) ? parsedHeight : 0;
        return width > 0 && height > 0 && width >= height;
    }

    public static bool GetCheck(Settings s, string key)
    {
        return key switch
        {
            "DodgeL" => s.DodgeL,
            "DodgeH" => s.DodgeH,
            "Leftdodge" => s.Leftdodge,
            "Rightdodge" => s.Rightdodge,
            "Unblockables" => s.Unblockables,
            "OrangeLight" => s.OrangeLight,
            "OrangeParry" => s.OrangeParry,
            "Autoblock" => s.Autoblock,
            "Lightbash" => s.Lightbash,
            "Parry" => s.Parry,
            "Crushing" => s.Crushing,
            "Deflect" => s.Deflect,
            "Parry2" => s.Parry2,
            "Crushing2" => s.Crushing2,
            "Nohero" => s.Nohero,
            "YourHero" => s.YourHero,
            "Legit" => s.Legit,
            "BulwarkFallback" => s.BulwarkFallback,
            _ => s.Ch(key)
        };
    }

    public static void SetCheck(Settings s, string key, bool value)
    {
        switch (key)
        {
            case "DodgeL": s.DodgeL = value; break;
            case "DodgeH": s.DodgeH = value; break;
            case "Leftdodge": s.Leftdodge = value; if (value) s.Rightdodge = false; break;
            case "Rightdodge": s.Rightdodge = value; if (value) s.Leftdodge = false; break;
            case "Unblockables": s.Unblockables = value; break;
            case "OrangeLight": s.OrangeLight = value; break;
            case "OrangeParry": s.OrangeParry = value; break;
            case "Autoblock": s.Autoblock = value; break;
            case "Lightbash": s.Lightbash = value; break;
            case "Parry": s.Parry = value; break;
            case "Crushing": s.Crushing = value; break;
            case "Deflect": s.Deflect = value; break;
            case "Parry2": s.Parry2 = value; break;
            case "Crushing2": s.Crushing2 = value; break;
            case "Nohero": s.Nohero = value; break;
            case "YourHero": s.YourHero = value; break;
            case "Legit": s.Legit = value; break;
            case "BulwarkFallback": s.BulwarkFallback = value; break;
            default: s.Chars[key] = value; break;
        }
    }

    public static void NormalizeHeroSelection(Settings s)
    {
        string selected = HeroKeys.FirstOrDefault(s.Ch);
        if (selected == null) return;
        foreach (string hero in HeroKeys) s.Chars[hero] = hero.Equals(selected, StringComparison.OrdinalIgnoreCase);
    }

    public static void ApplyPeacekeeperRuntimeOverride(Settings settings)
    {
        if (!settings.Ch("Peacekeeper")) return;
        settings.Left = PeacekeeperDeflectDelayMs;
        settings.Right = PeacekeeperDeflectDelayMs;
    }

    public static int ClampDelay(int value) => Math.Clamp(value, 0, MaxDelayMs);

    public static string GetEdit(Settings s, string key)
    {
        return key switch
        {
            "res1" => s.Res1,
            "res2" => s.Res2,
            "Pause" => s.Pause.ToString(),
            "Pause1" => s.Pause1.ToString(),
            "Pause2" => s.Pause2.ToString(),
            "Pause3" => s.Pause3.ToString(),
            "ParryDelay" => s.ParryDelay.ToString(),
            "LegitParryChance" => s.LegitParryChance.ToString(),
            "CrushingFallbackChance" => s.CrushingFallbackChance.ToString(),
            "DeflectFallbackChance" => s.DeflectFallbackChance.ToString(),
            "GuardHold" => s.GuardHold.ToString(),
            "Left" => s.Left.ToString(),
            "Right" => s.Right.ToString(),
            "TopDeflect" => s.TopDeflect.ToString(),
            "AutoDodgeBind" => s.AutoDodgeBind,
            _ => ""
        };
    }

    public static void SetEdit(Settings s, string key, string value)
    {
        switch (key)
        {
            case "res1": s.Res1 = value; break;
            case "res2": s.Res2 = value; break;
            case "Pause": s.Pause = ClampDelay(ToInt(value)); break;
            case "Pause1": s.Pause1 = ClampDelay(ToInt(value)); break;
            case "Pause2": s.Pause2 = ClampDelay(ToInt(value)); break;
            case "Pause3": s.Pause3 = ClampDelay(ToInt(value)); break;
            case "ParryDelay": s.ParryDelay = ClampDelay(ToInt(value)); break;
            case "LegitParryChance": s.LegitParryChance = Math.Clamp(ToInt(value), 0, 100); break;
            case "CrushingFallbackChance": s.CrushingFallbackChance = Math.Clamp(ToInt(value), 0, 100); break;
            case "DeflectFallbackChance": s.DeflectFallbackChance = Math.Clamp(ToInt(value), 0, 100); break;
            case "GuardHold": s.GuardHold = Math.Clamp(ToInt(value), 60, MaxDelayMs); break;
            case "Left": s.Left = ClampDelay(ToInt(value)); break;
            case "Right": s.Right = ClampDelay(ToInt(value)); break;
            case "TopDeflect": s.TopDeflect = ClampDelay(ToInt(value)); break;
            case "AutoDodgeBind": s.AutoDodgeBind = value.Trim(); break;
        }
    }

    public static int ToInt(string value) => int.TryParse(value, out int number) ? number : 0;

    public static string ReadString(JsonElement values, string key, string fallback)
    {
        if (!values.TryGetProperty(key, out JsonElement value)) return fallback;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
    }

    public static int ReadInt(JsonElement values, string key, int fallback)
    {
        if (!values.TryGetProperty(key, out JsonElement value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        return int.TryParse(value.ToString(), out number) ? number : fallback;
    }

    public static bool ReadBool(JsonElement values, string key, bool fallback)
    {
        if (!values.TryGetProperty(key, out JsonElement value)) return fallback;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return value.ToString() is "1" or "true" or "True";
    }
}
