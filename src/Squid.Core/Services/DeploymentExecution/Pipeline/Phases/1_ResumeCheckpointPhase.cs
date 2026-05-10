using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Security;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class ResumeCheckpointPhase(
    IDeploymentCheckpointService checkpointService,
    IVariableEncryptionService variableEncryptionService) : IDeploymentPipelinePhase
{
    public int Order => 50;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        var checkpoint = await checkpointService.LoadAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);

        if (checkpoint == null) return;

        ctx.ResumeFromBatchIndex = checkpoint.LastCompletedBatchIndex;
        ctx.FailureEncountered = checkpoint.FailureEncountered;

        if (checkpoint.OutputVariablesJson != null)
            await RestoreOutputVariablesAsync(ctx, checkpoint.OutputVariablesJson).ConfigureAwait(false);

        RestoreBatchStates(ctx, checkpoint.BatchStatesJson);

        Log.Information("[Deploy] Resuming deployment from batch index {BatchIndex} with {BatchStateCount} per-target state entries",
            checkpoint.LastCompletedBatchIndex, ctx.ResumeBatchStates.Count);
    }

    /// <summary>
    /// P0-3: counterpart to <c>ExecuteStepsPhase.SerializeOutputVariables</c> —
    /// when restoring from a checkpoint, decrypt sensitive values that were
    /// encrypted on persist. Non-sensitive values pass through untouched.
    ///
    /// <para><b>Backward compat</b>: pre-P0-3 checkpoints have plaintext
    /// sensitive values. <c>IsValidEncryptedValue</c> returns false for
    /// un-prefixed text, so we leave them as-is — old checkpoints resume
    /// cleanly without operator intervention. Only NEW checkpoints written
    /// by a 1.6.6+ server carry the encrypted prefix.</para>
    ///
    /// <para>Same scope salt as on encrypt (<c>ServerTaskId</c>) — the salt
    /// is implicit per-checkpoint, never written to disk; ciphertext from
    /// task-A cannot be decrypted under task-B's salt even with the same
    /// master key.</para>
    /// </summary>
    private async Task RestoreOutputVariablesAsync(DeploymentTaskContext ctx, string json)
    {
        var restored = System.Text.Json.JsonSerializer.Deserialize<List<VariableDto>>(json);

        if (restored == null || restored.Count == 0) return;

        var decryptedCount = 0;

        foreach (var v in restored)
        {
            if (!v.IsSensitive || string.IsNullOrEmpty(v.Value)) continue;
            if (!variableEncryptionService.IsValidEncryptedValue(v.Value)) continue;

            v.Value = await variableEncryptionService.DecryptAsync(v.Value, ctx.ServerTaskId).ConfigureAwait(false);
            decryptedCount++;
        }

        ctx.RestoredOutputVariables.AddRange(restored);

        Log.Information("[Deploy] Restored {Count} output variables from checkpoint ({DecryptedCount} sensitive decrypted)",
            restored.Count, decryptedCount);
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
