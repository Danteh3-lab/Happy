using System.Drawing;
using HappyBot;

static class Program
{
    private static int Main()
    {
        try
        {
            FlashWithinGuardSendsOnce();
            PersistentThreatSurvivesGuardWindow();
            MissingThreatExpiresAfterGrace();
            StaleFlashIsIgnored();
            CandidateTimesOutAndRequiresClear();
            LatestDirectionReplacesCandidate();
            IgnoredFlashIsConsumedAndCannotTriggerLate();
            LegitPercentageUsesBoundaryRolls();
            LegitOffAlwaysParriesWithoutRolling();
            FAndEParriesBothUsePercentage();
            FailedFParryCanResolveToBulwark();
            FailedFParryCanResolveToCrushing();
            CrushingFallbackMixUsesConfiguredPercentage();
            DeflectFallbackMixUsesConfiguredPercentage();
            BulwarkFallbackEligibilityIsStrict();
            OrangeOnlyLightSelectionIsDeterministic();
            OrangeRedResponseKeepsCurrentPriority();
            OrangeMarkerLossDoesNotClearResponseLatch();
            OutgoingOrangeGuardSuppressesOwnAttackUntilClear();
            FailedLegitDecisionLeavesCandidateAvailableForGuard();
            AutoBlockOffDoesNotArmCandidate();
            FullFrameScreenCoordinatesPreserveRoiDetection();
            Console.WriteLine("ReactionCoordinator tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void FlashWithinGuardSendsOnce()
    {
        var coordinator = new ReactionCoordinator();
        CoordinatorTick armed = coordinator.Tick(Observation(1, CombatDirection.Left), ReactionCommandKind.None, "");
        Require(armed.Candidate is not null, "candidate should arm");
        CoordinatorTick flash = coordinator.Tick(Observation(400, CombatDirection.Left, flash: true), ReactionCommandKind.Parry, "F");
        Require(flash.Command is { Kind: ReactionCommandKind.Parry }, "flash should issue parry");
        CoordinatorTick duplicate = coordinator.Tick(Observation(410, CombatDirection.Left, flash: true), ReactionCommandKind.Parry, "F");
        Require(duplicate.Command is null, "consumed candidate must not issue a duplicate parry");
    }

    private static void PersistentThreatSurvivesGuardWindow()
    {
        var coordinator = new ReactionCoordinator();
        coordinator.Tick(Observation(1, CombatDirection.Top), ReactionCommandKind.None, "");
        CoordinatorTick tick = coordinator.Tick(Observation(1000, CombatDirection.Top), ReactionCommandKind.None, "");
        Require(tick.Candidate is { Direction: CombatDirection.Top }, "persistent threat should remain active beyond guard hold");
    }

    private static void MissingThreatExpiresAfterGrace()
    {
        var coordinator = new ReactionCoordinator();
        coordinator.Tick(Observation(1, CombatDirection.Right), ReactionCommandKind.None, "");
        coordinator.Tick(Observation(201, CombatDirection.None, hasThreat: false), ReactionCommandKind.None, "");
        CoordinatorTick expired = coordinator.Tick(Observation(252, CombatDirection.None, hasThreat: false), ReactionCommandKind.None, "");
        Require(expired.Candidate is null && expired.CancellationReason == "indicator-stale", "candidate should expire after 250ms grace");
    }

    private static void StaleFlashIsIgnored()
    {
        var coordinator = new ReactionCoordinator();
        CoordinatorTick tick = coordinator.Tick(Observation(1, CombatDirection.None, hasThreat: false, flash: true), ReactionCommandKind.Parry, "F");
        Require(tick.IgnoredStaleFlash && tick.Command is null, "flash without candidate must be ignored");
    }

    private static void CandidateTimesOutAndRequiresClear()
    {
        var coordinator = new ReactionCoordinator();
        coordinator.Tick(Observation(1, CombatDirection.Left), ReactionCommandKind.None, "");
        CoordinatorTick timeout = coordinator.Tick(Observation(3002, CombatDirection.Left), ReactionCommandKind.None, "");
        Require(timeout.Candidate is null && timeout.CancellationReason == "candidate-timeout", "candidate should hit 3 second limit");
        CoordinatorTick blocked = coordinator.Tick(Observation(3011, CombatDirection.Left), ReactionCommandKind.None, "");
        Require(blocked.Candidate is null, "same persistent indicator must clear before rearm");
        coordinator.Tick(Observation(3021, CombatDirection.None, hasThreat: false), ReactionCommandKind.None, "");
        CoordinatorTick rearmed = coordinator.Tick(Observation(3031, CombatDirection.Left), ReactionCommandKind.None, "");
        Require(rearmed.Candidate is not null, "indicator should rearm after clear");
    }

    private static void LatestDirectionReplacesCandidate()
    {
        var coordinator = new ReactionCoordinator();
        CoordinatorTick first = coordinator.Tick(Observation(1, CombatDirection.Left), ReactionCommandKind.None, "");
        CoordinatorTick replacement = coordinator.Tick(Observation(50, CombatDirection.Right), ReactionCommandKind.None, "");
        Require(replacement.Transition == "replaced" && replacement.Candidate.Id != first.Candidate.Id && replacement.Candidate.Direction == CombatDirection.Right,
            "latest valid direction should replace candidate");
    }

    private static void IgnoredFlashIsConsumedAndCannotTriggerLate()
    {
        var coordinator = new ReactionCoordinator();
        coordinator.Tick(Observation(1, CombatDirection.Top), ReactionCommandKind.None, "");
        CoordinatorTick ignored = coordinator.Tick(Observation(50, CombatDirection.Top, flash: true), ReactionCommandKind.None, "");
        CoordinatorTick late = coordinator.Tick(Observation(75, CombatDirection.Top, flash: true), ReactionCommandKind.Parry, "F");
        Require(ignored.Command is null && ignored.Candidate is { Consumed: true } && late.Command is null,
            "an ignored flash must be consumed so it cannot trigger late after a cooldown");
    }

    private static void LegitPercentageUsesBoundaryRolls()
    {
        ReactionCommand command = new(17, ReactionCommandKind.Parry, "F", CombatDirection.Left);
        ParryDecision zero = ParryDecision.Create(command, true, 0, new FixedRollSource(0));
        ParryDecision full = ParryDecision.Create(command, true, 100, new FixedRollSource(99));
        ParryDecision success = ParryDecision.Create(command, true, 55, new FixedRollSource(54));
        ParryDecision blocked = ParryDecision.Create(command, true, 55, new FixedRollSource(55));
        Require(!zero.ShouldParry && zero.Outcome == "BLOCK", "0% must always block");
        Require(full.ShouldParry && full.Outcome == "PARRY", "100% must always parry");
        Require(success.ShouldParry, "roll below chance must parry");
        Require(!blocked.ShouldParry, "roll equal to chance must block");
    }

    private static void LegitOffAlwaysParriesWithoutRolling()
    {
        ReactionCommand command = new(18, ReactionCommandKind.Parry, "F", CombatDirection.Top);
        var rolls = new FixedRollSource(99);
        ParryDecision decision = ParryDecision.Create(command, false, 0, rolls);
        Require(decision.ShouldParry && decision.Roll is null && rolls.Calls == 0, "Legit off must bypass the percentage roll");
    }

    private static void FAndEParriesBothUsePercentage()
    {
        ParryDecision f = ParryDecision.Create(new ReactionCommand(19, ReactionCommandKind.Parry, "F", CombatDirection.Left), true, 50, new FixedRollSource(49));
        ParryDecision e = ParryDecision.Create(new ReactionCommand(20, ReactionCommandKind.Parry, "E", CombatDirection.Right), true, 50, new FixedRollSource(50));
        Require(f.ShouldParry && f.Hold == "F", "F parry should use percentage decision");
        Require(!e.ShouldParry && e.Hold == "E", "E parry should use percentage decision");
    }

    private static void FailedLegitDecisionLeavesCandidateAvailableForGuard()
    {
        var coordinator = new ReactionCoordinator();
        CoordinatorTick armed = coordinator.Tick(Observation(1, CombatDirection.Right), ReactionCommandKind.None, "");
        CoordinatorTick flash = coordinator.Tick(Observation(50, CombatDirection.Right, flash: true), ReactionCommandKind.Parry, "F");
        ParryDecision decision = ParryDecision.Create(flash.Command!, true, 0, new FixedRollSource(0));
        CoordinatorTick guarded = coordinator.Tick(Observation(1000, CombatDirection.Right), ReactionCommandKind.None, "");
        Require(!decision.ShouldParry && armed.Candidate.Id == guarded.Candidate.Id && guarded.Candidate.Direction == CombatDirection.Right,
            "a blocked parry decision must leave the live candidate available for guard renewal");
    }

    private static void FailedFParryCanResolveToBulwark()
    {
        ReactionCommand command = new(21, ReactionCommandKind.Parry, "F", CombatDirection.Left);
        ParryResolution failed = ParryResolution.Create(command, true, 55, new FixedRollSource(55), true, true);
        ParryResolution passed = ParryResolution.Create(command, true, 55, new FixedRollSource(54), true, true);
        Require(failed.Outcome == ParryOutcome.Bulwark, "failed eligible F roll should resolve to Bulwark");
        Require(passed.Outcome == ParryOutcome.Parry, "successful roll must remain a normal parry");
    }

    private static void FailedFParryCanResolveToCrushing()
    {
        ReactionCommand f = new(24, ReactionCommandKind.Parry, "F", CombatDirection.Top);
        ReactionCommand e = new(25, ReactionCommandKind.Parry, "E", CombatDirection.Top);
        ParryResolution crushing = ParryResolution.Create(f, true, 0, new FixedRollSource(0), false, false, true);
        ParryResolution unavailable = ParryResolution.Create(f, true, 0, new FixedRollSource(0), false, false, false);
        ParryResolution eBlocked = ParryResolution.Create(e, true, 0, new FixedRollSource(0), false, false, true);
        Require(crushing.Outcome == ParryOutcome.Crushing, "failed eligible F roll should resolve to Crushing");
        Require(unavailable.Outcome == ParryOutcome.Block, "failed F roll without any fallback must guard only");
        Require(eBlocked.Outcome == ParryOutcome.Block, "E failed roll must remain guard-only even when Crushing is enabled");
    }

    private static void CrushingFallbackMixUsesConfiguredPercentage()
    {
        ReactionCommand f = new(26, ReactionCommandKind.Parry, "F", CombatDirection.Left);
        ParryResolution crushing = ParryResolution.Create(f, true, 0, new FixedRollSource(0), true, true, true, 50, new FixedRollSource(49));
        ParryResolution bulwark = ParryResolution.Create(f, true, 0, new FixedRollSource(0), true, true, true, 50, new FixedRollSource(50));
        ParryResolution zero = ParryResolution.Create(f, true, 0, new FixedRollSource(0), true, true, true, 0, new FixedRollSource(0));
        ParryResolution full = ParryResolution.Create(f, true, 0, new FixedRollSource(0), true, true, true, 100, new FixedRollSource(99));
        Require(crushing.Outcome == ParryOutcome.Crushing && crushing.FallbackRoll == 49, "roll below the Crushing chance must send RB");
        Require(bulwark.Outcome == ParryOutcome.Bulwark && bulwark.FallbackRoll == 50, "roll equal to the Crushing chance must flip");
        Require(zero.Outcome == ParryOutcome.Bulwark, "0% Crushing chance must always use Bulwark");
        Require(full.Outcome == ParryOutcome.Crushing, "100% Crushing chance must always use RB");
        Require(new Settings().CrushingFallbackChance == 50, "existing configurations must default to a 50/50 fallback mix");
    }

    private static void DeflectFallbackMixUsesConfiguredPercentage()
    {
        ReactionCommand f = new(27, ReactionCommandKind.Parry, "F", CombatDirection.Right);
        ReactionCommand e = new(28, ReactionCommandKind.Parry, "E", CombatDirection.Right);
        ParryResolution deflect = ParryResolution.Create(f, true, 0, new FixedRollSource(0), true, true,
            true, 50, new FixedRollSource(49), true, 50, new FixedRollSource(49));
        ParryResolution crushing = ParryResolution.Create(f, true, 0, new FixedRollSource(0), true, true,
            true, 50, new FixedRollSource(49), true, 50, new FixedRollSource(50));
        ParryResolution bulwark = ParryResolution.Create(f, true, 0, new FixedRollSource(0), true, true,
            true, 50, new FixedRollSource(50), true, 50, new FixedRollSource(50));
        ParryResolution deflectOnly = ParryResolution.Create(f, true, 0, new FixedRollSource(0), false, false,
            false, 50, null, true, 0, new FixedRollSource(0));
        ParryResolution deflectOnlySuccess = ParryResolution.Create(f, true, 0, new FixedRollSource(0), false, false,
            false, 50, null, true, 100, new FixedRollSource(99));
        ParryResolution eBlocked = ParryResolution.Create(e, true, 0, new FixedRollSource(0), false, false,
            false, 50, null, true, 100);

        Require(deflect.Outcome == ParryOutcome.Deflect && deflect.DeflectRoll == 49,
            "roll below the Deflect chance must dodge");
        Require(crushing.Outcome == ParryOutcome.Crushing && crushing.DeflectRoll == 50 && crushing.FallbackRoll == 49,
            "a missed Deflect roll must proceed to the existing Crushing mix");
        Require(bulwark.Outcome == ParryOutcome.Bulwark && bulwark.DeflectRoll == 50 && bulwark.FallbackRoll == 50,
            "a missed Deflect roll must preserve the Bulwark branch");
        Require(deflectOnly.Outcome == ParryOutcome.Block && deflectOnly.DeflectRoll == 0,
            "a missed sole Deflect roll must retain ordinary guard");
        Require(deflectOnlySuccess.Outcome == ParryOutcome.Deflect && deflectOnlySuccess.DeflectRoll == 99,
            "a successful sole Deflect roll must dodge");
        Require(eBlocked.Outcome == ParryOutcome.Block,
            "E path must remain guard-only even when Deflect is enabled");
        Require(new Settings().DeflectFallbackChance == 50,
            "existing configurations must default Deflect fallback to 50 percent");
    }

    private static void BulwarkFallbackEligibilityIsStrict()
    {
        ReactionCommand f = new(22, ReactionCommandKind.Parry, "F", CombatDirection.Right);
        ReactionCommand e = new(23, ReactionCommandKind.Parry, "E", CombatDirection.Right);
        Require(ParryResolution.Create(f, true, 0, new FixedRollSource(0), false, true).Outcome == ParryOutcome.Block,
            "fallback toggle off must remain guard-only");
        Require(ParryResolution.Create(f, true, 0, new FixedRollSource(0), true, false).Outcome == ParryOutcome.Block,
            "ineligible hero or input must remain guard-only");
        Require(ParryResolution.Create(e, true, 0, new FixedRollSource(0), true, true).Outcome == ParryOutcome.Block,
            "E path must remain guard-only");
        Require(ParryResolution.Create(f, false, 0, new FixedRollSource(99), true, true).Outcome == ParryOutcome.Parry,
            "Legit off must always use the normal parry path");
        Require(!new Settings().BulwarkFallback, "existing configurations must default fallback to off");
    }

    private static void OrangeOnlyLightSelectionIsDeterministic()
    {
        OrangeLightDecision light = OrangeLightDecision.Create(new FixedOrangeDirectionSource(CombatDirection.Right));
        OrangeLightDecision invalid = OrangeLightDecision.Create(new FixedOrangeDirectionSource(CombatDirection.None));
        Require(light.Direction == CombatDirection.Right, "orange-only light should use the injected direction");
        Require(invalid.Direction == CombatDirection.Top, "invalid orange direction should safely fall back to top");
        Require(OrangeResponseResolver.Resolve(false, false, true) == OrangeResponseKind.Light,
            "orange-only with Auto light enabled must choose exactly one light instead of a dodge");
        Require(!new Settings().OrangeLight, "existing configurations must default Auto light on orange to off");
    }

    private static void OrangeRedResponseKeepsCurrentPriority()
    {
        Require(OrangeResponseResolver.Resolve(true, true, true) == OrangeResponseKind.Parry,
            "orange plus red/feint must retain orange parry priority");
        Require(OrangeResponseResolver.Resolve(true, false, true) == OrangeResponseKind.Dodge,
            "orange plus red/feint with parry disabled must retain dodge behavior, never Auto light");
        Require(OrangeResponseResolver.Resolve(false, false, false) == OrangeResponseKind.Dodge,
            "Auto light off must preserve the normal orange dodge");
    }

    private static void OrangeMarkerLossDoesNotClearResponseLatch()
    {
        Require(!OrangeResponseLatch.IsConfirmedClear(false, false),
            "marker loss is an unknown orange frame and must not re-arm the same attack");
        Require(!OrangeResponseLatch.IsConfirmedClear(true, true),
            "a present orange indicator must retain its one-response latch");
        Require(OrangeResponseLatch.IsConfirmedClear(true, false),
            "only a valid marker frame with no orange can clear the response latch");
    }

    private static void OutgoingOrangeGuardSuppressesOwnAttackUntilClear()
    {
        var guard = new OutgoingOrangeGuard();
        OutgoingOrangeGuardResult attack = guard.Observe(100, true, false, true);
        OutgoingOrangeGuardResult preOrange = guard.Observe(1200, true, false, false);
        OutgoingOrangeGuardResult ownOrange = guard.Observe(1400, true, true, false);
        OutgoingOrangeGuardResult afterRelease = guard.Observe(1800, true, true, false);
        OutgoingOrangeGuardResult markerLoss = guard.Observe(1810, false, false, false);
        OutgoingOrangeGuardResult clear = guard.Observe(1820, true, false, false);
        OutgoingOrangeGuardResult nextEnemy = guard.Observe(1900, true, true, false);
        OutgoingOrangeGuardResult noSource = new OutgoingOrangeGuard().Observe(100, true, true, false);

        Require(attack.WindowActive && !attack.SelfOrangeLatched,
            "source RT should start the outgoing-orange suppression window");
        Require(preOrange.WindowActive && !preOrange.SelfOrangeLatched,
            "the outgoing-orange window should remain active for the observed attack delay");
        Require(ownOrange.SuppressesOrange && ownOrange.SelfOrangeStarted,
            "orange appearing during the source RT window should be attributed to the own attack");
        Require(ownOrange.AttributionSource == "RT",
            "the delayed orange should retain RT attribution after release");
        Require(afterRelease.SuppressesOrange && afterRelease.SelfOrangeLatched,
            "releasing RT must not allow a late response while the same orange remains");
        Require(markerLoss.SuppressesOrange,
            "marker loss must not clear the self-orange latch");
        Require(clear.SelfOrangeCleared && !clear.SuppressesOrange,
            "a valid marker frame without orange should clear the self-orange latch");
        Require(clear.AttributionSource == "RT",
            "the clear event should retain the original RT attribution");
        Require(!nextEnemy.SuppressesOrange && nextEnemy.SelfOrangeStarted == false,
            "the next orange after a confirmed clear should be eligible for normal handling");
        Require(!noSource.SuppressesOrange && !noSource.SelfOrangeLatched,
            "without a source attack signal, orange must retain normal handling");

        var lightGuard = new OutgoingOrangeGuard();
        OutgoingOrangeGuardResult lightAttack = lightGuard.Observe(100, true, false, false, true);
        OutgoingOrangeGuardResult lightOrange = lightGuard.Observe(1400, true, true, false, false);
        Require(lightAttack.WindowActive && lightOrange.SelfOrangeLatched && lightOrange.SuppressesOrange,
            "source RB/light should attribute a delayed orange indicator to the own attack");
        Require(lightOrange.AttributionSource == "RB",
            "the delayed orange should retain RB attribution after release");
    }

    private static void AutoBlockOffDoesNotArmCandidate()
    {
        var coordinator = new ReactionCoordinator();
        CoordinatorTick tick = coordinator.Tick(Observation(1, CombatDirection.Left, hasThreat: false, flash: true), ReactionCommandKind.Parry, "F");
        Require(tick.Candidate is null && tick.Command is null && tick.IgnoredStaleFlash, "without Auto block threat input, no candidate or parry may be produced");
    }

    private static void FullFrameScreenCoordinatesPreserveRoiDetection()
    {
        var frame = new ScreenFrame { Width = 4, Height = 3, Stride = 16, OriginX = 100, OriginY = 200, Buffer = new byte[48] };
        int pixel = 1 * frame.Stride + 2 * 4;
        frame.Buffer[pixel] = 41;
        frame.Buffer[pixel + 1] = 49;
        frame.Buffer[pixel + 2] = 255;

        bool found = frame.ScreenPixelSearch(100, 200, 103, 202, 255, 49, 41, 0, out int x, out int y);
        ColorProbe probe = frame.ProbeColor(2, 1, 2, 1, 255, 49, 41, 0);
        Require(found && x == 102 && y == 201, "full-frame ROI search must return screen coordinates");
        Require(probe.MatchCount == 1, "ROI telemetry probe must remain scoped to the combat region");
    }

    private static CombatObservation Observation(long ms, CombatDirection direction, bool hasThreat = true, bool flash = false) =>
        new(ms, hasThreat, new Point(900, 400), 2, new Rectangle(700, 400, 360, 450), hasThreat,
            new Point(900, 550), direction, false, flash, false, false, false, true, true, true);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixedRollSource(int value) : IParryRollSource
    {
        public int Calls { get; private set; }

        public int NextPercent()
        {
            Calls++;
            return value;
        }
    }

    private sealed class FixedOrangeDirectionSource(CombatDirection direction) : IOrangeLightDirectionSource
    {
        public CombatDirection NextDirection() => direction;
    }
}
