using System.Text.Json;
using System.Text.Json.Serialization;
using HappyBot.Combat;

namespace HappyBot.App.Protocol;

// Outbound wire records. Every property carries an explicit JSON name and no
// naming policy is applied, so the page keeps receiving exactly the keys it
// did before these types existed: lowerCamel for host-built objects, while the
// nested `behavior` and `bridge` records keep their PascalCase members.

internal abstract record UiMessage
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

internal sealed record InitMessage(
    [property: JsonPropertyName("settings")] Dictionary<string, object> Settings,
    [property: JsonPropertyName("status")] StatusDto Status) : UiMessage
{
    public override string Type => UiMessageTypes.Outbound.Init;

    [JsonPropertyName("protocol")]
    public int Protocol => UiMessageTypes.ProtocolVersion;
}

internal sealed record StatusMessage(
    [property: JsonPropertyName("status")] StatusDto Status) : UiMessage
{
    public override string Type => UiMessageTypes.Outbound.Status;
}

internal sealed record SettingsMessage(
    [property: JsonPropertyName("settings")] Dictionary<string, object> Settings) : UiMessage
{
    public override string Type => UiMessageTypes.Outbound.Settings;
}

internal sealed record ApplyResultMessage(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message) : UiMessage
{
    public override string Type => UiMessageTypes.Outbound.ApplyResult;
}

internal sealed record ToastMessage(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("kind")] string Kind) : UiMessage
{
    public override string Type => UiMessageTypes.Outbound.Toast;
}

internal sealed record DialogMessage(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("eyebrow")] string Eyebrow) : UiMessage
{
    public override string Type => UiMessageTypes.Outbound.Dialog;
}

internal sealed record LastReactionDto(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("delayMs")] int DelayMs,
    [property: JsonPropertyName("confirmation")] string Confirmation);

internal sealed record TelemetryDto(
    [property: JsonPropertyName("recording")] bool Recording,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("durationSeconds")] int DurationSeconds,
    [property: JsonPropertyName("failures")] long Failures,
    [property: JsonPropertyName("dropped")] long Dropped);

/// <summary>Account and sync state for the Cloud profiles card.</summary>
internal sealed record CloudStatusDto
{
    public static readonly CloudStatusDto Unavailable = new();

    [JsonPropertyName("available")] public bool Available { get; init; }
    [JsonPropertyName("signedIn")] public bool SignedIn { get; init; }
    [JsonPropertyName("email")] public string Email { get; init; } = "";
    [JsonPropertyName("busy")] public bool Busy { get; init; }
    [JsonPropertyName("lastSync")] public string LastSync { get; init; } = "";
    [JsonPropertyName("error")] public string Error { get; init; } = "";
    [JsonPropertyName("license")] public LicenseStatusDto License { get; init; } = LicenseStatusDto.Unavailable;
}

/// <summary>License state for the account card, the readiness list and the start gate.</summary>
internal sealed record LicenseStatusDto
{
    public static readonly LicenseStatusDto Unavailable = new();

    [JsonPropertyName("active")] public bool Active { get; init; }
    /// <summary>active, none, expired, revoked, device_limit or unchecked.</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "unchecked";
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";
}

/// <summary>The 200 ms status payload. Field names are the page contract.</summary>
internal sealed class StatusDto
{
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("build")] public string Build { get; init; } = "";
    [JsonPropertyName("profile")] public string Profile { get; init; } = "";
    [JsonPropertyName("profileDirty")] public bool ProfileDirty { get; init; }
    [JsonPropertyName("timingsDirty")] public bool TimingsDirty { get; init; }
    [JsonPropertyName("behavior")] public ReactionBehaviorSummary Behavior { get; init; }
    [JsonPropertyName("profiles")] public IReadOnlyList<string> Profiles { get; init; } = Array.Empty<string>();
    [JsonPropertyName("running")] public bool Running { get; init; }
    [JsonPropertyName("paused")] public bool Paused { get; init; }
    [JsonPropertyName("error")] public string Error { get; init; } = "";
    [JsonPropertyName("marker")] public string Marker { get; init; } = "";
    [JsonPropertyName("hold")] public string Hold { get; init; } = "";
    [JsonPropertyName("indicator")] public string Indicator { get; init; } = "";
    [JsonPropertyName("guard")] public string Guard { get; init; } = "";
    [JsonPropertyName("flash")] public string Flash { get; init; } = "";
    [JsonPropertyName("lastReaction")] public LastReactionDto LastReaction { get; init; }

    // parryCount and parryAttempts duplicate rtSent; kept for older pages.
    [JsonPropertyName("parryCount")] public int ParryCount { get; init; }
    [JsonPropertyName("rtSent")] public int RtSent { get; init; }
    [JsonPropertyName("parryAttempts")] public int ParryAttempts { get; init; }
    [JsonPropertyName("parriesConfirmed")] public int ParriesConfirmed { get; init; }
    [JsonPropertyName("parriesUnconfirmed")] public int ParriesUnconfirmed { get; init; }
    [JsonPropertyName("injected")] public long Injected { get; init; }
    [JsonPropertyName("mode")] public string Mode { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("sourceSlot")] public int SourceSlot { get; init; }
    [JsonPropertyName("sourceType")] public string SourceType { get; init; } = "";
    [JsonPropertyName("virtualState")] public string VirtualState { get; init; } = "";
    [JsonPropertyName("bridge")] public InputBridgeSnapshot Bridge { get; init; }
    [JsonPropertyName("loop")] public int Loop { get; init; }
    [JsonPropertyName("legit")] public bool Legit { get; init; }
    [JsonPropertyName("orangeParry")] public bool OrangeParry { get; init; }
    [JsonPropertyName("autoDodgeBind")] public string AutoDodgeBind { get; init; } = "";
    [JsonPropertyName("orangeParryBind")] public string OrangeParryBind { get; init; } = "";
    [JsonPropertyName("autoParryBind")] public string AutoParryBind { get; init; } = "";
    [JsonPropertyName("bindingAutoDodge")] public bool BindingAutoDodge { get; init; }
    [JsonPropertyName("bindingOrangeParry")] public bool BindingOrangeParry { get; init; }
    [JsonPropertyName("bindingAutoParry")] public bool BindingAutoParry { get; init; }
    [JsonPropertyName("visionOverlay")] public bool VisionOverlay { get; init; }
    [JsonPropertyName("anchorScan")] public bool AnchorScan { get; init; }
    [JsonPropertyName("telemetry")] public TelemetryDto Telemetry { get; init; }
    [JsonPropertyName("cloud")] public CloudStatusDto Cloud { get; init; } = CloudStatusDto.Unavailable;
}

internal static class UiJson
{
    public static string Serialize(UiMessage message) => JsonSerializer.Serialize(message, message.GetType());
}
