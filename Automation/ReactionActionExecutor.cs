using HappyBot.Combat;
using HappyBot.Infrastructure.Input;

namespace HappyBot.Automation;

/// <summary>Small host surface for the serialized automation workers.</summary>
internal interface IAutomationHost
{
    Settings Settings { get; }
    CancellationToken ShutdownToken { get; }
    IInputGateway Input { get; }
    bool IsReactionActive { get; }
    bool MarkerFound { get; }
    bool OrangeParryEnabled { get; }
    OutgoingOrangeGuardResult OutgoingOrangeState { get; }
    bool IsEHeld();
    bool IsFHeld();
    bool IsCurrentCandidate(long candidateId);
    bool IsYourChar(string name);
    bool HasHeroAction { get; }
    void SetVisionReaction(string state, string reason, string direction = "", int displayMs = 1100);
    void RecordTelemetry(string name, object data, bool failure = false);
    void IncrementParryCount();
    void RequestParryEvidence(long candidateId, CombatDirection direction);
    void RegisterAutomationLight();
    void RestoreAutoGuardAfterDirectionalLight();
}

/// <summary>
/// Owns reaction feature policy and input sequences.  Serialization, cooldown,
/// cancellation, and committed-state ownership remain in ActionScheduler.
/// </summary>
internal sealed class ReactionActionExecutor
{
    private readonly IAutomationHost _host;
    private readonly ActionScheduler _scheduler;
    private readonly IParryRollSource _parryRolls;

    public ReactionActionExecutor(IAutomationHost host, ActionScheduler scheduler, IParryRollSource parryRolls)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _parryRolls = parryRolls ?? throw new ArgumentNullException(nameof(parryRolls));
    }

    public ParryDecision LatestParryDecision { get; private set; }
    public ParryOutcome? LatestParryOutcome { get; private set; }

    public void QueueReaction(ReactionCommand command)
    {
        if (command == null) return;
        if (command.Kind == ReactionCommandKind.Parry)
        {
            Settings settings = _host.Settings;
            bool bulwarkEligible = settings.Autoblock && settings.Parry && settings.Legit &&
                _host.IsYourChar("Blackprior") && _host.Input.CanSendBulwark;
            bool crushingEligible = settings.Autoblock && settings.Parry && settings.Crushing && settings.Legit;
            bool deflectEligible = settings.Autoblock && settings.Parry && settings.Deflect && settings.Legit &&
                !ReactionPolicy.IsNuxiaTopDeflectBlocked(settings, command.Direction);
            ParryResolution resolution = ParryResolution.Create(command, settings.Legit,
                settings.LegitParryChance, _parryRolls, settings.BulwarkFallback,
                bulwarkEligible, crushingEligible, settings.CrushingFallbackChance,
                _parryRolls, deflectEligible, settings.DeflectFallbackChance, _parryRolls);
            ParryDecision decision = resolution.Decision;
            LatestParryDecision = decision;
            LatestParryOutcome = resolution.Outcome;
            _host.RecordTelemetry("legit-parry-decision", new
            {
                candidateId = decision.CandidateId,
                hold = decision.Hold,
                direction = decision.Direction.ToString(),
                chancePercent = decision.ChancePercent,
                roll = decision.Roll,
                outcome = resolution.Outcome.ToString().ToUpperInvariant(),
                legitEnabled = decision.LegitEnabled,
                bulwarkFallbackEnabled = settings.BulwarkFallback,
                bulwarkEligible,
                crushingEligible,
                deflectEligible,
                crushingFallbackChance = settings.CrushingFallbackChance,
                deflectFallbackChance = settings.DeflectFallbackChance,
                fallbackRoll = resolution.FallbackRoll,
                deflectRoll = resolution.DeflectRoll
            });

            if (resolution.Outcome == ParryOutcome.Deflect)
            {
                string mix = resolution.DeflectRoll is int roll
                    ? $"; deflect {settings.DeflectFallbackChance}% roll {roll}" : "";
                _host.SetVisionReaction("DEFLECT FALLBACK",
                    $"Legit {decision.ChancePercent}% roll {decision.Roll}: dodge{mix}",
                    DirectionName(decision.Direction), 1100);
                QueueDirectionalAction(command with { Kind = ReactionCommandKind.Deflect });
                return;
            }
            if (resolution.Outcome == ParryOutcome.Crushing)
            {
                _host.SetVisionReaction("CRUSHING FALLBACK",
                    $"Legit {decision.ChancePercent}% roll {decision.Roll}: RB{DescribeFallbackRolls(resolution, settings)}",
                    DirectionName(decision.Direction), 1100);
                QueueDirectionalAction(command with { Kind = ReactionCommandKind.Crushing });
                return;
            }
            if (resolution.Outcome == ParryOutcome.Bulwark)
            {
                _host.SetVisionReaction("BULWARK FALLBACK",
                    $"Legit {decision.ChancePercent}% roll {decision.Roll}: flip{DescribeFallbackRolls(resolution, settings)}",
                    DirectionName(decision.Direction), 1100);
                QueueDirectionalAction(command with { Kind = ReactionCommandKind.Bulwark });
                return;
            }
            if (resolution.Outcome == ParryOutcome.Block)
            {
                _host.SetVisionReaction("BLOCK ONLY",
                    $"Legit {decision.ChancePercent}% roll {decision.Roll}: block",
                    DirectionName(decision.Direction), 1100);
                return;
            }
        }
        QueueDirectionalAction(command);
    }

    private void QueueDirectionalAction(ReactionCommand command)
    {
        _scheduler.TrySchedule(command.CandidateId, "ARMED " + command.Kind,
            token => ExecuteDirectionalActionAsync(command, token),
            snapshot => _host.RecordTelemetry("action-cancelled", new
            {
                candidateId = command.CandidateId,
                state = snapshot.State,
                committed = snapshot.Committed
            }));
    }

    private async Task<bool> ExecuteDirectionalActionAsync(ReactionCommand command, CancellationToken token)
    {
        if (command.Kind == ReactionCommandKind.Parry)
        {
            _host.SetVisionReaction("PARRY READY", command.Hold + " hold + flash gate", DirectionName(command.Direction), 900);
            await Task.Delay(Math.Max(0, _host.Settings.ParryDelay), token);
            if (!CanCommitAction(command, token)) return false;
            _scheduler.SetCommitted(true);
            if (!_host.Input.MouseClick(Input.VK_RBUTTON))
                _host.SetVisionReaction("PARRY FAILED", "RT input was not delivered", DirectionName(command.Direction), 1300);
            else
            {
                _host.IncrementParryCount();
                _host.RequestParryEvidence(command.CandidateId, command.Direction);
                _host.SetVisionReaction("PARRY SENT", "RT input sent", DirectionName(command.Direction), 1300);
            }
            return true;
        }
        if (command.Kind == ReactionCommandKind.Crushing)
        {
            if (!CanCommitAction(command, token)) return false;
            _scheduler.SetCommitted(true);
            _host.Input.MouseClick(Input.VK_LBUTTON);
            _host.SetVisionReaction("CRUSHING SENT", command.Hold + " hold + flash gate", DirectionName(command.Direction), 1300);
            return true;
        }
        if (command.Kind == ReactionCommandKind.Deflect)
        {
            int delay = command.Direction == CombatDirection.Left ? _host.Settings.Left
                : command.Direction == CombatDirection.Right ? _host.Settings.Right : _host.Settings.TopDeflect;
            await Task.Delay(Math.Max(0, delay), token);
            if (!CanCommitAction(command, token)) return false;
            _scheduler.SetCommitted(true);
            if (SendDeflect(command.Direction))
            {
                if (_host.Input.MouseClick(Input.VK_LBUTTON))
                    _host.RegisterAutomationLight();
                _host.SetVisionReaction("DEFLECT + LIGHT SENT", "F hold + directional dodge + RB", DirectionName(command.Direction), 1300);
            }
            else
                _host.SetVisionReaction("DEFLECT FAILED", "Directional dodge input was not delivered", DirectionName(command.Direction), 1300);
            return true;
        }
        if (command.Kind == ReactionCommandKind.Hero)
            return await ExecuteHeroActionAsync(command, token);
        if (command.Kind == ReactionCommandKind.Bulwark)
            return await ExecuteBulwarkCounterAsync(command, token, "legit-fallback");
        return false;
    }

    private bool CanCommitAction(ReactionCommand command, CancellationToken token)
    {
        bool holdStillDown = command.Hold == "E" ? _host.IsEHeld() : _host.IsFHeld();
        return !token.IsCancellationRequested && holdStillDown && _host.IsReactionActive &&
            IsCommandStillEnabled(command) && _scheduler.IsCurrent(command.CandidateId) &&
            _host.IsCurrentCandidate(command.CandidateId);
    }

    private bool IsCommandStillEnabled(ReactionCommand command)
    {
        Settings settings = _host.Settings;
        if (!settings.Autoblock) return false;
        if (command.RequiresParryEnabled && !(command.Hold == "E" ? settings.Parry2 : settings.Parry)) return false;
        return command.Kind switch
        {
            ReactionCommandKind.Parry => command.Hold == "E" ? settings.Parry2 : settings.Parry,
            ReactionCommandKind.Crushing => command.Hold == "E"
                ? settings.Crushing2
                : settings.Crushing || (settings.Parry && command.Direction == CombatDirection.Top && _host.IsYourChar("Warden")),
            ReactionCommandKind.Deflect => settings.Deflect,
            ReactionCommandKind.Bulwark => settings.BulwarkFallback && settings.Legit && settings.Parry && _host.IsYourChar("Blackprior"),
            ReactionCommandKind.Hero => _host.HasHeroAction,
            _ => false
        };
    }

    private bool SendDeflect(CombatDirection direction)
    {
        bool sent = true;
        IInputGateway input = _host.Input;
        input.Block(true);
        try
        {
            sent &= input.KeyUp(Input.VK_W);
            sent &= input.KeyUp(Input.VK_S);
            sent &= input.KeyUp(Input.VK_A);
            sent &= input.KeyUp(Input.VK_D);
            if (direction == CombatDirection.Left) sent &= input.KeyDown(Input.VK_LEFT);
            else if (direction == CombatDirection.Right) sent &= input.KeyDown(Input.VK_RIGHT);
            else sent &= input.KeyDown(Input.VK_UP);
            sent &= input.KeyTap(Input.VK_SPACE);
            if (direction == CombatDirection.Left) sent &= input.KeyUp(Input.VK_LEFT);
            else if (direction == CombatDirection.Right) sent &= input.KeyUp(Input.VK_RIGHT);
            else sent &= input.KeyUp(Input.VK_UP);
        }
        finally { input.Block(false); }
        return sent;
    }

    private async Task<bool> ExecuteHeroActionAsync(ReactionCommand command, CancellationToken token)
    {
        if (!CanCommitAction(command, token)) return false;
        if (_host.IsYourChar("Blackprior"))
            return await ExecuteBulwarkCounterAsync(command, token, "hero-flash");

        _scheduler.SetCommitted(true);
        IInputGateway input = _host.Input;
        if (_host.IsYourChar("Warlord")) { input.KeyTap(Input.VK_C); input.MouseClick(Input.VK_LBUTTON); }
        else if (_host.IsYourChar("Shaman")) { input.KeyTap(Input.VK_SPACE); input.KeyTap(Input.VK_NUMPAD5); }
        else if (_host.IsYourChar("Varangian")) { input.KeyTap(Input.VK_C); input.MouseClick(Input.VK_RBUTTON); }
        else if (_host.IsYourChar("Orochi")) { input.KeyTap(Input.VK_SPACE); input.KeyTap(Input.VK_NUMPAD9); }
        else if (_host.IsYourChar("Nobushi")) input.KeyTap(Input.VK_C);
        else if (_host.IsYourChar("Aramusha")) { input.KeyTap(Input.VK_C); input.MouseClick(Input.VK_RBUTTON); }
        else if (_host.IsYourChar("Jiangjun"))
        {
            input.KeyDown(Input.VK_C);
            try
            {
                await Task.Delay(250, token);
                if (!CanCommitAction(command, token)) return false;
                input.MouseClick(Input.VK_LBUTTON);
                input.MouseClick(Input.VK_RBUTTON);
            }
            finally { input.KeyUp(Input.VK_C); }
        }
        else return false;
        _host.SetVisionReaction("HERO RESPONSE SENT", "F hold + flash gate", DirectionName(command.Direction), 1300);
        return true;
    }

    private async Task<bool> ExecuteBulwarkCounterAsync(ReactionCommand command, CancellationToken token, string path)
    {
        if (!CanCommitAction(command, token)) return false;
        _scheduler.SetState("BULWARK STANCE");
        _host.SetVisionReaction("BULWARK READY", "RS down -> 50ms -> RB", DirectionName(command.Direction), 900);
        _host.RecordTelemetry("bulwark-ready", new { candidateId = command.CandidateId, path, bridge = _host.Input.Diagnostics });
        if (!_host.Input.BeginBulwarkStance())
        {
            _host.SetVisionReaction("BULWARK FAILED", "Controller input was not delivered; guard remains active", DirectionName(command.Direction), 1300);
            _host.RecordTelemetry("bulwark-failed", new { candidateId = command.CandidateId, path, reason = "stance-input", bridge = _host.Input.Diagnostics });
            return false;
        }
        try
        {
            await Task.Delay(50, token);
            if (!CanCommitAction(command, token)) return false;
            _scheduler.SetCommitted(true);
            if (_host.Input.MouseClick(Input.VK_LBUTTON))
            {
                _host.SetVisionReaction("BULWARK SENT", "RS down + RB counter", DirectionName(command.Direction), 1300);
                _host.RecordTelemetry("bulwark-sent", new { candidateId = command.CandidateId, path, bridge = _host.Input.Diagnostics });
            }
            else
            {
                _host.SetVisionReaction("BULWARK FAILED", "RB input was not delivered; guard remains active", DirectionName(command.Direction), 1300);
                _host.RecordTelemetry("bulwark-failed", new { candidateId = command.CandidateId, path, reason = "right-shoulder", bridge = _host.Input.Diagnostics });
            }
            return true;
        }
        finally { _host.Input.EndBulwarkStance(); }
    }

    private static string DescribeFallbackRolls(ParryResolution resolution, Settings settings)
    {
        string detail = resolution.DeflectRoll is int deflectRoll
            ? $"; deflect {settings.DeflectFallbackChance}% roll {deflectRoll}" : "";
        return resolution.FallbackRoll is int fallbackRoll
            ? detail + $"; fallback {settings.CrushingFallbackChance}% roll {fallbackRoll}" : detail;
    }

    private static string DirectionName(CombatDirection direction) => direction switch
    {
        CombatDirection.Left => "LEFT",
        CombatDirection.Right => "RIGHT",
        CombatDirection.Top => "TOP",
        _ => ""
    };
}
