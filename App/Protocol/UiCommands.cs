using System.Text.Json;

namespace HappyBot.App.Protocol;

internal abstract record UiCommand;

internal sealed record ReadyCommand : UiCommand;
internal sealed record SettingsCommand(JsonElement Settings) : UiCommand;
internal sealed record StartCommand(string RequestId) : UiCommand;
internal sealed record ApplyCommand(string RequestId) : UiCommand;
internal sealed record TogglePauseCommand : UiCommand;
internal sealed record ResolutionCommand : UiCommand;
internal sealed record ScanCommand : UiCommand;
internal sealed record TestInputCommand : UiCommand;
internal sealed record ProfileSaveCommand : UiCommand;
internal sealed record ProfileLoadCommand(bool Discard, bool DraftDirty) : UiCommand;
internal sealed record ProfileSelectCommand(string Name, bool Discard, bool DraftDirty) : UiCommand;
internal sealed record ProfileSaveAsCommand(string Name) : UiCommand;
internal sealed record ProfileDeleteCommand(string Name, bool Discard, bool DraftDirty) : UiCommand;
internal sealed record OrangeParryToggleCommand : UiCommand;
internal sealed record VisionOverlayToggleCommand : UiCommand;
internal sealed record AnchorScanToggleCommand : UiCommand;
internal sealed record BindCaptureCommand(ControllerToggleAction Action) : UiCommand;
internal sealed record TelemetryToggleCommand(string Label) : UiCommand;
internal sealed record TelemetryExportCommand : UiCommand;
internal sealed record CloudSignInCommand(string Email, string Password) : UiCommand
{
    // Keep the password out of logs and exception messages that print the record.
    public override string ToString() => $"CloudSignInCommand {{ Email = {Email} }}";
}
internal sealed record CloudSignUpCommand(string Email, string Password) : UiCommand
{
    public override string ToString() => $"CloudSignUpCommand {{ Email = {Email} }}";
}
internal sealed record CloudSignOutCommand : UiCommand;
internal sealed record CloudSyncCommand : UiCommand;
internal sealed record LicenseRedeemCommand(string Key) : UiCommand
{
    public override string ToString() => "LicenseRedeemCommand { Key = (hidden) }";
}
internal sealed record HelpCommand(HelpTopic Topic) : UiCommand;
internal sealed record WindowCommand(WindowCommandKind Kind) : UiCommand;
internal sealed record UnknownCommand(string Type) : UiCommand;

internal enum HelpTopic
{
    HowTo,
    ReadMe
}

internal enum WindowCommandKind
{
    Reload,
    Minimize,
    Close,
    Drag
}

/// <summary>Turns a page message into a typed command. Missing fields default to "" or false.</summary>
internal static class UiCommandParser
{
    public static UiCommand Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out JsonElement typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
            return null;

        string type = typeElement.GetString() ?? "";
        return type switch
        {
            UiMessageTypes.Inbound.Ready => new ReadyCommand(),
            UiMessageTypes.Inbound.Settings => root.TryGetProperty("settings", out JsonElement settings)
                ? new SettingsCommand(settings.Clone())
                : new UnknownCommand(type),
            UiMessageTypes.Inbound.Start => new StartCommand(ReadString(root, "requestId")),
            UiMessageTypes.Inbound.Apply => new ApplyCommand(ReadString(root, "requestId")),
            UiMessageTypes.Inbound.TogglePause => new TogglePauseCommand(),
            UiMessageTypes.Inbound.Resolution => new ResolutionCommand(),
            UiMessageTypes.Inbound.Scan => new ScanCommand(),
            UiMessageTypes.Inbound.Test => new TestInputCommand(),
            UiMessageTypes.Inbound.ProfileSave or UiMessageTypes.Inbound.LegacySave => new ProfileSaveCommand(),
            UiMessageTypes.Inbound.LegacyLoad => new ProfileLoadCommand(false, false),
            UiMessageTypes.Inbound.ProfileLoad => new ProfileLoadCommand(ReadBool(root, "discard"), ReadBool(root, "draftDirty")),
            UiMessageTypes.Inbound.ProfileSelect => new ProfileSelectCommand(
                ReadString(root, "name"), ReadBool(root, "discard"), ReadBool(root, "draftDirty")),
            UiMessageTypes.Inbound.ProfileSaveAs => new ProfileSaveAsCommand(ReadString(root, "name")),
            UiMessageTypes.Inbound.ProfileDelete => new ProfileDeleteCommand(
                ReadString(root, "name"), ReadBool(root, "discard"), ReadBool(root, "draftDirty")),
            UiMessageTypes.Inbound.HowTo => new HelpCommand(HelpTopic.HowTo),
            UiMessageTypes.Inbound.ReadMe => new HelpCommand(HelpTopic.ReadMe),
            UiMessageTypes.Inbound.Reload => new WindowCommand(WindowCommandKind.Reload),
            UiMessageTypes.Inbound.Minimize => new WindowCommand(WindowCommandKind.Minimize),
            UiMessageTypes.Inbound.Close => new WindowCommand(WindowCommandKind.Close),
            UiMessageTypes.Inbound.Drag => new WindowCommand(WindowCommandKind.Drag),
            UiMessageTypes.Inbound.OrangeParry => new OrangeParryToggleCommand(),
            UiMessageTypes.Inbound.VisionOverlay => new VisionOverlayToggleCommand(),
            UiMessageTypes.Inbound.AnchorScan => new AnchorScanToggleCommand(),
            UiMessageTypes.Inbound.BindAutoDodge => new BindCaptureCommand(ControllerToggleAction.AutoDodge),
            UiMessageTypes.Inbound.BindOrangeParry => new BindCaptureCommand(ControllerToggleAction.OrangeParry),
            UiMessageTypes.Inbound.BindAutoParry => new BindCaptureCommand(ControllerToggleAction.AutoParry),
            UiMessageTypes.Inbound.Telemetry => new TelemetryToggleCommand(ReadString(root, "label", "Other")),
            UiMessageTypes.Inbound.ExportTelemetry => new TelemetryExportCommand(),
            UiMessageTypes.Inbound.CloudSignIn => new CloudSignInCommand(ReadString(root, "email"), ReadString(root, "password")),
            UiMessageTypes.Inbound.CloudSignUp => new CloudSignUpCommand(ReadString(root, "email"), ReadString(root, "password")),
            UiMessageTypes.Inbound.CloudSignOut => new CloudSignOutCommand(),
            UiMessageTypes.Inbound.CloudSync => new CloudSyncCommand(),
            UiMessageTypes.Inbound.LicenseRedeem => new LicenseRedeemCommand(ReadString(root, "key")),
            _ => new UnknownCommand(type)
        };
    }

    private static string ReadString(JsonElement root, string name, string fallback = "") =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
