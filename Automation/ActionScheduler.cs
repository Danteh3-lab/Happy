namespace HappyBot.Automation;

/// <summary>Immutable scheduler state used by cancellation and telemetry seams.</summary>
internal sealed record ActionSchedulerSnapshot(long CandidateId, bool Committed, string State, bool IsBusy);

/// <summary>
/// Owns the single serialized automation task.  Reaction and orange features
/// share this scheduler so neither can overlap the other, and cancellation
/// can only interrupt work before it has committed input unless forced by
/// pause/stop cleanup.
/// </summary>
internal sealed class ActionScheduler : IDisposable
{
    private const int CooldownMs = 150;
    private readonly CancellationToken _shutdownToken;
    private readonly object _sync = new();
    private Task _task = Task.CompletedTask;
    private CancellationTokenSource _cts;
    private long _operationSequence;
    private long _activeOperation;
    private long _candidateId;
    private bool _committed;
    private string _state = "IDLE";
    private bool _disposed;

    public ActionScheduler(CancellationToken shutdownToken)
    {
        _shutdownToken = shutdownToken;
    }

    public bool IsBusy
    {
        get { lock (_sync) return _activeOperation != 0; }
    }

    public string State
    {
        get { lock (_sync) return _state; }
    }

    public long CandidateId
    {
        get { lock (_sync) return _candidateId; }
    }

    public bool Committed
    {
        get { lock (_sync) return _committed; }
    }

    public bool IsCurrent(long candidateId)
    {
        lock (_sync) return _activeOperation != 0 && _candidateId == candidateId;
    }

    public bool TrySchedule(
        long candidateId,
        string initialState,
        Func<CancellationToken, Task<bool>> worker,
        Action<ActionSchedulerSnapshot> onCancelled = null)
    {
        if (worker == null) throw new ArgumentNullException(nameof(worker));
        lock (_sync)
        {
            if (_disposed || _activeOperation != 0) return false;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            _activeOperation = ++_operationSequence;
            _candidateId = candidateId;
            _committed = false;
            _state = initialState;
            long operation = _activeOperation;
            _task = RunAsync(operation, worker, onCancelled, _cts.Token);
            return true;
        }
    }

    public bool CancelPending(string reason, bool force, Action<ActionSchedulerSnapshot> recordCancellation)
    {
        ActionSchedulerSnapshot snapshot;
        lock (_sync)
        {
            if (_activeOperation == 0 || (_committed && !force)) return false;
            snapshot = SnapshotLocked();
            _cts?.Cancel();
        }
        recordCancellation?.Invoke(snapshot);
        return true;
    }

    public void SetCommitted(bool committed)
    {
        lock (_sync)
        {
            if (!_disposed) _committed = committed;
        }
    }

    public void SetState(string state)
    {
        lock (_sync)
        {
            if (!_disposed) _state = state ?? "IDLE";
        }
    }

    public ActionSchedulerSnapshot Snapshot()
    {
        lock (_sync) return SnapshotLocked();
    }

    private ActionSchedulerSnapshot SnapshotLocked() =>
        new(_candidateId, _committed, _state, _activeOperation != 0);

    private async Task RunAsync(
        long operation,
        Func<CancellationToken, Task<bool>> worker,
        Action<ActionSchedulerSnapshot> onCancelled,
        CancellationToken token)
    {
        try
        {
            bool completed = await worker(token).ConfigureAwait(false);
            if (completed)
            {
                SetState("COOLDOWN");
                await Task.Delay(CooldownMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            onCancelled?.Invoke(Snapshot());
        }
        finally
        {
            lock (_sync)
            {
                if (_activeOperation == operation)
                {
                    _activeOperation = 0;
                    _candidateId = 0;
                    _committed = false;
                    _state = "IDLE";
                }
            }
        }
    }

    public void Dispose()
    {
        Task task;
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            cts = _cts;
            task = _task;
            cts?.Cancel();
        }
        try { task?.Wait(1000); } catch { }
        lock (_sync)
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts?.Dispose();
                _cts = null;
            }
            _candidateId = 0;
            _committed = false;
            _state = "IDLE";
        }
    }
}
