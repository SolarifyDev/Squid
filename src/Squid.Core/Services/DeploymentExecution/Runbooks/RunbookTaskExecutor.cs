using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Runbooks;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Machines;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.Core.Services.DeploymentExecution.Runbooks;

/// <summary>
/// Executes the deliberately narrow first Runbook path. This worker never creates
/// a Release or Deployment and never enters the deployment pipeline.
/// </summary>
public sealed class RunbookTaskExecutor(
    IRunbookRunDataProvider runbookRunDataProvider,
    IRunbookPublicationDataProvider publicationDataProvider,
    IMachineDataProvider machineDataProvider,
    ITransportRegistry transportRegistry,
    IEndpointContextBuilder endpointContextBuilder,
    IServerTaskService serverTaskService,
    IServerTaskDataProvider serverTaskDataProvider) : IRunbookTaskExecutor
{
    public async Task ProcessAsync(int serverTaskId, CancellationToken cancellationToken)
    {
        var run = await runbookRunDataProvider
            .GetByServerTaskIdAsync(serverTaskId, cancellationToken)
            .ConfigureAwait(false);

        if (run?.Task == null || !string.Equals(run.Task.ServerTaskType, RunbookTaskTypes.Runbook, StringComparison.OrdinalIgnoreCase))
            return;

        // This conditional update is the single dispatch gate. A duplicate Hangfire
        // delivery sees Executing or a terminal state and exits without touching the agent.
        if (!await runbookRunDataProvider.TryClaimAsync(serverTaskId, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            var published = await publicationDataProvider
                .GetPublishedDataAsync(run.RunbookId, run.SnapshotId, cancellationToken)
                .ConfigureAwait(false);

            if (published?.Process == null)
                throw new InvalidOperationException("The pinned Runbook snapshot is unavailable.");

            RunbookPublicationValidator.EnsureSupported(published.Process);

            var machines = await machineDataProvider
                .GetMachinesByFilterAsync([run.EnvironmentId], [], cancellationToken)
                .ConfigureAwait(false);

            var actions = published.Process.StepSnapshots
                .Where(step => !step.IsDisabled)
                .OrderBy(step => step.StepOrder)
                .SelectMany(step => step.ActionSnapshots
                    .Where(action => !action.IsDisabled)
                    .OrderBy(action => action.ActionOrder)
                    .Select(action => (Step: step, Action: action)))
                .ToList();

            if (actions.Count == 0)
                throw new InvalidOperationException("The pinned Runbook snapshot has no enabled actions.");

            var sequence = 0L;
            foreach (var pair in actions)
            {
                var targets = machines
                    .Where(machine => IsEligibleTarget(machine, run, pair.Step, pair.Action))
                    .OrderBy(machine => machine.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(machine => machine.Id)
                    .ToList();

                if (targets.Count == 0)
                    throw new InvalidOperationException($"No eligible Tentacle Listening target was found for action '{pair.Action.Name}'.");

                foreach (var machine in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await ExecuteScriptAsync(
                            run.Task,
                            machine,
                            pair.Step,
                            pair.Action,
                            cancellationToken)
                        .ConfigureAwait(false);

                    sequence = await WriteResultLogsAsync(run.Task.Id, result, sequence, cancellationToken)
                        .ConfigureAwait(false);

                    if (!result.Success)
                        throw new InvalidOperationException(result.BuildErrorSummary());
                }
            }

            await serverTaskService
                .TransitionStateAsync(serverTaskId, TaskState.Executing, TaskState.Success, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FailAsync(serverTaskId, "Runbook execution was interrupted.", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await FailAsync(serverTaskId, exception.Message, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<ScriptExecutionResult> ExecuteScriptAsync(
        ServerTask task,
        Machine machine,
        DeploymentStepSnapshotDataDto step,
        DeploymentActionSnapshotDataDto action,
        CancellationToken cancellationToken)
    {
        var style = CommunicationStyleParser.Parse(machine.Endpoint);
        var transport = transportRegistry.Resolve(style);
        if (transport == null || style != CommunicationStyle.TentacleListening)
            throw new InvalidOperationException($"Machine '{machine.Name}' does not use Tentacle Listening transport.");

        var endpoint = await endpointContextBuilder
            .BuildAsync(machine.Endpoint, transport.Variables, cancellationToken)
            .ConfigureAwait(false);
        endpoint.MachineId = machine.Id;

        return await transport.Strategy.ExecuteScriptAsync(new ScriptExecutionRequest
        {
            Machine = machine,
            EndpointContext = endpoint,
            Variables = transport.Variables.ContributeVariables(endpoint),
            ScriptBody = action.Properties[SpecialVariables.Action.ScriptBody],
            Syntax = ScriptSyntax.Bash,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            ActionType = SpecialVariables.ActionTypes.Script,
            StepName = step.Name,
            ActionName = action.Name,
            StepId = step.Id,
            ActionId = action.Id,
            ServerTaskId = task.Id
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> WriteResultLogsAsync(
        int taskId,
        ScriptExecutionResult result,
        long sequence,
        CancellationToken cancellationToken)
    {
        var stderr = result.StderrLines.ToHashSet(StringComparer.Ordinal);
        var entries = result.LogLines.Select(line => new ServerTaskLogWriteEntry
        {
            SequenceNumber = ++sequence,
            Category = stderr.Contains(line) ? ServerTaskLogCategory.Error : ServerTaskLogCategory.Info,
            MessageText = line,
            Source = "Runbook"
        }).ToList();

        await serverTaskService.AddLogsAsync(taskId, entries, cancellationToken).ConfigureAwait(false);
        return sequence;
    }

    private async Task FailAsync(int taskId, string message, CancellationToken cancellationToken)
    {
        await serverTaskDataProvider.SetErrorMessageAsync(taskId, message, cancellationToken).ConfigureAwait(false);
        try
        {
            await serverTaskService
                .TransitionStateAsync(taskId, TaskState.Executing, TaskState.Failed, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ServerTaskStateTransitionException)
        {
            // Another completion path won the race; never replay the script.
        }
    }

    private static bool IsEligibleTarget(
        Machine machine,
        RunbookRun run,
        DeploymentStepSnapshotDataDto step,
        DeploymentActionSnapshotDataDto action)
    {
        if (machine.IsDisabled || machine.SpaceId != run.SpaceId)
            return false;
        if (CommunicationStyleParser.Parse(machine.Endpoint) != CommunicationStyle.TentacleListening
            || EndpointJsonHelper.ParseTentacleListeningEndpoint(machine.Endpoint) == null)
            return false;

        var environments = DeploymentTargetFinder.ParseIds(machine.EnvironmentIds);
        if (!environments.Contains(run.EnvironmentId))
            return false;
        if (action.ExcludedEnvironments.Contains(run.EnvironmentId)
            || (action.Environments.Count > 0 && !action.Environments.Contains(run.EnvironmentId)))
            return false;

        var stepRoles = DeploymentTargetFinder.ParseCsvRoles(
            step.Properties.GetValueOrDefault(SpecialVariables.Step.TargetRoles));
        var actionRoles = action.MachineRoles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var machineRoles = DeploymentTargetFinder.ParseRoles(machine.Roles);

        return (stepRoles.Count == 0 || machineRoles.Overlaps(stepRoles))
            && (actionRoles.Count == 0 || machineRoles.Overlaps(actionRoles));
    }
}
