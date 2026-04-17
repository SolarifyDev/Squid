using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class ResumeCheckpointPhase(IDeploymentCheckpointService checkpointService) : IDeploymentPipelinePhase
{
    public int Order => 50;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        var checkpoint = await checkpointService.LoadAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);

        if (checkpoint == null) return;

        ctx.ResumeFromBatchIndex = checkpoint.LastCompletedBatchIndex;
        ctx.FailureEncountered = checkpoint.FailureEncountered;

        if (checkpoint.OutputVariablesJson != null)
            RestoreOutputVariables(ctx, checkpoint.OutputVariablesJson);

        RestoreBatchStates(ctx, checkpoint.BatchStatesJson);

        Log.Information("[Deploy] Resuming deployment from batch index {BatchIndex} with {BatchStateCount} per-target state entries",
            checkpoint.LastCompletedBatchIndex, ctx.ResumeBatchStates.Count);
    }

    private static void RestoreOutputVariables(DeploymentTaskContext ctx, string json)
    {
        var restored = System.Text.Json.JsonSerializer.Deserialize<List<VariableDto>>(json);

        if (restored == null || restored.Count == 0) return;

        ctx.RestoredOutputVariables.AddRange(restored);

        Log.Information("[Deploy] Restored {Count} output variables from checkpoint", restored.Count);
    }

    private static void RestoreBatchStates(DeploymentTaskContext ctx, string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return;

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, BatchCheckpointState>>(json);
            if (parsed == null) return;

            foreach (var (key, value) in parsed)
            {
                if (int.TryParse(key, out var batchIndex))
                    ctx.ResumeBatchStates[batchIndex] = value;
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            Log.Warning(ex, "[Deploy] Malformed batch_states checkpoint JSON — ignoring and starting fresh");
        }
    }
}
