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

        Log.Information("Resuming deployment from batch index {BatchIndex}", checkpoint.LastCompletedBatchIndex);
    }

    private static void RestoreOutputVariables(DeploymentTaskContext ctx, string json)
    {
        var restored = System.Text.Json.JsonSerializer.Deserialize<List<VariableDto>>(json);

        if (restored == null || restored.Count == 0) return;

        ctx.RestoredOutputVariables.AddRange(restored);

        Log.Information("Restored {Count} output variables from checkpoint", restored.Count);
    }
}
