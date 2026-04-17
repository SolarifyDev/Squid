using Squid.Core.Observability;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed partial class ExecuteStepsPhase(
    IActionHandlerRegistry actionHandlerRegistry,
    IDeploymentLifecycle lifecycle,
    IDeploymentInterruptionService interruptionService,
    IDeploymentCheckpointService checkpointService,
    IServerTaskService serverTaskService,
    ITransportRegistry transportRegistry,
    IExternalFeedDataProvider externalFeedDataProvider,
    IPackageAcquisitionService packageAcquisitionService,
    IServiceMessageParser serviceMessageParser,
    IIntentRendererRegistry intentRendererRegistry) : IDeploymentPipelinePhase
{
    public int Order => 500;

    private DeploymentTaskContext _ctx;
    private int _currentBatchIndex;
    private readonly Dictionary<int, Squid.Core.Services.Deployments.Checkpoints.BatchCheckpointState> _batchStates = new();

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        foreach (var (batch, state) in ctx.ResumeBatchStates)
            _batchStates[batch] = state;

        // Root span for the entire deployment execution. Child spans (batch,
        // step, target) attach automatically via System.Diagnostics.Activity's
        // async-local parenting. If no OTel listener is registered, this is a
        // cheap null — no allocation, no overhead.
        using var deploymentSpan = DeploymentTracing.StartDeployment(
            _ctx.ServerTaskId,
            _ctx.Deployment?.Id ?? 0,
            _ctx.Release?.Version ?? "unknown");

        try
        {
            await ExecuteDeploymentStepsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            deploymentSpan?.RecordException(ex);
            throw;
        }
    }

    private async Task ExecuteDeploymentStepsAsync(CancellationToken ct)
    {
        var orderedSteps = _ctx.Steps.OrderBy(p => p.StepOrder).ToList();
        var batches = StepBatcher.BatchSteps(orderedSteps);
        var stepSortOrderByStep = orderedSteps.ToDictionary(step => step, step => step.StepOrder);

        _currentBatchIndex = 0;

        foreach (var batch in batches)
        {
            if (_ctx.ResumeFromBatchIndex.HasValue && _currentBatchIndex <= _ctx.ResumeFromBatchIndex.Value)
            {
                Log.Information("[Deploy] Skipping batch {BatchIndex} (already completed in previous run)", _currentBatchIndex);
                _currentBatchIndex++;
                continue;
            }

            using var batchSpan = DeploymentTracing.StartBatch(_currentBatchIndex);

            var batchEntries = batch.Select(step => (Step: step, SortOrder: stepSortOrderByStep[step])).ToList();
            var batchResults = batch.Count == 1
                ? [await ExecuteStepAcrossTargetsAsync(batchEntries[0].Step, batchEntries[0].SortOrder, ct).ConfigureAwait(false)]
                : await Task.WhenAll(batchEntries.Select(entry =>
                    ExecuteStepAcrossTargetsAsync(entry.Step, entry.SortOrder, ct))).ConfigureAwait(false);

            ApplyBatchResults(batchResults);

            await PersistCheckpointAsync(_currentBatchIndex, ct).ConfigureAwait(false);

            _currentBatchIndex++;
        }
    }

    private async Task PersistCheckpointAsync(int batchIndex, CancellationToken ct)
    {
        try
        {
            var outputVariablesJson = SerializeOutputVariables(_ctx.Variables);
            var batchStatesJson = SerializeBatchStates();

            await checkpointService.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = _ctx.ServerTaskId,
                DeploymentId = _ctx.Deployment.Id,
                LastCompletedBatchIndex = batchIndex,
                FailureEncountered = _ctx.FailureEncountered,
                OutputVariablesJson = outputVariablesJson,
                BatchStatesJson = batchStatesJson,
                InFlightScriptsJson = "{}"
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to persist checkpoint at batch {BatchIndex}, continuing", batchIndex);
        }
    }

    private string SerializeBatchStates()
    {
        if (_batchStates.Count == 0) return "{}";

        var keyed = _batchStates.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
        return System.Text.Json.JsonSerializer.Serialize(keyed);
    }

    internal Squid.Core.Services.Deployments.Checkpoints.BatchCheckpointState GetOrCreateBatchState(int batchIndex)
    {
        if (!_batchStates.TryGetValue(batchIndex, out var state))
        {
            state = new Squid.Core.Services.Deployments.Checkpoints.BatchCheckpointState();
            _batchStates[batchIndex] = state;
        }
        return state;
    }

    internal void MarkTargetCompleted(int batchIndex, int machineId, bool failed)
    {
        var state = GetOrCreateBatchState(batchIndex);
        if (failed) state.FailedMachineIds.Add(machineId);
        else state.CompletedMachineIds.Add(machineId);
    }

    private static string SerializeOutputVariables(List<VariableDto> variables)
    {
        if (variables == null || variables.Count == 0) return null;

        var outputVars = variables.Where(v => v.Name.StartsWith("Squid.Action.", StringComparison.OrdinalIgnoreCase)).ToList();

        if (outputVars.Count == 0) return null;

        return System.Text.Json.JsonSerializer.Serialize(outputVars);
    }

    private void ApplyBatchResults(IEnumerable<StepExecutionResult> results)
    {
        foreach (var result in results)
        {
            if (result.OutputVariables.Count > 0)
            {
                _ctx.Variables.AddRange(result.OutputVariables);
                Log.Information("[Deploy] Captured {Count} output variables from batch {BatchIndex}", result.OutputVariables.Count, _currentBatchIndex);

                foreach (var v in result.OutputVariables)
                    Log.Debug("[Deploy]   {Name} = {Value}", v.Name, v.IsSensitive ? "***" : v.Value?[..Math.Min(v.Value.Length, 100)]);
            }

            _ctx.FailureEncountered |= result.Failed;
        }
    }

    private class StepExecutionResult
    {
        private readonly object _lock = new();
        public List<VariableDto> OutputVariables { get; } = new();
        public bool Failed { get; set; }
        public bool Executed { get; set; }

        public void Absorb(StepExecutionResult other)
        {
            lock (_lock)
            {
                OutputVariables.AddRange(other.OutputVariables);
                Failed |= other.Failed;
                Executed |= other.Executed;
            }
        }
    }
}
