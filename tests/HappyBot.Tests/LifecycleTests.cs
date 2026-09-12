using System.Diagnostics;
using System.Drawing;
using HappyBot;
using HappyBot.Automation;
using HappyBot.Combat;
using HappyBot.Infrastructure.Input;
using HappyBot.Vision;

static partial class Program
{
    private static void AutoGuardFakeInputAppliesReplacesAndReleases()
    {
        var input = new FakeInputGateway();
        var settings = new Settings { Autoblock = true, GuardHold = 1000 };
        ReactionCandidate current = new(1, CombatDirection.Left, 1, 1, false);
        string direction = "";
        var guard = new AutoGuardController(
            input,
            () => settings,
            () => true,
            () => current,
            () => false,
            () => 0,
            () => new Rectangle(10, 20, 30, 40),
            (_, _, _) => { },
            (_, _) => { },
            value => direction = value);

        guard.Apply(current);
        Require(guard.ActiveGuardKey == Input.VK_NUMPAD4 && direction == "LFT",
            "AutoGuard should apply the left guard and publish its direction");
        Require(input.Events.Contains("down:" + Input.VK_NUMPAD4),
            "AutoGuard should press the left guard key");

        current = current with { Id = 2, Direction = CombatDirection.Right };
        guard.Apply(current);
        Require(guard.ActiveGuardKey == Input.VK_NUMPAD6 && direction == "RGT",
            "a replacement candidate should switch the active guard direction");
        Require(input.Events.Contains("up:" + Input.VK_NUMPAD4) && input.Events.Contains("down:" + Input.VK_NUMPAD6),
            "replacing a guard must release the old key before pressing the new key");

        guard.Release("test");
        Require(guard.ActiveGuardKey == 0 && guard.ReleaseTick == 0,
            "explicit guard release should clear the active state");
        Require(input.Events.Contains("up:" + Input.VK_NUMPAD6),
            "explicit guard release must release the active key");
        guard.Dispose();
        guard.Dispose();
    }

    private static void SchedulerImmediateStateIsAuthoritative()
    {
        var scheduler = new ActionScheduler(CancellationToken.None);
        bool sawCurrent = false;
        bool sawBusy = false;
        bool scheduled = scheduler.TrySchedule(77, "IMMEDIATE", _ =>
        {
            sawCurrent = scheduler.IsCurrent(77);
            sawBusy = scheduler.IsBusy;
            return Task.FromResult(false);
        });

        Require(scheduled && sawCurrent && sawBusy,
            "scheduler state must be active while an immediate worker is starting");
        scheduler.Dispose();
    }

    private static void ZeroDelayReactionActionsCommit()
    {
        var parryInput = new FakeInputGateway();
        parryInput.HeldKeys.Add(Input.VK_F);
        var parrySettings = new Settings
        {
            Autoblock = true,
            Parry = true,
            Legit = false,
            ParryDelay = 0
        };
        var parryHost = new FakeAutomationHost(parryInput, parrySettings, 101);
        var parryScheduler = new ActionScheduler(parryHost.ShutdownToken);
        var parryExecutor = new ReactionActionExecutor(parryHost, parryScheduler, new FixedRollSource(0));
        parryExecutor.QueueReaction(new ReactionCommand(101, ReactionCommandKind.Parry, "F", CombatDirection.Left));
        Require(parryHost.ParryCount == 1 && parryInput.Events.Contains("click:" + Input.VK_RBUTTON) &&
            parryHost.ParryEvidenceRequests.SequenceEqual(new[] { "101:Left" }) &&
            parryHost.ParryEvidenceDelays.SequenceEqual(new[] { 0 }) &&
            parryHost.VisionDelays["PARRY SENT"] == 0,
            "a delivered zero-delay parry should increment RT sent and request one evidence attempt");
        parryScheduler.Dispose();

        var failedParryInput = new FakeInputGateway { FailHeavy = true };
        failedParryInput.HeldKeys.Add(Input.VK_F);
        var failedParryHost = new FakeAutomationHost(failedParryInput, parrySettings, 103);
        var failedParryScheduler = new ActionScheduler(failedParryHost.ShutdownToken);
        var failedParryExecutor = new ReactionActionExecutor(failedParryHost, failedParryScheduler, new FixedRollSource(0));
        failedParryExecutor.QueueReaction(new ReactionCommand(103, ReactionCommandKind.Parry, "F", CombatDirection.Right));
        Require(failedParryHost.ParryCount == 0 && failedParryHost.ParryEvidenceRequests.Count == 0 &&
            failedParryHost.VisionStates.Contains("PARRY FAILED"),
            "an undelivered RT must not increment attempts or request evidence");
        failedParryScheduler.Dispose();

        var crushingInput = new FakeInputGateway();
        crushingInput.HeldKeys.Add(Input.VK_F);
        var crushingSettings = new Settings { Autoblock = true, Crushing = true, ParryDelay = 0 };
        var crushingHost = new FakeAutomationHost(crushingInput, crushingSettings, 102);
        var crushingScheduler = new ActionScheduler(crushingHost.ShutdownToken);
        var crushingExecutor = new ReactionActionExecutor(crushingHost, crushingScheduler, new FixedRollSource(0));
        crushingExecutor.QueueReaction(new ReactionCommand(102, ReactionCommandKind.Crushing, "F", CombatDirection.Right));
        Require(crushingInput.Events.Contains("click:" + Input.VK_LBUTTON),
            "a zero-delay crushing action should commit RB input");
        Require(crushingHost.VisionDelays["CRUSHING SENT"] == 0,
            "a crushing action should publish its immediate timing");
        crushingScheduler.Dispose();
    }

    private static void CapturedReactionDelaySurvivesTimingEdit()
    {
        var input = new FakeInputGateway();
        input.HeldKeys.Add(Input.VK_F);
        var settings = new Settings
        {
            Autoblock = true,
            Parry = true,
            Legit = false,
            ParryDelay = 80
        };
        var host = new FakeAutomationHost(input, settings, 106);
        var scheduler = new ActionScheduler(host.ShutdownToken);
        var executor = new ReactionActionExecutor(host, scheduler, new FixedRollSource(0));
        executor.QueueReaction(new ReactionCommand(106, ReactionCommandKind.Parry, "F", CombatDirection.Left));

        Require(SpinWait.SpinUntil(() => host.VisionStates.Contains("PARRY READY"), 1000),
            "the parry action should publish its ready state before waiting");
        settings.ParryDelay = 1;
        Require(SpinWait.SpinUntil(() => host.VisionStates.Contains("PARRY SENT"), 1000) &&
            host.VisionDelays["PARRY SENT"] == 80,
            "a later timing edit must not change the delay retained for the in-flight parry");
        scheduler.Dispose();
    }

    private static void ParryConfirmationUsesAttemptDelayAfterUnrelatedReaction()
    {
        var input = new FakeInputGateway();
        var bot = new BotCore(input, new FixedRollSource(0), new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            IAutomationHost host = bot;
            host.RequestParryEvidence(501, CombatDirection.Left, 80);
            host.SetVisionReaction("PARRY SENT", "RT input sent", "LEFT", 1300, 80);
            host.SetVisionReaction("GUARD", "Unrelated reaction", "LEFT", 900, -1);

            long sentTick = Environment.TickCount64;
            Rectangle bounds = new(0, 0, 1920, 1200);
            ScreenFrame clear = SyntheticImpactFrame(1920, 1200, 0, 0, 0, 0);
            ScreenFrame impact = SyntheticImpactFrame(1920, 1200, 900, 255, 255, 255);
            bot.ProcessParryConfirmationForTests(clear, sentTick + 10, bounds);
            bot.ProcessParryConfirmationForTests(clear, sentTick + 50, bounds);
            bot.ProcessParryConfirmationForTests(impact, sentTick + 150, bounds);
            bot.ProcessParryConfirmationForTests(impact, sentTick + 180, bounds);

            VisionSnapshot snapshot = bot.GetVisionSnapshot();
            Require(snapshot.LastReactionState == "PARRY CONFIRMED" && snapshot.LastReactionDelayMs == 80,
                "confirmation must retrieve the original parry attempt delay after an unrelated reaction");
        }
        finally
        {
            bot.Dispose();
        }
    }

    private static void ParryConfirmationTrackerConfirmsLightAndHeavyImpacts()
    {
        var cases = new[]
        {
            (id: "light", direction: CombatDirection.Left, pixels: 900, red: 255, green: 255, blue: 255),
            (id: "heavy", direction: CombatDirection.Top, pixels: 500, red: 255, green: 225, blue: 110)
        };
        foreach (var testCase in cases)
        {
            var tracker = new ParryConfirmationTracker();
            const long sent = 1000;
            tracker.Start(testCase.id, 7, testCase.direction, sent);
            ScreenFrame baseline = SyntheticImpactFrame(1920, 1200, 0, 0, 0, 0);
            Require(tracker.Scan(baseline, sent + 10).Single().Result == ParryConfirmationResult.None,
                "the first post-RT scan should only seed the baseline");
            ParryConfirmationScan baselineScan = tracker.Scan(baseline, sent + 50).Single();
            Require(baselineScan.BaselineEstablished && baselineScan.Baseline == 0,
                "the second post-RT scan should establish a zero baseline");

            ScreenFrame impact = SyntheticImpactFrame(1920, 1200, testCase.pixels,
                testCase.red, testCase.green, testCase.blue);
            ParryConfirmationScan firstBright = tracker.Scan(impact, sent + 150).Single();
            Require(firstBright.Qualifying && firstBright.Result == ParryConfirmationResult.None,
                testCase.id + " impact should qualify once without confirming");
            ParryConfirmationScan secondBright = tracker.Scan(impact, sent + 180).Single();
            Require(secondBright.Qualifying && secondBright.Result == ParryConfirmationResult.Confirmed,
                testCase.id + " impact should confirm on two consecutive qualifying scans");
        }
    }

    private static void ParryConfirmationTrackerRespectsTimingAndScaledThresholds()
    {
        var early = new ParryConfirmationTracker();
        early.Start("early", 1, CombatDirection.Right, 2000);
        ScreenFrame clear = SyntheticImpactFrame(1920, 1200, 0, 0, 0, 0);
        early.Scan(clear, 2010);
        early.Scan(SyntheticImpactFrame(1920, 1200, 900, 255, 255, 255), 2040);
        ParryConfirmationScan earlyBright = early.Scan(SyntheticImpactFrame(1920, 1200, 900, 255, 255, 255), 2150).Single();
        Require(!earlyBright.Qualifying, "a bright scene before 150ms must remain part of the baseline");

        var oneScan = new ParryConfirmationTracker();
        oneScan.Start("one", 2, CombatDirection.Left, 3000);
        oneScan.Scan(clear, 3010);
        oneScan.Scan(clear, 3050);
        ScreenFrame impact = SyntheticImpactFrame(1920, 1200, 500, 255, 255, 255);
        Require(oneScan.Scan(impact, 3150).Single().Qualifying,
            "the first post-window impact scan should qualify");
        ParryConfirmationScan expired = oneScan.Scan(impact, 3701).Single();
        Require(expired.Result == ParryConfirmationResult.Unconfirmed,
            "one qualifying scan must expire as unconfirmed");

        var late = new ParryConfirmationTracker();
        late.Start("late", 3, CombatDirection.Top, 4000);
        late.Scan(clear, 4010);
        late.Scan(clear, 4050);
        ParryConfirmationScan lateScan = late.Scan(impact, 4651).Single();
        Require(lateScan.Result == ParryConfirmationResult.Unconfirmed,
            "a scan after 650ms must not confirm");

        Require(ParryConfirmationTracker.ScaledThreshold(0, 1920, 1200) == 400 &&
            ParryConfirmationTracker.ScaledThreshold(0, 960, 600) == 100,
            "confirmation thresholds should scale by screen area");
        var scaled = new ParryConfirmationTracker();
        scaled.Start("scaled", 4, CombatDirection.Left, 5000);
        ScreenFrame smallClear = SyntheticImpactFrame(960, 600, 0, 0, 0, 0);
        scaled.Scan(smallClear, 5010);
        scaled.Scan(smallClear, 5050);
        ScreenFrame smallImpact = SyntheticImpactFrame(960, 600, 110, 255, 225, 110);
        Require(scaled.Scan(smallImpact, 5150).Single().Qualifying,
            "a scaled frame should use the scaled bright-pixel threshold");
        Require(scaled.Scan(smallImpact, 5180).Single().Result == ParryConfirmationResult.Confirmed,
            "a scaled frame should confirm after two qualifying scans");
    }

    private static void DeflectSendsLightOnlyAfterSuccessfulDodge()
    {
        var successInput = new FakeInputGateway();
        successInput.HeldKeys.Add(Input.VK_F);
        var settings = new Settings { Autoblock = true, Deflect = true, Left = 0, Right = 0 };
        var successHost = new FakeAutomationHost(successInput, settings, 104);
        var successScheduler = new ActionScheduler(successHost.ShutdownToken);
        var successExecutor = new ReactionActionExecutor(successHost, successScheduler, new FixedRollSource(0));
        successExecutor.QueueReaction(new ReactionCommand(104, ReactionCommandKind.Deflect, "F", CombatDirection.Left));

        int dodgeIndex = successInput.Events.IndexOf("tap:" + Input.VK_SPACE);
        int lightIndex = successInput.Events.IndexOf("click:" + Input.VK_LBUTTON);
        Require(dodgeIndex >= 0 && lightIndex > dodgeIndex,
            "a successful deflect must complete the dodge sequence before sending the RB light");
        Require(successHost.VisionStates.Contains("DEFLECT + LIGHT SENT"),
            "a successful deflect-plus-light should publish its combined state");
        Require(successHost.VisionDelays["DEFLECT + LIGHT SENT"] == 0,
            "a deflect should publish the delay used before the directional dodge");
        Require(successHost.AutomationLightRegistrations == 1,
            "a successfully delivered deflect light must register outgoing-orange suppression");
        successScheduler.Dispose();

        var failedInput = new FakeInputGateway { FailDeflect = true };
        failedInput.HeldKeys.Add(Input.VK_F);
        var failedHost = new FakeAutomationHost(failedInput, settings, 105);
        var failedScheduler = new ActionScheduler(failedHost.ShutdownToken);
        var failedExecutor = new ReactionActionExecutor(failedHost, failedScheduler, new FixedRollSource(0));
        failedExecutor.QueueReaction(new ReactionCommand(105, ReactionCommandKind.Deflect, "F", CombatDirection.Left));

        Require(!failedInput.Events.Contains("click:" + Input.VK_LBUTTON),
            "a failed deflect must not send the RB light");
        Require(failedHost.AutomationLightRegistrations == 0,
            "a failed deflect must not register outgoing-orange suppression");
        Require(failedHost.VisionStates.Contains("DEFLECT FAILED"),
            "a failed deflect should retain its failure state");
        failedScheduler.Dispose();

        var undeliveredInput = new FakeInputGateway { FailLight = true };
        undeliveredInput.HeldKeys.Add(Input.VK_F);
        var undeliveredHost = new FakeAutomationHost(undeliveredInput, settings, 106);
        var undeliveredScheduler = new ActionScheduler(undeliveredHost.ShutdownToken);
        var undeliveredExecutor = new ReactionActionExecutor(undeliveredHost, undeliveredScheduler, new FixedRollSource(0));
        undeliveredExecutor.QueueReaction(new ReactionCommand(106, ReactionCommandKind.Deflect, "F", CombatDirection.Left));
        Require(undeliveredHost.AutomationLightRegistrations == 0,
            "an undelivered RB light must not register outgoing-orange suppression");
        undeliveredScheduler.Dispose();
    }

    private static void OrochiDeflectSendsHeavyAfterSuccessfulDodge()
    {
        var input = new FakeInputGateway();
        input.HeldKeys.Add(Input.VK_F);
        var settings = new Settings { Autoblock = true, Deflect = true, YourHero = true, Left = 0 };
        settings.Chars["Orochi"] = true;
        var host = new FakeAutomationHost(input, settings, 107);
        var scheduler = new ActionScheduler(host.ShutdownToken);
        var executor = new ReactionActionExecutor(host, scheduler, new FixedRollSource(0));
        executor.QueueReaction(new ReactionCommand(107, ReactionCommandKind.Deflect, "F", CombatDirection.Left));

        Require(SpinWait.SpinUntil(() => input.Events.Contains("click:" + Input.VK_RBUTTON), 1000),
            "an Orochi deflect must send the RT heavy follow-up");
        int dodgeIndex = input.Events.IndexOf("tap:" + Input.VK_SPACE);
        int heavyIndex = input.Events.IndexOf("click:" + Input.VK_RBUTTON);
        Require(dodgeIndex >= 0 && heavyIndex > dodgeIndex && !input.Events.Contains("click:" + Input.VK_LBUTTON),
            "an Orochi deflect must complete the dodge before RT and must not send RB");
        Require(host.VisionStates.Contains("DEFLECT + HEAVY SENT") && host.AutomationLightRegistrations == 0,
            "an Orochi deflect should publish the heavy response without registering a light attack");
        scheduler.Dispose();
    }

    private static void ShutdownWithoutStartReleasesInputsInBackground()
    {
        // Closing without ever starting automation, with input submission
        // blocked: Dispose must return without waiting on input, and the
        // shared pipeline must still neutralize and tear down after unblock.
        var input = new BlockingReleaseInputGateway();
        input.ReleaseGate.Reset();
        var bot = new BotCore(input, new FixedRollSource(0), new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            var sw = Stopwatch.StartNew();
            bot.Dispose();
            sw.Stop();
            Require(sw.Elapsed < TimeSpan.FromSeconds(2), "Dispose must not wait on blocked input submission");
            Require(input.ReleaseEntered.Wait(TimeSpan.FromSeconds(10)), "background pipeline must reach input release");
            Require(!bot.LoopResourcesDisposed, "teardown must wait for neutralization");
            input.ReleaseGate.Set();
            Require(SpinUntil(() => input.SawEvent("release-all"), TimeSpan.FromSeconds(10)), "inputs must be neutralized after unblock");
            Require(SpinUntil(() => bot.LoopResourcesDisposed, TimeSpan.FromSeconds(10)), "loop resources must be torn down after unblock");
            Require(!bot.IsRunning, "worker must not be running");
        }
        finally
        {
            input.ReleaseGate.Set();
            bot.Dispose();
        }
    }

    private static void ShutdownDuringBlockedCleanupStillTearsDownAfterUnblock()
    {
        // Worker exit while deferred cleanup holds the shutdown gate: Stop
        // blocks the gate on stalled input, Dispose must still return
        // immediately, and teardown must complete after unblock.
        var input = new BlockingReleaseInputGateway();
        input.ReleaseGate.Reset();
        var bot = new BotCore(input, new FixedRollSource(0), new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            bot.Start();
            Require(SpinUntil(() => bot.IsRunning, TimeSpan.FromSeconds(10)), "worker must start");
            bot.Stop();
            Require(input.ReleaseEntered.Wait(TimeSpan.FromSeconds(10)), "Stop pipeline must reach blocked input release");
            var sw = Stopwatch.StartNew();
            bot.Dispose();
            sw.Stop();
            Require(sw.Elapsed < TimeSpan.FromSeconds(2), "Dispose must not wait on the held shutdown gate");
            Require(!bot.LoopResourcesDisposed, "teardown must wait for the gate");
            input.ReleaseGate.Set();
            Require(SpinUntil(() => input.SawEvent("release-all"), TimeSpan.FromSeconds(10)), "inputs must be neutralized after unblock");
            Require(SpinUntil(() => bot.LoopResourcesDisposed, TimeSpan.FromSeconds(10)), "teardown must complete after unblock");
            Require(SpinUntil(() => !bot.IsRunning, TimeSpan.FromSeconds(10)), "worker must exit");
        }
        finally
        {
            input.ReleaseGate.Set();
            bot.Dispose();
        }
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed >= timeout) return false;
            Thread.Sleep(10);
        }
        return true;
    }
}
