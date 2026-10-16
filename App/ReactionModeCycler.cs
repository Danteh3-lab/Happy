namespace HappyBot.App;

/// <summary>The F3 / F4 hotkey cycles. Each returns the label of the new mode.</summary>
internal static class ReactionModeCycler
{
    /// <summary>F/LT hold: Parry → Crushing counter → Deflect → Parry.</summary>
    public static string CycleF(Settings s)
    {
        if (s.Parry)
        {
            s.Parry = false; s.Crushing = true; s.Deflect = false;
            return "Crushing counter";
        }
        if (s.Crushing)
        {
            s.Parry = false; s.Crushing = false; s.Deflect = true;
            return "Deflect";
        }
        s.Parry = true; s.Crushing = false; s.Deflect = false;
        return "Parry";
    }

    /// <summary>E hold: Parry → Crushing counter → Off → Parry.</summary>
    public static string CycleE(Settings s)
    {
        if (s.Parry2)
        {
            s.Parry2 = false; s.Crushing2 = true;
            return "Crushing counter";
        }
        if (s.Crushing2)
        {
            s.Parry2 = false; s.Crushing2 = false;
            return "Off";
        }
        s.Parry2 = true; s.Crushing2 = false;
        return "Parry";
    }
}
