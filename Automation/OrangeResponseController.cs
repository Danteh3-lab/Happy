using HappyBot.Combat;
using HappyBot.Infrastructure.Input;

namespace HappyBot.Automation;

/// <summary>
/// Owns orange indicator latching, priority, suppression, and feature-level
/// responses. It shares ActionScheduler with reaction actions, so orange can
/// cancel only pre-commit work and never overlap a committed response.
/// </summary>
internal sealed class OrangeResponseController
{
    private readonly IAutomationHost _host;
    private readonly ActionScheduler _scheduler;
    private readonly IOrangeLightDirectionSource _orangeLightDirections;
    private long _orangeFeintLastSeen;
    private long _orangeLastActionTick;
    private long _orangeLastSeen;
    private volatile bool _orangeMustClear;

    public OrangeResponseController(
        IAutomationHost host,
        ActionScheduler scheduler,
        IOrangeLightDirectionSource orangeLightDirections)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _orangeLightDirections = orangeLightDirections ?? throw new ArgumentNullException(nameof(orangeLightDirections));
    }

    public void ProcessObservation(CombatObservation observation, bool suppressOrange)
    {
        Settings settings = _host.Settings;
        if (!settings.Unblockables) return;
        bool freshTracking = observation.RawMarkerFrame && observation.UsableTracking;
        if (OrangeResponseLatch.IsConfirmedClear(freshTracking, observation.OrangeIndicator))
        {
            _orangeMustClear = false;
            Interlocked.Exchange(ref _orangeFeintLastSeen, 0);
            return;
        }
        if (suppressOrange && observation.OrangeIndicator) return;
        // Marker loss is unknown, not a clear frame; preserve the one-response latch.
        if (!freshTracking) return;

        long now = observation.TimestampMs;
        Interlocked.Exchange(ref _orangeLastSeen, now);
        if (observation.OrangeFeint)
        {
            Interlocked.Exchange(ref _orangeFeintLastSeen, now);
            if (_orangeMustClear) return;
            _host.SetVisionReaction("ORANGE PARRY WINDOW", "Red feint indicator detected", "", 900);
            return;
        }
        if (_orangeMustClear) return;
        bool afterFeint = Interlocked.Read(ref _orangeFeintLastSeen) != 0;
        int delay = afterFeint ? settings.Pause1 : settings.Pause;
        if (now - _orangeLastActionTick < Math.Max(250, delay + 150)) return;

        // Orange has priority. The scheduler refuses to cancel committed work.
        _scheduler.CancelPending("orange-priority", false, snapshot => _host.RecordTelemetry("action-cancel-request", new
        {
            reason = "orange-priority",
            candidateId = snapshot.CandidateId,
            state = snapshot.State,
            force = false
        }));
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
        return _scheduler.TrySchedule(0, afterFeint ? "ORANGE FEINT" : "ORANGE",
            token => ExecuteOrangeActionAsync(afterFeint, delay, token),
            snapshot => _host.RecordTelemetry("orange-action-cancelled", new
            {
                afterFeint,
                committed = snapshot.Committed
            }));
    }

    private async Task<bool> ExecuteOrangeActionAsync(bool afterFeint, int delay, CancellationToken token)
    {
        await Task.Delay(Math.Max(0, delay), token);
        if (!CanCommitOrangeAction(token)) return false;
        bool redOrFeint = afterFeint || Interlocked.Read(ref _orangeFeintLastSeen) != 0;
        Settings settings = _host.Settings;
        OrangeResponseKind response = OrangeResponseResolver.Resolve(redOrFeint,
            _host.OrangeParryEnabled, settings.OrangeLight);
        if (response == OrangeResponseKind.Parry)
        {
            _host.SetVisionReaction("ORANGE PARRY READY", "Feint check passed", "", 900);
            await Task.Delay(Math.Max(0, settings.ParryDelay), token);
            if (!_host.OrangeParryEnabled || !CanCommitOrangeAction(token)) return false;
            _scheduler.SetCommitted(true);
            if (!CanCommitOrangeAction(token)) return false;
            if (_host.Input.MouseClick(Input.VK_RBUTTON))
            {
                _host.IncrementParryCount();
                _host.SetVisionReaction("ORANGE PARRY SENT", "RT input sent", "", 1300);
            }
            else _host.SetVisionReaction("ORANGE PARRY FAILED", "RT input was not delivered", "", 1300);
            return true;
        }
        if (response == OrangeResponseKind.Light)
        {
            return SendOrangeLight(token);
        }

        if (!CanCommitOrangeAction(token)) return false;
        OrangeDodgeResult dodgeResult = await SendOrangeDodgeSequenceAsync(token);
        if (dodgeResult == OrangeDodgeResult.Cancelled) return false;
        if (dodgeResult == OrangeDodgeResult.DodgeSent)
            _host.SetVisionReaction("ORANGE DODGE SENT",
                redOrFeint ? "Orange parry is disabled" : "Orange indicator detected", "", 1300);
        return true;
    }

    private bool CanCommitOrangeAction(CancellationToken token)
    {
        long now = Environment.TickCount64;
        var tracking = _host.GetTrackingSnapshot(now);
        return !token.IsCancellationRequested && _host.IsReactionActive &&
            tracking.RawMarkerFound && tracking.TrackingUsable &&
            _host.Settings.Unblockables && _orangeMustClear &&
            !_host.OutgoingOrangeState.SuppressesOrange && _scheduler.IsCurrent(0) &&
            now - Interlocked.Read(ref _orangeLastSeen) <= ReactionCoordinator.MissingGraceMs;
    }

    private bool SendOrangeLight(CancellationToken token)
    {
        if (!CanCommitOrangeLight(token)) return false;
        OrangeLightDecision decision = OrangeLightDecision.Create(_orangeLightDirections);
        _scheduler.SetState("ORANGE LIGHT");
        if (!CanCommitOrangeLight(token)) return false;
        _scheduler.SetCommitted(true);
        if (!CanCommitOrangeLight(token)) return false;
        bool delivered = _host.Input.DirectionalLight(GuardKey(decision.Direction));
        _host.RestoreAutoGuardAfterDirectionalLight();
        string direction = DirectionName(decision.Direction);
        _host.SetVisionReaction(delivered ? "ORANGE LIGHT SENT" : "ORANGE LIGHT FAILED",
            delivered ? "Orange-only indicator -> RB light" : "Directional RB input was not delivered",
            direction, 1300);
        _host.RecordTelemetry("orange-light-decision", new
        {
            direction,
            delivered,
            bridge = _host.Input.Diagnostics
        }, !delivered);
        return true;
    }

    private bool CanCommitOrangeLight(CancellationToken token) =>
        _host.Settings.OrangeLight && CanCommitOrangeAction(token);

    private enum OrangeDodgeResult
    {
        Cancelled,
        DodgeSent,
        BulwarkSent
    }

    private bool SendConfiguredDodge(CancellationToken token)
    {
        Settings settings = _host.Settings;
        if (!settings.Leftdodge && !settings.Rightdodge)
        {
            if (!CanCommitOrangeAction(token)) return false;
            _scheduler.SetCommitted(true);
            if (!CanCommitOrangeAction(token)) return false;
            _host.Input.KeyTap(Input.VK_SPACE);
            return true;
        }
        int direction = settings.Leftdodge ? Input.VK_LEFT : Input.VK_RIGHT;
        IInputGateway input = _host.Input;
        input.Block(true);
        try
        {
            if (!CanCommitOrangeAction(token)) return false;
            _scheduler.SetCommitted(true);
            if (!CanCommitOrangeAction(token)) return false;
            input.KeyDown(direction);
            try
            {
                if (!CanCommitOrangeAction(token)) return false;
                input.KeyTap(Input.VK_SPACE);
                return true;
            }
            finally { input.KeyUp(direction); }
        }
        finally { input.Block(false); }
    }

    private async Task<OrangeDodgeResult> SendOrangeDodgeSequenceAsync(CancellationToken token)
    {
        Settings settings = _host.Settings;
        IInputGateway input = _host.Input;
        if (settings.Ch("Blackprior") && input.MovingForwardHeld())
        {
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            _scheduler.SetState("BULWARK STANCE");
            _host.SetVisionReaction("BULWARK READY", "Orange response: RS down -> 50ms -> RB", "", 900);
            _host.RecordTelemetry("bulwark-ready", new { candidateId = 0, path = "orange", bridge = input.Diagnostics });
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            _scheduler.SetCommitted(true);
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            if (!input.BeginBulwarkStance())
            {
                _host.SetVisionReaction("BULWARK FAILED", "Controller input was not delivered", "", 1300);
                _host.RecordTelemetry("bulwark-failed", new { candidateId = 0, path = "orange", reason = "stance-input", bridge = input.Diagnostics });
                return OrangeDodgeResult.BulwarkSent;
            }
            try
            {
                await Task.Delay(50, token);
                if (!_host.IsReactionActive || !CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
                if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
                if (input.MouseClick(Input.VK_LBUTTON))
                {
                    _host.SetVisionReaction("BULWARK SENT", "Orange response: RS down + RB", "", 1300);
                    _host.RecordTelemetry("bulwark-sent", new { candidateId = 0, path = "orange", bridge = input.Diagnostics });
                }
                else
                {
                    _host.SetVisionReaction("BULWARK FAILED", "RB input was not delivered", "", 1300);
                    _host.RecordTelemetry("bulwark-failed", new { candidateId = 0, path = "orange", reason = "right-shoulder", bridge = input.Diagnostics });
                }
            }
            finally { input.EndBulwarkStance(); }
            return OrangeDodgeResult.BulwarkSent;
        }

        if (input.IsDown(Input.VK_W))
        {
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            input.Block(true);
            try
            {
                _scheduler.SetCommitted(true);
                if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
                input.KeyDown(Input.VK_DOWN);
                try
                {
                    if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
                    input.KeyTap(Input.VK_SPACE);
                }
                finally { input.KeyUp(Input.VK_DOWN); }
            }
            finally { input.Block(false); }
            return OrangeDodgeResult.DodgeSent;
        }

        if (!SendConfiguredDodge(token)) return OrangeDodgeResult.Cancelled;
        if (settings.DodgeL)
        {
            await Task.Delay(Math.Max(0, settings.Pause2), token);
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            input.MouseClick(Input.VK_LBUTTON);
        }
        if (settings.DodgeH)
        {
            await Task.Delay(Math.Max(0, settings.Pause2), token);
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            input.MouseClick(Input.VK_RBUTTON);
        }
        if (settings.Lightbash)
        {
            await Task.Delay(Math.Max(0, settings.Pause2), token);
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            input.KeyTap(Input.VK_NUMPAD5);
        }

        if (settings.Nohero) return OrangeDodgeResult.DodgeSent;
        if (settings.Ch("Nobushi"))
        {
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            input.KeyTap(Input.VK_C);
        }
        if (settings.Ch("Shaman"))
        {
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            input.KeyTap(Input.VK_SPACE);
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            input.KeyTap(Input.VK_NUMPAD5);
        }
        if (settings.Ch("Orochi"))
        {
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            input.KeyTap(Input.VK_SPACE);
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            input.KeyTap(Input.VK_NUMPAD9);
        }
        if (!settings.Ch("Jiangjun")) return OrangeDodgeResult.DodgeSent;
        if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
        _scheduler.SetCommitted(true);
        if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
        input.KeyDown(Input.VK_C);
        try
        {
            await Task.Delay(250, token);
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            input.MouseClick(Input.VK_LBUTTON);
            if (!CanCommitOrangeAction(token)) return OrangeDodgeResult.Cancelled;
            input.MouseClick(Input.VK_RBUTTON);
        }
        finally { input.KeyUp(Input.VK_C); }
        return OrangeDodgeResult.DodgeSent;
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
}
