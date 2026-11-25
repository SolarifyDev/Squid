using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Settings.ServerTask;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.ServerTask;

namespace Squid.Core.Services.Deployments.ServerTask.Lease;

public interface IServerTaskLeaseReaperService : IScopedDependency
{
    /// <summary>Recovers every active task whose lease went stale and reports how many this call recovered.</summary>
    Task<int> ReapStaleAsync(CancellationToken ct = default);
}

/// <summary>
/// A running task renews its lease; one whose renewal stopped lost its process. Executing is paused, not failed — its checkpoint stays
/// so the task resumes where it stopped — and Cancelling, whose cancel was already requested, is completed as Cancelled. Paused needs no live
/// owner and is never touched, nor is a task that was never leased: with no proof its owner died (an older server's task during a rolling
/// upgrade, or a row from before leases) it is not ours to take, so the reaper only ever recovers what a lease-aware process stopped renewing.
/// Every recovery is a compare-and-swap on state, owner and staleness, so a renewal that lands first wins and the reaper no-ops.
/// A paused task whose server stopped is then resumed automatically (the redelivery of its job, which the lease fence may already have dropped, would have
/// done the same), at most <see cref="ServerTaskLeaseOptions.MaxAutoResumes"/> times per task so a deployment that kills its server cannot loop forever.
/// </summary>
public sealed class ServerTaskLeaseReaperService : IServerTaskLeaseReaperService
{
    internal const string LogSource = "ServerTaskLeaseReaper";

    private readonly IServerTaskLeaseDataProvider _leases;
    private readonly IServerTaskService _serverTaskService;
    private readonly IDeploymentCheckpointService _checkpointService;
    private readonly IDeploymentInterruptionService _interruptionService;
    private readonly IInFlightScriptCanceller _scriptCanceller;
    private readonly IServerTaskControlService _controlService;
    private readonly ServerTaskLeaseOptions _options;

    public ServerTaskLeaseReaperService(IServerTaskLeaseDataProvider leases, IServerTaskService serverTaskService, IDeploymentCheckpointService checkpointService, IDeploymentInterruptionService interruptionService, ServerTaskSetting setting, IInFlightScriptCanceller scriptCanceller = null, IServerTaskControlService controlService = null) : this(leases, serverTaskService, checkpointService, interruptionService, ServerTaskLeaseOptions.From(setting), scriptCanceller, controlService)
    {
    }

    internal ServerTaskLeaseReaperService(IServerTaskLeaseDataProvider leases, IServerTaskService serverTaskService, IDeploymentCheckpointService checkpointService, IDeploymentInterruptionService interruptionService, ServerTaskLeaseOptions options, IInFlightScriptCanceller scriptCanceller = null, IServerTaskControlService controlService = null)
    {
        _controlService = controlService;
        _leases = leases;
        _serverTaskService = serverTaskService;
        _checkpointService = checkpointService;
        _interruptionService = interruptionService;
        _scriptCanceller = scriptCanceller;
        _options = options;
    }

    public async Task<int> ReapStaleAsync(CancellationToken ct = default)
    {
        var stale = await _leases.GetStaleAsync(_options.Stale, ServerTaskLeaseOptions.ReaperBatchSize, ct).ConfigureAwait(false);
        var recovered = 0;

        foreach (var task in stale)
            recovered += await TryRecoverAsync(task, ct).ConfigureAwait(false) ? 1 : 0;

        return recovered;
    }

    /// <summary>The state a task with a stale lease is recovered to, or null when it is not recovered (Paused, terminal, anything else).</summary>
    internal static string RecoveryTarget(string state) => state switch
    {
        TaskState.Executing => TaskState.Paused,
        TaskState.Cancelling => TaskState.Cancelled,
        _ => null
    };

    // One task failing (a DB blip, a bad row) must not stop the rest of the sweep from recovering theirs.
    private async Task<bool> TryRecoverAsync(StaleServerTask task, CancellationToken ct)
    {
        var target = RecoveryTarget(task.State);

        if (target == null) return false;

        try
        {
            if (await _leases.RecoverAsync(task, target, _options.Stale, ct).ConfigureAwait(false) == 0) return false;

            await AfterRecoveredAsync(task, target, ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "[Lease] Failed to recover task {TaskId} with a stale lease (state {State})", task.Id, task.State);

            return false;
        }
    }

    // The recovery is already committed; the bookkeeping around it is best effort and never undoes it.
    private async Task AfterRecoveredAsync(StaleServerTask task, string target, CancellationToken ct)
    {
        Log.Warning("[Lease] Task {TaskId} was {State} but its lease (owner {Owner}) stopped renewing; recovered to {Target}", task.Id, task.State, task.LeaseOwner, target);

        if (target == TaskState.Cancelled) await ReleaseCancelledAsync(task.Id, ct).ConfigureAwait(false);

        var resume = target == TaskState.Paused ? await DecideAutoResumeAsync(task, ct).ConfigureAwait(false) : AutoResume.NotApplicable;

        await WriteLogAsync(task, target, resume, ct).ConfigureAwait(false);

        if (resume == AutoResume.Requested) await RequestResumeAsync(task, ct).ConfigureAwait(false);
    }

    internal enum AutoResume { NotApplicable, Requested, LimitReached, Disabled }

    // The log lines the reaper wrote before this recovery are the count of earlier recoveries of the task: no extra column, and a task an operator resumed
    // by hand keeps its history, so one that keeps stopping its server eventually waits for a human.
    private async Task<AutoResume> DecideAutoResumeAsync(StaleServerTask task, CancellationToken ct)
    {
        if (_controlService == null) return AutoResume.NotApplicable;

        if (_options.MaxAutoResumes == 0) return AutoResume.Disabled;

        try
        {
            var earlier = await _leases.CountLogsFromSourceAsync(task.Id, LogSource, ct).ConfigureAwait(false);

            return earlier < _options.MaxAutoResumes ? AutoResume.Requested : AutoResume.LimitReached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[Lease] Could not count earlier recoveries of task {TaskId}; leaving it Paused for an operator", task.Id);

            return AutoResume.LimitReached;
        }
    }

    // Best effort: the recovery is committed, and a task that is not resumed here is still Paused with its checkpoint for an operator.
    private async Task RequestResumeAsync(StaleServerTask task, CancellationToken ct)
    {
        try
        {
            await _controlService.TryAutoResumeAsync(task.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[Lease] Failed to resume recovered task {TaskId} automatically; it stays Paused", task.Id);
        }
    }

    private async Task ReleaseCancelledAsync(int taskId, CancellationToken ct)
    {
        try
        {
            await _interruptionService.CancelPendingInterruptionsAsync(taskId, ct).ConfigureAwait(false);

            // The dead owner can no longer cancel the agent script it was running, and the checkpoint deleted next is the only record of its ticket.
            if (_scriptCanceller != null) await _scriptCanceller.CancelAllAsync(taskId, ct).ConfigureAwait(false);

            await _checkpointService.DeleteAsync(taskId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[Lease] Failed to release interruptions/checkpoint of recovered cancelled task {TaskId}", taskId);
        }
    }

    private async Task WriteLogAsync(StaleServerTask task, string target, AutoResume resume, CancellationToken ct)
    {
        try
        {
            var sequence = await _leases.GetMaxLogSequenceAsync(task.Id, ct).ConfigureAwait(false) + 1;

            await _serverTaskService.AddLogAsync(task.Id, sequence, ServerTaskLogCategory.Warning, Describe(task, target, resume), LogSource, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[Lease] Failed to write the recovery log line for task {TaskId}", task.Id);
        }
    }

    internal static string Describe(StaleServerTask task, string target, AutoResume resume = AutoResume.NotApplicable) => target == TaskState.Paused
        ? $"The server running this task (owner {task.LeaseOwner}) stopped responding, so the task was paused. {DescribeResume(resume)}"
        : $"The server running this task (owner {task.LeaseOwner}) stopped responding after cancellation was requested, so the task was marked Cancelled.";

    private static string DescribeResume(AutoResume resume) => resume switch
    {
        AutoResume.Requested => "Its progress is saved and it is being resumed automatically from the last checkpoint.",
        AutoResume.LimitReached => "Its progress is saved, but it was already resumed automatically after earlier stops, so resume it manually to continue from the last checkpoint.",
        AutoResume.Disabled => "Its progress is saved, and automatic resume is turned off, so resume it manually to continue from the last checkpoint.",
        _ => "Its progress is saved: resume it to continue from the last checkpoint."
    };
}
