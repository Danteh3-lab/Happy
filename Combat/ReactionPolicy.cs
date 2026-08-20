using HappyBot;

namespace HappyBot.Combat;

/// <summary>
/// Pure reaction-selection rules.  The coordinator owns candidate timing;
/// this policy only answers which feature may respond to the current frame.
/// Keeping this decision separate makes priority and hold-gate behavior easy
/// to exercise without a screen capture or an input device.
/// </summary>
internal static class ReactionPolicy
{
    public static ReactionSelection ResolveCommand(CombatObservation observation, Settings settings)
    {
        if (observation.EHeld && HasEAction(settings))
        {
            return new ReactionSelection(
                settings.Parry2 ? ReactionCommandKind.Parry : ReactionCommandKind.Crushing,
                "E");
        }

        if (!observation.FHeld || !HasFAction(settings))
            return new ReactionSelection(ReactionCommandKind.None, "");

        if (settings.Parry)
        {
            ReactionCommandKind kind = observation.Direction == CombatDirection.Top && IsYourChar(settings, "Warden")
                ? ReactionCommandKind.Crushing
                : ReactionCommandKind.Parry;
            return new ReactionSelection(kind, "F");
        }
        if (settings.Crushing)
            return new ReactionSelection(ReactionCommandKind.Crushing, "F");
        if (settings.Deflect)
            return new ReactionSelection(ReactionCommandKind.Deflect, "F");
        return HasHeroAction(settings)
            ? new ReactionSelection(ReactionCommandKind.Hero, "F")
            : new ReactionSelection(ReactionCommandKind.None, "");
    }

    public static bool HasEAction(Settings settings) => settings.Parry2 || settings.Crushing2;

    public static bool HasFAction(Settings settings) =>
        settings.Parry || settings.Crushing || settings.Deflect || HasHeroAction(settings);

    public static bool HasHeroAction(Settings settings) =>
        settings.YourHero && !settings.Nohero &&
        (IsYourChar(settings, "Blackprior") || IsYourChar(settings, "Warlord") ||
         IsYourChar(settings, "Shaman") || IsYourChar(settings, "Varangian") ||
         IsYourChar(settings, "Orochi") || IsYourChar(settings, "Nobushi") ||
         IsYourChar(settings, "Aramusha") || IsYourChar(settings, "Jiangjun"));

    public static bool IsYourChar(Settings settings, string name) =>
        settings.YourHero && !settings.Nohero && settings.Ch(name);

    /// <summary>
    /// Nuxia's automated deflect path is intentionally limited to side
    /// attacks.  This is a reliability rule only; all other heroes and
    /// generic/no-hero modes retain the existing directional behavior.
    /// </summary>
    public static bool IsDeflectDirectionEligible(Settings settings, CombatDirection direction)
    {
        if (!IsYourChar(settings, "Nuxia")) return true;
        return direction is CombatDirection.Left or CombatDirection.Right;
    }

    public static bool IsNuxiaTopDeflectSuppressed(Settings settings, CombatDirection direction) =>
        IsYourChar(settings, "Nuxia") && direction == CombatDirection.Top;

    public static bool OrangeHasPriority(CombatObservation observation, Settings settings, bool actionBusy) =>
        (settings.Unblockables && observation.OrangeIndicator) || actionBusy;
}

internal readonly record struct ReactionSelection(ReactionCommandKind Kind, string Hold);
