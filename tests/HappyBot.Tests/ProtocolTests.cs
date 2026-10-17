using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using HappyBot;
using HappyBot.App;
using HappyBot.App.Protocol;

static partial class Program
{
    private static UiCommand ParseUi(string json)
    {
        using var document = JsonDocument.Parse(json);
        return UiCommandParser.Parse(document.RootElement);
    }

    private static void UiCommandParserMapsEveryMessage()
    {
        Require(ParseUi("{}") == null && ParseUi("{\"type\":3}") == null, "messages without a string type must be ignored");
        Require(ParseUi("{\"type\":\"ready\"}") is ReadyCommand, "ready");
        Require(ParseUi("{\"type\":\"start\",\"requestId\":\"r1\"}") is StartCommand { RequestId: "r1" }, "start carries its request id");
        Require(ParseUi("{\"type\":\"apply\"}") is ApplyCommand { RequestId: "" }, "a missing request id defaults to empty");
        Require(ParseUi("{\"type\":\"profile-select\",\"name\":\"A\",\"discard\":true}") is
            ProfileSelectCommand { Name: "A", Discard: true, DraftDirty: false }, "profile-select reads its flags");
        Require(ParseUi("{\"type\":\"profile-load\",\"discard\":\"yes\"}") is ProfileLoadCommand { Discard: false },
            "only JSON true counts as true");
        Require(ParseUi("{\"type\":\"load\"}") is ProfileLoadCommand { Discard: false, DraftDirty: false }, "legacy load alias");
        Require(ParseUi("{\"type\":\"save\"}") is ProfileSaveCommand, "legacy save alias");
        Require(ParseUi("{\"type\":\"profile-delete\",\"name\":\"B\",\"draftDirty\":true}") is
            ProfileDeleteCommand { Name: "B", DraftDirty: true }, "profile-delete");
        Require(ParseUi("{\"type\":\"telemetry\"}") is TelemetryToggleCommand { Label: "Other" }, "telemetry label defaults to Other");
        Require(ParseUi("{\"type\":\"bind-orange-parry\"}") is BindCaptureCommand { Action: ControllerToggleAction.OrangeParry },
            "bind commands carry their action");
        Require(ParseUi("{\"type\":\"drag\"}") is WindowCommand { Kind: WindowCommandKind.Drag }, "window commands");
        Require(ParseUi("{\"type\":\"howto\"}") is HelpCommand { Topic: HelpTopic.HowTo }, "help topics");
        Require(ParseUi("{\"type\":\"nope\"}") is UnknownCommand { Type: "nope" }, "unknown types are reported, not thrown");

        var settings = ParseUi("{\"type\":\"settings\",\"settings\":{\"Pause\":40}}") as SettingsCommand;
        Require(settings != null && settings.Settings.GetProperty("Pause").GetInt32() == 40,
            "settings must survive the source document being disposed");

        foreach (string type in InboundTypes())
        {
            Require(ParseUi("{\"type\":\"" + type + "\",\"settings\":{}}") is not UnknownCommand,
                "inbound type " + type + " has no parser case");
        }
    }

    private static void StatusPayloadKeepsWireNames()
    {
        var input = new FakeInputEnvironment();
        WithDeck((deck, _, _) =>
        {
            using JsonDocument document = JsonDocument.Parse(UiJson.Serialize(new StatusMessage(deck.BuildStatus())));
            JsonElement root = document.RootElement;
            Require(root.GetProperty("type").GetString() == "status", "status messages carry their type");
            JsonElement status = root.GetProperty("status");
            string[] expected =
            {
                "version", "build", "profile", "profileDirty", "timingsDirty", "behavior", "profiles", "running", "paused",
                "error", "marker", "hold", "indicator", "guard", "flash", "lastReaction", "parryCount", "rtSent",
                "parryAttempts", "parriesConfirmed", "parriesUnconfirmed", "injected", "mode", "source", "sourceSlot",
                "sourceType", "virtualState", "bridge", "loop", "legit", "orangeParry", "autoDodgeBind", "orangeParryBind",
                "autoParryBind", "bindingAutoDodge", "bindingOrangeParry", "bindingAutoParry", "visionOverlay",
                "anchorScan", "telemetry", "cloud"
            };
            string[] actual = status.EnumerateObject().Select(p => p.Name).ToArray();
            Require(actual.OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x)),
                "status keys changed: " + string.Join(",", actual.Except(expected).Concat(expected.Except(actual))));
            Require(status.GetProperty("behavior").TryGetProperty("FAction", out _) &&
                status.GetProperty("behavior").TryGetProperty("DeflectTiming", out _),
                "behavior keeps its PascalCase members");
            Require(status.GetProperty("bridge").TryGetProperty("OtherXInputControllers", out _),
                "bridge keeps its PascalCase members");
            Require(status.GetProperty("lastReaction").TryGetProperty("delayMs", out _) &&
                status.GetProperty("telemetry").TryGetProperty("durationSeconds", out _),
                "nested host objects keep lowerCamel names");
        }, input);

        using JsonDocument init = JsonDocument.Parse(UiJson.Serialize(
            new InitMessage(new Dictionary<string, object> { ["Pause"] = 0 }, new StatusDto())));
        Require(init.RootElement.GetProperty("type").GetString() == "init" &&
            init.RootElement.GetProperty("protocol").GetInt32() == UiMessageTypes.ProtocolVersion,
            "init carries the protocol version");
        using JsonDocument apply = JsonDocument.Parse(UiJson.Serialize(new ApplyResultMessage("r", true, "ok")));
        Require(apply.RootElement.GetProperty("requestId").GetString() == "r" &&
            apply.RootElement.GetProperty("type").GetString() == "apply-result", "apply-result keys");
    }

    private static void ProtocolNamesMatchPage()
    {
        string protocolJs = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "js", "protocol.js"));
        string Block(string name) => Regex.Match(protocolJs, name + @" = Object\.freeze\(\{([\s\S]*?)\}\);").Groups[1].Value;
        HashSet<string> Values(string block) => Regex.Matches(block, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();

        HashSet<string> pageToHost = Values(Block("MSG"));
        HashSet<string> hostToPage = Values(Block("HOST"));
        var inbound = InboundTypes().ToHashSet();
        var outbound = typeof(UiMessageTypes.Outbound).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()).ToHashSet();

        Require(pageToHost.SetEquals(inbound),
            "protocol.js MSG differs from UiMessageTypes.Inbound: " + string.Join(",", pageToHost.Except(inbound).Concat(inbound.Except(pageToHost))));
        Require(hostToPage.SetEquals(outbound),
            "protocol.js HOST differs from UiMessageTypes.Outbound: " + string.Join(",", hostToPage.Except(outbound).Concat(outbound.Except(hostToPage))));
        Require(protocolJs.Contains("PROTOCOL_VERSION = " + UiMessageTypes.ProtocolVersion + ";"), "protocol versions differ");
    }

    // Current inbound names, without the legacy aliases the page no longer sends.
    private static IEnumerable<string> InboundTypes() =>
        typeof(UiMessageTypes.Inbound).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => !f.Name.StartsWith("Legacy", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue());

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "HappyBot.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("HappyBot.csproj not found above the test binaries.");
    }
}
