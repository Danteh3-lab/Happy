using System.Text.Json;
using HappyBot;
using HappyBot.App;
using HappyBot.App.Protocol;
using HappyBot.Combat;

static partial class Program
{
    private static void DeckControllerApplyCommitsAndReportsInOrder()
    {
        WithDeck((deck, ui, h) =>
        {
            deck.Handle(SettingsJson("{\"Pause\":120,\"Autoblock\":true}"));
            Require(h.Bot.S.Autoblock, "switches must go live immediately");
            Require(h.Bot.S.Pause == 0 && h.Session.HasUnappliedTimings(), "timings must wait for Apply");
            Require(ui.Types().SequenceEqual(new[] { "status", "settings" }), "a settings edit reports status then settings");

            ui.Messages.Clear();
            deck.Handle(new ApplyCommand("r1"));
            Require(h.Bot.S.Pause == 120 && !h.Session.HasUnappliedTimings(), "Apply must commit timings");
            Require(ui.Types().SequenceEqual(new[] { "settings", "status", "apply-result", "toast" }),
                "Apply reports settings, status, the result, then a toast: " + string.Join(",", ui.Types()));
            var result = ui.Messages.OfType<ApplyResultMessage>().Single();
            Require(result.RequestId == "r1" && result.Success, "Apply acknowledges its request id");
        });
    }

    private static void DeckControllerStartRejectsWhenNotReady()
    {
        var input = new FakeInputEnvironment { IsReady = false };
        WithDeck((deck, ui, h) =>
        {
            deck.Handle(new StartCommand("s1"));
            var result = ui.Messages.OfType<ApplyResultMessage>().Single();
            Require(!result.Success && result.RequestId == "s1" && !h.Bot.IsRunning, "Start must refuse without ViGEm");
            Require(ui.Messages.OfType<ToastMessage>().Any(t => t.Kind == "error"), "Start failure must toast an error");

            ui.Messages.Clear();
            input.IsReady = true;
            deck.Handle(SettingsJson("{\"res1\":\"800\",\"res2\":\"1200\"}"));
            deck.Handle(new StartCommand("s2"));
            result = ui.Messages.OfType<ApplyResultMessage>().Single();
            Require(!result.Success && result.Message.Contains("resolution") && !h.Bot.IsRunning,
                "Start must refuse a portrait resolution");
        }, input);
    }

    private static void DeckControllerHotkeysKeepDraftAndLiveInStep()
    {
        WithDeck((deck, ui, h) =>
        {
            deck.Handle(SettingsJson("{\"ParryDelay\":55}"));
            deck.OnHotkey(HotkeyAction.OrangeDelayToggle);
            Require(h.Session.Draft.Pause == 80 && h.Bot.S.Pause == 80, "F1 must push the orange delay live");
            Require(h.Bot.S.ParryDelay == 0 && h.Session.Draft.ParryDelay == 55,
                "F1 must not push other pending timings live");
            deck.OnHotkey(HotkeyAction.OrangeDelayToggle);
            Require(h.Bot.S.Pause == 0 && h.Sound.Unclicks == 1 && h.Sound.Clicks == 1, "F1 toggles back with the unclick cue");

            ui.Messages.Clear();
            deck.OnHotkey(HotkeyAction.CycleF);
            Require(h.Bot.S.Parry && !h.Bot.S.Crushing && h.Session.Draft.Parry, "F3 from nothing selects Parry");
            Require(ui.Messages.OfType<ToastMessage>().Any(t => t.Message == "F-mode: Parry"), "F3 announces the mode");
            Require(ui.Types().Count(t => t == "status") == 1, "a hotkey sends one status update");
            deck.OnHotkey(HotkeyAction.CycleF);
            Require(h.Bot.S.Crushing && !h.Bot.S.Parry, "F3 then selects Crushing");

            deck.OnHotkey(HotkeyAction.CycleE);
            Require(h.Bot.S.Parry2 && h.Session.Draft.Parry2, "F4 from nothing selects E parry");

            ui.Messages.Clear();
            deck.OnHotkey(HotkeyAction.OrangeParry);
            Require(h.Bot.OrangeParry && h.Bot.S.OrangeParry && h.Session.Draft.OrangeParry,
                "F5 must update both orange-parry flags and the draft");
            Require(ui.Types().Count(t => t == "status") == 1, "F5 sends one status update");
            Require(h.Session.IsDirty, "hotkey changes mark the profile dirty");
        });
    }

    private static void DeckControllerOverlayAndTelemetryReportFailures()
    {
        WithDeck((deck, ui, h) =>
        {
            h.Overlay.ShowSucceeds = false;
            deck.OnHotkey(HotkeyAction.VisionOverlay);
            Require(!deck.VisionOverlayVisible && ui.Messages.OfType<ToastMessage>().Any(t => t.Kind == "error"),
                "an overlay that cannot hide from capture must stay off");
            h.Overlay.ShowSucceeds = true;
            deck.Handle(new VisionOverlayToggleCommand());
            Require(deck.VisionOverlayVisible && h.Overlay.Visible, "the overlay shows when capture exclusion works");
            deck.Handle(new AnchorScanToggleCommand());
            Require(!deck.AnchorScanVisible && !h.Overlay.AnchorScan, "anchor scan toggles on the overlay");
            Require(!deck.BuildStatus().AnchorScan && deck.BuildStatus().VisionOverlay, "status reports overlay state");

            ui.Messages.Clear();
            deck.Handle(new TelemetryExportCommand());
            Require(h.Picker.Calls == 0 && ui.Messages.OfType<ToastMessage>().Single().Message.Contains("No telemetry"),
                "export without a session must not open the save dialog");
        });
    }

    private static void DeckControllerBindsAndTogglesFromController()
    {
        var input = new FakeInputEnvironment { SourceConnected = false };
        WithDeck((deck, ui, h) =>
        {
            deck.Handle(new BindCaptureCommand(ControllerToggleAction.AutoDodge));
            Require(deck.CapturingBinding == ControllerToggleAction.None &&
                ui.Messages.OfType<ToastMessage>().Single().Kind == "error", "binding needs a source controller");

            input.SourceConnected = true;
            input.State = Gamepad(0x0100);
            deck.OnControllerPollTick();
            deck.Handle(new BindCaptureCommand(ControllerToggleAction.AutoDodge));
            Require(deck.CapturingBinding == ControllerToggleAction.AutoDodge && deck.BuildStatus().BindingAutoDodge,
                "capture starts and is reported");
            deck.OnControllerPollTick();
            Require(deck.CapturingBinding == ControllerToggleAction.AutoDodge, "the held LB must not bind itself");
            input.State = Gamepad(0x0100 | 0x0200);
            deck.OnControllerPollTick();
            Require(h.Session.Draft.AutoDodgeBind == "RB" && h.Bot.S.AutoDodgeBind == "RB" &&
                deck.CapturingBinding == ControllerToggleAction.None, "a new press binds and goes live");

            bool before = h.Bot.S.Unblockables;
            input.State = Gamepad(0);
            deck.OnControllerPollTick();
            input.State = Gamepad(0x0200);
            deck.OnControllerPollTick();
            Require(h.Bot.S.Unblockables == !before && h.Session.Draft.Unblockables == !before,
                "the bound button toggles auto dodge");
            deck.OnControllerPollTick();
            Require(h.Bot.S.Unblockables == !before, "holding the button must not toggle again");
        }, input);
    }

    private static void ControllerBindingCaptureEdgesAndThresholds()
    {
        var capture = new ControllerBindingCapture();
        Require(capture.Poll(true, Gamepad(0x1000)).Kind == BindingEventKind.None, "the first sample only initializes");
        Require(capture.Poll(true, Gamepad(0x1000)).Kind == BindingEventKind.None, "a held button is not a new press");
        Require(capture.Poll(true, Gamepad(0x1000 | 0x2000)) is { Kind: BindingEventKind.Pressed, Button: "B" }, "rising edge");
        Require(capture.Poll(true, Gamepad(0, lt: 32)).Kind == BindingEventKind.None, "LT at the threshold is not pressed");
        Require(capture.Poll(true, Gamepad(0, lt: 33)) is { Button: "LT" }, "LT above the threshold is pressed");
        Require(capture.Poll(true, Gamepad(0, rt: 200)) is { Button: "RT" }, "RT press");

        capture.Begin(ControllerToggleAction.AutoParry, Gamepad(0, rt: 200));
        Require(capture.Poll(true, Gamepad(0, rt: 200)).Kind == BindingEventKind.None, "the trigger held at capture start is ignored");
        Require(capture.Poll(true, Gamepad(0x8000, rt: 200)) is
            { Kind: BindingEventKind.Bound, Action: ControllerToggleAction.AutoParry, Button: "Y" }, "capture binds a new press");
        Require(capture.Capturing == ControllerToggleAction.None, "capture ends after binding");

        capture.Begin(ControllerToggleAction.OrangeParry, Gamepad(0));
        capture.Cancel();
        Require(capture.Capturing == ControllerToggleAction.None, "cancel ends capture");

        Require(capture.Poll(false, default).Kind == BindingEventKind.None, "a lost controller resets");
        Require(capture.Poll(true, Gamepad(0x0010)).Kind == BindingEventKind.None, "after a reset the first sample initializes again");
    }

    // ------------------------------------------------------------- harness

    private static Native.XINPUT_GAMEPAD Gamepad(ushort buttons, byte lt = 0, byte rt = 0) =>
        new() { wButtons = buttons, bLeftTrigger = lt, bRightTrigger = rt };

    private static SettingsCommand SettingsJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new SettingsCommand(document.RootElement.Clone());
    }

    private static void WithDeck(Action<DeckController, FakeUiSink, DeckHarness> body, FakeInputEnvironment input = null)
    {
        using var harness = new DeckHarness(input ?? new FakeInputEnvironment());
        body(harness.Deck, harness.Ui, harness);
    }

    private sealed class DeckHarness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "HappyBot.DeckTests", Guid.NewGuid().ToString("N"));

        public DeckHarness(FakeInputEnvironment input)
        {
            Directory.CreateDirectory(_root);
            Input = input;
            Bot = new BotCore(new FakeInputGateway(), new FixedRollSource(0), new FixedOrangeDirectionSource(CombatDirection.Top));
            Session = new SettingsSession(new ProfileEditorController(new ProfileStore(_root)), Bot);
            Deck = new DeckController(Bot, Session, Ui, new SyncDispatcher(), Input, Overlay, Sound, Picker,
                new InputTestRunner(Array.Empty<(string, Action)>(), (_, _) => Task.CompletedTask));
            Deck.Initialize(1920, 1080);
        }

        public BotCore Bot { get; }
        public SettingsSession Session { get; }
        public DeckController Deck { get; }
        public FakeInputEnvironment Input { get; }
        public FakeUiSink Ui { get; } = new();
        public FakeOverlay Overlay { get; } = new();
        public FakeSound Sound { get; } = new();
        public FakeSavePicker Picker { get; } = new();

        public void Dispose()
        {
            Deck.Dispose();
            Bot.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }

    private sealed class FakeUiSink : IUiSink
    {
        public List<UiMessage> Messages { get; } = new();
        public bool IsConnected => true;
        public void Post(UiMessage message) => Messages.Add(message);
        public IEnumerable<string> Types() => Messages.Select(m => m.Type);
    }

    private sealed class SyncDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FakeInputEnvironment : IInputEnvironment
    {
        public bool IsReady { get; set; } = true;
        public bool CanSendBulwark => true;
        public long InjectedCount => 0;
        public string ActiveMode => "ViGEm";
        public bool SourceConnected { get; set; } = true;
        public int SourceSlot => 0;
        public string SourceType => "xinput-legacy";
        public bool VirtualAvailable => true;
        public Native.XINPUT_GAMEPAD State { get; set; }
        public bool HoldButtonHeld() => false;
        public bool TryGetSourceState(out Native.XINPUT_GAMEPAD state)
        {
            state = State;
            return SourceConnected;
        }
        public void TryRecover() { }
        public InputBridgeSnapshot GetDiagnostics() => new(true, SourceConnected, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed class FakeOverlay : IVisionOverlay
    {
        public bool ShowSucceeds { get; set; } = true;
        public bool Visible { get; private set; }
        public bool AnchorScan { get; private set; } = true;
        public bool TryShow() => Visible = ShowSucceeds;
        public void Hide() => Visible = false;
        public void SetAnchorScanVisible(bool visible) => AnchorScan = visible;
    }

    private sealed class FakeSound : ISoundCue
    {
        public int Clicks { get; private set; }
        public int Unclicks { get; private set; }
        public void Click() => Clicks++;
        public void Unclick() => Unclicks++;
        public void Beep() { }
    }

    private sealed class FakeSavePicker : ISaveFilePicker
    {
        public int Calls { get; private set; }
        public string Result { get; set; }
        public string PickSavePath(string title, string filter, string suggestedName)
        {
            Calls++;
            return Result;
        }
    }
}
