namespace HappyBot.Combat;

/// <summary>
/// User-facing explanation of the same priorities enforced by
/// <see cref="ReactionPolicy"/> and the reaction executor.
/// </summary>
internal sealed record ReactionBehaviorSummary(
    string Hero,
    bool HeroResponseEnabled,
    string HeroMode,
    string FAction,
    string FDetail,
    string EAction,
    string EDetail,
    string HeroBehavior,
    string HeroStatus,
    string HeroRequirements,
    string HeroTimings,
    string[] Notices)
{
    // These descriptions are generated beside the reaction-priority rules so the
    // timing page cannot claim a delay is active when the executor will skip it.
    public string DeflectTiming { get; init; } = "";
    public string ParryTiming { get; init; } = "";
    public string DodgeTiming { get; init; } = "";

    public static ReactionBehaviorSummary Create(Settings settings, bool canSendBulwark)
    {
        string hero = SettingsCodec.HeroKeys.FirstOrDefault(settings.Ch) ?? "None";
        bool heroEnabled = settings.YourHero && !settings.Nohero && hero != "None";
        var notices = new List<string>();

        string configuredF = ConfiguredFAction(settings);
        string fAction;
        string fDetail;
        if (!settings.Autoblock)
        {
            fAction = "Inactive";
            fDetail = configuredF == "None"
                ? "No F/LT reaction is configured."
                : configuredF + " is configured, but Auto Block is required.";
            if (configuredF != "None") notices.Add("Enable Auto Block to arm F/LT reactions.");
        }
        else if (settings.Parry)
        {
            bool wardenTopOverride = heroEnabled && hero == "Warden";
            fAction = settings.Legit
                ? (wardenTopOverride ? "Mixed parry / top Crushing" : "Mixed parry")
                : (wardenTopOverride ? "Parry / top Crushing" : "Parry");
            fDetail = settings.Legit
                ? wardenTopOverride
                    ? $"Top attacks send a crushing counter immediately; sides: {DescribeLegitF(settings, heroEnabled, canSendBulwark)}"
                    : DescribeLegitF(settings, heroEnabled, canSendBulwark)
                : wardenTopOverride
                    ? $"Top attacks send a crushing counter immediately; side attacks use Parry after the flash gate and {settings.ParryDelay} ms delay."
                    : $"Parry after the flash gate and {settings.ParryDelay} ms delay.";
        }
        else if (settings.Crushing)
        {
            fAction = "Crushing counter";
            fDetail = "Sends RB immediately after the flash gate.";
        }
        else if (settings.Deflect)
        {
            fAction = "Deflect";
            fDetail = DescribeDeflect(settings, heroEnabled && hero == "Nuxia");
        }
        else if (ReactionPolicy.HasHeroAction(settings))
        {
            fAction = hero + " response";
            fDetail = DescribeHeroBehavior(hero);
        }
        else
        {
            fAction = "None";
            fDetail = "Holding F/LT will only keep the normal guard active.";
        }

        string eAction;
        string eDetail;
        if (!settings.Autoblock && ReactionPolicy.HasEAction(settings))
        {
            eAction = "Inactive";
            eDetail = "An E reaction is configured, but Auto Block is required.";
            notices.Add("Enable Auto Block to arm E reactions.");
        }
        else if (settings.Parry2)
        {
            eAction = settings.Legit ? "Mixed parry" : "Parry";
            eDetail = settings.Legit
                ? $"{settings.LegitParryChance}% parry after {settings.ParryDelay} ms; otherwise block only."
                : $"Parry after the flash gate and {settings.ParryDelay} ms delay.";
        }
        else if (settings.Crushing2)
        {
            eAction = "Crushing counter";
            eDetail = "Sends RB immediately after the flash gate.";
        }
        else
        {
            eAction = "None";
            eDetail = "No reaction is assigned to E hold.";
        }

        string heroStatus = DescribeHeroStatus(settings, hero, heroEnabled);
        if (hero != "None" && !heroStatus.EndsWith("active F/LT action.", StringComparison.Ordinal))
            notices.Add(heroStatus);
        if (settings.BulwarkFallback && heroEnabled && hero == "Blackprior" && !canSendBulwark)
            notices.Add("Bulwark fallback unavailable: the ViGEm controller path is not ready.");
        if (hero != "None" && heroEnabled && !HasKnownHeroBehavior(hero))
            notices.Add(hero + " has no hero-specific response implemented.");

        return new ReactionBehaviorSummary(
            hero,
            heroEnabled,
            DescribeHeroMode(settings, hero, heroEnabled),
            fAction,
            fDetail,
            eAction,
            eDetail,
            DescribeHeroBehavior(hero),
            heroStatus,
            DescribeHeroRequirements(hero),
            DescribeHeroTimings(hero, settings),
            notices.Distinct().ToArray())
        {
            DeflectTiming = DescribeDeflectTiming(settings, hero, heroEnabled),
            ParryTiming = DescribeParryTiming(settings),
            DodgeTiming = DescribeDodgeTiming(settings)
        };
    }

    private static string ConfiguredFAction(Settings settings)
    {
        if (settings.Parry) return settings.Legit ? "Mixed parry" : "Parry";
        if (settings.Crushing) return "Crushing counter";
        if (settings.Deflect) return "Deflect";
        if (ReactionPolicy.HasHeroAction(settings))
            return (SettingsCodec.HeroKeys.FirstOrDefault(settings.Ch) ?? "Hero") + " response";
        return "None";
    }

    private static string DescribeLegitF(Settings settings, bool heroEnabled, bool canSendBulwark)
    {
        var fallbacks = new List<string>();
        if (settings.Deflect)
            fallbacks.Add($"{settings.DeflectFallbackChance}% deflect first ({settings.Left}/{settings.TopDeflect}/{settings.Right} ms L/T/R)" +
                (heroEnabled && settings.Ch("Nuxia") ? "; Nuxia top is excluded" : ""));

        bool crushing = settings.Crushing;
        bool bulwark = settings.BulwarkFallback && heroEnabled && settings.Ch("Blackprior") && canSendBulwark;
        if (crushing && bulwark)
            fallbacks.Add($"then {settings.CrushingFallbackChance}% crushing, otherwise Bulwark");
        else if (crushing)
            fallbacks.Add("then crushing");
        else if (bulwark)
            fallbacks.Add("then Bulwark");
        else
            fallbacks.Add("otherwise block only");

        return $"{settings.LegitParryChance}% parry after {settings.ParryDelay} ms; failed rolls: {string.Join(", ", fallbacks)}.";
    }

    private static string DescribeDeflect(Settings settings, bool nuxia)
    {
        string result = $"Directional dodge after the flash gate: {settings.Left} ms left, {settings.TopDeflect} ms top, {settings.Right} ms right.";
        return nuxia ? result + " Nuxia top deflect is disabled." : result;
    }

    private static string DescribeDeflectTiming(Settings settings, string hero, bool heroEnabled)
    {
        if (!settings.Autoblock)
            return "Inactive: Auto Block is disabled, so F/LT and E reactions cannot arm.";
        if (!settings.Deflect)
            return "Inactive: Deflect is disabled, so these values do not affect the selected reaction.";

        bool nuxiaTopBlocked = heroEnabled && hero == "Nuxia";
        if (settings.Parry)
        {
            if (!settings.Legit)
                return "Inactive: normal Parry takes priority for F/LT; these values are not used.";

            return nuxiaTopBlocked
                ? "Legit fallback only: after the Parry roll, left/right use these delays; Nuxia top is excluded while standalone response is enabled."
                : "Legit fallback only: after the Parry roll, an eligible directional deflect uses the matching delay.";
        }
        if (settings.Crushing)
            return "Inactive: Crushing counter takes priority for F/LT; these values are not used.";

        return nuxiaTopBlocked
            ? "Direct F/LT deflect after the flash gate: left/right use these delays; Nuxia top is excluded while standalone response is enabled."
            : "Direct F/LT deflect after the flash gate: the matching directional delay starts after the flash gate.";
    }

    private static string DescribeParryTiming(Settings settings)
    {
        bool fParry = settings.Autoblock && settings.Parry;
        bool eParry = settings.Autoblock && settings.Parry2;
        bool orangeParry = settings.Unblockables && settings.OrangeParry;
        if (!fParry && !eParry && !orangeParry)
            return "Inactive: no selected Parry reaction uses this delay.";

        var channels = new List<string>();
        if (fParry) channels.Add("F/LT");
        if (eParry) channels.Add("E");
        if (orangeParry) channels.Add("orange");
        string channelText = string.Join(", ", channels);
        var flashChannels = new List<string>();
        if (fParry) flashChannels.Add("F/LT");
        if (eParry) flashChannels.Add("E");
        string start = flashChannels.Count > 0 && orangeParry
            ? $"{string.Join("/", flashChannels)} Parry starts after the flash gate; orange Parry starts after the orange Pause/Pause1 gate"
            : orangeParry
                ? "Orange Parry starts after the orange Pause/Pause1 gate"
                : $"{string.Join("/", flashChannels)} Parry starts after the flash gate";
        return $"Active for {channelText}: {start}; {settings.ParryDelay} ms is applied before RT.";
    }

    private static string DescribeDodgeTiming(Settings settings) => settings.Unblockables
        ? "Active for orange responses: Pause/Pause1 starts after orange detection (or the feint transition), and Pause2 starts between the dodge and an enabled follow-up."
        : "Inactive: orange responses are disabled, so these dodge delays do not affect the selected reaction.";

    private static string DescribeHeroStatus(Settings settings, string hero, bool enabled)
    {
        if (hero == "None") return "Choose a hero to see its implemented behavior.";
        if (hero == "Peacekeeper")
            return "Peacekeeper has no standalone hero response. Selecting Peacekeeper forces side deflect delays to 100 ms when timings are applied, even when this switch is off.";
        if (hero == "Warden")
        {
            if (!enabled) return "Warden has no standalone hero response; its top override is disabled.";
            if (!settings.Autoblock) return "Warden top override inactive: Auto Block is disabled.";
            if (settings.Parry) return "Warden top override active: top attacks use an immediate crushing counter; sides use Parry.";
            if (settings.Crushing) return "Warden top override inactive: Crushing counter takes priority.";
            if (settings.Deflect) return "Warden top override inactive: Deflect takes priority.";
            return "Warden has no standalone hero response; enable Parry to activate the top override.";
        }
        if (hero == "Nuxia")
        {
            if (!enabled) return "Nuxia has no standalone hero response; its top-deflect rule is disabled.";
            if (!settings.Autoblock) return "Nuxia top-deflect rule inactive: Auto Block is disabled.";
            if (IsNuxiaDeflectEligible(settings, enabled))
            {
                return settings.Parry
                    ? "Nuxia modifier active in the Legit deflect fallback: Parry is primary; top deflect is blocked while side deflects remain available."
                    : "Nuxia modifier active: top deflect is blocked while side deflects remain available.";
            }
            if (settings.Parry)
                return "Nuxia top-deflect rule inactive: Parry takes priority.";
            if (settings.Crushing) return "Nuxia top-deflect rule inactive: Crushing counter takes priority.";
            return "Nuxia has no standalone hero response; enable Deflect to activate its top-direction rule.";
        }
        if (!HasKnownHeroBehavior(hero)) return hero + " has no hero-specific response implemented.";
        if (!enabled) return hero + " response is disabled.";
        if (!settings.Autoblock) return hero + " response inactive: Auto Block is disabled.";
        if (settings.Parry) return hero + " response inactive: Parry takes priority.";
        if (settings.Crushing) return hero + " response inactive: Crushing counter takes priority.";
        if (settings.Deflect) return hero + " response inactive: Deflect takes priority.";
        return hero + " response is the active F/LT action.";
    }

    private static bool IsStandaloneHero(string hero) => hero is
        "Blackprior" or "Warlord" or "Shaman" or "Varangian" or "Orochi" or
        "Nobushi" or "Aramusha" or "Jiangjun";

    private static bool HasKnownHeroBehavior(string hero) => IsStandaloneHero(hero) || hero is "Warden" or "Nuxia" or "Peacekeeper";

    private static bool IsNuxiaDeflectEligible(Settings settings, bool enabled) =>
        enabled && settings.Autoblock && settings.Deflect &&
        ((settings.Parry && settings.Legit) || (!settings.Parry && !settings.Crushing));

    private static string DescribeHeroMode(Settings settings, string hero, bool enabled)
    {
        if (hero == "Peacekeeper") return "TIMING ONLY";
        if (hero == "Warden") return enabled && settings.Autoblock && settings.Parry ? "OVERRIDE ACTIVE" : "INACTIVE";
        if (hero == "Nuxia") return IsNuxiaDeflectEligible(settings, enabled) ? "MODIFIER ACTIVE" : "INACTIVE";
        return enabled && settings.Autoblock && IsStandaloneHero(hero) && !settings.Parry && !settings.Crushing && !settings.Deflect
            ? "ENABLED"
            : "INACTIVE";
    }

    private static string DescribeHeroBehavior(string hero) => hero switch
    {
        "Blackprior" => "Enters Bulwark stance, waits 50 ms, then sends RB.",
        "Warlord" => "Sends full block, then RB.",
        "Shaman" => "Sends dodge, then guard break.",
        "Varangian" => "Sends full block, then RT.",
        "Orochi" => "Sends dodge, then the configured Orochi follow-up. No configurable delay is used.",
        "Nobushi" => "Sends hidden stance.",
        "Aramusha" => "Sends full block, then RT.",
        "Jiangjun" => "Holds stance for 250 ms, then sends RB and RT.",
        "Warden" => "No standalone hero action. With hero response and Parry enabled, top attacks use an immediate crushing counter; sides use Parry.",
        "Nuxia" => "No standalone hero action. With hero response and Deflect enabled, top deflect is blocked; left and right remain available.",
        "Peacekeeper" => "No standalone hero action. Selecting Peacekeeper forces side deflect delays to 100 ms when timings are applied.",
        "None" => "No hero-specific behavior is selected.",
        _ => "No hero-specific response is implemented for this hero."
    };

    private static string DescribeHeroRequirements(string hero) => hero switch
    {
        "None" => "Select a hero, then enable standalone hero response.",
        "Warden" => "Enable standalone hero response and Parry. Auto Block and the flash gate are required; Warden has no separate hero action.",
        "Nuxia" => "Enable standalone hero response and Deflect. Auto Block and the flash gate are required; Nuxia has no separate hero action.",
        "Peacekeeper" => "The standalone toggle is not required for this timing override. Selecting Peacekeeper forces side deflects to 100 ms when timings are applied.",
        _ when IsStandaloneHero(hero) => "Enable standalone hero response, Auto Block, and hold F/LT. Parry, Crushing, and Deflect must be off for the standalone response.",
        _ => "Selection is saved for compatibility, but this hero has no implemented response."
    };

    private static string DescribeHeroTimings(string hero, Settings settings) => hero switch
    {
        "Blackprior" => "Fixed 50 ms stance delay; timing fields do not change it.",
        "Jiangjun" => "Fixed 250 ms stance hold; timing fields do not change it.",
        "Peacekeeper" => $"When applied, selected Peacekeeper side deflects are fixed at 100 ms, regardless of the toggle; top uses Top deflect ({settings.TopDeflect} ms).",
        "Nuxia" => $"Left {settings.Left} ms and right {settings.Right} ms; top is disabled.",
        "Warden" => "The top crushing response is immediate; Parry delay applies to side parries.",
        "None" => "No hero timing applies.",
        _ when IsStandaloneHero(hero) => "No configurable hero-specific timing applies.",
        _ => "No hero-specific timing applies."
    };
}
