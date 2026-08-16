namespace HappyBot;

public sealed class BotCore
{
    private const int BulwarkSettleMs = 50;
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
    private readonly System.Threading.Timer _guardReleaseTimer;
    private readonly object _visionSync = new();
    private readonly object _guardSync = new();
    private readonly object _actionSync = new();
    // Serializes pause/stop transitions with the coordinator portion of each frame.
    private readonly object _combatStateSync = new();
    private readonly TelemetryRecorder _telemetry = new();
    private readonly ReactionCoordinator _reactionCoordinator = new();
    private readonly IParryRollSource _parryRolls;
    private readonly IOrangeLightDirectionSource _orangeLightDirections;
    private long _guardReleaseTick;
    private long _guardPressedTick;
    private int _activeGuardKey;
    private long _lastGuardRenewTelemetryTick;
    private long _reactionWaitTick;
    private long _lastTelemetryHeartbeatTick;
    private long _anchorChangedTick;
    private int _anchorDeltaX;
    private int _anchorDeltaY;
    private string _reactionWaitKind = "";
    private bool _waitImageCaptured;
    private int _lastRedMatchCount;
    private string _lastClosestRed = "";
    private int _lastCaptureDurationMs;
    private CancellationTokenSource _actionCts;
    private Task _actionTask = Task.CompletedTask;
    private long _actionCandidateId;
    private bool _actionCommitted;
    private string _actionState = "IDLE";
    private long _orangeFeintLastSeen;
    private long _orangeLastActionTick;
    private long _orangeLastSeen;
    private volatile bool _orangeMustClear;
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
    private ParryDecision _latestParryDecision;
    private ParryOutcome? _latestParryOutcome;
    private VisionSnapshot _vision = new();
    private ScreenFrame _frame = new();
    private Thread _thread;

    public BotCore() : this(RandomParryRollSource.Instance, RandomOrangeLightDirectionSource.Instance)
    {
    }

    internal BotCore(IParryRollSource parryRolls) : this(parryRolls, RandomOrangeLightDirectionSource.Instance)
    {
    }

    internal BotCore(IParryRollSource parryRolls, IOrangeLightDirectionSource orangeLightDirections)
    {
        _parryRolls = parryRolls ?? throw new ArgumentNullException(nameof(parryRolls));
        _orangeLightDirections = orangeLightDirections ?? throw new ArgumentNullException(nameof(orangeLightDirections));
        _guardReleaseTimer = new System.Threading.Timer(_ => ReleaseAutoGuardWhenDue(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsRunning => _thread is { IsAlive: true };
    public bool IsPaused => !_paused.IsSet;
    public TelemetryStatus Telemetry => _telemetry.Status;

    public void Start()
    {
        if (IsRunning) return;
        _paused.Set();
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        lock (_combatStateSync)
        {
            _paused.Set();
            AbortCombatState("shutdown", true);
        }
        _thread?.Join();
        ReleaseAutoGuard();
        Input.ReleaseAutomationInputs();
    }

    public void TogglePause()
    {
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
        // Treat telemetry start as a fresh edge-detection boundary so a held
        // source RT is visible immediately in the new session.
        _lastSourceHeavyHeld = false;
        _lastSourceLightHeld = false;
        _telemetry.Start(label);
        _telemetry.Record("runtime-settings", new { resolution = new { S.Res1, S.Res2 }, S.GuardHold, S.Pause3, S.ParryDelay, S.Legit, S.LegitParryChance, S.BulwarkFallback, S.CrushingFallbackChance, S.DeflectFallbackChance, S.OrangeLight, outgoingOrangeSuppressionWindowMs = OutgoingOrangeGuard.SuppressionWindowMs });
    }

    public void StopTelemetry() => _telemetry.Stop();

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
            BulwarkFallback = autoBlock && settings.Parry && settings.Legit && settings.BulwarkFallback && blackPrior && Input.CanSendBulwark,
            Telemetry = _telemetry.IsRecording
        };
    }

    public void RefreshVisionSnapshot() => PublishVision();

    private static bool IsEHeld() => Input.IsDown(Input.VK_E);

    private static bool IsFHeld() => Input.IsDown(Input.VK_F) || Input.HoldButtonHeld();

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
                    if (!Input.IsReady)
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
        bool eHeld = IsEHeld();
        bool ltHeld = Input.HoldButtonHeld();
        bool fHeld = Input.IsDown(Input.VK_F) || ltHeld;
        bool sourceHeavyHeld = Input.PhysicalHeavyAttackHeld();
        bool sourceLightHeld = Input.PhysicalLightAttackHeld();
        if (!MarkerFound)
        {
            AttackIndicator = false;
            _indicatorX = _indicatorY = -1;
            return new CombatObservation(now, false, new Point(Ax, Ay), Box, CombatRoiRectangle(), false,
                new Point(-1, -1), CombatDirection.None, false, false, false, false, eHeld, fHeld, ltHeld, Input.IsReady, sourceHeavyHeld, sourceLightHeld);
        }

        Rectangle roi = CombatRoiRectangle();
        Rectangle screen = Screen.PrimaryScreen.Bounds;
        roi = Rectangle.Intersect(roi, screen);
        if (roi.Width <= 0 || roi.Height <= 0)
            return new CombatObservation(now, true, new Point(Ax, Ay), Box, roi, false,
                new Point(-1, -1), CombatDirection.None, false, false, false, false, eHeld, fHeld, ltHeld, Input.IsReady, sourceHeavyHeld, sourceLightHeld);

        bool red = _frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1, 255, 49, 41, 2, out int indicatorX, out int indicatorY);
        bool darkRed = _frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1, 255, 41, 34, 0, out _, out _);
        bool lightFlash = _frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1, 255, 154, 141, 0, out _, out _);
        bool orange = _frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1, 246, 98, 8, 0, out _, out _);
        bool orangeFeint = _frame.ScreenPixelSearch(roi.Left, roi.Top, roi.Right - 1, roi.Bottom - 1, 255, 34, 28, 3, out _, out _);
        Point indicator = red ? new Point(indicatorX, indicatorY) : new Point(-1, -1);
        CombatDirection direction = red ? ClassifyDirection(indicator.X, indicator.Y) : CombatDirection.None;

        AttackIndicator = red;
        _indicatorX = indicator.X;
        _indicatorY = indicator.Y;
        Flash = lightFlash;
        if (_telemetry.IsRecording)
        {
            ColorProbe probe = _frame.ProbeColor(roi.Left - _frame.OriginX, roi.Top - _frame.OriginY,
                roi.Right - 1 - _frame.OriginX, roi.Bottom - 1 - _frame.OriginY, 255, 49, 41, 2);
            _lastRedMatchCount = probe.MatchCount;
            _lastClosestRed = probe.ClosestRgb;
        }

        return new CombatObservation(now, true, new Point(Ax, Ay), Box, roi, red, indicator, direction,
            darkRed, lightFlash, orange, orangeFeint, eHeld, fHeld, ltHeld, Input.IsReady, sourceHeavyHeld, sourceLightHeld);
    }

    private CombatDirection ClassifyDirection(int x, int y)
    {
        if (x > X4 && y > Y4) return CombatDirection.Right;
        if (x < X7 && y > Y4) return CombatDirection.Left;
        if (y > Y2 && y < Y3) return CombatDirection.Top;
        return CombatDirection.None;
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

        ProcessOrangeObservation(observation, outgoingOrange.SuppressesOrange);
        if (!observation.EHeld && !observation.FHeld)
            CancelPendingAction("hold-released");
        CombatObservation effectiveObservation = outgoingOrange.SuppressesOrange
            ? observation with { OrangeIndicator = false, OrangeFeint = false }
            : observation;
        bool orangePriority = (S.Unblockables && effectiveObservation.OrangeIndicator) || IsActionBusy;
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
                box = observation.Box
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
                    box = observation.Box
                });
                _telemetry.CaptureRoi("indicator-" + tick.Candidate.Direction, observation.CombatRoi);
                SetVisionReaction("GUARD", "Current classified red indicator", DirectionName(tick.Candidate.Direction), 900);
            }
            if (tick.Transition.Contains("replaced", StringComparison.Ordinal))
                CancelPendingAction("candidate-replaced");
        }
        if (!string.IsNullOrEmpty(tick.CancellationReason))
        {
            RecordTelemetry("candidate-cancelled", new { reason = tick.CancellationReason }, true);
            _telemetry.CaptureRoi("candidate-" + tick.CancellationReason, observation.CombatRoi);
            SetVisionReaction("REACTION CANCELLED", tick.CancellationReason, "", 800);
            CancelPendingAction(tick.CancellationReason);
        }
        if (tick.IgnoredStaleFlash)
        {
            RecordTelemetry("flash-ignored-stale", new { observation.CombatRoi, observation.Box });
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
            RecordTelemetry("flash-accepted", new { candidateId = command.CandidateId, direction = command.Direction.ToString(), kind = command.Kind.ToString() });
            _telemetry.CaptureRoi("flash-accepted", observation.CombatRoi);
            if (command.Kind == ReactionCommandKind.Parry)
            {
                bool bulwarkEligible = S.Autoblock && S.Parry && S.Legit && YourChar("Blackprior") && Input.CanSendBulwark;
                bool crushingEligible = S.Autoblock && S.Parry && S.Crushing && S.Legit;
                bool deflectEligible = S.Autoblock && S.Parry && S.Deflect && S.Legit;
                ParryResolution resolution = ParryResolution.Create(command, S.Legit, S.LegitParryChance, _parryRolls,
                    S.BulwarkFallback, bulwarkEligible, crushingEligible, S.CrushingFallbackChance, _parryRolls,
                    deflectEligible, S.DeflectFallbackChance, _parryRolls);
                ParryDecision decision = resolution.Decision;
                _latestParryDecision = decision;
                _latestParryOutcome = resolution.Outcome;
                RecordTelemetry("legit-parry-decision", new
                {
                    candidateId = decision.CandidateId,
                    hold = decision.Hold,
                    direction = decision.Direction.ToString(),
                    chancePercent = decision.ChancePercent,
                    roll = decision.Roll,
                    outcome = resolution.Outcome.ToString().ToUpperInvariant(),
                    legitEnabled = decision.LegitEnabled,
                    bulwarkFallbackEnabled = S.BulwarkFallback,
                    bulwarkEligible,
                    crushingEligible,
                    deflectEligible,
                    crushingFallbackChance = S.CrushingFallbackChance,
                    deflectFallbackChance = S.DeflectFallbackChance,
                    fallbackRoll = resolution.FallbackRoll,
                    deflectRoll = resolution.DeflectRoll
                });
                if (resolution.Outcome == ParryOutcome.Deflect)
                {
                    string mix = resolution.DeflectRoll is int roll ? $"; deflect {S.DeflectFallbackChance}% roll {roll}" : "";
                    SetVisionReaction("DEFLECT FALLBACK", $"Legit {decision.ChancePercent}% roll {decision.Roll}: dodge{mix}", DirectionName(decision.Direction), 1100);
                    QueueDirectionalAction(command with { Kind = ReactionCommandKind.Deflect });
                    return;
                }
                if (resolution.Outcome == ParryOutcome.Crushing)
                {
                    string mix = DescribeLegitFallbackRolls(resolution);
                    SetVisionReaction("CRUSHING FALLBACK", $"Legit {decision.ChancePercent}% roll {decision.Roll}: RB{mix}", DirectionName(decision.Direction), 1100);
                    QueueDirectionalAction(command with { Kind = ReactionCommandKind.Crushing });
                    return;
                }
                if (resolution.Outcome == ParryOutcome.Bulwark)
                {
                    string mix = DescribeLegitFallbackRolls(resolution);
                    SetVisionReaction("BULWARK FALLBACK", $"Legit {decision.ChancePercent}% roll {decision.Roll}: flip{mix}", DirectionName(decision.Direction), 1100);
                    QueueDirectionalAction(command with { Kind = ReactionCommandKind.Bulwark });
                    return;
                }
                if (resolution.Outcome == ParryOutcome.Block)
                {
                    SetVisionReaction("BLOCK ONLY", $"Legit {decision.ChancePercent}% roll {decision.Roll}: block", DirectionName(decision.Direction), 1100);
                    return;
                }
            }
            QueueDirectionalAction(command);
        }
    }

    private string DescribeLegitFallbackRolls(ParryResolution resolution)
    {
        string detail = resolution.DeflectRoll is int deflectRoll
            ? $"; deflect {S.DeflectFallbackChance}% roll {deflectRoll}"
            : "";
        return resolution.FallbackRoll is int fallbackRoll
            ? detail + $"; fallback {S.CrushingFallbackChance}% roll {fallbackRoll}"
            : detail;
    }

    private (ReactionCommandKind Kind, string Hold) ResolveReactionCommand(CombatObservation observation)
    {
        if (observation.EHeld && HasEAction())
            return S.Parry2 ? (ReactionCommandKind.Parry, "E") : (ReactionCommandKind.Crushing, "E");
        if (!observation.FHeld || !HasFAction()) return (ReactionCommandKind.None, "");
        if (S.Parry) return observation.Direction == CombatDirection.Top && YourChar("Warden")
            ? (ReactionCommandKind.Crushing, "F") : (ReactionCommandKind.Parry, "F");
        if (S.Crushing) return (ReactionCommandKind.Crushing, "F");
        if (S.Deflect) return (ReactionCommandKind.Deflect, "F");
        return HasHeroAction() ? (ReactionCommandKind.Hero, "F") : (ReactionCommandKind.None, "");
    }

    private bool IsActionBusy
    {
        get { lock (_actionSync) return !_actionTask.IsCompleted; }
    }

    private void QueueDirectionalAction(ReactionCommand command)
    {
        lock (_actionSync)
        {
            if (!_actionTask.IsCompleted) return;
            _actionCts?.Dispose();
            _actionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _actionCandidateId = command.CandidateId;
            _actionCommitted = false;
            _actionState = "ARMED " + command.Kind;
            _actionTask = ExecuteDirectionalActionAsync(command, _actionCts.Token);
        }
    }

    private async Task ExecuteDirectionalActionAsync(ReactionCommand command, CancellationToken token)
    {
        try
        {
            if (command.Kind == ReactionCommandKind.Parry)
            {
                SetVisionReaction("PARRY READY", command.Hold + " hold + flash gate", DirectionName(command.Direction), 900);
                await Task.Delay(Math.Max(0, S.ParryDelay), token);
                if (!CanCommitAction(command, token)) return;
                _actionCommitted = true;
                if (!Input.MouseClick(Input.VK_RBUTTON))
                    SetVisionReaction("PARRY FAILED", "RT input was not delivered", DirectionName(command.Direction), 1300);
                else
                {
                    ParryCount++;
                    SetVisionReaction("PARRY SENT", "RT input sent", DirectionName(command.Direction), 1300);
                }
            }
            else if (command.Kind == ReactionCommandKind.Crushing)
            {
                if (!CanCommitAction(command, token)) return;
                _actionCommitted = true;
                Input.MouseClick(Input.VK_LBUTTON);
                SetVisionReaction("CRUSHING SENT", command.Hold + " hold + flash gate", DirectionName(command.Direction), 1300);
            }
            else if (command.Kind == ReactionCommandKind.Deflect)
            {
                int delay = command.Direction == CombatDirection.Left ? S.Left : command.Direction == CombatDirection.Right ? S.Right : 0;
                await Task.Delay(Math.Max(0, delay), token);
                if (!CanCommitAction(command, token)) return;
                _actionCommitted = true;
                if (SendDeflect(command.Direction))
                    SetVisionReaction("DEFLECT SENT", "F hold + flash gate", DirectionName(command.Direction), 1300);
                else
                    SetVisionReaction("DEFLECT FAILED", "Directional dodge input was not delivered", DirectionName(command.Direction), 1300);
            }
            else if (command.Kind == ReactionCommandKind.Hero)
            {
                await ExecuteHeroActionAsync(command, token);
            }
            else if (command.Kind == ReactionCommandKind.Bulwark)
            {
                await ExecuteBulwarkCounterAsync(command, token, "legit-fallback");
            }
            _actionState = "COOLDOWN";
            await Task.Delay(150, token);
        }
        catch (OperationCanceledException)
        {
            RecordTelemetry("action-cancelled", new { candidateId = command.CandidateId, state = _actionState, committed = _actionCommitted });
        }
        finally
        {
            lock (_actionSync)
            {
                if (_actionCandidateId == command.CandidateId)
                {
                    _actionCandidateId = 0;
                    _actionCommitted = false;
                    _actionState = "IDLE";
                }
            }
        }
    }

    private bool CanCommitAction(ReactionCommand command, CancellationToken token)
    {
        bool holdStillDown = command.Hold == "E" ? IsEHeld() : IsFHeld();
        return !token.IsCancellationRequested && holdStillDown && ReactionActive() &&
            IsCommandStillEnabled(command) && _reactionCoordinator.IsCurrent(command.CandidateId);
    }

    private bool IsCommandStillEnabled(ReactionCommand command)
    {
        Settings settings = S;
        if (!settings.Autoblock) return false;
        if (command.RequiresParryEnabled && !(command.Hold == "E" ? settings.Parry2 : settings.Parry)) return false;
        return command.Kind switch
        {
            ReactionCommandKind.Parry => command.Hold == "E" ? settings.Parry2 : settings.Parry,
            ReactionCommandKind.Crushing => command.Hold == "E"
                ? settings.Crushing2
                : settings.Crushing || (settings.Parry && command.Direction == CombatDirection.Top && YourChar("Warden")),
            ReactionCommandKind.Deflect => settings.Deflect,
            ReactionCommandKind.Bulwark => settings.BulwarkFallback && settings.Legit && settings.Parry && YourChar("Blackprior"),
            ReactionCommandKind.Hero => HasHeroAction(),
            _ => false
        };
    }

    private bool SendDeflect(CombatDirection direction)
    {
        bool sent = true;
        WithBlock(() =>
        {
            sent &= Input.KeyUp(Input.VK_W);
            sent &= Input.KeyUp(Input.VK_S);
            sent &= Input.KeyUp(Input.VK_A);
            sent &= Input.KeyUp(Input.VK_D);
            if (direction == CombatDirection.Left) sent &= Input.KeyDown(Input.VK_LEFT);
            else if (direction == CombatDirection.Right) sent &= Input.KeyDown(Input.VK_RIGHT);
            else sent &= Input.KeyDown(Input.VK_UP);
            sent &= Input.KeyTap(Input.VK_SPACE);
            if (direction == CombatDirection.Left) sent &= Input.KeyUp(Input.VK_LEFT);
            else if (direction == CombatDirection.Right) sent &= Input.KeyUp(Input.VK_RIGHT);
            else sent &= Input.KeyUp(Input.VK_UP);
        });
        return sent;
    }

    private async Task ExecuteHeroActionAsync(ReactionCommand command, CancellationToken token)
    {
        if (!CanCommitAction(command, token)) return;
        if (YourChar("Blackprior"))
        {
            await ExecuteBulwarkCounterAsync(command, token, "hero-flash");
            return;
        }

        _actionCommitted = true;
        if (YourChar("Warlord")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_LBUTTON); }
        else if (YourChar("Shaman")) { Input.KeyTap(Input.VK_SPACE); Input.KeyTap(Input.VK_NUMPAD5); }
        else if (YourChar("Varangian")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_RBUTTON); }
        else if (YourChar("Orochi")) { Input.KeyTap(Input.VK_SPACE); Input.KeyTap(Input.VK_NUMPAD9); }
        else if (YourChar("Nobushi")) Input.KeyTap(Input.VK_C);
        else if (YourChar("Aramusha")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_RBUTTON); }
        else if (YourChar("Jiangjun"))
        {
            Input.KeyDown(Input.VK_C);
            try
            {
                await Task.Delay(250, token);
                if (!CanCommitAction(command, token)) return;
                Input.MouseClick(Input.VK_LBUTTON);
                Input.MouseClick(Input.VK_RBUTTON);
            }
            finally
            {
                Input.KeyUp(Input.VK_C);
            }
        }
        else return;
        SetVisionReaction("HERO RESPONSE SENT", "F hold + flash gate", DirectionName(command.Direction), 1300);
    }

    private async Task ExecuteBulwarkCounterAsync(ReactionCommand command, CancellationToken token, string path)
    {
        if (!CanCommitAction(command, token)) return;
        _actionState = "BULWARK STANCE";
        SetVisionReaction("BULWARK READY", "RS down -> 50ms -> RB", DirectionName(command.Direction), 900);
        RecordTelemetry("bulwark-ready", new { candidateId = command.CandidateId, path, bridge = ViGEmInput.GetDiagnostics() });
        if (!Input.BeginBulwarkStance())
        {
            SetVisionReaction("BULWARK FAILED", "Controller input was not delivered; guard remains active", DirectionName(command.Direction), 1300);
            RecordTelemetry("bulwark-failed", new { candidateId = command.CandidateId, path, reason = "stance-input", bridge = ViGEmInput.GetDiagnostics() });
            return;
        }
        try
        {
            await Task.Delay(BulwarkSettleMs, token);
            if (!CanCommitAction(command, token)) return;
            _actionCommitted = true;
            if (Input.MouseClick(Input.VK_LBUTTON))
            {
                SetVisionReaction("BULWARK SENT", "RS down + RB counter", DirectionName(command.Direction), 1300);
                RecordTelemetry("bulwark-sent", new { candidateId = command.CandidateId, path, bridge = ViGEmInput.GetDiagnostics() });
            }
            else
            {
                SetVisionReaction("BULWARK FAILED", "RB input was not delivered; guard remains active", DirectionName(command.Direction), 1300);
                RecordTelemetry("bulwark-failed", new { candidateId = command.CandidateId, path, reason = "right-shoulder", bridge = ViGEmInput.GetDiagnostics() });
            }
        }
        finally
        {
            Input.EndBulwarkStance();
        }
    }

    private void ProcessOrangeObservation(CombatObservation observation, bool suppressOrange)
    {
        if (!S.Unblockables) return;
        if (OrangeResponseLatch.IsConfirmedClear(observation.MarkerFound, observation.OrangeIndicator))
        {
            _orangeMustClear = false;
            Interlocked.Exchange(ref _orangeFeintLastSeen, 0);
            return;
        }
        if (suppressOrange && observation.OrangeIndicator) return;
        // Marker loss produces an unknown frame, not proof the orange indicator
        // cleared. Preserve the one-response latch so the same attack cannot
        // re-arm when the anchor flickers back.
        if (!observation.MarkerFound) return;
        long now = observation.TimestampMs;
        Interlocked.Exchange(ref _orangeLastSeen, now);
        if (observation.OrangeFeint)
        {
            Interlocked.Exchange(ref _orangeFeintLastSeen, now);
            if (_orangeMustClear) return;
            SetVisionReaction("ORANGE PARRY WINDOW", "Red feint indicator detected", "", 900);
            return;
        }
        if (_orangeMustClear) return;
        bool afterFeint = Interlocked.Read(ref _orangeFeintLastSeen) != 0;
        int delay = afterFeint ? S.Pause1 : S.Pause;
        if (now - _orangeLastActionTick < Math.Max(250, delay + 150)) return;
        // Orange has priority. Cancel work that has not issued input yet, then
        // retry on later frames until the worker is free rather than consuming
        // this unblockable without scheduling a response.
        CancelPendingAction("orange-priority");
        // The task may run synchronously when the configured delay is zero, so
        // publish its active state before it is queued and roll it back on refusal.
        _orangeMustClear = true;
        if (!QueueOrangeAction(afterFeint, delay))
        {
            _orangeMustClear = false;
            return;
        }
        _orangeLastActionTick = now;
    }

    private bool QueueOrangeAction(bool afterFeint, int delay)
    {
        lock (_actionSync)
        {
            if (!_actionTask.IsCompleted) return false;
            _actionCts?.Dispose();
            _actionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _actionCandidateId = 0;
            _actionCommitted = false;
            _actionState = afterFeint ? "ORANGE FEINT" : "ORANGE";
            _actionTask = ExecuteOrangeActionAsync(afterFeint, delay, _actionCts.Token);
            return true;
        }
    }

    private async Task ExecuteOrangeActionAsync(bool afterFeint, int delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(Math.Max(0, delay), token);
            if (!CanCommitOrangeAction(token)) return;
            bool redOrFeint = afterFeint || Interlocked.Read(ref _orangeFeintLastSeen) != 0;
            OrangeResponseKind response = OrangeResponseResolver.Resolve(redOrFeint, OrangeParry, S.OrangeLight);
            if (response == OrangeResponseKind.Parry)
            {
                SetVisionReaction("ORANGE PARRY READY", "Feint check passed", "", 900);
                await Task.Delay(Math.Max(0, S.ParryDelay), token);
                if (!OrangeParry || !CanCommitOrangeAction(token)) return;
                _actionCommitted = true;
                if (Input.MouseClick(Input.VK_RBUTTON))
                {
                    ParryCount++;
                    SetVisionReaction("ORANGE PARRY SENT", "RT input sent", "", 1300);
                }
                else SetVisionReaction("ORANGE PARRY FAILED", "RT input was not delivered", "", 1300);
            }
            else if (response == OrangeResponseKind.Light)
            {
                _actionCommitted = true;
                SendOrangeLight();
            }
            else
            {
                if (!CanCommitOrangeAction(token)) return;
                _actionCommitted = true;
                bool handledByBulwark = await SendOrangeDodgeSequenceAsync(token);
                if (!handledByBulwark)
                    SetVisionReaction("ORANGE DODGE SENT", redOrFeint ? "Orange parry is disabled" : "Orange indicator detected", "", 1300);
            }
            _actionState = "COOLDOWN";
            await Task.Delay(150, token);
        }
        catch (OperationCanceledException)
        {
            RecordTelemetry("orange-action-cancelled", new { afterFeint, committed = _actionCommitted });
        }
        finally
        {
            lock (_actionSync)
            {
                _actionCandidateId = 0;
                _actionCommitted = false;
                _actionState = "IDLE";
            }
        }
    }

    private bool CanCommitOrangeAction(CancellationToken token) =>
        !token.IsCancellationRequested && ReactionActive() && MarkerFound && S.Unblockables && _orangeMustClear &&
        !_outgoingOrangeState.SuppressesOrange &&
        Environment.TickCount64 - Interlocked.Read(ref _orangeLastSeen) <= ReactionCoordinator.MissingGraceMs;

    private void SendOrangeLight()
    {
        OrangeLightDecision decision = OrangeLightDecision.Create(_orangeLightDirections);
        _actionState = "ORANGE LIGHT";
        bool delivered = Input.DirectionalLight(GuardKey(decision.Direction));
        RestoreAutoGuardAfterDirectionalLight();
        string direction = DirectionName(decision.Direction);
        SetVisionReaction(delivered ? "ORANGE LIGHT SENT" : "ORANGE LIGHT FAILED",
            delivered ? "Orange-only indicator -> RB light" : "Directional RB input was not delivered", direction, 1300);
        RecordTelemetry("orange-light-decision", new
        {
            direction,
            delivered,
            bridge = ViGEmInput.GetDiagnostics()
        }, !delivered);
    }

    private void RestoreAutoGuardAfterDirectionalLight()
    {
        if (Input.UsesControllerBridge) return;
        lock (_guardSync)
        {
            if (_activeGuardKey == 0 || !ReactionActive()) return;
            int holdMs = Math.Max(60, S.GuardHold);
            Input.KeyDown(_activeGuardKey);
            _guardReleaseTick = Environment.TickCount64 + holdMs;
            _guardReleaseTimer.Change(holdMs, Timeout.Infinite);
            RecordTelemetry("guard-restored-after-orange-light", new { key = _activeGuardKey, holdMs });
        }
    }

    private string SendConfiguredDodge()
    {
        Settings settings = S;
        if (!settings.Leftdodge && !settings.Rightdodge)
        {
            Input.KeyTap(Input.VK_SPACE);
            return "neutral";
        }

        int direction = settings.Leftdodge ? Input.VK_LEFT : Input.VK_RIGHT;
        WithBlock(() =>
        {
            Input.KeyDown(direction);
            try { Input.KeyTap(Input.VK_SPACE); }
            finally { Input.KeyUp(direction); }
        });
        return settings.Leftdodge ? "left" : "right";
    }

    private async Task<bool> SendOrangeDodgeSequenceAsync(CancellationToken token)
    {
        if (S.Ch("Blackprior") && Input.MovingForwardHeld())
        {
            _actionState = "BULWARK STANCE";
            SetVisionReaction("BULWARK READY", "Orange response: RS down -> 50ms -> RB", "", 900);
            RecordTelemetry("bulwark-ready", new { candidateId = 0, path = "orange", bridge = ViGEmInput.GetDiagnostics() });
            if (!Input.BeginBulwarkStance())
            {
                SetVisionReaction("BULWARK FAILED", "Controller input was not delivered", "", 1300);
                RecordTelemetry("bulwark-failed", new { candidateId = 0, path = "orange", reason = "stance-input", bridge = ViGEmInput.GetDiagnostics() });
                return true;
            }
            try
            {
                await Task.Delay(BulwarkSettleMs, token);
                if (!ReactionActive()) return true;
                if (Input.MouseClick(Input.VK_LBUTTON))
                {
                    SetVisionReaction("BULWARK SENT", "Orange response: RS down + RB", "", 1300);
                    RecordTelemetry("bulwark-sent", new { candidateId = 0, path = "orange", bridge = ViGEmInput.GetDiagnostics() });
                }
                else
                {
                    SetVisionReaction("BULWARK FAILED", "RB input was not delivered", "", 1300);
                    RecordTelemetry("bulwark-failed", new { candidateId = 0, path = "orange", reason = "right-shoulder", bridge = ViGEmInput.GetDiagnostics() });
                }
            }
            finally
            {
                Input.EndBulwarkStance();
            }
            return true;
        }

        if (Input.IsDown(Input.VK_W))
        {
            WithBlock(() =>
            {
                Input.KeyDown(Input.VK_DOWN);
                Input.KeyTap(Input.VK_SPACE);
                Input.KeyUp(Input.VK_DOWN);
            });
            return false;
        }

        SendConfiguredDodge();
        if (S.DodgeL)
        {
            await Task.Delay(Math.Max(0, S.Pause2), token);
            Input.MouseClick(Input.VK_LBUTTON);
        }
        if (S.DodgeH)
        {
            await Task.Delay(Math.Max(0, S.Pause2), token);
            Input.MouseClick(Input.VK_RBUTTON);
        }
        if (S.Lightbash)
        {
            await Task.Delay(Math.Max(0, S.Pause2), token);
            Input.KeyTap(Input.VK_NUMPAD5);
        }

        if (S.Nohero) return false;
        if (S.Ch("Nobushi")) Input.KeyTap(Input.VK_C);
        if (S.Ch("Shaman")) { Input.KeyTap(Input.VK_SPACE); Input.KeyTap(Input.VK_NUMPAD5); }
        if (S.Ch("Orochi")) { Input.KeyTap(Input.VK_SPACE); Input.KeyTap(Input.VK_NUMPAD9); }
        if (!S.Ch("Jiangjun")) return false;
        Input.KeyDown(Input.VK_C);
        try
        {
            await Task.Delay(250, token);
            Input.MouseClick(Input.VK_LBUTTON);
            Input.MouseClick(Input.VK_RBUTTON);
        }
        finally
        {
            Input.KeyUp(Input.VK_C);
        }
        return false;
    }

    private void CancelPendingAction(string reason, bool force = false)
    {
        lock (_actionSync)
        {
            if (_actionTask.IsCompleted || (_actionCommitted && !force)) return;
            RecordTelemetry("action-cancel-request", new { reason, candidateId = _actionCandidateId, state = _actionState, force });
            _actionCts?.Cancel();
        }
    }

    private void AbortCombatState(string reason, bool forceAction)
    {
        lock (_combatStateSync)
        {
            _reactionCoordinator.Cancel(reason);
            CancelPendingAction(reason, forceAction);
            ReleaseAutoGuard();
            Input.ReleaseAutomationInputs();
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

    private bool ReactionActive() => !_cts.IsCancellationRequested && _paused.IsSet && Input.IsReady;

    private void SetAutoGuard(int key)
    {
        lock (_guardSync)
        {
            ReleaseAutoGuardLocked("replace");
            int holdMs = Math.Max(60, S.GuardHold);
            _guardReleaseTick = Environment.TickCount64 + holdMs;
            _guardPressedTick = Environment.TickCount64;
            _activeGuardKey = key;
            Input.KeyDown(key);
            _guardReleaseTimer.Change(holdMs, Timeout.Infinite);
            RecordTelemetry("guard-down", new { key, holdMs, releaseDeadlineMs = _guardReleaseTick, bridge = ViGEmInput.GetDiagnostics() });
        }
    }

    private void ReleaseAutoGuard()
    {
        lock (_guardSync) ReleaseAutoGuardLocked("manual-stop");
    }

    private void ReleaseAutoGuardLocked(string reason)
    {
        long heldMs = _guardPressedTick == 0 ? 0 : Environment.TickCount64 - _guardPressedTick;
        bool waiting = IsReactionWaiting;
        _guardReleaseTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _guardReleaseTick = 0;
        _guardPressedTick = 0;
        _activeGuardKey = 0;
        Input.KeyUp(Input.VK_NUMPAD4);
        Input.KeyUp(Input.VK_NUMPAD6);
        Input.KeyUp(Input.VK_NUMPAD8);
        if (heldMs > 0)
        {
            RecordTelemetry("guard-release", new { reason, heldMs, waitingForFlash = waiting, waitMs = ReactionWaitMilliseconds, bridge = ViGEmInput.GetDiagnostics() }, waiting && reason == "expiry");
            if (waiting && reason == "expiry")
            {
                RecordTelemetry("guard-expired-waiting", new { heldMs, waitMs = ReactionWaitMilliseconds, guard = GuardDir }, true);
                _telemetry.CaptureRoi("guard-expired-waiting", CombatRoiRectangle());
            }
        }
    }

    private void ReleaseAutoGuardWhenDue()
    {
        lock (_guardSync)
        {
            long remaining = _guardReleaseTick - Environment.TickCount64;
            if (remaining > 0)
            {
                _guardReleaseTimer.Change((int)Math.Min(remaining, int.MaxValue), Timeout.Infinite);
                return;
            }

            if (_reactionCoordinator.CurrentCandidate != null && ReactionActive() && S.Autoblock)
            {
                int holdMs = Math.Max(60, S.GuardHold);
                _guardReleaseTick = Environment.TickCount64 + holdMs;
                _guardReleaseTimer.Change(holdMs, Timeout.Infinite);
                RecordTelemetry("guard-watchdog-renew", new { holdMs, candidateId = _reactionCoordinator.CurrentCandidate.Id });
                return;
            }
            ReleaseAutoGuardLocked("expiry");
        }
    }

    private void ApplyCoordinatorGuard(ReactionCandidate candidate)
    {
        int key = candidate?.Direction switch
        {
            CombatDirection.Left => Input.VK_NUMPAD4,
            CombatDirection.Right => Input.VK_NUMPAD6,
            CombatDirection.Top => Input.VK_NUMPAD8,
            _ => 0
        };
        lock (_guardSync)
        {
            if (key == 0)
            {
                if (_activeGuardKey != 0) ReleaseAutoGuardLocked("candidate-cleared");
                GuardDir = "-";
                return;
            }

            GuardDir = candidate.Direction switch
            {
                CombatDirection.Left => "LFT",
                CombatDirection.Right => "RGT",
                CombatDirection.Top => "TOP",
                _ => "-"
            };
            int holdMs = Math.Max(60, S.GuardHold);
            if (_activeGuardKey == key && _guardPressedTick != 0)
            {
                _guardReleaseTick = Environment.TickCount64 + holdMs;
                _guardReleaseTimer.Change(holdMs, Timeout.Infinite);
                long now = Environment.TickCount64;
                if (now - _lastGuardRenewTelemetryTick >= 100)
                {
                    _lastGuardRenewTelemetryTick = now;
                    RecordTelemetry("guard-renew", new { key, holdMs, candidateId = candidate.Id });
                }
                return;
            }
            SetAutoGuard(key);
        }
    }

    private static void WithBlock(Action action)
    {
        Input.Block(true);
        try
        {
            action();
        }
        finally
        {
            Input.Block(false);
        }
    }

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
        int left = (int)Math.Floor(Math.Min(X16, X17));
        int top = (int)Math.Floor(Math.Min(Y16, Y17));
        int right = (int)Math.Ceiling(Math.Max(X16, X17));
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
        get
        {
            lock (_guardSync) return Math.Max(0, _guardReleaseTick - Environment.TickCount64);
        }
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
                worker = _actionState,
                candidateId = candidate?.Id ?? 0,
                candidateAgeMs = candidate == null ? 0 : now - candidate.StartedMs,
                lastValidAgeMs = candidate == null ? 0 : now - candidate.LastValidMs,
                candidateDirection = candidate?.Direction.ToString() ?? "NONE",
                EHeld, FHeld, ltHeld = Input.HoldButtonHeld(), Flash
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
            guard = new { direction = GuardDir, remainingMs = GuardRemainingMilliseconds, keyDownTick = _guardPressedTick, releaseDeadlineTick = _guardReleaseTick },
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
            ParryDecision decision = _latestParryDecision;
            return decision == null
                ? $"LEGIT {S.LegitParryChance}% WAIT"
                : $"LEGIT {decision.ChancePercent}% {(_latestParryOutcome?.ToString() ?? decision.Outcome).ToUpperInvariant()}";
        }
    }

    private void PublishVision()
    {
        ReactionCandidate candidate = _reactionCoordinator.CurrentCandidate;
        long now = Environment.TickCount64;
        var anchorScan = RectangleF.FromLTRB((float)X8, (float)Y8, (float)X9, (float)Y9);
        var combatRoi = RectangleF.FromLTRB((float)Math.Min(X16, X17), (float)Math.Min(Y16, Y17),
            (float)Math.Max(X16, X17), (float)Math.Max(Y16, Y17));
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
            ActionWorkerState = _actionState,
            LegitParryStatus = LegitParryStatus,
            TelemetryRecording = _telemetry.IsRecording
        };
        lock (_visionSync) _vision = snapshot;
    }

    private void Calculate()
    {
        bool wasFound = MarkerFound;
        int oldAx = Ax, oldAy = Ay, oldBox = Box;
        ScreenWidth = _frame.Width;
        ScreenHeight = _frame.Height;

        if (CurrentPx(X18, Y18, X19, Y19, 5, 131, 65, 0, out _, out _)) Box = 1;
        else Box = 2;

        if (CurrentPx(X8, Y8, X9, Y9, 5, 131, 65, 0, out int ax, out int ay))
        {
            Ax = ax;
            Ay = ay;
            MarkerFound = true;
            _markerKind = "GREEN";
            if (Box == 2)
                SetCoords(ax - 200 * B55, ay + 20 * Y55, ax + 160 * B55, ay + 170 * Y55,
                          ax + 5 * B55, ay + 195 * Y55, ax + 160 * B55, ay + 430 * Y55,
                          ax - 200 * B55, ay + 195 * Y55, ax - 30 * B55, ay + 430 * Y55,
                          ax - 200 * B55, ay + 20 * Y55, ax + 160 * B55, ay + 430 * Y55);
            else
                SetCoords(ax - 100 * B55, ay + 10 * Y55, ax + 80 * B55, ay + 85 * Y55,
                          ax + 2.5 * B55, ay + 97.5 * Y55, ax + 80 * B55, ay + 227.7 * Y55,
                          ax - 100 * B55, ay + 97.5 * Y55, ax - 15 * B55, ay + 227.7 * Y55,
                          ax - 117.6 * B55, ay + 10 * Y55, ax + 94.11 * B55, ay + 227.7 * Y55);
        }
        else if (CurrentPx(X8, Y8, X9, Y9, 255, 255, 10, 0, out ax, out ay))
        {
            Ax = ax;
            Ay = ay;
            MarkerFound = true;
            _markerKind = "YELLOW";
            if (Box == 2)
                SetCoords(ax - 175 * B55, ay + 65 * Y55, ax + 185 * B55, ay + 185 * Y55,
                          ax + 30 * B55, ay + 215 * Y55, ax + 185 * B55, ay + 430 * Y55,
                          ax - 175 * B55, ay + 215 * Y55, ax - 5 * B55, ay + 430 * Y55,
                          ax - 175 * B55, ay + 65 * Y55, ax + 185 * B55, ay + 430 * Y55);
            else
                SetCoords(ax - 87.5 * B55, ay + 35 * Y55, ax + 92.5 * B55, ay + 92.5 * Y55,
                          ax + 15 * B55, ay + 107.5 * Y55, ax + 92.5 * B55, ay + 215 * Y55,
                          ax - 87.5 * B55, ay + 107.5 * Y55, ax - 2.5 * B55, ay + 215 * Y55,
                          ax - 87.5 * B55, ay + 35 * Y55, ax + 92.5 * B55, ay + 215 * Y55);
        }
        else
        {
            MarkerFound = false;
            _markerKind = "NONE";
        }
        ObserveAnchorTracking(wasFound, oldAx, oldAy, oldBox);
    }

    private void ObserveAnchorTracking(bool wasFound, int oldAx, int oldAy, int oldBox)
    {
        long now = Environment.TickCount64;
        if (!MarkerFound)
        {
            if (wasFound)
            {
                RecordTelemetry("marker-lost", new { oldAx, oldAy, oldBox }, true);
                _telemetry.CaptureRoi("marker-lost", CombatRoiRectangle());
            }
            return;
        }

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

    private bool YourChar(string name) => S.YourHero && !S.Nohero && S.Ch(name);

    private bool HasEAction() => S.Parry2 || S.Crushing2;

    private bool HasFAction() => S.Parry || S.Crushing || S.Deflect || HasHeroAction();

    private bool HasHeroAction() =>
        S.YourHero && !S.Nohero &&
        (S.Ch("Blackprior") || S.Ch("Warlord") ||
         S.Ch("Shaman") || S.Ch("Varangian") || S.Ch("Orochi") ||
         S.Ch("Nobushi") || S.Ch("Aramusha") || S.Ch("Jiangjun"));

}
