using System.Diagnostics;
using System.Text.Json;
using HappyBot;
using HappyBot.Automation;
using HappyBot.Combat;
using HappyBot.Infrastructure.Input;

static partial class Program
{
    private static void SettingsCodecDodgeDirectionMappingCoversBackLeftRight()
    {
        var back = new Settings();
        Require(!back.Leftdodge && !back.Rightdodge,
            "Back must map to both dodge flags false");

        var left = new Settings();
        SettingsCodec.SetCheck(left, "Leftdodge", true);
        Require(left.Leftdodge && !left.Rightdodge,
            "Left must set Leftdodge only");

        var right = new Settings();
        SettingsCodec.SetCheck(right, "Rightdodge", true);
        Require(!right.Leftdodge && right.Rightdodge,
            "Right must set Rightdodge only");

        SettingsCodec.SetCheck(left, "Leftdodge", false);
        Require(!left.Leftdodge && !left.Rightdodge,
            "clearing Left must return to Back (both flags false)");

        var exclusive = new Settings();
        SettingsCodec.SetCheck(exclusive, "Leftdodge", true);
        SettingsCodec.SetCheck(exclusive, "Rightdodge", true);
        Require(exclusive.Rightdodge && !exclusive.Leftdodge,
            "enabling Right must clear Left");
        SettingsCodec.SetCheck(exclusive, "Leftdodge", true);
        Require(exclusive.Leftdodge && !exclusive.Rightdodge,
            "enabling Left must clear Right");

        // Backend persistence round-trip for each direction. DOM hydration
        // (settings -> checked radio + roving tabindex) is covered by
        // ui/dodge-direction.test.js, which exercises the shared module
        // against fake radiogroup buttons and the real index.html markup.
        foreach (string direction in new[] { "back", "left", "right" })
        {
            var original = new Settings();
            if (direction == "left") original.Leftdodge = true;
            if (direction == "right") original.Rightdodge = true;
            var snapshot = SettingsCodec.ToSnapshot(original);
            Require((bool)snapshot["Leftdodge"] == original.Leftdodge &&
                (bool)snapshot["Rightdodge"] == original.Rightdodge,
                "dodge snapshot must preserve " + direction);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
            Settings rehydrated = SettingsCodec.ApplyJson(new Settings(), document.RootElement);
            Require(rehydrated.Leftdodge == original.Leftdodge && rehydrated.Rightdodge == original.Rightdodge,
                "profile persistence round-trip must preserve " + direction + " for later UI hydration");
        }

        using var bothDocument = JsonDocument.Parse("""{"Leftdodge":true,"Rightdodge":true}""");
        Settings both = SettingsCodec.ApplyJson(new Settings(), bothDocument.RootElement);
        Require(both.Rightdodge && !both.Leftdodge,
            "invalid legacy data with both dodge sides true must normalize to mutually exclusive");

        var runtime = new Settings();
        var editor = new Settings { Leftdodge = true };
        runtime.CopyLiveSwitchesFrom(editor);
        Require(runtime.Leftdodge && !runtime.Rightdodge,
            "live bot updates must copy the selected dodge side immediately");
        editor = new Settings { Rightdodge = true };
        runtime.CopyLiveSwitchesFrom(editor);
        Require(runtime.Rightdodge && !runtime.Leftdodge,
            "live bot updates must follow dodge side changes");
        editor = new Settings();
        runtime.CopyLiveSwitchesFrom(editor);
        Require(!runtime.Leftdodge && !runtime.Rightdodge,
            "live bot updates must follow Back (both flags false)");
    }

    private static Settings OrangeDodgeSettings(bool left = false, bool right = false)
    {
        return new Settings
        {
            Unblockables = true,
            Pause = 0,
            Pause1 = 0,
            Pause2 = 0,
            Nohero = true,
            Leftdodge = left,
            Rightdodge = right
        };
    }

    private static CombatObservation OrangeObservationNow() =>
        Observation(Environment.TickCount64, CombatDirection.None) with
        {
            OrangeIndicator = true,
            OrangeFeint = false
        };

    private static void DriveOrangeDodge(Settings settings, FakeInputGateway input,
        FakeAutomationHost host, ActionScheduler scheduler, OrangeResponseController controller)
    {
        controller.ProcessObservation(OrangeObservationNow(), false);
        Require(SpinWait.SpinUntil(() =>
            host.VisionStates.Contains("ORANGE DODGE SENT") ||
            host.VisionStates.Contains("BULWARK SENT") ||
            host.VisionStates.Contains("BULWARK FAILED"), 2000),
            "orange response should commit promptly");
    }

    private static void OrangeExplicitSideDodgeBeatsHeldForward()
    {
        var leftInput = new FakeInputGateway();
        leftInput.HeldKeys.Add(HappyBot.Input.VK_W);
        var leftSettings = OrangeDodgeSettings(left: true);
        var leftHost = new FakeAutomationHost(leftInput, leftSettings, 201);
        var leftScheduler = new ActionScheduler(leftHost.ShutdownToken);
        var leftController = new OrangeResponseController(leftHost, leftScheduler,
            new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            DriveOrangeDodge(leftSettings, leftInput, leftHost, leftScheduler, leftController);
            Require(leftInput.Events.Contains("down:" + HappyBot.Input.VK_LEFT) &&
                leftInput.Events.Contains("tap:" + HappyBot.Input.VK_SPACE),
                "explicit Left dodge must fire while forward is held");
            Require(!leftInput.Events.Contains("down:" + HappyBot.Input.VK_DOWN),
                "explicit Left dodge must not be overridden by held-forward Down + Dodge");
        }
        finally { leftScheduler.Dispose(); }

        var rightInput = new FakeInputGateway();
        rightInput.HeldKeys.Add(HappyBot.Input.VK_W);
        var rightSettings = OrangeDodgeSettings(right: true);
        var rightHost = new FakeAutomationHost(rightInput, rightSettings, 202);
        var rightScheduler = new ActionScheduler(rightHost.ShutdownToken);
        var rightController = new OrangeResponseController(rightHost, rightScheduler,
            new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            DriveOrangeDodge(rightSettings, rightInput, rightHost, rightScheduler, rightController);
            Require(rightInput.Events.Contains("down:" + HappyBot.Input.VK_RIGHT) &&
                rightInput.Events.Contains("tap:" + HappyBot.Input.VK_SPACE),
                "explicit Right dodge must fire while forward is held");
            Require(!rightInput.Events.Contains("down:" + HappyBot.Input.VK_DOWN),
                "explicit Right dodge must not be overridden by held-forward Down + Dodge");
        }
        finally { rightScheduler.Dispose(); }

        var bothInput = new FakeInputGateway();
        bothInput.HeldKeys.Add(HappyBot.Input.VK_W);
        var bothSettings = OrangeDodgeSettings(left: true, right: true);
        var bothHost = new FakeAutomationHost(bothInput, bothSettings, 203);
        var bothScheduler = new ActionScheduler(bothHost.ShutdownToken);
        var bothController = new OrangeResponseController(bothHost, bothScheduler,
            new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            DriveOrangeDodge(bothSettings, bothInput, bothHost, bothScheduler, bothController);
            Require(bothInput.Events.Contains("down:" + HappyBot.Input.VK_RIGHT) &&
                bothInput.Events.Contains("tap:" + HappyBot.Input.VK_SPACE),
                "legacy both-true dodge data must normalize to an explicit side dodge");
            Require(!bothInput.Events.Contains("down:" + HappyBot.Input.VK_DOWN),
                "legacy both-true dodge data must not fall back to Down + Dodge");
        }
        finally { bothScheduler.Dispose(); }
    }

    private static void OrangeBackDodgeCancelsHeldForward()
    {
        var input = new FakeInputGateway();
        input.HeldKeys.Add(HappyBot.Input.VK_W);
        var settings = OrangeDodgeSettings();
        var host = new FakeAutomationHost(input, settings, 204);
        var scheduler = new ActionScheduler(host.ShutdownToken);
        var controller = new OrangeResponseController(host, scheduler,
            new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            DriveOrangeDodge(settings, input, host, scheduler, controller);
            Require(input.Events.Contains("down:" + HappyBot.Input.VK_DOWN) &&
                input.Events.Contains("tap:" + HappyBot.Input.VK_SPACE),
                "Back dodge must cancel held-forward input with Down + Dodge");
            Require(!input.Events.Contains("down:" + HappyBot.Input.VK_LEFT) &&
                !input.Events.Contains("down:" + HappyBot.Input.VK_RIGHT),
                "Back dodge with forward held must not send a side dodge");
        }
        finally { scheduler.Dispose(); }
    }

    private static void OrangeBackDodgeNeutralWithoutForward()
    {
        var input = new FakeInputGateway();
        var settings = OrangeDodgeSettings();
        var host = new FakeAutomationHost(input, settings, 205);
        var scheduler = new ActionScheduler(host.ShutdownToken);
        var controller = new OrangeResponseController(host, scheduler,
            new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            DriveOrangeDodge(settings, input, host, scheduler, controller);
            Require(input.Events.Contains("tap:" + HappyBot.Input.VK_SPACE),
                "Back dodge without forward must send neutral Dodge");
            Require(!input.Events.Contains("down:" + HappyBot.Input.VK_LEFT) &&
                !input.Events.Contains("down:" + HappyBot.Input.VK_RIGHT) &&
                !input.Events.Contains("down:" + HappyBot.Input.VK_DOWN),
                "Back dodge without forward must not send any direction key");
        }
        finally { scheduler.Dispose(); }
    }

    private static void OrangeBulwarkStaysHighestPriority()
    {
        var input = new FakeInputGateway { ForwardHeld = true };
        input.HeldKeys.Add(HappyBot.Input.VK_W);
        var settings = OrangeDodgeSettings(left: true);
        settings.Chars["Blackprior"] = true;
        var host = new FakeAutomationHost(input, settings, 206);
        var scheduler = new ActionScheduler(host.ShutdownToken);
        var controller = new OrangeResponseController(host, scheduler,
            new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            DriveOrangeDodge(settings, input, host, scheduler, controller);
            Require(input.Events.Contains("bulwark-down"),
                "Black Prior forward response must keep Bulwark as the highest priority");
            Require(!input.Events.Contains("down:" + HappyBot.Input.VK_LEFT) &&
                !input.Events.Contains("down:" + HappyBot.Input.VK_DOWN),
                "Bulwark must preempt the configured side dodge and the forward override");
        }
        finally { scheduler.Dispose(); }
    }
}
