namespace HappyBot;

public sealed class BotCore
{
    private const int VisionStageMinimumMs = 50;
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
    public volatile bool ParryToggle = true;
    public volatile bool OrangeParry;
    public volatile int ParryCount;
    public volatile string GuardDir = "-";
    public volatile bool Flash;
    public volatile int LoopHz;
    public volatile string LastError = "";
    public volatile bool DodgeEnabled = true;
    public volatile int ScreenWidth;
    public volatile int ScreenHeight;
    public int Ax;
    public int Ay;
    public int Box;

    private readonly ManualResetEventSlim _paused = new(true);
    private CancellationTokenSource _cts = new();
    private readonly System.Threading.Timer _releaseTimer;
    private readonly System.Threading.Timer _releTimer;
    private readonly System.Threading.Timer _guardReleaseTimer;
    private readonly object _visionSync = new();
    private readonly object _guardSync = new();
    private readonly object _actionSync = new();
    private readonly TelemetryRecorder _telemetry = new();
    private readonly ReactionCoordinator _reactionCoordinator = new();
    private readonly IParryRollSource _parryRolls;
    private int _ubp;
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
    private bool _flashSeenWhileWaiting;
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
    private bool _orangeMustClear;
    private long _reactionDisplayUntil;
    private string _markerKind = "NONE";
    private int _indicatorX = -1;
    private int _indicatorY = -1;
    private string _reactionState = "SEARCHING";
    private string _reactionReason = "Waiting for an anchor";
    private string _reactionDirection = "";
    private ParryDecision _latestParryDecision;
    private VisionSnapshot _vision = new();
    private ScreenFrame _frame = new();
    private Thread _thread;

    public BotCore() : this(RandomParryRollSource.Instance)
    {
    }

    internal BotCore(IParryRollSource parryRolls)
    {
        _parryRolls = parryRolls ?? throw new ArgumentNullException(nameof(parryRolls));
        _releaseTimer = new System.Threading.Timer(_ => Volatile.Write(ref _ubp, 0), null, Timeout.Infinite, Timeout.Infinite);
        _releTimer = new System.Threading.Timer(_ => DodgeEnabled = true, null, Timeout.Infinite, Timeout.Infinite);
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
        CancelPendingAction("shutdown", true);
        _reactionCoordinator.Cancel("shutdown");
        _paused.Set();
        _thread?.Join();
        ReleaseAutoGuard();
        Input.ReleaseAutomationInputs();
    }

    public void TogglePause()
    {
        if (_paused.IsSet)
        {
            _reactionCoordinator.Cancel("paused");
            CancelPendingAction("paused", true);
            ReleaseAutoGuard();
            Input.ReleaseAutomationInputs();
            _paused.Reset();
        }
        else _paused.Set();
    }

    public void ScheduleRele() => _releTimer.Change(9000, Timeout.Infinite);

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
        _telemetry.Start(label);
        _telemetry.Record("runtime-settings", new { resolution = new { S.Res1, S.Res2 }, S.GuardHold, S.Pause3, S.ParryDelay, S.Legit, S.LegitParryChance });
    }

    public void StopTelemetry() => _telemetry.Stop();

    public bool ExportTelemetry(IWin32Window owner, out string result) => _telemetry.ExportLatest(owner, out result);

    public VisionSnapshot GetVisionSnapshot()
    {
        lock (_visionSync) return _vision;
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
                        _reactionCoordinator.Cancel("input-unavailable");
                        CancelPendingAction("input-unavailable", true);
                        LoopHz = (int)(1000.0 / Math.Max(1.0, sw.Elapsed.TotalMilliseconds));
                        ApplyCoordinatorGuard(null);
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
                    Thread.Sleep(250);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool Px(double x1, double y1, double x2, double y2, int r, int g, int b, int variation, out int px, out int py)
    {
        return _frame.ScreenPixelSearch(x1, y1, x2, y2, r, g, b, variation, out px, out py);
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
        if (!MarkerFound)
        {
            AttackIndicator = false;
            _indicatorX = _indicatorY = -1;
            return new CombatObservation(now, false, new Point(Ax, Ay), Box, CombatRoiRectangle(), false,
                new Point(-1, -1), CombatDirection.None, false, false, false, false, eHeld, fHeld, ltHeld, Input.IsReady);
        }

        Rectangle roi = CombatRoiRectangle();
        Rectangle screen = Screen.PrimaryScreen.Bounds;
        roi = Rectangle.Intersect(roi, screen);
        if (roi.Width <= 0 || roi.Height <= 0)
            return new CombatObservation(now, true, new Point(Ax, Ay), Box, roi, false,
                new Point(-1, -1), CombatDirection.None, false, false, false, false, eHeld, fHeld, ltHeld, Input.IsReady);

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
            darkRed, lightFlash, orange, orangeFeint, eHeld, fHeld, ltHeld, Input.IsReady);
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
        ProcessOrangeObservation(observation);
        if (!observation.EHeld && !observation.FHeld)
            CancelPendingAction("hold-released");
        bool orangePriority = (S.Unblockables && observation.OrangeIndicator) || IsActionBusy;
        (ReactionCommandKind kind, string hold) = orangePriority ? (ReactionCommandKind.None, "") : ResolveReactionCommand(observation);
        CoordinatorTick tick = _reactionCoordinator.Tick(observation with
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
            RecordTelemetry("flash-accepted", new { candidateId = tick.Command.CandidateId, direction = tick.Command.Direction.ToString(), kind = tick.Command.Kind.ToString() });
            _telemetry.CaptureRoi("flash-accepted", observation.CombatRoi);
            if (tick.Command.Kind == ReactionCommandKind.Parry)
            {
                ParryDecision decision = ParryDecision.Create(tick.Command, S.Legit, S.LegitParryChance, _parryRolls);
                _latestParryDecision = decision;
                RecordTelemetry("legit-parry-decision", new
                {
                    candidateId = decision.CandidateId,
                    hold = decision.Hold,
                    direction = decision.Direction.ToString(),
                    chancePercent = decision.ChancePercent,
                    roll = decision.Roll,
                    outcome = decision.Outcome,
                    legitEnabled = decision.LegitEnabled
                });
                if (!decision.ShouldParry)
                {
                    SetVisionReaction("BLOCK ONLY", $"Legit {decision.ChancePercent}% roll {decision.Roll}: block", DirectionName(decision.Direction), 1100);
                    return;
                }
            }
            QueueDirectionalAction(tick.Command);
        }
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
                SendDeflect(command.Direction);
                SetVisionReaction("DEFLECT SENT", "F hold + flash gate", DirectionName(command.Direction), 1300);
            }
            else if (command.Kind == ReactionCommandKind.Hero)
            {
                await ExecuteHeroActionAsync(command, token);
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
        return !token.IsCancellationRequested && holdStillDown && ReactionActive() && _reactionCoordinator.IsCurrent(command.CandidateId);
    }

    private void SendDeflect(CombatDirection direction)
    {
        WithBlock(() =>
        {
            Input.KeyUp(Input.VK_W);
            Input.KeyUp(Input.VK_S);
            Input.KeyUp(Input.VK_A);
            Input.KeyUp(Input.VK_D);
            if (direction == CombatDirection.Left) Input.KeyDown(Input.VK_LEFT);
            else if (direction == CombatDirection.Right) Input.KeyDown(Input.VK_RIGHT);
            else Input.KeyDown(Input.VK_UP);
            Input.KeyTap(Input.VK_SPACE);
            if (direction == CombatDirection.Left) Input.KeyUp(Input.VK_LEFT);
            else if (direction == CombatDirection.Right) Input.KeyUp(Input.VK_RIGHT);
            else Input.KeyUp(Input.VK_UP);
        });
    }

    private async Task ExecuteHeroActionAsync(ReactionCommand command, CancellationToken token)
    {
        if (!CanCommitAction(command, token)) return;
        _actionCommitted = true;
        if (YourChar("Blackprior")) Input.KeyTap(Input.VK_NUMPAD9);
        else if (YourChar("Warlord")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_LBUTTON); }
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

    private void ProcessOrangeObservation(CombatObservation observation)
    {
        if (!S.Unblockables) return;
        if (!observation.OrangeIndicator)
        {
            _orangeMustClear = false;
            return;
        }
        long now = observation.TimestampMs;
        Interlocked.Exchange(ref _orangeLastSeen, now);
        if (_orangeMustClear) return;
        if (observation.OrangeFeint)
        {
            _orangeFeintLastSeen = now;
            SetVisionReaction("ORANGE PARRY WINDOW", "Red feint indicator detected", "", 900);
            return;
        }
        bool afterFeint = _orangeFeintLastSeen != 0;
        int delay = afterFeint ? S.Pause1 : S.Pause;
        if (now - _orangeLastActionTick < Math.Max(250, delay + 150)) return;
        _orangeFeintLastSeen = 0;
        _orangeLastActionTick = now;
        _orangeMustClear = true;
        QueueOrangeAction(afterFeint, delay);
    }

    private void QueueOrangeAction(bool afterFeint, int delay)
    {
        lock (_actionSync)
        {
            if (!_actionTask.IsCompleted) return;
            _actionCts?.Dispose();
            _actionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _actionCandidateId = 0;
            _actionCommitted = false;
            _actionState = afterFeint ? "ORANGE FEINT" : "ORANGE";
            _actionTask = ExecuteOrangeActionAsync(afterFeint, delay, _actionCts.Token);
        }
    }

    private async Task ExecuteOrangeActionAsync(bool afterFeint, int delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(Math.Max(0, delay), token);
            if (token.IsCancellationRequested || !ReactionActive() || !S.Unblockables ||
                Environment.TickCount64 - Interlocked.Read(ref _orangeLastSeen) > ReactionCoordinator.MissingGraceMs) return;
            _actionCommitted = true;
            if (afterFeint && OrangeParry)
            {
                SetVisionReaction("ORANGE PARRY READY", "Feint check passed", "", 900);
                await Task.Delay(Math.Max(0, S.ParryDelay), token);
                if (!ReactionActive()) return;
                if (Input.MouseClick(Input.VK_RBUTTON))
                {
                    ParryCount++;
                    SetVisionReaction("ORANGE PARRY SENT", "RT input sent", "", 1300);
                }
                else SetVisionReaction("ORANGE PARRY FAILED", "RT input was not delivered", "", 1300);
            }
            else
            {
                await SendOrangeDodgeSequenceAsync(token);
                SetVisionReaction("ORANGE DODGE SENT", afterFeint ? "Orange parry is disabled" : "Orange indicator detected", "", 1300);
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

    private async Task SendOrangeDodgeSequenceAsync(CancellationToken token)
    {
        if (S.Ch("Blackprior") && Input.IsDown(Input.VK_W))
        {
            Input.KeyTap(Input.VK_NUMPAD9);
            return;
        }

        if (Input.IsDown(Input.VK_W))
        {
            WithBlock(() =>
            {
                Input.KeyDown(Input.VK_DOWN);
                Input.KeyTap(Input.VK_SPACE);
                Input.KeyUp(Input.VK_DOWN);
            });
            return;
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

        if (S.Nohero) return;
        if (S.Ch("Nobushi")) Input.KeyTap(Input.VK_C);
        if (S.Ch("Shaman")) { Input.KeyTap(Input.VK_SPACE); Input.KeyTap(Input.VK_NUMPAD5); }
        if (S.Ch("Orochi")) { Input.KeyTap(Input.VK_SPACE); Input.KeyTap(Input.VK_NUMPAD9); }
        if (!S.Ch("Jiangjun")) return;
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

    private bool FreshIndicatorPx(int r, int g, int b, int variation, out int px, out int py)
    {
        px = py = 0;
        int left = (int)Math.Floor(Math.Min(X16, X17));
        int top = (int)Math.Floor(Math.Min(Y16, Y17));
        int right = (int)Math.Ceiling(Math.Max(X16, X17));
        int bottom = (int)Math.Ceiling(Math.Max(Y16, Y17));
        var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        left = Math.Clamp(left, bounds.Left, bounds.Right - 1);
        top = Math.Clamp(top, bounds.Top, bounds.Bottom - 1);
        right = Math.Clamp(right, left + 1, bounds.Right);
        bottom = Math.Clamp(bottom, top + 1, bounds.Bottom);

        CaptureCombatRoi(left, top, right - left, bottom - top);
        ScreenWidth = bounds.Width;
        ScreenHeight = bounds.Height;
        bool found = _frame.PixelSearch(0, 0, _frame.Width - 1, _frame.Height - 1, r, g, b, variation, out int localX, out int localY);
        if (_telemetry.IsRecording)
        {
            ColorProbe probe = _frame.ProbeColor(r, g, b, variation);
            _lastRedMatchCount = probe.MatchCount;
            _lastClosestRed = probe.ClosestRgb;
        }
        else
        {
            _lastRedMatchCount = found ? 1 : 0;
            _lastClosestRed = "";
        }
        if (!found) return false;

        px = localX + left;
        py = localY + top;
        return true;
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
        _flashSeenWhileWaiting = false;
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

    private void CaptureCombatRoi(int left, int top, int width, int height)
    {
        if (!_telemetry.IsRecording)
        {
            _frame = ScreenCapture.Capture(_frame, new Rectangle(left, top, width, height));
            return;
        }
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _frame = ScreenCapture.Capture(_frame, new Rectangle(left, top, width, height));
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
            state.Contains("HERO RESPONSE SENT", StringComparison.OrdinalIgnoreCase))
        {
            _telemetry.CaptureRoi("reaction-" + state, CombatRoiRectangle());
            EndReactionWait("reaction-finished");
        }
        PublishVision();
    }

    private long SetVisionReady(string hold)
    {
        SetVisionReaction("PARRY READY", hold + " hold + flash gate", DirectionName(GuardDir), 900);
        return Environment.TickCount64;
    }

    private static void KeepVisionStageVisible(long started)
    {
        long remaining = VisionStageMinimumMs - (Environment.TickCount64 - started);
        if (remaining > 0) Thread.Sleep((int)remaining);
    }

    private static string DirectionName(string direction) => direction switch
    {
        "LFT" => "LEFT",
        "RGT" => "RIGHT",
        "TOP" => "TOP",
        _ => ""
    };

    private static string DirectionName(CombatDirection direction) => direction switch
    {
        CombatDirection.Left => "LEFT",
        CombatDirection.Right => "RIGHT",
        CombatDirection.Top => "TOP",
        _ => ""
    };

    private string LegitParryStatus
    {
        get
        {
            if (!S.Legit) return "LEGIT OFF";
            ParryDecision decision = _latestParryDecision;
            return decision == null
                ? $"LEGIT {S.LegitParryChance}% WAIT"
                : $"LEGIT {decision.ChancePercent}% {decision.Outcome}";
        }
    }

    private void PublishVision()
    {
        ReactionCandidate candidate = _reactionCoordinator.CurrentCandidate;
        long now = Environment.TickCount64;
        var anchorScan = RectangleF.FromLTRB((float)X8, (float)Y8, (float)X9, (float)Y9);
        var combatRoi = RectangleF.FromLTRB((float)Math.Min(X16, X17), (float)Math.Min(Y16, Y17),
            (float)Math.Max(X16, X17), (float)Math.Max(Y16, Y17));
        // AutoBlock searches only inside CombatRoi, then applies these exact
        // half-plane thresholds. Clip the visual zones to that same ROI.
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

    private void SearchBot()
    {
        if (!S.Unblockables || !MarkerFound) return;
        if (!CurrentPx(X16, Y16, X17, Y17, 246, 98, 8, 0, out int ox, out int oy)) return;
        _indicatorX = ox;
        _indicatorY = oy;
        AttackIndicator = true;
        RecordTelemetry("orange-indicator", new { x = ox, y = oy, box = Box });
        _telemetry.CaptureRoi("orange-indicator", CombatRoiRectangle());
        SetVisionReaction("ORANGE DETECTED", "Unblockable indicator inside combat ROI", "", 800);

        if (CurrentPx(X16, Y16, X17, Y17, 255, 34, 28, 3, out int rx, out int ry))
        {
            _indicatorX = rx;
            _indicatorY = ry;
            Volatile.Write(ref _ubp, 1);
            _releaseTimer.Change(1000, Timeout.Infinite);
            SetVisionReaction("ORANGE PARRY WINDOW", "Red feint indicator detected", "", 900);
            UbParry();
            return;
        }

        if (!DodgeEnabled) return;

        if (S.Nohero)
        {
            Dodge2();
            return;
        }

        if (S.Ch("Blackprior")) Dodge1();
        else Dodge2();
        if (S.Ch("Nobushi")) Dodge4();
        if (S.Ch("Shaman")) Dodge5();
        if (S.Ch("Orochi")) Dodge6();
        if (S.Ch("Jiangjun")) Dodge7();
    }

    private void AutoBlock()
    {
        GuardDir = "-";
        AttackIndicator = false;
        _indicatorX = -1;
        _indicatorY = -1;
        if (!S.Autoblock || !MarkerFound) return;
        if (!FreshIndicatorPx(255, 49, 41, 2, out int zx, out int zy)) return;
        AttackIndicator = true;
        _indicatorX = zx;
        _indicatorY = zy;

        string classification = zx > X4 && zy > Y4 ? "RIGHT" :
            zx < X7 && zy > Y4 ? "LEFT" :
            zy > Y2 && zy < Y3 ? "TOP" : "UNKNOWN";
        RecordTelemetry("indicator-classified", new { classification, x = zx, y = zy, matches = _lastRedMatchCount, closestRgb = _lastClosestRed, box = Box });
        _telemetry.CaptureRoi("indicator-" + classification, CombatRoiRectangle());
        if (classification == "RIGHT") { RGT(); return; }
        if (classification == "LEFT") { LFT(); return; }
        if (classification == "TOP") { TOP(); return; }
        GuardDir = "UNKNOWN";
        RecordTelemetry("indicator-unknown", new { x = zx, y = zy, box = Box }, true);
        SetVisionReaction("INDICATOR UNKNOWN", "Red indicator was outside the directional zones", "", 800);
    }

    private void UbParry()
    {
        if (!S.Unblockables)
        {
            Volatile.Write(ref _ubp, 0);
            return;
        }

        while (ReactionActive() && Volatile.Read(ref _ubp) == 1)
        {
            _frame = ScreenCapture.Capture(_frame);
            ScreenWidth = _frame.Width;
            ScreenHeight = _frame.Height;
            if (CurrentPx(X16, Y16, X17, Y17, 255, 34, 28, 3, out _, out _)) continue;

            Sleep(S.Pause1);
            if (!ReactionActive()) return;
            _frame = ScreenCapture.Capture(_frame);
            ScreenWidth = _frame.Width;
            ScreenHeight = _frame.Height;
            if (!CurrentPx(X16, Y16, X17, Y17, 246, 98, 8, 0, out _, out _) || !S.Unblockables) continue;
            if (!DodgeEnabled)
            {
                Volatile.Write(ref _ubp, 0);
                return;
            }

            if (Input.IsDown(Input.VK_W))
            {
                if (S.Ch("Blackprior"))
                {
                    Input.KeyTap(Input.VK_NUMPAD9);
                    SetVisionReaction("ORANGE DODGE SENT", "Black Prior full block response", "", 1300);
                    Sleep(2300);
                    return;
                }

                WithBlock(() =>
                {
                    Input.KeyDown(Input.VK_DOWN);
                    Input.KeyTap(Input.VK_SPACE);
                    Input.KeyUp(Input.VK_DOWN);
                });
                SetVisionReaction("ORANGE DODGE SENT", "Backward dodge response", "", 1300);
                Sleep(700);
                return;
            }

            if (OrangeParry)
            {
                SetVisionReaction("ORANGE PARRY READY", "Feint check passed", "", 900);
                long readyTick = Environment.TickCount64;
                Sleep(S.ParryDelay);
                if (!ReactionActive()) return;
                if (!Input.MouseClick(Input.VK_RBUTTON))
                {
                    SetVisionReaction("ORANGE PARRY FAILED", "RT input was not delivered", "", 1300);
                    return;
                }
                ParryCount++;
                KeepVisionStageVisible(readyTick);
                SetVisionReaction("ORANGE PARRY SENT", "RT input sent", "", 1300);
            }
            else
            {
                Input.KeyTap(Input.VK_SPACE);
                SetVisionReaction("ORANGE DODGE SENT", "Orange parry is disabled", "", 1300);
            }
            Sleep(700);
            return;
        }

        Volatile.Write(ref _ubp, 0);
    }

    private void Dodge1()
    {
        if (!S.Unblockables) return;

        if (S.Unblockables)
        {
            Sleep(S.Pause);
            if (!ReactionActive()) return;
            if (Input.IsDown(Input.VK_W))
            {
                Input.KeyTap(Input.VK_NUMPAD9);
                SetVisionReaction("ORANGE DODGE SENT", "Black Prior full block response", "", 1300);
                Sleep(2000);
                return;
            }
            string direction = SendConfiguredDodge();
            SetVisionReaction("ORANGE DODGE SENT", $"Black Prior {direction} dodge response", "", 1300);
        }

        if (S.DodgeL)
        {
            Sleep(S.Pause2);
            if (!ReactionActive()) return;
            Input.MouseClick(Input.VK_LBUTTON);
        }

        Sleep(500);
    }

    private void Dodge2()
    {
        if (!S.Unblockables || (!S.Nohero &&
            (S.Ch("Nobushi") || S.Ch("Shaman") || S.Ch("Orochi") || S.Ch("Jiangjun")))) return;

        Sleep(S.Pause);
        if (!ReactionActive()) return;
        if (Input.IsDown(Input.VK_W))
        {
            WithBlock(() =>
            {
                Input.KeyDown(Input.VK_DOWN);
                Input.KeyTap(Input.VK_SPACE);
                Input.KeyUp(Input.VK_DOWN);
            });
            SetVisionReaction("ORANGE DODGE SENT", "Backward dodge response", "", 1300);
            Sleep(700);
            return;
        }

        string direction = SendConfiguredDodge();
        SetVisionReaction("ORANGE DODGE SENT", $"Generic {direction} dodge response", "", 1300);

        if (S.DodgeL)
        {
            Sleep(S.Pause2);
            if (!ReactionActive()) return;
            Input.MouseClick(Input.VK_LBUTTON);
        }

        if (S.DodgeH)
        {
            Sleep(S.Pause2);
            if (!ReactionActive()) return;
            Input.MouseClick(Input.VK_RBUTTON);
        }

        if (S.Lightbash)
        {
            Sleep(S.Pause2);
            if (!ReactionActive()) return;
            Input.KeyTap(Input.VK_NUMPAD5);
            Sleep(700);
        }

        Sleep(700);
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

    private void Dodge4()
    {
        if (!S.Unblockables || S.Nohero || !S.Ch("Nobushi")) return;
        Sleep(S.Pause2);
        if (!ReactionActive()) return;
        Input.KeyTap(Input.VK_C);
        SetVisionReaction("ORANGE RESPONSE SENT", "Nobushi hero response", "", 1300);
    }

    private void Dodge5()
    {
        if (!S.Unblockables || S.Nohero || !S.Ch("Shaman")) return;
        Sleep(S.Pause2);
        if (!ReactionActive()) return;
        Input.KeyDown(Input.VK_SPACE);
        Input.KeyTap(Input.VK_NUMPAD5);
        Input.KeyUp(Input.VK_SPACE);
        SetVisionReaction("ORANGE RESPONSE SENT", "Shaman hero response", "", 1300);
        Sleep(2000);
    }

    private void Dodge6()
    {
        if (!S.Unblockables || S.Nohero || !S.Ch("Orochi")) return;
        Sleep(S.Pause2);
        if (!ReactionActive()) return;
        Input.KeyTap(Input.VK_SPACE);
        Input.KeyTap(Input.VK_NUMPAD9);
        SetVisionReaction("ORANGE RESPONSE SENT", "Orochi hero response", "", 1300);
        Sleep(500);
    }

    private void Dodge7()
    {
        if (!S.Unblockables || S.Nohero || !S.Ch("Jiangjun")) return;
        Sleep(S.Pause2);
        if (!ReactionActive()) return;
        Input.KeyDown(Input.VK_C);
        Sleep(250);
        Input.MouseClick(Input.VK_LBUTTON);
        Input.MouseClick(Input.VK_RBUTTON);
        Input.KeyUp(Input.VK_C);
        SetVisionReaction("ORANGE RESPONSE SENT", "Jiang Jun hero response", "", 1300);
        Sleep(2000);
    }

    private bool AttackFlashing()
    {
        RecordReactionWaitProgress();
        double left = Math.Min(X16, X17), top = Math.Min(Y16, Y17);
        double right = Math.Max(X16, X17), bottom = Math.Max(Y16, Y17);
        int w = (int)(right - left), h = (int)(bottom - top);
        if (w > 0 && h > 0)
            CaptureCombatRoi((int)left, (int)top, w, h);
        else
            CapturePrimaryFrame();
        ScreenWidth = _frame.Width;
        ScreenHeight = _frame.Height;
        if (_frame.PixelSearch(0, 0, _frame.Width - 1, _frame.Height - 1, 255, 41, 34, 0, out _, out _))
        {
            if (_flashSeenWhileWaiting)
            {
                RecordTelemetry("flash-lost", new { waitMs = ReactionWaitMilliseconds, kind = _reactionWaitKind });
                _telemetry.CaptureRoi("flash-lost", CombatRoiRectangle());
            }
            _flashSeenWhileWaiting = false;
            Flash = false;
            RecordTelemetry("flash-blocked-red", new { waitMs = ReactionWaitMilliseconds, kind = _reactionWaitKind });
            return false;
        }

        bool lightFlash = _frame.PixelSearch(0, 0, _frame.Width - 1, _frame.Height - 1, 255, 154, 141, 0, out _, out _);
        Flash = lightFlash;
        if (Flash)
        {
            _flashSeenWhileWaiting = true;
            RecordTelemetry("flash-detected", new { waitMs = ReactionWaitMilliseconds, kind = _reactionWaitKind, guardRemainingMs = GuardRemainingMilliseconds });
            _telemetry.CaptureRoi("flash-detected", CombatRoiRectangle());
        }
        return Flash;
    }

    private bool YourChar(string name) => S.YourHero && !S.Nohero && S.Ch(name);

    private bool HasEAction() => S.Parry2 || S.Crushing2;

    private bool HasFAction() => S.Parry || S.Crushing || S.Deflect || HasHeroAction();

    private bool TryPrimaryFReaction()
    {
        if (S.Parry)
        {
            if (GuardDir == "TOP" && YourChar("Warden"))
            {
                Input.MouseClick(Input.VK_LBUTTON);
                Sleep(500);
                Input.MouseClick(Input.VK_LBUTTON);
                SetVisionReaction("CRUSHING SENT", "Warden top counter", DirectionName(GuardDir), 1300);
                Sleep(850);
                return true;
            }

            SendParry("F");
            Sleep(850);
            return true;
        }

        if (S.Crushing)
        {
            Input.MouseClick(Input.VK_LBUTTON);
            SetVisionReaction("CRUSHING SENT", "F hold + flash gate", DirectionName(GuardDir), 1300);
            Sleep(1200);
            return true;
        }

        if (S.Deflect)
        {
            if (GuardDir == "LFT") DeflectLeft();
            else if (GuardDir == "RGT") DeflectRight();
            else DeflectBack();
            return true;
        }

        return false;
    }

    private bool HasHeroAction() =>
        S.YourHero && !S.Nohero &&
        (S.Ch("Blackprior") || S.Ch("Warlord") ||
         S.Ch("Shaman") || S.Ch("Varangian") || S.Ch("Orochi") ||
         S.Ch("Nobushi") || S.Ch("Aramusha") || S.Ch("Jiangjun"));

    private void SendParry(string hold)
    {
        if (!ParryToggle)
        {
            SetVisionReaction("PARRY BLOCKED", "Parry toggle is off", DirectionName(GuardDir), 900);
            return;
        }
        long readyTick = SetVisionReady(hold);
        Sleep(S.ParryDelay);
        if (!ReactionActive())
        {
            SetVisionReaction("PARRY CANCELLED", "Bot paused before input", DirectionName(GuardDir), 900);
            return;
        }
        if (!Input.MouseClick(Input.VK_RBUTTON))
        {
            SetVisionReaction("PARRY FAILED", "RT input was not delivered", DirectionName(GuardDir), 1300);
            return;
        }
        ParryCount++;
        KeepVisionStageVisible(readyTick);
        SetVisionReaction("PARRY SENT", "RT input sent", DirectionName(GuardDir), 1300);
    }

    private void DeflectBack()
    {
        WithBlock(() =>
        {
            Input.KeyUp(Input.VK_W);
            Input.KeyUp(Input.VK_S);
            Input.KeyUp(Input.VK_A);
            Input.KeyUp(Input.VK_D);
            Input.KeyDown(Input.VK_UP);
            Input.KeyTap(Input.VK_SPACE);
            Input.KeyUp(Input.VK_UP);
        });
        SetVisionReaction("DEFLECT SENT", "F hold + flash gate", DirectionName(GuardDir), 1300);
        Sleep(700);
    }

    private void DeflectLeft()
    {
        bool sent = false;
        WithBlock(() =>
        {
            Input.KeyUp(Input.VK_W);
            Input.KeyUp(Input.VK_S);
            Input.KeyUp(Input.VK_A);
            Input.KeyUp(Input.VK_D);
            Sleep(S.Left);
            if (!ReactionActive()) return;
            Input.KeyDown(Input.VK_LEFT);
            Input.KeyTap(Input.VK_SPACE);
            Input.KeyUp(Input.VK_LEFT);
            sent = true;
        });
        if (!sent) return;
        SetVisionReaction("DEFLECT SENT", "Left directional evade", DirectionName(GuardDir), 1300);
        Sleep(700);
    }

    private void DeflectRight()
    {
        bool sent = false;
        WithBlock(() =>
        {
            Input.KeyUp(Input.VK_W);
            Input.KeyUp(Input.VK_S);
            Input.KeyUp(Input.VK_A);
            Input.KeyUp(Input.VK_D);
            Sleep(S.Right);
            if (!ReactionActive()) return;
            Input.KeyDown(Input.VK_RIGHT);
            Input.KeyTap(Input.VK_SPACE);
            Input.KeyUp(Input.VK_RIGHT);
            sent = true;
        });
        if (!sent) return;
        SetVisionReaction("DEFLECT SENT", "Right directional evade", DirectionName(GuardDir), 1300);
        Sleep(700);
    }

    private bool TryHeroFReaction(string direction)
    {
        if (YourChar("Blackprior"))
        {
            Input.KeyTap(Input.VK_NUMPAD9);
            SetVisionReaction("HERO RESPONSE SENT", "Black Prior full block", direction, 1300);
            Sleep(2000);
            return true;
        }
        if (YourChar("Warlord"))
        {
            Input.KeyTap(Input.VK_C);
            Input.MouseClick(Input.VK_LBUTTON);
            SetVisionReaction("HERO RESPONSE SENT", "Warlord counter", direction, 1300);
            Sleep(250);
            return true;
        }
        if (YourChar("Shaman"))
        {
            Input.KeyTap(Input.VK_SPACE);
            Input.KeyTap(Input.VK_NUMPAD5);
            SetVisionReaction("HERO RESPONSE SENT", "Shaman evade and guard break", direction, 1300);
            Sleep(2000);
            return true;
        }
        if (YourChar("Varangian"))
        {
            Input.KeyTap(Input.VK_C);
            Input.MouseClick(Input.VK_RBUTTON);
            SetVisionReaction("HERO RESPONSE SENT", "Varangian counter", direction, 1300);
            Sleep(2000);
            return true;
        }
        if (YourChar("Orochi"))
        {
            Input.KeyTap(Input.VK_SPACE);
            Input.KeyTap(Input.VK_NUMPAD9);
            SetVisionReaction("HERO RESPONSE SENT", "Orochi evade response", direction, 1300);
            Sleep(500);
            return true;
        }
        if (YourChar("Nobushi"))
        {
            Input.KeyTap(Input.VK_C);
            SetVisionReaction("HERO RESPONSE SENT", "Nobushi stance response", direction, 1300);
            Sleep(250);
            return true;
        }
        if (YourChar("Aramusha"))
        {
            Input.KeyTap(Input.VK_C);
            Input.MouseClick(Input.VK_RBUTTON);
            SetVisionReaction("HERO RESPONSE SENT", "Aramusha counter", direction, 1300);
            Sleep(850);
            return true;
        }
        if (!YourChar("Jiangjun")) return false;

        Input.KeyDown(Input.VK_C);
        try
        {
            Sleep(250);
            Input.MouseClick(Input.VK_LBUTTON);
            Input.MouseClick(Input.VK_RBUTTON);
        }
        finally
        {
            Input.KeyUp(Input.VK_C);
        }
        SetVisionReaction("HERO RESPONSE SENT", "Jiang Jun counter", direction, 1300);
        Sleep(250);
        return true;
    }

    private void TOP()
    {
        GuardDir = "TOP";
        Sleep(S.Pause3);
        if (!ReactionActive()) return;
        SetAutoGuard(Input.VK_NUMPAD8);
        SetVisionReaction("GUARD", "Top red indicator detected", "TOP", 900);

        if (IsEHeld() && HasEAction())
        {
            BeginReactionWait("E");
            while (ReactionActive() && IsEHeld())
            {
                if (!AttackFlashing()) continue;
                if (S.Parry2) { SendParry("E"); Sleep(850); return; }
                if (S.Crushing2) { Input.MouseClick(Input.VK_LBUTTON); SetVisionReaction("CRUSHING SENT", "E hold + flash gate", "TOP", 1300); Sleep(1200); return; }
            }
            EndReactionWait("E-released");
        }

        if (!ReactionActive()) return;

        if (IsFHeld() && HasFAction())
        {
            BeginReactionWait("F");
            while (ReactionActive() && IsFHeld())
            {
                if (!AttackFlashing()) continue;
                if (TryPrimaryFReaction()) return;
                if (TryHeroFReaction("TOP")) return;
            }
            EndReactionWait("F-released");
        }
    }

    private void LFT()
    {
        GuardDir = "LFT";
        Sleep(S.Pause3);
        if (!ReactionActive()) return;
        SetAutoGuard(Input.VK_NUMPAD4);
        SetVisionReaction("GUARD", "Left red indicator detected", "LEFT", 900);

        if (IsEHeld() && HasEAction())
        {
            BeginReactionWait("E");
            while (ReactionActive() && IsEHeld())
            {
                if (!AttackFlashing()) continue;
                if (S.Parry2) { SendParry("E"); Sleep(850); return; }
                if (S.Crushing2) { Input.MouseClick(Input.VK_LBUTTON); SetVisionReaction("CRUSHING SENT", "E hold + flash gate", "LEFT", 1300); Sleep(1200); return; }
            }
            EndReactionWait("E-released");
        }

        if (!ReactionActive()) return;

        if (IsFHeld() && HasFAction())
        {
            BeginReactionWait("F");
            while (ReactionActive() && IsFHeld())
            {
                if (!AttackFlashing()) continue;
                if (TryPrimaryFReaction()) return;
                if (TryHeroFReaction("LEFT")) return;
            }
            EndReactionWait("F-released");
        }
    }

    private void RGT()
    {
        GuardDir = "RGT";
        Sleep(S.Pause3);
        if (!ReactionActive()) return;
        SetAutoGuard(Input.VK_NUMPAD6);
        SetVisionReaction("GUARD", "Right red indicator detected", "RIGHT", 900);

        if (IsEHeld() && HasEAction())
        {
            BeginReactionWait("E");
            while (ReactionActive() && IsEHeld())
            {
                if (!AttackFlashing()) continue;
                if (S.Parry2) { SendParry("E"); Sleep(850); return; }
                if (S.Crushing2) { Input.MouseClick(Input.VK_LBUTTON); SetVisionReaction("CRUSHING SENT", "E hold + flash gate", "RIGHT", 1300); Sleep(1200); return; }
            }
            EndReactionWait("E-released");
        }

        if (!ReactionActive()) return;

        if (IsFHeld() && HasFAction())
        {
            BeginReactionWait("F");
            while (ReactionActive() && IsFHeld())
            {
                if (!AttackFlashing()) continue;
                if (TryPrimaryFReaction()) return;
                if (TryHeroFReaction("RIGHT")) return;
            }
            EndReactionWait("F-released");
        }
    }
}
