using System.Diagnostics;
using System.Drawing;
using HappyBot;
using HappyBot.Automation;
using HappyBot.Combat;
using HappyBot.Infrastructure.Input;
using HappyBot.Vision;

static partial class Program
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
            OrangeFeintBlocksNormalReactionPriority();
            OrangeParryEvidenceArmsOnFeint();
            OrangeFeintGraceExpiresBeforePlainOrange();
            OrangeMarkerLossDoesNotClearResponseLatch();
            OutgoingOrangeGuardSuppressesOwnAttackUntilClear();
            OutgoingOrangeGuardAutomationLightSuppressesUntilClear();
            OutgoingOrangeGuardAutomationHeavySuppressesUntilClear();
            FailedLegitDecisionLeavesCandidateAvailableForGuard();
            AutoBlockOffDoesNotArmCandidate();
            FullFrameScreenCoordinatesPreserveRoiDetection();
            CroppedSearchDoesNotRepeatEdgePixels();
            CapturePlannerCoversBootstrapAndTrackedRegions();
            ReactionPolicySelectionCoversEAndFWardenPriority();
            BehaviorSummaryMatchesReactionPriorityAndRequirements();
            NuxiaTopDeflectIsDisabledForYourHero();
            VisionAnalyzerUsesExplicitBoundsAndPreservesMarkerLoss();
            VisionAnalyzerUsesOriginalRoiAndStrictFlashPixel();
            VisionAnalyzerProfilesStrictFlashAtArmedIndicator();
            VisionAnalyzerGraceScanAcceptsFlashWithoutMarker();
            AnchorGraceKeepsExistingCandidateFlashOnly();
            AnchorGraceDoesNotRefreshCandidateValidity();
            AnchorMarkerDetectorRanksNearbyCandidates();
            TemporalFlashCalibrationExcludesArmedIndicator();
            AnchorTrackerConfirmsNewMarkerAfterTwoSamples();
            AnchorTrackerHoldsThroughSingleFrameHole();
            AnchorTrackerJumpArmsGraceOnlyWithLiveCandidate();
            AnchorTrackerBoxFlipDetected();
            AnchorJumpPayloadKeepsLegacyFieldNames();
            CombatGeometryResolutionAndRoi();
            VisionTrackingSnapshotPublishesCoherentGeometry();
            AutoGuardFakeInputAppliesReplacesAndReleases();
            SchedulerImmediateStateIsAuthoritative();
            ZeroDelayReactionActionsCommit();
            CapturedReactionDelaySurvivesTimingEdit();
            ParryConfirmationUsesAttemptDelayAfterUnrelatedReaction();
            ParryConfirmationTrackerConfirmsLightAndHeavyImpacts();
            ParryConfirmationTrackerRespectsTimingAndScaledThresholds();
            DeflectSendsLightOnlyAfterSuccessfulDodge();
            OrochiDeflectSendsHeavyAfterSuccessfulDodge();
            Ds4UsbReportMapsToXboxState();
            Ds4BluetoothReportMapsToXboxState();
            Ds4MalformedReportIsRejected();
            MergerPrefersBotSticksOtherwiseSource();
            MergerOrButtonsMaxTriggersAndOverride();
            MergerReportsCompareByValue();
            ProfileStoreRoundTripsAndProtectsPaths();
            SettingsCodecApplyJsonClampsAndValidates();
            SettingsCodecDodgeSidesStayExclusive();
            SettingsCodecDodgeDirectionMappingCoversBackLeftRight();
            OrangeExplicitSideDodgeBeatsHeldForward();
            OrangeBackDodgeCancelsHeldForward();
            OrangeBackDodgeNeutralWithoutForward();
            OrangeBulwarkStaysHighestPriority();
            SettingsCodecHeroSelectionKeepsFirst();
            SettingsCodecTryParseResolution();
            SettingsCodecEditRoundTrip();
            ControllerToggleBindingsAreExclusiveAndResolvable();
            ProfileEditorDirtyLifecycle();
            ShutdownWithoutStartReleasesInputsInBackground();
            ShutdownDuringBlockedCleanupStillTearsDownAfterUnblock();
            Console.WriteLine("ReactionCoordinator and seam tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void SetPixel(ScreenFrame frame, int x, int y, int red, int green, int blue)
    {
        int offset = y * frame.Stride + x * 4;
        frame.Buffer[offset] = (byte)blue;
        frame.Buffer[offset + 1] = (byte)green;
        frame.Buffer[offset + 2] = (byte)red;
        frame.Buffer[offset + 3] = 255;
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
        public bool FailHeavy { get; set; }
        public bool ForwardHeld { get; set; }
        public bool IsReady => true;
        public bool UsesControllerBridge => false;
        public bool CanSendBulwark => true;
        public InputBridgeSnapshot Diagnostics => new(false, false, 0, 0, 0, 0, 0, 0, 0);
        public bool IsDown(int virtualKey) => HeldKeys.Contains(virtualKey);
        public bool HoldButtonHeld() => false;
        public bool PhysicalHeavyAttackHeld() => false;
        public bool PhysicalLightAttackHeld() => false;
        public bool MovingForwardHeld() => ForwardHeld;
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
            return !(FailLight && virtualKey == Input.VK_LBUTTON) && !(FailHeavy && virtualKey == Input.VK_RBUTTON);
        }
        public void Block(bool on) => Events.Add("block:" + on);
        public bool BeginBulwarkStance() { Events.Add("bulwark-down"); return true; }
        public void EndBulwarkStance() => Events.Add("bulwark-up");
        public bool DirectionalLight(int guardKey) { Events.Add("light:" + guardKey); return true; }
        public void ReleaseAutomationInputs() => Events.Add("release-all");
    }

    /// <summary>
    /// Input gateway whose ReleaseAutomationInputs blocks until ReleaseGate
    /// is set, simulating a stalled controller submission during shutdown.
    /// </summary>
    private sealed class BlockingReleaseInputGateway : IInputGateway
    {
        private readonly object _eventsSync = new();
        private readonly List<string> _events = new();
        public ManualResetEventSlim ReleaseGate { get; } = new(true);
        public ManualResetEventSlim ReleaseEntered { get; } = new(false);
        public bool SawEvent(string name) { lock (_eventsSync) return _events.Contains(name); }
        private void Add(string name) { lock (_eventsSync) _events.Add(name); }
        public bool IsReady => true;
        public bool UsesControllerBridge => false;
        public bool CanSendBulwark => true;
        public InputBridgeSnapshot Diagnostics => new(false, false, 0, 0, 0, 0, 0, 0, 0);
        public bool IsDown(int virtualKey) => false;
        public bool HoldButtonHeld() => false;
        public bool PhysicalHeavyAttackHeld() => false;
        public bool PhysicalLightAttackHeld() => false;
        public bool MovingForwardHeld() => false;
        public bool KeyDown(int virtualKey) { Add("down:" + virtualKey); return true; }
        public bool KeyUp(int virtualKey) { Add("up:" + virtualKey); return true; }
        public bool KeyTap(int virtualKey) { Add("tap:" + virtualKey); return true; }
        public bool MouseClick(int virtualKey) { Add("click:" + virtualKey); return true; }
        public void Block(bool on) => Add("block:" + on);
        public bool BeginBulwarkStance() { Add("bulwark-down"); return true; }
        public void EndBulwarkStance() => Add("bulwark-up");
        public bool DirectionalLight(int guardKey) { Add("light:" + guardKey); return true; }
        public void ReleaseAutomationInputs()
        {
            ReleaseEntered.Set();
            ReleaseGate.Wait();
            Add("release-all");
        }
    }

    private sealed class FakeAutomationHost : IAutomationHost
    {
        private readonly long _candidateId;

        public FakeAutomationHost(FakeInputGateway input, Settings settings, long candidateId,
            bool orangeParryEnabled = false)
        {
            Input = input;
            Settings = settings;
            _candidateId = candidateId;
            OrangeParryEnabled = orangeParryEnabled;
        }

        public Settings Settings { get; }
        public CancellationToken ShutdownToken => CancellationToken.None;
        public IInputGateway Input { get; }
        public bool IsReactionActive => true;
        public bool MarkerFound => true;
        public bool OrangeParryEnabled { get; }
        public OutgoingOrangeGuardResult OutgoingOrangeState { get; } =
            new(false, false, "", false, false, 0, false, false, false);
        public bool IsEHeld() => Input.IsDown(HappyBot.Input.VK_E);
        public bool IsFHeld() => Input.IsDown(HappyBot.Input.VK_F) || Input.HoldButtonHeld();
        public bool IsCurrentCandidate(long candidateId) => candidateId == _candidateId;
        public bool IsYourChar(string name) => ReactionPolicy.IsYourChar(Settings, name);
        public bool HasHeroAction => ReactionPolicy.HasHeroAction(Settings);
        public int ParryCount { get; private set; }
        public List<string> ParryEvidenceRequests { get; } = new();
        public List<string> OrangeParryEvidenceRequests { get; } = new();
        public List<string> TelemetryEvents { get; } = new();
        public int AutomationLightRegistrations { get; private set; }
        public int AutomationHeavyRegistrations { get; private set; }
        public List<string> VisionStates { get; } = new();
        public void SetVisionReaction(string state, string reason, string direction = "", int displayMs = 1100,
            int? appliedDelayMs = null)
        {
            VisionStates.Add(state);
            if (appliedDelayMs.HasValue) VisionDelays[state] = appliedDelayMs.Value;
        }
        public Dictionary<string, int> VisionDelays { get; } = new();
        public void RecordTelemetry(string name, object data, bool failure = false) => TelemetryEvents.Add(name);
        public void IncrementParryCount() => ParryCount++;
        public void RequestParryEvidence(long candidateId, CombatDirection direction, int delayMs)
        {
            ParryEvidenceRequests.Add(candidateId + ":" + direction);
            ParryEvidenceDelays.Add(delayMs);
        }
        public List<int> ParryEvidenceDelays { get; } = new();
        public void CaptureOrangeParryEvidence(CombatObservation observation, int delay,
            int feintTransitionGraceMs, long clearGapAgeMs, bool usedTransitionGrace,
            long feintDetectedAtMs, long clearStartedAtMs) =>
            OrangeParryEvidenceRequests.Add(observation.TimestampMs + ":" + delay + ":" + clearGapAgeMs);
        public void RegisterAutomationLight() => AutomationLightRegistrations++;
        public void RegisterAutomationHeavy() => AutomationHeavyRegistrations++;
        public void RestoreAutoGuardAfterDirectionalLight() { }
    }
}
