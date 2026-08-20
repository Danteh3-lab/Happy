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

    // Geometry and marker state are published as one immutable reference.
    // Read-only compatibility properties below keep diagnostics and the UI
    // source-compatible without exposing independently mutable coordinates.
    private readonly object _trackingSync = new();
    private VisionTrackingSnapshot _tracking = VisionTrackingSnapshot.Empty;
    private long _trackingVersion;

    public bool MarkerFound => ReadTrackingSnapshot().RawMarkerFound;
    public volatile bool AttackIndicator;
    public volatile bool EHeld;
    public volatile bool FHeld;
    public volatile bool OrangeParry;
    public volatile int ParryCount;
    public volatile string GuardDir = "-";
    public volatile bool Flash;
    public volatile int LoopHz;
    public volatile string LastError = "";
    public volatile int ScreenWidth;
    public volatile int ScreenHeight;
    public int Ax => ReadTrackingSnapshot().Anchor.X;
    public int Ay => ReadTrackingSnapshot().Anchor.Y;
    public int Box => ReadTrackingSnapshot().Box;
    public double B55 => ReadTrackingSnapshot().Geometry.B55;
    public double Y55 => ReadTrackingSnapshot().Geometry.Y55;
    public double X2 => ReadTrackingSnapshot().Geometry.X2;
    public double Y2 => ReadTrackingSnapshot().Geometry.Y2;
    public double X3 => ReadTrackingSnapshot().Geometry.X3;
    public double Y3 => ReadTrackingSnapshot().Geometry.Y3;
    public double X4 => ReadTrackingSnapshot().Geometry.X4;
    public double Y4 => ReadTrackingSnapshot().Geometry.Y4;
    public double X5 => ReadTrackingSnapshot().Geometry.X5;
    public double Y5 => ReadTrackingSnapshot().Geometry.Y5;
    public double X6 => ReadTrackingSnapshot().Geometry.X6;
    public double Y6 => ReadTrackingSnapshot().Geometry.Y6;
    public double X7 => ReadTrackingSnapshot().Geometry.X7;
    public double Y7 => ReadTrackingSnapshot().Geometry.Y7;
    public double X8 => ReadTrackingSnapshot().Geometry.AnchorScan.Left;
    public double Y8 => ReadTrackingSnapshot().Geometry.AnchorScan.Top;
    public double X9 => ReadTrackingSnapshot().Geometry.AnchorScan.Right;
    public double Y9 => ReadTrackingSnapshot().Geometry.AnchorScan.Bottom;
    public double X16 => ReadTrackingSnapshot().Geometry.X16;
    public double Y16 => ReadTrackingSnapshot().Geometry.Y16;
    public double X17 => ReadTrackingSnapshot().Geometry.X17;
    public double Y17 => ReadTrackingSnapshot().Geometry.Y17;
    public double X18 => ReadTrackingSnapshot().Geometry.BoxScan.Left;
    public double Y18 => ReadTrackingSnapshot().Geometry.BoxScan.Top;
    public double X19 => ReadTrackingSnapshot().Geometry.BoxScan.Right;
    public double Y19 => ReadTrackingSnapshot().Geometry.BoxScan.Bottom;

    private readonly ManualResetEventSlim _paused = new(true);
    private CancellationTokenSource _cts = new();
    private readonly object _visionSync = new();
    private readonly object _observationSync = new();
    private CombatObservation _latestObservation;
    // Serializes pause/stop transitions with the coordinator portion of each frame.
    private readonly object _combatStateSync = new();
    private readonly TelemetryRecorder _telemetry = new();
    private readonly ReactionCoordinator _reactionCoordinator = new();
    private readonly IInputGateway _input;
    private readonly VisionAnalyzer _visionAnalyzer = new();
    private readonly IParryRollSource _parryRolls;
    private readonly IOrangeLightDirectionSource _orangeLightDirections;
    private readonly DirectionalActionExecutor _actions;
    private readonly AutoGuardController _autoGuard;
    private long _reactionWaitTick;
    private long _lastTelemetryHeartbeatTick;
    private long _lastAnchorChangedTick;
    private string _reactionWaitKind = "";
    private bool _waitImageCaptured;
    private int _lastRedMatchCount;
    private string _lastClosestRed = "";
    private int _lastCaptureDurationMs;
    private readonly OutgoingOrangeGuard _outgoingOrangeGuard = new();
    private OutgoingOrangeGuardResult _outgoingOrangeState = new(false, false, "", false, false, 0, false, false, false);
    private bool _lastSourceHeavyHeld;
    private bool _lastSourceLightHeld;
    private bool _staleOrangeReported;
    private long _reactionDisplayUntil;
    private int _indicatorX = -1;
    private int _indicatorY = -1;
    private string _reactionState = "SEARCHING";
    private string _reactionReason = "Waiting for an anchor";
    private string _reactionDirection = "";
    private VisionSnapshot _vision = new();
    private ScreenFrame _frame = new();
    private Thread _thread;
    private int _stopRequested;
    private int _disposed;

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
    VisionTrackingSnapshot IAutomationHost.GetTrackingSnapshot(long observationTimestamp) =>
        ReadTrackingSnapshot().At(observationTimestamp);
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
        _telemetry.Start(label);
        _telemetry.Record("runtime-settings", new { resolution = new { S.Res1, S.Res2 }, S.GuardHold, S.Pause3, S.ParryDelay, S.Legit, S.LegitParryChance, S.BulwarkFallback, S.CrushingFallbackChance, S.DeflectFallbackChance, S.OrangeLight, outgoingOrangeSuppressionWindowMs = OutgoingOrangeGuard.SuppressionWindowMs });
    }

    public void StopTelemetry()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _telemetry.Stop();
    }

    public bool ExportTelemetry(IWin32Window owner, out string result) => _telemetry.ExportLatest(owner, out result);

    public VisionSnapshot GetVisionSnapshot()
    {
        lock (_visionSync) return _vision;
    }

    internal VisionTrackingSnapshot GetTrackingSnapshot() => ReadTrackingSnapshot();

    /// <summary>
    /// Publishes resolution geometry as one snapshot. A known anchor is
    /// rebased with the new scalers, while its last-seen timestamp is kept.
    /// </summary>
    public void ConfigureResolution(int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        long now = Environment.TickCount64;
        lock (_trackingSync)
        {
            VisionTrackingSnapshot previous = _tracking;
            VisionGeometry geometry = VisionGeometry.CreateResolution(width, height);
            if (previous.Anchor.X >= 0 && previous.LastMarkerKind != null && previous.LastMarkerKind != "NONE")
                geometry = geometry.WithAnchor(previous.Anchor, previous.Box, previous.LastMarkerKind);

            _tracking = VisionTrackingSnapshot.Create(
                Interlocked.Increment(ref _trackingVersion),
                now,
                false,
                previous.LastMarkerKind,
                previous.Anchor,
                previous.Box,
                geometry,
                previous.LastSeenMs,
                previous.AnchorDeltaX,
                previous.AnchorDeltaY,
                previous.LastMarkerKind);
            ScreenWidth = width;
            ScreenHeight = height;
        }
        lock (_observationSync) _latestObservation = null;
        AttackIndicator = false;
        Flash = false;
        _indicatorX = -1;
        _indicatorY = -1;
        PublishVision();
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

    private VisionTrackingSnapshot ReadTrackingSnapshot() { lock (_trackingSync) return _tracking; }

    private CombatObservation ReadLatestObservation()
    {
        lock (_observationSync) return _latestObservation;
    }

    private void ClearLatestObservation()
    {
        lock (_observationSync) _latestObservation = null;
        AttackIndicator = false;
        Flash = false;
        _indicatorX = -1;
        _indicatorY = -1;
    }

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
                    ScreenWidth = _frame.Width;
                    ScreenHeight = _frame.Height;
                    Calculate();
                    CombatObservation observation = CaptureCombatObservation();
                    UpdateVisionTracking();
                    if (!_input.IsReady)
                    {
                        LastError = "Requested ViGEm input is unavailable; reactions are paused.";
                        AbortCombatState("input-unavailable", true);
                        LoopHz = (int)(1000.0 / Math.Max(1.0, sw.Elapsed.TotalMilliseconds));
                        RecordTelemetryHeartbeat();
                        PublishVision();
                        Sleep(100);
                        continue;
                    }
                    if (LastError.StartsWith("Requested ViGEm input", StringComparison.Ordinal)) LastError = "";
                    ProcessCombatObservation(observation);
                    LoopHz = (int)(1000.0 / Math.Max(1.0, sw.Elapsed.TotalMilliseconds));
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
        VisionTrackingSnapshot tracking = ReadTrackingSnapshot().At(now);
        bool eHeld = IsEHeld();
        bool ltHeld = _input.HoldButtonHeld();
        bool fHeld = _input.IsDown(Input.VK_F) || ltHeld;
        VisionAnalysisResult result = _visionAnalyzer.Scan(_frame, new VisionScanRequest(
            now,
            tracking,
            Screen.PrimaryScreen.Bounds,
            eHeld,
            fHeld,
            ltHeld,
            _input.IsReady,
            _input.PhysicalHeavyAttackHeld(),
            _input.PhysicalLightAttackHeld(),
            _telemetry.IsRecording));

        CombatObservation observation = result.Observation;
        VisionTrackingSnapshot currentTracking = ReadTrackingSnapshot();
        if (currentTracking.Version != tracking.Version)
        {
            // Resolution publication won the race while this frame was being
            // analyzed. Do not publish or process pixels measured against the
            // old ROI; the next capture will use the new atomic geometry.
            ClearLatestObservation();
            VisionTrackingSnapshot rebased = currentTracking.At(now);
            return new CombatObservation(now, rebased.RawMarkerFound, rebased.Anchor, rebased.Box,
                rebased.Geometry.CombatRoiRectangle, false, new Point(-1, -1), CombatDirection.None,
                false, false, false, false, eHeld, fHeld, ltHeld, _input.IsReady,
                _input.PhysicalHeavyAttackHeld(), _input.PhysicalLightAttackHeld(), rebased);
        }
        AttackIndicator = observation.HasIndicator;
        _indicatorX = observation.Indicator.X;
        _indicatorY = observation.Indicator.Y;
        Flash = observation.LightFlash;
        if (_telemetry.IsRecording)
        {
            _lastRedMatchCount = result.RedProbe.MatchCount;
            _lastClosestRed = result.RedProbe.ClosestRgb;
        }
        lock (_observationSync) _latestObservation = observation;
        return observation;
    }

    private void ProcessCombatObservation(CombatObservation observation)
    {
        lock (_combatStateSync)
        {
            if (!ReactionActive()) return;
            VisionTrackingSnapshot currentTracking = ReadTrackingSnapshot();
            if (observation.Tracking != null && observation.Tracking.Version != currentTracking.Version)
                return;
            ProcessCombatObservationCore(observation);
        }
    }

    private void ProcessCombatObservationCore(CombatObservation observation)
    {
        bool freshTracking = observation.RawMarkerFrame && observation.UsableTracking;
        bool staleOrange = !freshTracking && (observation.OrangeIndicator || observation.OrangeFeint);
        if (staleOrange && !_staleOrangeReported)
        {
            RecordTelemetry("orange-stale-rejected", new
            {
                indicator = observation.OrangeIndicator,
                feint = observation.OrangeFeint,
                markerAgeMs = observation.MarkerAgeMs,
                trackingVersion = observation.TrackingVersion,
                roi = observation.CombatRoi
            });
            _staleOrangeReported = true;
        }
        else if (!staleOrange)
        {
            _staleOrangeReported = false;
        }
        // A stale ROI is useful only for an already-armed directional
        // candidate. It must never enter the orange priority/action path.
        CombatObservation orangeObservation = freshTracking
            ? observation
            : observation with { OrangeIndicator = false, OrangeFeint = false };
        OutgoingOrangeGuardResult outgoingOrange = _outgoingOrangeGuard.Observe(
            observation.TimestampMs,
            freshTracking,
            orangeObservation.OrangeIndicator,
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
                orangeIndicator = orangeObservation.OrangeIndicator,
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
                orangeIndicator = orangeObservation.OrangeIndicator,
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

        _actions.ProcessOrangeObservation(orangeObservation, outgoingOrange.SuppressesOrange);
        if (!observation.EHeld && !observation.FHeld)
            _actions.CancelPendingAction("hold-released");
        CombatObservation effectiveObservation = outgoingOrange.SuppressesOrange
            ? orangeObservation with { OrangeIndicator = false, OrangeFeint = false }
            : orangeObservation;
        bool orangePriority = ReactionPolicy.OrangeHasPriority(effectiveObservation, S, _actions.IsBusy);
        (ReactionCommandKind kind, string hold) = orangePriority ? (ReactionCommandKind.None, "") : ResolveReactionCommand(effectiveObservation);
        CoordinatorTick tick = _reactionCoordinator.Tick(effectiveObservation with
        {
            HasIndicator = S.Autoblock && observation.HasIndicator
        }, kind, hold);

        if (!string.IsNullOrEmpty(tick.Transition))
        {
            RecordTelemetry("candidate-" + tick.Transition, new
            {
                id = tick.Candidate?.Id,
                direction = tick.Candidate?.Direction.ToString(),
                indicator = observation.Indicator,
                box = observation.Box,
                markerAgeMs = observation.MarkerAgeMs,
                trackingStale = observation.StaleTracking,
                trackingVersion = observation.TrackingVersion
            });
            if (tick.Candidate != null && (tick.Transition.StartsWith("armed", StringComparison.Ordinal) || tick.Transition.StartsWith("replaced", StringComparison.Ordinal)))
            {
                RecordTelemetry("indicator-classified", new
                {
                    classification = tick.Candidate.Direction.ToString().ToUpperInvariant(),
                    x = observation.Indicator.X,
                    y = observation.Indicator.Y,
                    matches = _lastRedMatchCount,
                    closestRgb = _lastClosestRed,
                    box = observation.Box,
                    markerAgeMs = observation.MarkerAgeMs,
                    trackingUsable = observation.UsableTracking,
                    trackingStale = observation.StaleTracking,
                    trackingVersion = observation.TrackingVersion
                });
                _telemetry.CaptureRoi("indicator-" + tick.Candidate.Direction, observation.CombatRoi);
                SetVisionReaction("GUARD", "Current classified red indicator", DirectionName(tick.Candidate.Direction), 900);
            }
            if (tick.Transition.Contains("replaced", StringComparison.Ordinal))
                _actions.CancelPendingAction("candidate-replaced");
        }
        if (!string.IsNullOrEmpty(tick.CancellationReason))
        {
            RecordTelemetry("candidate-cancelled", new
            {
                reason = tick.CancellationReason,
                markerAgeMs = observation.MarkerAgeMs,
                trackingUsable = observation.UsableTracking,
                trackingStale = observation.StaleTracking,
                trackingVersion = observation.TrackingVersion
            }, true);
            _telemetry.CaptureRoi("candidate-" + tick.CancellationReason, observation.CombatRoi);
            SetVisionReaction("REACTION CANCELLED", tick.CancellationReason, "", 800);
            _actions.CancelPendingAction(tick.CancellationReason);
        }
        if (tick.IgnoredStaleFlash)
        {
            RecordTelemetry("flash-ignored-stale", new
            {
                observation.CombatRoi,
                observation.Box,
                trackingUsable = observation.UsableTracking,
                trackingStale = observation.StaleTracking,
                markerAgeMs = observation.MarkerAgeMs,
                trackingVersion = observation.TrackingVersion,
                direction = observation.Direction.ToString()
            });
            _telemetry.CaptureRoi("flash-ignored-stale", observation.CombatRoi);
        }
        if (tick.StaleDirectionMismatch)
        {
            RecordTelemetry("stale-direction-rejected", new
            {
                candidateId = tick.Candidate?.Id,
                candidateDirection = tick.Candidate?.Direction.ToString(),
                observedDirection = observation.Direction.ToString(),
                markerAgeMs = observation.MarkerAgeMs,
                trackingVersion = observation.TrackingVersion
            });
        }
        if (tick.StaleCandidateSuppressed)
        {
            RecordTelemetry("stale-candidate-suppressed", new
            {
                candidateId = tick.Candidate?.Id,
                candidateDirection = tick.Candidate?.Direction.ToString(),
                observedDirection = observation.Direction.ToString(),
                markerAgeMs = observation.MarkerAgeMs,
                trackingVersion = observation.TrackingVersion
            });
        }

        ApplyCoordinatorGuard(tick.Candidate);
        if (observation.RawMarkerFrame && observation.UsableTracking && observation.HasIndicator && observation.Direction == CombatDirection.None)
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
                markerAgeMs = observation.MarkerAgeMs,
                trackingStale = observation.StaleTracking,
                trackingVersion = observation.TrackingVersion
            });
            _telemetry.CaptureRoi("flash-accepted", observation.CombatRoi);

            if (command.Kind == ReactionCommandKind.Deflect &&
                command.Hold == "F" &&
                ReactionPolicy.IsNuxiaTopDeflectSuppressed(S, command.Direction))
            {
                RecordTelemetry("deflect-direction-suppressed", new
                {
                    candidateId = command.CandidateId,
                    hero = "Nuxia",
                    direction = command.Direction.ToString(),
                    path = "direct-deflect",
                    reason = "nuxia-top-deflect-disabled",
                    finalOutcome = "BLOCK",
                    markerAgeMs = observation.MarkerAgeMs,
                    trackingVersion = observation.TrackingVersion
                });
                _telemetry.CaptureRoi("deflect-direction-suppressed", observation.CombatRoi);
                SetVisionReaction("BLOCK ONLY · NUXIA TOP",
                    "Auto deflect suppressed; auto guard retained", DirectionName(command.Direction), 1100);
                return;
            }

            _actions.QueueReaction(command);
        }
    }

    private (ReactionCommandKind Kind, string Hold) ResolveReactionCommand(CombatObservation observation)
    {
        ReactionSelection selection = ReactionPolicy.ResolveCommand(observation, S);
        return (selection.Kind, selection.Hold);
    }

    private void AbortCombatState(string reason, bool forceAction)
    {
        lock (_combatStateSync)
        {
            _reactionCoordinator.Cancel(reason);
            _actions.CancelPendingAction(reason, forceAction);
            ReleaseAutoGuard();
            _input.ReleaseAutomationInputs();
        }
    }

    public string DebugScan()
    {
        var f = ScreenCapture.Capture(null);
        VisionGeometry geometry = ReadTrackingSnapshot().Geometry;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Screen captured: {f.Width}x{f.Height}");
        sb.AppendLine($"Search region: ({geometry.AnchorScan.Left:0},{geometry.AnchorScan.Top:0})-({geometry.AnchorScan.Right:0},{geometry.AnchorScan.Bottom:0})");
        sb.AppendLine($"Scalers: B55={geometry.B55:0.###} Y55={geometry.Y55:0.###}");
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

        int sx = Math.Max(0, Math.Min((int)geometry.AnchorScan.Left, f.Width - 1));
        int ex = Math.Max(0, Math.Min((int)geometry.AnchorScan.Right, f.Width - 1));
        int sy = Math.Max(0, Math.Min((int)geometry.AnchorScan.Top, f.Height - 1));
        int ey = Math.Max(0, Math.Min((int)geometry.AnchorScan.Bottom, f.Height - 1));

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

        int cx = (int)((geometry.AnchorScan.Left + geometry.AnchorScan.Right) / 2);
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

    private bool IsReactionWaiting => Volatile.Read(ref _reactionWaitTick) != 0;

    private long ReactionWaitMilliseconds
    {
        get
        {
            long started = Volatile.Read(ref _reactionWaitTick);
            return started == 0 ? 0 : Math.Max(0, Environment.TickCount64 - started);
        }
    }

    private Rectangle CombatRoiRectangle() => ReadTrackingSnapshot().Geometry.CombatRoiRectangle;

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
        VisionTrackingSnapshot currentTracking = ReadTrackingSnapshot().At(now);
        CombatObservation observation = ReadLatestObservation();
        bool coherentObservation = observation?.Tracking != null &&
            observation.Tracking.Version == currentTracking.Version;
        VisionTrackingSnapshot tracking = coherentObservation
            ? observation.Tracking.At(now)
            : currentTracking;
        VisionGeometry geometry = tracking.Geometry;
        InputBridgeSnapshot bridge = ViGEmInput.GetDiagnostics();
        ReactionCandidate candidate = _reactionCoordinator.CurrentCandidate;
        _telemetry.Record("heartbeat", new
        {
            marker = new
            {
                found = tracking.RawMarkerFound,
                rawMarkerFound = tracking.RawMarkerFound,
                trackingUsable = tracking.TrackingUsable,
                trackingStale = tracking.TrackingStale,
                kind = tracking.MarkerKind,
                lastKind = tracking.LastMarkerKind,
                x = tracking.Anchor.X,
                y = tracking.Anchor.Y,
                deltaX = tracking.AnchorDeltaX,
                deltaY = tracking.AnchorDeltaY,
                // Compatibility: ageMs historically measured time since the
                // anchor last moved. markerAgeMs is the additive raw-marker
                // freshness age used by the temporal tracking plan.
                ageMs = _lastAnchorChangedTick == 0 ? 0 : Math.Max(0, now - _lastAnchorChangedTick),
                markerAgeMs = tracking.MarkerAgeMs,
                lastSeenMs = tracking.LastSeenMs,
                trackingVersion = tracking.Version,
                anchorMovementAgeMs = _lastAnchorChangedTick == 0 ? -1 : Math.Max(0, now - _lastAnchorChangedTick)
            },
            box = tracking.Box,
            roi = geometry.CombatRoiRectangle,
            zones = new
            {
                top = new { X2 = geometry.X2, Y2 = geometry.Y2, X3 = geometry.X3, Y3 = geometry.Y3 },
                left = new { X6 = geometry.X6, Y6 = geometry.Y6, X7 = geometry.X7, Y7 = geometry.Y7 },
                right = new { X4 = geometry.X4, Y4 = geometry.Y4, X5 = geometry.X5, Y5 = geometry.Y5 }
            },
            indicator = new
            {
                present = coherentObservation && observation.HasIndicator,
                x = coherentObservation ? observation.Indicator.X : -1,
                y = coherentObservation ? observation.Indicator.Y : -1,
                matches = _lastRedMatchCount,
                closestRgb = _lastClosestRed
            },
            reaction = new
            {
                state = _reactionState,
                worker = _actions.State,
                candidateId = candidate?.Id ?? 0,
                candidateAgeMs = candidate == null ? 0 : now - candidate.StartedMs,
                lastValidAgeMs = candidate == null ? 0 : now - candidate.LastValidMs,
                candidateDirection = candidate?.Direction.ToString() ?? "NONE",
                EHeld = coherentObservation ? observation.EHeld : EHeld,
                FHeld = coherentObservation ? observation.FHeld : FHeld,
                ltHeld = coherentObservation ? observation.LtHeld : _input.HoldButtonHeld(),
                Flash = coherentObservation && observation.LightFlash
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
            captureMs = _lastCaptureDurationMs
        });
    }

    private void CapturePrimaryFrame()
    {
        if (!_telemetry.IsRecording)
        {
            _frame = ScreenCapture.Capture(_frame);
            return;
        }
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _frame = ScreenCapture.Capture(_frame);
        _lastCaptureDurationMs = (int)stopwatch.ElapsedMilliseconds;
    }

    private void UpdateVisionTracking()
    {
        VisionTrackingSnapshot tracking = ReadTrackingSnapshot().At(Environment.TickCount64);
        if (!tracking.TrackingUsable)
        {
            _reactionState = "SEARCHING";
            _reactionReason = tracking.MarkerAgeMs >= 0
                ? "Marker age exceeded the 150ms tracking window"
                : "Waiting for a green or yellow anchor";
            _reactionDirection = "";
        }
        else if (tracking.TrackingStale)
        {
            _reactionState = "TRACKING STALE";
            _reactionReason = "Marker missing; preserving only the short tracking grace";
            _reactionDirection = "";
        }
        else if (!tracking.RawMarkerFound)
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
        VisionTrackingSnapshot currentTracking = ReadTrackingSnapshot().At(now);
        CombatObservation observation = ReadLatestObservation();
        bool coherentObservation = observation?.Tracking != null &&
            observation.Tracking.Version == currentTracking.Version;
        VisionTrackingSnapshot tracking = coherentObservation
            ? observation.Tracking.At(now)
            : currentTracking;
        VisionGeometry geometry = tracking.Geometry;
        bool frameIndicator = coherentObservation && observation.HasIndicator;
        Point frameIndicatorPoint = coherentObservation ? observation.Indicator : new Point(-1, -1);
        bool frameFlash = coherentObservation && observation.LightFlash;

        var snapshot = new VisionSnapshot
        {
            Running = IsRunning,
            MarkerFound = tracking.RawMarkerFound,
            RawMarkerFound = tracking.RawMarkerFound,
            TrackingUsable = tracking.TrackingUsable,
            TrackingStale = tracking.TrackingStale,
            MarkerKind = tracking.MarkerKind,
            Anchor = tracking.Anchor,
            AnchorScan = geometry.AnchorScan,
            CombatRoi = geometry.CombatRoi,
            TopZone = geometry.TopZone,
            LeftZone = geometry.LeftZone,
            RightZone = geometry.RightZone,
            AttackIndicator = frameIndicator,
            Indicator = frameIndicatorPoint,
            GuardDirection = GuardDir,
            DecisionDirection = _reactionDirection,
            ReactionState = _reactionState,
            ReactionReason = _reactionReason,
            Flash = frameFlash,
            LoopHz = LoopHz,
            Box = tracking.Box,
            AnchorAgeMs = tracking.RawMarkerFound && _lastAnchorChangedTick > 0
                ? Math.Max(0, now - _lastAnchorChangedTick)
                : 0,
            MarkerAgeMs = tracking.MarkerAgeMs,
            LastMarkerSeenMs = tracking.LastSeenMs,
            TrackingVersion = tracking.Version,
            AnchorDeltaX = tracking.AnchorDeltaX,
            AnchorDeltaY = tracking.AnchorDeltaY,
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
        VisionTrackingSnapshot previous;
        VisionTrackingSnapshot current;
        lock (_trackingSync)
        {
            // Resolution changes publish through the same lock. Keeping the
            // scan and publication in one critical section prevents a frame
            // calculated with the old scalers from clobbering a newer atomic
            // geometry snapshot.
            long now = Environment.TickCount64;
            previous = _tracking;
            VisionGeometry geometry = previous.Geometry;
            ScreenWidth = _frame.Width;
            ScreenHeight = _frame.Height;

            RectangleF boxScan = geometry.BoxScan;
            int scannedBox = CurrentPx(boxScan.Left, boxScan.Top, boxScan.Right, boxScan.Bottom,
                5, 131, 65, 0, out _, out _) ? 1 : 2;
            int box = previous.Box;

            Point anchor = previous.Anchor;
            string markerKind = previous.LastMarkerKind;
            bool rawMarkerFound = false;
            RectangleF anchorScan = geometry.AnchorScan;
            if (CurrentPx(anchorScan.Left, anchorScan.Top, anchorScan.Right, anchorScan.Bottom,
                5, 131, 65, 0, out int ax, out int ay))
            {
                anchor = new Point(ax, ay);
                markerKind = "GREEN";
                rawMarkerFound = true;
            }
            else if (CurrentPx(anchorScan.Left, anchorScan.Top, anchorScan.Right, anchorScan.Bottom,
                255, 255, 10, 0, out ax, out ay))
            {
                anchor = new Point(ax, ay);
                markerKind = "YELLOW";
                rawMarkerFound = true;
            }

            // A new Box is valid only when this frame also contains a fresh
            // anchor. Marker-loss frames retain the complete last-valid
            // Box/anchor/geometry tuple instead of mixing a new Box with old
            // anchor-relative coordinates.
            if (rawMarkerFound)
            {
                box = scannedBox;
                geometry = geometry.WithAnchor(anchor, box, markerKind);
            }

            int deltaX = rawMarkerFound && previous.RawMarkerFound ? anchor.X - previous.Anchor.X : 0;
            int deltaY = rawMarkerFound && previous.RawMarkerFound ? anchor.Y - previous.Anchor.Y : 0;
            long lastSeenMs = rawMarkerFound ? now : previous.LastSeenMs;
            string lastMarkerKind = rawMarkerFound ? markerKind : previous.LastMarkerKind;
            current = VisionTrackingSnapshot.Create(
                Interlocked.Increment(ref _trackingVersion),
                now,
                rawMarkerFound,
                markerKind,
                anchor,
                box,
                geometry,
                lastSeenMs,
                deltaX,
                deltaY,
                lastMarkerKind);

            _tracking = current;
        }
        ObserveAnchorTracking(previous, current);
    }

    private void ObserveAnchorTracking(VisionTrackingSnapshot previous, VisionTrackingSnapshot current)
    {
        long now = Environment.TickCount64;
        if (!current.RawMarkerFound)
        {
            if (previous.RawMarkerFound)
            {
                RecordTelemetry("marker-lost", new
                {
                    oldAx = previous.Anchor.X,
                    oldAy = previous.Anchor.Y,
                    oldBox = previous.Box,
                    markerAgeMs = current.MarkerAgeMs,
                    lastSeenMs = current.LastSeenMs,
                    trackingUsable = current.TrackingUsable,
                    trackingVersion = current.Version
                }, true);
                _telemetry.CaptureRoi("marker-lost", current.Geometry.CombatRoiRectangle);
            }
            return;
        }

        int deltaX = current.AnchorDeltaX;
        int deltaY = current.AnchorDeltaY;
        int distance = Math.Max(Math.Abs(deltaX), Math.Abs(deltaY));
        if (!previous.RawMarkerFound)
        {
            _lastAnchorChangedTick = now;
            RecordTelemetry("marker-found", new { kind = current.MarkerKind, x = current.Anchor.X, y = current.Anchor.Y, box = current.Box, trackingVersion = current.Version });
            _telemetry.CaptureRoi("marker-found", current.Geometry.CombatRoiRectangle);
            if (previous.LastSeenMs >= 0)
            {
                RecordTelemetry("marker-recovered", new
                {
                    kind = current.MarkerKind,
                    x = current.Anchor.X,
                    y = current.Anchor.Y,
                    box = current.Box,
                    markerAgeMs = current.MarkerAgeMs,
                    trackingVersion = current.Version
                });
                _telemetry.CaptureRoi("marker-recovered", current.Geometry.CombatRoiRectangle);
            }
        }
        else if (distance > 2)
        {
            _lastAnchorChangedTick = now;
            if (distance >= 40)
            {
                RecordTelemetry("anchor-jump", new { x = current.Anchor.X, y = current.Anchor.Y, deltaX, deltaY, distance, box = current.Box, trackingVersion = current.Version }, true);
                _telemetry.CaptureRoi("anchor-jump", current.Geometry.CombatRoiRectangle);
            }
        }
        if (previous.Box != current.Box)
        {
            RecordTelemetry("box-flip", new { from = previous.Box, to = current.Box, x = current.Anchor.X, y = current.Anchor.Y, trackingVersion = current.Version }, true);
            _telemetry.CaptureRoi("box-flip", current.Geometry.CombatRoiRectangle);
        }
    }

    private bool YourChar(string name) => ReactionPolicy.IsYourChar(S, name);

    private bool HasEAction() => ReactionPolicy.HasEAction(S);

    private bool HasFAction() => ReactionPolicy.HasFAction(S);

    private bool HasHeroAction() => ReactionPolicy.HasHeroAction(S);

}
