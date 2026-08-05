using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Squid.Message.Models.Deployments.Variable;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Anti-regression pin for the SEAM the output-variable checkpoint bug lived in: that
/// <c>ExecuteStepsPhase.PersistCheckpointAsync</c> serializes the variables the executor
/// actually CAPTURED, and not a name-filtered slice of the live variable list.
///
/// <para><b>The bug</b>: the checkpoint content used to be selected with
/// <c>Name.StartsWith("Squid.Action.")</c>. Real output variables are minted as
/// <c>Squid.Action[{step}].Output.{name}</c> — a BRACKET — so that predicate matched none of
/// them, while still matching unrelated action-scoped config variables such as
/// <c>Squid.Action.Kubernetes.Namespace</c>. Every resumed deployment silently lost its output
/// variables while the checkpoint column still looked populated.</para>
///
/// <para><b>Why this test and not the serializer tests</b>: the serializer can be exercised
/// directly, but doing so proves nothing about which set the phase hands it — the bug can be
/// fully reintroduced with every serializer test still green. This drives the REAL phase and
/// asserts on the checkpoint the phase actually saved, so reverting either half fails here.</para>
///
/// <para>The discriminator is deliberate: the context carries BOTH a decoy that only the old
/// predicate matches and an output variable that only the new path captures, so the two
/// implementations produce opposite results rather than merely differing in count.</para>
/// </summary>
public sealed class CheckpointOutputVariableSeamTests
{
    /// <summary>Matches the OLD predicate, is NOT an output variable. Must never be checkpointed.</summary>
    private const string DecoyActionScopedConfigName = "Squid.Action.Kubernetes.Namespace";

    private const string EmittedOutputName = "DeployedUrl";
    private const string EmittedOutputValue = "https://web.test";

    private static readonly (string, string)[] OneOutput = { (EmittedOutputName, EmittedOutputValue) };

    private static readonly VariableDto[] DecoySeed =
    {
        new() { Name = DecoyActionScopedConfigName, Value = "production" }
    };

    [Fact]
    public async Task PersistCheckpoint_WritesCapturedOutputVariables_NotNameFilteredConfigVariables()
    {
        var saved = await CheckpointPhaseHarness.RunOneBatchAsync(OneOutput, seedVariables: DecoySeed);

        saved.ShouldNotBeNull("the batch-boundary checkpoint must have been persisted");
        saved.OutputVariablesJson.ShouldNotBeNull(
            customMessage: "The captured output variable must reach the checkpoint column. Null here means the " +
                           "persist path selected nothing — the exact symptom of the name-filter bug.");

        saved.OutputVariablesJson.ShouldContain(EmittedOutputValue,
            customMessage: "The value emitted via ##squid[setVariable] must be checkpointed. Its absence means " +
                           "PersistCheckpointAsync is no longer reading the captured set (regression to a name filter).");

        saved.OutputVariablesJson.ShouldNotContain(DecoyActionScopedConfigName,
            customMessage: $"'{DecoyActionScopedConfigName}' is action-scoped CONFIG, not an output variable. It is " +
                           "present only because the old StartsWith(\"Squid.Action.\") predicate matched it. Seeing it " +
                           "here means the persist path reverted to name-filtering the live variable list.");
    }

    [Fact]
    public async Task PersistCheckpoint_CheckpointsTheBareAlias_WhichNoNamePredicateCouldSelect()
    {
        // One ##squid[setVariable] mints several forms, including a bare alias that is
        // indistinguishable from an ordinary variable by name. It is therefore the form that
        // proves capture-site accumulation rather than any name matching.
        var saved = await CheckpointPhaseHarness.RunOneBatchAsync(OneOutput, seedVariables: DecoySeed);

        var restored = CheckpointPhaseHarness.ReadCheckpoint(saved);

        restored.ShouldContain(v => v.Name == EmittedOutputName,
            customMessage: "The bare alias must be checkpointed. No name predicate can select it, so its presence " +
                           "is only possible via capture-site accumulation.");
        restored.ShouldContain(v => v.Name.Contains("Output." + EmittedOutputName),
            customMessage: "The step-qualified form (Squid.Action[{step}].Output.{name}) must also be checkpointed.");
    }

    [Fact]
    public async Task PersistCheckpoint_SameValueFromEveryTarget_IsNotDuplicated()
    {
        // The merge skips a same-value re-emit (it never appends it), so the checkpoint inherits
        // that de-duplication for free — no copy per target, nothing to grow across resumes.
        var saved = await CheckpointPhaseHarness.RunOneBatchAsync(OneOutput, targetCount: 3);

        var restored = CheckpointPhaseHarness.ReadCheckpoint(saved);
        var bareAliasCount = restored.Count(v => v.Name == EmittedOutputName);

        bareAliasCount.ShouldBe(1,
            customMessage: $"Three targets emitted the SAME (name, value); the merge appends it once, so the " +
                           $"checkpoint must hold one entry for the bare alias, not {bareAliasCount}.");
    }

    [Fact]
    public async Task PersistCheckpoint_TargetsEmittingDifferentValues_ResolveAsTheLiveRunDid()
    {
        // ReSharper disable once RedundantExplicitArrayCreation
        // The load-bearing end-to-end case. Three targets emit A, B, A for one name. The merge
        // compares each write against the FIRST indexed value and never re-indexes, so it appends
        // A and B and skips the trailing A — live resolves B under last-wins. The checkpoint must
        // resolve B too. A checkpoint built by de-duplicating, or by appending the raw incoming
        // list, resolves a different value here.
        var saved = await CheckpointPhaseHarness.RunOneBatchAsync(perTargetOutputs: new[]
        {
            new[] { ("Digest", "A") },
            new[] { ("Digest", "B") },
            new[] { ("Digest", "A") }
        });

        var checkpointed = CheckpointPhaseHarness.ReadCheckpoint(saved);

        // Last-wins, matching VariableDictionary's dictionary assignment.
        var resolved = checkpointed.Where(v => v.Name == "Digest").Select(v => v.Value).LastOrDefault();

        resolved.ShouldBe("B",
            customMessage: "Targets emitted A,B,A for one name. The live merge appends A then B and skips the " +
                           $"trailing A, so last-wins resolves 'B'. The checkpoint resolved '{resolved}' — a resumed " +
                           "deployment would reconfigure itself with a different target's value.");
    }

    [Fact]
    public async Task PersistCheckpoint_ShadowedName_KeepsTheTrailingRepeatTheMergeAppended()
    {
        // The case that de-duplication gets WRONG, driven through the real phase.
        //
        // A project variable already holds this name, so the merge's first-indexed value is that
        // prior one and EVERY emit differs from it — U1, U2 and the trailing U1 are ALL appended,
        // and last-wins resolves U1. Collapsing the append list to distinct (name, value) pairs
        // yields [U1, U2] and resolves U2 instead. The A,B,A test above cannot catch that,
        // because without shadowing the merge already skips the trailing repeat.
        var saved = await CheckpointPhaseHarness.RunOneBatchAsync(
            seedVariables: new VariableDto[] { new() { Name = "Digest", Value = "project-default" } },
            perTargetOutputs: new[]
            {
                new[] { ("Digest", "U1") },
                new[] { ("Digest", "U2") },
                new[] { ("Digest", "U1") }
            });

        var checkpointed = CheckpointPhaseHarness.ReadCheckpoint(saved);
        var resolved = checkpointed.Where(v => v.Name == "Digest").Select(v => v.Value).LastOrDefault();

        resolved.ShouldBe("U1",
            customMessage: "With a prior variable shadowing the name, the merge appends U1, U2 AND the trailing U1, " +
                           $"so last-wins resolves 'U1'. The checkpoint resolved '{resolved}' — the append list is " +
                           "being de-duplicated or otherwise re-derived instead of stored verbatim.");
    }
}
