using HappyBot;
using HappyBot.Automation;
using HappyBot.Combat;

static partial class Program
{
    private static void OrangeHeroFollowUpRequiresYourHeroAndSkipsAfterGenericFollowUp()
    {
        // Orochi picked but the hero response switch is off: no hero follow-up.
        var offSettings = OrangeDodgeSettings(left: true);
        offSettings.Nohero = false;
        offSettings.Chars["Orochi"] = true;
        var offInput = new FakeInputGateway();
        var offHost = new FakeAutomationHost(offInput, offSettings, 301);
        var offScheduler = new ActionScheduler(offHost.ShutdownToken);
        try
        {
            DriveOrangeDodge(offSettings, offInput, offHost, offScheduler,
                new OrangeResponseController(offHost, offScheduler, new FixedOrangeDirectionSource(CombatDirection.Top)));
            Require(!offInput.Events.Contains("tap:" + Input.VK_NUMPAD9),
                "an orange dodge must not send the Orochi follow-up while the hero response is off");
        }
        finally { offScheduler.Dispose(); }

        // Hero response on, no generic follow-up: Orochi follow-up is sent.
        var heroSettings = OrangeDodgeSettings(left: true);
        heroSettings.Nohero = false;
        heroSettings.YourHero = true;
        heroSettings.Chars["Orochi"] = true;
        var heroInput = new FakeInputGateway();
        var heroHost = new FakeAutomationHost(heroInput, heroSettings, 302);
        var heroScheduler = new ActionScheduler(heroHost.ShutdownToken);
        try
        {
            DriveOrangeDodge(heroSettings, heroInput, heroHost, heroScheduler,
                new OrangeResponseController(heroHost, heroScheduler, new FixedOrangeDirectionSource(CombatDirection.Top)));
            Require(SpinWait.SpinUntil(() => heroInput.Events.Contains("tap:" + Input.VK_NUMPAD9), 2000),
                "an orange dodge should send the Orochi follow-up when the hero response is on");
        }
        finally { heroScheduler.Dispose(); }

        // Hero response on plus a dodge light: only the light follows the dodge.
        var stackSettings = OrangeDodgeSettings(left: true);
        stackSettings.Nohero = false;
        stackSettings.YourHero = true;
        stackSettings.DodgeL = true;
        stackSettings.Chars["Orochi"] = true;
        var stackInput = new FakeInputGateway();
        var stackHost = new FakeAutomationHost(stackInput, stackSettings, 303);
        var stackScheduler = new ActionScheduler(stackHost.ShutdownToken);
        try
        {
            DriveOrangeDodge(stackSettings, stackInput, stackHost, stackScheduler,
                new OrangeResponseController(stackHost, stackScheduler, new FixedOrangeDirectionSource(CombatDirection.Top)));
            Require(SpinWait.SpinUntil(() => stackInput.Events.Contains("click:" + Input.VK_LBUTTON), 2000),
                "the dodge light follow-up should still be sent");
            Require(!stackInput.Events.Contains("tap:" + Input.VK_NUMPAD9),
                "the hero follow-up must not stack on top of a generic dodge follow-up");
        }
        finally { stackScheduler.Dispose(); }
    }

    private static void WardenTopCrushingUsesLegitRoll()
    {
        var settings = new Settings
        {
            Autoblock = true,
            Parry = true,
            Legit = true,
            LegitParryChance = 40,
            YourHero = true
        };
        settings.Chars["Warden"] = true;
        var command = new ReactionCommand(401, ReactionCommandKind.Crushing, "F", CombatDirection.Top,
            RequiresParryEnabled: true);

        var failInput = new FakeInputGateway();
        failInput.HeldKeys.Add(Input.VK_F);
        var failHost = new FakeAutomationHost(failInput, settings, 401);
        var failScheduler = new ActionScheduler(failHost.ShutdownToken);
        try
        {
            new ReactionActionExecutor(failHost, failScheduler, new FixedRollSource(99)).QueueReaction(command);
            Require(!failInput.Events.Contains("click:" + Input.VK_LBUTTON) && failHost.VisionStates.Contains("BLOCK ONLY"),
                "a failed Legit roll must block instead of sending Warden's top crushing counter");
        }
        finally { failScheduler.Dispose(); }

        var passInput = new FakeInputGateway();
        passInput.HeldKeys.Add(Input.VK_F);
        var passHost = new FakeAutomationHost(passInput, settings, 401);
        var passScheduler = new ActionScheduler(passHost.ShutdownToken);
        try
        {
            new ReactionActionExecutor(passHost, passScheduler, new FixedRollSource(0)).QueueReaction(command);
            Require(passInput.Events.Contains("click:" + Input.VK_LBUTTON) && passHost.VisionStates.Contains("CRUSHING SENT"),
                "a passed Legit roll must send Warden's top crushing counter");
        }
        finally { passScheduler.Dispose(); }
    }

    private static void BehaviorSummaryReportsOverriddenSwitches()
    {
        var parryDeflect = new Settings { Autoblock = true, Parry = true, Deflect = true, Crushing = true };
        string[] notices = ReactionBehaviorSummary.Create(parryDeflect, true).Notices;
        Require(notices.Any(n => n.StartsWith("Deflect never runs: Parry")) &&
                notices.Any(n => n.StartsWith("Crushing counter never runs: Parry")),
            "Parry without Legit must report that Deflect and Crushing are overridden");

        parryDeflect.Legit = true;
        notices = ReactionBehaviorSummary.Create(parryDeflect, true).Notices;
        Require(!notices.Any(n => n.Contains("never runs")),
            "with Legit on, Deflect and Crushing are live fallbacks and must not be reported");

        var crushingDeflect = new Settings { Autoblock = true, Crushing = true, Deflect = true };
        Require(ReactionBehaviorSummary.Create(crushingDeflect, true).Notices
                .Any(n => n.StartsWith("Deflect never runs: Crushing")),
            "Crushing must report that it overrides Deflect");

        var legitDeflect = new Settings { Autoblock = true, Deflect = true, Legit = true };
        Require(ReactionBehaviorSummary.Create(legitDeflect, true).Notices
                .Any(n => n.StartsWith("Legit has no effect")),
            "Legit without Parry must be reported as having no effect");

        var eBoth = new Settings { Autoblock = true, Parry2 = true, Crushing2 = true };
        Require(ReactionBehaviorSummary.Create(eBoth, true).Notices
                .Any(n => n.StartsWith("E Crushing counter never runs")),
            "E Parry must report that it overrides E Crushing");
    }
}
