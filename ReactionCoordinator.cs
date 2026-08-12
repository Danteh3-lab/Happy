using System.Drawing;

namespace HappyBot;

internal enum CombatDirection
{
    None,
    Top,
    Left,
    Right
}

internal enum ReactionCommandKind
{
    None,
    Parry,
    Crushing,
    Deflect,
    Hero
}

/// <summary>Provides a testable 0-99 roll for legitimate parry selection.</summary>
internal interface IParryRollSource
{
    int NextPercent();
}

internal sealed class RandomParryRollSource : IParryRollSource
{
    public static readonly RandomParryRollSource Instance = new();

    private RandomParryRollSource() { }

    public int NextPercent() => Random.Shared.Next(100);
}

/// <summary>One immutable parry-or-block choice for an accepted flash candidate.</summary>
internal sealed record ParryDecision(
    long CandidateId,
    string Hold,
    CombatDirection Direction,
    int ChancePercent,
    int? Roll,
    bool ShouldParry,
    bool LegitEnabled)
{
    public string Outcome => ShouldParry ? "PARRY" : "BLOCK";

    public static ParryDecision Create(ReactionCommand command, bool legitEnabled, int chancePercent, IParryRollSource rolls)
    {
        int chance = Math.Clamp(chancePercent, 0, 100);
        int? roll = legitEnabled ? Math.Clamp(rolls.NextPercent(), 0, 99) : null;
        bool shouldParry = !legitEnabled || roll.GetValueOrDefault() < chance;
        return new ParryDecision(command.CandidateId, command.Hold, command.Direction, chance, roll, shouldParry, legitEnabled);
    }
}

/// <summary>
/// Immutable result of one full capture/detection pass. The coordinator never
/// captures the screen itself, so its candidate lifecycle is deterministic.
/// </summary>
internal sealed record CombatObservation(
    long TimestampMs,
    bool MarkerFound,
    Point Anchor,
    int Box,
    Rectangle CombatRoi,
    bool HasIndicator,
    Point Indicator,
    CombatDirection Direction,
    bool DarkRedGate,
    bool LightFlash,
    bool OrangeIndicator,
    bool OrangeFeint,
    bool EHeld,
    bool FHeld,
    bool LtHeld,
    bool InputReady);

internal sealed record ReactionCandidate(
    long Id,
    CombatDirection Direction,
    long StartedMs,
    long LastValidMs,
    bool Consumed);

internal sealed record ReactionCommand(
    long CandidateId,
    ReactionCommandKind Kind,
    string Hold,
    CombatDirection Direction);

internal sealed record CoordinatorTick(
    ReactionCandidate Candidate,
    ReactionCommand Command,
    string Transition,
    string CancellationReason,
    bool IgnoredStaleFlash);

/// <summary>
/// Owns only the directional threat lifecycle. Input and image capture remain
/// outside this class so either can be tested/replaced without timing races.
/// </summary>
internal sealed class ReactionCoordinator
{
    internal const int MissingGraceMs = 250;
    internal const int CandidateMaximumMs = 3000;

    private readonly TimeProvider _clock;
    private ReactionCandidate _candidate;
    private bool _mustClearBeforeRearm;
    private long _nextId;

    public ReactionCoordinator(TimeProvider clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    public ReactionCandidate CurrentCandidate => _candidate;

    public bool IsCurrent(long candidateId) => _candidate is { Id: var id } && id == candidateId;

    public CoordinatorTick Tick(CombatObservation observation, ReactionCommandKind requestedKind, string hold)
    {
        long now = observation.TimestampMs != 0 ? observation.TimestampMs : _clock.GetUtcNow().ToUnixTimeMilliseconds();
        bool validThreat = observation.MarkerFound && observation.HasIndicator && observation.Direction != CombatDirection.None;
        string transition = "";
        string cancellation = "";
        bool staleFlash = false;

        if (!validThreat && _mustClearBeforeRearm)
            _mustClearBeforeRearm = false;

        if (validThreat && !_mustClearBeforeRearm)
        {
            if (_candidate == null)
            {
                _candidate = NewCandidate(observation.Direction, now);
                transition = "armed";
            }
            else if (_candidate.Direction != observation.Direction)
            {
                _candidate = NewCandidate(observation.Direction, now);
                transition = "replaced";
            }
            else
            {
                _candidate = _candidate with { LastValidMs = now };
            }
        }

        if (_candidate != null)
        {
            long age = now - _candidate.StartedMs;
            long missingAge = now - _candidate.LastValidMs;
            if (age > CandidateMaximumMs)
            {
                cancellation = "candidate-timeout";
                _candidate = null;
                _mustClearBeforeRearm = true;
            }
            else if (!validThreat && missingAge > MissingGraceMs)
            {
                cancellation = "indicator-stale";
                _candidate = null;
            }
        }

        ReactionCommand command = null;
        if (_candidate != null && !_candidate.Consumed && observation.LightFlash && !observation.DarkRedGate)
        {
            if (requestedKind == ReactionCommandKind.None)
            {
                // A flash without a held/eligible action is visual-only; guard
                // remains active until the current red threat clears.
            }
            else
            {
                command = new ReactionCommand(_candidate.Id, requestedKind, hold, _candidate.Direction);
                _candidate = _candidate with { Consumed = true, LastValidMs = now };
                transition = string.IsNullOrEmpty(transition) ? "flash-accepted" : transition + "+flash-accepted";
            }
        }
        else if (_candidate == null && observation.LightFlash)
        {
            staleFlash = true;
        }

        return new CoordinatorTick(_candidate, command, transition, cancellation, staleFlash);
    }

    public string Cancel(string reason)
    {
        if (_candidate == null) return "";
        _candidate = null;
        return reason;
    }

    private ReactionCandidate NewCandidate(CombatDirection direction, long now) =>
        new(Interlocked.Increment(ref _nextId), direction, now, now, false);
}
