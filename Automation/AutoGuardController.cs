using System.Drawing;
using HappyBot.Combat;
using HappyBot.Infrastructure.Input;

namespace HappyBot.Automation;

/// <summary>
/// Owns the short-lived directional guard and its watchdog timer.  Candidate
/// lifecycle remains in ReactionCoordinator; this class only translates a
/// candidate direction to the legacy guard key and guarantees release on every
/// replacement, expiry, pause, and stop path.
/// </summary>
internal sealed class AutoGuardController : IDisposable
{
    private readonly IInputGateway _input;
    private readonly Func<Settings> _settings;
    private readonly Func<bool> _reactionActive;
    private readonly Func<ReactionCandidate> _currentCandidate;
    private readonly Func<bool> _isReactionWaiting;
    private readonly Func<long> _reactionWaitMilliseconds;
    private readonly Func<Rectangle> _combatRoi;
    private readonly Action<string, object, bool> _recordTelemetry;
    private readonly Action<string, Rectangle> _captureRoi;
    private readonly Action<string> _setDirection;
    private readonly object _sync = new();
    private readonly System.Threading.Timer _releaseTimer;
    private long _releaseTick;
    private long _pressedTick;
    private int _activeGuardKey;
    private long _lastRenewTelemetryTick;
    private bool _disposed;

    public AutoGuardController(
        IInputGateway input,
        Func<Settings> settings,
        Func<bool> reactionActive,
        Func<ReactionCandidate> currentCandidate,
        Func<bool> isReactionWaiting,
        Func<long> reactionWaitMilliseconds,
        Func<Rectangle> combatRoi,
        Action<string, object, bool> recordTelemetry,
        Action<string, Rectangle> captureRoi,
        Action<string> setDirection)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _reactionActive = reactionActive ?? throw new ArgumentNullException(nameof(reactionActive));
        _currentCandidate = currentCandidate ?? throw new ArgumentNullException(nameof(currentCandidate));
        _isReactionWaiting = isReactionWaiting ?? throw new ArgumentNullException(nameof(isReactionWaiting));
        _reactionWaitMilliseconds = reactionWaitMilliseconds ?? throw new ArgumentNullException(nameof(reactionWaitMilliseconds));
        _combatRoi = combatRoi ?? throw new ArgumentNullException(nameof(combatRoi));
        _recordTelemetry = recordTelemetry ?? throw new ArgumentNullException(nameof(recordTelemetry));
        _captureRoi = captureRoi ?? throw new ArgumentNullException(nameof(captureRoi));
        _setDirection = setDirection ?? throw new ArgumentNullException(nameof(setDirection));
        _releaseTimer = new System.Threading.Timer(_ => ReleaseWhenDue(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public int ActiveGuardKey
    {
        get { lock (_sync) return _activeGuardKey; }
    }

    public long PressedTick
    {
        get { lock (_sync) return _pressedTick; }
    }

    public long ReleaseTick
    {
        get { lock (_sync) return _releaseTick; }
    }

    public long RemainingMilliseconds
    {
        get { lock (_sync) return Math.Max(0, _releaseTick - Environment.TickCount64); }
    }

    public void Apply(ReactionCandidate candidate)
    {
        int key = GuardKey(candidate?.Direction);
        lock (_sync)
        {
            if (key == 0)
            {
                if (_activeGuardKey != 0) ReleaseLocked("candidate-cleared");
                _setDirection("-");
                return;
            }

            _setDirection(DirectionName(candidate.Direction));
            int holdMs = Math.Max(60, _settings().GuardHold);
            if (_activeGuardKey == key && _pressedTick != 0)
            {
                RenewLocked(holdMs);
                long now = Environment.TickCount64;
                if (now - _lastRenewTelemetryTick >= 100)
                {
                    _lastRenewTelemetryTick = now;
                    _recordTelemetry("guard-renew", new { key, holdMs, candidateId = candidate.Id }, false);
                }
                return;
            }

            ReleaseLocked("replace");
            _releaseTick = Environment.TickCount64 + holdMs;
            _pressedTick = Environment.TickCount64;
            _activeGuardKey = key;
            _input.KeyDown(key);
            _releaseTimer.Change(holdMs, Timeout.Infinite);
            _recordTelemetry("guard-down", new
            {
                key,
                holdMs,
                releaseDeadlineMs = _releaseTick,
                bridge = _input.Diagnostics
            }, false);
        }
    }

    public void Release(string reason)
    {
        lock (_sync) ReleaseLocked(reason);
    }

    /// <summary>Re-establishes the keyboard guard after a directional light.</summary>
    public void RestoreAfterDirectionalLight()
    {
        if (_input.UsesControllerBridge) return;
        lock (_sync)
        {
            if (_activeGuardKey == 0 || !_reactionActive()) return;
            int holdMs = Math.Max(60, _settings().GuardHold);
            _input.KeyDown(_activeGuardKey);
            _releaseTick = Environment.TickCount64 + holdMs;
            _releaseTimer.Change(holdMs, Timeout.Infinite);
            _recordTelemetry("guard-restored-after-orange-light", new { key = _activeGuardKey, holdMs }, false);
        }
    }

    private void ReleaseWhenDue()
    {
        lock (_sync)
        {
            if (_disposed) return;
            long remaining = _releaseTick - Environment.TickCount64;
            if (remaining > 0)
            {
                _releaseTimer.Change((int)Math.Min(remaining, int.MaxValue), Timeout.Infinite);
                return;
            }

            if (_currentCandidate() != null && _reactionActive() && _settings().Autoblock)
            {
                int holdMs = Math.Max(60, _settings().GuardHold);
                RenewLocked(holdMs);
                _recordTelemetry("guard-watchdog-renew", new { holdMs, candidateId = _currentCandidate().Id }, false);
                return;
            }
            ReleaseLocked("expiry");
        }
    }

    private void RenewLocked(int holdMs)
    {
        _releaseTick = Environment.TickCount64 + holdMs;
        _releaseTimer.Change(holdMs, Timeout.Infinite);
    }

    private void ReleaseLocked(string reason)
    {
        long heldMs = _pressedTick == 0 ? 0 : Environment.TickCount64 - _pressedTick;
        int previousKey = _activeGuardKey;
        bool waiting = _isReactionWaiting();
        _releaseTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _releaseTick = 0;
        _pressedTick = 0;
        _activeGuardKey = 0;
        _input.KeyUp(HappyBot.Input.VK_NUMPAD4);
        _input.KeyUp(HappyBot.Input.VK_NUMPAD6);
        _input.KeyUp(HappyBot.Input.VK_NUMPAD8);
        if (heldMs <= 0) return;

        _recordTelemetry("guard-release", new
        {
            reason,
            heldMs,
            waitingForFlash = waiting,
            waitMs = _reactionWaitMilliseconds(),
            bridge = _input.Diagnostics
        }, reason == "expiry" && waiting);
        if (waiting && reason == "expiry")
        {
            _recordTelemetry("guard-expired-waiting", new
            {
                heldMs,
                waitMs = _reactionWaitMilliseconds(),
                guard = DirectionNameFromKey(previousKey)
            }, true);
            _captureRoi("guard-expired-waiting", _combatRoi());
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseLocked("dispose");
        }
        _releaseTimer.Dispose();
    }

    private static int GuardKey(CombatDirection? direction) => direction switch
    {
        CombatDirection.Left => HappyBot.Input.VK_NUMPAD4,
        CombatDirection.Right => HappyBot.Input.VK_NUMPAD6,
        CombatDirection.Top => HappyBot.Input.VK_NUMPAD8,
        _ => 0
    };

    private static string DirectionName(CombatDirection direction) => direction switch
    {
        CombatDirection.Left => "LFT",
        CombatDirection.Right => "RGT",
        CombatDirection.Top => "TOP",
        _ => "-"
    };

    private static string DirectionNameFromKey(int key) => key switch
    {
        HappyBot.Input.VK_NUMPAD4 => "LFT",
        HappyBot.Input.VK_NUMPAD6 => "RGT",
        HappyBot.Input.VK_NUMPAD8 => "TOP",
        _ => "-"
    };
}
