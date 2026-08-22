using HappyBot.Combat;
using HappyBot.Automation;
using HappyBot.Infrastructure.Input;
using HappyBot.Vision;

namespace HappyBot;

public sealed class BotCore : IAutomationHost, IDisposable
{
    private readonly object _settingsSync = new();
    private Settings _settings = new();
    public Settings S => Volatile.Read(ref _settings);

    public double B55, Y55;
    public double X2, Y2, X3, Y3, X4, Y4, X5, Y5, X6, Y6, X7, Y7;
    public double X8, Y8, X9, Y9, X16, Y16, X17, Y17, X18, Y18, X19, Y19;

    public volatile bool MarkerFound;
    public volatile bool AttackIndicator;
    public volatile bool EHeld;
    public volatile bool FHeld;
    public volatile bool OrangeParry;
    public volatile int ParryCount;
    public volatile int ParryConfirmedCount;
    public volatile int ParryUnconfirmedCount;
    public volatile string GuardDir = "-";
    public volatile bool Flash;
    public volatile int LoopHz;
    public volatile string LastError = "";
    public volatile int ScreenWidth;
    public volatile int ScreenHeight;
    public int Ax;
    public int Ay;
    public int Box;

    private readonly ManualResetEventSlim _paused = new(true);
    private CancellationTokenSource _cts = new();
    private readonly object _visionSync = new();
    // Serializes pause/stop transitions with the coordinator portion of each frame.
    private readonly object _combatStateSync = new();
    private readonly object _parryEvidenceSync = new();
    private readonly TelemetryRecorder _telemetry = new();
    private readonly ScreenCaptureSession _captureSession = new();
    private readonly ParryConfirmationTracker _parryConfirmation = new();
    private readonly ReactionCoordinator _reactionCoordinator = new();
    private readonly IInputGateway _input;
    private readonly VisionAnalyzer _visionAnalyzer = new();
    private readonly IParryRollSource _parryRolls;
    private readonly IOrangeLightDirectionSource _orangeLightDirections;
    private readonly DirectionalActionExecutor _actions;
    private readonly AutoGuardController _autoGuard;
    private long _reactionWaitTick;
    private long _lastTelemetryHeartbeatTick;
    private long _anchorChangedTick;
    private long _markerLossStartedTick;
    private long _anchorGraceStartedTick;
    private long _rawMarkerMissingSinceTick;
    private long _pendingMarkerSinceTick;
    private int _pendingMarkerX;
    private int _pendingMarkerY;
    private int _pendingMarkerBox;
    private int _pendingMarkerSamples;
    private string _pendingMarkerKind = "NONE";
    private int _anchorDeltaX;
    private int _anchorDeltaY;
    private string _reactionWaitKind = "";
    private bool _waitImageCaptured;
    private int _lastRedMatchCount;
    private string _lastClosestRed = "";
    private int _lastCaptureDurationMs;
    private CapturePlan _capturePlan;
    private CaptureMode _captureMode = CaptureMode.FullFallback;
    private Rectangle _captureRegion;
    private int _captureFallbackCount;
    private long _loopRateWindowStartedTick;
    private int _loopRateWindowFrames;
    private int _lastVisionDurationMs;
    private readonly OutgoingOrangeGuard _outgoingOrangeGuard = new();
    private OutgoingOrangeGuardResult _outgoingOrangeState = new(false, false, "", false, false, 0, false, false, false);
    private bool _lastSourceHeavyHeld;
    private bool _lastSourceLightHeld;
    private long _reactionDisplayUntil;
    private string _markerKind = "NONE";
    private int _indicatorX = -1;
    private int _indicatorY = -1;
    private string _reactionState = "SEARCHING";
    private string _reactionReason = "Waiting for an anchor";
    private string _reactionDirection = "";
    private VisionSnapshot _vision = new();
    private ScreenFrame _frame = new();
    private ParryEvidenceSequence _parryEvidence;
    private CachedCandidateGeometry _cachedCandidateGeometry;
    private FlashCalibrationCandidate _flashCalibration;
    private long _nextParryEvidenceId;
    private Thread _thread;
    private int _stopRequested;
    private int _disposed;

    private static readonly int[] ParryEvidenceOffsetsMs = { 0, 75, 150, 250, 350, 500 };
    private const int MarkerSamplePositionTolerancePx = 12;
    private const int MarkerSampleConfirmationFrames = 2;
    private const int MarkerLossDebounceMs = 75;
    private const int PendingMarkerMaximumMs = 250;

    public BotCore() : this(new StaticInputGateway(), RandomParryRollSource.Instance, RandomOrangeLightDirectionSource.Instance)
    {
    }

    internal BotCore(IParryRollSource parryRolls) : this(new StaticInputGateway(), parryRolls, RandomOrangeLightDirectionSource.Instance)
    {
    }

    internal BotCore(IParryRollSource parryRolls, IOrangeLightDirectionSource orangeLightDirections) :
        this(new StaticInputGateway(), parryRolls, orangeLightDirections)
    {
    }

    internal BotCore(IInputGateway input, IParryRollSource parryRolls, IOrangeLightDirectionSource orangeLightDirections)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _parryRolls = parryRolls ?? throw new ArgumentNullException(nameof(parryRolls));
        _orangeLightDirections = orangeLightDirections ?? throw new ArgumentNullException(nameof(orangeLightDirections));
        _actions = new DirectionalActionExecutor(this, _parryRolls, _orangeLightDirections);
        _autoGuard = new AutoGuardController(
            _input,
            () => S,
            ReactionActive,
            () => _reactionCoordinator.CurrentCandidate,
            () => IsReactionWaiting,
            () => ReactionWaitMilliseconds,
            CombatRoiRectangle,
            RecordTelemetry,
            _telemetry.CaptureRoi,
            direction => GuardDir = direction);
    }

    Settings IAutomationHost.Settings => S;
    CancellationToken IAutomationHost.ShutdownToken => _cts.Token;
    IInputGateway IAutomationHost.Input => _input;
    bool IAutomationHost.IsReactionActive => ReactionActive();
    bool IAutomationHost.MarkerFound => MarkerFound;
    bool IAutomationHost.OrangeParryEnabled => OrangeParry;
    OutgoingOrangeGuardResult IAutomationHost.OutgoingOrangeState => _outgoingOrangeState;
    bool IAutomationHost.IsEHeld() => IsEHeld();
    bool IAutomationHost.IsFHeld() => IsFHeld();
    bool IAutomationHost.IsCurrentCandidate(long candidateId) => _reactionCoordinator.IsCurrent(candidateId);
    bool IAutomationHost.IsYourChar(string name) => YourChar(name);
    bool IAutomationHost.HasHeroAction => HasHeroAction();
    void IAutomationHost.SetVisionReaction(string state, string reason, string direction, int displayMs) =>
        SetVisionReaction(state, reason, direction, displayMs);
    void IAutomationHost.RecordTelemetry(string name, object data, bool failure) => RecordTelemetry(name, data, failure);
    void IAutomationHost.IncrementParryCount() => ParryCount++;
    void IAutomationHost.RequestParryEvidence(long candidateId, CombatDirection direction) =>
        RequestParryEvidence(candidateId, direction);
    void IAutomationHost.RegisterAutomationLight() =>
        _outgoingOrangeGuard.RegisterAutomationLight(Environment.TickCount64);
    void IAutomationHost.RestoreAutoGuardAfterDirectionalLight() => RestoreAutoGuardAfterDirectionalLight();

    public bool IsRunning => _thread is { IsAlive: true };
    public bool IsPaused => !_paused.IsSet;
    public TelemetryStatus Telemetry => _telemetry.Status;

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _stopRequested) != 0) return;
        if (IsRunning) return;
        _paused.Set();
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
        _cts.Cancel();
        _parryConfirmation.Clear();
        lock (_combatStateSync)
        {
            _paused.Set();
            AbortCombatState("shutdown", true);
        }
        _thread?.Join();
        _autoGuard.Release("manual-stop");
        _input.ReleaseAutomationInputs();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        _actions.Dispose();
        _autoGuard.Dispose();
        _captureSession.Dispose();
        _telemetry.Dispose();
        _cts.Dispose();
        _paused.Dispose();
    }

    public void TogglePause()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_combatStateSync)
        {
            if (_paused.IsSet)
            {
                // Close the loop gate before releasing input so an in-flight
                // frame cannot re-arm guard after pause was requested.
                _paused.Reset();
                AbortCombatState("paused", true);
            }
            else _paused.Set();
        }
    }

    public void UpdateSettings(Action<Settings> update)
    {
        lock (_settingsSync)
        {
            Settings next = _settings.Clone();
            update(next);
            Volatile.Write(ref _settings, next);
        }
    }

    public void StartTelemetry(string label)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // Treat telemetry start as a fresh edge-detection boundary so a held
        // source RT is visible immediately in the new session.
        _lastSourceHeavyHeld = false;
        _lastSourceLightHeld = false;
        lock (_parryEvidenceSync) _parryEvidence = null;
        lock (_combatStateSync) _flashCalibration = null;
        _telemetry.Start(label);
        _telemetry.Record("runtime-settings", new { resolution = new { S.Res1, S.Res2 }, S.GuardHold, S.Pause3, S.ParryDelay, S.Left, S.Right, S.TopDeflect, S.Legit, S.LegitParryChance, S.BulwarkFallback, S.CrushingFallbackChance, S.DeflectFallbackChance, S.OrangeLight, outgoingOrangeSuppressionWindowMs = OutgoingOrangeGuard.SuppressionWindowMs });
    }

    public void StopTelemetry()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_parryEvidenceSync) _parryEvidence = null;
        lock (_combatStateSync) _flashCalibration = null;
        _telemetry.Stop();
    }

    public bool ExportTelemetry(IWin32Window owner, out string result) => _telemetry.ExportLatest(owner, out result);

    public VisionSnapshot GetVisionSnapshot()
    {
        lock (_visionSync) return _vision;
    }

    public OverlayFeatureSnapshot GetOverlayFeatures()
    {
        Settings settings = S;
        bool autoBlock = settings.Autoblock;
        bool blackPrior = settings.YourHero && !settings.Nohero && settings.Ch("Blackprior");

        return new OverlayFeatureSnapshot
        {
            AutoBlock = autoBlock,
            AutoParry = autoBlock && settings.Parry,
            AutoCrushing = autoBlock && settings.Crushing,
            AutoDeflect = autoBlock && settings.Deflect,
            AutoDodge = settings.Unblockables && !settings.OrangeLight,
            OrangeLight = settings.Unblockables && settings.OrangeLight,
            OrangeParry = OrangeParry,
            Legit = autoBlock && settings.Legit && settings.Parry,
            LegitChance = Math.Clamp(settings.LegitParryChance, 0, 100),
            BulwarkFallback = autoBlock && settings.Parry && settings.Legit && settings.BulwarkFallback && blackPrior && _input.CanSendBulwark,
            Telemetry = _telemetry.IsRecording
        };
    }

    public void RefreshVisionSnapshot() => PublishVision();

    private bool IsEHeld() => _input.IsDown(Input.VK_E);

    private bool IsFHeld() => _input.IsDown(Input.VK_F) || _input.HoldButtonHeld();

    private void Loop()
    {
        try
        {
            var sw = new System.Diagnostics.Stopwatch();
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    _paused.Wait(_cts.Token);
                    sw.Restart();
                    Flash = false;
                    CapturePrimaryFrame();
                    SetScreenDimensions();
                    long visionStarted = Environment.TickCount64;
                    Calculate();
                    EnsureCombatRegionCaptured();
                    CombatObservation observation = CaptureCombatObservation();
                    _lastVisionDurationMs = (int)Math.Max(0, Environment.TickCount64 - visionStarted);
                    ProcessParryEvidence();
                    UpdateVisionTracking();
                    if (!_input.IsReady)
                    {
                        LastError = "Requested ViGEm input is unavailable; reactions are paused.";
                        AbortCombatState("input-unavailable", true);
                        ProcessParryConfirmation();
                        UpdateLoopRate(sw.Elapsed.TotalMilliseconds);
                        RecordTelemetryHeartbeat();
                        PublishVision();
                        Sleep(100);
                        continue;
                    }
                    if (LastError.StartsWith("Requested ViGEm input", StringComparison.Ordinal)) LastError = "";
                    ProcessCombatObservation(observation);
                    // An RT sent in the current frame creates a new
                    // confirmation request. Include its screen region before
                    // taking the first post-action baseline sample.
                    EnsureConfirmationRegionCaptured();
                    ProcessParryConfirmation();
                    UpdateLoopRate(sw.Elapsed.TotalMilliseconds);
                    RecordTelemetryHeartbeat();
                    PublishVision();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LastError = $"{ex.GetType().Name}: {ex.Message}";
                    AbortCombatState("vision-error", true);
                    Thread.Sleep(250);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool CurrentPx(double x1, double y1, double x2, double y2, int r, int g, int b, int variation, out int px, out int py)
    {
        return _frame.ScreenPixelSearch(x1, y1, x2, y2, r, g, b, variation, out px, out py);
    }

    private CombatObservation CaptureCombatObservation()
    {
        long now = Environment.TickCount64;
        bool eHeld = IsEHeld();
        bool ltHeld = _input.HoldButtonHeld();
        bool fHeld = _input.IsDown(Input.VK_F) || ltHeld;
        ReactionCandidate candidate = _reactionCoordinator.CurrentCandidate;
        CachedCandidateGeometry cached = _cachedCandidateGeometry;
        FlashCalibrationCandidate calibration = _flashCalibration;
        long markerLossAgeMs = MarkerFound || _markerLossStartedTick <= 0
            ? 0
            : Math.Max(0, now - _markerLossStartedTick);
        long anchorGraceAgeMs = _anchorGraceStartedTick <= 0
            ? 0
            : Math.Max(0, now - _anchorGraceStartedTick);
        bool markerGraceScan = !MarkerFound && candidate is { Consumed: false } &&
            cached != null && cached.CandidateId == candidate.Id &&
            markerLossAgeMs <= ReactionCoordinator.MissingGraceMs;
        bool anchorGraceScan = MarkerFound && candidate is { Consumed: false } &&
            cached != null && cached.CandidateId == candidate.Id &&
            _anchorGraceStartedTick > 0 && anchorGraceAgeMs <= ReactionCoordinator.MissingGraceMs;
        bool candidateGraceScan = markerGraceScan || anchorGraceScan;
        long trackingGraceAgeMs = markerGraceScan ? markerLossAgeMs : anchorGraceScan ? anchorGraceAgeMs : 0;
        Point scanAnchor = candidateGraceScan ? cached.Anchor : new Point(Ax, Ay);
        int scanBox = candidateGraceScan ? cached.Box : Box;
        FlashTemporalBaseline baseline = calibration is { CandidateId: var calibrationId } && candidate is { Id: var candidateId } &&
            calibrationId == candidateId ? calibration.TemporalBaseline : null;
        VisionAnalysisResult result = _visionAnalyzer.Scan(_frame, new VisionScanRequest(
            now,
            MarkerFound,
            scanAnchor,
            scanBox,
            CombatRoiRectangle(),
            candidateGraceScan ? cached.TopLeftX : X2,
            candidateGraceScan ? cached.TopLeftY : Y2,
            candidateGraceScan ? cached.TopRightX : X3,
            candidateGraceScan ? cached.TopRightY : Y3,
            candidateGraceScan ? cached.RightX : X4,
            candidateGraceScan ? cached.RightY : Y4,
            candidateGraceScan ? cached.LeftX : X7,
            candidateGraceScan ? cached.LeftY : Y4,
            Screen.PrimaryScreen.Bounds,
            eHeld,
            fHeld,
            ltHeld,
            _input.IsReady,
            _input.PhysicalHeavyAttackHeld(),
            _input.PhysicalLightAttackHeld(),
            _telemetry.IsRecording,
            candidateGraceScan ? cached.CombatRoi : null,
            markerLossAgeMs,
            candidateGraceScan ? cached.Direction : CombatDirection.None,
            candidateGraceScan,
            trackingGraceAgeMs,
            baseline));

        CombatObservation observation = result.Observation;
        AttackIndicator = observation.HasIndicator;
        _indicatorX = observation.Indicator.X;
        _indicatorY = observation.Indicator.Y;
        Flash = observation.LightFlash;
        if (_telemetry.IsRecording)
        {
            _lastRedMatchCount = result.RedProbe.MatchCount;
            _lastClosestRed = result.RedProbe.ClosestRgb;
        }
        return observation;
    }

    private void ProcessCombatObservation(CombatObservation observation)
    {
        lock (_combatStateSync)
        {
            if (!ReactionActive()) return;
            ProcessCombatObservationCore(observation);
        }
    }

    private void ProcessCombatObservationCore(CombatObservation observation)
    {
        OutgoingOrangeGuardResult outgoingOrange = _outgoingOrangeGuard.Observe(
            observation.TimestampMs,
            observation.MarkerFound,
            observation.OrangeIndicator,
            observation.SourceHeavyHeld,
            observation.SourceLightHeld);
        _outgoingOrangeState = outgoingOrange;
        if (outgoingOrange.SourceHeavyHeld != _lastSourceHeavyHeld)
        {
            RecordTelemetry("source-rt-transition", new
            {
                held = outgoingOrange.SourceHeavyHeld,
                sourceRbHeld = outgoingOrange.SourceLightHeld,
                markerFound = observation.MarkerFound,
                orangeIndicator = observation.OrangeIndicator,
                suppressionUntilMs = outgoingOrange.SuppressionUntilMs,
                windowActive = outgoingOrange.WindowActive,
                selfOrangeLatched = outgoingOrange.SelfOrangeLatched
            });
            _lastSourceHeavyHeld = outgoingOrange.SourceHeavyHeld;
        }
        if (outgoingOrange.SourceLightHeld != _lastSourceLightHeld)
        {
            RecordTelemetry("source-rb-transition", new
            {
                held = outgoingOrange.SourceLightHeld,
                sourceRtHeld = outgoingOrange.SourceHeavyHeld,
                markerFound = observation.MarkerFound,
                orangeIndicator = observation.OrangeIndicator,
                suppressionUntilMs = outgoingOrange.SuppressionUntilMs,
                windowActive = outgoingOrange.WindowActive,
                selfOrangeLatched = outgoingOrange.SelfOrangeLatched
            });
            _lastSourceLightHeld = outgoingOrange.SourceLightHeld;
        }
        if (outgoingOrange.SelfOrangeStarted)
        {
            RecordTelemetry("orange-self-attack-suppressed", new
            {
                source = outgoingOrange.AttributionSource,
                attributedSource = outgoingOrange.AttributionSource,
                sourceRtHeld = outgoingOrange.SourceHeavyHeld,
                sourceRbHeld = outgoingOrange.SourceLightHeld,
                suppressionUntilMs = outgoingOrange.SuppressionUntilMs,
                selfOrangeLatched = outgoingOrange.SelfOrangeLatched,
                indicator = observation.Indicator,
                roi = observation.CombatRoi
            });
            _telemetry.CaptureRoi("orange-self-attack-suppressed", observation.CombatRoi);
            SetVisionReaction("ORANGE IGNORED", $"Own source {outgoingOrange.AttributionSource} attack", "", 1300);
        }
        if (outgoingOrange.SelfOrangeCleared)
        {
            RecordTelemetry("orange-self-attack-cleared", new
            {
                source = outgoingOrange.AttributionSource,
                attributedSource = outgoingOrange.AttributionSource,
                suppressionUntilMs = outgoingOrange.SuppressionUntilMs
            });
        }

        _actions.ProcessOrangeObservation(observation, outgoingOrange.SuppressesOrange);
        if (!observation.EHeld && !observation.FHeld)
            _actions.CancelPendingAction("hold-released");
        CombatObservation effectiveObservation = outgoingOrange.SuppressesOrange
            ? observation with { OrangeIndicator = false, OrangeFeint = false }
            : observation;
        bool orangePriority = ReactionPolicy.OrangeHasPriority(effectiveObservation, S, _actions.IsBusy);
        (ReactionCommandKind kind, string hold) = orangePriority ? (ReactionCommandKind.None, "") : ResolveReactionCommand(effectiveObservation);
        CoordinatorTick tick = _reactionCoordinator.Tick(effectiveObservation with
        {
            HasIndicator = S.Autoblock && observation.HasIndicator
        }, kind, hold);

        if (_flashCalibration != null && tick.Candidate is { Id: var candidateId } &&
            candidateId == _flashCalibration.CandidateId)
            TrackFlashCalibration(observation);

        if (tick.Transition.Contains("replaced", StringComparison.Ordinal))
            CompleteFlashCalibration("replaced", observation, "candidate-replaced", false);

        if (!string.IsNullOrEmpty(tick.Transition))
        {
            RecordTelemetry("candidate-" + tick.Transition, new
            {
                id = tick.Candidate?.Id,
                direction = tick.Candidate?.Direction.ToString(),
                indicator = observation.Indicator,
                box = observation.Box,
                scanMode = ScanModeName(observation.ScanMode),
                cachedRoi = RectangleTelemetry(observation.CachedCombatRoi),
                markerLossAgeMs = observation.MarkerLossAgeMs,
                trackingGraceAgeMs = observation.TrackingGraceAgeMs,
                flashClusterMatches = observation.FlashClusterMatches,
                strictFlashPoint = observation.StrictFlashPoint,
                indicatorFlashClusterMatches = observation.IndicatorFlashClusterMatches,
                indicatorFlashClusterBounds = RectangleTelemetry(observation.IndicatorFlashClusterBounds),
                temporalFlashMatches = observation.TemporalFlashMatches,
                temporalFlashLargestCluster = observation.TemporalFlashLargestCluster
            });
            if (tick.Candidate != null && (tick.Transition.StartsWith("armed", StringComparison.Ordinal) || tick.Transition.StartsWith("replaced", StringComparison.Ordinal)))
            {
                CacheCandidateGeometry(tick.Candidate, observation);
                StartFlashCalibration(tick.Candidate, observation);
                RecordTelemetry("indicator-classified", new
                {
                    classification = tick.Candidate.Direction.ToString().ToUpperInvariant(),
                    x = observation.Indicator.X,
                    y = observation.Indicator.Y,
                    matches = _lastRedMatchCount,
                    closestRgb = _lastClosestRed,
                    box = observation.Box,
                    scanMode = ScanModeName(observation.ScanMode),
                    cachedRoi = RectangleTelemetry(observation.CachedCombatRoi),
                    markerLossAgeMs = observation.MarkerLossAgeMs,
                    trackingGraceAgeMs = observation.TrackingGraceAgeMs,
                    flashClusterMatches = observation.FlashClusterMatches,
                    strictFlashPoint = observation.StrictFlashPoint,
                    indicatorFlashClusterMatches = observation.IndicatorFlashClusterMatches,
                    indicatorFlashClusterBounds = RectangleTelemetry(observation.IndicatorFlashClusterBounds),
                    temporalFlashMatches = observation.TemporalFlashMatches,
                    temporalFlashLargestCluster = observation.TemporalFlashLargestCluster
                });
                _telemetry.CaptureRoi("indicator-" + tick.Candidate.Direction, observation.CombatRoi);
                SetVisionReaction("GUARD", "Current classified red indicator", DirectionName(tick.Candidate.Direction), 900);
            }
            if (tick.Transition.Contains("replaced", StringComparison.Ordinal))
                _actions.CancelPendingAction("candidate-replaced");
        }
        if (!string.IsNullOrEmpty(tick.CancellationReason))
        {
            CompleteFlashCalibration("cancelled", observation, tick.CancellationReason);
            RecordTelemetry("candidate-cancelled", new
            {
                reason = tick.CancellationReason,
                scanMode = ScanModeName(observation.ScanMode),
                cachedRoi = RectangleTelemetry(observation.CachedCombatRoi),
                markerLossAgeMs = observation.MarkerLossAgeMs,
                trackingGraceAgeMs = observation.TrackingGraceAgeMs,
                flashClusterMatches = observation.FlashClusterMatches,
                strictFlashPoint = observation.StrictFlashPoint,
                indicatorFlashClusterMatches = observation.IndicatorFlashClusterMatches,
                indicatorFlashClusterBounds = RectangleTelemetry(observation.IndicatorFlashClusterBounds),
                temporalFlashMatches = observation.TemporalFlashMatches,
                temporalFlashLargestCluster = observation.TemporalFlashLargestCluster
            }, true);
            _telemetry.CaptureRoi("candidate-" + tick.CancellationReason, observation.CombatRoi);
            SetVisionReaction("REACTION CANCELLED", tick.CancellationReason, "", 800);
            _actions.CancelPendingAction(tick.CancellationReason);
            if (tick.Candidate == null) _cachedCandidateGeometry = null;
        }
        if (tick.IgnoredStaleFlash)
        {
            RecordTelemetry("flash-ignored-stale", new
            {
                observation.CombatRoi,
                observation.Box,
                scanMode = ScanModeName(observation.ScanMode),
                cachedRoi = RectangleTelemetry(observation.CachedCombatRoi),
                markerLossAgeMs = observation.MarkerLossAgeMs,
                trackingGraceAgeMs = observation.TrackingGraceAgeMs,
                flashClusterMatches = observation.FlashClusterMatches,
                temporalFlashMatches = observation.TemporalFlashMatches,
                temporalFlashLargestCluster = observation.TemporalFlashLargestCluster
            });
            _telemetry.CaptureRoi("flash-ignored-stale", observation.CombatRoi);
        }

        ApplyCoordinatorGuard(tick.Candidate);
        if (observation.HasIndicator && observation.Direction == CombatDirection.None)
        {
            RecordTelemetry("indicator-unknown", new { x = observation.Indicator.X, y = observation.Indicator.Y, box = Box }, true);
            SetVisionReaction("INDICATOR UNKNOWN", "Red indicator was outside the directional zones", "", 800);
        }
        if (tick.Command != null)
        {
            bool wardenTopParry = tick.Command.Kind == ReactionCommandKind.Crushing && tick.Command.Hold == "F" &&
                S.Parry && tick.Command.Direction == CombatDirection.Top && YourChar("Warden");
            ReactionCommand command = tick.Command with
            {
                RequiresParryEnabled = tick.Command.Kind == ReactionCommandKind.Parry || wardenTopParry
            };
            RecordTelemetry("flash-accepted", new
            {
                candidateId = command.CandidateId,
                direction = command.Direction.ToString(),
                kind = command.Kind.ToString(),
                scanMode = ScanModeName(observation.ScanMode),
                cachedRoi = RectangleTelemetry(observation.CachedCombatRoi),
                markerLossAgeMs = observation.MarkerLossAgeMs,
                flashClusterMatches = observation.FlashClusterMatches,
                strictFlashPoint = observation.StrictFlashPoint,
                indicatorFlashClusterMatches = observation.IndicatorFlashClusterMatches,
                indicatorFlashClusterBounds = RectangleTelemetry(observation.IndicatorFlashClusterBounds),
                temporalFlashMatches = observation.TemporalFlashMatches,
                temporalFlashLargestCluster = observation.TemporalFlashLargestCluster,
                graceAccepted = observation.ScanMode != VisionScanMode.Tracked
            });
            if (observation.ScanMode != VisionScanMode.Tracked)
            {
                RecordTelemetry("flash-accepted-during-grace", new
                {
                    candidateId = command.CandidateId,
                    direction = command.Direction.ToString(),
                    kind = command.Kind.ToString(),
                    scanMode = ScanModeName(observation.ScanMode),
                    cachedRoi = RectangleTelemetry(observation.CachedCombatRoi),
                    markerLossAgeMs = observation.MarkerLossAgeMs,
                    trackingGraceAgeMs = observation.TrackingGraceAgeMs,
                    flashClusterMatches = observation.FlashClusterMatches,
                    strictFlashPoint = observation.StrictFlashPoint,
                    indicatorFlashClusterMatches = observation.IndicatorFlashClusterMatches,
                    indicatorFlashClusterBounds = RectangleTelemetry(observation.IndicatorFlashClusterBounds),
                    temporalFlashMatches = observation.TemporalFlashMatches,
                    temporalFlashLargestCluster = observation.TemporalFlashLargestCluster
                });
            }
            CompleteFlashCalibration("accepted", observation, "flash-accepted");
            _telemetry.CaptureRoi("flash-accepted", observation.CombatRoi);
            _actions.QueueReaction(command);
        }
    }

    private (ReactionCommandKind Kind, string Hold) ResolveReactionCommand(CombatObservation observation)
    {
        ReactionSelection selection = ReactionPolicy.ResolveCommand(observation, S);
        return (selection.Kind, selection.Hold);
    }

    private void CacheCandidateGeometry(ReactionCandidate candidate, CombatObservation observation)
    {
        if (candidate == null || observation.ScanMode != VisionScanMode.Tracked || !observation.MarkerFound)
            return;

        _cachedCandidateGeometry = new CachedCandidateGeometry(
            candidate.Id,
            observation.CombatRoi,
            X2,
            Y2,
            X3,
            Y3,
            X4,
            Y4,
            X7,
            Y4,
            candidate.Direction,
            observation.Box,
            observation.Anchor,
            observation.TimestampMs);
    }

    private void StartFlashCalibration(ReactionCandidate candidate, CombatObservation observation)
    {
        if (!_telemetry.IsRecording || candidate == null) return;

        CompleteFlashCalibration("replaced", observation, "candidate-replaced", false);
        long elapsedMs = _telemetry.ElapsedMs;
        string armFrame = _telemetry.CaptureCalibrationRegionSnapshot(candidate.Id, "armed",
            observation.FlashClusterMatches, _frame, observation.CombatRoi);
        FlashTemporalBaseline baseline = VisionAnalyzer.CaptureTemporalBaseline(_frame, observation.CombatRoi,
            observation.Indicator);
        _flashCalibration = new FlashCalibrationCandidate(candidate.Id, candidate.Direction, observation.CombatRoi,
            elapsedMs, observation.FlashClusterMatches, observation.IndicatorFlashClusterMatches,
            observation.TemporalFlashMatches, observation.TemporalFlashLargestCluster, armFrame, armFrame, "", "", baseline);
        RecordTelemetry("flash-calibration-armed", new
        {
            candidateId = candidate.Id,
            direction = candidate.Direction.ToString(),
            armedElapsedMs = elapsedMs,
            scanMode = ScanModeName(observation.ScanMode),
            markerLossAgeMs = observation.MarkerLossAgeMs,
            trackingGraceAgeMs = observation.TrackingGraceAgeMs,
            flashClusterMatches = observation.FlashClusterMatches,
            strictFlashPoint = observation.StrictFlashPoint,
            indicatorFlashClusterMatches = observation.IndicatorFlashClusterMatches,
            indicatorFlashClusterBounds = RectangleTelemetry(observation.IndicatorFlashClusterBounds),
            temporalFlashMatches = observation.TemporalFlashMatches,
            temporalFlashLargestCluster = observation.TemporalFlashLargestCluster,
            armFrame
        });
    }

    private void TrackFlashCalibration(CombatObservation observation)
    {
        FlashCalibrationCandidate calibration = _flashCalibration;
        if (!_telemetry.IsRecording || calibration == null)
            return;

        bool clusterPeak = observation.FlashClusterMatches > calibration.PeakMatches;
        bool indicatorClusterPeak = observation.IndicatorFlashClusterMatches > calibration.IndicatorPeakMatches;
        bool temporalPeak = observation.TemporalFlashMatches > calibration.TemporalPeakMatches ||
            (observation.TemporalFlashMatches == calibration.TemporalPeakMatches &&
             observation.TemporalFlashLargestCluster > calibration.TemporalPeakLargestCluster);
        if (!clusterPeak && !indicatorClusterPeak && !temporalPeak) return;

        string peakFrame = clusterPeak
            ? _telemetry.CaptureCalibrationRegionSnapshot(calibration.CandidateId, "peak",
                observation.FlashClusterMatches, _frame, observation.CombatRoi)
            : calibration.PeakFrame;
        string temporalPeakFrame = temporalPeak
            ? _telemetry.CaptureCalibrationRegionSnapshot(calibration.CandidateId, "temporal-peak",
                observation.TemporalFlashMatches, _frame, calibration.Region)
            : calibration.TemporalPeakFrame;
        string indicatorPeakFrame = indicatorClusterPeak
            ? _telemetry.CaptureCalibrationRegionSnapshot(calibration.CandidateId, "indicator-peak",
                observation.IndicatorFlashClusterMatches, _frame, calibration.Region)
            : calibration.IndicatorPeakFrame;
        _flashCalibration = calibration with
        {
            Region = observation.CombatRoi,
            PeakMatches = clusterPeak ? observation.FlashClusterMatches : calibration.PeakMatches,
            PeakFrame = peakFrame,
            IndicatorPeakMatches = indicatorClusterPeak
                ? observation.IndicatorFlashClusterMatches
                : calibration.IndicatorPeakMatches,
            IndicatorPeakFrame = indicatorPeakFrame,
            TemporalPeakMatches = temporalPeak ? observation.TemporalFlashMatches : calibration.TemporalPeakMatches,
            TemporalPeakLargestCluster = temporalPeak
                ? observation.TemporalFlashLargestCluster
                : calibration.TemporalPeakLargestCluster,
            TemporalPeakFrame = temporalPeakFrame
        };
    }

    private void CompleteFlashCalibration(string outcome, CombatObservation observation, string reason,
        bool useCurrentObservation = true)
    {
        FlashCalibrationCandidate calibration = _flashCalibration;
        if (calibration == null) return;

        if (useCurrentObservation) TrackFlashCalibration(observation);
        calibration = _flashCalibration;
        string finalFrame = _telemetry.IsRecording && useCurrentObservation
            ? _telemetry.CaptureCalibrationRegionSnapshot(calibration.CandidateId, "final-" + outcome,
                observation.FlashClusterMatches, _frame, calibration.Region)
            : "";
        RecordTelemetry("flash-calibration-result", new
        {
            candidateId = calibration.CandidateId,
            direction = calibration.Direction.ToString(),
            outcome,
            reason,
            armedElapsedMs = calibration.ArmedElapsedMs,
            peakClusterMatches = calibration.PeakMatches,
            finalClusterMatches = useCurrentObservation ? observation.FlashClusterMatches : -1,
            indicatorPeakClusterMatches = calibration.IndicatorPeakMatches,
            finalIndicatorFlashClusterMatches = useCurrentObservation ? observation.IndicatorFlashClusterMatches : -1,
            strictFlashPoint = useCurrentObservation ? observation.StrictFlashPoint : new Point(-1, -1),
            indicatorFlashClusterBounds = useCurrentObservation
                ? RectangleTelemetry(observation.IndicatorFlashClusterBounds)
                : RectangleTelemetry(Rectangle.Empty),
            temporalPeakMatches = calibration.TemporalPeakMatches,
            temporalPeakLargestCluster = calibration.TemporalPeakLargestCluster,
            finalTemporalMatches = useCurrentObservation ? observation.TemporalFlashMatches : -1,
            finalTemporalLargestCluster = useCurrentObservation ? observation.TemporalFlashLargestCluster : -1,
            scanMode = ScanModeName(observation.ScanMode),
            markerLossAgeMs = observation.MarkerLossAgeMs,
            trackingGraceAgeMs = observation.TrackingGraceAgeMs,
            armFrame = calibration.ArmFrame,
            peakFrame = calibration.PeakFrame,
            indicatorPeakFrame = calibration.IndicatorPeakFrame,
            temporalPeakFrame = calibration.TemporalPeakFrame,
            finalFrame
        }, outcome == "cancelled");
        _flashCalibration = null;
    }

    private static string ScanModeName(VisionScanMode mode) => mode switch
    {
        VisionScanMode.MarkerGrace => "marker-grace",
        VisionScanMode.AnchorGrace => "anchor-grace",
        _ => "tracked"
    };

    private static object RectangleTelemetry(Rectangle rectangle) => new
    {
        x = rectangle.X,
        y = rectangle.Y,
        width = rectangle.Width,
        height = rectangle.Height,
        right = rectangle.Right,
        bottom = rectangle.Bottom
    };

    private void AbortCombatState(string reason, bool forceAction)
    {
        lock (_combatStateSync)
        {
            _reactionCoordinator.Cancel(reason);
            _cachedCandidateGeometry = null;
            _flashCalibration = null;
            _actions.CancelPendingAction(reason, forceAction);
            ReleaseAutoGuard();
            _input.ReleaseAutomationInputs();
        }
    }

    public string DebugScan()
    {
        var f = ScreenCapture.Capture(null);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Screen captured: {f.Width}x{f.Height}");
        sb.AppendLine($"Search region: ({X8:0},{Y8:0})-({X9:0},{Y9:0})");
        sb.AppendLine($"Scalers: B55={B55:0.###} Y55={Y55:0.###}");
        sb.AppendLine();

        int black = 0;
        for (int y = 0; y < f.Height; y += 97)
        {
            for (int x = 0; x < f.Width; x += 97)
            {
                if (f.SamplePixel(x, y, out int r, out int g, out int b) && r + g + b < 15)
                    black++;
            }
        }
        sb.AppendLine($"Frame is {(black > 300 ? "BLACK (capture likely blocked)" : "OK (not black)")}");
        sb.AppendLine();

        int sx = Math.Max(0, Math.Min((int)X8, f.Width - 1));
        int ex = Math.Max(0, Math.Min((int)X9, f.Width - 1));
        int sy = Math.Max(0, Math.Min((int)Y8, f.Height - 1));
        int ey = Math.Max(0, Math.Min((int)Y9, f.Height - 1));

        int fx, fy;
        int inRegionGreen = 0, inRegionYellow = 0;
        for (int y = sy; y <= ey && inRegionGreen < 50; y++)
        {
            for (int x = sx; x <= ex && inRegionGreen < 50; x++)
            {
                if (f.SamplePixel(x, y, out int r, out int g, out int b))
                {
                    if (Math.Abs(r - 5) <= 10 && Math.Abs(g - 131) <= 10 && Math.Abs(b - 65) <= 10)
                    {
                        if (inRegionGreen == 0) { fx = x; fy = y; sb.AppendLine($"GREEN inside region first at ({fx},{fy})"); }
                        inRegionGreen++;
                    }
                    if (Math.Abs(r - 255) <= 10 && Math.Abs(g - 255) <= 10 && Math.Abs(b - 10) <= 10)
                        inRegionYellow++;
                }
            }
        }
        sb.AppendLine($"Green inside region: {inRegionGreen} px | Yellow inside region: {inRegionYellow} px");
        sb.AppendLine();

        int greens = f.CountColor(5, 131, 65, 10, out fx, out fy);
        sb.AppendLine($"Green anywhere (var10): {greens}" + (greens > 0 ? $" first at ({fx},{fy})" : ""));
        int yellows = f.CountColor(255, 255, 10, 10, out fx, out fy);
        sb.AppendLine($"Yellow anywhere (var10): {yellows}" + (yellows > 0 ? $" first at ({fx},{fy})" : ""));
        sb.AppendLine();

        int cx = (int)((X8 + X9) / 2);
        sb.AppendLine("Vertical strip at region center X=" + cx + ":");
        for (int y = sy; y <= ey; y += Math.Max(1, (ey - sy) / 10))
            sb.AppendLine($"  y={y}: {Sample(f, cx, y)}");
        sb.AppendLine($"Screen center: {Sample(f, f.Width / 2, f.Height / 2)}");
        return sb.ToString();
    }

    private static string Sample(ScreenFrame f, int x, int y)
    {
        return f.SamplePixel(x, y, out int r, out int g, out int b) ? $"RGB({r},{g},{b})" : "off-screen";
    }

    private void Sleep(int ms)
    {
        if (ms <= 0) return;
        if (_cts.Token.WaitHandle.WaitOne(ms)) _cts.Token.ThrowIfCancellationRequested();
    }

    private bool ReactionActive() => !_cts.IsCancellationRequested && _paused.IsSet && _input.IsReady;

    private void ReleaseAutoGuard() => _autoGuard.Release("manual-stop");

    private void RestoreAutoGuardAfterDirectionalLight() => _autoGuard.RestoreAfterDirectionalLight();

    private void ApplyCoordinatorGuard(ReactionCandidate candidate) => _autoGuard.Apply(candidate);

    private void SetCoords(double nx2, double ny2, double nx3, double ny3, double nx4, double ny4,
                          double nx5, double ny5, double nx6, double ny6, double nx7, double ny7,
                          double nx16, double ny16, double nx17, double ny17)
    {
        X2 = nx2; Y2 = ny2;
        X3 = nx3; Y3 = ny3;
        X4 = nx4; Y4 = ny4;
        X5 = nx5; Y5 = ny5;
        X6 = nx6; Y6 = ny6;
        X7 = nx7; Y7 = ny7;
        X16 = nx16; Y16 = ny16;
        X17 = nx17; Y17 = ny17;
    }

    private bool IsReactionWaiting => Volatile.Read(ref _reactionWaitTick) != 0;

    private long ReactionWaitMilliseconds
    {
        get
        {
            long started = Volatile.Read(ref _reactionWaitTick);
            return started == 0 ? 0 : Math.Max(0, Environment.TickCount64 - started);
        }
    }

    private Rectangle CombatRoiRectangle()
    {
        int horizontalPadding = Math.Max(0, (int)Math.Round(96 * B55));
        int left = (int)Math.Floor(Math.Min(X16, X17)) - horizontalPadding;
        int top = (int)Math.Floor(Math.Min(Y16, Y17));
        int right = (int)Math.Ceiling(Math.Max(X16, X17)) + horizontalPadding;
        int bottom = (int)Math.Ceiling(Math.Max(Y16, Y17));
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private void BeginReactionWait(string kind)
    {
        _reactionWaitKind = kind;
        _waitImageCaptured = false;
        Interlocked.Exchange(ref _reactionWaitTick, Environment.TickCount64);
        RecordTelemetry("wait-flash-start", new { kind, guard = GuardDir, guardRemainingMs = GuardRemainingMilliseconds });
    }

    private void EndReactionWait(string reason)
    {
        long started = Interlocked.Exchange(ref _reactionWaitTick, 0);
        if (started == 0) return;
        RecordTelemetry("wait-flash-end", new { kind = _reactionWaitKind, reason, waitMs = Environment.TickCount64 - started, guardRemainingMs = GuardRemainingMilliseconds });
        _reactionWaitKind = "";
    }

    private void RecordReactionWaitProgress()
    {
        RecordTelemetryHeartbeat();
        long waitMs = ReactionWaitMilliseconds;
        if (waitMs >= 500 && !_waitImageCaptured)
        {
            _waitImageCaptured = true;
            RecordTelemetry("wait-flash-500ms", new { kind = _reactionWaitKind, waitMs, guardRemainingMs = GuardRemainingMilliseconds }, true);
            _telemetry.CaptureRoi("wait-flash-500ms", CombatRoiRectangle());
        }
    }

    private long GuardRemainingMilliseconds
    {
        get => _autoGuard.RemainingMilliseconds;
    }

    private void RecordTelemetry(string name, object data, bool failure = false) => _telemetry.Record(name, data, failure);

    private void RecordTelemetryHeartbeat()
    {
        if (!_telemetry.IsRecording) return;
        long now = Environment.TickCount64;
        if (now - _lastTelemetryHeartbeatTick < 100) return;
        _lastTelemetryHeartbeatTick = now;
        InputBridgeSnapshot bridge = ViGEmInput.GetDiagnostics();
        ReactionCandidate candidate = _reactionCoordinator.CurrentCandidate;
        _telemetry.Record("heartbeat", new
        {
            marker = new { found = MarkerFound, kind = _markerKind, x = Ax, y = Ay, deltaX = _anchorDeltaX, deltaY = _anchorDeltaY, ageMs = Math.Max(0, now - _anchorChangedTick) },
            box = Box,
            roi = CombatRoiRectangle(),
            zones = new { top = new { X2, Y2, X3, Y3 }, left = new { X6, Y6, X7, Y7 }, right = new { X4, Y4, X5, Y5 } },
            indicator = new { present = AttackIndicator, x = _indicatorX, y = _indicatorY, matches = _lastRedMatchCount, closestRgb = _lastClosestRed },
            reaction = new
            {
                state = _reactionState,
                worker = _actions.State,
                candidateId = candidate?.Id ?? 0,
                candidateAgeMs = candidate == null ? 0 : now - candidate.StartedMs,
                lastValidAgeMs = candidate == null ? 0 : now - candidate.LastValidMs,
                candidateDirection = candidate?.Direction.ToString() ?? "NONE",
                EHeld, FHeld, ltHeld = _input.HoldButtonHeld(), Flash
            },
            outgoingOrange = new
            {
                sourceRtHeld = _outgoingOrangeState.SourceHeavyHeld,
                sourceRbHeld = _outgoingOrangeState.SourceLightHeld,
                windowActive = _outgoingOrangeState.WindowActive,
                selfOrangeLatched = _outgoingOrangeState.SelfOrangeLatched,
                suppressUntilMs = _outgoingOrangeState.SuppressionUntilMs,
                suppressionWindowMs = OutgoingOrangeGuard.SuppressionWindowMs,
                suppressed = _outgoingOrangeState.SuppressesOrange
            },
            guard = new { direction = GuardDir, remainingMs = GuardRemainingMilliseconds, keyDownTick = _autoGuard.PressedTick, releaseDeadlineTick = _autoGuard.ReleaseTick },
            bridge,
            output = new { Input.LastSendResult, Input.LastSendError, Input.InjectedCount },
            loopHz = LoopHz,
            captureMs = _lastCaptureDurationMs,
            performance = new
            {
                captureMs = _lastCaptureDurationMs,
                visionMs = _lastVisionDurationMs,
                captureMode = _captureMode.ToString(),
                captureRegion = _captureRegion,
                captureFallbacks = _captureFallbackCount
            }
        });
    }

    private void SetScreenDimensions()
    {
        Rectangle bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        ScreenWidth = bounds.Width;
        ScreenHeight = bounds.Height;
    }

    private void UpdateLoopRate(double elapsedMilliseconds)
    {
        int instantaneous = (int)(1000.0 / Math.Max(1.0, elapsedMilliseconds));
        long now = Environment.TickCount64;
        if (_loopRateWindowStartedTick == 0)
        {
            _loopRateWindowStartedTick = now;
            _loopRateWindowFrames = 1;
            LoopHz = instantaneous;
            return;
        }

        _loopRateWindowFrames++;
        long windowMs = now - _loopRateWindowStartedTick;
        if (windowMs >= 1000)
        {
            LoopHz = (int)Math.Round(_loopRateWindowFrames * 1000.0 / Math.Max(1, windowMs));
            _loopRateWindowStartedTick = now;
            _loopRateWindowFrames = 0;
        }
        else if (LoopHz <= 0)
        {
            LoopHz = instantaneous;
        }
    }

    private void CapturePrimaryFrame()
    {
        CapturePlan plan = BuildCapturePlan();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _frame = _captureSession.Capture(_frame, plan.Region);
        }
        catch when (!plan.IsFullScreen)
        {
            plan = CapturePlan.Full(System.Windows.Forms.Screen.PrimaryScreen.Bounds);
            _captureFallbackCount++;
            _frame = _captureSession.Capture(_frame, plan.Region);
        }
        _capturePlan = plan;
        _captureMode = plan.Mode;
        _captureRegion = plan.Region;
        _lastCaptureDurationMs = (int)stopwatch.ElapsedMilliseconds;
    }

    /// <summary>
    /// The marker can move to a different valid pixel after the initial crop
    /// was selected. If the newly accepted marker-relative ROI is not inside
    /// that crop, take one supplemental crop in the same loop. This keeps the
    /// optimization lossless while retaining a full-screen fallback for any
    /// unexpected geometry/capture failure.
    /// </summary>
    private void EnsureCombatRegionCaptured()
    {
        Rectangle screenBounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        Rectangle required = MarkerFound
            ? CombatRoiRectangle()
            : _cachedCandidateGeometry?.CombatRoi ?? Rectangle.Empty;
        required = Rectangle.Intersect(required, screenBounds);
        Rectangle frameBounds = new(_frame.OriginX, _frame.OriginY, _frame.Width, _frame.Height);
        if (required.Width <= 0 || required.Height <= 0 || frameBounds.Contains(required)) return;

        CapturePlan plan = BuildCapturePlan();
        if (!plan.Region.Contains(required)) plan = CapturePlan.Full(screenBounds);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _frame = _captureSession.Capture(_frame, plan.Region);
        }
        catch when (!plan.IsFullScreen)
        {
            plan = CapturePlan.Full(screenBounds);
            _captureFallbackCount++;
            _frame = _captureSession.Capture(_frame, plan.Region);
        }
        _capturePlan = plan;
        _captureMode = plan.Mode;
        _captureRegion = plan.Region;
        _lastCaptureDurationMs += (int)stopwatch.ElapsedMilliseconds;
    }

    private void EnsureConfirmationRegionCaptured()
    {
        if (!_parryConfirmation.HasPending) return;
        Rectangle screenBounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        Rectangle normalized = ParryConfirmationTracker.NormalizedRegion(screenBounds.Width, screenBounds.Height);
        Rectangle required = new(screenBounds.Left + normalized.Left, screenBounds.Top + normalized.Top,
            normalized.Width, normalized.Height);
        Rectangle frameBounds = new(_frame.OriginX, _frame.OriginY, _frame.Width, _frame.Height);
        if (frameBounds.Contains(required)) return;

        CapturePlan plan = BuildCapturePlan();
        if (!plan.Region.Contains(required)) plan = CapturePlan.Full(screenBounds);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _frame = _captureSession.Capture(_frame, plan.Region);
        }
        catch when (!plan.IsFullScreen)
        {
            plan = CapturePlan.Full(screenBounds);
            _captureFallbackCount++;
            _frame = _captureSession.Capture(_frame, plan.Region);
        }
        _capturePlan = plan;
        _captureMode = plan.Mode;
        _captureRegion = plan.Region;
        _lastCaptureDurationMs += (int)stopwatch.ElapsedMilliseconds;
    }

    private CapturePlan BuildCapturePlan()
    {
        Rectangle screenBounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        Rectangle markerScan = Rectangle.FromLTRB((int)Math.Floor(X8), (int)Math.Floor(Y8),
            (int)Math.Ceiling(X9), (int)Math.Ceiling(Y9));
        Rectangle boxScan = Rectangle.FromLTRB((int)Math.Floor(X18), (int)Math.Floor(Y18),
            (int)Math.Ceiling(X19), (int)Math.Ceiling(Y19));
        Rectangle possibleCombat = CaptureRegionPlanner.PossibleCombatBounds(markerScan, B55, Y55);
        Rectangle activeCombat = MarkerFound ? CombatRoiRectangle() : Rectangle.Empty;
        Rectangle cachedCombat = _cachedCandidateGeometry?.CombatRoi ?? Rectangle.Empty;
        Rectangle confirmation = Rectangle.Empty;
        if (_parryConfirmation.HasPending)
        {
            Rectangle local = ParryConfirmationTracker.NormalizedRegion(screenBounds.Width, screenBounds.Height);
            confirmation = new Rectangle(screenBounds.Left + local.Left, screenBounds.Top + local.Top,
                local.Width, local.Height);
        }

        bool tracked = MarkerFound && markerScan.Width > 0 && markerScan.Height > 0;
        return CaptureRegionPlanner.Build(screenBounds, markerScan, boxScan, possibleCombat,
            activeCombat, cachedCombat, confirmation, tracked);
    }

    private void RequestParryEvidence(long candidateId, CombatDirection direction)
    {
        string attemptId = $"parry-{Interlocked.Increment(ref _nextParryEvidenceId):D6}";
        _parryConfirmation.Start(attemptId, candidateId, direction, Environment.TickCount64);

        lock (_parryEvidenceSync)
        {
            if (!_telemetry.IsRecording) return;

            long sentAtMs = _telemetry.ElapsedMs;
            if (_parryEvidence != null)
            {
                RecordTelemetry("parry-evidence-coalesced", new
                {
                    attemptId,
                    candidateId,
                    direction = direction.ToString(),
                    activeAttemptId = _parryEvidence.AttemptId,
                    activeCandidateId = _parryEvidence.CandidateId,
                    activeDirection = _parryEvidence.Direction.ToString(),
                    timestampMs = sentAtMs,
                    sentAtMs
                });
                return;
            }

            _parryEvidence = new ParryEvidenceSequence(attemptId, candidateId, direction,
                sentAtMs);
            // This is input delivery evidence only. It deliberately makes no
            // claim that the game accepted the parry.
            RecordTelemetry("parry-sent", new
            {
                attemptId,
                candidateId,
                direction = direction.ToString(),
                timestampMs = sentAtMs,
                sentAtMs
            });
        }
    }

    private void ProcessParryConfirmation()
    {
        IReadOnlyList<ParryConfirmationScan> scans = _parryConfirmation.Scan(_frame, Environment.TickCount64,
            System.Windows.Forms.Screen.PrimaryScreen.Bounds);
        foreach (ParryConfirmationScan scan in scans)
        {
            if (_telemetry.IsRecording)
            {
                if (scan.BaselineEstablishedNow)
                {
                    RecordTelemetry("parry-confirmation-baseline", new
                    {
                        scan.AttemptId,
                        scan.CandidateId,
                        direction = scan.Direction.ToString(),
                        baseline = scan.Baseline,
                        threshold = scan.Threshold,
                        region = scan.Region,
                        resolution = new { width = ScreenWidth, height = ScreenHeight }
                    });
                }
                RecordTelemetry("parry-confirmation-scan", new
                {
                    scan.AttemptId,
                    scan.CandidateId,
                    direction = scan.Direction.ToString(),
                    elapsedMs = scan.ElapsedMs,
                    brightPixels = scan.BrightPixels,
                    baseline = scan.Baseline,
                    threshold = scan.Threshold,
                    baselineEstablished = scan.BaselineEstablished,
                    qualifying = scan.Qualifying,
                    consecutiveQualifying = scan.ConsecutiveQualifying,
                    region = scan.Region,
                    resolution = new { width = ScreenWidth, height = ScreenHeight }
                });
            }

            if (scan.Result == ParryConfirmationResult.Confirmed)
            {
                Interlocked.Increment(ref ParryConfirmedCount);
                RecordTelemetry("parry-confirmation-result", new
                {
                    scan.AttemptId,
                    scan.CandidateId,
                    direction = scan.Direction.ToString(),
                    result = "CONFIRMED",
                    elapsedMs = scan.ElapsedMs,
                    brightPixels = scan.BrightPixels,
                    baseline = scan.Baseline,
                    threshold = scan.Threshold
                });
                SetVisionReaction("PARRY CONFIRMED", "White/gold impact detected", DirectionName(scan.Direction), 1300);
            }
            else if (scan.Result == ParryConfirmationResult.Unconfirmed)
            {
                Interlocked.Increment(ref ParryUnconfirmedCount);
                RecordTelemetry("parry-confirmation-result", new
                {
                    scan.AttemptId,
                    scan.CandidateId,
                    direction = scan.Direction.ToString(),
                    result = "UNCONFIRMED",
                    elapsedMs = scan.ElapsedMs,
                    brightPixels = scan.BrightPixels,
                    baseline = scan.Baseline,
                    threshold = scan.Threshold,
                    reason = "No visual proof found in the confirmation window"
                });
                SetVisionReaction("PARRY UNCONFIRMED", "No visual proof found; RT delivery did not fail", DirectionName(scan.Direction), 1300);
            }
        }
    }

    private void ProcessParryEvidence()
    {
        lock (_parryEvidenceSync)
        {
            ParryEvidenceSequence sequence;
            if (!_telemetry.IsRecording || (sequence = _parryEvidence) == null) return;
            long elapsedSinceInput = Math.Max(0, _telemetry.ElapsedMs - sequence.SentAtMs);
            if (sequence.NextOffsetIndex >= ParryEvidenceOffsetsMs.Length ||
                elapsedSinceInput < ParryEvidenceOffsetsMs[sequence.NextOffsetIndex]) return;

            int scheduledOffsetMs = ParryEvidenceOffsetsMs[sequence.NextOffsetIndex];
            long capturedElapsedMs = _telemetry.ElapsedMs;
            // Keep the queue insertion under the same lock as the request state:
            // a later RT cannot overtake this frame's telemetry item.
            if (!_telemetry.CaptureFrameSnapshot(sequence.AttemptId, scheduledOffsetMs, capturedElapsedMs, _frame)) return;
            sequence.NextOffsetIndex++;
            if (sequence.NextOffsetIndex >= ParryEvidenceOffsetsMs.Length) _parryEvidence = null;
        }
    }

    private void UpdateVisionTracking()
    {
        if (!MarkerFound)
        {
            _reactionState = "SEARCHING";
            _reactionReason = "Waiting for a green or yellow anchor";
            _reactionDirection = "";
            _reactionDisplayUntil = 0;
        }
        else if (Environment.TickCount64 >= _reactionDisplayUntil)
        {
            _reactionState = "TRACKING";
            _reactionReason = "Anchor-relative combat region";
            _reactionDirection = "";
        }
        PublishVision();
    }

    private void SetVisionReaction(string state, string reason, string direction = "", int displayMs = 1100)
    {
        _reactionState = state;
        _reactionReason = reason;
        _reactionDirection = direction;
        _reactionDisplayUntil = Environment.TickCount64 + displayMs;
        RecordTelemetry("reaction-state", new { state, reason, direction, guard = GuardDir, waitMs = ReactionWaitMilliseconds, flash = Flash });
        if (state.Contains("PARRY SENT", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("PARRY FAILED", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("PARRY CANCELLED", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("PARRY BLOCKED", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("CRUSHING SENT", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("DEFLECT SENT", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("DEFLECT FAILED", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("HERO RESPONSE SENT", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("BULWARK", StringComparison.OrdinalIgnoreCase))
        {
            _telemetry.CaptureRoi("reaction-" + state, CombatRoiRectangle());
            EndReactionWait("reaction-finished");
        }
        PublishVision();
    }

    private static string DirectionName(CombatDirection direction) => direction switch
    {
        CombatDirection.Left => "LEFT",
        CombatDirection.Right => "RIGHT",
        CombatDirection.Top => "TOP",
        _ => ""
    };

    private static int GuardKey(CombatDirection direction) => direction switch
    {
        CombatDirection.Left => Input.VK_NUMPAD4,
        CombatDirection.Right => Input.VK_NUMPAD6,
        _ => Input.VK_NUMPAD8
    };

    private string LegitParryStatus
    {
        get
        {
            if (!S.Legit) return "LEGIT OFF";
            ParryDecision decision = _actions.LatestParryDecision;
            return decision == null
                ? $"LEGIT {S.LegitParryChance}% WAIT"
                : $"LEGIT {decision.ChancePercent}% {(_actions.LatestParryOutcome?.ToString() ?? decision.Outcome).ToUpperInvariant()}";
        }
    }

    private void PublishVision()
    {
        ReactionCandidate candidate = _reactionCoordinator.CurrentCandidate;
        long now = Environment.TickCount64;
        var anchorScan = RectangleF.FromLTRB((float)X8, (float)Y8, (float)X9, (float)Y9);
        Rectangle combatRoiRectangle = CombatRoiRectangle();
        var combatRoi = new RectangleF(combatRoiRectangle.X, combatRoiRectangle.Y,
            combatRoiRectangle.Width, combatRoiRectangle.Height);
        // The active coordinator searches only inside CombatRoi, then applies
        // these exact half-plane thresholds. Clip the visual zones to that ROI.
        var topZone = RectangleF.FromLTRB(combatRoi.Left, Math.Max(combatRoi.Top, (float)Math.Min(Y2, Y3)),
            combatRoi.Right, Math.Min(combatRoi.Bottom, (float)Math.Max(Y2, Y3)));
        var rightZone = RectangleF.FromLTRB(Math.Max(combatRoi.Left, (float)X4), Math.Max(combatRoi.Top, (float)Y4),
            combatRoi.Right, combatRoi.Bottom);
        var leftZone = RectangleF.FromLTRB(combatRoi.Left, Math.Max(combatRoi.Top, (float)Y4),
            Math.Min(combatRoi.Right, (float)X7), combatRoi.Bottom);

        var snapshot = new VisionSnapshot
        {
            Running = IsRunning,
            MarkerFound = MarkerFound,
            MarkerKind = _markerKind,
            Anchor = new Point(Ax, Ay),
            AnchorScan = anchorScan,
            CombatRoi = combatRoi,
            TopZone = topZone,
            LeftZone = leftZone,
            RightZone = rightZone,
            AttackIndicator = AttackIndicator,
            Indicator = new Point(_indicatorX, _indicatorY),
            GuardDirection = GuardDir,
            DecisionDirection = _reactionDirection,
            ReactionState = _reactionState,
            ReactionReason = _reactionReason,
            Flash = Flash,
            LoopHz = LoopHz,
            Box = Box,
            AnchorAgeMs = MarkerFound && _anchorChangedTick > 0 ? Math.Max(0, now - _anchorChangedTick) : 0,
            GuardRemainingMs = GuardRemainingMilliseconds,
            ReactionWaitMs = candidate == null ? 0 : Math.Max(0, now - candidate.StartedMs),
            CandidateId = candidate?.Id ?? 0,
            CandidateAgeMs = candidate == null ? 0 : Math.Max(0, now - candidate.StartedMs),
            CandidateLastValidAgeMs = candidate == null ? 0 : Math.Max(0, now - candidate.LastValidMs),
            ActionWorkerState = _actions.State,
            LegitParryStatus = LegitParryStatus,
            TelemetryRecording = _telemetry.IsRecording
        };
        lock (_visionSync) _vision = snapshot;
    }

    private void Calculate()
    {
        bool wasFound = MarkerFound;
        int oldAx = Ax, oldAy = Ay, oldBox = Box;
        SetScreenDimensions();

        int rawBox = CurrentPx(X18, Y18, X19, Y19, 5, 131, 65, 0, out _, out _) ? 1 : 2;
        bool rawFound = CurrentPx(X8, Y8, X9, Y9, 5, 131, 65, 0, out int rawX, out int rawY);
        string rawKind = rawFound ? "GREEN" : "NONE";
        if (!rawFound && CurrentPx(X8, Y8, X9, Y9, 255, 255, 10, 0, out rawX, out rawY))
        {
            rawFound = true;
            rawKind = "YELLOW";
        }

        ApplyDebouncedMarkerSample(wasFound, rawFound, rawX, rawY, rawKind, rawBox);
        UpdateMarkerLossAge();
        ObserveAnchorTracking(wasFound, oldAx, oldAy, oldBox);
    }

    /// <summary>
    /// The marker search returns the first matching pixel, which can briefly
    /// jump between decorative pixels.  Keep the last accepted geometry until
    /// a new sample has been seen twice in the same small neighborhood.
    /// </summary>
    private void ApplyDebouncedMarkerSample(bool wasFound, bool rawFound, int rawX, int rawY,
        string rawKind, int rawBox)
    {
        long now = Environment.TickCount64;
        if (!rawFound)
        {
            ClearPendingMarker();
            if (wasFound && _rawMarkerMissingSinceTick == 0)
                _rawMarkerMissingSinceTick = now;

            if (wasFound && now - _rawMarkerMissingSinceTick <= MarkerLossDebounceMs)
            {
                // A one- or two-frame hole must not relocate or drop the ROI.
                MarkerFound = true;
                return;
            }

            MarkerFound = false;
            _markerKind = "NONE";
            return;
        }

        _rawMarkerMissingSinceTick = 0;
        int distance = wasFound ? Math.Max(Math.Abs(rawX - Ax), Math.Abs(rawY - Ay)) : int.MaxValue;
        bool sameAcceptedMarker = wasFound && rawKind == _markerKind && rawBox == Box &&
            distance <= MarkerSamplePositionTolerancePx;
        if (sameAcceptedMarker)
        {
            ClearPendingMarker();
            ApplyAcceptedMarker(rawX, rawY, rawKind, rawBox);
            return;
        }

        bool samePendingMarker = _pendingMarkerSamples > 0 && rawKind == _pendingMarkerKind &&
            rawBox == _pendingMarkerBox &&
            Math.Max(Math.Abs(rawX - _pendingMarkerX), Math.Abs(rawY - _pendingMarkerY)) <= MarkerSamplePositionTolerancePx;
        if (samePendingMarker)
        {
            _pendingMarkerSamples++;
            // Average the trusted samples slightly so one edge pixel does not
            // create a needless small geometry wobble.
            _pendingMarkerX = (_pendingMarkerX + rawX) / 2;
            _pendingMarkerY = (_pendingMarkerY + rawY) / 2;
        }
        else
        {
            _pendingMarkerX = rawX;
            _pendingMarkerY = rawY;
            _pendingMarkerKind = rawKind;
            _pendingMarkerBox = rawBox;
            _pendingMarkerSamples = 1;
            _pendingMarkerSinceTick = now;
        }

        if (_pendingMarkerSamples >= MarkerSampleConfirmationFrames)
        {
            ApplyAcceptedMarker(_pendingMarkerX, _pendingMarkerY, _pendingMarkerKind, _pendingMarkerBox);
            ClearPendingMarker();
            return;
        }

        if (wasFound && now - _pendingMarkerSinceTick <= PendingMarkerMaximumMs)
        {
            // Keep scanning the previous ROI while the raw position proves
            // itself.  This prevents an isolated 40+ px jump from dragging the
            // side-indicator search out of range.
            MarkerFound = true;
            return;
        }

        MarkerFound = false;
        _markerKind = "NONE";
    }

    private void ApplyAcceptedMarker(int x, int y, string kind, int box)
    {
        Ax = x;
        Ay = y;
        Box = box;
        MarkerFound = true;
        _markerKind = kind;
        if (kind == "GREEN")
        {
            if (box == 2)
                SetCoords(x - 200 * B55, y + 20 * Y55, x + 160 * B55, y + 170 * Y55,
                          x + 5 * B55, y + 195 * Y55, x + 160 * B55, y + 430 * Y55,
                          x - 200 * B55, y + 195 * Y55, x - 30 * B55, y + 430 * Y55,
                          x - 200 * B55, y + 20 * Y55, x + 160 * B55, y + 430 * Y55);
            else
                SetCoords(x - 100 * B55, y + 10 * Y55, x + 80 * B55, y + 85 * Y55,
                          x + 2.5 * B55, y + 97.5 * Y55, x + 80 * B55, y + 227.7 * Y55,
                          x - 100 * B55, y + 97.5 * Y55, x - 15 * B55, y + 227.7 * Y55,
                          x - 117.6 * B55, y + 10 * Y55, x + 94.11 * B55, y + 227.7 * Y55);
            return;
        }

        if (box == 2)
            SetCoords(x - 175 * B55, y + 65 * Y55, x + 185 * B55, y + 185 * Y55,
                      x + 30 * B55, y + 215 * Y55, x + 185 * B55, y + 430 * Y55,
                      x - 175 * B55, y + 215 * Y55, x - 5 * B55, y + 430 * Y55,
                      x - 175 * B55, y + 65 * Y55, x + 185 * B55, y + 430 * Y55);
        else
            SetCoords(x - 87.5 * B55, y + 35 * Y55, x + 92.5 * B55, y + 92.5 * Y55,
                      x + 15 * B55, y + 107.5 * Y55, x + 92.5 * B55, y + 215 * Y55,
                      x - 87.5 * B55, y + 107.5 * Y55, x - 2.5 * B55, y + 215 * Y55,
                      x - 87.5 * B55, y + 35 * Y55, x + 92.5 * B55, y + 215 * Y55);
    }

    private void ClearPendingMarker()
    {
        _pendingMarkerSinceTick = 0;
        _pendingMarkerSamples = 0;
        _pendingMarkerKind = "NONE";
    }

    private void UpdateMarkerLossAge()
    {
        if (MarkerFound)
        {
            _markerLossStartedTick = 0;
            return;
        }
        if (_markerLossStartedTick == 0) _markerLossStartedTick = Environment.TickCount64;
    }

    private void ObserveAnchorTracking(bool wasFound, int oldAx, int oldAy, int oldBox)
    {
        long now = Environment.TickCount64;
        if (!MarkerFound)
        {
            _anchorGraceStartedTick = 0;
            if (_markerLossStartedTick == 0) _markerLossStartedTick = now;
            if (wasFound)
            {
                RecordTelemetry("marker-lost", new { oldAx, oldAy, oldBox }, true);
                _telemetry.CaptureRoi("marker-lost", CombatRoiRectangle());
            }
            return;
        }

        _markerLossStartedTick = 0;
        if (_anchorGraceStartedTick != 0 && now - _anchorGraceStartedTick > ReactionCoordinator.MissingGraceMs)
            _anchorGraceStartedTick = 0;

        int deltaX = wasFound ? Ax - oldAx : 0;
        int deltaY = wasFound ? Ay - oldAy : 0;
        _anchorDeltaX = deltaX;
        _anchorDeltaY = deltaY;
        int distance = Math.Max(Math.Abs(deltaX), Math.Abs(deltaY));
        if (!wasFound)
        {
            _anchorChangedTick = now;
            RecordTelemetry("marker-found", new { kind = _markerKind, x = Ax, y = Ay, box = Box });
            _telemetry.CaptureRoi("marker-found", CombatRoiRectangle());
        }
        else if (distance > 2)
        {
            _anchorChangedTick = now;
            if (distance >= 40)
            {
                // Freeze a currently armed candidate at its last trustworthy
                // geometry.  Do not refresh this timer on repeated bad reads;
                // the grace remains bounded to the coordinator policy.
                if (_anchorGraceStartedTick == 0 && _reactionCoordinator.CurrentCandidate is { Consumed: false })
                    _anchorGraceStartedTick = now;
                RecordTelemetry("anchor-jump", new { x = Ax, y = Ay, deltaX, deltaY, distance, box = Box }, true);
                _telemetry.CaptureRoi("anchor-jump", CombatRoiRectangle());
            }
        }
        if (oldBox != Box)
        {
            RecordTelemetry("box-flip", new { from = oldBox, to = Box, x = Ax, y = Ay }, true);
            _telemetry.CaptureRoi("box-flip", CombatRoiRectangle());
        }
    }

    private bool YourChar(string name) => ReactionPolicy.IsYourChar(S, name);

    private bool HasEAction() => ReactionPolicy.HasEAction(S);

    private bool HasFAction() => ReactionPolicy.HasFAction(S);

    private bool HasHeroAction() => ReactionPolicy.HasHeroAction(S);

    private sealed record CachedCandidateGeometry(
        long CandidateId,
        Rectangle CombatRoi,
        double TopLeftX,
        double TopLeftY,
        double TopRightX,
        double TopRightY,
        double RightX,
        double RightY,
        double LeftX,
        double LeftY,
        CombatDirection Direction,
        int Box,
        Point Anchor,
        long CachedAtMs);

    private sealed record FlashCalibrationCandidate(
        long CandidateId,
        CombatDirection Direction,
        Rectangle Region,
        long ArmedElapsedMs,
        int PeakMatches,
        int IndicatorPeakMatches,
        int TemporalPeakMatches,
        int TemporalPeakLargestCluster,
        string ArmFrame,
        string PeakFrame,
        string IndicatorPeakFrame,
        string TemporalPeakFrame,
        FlashTemporalBaseline TemporalBaseline);

    private sealed class ParryEvidenceSequence
    {
        public ParryEvidenceSequence(string attemptId, long candidateId, CombatDirection direction,
            long sentAtMs)
        {
            AttemptId = attemptId;
            CandidateId = candidateId;
            Direction = direction;
            SentAtMs = sentAtMs;
        }

        public string AttemptId { get; }
        public long CandidateId { get; }
        public CombatDirection Direction { get; }
        public long SentAtMs { get; }
        public int NextOffsetIndex { get; set; }
    }

}
