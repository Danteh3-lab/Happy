namespace HappyBot;

internal enum ControllerToggleAction
{
    None,
    AutoDodge,
    OrangeParry,
    AutoParry
}

/// <summary>
/// Owns the profile-backed controller button assignments used to toggle live
/// reaction features. Assignments are exclusive so one press cannot toggle
/// multiple features.
/// </summary>
internal static class ControllerToggleBindings
{
    private static readonly ControllerToggleAction[] Actions =
    {
        ControllerToggleAction.AutoDodge,
        ControllerToggleAction.OrangeParry,
        ControllerToggleAction.AutoParry
    };

    public static string DisplayName(ControllerToggleAction action) => action switch
    {
        ControllerToggleAction.AutoDodge => "Auto dodge",
        ControllerToggleAction.OrangeParry => "Orange parry",
        ControllerToggleAction.AutoParry => "F auto parry",
        _ => "Controller"
    };

    public static string Get(Settings settings, ControllerToggleAction action) => action switch
    {
        ControllerToggleAction.AutoDodge => settings.AutoDodgeBind,
        ControllerToggleAction.OrangeParry => settings.OrangeParryBind,
        ControllerToggleAction.AutoParry => settings.AutoParryBind,
        _ => ""
    };

    public static void Assign(Settings settings, ControllerToggleAction action, string binding)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (action == ControllerToggleAction.None)
            throw new ArgumentOutOfRangeException(nameof(action));

        string normalized = (binding ?? "").Trim();
        if (normalized.Length > 0)
        {
            foreach (ControllerToggleAction other in Actions)
            {
                if (other != action && string.Equals(Get(settings, other), normalized, StringComparison.OrdinalIgnoreCase))
                    Set(settings, other, "");
            }
        }

        Set(settings, action, normalized);
    }

    public static ControllerToggleAction Resolve(Settings settings, string pressed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(pressed)) return ControllerToggleAction.None;

        foreach (ControllerToggleAction action in Actions)
        {
            if (string.Equals(Get(settings, action), pressed, StringComparison.OrdinalIgnoreCase))
                return action;
        }
        return ControllerToggleAction.None;
    }

    public static void Copy(Settings destination, Settings source)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        destination.AutoDodgeBind = source.AutoDodgeBind;
        destination.OrangeParryBind = source.OrangeParryBind;
        destination.AutoParryBind = source.AutoParryBind;
    }

    private static void Set(Settings settings, ControllerToggleAction action, string binding)
    {
        switch (action)
        {
            case ControllerToggleAction.AutoDodge: settings.AutoDodgeBind = binding; break;
            case ControllerToggleAction.OrangeParry: settings.OrangeParryBind = binding; break;
            case ControllerToggleAction.AutoParry: settings.AutoParryBind = binding; break;
        }
    }
}
