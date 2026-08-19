using System.Drawing;
using HappyBot;
using HappyBot.Automation;
using HappyBot.Combat;
using HappyBot.Infrastructure.Input;
using HappyBot.Vision;

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
            OutgoingOrangeGuardAutomationLightSuppressesUntilClear();
            FailedLegitDecisionLeavesCandidateAvailableForGuard();
            AutoBlockOffDoesNotArmCandidate();
            FullFrameScreenCoordinatesPreserveRoiDetection();
            ReactionPolicySelectionCoversEAndFWardenPriority();
            VisionAnalyzerUsesExplicitBoundsAndPreservesMarkerLoss();
            AutoGuardFakeInputAppliesReplacesAndReleases();
            SchedulerImmediateStateIsAuthoritative();
            ZeroDelayReactionActionsCommit();
            DeflectSendsLightOnlyAfterSuccessfulDodge();
            Console.WriteLine("ReactionCoordinator and seam tests passed.");
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

    private static void OutgoingOrangeGuardAutomationLightSuppressesUntilClear()
    {
        var guard = new OutgoingOrangeGuard();
        guard.RegisterAutomationLight(100);

        OutgoingOrangeGuardResult laterOrange = guard.Observe(500, true, true, false, false);
        Require(laterOrange.SuppressesOrange && laterOrange.SelfOrangeStarted && laterOrange.AttributionSource == "RB",
            "a bot-generated RB light should suppress and attribute a later orange as RB");

        OutgoingOrangeGuardResult markerLoss = guard.Observe(600, false, true, false, false);
        Require(markerLoss.SuppressesOrange && markerLoss.SelfOrangeLatched,
            "marker loss must not clear the automation-light orange latch");

        OutgoingOrangeGuardResult clear = guard.Observe(1601, true, false, false, false);
        Require(clear.SelfOrangeCleared && !clear.SuppressesOrange,
            "a confirmed clear after the automation window must release suppression");

        OutgoingOrangeGuardResult nextEnemy = guard.Observe(1700, true, true, false, false);
        Require(!nextEnemy.SuppressesOrange && !nextEnemy.SelfOrangeStarted,
            "a new orange after confirmed clear and window expiry must be eligible again");
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

    private static void ReactionPolicySelectionCoversEAndFWardenPriority()
    {
        Settings eSettings = new() { Autoblock = true, Parry2 = true };
        ReactionSelection eSelection = ReactionPolicy.ResolveCommand(
            Observation(10, CombatDirection.Left) with { EHeld = true, FHeld = false, LtHeld = false }, eSettings);
        Require(eSelection.Kind == ReactionCommandKind.Parry && eSelection.Hold == "E",
            "E should select the configured E parry action before the F path");

        Settings wardenSettings = new() { Autoblock = true, Parry = true, YourHero = true };
        wardenSettings.Chars["Warden"] = true;
        ReactionSelection wardenSelection = ReactionPolicy.ResolveCommand(
            Observation(20, CombatDirection.Top), wardenSettings);
        Require(wardenSelection.Kind == ReactionCommandKind.Crushing && wardenSelection.Hold == "F",
            "Warden top F should retain its crushing priority");

        wardenSettings.Chars["Warden"] = false;
        ReactionSelection normalSelection = ReactionPolicy.ResolveCommand(
            Observation(21, CombatDirection.Top), wardenSettings);
        Require(normalSelection.Kind == ReactionCommandKind.Parry && normalSelection.Hold == "F",
            "a non-Warden top F should remain a normal parry");

        Settings orangeSettings = new() { Unblockables = true };
        CombatObservation orange = Observation(30, CombatDirection.Right) with { OrangeIndicator = true };
        Require(ReactionPolicy.OrangeHasPriority(orange, orangeSettings, false),
            "an orange indicator must win before reaction selection");
        Require(ReactionPolicy.OrangeHasPriority(orange with { OrangeIndicator = false }, orangeSettings, true),
            "an active action must remain a priority gate even without orange");
    }

    private static void VisionAnalyzerUsesExplicitBoundsAndPreservesMarkerLoss()
    {
        var analyzer = new VisionAnalyzer();
        Rectangle combatRoi = new(120, 220, 80, 80);
        Rectangle screenBounds = new(100, 200, 70, 70);

        VisionScanRequest request = new(
            100,
            true,
            new Point(110, 210),
            2,
            combatRoi,
            0,
            240,
            0,
            280,
            150,
            240,
            130,
            240,
            screenBounds,
            false,
            true,
            true,
            true,
            false,
            false,
            true);

        ScreenFrame rightFrame = SyntheticIndicatorFrame(160, 260);
        VisionAnalysisResult right = analyzer.Scan(rightFrame, request);
        Require(right.Observation.CombatRoi == new Rectangle(120, 220, 50, 50),
            "vision ROI should be clipped to the supplied screen bounds");
        Require(right.Observation.HasIndicator && right.Observation.Direction == CombatDirection.Right,
            "a red indicator in the right half-plane should classify as right");
        Require(right.Observation.Indicator == new Point(160, 260) && right.RedProbe.MatchCount == 1,
            "vision should preserve screen coordinates and scoped red telemetry");

        VisionAnalysisResult left = analyzer.Scan(SyntheticIndicatorFrame(125, 260), request);
        Require(left.Observation.Direction == CombatDirection.Left,
            "a red indicator in the left half-plane should classify as left");

        VisionAnalysisResult top = analyzer.Scan(SyntheticIndicatorFrame(145, 245), request);
        Require(top.Observation.Direction == CombatDirection.Top,
            "a red indicator between the vertical thresholds should classify as top");

        VisionAnalysisResult markerLoss = analyzer.Scan(rightFrame, request with { MarkerFound = false });
        Require(!markerLoss.Observation.HasIndicator && markerLoss.Observation.Direction == CombatDirection.None,
            "marker loss must suppress indicator and direction output");
        Require(markerLoss.Observation.CombatRoi == combatRoi,
            "marker loss should retain the configured combat ROI for diagnostics");
    }

    private static ScreenFrame SyntheticIndicatorFrame(int screenX, int screenY)
    {
        var frame = new ScreenFrame
        {
            Width = 100,
            Height = 100,
            Stride = 400,
            OriginX = 100,
            OriginY = 200,
            Buffer = new byte[40000]
        };
        int localX = screenX - frame.OriginX;
        int localY = screenY - frame.OriginY;
        int offset = localY * frame.Stride + localX * 4;
        frame.Buffer[offset] = 41;
        frame.Buffer[offset + 1] = 49;
        frame.Buffer[offset + 2] = 255;
        return frame;
    }

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
        Require(parryHost.ParryCount == 1 && parryInput.Events.Contains("click:" + Input.VK_RBUTTON),
            "a zero-delay parry should commit RT input and increment the parry count");
        parryScheduler.Dispose();

        var crushingInput = new FakeInputGateway();
        crushingInput.HeldKeys.Add(Input.VK_F);
        var crushingSettings = new Settings { Autoblock = true, Crushing = true, ParryDelay = 0 };
        var crushingHost = new FakeAutomationHost(crushingInput, crushingSettings, 102);
        var crushingScheduler = new ActionScheduler(crushingHost.ShutdownToken);
        var crushingExecutor = new ReactionActionExecutor(crushingHost, crushingScheduler, new FixedRollSource(0));
        crushingExecutor.QueueReaction(new ReactionCommand(102, ReactionCommandKind.Crushing, "F", CombatDirection.Right));
        Require(crushingInput.Events.Contains("click:" + Input.VK_LBUTTON),
            "a zero-delay crushing action should commit RB input");
        crushingScheduler.Dispose();
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

    private sealed class FakeInputGateway : IInputGateway
    {
        public List<string> Events { get; } = new();
        public HashSet<int> HeldKeys { get; } = new();
        public bool FailDeflect { get; set; }
        public bool FailLight { get; set; }
        public bool IsReady => true;
        public bool UsesControllerBridge => false;
        public bool CanSendBulwark => true;
        public InputBridgeSnapshot Diagnostics => new(false, false, 0, 0, 0, 0, 0, 0, 0);
        public bool IsDown(int virtualKey) => HeldKeys.Contains(virtualKey);
        public bool HoldButtonHeld() => false;
        public bool PhysicalHeavyAttackHeld() => false;
        public bool PhysicalLightAttackHeld() => false;
        public bool MovingForwardHeld() => false;
        public bool KeyDown(int virtualKey) { Events.Add("down:" + virtualKey); return true; }
        public bool KeyUp(int virtualKey) { Events.Add("up:" + virtualKey); return true; }
        public bool KeyTap(int virtualKey)
        {
            Events.Add("tap:" + virtualKey);
            return !(FailDeflect && virtualKey == Input.VK_SPACE);
        }
        public bool MouseClick(int virtualKey)
        {
            Events.Add("click:" + virtualKey);
            return !(FailLight && virtualKey == Input.VK_LBUTTON);
        }
        public void Block(bool on) => Events.Add("block:" + on);
        public bool BeginBulwarkStance() { Events.Add("bulwark-down"); return true; }
        public void EndBulwarkStance() => Events.Add("bulwark-up");
        public bool DirectionalLight(int guardKey) { Events.Add("light:" + guardKey); return true; }
        public void ReleaseAutomationInputs() => Events.Add("release-all");
    }

    private sealed class FakeAutomationHost : IAutomationHost
    {
        private readonly long _candidateId;

        public FakeAutomationHost(FakeInputGateway input, Settings settings, long candidateId)
        {
            Input = input;
            Settings = settings;
            _candidateId = candidateId;
        }

        public Settings Settings { get; }
        public CancellationToken ShutdownToken => CancellationToken.None;
        public IInputGateway Input { get; }
        public bool IsReactionActive => true;
        public bool MarkerFound => true;
        public bool OrangeParryEnabled => false;
        public OutgoingOrangeGuardResult OutgoingOrangeState { get; } =
            new(false, false, "", false, false, 0, false, false, false);
        public bool IsEHeld() => Input.IsDown(HappyBot.Input.VK_E);
        public bool IsFHeld() => Input.IsDown(HappyBot.Input.VK_F) || Input.HoldButtonHeld();
        public bool IsCurrentCandidate(long candidateId) => candidateId == _candidateId;
        public bool IsYourChar(string name) => ReactionPolicy.IsYourChar(Settings, name);
        public bool HasHeroAction => ReactionPolicy.HasHeroAction(Settings);
        public int ParryCount { get; private set; }
        public int AutomationLightRegistrations { get; private set; }
        public List<string> VisionStates { get; } = new();
        public void SetVisionReaction(string state, string reason, string direction = "", int displayMs = 1100) => VisionStates.Add(state);
        public void RecordTelemetry(string name, object data, bool failure = false) { }
        public void IncrementParryCount() => ParryCount++;
        public void RegisterAutomationLight() => AutomationLightRegistrations++;
        public void RestoreAutoGuardAfterDirectionalLight() { }
    }
}
