using Squid.Core.Services.Jobs;
using Squid.Message.Models.Machines;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Keeps the server-side upgrade timeline (<see cref="IUpgradeEventTimelineStore"/>) fresh while an upgrade runs, so the
/// UI sees progress and the final outcome without waiting for the next scheduled health check. Each poll is one Hangfire
/// job: it re-probes the agent (a manual health check re-reads its upgrade events, status and Phase B log) and, unless
/// this run has reported its terminal status or a ceiling is reached, schedules the next poll. Polling therefore lasts as
/// long as the upgrade, bounded by the dispatching strategy's <see cref="IUpgradeDurationBudget"/>, instead of a fixed
/// window that ended before slow starts, rollbacks and Windows upgrades wrote their outcome. A dispatch whose run is
/// never seen in flight stops at <see cref="DefaultCeiling"/>, as before.
/// </summary>
public interface IUpgradeProgressPoller : IScopedDependency
{
    Task PollAsync(UpgradeProgressPoll poll, CancellationToken ct);
}

public sealed class UpgradeProgressPoller : IUpgradeProgressPoller
{
    /// <summary>Delay between polls; matches the UI's upgrade-events poll cadence (3s).</summary>
    internal const int IntervalSeconds = 3;

    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(IntervalSeconds);

    /// <summary>How long polling waits for a first sign of the run, and the whole ceiling for a strategy without a budget: the historical fixed window.</summary>
    internal static readonly TimeSpan DefaultCeiling = TimeSpan.FromSeconds(120);

    /// <summary>Added to a script's worst case: the agent is probed once more after it writes the terminal status.</summary>
    internal static readonly TimeSpan ReconnectCushion = TimeSpan.FromSeconds(40);

    private readonly IMachineHealthCheckService _healthCheck;
    private readonly IUpgradeEventTimelineStore _store;
    private readonly ISquidBackgroundJobClient _jobs;

    public UpgradeProgressPoller(IMachineHealthCheckService healthCheck, IUpgradeEventTimelineStore store, ISquidBackgroundJobClient jobs)
    {
        _healthCheck = healthCheck;
        _store = store;
        _jobs = jobs;
    }

    /// <summary>How long polling may last once the run is seen in flight, for an upgrade dispatched by <paramref name="strategy"/>.</summary>
    internal static TimeSpan CeilingFor(IMachineUpgradeStrategy strategy) => strategy is IUpgradeDurationBudget budget ? budget.WorstCaseDuration + ReconnectCushion : DefaultCeiling;

    /// <summary>Schedules the first poll of an upgrade dispatched now. Never throws: a failed schedule only costs UI freshness.</summary>
    internal static void Start(ISquidBackgroundJobClient jobs, int machineId, TimeSpan ceiling, DateTimeOffset? baselineStartedAt)
    {
        var now = DateTimeOffset.UtcNow;
        var poll = new UpgradeProgressPoll { MachineId = machineId, FirstSignDeadline = now + DefaultCeiling, Deadline = now + ceiling, BaselineStartedAt = baselineStartedAt };

        if (!TrySchedule(jobs, poll)) return;

        Log.Information("[UpgradeAudit] Polling upgrade progress for machine {MachineId} every {Interval}s until its terminal status, at most {Ceiling}s", machineId, IntervalSeconds, ceiling.TotalSeconds);
    }

    public async Task PollAsync(UpgradeProgressPoll poll, CancellationToken ct)
    {
        await RefreshAsync(poll.MachineId, ct).ConfigureAwait(false);

        var status = _store.GetStatus(poll.MachineId);

        if (IsFinished(status, poll))
        {
            Log.Information("[UpgradeAudit] Upgrade on machine {MachineId} reported {Status}; progress polling done", poll.MachineId, status.Status);
            return;
        }

        var next = poll with { SawThisRun = poll.SawThisRun || ShowsThisRunInFlight(status, poll.BaselineStartedAt) };

        if (IsPastCeiling(next))
        {
            Log.Information("[UpgradeAudit] No terminal upgrade status from machine {MachineId} within the polling ceiling; the next scheduled health check picks it up", poll.MachineId);
            return;
        }

        TrySchedule(_jobs, next);
    }

    /// <summary>
    /// Terminal AND provably this run's: either this run was seen in flight, or the status names another run than the one
    /// reported before dispatch. A terminal status that cannot be told apart from the previous run's keeps polling.
    /// </summary>
    internal static bool IsFinished(UpgradeStatusPayload status, UpgradeProgressPoll poll)
    {
        if (status == null || !UpgradeStatusClassifier.IsTerminal(status.Status)) return false;

        return poll.SawThisRun || IsLaterRun(status, poll.BaselineStartedAt);
    }

    private static bool IsLaterRun(UpgradeStatusPayload status, DateTimeOffset? baselineStartedAt) => baselineStartedAt != null && status.StartedAt != null && status.StartedAt != baselineStartedAt;

    /// <summary>In flight, and not the (abandoned) run that was already reported before dispatch.</summary>
    private static bool ShowsThisRunInFlight(UpgradeStatusPayload status, DateTimeOffset? baselineStartedAt)
    {
        if (status == null || !UpgradeStatusClassifier.InFlightStatuses.Contains(status.Status)) return false;

        return baselineStartedAt == null || status.StartedAt != baselineStartedAt;
    }

    private static bool IsPastCeiling(UpgradeProgressPoll poll) => DateTimeOffset.UtcNow + Interval > (poll.SawThisRun ? poll.Deadline : poll.FirstSignDeadline);

    /// <summary>A failed probe (agent restarting mid-upgrade) must not end the chain.</summary>
    private async Task RefreshAsync(int machineId, CancellationToken ct)
    {
        try
        {
            await _healthCheck.ManualHealthCheckAsync(machineId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[UpgradeAudit] Upgrade progress probe failed for machine {MachineId}; polling continues", machineId);
        }
    }

    /// <summary>Never throws: a throw from a poll would make Hangfire retry it and start a second chain.</summary>
    private static bool TrySchedule(ISquidBackgroundJobClient jobs, UpgradeProgressPoll poll)
    {
        try
        {
            jobs.Schedule<IUpgradeProgressPoller>(poller => poller.PollAsync(poll, CancellationToken.None), Interval);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[UpgradeAudit] Failed to schedule upgrade progress polling for machine {MachineId}; UI shows stale events until the next scheduled health check", poll.MachineId);
            return false;
        }
    }
}
