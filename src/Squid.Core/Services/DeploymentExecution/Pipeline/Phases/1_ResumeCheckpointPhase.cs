using Squid.Core.Services.DeploymentExecution.Exceptions;
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
    /// <para>Decrypts under the same KDF scope used on encrypt (<c>ServerTaskId</c>).
    /// See <see cref="Variables.CheckpointOutputVariableSerializer"/> for what that
    /// scope does and does not guarantee — it is domain separation, not an
    /// access-control boundary.</para>
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
            // Unlike an undecryptable value (below), malformed JSON is NOT recoverable — no
            // amount of operator action makes this column parse, so pausing would wedge the
            // deployment permanently. Failing would delete the checkpoint and discard the
            // per-batch progress stored beside it, which is still perfectly readable. Degrade
            // to "no restored output variables", log loudly, and let the run continue.
            Log.Error(ex, "[Deploy] Checkpoint output-variable JSON for task {ServerTaskId} is unreadable — resuming WITHOUT restored output variables. Steps referencing them will see empty values.", ctx.ServerTaskId);
            return;
        }

        if (restored == null || restored.Count == 0) return;

        var decryptedCount = 0;

        foreach (var v in restored)
        {
            if (!v.IsSensitive || string.IsNullOrEmpty(v.Value)) continue;
            if (!variableEncryptionService.IsValidEncryptedValue(v.Value)) continue;

            try
            {
                v.Value = await variableEncryptionService.DecryptAsync(v.Value, ctx.ServerTaskId).ConfigureAwait(false);
                decryptedCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Typically the master key rotated between pause and resume. The CIPHERTEXT is
                // intact — only the key to read it is missing — so this is recoverable: restore
                // the key and resume. Pause rather than continue or fail:
                //   - continuing would substitute an EMPTY secret into later steps and let the
                //     deployment report Success, which is the worst outcome (an empty password
                //     can silently "work" somewhere);
                //   - failing would delete the checkpoint, discarding both the still-readable
                //     batch progress and the recoverable ciphertext itself.
                // Pausing preserves the checkpoint verbatim and leaves an explicit, resumable
                // state; an operator who genuinely discarded the old key can cancel the task.
                Log.Error(ex, "[Deploy] Could not decrypt checkpointed output variable {VariableName} for task {ServerTaskId} — the master key has most likely rotated since the checkpoint was written. Pausing (checkpoint preserved): restore the key and resume, or cancel the task.", v.Name, ctx.ServerTaskId);

                throw new DeploymentSuspendedException(ctx.ServerTaskId);
            }
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
