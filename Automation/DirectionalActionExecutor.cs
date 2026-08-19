using HappyBot.Combat;

namespace HappyBot.Automation;

/// <summary>
/// Thin automation facade retained for BotCore's existing lifecycle and status
/// surface.  Feature behavior lives in the reaction and orange controllers;
/// they share one scheduler so all input remains serialized.
/// </summary>
internal sealed class DirectionalActionExecutor : IDisposable
{
    private readonly IAutomationHost _host;
    private readonly ActionScheduler _scheduler;
    private readonly ReactionActionExecutor _reaction;
    private readonly OrangeResponseController _orange;

    public DirectionalActionExecutor(
        IAutomationHost host,
        IParryRollSource parryRolls,
        IOrangeLightDirectionSource orangeLightDirections)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _scheduler = new ActionScheduler(host.ShutdownToken);
        _reaction = new ReactionActionExecutor(host, _scheduler, parryRolls);
        _orange = new OrangeResponseController(host, _scheduler, orangeLightDirections);
    }

    public bool IsBusy => _scheduler.IsBusy;
    public string State => _scheduler.State;
    public long CandidateId => _scheduler.CandidateId;
    public bool Committed => _scheduler.Committed;
    public ParryDecision LatestParryDecision => _reaction.LatestParryDecision;
    public ParryOutcome? LatestParryOutcome => _reaction.LatestParryOutcome;

    public void QueueReaction(ReactionCommand command) => _reaction.QueueReaction(command);

    public void ProcessOrangeObservation(CombatObservation observation, bool suppressOrange) =>
        _orange.ProcessObservation(observation, suppressOrange);

    public void CancelPendingAction(string reason, bool force = false)
    {
        _scheduler.CancelPending(reason, force, snapshot =>
            _host.RecordTelemetry("action-cancel-request", new
            {
                reason,
                candidateId = snapshot.CandidateId,
                state = snapshot.State,
                force
            }));
    }

    public void Dispose() => _scheduler.Dispose();
}
