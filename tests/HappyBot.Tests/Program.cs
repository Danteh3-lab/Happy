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
            ReleasedHoldDoesNotIssueReaction();
            LegitPercentageUsesBoundaryRolls();
            LegitOffAlwaysParriesWithoutRolling();
            FAndEParriesBothUsePercentage();
            FailedFParryCanResolveToBulwark();
            BulwarkFallbackEligibilityIsStrict();
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

    private static void ReleasedHoldDoesNotIssueReaction()
    {
        var coordinator = new ReactionCoordinator();
        coordinator.Tick(Observation(1, CombatDirection.Top), ReactionCommandKind.None, "");
        CoordinatorTick flash = coordinator.Tick(Observation(50, CombatDirection.Top, flash: true), ReactionCommandKind.None, "");
        Require(flash.Command is null && flash.Candidate is { Consumed: false }, "flash with released hold should remain unconsumed");
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
}
