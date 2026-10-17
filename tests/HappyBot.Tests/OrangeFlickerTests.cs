using HappyBot;
using HappyBot.Automation;
using HappyBot.Combat;

static partial class Program
{
    private static int OrangeDodgesSent(FakeAutomationHost host) =>
        host.VisionStates.Count(state => state == "ORANGE DODGE SENT");

    private static void OrangeFlickerDoesNotRepeatResponse()
    {
        var input = new FakeInputGateway();
        var host = new FakeAutomationHost(input, OrangeDodgeSettings(), 301);
        var scheduler = new ActionScheduler(host.ShutdownToken);
        var controller = new OrangeResponseController(host, scheduler,
            new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            CombatObservation orange = OrangeObservationNow();
            long t = orange.TimestampMs;
            controller.ProcessObservation(orange, false);
            Require(OrangeDodgesSent(host) == 1, "the first orange frame should dodge");
            Require(SpinWait.SpinUntil(() => !scheduler.IsBusy, 2000), "the dodge should finish its cooldown");

            // One dropout frame, then the same orange past the 250ms re-arm throttle.
            controller.ProcessObservation(orange with { TimestampMs = t + 20, OrangeIndicator = false }, false);
            controller.ProcessObservation(orange with { TimestampMs = t + 300 }, false);
            Require(OrangeDodgesSent(host) == 1,
                "a single-frame orange dropout must not fire a second response to the same attack");

            // A sustained clear does re-arm for the next unblockable.
            controller.ProcessObservation(orange with { TimestampMs = t + 320, OrangeIndicator = false }, false);
            controller.ProcessObservation(orange with { TimestampMs = t + 420, OrangeIndicator = false }, false);
            controller.ProcessObservation(orange with { TimestampMs = t + 700 }, false);
            Require(OrangeDodgesSent(host) == 2,
                "an orange after a debounced clear must get its own response");
        }
        finally { scheduler.Dispose(); }
    }

    private static void OrangeFlickerDuringDelayDoesNotDropResponse()
    {
        var input = new FakeInputGateway();
        Settings settings = OrangeDodgeSettings();
        settings.Pause = 120;
        var host = new FakeAutomationHost(input, settings, 302);
        var scheduler = new ActionScheduler(host.ShutdownToken);
        var controller = new OrangeResponseController(host, scheduler,
            new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            CombatObservation orange = OrangeObservationNow();
            controller.ProcessObservation(orange, false);
            controller.ProcessObservation(orange with { TimestampMs = orange.TimestampMs + 20, OrangeIndicator = false }, false);
            controller.ProcessObservation(orange with { TimestampMs = orange.TimestampMs + 40 }, false);
            Require(SpinWait.SpinUntil(() => OrangeDodgesSent(host) == 1, 2000),
                "a dropout frame during the orange delay must not cancel the pending response");
        }
        finally { scheduler.Dispose(); }
    }

    private static void HoldReleaseCancelsReactionsButNotOrange()
    {
        var input = new FakeInputGateway();
        Settings settings = OrangeDodgeSettings();
        settings.Pause = 120;
        var host = new FakeAutomationHost(input, settings, 303);
        using var actions = new DirectionalActionExecutor(host, new FixedRollSource(0),
            new FixedOrangeDirectionSource(CombatDirection.Top));

        actions.ProcessOrangeObservation(OrangeObservationNow(), false);
        actions.CancelPendingReaction("hold-released");
        Require(SpinWait.SpinUntil(() => OrangeDodgesSent(host) == 1, 2000),
            "releasing LT must not cancel a pending orange response");
        Require(SpinWait.SpinUntil(() => !actions.IsBusy, 2000), "the orange dodge should finish");

        var scheduler = new ActionScheduler(CancellationToken.None);
        try
        {
            var started = new ManualResetEventSlim();
            Require(scheduler.TrySchedule(303, "ARMED Deflect", async token =>
            {
                started.Set();
                await Task.Delay(5000, token);
                return true;
            }), "the reaction should schedule");
            started.Wait(2000);
            Require(!scheduler.CancelPending("hold-released", false, null, id => id == 0),
                "a filter that rejects the candidate must leave the work running");
            Require(scheduler.CancelPending("hold-released", false, null, id => id != 0),
                "hold release must still cancel pending reaction work");
            Require(SpinWait.SpinUntil(() => !scheduler.IsBusy, 2000), "cancelled reaction should end");
        }
        finally { scheduler.Dispose(); }
    }

    private static void OutgoingOrangeGuardDebouncesClear()
    {
        // Own orange outlives the window; a dropout after expiry must not hand it to the response.
        var guard = new OutgoingOrangeGuard();
        guard.Observe(100, true, false, true);
        guard.Observe(1400, true, true, false);
        OutgoingOrangeGuardResult dropout = guard.Observe(1700, true, false, false);
        OutgoingOrangeGuardResult same = guard.Observe(1720, true, true, false);
        Require(!dropout.SelfOrangeCleared && same.SuppressesOrange && same.SelfOrangeLatched && !same.SelfOrangeStarted,
            "a single clear frame must not release the own-orange latch");
        guard.Observe(1740, true, false, false);
        OutgoingOrangeGuardResult cleared = guard.Observe(1840, true, false, false);
        Require(cleared.SelfOrangeCleared && !cleared.SuppressesOrange,
            "a sustained clear must release the own-orange latch");

        // Enemy orange keeps its attribution through a dropout inside the window.
        var enemy = new OutgoingOrangeGuard();
        enemy.RegisterAutomationLight(100);
        enemy.Observe(500, true, true, false, false, incomingDirection: true);
        enemy.Observe(520, true, false, false, false);
        OutgoingOrangeGuardResult directionless = enemy.Observe(540, true, true, false, false);
        Require(!directionless.SuppressesOrange && !directionless.SelfOrangeStarted,
            "a dropout must not re-attribute an enemy orange to the outgoing window");
    }
}
