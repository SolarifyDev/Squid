using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
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
/// </summary>
public sealed class ServerTaskLeaseReaperService : IServerTaskLeaseReaperService
{
    internal const string LogSource = "ServerTaskLeaseReaper";

    private readonly IServerTaskLeaseDataProvider _leases;
    private readonly IServerTaskService _serverTaskService;
    private readonly IDeploymentCheckpointService _checkpointService;
    private readonly IDeploymentInterruptionService _interruptionService;
    private readonly ServerTaskLeaseOptions _options;

    public ServerTaskLeaseReaperService(IServerTaskLeaseDataProvider leases, IServerTaskService serverTaskService, IDeploymentCheckpointService checkpointService, IDeploymentInterruptionService interruptionService) : this(leases, serverTaskService, checkpointService, interruptionService, ServerTaskLeaseOptions.FromEnvironment())
    {
    }

    internal ServerTaskLeaseReaperService(IServerTaskLeaseDataProvider leases, IServerTaskService serverTaskService, IDeploymentCheckpointService checkpointService, IDeploymentInterruptionService interruptionService, ServerTaskLeaseOptions options)
    {
        _leases = leases;
        _serverTaskService = serverTaskService;
        _checkpointService = checkpointService;
        _interruptionService = interruptionService;
        _options = options;
    }

    public async Task<int> ReapStaleAsync(CancellationToken ct = default)
    {
        if (!_options.ReaperEnabled) return 0;

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

        await WriteLogAsync(task, target, ct).ConfigureAwait(false);
    }

    private async Task ReleaseCancelledAsync(int taskId, CancellationToken ct)
    {
        try
        {
            await _interruptionService.CancelPendingInterruptionsAsync(taskId, ct).ConfigureAwait(false);
            await _checkpointService.DeleteAsync(taskId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[Lease] Failed to release interruptions/checkpoint of recovered cancelled task {TaskId}", taskId);
        }
    }

    private async Task WriteLogAsync(StaleServerTask task, string target, CancellationToken ct)
    {
        try
        {
            var sequence = await _leases.GetMaxLogSequenceAsync(task.Id, ct).ConfigureAwait(false) + 1;

            await _serverTaskService.AddLogAsync(task.Id, sequence, ServerTaskLogCategory.Warning, Describe(task, target), LogSource, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[Lease] Failed to write the recovery log line for task {TaskId}", task.Id);
        }
    }

    internal static string Describe(StaleServerTask task, string target) => target == TaskState.Paused
        ? $"The server running this task (owner {task.LeaseOwner}) stopped responding, so the task was paused. Its progress is saved: resume it to continue from the last checkpoint."
        : $"The server running this task (owner {task.LeaseOwner}) stopped responding after cancellation was requested, so the task was marked Cancelled.";
}
