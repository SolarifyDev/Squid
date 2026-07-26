using System.Text.Json;
using Squid.Core.Services.Security;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Variables;

/// <summary>
/// Serializes the output variables captured during a deployment into the
/// <c>DeploymentExecutionCheckpoint.OutputVariablesJson</c> column, encrypting
/// sensitive values at rest, and is the single source of truth for that shape.
///
/// <para><b>Why this takes an explicit captured-set rather than filtering by name</b>:
/// the previous implementation selected checkpoint content out of the full variable
/// list with <c>Name.StartsWith("Squid.Action.")</c>. Output variables are minted by
/// <see cref="Squid.Message.Constants.SpecialVariables.Output"/> as
/// <c>Squid.Action[{step}].Output.{name}</c> — a BRACKET, not a dot — so that predicate
/// matched none of them, while still matching unrelated action-scoped config variables
/// such as <c>Squid.Action.Kubernetes.Namespace</c>. Every resumed deployment therefore
/// silently lost its output variables while the checkpoint still looked populated.
/// Name-matching re-derives knowledge that the capture site already has exactly; the
/// caller now hands us the variables it captured, so no predicate can drift again and
/// the un-qualified alias (<c>{name}</c>, indistinguishable from an ordinary variable
/// by name alone) is preserved too.</para>
///
/// <para><b>Sensitive values</b> are encrypted with the deployment's task id as the KDF
/// scope. That scope is a domain-separation input, NOT an access-control boundary: anyone
/// holding the master key can derive any task's key by supplying that task's id, so the
/// scope limits accidental cross-task reuse rather than preventing deliberate cross-task
/// decryption. Confidentiality rests entirely on the master key. Non-sensitive values stay
/// plaintext deliberately — operators inspect checkpoints to debug stuck deployments, and
/// encrypting everything would block that workflow for no security gain. Already-encrypted
/// values are passed through unchanged so a resumed-and-rewritten checkpoint never
/// double-wraps.</para>
///
/// <para>The read counterpart is <c>ResumeCheckpointPhase.RestoreOutputVariablesAsync</c>,
/// which decrypts using the same scope and tolerates legacy plaintext checkpoints.</para>
/// </summary>
public static class CheckpointOutputVariableSerializer
{
    /// <summary>
    /// Returns the checkpoint JSON for <paramref name="capturedOutputVariables"/>, or
    /// <c>null</c> when there is nothing to persist (the column stays null rather than
    /// holding an empty array, matching the pre-existing contract).
    /// </summary>
    public static string Serialize(IReadOnlyList<VariableDto> capturedOutputVariables, IVariableEncryptionService encryption, int kdfScope)
    {
        if (capturedOutputVariables == null || capturedOutputVariables.Count == 0) return null;

        var protectedVariables = capturedOutputVariables.Select(v => EncryptIfSensitive(v, encryption, kdfScope)).ToList();

        return JsonSerializer.Serialize(protectedVariables);
    }

    /// <summary>
    /// Encrypts <paramref name="variable"/>'s value when it is sensitive, returning a
    /// clone so the live in-memory variable is never rewritten. Non-sensitive, empty and
    /// already-encrypted values are returned as-is.
    /// </summary>
    public static VariableDto EncryptIfSensitive(VariableDto variable, IVariableEncryptionService encryption, int kdfScope)
    {
        if (variable == null) return null;

        if (!variable.IsSensitive || string.IsNullOrEmpty(variable.Value)) return variable;

        // Already encrypted (e.g. the resumed-and-rewritten path) — don't re-wrap.
        if (encryption.IsValidEncryptedValue(variable.Value)) return variable;

        // Clone via the copy-constructor: a manual field-by-field copy is fragile against
        // future VariableDto fields (silent loss, no compiler warning). The copy-ctor is
        // pinned by VariableDtoCopyConstructorTests.
        return new VariableDto(variable) { Value = encryption.EncryptAsync(variable.Value, kdfScope) };
    }
}
