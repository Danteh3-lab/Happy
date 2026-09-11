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

    public readonly CombatGeometry Geometry = new();
    private readonly AnchorTracker _anchorTracker = new();

    public bool MarkerFound => _anchorTracker.Found;
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
    private int _indicatorX = -1;
    private int _indicatorY = -1;
    private string _reactionState = "SEARCHING";
    private string _reactionReason = "Waiting for an anchor";
    private string _reactionDirection = "";
    private string _lastReactionState = "NONE";
    private string _lastReactionReason = "No reaction has been sent yet.";
    private string _lastReactionDirection = "";
    private int _lastReactionDelayMs = -1;
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
            () => Geometry.CombatRoi(),
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
    void IAutomationHost.SetVisionReaction(string state, string reason, string direction, int displayMs, int? appliedDelayMs) =>
        SetVisionReaction(state, reason, direction, displayMs, appliedDelayMs);
    void IAutomationHost.RecordTelemetry(string name, object data, bool failure) => RecordTelemetry(name, data, failure);
    void IAutomationHost.IncrementParryCount() => ParryCount++;
    void IAutomationHost.RequestParryEvidence(long candidateId, CombatDirection direction, int delayMs) =>
        RequestParryEvidence(candidateId, direction, delayMs);
    void IAutomationHost.CaptureOrangeParryEvidence(CombatObservation observation, int delay,
        int feintTransitionGraceMs, long clearGapAgeMs, bool usedTransitionGrace,
        long feintDetectedAtMs, long clearStartedAtMs) =>
        CaptureOrangeParryEvidence(observation, delay, feintTransitionGraceMs, clearGapAgeMs,
            usedTransitionGrace, feintDetectedAtMs, clearStartedAtMs);
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

    // Serializes all shutdown work (abort, neutralize, teardown) so
    // concurrent Stop/Dispose pipelines can never touch resources that
    // Dispose tore down. Lock order is always _shutdownSync -> _combatStateSync.
    private readonly object _shutdownSync = new();
    // Observability for shutdown regression tests.
    internal volatile bool LoopResourcesDisposed;

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
        _cts.Cancel();
        _parryConfirmation.Clear();
        _paused.Set();
        // Never wait here: the worker may hold the combat lock inside stalled
        // capture or device I/O, and the bridge runs independently of
        // automation — so even a never-started bot could hang the caller on a
        // stalled controller submission. One shared background pipeline owns
        // abort and neutralize for every worker state.
        ThreadPool.QueueUserWorkItem(_ => ShutdownPipeline(teardown: false));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        Thread worker = _thread;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { worker?.Join(); } catch { }
            ShutdownPipeline(teardown: true);
        });
    }

    /// <summary>
    /// Shared shutdown pipeline for every worker state. Runs fully on a pool
    /// thread: callers never wait on input operations or this gate. Abort and
    /// neutralize are idempotent; teardown runs exactly once (only the Dispose
    /// pipeline passes teardown: true, queued once).
    /// </summary>
    private void ShutdownPipeline(bool teardown)
    {
        lock (_shutdownSync)
        {
            // A Dispose pipeline owns post-exit work once disposal starts; a
            // Stop pipeline must not touch torn-down resources.
            if (!teardown && Volatile.Read(ref _disposed) != 0) return;
            try
            {
                AbortCombatState("shutdown", true);
                if (!teardown && Volatile.Read(ref _disposed) != 0) return;
                NeutralizeInputs();
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
            }
            if (teardown) DisposeLoopResources();
        }
    }

    private void NeutralizeInputs()
    {
        _autoGuard.Release("manual-stop");
        _input.ReleaseAutomationInputs();
    }

    private void DisposeLoopResources()
    {
        _actions.Dispose();
        _autoGuard.Dispose();
        _captureSession.Dispose();
        _telemetry.Dispose();
        _cts.Dispose();
        _paused.Dispose();
        LoopResourcesDisposed = true;
    }

    public void TogglePause()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_paused.IsSet)
        {
            // Close the loop gate before releasing input so an in-flight
            // frame cannot re-arm guard after pause was requested.
            _paused.Reset();
            // Bounded like Stop. On timeout the in-flight frame may still
            // have applied guard, so retry once it releases the lock —
            // rechecking pause state under the lock so a resume in between
            // cannot cancel fresh state.
            if (!TryAbortCombatState("paused", true, 250))
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        lock (_combatStateSync)
                        {
                            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _stopRequested) != 0) return;
                            if (_paused.IsSet) return;
                            AbortCombatStateLocked("paused-deferred", true);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
            }
        }
        else _paused.Set();
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

    /// <summary>
    /// Records bridge-only diagnostics while the combat loop is stopped or
    /// paused. The UI status timer calls this at a low rate so idle input
    /// stutter can be compared with active-loop telemetry.
    /// </summary>
    public void RecordBridgeHeartbeat()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_telemetry.IsRecording) return;
        _telemetry.Record("bridge-heartbeat", new
        {
            running = IsRunning,
            paused = IsPaused,
            bridge = ViGEmInput.GetDiagnostics()
        });
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
                    Sleep(250);
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
        long markerLossAgeMs = MarkerFound ? 0 : _anchorTracker.MarkerLossAgeMs(now);
        long anchorGraceAgeMs = _anchorTracker.AnchorGraceAgeMs(now);
        bool markerGraceScan = !MarkerFound && candidate is { Consumed: false } &&
            cached != null && cached.CandidateId == candidate.Id &&
            markerLossAgeMs <= ReactionCoordinator.MissingGraceMs;
        bool anchorGraceScan = MarkerFound && candidate is { Consumed: false } &&
            cached != null && cached.CandidateId == candidate.Id &&
            _anchorTracker.AnchorGraceActive && anchorGraceAgeMs <= ReactionCoordinator.MissingGraceMs;
        bool candidateGraceScan = markerGraceScan || anchorGraceScan;
        long trackingGraceAgeMs = markerGraceScan ? markerLossAgeMs : anchorGraceScan ? anchorGraceAgeMs : 0;
        Point scanAnchor = candidateGraceScan ? cached.Anchor : new Point(Geometry.Ax, Geometry.Ay);
        int scanBox = candidateGraceScan ? cached.Box : Geometry.Box;
        FlashTemporalBaseline baseline = calibration is { CandidateId: var calibrationId } && candidate is { Id: var candidateId } &&
            calibrationId == candidateId ? calibration.TemporalBaseline : null;
        VisionAnalysisResult result = _visionAnalyzer.Scan(_frame, new VisionScanRequest(
            now,
            MarkerFound,
            scanAnchor,
            scanBox,
            Geometry.CombatRoi(),
            candidateGraceScan ? cached.TopLeftX : Geometry.X2,
            candidateGraceScan ? cached.TopLeftY : Geometry.Y2,
            candidateGraceScan ? cached.TopRightX : Geometry.X3,
            candidateGraceScan ? cached.TopRightY : Geometry.Y3,
            candidateGraceScan ? cached.RightX : Geometry.X4,
            candidateGraceScan ? cached.RightY : Geometry.Y4,
            candidateGraceScan ? cached.LeftX : Geometry.X7,
            candidateGraceScan ? cached.LeftY : Geometry.Y4,
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
            SetVisionReaction("ORANGE IGNORED", $"Own source {outgoingOrange.AttributionSource} attack", "", 1300, -1);
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
                SetVisionReaction("GUARD", "Current classified red indicator", DirectionName(tick.Candidate.Direction), 900, -1);
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
            SetVisionReaction("REACTION CANCELLED", tick.CancellationReason, "", 800, -1);
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
            RecordTelemetry("indicator-unknown", new { x = observation.Indicator.X, y = observation.Indicator.Y, box = Geometry.Box }, true);
            SetVisionReaction("INDICATOR UNKNOWN", "Red indicator was outside the directional zones", "", 800, -1);
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

    private void CaptureOrangeParryEvidence(CombatObservation observation, int delay,
        int feintTransitionGraceMs, long clearGapAgeMs, bool usedTransitionGrace,
        long feintDetectedAtMs, long clearStartedAtMs)
    {
        string file = _telemetry.CaptureRegionSnapshot("orange-parry-detected", _frame, observation.CombatRoi);
        RecordTelemetry("orange-parry-detected", new
        {
            response = "parry",
            orangeParryEnabled = OrangeParry,
            orangeIndicator = observation.OrangeIndicator,
            orangeFeint = observation.OrangeFeint,
            markerFound = observation.MarkerFound,
            scanMode = ScanModeName(observation.ScanMode),
            roi = RectangleTelemetry(observation.CombatRoi),
            cachedRoi = RectangleTelemetry(observation.CachedCombatRoi),
            markerLossAgeMs = observation.MarkerLossAgeMs,
            orangeDelayMs = delay,
            parryDelayMs = S.ParryDelay,
            totalDelayMs = Math.Max(0, delay) + Math.Max(0, S.ParryDelay),
            feintTransitionGraceMs,
            clearGapAgeMs,
            usedTransitionGrace,
            feintDetectedAtMs,
            clearStartedAtMs,
            file
        });
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
            Geometry.X2,
            Geometry.Y2,
            Geometry.X3,
            Geometry.Y3,
            Geometry.X4,
            Geometry.Y4,
            Geometry.X7,
            Geometry.Y4,
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
            AbortCombatStateLocked(reason, forceAction);
        }
    }

    private bool TryAbortCombatState(string reason, bool forceAction, int timeoutMs)
    {
        bool entered = Monitor.TryEnter(_combatStateSync, timeoutMs);
        if (!entered) return false;
        try
        {
            AbortCombatStateLocked(reason, forceAction);
            return true;
        }
        finally
        {
            Monitor.Exit(_combatStateSync);
        }
    }

    private void AbortCombatStateLocked(string reason, bool forceAction)
    {
        _reactionCoordinator.Cancel(reason);
        _cachedCandidateGeometry = null;
        _flashCalibration = null;
        _actions.CancelPendingAction(reason, forceAction);
        ReleaseAutoGuard();
        _input.ReleaseAutomationInputs();
    }

    public string DebugScan()
    {
        var f = ScreenCapture.Capture(null);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Screen captured: {f.Width}x{f.Height}");
        sb.AppendLine($"Search region: ({Geometry.X8:0},{Geometry.Y8:0})-({Geometry.X9:0},{Geometry.Y9:0})");
        sb.AppendLine($"Scalers: B55={Geometry.B55:0.###} Y55={Geometry.Y55:0.###}");
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

        int sx = Math.Max(0, Math.Min((int)Geometry.X8, f.Width - 1));
        int ex = Math.Max(0, Math.Min((int)Geometry.X9, f.Width - 1));
        int sy = Math.Max(0, Math.Min((int)Geometry.Y8, f.Height - 1));
        int ey = Math.Max(0, Math.Min((int)Geometry.Y9, f.Height - 1));

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

        int cx = (int)((Geometry.X8 + Geometry.X9) / 2);
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

    private bool HasLiveCandidate() => _reactionCoordinator.CurrentCandidate is { Consumed: false };

    /// <summary>
    /// Legacy anchor-jump payload shape. Property names are contractual:
    /// telemetry consumers expect lowercase deltaX/deltaY/distance, so this
    /// stays a named builder with a serialized regression test instead of an
    /// inline anonymous object (shorthand would capitalize the names).
    /// </summary>
    internal static object AnchorJumpPayload(int x, int y, int deltaX, int deltaY, int distance, int box) =>
        new { x, y, deltaX, deltaY, distance, box };

    public void UpdateResolution(int width, int height)
    {
        Geometry.UpdateResolution(width, height);
        RefreshVisionSnapshot();
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
            _telemetry.CaptureRoi("wait-flash-500ms", Geometry.CombatRoi());
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
            marker = new { found = MarkerFound, kind = _anchorTracker.Kind, x = Geometry.Ax, y = Geometry.Ay, deltaX = _anchorTracker.DeltaX, deltaY = _anchorTracker.DeltaY, ageMs = Math.Max(0, now - _anchorTracker.AnchorChangedTick) },
            box = Geometry.Box,
            roi = Geometry.CombatRoi(),
            zones = new { top = new { Geometry.X2, Geometry.Y2, Geometry.X3, Geometry.Y3 }, left = new { Geometry.X6, Geometry.Y6, Geometry.X7, Geometry.Y7 }, right = new { Geometry.X4, Geometry.Y4, Geometry.X5, Geometry.Y5 } },
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
            ? Geometry.CombatRoi()
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
        Rectangle markerScan = Geometry.AnchorScan();
        Rectangle boxScan = Geometry.BoxScan();
        Rectangle possibleCombat = CaptureRegionPlanner.PossibleCombatBounds(markerScan, Geometry.B55, Geometry.Y55);
        Rectangle activeCombat = MarkerFound ? Geometry.CombatRoi() : Rectangle.Empty;
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

    private void RequestParryEvidence(long candidateId, CombatDirection direction, int delayMs)
    {
        string attemptId = $"parry-{Interlocked.Increment(ref _nextParryEvidenceId):D6}";
        int capturedDelayMs = delayMs < 0 ? -1 : delayMs;
        _parryConfirmation.Start(attemptId, candidateId, direction, Environment.TickCount64, capturedDelayMs);

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
                    delayMs = capturedDelayMs,
                    timestampMs = sentAtMs,
                    sentAtMs
                });
                return;
            }

            _parryEvidence = new ParryEvidenceSequence(attemptId, candidateId, direction,
                sentAtMs, capturedDelayMs);
            // This is input delivery evidence only. It deliberately makes no
            // claim that the game accepted the parry.
            RecordTelemetry("parry-sent", new
            {
                attemptId,
                candidateId,
                direction = direction.ToString(),
                delayMs = capturedDelayMs,
                timestampMs = sentAtMs,
                sentAtMs
            });
        }
    }

    private void ProcessParryConfirmation()
    {
        ProcessParryConfirmation(_frame, Environment.TickCount64,
            System.Windows.Forms.Screen.PrimaryScreen.Bounds);
    }

    // Keeps the confirmation lifecycle deterministic for regression coverage
    // without changing the production capture loop.
    internal void ProcessParryConfirmationForTests(ScreenFrame frame, long nowTick, Rectangle screenBounds)
    {
        ProcessParryConfirmation(frame, nowTick, screenBounds);
    }

    private void ProcessParryConfirmation(ScreenFrame frame, long nowTick, Rectangle screenBounds)
    {
        IReadOnlyList<ParryConfirmationScan> scans = _parryConfirmation.Scan(frame, nowTick, screenBounds);
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
                    delayMs = scan.DelayMs,
                    elapsedMs = scan.ElapsedMs,
                    brightPixels = scan.BrightPixels,
                    baseline = scan.Baseline,
                    threshold = scan.Threshold
                });
                SetVisionReaction("PARRY CONFIRMED", "White/gold impact detected", DirectionName(scan.Direction), 1300, scan.DelayMs);
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
                    delayMs = scan.DelayMs,
                    elapsedMs = scan.ElapsedMs,
                    brightPixels = scan.BrightPixels,
                    baseline = scan.Baseline,
                    threshold = scan.Threshold,
                    reason = "No visual proof found in the confirmation window"
                });
                SetVisionReaction("PARRY UNCONFIRMED", "No visual proof found; RT delivery did not fail", DirectionName(scan.Direction), 1300, scan.DelayMs);
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

    private void SetVisionReaction(string state, string reason, string direction = "", int displayMs = 1100,
        int? appliedDelayMs = null)
    {
        _reactionState = state;
        _reactionReason = reason;
        _reactionDirection = direction;
        _lastReactionState = state;
        _lastReactionReason = reason;
        _lastReactionDirection = direction;
        if (appliedDelayMs.HasValue)
            _lastReactionDelayMs = appliedDelayMs.Value < 0 ? -1 : appliedDelayMs.Value;
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
            _telemetry.CaptureRoi("reaction-" + state, Geometry.CombatRoi());
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
        var anchorScan = RectangleF.FromLTRB((float)Geometry.X8, (float)Geometry.Y8, (float)Geometry.X9, (float)Geometry.Y9);
        Rectangle combatRoiRectangle = Geometry.CombatRoi();
        var combatRoi = new RectangleF(combatRoiRectangle.X, combatRoiRectangle.Y,
            combatRoiRectangle.Width, combatRoiRectangle.Height);
        (RectangleF topZone, RectangleF rightZone, RectangleF leftZone) = Geometry.Zones(combatRoi);

        var snapshot = new VisionSnapshot
        {
            Running = IsRunning,
            MarkerFound = MarkerFound,
            MarkerKind = _anchorTracker.Kind,
            Anchor = new Point(Geometry.Ax, Geometry.Ay),
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
            LastReactionState = _lastReactionState,
            LastReactionReason = _lastReactionReason,
            LastReactionDirection = _lastReactionDirection,
            LastReactionDelayMs = _lastReactionDelayMs,
            Flash = Flash,
            LoopHz = LoopHz,
            Box = Geometry.Box,
            AnchorAgeMs = MarkerFound && _anchorTracker.AnchorChangedTick > 0 ? Math.Max(0, now - _anchorTracker.AnchorChangedTick) : 0,
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
        int oldAx = Geometry.Ax, oldAy = Geometry.Ay, oldBox = Geometry.Box;
        SetScreenDimensions();

        int rawBox = CurrentPx(Geometry.X18, Geometry.Y18, Geometry.X19, Geometry.Y19, 5, 131, 65, 0, out _, out _) ? 1 : 2;
        bool rawFound = CurrentPx(Geometry.X8, Geometry.Y8, Geometry.X9, Geometry.Y9, 5, 131, 65, 0, out int rawX, out int rawY);
        string rawKind = rawFound ? "GREEN" : "NONE";
        if (!rawFound && CurrentPx(Geometry.X8, Geometry.Y8, Geometry.X9, Geometry.Y9, 255, 255, 10, 0, out rawX, out rawY))
        {
            rawFound = true;
            rawKind = "YELLOW";
        }

        AnchorTracking tracking = _anchorTracker.Observe(wasFound, oldAx, oldAy, oldBox,
            rawFound, rawX, rawY, rawKind, rawBox, Geometry, Environment.TickCount64, HasLiveCandidate);
        if (tracking.BecameLost)
        {
            RecordTelemetry("marker-lost", new { oldAx, oldAy, oldBox }, true);
            _telemetry.CaptureRoi("marker-lost", Geometry.CombatRoi());
        }
        if (tracking.BecameFound)
        {
            RecordTelemetry("marker-found", new { kind = _anchorTracker.Kind, x = Geometry.Ax, y = Geometry.Ay, box = Geometry.Box });
            _telemetry.CaptureRoi("marker-found", Geometry.CombatRoi());
        }
        if (tracking.Jumped)
        {
            RecordTelemetry("anchor-jump", AnchorJumpPayload(Geometry.Ax, Geometry.Ay, tracking.DeltaX, tracking.DeltaY, tracking.Distance, Geometry.Box), true);
            _telemetry.CaptureRoi("anchor-jump", Geometry.CombatRoi());
        }
        if (tracking.BoxFlipped)
        {
            RecordTelemetry("box-flip", new { from = tracking.OldBox, to = Geometry.Box, x = Geometry.Ax, y = Geometry.Ay }, true);
            _telemetry.CaptureRoi("box-flip", Geometry.CombatRoi());
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
            long sentAtMs, int delayMs)
        {
            AttemptId = attemptId;
            CandidateId = candidateId;
            Direction = direction;
            SentAtMs = sentAtMs;
            DelayMs = delayMs;
        }

        public string AttemptId { get; }
        public long CandidateId { get; }
        public CombatDirection Direction { get; }
        public long SentAtMs { get; }
        public int DelayMs { get; }
        public int NextOffsetIndex { get; set; }
    }

}
