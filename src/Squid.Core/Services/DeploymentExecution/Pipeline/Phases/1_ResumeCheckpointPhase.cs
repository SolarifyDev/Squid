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
        List<VariableDto> restored;

        try
        {
            restored = System.Text.Json.JsonSerializer.Deserialize<List<VariableDto>>(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            // A malformed column must not destroy the resume. Throwing here fails the whole
            // deployment AND (via the failure path) deletes the checkpoint, discarding the
            // per-batch progress that is still perfectly readable. Degrade to "no restored
            // output variables" and let the run continue from its batch index instead.
            Log.Error(ex, "[Deploy] Checkpoint output-variable JSON for task {ServerTaskId} is unreadable — resuming WITHOUT restored output variables. Steps referencing them will see empty values.", ctx.ServerTaskId);
            return;
        }

        if (restored == null || restored.Count == 0) return;

        var decryptedCount = 0;
        var undecryptable = 0;

        foreach (var v in restored)
        {
            if (!v.IsSensitive || string.IsNullOrEmpty(v.Value)) continue;
            if (!variableEncryptionService.IsValidEncryptedValue(v.Value)) continue;

            try
            {
                v.Value = await variableEncryptionService.DecryptAsync(v.Value, ctx.ServerTaskId).ConfigureAwait(false);
                decryptedCount++;
            }
            catch (Exception ex)
            {
                // Typically a master key rotated between pause and resume. Dropping ONE
                // variable is strictly better than failing the deployment and deleting the
                // checkpoint with it — the operator can re-run the step that produced it,
                // but cannot recover discarded batch progress. Blank the value rather than
                // leaving ciphertext, so a consumer never substitutes an encrypted blob.
                Log.Error(ex, "[Deploy] Could not decrypt checkpointed output variable {VariableName} for task {ServerTaskId} (master key rotated since the checkpoint was written?) — continuing with an empty value.", v.Name, ctx.ServerTaskId);
                v.Value = string.Empty;
                undecryptable++;
            }
        }

        ctx.RestoredOutputVariables.AddRange(restored);

        Log.Information("[Deploy] Restored {Count} output variables from checkpoint ({DecryptedCount} sensitive decrypted, {UndecryptableCount} undecryptable)",
            restored.Count, decryptedCount, undecryptable);
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
