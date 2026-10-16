using HappyBot.App.Protocol;
using HappyBot.Combat;

namespace HappyBot.App;

/// <summary>Builds the 200 ms status payload. Pure apart from reading its inputs.</summary>
internal static class StatusSnapshotBuilder
{
    internal readonly record struct UiFlags(
        ControllerToggleAction Capturing,
        bool VisionOverlayVisible,
        bool AnchorScanVisible);

    public static StatusDto Build(BotCore bot, SettingsSession session, IInputEnvironment input, UiFlags flags)
    {
        TelemetryStatus telemetry = bot.Telemetry;
        VisionSnapshot vision = bot.GetVisionSnapshot();
        Settings draft = session.Draft;
        int parryCount = bot.ParryCount;
        return new StatusDto
        {
            Version = BuildInfo.Version,
            Build = BuildInfo.Configuration,
            Profile = session.ActiveProfile,
            ProfileDirty = session.IsDirty,
            TimingsDirty = session.HasUnappliedTimings(),
            Behavior = ReactionBehaviorSummary.Create(bot.S, input.CanSendBulwark),
            Profiles = session.ProfileNames(),
            Running = bot.IsRunning,
            Paused = bot.IsPaused,
            Error = bot.LastError,
            Marker = bot.MarkerFound ? "FOUND" : "MISSING",
            Hold = bot.FHeld ? "DOWN" : "UP",
            Indicator = bot.AttackIndicator ? "YES" : "NO",
            Guard = bot.GuardDir,
            Flash = bot.Flash ? "YES" : "NO",
            LastReaction = new LastReactionDto(
                vision.LastReactionState,
                vision.LastReactionReason,
                vision.LastReactionDirection,
                vision.LastReactionDelayMs,
                ReactionConfirmationLabel(vision.LastReactionState)),
            ParryCount = parryCount,
            RtSent = parryCount,
            ParryAttempts = parryCount,
            ParriesConfirmed = bot.ParryConfirmedCount,
            ParriesUnconfirmed = bot.ParryUnconfirmedCount,
            Injected = input.InjectedCount,
            Mode = input.ActiveMode,
            Source = input.SourceConnected ? "ON" : "OFF",
            SourceSlot = input.SourceSlot,
            SourceType = input.SourceType,
            VirtualState = input.VirtualAvailable ? "ON" : "OFF",
            Bridge = input.GetDiagnostics(),
            Loop = bot.LoopHz,
            Legit = bot.S.Legit,
            OrangeParry = bot.OrangeParry,
            AutoDodgeBind = BindingLabel(draft, ControllerToggleAction.AutoDodge),
            OrangeParryBind = BindingLabel(draft, ControllerToggleAction.OrangeParry),
            AutoParryBind = BindingLabel(draft, ControllerToggleAction.AutoParry),
            BindingAutoDodge = flags.Capturing == ControllerToggleAction.AutoDodge,
            BindingOrangeParry = flags.Capturing == ControllerToggleAction.OrangeParry,
            BindingAutoParry = flags.Capturing == ControllerToggleAction.AutoParry,
            VisionOverlay = flags.VisionOverlayVisible,
            AnchorScan = flags.AnchorScanVisible,
            Telemetry = new TelemetryDto(
                telemetry.Recording,
                telemetry.Label,
                (int)telemetry.Duration.TotalSeconds,
                telemetry.Failures,
                telemetry.DroppedItems)
        };
    }

    public static string ReactionConfirmationLabel(string state)
    {
        if (state.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return "WAITING";
        if (state.Contains("UNCONFIRMED", StringComparison.OrdinalIgnoreCase)) return "UNCONFIRMED";
        if (state.Contains("CONFIRMED", StringComparison.OrdinalIgnoreCase)) return "CONFIRMED";
        if (state.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("CANCELLED", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("BLOCKED", StringComparison.OrdinalIgnoreCase)) return "NOT DELIVERED";
        if (state.Contains("SENT", StringComparison.OrdinalIgnoreCase)) return "INPUT SENT";
        if (state.Contains("READY", StringComparison.OrdinalIgnoreCase)) return "PENDING";
        return "OBSERVED";
    }

    private static string BindingLabel(Settings draft, ControllerToggleAction action)
    {
        string binding = ControllerToggleBindings.Get(draft, action);
        return string.IsNullOrWhiteSpace(binding) ? "UNBOUND" : binding;
    }
}
