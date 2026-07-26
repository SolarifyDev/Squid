using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Variable;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Pins the RESUME half of the checkpoint output-variable contract: outputs restored from a
/// checkpoint must be carried into the NEXT checkpoint this run writes.
///
/// <para><b>Why this is load-bearing</b>: the checkpoint is written from the captured set. A
/// deployment that pauses TWICE (transient agent blip, resume, then a second blip) would
/// otherwise checkpoint only the outputs captured since the most recent resume, silently
/// discarding everything the earlier run produced — the same class of silent loss the
/// bracket/dot prefix bug caused, just one resume later. Only a multi-pause scenario catches
/// it; a single pause→resume passes either way.</para>
///
/// <para>Asserted against the persisted checkpoint rather than the in-memory accumulator, so
/// the test survives refactors of where the re-seed lives and keeps testing the property that
/// actually matters to an operator.</para>
/// </summary>
public sealed class CheckpointOutputVariableAccumulationTests
{
    /// <summary>
    /// The step name <see cref="CheckpointPhaseHarness"/> runs. The re-emit test below counts
    /// occurrences of a step-qualified name, so it MUST match — qualified under any other step
    /// the emitted name differs from the restored one and the assertion passes vacuously.
    /// </summary>
    private const string HarnessStepName = "OneStep";

    private static readonly string FirstRunOutputName = SpecialVariables.Output.Variable(HarnessStepName, "Url");

    private static VariableDto FirstRunOutput() => new()
    {
        Name = FirstRunOutputName,
        Value = "https://first-run.test"
    };

    [Fact]
    public async Task SecondPause_StillCheckpointsTheFirstRunsOutputs()
    {
        // Resume carrying one restored output, then capture a new one and pause again.
        var saved = await CheckpointPhaseHarness.RunOneBatchAsync(
            emittedOutputs: new[] { ("SecondRunKey", "second-run-value") },
            restoredOutputVariables: new[] { FirstRunOutput() });

        var checkpointed = CheckpointPhaseHarness.ReadCheckpoint(saved);

        checkpointed.ShouldContain(v => v.Name == FirstRunOutputName,
            customMessage: "A restored output variable MUST be carried into the next checkpoint, otherwise the " +
                           "second pause silently drops everything the previous run produced.");
        checkpointed.ShouldContain(v => v.Value == "second-run-value",
            customMessage: "The output captured after the resume must be checkpointed alongside the restored one.");
    }

    [Fact]
    public async Task RestoredOutputs_AreNotDuplicatedWhenReEmittedAfterResume()
    {
        // The resumed run re-runs a step that emits the SAME value the restored checkpoint
        // already holds. Without de-duplication every resume cycle would add another copy.
        var restored = FirstRunOutput();

        var saved = await CheckpointPhaseHarness.RunOneBatchAsync(
            emittedOutputs: new[] { ("Url", restored.Value) },
            restoredOutputVariables: new[] { restored });

        var checkpointed = CheckpointPhaseHarness.ReadCheckpoint(saved);
        var occurrences = checkpointed.Count(v => v.Name == FirstRunOutputName);

        occurrences.ShouldBe(1,
            customMessage: $"The restored variable was re-emitted with the same value; the checkpoint must still " +
                           $"hold ONE entry for it, not {occurrences}. Growth here compounds every resume cycle.");
    }

    [Fact]
    public async Task FreshDeployment_NothingRestored_CheckpointsOnlyWhatItCaptured()
    {
        var saved = await CheckpointPhaseHarness.RunOneBatchAsync(
            emittedOutputs: new[] { ("Key", "value") });

        var checkpointed = CheckpointPhaseHarness.ReadCheckpoint(saved);

        checkpointed.ShouldNotBeEmpty("the batch captured an output variable");
        checkpointed.ShouldAllBe(v => v.Value == "value",
            "a fresh deployment has no restored state — checkpointing anything else would persist phantom values");
    }

    [Fact]
    public async Task NoOutputVariablesAtAll_LeavesTheColumnNull()
    {
        // Pre-existing contract: the column stays null rather than holding an empty array.
        var saved = await CheckpointPhaseHarness.RunOneBatchAsync();

        saved.ShouldNotBeNull();
        saved.OutputVariablesJson.ShouldBeNull(
            customMessage: "A deployment that captured nothing must leave OutputVariablesJson null, matching the " +
                           "pre-existing contract that resume checks before deserializing.");
    }
}
