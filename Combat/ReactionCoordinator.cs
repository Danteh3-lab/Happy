using System.Drawing;

namespace HappyBot.Combat;

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
    Bulwark,
    Crushing,
    Deflect,
    Hero
}

internal enum ParryOutcome
{
    Parry,
    Deflect,
    Crushing,
    Bulwark,
    Block
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

/// <summary>Provides a testable random direction for orange-only light interrupts.</summary>
internal interface IOrangeLightDirectionSource
{
    CombatDirection NextDirection();
}

internal sealed class RandomOrangeLightDirectionSource : IOrangeLightDirectionSource
{
    public static readonly RandomOrangeLightDirectionSource Instance = new();

    private RandomOrangeLightDirectionSource() { }

    public CombatDirection NextDirection() => Random.Shared.Next(3) switch
    {
        0 => CombatDirection.Left,
        1 => CombatDirection.Top,
        _ => CombatDirection.Right
    };
}

internal sealed record OrangeLightDecision(CombatDirection Direction)
{
    public static OrangeLightDecision Create(IOrangeLightDirectionSource source)
    {
        CombatDirection direction = source?.NextDirection() ?? CombatDirection.Top;
        return new OrangeLightDecision(direction is CombatDirection.Left or CombatDirection.Top or CombatDirection.Right
            ? direction
            : CombatDirection.Top);
    }
}

internal enum OrangeResponseKind
{
    Dodge,
    Parry,
    Light
}

internal static class OrangeResponseResolver
{
    public static OrangeResponseKind Resolve(bool redOrFeint, bool orangeParryEnabled, bool orangeLightEnabled) =>
        redOrFeint
            ? orangeParryEnabled ? OrangeResponseKind.Parry : OrangeResponseKind.Dodge
            : orangeLightEnabled ? OrangeResponseKind.Light : OrangeResponseKind.Dodge;
}

/// <summary>
/// Orange state can only clear after a valid vision frame confirms the indicator
/// disappeared. A missing anchor is an unknown frame, not an indicator clear.
/// </summary>
internal static class OrangeResponseLatch
{
    public static bool IsConfirmedClear(bool markerFound, bool orangeIndicator) =>
        markerFound && !orangeIndicator;
}

/// <summary>
/// Attributes an orange indicator to a physical source-controller heavy attack
/// when it appears during the short outgoing-attack window. The attribution
/// remains latched until a valid marker frame confirms that orange has cleared.
/// </summary>
internal sealed record OutgoingOrangeGuardResult(
    bool SourceHeavyHeld,
    bool SourceLightHeld,
    string AttributionSource,
    bool WindowActive,
    bool SelfOrangeLatched,
    long SuppressionUntilMs,
    bool SuppressesOrange,
    bool SelfOrangeStarted,
    bool SelfOrangeCleared);

internal sealed class OutgoingOrangeGuard
{
    // The orange indicator can appear well after the physical heavy is
    // released. Keep this internal so it remains a safety attribution window,
    // not a user-facing timing setting.
    public const int SuppressionWindowMs = 1500;

    private readonly object _sync = new();
    private long _suppressionUntilMs;
    private bool _selfOrangeLatched;
    private bool _windowSourceHeavySeen;
    private bool _windowSourceLightSeen;
    private bool _selfOrangeSourceHeavy;
    private bool _selfOrangeSourceLight;

    private static string SourceName(bool sourceHeavy, bool sourceLight) =>
        sourceHeavy && sourceLight ? "RT+RB" : sourceHeavy ? "RT" : sourceLight ? "RB" : "";

    /// <summary>Registers a bot-generated RB light in the same outgoing window as a physical light.</summary>
    public void RegisterAutomationLight(long now)
    {
        lock (_sync)
        {
            _windowSourceLightSeen = true;
            _suppressionUntilMs = Math.Max(_suppressionUntilMs, now + SuppressionWindowMs);
        }
    }

    public OutgoingOrangeGuardResult Observe(long now, bool markerFound, bool orangeIndicator, bool sourceHeavyHeld, bool sourceLightHeld = false)
    {
        lock (_sync)
        {
            if (sourceHeavyHeld)
                _windowSourceHeavySeen = true;
            if (sourceLightHeld)
                _windowSourceLightSeen = true;
            if (sourceHeavyHeld || sourceLightHeld)
                _suppressionUntilMs = Math.Max(_suppressionUntilMs, now + SuppressionWindowMs);
            else if (!_selfOrangeLatched && now >= _suppressionUntilMs)
            {
                _windowSourceHeavySeen = false;
                _windowSourceLightSeen = false;
            }

            bool selfOrangeCleared = false;
            string clearedSource = "";
            if (_selfOrangeLatched && OrangeResponseLatch.IsConfirmedClear(markerFound, orangeIndicator))
            {
                clearedSource = SourceName(_selfOrangeSourceHeavy, _selfOrangeSourceLight);
                _selfOrangeLatched = false;
                _selfOrangeSourceHeavy = false;
                _selfOrangeSourceLight = false;
                selfOrangeCleared = true;
            }

            bool windowActive = now < _suppressionUntilMs;
            bool selfOrangeStarted = false;
            if (!_selfOrangeLatched && windowActive && markerFound && orangeIndicator)
            {
                _selfOrangeLatched = true;
                _selfOrangeSourceHeavy = _windowSourceHeavySeen;
                _selfOrangeSourceLight = _windowSourceLightSeen;
                selfOrangeStarted = true;
            }

            string attributionSource = _selfOrangeLatched
                ? SourceName(_selfOrangeSourceHeavy, _selfOrangeSourceLight)
                : clearedSource;

            return new OutgoingOrangeGuardResult(
                sourceHeavyHeld,
                sourceLightHeld,
                attributionSource,
                windowActive,
                _selfOrangeLatched,
                _suppressionUntilMs,
                windowActive || _selfOrangeLatched,
                selfOrangeStarted,
                selfOrangeCleared);
        }
    }
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

/// <summary>Combines the percentage roll with optional F-path fallback mixes.</summary>
internal sealed record ParryResolution(ParryDecision Decision, ParryOutcome Outcome, int? FallbackRoll, int? DeflectRoll)
{
    public static ParryResolution Create(
        ReactionCommand command,
        bool legitEnabled,
        int chancePercent,
        IParryRollSource rolls,
        bool bulwarkFallbackEnabled,
        bool bulwarkEligible,
        bool crushingEligible = false,
        int crushingFallbackChance = 50,
        IParryRollSource fallbackRolls = null,
        bool deflectEligible = false,
        int deflectFallbackChance = 50,
        IParryRollSource deflectRolls = null)
    {
        ParryDecision decision = ParryDecision.Create(command, legitEnabled, chancePercent, rolls);
        if (decision.ShouldParry) return new ParryResolution(decision, ParryOutcome.Parry, null, null);
        if (command.Hold != "F") return new ParryResolution(decision, ParryOutcome.Block, null, null);

        bool canBulwark = bulwarkFallbackEnabled && bulwarkEligible;
        int? deflectRoll = null;
        if (deflectEligible)
        {
            deflectRoll = Math.Clamp((deflectRolls ?? rolls).NextPercent(), 0, 99);
            if (deflectRoll < Math.Clamp(deflectFallbackChance, 0, 100))
                return new ParryResolution(decision, ParryOutcome.Deflect, null, deflectRoll);
        }
        if (crushingEligible && canBulwark)
        {
            int roll = Math.Clamp((fallbackRolls ?? rolls).NextPercent(), 0, 99);
            ParryOutcome mixedOutcome = roll < Math.Clamp(crushingFallbackChance, 0, 100)
                ? ParryOutcome.Crushing
                : ParryOutcome.Bulwark;
            return new ParryResolution(decision, mixedOutcome, roll, deflectRoll);
        }
        if (crushingEligible) return new ParryResolution(decision, ParryOutcome.Crushing, null, deflectRoll);
        if (canBulwark) return new ParryResolution(decision, ParryOutcome.Bulwark, null, deflectRoll);
        return new ParryResolution(decision, ParryOutcome.Block, null, deflectRoll);
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
    bool InputReady,
    bool SourceHeavyHeld = false,
    bool SourceLightHeld = false);

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
    CombatDirection Direction,
    bool RequiresParryEnabled = false);

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
    private readonly object _sync = new();
    private ReactionCandidate _candidate;
    private bool _mustClearBeforeRearm;
    private long _nextId;

    public ReactionCoordinator(TimeProvider clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    public ReactionCandidate CurrentCandidate
    {
        get { lock (_sync) return _candidate; }
    }

    public bool IsCurrent(long candidateId)
    {
        lock (_sync) return _candidate is { Id: var id } && id == candidateId;
    }

    public CoordinatorTick Tick(CombatObservation observation, ReactionCommandKind requestedKind, string hold)
    {
        lock (_sync)
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
                    // A flash is a one-shot timing opportunity. If no action can
                    // run now (for example while another worker is busy), consume
                    // it so it cannot fire late after a cooldown.
                    _candidate = _candidate with { Consumed = true, LastValidMs = now };
                    transition = string.IsNullOrEmpty(transition) ? "flash-ignored" : transition + "+flash-ignored";
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
    }

    public string Cancel(string reason)
    {
        lock (_sync)
        {
            if (_candidate == null) return "";
            _candidate = null;
            return reason;
        }
    }

    private ReactionCandidate NewCandidate(CombatDirection direction, long now) =>
        new(Interlocked.Increment(ref _nextId), direction, now, now, false);
}

