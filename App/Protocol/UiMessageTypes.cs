namespace HappyBot.App.Protocol;

/// <summary>
/// Message type names shared with <c>ui/js/protocol.js</c>. The names are the
/// wire contract between the WebView2 page and the host; ProtocolTests keeps
/// both sides in sync.
/// </summary>
internal static class UiMessageTypes
{
    public const int ProtocolVersion = 1;

    /// <summary>Messages the page sends to the host.</summary>
    internal static class Inbound
    {
        public const string Ready = "ready";
        public const string Settings = "settings";
        public const string Start = "start";
        public const string TogglePause = "toggle-pause";
        public const string Resolution = "resolution";
        public const string Scan = "scan";
        public const string Test = "test";
        public const string Apply = "apply";
        public const string ProfileSelect = "profile-select";
        public const string ProfileLoad = "profile-load";
        public const string ProfileSave = "profile-save";
        public const string ProfileSaveAs = "profile-save-as";
        public const string ProfileDelete = "profile-delete";
        public const string HowTo = "howto";
        public const string ReadMe = "readme";
        public const string Reload = "reload";
        public const string Minimize = "minimize";
        public const string Close = "close";
        public const string Drag = "drag";
        public const string OrangeParry = "orange-parry";
        public const string VisionOverlay = "vision-overlay";
        public const string AnchorScan = "anchor-scan";
        public const string BindAutoDodge = "bind-auto-dodge";
        public const string BindOrangeParry = "bind-orange-parry";
        public const string BindAutoParry = "bind-auto-parry";
        public const string Telemetry = "telemetry";
        public const string ExportTelemetry = "export-telemetry";

        // Legacy aliases still accepted from older pages.
        public const string LegacySave = "save";
        public const string LegacyLoad = "load";
    }

    /// <summary>Messages the host sends to the page.</summary>
    internal static class Outbound
    {
        public const string Init = "init";
        public const string Status = "status";
        public const string Settings = "settings";
        public const string ApplyResult = "apply-result";
        public const string Toast = "toast";
        public const string Dialog = "dialog";
    }
}
