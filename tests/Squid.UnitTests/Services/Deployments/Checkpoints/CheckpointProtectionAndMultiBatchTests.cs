using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Closes two coverage gaps the round-2 review found in the checkpoint suites.
///
/// <list type="number">
///   <item><b>Protect-at-capture wiring</b>: every other suite runs an IDENTITY encryption mock,
///   under which removing protection entirely is invisible — a sensitive value looks the same
///   protected or not. These tests use the harness's faithful mock (it transforms and recognises
///   its own output) so the wiring is actually observable.</item>
///   <item><b>Multi-batch accumulation</b>: the phase writes a checkpoint per batch, and the
///   whole point of the accumulator is that later writes carry earlier batches' outputs. Every
///   other test drives a single batch, so monotonic accumulation was untested at every tier.</item>
/// </list>
/// </summary>
public sealed class CheckpointProtectionAndMultiBatchTests
{
    [Fact]
    public async Task SensitiveOutputVariable_IsEncryptedInTheCheckpoint()
    {
        var saved = await CheckpointPhaseHarness.RunOneBatchAsync(
            emittedSensitiveOutputs: new[] { ("ApiKey", "super-secret") },
            faithfulEncryption: true);

        saved.OutputVariablesJson.ShouldNotBeNull();
        saved.OutputVariablesJson.ShouldNotContain("super-secret",
            customMessage: "A sensitive output variable must never reach the checkpoint column in plaintext. " +
                           "Seeing it here means protection is not applied at the capture site.");
        saved.OutputVariablesJson.ShouldContain(CheckpointPhaseHarness.EncryptedPrefix,
            customMessage: "The stored value must carry the encryption envelope.");
    }

    [Fact]
    public async Task NonSensitiveOutputVariable_StaysPlaintext_ForOperatorInspection()
    {
        // Deliberate contract: operators read checkpoints to debug stuck deployments, so only
        // sensitive values are encrypted. Guards against 'fix by encrypting everything'.
        var saved = await CheckpointPhaseHarness.RunOneBatchAsync(
            emittedOutputs: new[] { ("Url", "https://web.test") },
            faithfulEncryption: true);

        saved.OutputVariablesJson.ShouldContain("https://web.test",
            customMessage: "Non-sensitive values stay readable in the checkpoint by design.");
    }

    [Fact]
    public async Task SensitiveValue_IsEncryptedOncePerEntry_NotOncePerBatchWrite()
    {
        // The cost contract, and the ONLY observable difference between protecting at capture
        // and protecting at serialize time: the checkpoint JSON looks identical either way,
        // because the serializer would encrypt on every write. What changes is how many 600k
        // PBKDF2 derivations a deployment pays — O(entries) versus O(batches x entries).
        var encryptCalls = new List<string>();
        var saves = new List<DeploymentExecutionCheckpoint>();

        await CheckpointPhaseHarness.RunOneBatchAsync(
            emittedSensitiveOutputs: new[] { ("ApiKey", "super-secret") },
            faithfulEncryption: true,
            stepCount: 4,
            allSaves: saves,
            encryptCalls: encryptCalls);

        // The contract: ONE encryption per checkpointed entry, for the whole deployment. Several
        // entries share the same value here (each step mints step-qualified, machine-qualified
        // and bare forms of the same variable), so count entries, not distinct values.
        var checkpointedSensitive = CheckpointPhaseHarness.ReadCheckpoint(saves[^1]).Count(v => v.IsSensitive);

        encryptCalls.Count.ShouldBe(checkpointedSensitive,
            customMessage: $"Expected exactly one encryption per checkpointed sensitive entry " +
                           $"({checkpointedSensitive}), got {encryptCalls.Count}. More means protection moved back " +
                           "to serialize time, where every batch write re-encrypts the whole accumulated set — the " +
                           "quadratic cost (a 600k-iteration PBKDF2 derivation each) this design exists to prevent.");
    }

    [Fact]
    public async Task EveryBatchWritesACheckpoint_AndLaterWritesCarryEarlierBatchesOutputs()
    {
        var saves = new List<DeploymentExecutionCheckpoint>();

        await CheckpointPhaseHarness.RunOneBatchAsync(
            emittedOutputs: new[] { ("Shared", "value") },
            allSaves: saves,
            stepCount: 3);

        saves.Count.ShouldBe(3,
            customMessage: $"Three sequential steps form three batches, so three checkpoints must be written. " +
                           $"Got {saves.Count}.");

        var counts = saves.Select(s => CheckpointPhaseHarness.ReadCheckpoint(s).Count).ToList();

        counts.ShouldBeInOrder(SortDirection.Ascending,
            customMessage: $"The checkpoint must accumulate rather than be rebuilt per batch — a later write that " +
                           $"holds FEWER entries means earlier batches' outputs were dropped. Sequence: [{string.Join(",", counts)}]");

        // Each step mints its own step-qualified name, so the final write must hold all three.
        var finalNames = CheckpointPhaseHarness.ReadCheckpoint(saves[^1]).Select(v => v.Name).ToList();

        foreach (var step in new[] { "Step1", "Step2", "Step3" })
            finalNames.ShouldContain(n => n.Contains(step),
                customMessage: $"The final checkpoint must carry {step}'s output; without accumulation a resume " +
                               "would lose every batch except the last.");
    }
}
