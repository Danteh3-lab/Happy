using HappyBot.Combat;

namespace HappyBot;

/// <summary>
/// Debounced anchor observations for one frame. Tells the caller which
/// telemetry edges fired; geometry updates are applied to the tracker-owned
/// <see cref="CombatGeometry"/> during <see cref="Observe"/>.
/// </summary>
internal readonly record struct AnchorTracking(
    bool BecameFound,
    bool BecameLost,
    bool Jumped,
    int DeltaX,
    int DeltaY,
    int Distance,
    bool BoxFlipped,
    int OldBox);

/// <summary>
/// Owns marker detection history: raw-sample debounce, loss timing, anchor
/// movement, and the missing-grace window. Pure state machine over an
/// injected clock, so every transition is unit-testable.
/// </summary>
internal sealed class AnchorTracker
{
    private const int MarkerSamplePositionTolerancePx = 12;
    private const int MarkerSampleConfirmationFrames = 2;
    private const int MarkerLossDebounceMs = 75;
    private const int PendingMarkerMaximumMs = 250;

    public volatile bool Found;
    public string Kind = "NONE";
    public int DeltaX;
    public int DeltaY;
    public long AnchorChangedTick;

    private long _markerLossStartedTick;
    private long _anchorGraceStartedTick;
    private long _rawMarkerMissingSinceTick;
    private long _pendingMarkerSinceTick;
    private int _pendingMarkerX;
    private int _pendingMarkerY;
    private int _pendingMarkerBox;
    private int _pendingMarkerSamples;
    private string _pendingMarkerKind = "NONE";

    public long MarkerLossAgeMs(long now) =>
        _markerLossStartedTick <= 0 ? 0 : Math.Max(0, now - _markerLossStartedTick);

    public long AnchorGraceAgeMs(long now) =>
        _anchorGraceStartedTick <= 0 ? 0 : Math.Max(0, now - _anchorGraceStartedTick);

    public bool AnchorGraceActive => _anchorGraceStartedTick != 0;

    /// <summary>
    /// Folds one raw marker sample into debounced state, updates loss and
    /// tracking ages, and applies accepted geometry. Returns telemetry edges.
    /// </summary>
    public AnchorTracking Observe(bool wasFound, int oldAx, int oldAy, int oldBox,
        bool rawFound, int rawX, int rawY, string rawKind, int rawBox,
        CombatGeometry geometry, long now, Func<bool> hasLiveCandidate)
    {
        ApplyDebouncedSample(wasFound, rawFound, rawX, rawY, rawKind, rawBox, geometry, now);
        UpdateLossAge(now);
        return Track(wasFound, oldAx, oldAy, oldBox, geometry, now, hasLiveCandidate);
    }

    /// <summary>
    /// The marker search returns the first matching pixel, which can briefly
    /// jump between decorative pixels.  Keep the last accepted geometry until
    /// a new sample has been seen twice in the same small neighborhood.
    /// </summary>
    private void ApplyDebouncedSample(bool wasFound, bool rawFound, int rawX, int rawY,
        string rawKind, int rawBox, CombatGeometry geometry, long now)
    {
        if (!rawFound)
        {
            ClearPendingMarker();
            if (wasFound && _rawMarkerMissingSinceTick == 0)
                _rawMarkerMissingSinceTick = now;

            if (wasFound && now - _rawMarkerMissingSinceTick <= MarkerLossDebounceMs)
            {
                // A one- or two-frame hole must not relocate or drop the ROI.
                Found = true;
                return;
            }

            Found = false;
            Kind = "NONE";
            return;
        }

        _rawMarkerMissingSinceTick = 0;
        int distance = wasFound
            ? Math.Max(Math.Abs(rawX - geometry.Ax), Math.Abs(rawY - geometry.Ay))
            : int.MaxValue;
        bool sameAcceptedMarker = wasFound && rawKind == Kind && rawBox == geometry.Box &&
            distance <= MarkerSamplePositionTolerancePx;
        if (sameAcceptedMarker)
        {
            ClearPendingMarker();
            Accept(rawX, rawY, rawKind, rawBox, geometry);
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
            Accept(_pendingMarkerX, _pendingMarkerY, _pendingMarkerKind, _pendingMarkerBox, geometry);
            ClearPendingMarker();
            return;
        }

        if (wasFound && now - _pendingMarkerSinceTick <= PendingMarkerMaximumMs)
        {
            // Keep scanning the previous ROI while the raw position proves
            // itself.  This prevents an isolated 40+ px jump from dragging the
            // side-indicator search out of range.
            Found = true;
            return;
        }

        Found = false;
        Kind = "NONE";
    }

    private void Accept(int x, int y, string kind, int box, CombatGeometry geometry)
    {
        geometry.ApplyMarker(x, y, kind, box);
        Found = true;
        Kind = kind;
    }

    private void ClearPendingMarker()
    {
        _pendingMarkerSinceTick = 0;
        _pendingMarkerSamples = 0;
        _pendingMarkerKind = "NONE";
    }

    private void UpdateLossAge(long now)
    {
        if (Found)
        {
            _markerLossStartedTick = 0;
            return;
        }
        if (_markerLossStartedTick == 0) _markerLossStartedTick = now;
    }

    private AnchorTracking Track(bool wasFound, int oldAx, int oldAy, int oldBox,
        CombatGeometry geometry, long now, Func<bool> hasLiveCandidate)
    {
        if (!Found)
        {
            _anchorGraceStartedTick = 0;
            if (_markerLossStartedTick == 0) _markerLossStartedTick = now;
            return new AnchorTracking(false, wasFound, false, 0, 0, 0, false, oldBox);
        }

        _markerLossStartedTick = 0;
        if (_anchorGraceStartedTick != 0 && now - _anchorGraceStartedTick > ReactionCoordinator.MissingGraceMs)
            _anchorGraceStartedTick = 0;

        int deltaX = wasFound ? geometry.Ax - oldAx : 0;
        int deltaY = wasFound ? geometry.Ay - oldAy : 0;
        DeltaX = deltaX;
        DeltaY = deltaY;
        int distance = Math.Max(Math.Abs(deltaX), Math.Abs(deltaY));
        bool becameFound = false;
        bool jumped = false;
        if (!wasFound)
        {
            AnchorChangedTick = now;
            becameFound = true;
        }
        else if (distance > 2)
        {
            AnchorChangedTick = now;
            if (distance >= 40)
            {
                // Freeze a currently armed candidate at its last trustworthy
                // geometry.  Do not refresh this timer on repeated bad reads;
                // the grace remains bounded to the coordinator policy.
                if (_anchorGraceStartedTick == 0 && hasLiveCandidate())
                    _anchorGraceStartedTick = now;
                jumped = true;
            }
        }
        bool boxFlipped = oldBox != geometry.Box;
        return new AnchorTracking(becameFound, false, jumped, deltaX, deltaY, distance, boxFlipped, oldBox);
    }
}
