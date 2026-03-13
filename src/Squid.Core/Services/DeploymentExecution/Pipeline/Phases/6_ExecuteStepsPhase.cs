using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed partial class ExecuteStepsPhase(IActionHandlerRegistry actionHandlerRegistry, IDeploymentLifecycle lifecycle, IDeploymentInterruptionService interruptionService, IDeploymentCheckpointService checkpointService) : IDeploymentPipelinePhase
{
    public int Order => 500;

    private DeploymentTaskContext _ctx;
    private int _currentBatchIndex;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        await ExecuteDeploymentStepsAsync(ct).ConfigureAwait(false);
    }

    private async Task ExecuteDeploymentStepsAsync(CancellationToken ct)
    {
        var orderedSteps = _ctx.Steps.OrderBy(p => p.StepOrder).ToList();
        var batches = StepBatcher.BatchSteps(orderedSteps);
        var stepSortOrderByStep = new Dictionary<DeploymentStepDto, int>();
        var displayOrder = 0;

        foreach (var step in orderedSteps)
            stepSortOrderByStep[step] = ++displayOrder;

        _currentBatchIndex = 0;

        foreach (var batch in batches)
        {
            if (_ctx.ResumeFromBatchIndex.HasValue && _currentBatchIndex <= _ctx.ResumeFromBatchIndex.Value)
            {
                Log.Information("Skipping batch {BatchIndex} (already completed in previous run)", _currentBatchIndex);
                _currentBatchIndex++;
                continue;
            }

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

            await checkpointService.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = _ctx.ServerTaskId,
                DeploymentId = _ctx.Deployment.Id,
                LastCompletedBatchIndex = batchIndex,
                FailureEncountered = _ctx.FailureEncountered,
                OutputVariablesJson = outputVariablesJson,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist checkpoint at batch {BatchIndex}, continuing", batchIndex);
        }
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
                foreach (var v in result.OutputVariables)
                {
                    var displayValue = v.IsSensitive ? "********" : v.Value;
                    Log.Information("Output variable captured: {Name} = {Value}", v.Name, displayValue);
                }
            }

            _ctx.FailureEncountered |= result.Failed;
        }
    }

    private class StepExecutionResult
    {
        public List<VariableDto> OutputVariables { get; } = new();
        public bool Failed { get; set; }

        public void Absorb(StepExecutionResult other)
        {
            OutputVariables.AddRange(other.OutputVariables);
            Failed |= other.Failed;
        }
    }
}
